# Salamander в Rider (и других IDE JetBrains)

Полная поддержка `.sal` в Rider: ошибки на лету, автодополнение (Engine, API
игры, события — с сигнатурами и доками из `salamander-api.json`), hover,
переход к определению, символы. Работает через LSP — тот же сервер, что и в
VS Code, тот же компилятор, что в игре.

## Шаг 1. Собрать LSP-сервер (один раз)

Из корня репозитория (нужен .NET SDK 8):

```bash
dotnet publish Tools/DslLsp -c Release -o Tools/DslLsp/publish
```

Появится `Tools/DslLsp/publish/DslLsp.dll`. После обновления движка — просто
повторите команду.

## Шаг 2. Установить LSP4IJ

Settings → Plugins → Marketplace → **LSP4IJ** (открытый LSP-клиент от Red Hat
для IDE JetBrains) → Install → перезапуск IDE.

## Шаг 3. Подключить сервер

Откройте панель **LSP4IJ** (View → Tool Windows → LSP4IJ или через Settings →
Languages & Frameworks → LSP4IJ) → **New Language Server** → шаблон
«User-defined»:

- **Name**: `Salamander`
- **Command**:
  `dotnet <абсолютный путь к репозиторию>/Tools/DslLsp/publish/DslLsp.dll`
- **Mappings** → File name patterns: `*.sal` (язык можно указать как
  `salamander` или оставить TextMate).

Сохраните. Откройте папку с модами (та, где лежат `module.json` и
`salamander-api.json`) как проект — сервер берёт корень воркспейса из IDE,
находит модули и манифест сам. Ошибки появятся в редакторе и в панели
Problems.

## Шаг 4. Подсветка синтаксиса (TextMate)

IDE JetBrains понимают TextMate-бандлы, включая формат VS Code-расширений:

Settings → Editor → **TextMate Bundles** → «+» → укажите папку
`Tools/vscode-salamander` из репозитория → Apply.

Готово: грамматика Salamander (те же цвета-скоупы, что в VS Code) начнёт
подсвечивать `.sal`.

## Если что-то не так

- «Ошибок нет вообще» — проверьте, что в корне открытого проекта есть
  `salamander-api.json` (игра экспортирует его при запуске в редакторе) и хотя
  бы один модуль (`подпапка/module.json`). Без манифеста сервер честно
  предупредит прямо в Problems.
- Сервер не стартует — выполните команду из шага 3 руками в терминале: живой
  сервер молча ждёт ввода (это норма), ошибки .NET будут видны сразу.
- Логи LSP4IJ: панель LSP4IJ → вкладка соответствующего сервера.
