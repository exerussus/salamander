using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Защита от залипшего цикла: while(true) без эффективной кооперативной паузы
    /// (в т.ч. когда wait until мгновенно истинно) обязан убиваться НЕМЕДЛЕННО и
    /// не вешать тик, а честный цикл с настоящим wait — жить.
    /// </summary>
    public sealed class SpinGuardTests
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
            // сущность с булевым свойством, которым управляет тест
            _host.Class<Unit>().Prop("ready", u => u.Ready);
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));
            _onPing = _host.Event<Unit>("OnPing");
        }

        private CompilationResult Compile(string body)
        {
            var src = "trigger T { event OnPing(Unit u) { " + body + " } }";
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "t", ApiVersion = 1, Sources = new[] { "t.sal" } },
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

        // while(true) БЕЗ wait вообще — убить за один тик, не вешая.
        [Test]
        public void InfiniteLoop_NoWait_KilledInOneTick()
        {
            var e = Load(Compile(@"int x = 0; while (true) { x = x + 1; }"), out var errors);
            e.Tick(0.016f);          // не должно зависнуть
            _onPing.Raise(e, new Unit());

            Assert.AreEqual(1, errors.Count, "залипший цикл убит с ошибкой");
            StringAssert.Contains("цикл без ожидания", errors[0]);
            Assert.AreEqual(0, e.GetStats().LiveFibers, "файбер не остался висеть");
        }

        // while(true) с wait until, который ИСТИНЕН СРАЗУ — тоже спин, тоже убить.
        [Test]
        public void InfiniteLoop_WaitUntilAlwaysTrue_Killed()
        {
            // u.ready == true с самого начала → wait until проскакивает мгновенно
            var e = Load(Compile(@"while (true) { wait until u.ready; }"), out var errors);
            e.Tick(0.016f);
            _onPing.Raise(e, new Unit { Ready = true });

            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains("цикл без ожидания", errors[0]);
            Assert.AreEqual(0, e.GetStats().LiveFibers);
        }

        // Здоровый цикл: wait until станет истинным ПОЗЖЕ — файбер спит, живёт, доходит.
        [Test]
        public void HealthyLoop_WaitUntilBecomesTrue_Survives()
        {
            var target = new Unit { Ready = false };
            var e = Load(Compile(@"wait until u.ready; Api.Note(""готово"");"), out var errors);
            e.Tick(0.016f);
            _onPing.Raise(e, target);

            // условие ложно — файбер спит, ошибок нет, лога ещё нет
            Assert.AreEqual(0, errors.Count);
            Assert.AreEqual(0, _log.Count);
            Assert.AreEqual(1, e.GetStats().LiveFibers, "файбер жив и ждёт");

            e.Tick(0.016f); // всё ещё ложно
            Assert.AreEqual(0, _log.Count);

            target.Ready = true;
            e.Tick(0.016f); // теперь истинно — просыпается и доходит
            Assert.AreEqual(new[] { "готово" }, _log);
            Assert.AreEqual(0, e.GetStats().LiveFibers);
        }

        // Здоровый бесконечный цикл с настоящим wait: живёт тик за тиком, не убивается.
        [Test]
        public void HealthyLoop_WithRealWait_RunsAcrossTicks()
        {
            var e = Load(Compile(@"while (true) { Api.Note(""tick""); wait 1.0; }"), out var errors);
            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());
            Assert.AreEqual(new[] { "tick" }, _log, "первый оборот до wait");

            e.Tick(1.1f); // проснулся — ещё оборот
            e.Tick(1.1f);
            Assert.AreEqual(new[] { "tick", "tick", "tick" }, _log);
            Assert.AreEqual(0, errors.Count, "честный цикл не тронут");
            Assert.AreEqual(1, e.GetStats().LiveFibers, "живёт дальше");
        }

        // Счётчик спина сбрасывается на wait: длинная работа С паузами не ложно-срабатывает.
        [Test]
        public void WorkWithPauses_NotFlaggedAsStuck()
        {
            // много оборотов, но каждый отдаёт кадр — не спин
            var e = Load(Compile(@"int i = 0; while (i < 3) { i = i + 1; wait 0.5; } Api.Note(""ok"");"),
                         out var errors);
            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());
            e.Tick(0.6f); e.Tick(0.6f); e.Tick(0.6f);
            Assert.AreEqual(new[] { "ok" }, _log);
            Assert.AreEqual(0, errors.Count);
        }
    }
}
