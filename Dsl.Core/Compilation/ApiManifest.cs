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

            /// <summary>
            /// Пояснения к элементам ПАРАЛЛЕЛЬНЫМ массивом (той же длины, null
            /// там, где пояснения нет). Параллельный массив, а не объекты
            /// {name, doc}: старые манифесты, где ключа нет вовсе, читаются
            /// как раньше, и `members` остаётся простым списком имён.
            /// </summary>
            [JsonProperty("memberDocs", NullValueHandling = NullValueHandling.Ignore)]
            public string[] MemberDocs;
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

            /// <summary>
            /// Методы самого объекта (basket.AddPerk("x")). Params — как их видит
            /// скрипт, без приёмника. Ключ пишется только когда методы есть:
            /// у классов-данных манифест выглядит как раньше.
            /// </summary>
            [JsonProperty("methods", NullValueHandling = NullValueHandling.Ignore)] public MethodDef[] Methods;
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
            [JsonProperty("consts", NullValueHandling = NullValueHandling.Ignore)] public ApiConstDef[] Consts;
        }

        /// <summary>
        /// Узел составного имени ("Api", "Api.PartsCatalog") — только описание.
        /// Сами узлы выводятся из имён API, поэтому в манифест попадают лишь
        /// описанные: перечислять остальные значило бы дублировать `apis`.
        /// </summary>
        public sealed class ApiNamespaceDef
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("summary")] public string Summary;
        }

        /// <summary>Именованное значение у API-класса: читается без скобок.</summary>
        public sealed class ApiConstDef
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("type")] public string Type;
            [JsonProperty("value")] public object Value;
            [JsonProperty("doc", NullValueHandling = NullValueHandling.Ignore)] public string Doc;
        }

        /// <summary>Поле структуры хоста.</summary>
        public sealed class StructFieldDef
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("type")] public string Type;
            [JsonProperty("default")] public object Default;
            [JsonProperty("doc", NullValueHandling = NullValueHandling.Ignore)] public string Doc;
        }

        public sealed class StructDef
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("summary", NullValueHandling = NullValueHandling.Ignore)] public string Summary;
            [JsonProperty("fields")] public StructFieldDef[] Fields = Array.Empty<StructFieldDef>();
        }

        /// <summary>Ожидаемая константа вида (поле блока-архетипа) — контракт контента.</summary>
        public sealed class ConstDef
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("type")] public string Type;
            [JsonProperty("required")] public bool Required;
            [JsonProperty("doc", NullValueHandling = NullValueHandling.Ignore)] public string Doc;

            /// <summary>
            /// Значение по умолчанию. Присутствие ключа = дефолт есть (в том числе
            /// "default": null — «по умолчанию строки нет»), поэтому Ignore здесь
            /// НЕ ставится, а факт наличия читается через hasDefault.
            /// Енум пишется ИМЕНЕМ элемента: перенумеровали енум — дефолт не поехал.
            /// </summary>
            [JsonProperty("default")] public object Default;
            [JsonProperty("hasDefault")] public bool HasDefault;
        }

        public sealed class ArchetypeKindDef
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("summary", NullValueHandling = NullValueHandling.Ignore)] public string Summary;
            [JsonProperty("knownIds", NullValueHandling = NullValueHandling.Ignore)] public string[] KnownIds;
            [JsonProperty("consts", NullValueHandling = NullValueHandling.Ignore)] public ConstDef[] Consts;
            [JsonProperty("events")] public EventDef[] Events = Array.Empty<EventDef>();

            /// <summary>
            /// Сущность вида вправе не реализовать ни одного события. Ключ
            /// пишется только когда true: у видов, где ничего не разрешали,
            /// манифест выглядит как раньше.
            /// </summary>
            [JsonProperty("eventsOptional", DefaultValueHandling = DefaultValueHandling.Ignore)]
            public bool EventsOptional;
        }

        public sealed class EventDef
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("summary", NullValueHandling = NullValueHandling.Ignore)] public string Summary;
            [JsonProperty("params")] public ParamDef[] Params = Array.Empty<ParamDef>();
        }

        [JsonProperty("apiVersion")] public int ApiVersion;
        // порядок объявления = порядок зависимостей: енумы ← структуры ← классы
        [JsonProperty("enums")] public EnumDef[] Enums = Array.Empty<EnumDef>();
        [JsonProperty("structs", NullValueHandling = NullValueHandling.Ignore)] public StructDef[] Structs;
        [JsonProperty("classes")] public ClassDef[] Classes = Array.Empty<ClassDef>();
        [JsonProperty("apiNamespaces", NullValueHandling = NullValueHandling.Ignore)] public ApiNamespaceDef[] ApiNamespaces;
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
                enums.Add(new EnumDef
                {
                    Name = e.Name, Summary = e.Summary, Members = e.Names,
                    // массива нет вовсе, если не описан ни один элемент —
                    // манифесты без пояснений выглядят как раньше
                    MemberDocs = HasAnyDoc(e.Docs) ? e.Docs : null,
                });
            m.Enums = enums.ToArray();

            // структуры идут перед классами: свойство класса может быть структурой,
            // а поле структуры классом быть не может — зависимость односторонняя
            var structs = new List<StructDef>();
            foreach (var st in r.AllStructs)
            {
                var fields = new List<StructFieldDef>();
                foreach (var f in st.Fields)
                    fields.Add(new StructFieldDef
                    {
                        Name = f.Name,
                        Type = TypeToString(r, f.Type),
                        Default = EncodeStructDefault(r, f),
                        Doc = f.Doc,
                    });
                structs.Add(new StructDef { Name = st.Name, Summary = st.Summary, Fields = fields.ToArray() });
            }
            if (structs.Count > 0) m.Structs = structs.ToArray();

            var classes = new List<ClassDef>();
            foreach (var c in r.AllClasses)
            {
                var props = new List<PropDef>();
                foreach (var p in c.Props.Values)
                    props.Add(new PropDef { Name = p.Name, Type = TypeToString(r, p.Type), ReadOnly = p.ReadOnly, Doc = p.Doc });

                MethodDef[] cmethods = null;
                if (c.Methods.Count > 0)
                {
                    var list = new List<MethodDef>();
                    foreach (var f in c.Methods.Values)
                        list.Add(new MethodDef
                        {
                            Name = f.Name,
                            Summary = f.Summary,
                            Params = BuildParams(r, f.Params, f.ParamNames, f.ParamDocs),
                            Returns = TypeToString(r, f.Ret),
                        });
                    cmethods = list.ToArray();
                }
                classes.Add(new ClassDef
                {
                    Name = c.Name, Summary = c.Summary,
                    Props = props.ToArray(), Methods = cmethods,
                });
            }
            m.Classes = classes.ToArray();

            // описанные узлы составных имён — перед apis, как и читаются
            var namespaces = new List<ApiNamespaceDef>();
            foreach (var ns in r.ApiNamespaces)
                if (!string.IsNullOrEmpty(ns.Value))
                    namespaces.Add(new ApiNamespaceDef { Name = ns.Key, Summary = ns.Value });
            if (namespaces.Count > 0)
            {
                namespaces.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name)); // словарь неупорядочен
                m.ApiNamespaces = namespaces.ToArray();
            }

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
                ApiConstDef[] consts = null;
                if (a.Consts.Count > 0)
                {
                    consts = new ApiConstDef[a.Consts.Count];
                    for (int i = 0; i < consts.Length; i++)
                    {
                        var ci = a.Consts[i];
                        consts[i] = new ApiConstDef
                        {
                            Name = ci.Name,
                            Type = TypeToString(r, ci.Type),
                            Value = EncodeLiteral(r, ci.Type, ci.Value, ci.ValueStr),
                            Doc = ci.Doc,
                        };
                    }
                }
                apis.Add(new ApiDef
                {
                    Name = a.Name, Summary = a.Summary,
                    Methods = methods.ToArray(), Consts = consts,
                });
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
                    ConstDef[] consts = null;
                    if (info.Consts.Count > 0)
                    {
                        // порядок объявления хостом — он же порядок в json
                        consts = new ConstDef[info.Consts.Count];
                        for (int c = 0; c < consts.Length; c++)
                        {
                            var ci = info.Consts[c];
                            consts[c] = new ConstDef
                            {
                                Name = ci.Name,
                                Type = TypeToString(r, ci.Type),
                                Required = ci.Required,
                                Doc = ci.Doc,
                                HasDefault = ci.HasDefault,
                                Default = ci.HasDefault ? EncodeDefault(r, ci) : null,
                            };
                        }
                    }

                    kinds.Add(new ArchetypeKindDef
                    {
                        Name = info.Name,
                        Summary = info.Summary,
                        KnownIds = known,
                        Consts = consts,
                        Events = kevents.ToArray(),
                        EventsOptional = info.EventsOptional,
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
            {
                var members = e.Members ?? Array.Empty<string>();
                var docs = e.MemberDocs;
                if (docs != null && docs.Length != members.Length)
                    throw new FormatException(
                        $"salamander-api.json: у енума '{e.Name}' {docs.Length} пояснений " +
                        $"на {members.Length} элементов — массивы идут параллельно.");
                r.DefineEnum(e.Name, e.Summary, members, docs);
            }

            // структуры — сразу после енумов: поле структуры бывает только
            // литеральным или элементом енума, а вот свойство класса и параметр
            // метода уже могут быть структурой
            foreach (var st in m.Structs ?? Array.Empty<StructDef>())
            {
                int sid = r.DefineStruct(st.Name, st.Summary);
                foreach (var f in st.Fields ?? Array.Empty<StructFieldDef>())
                {
                    var ft = ParseType(r, f.Type);
                    DecodeStructDefault(r, f, ft, out var dv, out var ds);
                    r.DefineStructField(sid, f.Name, ft, dv, ds, f.Doc);
                }
            }

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
                foreach (var f in c.Methods ?? Array.Empty<MethodDef>())
                {
                    SplitParams(r, f.Params, out var types, out var names, out var docs);
                    r.DefineClassMethod(c.Name, f.Name, types, ParseType(r, f.Returns),
                                        StubFunction, f.Summary, names, docs);
                }
            }

            // узлы описываем ДО API: DefineApiClass создаёт недостающие узлы,
            // и текст, уже лежащий на узле, он не трогает
            foreach (var ns in m.ApiNamespaces ?? Array.Empty<ApiNamespaceDef>())
                r.DescribeApiNamespace(ns.Name, ns.Summary);

            foreach (var a in m.Apis ?? Array.Empty<ApiDef>())
            {
                r.DescribeApi(a.Name, a.Summary);
                foreach (var f in a.Methods ?? Array.Empty<MethodDef>())
                {
                    SplitParams(r, f.Params, out var types, out var names, out var docs);
                    r.DefineMethod(a.Name, f.Name, types, ParseType(r, f.Returns), StubFunction, f.Summary, names, docs);
                }
                foreach (var c in a.Consts ?? Array.Empty<ApiConstDef>())
                {
                    var ct = ParseType(r, c.Type);
                    DecodeLiteral(r, c.Value, ct, $"константа '{a.Name}.{c.Name}'", out var cv, out var cs);
                    r.DefineApiConst(a.Name, c.Name, ct, cv, cs, c.Doc);
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
                if (k.EventsOptional) r.SetArchetypeEventsOptional(kid);
                foreach (var ev in k.Events ?? Array.Empty<EventDef>())
                {
                    SplitParams(r, ev.Params, out var types, out var names, out var docs);
                    r.DefineArchetypeEvent(kid, ev.Name, types, ev.Summary, names, docs);
                }
                foreach (var c in k.Consts ?? Array.Empty<ConstDef>())
                {
                    var ct = ParseType(r, c.Type);
                    DecodeDefault(r, c, ct, out var dv, out var ds);
                    r.DefineArchetypeConst(kid, c.Name, ct, c.Required, c.Doc, c.HasDefault, dv, ds);
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

        // ===================================================================
        // Дефолты констант ⇄ json
        // ===================================================================

        // Литерал в манифесте — один и тот же для дефолта константы вида, дефолта
        // поля структуры и значения константы API. Кодировщик поэтому тоже один:
        // три копии этой лестницы уже начинали расходиться.

        private static bool HasAnyDoc(string[] docs)
        {
            if (docs == null) return false;
            foreach (var d in docs) if (!string.IsNullOrEmpty(d)) return true;
            return false;
        }

        private static object EncodeLiteral(HostRegistry r, TypeRef type, Variant value, string str)
        {
            switch (type.Kind)
            {
                case TypeKind.Bool: return value.AsBool;
                case TypeKind.Int: return value.AsInt;
                case TypeKind.Float: return value.ToF();
                case TypeKind.Double: return value.ToD();
                case TypeKind.Str: return str;
                case TypeKind.Enum:
                    // именем, а не индексом: перенумеровали енум — значение не съехало
                    if (r.TryGetEnumById(type.EnumId, out var en)
                        && (uint)value.EnumValue < (uint)en.Names.Length)
                        return en.Names[value.EnumValue];
                    return value.EnumValue;
                default: return null;
            }
        }

        /// <summary>
        /// Обратно. <paramref name="what"/> попадает в текст ошибки — «дефолт
        /// константы 'windup'», «поле структуры 'slash'»: без этого сообщение о
        /// битом манифесте не говорит, ГДЕ именно битое.
        /// </summary>
        private static void DecodeLiteral(HostRegistry r, object raw, TypeRef type, string what,
                                          out Variant value, out string str)
        {
            value = Variant.Nil;
            str = null;
            // Newtonsoft отдаёт числа как long/double — приводим по ОБЪЯВЛЕННОМУ
            // типу, а не по тому, что угадал json (3 и 3.0 неразличимы в тексте)
            switch (type.Kind)
            {
                case TypeKind.Bool: value = Variant.Bool(Convert.ToBoolean(raw ?? false)); return;
                case TypeKind.Int: value = Variant.Int(Convert.ToInt32(raw ?? 0)); return;
                case TypeKind.Float: value = Variant.Float(Convert.ToSingle(raw ?? 0f)); return;
                case TypeKind.Double: value = Variant.Double(Convert.ToDouble(raw ?? 0d)); return;
                case TypeKind.Str: str = raw as string; return;
                case TypeKind.Enum:
                {
                    if (!r.TryGetEnumById(type.EnumId, out var en))
                        throw new FormatException(
                            $"salamander-api.json: {what} ссылается на неизвестный енум.");
                    string member = raw as string;
                    if (member == null) { value = Variant.Enum(en.Id, 0); return; }
                    if (!en.Members.TryGetValue(member, out int v))
                        throw new FormatException(
                            $"salamander-api.json: '{member}' не является элементом енума '{en.Name}' ({what}).");
                    value = Variant.Enum(en.Id, v);
                    return;
                }
                default:
                    throw new FormatException(
                        $"salamander-api.json: {what} не может быть типа '{type}'.");
            }
        }

        private static object EncodeDefault(HostRegistry r, ArchetypeConstInfo c)
            => EncodeLiteral(r, c.Type, c.DefaultValue, c.DefaultStr);

        private static void DecodeDefault(HostRegistry r, ConstDef c, TypeRef type,
                                          out Variant value, out string str)
        {
            value = Variant.Nil;
            str = null;
            if (!c.HasDefault) return;
            DecodeLiteral(r, c.Default, type, $"дефолт константы '{c.Name}'", out value, out str);
        }

        private static object EncodeStructDefault(HostRegistry r, HostStructFieldInfo f)
            => EncodeLiteral(r, f.Type, f.Default, f.DefaultStr);

        private static void DecodeStructDefault(HostRegistry r, StructFieldDef f, TypeRef type,
                                                out Variant value, out string str)
            => DecodeLiteral(r, f.Default, type, $"поле структуры '{f.Name}'", out value, out str);

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
                // double — полноценный тип языка; без этой ветки он уезжал в
                // default и печатался как "void": инструменты видели в манифесте
                // не тот тип, чем ломали проверку у себя
                case TypeKind.Double: return "double";
                case TypeKind.Str: return "string";
                case TypeKind.Fiber: return "Fiber";
                case TypeKind.Sub: return "Subscription";
                case TypeKind.Entity:
                    return r.TryGetClassById(t.HostTypeId, out var cls) ? cls.Name : "<entity>";
                case TypeKind.Enum:
                    return r.TryGetEnumById(t.EnumId, out var en) ? en.Name : "<enum>";
                case TypeKind.Struct:
                    return r.TryGetStructById(t.StructId, out var st) ? st.Name : "<struct>";
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
                case "double": return TypeRef.Double;
                case "string": return TypeRef.Str;
                case "Fiber": return TypeRef.Fiber;
                case "Subscription": return TypeRef.Subscription;
            }

            if (r.TryGetClass(s, out var cls)) return TypeRef.Entity(cls.Id);
            if (r.TryGetStruct(s, out var st)) return TypeRef.StructOf(st.Id);
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
