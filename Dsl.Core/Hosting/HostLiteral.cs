using System;
using Dsl.Runtime;
using Dsl.Semantics;

namespace Dsl.Hosting
{
    /// <summary>
    /// C#-значение → пара (Variant, строка), в которой реестр хранит литералы:
    /// дефолты полей структур, дефолты констант вида, значения констант API.
    ///
    /// Строка идёт ОТДЕЛЬНЫМ полем, а не внутри Variant: её id раздаёт
    /// StringTable уже при загрузке программы, а на регистрации никакого движка
    /// ещё нет. Литералом может быть bool/int/float/double/string или элемент
    /// енума — всё остальное значением по умолчанию быть не может.
    /// </summary>
    internal static class HostLiteral
    {
        public static bool TryEncode<T>(TypeRef type, T value, out Variant variant, out string str)
        {
            variant = Variant.Nil;
            str = null;
            if (type == null) return false;
            switch (type.Kind)
            {
                case TypeKind.Bool: variant = Variant.Bool((bool)(object)value); return true;
                case TypeKind.Int: variant = Variant.Int((int)(object)value); return true;
                case TypeKind.Float: variant = Variant.Float((float)(object)value); return true;
                case TypeKind.Double: variant = Variant.Double((double)(object)value); return true;
                case TypeKind.Str: str = (string)(object)value; return true;
                case TypeKind.Enum:
                    // значения енума в реестре обязаны идти 0..N-1, поэтому
                    // числовое значение и есть индекс имени
                    variant = Variant.Enum(type.EnumId, Convert.ToInt32(value));
                    return true;
                default: return false;
            }
        }

        /// <summary>Общий хвост сообщения об ошибке — чтобы он был одинаковым везде.</summary>
        public const string Supported =
            "значением может быть только литерал (bool/int/float/double/string) " +
            "или элемент енума.";
    }
}
