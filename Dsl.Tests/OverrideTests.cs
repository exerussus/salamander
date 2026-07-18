using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// По-членный мерж деклараций: блоки с одним именем (trigger/class/listener,
    /// как и архетипы по (вид, id)) — одна сущность; поздний блок переопределяет
    /// только объявленные члены, ранние обработчики/вызовы видят итог.
    /// </summary>
    public sealed class OverrideTests
    {
        public sealed class Unit { public string Name; }

        private HostBuilder _host;
        private EventRef<Unit> _onPing;
        private EventRef<Unit, float> _onHit;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _host = new HostBuilder();
            _host.Class<Unit>().Prop("name", u => u.Name);
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));
            _onPing = _host.Event<Unit>("OnPing");
            _onHit = _host.Event<Unit, float>("OnHit");
        }

        private static ModuleSourceSet Mod(string name, string src, params string[] deps)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest
                {
                    Name = name, ApiVersion = 1,
                    Dependencies = deps, Sources = new[] { name + ".sal" },
                },
            };
            set.Files.Add((name + "/" + name + ".sal", src));
            return set;
        }

        private CompilationResult Compile(params ModuleSourceSet[] mods)
            => ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet>(mods));

        private static string Dump(CompilationResult r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            return sb.ToString();
        }

        private static bool Has(CompilationResult r, string code)
        {
            foreach (var d in r.Diagnostics) if (d.Code == code) return true;
            return false;
        }

        private ScriptEngine Load(CompilationResult r)
        {
            Assert.IsTrue(r.Success, Dump(r));
            var engine = new ScriptEngine(_host.Registry);
            engine.OnError += m => Assert.Fail(m);
            engine.LoadProgram(r.Program);
            engine.Tick(0.016f);
            return engine;
        }

        // Триггер: поле-патч + переопределённое событие срабатывает РОВНО один раз.
        [Test]
        public void Trigger_MergesPerMember_EventFiresOnce()
        {
            var engine = Load(Compile(Mod("game", @"
                trigger T { int n = 1; event OnPing(Unit u) { Api.Note($""a{n}""); } }
                trigger T { event OnPing(Unit u) { Api.Note($""b{n}""); } }
                trigger T { int n = 5; }")));

            _onPing.Raise(engine, new Unit());
            Assert.AreEqual(new[] { "b5" }, _log, "поздний обработчик, позднее значение, один запуск");
        }

        // Класс: патч значения поля и константы; функции видят итоговое поле.
        [Test]
        public void Class_FieldAndConstPatch()
        {
            var engine = Load(Compile(Mod("game", @"
                class Balance {
                    int hp = 100;
                    const int MAX = 3;
                    func Get() -> int { return hp; }
                }
                class Balance { int hp = 150; const int MAX = 9; }
                trigger T { event OnPing(Unit u) { Api.Note($""{Balance.Get()} {Balance.MAX}""); } }")));

            _onPing.Raise(engine, new Unit());
            Assert.AreEqual(new[] { "150 9" }, _log);
        }

        // Класс: переопределение функции видят вызовы, написанные ДО него.
        [Test]
        public void Class_FuncOverride_SeenByEarlierCallers()
        {
            var engine = Load(Compile(Mod("game", @"
                class Util { func V() -> int { return 1; } }
                trigger T { event OnPing(Unit u) { Api.Note($""{Util.V()}""); } }
                class Util { func V() -> int { return 2; } }")));

            _onPing.Raise(engine, new Unit());
            Assert.AreEqual(new[] { "2" }, _log, "все вызовы вяжутся на позднюю версию");
        }

        // Listener: поле-патч + переопределение обработчика; поле — общий слот подписки.
        [Test]
        public void Listener_MergesPerMember()
        {
            var engine = Load(Compile(Mod("game", @"
                listener W { int x = 1; event OnHit(Unit u, float d) { Api.Note($""a{x}""); } }
                listener W { event OnHit(Unit u, float d) { x = x + 1; Api.Note($""b{x}""); } }
                listener W { int x = 9; }
                trigger S { event OnPing(Unit u) { Engine.Attach(W, u); } }")));

            var a = new Unit { Name = "A" };
            engine.Entities.Register(a);
            _onPing.Raise(engine, a);
            _onHit.Raise(engine, a, 1f);
            _onHit.Raise(engine, a, 1f);

            // поздний обработчик, инициализатор из блока-патча, один запуск на событие
            Assert.AreEqual(new[] { "b10", "b11" }, _log);
        }

        // Кросс-модульный патч триггера: мод меняет только значение поля.
        [Test]
        public void CrossModule_TriggerFieldPatch()
        {
            var baseMod = Mod("base", @"
                trigger T { int dmg = 3; event OnPing(Unit u) { Api.Note($""{dmg}""); } }");
            var patch = Mod("patch", @"trigger T { int dmg = 7; }", "base");

            var engine = Load(Compile(baseMod, patch));
            _onPing.Raise(engine, new Unit());
            Assert.AreEqual(new[] { "7" }, _log);
        }

        // disabled: поздний блок решает стартовый флаг.
        [Test]
        public void DisabledFlag_LaterBlockWins()
        {
            var engine = Load(Compile(Mod("game", @"
                trigger T { event OnPing(Unit u) { Api.Note(""fired""); } }
                disabled trigger T { }
                trigger Enabler { event OnHit(Unit u, float d) { Engine.EnableTrigger(T); } }")));

            _onPing.Raise(engine, new Unit());
            Assert.AreEqual(0, _log.Count, "патч выключил триггер со старта");

            _onHit.Raise(engine, new Unit(), 1f);   // включаем
            _onPing.Raise(engine, new Unit());
            Assert.AreEqual(new[] { "fired" }, _log);
        }

        // ===== ошибки =====

        [Test]
        public void Trigger_FieldTypeMismatch_IsError()
        {
            var r = Compile(Mod("game", @"
                trigger T { int n = 1; event OnPing(Unit u) { } }
                trigger T { float n = 2.0; }"));
            Assert.IsTrue(Has(r, "E0207"), Dump(r));
        }

        [Test]
        public void Class_ConstToField_IsError()
        {
            var r = Compile(Mod("game", @"
                class B { const int X = 1; }
                class B { int X = 2; }
                trigger T { event OnPing(Unit u) { } }"));
            Assert.IsTrue(Has(r, "E0207"), Dump(r));
        }

        [Test]
        public void SameBlock_DuplicateEvent_IsStillError()
        {
            var r = Compile(Mod("game", @"
                trigger T {
                    event OnPing(Unit u) { }
                    event OnPing(Unit u) { }
                }"));
            Assert.IsTrue(Has(r, "E0113"), Dump(r));
        }
    }
}
