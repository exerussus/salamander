using System;
using System.Collections.Generic;
using Dsl.Runtime;
using Dsl.Semantics;
using Newtonsoft.Json;

namespace Dsl.Compilation
{
    /// <summary>
    /// Манифест API хоста (salamander-api.json): всё, что игра открыла скриптам —
    /// енумы, классы со свойствами, API-методы, события — с человекочитаемыми
    /// описаниями (summary у методов/событий, name/type/doc у каждого параметра).
    /// Источник истины один: игра экспортирует манифест из своего HostRegistry,
    /// а инструменты (CLI-чекер, расширение VS Code) читают его, чтобы
    /// компилировать, дополнять код и показывать документацию ВНЕ игры.
    ///
    /// Импорт строит реестр с делегатами-заглушками: для проверки типов нужны
    /// только сигнатуры, VM в инструментах не запускается.
    /// </summary>
    public sealed class ApiManifest
    {
        public sealed class ParamDef
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("type")] public string Type;
            [JsonProperty("doc", NullValueHandling = NullValueHandling.Ignore)] public string Doc;
        }

        public sealed class EnumDef
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("summary", NullValueHandling = NullValueHandling.Ignore)] public string Summary;
            [JsonProperty("members")] public string[] Members = Array.Empty<string>();
        }

        public sealed class PropDef
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("type")] public string Type;
            [JsonProperty("readOnly")] public bool ReadOnly;
            [JsonProperty("doc", NullValueHandling = NullValueHandling.Ignore)] public string Doc;
        }

        public sealed class ClassDef
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("summary", NullValueHandling = NullValueHandling.Ignore)] public string Summary;
            [JsonProperty("props")] public PropDef[] Props = Array.Empty<PropDef>();
        }

        public sealed class MethodDef
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("summary", NullValueHandling = NullValueHandling.Ignore)] public string Summary;
            [JsonProperty("params")] public ParamDef[] Params = Array.Empty<ParamDef>();
            [JsonProperty("returns")] public string Returns = "void";
        }

        public sealed class ApiDef
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("summary", NullValueHandling = NullValueHandling.Ignore)] public string Summary;
            [JsonProperty("methods")] public MethodDef[] Methods = Array.Empty<MethodDef>();
        }

        public sealed class ArchetypeKindDef
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("summary", NullValueHandling = NullValueHandling.Ignore)] public string Summary;
            [JsonProperty("knownIds", NullValueHandling = NullValueHandling.Ignore)] public string[] KnownIds;
            [JsonProperty("events")] public EventDef[] Events = Array.Empty<EventDef>();
        }

        public sealed class EventDef
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("summary", NullValueHandling = NullValueHandling.Ignore)] public string Summary;
            [JsonProperty("params")] public ParamDef[] Params = Array.Empty<ParamDef>();
        }

        [JsonProperty("apiVersion")] public int ApiVersion;
        [JsonProperty("enums")] public EnumDef[] Enums = Array.Empty<EnumDef>();
        [JsonProperty("classes")] public ClassDef[] Classes = Array.Empty<ClassDef>();
        [JsonProperty("apis")] public ApiDef[] Apis = Array.Empty<ApiDef>();
        [JsonProperty("events")] public EventDef[] Events = Array.Empty<EventDef>();
        [JsonProperty("archetypes", NullValueHandling = NullValueHandling.Ignore)] public ArchetypeKindDef[] Archetypes;

        // ===================================================================
        // Экспорт: HostRegistry → json
        // ===================================================================

        public static string Export(HostRegistry r, int apiVersion)
        {
            var m = new ApiManifest { ApiVersion = apiVersion };

            var enums = new List<EnumDef>();
            foreach (var e in r.AllEnums)
                enums.Add(new EnumDef { Name = e.Name, Summary = e.Summary, Members = e.Names });
            m.Enums = enums.ToArray();

            var classes = new List<ClassDef>();
            foreach (var c in r.AllClasses)
            {
                var props = new List<PropDef>();
                foreach (var p in c.Props.Values)
                    props.Add(new PropDef { Name = p.Name, Type = TypeToString(r, p.Type), ReadOnly = p.ReadOnly, Doc = p.Doc });
                classes.Add(new ClassDef { Name = c.Name, Summary = c.Summary, Props = props.ToArray() });
            }
            m.Classes = classes.ToArray();

            var apis = new List<ApiDef>();
            foreach (var a in r.AllApis)
            {
                var methods = new List<MethodDef>();
                foreach (var f in a.Methods.Values)
                    methods.Add(new MethodDef
                    {
                        Name = f.Name,
                        Summary = f.Summary,
                        Params = BuildParams(r, f.Params, f.ParamNames, f.ParamDocs),
                        Returns = TypeToString(r, f.Ret),
                    });
                apis.Add(new ApiDef { Name = a.Name, Summary = a.Summary, Methods = methods.ToArray() });
            }
            m.Apis = apis.ToArray();

            var events = new List<EventDef>();
            foreach (var ev in r.Events)
                events.Add(new EventDef
                {
                    Name = ev.Name,
                    Summary = ev.Summary,
                    Params = BuildParams(r, ev.Params, ev.ParamNames, ev.ParamDocs),
                });
            m.Events = events.ToArray();

            if (r.ArchetypeKindCount > 0)
            {
                var kinds = new List<ArchetypeKindDef>();
                for (int k = 0; k < r.ArchetypeKindCount; k++)
                {
                    var info = r.GetArchetypeKind(k);
                    var kevents = new List<EventDef>();
                    foreach (var ev in info.Events)
                        kevents.Add(new EventDef
                        {
                            Name = ev.Name,
                            Summary = ev.Summary,
                            Params = BuildParams(r, ev.Params, ev.ParamNames, ev.ParamDocs),
                        });
                    string[] known = null;
                    if (info.KnownIds != null && info.KnownIds.Count > 0)
                    {
                        known = new string[info.KnownIds.Count];
                        info.KnownIds.CopyTo(known);
                        Array.Sort(known, StringComparer.Ordinal); // стабильный порядок в json
                    }
                    kinds.Add(new ArchetypeKindDef
                    {
                        Name = info.Name,
                        Summary = info.Summary,
                        KnownIds = known,
                        Events = kevents.ToArray(),
                    });
                }
                m.Archetypes = kinds.ToArray();
            }

            return JsonConvert.SerializeObject(m, Formatting.Indented);
        }

        /// <summary>Собирает описания параметров; имена синтезируются (argN), если не заданы.</summary>
        private static ParamDef[] BuildParams(HostRegistry r, TypeRef[] types, string[] names, string[] docs)
        {
            if (types == null) return Array.Empty<ParamDef>();
            var result = new ParamDef[types.Length];
            for (int i = 0; i < types.Length; i++)
            {
                result[i] = new ParamDef
                {
                    Name = names != null && i < names.Length && !string.IsNullOrEmpty(names[i]) ? names[i] : "arg" + i,
                    Type = TypeToString(r, types[i]),
                    Doc = docs != null && i < docs.Length ? docs[i] : null,
                };
            }
            return result;
        }

        // ===================================================================
        // Импорт: json → HostRegistry с заглушками (метаданные сохраняются)
        // ===================================================================

        public static HostRegistry Import(string json, out int apiVersion)
        {
            var m = JsonConvert.DeserializeObject<ApiManifest>(json)
                    ?? throw new FormatException("salamander-api.json: пустой или некорректный документ.");
            apiVersion = m.ApiVersion;

            var r = new HostRegistry();

            // порядок важен: сперва имена типов (енумы/классы), потом сигнатуры
            foreach (var e in m.Enums ?? Array.Empty<EnumDef>())
                r.DefineEnum(e.Name, e.Summary, e.Members ?? Array.Empty<string>());

            foreach (var c in m.Classes ?? Array.Empty<ClassDef>())
                r.DefineClass(c.Name, c.Summary);

            foreach (var c in m.Classes ?? Array.Empty<ClassDef>())
            {
                foreach (var p in c.Props ?? Array.Empty<PropDef>())
                {
                    var t = ParseType(r, p.Type);
                    r.DefineProperty(c.Name, p.Name, t, p.ReadOnly,
                        getter: StubGetter,
                        setter: p.ReadOnly ? null : StubSetter,
                        doc: p.Doc);
                }
            }

            foreach (var a in m.Apis ?? Array.Empty<ApiDef>())
            {
                r.DescribeApi(a.Name, a.Summary);
                foreach (var f in a.Methods ?? Array.Empty<MethodDef>())
                {
                    SplitParams(r, f.Params, out var types, out var names, out var docs);
                    r.DefineMethod(a.Name, f.Name, types, ParseType(r, f.Returns), StubFunction, f.Summary, names, docs);
                }
            }

            foreach (var ev in m.Events ?? Array.Empty<EventDef>())
            {
                SplitParams(r, ev.Params, out var types, out var names, out var docs);
                r.DefineEvent(ev.Name, types, ev.Summary, names, docs);
            }

            foreach (var k in m.Archetypes ?? Array.Empty<ArchetypeKindDef>())
            {
                int kid = r.DefineArchetypeKind(k.Name, k.Summary);
                foreach (var ev in k.Events ?? Array.Empty<EventDef>())
                {
                    SplitParams(r, ev.Params, out var types, out var names, out var docs);
                    r.DefineArchetypeEvent(kid, ev.Name, types, ev.Summary, names, docs);
                }
                if (k.KnownIds != null && k.KnownIds.Length > 0)
                    r.SetArchetypeKnownIds(kid, k.KnownIds);
            }

            return r;
        }

        private static void SplitParams(HostRegistry r, ParamDef[] ps,
                                        out TypeRef[] types, out string[] names, out string[] docs)
        {
            ps ??= Array.Empty<ParamDef>();
            types = new TypeRef[ps.Length];
            names = new string[ps.Length];
            docs = new string[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                types[i] = ParseType(r, ps[i].Type);
                names[i] = ps[i].Name;
                docs[i] = ps[i].Doc;
            }
        }

        private static Variant StubGetter(IHostContext ctx, object o) => Variant.Nil;
        private static void StubSetter(IHostContext ctx, object o, Variant v) { }
        private static void StubFunction(ref CallContext ctx) { }

        // ===================================================================
        // TypeRef ⇄ строка ("float", "Unit", "List<int>", "Map<string, Unit>", "int[]")
        // ===================================================================

        public static string TypeToString(HostRegistry r, TypeRef t)
        {
            if (t == null) return "void";
            switch (t.Kind)
            {
                case TypeKind.Void: return "void";
                case TypeKind.Bool: return "bool";
                case TypeKind.Int: return "int";
                case TypeKind.Float: return "float";
                case TypeKind.Str: return "string";
                case TypeKind.Fiber: return "Fiber";
                case TypeKind.Sub: return "Subscription";
                case TypeKind.Entity:
                    return r.TryGetClassById(t.HostTypeId, out var cls) ? cls.Name : "<entity>";
                case TypeKind.Enum:
                    return r.TryGetEnumById(t.EnumId, out var en) ? en.Name : "<enum>";
                case TypeKind.Array: return TypeToString(r, t.Elem) + "[]";
                case TypeKind.List: return "List<" + TypeToString(r, t.Elem) + ">";
                case TypeKind.Map: return "Map<" + TypeToString(r, t.Key) + ", " + TypeToString(r, t.Val) + ">";
                default: return "void";
            }
        }

        public static TypeRef ParseType(HostRegistry r, string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return TypeRef.Void;
            s = s.Trim();

            if (s.EndsWith("[]", StringComparison.Ordinal))
                return TypeRef.ArrayOf(ParseType(r, s.Substring(0, s.Length - 2)));

            if (s.StartsWith("List<", StringComparison.Ordinal) && s.EndsWith(">", StringComparison.Ordinal))
                return TypeRef.ListOf(ParseType(r, s.Substring(5, s.Length - 6)));

            if (s.StartsWith("Map<", StringComparison.Ordinal) && s.EndsWith(">", StringComparison.Ordinal))
            {
                string inner = s.Substring(4, s.Length - 5);
                int comma = SplitTopLevelComma(inner);
                if (comma < 0)
                    throw new FormatException($"salamander-api.json: некорректный тип '{s}'.");
                return TypeRef.MapOf(
                    ParseType(r, inner.Substring(0, comma)),
                    ParseType(r, inner.Substring(comma + 1)));
            }

            switch (s)
            {
                case "void": return TypeRef.Void;
                case "bool": return TypeRef.Bool;
                case "int": return TypeRef.Int;
                case "float": return TypeRef.Float;
                case "string": return TypeRef.Str;
                case "Fiber": return TypeRef.Fiber;
                case "Subscription": return TypeRef.Subscription;
            }

            if (r.TryGetClass(s, out var cls)) return TypeRef.Entity(cls.Id);
            if (r.TryGetEnum(s, out var en)) return TypeRef.EnumOf(en.Id);

            throw new FormatException(
                $"salamander-api.json: неизвестный тип '{s}' — енум/класс должен быть объявлен в манифесте раньше использования.");
        }

        /// <summary>Индекс запятой верхнего уровня (вне вложенных &lt;&gt;).</summary>
        private static int SplitTopLevelComma(string s)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '<') depth++;
                else if (s[i] == '>') depth--;
                else if (s[i] == ',' && depth == 0) return i;
            }
            return -1;
        }
    }
}
