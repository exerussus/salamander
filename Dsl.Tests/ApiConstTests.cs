using System.Collections.Generic;
using System.Globalization;
using Dsl.Codegen;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Константы API-класса: host.Api("Api.Parts.Grip").Const("sword_01", "...")
    /// и чтение БЕЗ скобок — Api.Parts.Grip.sword_01.
    ///
    /// Зачем это отдельная форма, а не метод без аргументов, возвращающий литерал:
    ///  - метод врёт читателю рецепта: «здесь что-то вычисляется»;
    ///  - каждое обращение стоит хостового вызова через делегат, хотя вычислять
    ///    нечего — константа сворачивается в литерал прямо в байткоде;
    ///  - реестр пухнет: каталог деталей — это десятки HostMethodInfo с делегатами,
    ///    каждый из которых возвращает строку.
    ///
    /// Правка дополняющая: у кого констант нет, для того не меняется ничего.
    /// </summary>
    public sealed class ApiConstTests
    {
        public sealed class Unit { public string Name; }

        public enum Team { Red, Blue }

        private HostBuilder _host;
        private EventRef<Unit> _onPing;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _host = new HostBuilder();
            _host.Enum<Team>();
            _host.Class<Unit>().Prop("name", u => u.Name);
            _onPing = _host.Event<Unit>("OnPing");
            _host.Api("Api.Log").Act("Note", (string s) => _log.Add(s));
        }

        private static ModuleSourceSet Mod(string name, string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = name, ApiVersion = 1, Sources = new[] { name + ".sal" } },
            };
            set.Files.Add((name + "/" + name + ".sal", src));
            return set;
        }

        private CompilationResult Compile(string src)
            => ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet> { Mod("game", src) });

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

        private void Fire(ScriptEngine engine)
        {
            _onPing.Raise(engine, new Unit { Name = "H" });
            engine.Tick(0.016f);
        }

        private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>Сколько хостовых вызовов осталось в байткоде программы.</summary>
        private static int CountHostCalls(CompiledProgram p)
        {
            int n = 0;
            foreach (var fn in p.Functions)
            {
                if (fn?.Code == null) continue;
                foreach (var ins in fn.Code) if (ins.Op == OpCode.CallHost) n++;
            }
            return n;
        }

        // ===================================================================
        // Ради чего всё затевалось
        // ===================================================================

        [Test]
        public void Const_ReadsWithoutParens()
        {
            _host.Api("Api.PartsCatalog.Weapon.Grip")
                 .Const("sword_1h_just_01", "weapon.grip.sword_1h.just.01");

            var engine = Load(Compile(@"
                trigger T {
                    event OnPing(Unit u) {
                        Api.Log.Note(Api.PartsCatalog.Weapon.Grip.sword_1h_just_01);
                    }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "weapon.grip.sword_1h.just.01" }, _log);
        }

        [Test]
        public void Const_OnFlatApiName_WorksToo()
        {
            _host.Api("Cfg").Const("MAX_RANGE", 12.5f);

            var engine = Load(Compile(@"
                trigger T { event OnPing(Unit u) { Api.Log.Note($""{Cfg.MAX_RANGE}""); } }"));
            Fire(engine);

            Assert.AreEqual(new[] { F(12.5f) }, _log);
        }

        [Test]
        public void Const_OfEveryLiteralType()
        {
            _host.Api("Cfg")
                 .Const("FLAG", true)
                 .Const("TICKS", 7)
                 .Const("WEIGHT", 1.5f)
                 .Const("PRECISE", 0.125)
                 .Const("ID", "grip.01")
                 .Const("SIDE", Team.Blue);

            var engine = Load(Compile(@"
                trigger T {
                    event OnPing(Unit u) {
                        Api.Log.Note($""{Cfg.FLAG} {Cfg.TICKS} {Cfg.WEIGHT} {Cfg.PRECISE} {Cfg.ID}"");
                        if (Cfg.SIDE == Team.Blue) { Api.Log.Note(""blue""); }
                    }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "true 7 1.5 0.125 grip.01", "blue" }, _log);
        }

        [Test]
        public void Const_DoesNotRegisterAHostFunction_AndFoldsToLiteral()
        {
            int before = _host.Registry.FunctionCount;
            _host.Api("Api.PartsCatalog.Weapon.Grip")
                 .Const("a", "grip.a").Const("b", "grip.b").Const("c", "grip.c");

            Assert.AreEqual(before, _host.Registry.FunctionCount,
                "константа не должна занимать слот делегата в реестре");

            // читаем три константы и НЕ вызываем ничего хостового
            var r = Compile(@"
                trigger T {
                    string x = """";
                    event OnPing(Unit u) {
                        x = Api.PartsCatalog.Weapon.Grip.a;
                        x = Api.PartsCatalog.Weapon.Grip.b;
                        x = Api.PartsCatalog.Weapon.Grip.c;
                    }
                }");
            Assert.IsTrue(r.Success, Dump(r));
            Assert.AreEqual(0, CountHostCalls(r.Program),
                "у константы нет вычисления — хостового вызова в байткоде остаться не должно");
        }

        [Test]
        public void Const_FlowsIntoExpressionsAndCalls()
        {
            _host.Api("Cfg").Const("BASE", 10.0);
            _host.Api("Math").Fn("Sum", (double a, double b) => a + b);

            var engine = Load(Compile(@"
                trigger T {
                    event OnPing(Unit u) {
                        double total = Cfg.BASE * 2.0;
                        Api.Log.Note($""{total} {Math.Sum(Cfg.BASE, 5.0)}"");
                    }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "20 15" }, _log);
        }

        [Test]
        public void Const_InitializesReadonlyFieldOfArchetype()
        {
            _host.Api("Api.PartsCatalog.Weapon.Grip").Const("sword_01", "weapon.grip.sword.01");
            var onHit = _host.Archetype("weapon").Event<Unit>("OnHit");

            var engine = Load(Compile(@"
                weapon sword {
                    readonly string grip = Api.PartsCatalog.Weapon.Grip.sword_01;
                    event OnHit(Unit u) { Api.Log.Note(grip); }
                }"));

            onHit.Raise(engine, "sword", new Unit { Name = "H" });
            engine.Tick(0.016f);

            Assert.AreEqual(new[] { "weapon.grip.sword.01" }, _log);
            // и хост читает то же значение как обычную константу вида
            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "grip", out var v));
            Assert.AreEqual("weapon.grip.sword.01", engine.ResolveString(v));
        }

        [Test]
        public void Const_TypeIsTheDeclaredOne()
        {
            _host.Api("Cfg").Const("ID", "grip.01").Const("TICKS", 3);

            var widening = Compile(@"
                trigger T { event OnPing(Unit u) { float f = Cfg.TICKS; Api.Log.Note($""{f}""); } }");
            Assert.IsTrue(widening.Success, Dump(widening));

            var wrong = Compile(@"
                trigger T { event OnPing(Unit u) { int n = Cfg.ID; } }");
            Assert.IsFalse(wrong.Success, Dump(wrong));
        }

        // ===================================================================
        // Диагностика: не превращать забытые скобки в «нет такого члена»
        // ===================================================================

        [Test]
        public void ConstCalledAsMethod_IsReported()
        {
            _host.Api("Cfg").Const("ID", "grip.01");

            var r = Compile(@"trigger T { event OnPing(Unit u) { Api.Log.Note(Cfg.ID()); } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0239"), Dump(r));
            StringAssert.Contains("без скобок", Dump(r));
        }

        [Test]
        public void MethodReadAsConst_SaysCallIt()
        {
            _host.Api("Cfg").Const("ID", "grip.01").Host.Api("Cfg2");
            _host.Api("Cfg").Fn("Compute", () => 1);

            var r = Compile(@"trigger T { event OnPing(Unit u) { Api.Log.Note($""{Cfg.Compute}""); } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0162"), Dump(r));
            StringAssert.Contains("вызвать", Dump(r));
        }

        [Test]
        public void UnknownConst_IsReported()
        {
            _host.Api("Cfg").Const("ID", "grip.01");

            var r = Compile(@"trigger T { event OnPing(Unit u) { Api.Log.Note(Cfg.OTHER); } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0238"), Dump(r));
        }

        [Test]
        public void UnknownConst_OnApiWithoutAnyConsts_HintsAtParens()
        {
            // у Api.Log констант нет вовсе — тогда сообщение подсказывает про
            // скобки, а не отправляет искать опечатку в списке констант
            var r = Compile(@"trigger T { event OnPing(Unit u) { Api.Log.Note(Api.Log.Nope); } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0238"), Dump(r));
            StringAssert.Contains("скобками", Dump(r));

            // а существующий метод без скобок — это именно «вызовите его»
            var m = Compile(@"trigger T { event OnPing(Unit u) { Api.Log.Note(Api.Log.Note); } }");
            Assert.IsTrue(Has(m, "E0162"), Dump(m));
        }

        [Test]
        public void TypoInMiddleSegment_StillSaysNoSuchApi()
        {
            _host.Api("Api.PartsCatalog.Weapon.Grip").Const("sword_01", "x");

            var r = Compile(@"
                trigger T { event OnPing(Unit u) { Api.Log.Note(Api.PartsCatalog.Wepon.Grip.sword_01); } }");

            Assert.IsFalse(r.Success, Dump(r));
        }

        // ===================================================================
        // Границы: константа не должна перехватывать чужие точки
        // ===================================================================

        [Test]
        public void LocalVariable_ShadowsApiHead()
        {
            _host.Api("Cfg").Const("ID", "from-api");

            // локаль важнее API: иначе свёртка пути перехватила бы обычный доступ
            var engine = Load(Compile(@"
                trigger T {
                    event OnPing(Unit u) {
                        Unit Cfg = u;
                        Api.Log.Note(Cfg.name);
                    }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "H" }, _log);
        }

        [Test]
        public void EntityPropertyChain_IsNotMistakenForConst()
        {
            _host.Api("Cfg").Const("ID", "x");

            var engine = Load(Compile(@"
                class Holder { Unit last = null; }
                trigger T {
                    event OnPing(Unit u) { Holder.last = u; Api.Log.Note(Holder.last.name); }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "H" }, _log);
        }

        // ===================================================================
        // Регистрация
        // ===================================================================

        [Test]
        public void DuplicateConst_IsRejected()
        {
            var api = _host.Api("Cfg").Const("ID", "a");
            Assert.Throws<System.InvalidOperationException>(() => api.Const("ID", "b"));
        }

        [Test]
        public void NameClashWithMethod_IsRejectedBothWays()
        {
            // имя у API одно на всех: молча разрешить — значит отдать разрешение
            // «это метод или константа?» на откуп порядку проверок в чекере
            var a = _host.Api("Cfg").Const("ID", "a");
            Assert.Throws<System.InvalidOperationException>(() => a.Fn("ID", () => 1));

            var b = _host.Api("Cfg2").Fn("Compute", () => 1);
            Assert.Throws<System.InvalidOperationException>(() => b.Const("Compute", 1));
        }

        [Test]
        public void UnsupportedConstType_IsRejected()
        {
            var api = _host.Api("Cfg");
            var ex = Assert.Throws<System.ArgumentException>(() => api.Const("owner", new Unit()));
            StringAssert.Contains("литерал", ex.Message);
        }

        // ===================================================================
        // Манифест
        // ===================================================================

        [Test]
        public void Manifest_RoundTrips_Consts()
        {
            _host.Api("Api.PartsCatalog.Weapon.Grip")
                 .Const("sword_01", "weapon.grip.sword.01", doc: "рукоять одноручного меча")
                 .Const("TICKS", 7)
                 .Const("SIDE", Team.Blue);

            string json = ApiManifest.Export(_host.Registry, 1);
            StringAssert.Contains("\"consts\"", json);
            StringAssert.Contains("\"Blue\"", json);           // енум именем, а не индексом
            StringAssert.Contains("рукоять одноручного меча", json);

            var imported = ApiManifest.Import(json, out _);
            Assert.IsTrue(imported.TryGetApi("Api.PartsCatalog.Weapon.Grip", out var api));
            Assert.AreEqual(3, api.Consts.Count);
            Assert.AreEqual("sword_01", api.Consts[0].Name, "порядок объявления сохраняется");

            Assert.IsTrue(api.TryGetConst("sword_01", out var grip));
            Assert.AreEqual("weapon.grip.sword.01", grip.ValueStr);
            Assert.AreEqual("рукоять одноручного меча", grip.Doc);
            Assert.IsTrue(api.TryGetConst("TICKS", out var ticks));
            Assert.AreEqual(7, ticks.Value.AsInt);
            Assert.IsTrue(api.TryGetConst("SIDE", out var side));
            Assert.AreEqual((int)Team.Blue, side.Value.EnumValue);
        }

        [Test]
        public void Manifest_ImportedRegistry_CompilesConstReads()
        {
            _host.Api("Api.PartsCatalog.Weapon.Grip").Const("sword_01", "weapon.grip.sword.01");

            var imported = ApiManifest.Import(ApiManifest.Export(_host.Registry, 1), out _);

            var r = ScriptCompiler.Compile(imported, 1, new List<ModuleSourceSet>
            {
                Mod("game", @"
                    trigger T {
                        event OnPing(Unit u) {
                            Api.Log.Note(Api.PartsCatalog.Weapon.Grip.sword_01);
                        }
                    }"),
            });
            Assert.IsTrue(r.Success, Dump(r));
        }
    }
}
