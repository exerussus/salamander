using System;
using System.Collections.Generic;
using Dsl.Codegen;
using Dsl.Semantics;

namespace Dsl.Runtime
{
    /// <summary>
    /// Ядро исполнения скриптов. Владеет программой, пулом файберов,
    /// планировщиком (таймеры + следующий тик + очередь запуска), строками,
    /// сущностями и коллекциями. Полностью engine-agnostic: адаптер Unity
    /// лишь качает Tick/Raise и подписывает колбэки логов.
    ///
    /// Семантика (W3-стиль):
    ///  - RaiseEvent запускает обработчики НЕМЕДЛЕННО до первой приостановки;
    ///  - spawn/ActivateTrigger ставят файбер в очередь, он стартует после
    ///    того, как текущий приостановится (без реентерабельности);
    ///  - Tick(dt) будит таймеры и «wait until»-файберы.
    /// </summary>
    public sealed partial class ScriptEngine : IHostContext
    {
        public HostRegistry Host { get; }
        public StringTable Strings { get; private set; }
        public EntityRegistry Entities { get; } = new EntityRegistry();
        public CollectionStore Collections { get; } = new CollectionStore();

        private readonly FiberPool _fibers = new FiberPool();
        private readonly Vm _vm;
        private CompiledProgram _prog;
        private Variant[] _statics = Array.Empty<Variant>();
        private int[] _litIds = Array.Empty<int>();

        private bool[] _triggerEnabled = Array.Empty<bool>();
        private bool[] _moduleEnabled = Array.Empty<bool>();
        private readonly Dictionary<string, int> _moduleIndex = new Dictionary<string, int>();

        // планировщик
        private readonly Queue<long> _runQueue = new Queue<long>();
        private readonly List<long> _nextTick = new List<long>();
        private double[] _timerTime = new double[64];
        private long[] _timerFiber = new long[64];
        private int _timerCount;

        // фаза Raise: буфер аргументов + список обработчиков (реентерабельно за счёт диапазонов)
        private readonly Variant[] _raiseArgs = new Variant[16];
        private int _raiseArgCount;
        private readonly List<long> _pendingHandlers = new List<long>();

        private Fiber _current;
        private double _time;
        private float _dt;

        /// <summary>Порог динамических строк, после которого Tick запускает свип. 0 — отключить.</summary>
        public int StringSweepThreshold = 4096;

        /// <summary>Порог живых коллекций, после которого Tick запускает свип. 0 — отключить.</summary>
        public int CollectionSweepThreshold = 4096;

        // ===== бюджет исполнения ===========================================

        /// <summary>Суммарный лимит инструкций скриптов на один Tick (≈ кадр).
        /// Дренаж очереди останавливается при исчерпании; недоделанные файберы
        /// продолжат в следующем тике. Так один кривой мод не роняет FPS.</summary>
        public long TickInstructionBudget = 5_000_000;

        /// <summary>Слайс одного запуска файбера: не даёт одному файберу выесть
        /// весь бюджет тика за раз, обеспечивает честный round-robin.</summary>
        public int MaxInstructionsPerFiberRun = 200_000;

        /// <summary>Потолок НОВЫХ файберов (spawn/activate) за тик — защита от спавн-бомбы.</summary>
        public int MaxFibersStartedPerTick = 4096;

        /// <summary>
        /// Потолок инструкций для инициализации: чанк &lt;init&gt; (статики) и сброс
        /// полей подписки listener. Ждать они не могут, но МОГУТ звать скриптовые
        /// функции — а значит и зациклиться. Без конечного предела такой
        /// инициализатор вешает LoadProgram/Attach без диагностики.
        /// </summary>
        public int InitInstructionLimit = 5_000_000;

        /// <summary>
        /// Предел глубины скриптовых вызовов внутри файбера (защита от рекурсии,
        /// которая съедает память раньше, чем срабатывает бюджет инструкций).
        /// </summary>
        public int MaxCallDepth
        {
            get => _vm.MaxCallDepth;
            set => _vm.MaxCallDepth = value;
        }

        /// <summary>Что делать с файбером, упёршимся в бюджет (см. BudgetPolicy).</summary>
        public BudgetPolicy Policy = BudgetPolicy.CarryOverThenKill;

        /// <summary>Для CarryOverThenKill: сколько тиков подряд у бюджета → kill.</summary>
        public int BudgetKillThreshold = 300;

        /// <summary>
        /// Порог «залипшего цикла»: если файбер исполнил столько инструкций, НЕ
        /// дойдя ни до одной кооперативной точки (wait/wait until/spawn-yield),
        /// он считается бесконечным циклом без ожидания и убивается немедленно —
        /// не дожидаясь BudgetKillThreshold тиков и не вешая кадр. Это ловит
        /// while(true) без эффективного wait (в т.ч. когда wait until мгновенно
        /// истинно). По умолчанию — один полный per-run cap: честная тяжёлая
        /// работа успевает приостановиться, спин — нет.
        /// </summary>
        public long StuckInstructionLimit = 200_000;

        /// <summary>Детальная разбивка инструкций по триггерам. В релизе держите
        /// off (горячий путь платит только за общий счётчик тика), в редакторе on.</summary>
        public bool EnableProfiling = false;

        /// <summary>Поднимается, когда файбер убит политикой бюджета. Аргумент — id
        /// триггера-владельца (или -1). Редакторское окно подсвечивает виновника.</summary>
        public event Action<int> OnBudgetExceeded;

        // ===== счётчики =====================================================

        private long _tickInstrLeft;
        private EngineStats _tick;       // накопители за текущий тик
        private TriggerRuntimeStats[] _trigStats = Array.Empty<TriggerRuntimeStats>();
        private int[] _aliveByTrigger = Array.Empty<int>(); // переиспользуемый буфер

        // ===== подписки listener =====
        // Экземпляр подписки: пулируемый блок полей + хэндл цели + версия (хэндлы
        // Subscription протухают на detach, как у файберов).
        private sealed class Attachment
        {
            public int Index;
            public int Version;
            public int ListenerId = -1;
            public Variant Target;
            public long TargetKey;
            public Variant[] Fields;
            public bool Active;
            public bool Finalizing;
        }

        private readonly List<Attachment> _attachments = new List<Attachment>();
        private readonly Stack<int> _freeAttachments = new Stack<int>();
        private Stack<Variant[]>[] _attachFieldPools = Array.Empty<Stack<Variant[]>>();
        private readonly Dictionary<long, List<int>> _subsByEntity = new Dictionary<long, List<int>>();
        private readonly List<int> _subScratch = new List<int>(); // снапшот на время detach/dispatch
        private int _liveAttachments;

        private static long PackEntity(Variant v) => ((long)v.Version << 32) | (uint)v.Index;

        // ===== архетипы =====
        private readonly Dictionary<string, int> _archKindByName = new Dictionary<string, int>();

        /// <summary>Поля одного блока-архетипа: имя → слот в _statics (порядок объявления).</summary>
        private sealed class ArchConsts
        {
            public static readonly ArchConsts Empty = new ArchConsts();
            public string[] Names = Array.Empty<string>();
            public int[] Slots = Array.Empty<int>();
        }

        // [kindId][archIndex] — «рецепт» сущности: что модер объявил в блоке.
        // Строится один раз в LoadProgram (см. BuildArchetypeConstIndex).
        private ArchConsts[][] _archConsts = Array.Empty<ArchConsts[]>();

        private sealed class TriggerRuntimeStats
        {
            public long TimesFired;
            public long FibersSpawned;
            public double LastFiredTime;
            public long ErrorCount;
            public string LastError;
            public long InstructionsThisTick;
            public long InstructionsTotal;
        }

        public event Action<string> OnLog;
        public event Action<string> OnWarn;
        public event Action<string> OnError;

        public double Time => _time;
        public bool IsLoaded => _prog != null;

        public ScriptEngine(HostRegistry host)
        {
            Host = host;
            Strings = new StringTable();
            _vm = new Vm(this);
        }

        // ===================================================================
        // Загрузка / горячая замена программы
        // ===================================================================

        public void LoadProgram(CompiledProgram prog)
        {
            // атомарная замена: гасим всё старое, ставим новое
            KillAllFibers();
            _runQueue.Clear();
            _nextTick.Clear();
            _timerCount = 0;
            Collections.Clear();

            Strings = new StringTable();
            _litIds = new int[prog.StringLiterals.Length];
            for (int i = 0; i < prog.StringLiterals.Length; i++)
                _litIds[i] = Strings.Intern(prog.StringLiterals[i]);
            Strings.FreezeStatics();

            _statics = new Variant[Math.Max(1, prog.StaticCount)];
            _prog = prog;
            _vm.Prog = prog;
            _vm.Statics = _statics;
            _vm.LitIds = _litIds;

            _triggerEnabled = new bool[prog.Triggers.Length];
            for (int i = 0; i < prog.Triggers.Length; i++)
                _triggerEnabled[i] = !prog.Triggers[i].StartDisabled;

            _trigStats = new TriggerRuntimeStats[prog.Triggers.Length];
            for (int i = 0; i < _trigStats.Length; i++) _trigStats[i] = new TriggerRuntimeStats();
            _aliveByTrigger = new int[prog.Triggers.Length];
            _tick = default;
            // Raise до первого Tick обязан исполняться немедленно (контракт событий):
            // выдаём полный бюджет сразу, первый Tick пополнит его как обычно
            _tickInstrLeft = TickInstructionBudget;

            // подписки принадлежат программе — при (пере)загрузке всё сбрасывается жёстко
            _subsByEntity.Clear();
            _freeAttachments.Clear();
            for (int i = _attachments.Count - 1; i >= 0; i--)
            {
                var a = _attachments[i];
                a.Active = false; a.Finalizing = false; a.Fields = null; a.ListenerId = -1; a.Version++;
                _freeAttachments.Push(i);
            }
            _liveAttachments = 0;
            _attachFieldPools = new Stack<Variant[]>[prog.Listeners.Length];
            for (int i = 0; i < _attachFieldPools.Length; i++) _attachFieldPools[i] = new Stack<Variant[]>();

            _archKindByName.Clear();
            for (int i = 0; i < prog.ArchetypeKinds.Length; i++)
                _archKindByName[prog.ArchetypeKinds[i].Name] = i;
            BuildArchetypeConstIndex();

            _moduleIndex.Clear();
            _moduleEnabled = new bool[prog.Modules.Length];
            for (int i = 0; i < prog.Modules.Length; i++)
            {
                _moduleIndex[prog.Modules[i]] = i;
                _moduleEnabled[i] = true;
            }

            RunInit();
        }

        private void RunInit()
        {
            ApplyStaticDefaults();
            var f = CreateFiber(CompiledProgram.InitFuncIndex, null, 0, 0, -1);
            _current = f;
            f.State = FiberState.Running;
            // инициализаторам даём щедрый, но КОНЕЧНЫЙ лимит: ждать они не могут,
            // а вот вызвать зациклившуюся функцию — вполне
            var r = _vm.Run(f, InitInstructionLimit);
            _current = null;
            if (r == RunResult.Waited || r == RunResult.Yielded)
                OnError?.Invoke("<init>: инициализаторы полей не должны ждать (wait) — инициализация прервана.");
            else if (r == RunResult.OutOfBudget)
                OnError?.Invoke($"<init>: инициализация статических полей не уложилась в {InitInstructionLimit} " +
                                "инструкций и прервана — вероятно, инициализатор вызывает зацикленную функцию.");
            _fibers.Return(f);
        }

        /// <summary>
        /// Значения по умолчанию из контракта вида — ДО &lt;init&gt;: инициализатор
        /// блока пишет в тот же слот и перекрывает дефолт сам собой. Зовётся и из
        /// LoadState (он тоже гоняет RunInit), поэтому readonly-константы после
        /// загрузки сейва берутся из ТЕКУЩЕЙ программы, а не из сохранённой.
        /// </summary>
        private void ApplyStaticDefaults()
        {
            var defs = _prog.StaticDefaults;
            if (defs == null) return;
            int n = Math.Min(defs.Length, _statics.Length);
            for (int i = 0; i < n; i++)
            {
                var d = defs[i];
                if (d.IsNil) continue;
                // строковый дефолт лежит как индекс в пуле литералов — настоящий
                // id в StringTable раздан только что, при загрузке программы
                _statics[i] = d.Type == VariantType.Str
                    ? Variant.Str(_litIds[d.StrId])
                    : d;
            }
        }

        // ===================================================================
        // Тик и события
        // ===================================================================

        /// <summary>Один игровой тик: будим спящих, гоняем очередь. Сбрасывает
        /// бюджет инструкций и per-tick счётчики — считайте это «началом кадра».</summary>
        public void Tick(float dt)
        {
            _time += dt;
            _dt = dt;

            // новый кадр — новый бюджет и обнулённые накопители
            _tickInstrLeft = TickInstructionBudget;
            _tick = default;
            if (EnableProfiling)
                for (int i = 0; i < _trigStats.Length; i++) _trigStats[i].InstructionsThisTick = 0;

            // просроченные таймеры
            while (_timerCount > 0 && _timerTime[0] <= _time)
            {
                long packed = PopTimer();
                var f = _fibers.ResolvePacked(packed);
                if (f != null && f.State == FiberState.Sleeping)
                {
                    f.State = FiberState.Ready;
                    _runQueue.Enqueue(packed);
                }
            }

            // «wait until» — по одной проверке за тик
            for (int i = 0; i < _nextTick.Count; i++)
            {
                var f = _fibers.ResolvePacked(_nextTick[i]);
                if (f != null && f.State == FiberState.YieldedTick)
                {
                    f.State = FiberState.Ready;
                    _runQueue.Enqueue(_nextTick[i]);
                }
            }
            _nextTick.Clear();

            DrainRunQueue();

            // сборка идёт МЕЖДУ файберами (конец тика), поэтому корнями гарантированно
            // накрыт весь живой рантайм: _current здесь всегда null
            bool needSweep = (StringSweepThreshold > 0 && Strings.DynamicCount > StringSweepThreshold)
                          || (CollectionSweepThreshold > 0 && Collections.LiveCount > CollectionSweepThreshold);
            if (needSweep) Collect();
        }

        /// <summary>Начать поднятие события: пишите аргументы и вызовите Commit.</summary>
        public RaiseScope Raise(int eventId)
        {
            _raiseArgCount = 0;
            return new RaiseScope(this, eventId);
        }

        // низкоуровневый путь для типизированных EventRef (Dsl.Hosting):
        // та же механика, что RaiseScope, но без промежуточной структуры
        internal void RaiseBegin() => _raiseArgCount = 0;
        internal void RaiseAdd(Variant v) => _raiseArgs[_raiseArgCount++] = v;
        internal void RaiseCommit(int eventId) => CommitRaise(eventId);

        // ===================================================================
        // Архетипы: адресный подъём событий по (вид, id) + API «сборщика»
        // ===================================================================

        /// <summary>Резолв строкового id в плотный хэндл (один раз при загрузке контента). -1 — кода нет.</summary>
        public int ResolveArchetype(int kindId, string id)
        {
            if (_prog == null || (uint)kindId >= (uint)_prog.ArchetypeKinds.Length || id == null) return -1;
            return _prog.ArchetypeKinds[kindId].IdIndex.TryGetValue(id, out int i) ? i : -1;
        }

        public int ResolveArchetype(string kind, string id) =>
            _archKindByName.TryGetValue(kind, out int k) ? ResolveArchetype(k, id) : -1;

        /// <summary>Есть ли у сущности (вид, id) скриптовый код. Для валидации манифеста контента.</summary>
        public bool HasArchetype(string kind, string id) => ResolveArchetype(kind, id) >= 0;

        /// <summary>Все id вида, для которых есть код (в порядке объявления). Для сверки покрытия.</summary>
        public void GetArchetypeIds(string kind, List<string> into)
        {
            into.Clear();
            if (_prog == null || !_archKindByName.TryGetValue(kind, out int k)) return;
            into.AddRange(_prog.ArchetypeKinds[k].Ids);
        }

        // ===================================================================
        // Константы контента: забрать рецепт, объявленный в блоке-архетипе
        // ===================================================================
        // Поле блока — обычный статик со стабильным ключом "a:вид:id.поле"
        // (CompiledProgram.StaticKeys). Ключи разбираются ОДИН раз при загрузке
        // программы, дальше чтение — две индексации и короткий линейный поиск
        // имени: полей у сущности единицы, строк и аллокаций на пути нет.

        private void BuildArchetypeConstIndex()
        {
            var kinds = _prog.ArchetypeKinds;
            var names = new List<string>[kinds.Length][];
            var slots = new List<int>[kinds.Length][];
            for (int i = 0; i < kinds.Length; i++)
            {
                names[i] = new List<string>[kinds[i].Ids.Length];
                slots[i] = new List<int>[kinds[i].Ids.Length];
            }

            var keys = _prog.StaticKeys;
            int n = keys == null ? 0 : Math.Min(keys.Length, _statics.Length);
            for (int i = 0; i < n; i++)
            {
                // Нужны только поля блоков-архетипов: "a:вид:id.поле" (у классов
                // "c:", у триггеров "t:"). Вид — идентификатор, двоеточий не
                // содержит; имя поля — идентификатор, точек не содержит; а вот id
                // может быть строковым литералом с чем угодно внутри. Поэтому
                // режем по ПЕРВОМУ ':' после "a:" и по ПОСЛЕДНЕЙ точке.
                var key = keys[i];
                if (key == null || !key.StartsWith("a:", StringComparison.Ordinal)) continue;
                int colon = key.IndexOf(':', 2);
                int dot = key.LastIndexOf('.');
                if (colon < 0 || dot <= colon + 1 || dot == key.Length - 1) continue;

                if (!_archKindByName.TryGetValue(key.Substring(2, colon - 2), out int k)) continue;
                int a = ResolveArchetype(k, key.Substring(colon + 1, dot - colon - 1));
                if (a < 0) continue;

                (names[k][a] ??= new List<string>()).Add(key.Substring(dot + 1));
                (slots[k][a] ??= new List<int>()).Add(i);
            }

            _archConsts = new ArchConsts[kinds.Length][];
            for (int k = 0; k < kinds.Length; k++)
            {
                _archConsts[k] = new ArchConsts[kinds[k].Ids.Length];
                for (int a = 0; a < _archConsts[k].Length; a++)
                    _archConsts[k][a] = names[k][a] == null
                        ? ArchConsts.Empty
                        : new ArchConsts { Names = names[k][a].ToArray(), Slots = slots[k][a].ToArray() };
            }
        }

        /// <summary>
        /// Значение «константы контента» — поля, объявленного в блоке-архетипе
        /// (<c>weapon sword { float damage = 12.0; }</c>). Читается из живых
        /// статиков, то есть уже с учётом мержа: побеждает поздний инициализатор
        /// патч-блока, ровно как это видят сами скрипты.
        /// false — нет такого вида, id или поля.
        /// Строку из результата распаковывает <see cref="ResolveString"/>,
        /// число — <c>value.AsInt</c> / <c>value.ToF()</c>.
        /// </summary>
        public bool TryGetArchetypeConst(string kind, string id, string field, out Variant value)
        {
            value = Variant.Nil;
            if (_prog == null || kind == null || field == null) return false;
            if (!_archKindByName.TryGetValue(kind, out int k)) return false;
            int a = ResolveArchetype(k, id);
            if (a < 0) return false;

            var c = _archConsts[k][a];
            for (int i = 0; i < c.Names.Length; i++)
            {
                if (!string.Equals(c.Names[i], field, StringComparison.Ordinal)) continue;
                value = _statics[c.Slots[i]];
                return true;
            }
            return false;
        }

        /// <summary>
        /// Имена всех констант, объявленных у сущности (вид, id), в порядке
        /// объявления. Нужно там, где набор полей ОТКРЫТ и хосту неизвестен
        /// принципиально: атрибут сам решает, в какие слои вкладывается, и может
        /// вложиться в тот, о котором кор никогда не слышал. Без перечисления
        /// такой рецепт нечем прочитать.
        /// </summary>
        public void GetArchetypeConsts(string kind, string id, List<string> into)
        {
            into.Clear();
            if (_prog == null || kind == null) return;
            if (!_archKindByName.TryGetValue(kind, out int k)) return;
            int a = ResolveArchetype(k, id);
            if (a < 0) return;
            into.AddRange(_archConsts[k][a].Names);
        }

        /// <summary>
        /// Объявил ли эту константу сам блок, или она пришла из дефолта вида.
        /// Симметрично ImplementsEvent: перечисление отдаёт ИТОГОВЫЙ набор полей,
        /// а игре иногда важно знать, тронул ли его модер (сверка покрытия,
        /// «этот меч кастомизирован»).
        /// </summary>
        public bool ImplementsConst(string kind, string id, string field)
        {
            if (_prog == null || kind == null || field == null) return false;
            if (!_archKindByName.TryGetValue(kind, out int k)) return false;
            int a = ResolveArchetype(k, id);
            if (a < 0) return false;

            var c = _archConsts[k][a];
            var declared = _prog.StaticDeclared;
            for (int i = 0; i < c.Names.Length; i++)
            {
                if (!string.Equals(c.Names[i], field, StringComparison.Ordinal)) continue;
                int slot = c.Slots[i];
                return declared != null && (uint)slot < (uint)declared.Length && declared[slot];
            }
            return false;
        }

        // ===================================================================
        // Интроспекция: пройтись по тому, что модеры описали в DSL
        // ===================================================================
        // Хост объявляет ВИДЫ и их события (шаблон для заполнения), DSL создаёт
        // сущности этого вида и реализует события. Эти геттеры позволяют игре на
        // старте обойти всё описанное, не зная заранее ни имён видов, ни id.

        /// <summary>Все виды архетипов, объявленные хостом (spell/item/game_mode/...).</summary>
        public void GetArchetypeKinds(List<string> into)
        {
            into.Clear();
            if (_prog == null) return;
            foreach (var k in _prog.ArchetypeKinds) into.Add(k.Name);
        }

        /// <summary>Имена событий вида в порядке объявления хостом (localEventId — индекс в списке).</summary>
        public void GetArchetypeEvents(string kind, List<string> into)
        {
            into.Clear();
            if (_prog == null || !_archKindByName.TryGetValue(kind, out int k)) return;
            into.AddRange(_prog.ArchetypeKinds[k].EventNames);
        }

        /// <summary>Реализовано ли конкретное событие у сущности (вид, id) хоть в одном блоке.</summary>
        public bool ImplementsEvent(string kind, string id, string eventName)
        {
            if (_prog == null || !_archKindByName.TryGetValue(kind, out int k)) return false;
            var kr = _prog.ArchetypeKinds[k];
            int arch = ResolveArchetype(k, id);
            if (arch < 0) return false;
            for (int e = 0; e < kr.EventCount; e++)
                if (kr.EventNames[e] == eventName)
                    return kr.Handlers[arch][e].Func != -1;
            return false;
        }

        /// <summary>Имена событий, реально реализованных у сущности (вид, id). Для проверки покрытия шаблона.</summary>
        public void GetImplementedEvents(string kind, string id, List<string> into)
        {
            into.Clear();
            if (_prog == null || !_archKindByName.TryGetValue(kind, out int k)) return;
            var kr = _prog.ArchetypeKinds[k];
            int arch = ResolveArchetype(k, id);
            if (arch < 0) return;
            for (int e = 0; e < kr.EventCount; e++)
                if (kr.Handlers[arch][e].Func != -1)
                    into.Add(kr.EventNames[e]);
        }

        /// <summary>
        /// Адресный подъём: выполняется обработчик ровно ОДНОГО блока (вид, id),
        /// если тот объявлен и его модуль включён; иначе тишина. Семантика та же,
        /// что у событий: немедленно, до первой приостановки.
        /// </summary>
        internal void RaiseCommitArch(int kindId, int archetype, int localEventId)
        {
            if (_prog == null || (uint)kindId >= (uint)_prog.ArchetypeKinds.Length) return;
            var kr = _prog.ArchetypeKinds[kindId];
            _tick.EventsRaisedThisTick++;
            if ((uint)archetype >= (uint)kr.Handlers.Length || (uint)localEventId >= (uint)kr.EventCount)
            {
                if (_current == null) DrainRunQueue();
                return;
            }
            var h = kr.Handlers[archetype][localEventId];
            if (h.Func < 0 || !_moduleEnabled[h.ModuleIndex])
            {
                if (_current == null) DrainRunQueue();
                return;
            }

            // та же двухфазность, что в CommitRaise: аргументы копируются в файбер
            // до запуска (вложенные Raise из хостовых методов безопасны)
            int start = _pendingHandlers.Count;
            var f = CreateFiber(h.Func, _raiseArgs, 0, _raiseArgCount, -1);
            _pendingHandlers.Add(FiberPool.Pack(f));
            _tick.HandlerInvocationsThisTick++;
            int end = _pendingHandlers.Count;
            for (int i = start; i < end; i++)
            {
                var pf = _fibers.ResolvePacked(_pendingHandlers[i]);
                if (pf != null && pf.State == FiberState.Ready)
                    RunFiberNow(pf);
            }
            _pendingHandlers.RemoveRange(start, end - start);

            if (_current == null) DrainRunQueue();
        }

        public struct RaiseScope
        {
            private readonly ScriptEngine _e;
            private readonly int _eventId;

            internal RaiseScope(ScriptEngine e, int eventId)
            {
                _e = e;
                _eventId = eventId;
            }

            public RaiseScope AddInt(int v) { _e._raiseArgs[_e._raiseArgCount++] = Variant.Int(v); return this; }
            public RaiseScope AddFloat(float v) { _e._raiseArgs[_e._raiseArgCount++] = Variant.Float(v); return this; }
            public RaiseScope AddBool(bool v) { _e._raiseArgs[_e._raiseArgCount++] = Variant.Bool(v); return this; }
            public RaiseScope AddStr(string v) { _e._raiseArgs[_e._raiseArgCount++] = v == null ? Variant.Nil : Variant.Str(_e.Strings.Intern(v)); return this; }
            public RaiseScope AddEntity(object o) { _e._raiseArgs[_e._raiseArgCount++] = _e.Entities.Register(o); return this; }
            public RaiseScope AddEnum(int enumTypeId, int value) { _e._raiseArgs[_e._raiseArgCount++] = Variant.Enum(enumTypeId, value); return this; }
            public RaiseScope AddVariant(Variant v) { _e._raiseArgs[_e._raiseArgCount++] = v; return this; }

            /// <summary>Запустить обработчики немедленно (до их первой приостановки).</summary>
            public void Commit() => _e.CommitRaise(_eventId);
        }

        private void CommitRaise(int eventId)
        {
            if (_prog == null) return;
            if ((uint)eventId >= (uint)_prog.EventHandlers.Length) return;
            _tick.EventsRaisedThisTick++;
            var handlers = _prog.EventHandlers[eventId];
            bool hasListeners = _prog.EventHasListenerHandlers.Length > eventId
                                && _prog.EventHasListenerHandlers[eventId];
            if (handlers.Length == 0 && !hasListeners)
            {
                if (_current == null) DrainRunQueue();
                return;
            }

            // фаза 1: копируем аргументы во все файберы-обработчики
            // (после этого _raiseArgs свободен — вложенные Raise из хостовых методов безопасны)
            int start = _pendingHandlers.Count;
            for (int i = 0; i < handlers.Length; i++)
            {
                var h = handlers[i];
                var trig = _prog.Triggers[h.TriggerId];
                if (!_triggerEnabled[h.TriggerId]) continue;
                if (!_moduleEnabled[trig.ModuleIndex]) continue;

                var f = CreateFiber(h.FuncIndex, _raiseArgs, 0, _raiseArgCount, h.TriggerId);
                _pendingHandlers.Add(FiberPool.Pack(f));

                _tick.HandlerInvocationsThisTick++;
                var ts = _trigStats[h.TriggerId];
                ts.TimesFired++;
                ts.FibersSpawned++;
                ts.LastFiredTime = _time;
            }
            // подписки listener: субъект — ПЕРВЫЙ аргумент-сущность события.
            // Создаём файберы здесь же (фаза 1): аргументы копируются до запуска,
            // а обработчики могут attach/detach — идём по снапшоту списка.
            if (_prog.EventHasListenerHandlers.Length > eventId && _prog.EventHasListenerHandlers[eventId]
                && _raiseArgCount > 0 && _raiseArgs[0].Type == VariantType.Entity
                && _subsByEntity.TryGetValue(PackEntity(_raiseArgs[0]), out var subs))
            {
                _subScratch.Clear();
                _subScratch.AddRange(subs);
                foreach (var idx in _subScratch)
                {
                    var att = _attachments[idx];
                    if (!att.Active || att.Finalizing) continue;
                    var linfo = _prog.Listeners[att.ListenerId];
                    if (!_moduleEnabled[linfo.ModuleIndex]) continue;
                    int func = _prog.ListenerHandlerFunc[att.ListenerId][eventId];
                    if (func < 0) continue;

                    var lf = CreateAttachedFiber(func, att, _raiseArgs, 0, _raiseArgCount);
                    _pendingHandlers.Add(FiberPool.Pack(lf));
                    _tick.HandlerInvocationsThisTick++;
                }
            }

            int end = _pendingHandlers.Count;

            // фаза 2: запускаем в детерминированном порядке (уважая бюджет тика)
            for (int i = start; i < end; i++)
            {
                var f = _fibers.ResolvePacked(_pendingHandlers[i]);
                if (f != null && f.State == FiberState.Ready)
                    RunFiberNow(f);
            }
            _pendingHandlers.RemoveRange(start, end - start);

            // очередь спавнов дренируем только на верхнем уровне: при вложенном
            // Raise (хостовый метод посреди файбера) заспавненное внешним файбером
            // должно стартовать после ЕГО приостановки, а не сейчас
            if (_current == null) DrainRunQueue();
        }

        private void DrainRunQueue()
        {
            // крутим очередь, пока есть и работа, и бюджет; недоделанное
            // (в т.ч. перенесённые из-за бюджета файберы) остаётся до следующего Tick
            while (_runQueue.Count > 0 && _tickInstrLeft > 0)
            {
                var f = _fibers.ResolvePacked(_runQueue.Dequeue());
                if (f == null || f.State != FiberState.Ready) continue;
                RunFiberNow(f);
            }
            if (_runQueue.Count > 0) _tick.BudgetExhaustedThisTick = true;
        }

        private void RunFiberNow(Fiber f)
        {
            // если бюджета тика уже нет — не запускаем, откладываем на след. тик
            if (_tickInstrLeft <= 0)
            {
                RequeueReady(f);
                _tick.FibersDeferredThisTick++;
                return;
            }

            int cap = (int)Math.Min(MaxInstructionsPerFiberRun, _tickInstrLeft);

            // Raise может прийти из хостового метода ПОСРЕДИ исполнения другого
            // файбера — сохраняем и восстанавливаем текущий, иначе Kill(self)
            // внешнего файбера перестанет распознаваться и живой файбер уйдёт в пул.
            var prev = _current;
            _current = f;
            f.State = FiberState.Running;
            var r = _vm.Run(f, cap);
            _current = prev;

            int executed = _vm.LastInstructionCount;
            _tickInstrLeft -= executed;
            _tick.InstructionsThisTick += executed;
            f.InstrSinceYield += executed; // сбросится на кооперативной точке (Waited/Yielded)
            if (EnableProfiling && (uint)f.TriggerId < (uint)_trigStats.Length)
            {
                _trigStats[f.TriggerId].InstructionsThisTick += executed;
                _trigStats[f.TriggerId].InstructionsTotal += executed;
            }

            if (f.KillRequested)
            {
                _fibers.Return(f);
                return;
            }

            switch (r)
            {
                case RunResult.Completed:
                    _tick.FibersCompletedThisTick++;
                    _fibers.Return(f);
                    break;

                case RunResult.Errored:
                    _tick.FibersErroredThisTick++;
                    _fibers.Return(f);
                    break;

                case RunResult.Waited:
                    f.BudgetHits = 0; // кооперативная приостановка — не вечный цикл
                    f.InstrSinceYield = 0;
                    f.State = FiberState.Sleeping;
                    f.WakeTime = _time + Math.Max(0f, f.PendingWaitSeconds);
                    PushTimer(f.WakeTime, FiberPool.Pack(f));
                    break;

                case RunResult.Yielded:
                    f.BudgetHits = 0;
                    f.InstrSinceYield = 0;
                    f.State = FiberState.YieldedTick;
                    _nextTick.Add(FiberPool.Pack(f));
                    break;

                case RunResult.OutOfBudget:
                    HandleOutOfBudget(f);
                    break;
            }
        }

        private void RequeueReady(Fiber f)
        {
            f.State = FiberState.Ready;
            _runQueue.Enqueue(FiberPool.Pack(f));
        }

        private void HandleOutOfBudget(Fiber f)
        {
            f.BudgetHits++;

            // залипший цикл: много инструкций подряд БЕЗ единой кооперативной
            // точки — убиваем немедленно, не дожидаясь BudgetKillThreshold тиков
            // (иначе такой файбер жёг бы весь бюджет каждого кадра, вешая сервер)
            bool stuck = f.InstrSinceYield >= StuckInstructionLimit;

            bool kill = stuck
                     || Policy == BudgetPolicy.KillImmediately
                     || (Policy == BudgetPolicy.CarryOverThenKill && f.BudgetHits >= BudgetKillThreshold);

            if (kill)
            {
                int trig = f.TriggerId;
                if ((uint)trig < (uint)_trigStats.Length)
                {
                    _trigStats[trig].ErrorCount++;
                    _trigStats[trig].LastError = "убит бюджетом (вероятно, цикл без wait)";
                }
                _tick.FibersKilledThisTick++;
                _fibers.Return(f);
                if (stuck)
                    OnError?.Invoke($"Файбер убит: цикл без ожидания — {f.InstrSinceYield} инструкций " +
                                    "без единого wait/wait until (бесконечный while без эффективной паузы). " +
                                    "Добавьте wait в тело цикла или убедитесь, что условие wait until может стать ложным.");
                else
                    OnError?.Invoke($"Файбер убит: превышен бюджет инструкций {f.BudgetHits} тиков подряд " +
                                    "(похоже на бесконечный цикл без wait).");
                OnBudgetExceeded?.Invoke(trig);
            }
            else
            {
                RequeueReady(f);
                _tick.FibersDeferredThisTick++;
            }
        }

        // ===================================================================
        // Создание файберов
        // ===================================================================

        private Fiber CreateFiber(int funcIndex, Variant[] args, int argBase, int argc, int triggerId)
        {
            var f = _fibers.Rent();
            f.TriggerId = triggerId;

            var chunk = _prog.Functions[funcIndex];
            f.EnsureStack(chunk.LocalCount + 16);
            for (int i = 0; i < argc; i++) f.Stack[i] = args[argBase + i];
            for (int i = argc; i < chunk.LocalCount; i++) f.Stack[i] = Variant.Nil;
            f.Sp = chunk.LocalCount;
            f.PushFrame(funcIndex, 0);
            return f;
        }

        /// <summary>Вызывается VM на op Spawn: файбер стартует после текущего.</summary>
        internal Variant SpawnFiber(int funcIndex, Variant[] stack, int argBase, int argc, int triggerId)
        {
            // защита от спавн-бомбы: сверх лимита за тик — не создаём (бережём и память)
            if (_tick.FibersStartedThisTick >= MaxFibersStartedPerTick)
            {
                _tick.SpawnsDroppedThisTick++;
                if (_tick.SpawnsDroppedThisTick == 1) // предупреждаем один раз за тик
                    OnWarn?.Invoke($"Лимит спавнов за тик ({MaxFibersStartedPerTick}) исчерпан — часть spawn проигнорирована.");
                return Variant.Nil;
            }
            _tick.FibersStartedThisTick++;

            var f = CreateFiber(funcIndex, stack, argBase, argc, triggerId);
            // spawn из обработчика listener наследует контекст подписки:
            // заспавненный файбер видит self и поля, а detach убьёт и его
            var cur = _current;
            if (cur != null && cur.AttachIndex >= 0)
            {
                f.AttachIndex = cur.AttachIndex;
                f.AttachFields = cur.AttachFields;
                f.AttachSelf = cur.AttachSelf;
            }
            if ((uint)triggerId < (uint)_trigStats.Length) _trigStats[triggerId].FibersSpawned++;
            _runQueue.Enqueue(FiberPool.Pack(f));
            return f.Handle;
        }

        // ===================================================================
        // Engine.* — встроенные операции
        // ===================================================================

        internal Variant ExecEngineOp(EngineOp op, Fiber self, int argBase, int argc)
        {
            var stack = self.Stack;
            Variant Arg(int i) => stack[argBase + i];

            switch (op)
            {
                case EngineOp.EnableTrigger:
                {
                    int id = Arg(0).AsInt;
                    if ((uint)id < (uint)_triggerEnabled.Length) _triggerEnabled[id] = true;
                    return Variant.Nil;
                }
                case EngineOp.DisableTrigger:
                {
                    int id = Arg(0).AsInt;
                    if ((uint)id < (uint)_triggerEnabled.Length) _triggerEnabled[id] = false;
                    return Variant.Nil;
                }
                case EngineOp.IsTriggerEnabled:
                {
                    int id = Arg(0).AsInt;
                    return Variant.Bool((uint)id < (uint)_triggerEnabled.Length && _triggerEnabled[id]);
                }
                case EngineOp.ActivateTrigger:
                {
                    int id = Arg(0).AsInt;
                    return ActivateTrigger(id);
                }

                case EngineOp.Kill:
                    KillFiberHandle(Arg(0));
                    return Variant.Nil;

                case EngineOp.IsAlive:
                    return Variant.Bool(_fibers.ResolveHandle(Arg(0)) != null);

                case EngineOp.KillAll:
                {
                    int id = Arg(0).AsInt;
                    KillTriggerFibers(id);
                    return Variant.Nil;
                }

                case EngineOp.EnableModule:
                case EngineOp.DisableModule:
                {
                    var name = ResolveString(Arg(0));
                    if (name != null && _moduleIndex.TryGetValue(name, out int mi))
                        _moduleEnabled[mi] = op == EngineOp.EnableModule;
                    else
                        OnWarn?.Invoke($"Engine: модуль '{name}' не загружен.");
                    return Variant.Nil;
                }
                case EngineOp.IsModuleEnabled:
                {
                    var name = ResolveString(Arg(0));
                    return Variant.Bool(name != null && _moduleIndex.TryGetValue(name, out int mi) && _moduleEnabled[mi]);
                }
                case EngineOp.IsModuleLoaded:
                {
                    var name = ResolveString(Arg(0));
                    return Variant.Bool(name != null && _moduleIndex.ContainsKey(name));
                }

                case EngineOp.Time: return Variant.Float((float)_time);
                case EngineOp.DeltaTime: return Variant.Float(_dt);

                case EngineOp.Log: OnLog?.Invoke(ResolveString(Arg(0))); return Variant.Nil;
                case EngineOp.Warn: OnWarn?.Invoke(ResolveString(Arg(0))); return Variant.Nil;
                case EngineOp.Error: OnError?.Invoke(ResolveString(Arg(0))); return Variant.Nil;

                case EngineOp.IsValid:
                    return Variant.Bool(Entities.IsValid(Arg(0)));

                case EngineOp.Attach:
                    return AttachListener(Arg(0).AsInt, Arg(1));

                case EngineOp.Detach:
                {
                    var a = ResolveSub(Arg(0));
                    if (a != null) DetachCore(a);
                    return Variant.Nil;
                }

                case EngineOp.DetachAll:
                    DetachAllFor(Arg(0).AsInt, Arg(1));
                    return Variant.Nil;

                case EngineOp.IsSubscribed:
                    return Variant.Bool(ResolveSub(Arg(0)) != null);

                case EngineOp.TriggerExists:
                {
                    var name = ResolveString(Arg(0));
                    return Variant.Bool(name != null && _prog.TriggerIdByName.ContainsKey(name));
                }
                case EngineOp.ClassExists:
                {
                    var name = ResolveString(Arg(0));
                    return Variant.Bool(name != null && _prog.ClassNames.Contains(name));
                }

                default:
                    throw new ScriptError($"Неизвестная операция Engine: {op}.");
            }
        }

        /// <summary>Запустить action Do триггера отдельным файбером (или Nil, если нельзя).</summary>
        public Variant ActivateTrigger(int triggerId)
        {
            if (_prog == null || (uint)triggerId >= (uint)_prog.Triggers.Length) return Variant.Nil;
            if (!_triggerEnabled[triggerId]) return Variant.Nil; // выключен — активация игнорируется
            var info = _prog.Triggers[triggerId];
            if (!_moduleEnabled[info.ModuleIndex]) return Variant.Nil;
            if (info.ActionFuncIndex < 0) return Variant.Nil;    // нет action Do

            if (_tick.FibersStartedThisTick >= MaxFibersStartedPerTick)
            {
                _tick.SpawnsDroppedThisTick++;
                return Variant.Nil;
            }
            _tick.FibersStartedThisTick++;

            var f = CreateFiber(info.ActionFuncIndex, null, 0, 0, triggerId);
            if ((uint)triggerId < (uint)_trigStats.Length)
            {
                _trigStats[triggerId].FibersSpawned++;
                _trigStats[triggerId].LastFiredTime = _time;
            }
            _runQueue.Enqueue(FiberPool.Pack(f));
            return f.Handle;
        }

        private void KillFiberHandle(Variant handle)
        {
            var f = _fibers.ResolveHandle(handle);
            if (f == null) return;
            if (f == _current)
            {
                f.KillRequested = true; // VM остановится после текущего опкода
                return;
            }
            _fibers.Return(f); // версия бампается — записи в очередях/куче протухают
        }

        private void KillTriggerFibers(int triggerId)
        {
            for (int i = 0; i < _fibers.SlotCount; i++)
            {
                var f = _fibers.Slot(i);
                if (f == null || f.State == FiberState.Free || f.TriggerId != triggerId) continue;
                if (f == _current) { f.KillRequested = true; continue; }
                _fibers.Return(f);
            }
        }

        // ===================================================================
        // Подписки listener: attach / detach / диспатч-вспомогательное
        // ===================================================================

        private Fiber CreateAttachedFiber(int funcIndex, Attachment att, Variant[] args, int argBase, int argc)
        {
            var f = CreateFiber(funcIndex, args, argBase, argc, -1);
            f.AttachIndex = att.Index;
            f.AttachFields = att.Fields;
            f.AttachSelf = att.Target;
            return f;
        }

        private Attachment ResolveSub(Variant sub)
        {
            if (sub.Type != VariantType.Sub) return null;
            int idx = sub.Index;
            if ((uint)idx >= (uint)_attachments.Count) return null;
            var a = _attachments[idx];
            return a.Active && a.Version == sub.Version ? a : null;
        }

        internal Variant AttachListener(int listenerId, Variant target)
        {
            if (_prog == null || (uint)listenerId >= (uint)_prog.Listeners.Length) return Variant.Nil;
            if (target.Type != VariantType.Entity || !Entities.IsValid(target))
            {
                OnWarn?.Invoke("Engine.Attach: цель не является живой сущностью — подписка не создана.");
                return Variant.Nil;
            }
            var info = _prog.Listeners[listenerId];
            if (!_moduleEnabled[info.ModuleIndex]) return Variant.Nil;

            Attachment att;
            if (_freeAttachments.Count > 0) att = _attachments[_freeAttachments.Pop()];
            else { att = new Attachment { Index = _attachments.Count }; _attachments.Add(att); }
            att.ListenerId = listenerId;
            att.Target = target;
            att.TargetKey = PackEntity(target);
            att.Active = true;
            att.Finalizing = false;

            var pool = _attachFieldPools[listenerId];
            att.Fields = pool.Count > 0 ? pool.Pop()
                       : info.FieldCount > 0 ? new Variant[info.FieldCount]
                       : Array.Empty<Variant>();

            if (!_subsByEntity.TryGetValue(att.TargetKey, out var list))
                _subsByEntity[att.TargetKey] = list = new List<int>();
            list.Add(att.Index);
            _liveAttachments++;

            // сброс полей к инициализаторам — ВНЕ бюджета: это константные
            // выражения (мгновенно), а отложить их нельзя — обработчик события
            // мог бы прочитать мусор из переиспользованного пулом массива
            if (info.InitFuncIndex >= 0)
            {
                var fi = CreateAttachedFiber(info.InitFuncIndex, att, null, 0, 0);
                var prev = _current;
                _current = fi;
                fi.State = FiberState.Running;
                if (_vm.Run(fi, InitInstructionLimit) == RunResult.OutOfBudget)
                    OnError?.Invoke($"listener '{info.Name}': инициализация полей подписки не уложилась в " +
                                    $"{InitInstructionLimit} инструкций и прервана.");
                _current = prev;
                _fibers.Return(fi);
            }

            // OnSubscribe — обычный файбер подписки (может wait/spawn)
            if (info.OnSubscribeFunc >= 0)
            {
                var fs = CreateAttachedFiber(info.OnSubscribeFunc, att, null, 0, 0);
                RunFiberNow(fs);
            }

            return Variant.Sub(att.Index, att.Version);
        }

        internal void DetachAllFor(int listenerId, Variant target)
        {
            if (target.Type != VariantType.Entity) return;
            if (!_subsByEntity.TryGetValue(PackEntity(target), out var list)) return;
            _subScratch.Clear();
            _subScratch.AddRange(list); // DetachCore правит список — идём по снапшоту
            foreach (var idx in _subScratch)
            {
                var a = _attachments[idx];
                if (a.Active && a.ListenerId == listenerId) DetachCore(a);
            }
        }

        /// <summary>
        /// Полное уничтожение подписки: убить её файберы → OnUnsubscribe (синхронно,
        /// поля ещё живы) → снять с сущности → блок полей в пул, версия протухает.
        /// Если detach зовёт файбер этой же подписки — он получает KillRequested,
        /// VM остановит его сразу после текущей Engine-операции, к полям он больше
        /// не прикоснётся.
        /// </summary>
        private void DetachCore(Attachment att)
        {
            if (!att.Active || att.Finalizing) return;
            att.Finalizing = true;

            for (int i = 0; i < _fibers.SlotCount; i++)
            {
                var f = _fibers.Slot(i);
                if (f == null || f.State == FiberState.Free || f.AttachIndex != att.Index) continue;
                if (f == _current) { f.KillRequested = true; continue; }
                _fibers.Return(f);
            }

            var info = _prog.Listeners[att.ListenerId];
            if (info.OnUnsubscribeFunc >= 0)
            {
                // wait/spawn в OnUnsubscribe запрещены компилятором (E0178) —
                // страховка на случай обхода через func: незавершившийся файбер гасим
                var fu = CreateAttachedFiber(info.OnUnsubscribeFunc, att, null, 0, 0);
                var prev = _current;
                _current = fu;
                fu.State = FiberState.Running;
                var r = _vm.Run(fu, MaxInstructionsPerFiberRun);
                _current = prev;
                if (r != RunResult.Completed && r != RunResult.Errored)
                    OnError?.Invoke($"listener '{info.Name}'.OnUnsubscribe должен завершаться немедленно — файбер остановлен.");
                _fibers.Return(fu);
            }

            if (_subsByEntity.TryGetValue(att.TargetKey, out var list))
            {
                list.Remove(att.Index);
                if (list.Count == 0) _subsByEntity.Remove(att.TargetKey);
            }
            if (att.Fields != null && att.Fields.Length > 0)
                _attachFieldPools[att.ListenerId].Push(att.Fields);
            att.Fields = null;
            att.Active = false;
            att.Finalizing = false;
            att.Version++; // все хэндлы Subscription на неё протухают
            _freeAttachments.Push(att.Index);
            _liveAttachments--;
        }

        private void KillAllFibers()
        {
            for (int i = 0; i < _fibers.SlotCount; i++)
            {
                var f = _fibers.Slot(i);
                if (f == null || f.State == FiberState.Free) continue;
                if (f == _current) { f.KillRequested = true; continue; }
                _fibers.Return(f);
            }
        }

        // ===================================================================
        // Публичные удобства для хоста
        // ===================================================================

        /// <summary>
        /// Хост сообщает: объект умер. Сначала снимаются его подписки listener
        /// (OnUnsubscribe видит ещё живой хэндл — симметрично OnUnitDied), затем
        /// хэндлы протухают.
        /// </summary>
        public void InvalidateEntity(object o)
        {
            if (Entities.TryGetHandle(o, out var h)
                && _subsByEntity.TryGetValue(PackEntity(h), out var list))
            {
                _subScratch.Clear();
                _subScratch.AddRange(list);
                foreach (var idx in _subScratch)
                {
                    var a = _attachments[idx];
                    if (a.Active) DetachCore(a);
                }
            }
            Entities.Invalidate(o);
        }

        /// <summary>Включить/выключить триггер по имени (для редактора/консоли).</summary>
        public bool SetTriggerEnabled(string name, bool enabled)
        {
            if (_prog == null || !_prog.TriggerIdByName.TryGetValue(name, out int id)) return false;
            _triggerEnabled[id] = enabled;
            return true;
        }

        /// <summary>Mark-and-sweep динамических строк (совместимость; зовёт общий Collect).</summary>
        public int CollectStrings() => Collect().Strings;

        /// <summary>Что освободила сборка.</summary>
        public readonly struct SweepResult
        {
            public readonly int Strings;
            public readonly int Collections;
            public SweepResult(int strings, int collections) { Strings = strings; Collections = collections; }
        }

        // рабочий список трассировки: коллекции могут содержать коллекции.
        // Циклов быть не может (рекурсивный тип невыразим), но разделяемые ссылки
        // — да, поэтому MarkCollection отсекает повторную пометку.
        private readonly Stack<Variant> _gcWork = new Stack<Variant>();

        /// <summary>
        /// Общая сборка строк и коллекций: один обход корней, две пометки.
        /// Корни: статики, стеки живых файберов, снапшоты for-in, поля активных
        /// подписок, аргументы поднимаемого события.
        ///
        /// ЧТО КОРНЕМ НЕ ЯВЛЯЕТСЯ: Variant, который придержал у себя хостовый код
        /// между тиками. Благодаря версиям хэндлов это не тихая порча, а понятная
        /// ScriptError при следующем обращении — но полагаться на такой хэндл нельзя.
        /// </summary>
        public SweepResult Collect()
        {
            _gcWork.Clear();   // страховка: после исключения посреди трассировки список мог остаться грязным
            Strings.BeginSweep();
            Collections.BeginSweep();

            for (int i = 0; i < _statics.Length; i++) MarkValue(_statics[i]);

            for (int i = 0; i < _raiseArgCount && i < _raiseArgs.Length; i++) MarkValue(_raiseArgs[i]);

            for (int i = 0; i < _fibers.SlotCount; i++)
            {
                var f = _fibers.Slot(i);
                if (f == null || f.State == FiberState.Free) continue;
                for (int s = 0; s < f.Sp; s++) MarkValue(f.Stack[s]);

                // снапшоты активных for-in — ПОЛНОЦЕННЫЙ корень: элемент может быть
                // удалён из исходной коллекции прямо в теле цикла (что язык явно
                // разрешает), и тогда буфер файбера — единственная живая ссылка
                for (int d = 0; d < f.IterDepth && d < f.IterCounts.Length; d++)
                {
                    var buf = f.IterBufs[d];
                    if (buf == null) continue;
                    int n = f.IterCounts[d];
                    if (n > buf.Length) n = buf.Length;
                    for (int k = 0; k < n; k++) MarkValue(buf[k]);
                }
            }

            for (int i = 0; i < _attachments.Count; i++)
            {
                var a = _attachments[i];
                if (!a.Active || a.Fields == null) continue;
                for (int s = 0; s < a.Fields.Length; s++) MarkValue(a.Fields[s]);
            }

            TraceReachable();

            int coll = Collections.EndSweep();
            int strs = Strings.EndSweep();
            _tick.StringsSwept += strs;
            _tick.CollectionsSwept += coll;
            return new SweepResult(strs, coll);
        }

        private void MarkValue(Variant v)
        {
            switch (v.Type)
            {
                case VariantType.Str:
                    Strings.Mark(v.StrId);
                    break;
                case VariantType.Array:
                case VariantType.List:
                case VariantType.Map:
                    if (Collections.MarkCollection(v)) _gcWork.Push(v);
                    break;
            }
        }

        /// <summary>Транзитивный обход содержимого достижимых коллекций.</summary>
        private void TraceReachable()
        {
            while (_gcWork.Count > 0)
            {
                var c = _gcWork.Pop();
                switch (c.Type)
                {
                    case VariantType.Array:
                    {
                        var a = Collections.GetArrayData(c.CollId);
                        if (a == null) break;
                        for (int i = 0; i < a.Length; i++) MarkValue(a[i]);
                        break;
                    }
                    case VariantType.List:
                    {
                        Collections.GetListData(c.CollId, out var buf, out int n);
                        for (int i = 0; i < n; i++) MarkValue(buf[i]);
                        break;
                    }
                    default:
                    {
                        foreach (var kv in Collections.GetMapData(c.CollId))
                        {
                            MarkValue(kv.Key);
                            MarkValue(kv.Value);
                        }
                        break;
                    }
                }
            }
        }

        internal void ReportFiberError(Fiber f, string message)
        {
            if ((uint)f.TriggerId < (uint)_trigStats.Length)
            {
                _trigStats[f.TriggerId].ErrorCount++;
                _trigStats[f.TriggerId].LastError = message;
            }
            string trace = _vm.BuildTrace(f);
            OnError?.Invoke($"Ошибка скрипта: {message}\n{trace}");
        }

        // ===================================================================
        // Статистика / интроспекция
        // ===================================================================

        /// <summary>Снимок состояния движка. Дёшево, без аллокаций (value-тип).</summary>
        public EngineStats GetStats()
        {
            var s = _tick; // копия накопителей за текущий тик
            int running = 0, sleeping = 0, yielded = 0, ready = 0, live = 0;
            for (int i = 0; i < _fibers.SlotCount; i++)
            {
                var f = _fibers.Slot(i);
                if (f == null || f.State == FiberState.Free) continue;
                live++;
                switch (f.State)
                {
                    case FiberState.Running: running++; break;
                    case FiberState.Sleeping: sleeping++; break;
                    case FiberState.YieldedTick: yielded++; break;
                    case FiberState.Ready: ready++; break;
                }
            }
            s.LiveFibers = live;
            s.RunningFibers = running;
            s.SleepingFibers = sleeping;
            s.YieldedFibers = yielded;
            s.ReadyFibers = ready;
            s.DynamicStrings = Strings.DynamicCount;
            s.Collections = Collections.LiveCount;
            s.LiveSubscriptions = _liveAttachments;
            s.Time = _time;
            return s;
        }

        /// <summary>
        /// Пер-триггерная статистика в переданный список (переиспользуйте его,
        /// чтобы не аллоцировать каждый кадр). Живые файберы считаются одним
        /// проходом по пулу.
        /// </summary>
        public void GetTriggerStats(List<TriggerStats> into)
        {
            into.Clear();
            if (_prog == null) return;

            Array.Clear(_aliveByTrigger, 0, _aliveByTrigger.Length);
            for (int i = 0; i < _fibers.SlotCount; i++)
            {
                var f = _fibers.Slot(i);
                if (f == null || f.State == FiberState.Free) continue;
                if ((uint)f.TriggerId < (uint)_aliveByTrigger.Length) _aliveByTrigger[f.TriggerId]++;
            }

            for (int i = 0; i < _prog.Triggers.Length; i++)
            {
                var rs = _trigStats[i];
                var t = _prog.Triggers[i];
                into.Add(new TriggerStats
                {
                    TriggerId = i,
                    Name = t.Name,
                    Module = t.Module,
                    Enabled = _triggerEnabled[i],
                    TimesFired = rs.TimesFired,
                    FibersSpawned = rs.FibersSpawned,
                    FibersAlive = _aliveByTrigger[i],
                    LastFiredTime = rs.LastFiredTime,
                    ErrorCount = rs.ErrorCount,
                    LastError = rs.LastError,
                    InstructionsThisTick = rs.InstructionsThisTick,
                    InstructionsTotal = rs.InstructionsTotal,
                });
            }
        }

        /// <summary>Удобная перегрузка, аллоцирующая новый список (для разовых вызовов).</summary>
        public List<TriggerStats> GetTriggerStats()
        {
            var list = new List<TriggerStats>();
            GetTriggerStats(list);
            return list;
        }

        internal string EnumMemberName(int enumTypeId, int value)
        {
            if (Host.TryGetEnumById(enumTypeId, out var he))
                return (uint)value < (uint)he.Names.Length ? he.Names[value] : null;
            if (_prog != null)
            {
                int idx = enumTypeId - Host.EnumCount;
                if ((uint)idx < (uint)_prog.ScriptEnums.Length)
                {
                    var m = _prog.ScriptEnums[idx].Members;
                    return (uint)value < (uint)m.Length ? m[value] : null;
                }
            }
            return null;
        }

        // ===================================================================
        // IHostContext
        // ===================================================================

        public int InternString(string s) => Strings.Intern(s);
        public string ResolveString(Variant v) => v.IsNil ? null : Strings.Get(v.StrId);
        public Variant WrapObject(object o) => Entities.Register(o);
        public object ResolveObject(Variant v) => Entities.Resolve(v);

        // ===================================================================
        // Мин-куча таймеров
        // ===================================================================

        private void PushTimer(double time, long fiber)
        {
            // страховка инварианта кучи: NaN несравним, поэтому попав внутрь он
            // остаётся там навсегда и блокирует пробуждения, пока сидит в корне.
            // Основная проверка стоит в опкоде Wait, эта — чтобы дыру нельзя было
            // открыть вообще ниоткуда.
            if (double.IsNaN(time)) time = _time;
            if (_timerCount >= _timerTime.Length)
            {
                Array.Resize(ref _timerTime, _timerTime.Length * 2);
                Array.Resize(ref _timerFiber, _timerFiber.Length * 2);
            }
            int i = _timerCount++;
            _timerTime[i] = time;
            _timerFiber[i] = fiber;
            // всплытие
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (_timerTime[p] <= _timerTime[i]) break;
                (_timerTime[p], _timerTime[i]) = (_timerTime[i], _timerTime[p]);
                (_timerFiber[p], _timerFiber[i]) = (_timerFiber[i], _timerFiber[p]);
                i = p;
            }
        }

        private long PopTimer()
        {
            long top = _timerFiber[0];
            _timerCount--;
            _timerTime[0] = _timerTime[_timerCount];
            _timerFiber[0] = _timerFiber[_timerCount];
            // просейка вниз
            int i = 0;
            while (true)
            {
                int l = i * 2 + 1, r = l + 1, m = i;
                if (l < _timerCount && _timerTime[l] < _timerTime[m]) m = l;
                if (r < _timerCount && _timerTime[r] < _timerTime[m]) m = r;
                if (m == i) break;
                (_timerTime[m], _timerTime[i]) = (_timerTime[i], _timerTime[m]);
                (_timerFiber[m], _timerFiber[i]) = (_timerFiber[i], _timerFiber[m]);
                i = m;
            }
            return top;
        }
    }
}
