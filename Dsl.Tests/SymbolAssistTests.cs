using System;
using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Tooling;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Подсказки по таблице символов компилятора (CompilationResult.Symbols):
    /// типы и сигнатуры скриптовых членов, «///»-описания, модули вне
    /// воркспейса (справочные: игра, зависимости мода) и видимость по
    /// зависимостям. В воркспейсе — только мод, модуль игры есть лишь в
    /// компиляции, как его и подкладывают IDE и LSP.
    /// </summary>
    public sealed class SymbolAssistTests
    {
        public sealed class Unit { public string Name; }

        private const string BaseSrc =
            "namespace Game {\n" +                                          // 1
            "    /// Баланс боя.\n" +                                       // 2
            "    /// Правится модами.\n" +                                  // 3
            "    class Balance {\n" +                                       // 4
            "        /// Здоровье босса.\n" +                               // 5
            "        int bossHp = 750;\n" +                                 // 6
            "        const int MAX = 3;\n" +                                // 7
            "        readonly float speed = 1.5;\n" +                       // 8
            "        /// Урон с учётом брони.\n" +                          // 9
            "        func Damage(int raw, float armor) -> int { return raw; }\n" + // 10
            "    }\n" +                                                     // 11
            "    enum Kind { Fire, Ice }\n" +                               // 12
            "}\n" +                                                         // 13
            "window main {\n" +                                             // 14
            "    int opened = 0;\n" +                                       // 15
            "    event OnBuild(Unit u) { Body(); }\n" +                     // 16
            "    /// Кнопки окна.\n" +                                      // 17
            "    func Body() { }\n" +                                       // 18
            "}\n";

        private const string OtherSrc = "class Secret { int x = 1; }\n";

        private static readonly string[] ModLines =
        {
            "trigger T {",                                                   // 1
            "    event OnPing(Unit u) {",                                    // 2
            "        int hp = Game.Balance.bossHp;",                         // 3
            "        int d = Game.Balance.Damage(1, 2.0);",                  // 4
            "        Game.Kind k = Game.Kind.Fire;",                         // 5
            "        Helper();",                                             // 6
            "    }",                                                         // 7
            "    /// Своя функция.",                                         // 8
            "    func Helper() -> string { return \"x\"; }",                 // 9
            "}",                                                             // 10
        };

        private LanguageService _ls;
        private string _modText;

        [SetUp]
        public void SetUp()
        {
            var host = new HostBuilder();
            host.Class<Unit>().Prop("name", u => u.Name);
            host.Event<Unit>("OnPing");
            host.Archetype("window").Event<Unit>("OnBuild");

            _modText = string.Join("\n", ModLines) + "\n";
            var modules = new List<ModuleSourceSet>
            {
                Mod("base", BaseSrc),
                Mod("other", OtherSrc),
                Mod("mod", _modText, "base"),
            };
            var result = ScriptCompiler.Compile(host.Registry, 1, modules);
            Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics));
            Assert.IsNotNull(result.Symbols);

            _ls = new LanguageService
            {
                TextProvider = k => k == "mod.sal" ? _modText : null,
                ModuleOfFile = k => k == "mod.sal" ? "mod" : null,
                FileOfLogical = logical => logical == "mod/mod.sal" ? "mod.sal" : "ref:" + logical,
            };
            _ls.UpdateSymbols(result);
            _ls.Index.Update("mod.sal", _modText); // в воркспейсе только мод
        }

        private static ModuleSourceSet Mod(string name, string src, params string[] deps)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = name, ApiVersion = 1, Dependencies = deps, Sources = new[] { name + ".sal" } },
            };
            set.Files.Add((name + "/" + name + ".sal", src));
            return set;
        }

        private static int After(int line, string needle) =>
            ModLines[line - 1].IndexOf(needle, StringComparison.Ordinal) + needle.Length + 1;

        private static int At(int line, string needle) =>
            ModLines[line - 1].IndexOf(needle, StringComparison.Ordinal) + 1;

        // ===== таблица символов =====

        [Test]
        public void Table_HasTypesSignaturesAndDocs()
        {
            var t = _ls.Symbols.Find("Game.Balance");
            Assert.IsNotNull(t);
            Assert.AreEqual("base", t.Module);
            Assert.AreEqual("base/base.sal", t.File);
            Assert.AreEqual(4, t.Line);
            Assert.AreEqual("Баланс боя.\nПравится модами.", t.Doc);

            Assert.AreEqual("int bossHp", t.FindMember("bossHp").Signature);
            Assert.AreEqual("Здоровье босса.", t.FindMember("bossHp").Doc);
            Assert.AreEqual("const int MAX = 3", t.FindMember("MAX").Signature);
            Assert.AreEqual("readonly float speed", t.FindMember("speed").Signature);
            var dmg = t.FindMember("Damage");
            Assert.AreEqual("func Damage(int raw, float armor) -> int", dmg.Signature);
            Assert.AreEqual("Урон с учётом брони.", dmg.Doc);
            Assert.AreEqual(10, dmg.Line);

            var win = _ls.Symbols.FindArchetype("window", "main");
            Assert.IsNotNull(win);
            Assert.AreEqual("event OnBuild(Unit u)", win.FindMember("OnBuild").Signature);
            Assert.AreEqual("Кнопки окна.", win.FindMember("Body").Doc);
        }

        // ===== автодополнение =====

        [Test]
        public void Completion_ReferenceModuleClass_MembersWithSignatures()
        {
            var items = _ls.Complete("mod.sal", 3, After(3, "Game.Balance."));
            var dmg = items.Find(i => i.Label == "Damage");
            Assert.IsNotNull(dmg, "класс из справочного модуля виден");
            Assert.AreEqual("func Damage(int raw, float armor) -> int", dmg.Detail);
            StringAssert.Contains("Урон с учётом брони.", dmg.Documentation);
            Assert.IsTrue(dmg.IsSnippet);
            StringAssert.Contains("${1:int raw}", dmg.InsertText);
            Assert.AreEqual("int bossHp", items.Find(i => i.Label == "bossHp").Detail);
            Assert.AreEqual(CompletionKind.Constant, items.Find(i => i.Label == "MAX").Kind);
        }

        [Test]
        public void Completion_ReferenceNamespace_And_Enum()
        {
            var ns = new List<string>();
            foreach (var i in _ls.Complete("mod.sal", 3, After(3, "Game."))) ns.Add(i.Label);
            CollectionAssert.IsSupersetOf(ns, new[] { "Balance", "Kind" });

            var en = new List<string>();
            foreach (var i in _ls.Complete("mod.sal", 5, After(5, "= Game.Kind."))) en.Add(i.Label);
            CollectionAssert.IsSupersetOf(en, new[] { "Fire", "Ice" });
        }

        [Test]
        public void Completion_Bare_OwnMembersAndVisibleNamesOnly()
        {
            var labels = new List<string>();
            foreach (var i in _ls.Complete("mod.sal", 6, 9)) labels.Add(i.Label);
            CollectionAssert.Contains(labels, "Helper", "своя функция");
            CollectionAssert.Contains(labels, "Game", "корень namespace из зависимости");
            CollectionAssert.DoesNotContain(labels, "Secret", "модуль other — не зависимость мода");
            CollectionAssert.DoesNotContain(labels, "OnPing", "обработчик не вызывают из кода");
        }

        // ===== hover / параметры / определение =====

        [Test]
        public void Hover_Member_Type_And_OwnFunc()
        {
            string md = _ls.Hover("mod.sal", 4, At(4, "Damage") + 1);
            StringAssert.Contains("func Damage(int raw, float armor) -> int", md);
            StringAssert.Contains("Урон с учётом брони.", md);
            StringAssert.Contains("модуль base", md);

            string tmd = _ls.Hover("mod.sal", 3, At(3, "Balance") + 1);
            StringAssert.Contains("class Game.Balance", tmd);
            StringAssert.Contains("Баланс боя.", tmd);

            string own = _ls.Hover("mod.sal", 6, At(6, "Helper") + 1);
            StringAssert.Contains("func Helper() -> string", own);
            StringAssert.Contains("Своя функция.", own);
        }

        [Test]
        public void SignatureHelp_ScriptFunc()
        {
            var s = _ls.SignatureHelp("mod.sal", 4, After(4, "Damage(1, "));
            Assert.IsNotNull(s);
            Assert.AreEqual("func Damage(int raw, float armor) -> int", s.Label);
            Assert.AreEqual(1, s.ActiveParameter);
            CollectionAssert.AreEqual(new[] { "int raw", "float armor" }, s.Parameters);

            var own = _ls.SignatureHelp("mod.sal", 6, After(6, "Helper("));
            Assert.IsNotNull(own);
            Assert.AreEqual("func Helper() -> string", own.Label);
        }

        [Test]
        public void Definition_IntoReferenceModule()
        {
            var loc = _ls.Definition("mod.sal", 4, At(4, "Damage") + 1);
            Assert.IsNotNull(loc);
            Assert.AreEqual("ref:base/base.sal", loc.File, "логическое имя отдано владельцу через FileOfLogical");
            Assert.AreEqual(10, loc.Line);

            var t = _ls.Definition("mod.sal", 3, At(3, "Balance") + 1);
            Assert.AreEqual(4, t.Line);
        }

        // ===== справочные модули =====

        [Test]
        public void References_OnlyDependencyClosure_WorkspaceWins_BrokenStrangerIgnored()
        {
            var host = new HostBuilder();
            var workspace = new List<ModuleSourceSet>
            {
                Mod("mod", "class M { func F() -> int { return Core.Cfg.v + Lib.X.k; } }", "core"),
                Mod("lib", "class Lib2 { int z = 0; }"),                     // правится в воркспейсе
            };
            var refs = new List<ModuleSourceSet>
            {
                Mod("core", "namespace Core { class Cfg { int v = 1; } }", "lib"),
                Mod("lib", "namespace Lib { class X { int k = 2; } }"),          // тёзка из воркспейса — не берётся
                Mod("broken", "class Oops { int = ; }"),                         // посторонний и сломанный
            };
            // зависимость core → lib закрывается модулем воркспейса, но Lib.X там нет
            var set = ReferenceSet.Append(workspace, refs);
            CollectionAssert.AreEqual(new[] { "core" }, set.Modules);
            Assert.IsTrue(set.Contains("core/core.sal"));
            Assert.IsFalse(set.Contains("broken/broken.sal"));
            Assert.AreEqual(3, workspace.Count);

            var r = ScriptCompiler.Compile(host.Registry, 1, workspace);
            bool refDiag = false;
            foreach (var d in r.Diagnostics) if (set.Contains(d.File)) refDiag = true;
            Assert.IsFalse(refDiag, "у справочного core ошибок нет");
            // Lib.X взят бы из справочного lib, но воркспейс его перекрыл — ошибка в моде честная
            Assert.IsFalse(r.Success);
        }

        [Test]
        public void Classifier_ColorsReferenceNames()
        {
            var spans = _ls.CreateClassifier().ClassifyDocument("mod.sal", _modText);
            int TypeAt(int line, int col)
            {
                foreach (var s in spans) if (s.Line == line && s.Col == col) return s.Type;
                return -1;
            }
            Assert.AreEqual(SemanticClassifier.TtNamespace, TypeAt(3, At(3, "Game")));
            Assert.AreEqual(SemanticClassifier.TtClass, TypeAt(3, At(3, "Balance")), "класс из модуля игры — как класс");
        }

        [Test]
        public void SyntaxError_KeepsPreviousSymbols()
        {
            var broken = ScriptCompiler.Compile(new HostBuilder().Registry, 1,
                new List<ModuleSourceSet> { Mod("mod", "trigger T { event OnPing(Unit u) { int x = ; } }") });
            Assert.IsNull(broken.Symbols, "синтаксическая ошибка — до чекера не дошло");
            _ls.UpdateSymbols(broken);
            Assert.IsNotNull(_ls.Symbols.Find("Game.Balance"), "прошлая таблица осталась");
        }
    }
}
