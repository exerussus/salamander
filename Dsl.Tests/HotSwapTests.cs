using System.Collections.Generic;
using Dsl.Codegen;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Горячая замена тел функций (ScriptEngine.TryHotSwap): правка метода класса
    /// или обработчика архетипа доезжает в живой движок без перезагрузки —
    /// поля, подписки, флаги и файберы остаются. Правка раскладки (новое поле,
    /// член, сигнатура) отклоняется с причиной, и движок не меняется.
    /// </summary>
    public sealed class HotSwapTests
    {
        public sealed class Unit { public string Name; }

        private HostBuilder _host;
        private EventRef<Unit> _onPing;
        private ArchEventRef<Unit> _onBuild;
        private List<string> _log;
        private List<string> _errors;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _errors = new List<string>();
            _host = new HostBuilder();
            _host.Class<Unit>().Prop("name", u => u.Name);
            _host.Api("Ui").Act("Add", (string s) => _log.Add(s));
            _onPing = _host.Event<Unit>("OnPing");
            // окно UI собирается из метода архетипа — главный сценарий горячей правки
            _onBuild = _host.Archetype("window").Event<Unit>("OnBuild");
        }

        private CompiledProgram Compile(string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "game", ApiVersion = 1, Sources = new[] { "g.sal" } },
            };
            set.Files.Add(("game/g.sal", src));
            var r = ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet> { set });
            var sb = new System.Text.StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            Assert.IsTrue(r.Success, sb.ToString());
            return r.Program;
        }

        private ScriptEngine Load(string src)
        {
            var engine = new ScriptEngine(_host.Registry);
            engine.OnError += m => _errors.Add(m);
            engine.LoadProgram(Compile(src));
            engine.Tick(0.016f);
            return engine;
        }

        private HotSwapReport Swap(ScriptEngine e, string src, HotSwapFiberPolicy p = HotSwapFiberPolicy.FinishOnOldCode)
        {
            var rep = e.TryHotSwap(Compile(src), p);
            Assert.IsTrue(rep.Applied, rep.ToString());
            return rep;
        }

        private List<string> Take()
        {
            var r = new List<string>(_log);
            _log.Clear();
            return r;
        }

        [TearDown]
        public void NoRuntimeErrors() => Assert.IsEmpty(_errors, string.Join("\n", _errors));

        // ===== основной сценарий: окно из метода архетипа =====

        private const string WindowV1 = @"
            window main {
                int opened = 0;
                event OnBuild(Unit u) {
                    opened = opened + 1;
                    Ui.Add($""title {u.name} #{opened}"");
                    Body();
                }
                func Body() { Ui.Add(""button OK""); }
            }";

        // тот же контракт — поменялось только тело Body
        private const string WindowV2 = @"
            window main {
                int opened = 0;
                event OnBuild(Unit u) {
                    opened = opened + 1;
                    Ui.Add($""title {u.name} #{opened}"");
                    Body();
                }
                func Body() { Ui.Add(""button Cancel""); Ui.Add(""slider volume""); }
            }";

        [Test]
        public void ArchetypeMethod_BodySwap_KeepsState()
        {
            var e = Load(WindowV1);
            var u = new Unit { Name = "P" };
            _onBuild.Raise(e, "main", u);
            Assert.AreEqual(new[] { "title P #1", "button OK" }, Take());

            var rep = Swap(e, WindowV2);
            CollectionAssert.AreEqual(new[] { "main.Body" }, rep.ChangedFunctions, rep.ToString());

            _onBuild.Raise(e, "main", u);
            // поле opened пережило замену (#2), тело Body — новое
            Assert.AreEqual(new[] { "title P #2", "button Cancel", "slider volume" }, Take());
        }

        [Test]
        public void SameCode_NoChanges()
        {
            var e = Load(WindowV1);
            var rep = Swap(e, WindowV1);
            Assert.IsTrue(rep.NoChanges, rep.ToString());
        }

        [Test]
        public void ClassMethod_And_TriggerHandler_Swap()
        {
            const string v1 = @"
                namespace Mods { class Cfg { int calls = 0; func Label() -> string { calls = calls + 1; return $""v1 {calls}""; } } }
                trigger T { event OnPing(Unit u) { Ui.Add(Mods.Cfg.Label()); } }";
            const string v2 = @"
                namespace Mods { class Cfg { int calls = 0; func Label() -> string { calls = calls + 1; return $""v2 {calls}""; } } }
                trigger T { event OnPing(Unit u) { Ui.Add(""<"" + Mods.Cfg.Label() + "">""); } }";
            var e = Load(v1);
            _onPing.Raise(e, new Unit());
            var rep = Swap(e, v2);
            Assert.AreEqual(2, rep.ChangedFunctions.Count, rep.ToString());
            _onPing.Raise(e, new Unit());
            Assert.AreEqual(new[] { "v1 1", "<v2 2>" }, Take());
        }

        // ===== файберы внутри изменённой функции =====

        private const string WaitV1 = @"
            trigger T { event OnPing(Unit u) { Ui.Add(""a1""); wait 1.0; Ui.Add(""a2""); } }";
        private const string WaitV2 = @"
            trigger T { event OnPing(Unit u) { Ui.Add(""b1""); wait 1.0; Ui.Add(""b2""); Ui.Add(""b3""); } }";

        [Test]
        public void FiberInsideChangedFunction_FinishesOldVersion()
        {
            var e = Load(WaitV1);
            _onPing.Raise(e, new Unit());               // стоит в wait внутри старого тела
            var rep = Swap(e, WaitV2);
            Assert.AreEqual(1, rep.FramesOnOldCode, rep.ToString());

            _onPing.Raise(e, new Unit());               // новый вызов — новое тело
            e.Tick(1.1f);
            CollectionAssert.AreEquivalent(new[] { "a1", "b1", "a2", "b2", "b3" }, Take());
        }

        [Test]
        public void KillPolicy_KillsOnlyFibersInChangedFunction()
        {
            const string v1 = @"
                trigger T { event OnPing(Unit u) { Ui.Add(""a1""); wait 1.0; Ui.Add(""a2""); } }
                trigger Other { event OnPing(Unit u) { wait 1.0; Ui.Add(""other""); } }";
            const string v2 = @"
                trigger T { event OnPing(Unit u) { Ui.Add(""b1""); wait 1.0; Ui.Add(""b2""); } }
                trigger Other { event OnPing(Unit u) { wait 1.0; Ui.Add(""other""); } }";
            var e = Load(v1);
            _onPing.Raise(e, new Unit());
            var rep = Swap(e, v2, HotSwapFiberPolicy.Kill);
            Assert.AreEqual(1, rep.FibersKilled, rep.ToString());
            e.Tick(1.1f);
            Assert.AreEqual(new[] { "a1", "other" }, Take(), "a2 не выполнился, чужой файбер жив");
        }

        [Test]
        public void CallerFrame_ReturnsIntoOldCaller_CalleeSwappedInPlace()
        {
            // обработчик (не менялся) стоит в wait внутри Step (менялась)
            const string v1 = @"
                class W { func Step() { Ui.Add(""s1""); wait 1.0; Ui.Add(""s1 end""); } }
                trigger T { event OnPing(Unit u) { W.Step(); Ui.Add(""after""); W.Step(); } }";
            const string v2 = @"
                class W { func Step() { Ui.Add(""s2""); wait 1.0; Ui.Add(""s2 end""); } }
                trigger T { event OnPing(Unit u) { W.Step(); Ui.Add(""after""); W.Step(); } }";
            var e = Load(v1);
            _onPing.Raise(e, new Unit());
            Swap(e, v2);
            e.Tick(1.1f);   // старый Step доработал; второй вызов — уже новый Step
            e.Tick(1.1f);
            Assert.AreEqual(new[] { "s1", "s1 end", "after", "s2", "s2 end" }, Take());
        }

        [Test]
        public void TwoSwaps_FiberStillOnFirstRetiredBody()
        {
            const string v3 = @"
                trigger T { event OnPing(Unit u) { Ui.Add(""c1""); wait 1.0; Ui.Add(""c2""); } }";
            var e = Load(WaitV1);
            _onPing.Raise(e, new Unit());          // a1, ждёт в v1
            Swap(e, WaitV2);
            var rep = Swap(e, v3);                  // v1-тело всё ещё нужно файберу
            Assert.AreEqual(1, rep.FramesOnOldCode, rep.ToString());
            _onPing.Raise(e, new Unit());          // c1
            e.Tick(1.1f);
            CollectionAssert.AreEquivalent(new[] { "a1", "c1", "a2", "c2" }, Take());

            // файберов на старом коде нет — следующая замена хвост не тащит
            var rep2 = Swap(e, WaitV1);
            Assert.AreEqual(0, rep2.FramesOnOldCode);
        }

        // ===== литералы, строки, readonly =====

        [Test]
        public void NewStringLiterals_SurviveSweep()
        {
            var e = Load(WindowV1);
            Swap(e, WindowV2);                       // "button Cancel", "slider volume" — новые литералы
            e.Collect();                             // полный mark-and-sweep
            _onBuild.Raise(e, "main", new Unit { Name = "Q" });
            Assert.AreEqual(new[] { "title Q #1", "button Cancel", "slider volume" }, Take());
        }

        [Test]
        public void LinesOnlyShift_KeepsFibersOnNewChunk()
        {
            var e = Load(WaitV1);
            _onPing.Raise(e, new Unit());
            var rep = Swap(e, "\n\n\n" + WaitV1);    // код тот же, строки съехали
            Assert.AreEqual(0, rep.ChangedFunctions.Count, rep.ToString());
            Assert.Greater(rep.LinesOnlyUpdated, 0);
            Assert.AreEqual(0, rep.FramesOnOldCode);
            e.Tick(1.1f);
            Assert.AreEqual(new[] { "a1", "a2" }, Take());
        }

        [Test]
        public void ReadonlyInitChanged_Reinitialized_MutableKept()
        {
            const string v1 = @"
                window main {
                    readonly float width = 300.0;
                    int opened = 0;
                    event OnBuild(Unit u) { opened = opened + 1; Ui.Add($""{width} {opened}""); }
                }";
            const string v2 = @"
                window main {
                    readonly float width = 420.0;
                    int opened = 0;
                    event OnBuild(Unit u) { opened = opened + 1; Ui.Add($""{width} {opened}""); }
                }";
            var e = Load(v1);
            _onBuild.Raise(e, "main", new Unit());
            var rep = Swap(e, v2);
            Assert.IsTrue(rep.ReadOnlyFieldsReinitialized, rep.ToString());
            _onBuild.Raise(e, "main", new Unit());
            Assert.AreEqual(new[] { "300 1", "420 2" }, Take());
            Assert.IsTrue(e.TryGetArchetypeConst("window", "main", "width", out var w));
            Assert.AreEqual(420f, w.ToF(), 1e-6f, "хост тоже видит новое значение");
        }

        [Test]
        public void SaveLoad_AfterSwap_RestoresFibersAgainstSameProgram()
        {
            var e = Load(WaitV1);
            _onPing.Raise(e, new Unit());
            Swap(e, WaitV2);
            var res = new NullResolver();
            byte[] save = e.SaveState(res);
            var rep = e.LoadState(save, res);
            Assert.IsTrue(rep.FibersRestored);
            e.Tick(1.1f);
            Assert.AreEqual(new[] { "a1", "a2" }, Take(), "файбер со старым телом восстановлен");
        }

        private sealed class NullResolver : ISaveEntityResolver
        {
            public long GetStableId(object entity) => 0;
            public object ResolveStableId(long id) => null;
        }

        // ===== несовместимые правки отклоняются, движок не меняется =====

        [TestCase("window main { int opened = 0; int extra = 1; event OnBuild(Unit u) { Ui.Add(\"x\"); } func Body() { } }", "пол")]
        [TestCase("window main { int opened = 0; event OnBuild(Unit u) { Ui.Add(\"x\"); } func Body() { } func More() { } }", "функций")]
        [TestCase("window main { int opened = 0; event OnBuild(Unit u) { Ui.Add(\"x\"); } func Body(int a) { } }", "параметров")]
        [TestCase("window main { int opened = 0; event OnBuild(Unit u) { Ui.Add(\"x\"); } func Body() { } } window second { event OnBuild(Unit u) { } }", "функций")]
        public void LayoutChange_Rejected_EngineUntouched(string v2, string reasonPart)
        {
            var e = Load(WindowV1);
            _onBuild.Raise(e, "main", new Unit { Name = "P" });
            Take();

            var rep = e.TryHotSwap(Compile(v2));
            Assert.IsFalse(rep.Applied);
            StringAssert.Contains(reasonPart, rep.Reason);

            _onBuild.Raise(e, "main", new Unit { Name = "P" });
            Assert.AreEqual(new[] { "title P #2", "button OK" }, Take(), "работает прежняя программа с прежним состоянием");
        }

        [Test]
        public void RejectedFromInsideScript()
        {
            ScriptEngine e = null;
            CompiledProgram next = null;
            HotSwapReport inside = null;
            _host.Api("Dev").Act("Swap", () => { inside = e.TryHotSwap(next); });
            const string src = @"trigger T { event OnPing(Unit u) { Dev.Swap(); } }";
            e = Load(src);
            next = Compile(src);
            _onPing.Raise(e, new Unit());
            Assert.IsNotNull(inside);
            Assert.IsFalse(inside.Applied);
        }
    }
}
