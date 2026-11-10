using System;
using System.Collections.Generic;
using Dsl.Text;
using Dsl.Semantics;

namespace Dsl.Hosting
{
    /// <summary>
    /// Fluent-обёртка над HostRegistry: типобезопасная регистрация без ручных
    /// Variant, кастов и кэширования id. Сырой реестр остаётся доступен
    /// (Registry) для случаев, которые обёртка не покрывает.
    ///
    /// Пример:
    /// <code>
    /// var host = new HostBuilder(registry);
    ///
    /// host.Enum&lt;Team&gt;()
    ///     .Enum&lt;DamageType&gt;();
    ///
    /// host.Class&lt;Unit&gt;()
    ///     .Prop("name",   u => u.Name)
    ///     .Prop("health", u => u.Health);
    ///
    /// host.Api("UnitApi")
    ///     .Fn("IsBoss", (Unit u) => u.IsBoss)
    ///     .Act("Heal",  (Unit u, float hp) => u.Health += hp);
    ///
    /// _evDied = host.Event&lt;Unit, Unit&gt;("OnUnitDied");
    /// // ...
    /// _evDied.Raise(Engine, target, attacker);
    /// </code>
    ///
    /// Порядок важен: тип должен быть объявлен (Class/Enum/Struct) до первого
    /// использования в свойствах/методах/событиях — иначе понятная ошибка.
    /// </summary>
    public sealed class HostBuilder
    {
        public HostRegistry Registry { get; }
        public TypeMap Types { get; }

        public HostBuilder() : this(new HostRegistry()) { }

        public HostBuilder(HostRegistry registry)
        {
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Types = new TypeMap { Owner = registry };
        }

        /// <summary>
        /// Язык ошибок регистрации и язык по умолчанию для компиляции и движков на этом
        /// хосте (<see cref="HostRegistry.Language"/>). По умолчанию — ru.
        /// </summary>
        public HostBuilder UseLanguage(Language language)
        {
            Registry.Language = language;
            return this;
        }

        internal string T(string ru, string en) => Loc.T(Registry.Language, ru, en);

        // ===================================================================
        // Енумы
        // ===================================================================

        /// <summary>
        /// Регистрирует C#-енум как скриптовый. Имя по умолчанию — имя типа;
        /// summary передавайте именованным аргументом: Enum&lt;Team&gt;(summary: "...").
        /// Значения обязаны быть 0..N-1 без пропусков (требование движка:
        /// значение енума = индекс имени).
        ///
        /// Возвращает построитель элементов — там описываются ЕДИНИЦЫ:
        /// <code>
        /// host.Enum&lt;Slot&gt;(summary: "Слоты характеристик.")
        ///     .Member(Slot.MoveSpeed,   "Скорость передвижения, м/с.")
        ///     .Member(Slot.AttackSpeed, "Множитель времён оружия. Меньше — быстрее.");
        /// </code>
        /// </summary>
        public EnumBuilder<TEnum> Enum<TEnum>(string name = null, DocText summary = null) where TEnum : struct, Enum
        {
            name ??= typeof(TEnum).Name;

            var values = (TEnum[])System.Enum.GetValues(typeof(TEnum)); // отсортированы по значению
            var names = System.Enum.GetNames(typeof(TEnum));
            for (int i = 0; i < values.Length; i++)
            {
                if (Convert.ToInt32(values[i]) != i)
                    throw new ArgumentException(
                        T($"Енум {typeof(TEnum).Name} нельзя открыть скриптам: значения должны идти " +
                        $"строго 0..{values.Length - 1} без пропусков (нарушает '{names[i]}'). " +
                        "Либо перенумеруйте енум, либо оберните его вручную через сырой HostRegistry.",
                            $"Enum {typeof(TEnum).Name} cannot be exposed to scripts: values must go " +
                            $"strictly 0..{values.Length - 1} without gaps (violated by '{names[i]}'). " +
                            "Either renumber the enum or wrap it manually via the raw HostRegistry."));
            }

            int id = Registry.DefineEnum(name, summary, names);
            Types.AddEnum(TypeRefFor(id), id, values);
            return new EnumBuilder<TEnum>(this, id);
        }

        /// <summary>
        /// Енум из ТАБЛИЦЫ, а не из объявления C#: значения C# — любые (с пропусками,
        /// 128+, собранные из открытого реестра), имена в скрипте и их порядок —
        /// по спискам, пояснение — на каждый элемент:
        /// <code>
        /// host.EnumTable("MatchField", "Поля матча.", fields, names, docs);
        /// </code>
        /// Значение в скрипте — индекс в таблице; перевод в C# и обратно делает
        /// таблица, как и у <see cref="Enum{TEnum}"/>. Таблица должна быть полной
        /// к моменту регистрации: всё, что модули хотят добавить, собирается ДО неё.
        /// Порядок таблицы — часть контракта сейва (как порядок элементов обычного
        /// енума): держите его стабильным между версиями.
        /// <paramref name="docs"/> — null или по пояснению на элемент (null внутри — без пояснения).
        /// </summary>
        public HostBuilder EnumTable<TEnum>(string name, DocText summary, IReadOnlyList<TEnum> values,
                                            IReadOnlyList<string> scriptNames, IReadOnlyList<DocText> docs = null)
            where TEnum : struct, Enum
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException(T("Пустое имя енума.", "Empty enum name."), nameof(name));
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (scriptNames == null) throw new ArgumentNullException(nameof(scriptNames));
            if (values.Count == 0)
                throw new ArgumentException(T($"Енум '{name}': пустая таблица.", $"Enum '{name}': the table is empty."), nameof(values));
            if (scriptNames.Count != values.Count)
                throw new ArgumentException(
                    T($"Енум '{name}': значений {values.Count}, а имён {scriptNames.Count}. Списки идут параллельно.",
                        $"Enum '{name}': {values.Count} values but {scriptNames.Count} names. The lists run in parallel."));
            if (docs != null && docs.Count != values.Count)
                throw new ArgumentException(
                    T($"Енум '{name}': значений {values.Count}, а пояснений {docs.Count}. " +
                      "Оставляйте null там, где пояснения нет.",
                        $"Enum '{name}': {values.Count} values but {docs.Count} descriptions. " +
                        "Leave null where there is no description."));

            var byIndex = new TEnum[values.Count];
            var names = new string[values.Count];
            var seenValues = new Dictionary<TEnum, int>();
            var seenNames = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < values.Count; i++)
            {
                var v = values[i];
                var n = scriptNames[i];
                if (!IsIdentifier(n))
                    throw new ArgumentException(
                        T($"Енум '{name}': имя #{i} ('{n}') не идентификатор — скрипт не сможет его набрать.",
                            $"Enum '{name}': name #{i} ('{n}') is not an identifier — a script could not type it."));
                if (Dsl.Text.Lexer.IsKeyword(n))
                    throw new ArgumentException(
                        T($"Енум '{name}': имя '{n}' — ключевое слово языка.",
                            $"Enum '{name}': the name '{n}' is a language keyword."));
                if (seenNames.TryGetValue(n, out int prevN))
                    throw new ArgumentException(
                        T($"Енум '{name}': имя '{n}' встречается дважды (#{prevN} и #{i}).",
                            $"Enum '{name}': the name '{n}' occurs twice (#{prevN} and #{i})."));
                if (seenValues.TryGetValue(v, out int prevV))
                    throw new ArgumentException(
                        T($"Енум '{name}': значение {v} встречается дважды ('{names[prevV]}' и '{n}') — " +
                          "обратный перевод C# → скрипт стал бы неоднозначным.",
                            $"Enum '{name}': the value {v} occurs twice ('{names[prevV]}' and '{n}') — " +
                            "the reverse mapping C# → script would be ambiguous."));
                seenNames[n] = i;
                seenValues[v] = i;
                byIndex[i] = v;
                names[i] = n;
            }

            DocText[] memberDocs = null;
            if (docs != null)
            {
                memberDocs = new DocText[docs.Count];
                for (int i = 0; i < docs.Count; i++)
                    memberDocs[i] = docs[i] == null || docs[i].IsEmpty ? null : docs[i];
            }

            int id = Registry.DefineEnum(name, summary, names, memberDocs);
            Types.AddEnum(TypeRefFor(id), id, byIndex);
            return this;
        }

        /// <summary>То же с пояснениями-строками (без разделения по языкам).</summary>
        public HostBuilder EnumTable<TEnum>(string name, DocText summary, IReadOnlyList<TEnum> values,
                                            IReadOnlyList<string> scriptNames, IReadOnlyList<string> docs)
            where TEnum : struct, Enum
        {
            DocText[] d = null;
            if (docs != null)
            {
                d = new DocText[docs.Count];
                for (int i = 0; i < docs.Count; i++) d[i] = docs[i];
            }
            return EnumTable(name, summary, values, scriptNames, (IReadOnlyList<DocText>)d);
        }

        private static bool IsIdentifier(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
            for (int i = 1; i < s.Length; i++)
                if (!(char.IsLetterOrDigit(s[i]) || s[i] == '_')) return false;
            return true;
        }

        private static Semantics.TypeRef TypeRefFor(int enumId) => Semantics.TypeRef.EnumOf(enumId);

        // ===================================================================
        // Классы-сущности
        // ===================================================================

        /// <summary>Регистрирует хостовый класс. Имя по умолчанию — имя типа; summary → в манифест.</summary>
        public ClassBuilder<T> Class<T>(string name = null, DocText summary = null) where T : class
        {
            name ??= typeof(T).Name;
            Registry.DefineClass(name, summary);
            Types.AddEntity<T>(Registry.ClassType(name));
            return new ClassBuilder<T>(this, name);
        }

        /// <summary>
        /// Дописать свойства и методы к классу, который уже объявлен в ДРУГОМ месте
        /// регистрации этого же хоста (движок объявил <c>UiBox</c>, модуль игры
        /// добавляет <c>b.Card(...)</c>):
        /// <code>
        /// host.ExtendClass&lt;UiBox&gt;()
        ///     .Act("Card", (UiBox b, string id) => ..., Sig.Doc("Карточка.").P("id", "..."));
        /// </code>
        /// Дописанное ничем не отличается от объявленного сразу: чекер, автодополнение
        /// и salamander-api.json видят его так же. Класса нет — исключение (расширение
        /// не объявляет, а дописывает); <typeparamref name="T"/> не тот, под которым
        /// класс объявлен, — исключение; имя метода или свойства уже занято —
        /// исключение с именем класса и члена, тихой перезаписи нет.
        /// Расширять можно, пока реестр не ушёл в компиляцию.
        /// </summary>
        public ClassBuilder<T> ExtendClass<T>(string name = null) where T : class
        {
            name ??= typeof(T).Name;
            if (!Registry.HasClass(name))
                throw new InvalidOperationException(
                    this.T($"Нельзя расширить класс '{name}': он не объявлен. ExtendClass дописывает " +
                      $"существующий класс — объявите его через Class<{typeof(T).Name}>() раньше.",
                        $"Cannot extend class '{name}': it is not declared. ExtendClass adds to an " +
                        $"existing class — declare it with Class<{typeof(T).Name}>() first."));

            var classRef = Registry.ClassType(name);
            if (!Types.TryGetRef(typeof(T), out var mapped) || !mapped.Same(classRef))
            {
                var declared = Types.FindType(classRef);
                throw new InvalidOperationException(declared != null
                    ? this.T($"Нельзя расширить класс '{name}' типом {typeof(T).Name}: класс объявлен под типом {declared.Name}.",
                        $"Cannot extend class '{name}' with type {typeof(T).Name}: the class is declared with type {declared.Name}.")
                    : this.T($"Нельзя расширить класс '{name}' типом {typeof(T).Name}: класс объявлен не через " +
                        "Class<T>() (сырой HostRegistry), и C#-тип его хэндлов неизвестен.",
                        $"Cannot extend class '{name}' with type {typeof(T).Name}: the class was not declared " +
                        "via Class<T>() (raw HostRegistry), so the C# type of its handles is unknown."));
            }
            return new ClassBuilder<T>(this, name);
        }

        // ===================================================================
        // Структуры: неизменяемый именованный набор полей
        // ===================================================================

        /// <summary>
        /// Объявить структуру хоста. Скрипт её не объявляет, только собирает:
        /// <code>readonly Damage damage = new Damage(slash: 21);</code>
        /// Незаданные поля берут значение по умолчанию, полю присвоить нельзя.
        /// Под капотом значение — массив Variant, поэтому сборщик и сейв о
        /// структурах ничего знать не обязаны.
        ///
        /// Структуру можно принимать параметром хостового метода (нужна фабрика
        /// из .Build(...)) и отдавать скрипту — из метода, свойства или события
        /// (нужны геттеры полей, см. перегрузку Field с getter).
        /// </summary>
        public StructBuilder<T> Struct<T>(string name = null, DocText summary = null)
        {
            name ??= typeof(T).Name;
            int id = Registry.DefineStruct(name, summary);
            var io = new StructIO<T>(id, Registry.GetStruct(id));
            Types.AddStruct<T>(Semantics.TypeRef.StructOf(id), io);
            return new StructBuilder<T>(this, id, io);
        }

        // ===================================================================
        // API-классы
        // ===================================================================

        public ApiBuilder Api(string name) => new ApiBuilder(this, name);

        /// <summary>
        /// Описание УЗЛА составного имени — того, что человек набирает первым:
        /// <code>
        /// host.DescribeApiNamespace("Api", "Всё, что доступно контенту.");
        /// host.DescribeApiNamespace("Api.PartsCatalog", "Имена деталей из каталога.");
        /// </code>
        /// Узел — не API-класс: у него нет методов, и вызвать его нельзя. Порядок
        /// свободный, узла может ещё не быть — он создастся.
        /// </summary>
        public HostBuilder DescribeApiNamespace(string name, DocText summary)
        {
            Registry.DescribeApiNamespace(name, summary);
            return this;
        }

        // ===================================================================
        // Сборка
        // ===================================================================

        /// <summary>
        /// Гранулярность карантина, когда игра собирает с
        /// <c>quarantineBrokenModules: true</c>:
        /// <code>
        /// host.Quarantine(QuarantineScope.File); // опечатка стоит файла, а не пака
        /// </code>
        /// По умолчанию <see cref="QuarantineScope.Module"/> — как было.
        /// </summary>
        public HostBuilder Quarantine(QuarantineScope scope)
        {
            Registry.Quarantine = scope;
            return this;
        }

        /// <summary>
        /// Объявить вид игровой сущности (spell/item/hero/...): скрипты описывают
        /// механики блоками «вид id { event ... }», хост поднимает события адресно
        /// по (вид, id). Виды — данные, а не ключевые слова языка.
        /// </summary>
        public ArchetypeBuilder Archetype(string name, DocText summary = null) =>
            new ArchetypeBuilder(this, Registry.DefineArchetypeKind(name, summary));

        /// <summary>
        /// Вид архетипа из C#-типа: имя вида — [SalamanderKind], константы вида —
        /// поля с [KindField] (Const/ConstOr регистрируются сами, описывать поля
        /// второй раз не нужно). Данные потом забирает engine.Bake&lt;T&gt;().
        /// Возвращает обычный построитель вида — события объявляются как раньше:
        /// <code>
        /// var onCast = host.Kind&lt;SpellDef&gt;().Event&lt;Unit, Unit&gt;("OnCast");
        /// </code>
        /// Енумы и структуры, которые встречаются в полях, регистрируйте раньше.
        /// </summary>
        public ArchetypeBuilder Kind<T>() => ArchetypeBaking.RegisterKind<T>(this);

        // ===================================================================
        // Регистрация по атрибутам (рефлексия только при регистрации)
        // ===================================================================

        /// <summary>
        /// Регистрирует тип, помеченный [SalamanderClass] (и его члены с
        /// [SalamanderProperty]/[SalamanderMethod]). Описания живут на самом
        /// типе; здесь его нужно лишь явно указать. Тип НЕ подключается сам.
        /// </summary>
        public HostBuilder Register(Type type)
        {
            ReflectionRegistrar.Register(this, type);
            return this;
        }

        public HostBuilder Register<T>() => Register(typeof(T));

        /// <summary>Зарегистрировать сразу несколько типов по атрибутам.</summary>
        public HostBuilder Register(params Type[] types)
        {
            foreach (var t in types) Register(t);
            return this;
        }

        /// <summary>
        /// Регистрирует API из ЭКЗЕМПЛЯРА класса, помеченного [SalamanderApi].
        /// Методы биндятся к этому экземпляру, поэтому его состояние (мир, комната,
        /// сервисы) не общее — у каждого движка/комнаты свой экземпляр. Никакой
        /// статики в коде не требуется.
        /// </summary>
        public HostBuilder RegisterApi(object instance)
        {
            ReflectionRegistrar.RegisterApiInstance(this, instance);
            return this;
        }

        /// <summary>
        /// То же, но с явным именем API в скриптах — в том числе составным
        /// ("Api.Weapon" → вызов Api.Weapon.Метод(...)). Перебивает
        /// [SalamanderApi(Name = ...)] и имя C#-типа.
        /// </summary>
        public HostBuilder RegisterApi(object instance, string name)
        {
            ReflectionRegistrar.RegisterApiInstance(this, instance, name);
            return this;
        }

        // ===================================================================
        // События — возвращают типизированные ссылки для Raise без Variant
        // ===================================================================

        public EventRef Event(string name, MethodDoc doc = null) =>
            new EventRef(DefineEvent(name, doc, System.Array.Empty<Semantics.TypeRef>()));

        public EventRef<T1> Event<T1>(string name, MethodDoc doc = null) =>
            new EventRef<T1>(
                DefineEvent(name, doc, Types.RefOf<T1>()),
                Types.Writer<T1>());

        public EventRef<T1, T2> Event<T1, T2>(string name, MethodDoc doc = null) =>
            new EventRef<T1, T2>(
                DefineEvent(name, doc, Types.RefOf<T1>(), Types.RefOf<T2>()),
                Types.Writer<T1>(), Types.Writer<T2>());

        public EventRef<T1, T2, T3> Event<T1, T2, T3>(string name, MethodDoc doc = null) =>
            new EventRef<T1, T2, T3>(
                DefineEvent(name, doc, Types.RefOf<T1>(), Types.RefOf<T2>(), Types.RefOf<T3>()),
                Types.Writer<T1>(), Types.Writer<T2>(), Types.Writer<T3>());

        public EventRef<T1, T2, T3, T4> Event<T1, T2, T3, T4>(string name, MethodDoc doc = null) =>
            new EventRef<T1, T2, T3, T4>(
                DefineEvent(name, doc, Types.RefOf<T1>(), Types.RefOf<T2>(), Types.RefOf<T3>(), Types.RefOf<T4>()),
                Types.Writer<T1>(), Types.Writer<T2>(), Types.Writer<T3>(), Types.Writer<T4>());

        public EventRef<T1, T2, T3, T4, T5> Event<T1, T2, T3, T4, T5>(string name, MethodDoc doc = null) =>
            new EventRef<T1, T2, T3, T4, T5>(
                DefineEvent(name, doc, Types.RefOf<T1>(), Types.RefOf<T2>(), Types.RefOf<T3>(), Types.RefOf<T4>(), Types.RefOf<T5>()),
                Types.Writer<T1>(), Types.Writer<T2>(), Types.Writer<T3>(), Types.Writer<T4>(), Types.Writer<T5>());

        public EventRef<T1, T2, T3, T4, T5, T6> Event<T1, T2, T3, T4, T5, T6>(string name, MethodDoc doc = null) =>
            new EventRef<T1, T2, T3, T4, T5, T6>(
                DefineEvent(name, doc, Types.RefOf<T1>(), Types.RefOf<T2>(), Types.RefOf<T3>(), Types.RefOf<T4>(), Types.RefOf<T5>(), Types.RefOf<T6>()),
                Types.Writer<T1>(), Types.Writer<T2>(), Types.Writer<T3>(), Types.Writer<T4>(), Types.Writer<T5>(), Types.Writer<T6>());

        public EventRef<T1, T2, T3, T4, T5, T6, T7> Event<T1, T2, T3, T4, T5, T6, T7>(string name, MethodDoc doc = null) =>
            new EventRef<T1, T2, T3, T4, T5, T6, T7>(
                DefineEvent(name, doc, Types.RefOf<T1>(), Types.RefOf<T2>(), Types.RefOf<T3>(), Types.RefOf<T4>(), Types.RefOf<T5>(), Types.RefOf<T6>(), Types.RefOf<T7>()),
                Types.Writer<T1>(), Types.Writer<T2>(), Types.Writer<T3>(), Types.Writer<T4>(), Types.Writer<T5>(), Types.Writer<T6>(), Types.Writer<T7>());

        public EventRef<T1, T2, T3, T4, T5, T6, T7, T8> Event<T1, T2, T3, T4, T5, T6, T7, T8>(string name, MethodDoc doc = null) =>
            new EventRef<T1, T2, T3, T4, T5, T6, T7, T8>(
                DefineEvent(name, doc, Types.RefOf<T1>(), Types.RefOf<T2>(), Types.RefOf<T3>(), Types.RefOf<T4>(), Types.RefOf<T5>(), Types.RefOf<T6>(), Types.RefOf<T7>(), Types.RefOf<T8>()),
                Types.Writer<T1>(), Types.Writer<T2>(), Types.Writer<T3>(), Types.Writer<T4>(), Types.Writer<T5>(), Types.Writer<T6>(), Types.Writer<T7>(), Types.Writer<T8>());

        private int DefineEvent(string name, MethodDoc doc, params Semantics.TypeRef[] paramTypes)
        {
            if (doc != null && doc.Names.Count != paramTypes.Length)
                throw new ArgumentException(
                    T($"Событие '{name}': в описании {doc.Names.Count} параметров, а в сигнатуре {paramTypes.Length}.",
                        $"Event '{name}': the description has {doc.Names.Count} parameters, the signature has {paramTypes.Length}."));
            return Registry.DefineEvent(name, paramTypes, doc?.Summary, doc?.NameArray(), doc?.DocArray());
        }

        /// <summary>Готовый реестр (тот же, что передан в конструктор).</summary>
        public HostRegistry Build() => Registry;
    }
}
