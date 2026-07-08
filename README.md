# Salamander — скриптовый DSL (W3-стиль) для Unity

Статически типизированный скриптовый язык с триггерами, событиями и
кооперативными файберами (`wait` / `spawn`), собственный лексер → парсер →
чекер → байткод-VM. Ядро не зависит от Unity и работает на всех платформах,
включая WebGL/IL2CPP (никакой кодогенерации в рантайме).

## Структура

Три зоны (подробности и пошаговая интеграция — в `INTEGRATION.md`):
**A** — рантайм, входит в билд Unity; **B** — только редактор Unity;
**C** — инструменты, в Unity не попадают.

```
Dsl.Core/            [A] ядро (asmdef без ссылок на движок; Newtonsoft для манифестов)
  Text/              позиции, диагностика, токены, лексер
  Syntax/            AST, парсер
  Semantics/         HostRegistry (биндинги), символы, чекер (статическая типизация)
  Hosting/           fluent-обёртка регистрации: HostBuilder/ClassBuilder/ApiBuilder/EventRef
  Codegen/           опкоды, компилятор AST→байткод, CompiledProgram
  Runtime/           Variant, строки (интерн + свип), сущности, коллекции,
                     файберы, VM, ScriptEngine (планировщик/события/триггеры)
  Compilation/       манифест модуля, драйвер компиляции, ApiManifest (экспорт API)
Dsl.Unity/           [A] адаптер: ScriptHostBootstrap (тик-насос), провайдер исходников
Dsl.Unity/Editor/    [B] импортёр .sal → TextAsset (editor-only, в билд не входит)
Dsl.Tests/           [B] NUnit-тесты (editor-only, в билд не входят)
Tools/DslCheck/      [C] CLI-чекер (тот же компилятор вне игры; для CI и редактора)
Tools/vscode-salamander/ [C] расширение VS Code (подсветка, ошибки, автодополнение)
Examples/UnityRpg/   [A] пример игровой обвязки + модуль скриптов (StreamingAssets)
Examples/DemoHost/   [C] чистый C#-хост: dotnet run — полная проводка без Unity
Examples/mymod/      пример модуля (манифест + скрипты)
modkit/              [C] готовая среда для модеров (VS Code + чекер + пример + доки)
INTEGRATION.md       что кладётся в Unity, что нет; экспорт манифеста; сборка модкита
STATS.md             бюджет исполнения и статистика: настройки, поля, профайлинг
```

## Быстрый старт в Unity

1. Скопируйте `Dsl.Core` и `Dsl.Unity` в `Assets/` (нужен пакет
   `com.unity.nuget.newtonsoft-json`).
2. Наследуйтесь от `ScriptHostBootstrap`, переопределите `ConfigureHost` и
   зарегистрируйте API игры через fluent `HostBuilder` (типы выводятся из
   лямбд — см. пример ниже). Сырой `HostRegistry` остаётся доступен для
   случаев, которые обёртка не покрывает (пример — `Examples/DemoHost`).
3. Для сервера FishNet переопределите `IsAuthority => netObject.IsServerInitialized`.
4. Положите модули в `StreamingAssets/Scripts/<имя>/module.json` + исходники.
   Правка файлов на диске перекомпилирует программу на лету; при ошибке
   компиляции продолжает работать старая версия.

## Язык за минуту

```
enum Phase { Intro, Combat }

class Balance {                       // классы статичны, экземпляров нет
    const float K = 2.5;
    int counter = 0;
    func Scale(float x) -> float { return x * K; }
}

trigger BossFight {                   // триггер ОБЯЗАН иметь хотя бы один event
    bool started = false;

    action Do() {                     // Engine.ActivateTrigger(BossFight) запускает
        wait 2.0;                     // это ОТДЕЛЬНЫМ файбером — wait разрешён
        SpawnApi.SpawnWave(6);
    }

    event OnUnitDamageTaken(Unit src, Unit dst, float amount, DamageType t) {
        if (!started) { started = true; Engine.ActivateTrigger(BossFight); }
        spawn Watch(dst);             // параллельный файбер
    }

    func Watch(Unit u) {
        wait until UnitApi.Health(u) <= 0.0;   // проверка раз в тик
        Engine.Log($"умер: {u.name}");
    }
}
```

Встроенный класс `Engine`: `EnableTrigger/DisableTrigger/IsTriggerEnabled/
ActivateTrigger/KillAll(триггер)`, `Kill/IsAlive(Fiber)`, `EnableModule/
DisableModule/IsModuleEnabled/IsModuleLoaded(строка)`, `Time()/DeltaTime()`,
`Log/Warn/Error`, `IsValid(сущность)`, `TriggerExists/ClassExists(строка)`.

## Пример: РПГ-симуляция боя (Unity, быстрый старт)

Готовый пример лежит в `Examples/UnityRpg/` — хост-файл, модуль скриптов в
структуре `StreamingAssets` и пошаговый README. Развёртывание: Newtonsoft из
UPM → `Dsl.Core` + `Dsl.Unity` в `Assets/` → `RpgSimulation.cs` в `Assets/` →
папку `StreamingAssets` примера в `Assets/` → пустой GameObject с компонентом
**RpgSimulation** → Play. Весь бой идёт логами в консоль.

**Сторона хоста** (`RpgSimulation.cs`, наследник `ScriptHostBootstrap`) —
владеет состоянием и «физикой» боя, а скриптам открывает API через fluent
`HostBuilder`: типы выводятся из лямбд, никаких Variant, кастов и ручных id:

```csharp
private EventRef<Unit, Unit> _evDied; // типизированное событие

protected override void ConfigureHost(HostRegistry registry)
{
    var host = new HostBuilder(registry);

    host.Enum<Team>()                       // C#-енум становится скриптовым
        .Enum<DamageType>();

    host.Class<Unit>()                      // сущность + свойства из лямбд
        .Prop("name",   u => u.Name)
        .Prop("health", u => u.Health);

    host.Api("UnitApi")                     // методы — обычные Func/Action
        .Fn("IsBoss", (Unit u) => u != null && u.IsBoss)
        .Act("Heal",  (Unit u, float hp) => u.Health += hp);

    host.Api("BattleApi")
        .Fn("SpawnUnit", (string name, Team team, float hp, float dmg) =>
            SpawnUnit(name, team, hp, dmg));

    _evDied = host.Event<Unit, Unit>("OnUnitDied");
}

// в игровом цикле: факт произошёл у хоста → поднимаем типизированное событие
_evDied.Raise(Engine, target, attacker);
Engine.InvalidateEntity(target); // после события — все хэндлы протухают
```

**Сторона скриптов** (`StreamingAssets/Scripts/rpg_demo/src/battle.sal`) —
реагирует на события и режиссирует бой; правится на лету во время Play:

```
trigger BossRage
{
    bool raging = false;

    event OnUnitDamageTaken(Unit source, Unit target, float amount, DamageType type)
    {
        if (raging || !UnitApi.IsBoss(target)) { return; }
        if (target.health / target.maxHealth > 0.4) { return; }
        raging = true;
        UnitApi.Buff(target, 2.0);
        spawn RegenLoop(target);      // параллельный файбер
    }

    func RegenLoop(Unit boss)
    {
        while (Engine.IsValid(boss))  // сам гаснет со смертью босса
        {
            UnitApi.Heal(boss, 12.0);
            wait 2.0;
        }
    }
}
```

Разграничение простое: хост решает, ЧТО произошло (урон посчитан, юнит умер),
скрипты решают, ЧТО С ЭТИМ ДЕЛАТЬ (ярость, подкрепление, реплики). Полные
файлы и ожидаемый вывод консоли — в `Examples/UnityRpg/README.md`.

## Listener: подписка на конкретную сущность

Помимо глобальных триггеров есть `listener` — шаблон подписки на события ОДНОЙ
сущности (пер-юнитная логика без объектов в куче): `Engine.Attach(L, unit)` →
`Subscription`; внутри обработчиков доступны `self` и собственные поля подписки
(блок пулируется — ноль аллокаций в установившемся режиме); субъект — всегда
первый параметр события; `Detach`/смерть цели убивают файберы подписки и зовут
`OnUnsubscribe`. Подробности — в скилле (references/language.md §9.5).

## Архетипы: механики контента по шаблону (spell/item/...)

Хост объявляет виды сущностей и их события (`host.Archetype("spell").Event<...>`),
скрипты описывают механики блоками `spell fireball { event OnCast(...) {...} }` —
id тот же, что в контентных манифестах игры. Диспатч адресный: игра резолвит id
в int один раз при загрузке контента и поднимает события по хэндлу (горячий путь
без строк). Поздний блок переопределяет ранний ПО-СОБЫТИЙНО (мод меняет только
OnCast, остальное живёт; убить реализацию — `pass;` или прототип `event X(...);`).
API сборщика: `HasArchetype`/`GetArchetypeIds`; опциональные KnownIds ловят
опечатки в id на компиляции. Подробности — в скилле (language.md §9.6).

## Поддержка VS Code: подсветка, ошибки, автодополнение

В `Tools/` лежат два инструмента, связанных одним файлом — `salamander-api.json`
(манифест API хоста). Игра выгружает его автоматически при Play в редакторе
(рядом с модулями), так что тулинг никогда не расходится с реальным API:

- **`Tools/vscode-salamander/`** — расширение VS Code: TextMate-подсветка
  (включая интерполяцию `$"...{x}..."`), сниппеты, автодополнение
  (`Engine.` → встроенные методы, `UnitApi.` → методы с аргументами,
  `Team.` → элементы енума, `event ` → готовые сигнатуры хостовых событий,
  плюс символы из `.sal`-файлов воркспейса), hover с сигнатурами.
  Установка: `npm install && npm run compile`, дальше F5 или VSIX —
  подробности в `Tools/vscode-salamander/README.md`.
- **`Tools/DslCheck/`** — CLI-чекер: тот же компилятор, что в игре
  (`dotnet build`, затем `dotnet DslCheck.dll <папка модулей> [--json]`).
  Расширение запускает его при сохранении и подсвечивает ошибки прямо
  в файлах с точными file:line:col; работает и в CI (код возврата 1 при
  ошибках).

Для РПГ-примера готовый `salamander-api.json` уже лежит рядом с модулем —
автодополнение работает сразу, до первого запуска игры.

## Гарантии и осознанные упрощения v1

- Ошибка в файбере убивает только этот файбер; движок и остальные скрипты живут.
  Хост получает сообщение со скриптовым стектрейсом `файл:строка`.
- Протухший хэндл сущности (юнит умер) — скриптовая ошибка, не краш.
- Порядок обработчиков детерминирован: модуль → файл → объявление.
- `RaiseEvent` исполняет обработчики немедленно до первой приостановки (W3).
- Горячий путь без аллокаций: Variant без managed-ссылок, пул файберов,
  строки через scratch-буфер с интернированием без промежуточных string.
- Упрощения v1: `switch` нет (if/else); составное `+=` на `a[i]`/свойстве
  вычисляет цель дважды; коллекции живут до перезагрузки программы;
  инициализаторы полей не могут ждать (`wait`), `spawn` в них запрещён.
