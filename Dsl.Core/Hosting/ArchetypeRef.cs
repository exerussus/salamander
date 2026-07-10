using System;
using System.Collections.Generic;
using Dsl.Runtime;

namespace Dsl.Hosting
{
    /// <summary>
    /// Строитель вида архетипа: host.Archetype("spell").Event&lt;Unit,Unit&gt;("OnCast").
    /// Скрипты пишут блоки «spell fireball { event OnCast(...) {...} }», а хост
    /// поднимает события адресно: onCast.Raise(engine, хэндл, args). Хэндл
    /// резолвится из строкового id ОДИН раз при загрузке контента
    /// (engine.ResolveArchetype) — на горячем пути только индексация, без строк.
    /// </summary>
    public sealed class ArchetypeBuilder
    {
        private readonly HostBuilder _host;
        public readonly int KindId;

        internal ArchetypeBuilder(HostBuilder host, int kindId)
        {
            _host = host;
            KindId = kindId;
        }

        /// <summary>Id, известные игре: непусто — компилятор ловит опечатки в id блоков (E0202).</summary>
        public ArchetypeBuilder KnownIds(IEnumerable<string> ids)
        {
            _host.Registry.SetArchetypeKnownIds(KindId, ids);
            return this;
        }

        public ArchetypeBuilder KnownIds(params string[] ids) => KnownIds((IEnumerable<string>)ids);

        private int Define(string name, MethodDoc doc, params Semantics.TypeRef[] ps)
        {
            if (doc != null && doc.Names.Count != ps.Length)
                throw new ArgumentException(
                    $"У события '{name}' {ps.Length} параметров, а в Sig.Doc описано {doc.Names.Count}.");
            return _host.Registry.DefineArchetypeEvent(
                KindId, name, ps, doc?.Summary,
                doc?.Names.ToArray(), doc?.Docs.ToArray());
        }

        public ArchEventRef Event(string name, MethodDoc doc = null) =>
            new ArchEventRef(KindId, Define(name, doc));

        public ArchEventRef<T1> Event<T1>(string name, MethodDoc doc = null) =>
            new ArchEventRef<T1>(KindId, Define(name, doc, _host.Types.RefOf<T1>()), _host.Types.Writer<T1>());

        public ArchEventRef<T1, T2> Event<T1, T2>(string name, MethodDoc doc = null) =>
            new ArchEventRef<T1, T2>(KindId, Define(name, doc, _host.Types.RefOf<T1>(), _host.Types.RefOf<T2>()), _host.Types.Writer<T1>(), _host.Types.Writer<T2>());

        public ArchEventRef<T1, T2, T3> Event<T1, T2, T3>(string name, MethodDoc doc = null) =>
            new ArchEventRef<T1, T2, T3>(KindId, Define(name, doc, _host.Types.RefOf<T1>(), _host.Types.RefOf<T2>(), _host.Types.RefOf<T3>()), _host.Types.Writer<T1>(), _host.Types.Writer<T2>(), _host.Types.Writer<T3>());

        public ArchEventRef<T1, T2, T3, T4> Event<T1, T2, T3, T4>(string name, MethodDoc doc = null) =>
            new ArchEventRef<T1, T2, T3, T4>(KindId, Define(name, doc, _host.Types.RefOf<T1>(), _host.Types.RefOf<T2>(), _host.Types.RefOf<T3>(), _host.Types.RefOf<T4>()), _host.Types.Writer<T1>(), _host.Types.Writer<T2>(), _host.Types.Writer<T3>(), _host.Types.Writer<T4>());

        public ArchEventRef<T1, T2, T3, T4, T5> Event<T1, T2, T3, T4, T5>(string name, MethodDoc doc = null) =>
            new ArchEventRef<T1, T2, T3, T4, T5>(KindId, Define(name, doc, _host.Types.RefOf<T1>(), _host.Types.RefOf<T2>(), _host.Types.RefOf<T3>(), _host.Types.RefOf<T4>(), _host.Types.RefOf<T5>()), _host.Types.Writer<T1>(), _host.Types.Writer<T2>(), _host.Types.Writer<T3>(), _host.Types.Writer<T4>(), _host.Types.Writer<T5>());

        public ArchEventRef<T1, T2, T3, T4, T5, T6> Event<T1, T2, T3, T4, T5, T6>(string name, MethodDoc doc = null) =>
            new ArchEventRef<T1, T2, T3, T4, T5, T6>(KindId, Define(name, doc, _host.Types.RefOf<T1>(), _host.Types.RefOf<T2>(), _host.Types.RefOf<T3>(), _host.Types.RefOf<T4>(), _host.Types.RefOf<T5>(), _host.Types.RefOf<T6>()), _host.Types.Writer<T1>(), _host.Types.Writer<T2>(), _host.Types.Writer<T3>(), _host.Types.Writer<T4>(), _host.Types.Writer<T5>(), _host.Types.Writer<T6>());

        public ArchEventRef<T1, T2, T3, T4, T5, T6, T7> Event<T1, T2, T3, T4, T5, T6, T7>(string name, MethodDoc doc = null) =>
            new ArchEventRef<T1, T2, T3, T4, T5, T6, T7>(KindId, Define(name, doc, _host.Types.RefOf<T1>(), _host.Types.RefOf<T2>(), _host.Types.RefOf<T3>(), _host.Types.RefOf<T4>(), _host.Types.RefOf<T5>(), _host.Types.RefOf<T6>(), _host.Types.RefOf<T7>()), _host.Types.Writer<T1>(), _host.Types.Writer<T2>(), _host.Types.Writer<T3>(), _host.Types.Writer<T4>(), _host.Types.Writer<T5>(), _host.Types.Writer<T6>(), _host.Types.Writer<T7>());

        public ArchEventRef<T1, T2, T3, T4, T5, T6, T7, T8> Event<T1, T2, T3, T4, T5, T6, T7, T8>(string name, MethodDoc doc = null) =>
            new ArchEventRef<T1, T2, T3, T4, T5, T6, T7, T8>(KindId, Define(name, doc, _host.Types.RefOf<T1>(), _host.Types.RefOf<T2>(), _host.Types.RefOf<T3>(), _host.Types.RefOf<T4>(), _host.Types.RefOf<T5>(), _host.Types.RefOf<T6>(), _host.Types.RefOf<T7>(), _host.Types.RefOf<T8>()), _host.Types.Writer<T1>(), _host.Types.Writer<T2>(), _host.Types.Writer<T3>(), _host.Types.Writer<T4>(), _host.Types.Writer<T5>(), _host.Types.Writer<T6>(), _host.Types.Writer<T7>(), _host.Types.Writer<T8>());

    }

    /// <summary>Типизированное событие вида архетипа; Raise адресный — по (вид, id).</summary>
    public readonly struct ArchEventRef
    {
        public readonly int KindId;
        public readonly int LocalId;


        public ArchEventRef(int kindId, int localId)
        {
            KindId = kindId; LocalId = localId; 
        }

        /// <summary>Горячий путь: хэндл из engine.ResolveArchetype — чистая индексация.</summary>
        public void Raise(ScriptEngine engine, int archetype)
        {
            engine.RaiseBegin();

            engine.RaiseCommitArch(KindId, archetype, LocalId);
        }

        /// <summary>Холодный путь: резолв строкового id при каждом вызове (один dict-lookup).</summary>
        public void Raise(ScriptEngine engine, string id)
            => Raise(engine, engine.ResolveArchetype(KindId, id));
    }

    /// <summary>Типизированное событие вида архетипа; Raise адресный — по (вид, id).</summary>
    public readonly struct ArchEventRef<T1>
    {
        public readonly int KindId;
        public readonly int LocalId;
        private readonly VariantWriter<T1> _w1;

        public ArchEventRef(int kindId, int localId, VariantWriter<T1> w1)
        {
            KindId = kindId; LocalId = localId; _w1 = w1;
        }

        /// <summary>Горячий путь: хэндл из engine.ResolveArchetype — чистая индексация.</summary>
        public void Raise(ScriptEngine engine, int archetype, T1 a1)
        {
            if (_w1 == null) throw EventRef<T1>.NotInitialized();
            engine.RaiseBegin();
            engine.RaiseAdd(_w1(engine, a1));
            engine.RaiseCommitArch(KindId, archetype, LocalId);
        }

        /// <summary>Холодный путь: резолв строкового id при каждом вызове (один dict-lookup).</summary>
        public void Raise(ScriptEngine engine, string id, T1 a1)
            => Raise(engine, engine.ResolveArchetype(KindId, id), a1);
    }

    /// <summary>Типизированное событие вида архетипа; Raise адресный — по (вид, id).</summary>
    public readonly struct ArchEventRef<T1, T2>
    {
        public readonly int KindId;
        public readonly int LocalId;
        private readonly VariantWriter<T1> _w1;
        private readonly VariantWriter<T2> _w2;

        public ArchEventRef(int kindId, int localId, VariantWriter<T1> w1, VariantWriter<T2> w2)
        {
            KindId = kindId; LocalId = localId; _w1 = w1; _w2 = w2;
        }

        /// <summary>Горячий путь: хэндл из engine.ResolveArchetype — чистая индексация.</summary>
        public void Raise(ScriptEngine engine, int archetype, T1 a1, T2 a2)
        {
            if (_w1 == null) throw EventRef<T1>.NotInitialized();
            engine.RaiseBegin();
            engine.RaiseAdd(_w1(engine, a1));
            engine.RaiseAdd(_w2(engine, a2));
            engine.RaiseCommitArch(KindId, archetype, LocalId);
        }

        /// <summary>Холодный путь: резолв строкового id при каждом вызове (один dict-lookup).</summary>
        public void Raise(ScriptEngine engine, string id, T1 a1, T2 a2)
            => Raise(engine, engine.ResolveArchetype(KindId, id), a1, a2);
    }

    /// <summary>Типизированное событие вида архетипа; Raise адресный — по (вид, id).</summary>
    public readonly struct ArchEventRef<T1, T2, T3>
    {
        public readonly int KindId;
        public readonly int LocalId;
        private readonly VariantWriter<T1> _w1;
        private readonly VariantWriter<T2> _w2;
        private readonly VariantWriter<T3> _w3;

        public ArchEventRef(int kindId, int localId, VariantWriter<T1> w1, VariantWriter<T2> w2, VariantWriter<T3> w3)
        {
            KindId = kindId; LocalId = localId; _w1 = w1; _w2 = w2; _w3 = w3;
        }

        /// <summary>Горячий путь: хэндл из engine.ResolveArchetype — чистая индексация.</summary>
        public void Raise(ScriptEngine engine, int archetype, T1 a1, T2 a2, T3 a3)
        {
            if (_w1 == null) throw EventRef<T1>.NotInitialized();
            engine.RaiseBegin();
            engine.RaiseAdd(_w1(engine, a1));
            engine.RaiseAdd(_w2(engine, a2));
            engine.RaiseAdd(_w3(engine, a3));
            engine.RaiseCommitArch(KindId, archetype, LocalId);
        }

        /// <summary>Холодный путь: резолв строкового id при каждом вызове (один dict-lookup).</summary>
        public void Raise(ScriptEngine engine, string id, T1 a1, T2 a2, T3 a3)
            => Raise(engine, engine.ResolveArchetype(KindId, id), a1, a2, a3);
    }

    /// <summary>Типизированное событие вида архетипа; Raise адресный — по (вид, id).</summary>
    public readonly struct ArchEventRef<T1, T2, T3, T4>
    {
        public readonly int KindId;
        public readonly int LocalId;
        private readonly VariantWriter<T1> _w1;
        private readonly VariantWriter<T2> _w2;
        private readonly VariantWriter<T3> _w3;
        private readonly VariantWriter<T4> _w4;

        public ArchEventRef(int kindId, int localId, VariantWriter<T1> w1, VariantWriter<T2> w2, VariantWriter<T3> w3, VariantWriter<T4> w4)
        {
            KindId = kindId; LocalId = localId; _w1 = w1; _w2 = w2; _w3 = w3; _w4 = w4;
        }

        /// <summary>Горячий путь: хэндл из engine.ResolveArchetype — чистая индексация.</summary>
        public void Raise(ScriptEngine engine, int archetype, T1 a1, T2 a2, T3 a3, T4 a4)
        {
            if (_w1 == null) throw EventRef<T1>.NotInitialized();
            engine.RaiseBegin();
            engine.RaiseAdd(_w1(engine, a1));
            engine.RaiseAdd(_w2(engine, a2));
            engine.RaiseAdd(_w3(engine, a3));
            engine.RaiseAdd(_w4(engine, a4));
            engine.RaiseCommitArch(KindId, archetype, LocalId);
        }

        /// <summary>Холодный путь: резолв строкового id при каждом вызове (один dict-lookup).</summary>
        public void Raise(ScriptEngine engine, string id, T1 a1, T2 a2, T3 a3, T4 a4)
            => Raise(engine, engine.ResolveArchetype(KindId, id), a1, a2, a3, a4);
    }

    /// <summary>Типизированное событие вида архетипа; Raise адресный — по (вид, id).</summary>
    public readonly struct ArchEventRef<T1, T2, T3, T4, T5>
    {
        public readonly int KindId;
        public readonly int LocalId;
        private readonly VariantWriter<T1> _w1;
        private readonly VariantWriter<T2> _w2;
        private readonly VariantWriter<T3> _w3;
        private readonly VariantWriter<T4> _w4;
        private readonly VariantWriter<T5> _w5;

        public ArchEventRef(int kindId, int localId, VariantWriter<T1> w1, VariantWriter<T2> w2, VariantWriter<T3> w3, VariantWriter<T4> w4, VariantWriter<T5> w5)
        {
            KindId = kindId; LocalId = localId; _w1 = w1; _w2 = w2; _w3 = w3; _w4 = w4; _w5 = w5;
        }

        /// <summary>Горячий путь: хэндл из engine.ResolveArchetype — чистая индексация.</summary>
        public void Raise(ScriptEngine engine, int archetype, T1 a1, T2 a2, T3 a3, T4 a4, T5 a5)
        {
            if (_w1 == null) throw EventRef<T1>.NotInitialized();
            engine.RaiseBegin();
            engine.RaiseAdd(_w1(engine, a1));
            engine.RaiseAdd(_w2(engine, a2));
            engine.RaiseAdd(_w3(engine, a3));
            engine.RaiseAdd(_w4(engine, a4));
            engine.RaiseAdd(_w5(engine, a5));
            engine.RaiseCommitArch(KindId, archetype, LocalId);
        }

        /// <summary>Холодный путь: резолв строкового id при каждом вызове (один dict-lookup).</summary>
        public void Raise(ScriptEngine engine, string id, T1 a1, T2 a2, T3 a3, T4 a4, T5 a5)
            => Raise(engine, engine.ResolveArchetype(KindId, id), a1, a2, a3, a4, a5);
    }

    /// <summary>Типизированное событие вида архетипа; Raise адресный — по (вид, id).</summary>
    public readonly struct ArchEventRef<T1, T2, T3, T4, T5, T6>
    {
        public readonly int KindId;
        public readonly int LocalId;
        private readonly VariantWriter<T1> _w1;
        private readonly VariantWriter<T2> _w2;
        private readonly VariantWriter<T3> _w3;
        private readonly VariantWriter<T4> _w4;
        private readonly VariantWriter<T5> _w5;
        private readonly VariantWriter<T6> _w6;

        public ArchEventRef(int kindId, int localId, VariantWriter<T1> w1, VariantWriter<T2> w2, VariantWriter<T3> w3, VariantWriter<T4> w4, VariantWriter<T5> w5, VariantWriter<T6> w6)
        {
            KindId = kindId; LocalId = localId; _w1 = w1; _w2 = w2; _w3 = w3; _w4 = w4; _w5 = w5; _w6 = w6;
        }

        /// <summary>Горячий путь: хэндл из engine.ResolveArchetype — чистая индексация.</summary>
        public void Raise(ScriptEngine engine, int archetype, T1 a1, T2 a2, T3 a3, T4 a4, T5 a5, T6 a6)
        {
            if (_w1 == null) throw EventRef<T1>.NotInitialized();
            engine.RaiseBegin();
            engine.RaiseAdd(_w1(engine, a1));
            engine.RaiseAdd(_w2(engine, a2));
            engine.RaiseAdd(_w3(engine, a3));
            engine.RaiseAdd(_w4(engine, a4));
            engine.RaiseAdd(_w5(engine, a5));
            engine.RaiseAdd(_w6(engine, a6));
            engine.RaiseCommitArch(KindId, archetype, LocalId);
        }

        /// <summary>Холодный путь: резолв строкового id при каждом вызове (один dict-lookup).</summary>
        public void Raise(ScriptEngine engine, string id, T1 a1, T2 a2, T3 a3, T4 a4, T5 a5, T6 a6)
            => Raise(engine, engine.ResolveArchetype(KindId, id), a1, a2, a3, a4, a5, a6);
    }

    /// <summary>Типизированное событие вида архетипа; Raise адресный — по (вид, id).</summary>
    public readonly struct ArchEventRef<T1, T2, T3, T4, T5, T6, T7>
    {
        public readonly int KindId;
        public readonly int LocalId;
        private readonly VariantWriter<T1> _w1;
        private readonly VariantWriter<T2> _w2;
        private readonly VariantWriter<T3> _w3;
        private readonly VariantWriter<T4> _w4;
        private readonly VariantWriter<T5> _w5;
        private readonly VariantWriter<T6> _w6;
        private readonly VariantWriter<T7> _w7;

        public ArchEventRef(int kindId, int localId, VariantWriter<T1> w1, VariantWriter<T2> w2, VariantWriter<T3> w3, VariantWriter<T4> w4, VariantWriter<T5> w5, VariantWriter<T6> w6, VariantWriter<T7> w7)
        {
            KindId = kindId; LocalId = localId; _w1 = w1; _w2 = w2; _w3 = w3; _w4 = w4; _w5 = w5; _w6 = w6; _w7 = w7;
        }

        /// <summary>Горячий путь: хэндл из engine.ResolveArchetype — чистая индексация.</summary>
        public void Raise(ScriptEngine engine, int archetype, T1 a1, T2 a2, T3 a3, T4 a4, T5 a5, T6 a6, T7 a7)
        {
            if (_w1 == null) throw EventRef<T1>.NotInitialized();
            engine.RaiseBegin();
            engine.RaiseAdd(_w1(engine, a1));
            engine.RaiseAdd(_w2(engine, a2));
            engine.RaiseAdd(_w3(engine, a3));
            engine.RaiseAdd(_w4(engine, a4));
            engine.RaiseAdd(_w5(engine, a5));
            engine.RaiseAdd(_w6(engine, a6));
            engine.RaiseAdd(_w7(engine, a7));
            engine.RaiseCommitArch(KindId, archetype, LocalId);
        }

        /// <summary>Холодный путь: резолв строкового id при каждом вызове (один dict-lookup).</summary>
        public void Raise(ScriptEngine engine, string id, T1 a1, T2 a2, T3 a3, T4 a4, T5 a5, T6 a6, T7 a7)
            => Raise(engine, engine.ResolveArchetype(KindId, id), a1, a2, a3, a4, a5, a6, a7);
    }

    /// <summary>Типизированное событие вида архетипа; Raise адресный — по (вид, id).</summary>
    public readonly struct ArchEventRef<T1, T2, T3, T4, T5, T6, T7, T8>
    {
        public readonly int KindId;
        public readonly int LocalId;
        private readonly VariantWriter<T1> _w1;
        private readonly VariantWriter<T2> _w2;
        private readonly VariantWriter<T3> _w3;
        private readonly VariantWriter<T4> _w4;
        private readonly VariantWriter<T5> _w5;
        private readonly VariantWriter<T6> _w6;
        private readonly VariantWriter<T7> _w7;
        private readonly VariantWriter<T8> _w8;

        public ArchEventRef(int kindId, int localId, VariantWriter<T1> w1, VariantWriter<T2> w2, VariantWriter<T3> w3, VariantWriter<T4> w4, VariantWriter<T5> w5, VariantWriter<T6> w6, VariantWriter<T7> w7, VariantWriter<T8> w8)
        {
            KindId = kindId; LocalId = localId; _w1 = w1; _w2 = w2; _w3 = w3; _w4 = w4; _w5 = w5; _w6 = w6; _w7 = w7; _w8 = w8;
        }

        /// <summary>Горячий путь: хэндл из engine.ResolveArchetype — чистая индексация.</summary>
        public void Raise(ScriptEngine engine, int archetype, T1 a1, T2 a2, T3 a3, T4 a4, T5 a5, T6 a6, T7 a7, T8 a8)
        {
            if (_w1 == null) throw EventRef<T1>.NotInitialized();
            engine.RaiseBegin();
            engine.RaiseAdd(_w1(engine, a1));
            engine.RaiseAdd(_w2(engine, a2));
            engine.RaiseAdd(_w3(engine, a3));
            engine.RaiseAdd(_w4(engine, a4));
            engine.RaiseAdd(_w5(engine, a5));
            engine.RaiseAdd(_w6(engine, a6));
            engine.RaiseAdd(_w7(engine, a7));
            engine.RaiseAdd(_w8(engine, a8));
            engine.RaiseCommitArch(KindId, archetype, LocalId);
        }

        /// <summary>Холодный путь: резолв строкового id при каждом вызове (один dict-lookup).</summary>
        public void Raise(ScriptEngine engine, string id, T1 a1, T2 a2, T3 a3, T4 a4, T5 a5, T6 a6, T7 a7, T8 a8)
            => Raise(engine, engine.ResolveArchetype(KindId, id), a1, a2, a3, a4, a5, a6, a7, a8);
    }

}
