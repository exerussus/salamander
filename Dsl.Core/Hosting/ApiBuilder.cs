using System;
using Dsl.Runtime;
using Dsl.Semantics;

namespace Dsl.Hosting
{
    /// <summary>
    /// Методы API-класса обычными C#-делегатами — без CallContext, индексов
    /// аргументов и Variant. Необязательный последний аргумент doc добавляет
    /// summary и имена/пояснения параметров, которые уходят в манифест API
    /// (автодополнение и подсказки в редакторе):
    ///
    /// <code>
    /// host.Api("UnitApi")
    ///     .Fn("IsBoss", (Unit u) => u.IsBoss)                     // без описания
    ///     .Act("Heal",  (Unit u, float amount) => u.Heal(amount),
    ///          Sig.Doc("Восстанавливает HP").P("target").P("amount", "сколько"));
    /// </code>
    ///
    /// Поддержано до 4 аргументов; экзотика — через сырой Registry.DefineMethod.
    /// Число .P(...) в doc должно совпасть с числом параметров лямбды.
    /// null-сущность приходит как null — гардьте в лямбде, если аргумент опционален.
    /// </summary>
    public sealed class ApiBuilder
    {
        private readonly HostBuilder _b;
        private readonly string _apiName;

        internal ApiBuilder(HostBuilder b, string apiName)
        {
            _b = b;
            _apiName = apiName;
        }

        public HostBuilder Host => _b;

        private TypeMap T => _b.Types;

        private void Define(string name, TypeRef[] args, TypeRef ret, HostFunction fn, MethodDoc doc)
        {
            if (doc != null && doc.Names.Count != args.Length)
                throw new ArgumentException(
                    $"Метод '{_apiName}.{name}': в описании {doc.Names.Count} параметров, а у метода {args.Length}. " +
                    "Число .P(...) должно совпадать с числом аргументов.");
            _b.Registry.DefineMethod(_apiName, name, args, ret, fn,
                doc?.Summary, doc?.NameArray(), doc?.DocArray());
        }

        // ===== void-методы =================================================

        public ApiBuilder Act(string name, Action fn, MethodDoc doc = null)
        {
            Define(name, Array.Empty<TypeRef>(), TypeRef.Void,
                (ref CallContext c) => fn(), doc);
            return this;
        }

        public ApiBuilder Act<T1>(string name, Action<T1> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>();
            Define(name, new[] { T.RefOf<T1>() }, TypeRef.Void,
                (ref CallContext c) => fn(r1(c.Host, c.Arg(0))), doc);
            return this;
        }

        public ApiBuilder Act<T1, T2>(string name, Action<T1, T2> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>(); var r2 = T.Reader<T2>();
            Define(name, new[] { T.RefOf<T1>(), T.RefOf<T2>() }, TypeRef.Void,
                (ref CallContext c) => fn(r1(c.Host, c.Arg(0)), r2(c.Host, c.Arg(1))), doc);
            return this;
        }

        public ApiBuilder Act<T1, T2, T3>(string name, Action<T1, T2, T3> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>(); var r2 = T.Reader<T2>(); var r3 = T.Reader<T3>();
            Define(name, new[] { T.RefOf<T1>(), T.RefOf<T2>(), T.RefOf<T3>() }, TypeRef.Void,
                (ref CallContext c) => fn(r1(c.Host, c.Arg(0)), r2(c.Host, c.Arg(1)), r3(c.Host, c.Arg(2))), doc);
            return this;
        }

        public ApiBuilder Act<T1, T2, T3, T4>(string name, Action<T1, T2, T3, T4> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>(); var r2 = T.Reader<T2>(); var r3 = T.Reader<T3>(); var r4 = T.Reader<T4>();
            Define(name, new[] { T.RefOf<T1>(), T.RefOf<T2>(), T.RefOf<T3>(), T.RefOf<T4>() }, TypeRef.Void,
                (ref CallContext c) => fn(r1(c.Host, c.Arg(0)), r2(c.Host, c.Arg(1)), r3(c.Host, c.Arg(2)), r4(c.Host, c.Arg(3))), doc);
            return this;
        }

        // ===== методы с результатом ========================================

        public ApiBuilder Fn<TR>(string name, Func<TR> fn, MethodDoc doc = null)
        {
            var w = T.Writer<TR>();
            Define(name, Array.Empty<TypeRef>(), T.RefOf<TR>(),
                (ref CallContext c) => c.Return(w(c.Host, fn())), doc);
            return this;
        }

        public ApiBuilder Fn<T1, TR>(string name, Func<T1, TR> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>(); var w = T.Writer<TR>();
            Define(name, new[] { T.RefOf<T1>() }, T.RefOf<TR>(),
                (ref CallContext c) => c.Return(w(c.Host, fn(r1(c.Host, c.Arg(0))))), doc);
            return this;
        }

        public ApiBuilder Fn<T1, T2, TR>(string name, Func<T1, T2, TR> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>(); var r2 = T.Reader<T2>(); var w = T.Writer<TR>();
            Define(name, new[] { T.RefOf<T1>(), T.RefOf<T2>() }, T.RefOf<TR>(),
                (ref CallContext c) => c.Return(w(c.Host,
                    fn(r1(c.Host, c.Arg(0)), r2(c.Host, c.Arg(1))))), doc);
            return this;
        }

        public ApiBuilder Fn<T1, T2, T3, TR>(string name, Func<T1, T2, T3, TR> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>(); var r2 = T.Reader<T2>(); var r3 = T.Reader<T3>(); var w = T.Writer<TR>();
            Define(name, new[] { T.RefOf<T1>(), T.RefOf<T2>(), T.RefOf<T3>() }, T.RefOf<TR>(),
                (ref CallContext c) => c.Return(w(c.Host,
                    fn(r1(c.Host, c.Arg(0)), r2(c.Host, c.Arg(1)), r3(c.Host, c.Arg(2))))), doc);
            return this;
        }

        public ApiBuilder Fn<T1, T2, T3, T4, TR>(string name, Func<T1, T2, T3, T4, TR> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>(); var r2 = T.Reader<T2>(); var r3 = T.Reader<T3>(); var r4 = T.Reader<T4>();
            var w = T.Writer<TR>();
            Define(name, new[] { T.RefOf<T1>(), T.RefOf<T2>(), T.RefOf<T3>(), T.RefOf<T4>() }, T.RefOf<TR>(),
                (ref CallContext c) => c.Return(w(c.Host,
                    fn(r1(c.Host, c.Arg(0)), r2(c.Host, c.Arg(1)), r3(c.Host, c.Arg(2)), r4(c.Host, c.Arg(3))))), doc);
            return this;
        }

        public ApiBuilder Act<T1, T2, T3, T4, T5>(string name, Action<T1, T2, T3, T4, T5> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>(); var r2 = T.Reader<T2>(); var r3 = T.Reader<T3>(); var r4 = T.Reader<T4>(); var r5 = T.Reader<T5>();
            Define(name, new[] { T.RefOf<T1>(), T.RefOf<T2>(), T.RefOf<T3>(), T.RefOf<T4>(), T.RefOf<T5>() }, TypeRef.Void,
                (ref CallContext c) => fn(r1(c.Host, c.Arg(0)), r2(c.Host, c.Arg(1)), r3(c.Host, c.Arg(2)), r4(c.Host, c.Arg(3)), r5(c.Host, c.Arg(4))), doc);
            return this;
        }

        public ApiBuilder Act<T1, T2, T3, T4, T5, T6>(string name, Action<T1, T2, T3, T4, T5, T6> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>(); var r2 = T.Reader<T2>(); var r3 = T.Reader<T3>(); var r4 = T.Reader<T4>(); var r5 = T.Reader<T5>(); var r6 = T.Reader<T6>();
            Define(name, new[] { T.RefOf<T1>(), T.RefOf<T2>(), T.RefOf<T3>(), T.RefOf<T4>(), T.RefOf<T5>(), T.RefOf<T6>() }, TypeRef.Void,
                (ref CallContext c) => fn(r1(c.Host, c.Arg(0)), r2(c.Host, c.Arg(1)), r3(c.Host, c.Arg(2)), r4(c.Host, c.Arg(3)), r5(c.Host, c.Arg(4)), r6(c.Host, c.Arg(5))), doc);
            return this;
        }

        public ApiBuilder Act<T1, T2, T3, T4, T5, T6, T7>(string name, Action<T1, T2, T3, T4, T5, T6, T7> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>(); var r2 = T.Reader<T2>(); var r3 = T.Reader<T3>(); var r4 = T.Reader<T4>(); var r5 = T.Reader<T5>(); var r6 = T.Reader<T6>(); var r7 = T.Reader<T7>();
            Define(name, new[] { T.RefOf<T1>(), T.RefOf<T2>(), T.RefOf<T3>(), T.RefOf<T4>(), T.RefOf<T5>(), T.RefOf<T6>(), T.RefOf<T7>() }, TypeRef.Void,
                (ref CallContext c) => fn(r1(c.Host, c.Arg(0)), r2(c.Host, c.Arg(1)), r3(c.Host, c.Arg(2)), r4(c.Host, c.Arg(3)), r5(c.Host, c.Arg(4)), r6(c.Host, c.Arg(5)), r7(c.Host, c.Arg(6))), doc);
            return this;
        }

        public ApiBuilder Act<T1, T2, T3, T4, T5, T6, T7, T8>(string name, Action<T1, T2, T3, T4, T5, T6, T7, T8> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>(); var r2 = T.Reader<T2>(); var r3 = T.Reader<T3>(); var r4 = T.Reader<T4>(); var r5 = T.Reader<T5>(); var r6 = T.Reader<T6>(); var r7 = T.Reader<T7>(); var r8 = T.Reader<T8>();
            Define(name, new[] { T.RefOf<T1>(), T.RefOf<T2>(), T.RefOf<T3>(), T.RefOf<T4>(), T.RefOf<T5>(), T.RefOf<T6>(), T.RefOf<T7>(), T.RefOf<T8>() }, TypeRef.Void,
                (ref CallContext c) => fn(r1(c.Host, c.Arg(0)), r2(c.Host, c.Arg(1)), r3(c.Host, c.Arg(2)), r4(c.Host, c.Arg(3)), r5(c.Host, c.Arg(4)), r6(c.Host, c.Arg(5)), r7(c.Host, c.Arg(6)), r8(c.Host, c.Arg(7))), doc);
            return this;
        }

        public ApiBuilder Fn<T1, T2, T3, T4, T5, TR>(string name, Func<T1, T2, T3, T4, T5, TR> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>(); var r2 = T.Reader<T2>(); var r3 = T.Reader<T3>(); var r4 = T.Reader<T4>(); var r5 = T.Reader<T5>(); var w = T.Writer<TR>();
            Define(name, new[] { T.RefOf<T1>(), T.RefOf<T2>(), T.RefOf<T3>(), T.RefOf<T4>(), T.RefOf<T5>() }, T.RefOf<TR>(),
                (ref CallContext c) => c.Return(w(c.Host,
                    fn(r1(c.Host, c.Arg(0)), r2(c.Host, c.Arg(1)), r3(c.Host, c.Arg(2)), r4(c.Host, c.Arg(3)), r5(c.Host, c.Arg(4))))), doc);
            return this;
        }

        public ApiBuilder Fn<T1, T2, T3, T4, T5, T6, TR>(string name, Func<T1, T2, T3, T4, T5, T6, TR> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>(); var r2 = T.Reader<T2>(); var r3 = T.Reader<T3>(); var r4 = T.Reader<T4>(); var r5 = T.Reader<T5>(); var r6 = T.Reader<T6>(); var w = T.Writer<TR>();
            Define(name, new[] { T.RefOf<T1>(), T.RefOf<T2>(), T.RefOf<T3>(), T.RefOf<T4>(), T.RefOf<T5>(), T.RefOf<T6>() }, T.RefOf<TR>(),
                (ref CallContext c) => c.Return(w(c.Host,
                    fn(r1(c.Host, c.Arg(0)), r2(c.Host, c.Arg(1)), r3(c.Host, c.Arg(2)), r4(c.Host, c.Arg(3)), r5(c.Host, c.Arg(4)), r6(c.Host, c.Arg(5))))), doc);
            return this;
        }

        public ApiBuilder Fn<T1, T2, T3, T4, T5, T6, T7, TR>(string name, Func<T1, T2, T3, T4, T5, T6, T7, TR> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>(); var r2 = T.Reader<T2>(); var r3 = T.Reader<T3>(); var r4 = T.Reader<T4>(); var r5 = T.Reader<T5>(); var r6 = T.Reader<T6>(); var r7 = T.Reader<T7>(); var w = T.Writer<TR>();
            Define(name, new[] { T.RefOf<T1>(), T.RefOf<T2>(), T.RefOf<T3>(), T.RefOf<T4>(), T.RefOf<T5>(), T.RefOf<T6>(), T.RefOf<T7>() }, T.RefOf<TR>(),
                (ref CallContext c) => c.Return(w(c.Host,
                    fn(r1(c.Host, c.Arg(0)), r2(c.Host, c.Arg(1)), r3(c.Host, c.Arg(2)), r4(c.Host, c.Arg(3)), r5(c.Host, c.Arg(4)), r6(c.Host, c.Arg(5)), r7(c.Host, c.Arg(6))))), doc);
            return this;
        }

        public ApiBuilder Fn<T1, T2, T3, T4, T5, T6, T7, T8, TR>(string name, Func<T1, T2, T3, T4, T5, T6, T7, T8, TR> fn, MethodDoc doc = null)
        {
            var r1 = T.Reader<T1>(); var r2 = T.Reader<T2>(); var r3 = T.Reader<T3>(); var r4 = T.Reader<T4>(); var r5 = T.Reader<T5>(); var r6 = T.Reader<T6>(); var r7 = T.Reader<T7>(); var r8 = T.Reader<T8>(); var w = T.Writer<TR>();
            Define(name, new[] { T.RefOf<T1>(), T.RefOf<T2>(), T.RefOf<T3>(), T.RefOf<T4>(), T.RefOf<T5>(), T.RefOf<T6>(), T.RefOf<T7>(), T.RefOf<T8>() }, T.RefOf<TR>(),
                (ref CallContext c) => c.Return(w(c.Host,
                    fn(r1(c.Host, c.Arg(0)), r2(c.Host, c.Arg(1)), r3(c.Host, c.Arg(2)), r4(c.Host, c.Arg(3)), r5(c.Host, c.Arg(4)), r6(c.Host, c.Arg(5)), r7(c.Host, c.Arg(6)), r8(c.Host, c.Arg(7))))), doc);
            return this;
        }
    }
}
