using System;
using System.Collections.Generic;
using System.IO;
using Dsl.Compilation;
using Dsl.Tooling;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Языковой сервис Dsl.Tooling — общий мозг LSP-сервера и встроенной IDE.
    /// Проверяется то, на что опирается IDE в игре: подсказки по составным
    /// именам и значениям, hover, go-to, раскраска, разворачивание сниппетов и
    /// компиляция воркспейса с несохранёнными правками.
    /// </summary>
    public sealed class ToolingTests
    {
        private const string ApiJson = @"{
  ""apiVersion"": 2,
  ""enums"": [ { ""name"": ""Slot"", ""summary"": ""Слот."", ""members"": [""MoveSpeed"", ""Armor""], ""memberDocs"": [""Скорость, м/с."", null] } ],
  ""structs"": [ { ""name"": ""Damage"", ""fields"": [ { ""name"": ""slash"", ""type"": ""float"", ""default"": 0.0 }, { ""name"": ""fire"", ""type"": ""float"", ""default"": 0.0 } ] } ],
  ""classes"": [
    { ""name"": ""Unit"", ""props"": [ { ""name"": ""name"", ""type"": ""string"", ""readOnly"": true } ] },
    { ""name"": ""PerkBasket"", ""props"": [], ""methods"": [ { ""name"": ""AddPerk"", ""params"": [ { ""name"": ""id"", ""type"": ""string"" } ], ""returns"": ""void"" } ] }
  ],
  ""apiNamespaces"": [ { ""name"": ""Api.Parts"", ""summary"": ""Каталог деталей."" } ],
  ""apis"": [
    { ""name"": ""UnitApi"", ""methods"": [ { ""name"": ""Heal"", ""summary"": ""Лечит."", ""params"": [ { ""name"": ""target"", ""type"": ""Unit"" }, { ""name"": ""amount"", ""type"": ""float"" } ], ""returns"": ""void"" } ] },
    { ""name"": ""Api.Parts.Grip"", ""methods"": [], ""consts"": [ { ""name"": ""sword_01"", ""type"": ""string"", ""value"": ""sword_01"" } ] }
  ],
  ""events"": [ { ""name"": ""OnPerks"", ""params"": [ { ""name"": ""u"", ""type"": ""Unit"" }, { ""name"": ""basket"", ""type"": ""PerkBasket"" } ] } ],
  ""archetypes"": [ { ""name"": ""spell"", ""events"": [ { ""name"": ""OnCast"", ""params"": [ { ""name"": ""caster"", ""type"": ""Unit"" } ] } ] } ]
}";

        private const string Code =
            "trigger T\n" +                                   // 1
            "{\n" +                                            // 2
            "    event OnPerks(Unit u, PerkBasket basket)\n" + // 3
            "    {\n" +                                        // 4
            "        basket.AddPerk(\"x\");\n" +               // 5
            "        string g = Api.Parts.Grip.sword_01;\n" +  // 6
            "        UnitApi.Heal(u, 1.5d);\n" +               // 7
            "        Helper();\n" +                            // 8
            "    }\n" +                                        // 9
            "    func Helper() { }\n" +                        // 10
            "}\n";                                             // 11

        private LanguageService _ls;
        private Dictionary<string, string> _texts;

        [SetUp]
        public void SetUp()
        {
            _texts = new Dictionary<string, string> { ["a.sal"] = Code };
            _ls = new LanguageService
            {
                Api = JsonConvert.DeserializeObject<ApiManifest>(ApiJson),
                TextProvider = k => _texts.TryGetValue(k, out var t) ? t : null,
            };
            _ls.Index.Update("a.sal", Code);
        }

        private static List<string> Labels(List<CompletionItem> items)
        {
            var r = new List<string>();
            foreach (var i in items) r.Add(i.Label);
            return r;
        }

        [Test]
        public void Completion_DottedApiNode_OffersNextSegment()
        {
            // "Api.Parts." → следующий сегмент составного имени
            var items = _ls.Complete("a.sal", 6, 30);  // после "Api.Parts."
            CollectionAssert.Contains(Labels(items), "Grip");
        }

        [Test]
        public void Completion_ApiConstants_ReadWithoutParens()
        {
            var items = _ls.Complete("a.sal", 6, 35);  // после "Api.Parts.Grip."
            var c = items.Find(i => i.Label == "sword_01");
            Assert.IsNotNull(c);
            Assert.AreEqual(CompletionKind.Constant, c.Kind);
            Assert.IsNull(c.InsertText, "константа вставляется без скобок");
        }

        [Test]
        public void Completion_ValueMethods_ByDeclaredType()
        {
            var items = _ls.Complete("a.sal", 5, 16);  // после "basket."
            var add = items.Find(i => i.Label == "AddPerk");
            Assert.IsNotNull(add);
            Assert.IsTrue(add.IsSnippet);
        }

        [Test]
        public void SignatureHelp_ActiveParameter_CountsCommas()
        {
            var s = _ls.SignatureHelp("a.sal", 7, 25);  // внутри Heal(u, |
            Assert.IsNotNull(s);
            Assert.AreEqual(1, s.ActiveParameter);
            StringAssert.StartsWith("UnitApi.Heal(", s.Label);
        }

        [Test]
        public void Hover_EnumMember_ShowsMemberDoc()
        {
            string line = "trigger T { event OnPerks(Unit u, PerkBasket b) { Slot s = Slot.MoveSpeed; } }";
            _texts["a.sal"] = line + "\n";
            string md = _ls.Hover("a.sal", 1, line.IndexOf("MoveSpeed", StringComparison.Ordinal) + 2);
            Assert.IsNotNull(md);
            StringAssert.Contains("м/с", md);
        }

        [Test]
        public void Definition_LocalFunc_InSameDecl()
        {
            var loc = _ls.Definition("a.sal", 8, 10);
            Assert.IsNotNull(loc);
            Assert.AreEqual(10, loc.Line);
        }

        [Test]
        public void Classifier_DoubleLiteral_IsNumber_Readonly_IsKeyword()
        {
            var spans = _ls.CreateClassifier().ClassifyDocument("b.sal",
                "class C { readonly double d = 1.5d; }\n");
            bool number = false, keyword = false;
            foreach (var s in spans)
            {
                if (s.Col == 31 && s.Type == SemanticClassifier.TtNumber) number = true;
                if (s.Col == 11 && s.Type == SemanticClassifier.TtKeyword) keyword = true;
            }
            Assert.IsTrue(number, "1.5d — число");
            Assert.IsTrue(keyword, "readonly — ключевое слово");
        }

        [Test]
        public void Classifier_DottedApiSegment_IsNamespace_Const_IsEnumMember()
        {
            var c = _ls.CreateClassifier();
            Assert.AreEqual(SemanticClassifier.TtNamespace, c.ClassifyIdentifier("Parts", false, true, false, "Api.Parts"));
            Assert.AreEqual(SemanticClassifier.TtEnumMember, c.ClassifyIdentifier("sword_01", false, true, false, "Api.Parts.Grip.sword_01"));
            Assert.AreEqual(SemanticClassifier.TtKeyword, c.ClassifyIdentifier("spell", false, false, false, null));
        }

        [Test]
        public void Snippet_ExpandsPlaceholders_AndIndent()
        {
            string s = SnippetText.Expand("Heal(${1:Unit target}, ${2:float amount})$0", "", "    ", out int caret, out int sel);
            Assert.AreEqual("Heal(Unit target, float amount)", s);
            Assert.AreEqual(5, caret);
            Assert.AreEqual("Unit target".Length, sel);

            s = SnippetText.Expand("OnCast(Unit caster)\n{\n\t$0\n}", "    ", "    ", out caret, out sel);
            Assert.AreEqual("OnCast(Unit caster)\n    {\n        \n    }", s);
            Assert.AreEqual(s.IndexOf("\n    }", StringComparison.Ordinal), caret);
            Assert.AreEqual(0, sel);
        }

        [Test]
        public void TextUtil_OffsetOfClamped_DoesNotSpillToNextLine()
        {
            string t = "ab\ncd\n";
            Assert.AreEqual(2, TextUtil.OffsetOfClamped(t, 1, 99));
            Assert.AreEqual(4, TextUtil.OffsetOfClamped(t, 2, 2));
            Assert.AreEqual(-1, TextUtil.OffsetOfClamped(t, 9, 1));
        }

        [Test]
        public void Workspace_CompilesAcrossFiles_WithOverlay()
        {
            string root = Path.Combine(Path.GetTempPath(), "sal-tooling-" + Guid.NewGuid().ToString("N"));
            try
            {
                string mod = Path.Combine(root, "mods", "core");
                Directory.CreateDirectory(Path.Combine(mod, "src"));
                File.WriteAllText(Path.Combine(mod, "module.json"),
                    "{ \"name\": \"core\", \"apiVersion\": 1, \"sources\": [\"src/a.sal\", \"src/b.sal\"] }");
                File.WriteAllText(Path.Combine(mod, "src", "a.sal"), "class Util { func F() -> int { return 1; } }\n");
                string bPath = Path.Combine(mod, "src", "b.sal");
                File.WriteAllText(bPath, "class Use { func G() -> int { return Util.F(); } }\n");

                var ws = WorkspaceLoader.Load(root);
                Assert.AreEqual(1, ws.Modules.Count);
                Assert.IsTrue(ws.ModuleDirs.ContainsKey("core"));
                Assert.AreEqual("core/src/b.sal", ws.LogicalOf(bPath));

                // ссылка через файл — не ложная ошибка, как было при компиляции буфера в одиночку
                var ok = ScriptCompiler.Compile(new Dsl.Semantics.HostRegistry(), 1, WorkspaceCompiler.WithOverlays(ws, null));
                Assert.IsTrue(ok.Success, string.Join("\n", ok.Diagnostics));

                // несохранённая правка попадает в компиляцию, снимок не мутируется
                var broken = WorkspaceCompiler.WithOverlays(ws, p =>
                    string.Equals(Path.GetFullPath(p), Path.GetFullPath(bPath), StringComparison.OrdinalIgnoreCase)
                        ? "class Use { func G() -> int { return Util.Nope(); } }\n" : null);
                var bad = ScriptCompiler.Compile(new Dsl.Semantics.HostRegistry(), 1, broken);
                Assert.IsFalse(bad.Success);
                StringAssert.Contains("Util.F()", ws.Modules[0].Files[1].text);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        // before/after/replace/base — контекстные: слово только в своей позиции
        [Test]
        public void Classifier_ChainWords_AreContextual()
        {
            const string text = "class C { int after = 1; after func F() { } func G() { base(); } }\n";
            var spans = _ls.CreateClassifier().ClassifyDocument("c.sal", text);
            int TypeAt(int index)
            {
                foreach (var s in spans) if (s.Line == 1 && s.Col == index + 1) return s.Type;
                return -1;
            }
            int field = text.IndexOf("after", StringComparison.Ordinal);
            int modifier = text.IndexOf("after", field + 1, StringComparison.Ordinal);
            int baseCall = text.IndexOf("base", StringComparison.Ordinal);
            Assert.AreNotEqual(SemanticClassifier.TtKeyword, TypeAt(field), "поле с именем after — не слово");
            Assert.AreEqual(SemanticClassifier.TtKeyword, TypeAt(modifier), "after перед func — модификатор");
            Assert.AreEqual(SemanticClassifier.TtKeyword, TypeAt(baseCall), "base( — вызов предыдущей версии");
        }

        // безусловный список слов красит подсветка встроенной IDE — контекстным там не место
        [Test]
        public void ChainWords_AreNotUnconditionalKeywords_ButAreCompleted()
        {
            foreach (var w in new[] { "before", "after", "replace", "base" })
                CollectionAssert.DoesNotContain(EngineDocs.Keywords, w);
            var labels = Labels(_ls.Complete("a.sal", 11, 1));
            CollectionAssert.Contains(labels, "after");
            CollectionAssert.Contains(labels, "base");
        }

        [Test]
        public void BuiltinMath_CompletionHoverSignature_AndYieldsToOwnMath()
        {
            _texts["m.sal"] = "trigger M\n{\n    event OnPerks(Unit u, PerkBasket b)\n    {\n        int r = Math.Round(Math.PI);\n        Math.\n    }\n}\n";
            _ls.Index.Update("m.sal", _texts["m.sal"]);

            var labels = Labels(_ls.Complete("m.sal", 6, 14));
            CollectionAssert.Contains(labels, "Clamp");
            CollectionAssert.Contains(labels, "PI");

            string line = "        int r = Math.Round(Math.PI);";
            StringAssert.Contains("Math.Round(число x) -> int", _ls.Hover("m.sal", 5, line.IndexOf("Round", StringComparison.Ordinal) + 2));
            StringAssert.Contains("Math.PI: float", _ls.Hover("m.sal", 5, line.IndexOf("PI", StringComparison.Ordinal) + 1));
            Assert.AreEqual("Math.Round(число x) -> int",
                _ls.SignatureHelp("m.sal", 5, line.IndexOf("Math.PI", StringComparison.Ordinal) + 1)?.Label);

            // свой class Math в воркспейсе — встроенный больше не подсказывается, как и в компиляторе
            _texts["own.sal"] = "class Math\n{\n    func Twice(int x) -> int { return x * 2; }\n}\n";
            _ls.Index.Update("own.sal", _texts["own.sal"]);
            labels = Labels(_ls.Complete("m.sal", 6, 14));
            CollectionAssert.DoesNotContain(labels, "Clamp");
            CollectionAssert.Contains(labels, "Twice");
        }

        private void IndexChainFiles()
        {
            _texts["base.sal"] = "spell fireball\n{\n    event OnCast(Unit c)\n    {\n    }\n}\n";
            _texts["mod.sal"] = "spell fireball\n{\n    after event OnCast(Unit c) { }\n    before event OnCast(Unit c) { }\n}\n";
            _texts["mod2.sal"] = "spell fireball\n{\n    replace event OnCast(Unit c) { }\n}\n";
            foreach (var k in new[] { "base.sal", "mod.sal", "mod2.sal" }) _ls.Index.Update(k, _texts[k]);
            var order = new Dictionary<string, int> { ["base.sal"] = 0, ["mod.sal"] = 1, ["mod2.sal"] = 2 };
            _ls.FileOrder = k => order.TryGetValue(k, out var r) ? r : int.MaxValue;
        }

        [Test]
        public void Hover_MemberName_ListsVersionsInLoadOrder()
        {
            IndexChainFiles();
            _texts.Remove("mod2.sal");
            _ls.Index.Remove("mod2.sal");

            string line = "    after event OnCast(Unit c) { }";
            string md = _ls.Hover("mod.sal", 3, line.IndexOf("OnCast", StringComparison.Ordinal) + 2);
            Assert.IsNotNull(md);
            StringAssert.Contains("OnCast(", md, "обычная подсказка события осталась");
            StringAssert.Contains("Выполнится: before mod.sal:4 → **ядро** base.sal:3 → after mod.sal:3", md);
            StringAssert.Contains("← здесь", md);
        }

        [Test]
        public void Hover_ReplaceWord_ExplainsAndShowsWhatSurvives()
        {
            IndexChainFiles();
            string line = "    replace event OnCast(Unit c) { }";
            string md = _ls.Hover("mod2.sal", 3, line.IndexOf("replace", StringComparison.Ordinal) + 2);
            Assert.IsNotNull(md);
            StringAssert.Contains("Стирает", md);
            StringAssert.Contains("Выполнится: **ядро** mod2.sal:3.", md, "replace стёр ядро базы и слои mod.sal");
        }
    }
}
