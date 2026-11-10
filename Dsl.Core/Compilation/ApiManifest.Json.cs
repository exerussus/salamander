using System;
using System.Collections.Generic;
using Dsl.Json;
using Dsl.Text;

namespace Dsl.Compilation
{
    // =======================================================================
    // salamander-api.json ⇄ ApiManifest: ручная привязка без рефлексии.
    //
    // Запись: порядок ключей — порядок полей; необязательные ключи (summary,
    // doc, structs, …) пишутся только когда есть значение — так же выглядели
    // манифесты и раньше. Чтение: ключи без учёта регистра, неизвестные ключи
    // пропускаются (манифест новой версии игры читается старым инструментом),
    // тип значения строгий.
    // =======================================================================

    public sealed partial class ApiManifest
    {
        /// <summary>Разобрать salamander-api.json. null — документ равен null. Бросает на битом формате.</summary>
        public static ApiManifest Parse(string json, Language language = Language.Ru)
        {
            var root = JsonNode.Parse(json, language);
            if (root.IsNull) return null;
            var o = JsonRead.Object(root, "salamander-api.json", language);
            return new Reader(language).Manifest(o);
        }

        /// <summary>Манифест в дерево JSON (текст — <c>ToJson().ToJson(indented: true)</c>).</summary>
        public JsonObject ToJson()
        {
            var o = new JsonObject { ["apiVersion"] = ApiVersion };
            o["enums"] = Arr(Enums, WriteEnum);
            if (Structs != null) o["structs"] = Arr(Structs, WriteStruct);
            o["classes"] = Arr(Classes, WriteClass);
            if (ApiNamespaces != null) o["apiNamespaces"] = Arr(ApiNamespaces, WriteNamespace);
            o["apis"] = Arr(Apis, WriteApi);
            o["events"] = Arr(Events, WriteEvent);
            if (Archetypes != null) o["archetypes"] = Arr(Archetypes, WriteKind);
            return o;
        }

        // ---------------------------------------------------------------
        // Запись
        // ---------------------------------------------------------------

        private static JsonNode Arr<T>(T[] items, Func<T, JsonNode> write) => JsonRead.Objects(items, write);

        /// <summary>Справка: строка (один текст) или {"ru","en"}; пустая — null.</summary>
        internal static JsonNode WriteDoc(DocText d)
        {
            if (d == null || d.IsEmpty) return JsonValue.Null;
            if (!d.IsBilingual) return d.Ru;
            var o = new JsonObject();
            if (!string.IsNullOrEmpty(d.Ru)) o["ru"] = d.Ru;
            if (!string.IsNullOrEmpty(d.En)) o["en"] = d.En;
            return o;
        }

        private static void OptDoc(JsonObject o, string key, DocText d)
        {
            if (d != null && !d.IsEmpty) o[key] = WriteDoc(d);
        }

        /// <summary>Литерал (значение константы, дефолт): bool / целое / вещественное / строка / null.</summary>
        private static JsonNode WriteLiteral(object v)
        {
            switch (v)
            {
                case null: return JsonValue.Null;
                case bool b: return b;
                case int i: return i;
                case long l: return l;
                case float f: return f;
                case double d: return d;
                case string s: return s;
                default:
                    throw new FormatException($"salamander-api.json: unsupported literal type {v.GetType().Name}.");
            }
        }

        private static JsonNode WriteParam(ParamDef p)
        {
            var o = new JsonObject { ["name"] = p.Name, ["type"] = p.Type };
            OptDoc(o, "doc", p.Doc);
            return o;
        }

        private static JsonNode WriteEnum(EnumDef e)
        {
            var o = new JsonObject { ["name"] = e.Name };
            OptDoc(o, "summary", e.Summary);
            o["members"] = JsonRead.Strings(e.Members);
            if (e.MemberDocs != null)
            {
                var a = new JsonArray();
                foreach (var d in e.MemberDocs) a.Add(WriteDoc(d));
                o["memberDocs"] = a;
            }
            return o;
        }

        private static JsonNode WriteProp(PropDef p)
        {
            var o = new JsonObject { ["name"] = p.Name, ["type"] = p.Type, ["readOnly"] = p.ReadOnly };
            OptDoc(o, "doc", p.Doc);
            return o;
        }

        private static JsonNode WriteClass(ClassDef c)
        {
            var o = new JsonObject { ["name"] = c.Name };
            OptDoc(o, "summary", c.Summary);
            o["props"] = Arr(c.Props, WriteProp);
            if (c.Methods != null) o["methods"] = Arr(c.Methods, WriteMethod);
            return o;
        }

        private static JsonNode WriteMethod(MethodDef m)
        {
            var o = new JsonObject { ["name"] = m.Name };
            OptDoc(o, "summary", m.Summary);
            o["params"] = Arr(m.Params, WriteParam);
            o["returns"] = m.Returns;
            return o;
        }

        private static JsonNode WriteApi(ApiDef a)
        {
            var o = new JsonObject { ["name"] = a.Name };
            OptDoc(o, "summary", a.Summary);
            o["methods"] = Arr(a.Methods, WriteMethod);
            if (a.Consts != null) o["consts"] = Arr(a.Consts, WriteApiConst);
            return o;
        }

        private static JsonNode WriteNamespace(ApiNamespaceDef n) =>
            new JsonObject { ["name"] = n.Name, ["summary"] = WriteDoc(n.Summary) };

        private static JsonNode WriteApiConst(ApiConstDef c)
        {
            var o = new JsonObject { ["name"] = c.Name, ["type"] = c.Type, ["value"] = WriteLiteral(c.Value) };
            OptDoc(o, "doc", c.Doc);
            return o;
        }

        private static JsonNode WriteStructField(StructFieldDef f)
        {
            var o = new JsonObject { ["name"] = f.Name, ["type"] = f.Type, ["default"] = WriteLiteral(f.Default) };
            OptDoc(o, "doc", f.Doc);
            return o;
        }

        private static JsonNode WriteStruct(StructDef s)
        {
            var o = new JsonObject { ["name"] = s.Name };
            OptDoc(o, "summary", s.Summary);
            o["fields"] = Arr(s.Fields, WriteStructField);
            return o;
        }

        private static JsonNode WriteConst(ConstDef c)
        {
            var o = new JsonObject { ["name"] = c.Name, ["type"] = c.Type, ["required"] = c.Required };
            OptDoc(o, "doc", c.Doc);
            // ключ "default" есть всегда: факт наличия дефолта — в hasDefault
            o["default"] = WriteLiteral(c.Default);
            o["hasDefault"] = c.HasDefault;
            return o;
        }

        private static JsonNode WriteKind(ArchetypeKindDef k)
        {
            var o = new JsonObject { ["name"] = k.Name };
            OptDoc(o, "summary", k.Summary);
            if (k.KnownIds != null) o["knownIds"] = JsonRead.Strings(k.KnownIds);
            if (k.Consts != null) o["consts"] = Arr(k.Consts, WriteConst);
            o["events"] = Arr(k.Events, WriteEvent);
            if (k.EventsOptional) o["eventsOptional"] = true;
            return o;
        }

        private static JsonNode WriteEvent(EventDef e)
        {
            var o = new JsonObject { ["name"] = e.Name };
            OptDoc(o, "summary", e.Summary);
            o["params"] = Arr(e.Params, WriteParam);
            return o;
        }

        // ---------------------------------------------------------------
        // Чтение
        // ---------------------------------------------------------------

        /// <summary>Справка из JSON: строка, {"ru","en"} или null.</summary>
        internal static DocText ReadDoc(JsonNode n, string path, Language lang)
        {
            if (n == null || n.IsNull) return null;
            if (n.Kind == JsonKind.String) return n.AsString;
            if (n is JsonObject o)
            {
                string ru = null, en = null;
                foreach (var kv in o)
                {
                    switch (JsonRead.Key(kv.Key))
                    {
                        case "ru": ru = JsonRead.String(kv.Value, path + ".ru", lang); break;
                        case "en": en = JsonRead.String(kv.Value, path + ".en", lang); break;
                    }
                }
                return ru == null && en == null ? null : new DocText(ru, en);
            }
            throw new JsonException(Loc.T(lang,
                $"salamander-api.json: {path} — справка должна быть строкой или объектом {{\"ru\", \"en\"}}, а записано: {JsonRead.KindName(n, lang)}.",
                $"salamander-api.json: {path} — a doc/summary must be a string or a {{\"ru\", \"en\"}} object, got {JsonRead.KindName(n, lang)}."));
        }

        private sealed class Reader
        {
            private readonly Language _lang;
            public Reader(Language lang) { _lang = lang; }

            private string S(JsonNode n, string p) => JsonRead.String(n, p, _lang);
            private DocText D(JsonNode n, string p) => ReadDoc(n, p, _lang);
            private T[] A<T>(JsonNode n, string p, Func<JsonObject, string, T> read) => JsonRead.ObjectArray(n, p, _lang, read);
            private static string P(string parent, string key) => parent + "." + key;

            /// <summary>Литерал как его ждёт DecodeLiteral: long / double / bool / string / null.</summary>
            private object Lit(JsonNode n, string p)
            {
                if (n == null || n.IsNull) return null;
                if (n is JsonValue v)
                {
                    switch (v.Kind)
                    {
                        case JsonKind.Bool: return (bool)v;
                        case JsonKind.String: return v.AsString;
                        case JsonKind.Number:
                            if (v.IsIntegerLiteral && v.TryGetInt64(out long l)) return l;
                            return v.DoubleValue;
                    }
                }
                throw JsonRead.TypeError(p, n, "число, строка, true/false или null", "a number, a string, true/false or null", _lang);
            }

            public ApiManifest Manifest(JsonObject o)
            {
                var m = new ApiManifest();
                const string root = "salamander-api.json";
                foreach (var kv in o)
                {
                    string p = kv.Key;
                    var v = kv.Value;
                    switch (JsonRead.Key(kv.Key))
                    {
                        case "apiversion": m.ApiVersion = JsonRead.Int(v, root + ": apiVersion", _lang); break;
                        case "enums": m.Enums = A(v, p, Enum); break;
                        case "structs": m.Structs = A(v, p, Struct); break;
                        case "classes": m.Classes = A(v, p, Class); break;
                        case "apinamespaces": m.ApiNamespaces = A(v, p, Namespace); break;
                        case "apis": m.Apis = A(v, p, Api); break;
                        case "events": m.Events = A(v, p, Event); break;
                        case "archetypes": m.Archetypes = A(v, p, Kind); break;
                    }
                }
                return m;
            }

            private ParamDef Param(JsonObject o, string path)
            {
                var d = new ParamDef();
                foreach (var kv in o)
                {
                    string p = P(path, kv.Key);
                    switch (JsonRead.Key(kv.Key))
                    {
                        case "name": d.Name = S(kv.Value, p); break;
                        case "type": d.Type = S(kv.Value, p); break;
                        case "doc": d.Doc = D(kv.Value, p); break;
                    }
                }
                return d;
            }

            private EnumDef Enum(JsonObject o, string path)
            {
                var d = new EnumDef();
                foreach (var kv in o)
                {
                    string p = P(path, kv.Key);
                    switch (JsonRead.Key(kv.Key))
                    {
                        case "name": d.Name = S(kv.Value, p); break;
                        case "summary": d.Summary = D(kv.Value, p); break;
                        case "members": d.Members = JsonRead.StringArray(kv.Value, p, _lang); break;
                        case "memberdocs":
                        {
                            var a = JsonRead.Array(kv.Value, p, _lang);
                            if (a == null) { d.MemberDocs = null; break; }
                            d.MemberDocs = new DocText[a.Count];
                            for (int i = 0; i < a.Count; i++) d.MemberDocs[i] = D(a[i], $"{p}[{i}]");
                            break;
                        }
                    }
                }
                return d;
            }

            private PropDef Prop(JsonObject o, string path)
            {
                var d = new PropDef();
                foreach (var kv in o)
                {
                    string p = P(path, kv.Key);
                    switch (JsonRead.Key(kv.Key))
                    {
                        case "name": d.Name = S(kv.Value, p); break;
                        case "type": d.Type = S(kv.Value, p); break;
                        case "readonly": d.ReadOnly = JsonRead.Bool(kv.Value, p, _lang); break;
                        case "doc": d.Doc = D(kv.Value, p); break;
                    }
                }
                return d;
            }

            private ClassDef Class(JsonObject o, string path)
            {
                var d = new ClassDef();
                foreach (var kv in o)
                {
                    string p = P(path, kv.Key);
                    switch (JsonRead.Key(kv.Key))
                    {
                        case "name": d.Name = S(kv.Value, p); break;
                        case "summary": d.Summary = D(kv.Value, p); break;
                        case "props": d.Props = A(kv.Value, p, Prop); break;
                        case "methods": d.Methods = A(kv.Value, p, Method); break;
                    }
                }
                return d;
            }

            private MethodDef Method(JsonObject o, string path)
            {
                var d = new MethodDef();
                foreach (var kv in o)
                {
                    string p = P(path, kv.Key);
                    switch (JsonRead.Key(kv.Key))
                    {
                        case "name": d.Name = S(kv.Value, p); break;
                        case "summary": d.Summary = D(kv.Value, p); break;
                        case "params": d.Params = A(kv.Value, p, Param); break;
                        case "returns": d.Returns = S(kv.Value, p); break;
                    }
                }
                return d;
            }

            private ApiDef Api(JsonObject o, string path)
            {
                var d = new ApiDef();
                foreach (var kv in o)
                {
                    string p = P(path, kv.Key);
                    switch (JsonRead.Key(kv.Key))
                    {
                        case "name": d.Name = S(kv.Value, p); break;
                        case "summary": d.Summary = D(kv.Value, p); break;
                        case "methods": d.Methods = A(kv.Value, p, Method); break;
                        case "consts": d.Consts = A(kv.Value, p, ApiConst); break;
                    }
                }
                return d;
            }

            private ApiNamespaceDef Namespace(JsonObject o, string path)
            {
                var d = new ApiNamespaceDef();
                foreach (var kv in o)
                {
                    string p = P(path, kv.Key);
                    switch (JsonRead.Key(kv.Key))
                    {
                        case "name": d.Name = S(kv.Value, p); break;
                        case "summary": d.Summary = D(kv.Value, p); break;
                    }
                }
                return d;
            }

            private ApiConstDef ApiConst(JsonObject o, string path)
            {
                var d = new ApiConstDef();
                foreach (var kv in o)
                {
                    string p = P(path, kv.Key);
                    switch (JsonRead.Key(kv.Key))
                    {
                        case "name": d.Name = S(kv.Value, p); break;
                        case "type": d.Type = S(kv.Value, p); break;
                        case "value": d.Value = Lit(kv.Value, p); break;
                        case "doc": d.Doc = D(kv.Value, p); break;
                    }
                }
                return d;
            }

            private StructFieldDef StructField(JsonObject o, string path)
            {
                var d = new StructFieldDef();
                foreach (var kv in o)
                {
                    string p = P(path, kv.Key);
                    switch (JsonRead.Key(kv.Key))
                    {
                        case "name": d.Name = S(kv.Value, p); break;
                        case "type": d.Type = S(kv.Value, p); break;
                        case "default": d.Default = Lit(kv.Value, p); break;
                        case "doc": d.Doc = D(kv.Value, p); break;
                    }
                }
                return d;
            }

            private StructDef Struct(JsonObject o, string path)
            {
                var d = new StructDef();
                foreach (var kv in o)
                {
                    string p = P(path, kv.Key);
                    switch (JsonRead.Key(kv.Key))
                    {
                        case "name": d.Name = S(kv.Value, p); break;
                        case "summary": d.Summary = D(kv.Value, p); break;
                        case "fields": d.Fields = A(kv.Value, p, StructField); break;
                    }
                }
                return d;
            }

            private ConstDef Const(JsonObject o, string path)
            {
                var d = new ConstDef();
                foreach (var kv in o)
                {
                    string p = P(path, kv.Key);
                    switch (JsonRead.Key(kv.Key))
                    {
                        case "name": d.Name = S(kv.Value, p); break;
                        case "type": d.Type = S(kv.Value, p); break;
                        case "required": d.Required = JsonRead.Bool(kv.Value, p, _lang); break;
                        case "doc": d.Doc = D(kv.Value, p); break;
                        case "default": d.Default = Lit(kv.Value, p); break;
                        case "hasdefault": d.HasDefault = JsonRead.Bool(kv.Value, p, _lang); break;
                    }
                }
                return d;
            }

            private ArchetypeKindDef Kind(JsonObject o, string path)
            {
                var d = new ArchetypeKindDef();
                foreach (var kv in o)
                {
                    string p = P(path, kv.Key);
                    switch (JsonRead.Key(kv.Key))
                    {
                        case "name": d.Name = S(kv.Value, p); break;
                        case "summary": d.Summary = D(kv.Value, p); break;
                        case "knownids": d.KnownIds = JsonRead.StringArray(kv.Value, p, _lang); break;
                        case "consts": d.Consts = A(kv.Value, p, Const); break;
                        case "events": d.Events = A(kv.Value, p, Event); break;
                        case "eventsoptional": d.EventsOptional = JsonRead.Bool(kv.Value, p, _lang); break;
                    }
                }
                return d;
            }

            private EventDef Event(JsonObject o, string path)
            {
                var d = new EventDef();
                foreach (var kv in o)
                {
                    string p = P(path, kv.Key);
                    switch (JsonRead.Key(kv.Key))
                    {
                        case "name": d.Name = S(kv.Value, p); break;
                        case "summary": d.Summary = D(kv.Value, p); break;
                        case "params": d.Params = A(kv.Value, p, Param); break;
                    }
                }
                return d;
            }
        }
    }
}
