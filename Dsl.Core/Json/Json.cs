using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dsl.Text;

namespace Dsl.Json
{
    // =======================================================================
    // Собственный JSON ядра: DOM + парсер + писатель, без внешних библиотек.
    //
    // Зачем свой: ядро собирается под Unity (netstandard2.1, IL2CPP) и под
    // обычный .NET одним и тем же кодом. Newtonsoft тянул отдельный пакет и
    // рефлексию (link.xml под IL2CPP), System.Text.Json в Unity — это пять
    // NuGet-DLL. Ядру же нужно немного: манифесты, build-файл, сессия IDE и
    // сообщения LSP — всё это маленькие документы, читаемые вручную.
    //
    // Что принимает парсер (мягкость, на которую опираются существующие моды,
    // писавшиеся под Newtonsoft):
    //  - комментарии // и /* */;
    //  - запятую после последнего элемента массива или объекта;
    //  - BOM в начале текста.
    // Что НЕ принимает (как и строгий JSON): одинарные кавычки, ключи без
    // кавычек, NaN/Infinity, ведущий '+', шестнадцатеричные числа, текст после
    // корневого значения. Повторный ключ — побеждает последний.
    //
    // Регистр ключей: индексатор JsonObject — точный (так нужно протоколу LSP).
    // Привязка манифестов регистр не различает — это делает сам маппинг.
    // =======================================================================

    public enum JsonKind { Null, Bool, Number, String, Array, Object }

    /// <summary>Ошибка разбора или формата JSON. Line/Column — с 1; 0 — позиция неизвестна.</summary>
    public sealed class JsonException : FormatException
    {
        public int Line { get; }
        public int Column { get; }

        public JsonException(string message, int line = 0, int column = 0)
            : base(message)
        {
            Line = line;
            Column = column;
        }
    }

    /// <summary>Узел дерева JSON.</summary>
    public abstract class JsonNode
    {
        public abstract JsonKind Kind { get; }

        public bool IsNull => Kind == JsonKind.Null;

        /// <summary>Значение по ключу объекта; у не-объекта и при отсутствии ключа — null.</summary>
        public virtual JsonNode this[string key]
        {
            get => null;
            set => throw new InvalidOperationException($"JSON {Kind} is not an object.");
        }

        /// <summary>Элемент массива.</summary>
        public virtual JsonNode this[int index]
        {
            get => throw new InvalidOperationException($"JSON {Kind} is not an array.");
            set => throw new InvalidOperationException($"JSON {Kind} is not an array.");
        }

        /// <summary>Строка, если узел — строка; иначе null.</summary>
        public string AsString => this is JsonValue v && v.Kind == JsonKind.String ? v.Text : null;

        public string ToJson(bool indented = false)
        {
            var sb = new StringBuilder(256);
            JsonWriter.Write(sb, this, indented, 0);
            return sb.ToString();
        }

        public override string ToString() => ToJson(true);

        // ---- разбор ----

        /// <summary>Разобрать документ. Бросает <see cref="JsonException"/> с позицией.</summary>
        public static JsonNode Parse(string text, Language language = Language.Ru)
            => new JsonParser(text, language).ParseDocument();

        // ---- неявные приведения: удобно строить сообщения ----

        public static implicit operator JsonNode(string value) => JsonValue.String(value);
        public static implicit operator JsonNode(bool value) => value ? JsonValue.True : JsonValue.False;
        public static implicit operator JsonNode(int value) => JsonValue.Number(value);
        public static implicit operator JsonNode(long value) => JsonValue.Number(value);
        public static implicit operator JsonNode(double value) => JsonValue.Number(value);
        public static implicit operator JsonNode(float value) => JsonValue.Number(value);

        // ---- явные приведения: чтение ----
        // Число в строку и строку в число НЕ приводим: тип значения в документе
        // — часть контракта. Исключение — (string) у числа и bool: это текст
        // значения, он нужен для сообщений и путей.

        public static explicit operator string(JsonNode node)
        {
            if (node == null) return null;
            switch (node.Kind)
            {
                case JsonKind.Null: return null;
                case JsonKind.String:
                case JsonKind.Number: return ((JsonValue)node).Text;
                case JsonKind.Bool: return ((JsonValue)node).BoolValue ? "true" : "false";
                default: throw new InvalidCastException($"JSON {node.Kind} cannot be read as a string.");
            }
        }

        public static explicit operator int(JsonNode node)
        {
            if (node is JsonValue v && v.TryGetInt32(out int i)) return i;
            throw new InvalidCastException($"JSON {Describe(node)} is not an int.");
        }

        public static explicit operator int?(JsonNode node)
        {
            if (node == null || node.IsNull) return null;
            return (int)node;
        }

        public static explicit operator long(JsonNode node)
        {
            if (node is JsonValue v && v.TryGetInt64(out long l)) return l;
            throw new InvalidCastException($"JSON {Describe(node)} is not an integer.");
        }

        public static explicit operator double(JsonNode node)
        {
            if (node is JsonValue v && v.Kind == JsonKind.Number) return v.DoubleValue;
            throw new InvalidCastException($"JSON {Describe(node)} is not a number.");
        }

        public static explicit operator bool(JsonNode node)
        {
            if (node is JsonValue v && v.Kind == JsonKind.Bool) return v.BoolValue;
            throw new InvalidCastException($"JSON {Describe(node)} is not a bool.");
        }

        public static explicit operator bool?(JsonNode node)
        {
            if (node == null || node.IsNull) return null;
            return (bool)node;
        }

        internal static string Describe(JsonNode node) => node == null ? "(missing)" : node.Kind.ToString();
    }

    /// <summary>Скаляр: null, bool, число, строка. Неизменяем.</summary>
    public sealed class JsonValue : JsonNode
    {
        public static readonly JsonValue Null = new JsonValue(JsonKind.Null, null, false, 0);
        public static readonly JsonValue True = new JsonValue(JsonKind.Bool, null, true, 0);
        public static readonly JsonValue False = new JsonValue(JsonKind.Bool, null, false, 0);

        private readonly JsonKind _kind;
        // строка — её значение; число — его текст в документе (точный, без потерь)
        internal readonly string Text;
        internal readonly bool BoolValue;
        private readonly double _number;

        private JsonValue(JsonKind kind, string text, bool b, double number)
        {
            _kind = kind;
            Text = text;
            BoolValue = b;
            _number = number;
        }

        public override JsonKind Kind => _kind;

        public static JsonValue String(string value) =>
            value == null ? Null : new JsonValue(JsonKind.String, value, false, 0);

        public static JsonValue Number(long value) =>
            new JsonValue(JsonKind.Number, value.ToString(CultureInfo.InvariantCulture), false, value);

        public static JsonValue Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentException("JSON cannot represent NaN or Infinity.", nameof(value));
            return new JsonValue(JsonKind.Number, FormatReal(value.ToString("R", CultureInfo.InvariantCulture)), false, value);
        }

        /// <summary>float пишется своим кратчайшим видом: 0.1f → 0.1, а не 0.100000001490116.</summary>
        public static JsonValue Number(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentException("JSON cannot represent NaN or Infinity.", nameof(value));
            string s = FormatReal(value.ToString("R", CultureInfo.InvariantCulture));
            return new JsonValue(JsonKind.Number, s, false, double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        /// <summary>Из уже проверенного парсером текста числа.</summary>
        internal static JsonValue NumberFromText(string text, double value) =>
            new JsonValue(JsonKind.Number, text, false, value);

        // 3 → "3.0": вещественное остаётся вещественным при чтении обратно
        // (так же писал Newtonsoft, и существующие манифесты выглядят так же)
        private static string FormatReal(string s)
        {
            if (s.IndexOf('.') >= 0 || s.IndexOf('E') >= 0 || s.IndexOf('e') >= 0) return s;
            return s + ".0";
        }

        public double DoubleValue => _number;

        /// <summary>Число записано без дробной части и экспоненты (3, -7), а не 3.0 или 1e2.</summary>
        public bool IsIntegerLiteral =>
            _kind == JsonKind.Number && Text.IndexOf('.') < 0 && Text.IndexOf('e') < 0 && Text.IndexOf('E') < 0;

        /// <summary>
        /// Целое: литерал без дробной части или вещественный с целым значением (3.0, 1e2).
        /// 3.5 — не целое (а не «округлим молча», как Convert.ToInt32).
        /// </summary>
        public bool TryGetInt64(out long value)
        {
            value = 0;
            if (_kind != JsonKind.Number) return false;
            if (IsIntegerLiteral)
                return long.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
            if (Math.Floor(_number) != _number || _number < long.MinValue || _number >= 9.2233720368547758E18) return false;
            value = (long)_number;
            return true;
        }

        public bool TryGetInt32(out int value)
        {
            value = 0;
            if (!TryGetInt64(out long l) || l < int.MinValue || l > int.MaxValue) return false;
            value = (int)l;
            return true;
        }
    }

    /// <summary>Массив.</summary>
    public sealed class JsonArray : JsonNode, IEnumerable<JsonNode>
    {
        private readonly List<JsonNode> _items;

        public JsonArray() { _items = new List<JsonNode>(); }

        public JsonArray(params JsonNode[] items)
        {
            _items = new List<JsonNode>(items?.Length ?? 0);
            if (items != null) foreach (var i in items) Add(i);
        }

        public JsonArray(IEnumerable<string> items)
        {
            _items = new List<JsonNode>();
            if (items != null) foreach (var s in items) Add(s);
        }

        public override JsonKind Kind => JsonKind.Array;

        public int Count => _items.Count;

        public override JsonNode this[int index]
        {
            get => _items[index];
            set => _items[index] = value ?? JsonValue.Null;
        }

        public void Add(JsonNode item) => _items.Add(item ?? JsonValue.Null);
        public void Insert(int index, JsonNode item) => _items.Insert(index, item ?? JsonValue.Null);
        public void RemoveAt(int index) => _items.RemoveAt(index);
        public void Clear() => _items.Clear();

        public List<JsonNode>.Enumerator GetEnumerator() => _items.GetEnumerator();
        IEnumerator<JsonNode> IEnumerable<JsonNode>.GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }

    /// <summary>Объект. Порядок ключей сохраняется (и при записи, и при правке).</summary>
    public sealed class JsonObject : JsonNode, IEnumerable<KeyValuePair<string, JsonNode>>
    {
        private readonly List<KeyValuePair<string, JsonNode>> _props = new List<KeyValuePair<string, JsonNode>>();
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);

        public override JsonKind Kind => JsonKind.Object;

        public int Count => _props.Count;

        /// <summary>Точный (с учётом регистра) доступ; отсутствующий ключ — null, запись null — JSON null.</summary>
        public override JsonNode this[string key]
        {
            get => key != null && _index.TryGetValue(key, out int i) ? _props[i].Value : null;
            set
            {
                if (key == null) throw new ArgumentNullException(nameof(key));
                var v = value ?? JsonValue.Null;
                if (_index.TryGetValue(key, out int i)) _props[i] = new KeyValuePair<string, JsonNode>(key, v);
                else
                {
                    _index[key] = _props.Count;
                    _props.Add(new KeyValuePair<string, JsonNode>(key, v));
                }
            }
        }

        public void Add(string key, JsonNode value) => this[key] = value;

        public bool ContainsKey(string key) => key != null && _index.ContainsKey(key);

        public bool TryGetValue(string key, out JsonNode value)
        {
            value = this[key];
            return value != null;
        }

        public bool Remove(string key)
        {
            if (key == null || !_index.TryGetValue(key, out int i)) return false;
            _props.RemoveAt(i);
            _index.Remove(key);
            for (int j = i; j < _props.Count; j++) _index[_props[j].Key] = j;
            return true;
        }

        public List<KeyValuePair<string, JsonNode>>.Enumerator GetEnumerator() => _props.GetEnumerator();
        IEnumerator<KeyValuePair<string, JsonNode>> IEnumerable<KeyValuePair<string, JsonNode>>.GetEnumerator() => _props.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _props.GetEnumerator();
    }

    // =======================================================================
    // Писатель
    // =======================================================================

    internal static class JsonWriter
    {
        // Отступ — два пробела, перевод строки — '\n' на любой ОС: экспорт
        // манифеста детерминирован и не шумит в диффах между Windows и Linux.
        public static void Write(StringBuilder sb, JsonNode node, bool indented, int depth)
        {
            switch (node?.Kind ?? JsonKind.Null)
            {
                case JsonKind.Null: sb.Append("null"); return;
                case JsonKind.Bool: sb.Append(((JsonValue)node).BoolValue ? "true" : "false"); return;
                case JsonKind.Number: sb.Append(((JsonValue)node).Text); return;
                case JsonKind.String: WriteString(sb, ((JsonValue)node).Text); return;
                case JsonKind.Array:
                {
                    var a = (JsonArray)node;
                    if (a.Count == 0) { sb.Append("[]"); return; }
                    sb.Append('[');
                    for (int i = 0; i < a.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        if (indented) NewLine(sb, depth + 1);
                        Write(sb, a[i], indented, depth + 1);
                    }
                    if (indented) NewLine(sb, depth);
                    sb.Append(']');
                    return;
                }
                case JsonKind.Object:
                {
                    var o = (JsonObject)node;
                    if (o.Count == 0) { sb.Append("{}"); return; }
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in o)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        if (indented) NewLine(sb, depth + 1);
                        WriteString(sb, kv.Key);
                        sb.Append(indented ? ": " : ":");
                        Write(sb, kv.Value, indented, depth + 1);
                    }
                    if (indented) NewLine(sb, depth);
                    sb.Append('}');
                    return;
                }
            }
        }

        private static void NewLine(StringBuilder sb, int depth)
        {
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static readonly char[] Hex = "0123456789abcdef".ToCharArray();

        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); continue;
                    case '\\': sb.Append("\\\\"); continue;
                    case '\n': sb.Append("\\n"); continue;
                    case '\r': sb.Append("\\r"); continue;
                    case '\t': sb.Append("\\t"); continue;
                    case '\b': sb.Append("\\b"); continue;
                    case '\f': sb.Append("\\f"); continue;
                }
                bool escape = c < 0x20;
                // одиночный суррогат в UTF-8 не кодируется (станет '?') — пишем эскейпом,
                // тогда он переживает и файл, и сообщение LSP
                if (!escape && char.IsSurrogate(c))
                {
                    bool paired = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
                    if (paired) { sb.Append(c).Append(s[i + 1]); i++; continue; }
                    escape = true;
                }
                if (escape)
                    sb.Append("\\u").Append(Hex[(c >> 12) & 0xF]).Append(Hex[(c >> 8) & 0xF])
                      .Append(Hex[(c >> 4) & 0xF]).Append(Hex[c & 0xF]);
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }

    // =======================================================================
    // Парсер
    // =======================================================================

    internal sealed class JsonParser
    {
        /// <summary>Предел вложенности: враждебный "[[[[…" не должен уронить стек.</summary>
        public const int MaxDepth = 256;

        private readonly string _s;
        private readonly Language _lang;
        private int _pos;
        private int _line = 1;
        private int _lineStart;

        public JsonParser(string text, Language language)
        {
            _s = text ?? "";
            _lang = language;
            if (_s.Length > 0 && _s[0] == '﻿') _pos = 1;
        }

        public JsonNode ParseDocument()
        {
            SkipTrivia();
            if (_pos >= _s.Length) throw Error("пустой документ", "empty document");
            var root = ParseValue(0);
            SkipTrivia();
            if (_pos < _s.Length) throw Error("лишний текст после конца документа", "unexpected text after the end of the document");
            return root;
        }

        private JsonException Error(string ru, string en)
        {
            int col = _pos - _lineStart + 1;
            string msg = Loc.T(_lang, $"JSON: {ru} (строка {_line}, столбец {col})", $"JSON: {en} (line {_line}, column {col})");
            return new JsonException(msg, _line, col);
        }

        private JsonNode ParseValue(int depth)
        {
            if (depth > MaxDepth) throw Error($"вложенность больше {MaxDepth}", $"nesting deeper than {MaxDepth}");
            if (_pos >= _s.Length) throw Error("неожиданный конец документа", "unexpected end of document");
            char c = _s[_pos];
            switch (c)
            {
                case '{': return ParseObject(depth);
                case '[': return ParseArray(depth);
                case '"': return JsonValue.String(ParseString());
                case 't': ExpectWord("true"); return JsonValue.True;
                case 'f': ExpectWord("false"); return JsonValue.False;
                case 'n': ExpectWord("null"); return JsonValue.Null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                    if (c == '\'')
                        throw Error("строки пишутся в двойных кавычках", "strings must use double quotes");
                    throw Error($"неожиданный символ '{c}'", $"unexpected character '{c}'");
            }
        }

        private void ExpectWord(string word)
        {
            if (string.CompareOrdinal(_s, _pos, word, 0, word.Length) != 0 || (_pos + word.Length < _s.Length && char.IsLetterOrDigit(_s[_pos + word.Length])))
                throw Error($"неизвестное слово (ожидалось {word})", $"unknown word (expected {word})");
            _pos += word.Length;
        }

        private JsonObject ParseObject(int depth)
        {
            var obj = new JsonObject();
            _pos++; // {
            while (true)
            {
                SkipTrivia();
                if (_pos >= _s.Length) throw Error("объект не закрыт", "unterminated object");
                if (_s[_pos] == '}') { _pos++; return obj; }
                if (_s[_pos] != '"')
                    throw Error("ожидался ключ в двойных кавычках", "expected a property name in double quotes");
                string key = ParseString();
                SkipTrivia();
                if (_pos >= _s.Length || _s[_pos] != ':') throw Error($"после ключа \"{key}\" ожидалось ':'", $"expected ':' after the key \"{key}\"");
                _pos++;
                SkipTrivia();
                obj[key] = ParseValue(depth + 1);
                SkipTrivia();
                if (_pos >= _s.Length) throw Error("объект не закрыт", "unterminated object");
                char c = _s[_pos];
                if (c == ',') { _pos++; continue; } // запятая перед '}' допустима
                if (c == '}') { _pos++; return obj; }
                throw Error("ожидалось ',' или '}'", "expected ',' or '}'");
            }
        }

        private JsonArray ParseArray(int depth)
        {
            var arr = new JsonArray();
            _pos++; // [
            while (true)
            {
                SkipTrivia();
                if (_pos >= _s.Length) throw Error("массив не закрыт", "unterminated array");
                if (_s[_pos] == ']') { _pos++; return arr; }
                arr.Add(ParseValue(depth + 1));
                SkipTrivia();
                if (_pos >= _s.Length) throw Error("массив не закрыт", "unterminated array");
                char c = _s[_pos];
                if (c == ',') { _pos++; continue; }
                if (c == ']') { _pos++; return arr; }
                throw Error("ожидалось ',' или ']'", "expected ',' or ']'");
            }
        }

        private string ParseString()
        {
            _pos++; // "
            StringBuilder sb = null;
            int runStart = _pos;
            while (true)
            {
                if (_pos >= _s.Length) throw Error("строка не закрыта", "unterminated string");
                char c = _s[_pos];
                if (c == '"')
                {
                    string result = sb == null ? _s.Substring(runStart, _pos - runStart)
                                               : sb.Append(_s, runStart, _pos - runStart).ToString();
                    _pos++;
                    return result;
                }
                if (c == '\n' || c == '\r') throw Error("перевод строки внутри строки", "line break inside a string");
                if (c != '\\') { _pos++; continue; }

                sb ??= new StringBuilder();
                sb.Append(_s, runStart, _pos - runStart);
                _pos++;
                if (_pos >= _s.Length) throw Error("строка не закрыта", "unterminated string");
                char e = _s[_pos++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                    {
                        if (_pos + 4 > _s.Length) throw Error("обрыв \\u-эскейпа", "truncated \\u escape");
                        int code = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            int h = HexVal(_s[_pos + k]);
                            if (h < 0) { _pos += k; throw Error("некорректный \\u-эскейп", "invalid \\u escape"); }
                            code = code * 16 + h;
                        }
                        _pos += 4;
                        sb.Append((char)code);
                        break;
                    }
                    default:
                        _pos--;
                        throw Error($"неизвестный эскейп '\\{e}'", $"unknown escape '\\{e}'");
                }
                runStart = _pos;
            }
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        private JsonValue ParseNumber()
        {
            int start = _pos;
            if (_s[_pos] == '-') _pos++;
            if (_pos >= _s.Length || !IsDigit(_s[_pos])) throw Error("некорректное число", "invalid number");
            if (_s[_pos] == '0')
            {
                _pos++;
                if (_pos < _s.Length && IsDigit(_s[_pos])) throw Error("ведущие нули в числе", "leading zeros in a number");
            }
            else while (_pos < _s.Length && IsDigit(_s[_pos])) _pos++;

            if (_pos < _s.Length && _s[_pos] == '.')
            {
                _pos++;
                if (_pos >= _s.Length || !IsDigit(_s[_pos])) throw Error("после точки нужна цифра", "a digit must follow the decimal point");
                while (_pos < _s.Length && IsDigit(_s[_pos])) _pos++;
            }
            if (_pos < _s.Length && (_s[_pos] == 'e' || _s[_pos] == 'E'))
            {
                _pos++;
                if (_pos < _s.Length && (_s[_pos] == '+' || _s[_pos] == '-')) _pos++;
                if (_pos >= _s.Length || !IsDigit(_s[_pos])) throw Error("некорректная экспонента", "invalid exponent");
                while (_pos < _s.Length && IsDigit(_s[_pos])) _pos++;
            }
            if (_pos < _s.Length && char.IsLetter(_s[_pos])) throw Error("некорректное число", "invalid number");

            string text = _s.Substring(start, _pos - start);
            double value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (double.IsInfinity(value)) { _pos = start; throw Error("число вне диапазона", "number out of range"); }
            return JsonValue.NumberFromText(text, value);
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private void SkipTrivia()
        {
            while (_pos < _s.Length)
            {
                char c = _s[_pos];
                if (c == '\n') { _pos++; _line++; _lineStart = _pos; continue; }
                if (c == ' ' || c == '\t' || c == '\r') { _pos++; continue; }
                if (c == '/' && _pos + 1 < _s.Length)
                {
                    if (_s[_pos + 1] == '/')
                    {
                        while (_pos < _s.Length && _s[_pos] != '\n') _pos++;
                        continue;
                    }
                    if (_s[_pos + 1] == '*')
                    {
                        int end = _s.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                        if (end < 0) throw Error("комментарий /* не закрыт", "unterminated /* comment");
                        for (int i = _pos; i < end; i++)
                            if (_s[i] == '\n') { _line++; _lineStart = i + 1; }
                        _pos = end + 2;
                        continue;
                    }
                }
                return;
            }
        }
    }
}
