using System;
using Dsl.Runtime;

namespace Dsl.Hosting
{
    /// <summary>
    /// Типизированная ссылка на событие. Получается из HostBuilder.Event&lt;...&gt;
    /// и заменяет ручное кэширование int-id + цепочки AddFloat/AddEntity:
    ///
    /// <code>
    /// _evDied = host.Event&lt;Unit, Unit&gt;("OnUnitDied");
    /// // ...
    /// _evDied.Raise(Engine, target, attacker);
    /// </code>
    ///
    /// Писатели аргументов выбираются один раз при регистрации; Raise в горячем
    /// пути — это только вызовы готовых делегатов.
    /// </summary>
    public readonly struct EventRef
    {
        public readonly int Id;

        public EventRef(int id) { Id = id; }

        public void Raise(ScriptEngine engine)
        {
            engine.RaiseBegin();
            engine.RaiseCommit(Id);
        }
    }

    public readonly struct EventRef<T1>
    {
        public readonly int Id;
        private readonly VariantWriter<T1> _w1;

        public EventRef(int id, VariantWriter<T1> w1) { Id = id; _w1 = w1; }

        public void Raise(ScriptEngine engine, T1 a1)
        {
            GuardInit(_w1);
            engine.RaiseBegin();
            engine.RaiseAdd(_w1(engine, a1));
            engine.RaiseCommit(Id);
        }

        private static void GuardInit(object w)
        {
            if (w == null) throw NotInitialized();
        }

        internal static Exception NotInitialized() => new InvalidOperationException(
            "EventRef не инициализирован: получите его из HostBuilder.Event<...>() при регистрации.");
    }

    public readonly struct EventRef<T1, T2>
    {
        public readonly int Id;
        private readonly VariantWriter<T1> _w1;
        private readonly VariantWriter<T2> _w2;

        public EventRef(int id, VariantWriter<T1> w1, VariantWriter<T2> w2)
        {
            Id = id; _w1 = w1; _w2 = w2;
        }

        public void Raise(ScriptEngine engine, T1 a1, T2 a2)
        {
            if (_w1 == null) throw EventRef<T1>.NotInitialized();
            engine.RaiseBegin();
            engine.RaiseAdd(_w1(engine, a1));
            engine.RaiseAdd(_w2(engine, a2));
            engine.RaiseCommit(Id);
        }
    }

    public readonly struct EventRef<T1, T2, T3>
    {
        public readonly int Id;
        private readonly VariantWriter<T1> _w1;
        private readonly VariantWriter<T2> _w2;
        private readonly VariantWriter<T3> _w3;

        public EventRef(int id, VariantWriter<T1> w1, VariantWriter<T2> w2, VariantWriter<T3> w3)
        {
            Id = id; _w1 = w1; _w2 = w2; _w3 = w3;
        }

        public void Raise(ScriptEngine engine, T1 a1, T2 a2, T3 a3)
        {
            if (_w1 == null) throw EventRef<T1>.NotInitialized();
            engine.RaiseBegin();
            engine.RaiseAdd(_w1(engine, a1));
            engine.RaiseAdd(_w2(engine, a2));
            engine.RaiseAdd(_w3(engine, a3));
            engine.RaiseCommit(Id);
        }
    }

    public readonly struct EventRef<T1, T2, T3, T4>
    {
        public readonly int Id;
        private readonly VariantWriter<T1> _w1;
        private readonly VariantWriter<T2> _w2;
        private readonly VariantWriter<T3> _w3;
        private readonly VariantWriter<T4> _w4;

        public EventRef(int id, VariantWriter<T1> w1, VariantWriter<T2> w2,
                        VariantWriter<T3> w3, VariantWriter<T4> w4)
        {
            Id = id; _w1 = w1; _w2 = w2; _w3 = w3; _w4 = w4;
        }

        public void Raise(ScriptEngine engine, T1 a1, T2 a2, T3 a3, T4 a4)
        {
            if (_w1 == null) throw EventRef<T1>.NotInitialized();
            engine.RaiseBegin();
            engine.RaiseAdd(_w1(engine, a1));
            engine.RaiseAdd(_w2(engine, a2));
            engine.RaiseAdd(_w3(engine, a3));
            engine.RaiseAdd(_w4(engine, a4));
            engine.RaiseCommit(Id);
        }
    }
    public readonly struct EventRef<T1, T2, T3, T4, T5>
    {
        public readonly int Id;
        private readonly VariantWriter<T1> _w1;
        private readonly VariantWriter<T2> _w2;
        private readonly VariantWriter<T3> _w3;
        private readonly VariantWriter<T4> _w4;
        private readonly VariantWriter<T5> _w5;

        public EventRef(int id, VariantWriter<T1> w1, VariantWriter<T2> w2, VariantWriter<T3> w3, VariantWriter<T4> w4, VariantWriter<T5> w5)
        {
            Id = id; _w1 = w1; _w2 = w2; _w3 = w3; _w4 = w4; _w5 = w5;
        }

        public void Raise(ScriptEngine engine, T1 a1, T2 a2, T3 a3, T4 a4, T5 a5)
        {
            if (_w1 == null) throw EventRef<T1>.NotInitialized();
            engine.RaiseBegin();
            engine.RaiseAdd(_w1(engine, a1));
            engine.RaiseAdd(_w2(engine, a2));
            engine.RaiseAdd(_w3(engine, a3));
            engine.RaiseAdd(_w4(engine, a4));
            engine.RaiseAdd(_w5(engine, a5));
            engine.RaiseCommit(Id);
        }
    }

    public readonly struct EventRef<T1, T2, T3, T4, T5, T6>
    {
        public readonly int Id;
        private readonly VariantWriter<T1> _w1;
        private readonly VariantWriter<T2> _w2;
        private readonly VariantWriter<T3> _w3;
        private readonly VariantWriter<T4> _w4;
        private readonly VariantWriter<T5> _w5;
        private readonly VariantWriter<T6> _w6;

        public EventRef(int id, VariantWriter<T1> w1, VariantWriter<T2> w2, VariantWriter<T3> w3, VariantWriter<T4> w4, VariantWriter<T5> w5, VariantWriter<T6> w6)
        {
            Id = id; _w1 = w1; _w2 = w2; _w3 = w3; _w4 = w4; _w5 = w5; _w6 = w6;
        }

        public void Raise(ScriptEngine engine, T1 a1, T2 a2, T3 a3, T4 a4, T5 a5, T6 a6)
        {
            if (_w1 == null) throw EventRef<T1>.NotInitialized();
            engine.RaiseBegin();
            engine.RaiseAdd(_w1(engine, a1));
            engine.RaiseAdd(_w2(engine, a2));
            engine.RaiseAdd(_w3(engine, a3));
            engine.RaiseAdd(_w4(engine, a4));
            engine.RaiseAdd(_w5(engine, a5));
            engine.RaiseAdd(_w6(engine, a6));
            engine.RaiseCommit(Id);
        }
    }

    public readonly struct EventRef<T1, T2, T3, T4, T5, T6, T7>
    {
        public readonly int Id;
        private readonly VariantWriter<T1> _w1;
        private readonly VariantWriter<T2> _w2;
        private readonly VariantWriter<T3> _w3;
        private readonly VariantWriter<T4> _w4;
        private readonly VariantWriter<T5> _w5;
        private readonly VariantWriter<T6> _w6;
        private readonly VariantWriter<T7> _w7;

        public EventRef(int id, VariantWriter<T1> w1, VariantWriter<T2> w2, VariantWriter<T3> w3, VariantWriter<T4> w4, VariantWriter<T5> w5, VariantWriter<T6> w6, VariantWriter<T7> w7)
        {
            Id = id; _w1 = w1; _w2 = w2; _w3 = w3; _w4 = w4; _w5 = w5; _w6 = w6; _w7 = w7;
        }

        public void Raise(ScriptEngine engine, T1 a1, T2 a2, T3 a3, T4 a4, T5 a5, T6 a6, T7 a7)
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
            engine.RaiseCommit(Id);
        }
    }

    public readonly struct EventRef<T1, T2, T3, T4, T5, T6, T7, T8>
    {
        public readonly int Id;
        private readonly VariantWriter<T1> _w1;
        private readonly VariantWriter<T2> _w2;
        private readonly VariantWriter<T3> _w3;
        private readonly VariantWriter<T4> _w4;
        private readonly VariantWriter<T5> _w5;
        private readonly VariantWriter<T6> _w6;
        private readonly VariantWriter<T7> _w7;
        private readonly VariantWriter<T8> _w8;

        public EventRef(int id, VariantWriter<T1> w1, VariantWriter<T2> w2, VariantWriter<T3> w3, VariantWriter<T4> w4, VariantWriter<T5> w5, VariantWriter<T6> w6, VariantWriter<T7> w7, VariantWriter<T8> w8)
        {
            Id = id; _w1 = w1; _w2 = w2; _w3 = w3; _w4 = w4; _w5 = w5; _w6 = w6; _w7 = w7; _w8 = w8;
        }

        public void Raise(ScriptEngine engine, T1 a1, T2 a2, T3 a3, T4 a4, T5 a5, T6 a6, T7 a7, T8 a8)
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
            engine.RaiseCommit(Id);
        }
    }
}
