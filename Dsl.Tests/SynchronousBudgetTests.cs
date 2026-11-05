using System.Collections.Generic;
using Dsl.Codegen;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Синхронный модуль обещает: обработчик исполняется целиком в момент Raise.
    /// Раньше бюджет тика это обещание нарушал — обработчик откладывался до
    /// следующего Tick или резался посередине, а сервер без Tick после ~5 млн
    /// инструкций молча переставал исполнять реакции. Здесь — регрессия на это
    /// и на обходные пути (кооперативный мод, дописавший wait в синхронную сущность).
    /// </summary>
    public sealed class SynchronousBudgetTests
    {
        private HostBuilder _host;
        private List<string> _log;
        private List<string> _errors;
        private List<string> _warns;
        private EventRef<int> _onPlay;
        private ArchEventRef<int> _onCast;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _errors = new List<string>();
            _warns = new List<string>();
            _host = new HostBuilder();
            _host.Api("Api").Act("Log", (string s) => _log.Add(s));
            _onPlay = _host.Event<int>("OnPlay");
            _onCast = _host.Archetype("card").Event<int>("OnCast");
        }

        private CompilationResult CompileRaw(params (string name, string exec, string[] deps, string src)[] mods)
        {
            var list = new List<ModuleSourceSet>();
            foreach (var m in mods)
            {
                var set = new ModuleSourceSet
                {
                    Manifest = new ModuleManifest
                    {
                        Name = m.name, ApiVersion = 1, Execution = m.exec,
                        Dependencies = m.deps ?? new string[0], Sources = new[] { "a.sal" },
                    },
                };
                set.Files.Add((m.name + "/a.sal", m.src));
                list.Add(set);
            }
            return ScriptCompiler.Compile(_host.Registry, 1, list);
        }

        private CompiledProgram Compile(params (string name, string exec, string[] deps, string src)[] mods)
        {
            var r = CompileRaw(mods);
            var sb = new System.Text.StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            Assert.IsTrue(r.Success, sb.ToString());
            return r.Program;
        }

        private ScriptEngine Load(CompiledProgram p)
        {
            var e = new ScriptEngine(_host.Registry);
            e.OnError += m => _errors.Add(m);
            e.OnWarn += m => _warns.Add(m);
            e.LoadProgram(p);
            return e;
        }

        private static bool Has(CompilationResult r, string code)
        {
            foreach (var d in r.Diagnostics) if (d.Code == code) return true;
            return false;
        }

        // ~2 700 инструкций на обработчик
        private const string Heavy =
            "trigger T { event OnPlay(int n) { Api.Log($\"start {n}\"); int s = 0; " +
            "for i in 0..300 { s = s + i; } Api.Log($\"end {n}\"); } }";

        [Test]
        public void SyncHandler_IsNotSplitOrDeferred_WhenTickBudgetIsTiny()
        {
            var e = Load(Compile(("cards", "synchronous", null, Heavy)));
            e.TickInstructionBudget = 1000; // меньше одного обработчика
            e.Tick(0.016f);

            _onPlay.Raise(e, 1); _log.Add("ret 1");
            _onPlay.Raise(e, 2); _log.Add("ret 2");
            _onPlay.Raise(e, 3); _log.Add("ret 3");

            CollectionAssert.AreEqual(new[]
            {
                "start 1", "end 1", "ret 1",
                "start 2", "end 2", "ret 2",
                "start 3", "end 3", "ret 3",
            }, _log);
            e.Tick(0.016f);
            Assert.AreEqual(9, _log.Count, "в следующем тике дорабатывать нечего");
            CollectionAssert.IsEmpty(_errors);
        }

        [Test]
        public void SyncHandlers_KeepRunning_WithoutTick()
        {
            var e = Load(Compile(("srv", "synchronous", null, Heavy)));
            for (int i = 0; i < 6000; i++) _onPlay.Raise(e, i); // ~16 млн инструкций > бюджета 5 млн
            Assert.AreEqual(12000, _log.Count);
            CollectionAssert.IsEmpty(_warns);
        }

        [Test]
        public void SyncArchetypeHandler_IsNotDeferred()
        {
            var e = Load(Compile(("cards", "synchronous", null,
                "card fire { event OnCast(int n) { int s = 0; for i in 0..300 { s = s + i; } Api.Log($\"cast {n}\"); } }")));
            e.TickInstructionBudget = 100;
            e.Tick(0.016f);
            int h = e.ResolveArchetype("card", "fire");
            _onCast.Raise(e, h, 1);
            _onCast.Raise(e, h, 2);
            CollectionAssert.AreEqual(new[] { "cast 1", "cast 2" }, _log);
        }

        [Test]
        public void CooperativeHandler_StillDefers_AndWarnsOnce()
        {
            var e = Load(Compile(("game", "cooperative", null, Heavy)));
            e.Tick(0.016f);
            _onPlay.Raise(e, 0);                         // калибровка: сколько стоит один обработчик
            long per = e.GetStats().InstructionsThisTick;
            _log.Clear();
            e.TickInstructionBudget = per * 3 / 2;       // полтора обработчика на тик
            e.Tick(0.016f);
            _onPlay.Raise(e, 1);
            _onPlay.Raise(e, 2);
            _onPlay.Raise(e, 3);
            // прежнее поведение кооперативного модуля: второй режется, третий ждёт
            CollectionAssert.AreEqual(new[] { "start 1", "end 1", "start 2" }, _log, string.Join("|", _log) + " // " + string.Join("|", _errors));
            Assert.AreEqual(1, _warns.Count, string.Join("\n", _warns));
            StringAssert.Contains("отложены до следующего Tick", _warns[0]);
            e.Tick(0.016f);
            CollectionAssert.Contains(_log, "end 2");
        }

        [Test]
        public void DeferWarning_MentionsMissingTick()
        {
            var e2 = Load(Compile(("game", "cooperative", null, Heavy)));
            e2.TickInstructionBudget = 1500;
            e2.LoadProgram(Compile(("game", "cooperative", null, Heavy))); // стартовый бюджет — 1500, Tick не было
            _onPlay.Raise(e2, 1);
            _onPlay.Raise(e2, 2);
            Assert.AreEqual(1, _warns.Count);
            StringAssert.Contains("Tick ни разу не вызывался", _warns[0]);
        }

        [Test]
        public void SyncRunaway_IsKilledWithTrace_NotCarriedOver()
        {
            var e = Load(Compile(("cards", "synchronous", null,
                "trigger T { event OnPlay(int n) { Api.Log(\"in\"); while (true) { n = n + 1; } } }")));
            e.Tick(0.016f);
            _onPlay.Raise(e, 1);
            Assert.AreEqual(1, _errors.Count);
            StringAssert.Contains("не уложился", _errors[0]);
            StringAssert.Contains("MaxInstructionsPerFiberRun", _errors[0]);
            e.Tick(0.016f);
            e.Tick(0.016f);
            Assert.AreEqual(1, _errors.Count, "файбер не должен продолжаться в следующих тиках");
            Assert.AreEqual(0, e.GetStats().LiveFibers);
        }

        [Test]
        public void SyncAction_RunsEvenWhenBudgetIsExhausted()
        {
            var e = Load(Compile(("cards", "synchronous", null,
                "trigger Card { action Do() { Api.Log(\"action\"); } event OnPlay(int n) { Engine.ActivateTrigger(Card); Api.Log(\"handler\"); } }\n" +
                "trigger Burn { event OnPlay(int n) { int s = 0; for i in 0..300 { s = s + i; } } }")));
            e.TickInstructionBudget = 50;
            e.Tick(0.016f);
            _onPlay.Raise(e, 1);
            CollectionAssert.AreEqual(new[] { "handler", "action" }, _log);
        }

        // ----- кооперативный мод не может разрезать синхронную сущность -----

        [Test]
        public void CooperativeLayer_WithWait_InSyncTrigger_IsE0252()
        {
            var r = CompileRaw(
                ("base", "synchronous", null, "trigger T { event OnPlay(int n) { Api.Log(\"core\"); } }"),
                ("mod", "cooperative", new[] { "base" }, "trigger T { after event OnPlay(int n) { wait 1.0; Api.Log(\"after\"); } }"));
            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0252"));
        }

        [Test]
        public void CooperativePatch_WithWaitUntil_InSyncArchetype_IsE0252()
        {
            var r = CompileRaw(
                ("base", "synchronous", null, "card fire { event OnCast(int n) { Api.Log(\"c\"); } }"),
                ("mod", "cooperative", new[] { "base" }, "card fire { before event OnCast(int n) { wait until true; } }"));
            Assert.IsTrue(Has(r, "E0252"));
        }

        [Test]
        public void CooperativePatch_WithLoop_InSyncClass_IsE0252()
        {
            var r = CompileRaw(
                ("base", "synchronous", null, "class Rules { func Step() { Api.Log(\"s\"); } }"),
                ("mod", "cooperative", new[] { "base" }, "class Rules { func Step() { loop (true) { Api.Log(\"x\"); } } }"));
            Assert.IsTrue(Has(r, "E0252"));
        }

        [Test]
        public void CooperativePatch_WithoutWait_InSyncTrigger_Compiles()
        {
            var e = Load(Compile(
                ("base", "synchronous", null, "trigger T { event OnPlay(int n) { Api.Log(\"core\"); } }"),
                ("mod", "cooperative", new[] { "base" }, "trigger T { after event OnPlay(int n) { Api.Log(\"after\"); } }")));
            e.TickInstructionBudget = 1;
            e.Tick(0.016f);
            _onPlay.Raise(e, 1);
            CollectionAssert.AreEqual(new[] { "core", "after" }, _log);
        }

        [Test]
        public void CooperativeTrigger_MayStillWait_InOwnEntity()
        {
            // правило касается только чужих синхронных сущностей
            var r = CompileRaw(
                ("base", "synchronous", null, "trigger T { event OnPlay(int n) { Api.Log(\"core\"); } }"),
                ("mod", "cooperative", new[] { "base" }, "trigger M { event OnPlay(int n) { wait 1.0; Api.Log(\"m\"); } }"));
            Assert.IsTrue(r.Success);
        }

        [Test]
        public void IndirectWait_FromCooperativeFunc_KillsSyncHandler()
        {
            var e = Load(Compile(
                ("base", "synchronous", null, "trigger T { event OnPlay(int n) { Api.Log(\"core\"); } }"),
                ("mod", "cooperative", new[] { "base" },
                    "class Pause { func Now() { wait 1.0; } }\n" +
                    "trigger T { after event OnPlay(int n) { Api.Log(\"before pause\"); Pause.Now(); Api.Log(\"after pause\"); } }")));
            e.Tick(0.016f);
            _onPlay.Raise(e, 1);
            CollectionAssert.AreEqual(new[] { "core", "before pause" }, _log);
            Assert.AreEqual(1, _errors.Count);
            StringAssert.Contains("дошёл до wait", _errors[0]);
            for (int i = 0; i < 100; i++) e.Tick(0.1f);
            CollectionAssert.DoesNotContain(_log, "after pause");
        }

        [Test]
        public void HotSwap_RejectsExecutionModeChange()
        {
            var e = Load(Compile(("m", "synchronous", null, "trigger T { event OnPlay(int n) { Api.Log(\"a\"); } }")));
            var rep = e.TryHotSwap(Compile(("m", "cooperative", null, "trigger T { event OnPlay(int n) { Api.Log(\"a\"); } }")));
            Assert.IsFalse(rep.Applied);
            StringAssert.Contains("режим исполнения", rep.Reason);
        }
    }
}
