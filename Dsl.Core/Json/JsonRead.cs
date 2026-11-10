using System;
using System.Collections.Generic;
using Dsl.Text;

namespace Dsl.Json
{
    /// <summary>
    /// Строгое чтение полей документа при ручной привязке (манифесты, сессия IDE).
    ///
    /// Тип значения — часть контракта: число в строке ("apiVersion": "1") — ошибка
    /// с подсказкой, а не молчаливое приведение. Иначе опечатка в типе живёт
    /// незамеченной, а поведение зависит от того, как именно библиотека приводит
    /// (Newtonsoft округлял 3.5 → 4 и превращал "yes" в исключение где-то дальше).
    ///
    /// Отсутствующий ключ сюда не попадает: привязка обходит то, что ЕСТЬ в
    /// документе, поэтому у отсутствующего поля остаётся значение по умолчанию.
    /// JSON null — «значения нет»: у ссылочных полей это null, у int/bool — ошибка.
    /// </summary>
    public static class JsonRead
    {
        public static string KindName(JsonNode n, Language lang)
        {
            switch (n?.Kind ?? JsonKind.Null)
            {
                case JsonKind.Null: return "null";
                case JsonKind.Bool: return Loc.T(lang, "логическое значение", "a boolean");
                case JsonKind.Number: return Loc.T(lang, "число", "a number");
                case JsonKind.String: return Loc.T(lang, "строка", "a string");
                case JsonKind.Array: return Loc.T(lang, "массив", "an array");
                default: return Loc.T(lang, "объект", "an object");
            }
        }

        public static JsonException TypeError(string path, JsonNode got, string expectedRu, string expectedEn, Language lang)
        {
            string hintRu = "", hintEn = "";
            // число или логическое значение, записанное строкой, — самая частая ошибка
            // ручного манифеста: подсказываем прямо, что исправить
            if (got is JsonValue v && v.Kind == JsonKind.String)
            {
                string s = v.AsString;
                bool looksNumber = double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out _);
                bool looksBool = s == "true" || s == "false";
                if ((looksNumber && expectedEn.Contains("number")) || (looksBool && expectedEn.Contains("boolean")))
                {
                    hintRu = $" — уберите кавычки: {s}";
                    hintEn = $" — remove the quotes: {s}";
                }
            }
            return new JsonException(Loc.T(lang,
                $"{path}: ожидалось {expectedRu}, а записано: {KindName(got, lang)}{(got is JsonValue sv && sv.Kind == JsonKind.String ? $" \"{sv.AsString}\"" : "")}{hintRu}.",
                $"{path}: expected {expectedEn}, got {KindName(got, lang)}{(got is JsonValue sv2 && sv2.Kind == JsonKind.String ? $" \"{sv2.AsString}\"" : "")}{hintEn}."));
        }

        /// <summary>Строка или null (JSON null).</summary>
        public static string String(JsonNode n, string path, Language lang)
        {
            if (n == null || n.IsNull) return null;
            if (n.Kind == JsonKind.String) return n.AsString;
            throw TypeError(path, n, "строка", "a string", lang);
        }

        public static int Int(JsonNode n, string path, Language lang)
        {
            if (n is JsonValue v && v.Kind == JsonKind.Number)
            {
                if (v.TryGetInt32(out int i)) return i;
                throw new JsonException(Loc.T(lang, $"{path}: ожидалось целое число в пределах int, а записано {v.Text}.",
                    $"{path}: expected an integer within the int range, got {v.Text}."));
            }
            throw TypeError(path, n, "целое число", "an integer number", lang);
        }

        public static long Long(JsonNode n, string path, Language lang)
        {
            if (n is JsonValue v && v.Kind == JsonKind.Number)
            {
                if (v.TryGetInt64(out long l)) return l;
                throw new JsonException(Loc.T(lang, $"{path}: ожидалось целое число, а записано {v.Text}.",
                    $"{path}: expected an integer number, got {v.Text}."));
            }
            throw TypeError(path, n, "целое число", "an integer number", lang);
        }

        public static double Double(JsonNode n, string path, Language lang)
        {
            if (n is JsonValue v && v.Kind == JsonKind.Number) return v.DoubleValue;
            throw TypeError(path, n, "число", "a number", lang);
        }

        public static float Float(JsonNode n, string path, Language lang) => (float)Double(n, path, lang);

        public static bool Bool(JsonNode n, string path, Language lang)
        {
            if (n is JsonValue v && v.Kind == JsonKind.Bool) return v.BoolValue;
            throw TypeError(path, n, "true или false", "true or false (a boolean)", lang);
        }

        /// <summary>Объект или null (JSON null).</summary>
        public static JsonObject Object(JsonNode n, string path, Language lang)
        {
            if (n == null || n.IsNull) return null;
            if (n is JsonObject o) return o;
            throw TypeError(path, n, "объект", "an object", lang);
        }

        /// <summary>Массив или null (JSON null).</summary>
        public static JsonArray Array(JsonNode n, string path, Language lang)
        {
            if (n == null || n.IsNull) return null;
            if (n is JsonArray a) return a;
            throw TypeError(path, n, "массив", "an array", lang);
        }

        /// <summary>Массив строк; элементы — только строки (null в списке путей — ошибка, а не NRE дальше).</summary>
        public static string[] StringArray(JsonNode n, string path, Language lang)
        {
            var a = Array(n, path, lang);
            if (a == null) return null;
            var r = new string[a.Count];
            for (int i = 0; i < r.Length; i++)
            {
                var e = a[i];
                if (e.Kind != JsonKind.String) throw TypeError($"{path}[{i}]", e, "строка", "a string", lang);
                r[i] = e.AsString;
            }
            return r;
        }

        /// <summary>Массив объектов через читатель элемента; null → null.</summary>
        public static T[] ObjectArray<T>(JsonNode n, string path, Language lang, Func<JsonObject, string, T> read)
        {
            var a = Array(n, path, lang);
            if (a == null) return null;
            var r = new T[a.Count];
            for (int i = 0; i < r.Length; i++)
            {
                string p = $"{path}[{i}]";
                var o = Object(a[i], p, lang);
                r[i] = o == null ? default : read(o, p);
            }
            return r;
        }

        /// <summary>Путь для сообщений: "apis[2].methods[0]" + "." + ключ.</summary>
        public static string Path(string parent, string key) => string.IsNullOrEmpty(parent) ? key : parent + "." + key;

        /// <summary>Ключ без учёта регистра (как привязывал Newtonsoft): "apiVersion" == "ApiVersion".</summary>
        public static string Key(string key) => key.ToLowerInvariant();

        // ---- запись ----

        public static JsonArray Strings(IEnumerable<string> items) => items == null ? null : new JsonArray(items);

        public static JsonArray Objects<T>(IEnumerable<T> items, Func<T, JsonNode> write)
        {
            if (items == null) return null;
            var a = new JsonArray();
            foreach (var i in items) a.Add(i == null ? JsonValue.Null : write(i));
            return a;
        }
    }
}
