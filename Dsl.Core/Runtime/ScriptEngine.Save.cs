using System;
using System.Collections.Generic;
using System.IO;
using Dsl.Codegen;

namespace Dsl.Runtime
{
    /// <summary>
    /// Мост между хэндлами сущностей и миром игры при сейве/загрузке.
    /// Entity-хэндлы указывают на живые C#-объекты — сериализовать их нельзя,
    /// поэтому при сейве движок спрашивает у хоста СТАБИЛЬНЫЙ id объекта
    /// (0 = «нет id», хэндл протухнет при загрузке), а при загрузке — объект
    /// по этому id (null = «не нашёлся» → протухший хэндл; скрипты уже умеют
    /// жить с «юнит умер, пока я ждал» через Engine.IsValid).
    /// </summary>
    public interface ISaveEntityResolver
    {
        long GetStableId(object entity);
        object ResolveStableId(long id);
    }

    /// <summary>
    /// Строковый хелпер поверх long-ядра: имена детерминированно хэшируются в
    /// 64 бита (FNV-1a). Сейв: конструктор с функцией «объект → имя». Загрузка:
    /// заранее зарегистрируйте живые объекты мира через RegisterForLoad.
    /// Коллизии 64-битного хэша на игровых объёмах астрономически маловероятны.
    /// </summary>
    public sealed class StringIdResolver : ISaveEntityResolver
    {
        private readonly Func<object, string> _stableName;
        private readonly Dictionary<long, object> _byId = new Dictionary<long, object>();

        public StringIdResolver(Func<object, string> stableName = null) => _stableName = stableName;

        public void RegisterForLoad(string name, object entity)
        {
            if (!string.IsNullOrEmpty(name) && entity != null) _byId[Hash(name)] = entity;
        }

        public long GetStableId(object entity)
        {
            var n = _stableName?.Invoke(entity);
            return string.IsNullOrEmpty(n) ? 0L : Hash(n);
        }

        public object ResolveStableId(long id) => _byId.TryGetValue(id, out var o) ? o : null;

        /// <summary>FNV-1a 64. Ноль зарезервирован под «нет id» — подменяется константой.</summary>
        public static long Hash(string s)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
            long r = unchecked((long)h);
            return r == 0 ? unchecked((long)0x9E3779B97F4A7C15UL) : r;
        }
    }

    /// <summary>Сейв не подходит к текущей программе/движку (формат, версия, отпечаток).</summary>
    public sealed class SaveStateException : Exception
    {
        public SaveStateException(string message) : base(message) { }
    }

    public sealed partial class ScriptEngine
    {
        private const int SaveMagic = 0x4C415353;  // "SSAL"
        private const byte SaveVersion = 1;
        private const int SaveEndMarker = 0x444E4553; // "SEND"

        // протухшие хэндлы: версии слотов никогда не бывают отрицательными,
        // поэтому version=-1 гарантированно невалиден в любом реестре
        private static readonly Variant StaleEntity = Variant.Entity(0, -1);
        private static readonly Variant StaleFiber = Variant.Fiber(0, -1);
        private static readonly Variant StaleSub = Variant.Sub(0, -1);

        // ===================================================================
        // Сохранение
        // ===================================================================

        /// <summary>
        /// Полный снапшот рантайма: статики, файберы (в т.ч. посреди wait),
        /// таймеры/очереди, подписки listener, динамические строки, коллекции,
        /// флаги триггеров/модулей, модельное время. Вызывать МЕЖДУ тиками
        /// (не из хостового метода, вызванного скриптом).
        /// </summary>
        public byte[] SaveState(ISaveEntityResolver resolver)
        {
            using var ms = new MemoryStream(64 * 1024);
            SaveState(ms, resolver);
            return ms.ToArray();
        }

        public void SaveState(Stream stream, ISaveEntityResolver resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (_prog == null) throw new InvalidOperationException("SaveState: программа не загружена.");
            if (_current != null)
                throw new InvalidOperationException(
                    "SaveState: нельзя сохраняться изнутри исполнения скрипта — вызовите между тиками.");

            var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            // --- заголовок ---
            w.Write(SaveMagic);
            w.Write(SaveVersion);
            w.Write(_prog.Fingerprint);
            w.Write(_time);

            // --- флаги ---
            w.Write(_triggerEnabled.Length);
            foreach (var b in _triggerEnabled) w.Write(b);
            w.Write(_moduleEnabled.Length);
            foreach (var b in _moduleEnabled) w.Write(b);

            // --- динамические строки (id -> значение; литералы стабильны по отпечатку) ---
            int dynStart = Strings.StaticCount;
            var dynIds = new List<int>();
            for (int id = dynStart; id < Strings.Count; id++)
                if (Strings.Get(id) != null) dynIds.Add(id);
            w.Write(dynIds.Count);
            foreach (var id in dynIds)
            {
                w.Write(id);
                w.Write(Strings.Get(id));
            }

            // --- стабильные списки для двухфазной записи (шапки → содержимое) ---
            var colls = new List<(VariantType kind, int id)>();
            Collections.CollectLive(colls);

            var atts = new List<Attachment>();
            for (int i = 0; i < _attachments.Count; i++)
                if (_attachments[i].Active) atts.Add(_attachments[i]);

            var fibs = new List<Fiber>();
            for (int i = 0; i < _fibers.SlotCount; i++)
            {
                var f = _fibers.Slot(i);
                if (f != null && f.State != FiberState.Free) fibs.Add(f);
            }

            // --- шапки коллекций ---
            w.Write(colls.Count);
            foreach (var (kind, id) in colls)
            {
                w.Write((byte)kind);
                w.Write(id);
                switch (kind)
                {
                    case VariantType.Array: w.Write(Collections.GetArrayData(id).Length); break;
                    case VariantType.List: Collections.GetListData(id, out _, out int lc); w.Write(lc); break;
                    default: w.Write(Collections.GetMapData(id).Count); break;
                }
            }

            // --- шапки подписок ---
            w.Write(atts.Count);
            foreach (var a in atts)
            {
                w.Write(a.ListenerId);
                long stable = 0;
                if (Entities.TryResolveObject(a.Target, out var obj)) stable = resolver.GetStableId(obj);
                w.Write(stable);
                w.Write(a.Index);
                w.Write(a.Version);
                w.Write(a.Fields?.Length ?? 0);
            }

            // --- шапки файберов (всё, кроме стека значений) ---
            w.Write(fibs.Count);
            foreach (var f in fibs)
            {
                w.Write(f.Index);
                w.Write(f.Version);
                w.Write((byte)f.State);
                w.Write(f.TriggerId);
                w.Write(f.BudgetHits);
                w.Write(f.WakeTime);
                w.Write(f.PendingWaitSeconds);
                w.Write(f.AttachIndex);
                w.Write(f.FrameCount);
                for (int i = 0; i < f.FrameCount; i++)
                {
                    w.Write(f.Frames[i].Func);
                    w.Write(f.Frames[i].Ip);
                    w.Write(f.Frames[i].Base);
                }
                // активные снапшоты for-in (переживают wait — значит и сейв)
                w.Write(f.IterDepth);
                for (int i = 0; i < f.IterDepth; i++) w.Write(f.IterCounts[i]);
            }

            // --- содержимое: статики ---
            w.Write(_statics.Length);
            foreach (var v in _statics) WriteVariant(w, v, resolver);

            // --- содержимое: коллекции (в порядке шапок) ---
            foreach (var (kind, id) in colls)
            {
                switch (kind)
                {
                    case VariantType.Array:
                    {
                        var arr = Collections.GetArrayData(id);
                        foreach (var v in arr) WriteVariant(w, v, resolver);
                        break;
                    }
                    case VariantType.List:
                    {
                        Collections.GetListData(id, out var buf, out int count);
                        for (int i = 0; i < count; i++) WriteVariant(w, buf[i], resolver);
                        break;
                    }
                    default:
                    {
                        foreach (var kv in Collections.GetMapData(id))
                        {
                            WriteVariant(w, kv.Key, resolver);
                            WriteVariant(w, kv.Value, resolver);
                        }
                        break;
                    }
                }
            }

            // --- содержимое: поля подписок ---
            foreach (var a in atts)
            {
                int n = a.Fields?.Length ?? 0;
                for (int i = 0; i < n; i++) WriteVariant(w, a.Fields[i], resolver);
            }

            // --- содержимое: стеки файберов ---
            foreach (var f in fibs)
            {
                w.Write(f.Sp);
                for (int i = 0; i < f.Sp; i++) WriteVariant(w, f.Stack[i], resolver);
                for (int d = 0; d < f.IterDepth; d++)
                    for (int i = 0; i < f.IterCounts[d]; i++)
                        WriteVariant(w, f.IterBufs[d][i], resolver);
            }

            // --- очереди/таймеры (хэндлы файберов как index+version) ---
            w.Write(_timerCount);
            for (int i = 0; i < _timerCount; i++)
            {
                w.Write(_timerTime[i]);
                w.Write((int)(_timerFiber[i] & 0xFFFFFFFF));
                w.Write((int)(_timerFiber[i] >> 32));
            }
            w.Write(_runQueue.Count);
            foreach (var packed in _runQueue)
            {
                w.Write((int)(packed & 0xFFFFFFFF));
                w.Write((int)(packed >> 32));
            }
            w.Write(_nextTick.Count);
            foreach (var packed in _nextTick)
            {
                w.Write((int)(packed & 0xFFFFFFFF));
                w.Write((int)(packed >> 32));
            }

            w.Write(SaveEndMarker);
            w.Flush();
        }

        private void WriteVariant(BinaryWriter w, in Variant v, ISaveEntityResolver resolver)
        {
            w.Write((byte)v.Type);
            switch (v.Type)
            {
                case VariantType.Nil: break;
                case VariantType.Bool: w.Write(v.AsBool); break;
                case VariantType.Int: w.Write(v.AsInt); break;
                case VariantType.Float: w.Write(v.AsFloat); break;
                case VariantType.Str: w.Write(v.StrId); break;
                case VariantType.Enum: w.Write(v.EnumTypeId); w.Write(v.EnumValue); break;

                case VariantType.Entity:
                {
                    long stable = 0;
                    if (Entities.TryResolveObject(v, out var obj)) stable = resolver.GetStableId(obj);
                    w.Write(stable); // 0 = протухнет при загрузке (объект умер / без id)
                    break;
                }

                case VariantType.Fiber:
                case VariantType.Sub:
                    w.Write(v.Index);
                    w.Write(v.Version);
                    break;

                default: // Array / List / Map
                    w.Write(v.CollId);
                    break;
            }
        }

        // ===================================================================
        // Загрузка
        // ===================================================================

        /// <summary>Контекст ремапа старых хэндлов сейва на новые.</summary>
        private sealed class LoadCtx
        {
            public ISaveEntityResolver Resolver;
            public Dictionary<int, int> StrMap = new Dictionary<int, int>();
            public Dictionary<int, int> ArrMap = new Dictionary<int, int>();
            public Dictionary<int, int> ListMap = new Dictionary<int, int>();
            public Dictionary<int, int> MapMap = new Dictionary<int, int>();
            public Dictionary<long, Variant> FiberMap = new Dictionary<long, Variant>(); // oldPacked -> новый хэндл
            public Dictionary<long, Variant> SubMap = new Dictionary<long, Variant>();   // oldPacked -> новый хэндл
        }

        /// <summary>
        /// Восстановить состояние из снапшота. Требования: LoadProgram уже вызван
        /// с ТОЙ ЖЕ программой (отпечаток сверяется — иначе SaveStateException),
        /// мир пересоздан и resolver готов отдавать объекты по стабильным id.
        /// Текущее состояние рантайма полностью сбрасывается.
        /// </summary>
        public void LoadState(byte[] data, ISaveEntityResolver resolver)
        {
            using var ms = new MemoryStream(data, writable: false);
            LoadState(ms, resolver);
        }

        public void LoadState(Stream stream, ISaveEntityResolver resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (_prog == null) throw new InvalidOperationException("LoadState: сначала загрузите программу (LoadProgram).");
            if (_current != null)
                throw new InvalidOperationException("LoadState: нельзя загружаться изнутри исполнения скрипта.");

            var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            // --- заголовок ---
            if (r.ReadInt32() != SaveMagic)
                throw new SaveStateException("Это не сейв Salamander (неверная сигнатура файла).");
            byte ver = r.ReadByte();
            if (ver != SaveVersion)
                throw new SaveStateException($"Версия формата сейва {ver} не поддерживается (движок понимает {SaveVersion}).");
            ulong fp = r.ReadUInt64();
            if (fp != _prog.Fingerprint)
                throw new SaveStateException(
                    "Сейв сделан с другой версией скриптов — загрузка невозможна. " +
                    "Файберы хранят позиции в байткоде и валидны только против того же кода.");
            double savedTime = r.ReadDouble();

            // --- полный сброс текущего рантайма ---
            KillAllFibers();
            _runQueue.Clear();
            _nextTick.Clear();
            _timerCount = 0;
            Collections.Clear();
            _subsByEntity.Clear();
            _freeAttachments.Clear();
            for (int i = _attachments.Count - 1; i >= 0; i--)
            {
                var a = _attachments[i];
                a.Active = false; a.Finalizing = false; a.Fields = null; a.ListenerId = -1; a.Version++;
                _freeAttachments.Push(i);
            }
            _liveAttachments = 0;
            _time = savedTime;

            var ctx = new LoadCtx { Resolver = resolver };

            // --- флаги ---
            int tCount = r.ReadInt32();
            if (tCount != _triggerEnabled.Length)
                throw new SaveStateException("Сейв не согласован с программой (число триггеров).");
            for (int i = 0; i < tCount; i++) _triggerEnabled[i] = r.ReadBoolean();
            int mCount = r.ReadInt32();
            if (mCount != _moduleEnabled.Length)
                throw new SaveStateException("Сейв не согласован с программой (число модулей).");
            for (int i = 0; i < mCount; i++) _moduleEnabled[i] = r.ReadBoolean();

            // --- динамические строки ---
            int strCount = r.ReadInt32();
            for (int i = 0; i < strCount; i++)
            {
                int oldId = r.ReadInt32();
                string s = r.ReadString();
                ctx.StrMap[oldId] = Strings.Intern(s);
            }

            // --- шапки коллекций: создаём пустые, строим ремап ---
            int collCount = r.ReadInt32();
            var collOrder = new (VariantType kind, int newId, int len)[collCount];
            for (int i = 0; i < collCount; i++)
            {
                var kind = (VariantType)r.ReadByte();
                int oldId = r.ReadInt32();
                int len = r.ReadInt32();
                Variant h;
                switch (kind)
                {
                    case VariantType.Array: h = Collections.NewArray(len); ctx.ArrMap[oldId] = h.CollId; break;
                    case VariantType.List: h = Collections.NewList(); ctx.ListMap[oldId] = h.CollId; break;
                    default: h = Collections.NewMap(); ctx.MapMap[oldId] = h.CollId; break;
                }
                collOrder[i] = (kind, h.CollId, len);
            }

            // --- шапки подписок: резолвим цель, недоступные — дропаем целиком ---
            int attCount = r.ReadInt32();
            var attOrder = new (Attachment att, int fieldCount, bool dropped)[attCount];
            var attByOldIndex = new Dictionary<int, Attachment>();
            for (int i = 0; i < attCount; i++)
            {
                int lid = r.ReadInt32();
                long stable = r.ReadInt64();
                int oldIdx = r.ReadInt32();
                int oldVer = r.ReadInt32();
                int fieldCount = r.ReadInt32();

                object obj = stable != 0 ? resolver.ResolveStableId(stable) : null;
                if (obj == null || (uint)lid >= (uint)_prog.Listeners.Length)
                {
                    // цель исчезла из мира — подписка не восстанавливается
                    // (OnUnsubscribe не зовём: в ЭТОМ мире она и не жила)
                    attOrder[i] = (null, fieldCount, true);
                    continue;
                }

                var info = _prog.Listeners[lid];
                var target = Entities.Register(obj);

                Attachment att;
                if (_freeAttachments.Count > 0) att = _attachments[_freeAttachments.Pop()];
                else { att = new Attachment { Index = _attachments.Count }; _attachments.Add(att); }
                att.ListenerId = lid;
                att.Target = target;
                att.TargetKey = PackEntity(target);
                att.Active = true;
                att.Finalizing = false;
                var pool = _attachFieldPools[lid];
                att.Fields = pool.Count > 0 ? pool.Pop()
                           : info.FieldCount > 0 ? new Variant[info.FieldCount]
                           : Array.Empty<Variant>();

                if (!_subsByEntity.TryGetValue(att.TargetKey, out var list))
                    _subsByEntity[att.TargetKey] = list = new List<int>();
                list.Add(att.Index);
                _liveAttachments++;

                ctx.SubMap[Pack(oldIdx, oldVer)] = Variant.Sub(att.Index, att.Version);
                attByOldIndex[oldIdx] = att;
                attOrder[i] = (att, fieldCount, false);
            }

            // --- шапки файберов: материализуем, строим ремап ---
            int fibCount = r.ReadInt32();
            var fibOrder = new (Fiber f, bool dropped, int[] iterCounts)[fibCount];
            for (int i = 0; i < fibCount; i++)
            {
                int oldIdx = r.ReadInt32();
                int oldVer = r.ReadInt32();
                var state = (FiberState)r.ReadByte();
                int triggerId = r.ReadInt32();
                int budgetHits = r.ReadInt32();
                double wakeTime = r.ReadDouble();
                float pendingWait = r.ReadSingle();
                int attachOld = r.ReadInt32();
                int frameCount = r.ReadInt32();

                Attachment att = null;
                bool dropped = false;
                if (attachOld >= 0 && !attByOldIndex.TryGetValue(attachOld, out att))
                    dropped = true; // подписка не восстановилась (цель исчезла) → файбер не жилец

                if (dropped)
                {
                    // кадры и шапку итераций прочитать и выбросить (выравнивание потока)
                    for (int k = 0; k < frameCount; k++) { r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); }
                    int dDepth = r.ReadInt32();
                    var dCounts = new int[dDepth];
                    for (int k = 0; k < dDepth; k++) dCounts[k] = r.ReadInt32();
                    fibOrder[i] = (null, true, dCounts);
                    continue;
                }

                var f = _fibers.Rent();
                f.State = state;
                f.TriggerId = triggerId;
                f.BudgetHits = budgetHits;
                f.WakeTime = wakeTime;
                f.PendingWaitSeconds = pendingWait;
                if (att != null)
                {
                    f.AttachIndex = att.Index;
                    f.AttachFields = att.Fields;
                    f.AttachSelf = att.Target;
                }
                if (f.Frames.Length < frameCount) Array.Resize(ref f.Frames, Math.Max(frameCount, f.Frames.Length * 2));
                f.FrameCount = frameCount;
                for (int k = 0; k < frameCount; k++)
                {
                    f.Frames[k].Func = r.ReadInt32();
                    f.Frames[k].Ip = r.ReadInt32();
                    f.Frames[k].Base = r.ReadInt32();
                }

                int iterDepth = r.ReadInt32();
                var iterCounts = new int[iterDepth];
                for (int k = 0; k < iterDepth; k++) iterCounts[k] = r.ReadInt32();

                ctx.FiberMap[Pack(oldIdx, oldVer)] = f.Handle;
                fibOrder[i] = (f, false, iterCounts);
            }

            // --- содержимое: статики ---
            int stCount = r.ReadInt32();
            if (stCount != _statics.Length)
                throw new SaveStateException("Сейв не согласован с программой (число статических полей).");
            for (int i = 0; i < stCount; i++) _statics[i] = ReadVariant(r, ctx);

            // --- содержимое: коллекции ---
            foreach (var (kind, newId, len) in collOrder)
            {
                switch (kind)
                {
                    case VariantType.Array:
                    {
                        var arr = Collections.GetArrayData(newId);
                        for (int i = 0; i < len; i++) arr[i] = ReadVariant(r, ctx);
                        break;
                    }
                    case VariantType.List:
                        for (int i = 0; i < len; i++) Collections.LoadListAdd(newId, ReadVariant(r, ctx));
                        break;
                    default:
                        for (int i = 0; i < len; i++)
                        {
                            var k = ReadVariant(r, ctx);
                            var v = ReadVariant(r, ctx);
                            Collections.LoadMapSet(newId, k, v);
                        }
                        break;
                }
            }

            // --- содержимое: поля подписок (дропнутые — читаем и выбрасываем) ---
            foreach (var (att, fieldCount, dropped) in attOrder)
            {
                for (int i = 0; i < fieldCount; i++)
                {
                    var v = ReadVariant(r, ctx);
                    if (!dropped && att.Fields != null && i < att.Fields.Length) att.Fields[i] = v;
                }
            }

            // --- содержимое: стеки файберов ---
            foreach (var (f, dropped, iterCounts) in fibOrder)
            {
                int sp = r.ReadInt32();
                if (dropped)
                {
                    for (int i = 0; i < sp; i++) ReadVariant(r, ctx);
                    foreach (var cnt in iterCounts)
                        for (int i = 0; i < cnt; i++) ReadVariant(r, ctx);
                    continue;
                }
                f.EnsureStack(sp);
                for (int i = 0; i < sp; i++) f.Stack[i] = ReadVariant(r, ctx);
                f.Sp = sp;
                for (int d = 0; d < iterCounts.Length; d++)
                {
                    f.EnsureIter(d, iterCounts[d]);
                    for (int i = 0; i < iterCounts[d]; i++) f.IterBufs[d][i] = ReadVariant(r, ctx);
                    f.IterCounts[d] = iterCounts[d];
                }
                f.IterDepth = iterCounts.Length;
            }

            // --- очереди/таймеры ---
            int timerCount = r.ReadInt32();
            for (int i = 0; i < timerCount; i++)
            {
                double t = r.ReadDouble();
                var h = RemapFiber(r, ctx);
                var tf = _fibers.ResolveHandle(h);
                if (tf != null) PushTimer(t, FiberPool.Pack(tf));
            }
            int rqCount = r.ReadInt32();
            for (int i = 0; i < rqCount; i++)
            {
                var h = RemapFiber(r, ctx);
                var f = _fibers.ResolveHandle(h);
                if (f != null) _runQueue.Enqueue(FiberPool.Pack(f));
            }
            int ntCount = r.ReadInt32();
            for (int i = 0; i < ntCount; i++)
            {
                var h = RemapFiber(r, ctx);
                var f = _fibers.ResolveHandle(h);
                if (f != null) _nextTick.Add(FiberPool.Pack(f));
            }

            if (r.ReadInt32() != SaveEndMarker)
                throw new SaveStateException("Сейв повреждён (нет завершающего маркера).");
        }

        private static long Pack(int index, int version) => ((long)version << 32) | (uint)index;

        private Variant RemapFiber(BinaryReader r, LoadCtx ctx)
        {
            int idx = r.ReadInt32();
            int ver = r.ReadInt32();
            return ctx.FiberMap.TryGetValue(Pack(idx, ver), out var h) ? h : StaleFiber;
        }

        private Variant ReadVariant(BinaryReader r, LoadCtx ctx)
        {
            var type = (VariantType)r.ReadByte();
            switch (type)
            {
                case VariantType.Nil: return Variant.Nil;
                case VariantType.Bool: return Variant.Bool(r.ReadBoolean());
                case VariantType.Int: return Variant.Int(r.ReadInt32());
                case VariantType.Float: return Variant.Float(r.ReadSingle());

                case VariantType.Str:
                {
                    int id = r.ReadInt32();
                    if (id < Strings.StaticCount) return Variant.Str(id); // литерал — стабилен
                    return ctx.StrMap.TryGetValue(id, out var nid) ? Variant.Str(nid) : Variant.Str(Strings.Intern(""));
                }

                case VariantType.Enum:
                {
                    int typeId = r.ReadInt32();
                    int value = r.ReadInt32();
                    return Variant.Enum(typeId, value);
                }

                case VariantType.Entity:
                {
                    long stable = r.ReadInt64();
                    if (stable == 0) return StaleEntity;
                    var obj = ctx.Resolver.ResolveStableId(stable);
                    return obj == null ? StaleEntity : Entities.Register(obj);
                }

                case VariantType.Fiber:
                {
                    int idx = r.ReadInt32();
                    int ver = r.ReadInt32();
                    return ctx.FiberMap.TryGetValue(Pack(idx, ver), out var h) ? h : StaleFiber;
                }

                case VariantType.Sub:
                {
                    int idx = r.ReadInt32();
                    int ver = r.ReadInt32();
                    return ctx.SubMap.TryGetValue(Pack(idx, ver), out var h) ? h : StaleSub;
                }

                case VariantType.Array:
                {
                    int id = r.ReadInt32();
                    return ctx.ArrMap.TryGetValue(id, out var nid) ? Variant.Coll(VariantType.Array, nid) : Variant.Nil;
                }
                case VariantType.List:
                {
                    int id = r.ReadInt32();
                    return ctx.ListMap.TryGetValue(id, out var nid) ? Variant.Coll(VariantType.List, nid) : Variant.Nil;
                }
                default:
                {
                    int id = r.ReadInt32();
                    return ctx.MapMap.TryGetValue(id, out var nid) ? Variant.Coll(VariantType.Map, nid) : Variant.Nil;
                }
            }
        }
    }
}
