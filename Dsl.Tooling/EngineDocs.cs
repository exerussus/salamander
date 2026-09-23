namespace Dsl.Tooling
{
    /// <summary>Документация встроенного класса Engine (для комплишенов и hover).</summary>
    public sealed class EngineMethod
    {
        public string Owner = "Engine";   // встроенный класс: Engine или Math
        public string Name;
        public string Summary;
        public string Returns;
        public (string name, string type)[] Params;

        public string Signature
        {
            get
            {
                var ps = new string[Params.Length];
                for (int i = 0; i < Params.Length; i++) ps[i] = $"{Params[i].type} {Params[i].name}";
                return $"{Owner}.{Name}({string.Join(", ", ps)}) -> {Returns}";
            }
        }
    }

    public static class EngineDocs
    {
        private static (string, string)[] P(params (string, string)[] p) => p;

        public static readonly EngineMethod[] Methods =
        {
            new EngineMethod { Name = "EnableTrigger", Summary = "Включить триггер.", Returns = "void", Params = P(("trigger", "trigger")) },
            new EngineMethod { Name = "DisableTrigger", Summary = "Выключить триггер.", Returns = "void", Params = P(("trigger", "trigger")) },
            new EngineMethod { Name = "IsTriggerEnabled", Summary = "Включён ли триггер.", Returns = "bool", Params = P(("trigger", "trigger")) },
            new EngineMethod { Name = "ActivateTrigger", Summary = "Запустить action Do триггера отдельным файбером.", Returns = "Fiber", Params = P(("trigger", "trigger")) },
            new EngineMethod { Name = "KillAll", Summary = "Убить все файберы триггера.", Returns = "void", Params = P(("trigger", "trigger")) },
            new EngineMethod { Name = "Kill", Summary = "Убить файбер.", Returns = "void", Params = P(("fiber", "Fiber")) },
            new EngineMethod { Name = "IsAlive", Summary = "Жив ли файбер.", Returns = "bool", Params = P(("fiber", "Fiber")) },
            new EngineMethod { Name = "EnableModule", Summary = "Включить модуль.", Returns = "void", Params = P(("name", "string")) },
            new EngineMethod { Name = "DisableModule", Summary = "Выключить модуль.", Returns = "void", Params = P(("name", "string")) },
            new EngineMethod { Name = "IsModuleEnabled", Summary = "Включён ли модуль.", Returns = "bool", Params = P(("name", "string")) },
            new EngineMethod { Name = "IsModuleLoaded", Summary = "Загружен ли модуль.", Returns = "bool", Params = P(("name", "string")) },
            new EngineMethod { Name = "Time", Summary = "Время с загрузки, сек.", Returns = "float", Params = P() },
            new EngineMethod { Name = "DeltaTime", Summary = "Длительность последнего тика, сек.", Returns = "float", Params = P() },
            new EngineMethod { Name = "Log", Summary = "Сообщение в лог.", Returns = "void", Params = P(("message", "string")) },
            new EngineMethod { Name = "Warn", Summary = "Предупреждение в лог.", Returns = "void", Params = P(("message", "string")) },
            new EngineMethod { Name = "Error", Summary = "Ошибка в лог.", Returns = "void", Params = P(("message", "string")) },
            new EngineMethod { Name = "IsValid", Summary = "Жив ли хэндл сущности.", Returns = "bool", Params = P(("entity", "entity")) },
            new EngineMethod { Name = "Attach", Summary = "Подписать listener на сущность; возвращает хэндл подписки.", Returns = "Subscription", Params = P(("listener", "listener"), ("entity", "entity")) },
            new EngineMethod { Name = "Detach", Summary = "Снять одну подписку по хэндлу.", Returns = "void", Params = P(("sub", "Subscription")) },
            new EngineMethod { Name = "DetachAll", Summary = "Снять все подписки этого listener с сущности.", Returns = "void", Params = P(("listener", "listener"), ("entity", "entity")) },
            new EngineMethod { Name = "IsSubscribed", Summary = "Жива ли подписка.", Returns = "bool", Params = P(("sub", "Subscription")) },
            new EngineMethod { Name = "TriggerExists", Summary = "Существует ли триггер с таким именем.", Returns = "bool", Params = P(("name", "string")) },
            new EngineMethod { Name = "ClassExists", Summary = "Существует ли класс с таким именем.", Returns = "bool", Params = P(("name", "string")) },
        };

        private static EngineMethod M(string name, string returns, string summary, params (string, string)[] ps) =>
            new EngineMethod { Owner = "Math", Name = name, Returns = returns, Summary = summary, Params = ps };

        /// <summary>
        /// Встроенный Math. «число» — int, float или double: тип результата — общий
        /// тип аргументов (Min(1, 2.5) → float). Если игра объявила свой API Math,
        /// работает он, а не этот.
        /// </summary>
        public static readonly EngineMethod[] MathMethods =
        {
            M("Min", "число", "Меньшее из двух. Тип — общий тип аргументов.", ("a", "число"), ("b", "число")),
            M("Max", "число", "Большее из двух. Тип — общий тип аргументов.", ("a", "число"), ("b", "число")),
            M("Clamp", "число", "Зажать value в [min, max]. min > max — ошибка скрипта.", ("value", "число"), ("min", "число"), ("max", "число")),
            M("Abs", "число", "Модуль. Math.Abs(-2147483648) — ошибка: в int не помещается.", ("x", "число")),
            M("Sign", "int", "Знак: -1, 0 или 1.", ("x", "число")),
            M("Floor", "int", "Округление вниз до int (как Mathf.FloorToInt). Вне диапазона int — ошибка.", ("x", "число")),
            M("Ceil", "int", "Округление вверх до int (как Mathf.CeilToInt). Вне диапазона int — ошибка.", ("x", "число")),
            M("Round", "int", "Округление до ближайшего int; половина — от нуля: 2.5 → 3, -2.5 → -3.", ("x", "число")),
            M("Sqrt", "float", "Квадратный корень (double для double). Отрицательный аргумент — ошибка, а не NaN.", ("x", "число")),
            M("Pow", "float", "x в степени y (double для double). Нечисловой или бесконечный результат — ошибка.", ("x", "число"), ("y", "число")),
            M("Lerp", "float", "a + (b - a) * t, t зажат в [0, 1] — как Mathf.Lerp.", ("a", "число"), ("b", "число"), ("t", "число")),
        };

        /// <summary>Константы встроенного Math (читаются без скобок).</summary>
        public static readonly (string name, string type, string doc)[] MathConsts =
        {
            ("PI", "float", "Число π (float). Сворачивается в литерал."),
        };

        public static readonly string[] Keywords =
        {
            "trigger", "class", "enum", "listener", "self", "pass", "disabled", "func", "action", "event",
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
        public static readonly (string word, string doc)[] ChainWords =
        {
            ("before", "Слой ДО ядра члена: `before event X(...)` / `before func F(...)`. " +
                       "Ядро выполнится после него в том же файбере (wait в слое задержит ядро). " +
                       "Слои идут в порядке загрузки модулей; обычное переопределение их не трогает."),
            ("after", "Слой ПОСЛЕ ядра члена: `after event X(...)` / `after func F(...)`. " +
                      "Функции с результатом слой может не объявлять результат — вызывающий получит результат ядра."),
            ("replace", "Стирает ВСЁ, что объявлено раньше (ядро и слои), и становится новым ядром. " +
                        "Слои, загруженные позже, ложатся поверх. `replace event X(...);` без тела глушит член целиком. " +
                        "Выключенный модуль ничего не стирает."),
            ("base", "`base(...)` в обычном переопределении вызывает предыдущую версию того же члена " +
                     "(можно с другими аргументами и с результатом). Недоступен в before/after и replace."),
        };

        public static readonly string[] Types =
        {
            "int", "float", "double", "bool", "string", "void", "Fiber", "Subscription", "List", "Map",
        };
    }
}
