using System;

namespace Dsl.Hosting
{
    /// <summary>
    /// Свойства хостового класса. Тип свойства выводится из лямбды; необязательный
    /// doc уходит в манифест API (подсказка в редакторе):
    ///
    /// <code>
    /// host.Class&lt;Unit&gt;("сущность-юнит в бою")
    ///     .Prop("name",   u => u.Name,   doc: "имя юнита")
    ///     .Prop("health", u => u.Health, doc: "текущее HP")
    ///     .Prop("speed",  u => u.Speed, (u, v) => u.Speed = v, "скорость");
    /// </code>
    /// </summary>
    public sealed class ClassBuilder<T> where T : class
    {
        private readonly HostBuilder _b;
        private readonly string _className;

        internal ClassBuilder(HostBuilder b, string className)
        {
            _b = b;
            _className = className;
        }

        /// <summary>Свойство только для чтения.</summary>
        public ClassBuilder<T> Prop<TV>(string name, Func<T, TV> getter, string doc = null)
        {
            if (getter == null) throw new ArgumentNullException(nameof(getter));
            var write = _b.Types.Writer<TV>();
            _b.Registry.DefineProperty(_className, name, _b.Types.RefOf<TV>(), readOnly: true,
                getter: (ctx, o) => write(ctx, getter((T)o)),
                setter: null,
                doc: doc);
            return this;
        }

        /// <summary>Свойство на чтение и запись.</summary>
        public ClassBuilder<T> Prop<TV>(string name, Func<T, TV> getter, Action<T, TV> setter, string doc = null)
        {
            if (getter == null) throw new ArgumentNullException(nameof(getter));
            if (setter == null) throw new ArgumentNullException(nameof(setter));
            var write = _b.Types.Writer<TV>();
            var read = _b.Types.Reader<TV>();
            _b.Registry.DefineProperty(_className, name, _b.Types.RefOf<TV>(), readOnly: false,
                getter: (ctx, o) => write(ctx, getter((T)o)),
                setter: (ctx, o, v) => setter((T)o, read(ctx, v)),
                doc: doc);
            return this;
        }

        /// <summary>Возврат к корневому билдеру для продолжения цепочки.</summary>
        public HostBuilder Host => _b;
    }
}
