using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Контракт бюджета исполнения и статистики: кривой мод не должен ронять
    /// движок, а активность триггеров — быть видимой снаружи.
    /// </summary>
    public sealed class BudgetTests
    {
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
        public void InfiniteLoop_KilledImmediately_EngineSurvives()
        {
            int pinged = 0;
            var host = new HostBuilder();
            host.Api("Api").Act("Ping", () => pinged++);
            var ev = host.Event("OnPing");

            // Loop объявлен первым — его обработчик стартует раньше и выедает бюджет
            var r = Compile(host.Registry, @"
                trigger Loop { event OnPing() { while (true) { } } }
                trigger Good { event OnPing() { Api.Ping(); } }");
            Assert.IsTrue(r.Success, Dump(r));

            var engine = new ScriptEngine(host.Registry);
            engine.TickInstructionBudget = 50_000;
            engine.MaxInstructionsPerFiberRun = 50_000;
            engine.Policy = BudgetPolicy.KillImmediately;
            string err = null;
            engine.OnError += m => err = m;
            engine.LoadProgram(r.Program);

            engine.Tick(0.016f);
            ev.Raise(engine);

            Assert.IsNotNull(err, "бесконечный цикл должен быть убит бюджетом");
            Assert.AreEqual(0, pinged, "Good отложен: Loop выел весь бюджет тика");

            // движок жив: следующий тик поднимает отложенный Good
            engine.Tick(0.016f);
            Assert.AreEqual(1, pinged);
        }

        [Test]
        public void CarryOver_DefersInsteadOfKilling()
        {
            var host = new HostBuilder();
            host.Api("Api").Act("Noop", () => { });
            var ev = host.Event("OnPing");

            var r = Compile(host.Registry, @"trigger Loop { event OnPing() { while (true) { } } }");
            Assert.IsTrue(r.Success, Dump(r));

            var engine = new ScriptEngine(host.Registry);
            engine.TickInstructionBudget = 30_000;
            engine.MaxInstructionsPerFiberRun = 30_000;
            engine.Policy = BudgetPolicy.CarryOver; // только переносить
            bool killed = false;
            engine.OnError += _ => killed = true;
            engine.LoadProgram(r.Program);

            engine.Tick(0.016f);
            ev.Raise(engine);

            var s = engine.GetStats();
            Assert.IsFalse(killed, "CarryOver не должен убивать");
            Assert.AreEqual(1, s.LiveFibers, "файбер жив и перенесён");
            Assert.Greater(s.FibersDeferredThisTick, 0);
        }

        [Test]
        public void Stats_ExposeTriggerActivity()
        {
            var host = new HostBuilder();
            host.Api("Api").Act("Ping", () => { });
            var ev = host.Event("OnPing");

            var r = Compile(host.Registry, @"trigger T { event OnPing() { Api.Ping(); } }");
            Assert.IsTrue(r.Success, Dump(r));

            var engine = new ScriptEngine(host.Registry);
            engine.LoadProgram(r.Program);

            engine.Tick(0.016f);
            ev.Raise(engine);
            ev.Raise(engine);

            var stats = engine.GetStats();
            Assert.AreEqual(2, stats.EventsRaisedThisTick);
            Assert.AreEqual(2, stats.HandlerInvocationsThisTick);

            var ts = engine.GetTriggerStats();
            Assert.AreEqual(1, ts.Count);
            Assert.AreEqual("T", ts[0].Name);
            Assert.AreEqual(2, ts[0].TimesFired);
            Assert.AreEqual(0, ts[0].FibersAlive, "оба обработчика завершились мгновенно");
        }

        [Test]
        public void Profiling_AttributesInstructionsToTrigger()
        {
            var host = new HostBuilder();
            var ev = host.Event("OnPing");

            var r = Compile(host.Registry, @"trigger T {
                event OnPing() { for i in 0..100 { } }
            }");
            Assert.IsTrue(r.Success, Dump(r));

            var engine = new ScriptEngine(host.Registry);
            engine.EnableProfiling = true;
            engine.LoadProgram(r.Program);

            engine.Tick(0.016f);
            ev.Raise(engine);

            var ts = engine.GetTriggerStats();
            Assert.Greater(ts[0].InstructionsTotal, 0, "профайлинг должен приписать инструкции триггеру");
        }
    }
}
