using System.Collections.Generic;

namespace Dsl.Runtime
{
    public enum FiberState : byte
    {
        Free = 0,     // слот в пуле
        Ready,        // готов к исполнению (в очереди)
        Running,      // исполняется прямо сейчас
        Sleeping,     // ждёт таймер (wait N)
        YieldedTick,  // ждёт следующий тик (wait until)
    }

    /// <summary>Кадр вызова внутри файбера.</summary>
    public struct Frame
    {
        public int Func;  // индекс функции в программе
        public int Ip;    // указатель инструкции
        public int Base;  // база локалей на стеке значений
    }

    /// <summary>
    /// Кооперативный «поток» скрипта. Все данные исполнения — плоские
    /// (Variant[] + Frame[]), без managed-ссылок в стеке значений, поэтому
    /// тысячи живых файберов не нагружают GC. Буферы переживают возврат
    /// в пул (сбрасываются счётчики, ёмкость остаётся).
    /// </summary>
    public sealed class Fiber
    {
        public Variant[] Stack;
        public int Sp;

        public Frame[] Frames;
        public int FrameCount;

        public int Index;      // слот в пуле
        public int Version;    // растёт при возврате в пул — хэндлы протухают
        public FiberState State;

        public double WakeTime;           // для Sleeping
        public float PendingWaitSeconds;  // заполняет VM при op Wait
        public int TriggerId = -1;        // триггер-владелец (для Engine.KillAll)
        public bool KillRequested;        // самоубийство через Engine.Kill(self)
        public int BudgetHits;            // сколько тиков подряд файбер упирался в бюджет

        // контекст подписки listener (наследуется в spawn и вызовы функций)
        public int AttachIndex = -1;      // слот подписки в движке (-1 — нет)
        public Variant[] AttachFields;    // блок полей этой подписки
        public Variant AttachSelf;        // сущность-цель (для 'self')

        public Fiber(int index)
        {
            Index = index;
            Stack = new Variant[64];
            Frames = new Frame[8];
        }

        public void Reset()
        {
            Sp = 0;
            FrameCount = 0;
            TriggerId = -1;
            KillRequested = false;
            BudgetHits = 0;
            AttachIndex = -1;
            AttachFields = null;
            AttachSelf = Variant.Nil;
            PendingWaitSeconds = 0f;
            WakeTime = 0;
        }

        public void EnsureStack(int size)
        {
            if (size <= Stack.Length) return;
            int cap = Stack.Length * 2;
            while (cap < size) cap *= 2;
            System.Array.Resize(ref Stack, cap);
        }

        public void PushFrame(int func, int baseSlot)
        {
            if (FrameCount >= Frames.Length)
                System.Array.Resize(ref Frames, Frames.Length * 2);
            Frames[FrameCount].Func = func;
            Frames[FrameCount].Ip = 0;
            Frames[FrameCount].Base = baseSlot;
            FrameCount++;
        }

        public Variant Handle => Variant.Fiber(Index, Version);
    }

    /// <summary>Пул файберов. Возврат бампает версию — старые хэндлы мертвы.</summary>
    public sealed class FiberPool
    {
        private Fiber[] _fibers;
        private int _count;
        private readonly Stack<int> _free = new Stack<int>();

        public FiberPool(int capacity = 64)
        {
            _fibers = new Fiber[capacity];
        }

        /// <summary>Все когда-либо созданные слоты (для итерации; проверяйте State).</summary>
        public int SlotCount => _count;
        public Fiber Slot(int i) => _fibers[i];

        public Fiber Rent()
        {
            Fiber f;
            if (_free.Count > 0)
            {
                f = _fibers[_free.Pop()];
            }
            else
            {
                if (_count >= _fibers.Length)
                    System.Array.Resize(ref _fibers, _fibers.Length * 2);
                f = new Fiber(_count);
                _fibers[_count++] = f;
            }
            f.Reset();
            f.State = FiberState.Ready;
            return f;
        }

        public void Return(Fiber f)
        {
            if (f.State == FiberState.Free) return;
            f.State = FiberState.Free;
            f.Version++;         // все выданные хэндлы протухают
            _free.Push(f.Index);
        }

        /// <summary>Разрешить хэндл: null, если файбер уже умер/переиспользован.</summary>
        public Fiber ResolveHandle(Variant v)
        {
            if (v.Type != VariantType.Fiber) return null;
            int idx = v.Index;
            if ((uint)idx >= (uint)_count) return null;
            var f = _fibers[idx];
            if (f.Version != v.Version || f.State == FiberState.Free) return null;
            return f;
        }

        // упаковка (index, version) в long для очередей/кучи таймеров
        public static long Pack(Fiber f) => ((long)f.Version << 32) | (uint)f.Index;

        public Fiber ResolvePacked(long packed)
        {
            int idx = (int)(packed & 0xFFFFFFFF);
            int ver = (int)(packed >> 32);
            if ((uint)idx >= (uint)_count) return null;
            var f = _fibers[idx];
            if (f.Version != ver || f.State == FiberState.Free) return null;
            return f;
        }
    }
}
