using System.Collections.Generic;

namespace Dsl.Codegen
{
    /// <summary>Ссылка «обработчик события»: триггер + функция.</summary>
    public struct EventHandlerRef
    {
        public int TriggerId;
        public int FuncIndex;
    }

    public sealed class TriggerRuntimeInfo
    {
        public int Id;
        public string Name;
        public string Module;
        public int ModuleIndex;
        public bool StartDisabled;
        public int ActionFuncIndex = -1; // action Do (или -1)
    }

    /// <summary>Рантайм-описание listener-шаблона (подписки на сущность).</summary>
    public sealed class ListenerRuntimeInfo
    {
        public int Id;
        public string Name;
        public string Module;
        public int ModuleIndex;
        public int FieldCount;             // размер блока полей одной подписки
        public int InitFuncIndex = -1;     // сброс полей к инициализаторам (или -1)
        public int OnSubscribeFunc = -1;
        public int OnUnsubscribeFunc = -1;
    }

    /// <summary>Обработчик события архетипа: функция + модуль-владелец (для гейта Enable/DisableModule).</summary>
    public struct ArchHandler
    {
        public int Func;        // funcIndex или -1
        public int ModuleIndex; // чей модуль поставил обработчик (мерж later-wins!)
    }

    /// <summary>
    /// Рантайм-таблица одного вида архетипа. Id интернированы в плотные int
    /// при компиляции: хост резолвит строку ОДИН раз при загрузке контента,
    /// на горячем пути Raise — чистая индексация Handlers[arch][localEvent].
    /// </summary>
    public sealed class ArchetypeKindRuntime
    {
        public string Name;
        public int EventCount;
        public string[] Ids = System.Array.Empty<string>();                 // порядок объявления
        public System.Collections.Generic.Dictionary<string, int> IdIndex; // id -> плотный индекс
        public ArchHandler[][] Handlers = System.Array.Empty<ArchHandler[]>();
    }

    public sealed class ScriptEnumInfo
    {
        public int Id;          // сквозная нумерация после хостовых енумов
        public string Name;
        public string[] Members;
    }

    /// <summary>
    /// Скомпилированная программа: чистые данные, ничего живого. Загружается
    /// в ScriptEngine; при hot-reload просто заменяется целиком.
    /// </summary>
    public sealed class CompiledProgram
    {
        public Chunk[] Functions;              // [0] — синтетический <init>
        public int StaticCount;                // размер массива статиков
        public string[] StringLiterals;        // пул строковых литералов
        public TriggerRuntimeInfo[] Triggers;
        public EventHandlerRef[][] EventHandlers; // [hostEventId] -> упорядоченные обработчики
        public string[] Modules;
        public ScriptEnumInfo[] ScriptEnums;

        // listener-ы: [listenerId][hostEventId] -> funcIndex обработчика (или -1);
        // EventHasListenerHandlers — быстрый фильтр «есть ли вообще подписчики на событие»
        public ListenerRuntimeInfo[] Listeners = System.Array.Empty<ListenerRuntimeInfo>();
        public int[][] ListenerHandlerFunc = System.Array.Empty<int[]>();
        public bool[] EventHasListenerHandlers = System.Array.Empty<bool>();

        // виды архетипов (индекс = kindId реестра хоста)
        public ArchetypeKindRuntime[] ArchetypeKinds = System.Array.Empty<ArchetypeKindRuntime>();

        // для Engine.TriggerExists / ClassExists (по строковому имени)
        public Dictionary<string, int> TriggerIdByName;
        public HashSet<string> ClassNames;

        public const int InitFuncIndex = 0;

        // ===== отпечаток программы (для сейвов) ============================
        // Файберы в сейве хранят (funcIndex, ip) — они валидны только против
        // ТОГО ЖЕ байткода. FNV-1a 64 по коду, литералам и таблицам; номера
        // строк исключены (правка форматирования исходника сейв не ломает).

        private ulong _fingerprint;
        private bool _fingerprintDone;

        public ulong Fingerprint
        {
            get
            {
                if (!_fingerprintDone)
                {
                    _fingerprint = ComputeFingerprint();
                    _fingerprintDone = true;
                }
                return _fingerprint;
            }
        }

        private ulong ComputeFingerprint()
        {
            ulong h = 14695981039346656037UL; // FNV offset basis
            void Mix(long v) { h ^= (ulong)v; h *= 1099511628211UL; }
            void MixStr(string s)
            {
                if (s == null) { Mix(-1); return; }
                Mix(s.Length);
                foreach (char c in s) Mix(c);
            }

            Mix(Functions.Length);
            foreach (var ch in Functions)
            {
                Mix(ch.ParamCount); Mix(ch.LocalCount); Mix(ch.Code.Length);
                foreach (var ins in ch.Code) { Mix((byte)ins.Op); Mix(ins.A); Mix(ins.B); }
            }
            Mix(StaticCount);
            Mix(StringLiterals.Length);
            foreach (var s in StringLiterals) MixStr(s);
            Mix(Triggers.Length);
            foreach (var t in Triggers) { MixStr(t.Name); MixStr(t.Module); Mix(t.ActionFuncIndex); }
            Mix(EventHandlers.Length);
            foreach (var row in EventHandlers)
            {
                Mix(row.Length);
                foreach (var hr in row) { Mix(hr.TriggerId); Mix(hr.FuncIndex); }
            }
            Mix(Listeners.Length);
            foreach (var l in Listeners)
            {
                MixStr(l.Name); Mix(l.FieldCount); Mix(l.InitFuncIndex);
                Mix(l.OnSubscribeFunc); Mix(l.OnUnsubscribeFunc);
            }
            foreach (var row in ListenerHandlerFunc)
                foreach (var f in row) Mix(f);
            Mix(ArchetypeKinds.Length);
            foreach (var k in ArchetypeKinds)
            {
                MixStr(k.Name); Mix(k.EventCount); Mix(k.Ids.Length);
                foreach (var id in k.Ids) MixStr(id);
                foreach (var row in k.Handlers)
                    foreach (var ah in row) { Mix(ah.Func); Mix(ah.ModuleIndex); }
            }
            Mix(Modules.Length);
            foreach (var mname in Modules) MixStr(mname);
            return h;
        }
    }
}
