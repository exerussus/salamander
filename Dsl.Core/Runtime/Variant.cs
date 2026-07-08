using System;

namespace Dsl.Runtime
{
    public enum VariantType : byte
    {
        Nil = 0,
        Bool,
        Int,
        Float,
        Str,     // _lo = id в StringTable
        Entity,  // _lo = index, _hi = version (хэндл в EntityRegistry)
        Fiber,   // _lo = index, _hi = version (хэндл в FiberPool)
        Sub,     // _lo = index, _hi = version (хэндл подписки listener)
        Enum,    // _lo = value, _hi = enumTypeId
        Array,   // _lo = id в CollectionStore
        List,    // _lo = id в CollectionStore
        Map,     // _lo = id в CollectionStore
    }

    /// <summary>
    /// Значение скриптового рантайма. Всё умещается в тип + два int:
    /// примитивы лежат в битах, строки/сущности/файберы/коллекции — как id
    /// или версионированные хэндлы. Никаких managed-ссылок внутри —
    /// массивы Variant не создают нагрузку на GC, что критично при тысячах файберов.
    /// </summary>
    public readonly struct Variant : IEquatable<Variant>
    {
        public readonly VariantType Type;
        private readonly int _lo;
        private readonly int _hi;

        private Variant(VariantType type, int lo, int hi)
        {
            Type = type;
            _lo = lo;
            _hi = hi;
        }

        public static readonly Variant Nil = default; // Type=Nil, lo=hi=0

        public static Variant Bool(bool b) => new Variant(VariantType.Bool, b ? 1 : 0, 0);
        public static Variant Int(int i) => new Variant(VariantType.Int, i, 0);
        public static Variant Float(float f) => new Variant(VariantType.Float, BitConverter.SingleToInt32Bits(f), 0);
        public static Variant Str(int id) => new Variant(VariantType.Str, id, 0);
        public static Variant Entity(int index, int version) => new Variant(VariantType.Entity, index, version);
        public static Variant Fiber(int index, int version) => new Variant(VariantType.Fiber, index, version);
        public static Variant Sub(int index, int version) => new Variant(VariantType.Sub, index, version);
        public static Variant Enum(int enumTypeId, int value) => new Variant(VariantType.Enum, value, enumTypeId);
        public static Variant Coll(VariantType kind, int id) => new Variant(kind, id, 0);

        public bool IsNil => Type == VariantType.Nil;

        public bool AsBool => _lo != 0;
        public int AsInt => _lo;
        public float AsFloat => BitConverter.Int32BitsToSingle(_lo);
        public int StrId => _lo;
        public int Index => _lo;
        public int Version => _hi;
        public int EnumValue => _lo;
        public int EnumTypeId => _hi;
        public int CollId => _lo;

        /// <summary>Приведение к float для арифметики (int тоже допустим).</summary>
        public float ToF()
        {
            if (Type == VariantType.Float) return AsFloat;
            if (Type == VariantType.Int) return _lo;
            return 0f;
        }

        /// <summary>"Истинность" для условий: применимо только к Bool (чекер это гарантирует).</summary>
        public bool Truthy() => Type == VariantType.Bool && _lo != 0;

        public bool Equals(Variant other)
        {
            if (Type != other.Type)
            {
                // сравнение null с ссылочным значением
                if (Type == VariantType.Nil) return other.IsRefLikeNilCheck();
                if (other.Type == VariantType.Nil) return IsRefLikeNilCheck();
                return false;
            }
            if (Type == VariantType.Float) return AsFloat == other.AsFloat;
            return _lo == other._lo && _hi == other._hi;
        }

        // null == entity истинно только когда сущность "пустая"; мы не храним
        // отдельного «нулевого» хэндла — сравнение с null для сущности/строки
        // трактуем как «значение отсутствует», а отсутствие представляется Nil.
        private bool IsRefLikeNilCheck() => Type == VariantType.Nil;

        public override bool Equals(object obj) => obj is Variant v && Equals(v);
        public override int GetHashCode() => ((int)Type * 397) ^ (_lo * 31 ^ _hi);

        public override string ToString()
        {
            switch (Type)
            {
                case VariantType.Nil: return "null";
                case VariantType.Bool: return AsBool ? "true" : "false";
                case VariantType.Int: return AsInt.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case VariantType.Float: return AsFloat.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case VariantType.Str: return $"str#{StrId}";
                case VariantType.Entity: return $"entity#{Index}.{Version}";
                case VariantType.Fiber: return $"fiber#{Index}.{Version}";
                case VariantType.Sub: return $"sub#{Index}.{Version}";
                case VariantType.Enum: return $"enum#{EnumTypeId}:{EnumValue}";
                default: return $"{Type}#{CollId}";
            }
        }
    }
}
