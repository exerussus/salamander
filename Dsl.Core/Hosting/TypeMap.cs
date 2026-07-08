using System;
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

        public TypeMap()
        {
            Add(TypeRef.Int,
                (ValueReader<int>)((h, v) => v.AsInt),
                (VariantWriter<int>)((h, x) => Variant.Int(x)));

            Add(TypeRef.Float,
                (ValueReader<float>)((h, v) => v.ToF()),
                (VariantWriter<float>)((h, x) => Variant.Float(x)));

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
                        throw new ScriptError($"Значение {i} вне диапазона енума {typeof(TEnum).Name}.");
                    return byValue[i];
                }),
                (VariantWriter<TEnum>)((h, x) => Variant.Enum(enumId, IndexOf(byValue, x))));
        }

        private static int IndexOf<TEnum>(TEnum[] values, TEnum x) where TEnum : struct, Enum
        {
            var cmp = EqualityComparer<TEnum>.Default;
            for (int i = 0; i < values.Length; i++)
                if (cmp.Equals(values[i], x)) return i;
            throw new ScriptError($"Значение {x} не входит в зарегистрированный енум {typeof(TEnum).Name}.");
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

        private static Exception NotRegistered(Type t) => new InvalidOperationException(
            $"Тип {t.Name} не зарегистрирован в скриптовом API. " +
            $"Вызовите host.Class<{t.Name}>() или host.Enum<{t.Name}>() ДО использования типа " +
            "в свойствах, методах и событиях (или спуститесь на сырой HostRegistry).");
    }
}
