using System;
using Dsl.Text;
using System.Collections.Generic;
using Dsl.Runtime;
using Dsl.Semantics;

namespace Dsl.Hosting
{
    /// <summary>Variant → значение C#-типа (аргументы методов, сеттеры свойств).</summary>
    public delegate T ValueReader<T>(IHostContext host, Variant v);

    /// <summary>Значение C#-типа → Variant (результаты, геттеры, аргументы событий).</summary>
    public delegate Variant VariantWriter<T>(IHostContext host, T value);

    /// <summary>
    /// Сердце fluent-слоя: для каждого C#-типа хранит TypeRef (для сигнатур
    /// компилятора) и пару читатель/писатель Variant. Все разрешения типов
    /// происходят ОДИН раз при регистрации; в горячем пути остаются только
    /// прямые вызовы уже выбранных делегатов — без рефлексии и боксинга
    /// (единственное исключение — возврат енума из хостового метода, см. AddEnum).
    /// </summary>
    public sealed class TypeMap
    {
        private sealed class Entry
        {
            public TypeRef Ref;
            public object Reader; // ValueReader<T>
            public object Writer; // VariantWriter<T>
        }

        private readonly Dictionary<Type, Entry> _entries = new Dictionary<Type, Entry>();

        /// <summary>Реестр-владелец: из него берётся язык сообщений (может быть null).</summary>
        public HostRegistry Owner { get; set; }

        public TypeMap()
        {
            Add(TypeRef.Int,
                (ValueReader<int>)((h, v) => v.AsInt),
                (VariantWriter<int>)((h, x) => Variant.Int(x)));

            Add(TypeRef.Float,
                (ValueReader<float>)((h, v) => v.ToF()),
                (VariantWriter<float>)((h, x) => Variant.Float(x)));

            Add(TypeRef.Double,
                (ValueReader<double>)((h, v) => v.ToD()),
                (VariantWriter<double>)((h, x) => Variant.Double(x)));

            Add(TypeRef.Bool,
                (ValueReader<bool>)((h, v) => v.AsBool),
                (VariantWriter<bool>)((h, x) => Variant.Bool(x)));

            Add(TypeRef.Str,
                (ValueReader<string>)((h, v) => h.ResolveString(v)),
                (VariantWriter<string>)((h, x) => x == null ? Variant.Nil : Variant.Str(h.InternString(x))));

            // прямой доступ к Variant — люк для продвинутых сценариев
            Add(TypeRef.Error,
                (ValueReader<Variant>)((h, v) => v),
                (VariantWriter<Variant>)((h, x) => x));
        }

        private void Add<T>(TypeRef r, ValueReader<T> reader, VariantWriter<T> writer)
        {
            _entries[typeof(T)] = new Entry { Ref = r, Reader = reader, Writer = writer };
        }

        /// <summary>Класс-сущность хоста: хэндлы через EntityRegistry движка.</summary>
        public void AddEntity<T>(TypeRef entityRef) where T : class
        {
            Add(entityRef,
                (ValueReader<T>)((h, v) => (T)h.ResolveObject(v)),
                (VariantWriter<T>)((h, x) => h.WrapObject(x)));
        }

        /// <summary>
        /// Структура хоста. Читатель и писатель идут через <paramref name="io"/>:
        /// фабрику и геттеры полей туда доливает StructBuilder уже ПОСЛЕ этой
        /// записи, поэтому объявлять поля можно в любом порядке, а понятная
        /// ошибка про «нет фабрики»/«нет геттера» всплывает только если этой
        /// стороной границы реально воспользовались.
        /// </summary>
        public void AddStruct<T>(TypeRef structRef, StructIO<T> io)
        {
            if (io == null) throw new ArgumentNullException(nameof(io));
            Add(structRef,
                (ValueReader<T>)((h, v) => io.Read(h, v)),
                (VariantWriter<T>)((h, x) => io.Write(h, x)));
        }

        /// <summary>
        /// Енум хоста. Требование движка: значения строго 0..N-1 (значение = индекс
        /// имени). Чтение int→TEnum идёт через таблицу без боксинга; запись
        /// TEnum→int — линейным поиском по таблице (енумы маленькие, боксинга нет).
        /// </summary>
        public void AddEnum<TEnum>(TypeRef enumRef, int enumId, TEnum[] byValue)
            where TEnum : struct, Enum
        {
            Add(enumRef,
                (ValueReader<TEnum>)((h, v) =>
                {
                    int i = v.EnumValue;
                    if ((uint)i >= (uint)byValue.Length)
                        throw new ScriptError($"Значение {i} вне диапазона енума {typeof(TEnum).Name}.",
                            $"Value {i} is out of range of enum {typeof(TEnum).Name}.");
                    return byValue[i];
                }),
                (VariantWriter<TEnum>)((h, x) => Variant.Enum(enumId, IndexOf(byValue, x))));
        }

        private static int IndexOf<TEnum>(TEnum[] values, TEnum x) where TEnum : struct, Enum
        {
            var cmp = EqualityComparer<TEnum>.Default;
            for (int i = 0; i < values.Length; i++)
                if (cmp.Equals(values[i], x)) return i;
            throw new ScriptError($"Значение {x} не входит в зарегистрированный енум {typeof(TEnum).Name}.",
                $"Value {x} is not in the registered enum {typeof(TEnum).Name}.");
        }

        /// <summary>Есть ли у C#-типа запись — без исключения.</summary>
        internal bool TryGetRef(Type type, out TypeRef r)
        {
            if (type != null && _entries.TryGetValue(type, out var e)) { r = e.Ref; return true; }
            r = null;
            return false;
        }

        /// <summary>C#-тип, зарегистрированный под этим скриптовым типом (для текста ошибок); null — нет такого.</summary>
        internal Type FindType(TypeRef r)
        {
            if (r == null) return null;
            foreach (var kv in _entries)
                if (kv.Value.Ref.Same(r)) return kv.Key;
            return null;
        }

        // ===== доступ (бросают понятные ошибки на этапе регистрации) =======

        public TypeRef RefOf<T>()
        {
            if (_entries.TryGetValue(typeof(T), out var e)) return e.Ref;
            throw NotRegistered(typeof(T));
        }

        public ValueReader<T> Reader<T>()
        {
            if (_entries.TryGetValue(typeof(T), out var e)) return (ValueReader<T>)e.Reader;
            throw NotRegistered(typeof(T));
        }

        public VariantWriter<T> Writer<T>()
        {
            if (_entries.TryGetValue(typeof(T), out var e)) return (VariantWriter<T>)e.Writer;
            throw NotRegistered(typeof(T));
        }

        private Exception NotRegistered(Type t) => new InvalidOperationException(
            Loc.T(Owner?.Language ?? Language.Ru, $"Тип {t.Name} не зарегистрирован в скриптовом API. " +
            $"Вызовите host.Class<{t.Name}>() или host.Enum<{t.Name}>() ДО использования типа " +
            "в свойствах, методах и событиях (или спуститесь на сырой HostRegistry).",
                $"Type {t.Name} is not registered in the script API. " +
                $"Call host.Class<{t.Name}>() or host.Enum<{t.Name}>() BEFORE using the type " +
                "in properties, methods and events (or drop down to the raw HostRegistry)."));
    }
}
