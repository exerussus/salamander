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

        internal StructBuilder(HostBuilder host, int id)
        {
            _host = host;
            _id = id;
        }

        /// <summary>Поле с типом TF и значением по умолчанию (по умолчанию — default(TF)).</summary>
        public StructBuilder<T> Field<TF>(string name, TF @default = default, string doc = null)
        {
            var type = _host.Types.RefOf<TF>();
            EncodeDefault(type, @default, out var v, out var str);
            _host.Registry.DefineStructField(_id, name, type, v, str, doc);
            return this;
        }

        /// <summary>
        /// Фабрика C#-значения из полей. Без неё структура всё равно работает —
        /// хост читает поля через engine.TryGetStructField, — но engine.ReadStruct&lt;T&gt;
        /// станет недоступен.
        /// </summary>
        public StructBuilder<T> Build(Func<StructValue, T> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _host.Registry.SetStructFactory(_id, factory);
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
