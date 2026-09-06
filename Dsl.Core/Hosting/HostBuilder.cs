using System;
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
            Types = new TypeMap();
        }

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
        public EnumBuilder<TEnum> Enum<TEnum>(string name = null, string summary = null) where TEnum : struct, Enum
        {
            name ??= typeof(TEnum).Name;

            var values = (TEnum[])System.Enum.GetValues(typeof(TEnum)); // отсортированы по значению
            var names = System.Enum.GetNames(typeof(TEnum));
            for (int i = 0; i < values.Length; i++)
            {
                if (Convert.ToInt32(values[i]) != i)
                    throw new ArgumentException(
                        $"Енум {typeof(TEnum).Name} нельзя открыть скриптам: значения должны идти " +
                        $"строго 0..{values.Length - 1} без пропусков (нарушает '{names[i]}'). " +
                        "Либо перенумеруйте енум, либо оберните его вручную через сырой HostRegistry.");
            }

            int id = Registry.DefineEnum(name, summary, names);
            Types.AddEnum(TypeRefFor(id), id, values);
            return new EnumBuilder<TEnum>(this, id);
        }

        private static Semantics.TypeRef TypeRefFor(int enumId) => Semantics.TypeRef.EnumOf(enumId);

        // ===================================================================
        // Классы-сущности
        // ===================================================================

        /// <summary>Регистрирует хостовый класс. Имя по умолчанию — имя типа; summary → в манифест.</summary>
        public ClassBuilder<T> Class<T>(string name = null, string summary = null) where T : class
        {
            name ??= typeof(T).Name;
            Registry.DefineClass(name, summary);
            Types.AddEntity<T>(Registry.ClassType(name));
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
        public StructBuilder<T> Struct<T>(string name = null, string summary = null)
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
        public HostBuilder DescribeApiNamespace(string name, string summary)
        {
            Registry.DescribeApiNamespace(name, summary);
            return this;
        }

        /// <summary>
        /// Объявить вид игровой сущности (spell/item/hero/...): скрипты описывают
        /// механики блоками «вид id { event ... }», хост поднимает события адресно
        /// по (вид, id). Виды — данные, а не ключевые слова языка.
        /// </summary>
        public ArchetypeBuilder Archetype(string name, string summary = null) =>
            new ArchetypeBuilder(this, Registry.DefineArchetypeKind(name, summary));

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
                    $"Событие '{name}': в описании {doc.Names.Count} параметров, а в сигнатуре {paramTypes.Length}.");
            return Registry.DefineEvent(name, paramTypes, doc?.Summary, doc?.NameArray(), doc?.DocArray());
        }

        /// <summary>Готовый реестр (тот же, что передан в конструктор).</summary>
        public HostRegistry Build() => Registry;
    }
}
