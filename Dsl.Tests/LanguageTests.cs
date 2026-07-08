using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Контракт языка. Каждый тест — маленький сниппет + ожидание:
    /// либо конкретная ошибка компиляции, либо успешная сборка и
    /// проверяемое поведение в VM.
    /// </summary>
    public sealed class LanguageTests
    {
        private HostRegistry _host;
        private int _updateId;
        private int _damageId;
        private readonly List<float> _sunk = new List<float>();

        [SetUp]
        public void SetUp()
        {
            _sunk.Clear();
            _host = new HostRegistry();
            _updateId = _host.DefineEvent("Update", TypeRef.Float, TypeRef.Float);

            _host.DefineClass("Unit");
            var unitT = _host.ClassType("Unit");
            _host.DefineProperty("Unit", "health", TypeRef.Float, false,
                (ctx, o) => Variant.Float(((TestUnit)o).Health),
                (ctx, o, v) => ((TestUnit)o).Health = v.ToF());

            _host.DefineMethod("TestApi", "Sink", new[] { TypeRef.Float }, TypeRef.Void,
                (ref CallContext c) => _sunk.Add(c.Float(0)));

            _damageId = _host.DefineEvent("OnDamage", unitT, TypeRef.Float);
        }

        private sealed class TestUnit { public float Health; }

        private CompilationResult Compile(string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "test", ApiVersion = 1 },
            };
            set.Manifest.Sources = new[] { "test.sal" };
            set.Files.Add(("test/test.sal", src));
            return ScriptCompiler.Compile(_host, 1, new List<ModuleSourceSet> { set });
        }

        private static bool HasError(CompilationResult r, string code)
        {
            foreach (var d in r.Diagnostics)
                if (d.Code == code) return true;
            return false;
        }

        // ===== структурные правила =========================================

        [Test]
        public void EmptyTrigger_IsError()
        {
            var r = Compile("trigger T { int x = 0; func F() {} }");
            Assert.IsFalse(r.Success);
            Assert.IsTrue(HasError(r, "E0109"), "триггер без event обязан падать с E0109");
        }

        [Test]
        public void EventOutsideTrigger_IsError()
        {
            var r = Compile("class C { event Update(float t, float dt) {} }");
            Assert.IsTrue(HasError(r, "E0105"));
        }

        [Test]
        public void ActionMustBeNamedDo_AndSingle()
        {
            var r = Compile(@"trigger T {
                event Update(float t, float dt) {}
                action Boom() {}
            }");
            Assert.IsTrue(HasError(r, "E0117"));
        }

        [Test]
        public void EventSignatureMustMatchHost()
        {
            var r = Compile("trigger T { event Update(int t) {} }");
            Assert.IsTrue(HasError(r, "E0115"));
        }

        [Test]
        public void UnknownEvent_IsError()
        {
            var r = Compile("trigger T { event NoSuchEvent() {} }");
            Assert.IsTrue(HasError(r, "E0114"));
        }

        [Test]
        public void EngineNameIsReserved()
        {
            var r = Compile("class Engine { int x = 0; }");
            Assert.IsTrue(HasError(r, "E0100"));
        }

        // ===== типизация ====================================================

        [Test]
        public void TypeMismatch_IsError()
        {
            var r = Compile(@"class C { func F() { int a = ""hello""; } }
                trigger T { event Update(float t, float dt) {} }");
            Assert.IsTrue(HasError(r, "E0151"));
        }

        [Test]
        public void CompilesCleanExample()
        {
            var r = Compile(@"
                enum Phase { A, B }
                class C {
                    const float K = 2.5;
                    int counter = 0;
                    func Mul(float x) -> float { return x * K; }
                }
                trigger T {
                    event Update(float t, float dt) {
                        var v = C.Mul(2.0);
                        if (v > 4.0) { TestApi.Sink(v); }
                        for i in 0..3 { C.counter = C.counter + i; }
                        wait until C.counter >= 0;
                    }
                }");
            Assert.IsTrue(r.Success, DumpDiags(r));
        }

        // ===== исполнение ===================================================

        [Test]
        public void UpdateHandler_Runs_And_CallsHost()
        {
            var r = Compile(@"trigger T {
                event Update(float t, float dt) { TestApi.Sink(t + dt); }
            }");
            Assert.IsTrue(r.Success, DumpDiags(r));

            var engine = new ScriptEngine(_host);
            engine.LoadProgram(r.Program);
            engine.Tick(0.5f);
            engine.Raise(_updateId).AddFloat(1.0f).AddFloat(0.5f).Commit();

            Assert.AreEqual(1, _sunk.Count);
            Assert.AreEqual(1.5f, _sunk[0], 1e-4f);
        }

        [Test]
        public void Wait_SuspendsUntilTime()
        {
            var r = Compile(@"trigger T {
                event Update(float t, float dt) {
                    TestApi.Sink(1.0);
                    wait 1.0;
                    TestApi.Sink(2.0);
                }
            }");
            Assert.IsTrue(r.Success, DumpDiags(r));

            var engine = new ScriptEngine(_host);
            engine.LoadProgram(r.Program);

            engine.Tick(0.1f);
            engine.Raise(_updateId).AddFloat(0f).AddFloat(0.1f).Commit();
            Assert.AreEqual(1, _sunk.Count, "до wait — немедленно");

            engine.Tick(0.5f);
            Assert.AreEqual(1, _sunk.Count, "0.6с — ещё спит");

            engine.Tick(0.6f);
            Assert.AreEqual(2, _sunk.Count, "1.2с — проснулся");
        }

        [Test]
        public void IntDivision_StaysInt_FloatDivision_IsFloat()
        {
            var r = Compile(@"trigger T {
                event Update(float t, float dt) {
                    int a = 7 / 2;
                    TestApi.Sink(a);      // 3 (int-деление)
                    float x = 7;
                    TestApi.Sink(x / 2);  // 3.5 (float-деление, int аргумент расширен)
                }
            }");
            Assert.IsTrue(r.Success, DumpDiags(r));

            var engine = new ScriptEngine(_host);
            engine.LoadProgram(r.Program);
            engine.Raise(_updateId).AddFloat(0f).AddFloat(0f).Commit();

            Assert.AreEqual(3f, _sunk[0], 1e-4f);
            Assert.AreEqual(3.5f, _sunk[1], 1e-4f);
        }

        [Test]
        public void StaleEntityHandle_KillsFiber_NotEngine()
        {
            var r = Compile(@"trigger T {
                event OnDamage(Unit u, float amount) {
                    TestApi.Sink(u.health);
                    wait 1.0;
                    TestApi.Sink(u.health); // к этому моменту юнит мёртв — файбер умрёт
                }
            }");
            Assert.IsTrue(r.Success, DumpDiags(r));

            var engine = new ScriptEngine(_host);
            string error = null;
            engine.OnError += m => error = m;
            engine.LoadProgram(r.Program);

            var unit = new TestUnit { Health = 40f };
            engine.Raise(_damageId).AddEntity(unit).AddFloat(5f).Commit();
            Assert.AreEqual(1, _sunk.Count);

            engine.InvalidateEntity(unit); // хост сообщает о смерти
            engine.Tick(2.0f);             // файбер просыпается и трогает протухший хэндл

            Assert.AreEqual(1, _sunk.Count, "второй Sink не должен случиться");
            Assert.IsNotNull(error, "ошибка файбера должна дойти до хоста");

            // движок жив: новые события работают
            var unit2 = new TestUnit { Health = 10f };
            engine.Raise(_damageId).AddEntity(unit2).AddFloat(1f).Commit();
            Assert.AreEqual(2, _sunk.Count);
        }

        [Test]
        public void DisabledTrigger_DoesNotFire_UntilEnabled()
        {
            var r = Compile(@"disabled trigger T {
                event Update(float t, float dt) { TestApi.Sink(t); }
            }");
            Assert.IsTrue(r.Success, DumpDiags(r));

            var engine = new ScriptEngine(_host);
            engine.LoadProgram(r.Program);

            engine.Raise(_updateId).AddFloat(1f).AddFloat(0f).Commit();
            Assert.AreEqual(0, _sunk.Count);

            engine.SetTriggerEnabled("T", true);
            engine.Raise(_updateId).AddFloat(2f).AddFloat(0f).Commit();
            Assert.AreEqual(1, _sunk.Count);
        }

        private static string DumpDiags(CompilationResult r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            return sb.ToString();
        }
    }
}
