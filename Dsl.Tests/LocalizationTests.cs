using System.Collections.Generic;
using System.Text.RegularExpressions;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using Dsl.Text;
using Dsl.Tooling;
using Dsl.Json;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Локализация: язык ("ru" / "en") задаётся на хосте, компиляции и движке;
    /// коды диагностик от языка не зависят; справка хоста бывает на двух языках.
    /// </summary>
    public sealed class LocalizationTests
    {
        private static readonly Regex Cyrillic = new Regex("[А-Яа-яЁё]");

        private static List<ModuleSourceSet> Module(string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "test", ApiVersion = 1, Sources = new[] { "test.sal" } },
            };
            set.Files.Add(("test/test.sal", src));
            return new List<ModuleSourceSet> { set };
        }

        private static HostRegistry Host(out int updateId)
        {
            var host = new HostRegistry();
            updateId = host.DefineEvent("Update", TypeRef.Float, TypeRef.Float);
            return host;
        }

        private static List<string> Codes(CompilationResult r)
        {
            var codes = new List<string>();
            foreach (var d in r.Diagnostics) codes.Add(d.Code);
            return codes;
        }

        // ===== компиляция ===================================================

        private const string Broken = @"
class C {
    func F() -> int { return ""s""; }
    func G() { var x = nope; break; wait 1; }
}
trigger T { action Do(int a) {} }
";

        [Test]
        public void Compile_SameCodes_DifferentLanguage()
        {
            var host = Host(out _);
            var ru = ScriptCompiler.Compile(host, 1, Module(Broken), false, Language.Ru);
            var en = ScriptCompiler.Compile(host, 1, Module(Broken), false, Language.En);

            Assert.IsFalse(ru.Success);
            Assert.Greater(ru.Diagnostics.Count, 2);
            CollectionAssert.AreEqual(Codes(ru), Codes(en), "коды от языка не зависят");
            for (int i = 0; i < ru.Diagnostics.Count; i++)
            {
                StringAssert.IsMatch("[А-Яа-яЁё]", ru.Diagnostics[i].Message, ru.Diagnostics[i].Code);
                Assert.IsFalse(Cyrillic.IsMatch(en.Diagnostics[i].Message), en.Diagnostics[i].ToString());
                Assert.AreNotEqual(ru.Diagnostics[i].Message, en.Diagnostics[i].Message);
            }
        }

        [Test]
        public void Compile_Default_IsRussian()
        {
            var r = ScriptCompiler.Compile(Host(out _), 1, Module(Broken));
            foreach (var d in r.Diagnostics) StringAssert.IsMatch("[А-Яа-яЁё]", d.Message, d.Code);
        }

        [Test]
        public void HostLanguage_IsDefaultForCompileAndEngine()
        {
            var host = Host(out _);
            host.Language = Language.En;

            var r = ScriptCompiler.Compile(host, 1, Module(Broken));
            Assert.IsFalse(r.Success);
            foreach (var d in r.Diagnostics) Assert.IsFalse(Cyrillic.IsMatch(d.Message), d.ToString());

            Assert.AreEqual(Language.En, new ScriptEngine(host).Language);

            // явный язык компиляции важнее языка хоста
            var ru = ScriptCompiler.Compile(host, 1, Module(Broken), false, Language.Ru);
            foreach (var d in ru.Diagnostics) StringAssert.IsMatch("[А-Яа-яЁё]", d.Message, d.Code);
        }

        [Test]
        public void Lexer_Parser_Manifest_AreLocalized()
        {
            var host = Host(out _);
            var lexParse = Module("class C { func F() { var s = \"unterminated; } ");
            var bad = new ModuleSourceSet { Manifest = new ModuleManifest { Name = "bad", ApiVersion = 7 } };
            bad.Manifest.Dependencies = new[] { "missing" };
            var all = new List<ModuleSourceSet>(lexParse) { bad };

            var en = ScriptCompiler.Compile(host, 1, lexParse, false, Language.En);
            Assert.IsTrue(Codes(en).Contains("E0002"), string.Join("\n", en.Diagnostics));
            foreach (var d in en.Diagnostics) Assert.IsFalse(Cyrillic.IsMatch(d.Message), d.ToString());

            var man = ScriptCompiler.Compile(host, 1, all, false, Language.En);
            Assert.IsTrue(Codes(man).Contains("E0302"), string.Join("\n", man.Diagnostics));
            foreach (var d in man.Diagnostics) Assert.IsFalse(Cyrillic.IsMatch(d.Message), d.ToString());

            var q = ScriptCompiler.Compile(host, 1, all, true, Language.En);
            Assert.IsNotEmpty(q.Excluded);
            foreach (var ex in q.Excluded) Assert.IsFalse(Cyrillic.IsMatch(ex.Reason), ex.Reason);
        }

        // ===== рантайм =====================================================

        private const string RuntimeFail = @"
trigger T {
    event Update(float t, float dt) {
        var l = new List<int>();
        int x = l[5];
    }
}";

        private static List<string> RunAndCollectErrors(Language? engineLang)
        {
            var host = Host(out int update);
            var r = ScriptCompiler.Compile(host, 1, Module(RuntimeFail));
            Assert.IsTrue(r.Success, string.Join("\n", r.Diagnostics));
            var engine = new ScriptEngine(host);
            if (engineLang != null) engine.Language = engineLang.Value;
            var errors = new List<string>();
            engine.OnError += errors.Add;
            engine.LoadProgram(r.Program);
            engine.Raise(update).AddFloat(0f).AddFloat(0.1f).Commit();
            engine.Tick(0.1f);
            return errors;
        }

        [Test]
        public void RuntimeError_FollowsEngineLanguage()
        {
            var ru = RunAndCollectErrors(null);
            Assert.AreEqual(1, ru.Count);
            StringAssert.StartsWith("Ошибка скрипта: Индекс 5 вне границ списка", ru[0]);
            StringAssert.Contains("  в T.Update", ru[0]);

            var en = RunAndCollectErrors(Language.En);
            Assert.AreEqual(1, en.Count);
            StringAssert.StartsWith("Script error: Index 5 is out of list bounds", en[0]);
            StringAssert.Contains("  at T.Update", en[0]);
            Assert.IsFalse(Cyrillic.IsMatch(en[0]), en[0]);
        }

        [Test]
        public void HostScriptError_WithTwoTexts_PicksEngineLanguage()
        {
            var host = Host(out int update);
            host.DefineMethod("Api", "Fail", new TypeRef[0], TypeRef.Void,
                (ref CallContext c) => throw new ScriptError("сломалось", "it broke"));
            var r = ScriptCompiler.Compile(host, 1, Module("trigger T { event Update(float t, float dt) { Api.Fail(); } }"));
            Assert.IsTrue(r.Success, string.Join("\n", r.Diagnostics));

            foreach (var (lang, expected) in new[] { (Language.Ru, "сломалось"), (Language.En, "it broke") })
            {
                var engine = new ScriptEngine(host) { Language = lang };
                string err = null;
                engine.OnError += m => err = m;
                engine.LoadProgram(r.Program);
                engine.Raise(update).AddFloat(0f).AddFloat(0.1f).Commit();
                engine.Tick(0.1f);
                StringAssert.Contains(expected, err);
            }

            var single = new ScriptError("один текст");
            Assert.AreEqual("один текст", single.MessageFor(Language.En), "один текст — для обоих языков");
        }

        [Test]
        public void HostBuilder_RegistrationErrors_UseHostLanguage()
        {
            var b = new HostBuilder().UseLanguage(Language.En);
            b.Registry.DefineClass("Unit");
            var ex = Assert.Throws<System.InvalidOperationException>(() => b.Registry.DefineClass("Unit"));
            StringAssert.Contains("already registered", ex.Message);

            var ru = new HostBuilder();
            ru.Registry.DefineClass("Unit");
            ex = Assert.Throws<System.InvalidOperationException>(() => ru.Registry.DefineClass("Unit"));
            StringAssert.Contains("уже зарегистрирован", ex.Message);
        }

        // ===== справка хоста =================================================

        [SalamanderApi("Работа со здоровьем.", En = "Health operations.")]
        private sealed class HealApi
        {
            [SalamanderMethod("Лечит цель.", En = "Heals the target.")]
            public void Heal([SalamanderParam("сколько HP", En = "HP amount")] int amount) { }

            [SalamanderMethod("Только по-русски.")]
            public void RuOnly() { }
        }

        [SalamanderClass("Слоты.", En = "Slots.")]
        private enum Slot { [SalamanderMember("Скорость", En = "Speed")] Speed = 0, Armor = 1 }

        private enum Kind { Melee = 0, Ranged = 1 }

        [Test]
        public void Docs_Bilingual_FromBuilderAndAttributes()
        {
            var b = new HostBuilder();
            b.Api("Unit")
             .Fn("Hp", () => 1, Sig.Doc(ru: "Здоровье.", en: "Health."))
             .Fn("Mp", (int k) => k, Sig.Doc("Мана.").P("k", ru: "множитель", en: "multiplier"));
            b.Register(typeof(Slot));
            b.Enum<Kind>("Kind", DocText.Of(ru: "Виды.", en: "Kinds."))
             .Member(Kind.Melee, ru: "ближний", en: "melee")
             .Member(Kind.Ranged, "дальний");
            b.Registry.DescribeApiNamespace("Game", DocText.Of("Игра.", "Game."));
            b.RegisterApi(new HealApi(), "Heal");

            var reg = b.Registry;
            reg.TryGetApi("Unit", out var unit);
            Assert.AreEqual("Health.", unit.Methods["Hp"].Summary.Get(Language.En));
            Assert.AreEqual("Здоровье.", unit.Methods["Hp"].Summary.Get(Language.Ru));
            Assert.AreEqual("Мана.", unit.Methods["Mp"].Summary.Get(Language.En), "нейтральный текст — на обоих языках");
            Assert.AreEqual("multiplier", unit.Methods["Mp"].ParamDocs[0].Get(Language.En));

            reg.TryGetApi("Heal", out var heal);
            Assert.AreEqual("Health operations.", heal.Summary.Get(Language.En));
            Assert.AreEqual("Heals the target.", heal.Methods["Heal"].Summary.Get(Language.En));
            Assert.AreEqual("HP amount", heal.Methods["Heal"].ParamDocs[0].Get(Language.En));
            Assert.AreEqual("Только по-русски.", heal.Methods["RuOnly"].Summary.Get(Language.En),
                "без En — русский текст и в английском режиме");

            reg.TryGetEnum("Slot", out var slot);
            Assert.AreEqual("Slots.", slot.Summary.Get(Language.En));
            Assert.AreEqual("Speed", slot.DocOf(0).Get(Language.En));
            Assert.IsNull(slot.DocOf(1));

            reg.TryGetEnum("Kind", out var kind);
            Assert.AreEqual("Kinds.", kind.Summary.Get(Language.En));
            Assert.AreEqual("melee", kind.DocOf(0).Get(Language.En));
            Assert.AreEqual("дальний", kind.DocOf(1).Get(Language.En));
        }

        [SalamanderKind("perk", "Перк.", SummaryEn = "Perk.")]
        public struct Perk
        {
            [KindField(doc: "Стоимость", required: true, DocEn = "Cost")] public int Cost;
            [KindField(refKind: "perk")] public int Next;
        }

        [Test]
        public void KindDocs_Bilingual()
        {
            var b = new HostBuilder();
            b.Kind<Perk>();
            Assert.IsTrue(b.Registry.TryGetArchetypeKind("perk", out var k));
            Assert.AreEqual("Perk.", k.Summary.Get(Language.En));
            Assert.AreEqual("Cost", k.Consts[0].Doc.Get(Language.En));
            Assert.AreEqual("Стоимость", k.Consts[0].Doc.Get(Language.Ru));
            Assert.AreEqual("(id of an entity of kind perk)", k.Consts[1].Doc.Get(Language.En));
            Assert.AreEqual("(id сущности вида perk)", k.Consts[1].Doc.Get(Language.Ru));
        }

        [Test]
        public void ApiManifest_RoundTrips_BilingualDocs_AndReadsOldStrings()
        {
            var b = new HostBuilder();
            b.Api("Unit")
             .Fn("Hp", () => 1, Sig.Doc(ru: "Здоровье.", en: "Health."))
             .Fn("Mp", () => 2, Sig.Doc("Мана."));
            string json = ApiManifest.Export(b.Registry, 1);

            var jo = JsonNode.Parse(json);
            var methods = (JsonArray)jo["apis"][0]["methods"];
            Assert.AreEqual(JsonKind.Object, methods[0]["summary"].Kind, "двуязычная справка — объект");
            Assert.AreEqual("Health.", (string)methods[0]["summary"]["en"]);
            Assert.AreEqual(JsonKind.String, methods[1]["summary"].Kind, "одноязычная — строка, как раньше");

            var back = ApiManifest.Import(json, out _, Language.En);
            back.TryGetApi("Unit", out var unit);
            Assert.AreEqual("Health.", unit.Methods["Hp"].Summary.Get(Language.En));
            Assert.AreEqual("Здоровье.", unit.Methods["Hp"].Summary.Get(Language.Ru));
            Assert.AreEqual(Language.En, back.Language);

            // старый манифест: только строки
            string old = json.Replace("\"en\": \"Health.\"", "\"en\": null");
            var oldBack = ApiManifest.Import(old, out _);
            oldBack.TryGetApi("Unit", out var oldUnit);
            Assert.AreEqual("Здоровье.", oldUnit.Methods["Hp"].Summary.Get(Language.En), "нет английского — русский");
        }

        [Test]
        public void LanguageService_Hover_UsesItsLanguage()
        {
            var b = new HostBuilder();
            b.Api("Unit").Fn("Hp", () => 1, Sig.Doc(ru: "Здоровье.", en: "Health."));
            var api = ApiManifest.Parse(ApiManifest.Export(b.Registry, 1));

            const string code = "class C { func F() { var h = Unit.Hp(); Engine.Log(\"x\"); } }";
            foreach (var (lang, hp, log) in new[] { (Language.Ru, "Здоровье.", "Сообщение в лог."), (Language.En, "Health.", "Message to the log.") })
            {
                var ls = new LanguageService { Api = api, Language = lang };
                ls.Index.Update("a.sal", code);
                ls.TextProvider = _ => code;
                StringAssert.Contains(hp, ls.Hover("a.sal", 1, code.IndexOf("Hp") + 2));
                StringAssert.Contains(log, ls.Hover("a.sal", 1, code.IndexOf("Log") + 2));
            }

            var en = new LanguageService { Language = Language.En };
            const string math = "class C { func F() { var r = Math.Round(1.5); } }";
            en.Index.Update("m.sal", math);
            en.TextProvider = _ => math;
            StringAssert.Contains("Math.Round(number x) -> int", en.Hover("m.sal", 1, math.IndexOf("Round") + 2));
        }

        [Test]
        public void BuildFile_LanguageKey_IsRead()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sal-l10n-" + System.Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string build = System.IO.Path.Combine(dir, WorkspaceLoader.BuildFileName);
                System.IO.File.WriteAllText(build, "{ \"language\": \"en\", \"modules\": [\"nope\"] }");
                var ws = WorkspaceLoader.Load(dir);
                Assert.AreEqual(Language.En, ws.Language);
                Assert.IsTrue(ws.LanguageFromBuild);
                Assert.IsNotEmpty(ws.LoadErrors);
                foreach (var e in ws.LoadErrors) Assert.IsFalse(Cyrillic.IsMatch(e.Value), e.Value);

                System.IO.File.WriteAllText(build, "{ \"language\": \"de\", \"modules\": [] }");
                ws = WorkspaceLoader.Load(dir);
                Assert.AreEqual(Language.Ru, ws.Language, "неизвестный язык — по умолчанию");
                Assert.IsFalse(ws.LanguageFromBuild);
                Assert.AreEqual(1, ws.LoadErrors.Count);
                StringAssert.Contains("\"de\"", ws.LoadErrors[0].Value);
            }
            finally { System.IO.Directory.Delete(dir, true); }
        }

        [Test]
        public void Loc_Parse()
        {
            Assert.IsTrue(Loc.TryParse(" EN ", out var l)); Assert.AreEqual(Language.En, l);
            Assert.IsTrue(Loc.TryParse("ru", out l)); Assert.AreEqual(Language.Ru, l);
            Assert.IsFalse(Loc.TryParse("de", out _));
            Assert.IsFalse(Loc.TryParse(null, out _));
            Assert.AreEqual(Language.En, Loc.Parse(null, Language.En));
            Assert.Throws<System.FormatException>(() => Loc.Parse("english"));
            Assert.AreEqual("en", Loc.Code(Language.En));
        }

        [Test]
        public void DocText_Basics()
        {
            DocText neutral = "текст";
            Assert.IsFalse(neutral.IsBilingual);
            Assert.AreEqual("текст", neutral.Get(Language.En));

            var both = DocText.Of("рус", "eng");
            Assert.IsTrue(both.IsBilingual);
            Assert.AreEqual("eng", both.Get(Language.En));
            Assert.AreEqual("рус", both.Get(Language.Ru));
            Assert.AreEqual("eng", DocText.Of(null, "eng").Get(Language.Ru), "нет русского — английский");

            DocText none = (string)null;
            Assert.IsNull(none);
            Assert.IsNull(DocText.Get(none, Language.En));
            Assert.AreEqual(DocText.Of("a", "b"), DocText.Of("a", "b"));
        }
    }
}
