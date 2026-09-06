using System;
using Dsl.Semantics;

namespace Dsl.Runtime
{
    /// <summary>
    /// Значение структуры на стороне хоста: чтение полей ПО ИМЕНИ. Передаётся
    /// в фабрику из Build(...), чтобы собрать C#-значение без завязки на порядок
    /// полей — перестановка объявлений не должна ломать проводку.
    /// </summary>
    public readonly struct StructValue
    {
        private readonly HostStructInfo _info;
        private readonly Variant[] _fields;
        private readonly IHostContext _ctx;

        internal StructValue(HostStructInfo info, Variant[] fields, IHostContext ctx)
        {
            _info = info;
            _fields = fields;
            _ctx = ctx;
        }

        public string TypeName => _info?.Name;

        public Variant Get(string field)
        {
            if (_info == null || !_info.TryGetField(field, out var f))
                throw new ArgumentException($"У структуры '{_info?.Name}' нет поля '{field}'.");
            return (uint)f.Index < (uint)_fields.Length ? _fields[f.Index] : Variant.Nil;
        }

        public float Float(string field) => Get(field).ToF();
        public double Double(string field) => Get(field).ToD();
        public int Int(string field) => Get(field).AsInt;
        public bool Bool(string field) => Get(field).AsBool;
        public string Str(string field) => _ctx?.ResolveString(Get(field));

        /// <summary>Элемент енума как C#-енум (значения в реестре идут 0..N-1).</summary>
        public TEnum Enum<TEnum>(string field) where TEnum : struct, System.Enum
            => (TEnum)System.Enum.ToObject(typeof(TEnum), Get(field).EnumValue);
    }
}
