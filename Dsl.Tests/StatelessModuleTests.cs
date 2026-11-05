using System.Collections.Generic;
using Dsl.Codegen;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// "state": "none" в module.json — модуль без состояния: всё состояние у хоста,
    /// поэтому откат хода на стороне хоста полный. Флаг необязательный: модули без
    /// него (и с "full") компилируются ровно как раньше — это проверяется первым.
    /// </summary>
    public sealed class StatelessModuleTests
    {
        public sealed class Unit { }

        private HostBuilder _host;

        [SetUp]
        public void SetUp()
        {
            _host = new HostBuilder();
            _host.Class<Unit>();
            _host.Api("Api").Act("Log", (string s) => { });
            _host.Event<int>("OnTurn");
            _host.Event<Unit>("OnHit");
            _host.Archetype("spell").Const<int>("cost");
        }

        private CompilationResult Compile(params (string name, string exec, string state, string[] deps, string src)[] mods)
        {
            var list = new List<ModuleSourceSet>();
            foreach (var m in mods)
            {
                var set = new ModuleSourceSet
                {
                    Manifest = new ModuleManifest
                    {
                        Name = m.name, ApiVersion = 1, Execution = m.exec, State = m.state,
                        Dependencies = m.deps ?? new string[0], Sources = new[] { "a.sal" },
                    },
                };
                set.Files.Add((m.name + "/a.sal", m.src));
                list.Add(set);
            }
            return ScriptCompiler.Compile(_host.Registry, 1, list);
        }

        private CompilationResult One(string src, string state = "none", string exec = "synchronous") =>
            Compile(("srv", exec, state, null, src));

        private static bool Has(CompilationResult r, string code)
        {
            foreach (var d in r.Diagnostics) if (d.Code == code) return true;
            return false;
        }

        private static string Dump(CompilationResult r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            return sb.ToString();
        }

        private const string Stateful = @"
class Counter { int turns = 0; readonly List<int> seen = new List<int>(); }
trigger T { int played = 0; event OnTurn(int n) { played = played + 1; Counter.turns = Counter.turns + 1; Counter.seen.Add(n); Engine.DisableTrigger(T); } }
listener L { int hits = 0; event OnHit(Unit u) { hits = hits + 1; } }
spell fireball { int cost = 3; int casts = 0; }";

        // ----- совместимость: без флага всё как раньше -----

        [Test]
        public void WithoutFlag_StatefulCodeCompilesAsBefore()
        {
            var r = One(Stateful, state: null, exec: "cooperative");
            Assert.IsTrue(r.Success, Dump(r));
            Assert.IsFalse(r.Program.AllModulesStateless);
        }

        [Test]
        public void ExplicitFull_IsSameAsDefault()
        {
            var r = One(Stateful, state: "full");
            Assert.IsTrue(r.Success, Dump(r));
        }

        // ----- манифест -----

        [Test]
        public void UnknownStateValue_IsE0307()
        {
            Assert.IsTrue(Has(One("trigger T { event OnTurn(int n) { } }", state: "stateless"), "E0307"));
        }

        [Test]
        public void Stateless_RequiresSynchronous_E0308()
        {
            Assert.IsTrue(Has(One("trigger T { event OnTurn(int n) { } }", exec: "cooperative"), "E0308"));
        }

        [Test]
        public void Stateless_CannotDependOnStateful_E0309()
        {
            var r = Compile(
                ("lib", "synchronous", null, null, "class Lib { func F() { } }"),
                ("srv", "synchronous", "none", new[] { "lib" }, "trigger T { event OnTurn(int n) { Lib.F(); } }"));
            Assert.IsTrue(Has(r, "E0309"), Dump(r));
        }

        [Test]
        public void StatefulMayDependOnStateless()
        {
            var r = Compile(
                ("core", "synchronous", "none", null, "class Rules { func Cost(int n) -> int { return n * 2; } }"),
                ("game", "cooperative", null, new[] { "core" }, "trigger G { int x = 0; event OnTurn(int n) { x = Rules.Cost(n); } }"));
            Assert.IsTrue(r.Success, Dump(r));
        }

        // ----- поля -----

        [Test]
        public void MutableFields_AreE0253_InEveryEntityKind()
        {
            Assert.IsTrue(Has(One("class C { int x = 0; }"), "E0253"), "class");
            Assert.IsTrue(Has(One("trigger T { int x = 0; event OnTurn(int n) { } }"), "E0253"), "trigger");
            Assert.IsTrue(Has(One("listener L { int x = 0; event OnHit(Unit u) { } }"), "E0253"), "listener");
            Assert.IsTrue(Has(One("spell s { int casts = 0; }"), "E0253"), "архетип");
        }

        [Test]
        public void CollectionFields_AreE0253_EvenReadonly()
        {
            Assert.IsTrue(Has(One("class C { readonly List<int> l = new List<int>(); }"), "E0253"));
            Assert.IsTrue(Has(One("class C { readonly int[] a = [1, 2]; }"), "E0253"));
            Assert.IsTrue(Has(One("class C { readonly Map<string, int> m = new Map<string, int>(); }"), "E0253"));
        }

        [Test]
        public void ReadonlyScalars_Consts_ContractFields_Locals_AreFine()
        {
            var r = One(@"
class C { readonly int x = 2; const int k = 3; readonly string name = ""a""; }
spell fireball { int cost = 3; }
trigger T {
    readonly int base = 1;
    event OnTurn(int n) {
        List<int> tmp = new List<int>();
        tmp.Add(n + C.x + C.k + base);
        int s = 0;
        for v in tmp { s = s + v; }
        if (Engine.IsTriggerEnabled(T)) { Engine.ActivateTrigger(T); }
    }
    action Do() { Api.Log(C.name); }
}");
            Assert.IsTrue(r.Success, Dump(r));
            Assert.IsTrue(r.Program.AllModulesStateless);
        }

        [Test]
        public void StickyReadonly_FromEarlierBlock_IsAccepted()
        {
            var r = One("class C { readonly int x = 1; }\nclass C { int x = 2; }");
            Assert.IsTrue(r.Success, Dump(r));
        }

        // ----- состояние движка -----

        [Test]
        public void EngineStateOps_AreE0254()
        {
            Assert.IsTrue(Has(One("trigger T { event OnTurn(int n) { Engine.DisableTrigger(T); } }"), "E0254"));
            Assert.IsTrue(Has(One("trigger T { event OnTurn(int n) { Engine.DisableModule(\"srv\"); } }"), "E0254"));
            Assert.IsTrue(Has(One("listener L { event OnHit(Unit u) { } }\ntrigger T { event OnHit(Unit u) { Engine.Attach(L, u); } }"), "E0254"));
            Assert.IsTrue(Has(One("listener L { event OnHit(Unit u) { } }\ntrigger T { event OnHit(Unit u) { Engine.DetachAll(L, u); } }"), "E0254"));
        }

        // ----- смешивание с модулями с состоянием -----

        [Test]
        public void StatefulBlock_InStatelessEntity_IsE0255()
        {
            var r = Compile(
                ("core", "synchronous", "none", null, "trigger T { event OnTurn(int n) { Api.Log(\"core\"); } }"),
                ("mod", "synchronous", null, new[] { "core" }, "trigger T { after event OnTurn(int n) { Api.Log(\"mod\"); } }"));
            Assert.IsTrue(Has(r, "E0255"), Dump(r));
        }

        [Test]
        public void StatelessBlock_InStatefulEntity_IsE0255()
        {
            var r = Compile(
                ("base", "synchronous", null, null, "spell fireball { int cost = 3; int casts = 0; }"),
                ("patch", "synchronous", "none", null, "spell fireball { int cost = 5; }"));
            Assert.IsTrue(Has(r, "E0255"), Dump(r));
        }

        [Test]
        public void StatelessMods_OnStatelessCore_Compile()
        {
            var r = Compile(
                ("core", "synchronous", "none", null, "spell fireball { int cost = 3; }\ntrigger T { event OnTurn(int n) { } }"),
                ("balance", "synchronous", "none", new[] { "core" }, "spell fireball { int cost = 5; }\ntrigger T { after event OnTurn(int n) { Api.Log(\"b\"); } }"));
            Assert.IsTrue(r.Success, Dump(r));
            Assert.IsTrue(r.Program.AllModulesStateless);
        }

        // ----- рантайм -----

        [Test]
        public void HotSwap_RejectsStateModeChange()
        {
            var a = One("trigger T { event OnTurn(int n) { } }", state: "none");
            var b = One("trigger T { event OnTurn(int n) { } }", state: null);
            var e = new ScriptEngine(_host.Registry);
            e.LoadProgram(a.Program);
            var rep = e.TryHotSwap(b.Program);
            Assert.IsFalse(rep.Applied);
            StringAssert.Contains("state", rep.Reason);
        }
    }
}
