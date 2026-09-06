using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Составные имена API: host.Api("Api.Weapon") → Api.Weapon.Cut(p, ...).
    /// Точка здесь часть ИМЕНИ, а не доступ к члену — цепочка сворачивается в
    /// строку и ищется в реестре целиком.
    ///
    /// Зачем: без этого приходится выбирать между «много API-классов» (автор
    /// рецепта должен помнить, в каком из них живёт вызов) и «один класс с
    /// префиксами в именах методов» (группировка уезжает в имя метода).
    /// Составное имя даёт одну точку входа И группировку.
    ///
    /// Правка дополняющая: односегментные имена работают как раньше, точка
    /// значима только у того, кто её сам зарегистрировал.
    /// </summary>
    public sealed class ApiNamespaceTests
    {
        public sealed class Unit { public string Name; }

        private HostBuilder _host;
        private EventRef<Unit> _onPing;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _host = new HostBuilder();
            _host.Class<Unit>().Prop("name", u => u.Name);
            _onPing = _host.Event<Unit>("OnPing");
            // общий приёмник логов под тем же префиксом — заодно проверяет,
            // что несколько API живут под одной головой пути
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

        // ===================================================================
        // Вызов через составное имя
        // ===================================================================

        [Test]
        public void TwoSegments_CallsThrough()
        {
            _host.Api("Api.Weapon").Act("Cut", (float x) => _log.Add($"cut {x}"));

            var engine = Load(Compile(@"
                trigger T { event OnPing(Unit u) { Api.Weapon.Cut(2.5); } }"));
            Fire(engine);

            Assert.AreEqual(new[] { "cut 2.5" }, _log);
        }

        [Test]
        public void ThreeSegments_CallsThrough()
        {
            _host.Api("Api.Weapon.Melee").Act("Swing", () => _log.Add("swing"));

            var engine = Load(Compile(@"
                trigger T { event OnPing(Unit u) { Api.Weapon.Melee.Swing(); } }"));
            Fire(engine);

            Assert.AreEqual(new[] { "swing" }, _log);
        }

        [Test]
        public void ReturnValue_AndArgs_TypeCheckAsUsual()
        {
            _host.Api("Api.Math").Fn("Twice", (float f) => f * 2f);

            var engine = Load(Compile(@"
                trigger T { event OnPing(Unit u) { Api.Log.Note($""{Api.Math.Twice(3.0)}""); } }"));
            Fire(engine);
            Assert.AreEqual(new[] { "6" }, _log);
        }

        [Test]
        public void SingleSegment_StillWorks_Unchanged()
        {
            _host.Api("UnitApi").Act("Note", (string s) => _log.Add(s));

            var engine = Load(Compile(@"
                trigger T { event OnPing(Unit u) { UnitApi.Note(""плоское имя""); } }"));
            Fire(engine);

            Assert.AreEqual(new[] { "плоское имя" }, _log);
        }

        [Test]
        public void FlatAndDotted_CoexistUnderSameHead()
        {
            // "Api" — и сам API, и голова пространства имён одновременно
            _host.Api("Api").Act("Root", () => _log.Add("root"));
            _host.Api("Api.Weapon").Act("Cut", () => _log.Add("cut"));

            var engine = Load(Compile(@"
                trigger T { event OnPing(Unit u) { Api.Root(); Api.Weapon.Cut(); } }"));
            Fire(engine);

            Assert.AreEqual(new[] { "root", "cut" }, _log);
        }

        [Test]
        public void SameMethodName_InDifferentApis_DoesNotCollide()
        {
            _host.Api("Api.Weapon").Act("Set", (float v) => _log.Add($"weapon {v}"));
            _host.Api("Api.Attribute").Act("Set", (float v) => _log.Add($"attr {v}"));

            var engine = Load(Compile(@"
                trigger T {
                    event OnPing(Unit u) { Api.Weapon.Set(1.0); Api.Attribute.Set(2.0); }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "weapon 1", "attr 2" }, _log);
        }

        // ===================================================================
        // Диагностика
        // ===================================================================

        [Test]
        public void TypoInMiddleSegment_SaysNoSuchApi_NotNoSuchMethod()
        {
            _host.Api("Api.Weapon").Act("Cut", () => { });

            var r = Compile(@"trigger T { event OnPing(Unit u) { Api.Wepon.Cut(); } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0228"), Dump(r));
            StringAssert.Contains("Api.Wepon", Dump(r));
        }

        [Test]
        public void UnknownMethod_OnDottedApi_IsReported()
        {
            _host.Api("Api.Weapon").Act("Cut", () => { });

            var r = Compile(@"trigger T { event OnPing(Unit u) { Api.Weapon.Slash(); } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0183"), Dump(r));
            StringAssert.Contains("Api.Weapon", Dump(r));
        }

        [Test]
        public void CallingNamespaceItself_IsReported()
        {
            _host.Api("Api.Weapon").Act("Cut", () => { });

            // "Api" — только узел пути, вызывать нечего
            var r = Compile(@"trigger T { event OnPing(Unit u) { Api.Weapon(); } }");
            Assert.IsFalse(r.Success, Dump(r));
        }

        [Test]
        public void NamespaceAsValue_IsReported()
        {
            _host.Api("Api.Weapon").Act("Cut", () => { });

            var r = Compile(@"
                trigger T {
                    event OnPing(Unit u) { float x = Api.Weapon; Api.Weapon.Cut(); }
                }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0227"), Dump(r));
        }

        [Test]
        public void LocalVariable_ShadowsApiHead_NoFalseFold()
        {
            // голову цепочки перекрыла локаль — это обычный доступ к члену,
            // и сворачивать путь нельзя
            _host.Api("Api.Weapon").Act("Cut", () => { });

            var r = Compile(@"
                trigger T {
                    event OnPing(Unit u) {
                        Unit Api = u;
                        Api.Weapon.Cut();
                    }
                }");

            Assert.IsFalse(r.Success, Dump(r));
            Assert.IsFalse(Has(r, "E0228"), "не должно быть «нет такого API» — цель это локаль:\n" + Dump(r));
        }

        [Test]
        public void EntityPropertyChain_IsNotMistakenForApiPath()
        {
            // обычная цепочка свойств не должна ловиться сворачиванием
            _host.Api("Api.Weapon").Act("Cut", () => { });

            var engine = Load(Compile(@"
                trigger T { event OnPing(Unit u) { Api.Log.Note(u.name); } }"));
            Fire(engine);

            Assert.AreEqual(new[] { "H" }, _log);
        }

        // ===================================================================
        // Регистрация
        // ===================================================================

        [Test]
        public void InvalidApiName_IsRejected()
        {
            Assert.Throws<System.ArgumentException>(() => _host.Api("Api.").Act("X", () => { }));
            Assert.Throws<System.ArgumentException>(() => _host.Api(".Api").Act("X", () => { }));
            Assert.Throws<System.ArgumentException>(() => _host.Api("Api..Weapon").Act("X", () => { }));
            Assert.Throws<System.ArgumentException>(() => _host.Api("Api.2Weapon").Act("X", () => { }));
        }

        [Test]
        public void DuplicateMethod_ThrowsInsteadOfSilentlyOverwriting()
        {
            // раньше второй молча затирал первый, и находилось это по
            // «эта строка рецепта ничего не делает»
            var api = _host.Api("Api.Weapon");
            api.Act("Cut", (float v) => _log.Add($"a {v}"));

            var ex = Assert.Throws<System.InvalidOperationException>(
                () => api.Act("Cut", (float v) => _log.Add($"b {v}")));
            StringAssert.Contains("Cut", ex.Message);
            StringAssert.Contains("Api.Weapon", ex.Message);
        }

        [Test]
        public void DuplicateMethod_AcrossDifferentApis_IsFine()
        {
            _host.Api("Api.Weapon").Act("Cut", () => { });
            Assert.DoesNotThrow(() => _host.Api("Api.Armor").Act("Cut", () => { }));
        }

        // ===================================================================
        // Рефлексионный путь
        // ===================================================================

        [SalamanderApi("Оружие.", Name = "Api.Weapon")]
        public sealed class WeaponApiImpl
        {
            private readonly List<string> _sink;
            public WeaponApiImpl(List<string> sink) => _sink = sink;

            [SalamanderMethod("Рубящий удар.")]
            public void Cut([SalamanderParam("сила")] float power) => _sink.Add($"cut {power}");
        }

        [SalamanderApi("Части.")]
        public sealed class PartsApi
        {
            private readonly List<string> _sink;
            public PartsApi(List<string> sink) => _sink = sink;

            [SalamanderMethod("Присоединить.")]
            public void Join([SalamanderParam("что")] string id) => _sink.Add($"join {id}");
        }

        [Test]
        public void Attribute_CanNameApiExplicitly()
        {
            _host.RegisterApi(new WeaponApiImpl(_log));

            var engine = Load(Compile(@"
                trigger T { event OnPing(Unit u) { Api.Weapon.Cut(4.0); } }"));
            Fire(engine);

            Assert.AreEqual(new[] { "cut 4" }, _log);
        }

        [Test]
        public void RegisterApi_WithExplicitName_OverridesAttributeAndTypeName()
        {
            _host.RegisterApi(new PartsApi(_log), "Api.Parts");

            var engine = Load(Compile(@"
                trigger T { event OnPing(Unit u) { Api.Parts.Join(""grip""); } }"));
            Fire(engine);

            Assert.AreEqual(new[] { "join grip" }, _log);
            // под именем типа его быть не должно
            var r = Compile(@"trigger T { event OnPing(Unit u) { PartsApi.Join(""x""); } }");
            Assert.IsFalse(r.Success, Dump(r));
        }

        [Test]
        public void RegisterApi_WithoutName_KeepsTypeName()
        {
            _host.RegisterApi(new PartsApi(_log));

            var engine = Load(Compile(@"
                trigger T { event OnPing(Unit u) { PartsApi.Join(""grip""); } }"));
            Fire(engine);
            Assert.AreEqual(new[] { "join grip" }, _log);
        }

        // ===================================================================
        // Манифест: имя как было строкой, так и осталось
        // ===================================================================

        [Test]
        public void Manifest_RoundTrips_DottedNames()
        {
            _host.Api("Api.Weapon").Act("Cut", (float v) => { });
            _host.Api("Api.Weapon.Melee").Act("Swing", () => { });
            _host.Api("Flat").Act("Ping", () => { });

            string json = ApiManifest.Export(_host.Registry, 1);
            StringAssert.Contains("Api.Weapon.Melee", json);

            var imported = ApiManifest.Import(json, out _);
            Assert.IsTrue(imported.TryGetApi("Api.Weapon", out _));
            Assert.IsTrue(imported.TryGetApi("Api.Weapon.Melee", out _));
            Assert.IsTrue(imported.TryGetApi("Flat", out _));

            // префиксы восстановились как узлы пространства имён
            Assert.IsTrue(imported.IsApiNamespace("Api"));
            Assert.IsTrue(imported.IsApiNamespace("Api.Weapon"));
            Assert.IsFalse(imported.IsApiNamespace("Flat"));
        }

        [Test]
        public void Manifest_ImportedRegistry_CompilesDottedCalls()
        {
            _host.Api("Api.Weapon").Act("Cut", (float v) => { });
            var imported = ApiManifest.Import(ApiManifest.Export(_host.Registry, 1), out _);

            var r = ScriptCompiler.Compile(imported, 1, new List<ModuleSourceSet>
            {
                Mod("game", @"trigger T { event OnPing(Unit u) { Api.Weapon.Cut(1.0); } }"),
            });
            Assert.IsTrue(r.Success, Dump(r));
        }
    }
}
