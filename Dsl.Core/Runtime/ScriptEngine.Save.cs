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

    /// <summary>Сейв не подходит к текущему движку (формат, версия) или повреждён.</summary>
    public sealed class SaveStateException : Exception
    {
        public SaveStateException(string message) : base(message) { }
    }

    /// <summary>
    /// Что именно не совпало при загрузке. Пустой отчёт (ничего не потеряно,
    /// FibersRestored == true) — это загрузка «как было».
    ///
    /// Смысл разделения: сейв содержит ДАННЫЕ (статики, коллекции, строки, поля
    /// подписок, флаги, время) и ПРОДОЛЖЕНИЯ (файберы: функция + позиция в
    /// байткоде + стек). Данные адресуются именами и переживают правку скриптов.
    /// Продолжения — это сырой счётчик команд, он валиден только против того же
    /// байткода, и мигрировать его нельзя в принципе.
    /// </summary>
    public sealed class SaveMigrationReport
    {
        /// <summary>false — скрипты изменились, файберы не восстановлены (данные — да).</summary>
        public bool FibersRestored = true;

        /// <summary>Сколько файберов не вернулось (включая дропнутые из-за исчезнувшей цели подписки).</summary>
        public int DroppedFibers;

        /// <summary>Имена триггеров, чьи файберы не вернулись (без повторов).</summary>
        public readonly List<string> DroppedFiberTriggers = new List<string>();

        /// <summary>Статики, которые были в сейве, но исчезли из программы — значение потеряно.</summary>
        public readonly List<string> MissingStatics = new List<string>();

        /// <summary>Статики, появившиеся в программе после сейва — остались со своим инициализатором.</summary>
        public readonly List<string> NewStatics = new List<string>();

        /// <summary>Подписки, которые не восстановились (цель исчезла или listener удалён).</summary>
        public int DroppedSubscriptions;

        /// <summary>Ничего не потеряно.</summary>
        public bool IsClean =>
            FibersRestored && DroppedFibers == 0 && MissingStatics.Count == 0
            && NewStatics.Count == 0 && DroppedSubscriptions == 0;

        public override string ToString()
        {
            if (IsClean) return "сейв восстановлен полностью";
            var sb = new System.Text.StringBuilder("сейв восстановлен с потерями:");
            if (!FibersRestored)
                sb.Append($" скрипты изменились, файберы не восстановлены ({DroppedFibers});");
            else if (DroppedFibers > 0)
                sb.Append($" файберов не вернулось: {DroppedFibers};");
            if (DroppedFiberTriggers.Count > 0)
                sb.Append(" триггеры: ").Append(string.Join(", ", DroppedFiberTriggers)).Append(';');
            if (MissingStatics.Count > 0)
                sb.Append($" полей исчезло: {MissingStatics.Count};");
            if (NewStatics.Count > 0)
                sb.Append($" новых полей: {NewStatics.Count};");
            if (DroppedSubscriptions > 0)
                sb.Append($" подписок не вернулось: {DroppedSubscriptions};");
            return sb.ToString();
        }
    }

    public sealed partial class ScriptEngine
    {
        private const int SaveMagic = 0x4C415353;  // "SSAL"
        private const byte SaveVersion = 2;
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

            // сборка перед снапшотом: иначе в сейв уедет мусор, который просто
            // ещё не подмели, и файл будет зависеть от того, когда его сделали
            Collect();

            var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            // --- заголовок ---
            w.Write(SaveMagic);
            w.Write(SaveVersion);
            w.Write(_prog.Fingerprint);   // отпечаток КОДА: гейт только для файберов
            w.Write(_time);

            // --- флаги ПО ИМЕНАМ: добавление триггера или модуля больше не ломает сейв ---
            w.Write(_prog.Triggers.Length);
            for (int i = 0; i < _prog.Triggers.Length; i++)
            {
                w.Write(_prog.Triggers[i].Name ?? "");
                w.Write(_triggerEnabled[i]);
            }
            w.Write(_prog.Modules.Length);
            for (int i = 0; i < _prog.Modules.Length; i++)
            {
                w.Write(_prog.Modules[i] ?? "");
                w.Write(_moduleEnabled[i]);
            }

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

            // --- шапки подписок: listener по ИМЕНИ, поля по ИМЕНАМ ---
            w.Write(atts.Count);
            foreach (var a in atts)
            {
                var info = _prog.Listeners[a.ListenerId];
                w.Write(info.Name ?? "");
                long stable = 0;
                if (Entities.TryResolveObject(a.Target, out var obj)) stable = resolver.GetStableId(obj);
                w.Write(stable);
                w.Write(a.Index);
                w.Write(a.Version);

                var names = info.FieldNames ?? Array.Empty<string>();
                int n = a.Fields?.Length ?? 0;
                if (n > names.Length) n = names.Length;
                w.Write(n);
                for (int i = 0; i < n; i++) w.Write(names[i] ?? "");
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

            // --- содержимое: статики ПО КЛЮЧАМ (модуль-независимым) ---
            var keys = _prog.StaticKeys ?? Array.Empty<string>();
            int stCount = Math.Min(_statics.Length, keys.Length);
            w.Write(stCount);
            for (int i = 0; i < stCount; i++)
            {
                w.Write(keys[i] ?? "");
                WriteVariant(w, _statics[i], resolver);
            }

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
                var info = _prog.Listeners[a.ListenerId];
                var names = info.FieldNames ?? Array.Empty<string>();
                int n = a.Fields?.Length ?? 0;
                if (n > names.Length) n = names.Length;
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
                case VariantType.Double: w.Write(v.AsDouble); break;
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
                    // версия слота при загрузке будет новая, поэтому едет только id;
                    // мёртвый хэндл пишем как -1, чтобы он не «попал» в чужой слот
                    w.Write(Collections.IsAlive(v) ? v.CollId : -1);
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
            // коллекции: старый id -> НОВЫЙ ХЭНДЛ целиком (id + актуальная версия слота)
            public Dictionary<int, Variant> ArrMap = new Dictionary<int, Variant>();
            public Dictionary<int, Variant> ListMap = new Dictionary<int, Variant>();
            public Dictionary<int, Variant> MapMap = new Dictionary<int, Variant>();
            public Dictionary<long, Variant> FiberMap = new Dictionary<long, Variant>(); // oldPacked -> новый хэндл
            public Dictionary<long, Variant> SubMap = new Dictionary<long, Variant>();   // oldPacked -> новый хэндл
        }

        /// <summary>
        /// Восстановить состояние из снапшота. Требования: LoadProgram уже вызван,
        /// мир пересоздан и resolver готов отдавать объекты по стабильным id.
        /// Текущее состояние рантайма полностью сбрасывается.
        ///
        /// Скрипты МОГЛИ измениться с момента сейва: данные восстанавливаются по
        /// именам, а файберы — только если байткод совпал. Что потерялось,
        /// написано в возвращённом отчёте.
        /// </summary>
        public SaveMigrationReport LoadState(byte[] data, ISaveEntityResolver resolver)
        {
            using var ms = new MemoryStream(data, writable: false);
            return LoadState(ms, resolver);
        }

        public SaveMigrationReport LoadState(Stream stream, ISaveEntityResolver resolver)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (_prog == null) throw new InvalidOperationException("LoadState: сначала загрузите программу (LoadProgram).");
            if (_current != null)
                throw new InvalidOperationException("LoadState: нельзя загружаться изнутри исполнения скрипта.");

            try
            {
                return LoadStateCore(stream, resolver);
            }
            catch (SaveStateException)
            {
                ResetRuntimeAfterFailedLoad();
                throw;
            }
            catch (Exception ex)
            {
                // Сейв — НЕДОВЕРЕННЫЕ данные: файл на диске игрока, облако, сеть.
                // Отпечаток ловит только несовпадение версии скриптов, но не порчу
                // и не подделку. Поэтому любой сбой разбора обязан стать
                // SaveStateException, а движок — остаться в заведомо пустом,
                // а не в полуразобранном состоянии.
                ResetRuntimeAfterFailedLoad();
                throw new SaveStateException("Сейв повреждён или несовместим: " + ex.Message);
            }
        }

        /// <summary>Привести рантайм к заведомо пустому состоянию после неудачной загрузки.</summary>
        private void ResetRuntimeAfterFailedLoad()
        {
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
        }

        /// <summary>Счётчик из сейва с проверкой диапазона: без неё крафт даёт OutOfMemory на аллокации.</summary>
        private static int ReadCount(BinaryReader r, int max, string what)
        {
            int n = r.ReadInt32();
            if (n < 0 || n > max)
                throw new SaveStateException($"Сейв повреждён: {what} = {n} вне диапазона 0..{max}.");
            return n;
        }

        private static double ReadFiniteTime(BinaryReader r, string what)
        {
            double t = r.ReadDouble();
            if (double.IsNaN(t) || double.IsInfinity(t))
                throw new SaveStateException($"Сейв повреждён: {what} не является конечным числом.");
            return t;
        }

        private SaveMigrationReport LoadStateCore(Stream stream, ISaveEntityResolver resolver)
        {
            var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            var report = new SaveMigrationReport();

            // --- заголовок ---
            if (r.ReadInt32() != SaveMagic)
                throw new SaveStateException("Это не сейв Salamander (неверная сигнатура файла).");
            byte ver = r.ReadByte();
            if (ver != SaveVersion)
                throw new SaveStateException(
                    $"Версия формата сейва {ver} не поддерживается (движок понимает {SaveVersion}).");

            ulong savedCodeFp = r.ReadUInt64();
            // Отпечаток кода гейтит ТОЛЬКО файберы: они хранят (функция, позиция в
            // байткоде), и против другого кода это мусор. Данные адресуются именами
            // и переживают правку скриптов.
            bool codeMatches = savedCodeFp == _prog.Fingerprint;
            report.FibersRestored = codeMatches;

            double savedTime = ReadFiniteTime(r, "модельное время");

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

            // Прогон <init> ДО чтения: статики, которых в сейве нет (новое поле в
            // новой версии скриптов), обязаны получить свой инициализатор, а не
            // остаться с хэндлами на коллекции, снесённые Collections.Clear().
            RunInit();

            var ctx = new LoadCtx { Resolver = resolver };

            // --- флаги по именам ---
            int tCount = ReadCount(r, 1 << 20, "число триггеров");
            var trigFlags = new Dictionary<string, bool>(tCount, StringComparer.Ordinal);
            for (int i = 0; i < tCount; i++)
            {
                string name = r.ReadString();
                bool on = r.ReadBoolean();
                trigFlags[name] = on;
            }
            for (int i = 0; i < _prog.Triggers.Length; i++)
                if (trigFlags.TryGetValue(_prog.Triggers[i].Name ?? "", out bool on))
                    _triggerEnabled[i] = on;
                // иначе триггер новый — остаётся со своим стартовым флагом

            int mCount = ReadCount(r, 1 << 20, "число модулей");
            var modFlags = new Dictionary<string, bool>(mCount, StringComparer.Ordinal);
            for (int i = 0; i < mCount; i++)
            {
                string name = r.ReadString();
                bool on = r.ReadBoolean();
                modFlags[name] = on;
            }
            for (int i = 0; i < _prog.Modules.Length; i++)
                if (modFlags.TryGetValue(_prog.Modules[i] ?? "", out bool on))
                    _moduleEnabled[i] = on;

            // --- динамические строки ---
            int strCount = ReadCount(r, 1 << 22, "число динамических строк");
            for (int i = 0; i < strCount; i++)
            {
                int oldId = r.ReadInt32();
                string s = r.ReadString();
                ctx.StrMap[oldId] = Strings.Intern(s);
            }

            // --- шапки коллекций: создаём пустые, строим ремап ---
            int collCount = ReadCount(r, Collections.MaxLiveCollections, "число коллекций");
            var collOrder = new (VariantType kind, Variant handle, int len)[collCount];
            for (int i = 0; i < collCount; i++)
            {
                var kind = (VariantType)r.ReadByte();
                if (kind != VariantType.Array && kind != VariantType.List && kind != VariantType.Map)
                    throw new SaveStateException($"Сейв повреждён: неизвестный вид коллекции {(byte)kind}.");
                int oldId = r.ReadInt32();
                int len = ReadCount(r,
                    kind == VariantType.Array ? Collections.MaxArrayLength : 1 << 24,
                    "длина коллекции");
                Variant h;
                switch (kind)
                {
                    case VariantType.Array: h = Collections.NewArray(len); ctx.ArrMap[oldId] = h; break;
                    case VariantType.List: h = Collections.NewList(); ctx.ListMap[oldId] = h; break;
                    default: h = Collections.NewMap(); ctx.MapMap[oldId] = h; break;
                }
                collOrder[i] = (kind, h, len);
            }

            // --- шапки подписок: listener по имени, цель по стабильному id ---
            var listenerByName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < _prog.Listeners.Length; i++)
                listenerByName[_prog.Listeners[i].Name ?? ""] = i;

            int attCount = ReadCount(r, 1 << 20, "число подписок");
            var attOrder = new (Attachment att, string[] savedFields, bool dropped)[attCount];
            var attByOldIndex = new Dictionary<int, Attachment>();
            for (int i = 0; i < attCount; i++)
            {
                string lname = r.ReadString();
                long stable = r.ReadInt64();
                int oldIdx = r.ReadInt32();
                int oldVer = r.ReadInt32();
                int fieldCount = ReadCount(r, 1 << 16, "число полей подписки");
                var savedFields = new string[fieldCount];
                for (int k = 0; k < fieldCount; k++) savedFields[k] = r.ReadString();

                object obj = stable != 0 ? resolver.ResolveStableId(stable) : null;
                if (obj == null || !listenerByName.TryGetValue(lname, out int lid))
                {
                    // цель исчезла из мира либо listener удалён из скриптов —
                    // подписка не восстанавливается (OnUnsubscribe не зовём:
                    // в ЭТОМ мире она и не жила)
                    attOrder[i] = (null, savedFields, true);
                    report.DroppedSubscriptions++;
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
                // пул отдаёт массив с чужими значениями: поля, которых в сейве нет,
                // обязаны быть чистыми, а не унаследованными от прошлой подписки
                if (att.Fields.Length > 0) Array.Clear(att.Fields, 0, att.Fields.Length);

                if (!_subsByEntity.TryGetValue(att.TargetKey, out var list))
                    _subsByEntity[att.TargetKey] = list = new List<int>();
                list.Add(att.Index);
                _liveAttachments++;

                ctx.SubMap[Pack(oldIdx, oldVer)] = Variant.Sub(att.Index, att.Version);
                attByOldIndex[oldIdx] = att;
                attOrder[i] = (att, savedFields, false);
            }

            // --- шапки файберов: материализуем, строим ремап ---
            int fibCount = ReadCount(r, 1 << 20, "число файберов");
            var fibOrder = new (Fiber f, bool dropped, int[] iterCounts)[fibCount];
            var fibMinStack = new int[fibCount];   // Base + LocalCount самого высокого кадра
            for (int i = 0; i < fibCount; i++)
            {
                int oldIdx = r.ReadInt32();
                int oldVer = r.ReadInt32();
                var state = (FiberState)r.ReadByte();
                int triggerId = r.ReadInt32();
                int budgetHits = r.ReadInt32();
                double wakeTime = ReadFiniteTime(r, "время пробуждения файбера");
                float pendingWait = r.ReadSingle();
                if (float.IsNaN(pendingWait) || float.IsInfinity(pendingWait))
                    throw new SaveStateException("Сейв повреждён: длительность wait не является конечным числом.");
                int attachOld = r.ReadInt32();
                int frameCount = ReadCount(r, MaxCallDepth, "глубина стека вызовов");

                Attachment att = null;
                // файбер не восстанавливается, если изменился код (позиция в байткоде
                // потеряла смысл) или не вернулась его подписка
                bool dropped = !codeMatches
                            || (attachOld >= 0 && !attByOldIndex.TryGetValue(attachOld, out att));

                if (dropped)
                {
                    // кадры и шапку итераций прочитать и выбросить (выравнивание потока)
                    for (int k = 0; k < frameCount; k++) { r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); }
                    int dDepth = ReadCount(r, 1 << 10, "глубина вложенных for-in");
                    var dCounts = new int[dDepth];
                    for (int k = 0; k < dDepth; k++) dCounts[k] = ReadCount(r, 1 << 24, "размер снапшота for-in");
                    fibOrder[i] = (null, true, dCounts);
                    NoteDroppedFiber(report, triggerId);
                    continue;
                }

                // сохраняются только приостановленные файберы; Free/Running в сейве
                // означают порчу, а Free ещё и оставил бы арендованный слот вне пула
                if (state != FiberState.Ready && state != FiberState.Sleeping && state != FiberState.YieldedTick)
                    throw new SaveStateException($"Сейв повреждён: недопустимое состояние файбера {(byte)state}.");

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
                int minStack = 0;
                for (int k = 0; k < frameCount; k++)
                {
                    int fn = r.ReadInt32();
                    int ipv = r.ReadInt32();
                    int bas = r.ReadInt32();
                    // (func, ip, base) адресуют байткод и стек напрямую: без сверки
                    // крафтовый сейв входит в произвольную функцию, в середину чанка
                    // или адресует локали мимо стека
                    if ((uint)fn >= (uint)_prog.Functions.Length)
                        throw new SaveStateException($"Сейв повреждён: индекс функции {fn} вне программы.");
                    var callee = _prog.Functions[fn];
                    if (ipv < 0 || ipv > callee.Code.Length)
                        throw new SaveStateException($"Сейв повреждён: позиция {ipv} вне кода функции '{callee.Name}'.");
                    if (bas < 0 || bas > (1 << 22))
                        throw new SaveStateException($"Сейв повреждён: база локалей {bas} вне допустимого диапазона.");
                    f.Frames[k].Func = fn;
                    f.Frames[k].Ip = ipv;
                    f.Frames[k].Base = bas;
                    int need = bas + callee.LocalCount;
                    if (need > minStack) minStack = need;
                }
                fibMinStack[i] = minStack;

                int iterDepth = ReadCount(r, 1 << 10, "глубина вложенных for-in");
                var iterCounts = new int[iterDepth];
                for (int k = 0; k < iterDepth; k++) iterCounts[k] = ReadCount(r, 1 << 24, "размер снапшота for-in");

                ctx.FiberMap[Pack(oldIdx, oldVer)] = f.Handle;
                fibOrder[i] = (f, false, iterCounts);
            }

            // --- содержимое: статики ПО КЛЮЧАМ ---
            var keyToSlot = new Dictionary<string, int>(StringComparer.Ordinal);
            var progKeys = _prog.StaticKeys ?? Array.Empty<string>();
            for (int i = 0; i < progKeys.Length && i < _statics.Length; i++)
                keyToSlot[progKeys[i] ?? ""] = i;

            int stCount = ReadCount(r, 1 << 22, "число статических полей");
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < stCount; i++)
            {
                string key = r.ReadString();
                var value = ReadVariant(r, ctx);
                seenKeys.Add(key);
                if (keyToSlot.TryGetValue(key, out int slot)) _statics[slot] = value;
                else report.MissingStatics.Add(key);   // поле исчезло из программы
            }
            foreach (var kv in keyToSlot)
                if (!seenKeys.Contains(kv.Key))
                    report.NewStatics.Add(kv.Key);     // поле появилось — осталось от <init>

            // --- содержимое: коллекции ---
            foreach (var (kind, handle, len) in collOrder)
            {
                switch (kind)
                {
                    case VariantType.Array:
                    {
                        var arr = Collections.GetArrayData(handle.CollId);
                        for (int i = 0; i < len; i++) arr[i] = ReadVariant(r, ctx);
                        break;
                    }
                    case VariantType.List:
                        for (int i = 0; i < len; i++) Collections.LoadListAdd(handle.CollId, ReadVariant(r, ctx));
                        break;
                    default:
                        for (int i = 0; i < len; i++)
                        {
                            var k = ReadVariant(r, ctx);
                            var v = ReadVariant(r, ctx);
                            Collections.LoadMapSet(handle.CollId, k, v);
                        }
                        break;
                }
            }

            // --- содержимое: поля подписок ПО ИМЕНАМ (дропнутые — читаем и выбрасываем) ---
            foreach (var (att, savedFields, dropped) in attOrder)
            {
                Dictionary<string, int> nameToSlot = null;
                if (!dropped)
                {
                    var names = _prog.Listeners[att.ListenerId].FieldNames ?? Array.Empty<string>();
                    nameToSlot = new Dictionary<string, int>(names.Length, StringComparer.Ordinal);
                    for (int k = 0; k < names.Length; k++) nameToSlot[names[k] ?? ""] = k;
                }

                for (int i = 0; i < savedFields.Length; i++)
                {
                    var v = ReadVariant(r, ctx);
                    if (dropped || att.Fields == null) continue;
                    if (nameToSlot.TryGetValue(savedFields[i] ?? "", out int slot) && slot < att.Fields.Length)
                        att.Fields[slot] = v;
                    // поля, которого больше нет в listener, просто нет: его значение теряется
                }
            }

            // --- содержимое: стеки файберов ---
            for (int fi = 0; fi < fibOrder.Length; fi++)
            {
                var (f, dropped, iterCounts) = fibOrder[fi];
                int sp = ReadCount(r, 1 << 22, "размер стека файбера");
                if (dropped)
                {
                    for (int i = 0; i < sp; i++) ReadVariant(r, ctx);
                    foreach (var cnt in iterCounts)
                        for (int i = 0; i < cnt; i++) ReadVariant(r, ctx);
                    continue;
                }
                // стек обязан вмещать не только сохранённый Sp, но и локали ВСЕХ
                // восстановленных кадров — иначе LoadLocal уедет за границу массива
                f.EnsureStack(Math.Max(sp, fibMinStack[fi]));
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

            // --- очереди/таймеры (дропнутые файберы не разрезолвятся и просто выпадут) ---
            int timerCount = ReadCount(r, 1 << 20, "число таймеров");
            for (int i = 0; i < timerCount; i++)
            {
                double t = ReadFiniteTime(r, "время таймера");
                var h = RemapFiber(r, ctx);
                var tf = _fibers.ResolveHandle(h);
                if (tf != null) PushTimer(t, FiberPool.Pack(tf));
            }
            int rqCount = ReadCount(r, 1 << 20, "длина очереди запуска");
            for (int i = 0; i < rqCount; i++)
            {
                var h = RemapFiber(r, ctx);
                var f = _fibers.ResolveHandle(h);
                if (f != null) _runQueue.Enqueue(FiberPool.Pack(f));
            }
            int ntCount = ReadCount(r, 1 << 20, "длина очереди следующего тика");
            for (int i = 0; i < ntCount; i++)
            {
                var h = RemapFiber(r, ctx);
                var f = _fibers.ResolveHandle(h);
                if (f != null) _nextTick.Add(FiberPool.Pack(f));
            }

            if (r.ReadInt32() != SaveEndMarker)
                throw new SaveStateException("Сейв повреждён (нет завершающего маркера).");

            return report;
        }

        private void NoteDroppedFiber(SaveMigrationReport report, int triggerId)
        {
            report.DroppedFibers++;
            string name = (uint)triggerId < (uint)_prog.Triggers.Length
                ? _prog.Triggers[triggerId].Name
                : "(без триггера)";
            if (!report.DroppedFiberTriggers.Contains(name)) report.DroppedFiberTriggers.Add(name);
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
                case VariantType.Double: return Variant.Double(r.ReadDouble());

                case VariantType.Str:
                {
                    int id = r.ReadInt32();
                    if (id < 0) return Variant.Nil;                        // порча: id строки не бывает отрицательным
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
                    return ctx.ArrMap.TryGetValue(id, out var h) ? h : Variant.Nil;
                }
                case VariantType.List:
                {
                    int id = r.ReadInt32();
                    return ctx.ListMap.TryGetValue(id, out var h) ? h : Variant.Nil;
                }
                default:
                {
                    int id = r.ReadInt32();
                    return ctx.MapMap.TryGetValue(id, out var h) ? h : Variant.Nil;
                }
            }
        }
    }
}
