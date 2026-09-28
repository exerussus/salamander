using System;
using Dsl.Runtime;
using Dsl.Semantics;

namespace Dsl.Hosting
{
    /// <summary>
    /// Свойства и методы хостового класса. Тип свойства выводится из лямбды;
    /// необязательный doc уходит в манифест API (подсказка в редакторе):
    ///
    /// <code>
    /// host.Class&lt;Unit&gt;("сущность-юнит в бою")
    ///     .Prop("name",   u => u.Name,   doc: "имя юнита")
    ///     .Prop("health", u => u.Health, doc: "текущее HP")
    ///     .Prop("speed",  u => u.Speed, (u, v) => u.Speed = v, "скорость");
    ///
    /// host.Class&lt;PerkBasket&gt;()
    ///     .Act("AddPerk", (PerkBasket b, string id) => b.Add(id));   // b.AddPerk("x")
    /// </code>
    ///
    /// Метод объекта и метод API-класса — одно и то же на уровне байткода:
    /// приёмник просто уходит нулевым аргументом хостовой функции. Разница
    /// в том, ГДЕ читатель ищет вызов, поэтому правило простое: метод у класса —
    /// когда объект существует ради вызовов (корзина, билдер, который передают
    /// в событие); API-класс — когда объект это данные.
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

        // ===================================================================
        // Методы объекта
        // ===================================================================

        private TypeMap Types => _b.Types;

        private void Define(string name, TypeRef[] args, TypeRef ret, HostFunction fn, MethodDoc doc)
        {
            if (doc != null && doc.Names.Count != args.Length)
                throw new ArgumentException(
                    $"Метод '{_className}.{name}': в описании {doc.Names.Count} параметров, а у метода " +
                    $"{args.Length}. Приёмник в описании не участвует — он не аргумент скрипта.");
            _b.Registry.DefineClassMethod(_className, name, args, ret, fn,
                doc?.Summary, doc?.NameArray(), doc?.DocArray());
        }

        /// <summary>
        /// Приёмник. Протухший хэндл бросает сам движок; пустая ссылка —
        /// ошибка здесь: вызывать метод на null это не «тихо ничего», а
        /// сломанный рецепт, и файбер должен умереть с внятным текстом.
        /// </summary>
        private T Self(IHostContext h, Variant v, string method)
        {
            var o = h.ResolveObject(v);
            if (o == null)
                throw new ScriptError($"'{_className}.{method}' вызван на пустой ссылке.");
            return (T)o;
        }

        // ----- без результата ---------------------------------------------

        public ClassBuilder<T> Act(string name, Action<T> fn, MethodDoc doc = null)
        {
            
            Define(name, Array.Empty<TypeRef>(), TypeRef.Void,
                (ref CallContext c) => fn(Self(c.Host, c.Arg(0), name)), doc);
            return this;
        }

        public ClassBuilder<T> Act<T1>(string name, Action<T, T1> fn, MethodDoc doc = null)
        {
            var r1 = Types.Reader<T1>();
            Define(name, new[] { Types.RefOf<T1>() }, TypeRef.Void,
                (ref CallContext c) => fn(Self(c.Host, c.Arg(0), name), r1(c.Host, c.Arg(1))), doc);
            return this;
        }

        public ClassBuilder<T> Act<T1, T2>(string name, Action<T, T1, T2> fn, MethodDoc doc = null)
        {
            var r1 = Types.Reader<T1>(); var r2 = Types.Reader<T2>();
            Define(name, new[] { Types.RefOf<T1>(), Types.RefOf<T2>() }, TypeRef.Void,
                (ref CallContext c) => fn(Self(c.Host, c.Arg(0), name), r1(c.Host, c.Arg(1)), r2(c.Host, c.Arg(2))), doc);
            return this;
        }

        public ClassBuilder<T> Act<T1, T2, T3>(string name, Action<T, T1, T2, T3> fn, MethodDoc doc = null)
        {
            var r1 = Types.Reader<T1>(); var r2 = Types.Reader<T2>(); var r3 = Types.Reader<T3>();
            Define(name, new[] { Types.RefOf<T1>(), Types.RefOf<T2>(), Types.RefOf<T3>() }, TypeRef.Void,
                (ref CallContext c) => fn(Self(c.Host, c.Arg(0), name), r1(c.Host, c.Arg(1)), r2(c.Host, c.Arg(2)), r3(c.Host, c.Arg(3))), doc);
            return this;
        }

        public ClassBuilder<T> Act<T1, T2, T3, T4>(string name, Action<T, T1, T2, T3, T4> fn, MethodDoc doc = null)
        {
            var r1 = Types.Reader<T1>(); var r2 = Types.Reader<T2>(); var r3 = Types.Reader<T3>(); var r4 = Types.Reader<T4>();
            Define(name, new[] { Types.RefOf<T1>(), Types.RefOf<T2>(), Types.RefOf<T3>(), Types.RefOf<T4>() }, TypeRef.Void,
                (ref CallContext c) => fn(Self(c.Host, c.Arg(0), name), r1(c.Host, c.Arg(1)), r2(c.Host, c.Arg(2)), r3(c.Host, c.Arg(3)), r4(c.Host, c.Arg(4))), doc);
            return this;
        }

        // ----- с результатом ----------------------------------------------

        public ClassBuilder<T> Fn<TR>(string name, Func<T, TR> fn, MethodDoc doc = null)
        {
            
            var w = Types.Writer<TR>();
            Define(name, Array.Empty<TypeRef>(), Types.RefOf<TR>(),
                (ref CallContext c) => c.Return(w(c.Host, fn(Self(c.Host, c.Arg(0), name)))), doc);
            return this;
        }

        public ClassBuilder<T> Fn<T1, TR>(string name, Func<T, T1, TR> fn, MethodDoc doc = null)
        {
            var r1 = Types.Reader<T1>();
            var w = Types.Writer<TR>();
            Define(name, new[] { Types.RefOf<T1>() }, Types.RefOf<TR>(),
                (ref CallContext c) => c.Return(w(c.Host, fn(Self(c.Host, c.Arg(0), name), r1(c.Host, c.Arg(1))))), doc);
            return this;
        }

        public ClassBuilder<T> Fn<T1, T2, TR>(string name, Func<T, T1, T2, TR> fn, MethodDoc doc = null)
        {
            var r1 = Types.Reader<T1>(); var r2 = Types.Reader<T2>();
            var w = Types.Writer<TR>();
            Define(name, new[] { Types.RefOf<T1>(), Types.RefOf<T2>() }, Types.RefOf<TR>(),
                (ref CallContext c) => c.Return(w(c.Host, fn(Self(c.Host, c.Arg(0), name), r1(c.Host, c.Arg(1)), r2(c.Host, c.Arg(2))))), doc);
            return this;
        }

        public ClassBuilder<T> Fn<T1, T2, T3, TR>(string name, Func<T, T1, T2, T3, TR> fn, MethodDoc doc = null)
        {
            var r1 = Types.Reader<T1>(); var r2 = Types.Reader<T2>(); var r3 = Types.Reader<T3>();
            var w = Types.Writer<TR>();
            Define(name, new[] { Types.RefOf<T1>(), Types.RefOf<T2>(), Types.RefOf<T3>() }, Types.RefOf<TR>(),
                (ref CallContext c) => c.Return(w(c.Host, fn(Self(c.Host, c.Arg(0), name), r1(c.Host, c.Arg(1)), r2(c.Host, c.Arg(2)), r3(c.Host, c.Arg(3))))), doc);
            return this;
        }

        public ClassBuilder<T> Fn<T1, T2, T3, T4, TR>(string name, Func<T, T1, T2, T3, T4, TR> fn, MethodDoc doc = null)
        {
            var r1 = Types.Reader<T1>(); var r2 = Types.Reader<T2>(); var r3 = Types.Reader<T3>(); var r4 = Types.Reader<T4>();
            var w = Types.Writer<TR>();
            Define(name, new[] { Types.RefOf<T1>(), Types.RefOf<T2>(), Types.RefOf<T3>(), Types.RefOf<T4>() }, Types.RefOf<TR>(),
                (ref CallContext c) => c.Return(w(c.Host, fn(Self(c.Host, c.Arg(0), name), r1(c.Host, c.Arg(1)), r2(c.Host, c.Arg(2)), r3(c.Host, c.Arg(3)), r4(c.Host, c.Arg(4))))), doc);
            return this;
        }

        /// <summary>Возврат к корневому билдеру для продолжения цепочки.</summary>
        public HostBuilder Host => _b;
    }
}
