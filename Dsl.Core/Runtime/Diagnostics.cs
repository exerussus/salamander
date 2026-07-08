namespace Dsl.Runtime
{
    /// <summary>
    /// Что делать с файбером, который исчерпал бюджет инструкций тика, но не
    /// завершился и сам не приостановился (нет wait/yield) — почти всегда это
    /// тяжёлый или бесконечный цикл.
    /// </summary>
    public enum BudgetPolicy : byte
    {
        /// <summary>Только переносить в следующий тик. Логика никогда не рвётся,
        /// но настоящий вечный цикл будет жрать бюджет каждый кадр вечно.</summary>
        CarryOver = 0,

        /// <summary>Переносить, но если файбер упирается в бюджет
        /// BudgetKillThreshold тиков ПОДРЯД (не делая wait) — убить с ошибкой.
        /// Рекомендуется: живая логика цела, вечный цикл ловится.</summary>
        CarryOverThenKill = 1,

        /// <summary>Сразу убивать при первом же исчерпании. Жёстко, но
        /// предсказуемо по нагрузке.</summary>
        KillImmediately = 2,
    }

    /// <summary>
    /// Снимок состояния движка. Дешёвые «датчики» (сколько файберов, строк)
    /// считаются на месте; «за тик» — накопители, обнуляемые каждым Tick.
    /// Это value-тип: GetStats() не аллоцирует.
    /// </summary>
    public struct EngineStats
    {
        // --- мгновенные датчики ---
        public int LiveFibers;      // всего живых файберов
        public int RunningFibers;   // исполняются прямо сейчас (обычно 0 при чтении)
        public int SleepingFibers;  // спят по таймеру (wait N)
        public int YieldedFibers;   // ждут следующего тика (wait until)
        public int ReadyFibers;     // в очереди на запуск
        public int DynamicStrings;  // размер динамического сегмента StringTable
        public int Collections;     // сколько живых коллекций (массивы+списки+мапы)
        public int LiveSubscriptions; // активных подписок listener

        // --- накоплено с прошлого Tick (≈ за кадр) ---
        public long InstructionsThisTick;
        public int FibersStartedThisTick;
        public int FibersCompletedThisTick;
        public int FibersKilledThisTick;      // убито политикой бюджета
        public int FibersErroredThisTick;     // умерло от скриптовой ошибки
        public int EventsRaisedThisTick;
        public int HandlerInvocationsThisTick;
        public int FibersDeferredThisTick;    // перенесено в следующий тик из-за бюджета
        public int SpawnsDroppedThisTick;     // спавны, отклонённые лимитом (спавн-бомба)
        public bool BudgetExhaustedThisTick;  // упёрлись в TickInstructionBudget

        public double Time;                   // модельное время движка
    }

    /// <summary>
    /// Пер-триггерная статистика — то, что первым спрашивает дизайнер:
    /// «сработал ли мой триггер, сколько раз, живы ли его файберы, не упал ли».
    /// Кумулятивные поля растут с загрузки программы; *ThisTick обнуляются.
    /// Детализация по инструкциям заполняется только при EnableProfiling.
    /// </summary>
    public struct TriggerStats
    {
        public int TriggerId;
        public string Name;
        public string Module;
        public bool Enabled;

        public long TimesFired;        // сколько раз запускались обработчики этого триггера
        public long FibersSpawned;     // сколько файберов породил (spawn + activate + обработчики)
        public int FibersAlive;        // сколько его файберов живо сейчас
        public double LastFiredTime;   // когда в последний раз сработал

        public long ErrorCount;        // сколько его файберов упало с ошибкой
        public string LastError;       // текст последней ошибки

        public long InstructionsThisTick; // только при EnableProfiling
        public long InstructionsTotal;    // только при EnableProfiling
    }
}
