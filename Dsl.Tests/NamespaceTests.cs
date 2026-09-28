using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using Dsl.Tooling;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// namespace A.B { ... }: пространство имён — часть личности объявления.
    /// Блоки сливаются по ПОЛНОМУ имени, поэтому переопределить сущность из
    /// namespace можно только из того же namespace; одноимённое глобальное
    /// объявление — другая сущность. Снаружи — "A.B.Имя", внутри — коротко.
    /// </summary>
    public sealed class NamespaceTests
    {
        public sealed class Unit { public string Name; }
        public enum Team { Red, Blue }

        private HostBuilder _host;
        private EventRef<Unit> _onPing;
        private EventRef<Unit, float> _onHit;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _host = new HostBuilder();
            _host.Class<Unit>().Prop("name", u => u.Name);
            _host.Enum<Team>();
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));
            _onPing = _host.Event<Unit>("OnPing");
            _onHit = _host.Event<Unit, float>("OnHit");
        }

        private static ModuleSourceSet Mod(string name, string src, params string[] deps)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest
                {
                    Name = name, ApiVersion = 1,
                    Dependencies = deps, Sources = new[] { name + ".sal" },
                },
            };
            set.Files.Add((name + "/" + name + ".sal", src));
            return set;
        }

        private CompilationResult Compile(params ModuleSourceSet[] mods)
            => ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet>(mods));

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

        private List<string> Ping(ScriptEngine engine)
        {
            _onPing.Raise(engine, new Unit());
            return _log;
        }

        // ===== разрешение имён =====

        [Test]
        public void FullNameOutside_ShortNameInside()
        {
            var engine = Load(Compile(Mod("game", @"
                namespace Mods {
                    class Balance {
                        int hp = 5;
                        func Twice() -> int { return hp * 2; }
                    }
                    class Report {
                        func Line() -> string { return $""{Balance.hp}/{Balance.Twice()}""; }
                    }
                }
                trigger T {
                    event OnPing(Unit u) {
                        Api.Note(Mods.Report.Line());
                        Api.Note($""{Mods.Balance.hp + Mods.Balance.Twice()}"");
                    }
                }")));

            Assert.AreEqual(new[] { "5/10", "15" }, Ping(engine));
        }

        [Test]
        public void NestedBlocks_EqualDottedPath()
        {
            var engine = Load(Compile(Mod("game", @"
                namespace A { namespace B { class X { int v = 1; } } }
                namespace A.B { class X { int v = 2; } }
                trigger T { event OnPing(Unit u) { Api.Note($""{A.B.X.v}""); } }")));

            Assert.AreEqual(new[] { "2" }, Ping(engine), "оба блока — одна сущность A.B.X");
        }

        [Test]
        public void RelativeNames_ResolveThroughEnclosingNamespaces()
        {
            var engine = Load(Compile(Mod("game", @"
                namespace Mods.Core { class Cfg { int k = 3; } }
                namespace Mods {
                    class Top { int t = 4; }
                    namespace Buffs {
                        class Use {
                            // Core — это Mods.Core, Top — это Mods.Top
                            func Sum() -> int { return Core.Cfg.k + Top.t + Mods.Core.Cfg.k; }
                        }
                    }
                }
                trigger T { event OnPing(Unit u) { Api.Note($""{Mods.Buffs.Use.Sum()}""); } }")));

            Assert.AreEqual(new[] { "10" }, Ping(engine));
        }

        [Test]
        public void InnerName_ShadowsGlobal()
        {
            var engine = Load(Compile(Mod("game", @"
                class Cfg { int v = 1; }
                namespace Mods {
                    class Cfg { int v = 2; }
                    class Probe { func Inner() -> int { return Cfg.v; } }
                }
                trigger T { event OnPing(Unit u) { Api.Note($""{Cfg.v} {Mods.Probe.Inner()} {Mods.Cfg.v}""); } }")));

            Assert.AreEqual(new[] { "1 2 2" }, Ping(engine));
        }

        [Test]
        public void InnerName_ShadowsHostName_OnlyInsideNamespace()
        {
            // внутри Mods имя Api — свой класс; снаружи — по-прежнему API игры
            var engine = Load(Compile(Mod("game", @"
                namespace Mods {
                    class Api { func Tag() -> string { return ""mod""; } }
                    class Probe { func Get() -> string { return Api.Tag(); } }
                }
                trigger T { event OnPing(Unit u) { Api.Note(Mods.Probe.Get()); } }")));

            Assert.AreEqual(new[] { "mod" }, Ping(engine));
        }

        // ===== личность и переопределение =====

        [Test]
        public void SameShortName_GlobalAndNamespaced_AreDifferentEntities()
        {
            var engine = Load(Compile(Mod("game", @"
                class Balance { int hp = 1; }
                namespace Mods { class Balance { int hp = 2; } }
                trigger T { event OnPing(Unit u) { Api.Note($""{Balance.hp} {Mods.Balance.hp}""); } }")));

            Assert.AreEqual(new[] { "1 2" }, Ping(engine), "не слились — разные полные имена");
        }

        [Test]
        public void CrossModule_OverrideOnlyThroughSameNamespace()
        {
            var baseMod = Mod("base", @"
                namespace ExerussusMods {
                    class Balance { int hp = 1; func Get() -> int { return hp; } }
                    trigger Hello { event OnPing(Unit u) { Api.Note($""hello {Balance.Get()}""); } }
                }");
            // тот же namespace — патч; глобальные блоки с теми же короткими именами — чужие сущности
            var patch = Mod("patch", @"
                namespace ExerussusMods {
                    class Balance { int hp = 7; }
                    trigger Hello { event OnPing(Unit u) { Api.Note($""patched {Balance.Get()}""); } }
                }
                class Balance { int hp = 99; }
                trigger Hello { event OnPing(Unit u) { Api.Note(""global""); } }", "base");

            var engine = Load(Compile(baseMod, patch));
            var log = Ping(engine);
            CollectionAssert.AreEquivalent(new[] { "patched 7", "global" }, log);
        }

        [Test]
        public void MemberLayers_WorkInsideNamespace()
        {
            var baseMod = Mod("base", @"
                namespace Mods { trigger T { event OnPing(Unit u) { Api.Note(""core""); } } }");
            var patch = Mod("patch", @"
                namespace Mods { trigger T { after event OnPing(Unit u) { Api.Note(""after""); } } }", "base");

            var engine = Load(Compile(baseMod, patch));
            Assert.AreEqual(new[] { "core", "after" }, Ping(engine));
        }

        // ===== енумы и типы =====

        [Test]
        public void Enums_TypesConstsAndValues()
        {
            var engine = Load(Compile(Mod("game", @"
                namespace Mods {
                    enum Kind { Fire, Ice }
                    class Cfg {
                        const Kind DEF = Kind.Ice;
                        Kind cur = Kind.Fire;
                        func Name(Kind k) -> string { if (k == Kind.Fire) { return ""fire""; } return ""ice""; }
                    }
                }
                class Outer {
                    const Mods.Kind K = Mods.Kind.Fire;
                    Mods.Kind[] all = [Mods.Kind.Fire, Mods.Kind.Ice];
                    Map<Mods.Kind, int> w = new Map<Mods.Kind, int>();
                }
                trigger T {
                    event OnPing(Unit u) {
                        Mods.Kind k = Mods.Cfg.DEF;
                        Api.Note(Mods.Cfg.Name(k));
                        Api.Note(Mods.Cfg.Name(Outer.K));
                        Api.Note(Mods.Cfg.Name(Mods.Cfg.cur));
                        Api.Note($""{Outer.all.length}"");
                    }
                }")));

            Assert.AreEqual(new[] { "ice", "fire", "fire", "2" }, Ping(engine));
        }

        [Test]
        public void NamespacedEnum_ShadowsHostEnumInside()
        {
            var r = Compile(Mod("game", @"
                namespace Mods {
                    enum Team { Green }
                    class C { Team t = Team.Green; }
                }
                class G { Team t = Team.Red; }
                trigger T { event OnPing(Unit u) { } }"));
            Assert.IsTrue(r.Success, Dump(r));
        }

        // ===== Engine.* и рантайм =====

        [Test]
        public void EngineArgs_TriggerAndListener_ByFullName()
        {
            var engine = Load(Compile(Mod("game", @"
                namespace Mods {
                    disabled trigger Late { event OnPing(Unit u) { Api.Note(""late""); } }
                    listener W { event OnHit(Unit u, float d) { Api.Note(""hit""); } }
                }
                trigger Boot {
                    event OnHit(Unit u, float d) {
                        if (!Engine.IsTriggerEnabled(Mods.Late)) {
                            Engine.EnableTrigger(Mods.Late);
                            Engine.Attach(Mods.W, u);
                        }
                    }
                    event OnPing(Unit u) {
                        if (Engine.TriggerExists(""Mods.Late"") && Engine.ClassExists(""Mods.Nope"") == false) { Api.Note(""exists""); }
                    }
                }")));

            var a = new Unit { Name = "A" };
            engine.Entities.Register(a);
            _onHit.Raise(engine, a, 1f);   // включает Late и подписывает W
            _onHit.Raise(engine, a, 1f);   // W срабатывает
            _onPing.Raise(engine, a);
            CollectionAssert.AreEquivalent(new[] { "hit", "exists", "late" }, _log);
        }

        [Test]
        public void Runtime_UsesFullNames()
        {
            var r = Compile(Mod("game", @"
                namespace Mods { trigger T { event OnPing(Unit u) { } } }
                trigger T { event OnPing(Unit u) { } }"));
            Assert.IsTrue(r.Success, Dump(r));
            var names = new List<string>();
            foreach (var t in r.Program.Triggers) names.Add(t.Name);
            CollectionAssert.AreEquivalent(new[] { "Mods.T", "T" }, names);
        }

        private sealed class Resolver : ISaveEntityResolver
        {
            public long GetStableId(object entity) => 0;
            public object ResolveStableId(long id) => null;
        }

        [Test]
        public void SaveLoad_KeepsNamespacedAndGlobalStateApart()
        {
            var r = Compile(Mod("game", @"
                namespace Mods {
                    trigger T { int n = 0; event OnPing(Unit u) { n = n + 10; Api.Note($""m{n}""); } }
                    class Cfg { int v = 0; }
                }
                trigger T { int n = 0; event OnPing(Unit u) { n = n + 1; Mods.Cfg.v = Mods.Cfg.v + 100; Api.Note($""g{n} {Mods.Cfg.v}""); } }"));
            Assert.IsTrue(r.Success, Dump(r));

            var e1 = Load(r);
            _onPing.Raise(e1, new Unit());
            byte[] save = e1.SaveState(new Resolver());

            _log.Clear();
            var e2 = new ScriptEngine(_host.Registry);
            e2.OnError += m => Assert.Fail(m);
            e2.LoadProgram(r.Program);
            e2.LoadState(save, new Resolver());
            e2.Tick(0.016f);
            _onPing.Raise(e2, new Unit());
            CollectionAssert.AreEquivalent(new[] { "m20", "g2 200" }, _log);
        }

        [Test]
        public void ModuleQualified_NamespacePath()
        {
            var baseMod = Mod("base", @"namespace Mods { class Cfg { int v = 4; } }");
            var game = Mod("game", @"
                trigger T { event OnPing(Unit u) { Api.Note($""{base::Mods.Cfg.v}""); } }", "base");
            var engine = Load(Compile(baseMod, game));
            Assert.AreEqual(new[] { "4" }, Ping(engine));
        }

        [Test]
        public void EngineMathAndHostApi_WorkInsideNamespace()
        {
            var engine = Load(Compile(Mod("game", @"
                namespace Mods.Deep {
                    trigger T {
                        event OnPing(Unit u) {
                            if (Engine.IsTriggerEnabled(T)) { Api.Note($""{Math.Max(2, 5)}""); }
                        }
                    }
                }")));
            Assert.AreEqual(new[] { "5" }, Ping(engine));
        }

        // ===== ошибки =====

        [Test]
        public void ParseErrors_RecoverWithoutCascade()
        {
            var noName = Compile(Mod("game", @"
                namespace { class A { int v = 1; } }
                trigger T { event OnPing(Unit u) { } }"));
            Assert.IsTrue(Has(noName, "E0310"), Dump(noName));
            Assert.AreEqual(1, noName.Diagnostics.Count, "одна ошибка, без каскада: " + Dump(noName));

            var noBrace = Compile(Mod("game", @"
                namespace Mods class A { int v = 1; }
                trigger T { event OnPing(Unit u) { } }"));
            Assert.IsTrue(Has(noBrace, "E0007"), Dump(noBrace));

            var unclosed = Compile(Mod("game", @"namespace Mods { class A { int v = 1; }"));
            Assert.IsTrue(Has(unclosed, "E0009"), Dump(unclosed));
        }

        [Test]
        public void Archetype_InsideNamespace_IsError()
        {
            _host.Archetype("spell").EventsOptional();
            var r = Compile(Mod("game", @"namespace Mods { spell fireball { } }"));
            Assert.IsTrue(Has(r, "E0311"), Dump(r));
        }

        [TestCase("Api")]
        [TestCase("Unit")]
        [TestCase("Team")]
        [TestCase("Engine")]
        [TestCase("Math")]
        [TestCase("int")]
        public void NamespaceRoot_ClashesWithHostOrBuiltin_IsError(string root)
        {
            var r = Compile(Mod("game", $"namespace {root} {{ class C {{ int v = 1; }} }}"));
            Assert.IsTrue(Has(r, "E0312"), Dump(r));
        }

        [Test]
        public void NestedSegment_MayReuseHostName_ButNotBuiltin()
        {
            Assert.IsTrue(Compile(Mod("game", "namespace Mods.Unit { class C { int v = 1; } }")).Success);
            Assert.IsTrue(Has(Compile(Mod("game", "namespace Mods.Engine { class C { int v = 1; } }")), "E0312"));
        }

        [Test]
        public void SymbolAndNamespace_SameFullName_IsError()
        {
            var r = Compile(Mod("game", @"
                class Mods { int v = 1; }
                namespace Mods { class C { int v = 1; } }"));
            Assert.IsTrue(Has(r, "E0313"), Dump(r));
        }

        [Test]
        public void MissingMember_IsError()
        {
            var r = Compile(Mod("game", @"
                namespace Mods { class C { int v = 1; } }
                trigger T { event OnPing(Unit u) { Api.Note($""{Mods.D.v}""); } }"));
            Assert.IsTrue(Has(r, "E0314"), Dump(r));
        }

        [Test]
        public void NamespaceAsValue_IsError()
        {
            var r = Compile(Mod("game", @"
                namespace Mods.Sub { class C { int v = 1; } }
                trigger T { event OnPing(Unit u) { var x = Mods.Sub; } }"));
            Assert.IsTrue(Has(r, "E0315"), Dump(r));
        }

        [Test]
        public void CallOnNamespace_IsError()
        {
            var r = Compile(Mod("game", @"
                namespace Mods { class C { func F() { } } }
                trigger T { event OnPing(Unit u) { Mods.F(); } }"));
            Assert.IsTrue(Has(r, "E0316"), Dump(r));
        }

        [Test]
        public void ShortNameOutsideNamespace_IsUnknown()
        {
            var r = Compile(Mod("game", @"
                namespace Mods { class C { int v = 1; } }
                trigger T { event OnPing(Unit u) { Api.Note($""{C.v}""); } }"));
            Assert.IsTrue(Has(r, "E0156"), Dump(r));
        }

        [Test]
        public void Namespace_NotVisibleWithoutDependency()
        {
            var a = Mod("a", @"namespace Mods { class C { int v = 1; } }");
            var b = Mod("b", @"trigger T { event OnPing(Unit u) { Api.Note($""{Mods.C.v}""); } }");
            var r = Compile(a, b);
            Assert.IsFalse(r.Success, "без зависимости namespace мода не виден");
            Assert.IsTrue(Has(r, "E0156"), Dump(r));
        }

        [Test]
        public void LocalShadowsNamespace()
        {
            var engine = Load(Compile(Mod("game", @"
                namespace Mods { class C { int v = 1; } }
                trigger T { event OnPing(Unit u) { string Mods = ""local""; Api.Note(Mods); } }")));
            Assert.AreEqual(new[] { "local" }, Ping(engine));
        }
    }
    /// <summary>Языковой сервис понимает namespace так же, как компилятор.</summary>
    public sealed class NamespaceToolingTests
    {
        private static readonly string[] Lines =
        {
            "namespace Mods {",                                                   // 1
            "    class Cfg { int v = 1; func F() -> int { return v; } }",         // 2
            "    namespace Buffs { enum Kind { Fire } }",                         // 3
            "    class User { func G() -> int { return Cfg.F(); } }",             // 4
            "}",                                                                  // 5
            "trigger T { event OnPing(Unit u) { int x = Mods.Cfg.v; } }",         // 6
        };

        private LanguageService _ls;

        [SetUp]
        public void SetUp()
        {
            string code = string.Join("\n", Lines) + "\n";
            _ls = new LanguageService { TextProvider = k => k == "a.sal" ? code : null };
            _ls.Index.Update("a.sal", code);
        }

        /// <summary>1-based колонка сразу после первого вхождения needle в строке.</summary>
        private static int After(int line, string needle) =>
            Lines[line - 1].IndexOf(needle, System.StringComparison.Ordinal) + needle.Length + 1;

        private static int At(int line, string needle) =>
            Lines[line - 1].IndexOf(needle, System.StringComparison.Ordinal) + 1;

        private static List<string> Labels(List<CompletionItem> items)
        {
            var r = new List<string>();
            foreach (var i in items) r.Add(i.Label);
            return r;
        }

        [Test]
        public void Index_KeepsFullNameAndNamespace()
        {
            Assert.IsTrue(_ls.Index.TryGet("a.sal", out var fs));
            var names = new List<string>();
            foreach (var d in fs.Decls) names.Add(d.Name);
            CollectionAssert.AreEqual(new[] { "Mods.Cfg", "Mods.Buffs.Kind", "Mods.User", "T" }, names);
            Assert.AreEqual("Mods.Buffs", fs.Decls[1].Namespace);
            Assert.AreEqual("Kind", fs.Decls[1].ShortName);
        }

        [Test]
        public void Completion_AfterNamespace_OffersDeclsAndSubNamespaces()
        {
            var labels = Labels(_ls.Complete("a.sal", 6, After(6, "Mods.")));
            CollectionAssert.IsSupersetOf(labels, new[] { "Cfg", "User", "Buffs" });
        }

        [Test]
        public void Completion_FullPathClass_OffersMembers()
        {
            var labels = Labels(_ls.Complete("a.sal", 6, After(6, "Mods.Cfg.")));
            CollectionAssert.IsSupersetOf(labels, new[] { "v", "F" });
        }

        [Test]
        public void Completion_ShortNameInsideNamespace_OffersMembers()
        {
            var labels = Labels(_ls.Complete("a.sal", 4, After(4, "Cfg.")));
            CollectionAssert.Contains(labels, "F");
        }

        [Test]
        public void Hover_And_Definition_ResolveFullPath()
        {
            int col = At(6, "Cfg.v") + 1;
            StringAssert.Contains("class Mods.Cfg", _ls.Hover("a.sal", 6, col));
            var loc = _ls.Definition("a.sal", 6, col);
            Assert.IsNotNull(loc);
            Assert.AreEqual(2, loc.Line);

            StringAssert.Contains("namespace Mods", _ls.Hover("a.sal", 6, At(6, "Mods") + 1));
        }

        [Test]
        public void Classifier_NamespaceKeywordAndSegments()
        {
            string code = string.Join("\n", Lines) + "\n";
            var spans = _ls.CreateClassifier().ClassifyDocument("a.sal", code);
            int TypeAt(int line, int col)
            {
                foreach (var s in spans) if (s.Line == line && s.Col == col) return s.Type;
                return -1;
            }
            Assert.AreEqual(SemanticClassifier.TtKeyword, TypeAt(1, 1), "namespace — ключевое слово");
            Assert.AreEqual(SemanticClassifier.TtNamespace, TypeAt(6, At(6, "Mods")));
            Assert.AreEqual(SemanticClassifier.TtClass, TypeAt(6, At(6, "Cfg.v")));
            Assert.AreEqual(SemanticClassifier.TtProperty, TypeAt(6, At(6, "v;")));
        }
    }
}
