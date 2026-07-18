namespace Dsl.Tools.Lsp
{
    /// <summary>Документация встроенного класса Engine (для комплишенов и hover).</summary>
    public sealed class EngineMethod
    {
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
                return $"Engine.{Name}({string.Join(", ", ps)}) -> {Returns}";
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

        public static readonly string[] Keywords =
        {
            "trigger", "class", "enum", "listener", "self", "pass", "disabled", "func", "action", "event",
            "const", "var", "if", "else", "while", "for", "in", "break", "continue", "return",
            "wait", "until", "spawn", "new", "true", "false", "null",
        };

        public static readonly string[] Types =
        {
            "int", "float", "bool", "string", "void", "Fiber", "Subscription", "List", "Map",
        };
    }
}
