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
    /// Методы самого объекта: basket.AddPerk("x") вместо Api.Npc.Perk(basket, "x").
    ///
    /// Приёмник уходит НУЛЕВЫМ аргументом хостовой функции, поэтому обе записи
    /// дают один и тот же CallHost — разница только в разрешении имени на
    /// компиляции. Отсюда и правило, когда что объявлять: метод у класса — когда
    /// объект существует ради вызовов (корзина, билдер, который передают в
    /// событие); API-класс — когда объект это данные, и вызов ищут в списке API.
    /// </summary>
    public sealed class ClassMethodTests
    {
        public sealed class Unit { public string Name; public float Health = 10f; }

        /// <summary>Корзина, которую игра передаёт в событие: она и ЕСТЬ интерфейс.</summary>
        public sealed class PerkBasket
        {
            public readonly List<string> Perks = new List<string>();
            public void Add(string id) => Perks.Add(id);
            public int Count => Perks.Count;
        }

        private HostBuilder _host;
        private EventRef<Unit, PerkBasket> _onBuild;
        private List<string> _log;
        private PerkBasket _basket;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _basket = new PerkBasket();
            _host = new HostBuilder();
            _host.Class<Unit>().Prop("name", u => u.Name).Prop("health", u => u.Health);
            _host.Class<PerkBasket>("PerkBasket", "Корзина перков, собираемая скриптом.")
                 .Prop("count", b => b.Count)
                 .Act("AddPerk", (PerkBasket b, string id) => b.Add(id),
                      Sig.Doc("Добавить перк.").P("id", "идентификатор перка"))
                 .Fn("Has", (PerkBasket b, string id) => b.Perks.Contains(id));
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));
            _onBuild = _host.Event<Unit, PerkBasket>("BuildPerks");
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

        private void Fire(ScriptEngine engine, PerkBasket basket = null)
        {
            _onBuild.Raise(engine, new Unit { Name = "H" }, basket ?? _basket);
            engine.Tick(0.016f);
        }

        private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);

        // ===================================================================
        // Ради чего всё затевалось
        // ===================================================================

        [Test]
        public void ObjectMethod_IsCalledOnTheValue()
        {
            var engine = Load(Compile(@"
                trigger T {
                    event BuildPerks(Unit u, PerkBasket b) { b.AddPerk(""dummy_trainer""); }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "dummy_trainer" }, _basket.Perks);
        }

        [Test]
        public void ObjectMethod_WithResult_AndAlongsideProperties()
        {
            var engine = Load(Compile(@"
                trigger T {
                    event BuildPerks(Unit u, PerkBasket b) {
                        b.AddPerk(""a"");
                        b.AddPerk(""b"");
                        Api.Note($""{b.count} {b.Has(""a"")} {b.Has(""zzz"")}"");
                    }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "2 true false" }, _log);
        }

        [Test]
        public void SameBytecodeAsTheStaticForm()
        {
            // приёмник — нулевой аргумент CallHost: обе записи компилируются
            // в одинаковую последовательность опкодов
            _host.Api("Api.Npc").Act("Perk", (PerkBasket b, string id) => b.Add(id));

            var viaObject = Compile(@"
                trigger T { event BuildPerks(Unit u, PerkBasket b) { b.AddPerk(""x""); } }");
            var viaApi = Compile(@"
                trigger T { event BuildPerks(Unit u, PerkBasket b) { Api.Npc.Perk(b, ""x""); } }");
            Assert.IsTrue(viaObject.Success, Dump(viaObject));
            Assert.IsTrue(viaApi.Success, Dump(viaApi));

            CollectionAssert.AreEqual(Shape(viaObject.Program), Shape(viaApi.Program));
        }

        /// <summary>Опкоды обработчика без привязки к id хостовой функции.</summary>
        private static List<string> Shape(CompiledProgram p)
        {
            var ops = new List<string>();
            foreach (var fn in p.Functions)
            {
                if (fn?.Code == null || fn.Name == null || !fn.Name.Contains("BuildPerks")) continue;
                foreach (var ins in fn.Code)
                    ops.Add(ins.Op == OpCode.CallHost ? $"CallHost/{ins.B}" : ins.Op.ToString());
            }
            Assert.IsNotEmpty(ops, "обработчик не найден в программе");
            return ops;
        }

        [Test]
        public void MethodOnEntityPassedAround_AndOnAField()
        {
            var engine = Load(Compile(@"
                class Keep { PerkBasket last = null; }
                trigger T {
                    func Fill(PerkBasket b) { b.AddPerk(""from_func""); }
                    event BuildPerks(Unit u, PerkBasket b) {
                        Fill(b);
                        Keep.last = b;
                        Keep.last.AddPerk(""from_field"");
                    }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "from_func", "from_field" }, _basket.Perks);
        }

        // ===================================================================
        // Диагностика
        // ===================================================================

        [Test]
        public void PropertyCalledAsMethod_IsReported()
        {
            var r = Compile(@"
                trigger T { event BuildPerks(Unit u, PerkBasket b) { Api.Note($""{b.count()}""); } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0240"), Dump(r));
            StringAssert.Contains("без скобок", Dump(r));
        }

        [Test]
        public void UnknownMethod_StillSaysNoSuchMethod()
        {
            var r = Compile(@"
                trigger T { event BuildPerks(Unit u, PerkBasket b) { b.Nope(""x""); } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0194"), Dump(r));
        }

        [Test]
        public void WrongArgumentTypeOrCount_IsReported()
        {
            var wrongType = Compile(@"
                trigger T { event BuildPerks(Unit u, PerkBasket b) { b.AddPerk(1.5); } }");
            Assert.IsFalse(wrongType.Success, Dump(wrongType));

            var wrongCount = Compile(@"
                trigger T { event BuildPerks(Unit u, PerkBasket b) { b.AddPerk(); } }");
            Assert.IsFalse(wrongCount.Success);
            Assert.IsTrue(Has(wrongCount, "E0188"), Dump(wrongCount),
                "приёмник не считается аргументом скрипта");
        }

        [Test]
        public void MethodOnEmptyReference_IsARuntimeError()
        {
            // вызвать метод на null — это сломанный рецепт, а не «тихо ничего»
            var r = Compile(@"
                class Keep { PerkBasket last = null; }
                trigger T { event BuildPerks(Unit u, PerkBasket b) { Keep.last.AddPerk(""x""); } }");
            Assert.IsTrue(r.Success, Dump(r));

            var errors = new List<string>();
            var engine = new ScriptEngine(_host.Registry);
            engine.OnError += m => errors.Add(m);
            engine.LoadProgram(r.Program);
            engine.Tick(0.016f);
            Fire(engine);

            Assert.AreEqual(1, errors.Count, string.Join("\n", errors));
            StringAssert.Contains("пустой ссылке", errors[0]);
            Assert.IsEmpty(_basket.Perks);
        }

        // ===================================================================
        // Регистрация
        // ===================================================================

        [Test]
        public void NameClashWithProperty_IsRejectedBothWays()
        {
            // имя у класса одно на всех: иначе «это свойство или метод?» решал бы
            // порядок проверок в чекере
            var b = _host.Class<PerkBasket>("Basket2");
            b.Prop("size", x => x.Count);
            Assert.Throws<System.InvalidOperationException>(() => b.Act("size", (PerkBasket x) => { }));

            var c = _host.Class<PerkBasket>("Basket3");
            c.Act("Reset", (PerkBasket x) => x.Perks.Clear());
            Assert.Throws<System.InvalidOperationException>(() => c.Prop("Reset", x => x.Count));
        }

        [Test]
        public void DuplicateMethod_IsRejected()
        {
            var b = _host.Class<PerkBasket>("Basket4").Act("Reset", (PerkBasket x) => x.Perks.Clear());
            Assert.Throws<System.InvalidOperationException>(
                () => b.Act("Reset", (PerkBasket x) => x.Perks.Clear()));
        }

        [Test]
        public void DocWithWrongParamCount_IsRejected()
        {
            var b = _host.Class<PerkBasket>("Basket5");
            var ex = Assert.Throws<System.ArgumentException>(
                () => b.Act("AddPerk", (PerkBasket x, string id) => x.Add(id),
                            Sig.Doc("Добавить.").P("self").P("id")));
            StringAssert.Contains("Приёмник", ex.Message);
        }

        // ===================================================================
        // Манифест
        // ===================================================================

        [Test]
        public void Manifest_RoundTrips_ClassMethods()
        {
            string json = ApiManifest.Export(_host.Registry, 1);
            StringAssert.Contains("\"methods\"", json);
            StringAssert.Contains("идентификатор перка", json);

            var imported = ApiManifest.Import(json, out _);
            Assert.IsTrue(imported.TryGetClass("PerkBasket", out var cls));
            Assert.AreEqual(2, cls.Methods.Count);
            Assert.IsTrue(cls.TryGetMethod("AddPerk", out var add));
            Assert.AreEqual(1, add.Params.Length, "приёмник в сигнатуру скрипта не входит");
            Assert.AreEqual("Добавить перк.", add.Summary);
            Assert.AreEqual("id", add.ParamNames[0]);

            var r = ScriptCompiler.Compile(imported, 1, new List<ModuleSourceSet>
            {
                Mod("game", @"
                    trigger T {
                        event BuildPerks(Unit u, PerkBasket b) {
                            b.AddPerk(""x"");
                            if (b.Has(""x"")) { Api.Note(""ok""); }
                        }
                    }"),
            });
            Assert.IsTrue(r.Success, Dump(r));
        }

        [Test]
        public void Manifest_ClassWithoutMethods_LooksAsBefore()
        {
            var host = new HostBuilder();
            host.Class<Unit>().Prop("name", u => u.Name);

            string json = ApiManifest.Export(host.Registry, 1);
            StringAssert.DoesNotContain("\"methods\"", json);
        }
    }
}
