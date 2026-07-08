using System;
using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Регистрация типов по атрибутам ([SalamanderClass] и т.д.) должна давать
    /// ровно то же поведение, что ручной fluent, а описания брать с самого типа.
    /// </summary>
    public sealed class ReflectionTests
    {
        private enum RawTeam { Red, Blue }

        [SalamanderClass("сторона")]
        public enum Team { Red, Blue }

        [SalamanderClass("боевой юнит")]
        public sealed class Unit
        {
            private float _hp;
            [SalamanderProperty("текущее HP")]
            public float Health { get => _hp; set => _hp = value; }   // read-write
            [SalamanderProperty]
            public string Name { get; set; }                          // без doc
        }

        [SalamanderApi("операции над юнитом")]
        public sealed class UnitApi
        {
            // состояние экземпляра — у каждой комнаты своё, никакой статики
            public int HealsDone;

            [SalamanderMethod("лечит юнита")]
            public void Heal(
                [SalamanderParam("кого")] Unit target,
                [SalamanderParam("сколько")] float amount)
            {
                target.Health += amount;
                HealsDone++;
            }

            [SalamanderMethod("мало ли HP")]
            public bool IsLow(Unit u) => u.Health < 10f;
        }

        // сущность со статическим методом — теперь ошибка (методы = API)
        [SalamanderClass]
        public sealed class EntityWithMethod
        {
            [SalamanderProperty] public int X { get; set; }
            [SalamanderMethod] public static void Do() { }
        }

        [SalamanderClass]
        public sealed class OpaqueHandle { } // сущность без свойств — допустимо

        private static CompilationResult Compile(HostRegistry reg, string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "test", ApiVersion = 1, Sources = new[] { "t.sal" } },
            };
            set.Files.Add(("test/t.sal", src));
            return ScriptCompiler.Compile(reg, 1, new List<ModuleSourceSet> { set });
        }

        private static string Dump(CompilationResult r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            return sb.ToString();
        }

        [Test]
        public void Attributes_RegisterAndRun_LikeFluent()
        {
            var host = new HostBuilder();
            var apiInstance = new UnitApi();
            host.Register<Team>();
            host.Register<Unit>();
            host.RegisterApi(apiInstance);   // API из экземпляра
            var onHit = host.Event<Unit>("OnHit");

            var r = Compile(host.Registry, @"trigger T {
                event OnHit(Unit u) {
                    UnitApi.Heal(u, 5.0);
                    if (UnitApi.IsLow(u)) { u.Name = ""low""; }
                }
            }");
            Assert.IsTrue(r.Success, Dump(r));

            var engine = new ScriptEngine(host.Registry);
            engine.OnError += m => Assert.Fail(m);
            engine.LoadProgram(r.Program);

            var u = new Unit { Health = 3f, Name = "hero" };
            onHit.Raise(engine, u);

            Assert.AreEqual(8f, u.Health, 1e-4f);   // Heal(+5): 3 -> 8
            Assert.AreEqual("low", u.Name);         // IsLow(8) == true
            Assert.AreEqual(1, apiInstance.HealsDone, "метод биндится к экземпляру");
        }

        [Test]
        public void TwoInstances_HaveIndependentState_NoStatics()
        {
            // две «комнаты»: отдельные движки, отдельные экземпляры API
            Unit RunRoom(UnitApi api)
            {
                var host = new HostBuilder();
                host.Register<Unit>();
                host.RegisterApi(api);
                var onHit = host.Event<Unit>("OnHit");
                var r = Compile(host.Registry, @"trigger T { event OnHit(Unit u) { UnitApi.Heal(u, 1.0); } }");
                Assert.IsTrue(r.Success, Dump(r));
                var engine = new ScriptEngine(host.Registry);
                engine.LoadProgram(r.Program);
                var u = new Unit { Health = 0f };
                onHit.Raise(engine, u);
                return u;
            }

            var apiA = new UnitApi();
            var apiB = new UnitApi();
            RunRoom(apiA); RunRoom(apiA); // комната A: 2 лечения
            RunRoom(apiB);                // комната B: 1 лечение

            Assert.AreEqual(2, apiA.HealsDone);
            Assert.AreEqual(1, apiB.HealsDone); // состояние не общее
        }

        [Test]
        public void Attributes_FlowIntoManifest_WithParamNamesAndDocs()
        {
            var host = new HostBuilder();
            host.Register<Unit>();
            host.RegisterApi(new UnitApi());

            string json = ApiManifest.Export(host.Registry, 1);

            StringAssert.Contains("боевой юнит", json);         // summary класса-сущности
            StringAssert.Contains("текущее HP", json);          // doc свойства
            StringAssert.Contains("операции над юнитом", json); // summary API-класса
            StringAssert.Contains("лечит юнита", json);         // summary метода
            StringAssert.Contains("\"target\"", json);          // имя параметра из сигнатуры
            StringAssert.Contains("кого", json);                // doc параметра из [SalamanderParam]
        }

        [Test]
        public void MissingAttribute_Throws()
        {
            var host = new HostBuilder();
            var ex = Assert.Throws<ArgumentException>(() => host.Register(typeof(string)));
            StringAssert.Contains("SalamanderClass", ex.Message);
        }

        [Test]
        public void ApiTypePassedToRegister_Throws()
        {
            var host = new HostBuilder();
            var ex = Assert.Throws<ArgumentException>(() => host.Register(typeof(UnitApi)));
            StringAssert.Contains("RegisterApi", ex.Message);
        }

        [Test]
        public void EntityWithMethod_Throws()
        {
            var host = new HostBuilder();
            var ex = Assert.Throws<ArgumentException>(() => host.Register<EntityWithMethod>());
            StringAssert.Contains("SalamanderApi", ex.Message);
        }

        [Test]
        public void NonApiInstance_ToRegisterApi_Throws()
        {
            var host = new HostBuilder();
            var ex = Assert.Throws<ArgumentException>(() => host.RegisterApi(new OpaqueHandle()));
            StringAssert.Contains("SalamanderApi", ex.Message);
        }

        [Test]
        public void OpaqueEntity_NoProps_Registers()
        {
            var host = new HostBuilder();
            Assert.DoesNotThrow(() => host.Register<OpaqueHandle>());
            // тип виден в сигнатурах: событие с ним компилируется
            var ev = host.Event<OpaqueHandle>("OnSpawn");
            var r = Compile(host.Registry, @"trigger T { event OnSpawn(OpaqueHandle h) { } }");
            Assert.IsTrue(r.Success, Dump(r));
        }
    }
}
