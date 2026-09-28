# Salamander в Sublime Text 4

Подсветка `.sal`, сниппеты, комментирование, отступы и Goto Symbol — сразу из
пакета. Ошибки на лету, автодополнение (Engine, API игры, события — с
сигнатурами и доками из `salamander-api.json`), hover, подсказка параметров,
переход к определению, символы воркспейса и семантическая подсветка — через
пакет **LSP** и тот же сервер `Tools/DslLsp`, что у VS Code и Rider (тот же
компилятор, что в игре).

| что | откуда |
|-----|--------|
| подсветка без компиляции | `Salamander.sublime-syntax` — сгенерирована из грамматики VS Code |
| сниппеты `trigger`, `event`, `before`/`after`/`replace`, `wait`, `spawn`, `for`, `arch`, … | `Salamander.sublime-completions` — из сниппетов VS Code |
| `Ctrl+/`, авто-отступ по `{ }`, `Ctrl+R` | `*.tmPreferences` |
| ошибки, автодополнение, hover, go-to, семантический цвет | LSP-сервер |
| значок `.sal` в сайдбаре и на вкладках | `Salamander.sublime-file-icons` + `icons/` |

## Шаг 1. Собрать LSP-сервер (один раз)

Из корня репозитория (нужен .NET SDK 8):

```bash
dotnet publish Tools/DslLsp -c Release -o Tools/DslLsp/publish
```

## Шаг 2. Поставить пакет Salamander

Папка пакета должна называться **`Salamander`** — на это имя завязаны команды
палитры. Проще всего не копировать, а связать папку репозитория с `Packages`
(обновления грамматики тогда приходят вместе с `git pull`):

Sublime → **Preferences → Browse Packages…** — откроется папка `Packages`.
Из неё (Windows, cmd; права администратора не нужны):

```bat
mklink /J Salamander "C:\путь к репозиторию\Tools\sublime-salamander"
```

macOS/Linux: `ln -s "/путь/к/репозиторию/Tools/sublime-salamander" Salamander`.

Или просто скопируйте содержимое `Tools/sublime-salamander` в
`Packages/Salamander`. Проверка: открыть `.sal` — справа внизу строки
состояния написано `Salamander`, код раскрашен.

## Шаг 3. Поставить пакет LSP

**Ctrl+Shift+P → Package Control: Install Package → `LSP`** (клиент от
sublimelsp). По желанию ещё **`LSP-file-watcher-chokidar`** — тогда
пере-экспортированный игрой манифест подхватывается без перезапуска сервера.

## Шаг 4. Подключить сервер

**Ctrl+Shift+P → `Preferences: Salamander — настройки LSP-клиента`**.
Слева откроется пример с комментариями, справа — ваш
`Packages/User/LSP.sublime-settings`. Перенесите в правую панель:

```jsonc
{
    "semantic_highlighting": true,
    "clients": {
        "salamander": {
            "enabled": true,
            "command": ["dotnet", "C:/путь к репозиторию/Tools/DslLsp/publish/DslLsp.dll"],
            "selector": "source.salamander"
        }
    }
}
```

- В `command` путь **без кавычек внутри строки**, даже с пробелами: каждый
  элемент массива — отдельный аргумент процесса. Слэши прямые (или `\\`).
- Если в `LSP.sublime-settings` уже есть `"clients"` — добавьте в него только
  ключ `"salamander"`.
- `semantic_highlighting` — глобальный ключ пакета LSP, по умолчанию выключен.
  Без него работает всё, кроме цвета, который знает только сервер (API игры,
  события, элементы енумов, пространства имён).
- Сервер лежит в открытой папке — можно относительно неё:
  `"command": ["dotnet", "${folder}/Tools/DslLsp/publish/DslLsp.dll"]`.

Сохраните — сервер стартует при открытии `.sal` (**LSP: Restart Server**, если
файл уже открыт). Сервер берёт корень из первой папки окна (Project → Add
Folder to Project) и ищет под ним модули (`module.json`) и
`salamander-api.json` — для папки с модами этого достаточно.

## Значок `.sal` (по желанию)

Пакет несёт значок саламандры (`icons/file_type_salamander*.png`) и файл
сопоставлений `Salamander.sublime-file-icons`. Включается одной строкой в
**Preferences → Settings** (правая панель, User):

```jsonc
"file_icon_theme": "Salamander.sublime-file-icons",
```

Значки темы перекрывают этот файл только там, где тема знает расширение сама,
а `.sal` она не знает — остальные значки остаются штатными. Настройку
понимают только свежие сборки Sublime Text 4; в старой ничего не изменится,
ошибок тоже не будет. Исходник значка — `Tools/icons` (см. там `build.py`).

## Настройки сервера

Те же четыре ключа, что у VS Code и Rider — в `"initialization_options"`
клиента. Пути абсолютные либо относительно корня открытой папки:

| ключ | что делает |
|------|------------|
| `salamander.modulesRoot` | папка, под которой искать модули и `*.sal`; по умолчанию — корень папки |
| `salamander.apiManifest` | точный путь к манифесту API игры |
| `salamander.buildFile` | точный путь к `salamander-build.json` |
| `salamander.referencePaths` | папки со **справочными модулями** (игра, зависимости мода) — только для подсказок и go-to, их ошибки не показываются |

```jsonc
"salamander": {
    "enabled": true,
    "command": ["dotnet", "C:/…/DslLsp.dll"],
    "selector": "source.salamander",
    "initialization_options": {
        "salamander.modulesRoot": "Assets/StreamingAssets/Core",
        "salamander.apiManifest": "Assets/StreamingAssets/Core/salamander-api.json"
    }
}
```

### Разные настройки для разных проектов

Глобальный клиент удобно держать с одним `command`, а пути — в проекте.
Шаблон — `Salamander.sublime-project.example`: скопируйте рядом с модулями как
`<имя>.sublime-project` и откройте через **Project → Open Project**.
Блок `"settings" → "LSP" → "salamander"` в нём перекрывает глобальный клиент
только в этом проекте — например, мод со своими `referencePaths` на модули
игры.

## Если что-то не так

Сначала — **Ctrl+Shift+P → LSP: Toggle Log Panel**. При старте сервер пишет в
stderr корень воркспейса, каждую применённую настройку
(`modulesRoot = …`, `referencePath = …`) и предупреждения о ненайденных
модулях и манифесте.

- **Сервер не стартует / в логе ошибка dotnet** — выполните команду из
  `command` руками в терминале. Живой сервер напечатает
  `salamander-lsp: запущен, жду initialize по stdio` и будет молча ждать.
  Иначе: нет `dotnet` в PATH (укажите полный путь к `dotnet.exe` в `command`),
  не выполнен шаг 1 или скопирована одна dll без `DslLsp.runtimeconfig.json`.
- **Сервер даже не пытается стартовать** — у файла не тот синтаксис. Справа
  внизу должно быть `Salamander`, а не `Plain Text`; иначе проверьте, что
  папка пакета называется именно `Salamander` (шаг 2).
- **Ошибок нет вообще, автодополнения по API нет** — сервер не нашёл модули
  или манифест: смотрите в логе, какой корень он взял. Окно без открытой папки
  (один файл) даёт корнем текущую папку процесса — откройте папку модов или
  задайте `salamander.modulesRoot`. Манифест не найден — предупреждение W0401
  прямо в диагностике; папки модулей нет по указанному пути — W0402.
- **Мод ругается «зависит от 'base', который не загружен»** — укажите
  `salamander.referencePaths` на модули игры.
- **Нет цвета API/событий** — `"semantic_highlighting": true` в
  `LSP.sublime-settings` (глобально, не в клиенте).

## Для разработчиков пакета

`Salamander.sublime-syntax` и `Salamander.sublime-completions` —
**сгенерированные** файлы. Единственный источник — грамматика и сниппеты
расширения VS Code (`Tools/vscode-salamander/syntaxes`, `…/snippets`). После
их правки:

```bash
python Tools/sublime-salamander/_build/generate.py
```

(нужен PyYAML). Генератор лежит в подпапке нарочно: `.py` из корня пакета
Sublime загрузил бы как плагин.
