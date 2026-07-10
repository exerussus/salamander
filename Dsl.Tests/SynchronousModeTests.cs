using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Синхронный режим модуля (execution: synchronous, декларируется в
    /// module.json): без файберов — wait/wait until/spawn запрещены компилятором,
    /// обработчик исполняется целиком до конца (карточная игра).
    /// </summary>
    public sealed class SynchronousModeTests
    {
        private HostRegistry _host;

        [SetUp]
        public void SetUp()
        {
            _host = new HostRegistry();
            _host.DefineEvent("OnPlay");
            _host.DefineMethod("Api", "Draw", new[] { TypeRef.Int }, TypeRef.Void, (ref CallContext c) => { });
        }

        private CompilationResult CompileSync(string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest
                {
                    Name = "cards", ApiVersion = 1, Execution = "synchronous",
                    Sources = new[] { "c.sal" },
                },
            };
            set.Files.Add(("cards/c.sal", src));
            return ScriptCompiler.Compile(_host, 1, new List<ModuleSourceSet> { set });
        }

        private static bool Has(CompilationResult r, string code)
        {
            foreach (var d in r.Diagnostics) if (d.Code == code) return true;
            return false;
        }

        [Test]
        public void Wait_IsForbidden_InSyncModule()
        {
            var r = CompileSync(@"trigger T { event OnPlay() { wait 1.0; } }");
            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0170"));
        }

        [Test]
        public void WaitUntil_IsForbidden_InSyncModule()
        {
            var r = CompileSync(@"trigger T { event OnPlay() { wait until true; } }");
            Assert.IsTrue(Has(r, "E0170"));
        }

        [Test]
        public void Spawn_IsForbidden_InSyncModule()
        {
            var r = CompileSync(@"
                trigger T { event OnPlay() { spawn Work(); } func Work() { Api.Draw(1); } }");
            Assert.IsTrue(Has(r, "E0170"));
        }

        [Test]
        public void SyncModule_WithoutAsync_CompilesFine()
        {
            // обычная синхронная логика: события, вызовы, циклы, action — всё можно
            var r = CompileSync(@"
                trigger Card {
                    int played = 0;
                    action Do() { played = played + 1; Api.Draw(played); }
                    event OnPlay() { Engine.ActivateTrigger(Card); }
                }");
            Assert.IsTrue(r.Success, DumpDiags(r));
        }

        [Test]
        public void SyncModule_CannotDependOnCooperative()
        {
            var core = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "core", ApiVersion = 1, Sources = new[] { "core.sal" } },
                // execution по умолчанию cooperative
            };
            core.Files.Add(("core/core.sal", "class Util { func Id(int x) -> int { return x; } }"));

            var cards = new ModuleSourceSet
            {
                Manifest = new ModuleManifest
                {
                    Name = "cards", ApiVersion = 1, Execution = "synchronous",
                    Dependencies = new[] { "core" }, Sources = new[] { "cards.sal" },
                },
            };
            cards.Files.Add(("cards/cards.sal", "trigger T { event OnPlay() { Api.Draw(1); } }"));

            var r = ScriptCompiler.Compile(_host, 1, new List<ModuleSourceSet> { core, cards });
            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0306"), "sync-модуль не должен зависеть от cooperative");
        }

        private static string DumpDiags(CompilationResult r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            return sb.ToString();
        }
    }
}
