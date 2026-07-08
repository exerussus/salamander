using System;
using System.Collections.Generic;
using System.IO;
using Dsl.Compilation;
using Dsl.Runtime;
using Dsl.Semantics;

namespace Dsl.DemoHost
{
    /// <summary>Игровая сущность демо-хоста. Скрипты видят её как хэндл Unit.</summary>
    public sealed class Unit
    {
        public string Name;
        public float Health;
        public float MaxHealth;
        public bool IsPlayer;
        public bool IsBoss;
    }

    /// <summary>
    /// Демонстрация полной проводки движка без Unity:
    /// регистрация API → компиляция модуля с диска → тики и события.
    /// Запуск: dotnet run [путь к папке модуля] (по умолчанию ../mymod).
    ///
    /// ВНИМАНИЕ: здесь НАМЕРЕННО показан сырой уровень (HostRegistry напрямую) —
    /// чтобы было видно, что происходит под капотом. Для повседневной работы
    /// используйте fluent-обёртку Dsl.Hosting.HostBuilder (пример —
    /// Examples/UnityRpg/RpgSimulation.cs): та же мощность, кратно меньше кода.
    /// </summary>
    public static class Program
    {
        private static readonly List<string> SpawnLog = new List<string>();
        private static Unit _player;
        private static Unit _boss;

        public static int Main(string[] args)
        {
            string modulePath = args.Length > 0 ? args[0] : Path.Combine("..", "mymod");
            if (!File.Exists(Path.Combine(modulePath, "module.json")))
            {
                Console.WriteLine($"Не найден module.json в '{modulePath}'. Укажите путь к модулю аргументом.");
                return 1;
            }

            // ----- 1. Регистрация API игры --------------------------------
            var registry = new HostRegistry();

            int updateEventId = registry.DefineEvent("Update", TypeRef.Float, TypeRef.Float);

            registry.DefineEnum("DamageType", "Physical", "Fire", "Ice");
            var damageType = registry.EnumType("DamageType");

            registry.DefineClass("Unit");
            var unitT = registry.ClassType("Unit");

            // свойства сущности (для доступа вида unit.health из скриптов)
            registry.DefineProperty("Unit", "health", TypeRef.Float, readOnly: false,
                getter: (ctx, o) => Variant.Float(((Unit)o).Health),
                setter: (ctx, o, v) => ((Unit)o).Health = v.ToF());
            registry.DefineProperty("Unit", "name", TypeRef.Str, readOnly: true,
                getter: (ctx, o) => Variant.Str(ctx.InternString(((Unit)o).Name)),
                setter: null);

            // API-класс UnitApi
            registry.DefineMethod("UnitApi", "IsPlayer", new[] { unitT }, TypeRef.Bool,
                (ref CallContext c) => c.ReturnBool(c.Entity<Unit>(0)?.IsPlayer ?? false));
            registry.DefineMethod("UnitApi", "IsBoss", new[] { unitT }, TypeRef.Bool,
                (ref CallContext c) => c.ReturnBool(c.Entity<Unit>(0)?.IsBoss ?? false));
            registry.DefineMethod("UnitApi", "Health", new[] { unitT }, TypeRef.Float,
                (ref CallContext c) => c.ReturnFloat(c.Entity<Unit>(0)?.Health ?? 0f));
            registry.DefineMethod("UnitApi", "MaxHealth", new[] { unitT }, TypeRef.Float,
                (ref CallContext c) => c.ReturnFloat(c.Entity<Unit>(0)?.MaxHealth ?? 0f));
            registry.DefineMethod("UnitApi", "PlayAnimation", new[] { unitT, TypeRef.Str }, TypeRef.Void,
                (ref CallContext c) => Console.WriteLine($"    [anim] {c.Entity<Unit>(0)?.Name}: {c.Str(1)}"));

            // API-класс SpawnApi
            registry.DefineMethod("SpawnApi", "SpawnWave", new[] { TypeRef.Int }, TypeRef.Void,
                (ref CallContext c) =>
                {
                    SpawnLog.Add($"волна x{c.Int(0)}");
                    Console.WriteLine($"    [spawn] волна из {c.Int(0)} врагов");
                });
            registry.DefineMethod("SpawnApi", "Boss", Array.Empty<TypeRef>(), unitT,
                (ref CallContext c) => c.ReturnEntity(_boss));

            int damageEventId = registry.DefineEvent("OnUnitDamageTaken", unitT, unitT, TypeRef.Float, damageType);

            // ----- 2. Компиляция модуля ------------------------------------
            var modules = LoadModule(modulePath);
            var result = ScriptCompiler.Compile(registry, hostApiVersion: 1, modules);

            foreach (var d in result.Diagnostics) Console.WriteLine(d);
            if (!result.Success)
            {
                Console.WriteLine("Компиляция не удалась.");
                return 2;
            }

            // ----- 3. Движок и сценарий ------------------------------------
            var engine = new ScriptEngine(registry);
            engine.OnLog += m => Console.WriteLine($"    [log] {m}");
            engine.OnWarn += m => Console.WriteLine($"    [warn] {m}");
            engine.OnError += m => Console.WriteLine($"    [ERROR] {m}");
            engine.LoadProgram(result.Program);

            _player = new Unit { Name = "Player", Health = 100, MaxHealth = 100, IsPlayer = true };
            _boss = new Unit { Name = "Boss", Health = 500, MaxHealth = 500, IsBoss = true };

            const float dt = 0.1f;
            for (int frame = 0; frame < 200; frame++) // 20 секунд симуляции
            {
                float t = frame * dt;

                engine.Tick(dt);
                engine.Raise(updateEventId).AddFloat((float)engine.Time).AddFloat(dt).Commit();

                // сценарий: на 0.5с игрока бьют → триггер засады
                if (frame == 5)
                {
                    Console.WriteLine($"[t={t:0.0}] игрок получает удар");
                    _player.Health -= 10;
                    engine.Raise(damageEventId)
                          .AddEntity(_boss).AddEntity(_player)
                          .AddFloat(10).AddEnum(registry.EnumType("DamageType").EnumId, 0)
                          .Commit();
                }

                // с 6-й секунды лупим босса, пока не взбесится
                if (t >= 6f && frame % 10 == 0 && _boss.Health > 0)
                {
                    _boss.Health -= 90;
                    Console.WriteLine($"[t={t:0.0}] босс получает 90 урона (hp={_boss.Health})");
                    engine.Raise(damageEventId)
                          .AddEntity(_player).AddEntity(_boss)
                          .AddFloat(90).AddEnum(registry.EnumType("DamageType").EnumId, 1)
                          .Commit();
                }
            }

            Console.WriteLine($"Симуляция окончена. Волн заспавнено: {SpawnLog.Count}.");
            return 0;
        }

        private static List<ModuleSourceSet> LoadModule(string dir)
        {
            var manifest = ModuleManifest.Parse(File.ReadAllText(Path.Combine(dir, "module.json")));
            var set = new ModuleSourceSet { Manifest = manifest };
            foreach (var rel in manifest.Sources)
                set.Files.Add(($"{manifest.Name}/{rel}", File.ReadAllText(Path.Combine(dir, rel))));
            return new List<ModuleSourceSet> { set };
        }
    }
}
