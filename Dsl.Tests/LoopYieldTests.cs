using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// loop(cond) — кооперативный цикл: каждая итерация отдаёт кадр (yield в
    /// конце тела), спин невозможен by design. yield — самостоятельный оператор
    /// «уступить кадр и продолжить», доступный где угодно (в т.ч. чинит while).
    /// </summary>
    public sealed class LoopYieldTests
    {
        public sealed class Unit { public bool Ready; }

        private HostBuilder _host;
        private EventRef<Unit> _onPing;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _host = new HostBuilder();
            _host.Class<Unit>().Prop("ready", u => u.Ready);
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));
            _onPing = _host.Event<Unit>("OnPing");
        }

        private CompilationResult Compile(string body, bool sync = false)
        {
            var src = "trigger T { event OnPing(Unit u) { " + body + " } }";
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest
                {
                    Name = "t", ApiVersion = 1,
                    Execution = sync ? "synchronous" : "cooperative",
                    Sources = new[] { "t.sal" },
                },
            };
            set.Files.Add(("t/t.sal", src));
            return ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet> { set });
        }

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

        private ScriptEngine Load(CompilationResult r, out List<string> errors)
        {
            Assert.IsTrue(r.Success, Dump(r));
            var errs = new List<string>();
            var e = new ScriptEngine(_host.Registry);
            e.OnError += errs.Add;
            e.LoadProgram(r.Program);
            errors = errs;
            return e;
        }

        // loop(true) с телом-счётчиком: НЕ вешает кадр (в отличие от while(true)),
        // делает по одному обороту за тик, спин-гард молчит.
        [Test]
        public void LoopTrue_YieldsEachIteration_NotStuck()
        {
            var e = Load(Compile(@"int n = 0; loop (true) { n = n + 1; Api.Note($""{n}""); }"), out var errors);
            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());
            Assert.AreEqual(new[] { "1" }, _log, "первый оборот за первый запуск");
            Assert.AreEqual(0, errors.Count, "loop не ловится спин-гардом");
            Assert.AreEqual(1, e.GetStats().LiveFibers, "файбер спит между оборотами");

            e.Tick(0.016f);
            e.Tick(0.016f);
            Assert.AreEqual(new[] { "1", "2", "3" }, _log, "по обороту за тик");
        }

        // loop с условием, которое станет ложным — завершается штатно.
        [Test]
        public void LoopWithCondition_Terminates()
        {
            var e = Load(Compile(@"int i = 0; loop (i < 3) { i = i + 1; } Api.Note($""done {i}"");"), out var errors);
            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());
            // 3 оборота = 3 кадра, потом условие ложно → выходим
            e.Tick(0.016f); e.Tick(0.016f); e.Tick(0.016f);
            Assert.AreEqual(new[] { "done 3" }, _log);
            Assert.AreEqual(0, errors.Count);
        }

        // continue в loop тоже отдаёт кадр (не лазейка для спина).
        [Test]
        public void LoopContinue_StillYields()
        {
            var e = Load(Compile(@"int n = 0; loop (true) { n = n + 1; if (n < 5) { continue; } Api.Note(""five""); }"),
                         out var errors);
            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());
            // каждый оборот (в т.ч. с continue) — один кадр; на 5-м пишем.
            // Raise выполняет оборот n=1, дальше по обороту за тик, значит до
            // n=5 нужно ровно 4 тика: на большем числе тиков loop(true) продолжит
            // крутиться и допишет "five" ещё раз за каждый лишний кадр.
            for (int k = 0; k < 4; k++) e.Tick(0.016f);
            Assert.AreEqual(new[] { "five" }, _log);
            Assert.AreEqual(0, errors.Count, "continue отдаёт кадр — спина нет");
        }

        // break из loop выходит сразу, без лишнего кадра.
        [Test]
        public void LoopBreak_ExitsImmediately()
        {
            var e = Load(Compile(@"loop (true) { Api.Note(""once""); break; } Api.Note(""after"");"), out var errors);
            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());
            // тело: Note("once") → break (выход без yield) → Note("after") в ТОМ ЖЕ кадре
            Assert.AreEqual(new[] { "once", "after" }, _log);
            Assert.AreEqual(0, e.GetStats().LiveFibers);
        }

        // yield как самостоятельный оператор в линейном коде: разбивает на кадры.
        [Test]
        public void Yield_AsStatement_SplitsAcrossFrames()
        {
            var e = Load(Compile(@"Api.Note(""a""); yield; Api.Note(""b""); yield; Api.Note(""c"");"), out var errors);
            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());
            Assert.AreEqual(new[] { "a" }, _log, "до первого yield");
            e.Tick(0.016f);
            Assert.AreEqual(new[] { "a", "b" }, _log);
            e.Tick(0.016f);
            Assert.AreEqual(new[] { "a", "b", "c" }, _log);
        }

        // yield спасает while(true) от спина: ручная уступка кадра.
        [Test]
        public void Yield_RescuesWhileFromSpin()
        {
            var e = Load(Compile(@"int n = 0; while (true) { n = n + 1; Api.Note($""{n}""); yield; }"), out var errors);
            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());
            Assert.AreEqual(new[] { "1" }, _log);
            Assert.AreEqual(0, errors.Count, "while с yield — не спин");
            e.Tick(0.016f);
            Assert.AreEqual(new[] { "1", "2" }, _log);
        }

        // loop внутри со спином (while без yield) — внутренний убивается гардом.
        [Test]
        public void SpinInsideLoop_InnerKilled()
        {
            var e = Load(Compile(@"loop (true) { while (true) { } }"), out var errors);
            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());
            Assert.AreEqual(1, errors.Count, "внутренний while без yield убит");
            StringAssert.Contains("цикл без ожидания", errors[0]);
        }

        // ===== синхронный модуль: loop/yield запрещены (уступают кадр) =====

        [Test]
        public void Loop_InSynchronousModule_IsError()
        {
            var r = Compile(@"loop (true) { }", sync: true);
            Assert.IsTrue(Has(r, "E0170"), Dump(r));
        }

        [Test]
        public void Yield_InSynchronousModule_IsError()
        {
            var r = Compile(@"yield;", sync: true);
            Assert.IsTrue(Has(r, "E0170"), Dump(r));
        }
    }
}
