namespace Dsl.Semantics
{
    public enum TypeKind : byte
    {
        Error = 0, // тип-ошибка для восстановления после диагностики
        Void,      // отсутствие значения (возврат func без ->)
        Nil,       // тип литерала null
        Bool,
        Int,
        Float,
        Str,
        Fiber,
        Sub,       // Subscription — хэндл подписки listener
        Entity,    // хостовый класс (Unit и т.п.), несёт HostTypeId
        Enum,      // енум (хостовый или скриптовый), несёт EnumId
        Array,     // T[]
        List,      // List<T>
        Map,       // Map<K,V>
    }

    /// <summary>
    /// Разрешённый статический тип. Значения-типы (примитивы) закешированы
    /// синглтонами; составные (Array/List/Map/Entity/Enum) создаются по месту
    /// на этапе проверки типов — это не горячий путь.
    /// </summary>
    public sealed class TypeRef
    {
        public readonly TypeKind Kind;
        public readonly int HostTypeId; // для Entity
        public readonly int EnumId;     // для Enum
        public readonly TypeRef Elem;   // Array/List
        public readonly TypeRef Key;    // Map
        public readonly TypeRef Val;    // Map

        private TypeRef(TypeKind kind, int hostTypeId = -1, int enumId = -1,
                        TypeRef elem = null, TypeRef key = null, TypeRef val = null)
        {
            Kind = kind;
            HostTypeId = hostTypeId;
            EnumId = enumId;
            Elem = elem;
            Key = key;
            Val = val;
        }

        public static readonly TypeRef Error = new TypeRef(TypeKind.Error);
        public static readonly TypeRef Void = new TypeRef(TypeKind.Void);
        public static readonly TypeRef Nil = new TypeRef(TypeKind.Nil);
        public static readonly TypeRef Bool = new TypeRef(TypeKind.Bool);
        public static readonly TypeRef Int = new TypeRef(TypeKind.Int);
        public static readonly TypeRef Float = new TypeRef(TypeKind.Float);
        public static readonly TypeRef Str = new TypeRef(TypeKind.Str);
        public static readonly TypeRef Fiber = new TypeRef(TypeKind.Fiber);
        public static readonly TypeRef Subscription = new TypeRef(TypeKind.Sub);

        public static TypeRef Entity(int hostTypeId) => new TypeRef(TypeKind.Entity, hostTypeId: hostTypeId);
        public static TypeRef EnumOf(int enumId) => new TypeRef(TypeKind.Enum, enumId: enumId);
        public static TypeRef ArrayOf(TypeRef elem) => new TypeRef(TypeKind.Array, elem: elem);
        public static TypeRef ListOf(TypeRef elem) => new TypeRef(TypeKind.List, elem: elem);
        public static TypeRef MapOf(TypeRef key, TypeRef val) => new TypeRef(TypeKind.Map, key: key, val: val);

        public bool IsError => Kind == TypeKind.Error;
        public bool IsNumeric => Kind == TypeKind.Int || Kind == TypeKind.Float;
        // "ссылочные" типы, которым разрешён null и сравнение с null
        public bool IsRefLike => Kind == TypeKind.Entity || Kind == TypeKind.Str
                                  || Kind == TypeKind.Fiber || Kind == TypeKind.Sub || Kind == TypeKind.Array
                                  || Kind == TypeKind.List || Kind == TypeKind.Map;

        /// <summary>Структурное равенство типов.</summary>
        public bool Same(TypeRef o)
        {
            if (ReferenceEquals(this, o)) return true;
            if (o == null || Kind != o.Kind) return false;
            switch (Kind)
            {
                case TypeKind.Entity: return HostTypeId == o.HostTypeId;
                case TypeKind.Enum: return EnumId == o.EnumId;
                case TypeKind.Array:
                case TypeKind.List: return Elem.Same(o.Elem);
                case TypeKind.Map: return Key.Same(o.Key) && Val.Same(o.Val);
                default: return true; // примитивы
            }
        }

        /// <summary>
        /// Можно ли значение типа <paramref name="from"/> присвоить/передать туда,
        /// где ожидается this. Учитывает int→float и null→ref-тип.
        /// </summary>
        public bool AcceptsValueOf(TypeRef from)
        {
            if (from == null || from.IsError || IsError) return true; // не плодим каскад ошибок
            if (Same(from)) return true;
            if (Kind == TypeKind.Float && from.Kind == TypeKind.Int) return true; // расширение
            if (from.Kind == TypeKind.Nil && IsRefLike) return true;
            return false;
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case TypeKind.Void: return "void";
                case TypeKind.Nil: return "null";
                case TypeKind.Bool: return "bool";
                case TypeKind.Int: return "int";
                case TypeKind.Float: return "float";
                case TypeKind.Str: return "string";
                case TypeKind.Fiber: return "Fiber";
                case TypeKind.Sub: return "Subscription";
                case TypeKind.Entity: return $"Entity#{HostTypeId}";
                case TypeKind.Enum: return $"Enum#{EnumId}";
                case TypeKind.Array: return $"{Elem}[]";
                case TypeKind.List: return $"List<{Elem}>";
                case TypeKind.Map: return $"Map<{Key}, {Val}>";
                default: return "<error>";
            }
        }
    }
}
