using System;
using System.Collections.Generic;
using System.Linq;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Json;
using Dsl.Text;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Собственный JSON ядра (Dsl.Json) и привязка манифестов к нему — замена
    /// Newtonsoft. Мягкость, на которую опираются существующие моды (комментарии,
    /// запятая в конце, регистр ключей), сохранена; тип значения — строгий.
    /// </summary>
    public sealed class JsonTests
    {
        // ---------------- парсер ----------------

        [Test]
        public void Parse_Scalars_AndStructure()
        {
            var n = JsonNode.Parse("{\"a\": 1, \"b\": -2.5e1, \"c\": \"x\\n\\u0416\\\"\", \"d\": [true, false, null], \"e\": {}}");
            Assert.AreEqual(1, (int)n["a"]);
            Assert.AreEqual(-25.0, (double)n["b"]);
            Assert.AreEqual("x\nЖ\"", (string)n["c"]);
            var d = (JsonArray)n["d"];
            Assert.AreEqual(3, d.Count);
            Assert.IsTrue((bool)d[0]);
            Assert.IsTrue(d[2].IsNull);
            Assert.AreEqual(JsonKind.Object, n["e"].Kind);
            Assert.IsNull(n["missing"], "отсутствующий ключ — null, а не JSON null");
        }

        [Test]
        public void Parse_AcceptsCommentsTrailingCommasAndBom()
        {
            var n = JsonNode.Parse("\uFEFF// шапка\n{ /* c */ \"a\": [1, 2,], \"b\": 3, }\n// хвост");
            Assert.AreEqual(2, ((JsonArray)n["a"]).Count);
            Assert.AreEqual(3, (int)n["b"]);
        }

        [TestCase("", "empty")]
        [TestCase("{\"a\": 1} x", "unexpected text")]
        [TestCase("{'a': 1}", "expected a property name")]
        [TestCase("{\"a\": 'x'}", "double quotes")]
        [TestCase("[01]", "leading zeros")]
        [TestCase("[NaN]", "unexpected character")]
        [TestCase("[1.]", "decimal point")]
        [TestCase("{\"a\" 1}", "expected ':'")]
        [TestCase("[1 2]", "expected ',' or ']'")]
        [TestCase("\"abc", "unterminated string")]
        [TestCase("[\"\\x\"]", "unknown escape")]
        [TestCase("[1e999]", "out of range")]
        [TestCase("/* open", "unterminated /*")]
        [TestCase("[tru]", "unknown word")]
        public void Parse_RejectsInvalid_WithPosition(string text, string fragment)
        {
            var ex = Assert.Throws<JsonException>(() => JsonNode.Parse(text, Language.En));
            StringAssert.Contains(fragment, ex.Message);
            StringAssert.Contains("line 1", ex.Message);
        }

        [Test]
        public void Parse_ReportsLineAndColumn()
        {
            var ex = Assert.Throws<JsonException>(() => JsonNode.Parse("{\n  \"a\": 1,\n  \"b\": @\n}", Language.En));
            Assert.AreEqual(3, ex.Line);
            Assert.AreEqual(8, ex.Column);
        }

        [Test]
        public void Parse_DeepNesting_IsAnError_NotAStackOverflow()
        {
            string text = new string('[', 100000) + new string(']', 100000);
            Assert.Throws<JsonException>(() => JsonNode.Parse(text));
            Assert.DoesNotThrow(() => JsonNode.Parse(new string('[', 200) + new string(']', 200)));
        }

        [Test]
        public void Parse_DuplicateKey_LastWins_OrderKept()
        {
            var o = (JsonObject)JsonNode.Parse("{\"b\": 1, \"a\": 2, \"b\": 3}");
            Assert.AreEqual(3, (int)o["b"]);
            Assert.AreEqual(new[] { "b", "a" }, o.Select(kv => kv.Key).ToArray());
        }

        [Test]
        public void Parse_ErrorMessages_FollowLanguage()
        {
            var ru = Assert.Throws<JsonException>(() => JsonNode.Parse("[1 2]", Language.Ru));
            var en = Assert.Throws<JsonException>(() => JsonNode.Parse("[1 2]", Language.En));
            StringAssert.Contains("строка 1", ru.Message);
            StringAssert.Contains("line 1", en.Message);
        }

        // ---------------- числа ----------------

        [Test]
        public void Numbers_IntegersAndReals_AreDistinguished()
        {
            var a = (JsonArray)JsonNode.Parse("[3, 3.0, 1e2, 3.5, 9007199254740993, 2147483648]");
            Assert.IsTrue(((JsonValue)a[0]).IsIntegerLiteral);
            Assert.IsFalse(((JsonValue)a[1]).IsIntegerLiteral);
            Assert.AreEqual(3, (int)a[1], "3.0 — целое значение");
            Assert.AreEqual(100, (int)a[2]);
            Assert.Throws<InvalidCastException>(() => { var _ = (int)a[3]; }, "3.5 не округляется молча");
            Assert.AreEqual(9007199254740993L, (long)a[4], "целый литерал читается без потери точности");
            Assert.Throws<InvalidCastException>(() => { var _ = (int)a[5]; }, "за пределами int");
        }

        [Test]
        public void Numbers_WriteLikeNewtonsoft()
        {
            Assert.AreEqual("3", ((JsonNode)3).ToJson());
            Assert.AreEqual("3.0", ((JsonNode)3.0).ToJson());
            Assert.AreEqual("0.1", ((JsonNode)0.1f).ToJson(), "float — своим кратчайшим видом");
            Assert.AreEqual("0.125", ((JsonNode)0.125).ToJson());
            Assert.AreEqual("-2147483648", ((JsonNode)int.MinValue).ToJson());
            Assert.Throws<ArgumentException>(() => { JsonNode _ = double.NaN; });
        }

        // ---------------- писатель ----------------

        [Test]
        public void Write_Indented_AndCompact()
        {
            var o = new JsonObject
            {
                ["a"] = 1,
                ["b"] = new JsonArray("x", "y"),
                ["c"] = new JsonArray(),
                ["d"] = new JsonObject(),
                ["e"] = null,
            };
            Assert.AreEqual("{\"a\":1,\"b\":[\"x\",\"y\"],\"c\":[],\"d\":{},\"e\":null}", o.ToJson());
            Assert.AreEqual("{\n  \"a\": 1,\n  \"b\": [\n    \"x\",\n    \"y\"\n  ],\n  \"c\": [],\n  \"d\": {},\n  \"e\": null\n}",
                o.ToJson(indented: true));
        }

        [Test]
        public void Write_EscapesAndRoundTripsStrings()
        {
            string s = "q\" b\\ \n\t\u0001 Ж 😀 \uD800";
            string json = ((JsonNode)s).ToJson();
            StringAssert.Contains("\\u0001", json);
            StringAssert.Contains("😀", json, "пара суррогатов — как есть");
            StringAssert.Contains("\\ud800", json, "одиночный суррогат — эскейпом, иначе не переживёт UTF-8");
            Assert.AreEqual(s, (string)JsonNode.Parse(json));
        }

        [Test]
        public void Object_SetKeepsOrder_RemoveReindexes()
        {
            var o = new JsonObject { ["a"] = 1, ["b"] = 2, ["c"] = 3 };
            o["a"] = 10;
            Assert.IsTrue(o.Remove("b"));
            o["d"] = 4;
            Assert.AreEqual("{\"a\":10,\"c\":3,\"d\":4}", o.ToJson());
            Assert.AreEqual(3, (int)o["c"]);
        }

        // ---------------- module.json ----------------

        [Test]
        public void ModuleManifest_KeysIgnoreCase_CommentsAllowed()
        {
            var m = ModuleManifest.Parse("// мод\n{ \"Name\": \"m\", \"ApiVersion\": 2, \"SOURCES\": [\"a.sal\",], \"execution\": \"synchronous\", }");
            Assert.AreEqual("m", m.Name);
            Assert.AreEqual(2, m.ApiVersion);
            Assert.AreEqual(new[] { "a.sal" }, m.Sources);
            Assert.IsTrue(m.IsSynchronous);
            Assert.AreEqual("0.0.0", m.Version, "отсутствующий ключ — значение по умолчанию");
            Assert.IsEmpty(m.UnknownKeys);
        }

        [Test]
        public void ModuleManifest_EmptyOrNull_IsNull()
        {
            Assert.IsNull(ModuleManifest.Parse(""));
            Assert.IsNull(ModuleManifest.Parse("  \n"));
            Assert.IsNull(ModuleManifest.Parse("null"));
            Assert.Throws<JsonException>(() => ModuleManifest.Parse("[]"));
        }

        [TestCase("{\"name\": \"m\", \"apiVersion\": \"1\"}", "remove the quotes: 1")]
        [TestCase("{\"name\": \"m\", \"apiVersion\": 1.5}", "int range")]
        [TestCase("{\"name\": 5}", "expected a string")]
        [TestCase("{\"name\": \"m\", \"sources\": \"a.sal\"}", "expected an array")]
        [TestCase("{\"name\": \"m\", \"sources\": [\"a.sal\", null]}", "sources[1]")]
        [TestCase("{\"name\": \"m\", \"apiVersion\": null}", "apiVersion")]
        public void ModuleManifest_WrongValueType_IsAnError(string json, string fragment)
        {
            var ex = Assert.Throws<JsonException>(() => ModuleManifest.Parse(json, Language.En));
            StringAssert.Contains(fragment, ex.Message);
        }

        [Test]
        public void ModuleManifest_UnknownKeys_Collected_DollarKeysSkipped()
        {
            var m = ModuleManifest.Parse("{\"$schema\": \"x\", \"name\": \"m\", \"scripts\": [\"a.sal\"], \"author\": \"me\"}");
            Assert.AreEqual(new[] { "scripts", "author" }, m.UnknownKeys);
        }

        [Test]
        public void UnknownManifestKey_GivesW0301_WithHint()
        {
            var m = ModuleManifest.Parse("{\"name\": \"m\", \"apiVersion\": 1, \"scripts\": [\"a.sal\"], \"autor\": \"x\"}");
            var set = new ModuleSourceSet { Manifest = m };
            var r = ScriptCompiler.Compile(new Dsl.Semantics.HostRegistry { Language = Language.En }, 1,
                new List<ModuleSourceSet> { set }, false, Language.En);
            var w = r.Diagnostics.Where(d => d.Code == "W0301").Select(d => d.Message).ToList();
            Assert.AreEqual(2, w.Count, string.Join("\n", r.Diagnostics));
            StringAssert.Contains("\"scripts\"", w[0]);
            StringAssert.Contains("\"sources\" key", w[0], "для списка исходников — прямая подсказка");
            StringAssert.Contains("Allowed keys: name, version", w[1]);
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "W0300"), "и модуль без файлов, как раньше");
            Assert.IsTrue(r.Success, "предупреждение, не ошибка");
        }

        [Test]
        public void KnownManifestKeys_GiveNoW0301()
        {
            var m = ModuleManifest.Parse("{\"name\": \"m\", \"version\": \"1.0.0\", \"apiVersion\": 1, \"dependencies\": [], " +
                                         "\"sources\": [], \"execution\": \"synchronous\", \"state\": \"none\"}");
            var set = new ModuleSourceSet { Manifest = m, EmptyIsIntended = true };
            var r = ScriptCompiler.Compile(new Dsl.Semantics.HostRegistry(), 1, new List<ModuleSourceSet> { set });
            Assert.IsFalse(r.Diagnostics.Any(d => d.Code == "W0301"), string.Join("\n", r.Diagnostics));
        }

        [Test]
        public void ModuleManifest_ToJson_And_Clone()
        {
            var m = ModuleManifest.Parse("{\"name\": \"m\", \"apiVersion\": 3, \"sources\": [\"a.sal\"], \"state\": \"none\"}");
            var back = ModuleManifest.Parse(m.ToJson().ToJson(indented: true));
            Assert.AreEqual(m.Name, back.Name);
            Assert.AreEqual(3, back.ApiVersion);
            Assert.AreEqual(m.Sources, back.Sources);
            Assert.AreEqual("none", back.State);

            var c = m.Clone();
            c.Sources[0] = "b.sal";
            Assert.AreEqual("a.sal", m.Sources[0], "копия не делит массивы с оригиналом");
        }

        // ---------------- salamander-api.json ----------------

        [Test]
        public void ApiManifest_ExportParseExport_IsStable()
        {
            var b = new HostBuilder();
            b.Api("Unit")
             .Fn("Hp", () => 1, Sig.Doc(ru: "Здоровье.", en: "Health."))
             .Fn("Mp", () => 2, Sig.Doc("Мана."));
            string json = ApiManifest.Export(b.Registry, 7);
            var parsed = ApiManifest.Parse(json);
            Assert.AreEqual(7, parsed.ApiVersion);
            Assert.AreEqual(json, parsed.ToJson().ToJson(indented: true));
            Assert.IsFalse(json.Contains("\r"), "перевод строки — \\n на любой ОС");
        }

        [Test]
        public void ApiManifest_DocText_StringOrObject_OtherIsAnError()
        {
            string ok = "{\"apiVersion\": 1, \"apis\": [{\"name\": \"A\", \"summary\": {\"RU\": \"р\", \"en\": \"e\"}, \"methods\": []}]}";
            var m = ApiManifest.Parse(ok);
            Assert.AreEqual("р", m.Apis[0].Summary.Get(Language.Ru));
            Assert.AreEqual("e", m.Apis[0].Summary.Get(Language.En));

            string bad = "{\"apiVersion\": 1, \"apis\": [{\"name\": \"A\", \"summary\": 5, \"methods\": []}]}";
            var ex = Assert.Throws<JsonException>(() => ApiManifest.Parse(bad, Language.En));
            StringAssert.Contains("apis[0].summary", ex.Message);
        }

        [Test]
        public void ApiManifest_UnknownKeys_AreIgnored()
        {
            var m = ApiManifest.Parse("{\"apiVersion\": 1, \"future\": {\"x\": [1]}, \"apis\": [{\"name\": \"A\", \"newKey\": 1, \"methods\": []}]}");
            Assert.AreEqual("A", m.Apis[0].Name);
        }

        [Test]
        public void ApiManifest_LiteralTypeMismatch_IsAFormatError()
        {
            const string head = "{\"apiVersion\": 1, \"apis\": [{\"name\": \"A\", \"methods\": [], \"consts\": [{\"name\": \"K\", \"type\": \"int\", \"value\": ";
            Assert.DoesNotThrow(() => ApiManifest.Import(head + "3}]}]}", out _));
            Assert.DoesNotThrow(() => ApiManifest.Import(head + "3.0}]}]}", out _), "3.0 — то же значение");
            var ex = Assert.Throws<FormatException>(() => ApiManifest.Import(head + "\"3\"}]}]}", out _, Language.En));
            StringAssert.Contains("expected an integer", ex.Message);
            Assert.Throws<FormatException>(() => ApiManifest.Import(head + "3.5}]}]}", out _));
        }
    }
}
