using Dsl.Text;

namespace Dsl.Tooling
{
    /// <summary>Документация встроенного класса Engine (для комплишенов и hover).</summary>
    public sealed class EngineMethod
    {
        public string Owner = "Engine";   // встроенный класс: Engine или Math
        public string Name;
        public DocText Summary;
        public string Returns;
        public (string name, string type)[] Params;

        /// <summary>Сигнатура (русские подписи типов).</summary>
        public string Signature => SignatureFor(Language.Ru);

        /// <summary>Сигнатура с подписями типов на языке <paramref name="lang"/> («число» → «number»).</summary>
        public string SignatureFor(Language lang)
        {
            var ps = new string[Params.Length];
            for (int i = 0; i < Params.Length; i++) ps[i] = $"{EngineDocs.TypeLabel(Params[i].type, lang)} {Params[i].name}";
            return $"{Owner}.{Name}({string.Join(", ", ps)}) -> {EngineDocs.TypeLabel(Returns, lang)}";
        }
    }

    public static class EngineDocs
    {
        private static (string, string)[] P(params (string, string)[] p) => p;

        /// <summary>«число» в сигнатурах встроенного Math — int, float или double.</summary>
        public const string NumberType = "число";

        /// <summary>Подпись типа на языке: «число» по-английски — «number», остальные типы как есть.</summary>
        public static string TypeLabel(string type, Language lang)
            => lang == Language.En && type == NumberType ? "number" : type;

        public static readonly EngineMethod[] Methods =
        {
            new EngineMethod { Name = "EnableTrigger", Summary = DocText.Of("Включить триггер.", "Enable the trigger."), Returns = "void", Params = P(("trigger", "trigger")) },
            new EngineMethod { Name = "DisableTrigger", Summary = DocText.Of("Выключить триггер.", "Disable the trigger."), Returns = "void", Params = P(("trigger", "trigger")) },
            new EngineMethod { Name = "IsTriggerEnabled", Summary = DocText.Of("Включён ли триггер.", "Whether the trigger is enabled."), Returns = "bool", Params = P(("trigger", "trigger")) },
            new EngineMethod { Name = "ActivateTrigger", Summary = DocText.Of("Запустить action Do триггера отдельным файбером.", "Run the trigger's action Do as a separate fiber."), Returns = "Fiber", Params = P(("trigger", "trigger")) },
            new EngineMethod { Name = "KillAll", Summary = DocText.Of("Убить все файберы триггера.", "Kill all fibers of the trigger."), Returns = "void", Params = P(("trigger", "trigger")) },
            new EngineMethod { Name = "Kill", Summary = DocText.Of("Убить файбер.", "Kill the fiber."), Returns = "void", Params = P(("fiber", "Fiber")) },
            new EngineMethod { Name = "IsAlive", Summary = DocText.Of("Жив ли файбер.", "Whether the fiber is alive."), Returns = "bool", Params = P(("fiber", "Fiber")) },
            new EngineMethod { Name = "EnableModule", Summary = DocText.Of("Включить модуль.", "Enable the module."), Returns = "void", Params = P(("name", "string")) },
            new EngineMethod { Name = "DisableModule", Summary = DocText.Of("Выключить модуль.", "Disable the module."), Returns = "void", Params = P(("name", "string")) },
            new EngineMethod { Name = "IsModuleEnabled", Summary = DocText.Of("Включён ли модуль.", "Whether the module is enabled."), Returns = "bool", Params = P(("name", "string")) },
            new EngineMethod { Name = "IsModuleLoaded", Summary = DocText.Of("Загружен ли модуль.", "Whether the module is loaded."), Returns = "bool", Params = P(("name", "string")) },
            new EngineMethod { Name = "Time", Summary = DocText.Of("Время с загрузки, сек.", "Time since load, seconds."), Returns = "float", Params = P() },
            new EngineMethod { Name = "DeltaTime", Summary = DocText.Of("Длительность последнего тика, сек.", "Duration of the last tick, seconds."), Returns = "float", Params = P() },
            new EngineMethod { Name = "Log", Summary = DocText.Of("Сообщение в лог.", "Message to the log."), Returns = "void", Params = P(("message", "string")) },
            new EngineMethod { Name = "Warn", Summary = DocText.Of("Предупреждение в лог.", "Warning to the log."), Returns = "void", Params = P(("message", "string")) },
            new EngineMethod { Name = "Error", Summary = DocText.Of("Ошибка в лог.", "Error to the log."), Returns = "void", Params = P(("message", "string")) },
            new EngineMethod { Name = "IsValid", Summary = DocText.Of("Жив ли хэндл сущности.", "Whether the entity handle is alive."), Returns = "bool", Params = P(("entity", "entity")) },
            new EngineMethod { Name = "Attach", Summary = DocText.Of("Подписать listener на сущность; возвращает хэндл подписки.",
                "Subscribe a listener to an entity; returns the subscription handle."), Returns = "Subscription", Params = P(("listener", "listener"), ("entity", "entity")) },
            new EngineMethod { Name = "Detach", Summary = DocText.Of("Снять одну подписку по хэндлу.", "Remove one subscription by its handle."), Returns = "void", Params = P(("sub", "Subscription")) },
            new EngineMethod { Name = "DetachAll", Summary = DocText.Of("Снять все подписки этого listener с сущности.",
                "Remove all subscriptions of this listener from the entity."), Returns = "void", Params = P(("listener", "listener"), ("entity", "entity")) },
            new EngineMethod { Name = "IsSubscribed", Summary = DocText.Of("Жива ли подписка.", "Whether the subscription is alive."), Returns = "bool", Params = P(("sub", "Subscription")) },
            new EngineMethod { Name = "TriggerExists", Summary = DocText.Of("Существует ли триггер с таким именем.", "Whether a trigger with this name exists."), Returns = "bool", Params = P(("name", "string")) },
            new EngineMethod { Name = "ClassExists", Summary = DocText.Of("Существует ли класс с таким именем.", "Whether a class with this name exists."), Returns = "bool", Params = P(("name", "string")) },
        };

        private static EngineMethod M(string name, string returns, DocText summary, params (string, string)[] ps) =>
            new EngineMethod { Owner = "Math", Name = name, Returns = returns, Summary = summary, Params = ps };

        /// <summary>
        /// Встроенный Math. «число» — int, float или double: тип результата — общий
        /// тип аргументов (Min(1, 2.5) → float). Если игра объявила свой API Math,
        /// работает он, а не этот.
        /// </summary>
        public static readonly EngineMethod[] MathMethods =
        {
            M("Min", "число", DocText.Of("Меньшее из двух. Тип — общий тип аргументов.",
                "The smaller of two. The type is the common type of the arguments."), ("a", "число"), ("b", "число")),
            M("Max", "число", DocText.Of("Большее из двух. Тип — общий тип аргументов.",
                "The larger of two. The type is the common type of the arguments."), ("a", "число"), ("b", "число")),
            M("Clamp", "число", DocText.Of("Зажать value в [min, max]. min > max — ошибка скрипта.",
                "Clamp value to [min, max]. min > max is a script error."), ("value", "число"), ("min", "число"), ("max", "число")),
            M("Abs", "число", DocText.Of("Модуль. Math.Abs(-2147483648) — ошибка: в int не помещается.",
                "Absolute value. Math.Abs(-2147483648) is an error: it does not fit in int."), ("x", "число")),
            M("Sign", "int", DocText.Of("Знак: -1, 0 или 1.", "Sign: -1, 0 or 1."), ("x", "число")),
            M("Floor", "int", DocText.Of("Округление вниз до int (как Mathf.FloorToInt). Вне диапазона int — ошибка.",
                "Round down to int (like Mathf.FloorToInt). Outside the int range is an error."), ("x", "число")),
            M("Ceil", "int", DocText.Of("Округление вверх до int (как Mathf.CeilToInt). Вне диапазона int — ошибка.",
                "Round up to int (like Mathf.CeilToInt). Outside the int range is an error."), ("x", "число")),
            M("Round", "int", DocText.Of("Округление до ближайшего int; половина — от нуля: 2.5 → 3, -2.5 → -3.",
                "Round to the nearest int; halves away from zero: 2.5 → 3, -2.5 → -3."), ("x", "число")),
            M("Sqrt", "float", DocText.Of("Квадратный корень (double для double). Отрицательный аргумент — ошибка, а не NaN.",
                "Square root (double for double). A negative argument is an error, not NaN."), ("x", "число")),
            M("Pow", "float", DocText.Of("x в степени y (double для double). Нечисловой или бесконечный результат — ошибка.",
                "x to the power of y (double for double). A non-numeric or infinite result is an error."), ("x", "число"), ("y", "число")),
            M("Lerp", "float", DocText.Of("a + (b - a) * t, t зажат в [0, 1] — как Mathf.Lerp.",
                "a + (b - a) * t, t clamped to [0, 1] — like Mathf.Lerp."), ("a", "число"), ("b", "число"), ("t", "число")),
        };

        /// <summary>Константы встроенного Math (читаются без скобок).</summary>
        public static readonly (string name, string type, DocText doc)[] MathConsts =
        {
            ("PI", "float", DocText.Of("Число π (float). Сворачивается в литерал.", "The number π (float). Folds into a literal.")),
        };

        public static readonly string[] Keywords =
        {
            "trigger", "class", "enum", "listener", "namespace", "self", "pass", "disabled", "func", "action", "event",
            "const", "readonly", "var", "if", "else", "while", "loop", "for", "in", "break", "continue", "return",
            "wait", "until", "yield", "spawn", "new", "true", "false", "null",
        };
        // before/after/replace/base сюда НЕ входят: список красит слова безусловно
        // (подсветка встроенной IDE), а эти — контекстные, и поле `after` — имя.
        // В автодополнение они попадают из ChainWords.

        /// <summary>
        /// Слова мерж-цепочки (контекстные: вне своей позиции — обычные имена).
        /// Показываются в hover — модер встречает их в чужом коде и должен
        /// понять порядок, не открывая документацию.
        /// </summary>
        public static readonly (string word, DocText doc)[] ChainWords =
        {
            ("before", DocText.Of("Слой ДО ядра члена: `before event X(...)` / `before func F(...)`. " +
                       "Ядро выполнится после него в том же файбере (wait в слое задержит ядро). " +
                       "Слои идут в порядке загрузки модулей; обычное переопределение их не трогает.",
                "Layer BEFORE the member core: `before event X(...)` / `before func F(...)`. " +
                "The core runs after it in the same fiber (a wait in the layer delays the core). " +
                "Layers go in module load order; a regular override does not touch them.")),
            ("after", DocText.Of("Слой ПОСЛЕ ядра члена: `after event X(...)` / `after func F(...)`. " +
                      "Функции с результатом слой может не объявлять результат — вызывающий получит результат ядра.",
                "Layer AFTER the member core: `after event X(...)` / `after func F(...)`. " +
                "For functions with a result the layer may omit the result — the caller gets the core's result.")),
            ("replace", DocText.Of("Стирает ВСЁ, что объявлено раньше (ядро и слои), и становится новым ядром. " +
                        "Слои, загруженные позже, ложатся поверх. `replace event X(...);` без тела глушит член целиком. " +
                        "Выключенный модуль ничего не стирает.",
                "Erases EVERYTHING declared earlier (the core and layers) and becomes the new core. " +
                "Layers loaded later go on top. `replace event X(...);` without a body silences the member entirely. " +
                "A disabled module erases nothing.")),
            ("base", DocText.Of("`base(...)` в обычном переопределении вызывает предыдущую версию того же члена " +
                     "(можно с другими аргументами и с результатом). Недоступен в before/after и replace.",
                "`base(...)` in a regular override calls the previous version of the same member " +
                "(possibly with other arguments and with a result). Not available in before/after and replace.")),
        };

        public static readonly string[] Types =
        {
            "int", "float", "double", "bool", "string", "void", "Fiber", "Subscription", "List", "Map",
        };
    }
}
