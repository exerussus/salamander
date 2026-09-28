# Плагин Salamander для Rider

Исходники плагина, который берёт на себя всю настройку `.sal` в Rider. Как им
пользоваться — в [`../README-Rider.md`](../README-Rider.md). Здесь — как он
устроен и как его пересобрать.

## Сборка

```bash
Tools/rider/plugin/build.sh      # → Tools/rider/plugin/build/Salamander-<версия>.zip
```

Gradle не нужен. Скрипт берёт `javac`/`java` и jar-ы платформы из установленного
Rider, jar-ы LSP4IJ — из его папки плагинов, сервер собирает `dotnet publish`
(нужен .NET SDK 8). Всё ищется само. Если нет — переменные:

| переменная | что |
|---|---|
| `RIDER_HOME` | папка установки Rider (в ней `lib/` и `jbr/`) |
| `LSP4IJ_LIB` | `…/plugins/lsp4ij/lib` нужного Rider |
| `SERVER_FROM` | готовая папка `publish` сервера вместо `dotnet publish` |

Python необязателен: без него live templates берутся из готового
`resources/liveTemplates/Salamander.xml`.

**Когда пересобирать:** после обновления движка (сервер едет внутри плагина),
после правки сниппетов или грамматики VS Code (плагин берёт их оттуда) и после
правки самого плагина. Затем Settings → Plugins → ⚙ → Install Plugin from Disk.

## Что внутри

```
build/Salamander-<версия>.zip
└── Salamander/
    ├── lib/salamander.jar        классы + plugin.xml + live templates + значок
    ├── server/                   DslLsp (dotnet publish Tools/DslLsp)
    └── bundles/salamander/       грамматика TextMate: bundle/package.json +
                                  грамматика и language-configuration из vscode-salamander
```

| что | где в коде |
|---|---|
| сервер для LSP4IJ, маска `*.sal`, поиск `dotnet` и `DslLsp.dll`, настройки в `initialize` | `lsp/`, `SalamanderProject`, `resources/META-INF/salamander-lsp4ij.xml` |
| Unity-проект: манифест из `Assets/StreamingAssets`, его папка — корень модулей | `SalamanderProject.initializationOptions` |
| ручной сервер из старой инструкции → уведомление «Удалить» | `lsp/SalamanderLspStartup` |
| тип файла `.sal` (TextMate, без него — текст), предупреждения про LSP4IJ и dotnet | `SalamanderStartup` |
| грамматика TextMate | `textmate/SalamanderBundleProvider`, `bundle/package.json` |
| Add → Salamander File (язык и архетипы из `salamander-api.json`) | `newfile/` |
| live templates | `gen-live-templates.py` ← `Tools/vscode-salamander/snippets/salamander.json` |
| значок `.sal` | `SalamanderFileIconProvider`, картинка — `Tools/rider/icons/salamander.svg` |

**Единые источники.** Грамматика, конфиг языка и сниппеты — общие с VS Code,
значок — общий для всех редакторов (`Tools/icons` → `Tools/rider/icons`).
В плагин они копируются при сборке и в репозитории не дублируются.

**Id плагина** — `com.exerussus.salamander.icon`, унаследован от первой версии
(только значок): установка поверх заменяет её, а не ставит второй плагин.
