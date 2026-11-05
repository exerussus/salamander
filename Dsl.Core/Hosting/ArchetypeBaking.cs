using System;
using Dsl.Text;
using System.Collections.Generic;
using System.Reflection;
using Dsl.Runtime;

namespace Dsl.Hosting
{
    // =======================================================================
    // Вид архетипа из C#-типа и запекание его данных в плотную таблицу.
    //
    //   [SalamanderKind("spell")]
    //   public struct SpellDef
    //   {
    //       [KindField(doc: "Стоимость в ОД", required: true)] public int Cost;
    //       [KindField(defaultValue: 1)]                        public int Range;
    //       [KindField(side: Side.Server)]                      public int LootChance;
    //       [KindField(refKind: "spell")]                       public int Upgrade;   // id → индекс
    //   }
    //
    //   host.Kind<SpellDef>();                          // поля вида — из типа, один раз
    //   ...
    //   var table = engine.Bake<SpellDef>();            // после LoadProgram / TryHotSwap
    //   var client = engine.Bake<SpellDef>(BakeSide.SharedOnly);
    //
    // Запекаются только поля-контракты вида, а они всегда readonly: это рецепт,
    // а не состояние, поэтому снимок не расходится с тем, что видят скрипты.
    // Индекс в таблице = хэндл ResolveArchetype(kind, id) = порядок объявления
    // блоков (модули по зависимостям, независимые — по имени).
    // =======================================================================

    /// <summary>Чья часть данных: уходит клиенту или остаётся на сервере.</summary>
    public enum Side : byte
    {
        /// <summary>Общая часть: попадает и в серверную, и в клиентскую таблицу.</summary>
        Shared = 0,
        /// <summary>Только сервер: в BakeSide.SharedOnly поле остаётся default.</summary>
        Server = 1,
    }

    /// <summary>Какие поля запекать.</summary>
    public enum BakeSide : byte
    {
        /// <summary>Все поля (сервер).</summary>
        All = 0,
        /// <summary>Только Side.Shared — для экспорта клиенту: серверные значения даже не читаются.</summary>
        SharedOnly = 1,
    }

    /// <summary>Тип — описание вида архетипа. Имя вида — аргумент; без него — имя типа в нижнем регистре.</summary>
    [AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
    public sealed class SalamanderKindAttribute : Attribute
    {
        public string Kind { get; }
        public string Summary { get; }

        /// <summary>Английское описание вида (необязательно).</summary>
        public string SummaryEn { get; set; }

        /// <summary>Описание на двух языках (null — нет).</summary>
        public DocText Text => DocText.Maybe(Summary, SummaryEn);

        public SalamanderKindAttribute(string kind = null, string summary = null) { Kind = kind; Summary = summary; }
    }

    /// <summary>
    /// Поле C#-типа — константа вида. Поля без атрибута в вид не попадают.
    /// Имя в скрипте по умолчанию — имя поля с маленькой буквы (Cost → cost);
    /// для snake_case задайте name.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = false)]
    public sealed class KindFieldAttribute : Attribute
    {
        public string Doc { get; }

        /// <summary>Английское описание поля (необязательно): <c>[KindField(doc: "…", DocEn = "…")]</c>.</summary>
        public string DocEn { get; set; }

        /// <summary>Описание на двух языках (null — нет).</summary>
        public DocText Text => DocText.Maybe(Doc, DocEn);

        public object DefaultValue { get; }
        public bool Required { get; }
        public Side Side { get; }
        public string Name { get; }
        public string RefKind { get; }

        /// <param name="doc">Описание для манифеста и подсказок редактора.</param>
        /// <param name="defaultValue">Значение, если блок поле не объявил (ConstOr). Несовместимо с required.</param>
        /// <param name="required">Блок без этого поля не компилируется (E0223).</param>
        /// <param name="side">Shared — уходит клиенту, Server — только сервер.</param>
        /// <param name="name">Имя поля в скрипте.</param>
        /// <param name="refKind">
        /// Ссылка на сущность другого (или того же) вида: в скрипте это строковый id,
        /// в таблице — его индекс (int), -1 — пусто. Неизвестный id — ошибка запекания.
        /// </param>
        public KindFieldAttribute(string doc = null, object defaultValue = null, bool required = false,
                                  Side side = Side.Shared, string name = null, string refKind = null)
        {
            Doc = doc; DefaultValue = defaultValue; Required = required;
            Side = side; Name = name; RefKind = refKind;
        }
    }

    /// <summary>Описание одного запекаемого поля (для экспортёров и диагностики).</summary>
    public sealed class KindFieldInfo
    {
        /// <summary>Имя в скрипте.</summary>
        public string Name { get; internal set; }
        /// <summary>Поле C#-типа.</summary>
        public FieldInfo Field { get; internal set; }
        public Side Side { get; internal set; }
        public DocText Doc { get; internal set; }
        public bool Required { get; internal set; }
        public bool HasDefault { get; internal set; }
        /// <summary>Вид, на который ссылается поле (null — не ссылка).</summary>
        public string RefKind { get; internal set; }

        internal Func<IHostContext, Variant, object> Read; // не для ссылок
    }

    /// <summary>Запекание не удалось: все найденные проблемы разом.</summary>
    public sealed class BakeException : Exception
    {
        public IReadOnlyList<string> Problems { get; }

        public BakeException(string kind, List<string> problems, Language language = Language.Ru)
            : base(Loc.T(language, $"Запекание вида '{kind}' не удалось ({problems.Count}):\n  " + string.Join("\n  ", problems),
                $"Baking of kind '{kind}' failed ({problems.Count}):\n  " + string.Join("\n  ", problems)))
        {
            Problems = problems;
        }
    }

    /// <summary>Схема вида: строится один раз в host.Kind&lt;T&gt;().</summary>
    internal sealed class KindSchema
    {
        public string Kind;
        public Type Type;
        public KindFieldInfo[] Fields;
    }

    /// <summary>
    /// Плотная таблица вида: Count элементов, индекс — тот же int-хэндл, что
    /// у ResolveArchetype и адресного Raise. Иммутабельна.
    /// </summary>
    public sealed class ArchetypeTable<T>
    {
        /// <summary>Язык сообщений (берётся у движка при запекании).</summary>
        public Language Language { get; internal set; }

        private readonly T[] _items;
        private readonly string[] _ids;
        private readonly Dictionary<string, int> _index;
        // хэш значения каждого поля: [i * FieldCount + f]; 0 — поле не запекалось (Server в SharedOnly)
        internal readonly ulong[] FieldHashes;

        internal ArchetypeTable(string kind, BakeSide side, IReadOnlyList<KindFieldInfo> fields,
                                T[] items, string[] ids, ulong[] fieldHashes, ulong layout, ulong content)
        {
            Kind = kind; Side = side; Fields = fields;
            _items = items; _ids = ids; FieldHashes = fieldHashes;
            LayoutHash = layout; ContentHash = content;
            _index = new Dictionary<string, int>(ids.Length, StringComparer.Ordinal);
            for (int i = 0; i < ids.Length; i++) _index[ids[i]] = i;
        }

        public string Kind { get; }
        public BakeSide Side { get; }
        public IReadOnlyList<KindFieldInfo> Fields { get; }
        public int Count => _items.Length;

        public ref readonly T this[int index] => ref _items[index];

        /// <summary>Строковый id по индексу.</summary>
        public string IdOf(int index) => _ids[index];

        /// <summary>Индекс по id; -1 — нет такой сущности.</summary>
        public int IndexOf(string id) => id != null && _index.TryGetValue(id, out int i) ? i : -1;

        /// <summary>
        /// Отпечаток раскладки: вид, набор и порядок id, имена/типы/стороны полей.
        /// Совпал — индексы на клиенте и сервере значат одно и то же. Разные
        /// наборы модулей (в т.ч. карантин на одной из машин) дают разный отпечаток.
        /// </summary>
        public ulong LayoutHash { get; }

        /// <summary>Отпечаток раскладки и всех запечённых значений — версия блоба.</summary>
        public ulong ContentHash { get; }

        /// <summary>
        /// Что изменилось от <paramref name="old"/> к <paramref name="current"/>: по id,
        /// поле за полем. Таблицы должны быть одного вида и одной стороны.
        /// Индексы новой таблицы могут сдвинуться (добавили сущность в середину) —
        /// это видно по <see cref="ArchetypeDiff.IndicesShifted"/>.
        /// </summary>
        public static ArchetypeDiff Diff(ArchetypeTable<T> old, ArchetypeTable<T> current)
        {
            if (old == null) throw new ArgumentNullException(nameof(old));
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (old.Side != current.Side)
                throw new ArgumentException(Loc.T(current.Language, $"Сравнивать можно таблицы одной стороны: {old.Side} и {current.Side}.",
                    $"Only tables of the same side can be compared: {old.Side} and {current.Side}."));

            var diff = new ArchetypeDiff(old.Kind) { Language = current.Language };
            int fc = old.Fields.Count;
            bool shifted = old.Count != current.Count;

            for (int i = 0; i < current.Count; i++)
            {
                string id = current._ids[i];
                int oi = old.IndexOf(id);
                if (oi != i) shifted = true;
                if (oi < 0)
                {
                    diff.Add(new ArchetypeChange(id, ArchetypeChangeKind.Added, -1, i, Array.Empty<string>()));
                    continue;
                }
                List<string> changed = null;
                for (int f = 0; f < fc; f++)
                    if (old.FieldHashes[oi * fc + f] != current.FieldHashes[i * fc + f])
                        (changed ??= new List<string>()).Add(old.Fields[f].Name);
                if (changed != null)
                    diff.Add(new ArchetypeChange(id, ArchetypeChangeKind.Changed, oi, i, changed.ToArray()));
            }
            for (int i = 0; i < old.Count; i++)
                if (current.IndexOf(old._ids[i]) < 0)
                    diff.Add(new ArchetypeChange(old._ids[i], ArchetypeChangeKind.Removed, i, -1, Array.Empty<string>()));

            diff.IndicesShifted = shifted;
            diff.LayoutChanged = old.LayoutHash != current.LayoutHash;
            return diff;
        }
    }

    public enum ArchetypeChangeKind : byte { Added, Removed, Changed }

    /// <summary>Одна изменённая сущность.</summary>
    public readonly struct ArchetypeChange
    {
        public readonly string Id;
        public readonly ArchetypeChangeKind Change;
        /// <summary>Индекс в старой таблице (-1 для Added).</summary>
        public readonly int OldIndex;
        /// <summary>Индекс в новой таблице (-1 для Removed).</summary>
        public readonly int NewIndex;
        /// <summary>Изменённые поля (только для Changed), имена как в скрипте.</summary>
        public readonly string[] Fields;

        public ArchetypeChange(string id, ArchetypeChangeKind change, int oldIndex, int newIndex, string[] fields)
        {
            Id = id; Change = change; OldIndex = oldIndex; NewIndex = newIndex; Fields = fields;
        }

        public override string ToString() =>
            Change == ArchetypeChangeKind.Changed ? $"{Id}: Changed [{string.Join(", ", Fields)}]" : $"{Id}: {Change}";
    }

    /// <summary>Итог ArchetypeTable.Diff.</summary>
    public sealed class ArchetypeDiff
    {
        private readonly List<ArchetypeChange> _changes = new List<ArchetypeChange>();
        internal ArchetypeDiff(string kind) { Kind = kind; }

        /// <summary>Язык <see cref="ToString"/>.</summary>
        public Language Language { get; internal set; }
        internal void Add(ArchetypeChange c) => _changes.Add(c);

        public string Kind { get; }
        public IReadOnlyList<ArchetypeChange> Changes => _changes;
        public bool IsEmpty => _changes.Count == 0;
        /// <summary>Хотя бы у одной сущности поменялся индекс — клиенту нужна вся таблица, а не дельта по индексам.</summary>
        public bool IndicesShifted { get; internal set; }
        /// <summary>Поменялся отпечаток раскладки (набор/порядок id).</summary>
        public bool LayoutChanged { get; internal set; }

        public override string ToString() =>
            Loc.T(Language, IsEmpty ? $"{Kind}: без изменений" : $"{Kind}: " + string.Join("; ", _changes),
                IsEmpty ? $"{Kind}: no changes" : $"{Kind}: " + string.Join("; ", _changes));
    }

    /// <summary>engine.Bake&lt;T&gt;() — запекание по схеме из host.Kind&lt;T&gt;().</summary>
    public static class ArchetypeBaking
    {
        /// <summary>
        /// Запечь все сущности вида в таблицу. Читает живые статики — итог мержа
        /// модулей и последней горячей замены. Аллокации: только результат и
        /// упаковка значений при записи полей (рефлексия) — зовите на загрузке
        /// контента, а не в игровом цикле.
        /// </summary>
        /// <exception cref="BakeException">ссылка на несуществующий id, поле не readonly и т.п.</exception>
        public static ArchetypeTable<T> Bake<T>(this ScriptEngine engine, BakeSide side = BakeSide.All)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));
            if (!engine.HasProgram) throw new InvalidOperationException(Loc.T(engine.Language, "Bake: программа не загружена (LoadProgram).", "Bake: no program is loaded (LoadProgram)."));
            if (!engine.Host.KindSchemas.TryGetValue(typeof(T), out var raw))
                throw new InvalidOperationException(
                    Loc.T(engine.Language, $"Bake<{typeof(T).Name}>: тип не объявлен как вид — вызовите host.Kind<{typeof(T).Name}>() при регистрации API.",
                        $"Bake<{typeof(T).Name}>: the type is not declared as a kind — call host.Kind<{typeof(T).Name}>() when registering the API."));
            var schema = (KindSchema)raw;
            var fields = schema.Fields;
            int fc = fields.Length;

            var problems = new List<string>();
            int k = engine.ArchKindIndex(schema.Kind);
            string[] ids = k < 0 ? Array.Empty<string>() : (string[])engine.ArchIds(k).Clone();
            var items = new T[ids.Length];
            var hashes = new ulong[ids.Length * fc];

            ulong layout = Fnv.Start;
            layout = Fnv.Str(layout, schema.Kind);
            layout = Fnv.Int(layout, (int)side);
            foreach (var f in fields)
            {
                layout = Fnv.Str(layout, f.Name);
                layout = Fnv.Str(layout, f.Field.FieldType.FullName);
                layout = Fnv.Int(layout, (int)f.Side);
                layout = Fnv.Str(layout, f.RefKind ?? "");
            }
            layout = Fnv.Int(layout, ids.Length);
            foreach (var id in ids) layout = Fnv.Str(layout, id);
            ulong content = layout;

            bool isValueType = typeof(T).IsValueType;
            for (int a = 0; a < ids.Length; a++)
            {
                object boxed = isValueType ? (object)default(T) : Activator.CreateInstance(typeof(T), true);
                for (int fi = 0; fi < fc; fi++)
                {
                    var f = fields[fi];
                    if (side == BakeSide.SharedOnly && f.Side == Side.Server) continue; // даже не читаем

                    bool present = engine.TryReadArchField(k, a, f.Name, out var v, out bool readOnly);
                    if (present && !readOnly)
                    {
                        problems.Add(Loc.T(engine.Language, $"{ids[a]}.{f.Name}: поле изменяемое — запекаются только readonly (рецепт).",
                            $"{ids[a]}.{f.Name}: the field is mutable — only readonly fields (the recipe) are baked."));
                        continue;
                    }

                    ulong h = Fnv.Int(Fnv.Start, fi + 1);
                    h = HashVariant(engine, h, v);
                    hashes[a * fc + fi] = h;
                    content = Fnv.Long(content, (long)h);

                    if (v.IsNil)
                    {
                        // поле не задано и дефолта нет: default(TField); ссылка — -1,
                        // а не 0 (0 — это первая сущность вида)
                        if (f.RefKind != null) f.Field.SetValue(boxed, -1);
                        continue;
                    }

                    object value;
                    if (f.RefKind != null)
                    {
                        string target = v.Type == VariantType.Str ? engine.ResolveString(v) : null;
                        if (v.Type != VariantType.Str) { problems.Add(Loc.T(engine.Language, $"{ids[a]}.{f.Name}: ожидался строковый id.", $"{ids[a]}.{f.Name}: expected a string id.")); continue; }
                        int idx = string.IsNullOrEmpty(target) ? -1 : engine.ResolveArchetype(f.RefKind, target);
                        if (idx < 0 && !string.IsNullOrEmpty(target))
                        {
                            problems.Add(Loc.T(engine.Language, $"{ids[a]}.{f.Name}: '{target}' — нет такой сущности вида '{f.RefKind}'.",
                                $"{ids[a]}.{f.Name}: '{target}' — no such entity of kind '{f.RefKind}'."));
                            continue;
                        }
                        value = idx;
                    }
                    else
                    {
                        try { value = f.Read(engine, v); }
                        catch (Exception ex)
                        {
                            problems.Add(Loc.T(engine.Language, $"{ids[a]}.{f.Name}: не читается как {f.Field.FieldType.Name} ({ex.Message}).",
                                $"{ids[a]}.{f.Name}: cannot be read as {f.Field.FieldType.Name} ({ex.Message})."));
                            continue;
                        }
                    }
                    f.Field.SetValue(boxed, value);
                }
                items[a] = (T)boxed;
            }

            if (problems.Count > 0) throw new BakeException(schema.Kind, problems, engine.Language);
            return new ArchetypeTable<T>(schema.Kind, side, fields, items, ids, hashes, layout, content) { Language = engine.Language };
        }

        // хэш значения по Variant — не зависит от C#-типа и машины: строки по
        // содержимому, float/double по битам, структуры по полям рекурсивно
        private static ulong HashVariant(ScriptEngine e, ulong h, Variant v)
        {
            h = Fnv.Int(h, (int)v.Type);
            switch (v.Type)
            {
                case VariantType.Nil: return h;
                case VariantType.Bool: return Fnv.Int(h, v.AsBool ? 1 : 0);
                case VariantType.Int: return Fnv.Int(h, v.AsInt);
                case VariantType.Float: return Fnv.Int(h, BitConverter.SingleToInt32Bits(v.AsFloat));
                case VariantType.Double: return Fnv.Long(h, BitConverter.DoubleToInt64Bits(v.AsDouble));
                case VariantType.Str: return Fnv.Str(h, e.ResolveString(v) ?? "");
                case VariantType.Enum: return Fnv.Int(h, v.EnumValue);
                default:
                {
                    string sname = e.GetStructTypeName(v);
                    if (sname == null) return Fnv.Int(h, v.CollId); // константой быть не должно; хоть как-то
                    h = Fnv.Str(h, sname);
                    var names = new List<string>();
                    e.GetStructFields(v, names);
                    foreach (var n in names)
                    {
                        h = Fnv.Str(h, n);
                        e.TryGetStructField(v, n, out var fv);
                        h = HashVariant(e, h, fv);
                    }
                    return h;
                }
            }
        }

        // ----- регистрация: host.Kind<T>() ---------------------------------

        internal static ArchetypeBuilder RegisterKind<T>(HostBuilder host)
        {
            var type = typeof(T);
            var kindAttr = type.GetCustomAttribute<SalamanderKindAttribute>();
            string kind = kindAttr?.Kind ?? type.Name.ToLowerInvariant();
            if (host.Registry.KindSchemas.ContainsKey(type))
                throw new InvalidOperationException(host.T($"host.Kind<{type.Name}>() уже вызывался.", $"host.Kind<{type.Name}>() has already been called."));

            var builder = host.Archetype(kind, kindAttr?.Text);
            var list = new List<KindFieldInfo>();
            var names = new HashSet<string>(StringComparer.Ordinal);

            // MetadataToken — порядок объявления в исходнике (GetFields порядок не гарантирует)
            var csFields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Array.Sort(csFields, (x, y) => x.MetadataToken.CompareTo(y.MetadataToken));

            foreach (var fi in csFields)
            {
                var a = fi.GetCustomAttribute<KindFieldAttribute>();
                if (a == null) continue;
                string name = a.Name ?? (char.ToLowerInvariant(fi.Name[0]) + fi.Name.Substring(1));
                string where = $"{type.Name}.{fi.Name}";
                if (!names.Add(name)) throw new ArgumentException(host.T($"{where}: имя поля '{name}' в виде '{kind}' повторяется.",
                    $"{where}: the field name '{name}' is repeated in kind '{kind}'."));
                if (fi.IsInitOnly) throw new ArgumentException(host.T($"{where}: поле readonly в C# — Bake не сможет его записать.",
                    $"{where}: the field is readonly in C# — Bake cannot write it."));
                if (a.Required && a.DefaultValue != null)
                    throw new ArgumentException(host.T($"{where}: required и defaultValue одновременно — выберите одно.",
                        $"{where}: required and defaultValue at the same time — choose one."));

                var info = new KindFieldInfo
                {
                    Name = name, Field = fi, Side = a.Side, Doc = a.Text,
                    Required = a.Required, HasDefault = a.DefaultValue != null, RefKind = a.RefKind,
                };

                Type scriptType = fi.FieldType;
                object def = a.DefaultValue;
                if (a.RefKind != null)
                {
                    if (fi.FieldType != typeof(int))
                        throw new ArgumentException(host.T($"{where}: поле-ссылка (refKind) должно быть int — в нём лежит индекс.",
                            $"{where}: a reference field (refKind) must be int — it holds an index."));
                    scriptType = typeof(string);
                    if (def != null && !(def is string))
                        throw new ArgumentException(host.T($"{where}: дефолт ссылки — строковый id.",
                            $"{where}: the default of a reference is a string id."));
                }
                else
                {
                    if (def != null) def = ConvertDefault(host, def, scriptType, where);
                    info.Read = MakeReader(host, scriptType, where);
                }

                var doc = a.Text;
                if (a.RefKind != null)
                    doc = DocText.Append(doc, $"(id сущности вида {a.RefKind})", $"(id of an entity of kind {a.RefKind})");
                try
                {
                    if (def != null)
                        typeof(ArchetypeBuilder).GetMethod(nameof(ArchetypeBuilder.ConstOr))
                            .MakeGenericMethod(scriptType)
                            .Invoke(builder, new[] { name, def, doc });
                    else
                        typeof(ArchetypeBuilder).GetMethod(nameof(ArchetypeBuilder.Const))
                            .MakeGenericMethod(scriptType)
                            .Invoke(builder, new object[] { name, a.Required, doc });
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    throw new ArgumentException($"{where}: {tie.InnerException.Message}", tie.InnerException);
                }
                list.Add(info);
            }

            if (list.Count == 0)
                throw new ArgumentException(host.T($"{type.Name}: нет ни одного поля с [KindField].", $"{type.Name}: there is no field with [KindField]."));

            host.Registry.KindSchemas[type] = new KindSchema { Kind = kind, Type = type, Fields = list.ToArray() };
            return builder;
        }

        private static object ConvertDefault(HostBuilder host, object def, Type t, string where)
        {
            try
            {
                if (t.IsEnum) return def.GetType() == t ? def : Enum.ToObject(t, def);
                if (def.GetType() == t) return def;
                return Convert.ChangeType(def, t, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                throw new ArgumentException(host.T($"{where}: дефолт {def} ({def.GetType().Name}) не приводится к {t.Name}: {ex.Message}",
                    $"{where}: the default {def} ({def.GetType().Name}) cannot be converted to {t.Name}: {ex.Message}"));
            }
        }

        private static Func<IHostContext, Variant, object> MakeReader(HostBuilder host, Type t, string where)
        {
            try
            {
                return (Func<IHostContext, Variant, object>)typeof(ArchetypeBaking)
                    .GetMethod(nameof(Box), BindingFlags.NonPublic | BindingFlags.Static)
                    .MakeGenericMethod(t)
                    .Invoke(null, new object[] { host });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw new ArgumentException(host.T($"{where}: тип {t.Name} не поддержан ({tie.InnerException.Message}). " +
                                            "Енумы и структуры регистрируйте до host.Kind<T>().",
                    $"{where}: type {t.Name} is not supported ({tie.InnerException.Message}). " +
                    "Register enums and structs before host.Kind<T>()."), tie.InnerException);
            }
        }

        private static Func<IHostContext, Variant, object> Box<TF>(HostBuilder host)
        {
            host.Types.RefOf<TF>(); // бросает понятную ошибку на незарегистрированный тип
            var r = host.Types.Reader<TF>();
            return (h, v) => r(h, v);
        }

        /// <summary>FNV-1a 64: стабилен между машинами и рантаймами (в отличие от string.GetHashCode).</summary>
        private static class Fnv
        {
            public const ulong Start = 14695981039346656037UL;
            private const ulong Prime = 1099511628211UL;

            public static ulong Int(ulong h, int v)
            {
                unchecked
                {
                    for (int i = 0; i < 4; i++) { h ^= (byte)(v >> (i * 8)); h *= Prime; }
                    return h;
                }
            }

            public static ulong Long(ulong h, long v) => Int(Int(h, (int)v), (int)(v >> 32));

            public static ulong Str(ulong h, string s)
            {
                unchecked
                {
                    h = Int(h, s.Length);
                    foreach (char c in s) { h ^= (byte)c; h *= Prime; h ^= (byte)(c >> 8); h *= Prime; }
                    return h;
                }
            }
        }
    }
}
