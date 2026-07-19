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

```
listener BurnStacks
{
    int stacks = 0;                        // СВОЙ счётчик у каждой подписки

    event OnUnitDamageTaken(Unit target, Unit src, float amount, DamageType t)
    {
        stacks = stacks + 1;
        spawn Tick();                      // файбер живёт, пока жива подписка
    }
    func Tick() { wait 1.0; UnitApi.Damage(self, 1.0 * stacks); }
    event OnUnsubscribe() { Engine.Log($"погашен: {self.name}"); }
}

// из триггера:
Subscription s = Engine.Attach(BurnStacks, unit);   // подписок сколько угодно
Engine.Detach(s);                                    // или смерть цели — авто-detach
```

## Итерация по коллекциям: `for` защищён снапшотом

`for x in list` / `for k in map` / `for k, v in map` обходят ЭЛЕМЕНТЫ: цикл
видит коллекцию на момент входа (снапшот в пулируемом буфере файбера), поэтому
менять её в теле можно свободно — фильтрация пишется в лоб. Правила:

- добавленное во время цикла не итерируется (и `Add` в теле не зациклит обход);
- Map: ключи, удалённые после входа, молча пропускаются; `v` в `for k, v` —
  живое значение, это `map[k]` на момент шага;
- каждый элемент посещается не больше одного раза, маршрут фиксируется на
  входе — перестановки элементов в теле на него не влияют;
- `wait` внутри цикла и сейв/лоад посреди итерации работают: снапшот живёт на
  файбере;
- буфер пулируется (ноль аллокаций в установившемся режиме) и освобождается
  при любом выходе — `break`, `return`, смерть файбера.

```
for k, v in inventory {                    // v — живое значение (map[k])
    if (v <= 0) { inventory.Remove(k); }   // фильтрация — из коробки
}
for item in loot { wait 0.2; DropApi.Spawn(item); }   // wait внутри — ок
```

### Позиционный обход: когда важны индексы

`for` обходит элементы («каждому по разу»). Если логика позиционная — свапы,
retry-очередь, «отложи в хвост и вернись к нему» — бери индексы и `while`:
`list[i]` читает живой список, перестановки видны немедленно, курсором
управляешь сам:

```
var i = 0;
while (i < queue.count) {
    var v = queue[i];
    if (NeedsRetry(v)) {
        var last = queue.count - 1;   // v уезжает в хвост —
        var tmp = queue[last];        // курсор встретит его снова
        queue[last] = queue[i];
        queue[i] = tmp;               // i не двигаем: на этой позиции новый элемент
    } else {
        Process(v);
        i = i + 1;
    }
}
```

Нюанс: в `for i in 0..list.count` верхняя граница вычисляется один раз на
входе — для растущей/усыхающей коллекции используй `while`, как выше.

## Сейвы: полный снапшот рантайма

`engine.SaveState(resolver)` / `engine.LoadState(bytes, resolver)` — бинарный
снапшот всего состояния: статики, файберы посреди `wait` (стек + позиция),
таймеры/очереди, подписки listener с полями, динамические строки, коллекции,
флаги, время. Entity-хэндлы едут как стабильные long-id от хоста
(`ISaveEntityResolver`; для строковых миров — `StringIdResolver`); пропавшие
объекты становятся протухшими хэндлами (`Engine.IsValid` == false — скрипты уже
умеют «юнит умер, пока я ждал»). Сейв привязан к версии скриптов отпечатком
программы — несовпадение даёт `SaveStateException`. Подробности — в скилле
(host-api.md, раздел Save/Load).

```csharp
byte[] save = engine.SaveState(resolver);   // между тиками

// восстановление: та же программа + пересозданный мир
engine.LoadProgram(prog);
engine.LoadState(save, resolver);           // `wait 300.0` продолжится с той же секунды
```

## Архетипы: механики контента по шаблону (spell/item/...)

Хост объявляет виды сущностей и их события; скрипты описывают механику
КОНКРЕТНОЙ сущности блоком с тем же id, что в контентных манифестах игры:

```csharp
// хост (виды — данные, а не ключевые слова языка):
var spell  = host.Archetype("spell");
var onCast = spell.Event<Unit, Unit>("OnCast");
spell.KnownIds(gameData.SpellIds);   // опционально: опечатка в id = ошибка компиляции
```

```
spell "arcane_missile"
{
    int damage = 3;                                   // статик, как у class

    event OnCast(Unit caster, Unit target) { UnitApi.Damage(target, damage); }
    event OnObtain(Unit unit) { Engine.Log($"{unit.name} выучил снаряд"); }
}
```

```csharp
// загрузка контента (один раз): строка -> плотный int-хэндл
int h = engine.ResolveArchetype("spell", "arcane_missile");   // -1 = кода нет

// геймплей (горячий путь — чистая индексация, без строк и хэшей):
onCast.Raise(engine, h, caster, target);

// сборщик: сверка покрытия манифеста
engine.HasArchetype("spell", "arcane_missile");
engine.GetArchetypeIds("spell", idsBuffer);
```

## Переопределение: поздние блоки патчат ранние

Мерж-семантика едина для ВСЕХ деклараций: `trigger`/`class`/`listener` — по
имени, архетипы — по (вид, id). Блоки сливаются в одну сущность,
переопределяются только объявленные члены (события, поля, функции), поздний
выигрывает, а ранние обработчики и вызовы видят итог — мод меняет механику базы,
не трогая её файлы:

```
spell "arcane_missile" { event OnCast(Unit c, Unit t) { /* реворк каста */ } }
spell "arcane_missile" { int damage = 5; }   // патч-блок: только значение

class Balance  { int bossHp = 750; }         // мод правит баланс
trigger Intro  { int delay = 10; }           // патч поля триггера
disabled trigger Cheats { }                  // поздний блок решает стартовый флаг
listener Burn  { event OnUnitDamageTaken(Unit t, Unit s, float a, DamageType d); } // «выключить» обработчик
```

Дубль имени — ошибка только внутри ОДНОГО блока; смена типа поля при
переопределении — E0207; убить реализацию — `pass;` или прототип без тела.
Подробности — в скилле (language.md §9.6).

## Как гонять тесты

Два пути, тесты одни и те же:

- **Быстрый (без Unity):** `dotnet test Tools/DslTests` из корня репозитория.
  Ядро engine-agnostic, харнесс подключает Dsl.Core и Dsl.Tests исходниками.
  Один класс: `dotnet test Tools/DslTests --filter FullyQualifiedName~ListenerTests`.
- **В Unity:** скопируйте `Dsl.Tests` в `Assets/` рядом с ядром (зона B, в билд
  не входит) → Window → General → Test Runner → вкладка EditMode → Run All.
  Требуется пакет com.unity.test-framework (обычно установлен по умолчанию).

## Граница: движок НЕ сборщик

Рантайм Salamander сам ничего не ищет и не собирает — наборы модулей ему отдаёт
СБОРЩИК ИГРЫ. В Unity-бутстрапе назначьте `SourceProvider` (или переопределите
`LoadModules`); хот-релоад — тоже явное решение сборщика (`WatchPath`).
Утилиты `ModuleLoader.LoadFromList/LoadFromFolder` существуют для сборщика и
инструментов — зовут их они, не движок. Инструменты (чекер, LSP) едят то же:
если сборщик экспортировал `salamander-build.json` (упорядоченный список папок
модулей — `{"modules": ["mods/base", "mods/patch"]}`), берётся РОВНО он; обход
папки — дев-режим без сборщика.

## LSP: одна поддержка для VS Code, Rider и любого редактора

Языковой сервер (`Tools/DslLsp`) — тот же компилятор, что в игре, живущий
процессом и отвечающий редакторам по LSP: диагностика на лету, автодополнение
(Engine, API игры, события по контексту — внутри `spell`-блока предложатся
события именно этого вида; методы вставляются со скобками и типизированными
плейсхолдерами аргументов), подсказка сигнатуры при наборе вызова (активный
аргумент подсвечивается), **семантическая подсветка с сервера** (Rider цветной
даже без TextMate; TextMate-грамматика всё равно рекомендована — добавит цвета
пунктуации), hover с доками из `salamander-api.json`, переход к определению,
символы файла и воркспейса. Ноль зависимостей, рукописный JSON-RPC.

- Сборка (один раз): `dotnet publish Tools/DslLsp -c Release -o Tools/DslLsp/publish`
- **VS Code**: расширение — тонкий клиент, находит сервер само (или настройка
  `salamander.server.path`).
- **Rider / IDE JetBrains**: через плагин LSP4IJ + TextMate-подсветка из нашей
  же грамматики — пошагово в `Tools/rider/README-Rider.md`.

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

## Возможности (сводка)

- **Типизация**: int/float/bool/string, enum (свои и хостовые), сущности хоста,
  `T[]`, `List<T>`, `Map<K,V>`, `Fiber`, `Subscription`; интерполяция `$"hp {x}"`.
- **Декларации**: статичный `class`, `trigger` (события + `action Do`),
  `listener` (подписка на сущность), блоки-архетипы (`spell id { ... }`, виды
  объявляет хост), `enum`; прототипы без тела и `pass;`.
- **Кооперативные файберы**: `wait`, `wait until`, `spawn`, хэндлы `Fiber`,
  `Engine.Kill/IsAlive/KillAll`; обработчики стартуют немедленно (до первой
  приостановки), порядок детерминирован.
- **Мерж-переопределение**: поздние блоки патчат события/поля/функции любой
  декларации — моды меняют механики базы без правки её файлов.
- **Коллекции**: снапшот-итерация (`for x in list`, `for k, v in map`) — мутация
  в цикле безопасна; ноль аллокаций в установившемся режиме.
- **Listener**: пер-сущностные подписки с собственными полями (пулируются),
  `self`, авто-detach при смерти цели, гибель файберов подписки на detach.
- **Архетипы**: адресный диспатч по (вид, id), id интернированы в int (горячий
  путь без строк), API сборщика `HasArchetype`/`GetArchetypeIds`, KnownIds ловят
  опечатки на компиляции.
- **Хост-API**: fluent (`Class/Prop/Fn/Act/Event/Enum/Archetype`) и атрибуты
  (`[SalamanderClass]`/`[SalamanderApi]`, инстанс-API); json-манифест API для
  оффлайн-чекера и IDE.
- **Модули**: манифесты, зависимости (топологический порядок),
  `Enable/DisableModule`, синхронный режим (`execution: synchronous`).
- **Бюджет исполнения**: лимит инструкций на тик с переносом недоделанного и
  статистикой (`GetStats`/`GetTriggerStats`) — вечный цикл мода не вешает кадр.
- **Сейвы**: полный бинарный снапшот, включая файберы посреди `wait`; стабильные
  id сущностей от хоста; отпечаток программы против несовместимых скриптов.
- **Безопасность рантайма**: протухающие хэндлы (`Engine.IsValid`), ошибка
  убивает файбер, а не движок; в горячих структурах нет managed-ссылок —
  GC-давления нет.
- **Инструменты**: LSP-сервер (VS Code, Rider через LSP4IJ, любой
  LSP-клиент — диагностика, автодополнение, hover, go-to, символы), CLI-чекер
  DslCheck, TextMate-грамматика и сниппеты, `.sal`-импортер Unity, модкит.
- **Диагностика** компилятора на русском с кодами E0xxx.
