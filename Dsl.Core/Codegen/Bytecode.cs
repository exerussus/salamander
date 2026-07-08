namespace Dsl.Codegen
{
    public enum OpCode : byte
    {
        Nop,

        // помещение констант (всё инлайном; строки — через пул литералов)
        PushNil, PushTrue, PushFalse,
        PushInt,      // A = значение
        PushFloat,    // A = биты float (Int32BitsToSingle)
        PushStr,      // A = индекс в StringLiterals
        PushEnum,     // A = enumTypeId, B = value

        // локали / статики
        LoadLocal, StoreLocal,     // A = slot
        LoadStatic, StoreStatic,   // A = static slot
        LoadAttach, StoreAttach,   // A = attach slot (поле текущей подписки listener)
        PushSelf,                  // сущность-цель текущей подписки

        // хостовые свойства
        LoadField,    // A = propId ; стек: [obj] -> [value]
        StoreField,   // A = propId ; стек: [obj, value] -> []

        // арифметика (динамическая по типу Variant)
        Add, Sub, Mul, Div, Mod, Neg,

        // сравнения -> bool
        Eq, Ne, Lt, Le, Gt, Ge,
        Not,

        // строки
        Concat,       // A = число частей на стеке -> одна строка

        // управление
        Jump, JumpIfFalse, JumpIfTrue,  // A = индекс инструкции
        Pop,

        // вызовы (каждый ОСТАВЛЯЕТ ровно одно значение: Nil для void)
        CallScript,   // A = funcIndex, B = argCount (тот же файбер)
        CallHost,     // A = hostFnId, B = argCount
        CallEngine,   // A = EngineOp, B = argCount

        // файберы / ожидание
        Spawn,        // A = funcIndex, B = argCount -> Fiber
        Wait,         // стек: [secs] -> [] ; приостановка по времени
        YieldTick,    // приостановка до следующего тика
        Return,       // A = 1 если на стеке значение, иначе 0

        // коллекции
        NewArray,     // стек: [size] -> [array]
        NewList,      // -> [list]
        NewMap,       // -> [map]
        ArrayLit,     // A = count ; стек: [e0..e{n-1}] -> [array]
        Index,        // стек: [coll, idx] -> [value]
        StoreIndex,   // стек: [coll, idx, value] -> []

        // встроенные операции коллекций
        Len,          // стек: [coll] -> [int] (length/count)
        ListAdd,      // стек: [list, value] -> []
        ListClear,    // стек: [list] -> []
        MapHas,       // стек: [map, key] -> [bool]
        MapRemove,    // стек: [map, key] -> []

        // преобразования
        IntToFloat,   // top: int -> float
    }

    /// <summary>Встроенные методы коллекций (list.Add и т.п.), различаются чекером.</summary>
    public enum BuiltinOp : int
    {
        ListAdd, ListClear, MapHas, MapRemove,
    }

    public enum EngineOp : int
    {
        EnableTrigger, DisableTrigger, IsTriggerEnabled, ActivateTrigger,
        Kill, IsAlive, KillAll,
        EnableModule, DisableModule, IsModuleEnabled, IsModuleLoaded,
        Time, DeltaTime,
        Log, Warn, Error,
        IsValid,
        TriggerExists, ClassExists,
        Attach, Detach, DetachAll, IsSubscribed,
    }

    /// <summary>
    /// Инструкция байткода. Операнды — просто два int (без битовой упаковки),
    /// чтобы декодирование в VM было тривиальным и не давало багов. Line хранит
    /// строку исходника для рантайм-стектрейсов.
    /// </summary>
    public struct Instr
    {
        public OpCode Op;
        public int A;
        public int B;
        public int Line;

        public Instr(OpCode op, int a = 0, int b = 0, int line = 0)
        {
            Op = op; A = a; B = b; Line = line;
        }
    }

    /// <summary>Скомпилированная функция: код + мета для стека и стектрейсов.</summary>
    public sealed class Chunk
    {
        public string Name;      // напр. "AmbushCutscene.Do"
        public string File;      // имя исходника
        public int ParamCount;
        public int LocalCount;   // всего слотов локалей (включая параметры и скрытые)
        public Instr[] Code;
    }
}
