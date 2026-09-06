using System;
using System.Collections.Generic;
using Dsl.Runtime;
using Dsl.Semantics;

namespace Dsl.Hosting
{
    /// <summary>
    /// Регистрация структуры хоста: имя, поля с типами и значениями по умолчанию,
    /// и (необязательно) фабрика, которая собирает C#-значение обратно.
    ///
    /// Скрипт такие типы НЕ объявляет — только конструирует:
    /// <code>readonly Damage damage = new Damage(slash: 21);</code>
    /// Незаданные поля берут дефолт. Поля неизменяемы: присваивание полю —
    /// ошибка компиляции, поэтому значение можно свободно передавать, не думая
    /// о копиях.
    /// </summary>
    public sealed class StructBuilder<T>
    {
        private readonly HostBuilder _host;
        private readonly int _id;
        private readonly StructIO<T> _io;

        internal StructBuilder(HostBuilder host, int id, StructIO<T> io)
        {
            _host = host;
            _id = id;
            _io = io;
        }

        /// <summary>
        /// Поле с типом TF и значением по умолчанию (по умолчанию — default(TF)).
        /// Такое поле объявлено «только для скрипта»: собрать структуру из
        /// C#-значения движок не сможет — для этого нужна перегрузка с геттером.
        /// </summary>
        public StructBuilder<T> Field<TF>(string name, TF @default = default, string doc = null)
        {
            DefineField<TF>(name, @default, doc);
            _io.AddFieldWithoutGetter(name);
            return this;
        }

        /// <summary>
        /// Поле вместе с геттером из C#-значения:
        /// <code>.Field("slash", (Damage d) => d.Slash)</code>
        /// Геттер нужен, только чтобы отдавать структуру СКРИПТУ — вернуть из
        /// метода, положить в свойство, передать аргументом события.
        /// </summary>
        public StructBuilder<T> Field<TF>(string name, Func<T, TF> getter,
                                          TF @default = default, string doc = null)
        {
            if (getter == null) throw new ArgumentNullException(nameof(getter));
            DefineField<TF>(name, @default, doc);
            var write = _host.Types.Writer<TF>();
            _io.AddField(name, (h, x) => write(h, getter(x)));
            return this;
        }

        private void DefineField<TF>(string name, TF @default, string doc)
        {
            var type = _host.Types.RefOf<TF>();
            EncodeDefault(type, @default, out var v, out var str);
            _host.Registry.DefineStructField(_id, name, type, v, str, doc);
        }

        /// <summary>
        /// Фабрика C#-значения из полей. Без неё структура всё равно работает —
        /// хост читает поля через engine.TryGetStructField, — но engine.ReadStruct&lt;T&gt;
        /// и приём структуры параметром хостового метода станут недоступны.
        /// </summary>
        public StructBuilder<T> Build(Func<StructValue, T> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _host.Registry.SetStructFactory(_id, factory);
            _io.Factory = factory;
            return this;
        }

        // Значение по умолчанию хранится так же, как у констант вида: строка
        // отдельным полем, потому что её id раздаёт StringTable уже при загрузке,
        // а на регистрации никакого движка ещё нет.
        private static void EncodeDefault<TF>(TypeRef type, TF value, out Variant variant, out string str)
        {
            variant = Variant.Nil;
            str = null;
            switch (type.Kind)
            {
                case TypeKind.Bool: variant = Variant.Bool((bool)(object)value); return;
                case TypeKind.Int: variant = Variant.Int((int)(object)value); return;
                case TypeKind.Float: variant = Variant.Float((float)(object)value); return;
                case TypeKind.Double: variant = Variant.Double((double)(object)value); return;
                case TypeKind.Str: str = (string)(object)value; return;
                case TypeKind.Enum: variant = Variant.Enum(type.EnumId, Convert.ToInt32(value)); return;
                default:
                    throw new ArgumentException(
                        $"Поле структуры '{name(type)}' не может быть такого типа: полем может быть " +
                        "литеральное значение (bool/int/float/double/string) или элемент енума. " +
                        "Сущности, коллекции и другие структуры полями быть не могут.");
            }

            string name(TypeRef t) => t?.ToString() ?? "?";
        }
    }
}
