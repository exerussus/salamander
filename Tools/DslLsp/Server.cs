using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dsl.Compilation;
using Dsl.Syntax;
using Dsl.Text;
using Dsl.Tooling;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dsl.Tools.Lsp
{
    /// <summary>
    /// Мозги сервера — это Dsl.Core: диагностика приходит из настоящего
    /// компилятора (как в игре и DslCheck), символы и контексты — из настоящего
    /// парсера, знание API хоста — из salamander-api.json. Сервер лишь
    /// удерживает воркспейс (модули + несохранённые правки) и переводит всё
    /// это на язык LSP.
    /// </summary>
    public sealed class Server
    {
        private readonly Rpc _rpc;
        private string _root;
        private bool _initialized;

        // несохранённые правки: абсолютный путь -> текущий текст
        private readonly Dictionary<string, string> _open = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // кому мы публиковали диагностику (чтобы уметь очищать)
        private readonly HashSet<string> _published = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Мозги подсказок — общий языковой сервис Dsl.Tooling (тот же, что во
        // встроенной IDE игры): манифест, синтакс-индекс, комплишены, hover,
        // go-to, раскраска. Сервер держит только воркспейс и протокол.
        private readonly LanguageService _ls = new LanguageService();

        public Server(Rpc rpc)
        {
            _rpc = rpc;
            _ls.TextProvider = GetText;
        }

        // ===================================================================
        // Главный цикл
        // ===================================================================

        public void Run()
        {
            while (true)
            {
                JObject msg;
                try
                {
                    msg = _rpc.Read();
                }
                catch (Newtonsoft.Json.JsonException ex)
                {
                    // Тело сообщения уже вычитано целиком по Content-Length, поток цел —
                    // одно битое сообщение не повод ронять сервер посреди сессии редактора.
                    System.Console.Error.WriteLine("salamander-lsp: пропущено некорректное JSON-сообщение: " + ex.Message);
                    continue;
                }
                if (msg == null) return; // клиент закрыл поток

                var method = (string)msg["method"];
                var id = msg["id"];
                var p = msg["params"] as JObject;

                try
                {
                    switch (method)
                    {
                        case "initialize": _rpc.Reply(id, Initialize(p)); break;
                        case "initialized": _initialized = true; RefreshAll(); break;
                        case "shutdown": _rpc.Reply(id, null); break;
                        case "exit": return;

                        case "textDocument/didOpen":
                        {
                            var doc = (JObject)p["textDocument"];
                            _open[UriToPath((string)doc["uri"])] = (string)doc["text"];
                            RefreshAll();
                            break;
                        }
                        case "textDocument/didChange":
                        {
                            var path = UriToPath((string)p["textDocument"]["uri"]);
                            var changes = (JArray)p["contentChanges"];
                            if (changes.Count > 0)
                                _open[path] = (string)changes[changes.Count - 1]["text"]; // full sync
                            RefreshAll();
                            break;
                        }
                        case "textDocument/didClose":
                            _open.Remove(UriToPath((string)p["textDocument"]["uri"]));
                            RefreshAll();
                            break;
                        case "textDocument/didSave":
                            RefreshAll();
                            break;

                        case "workspace/didChangeConfiguration":
                            // клиент может передать настройки и после старта
                            ApplySettings(p?["settings"] as JObject);
                            RefreshAll();
                            break;
                        case "workspace/didChangeWatchedFiles":
                            // манифест могли переэкспортировать или переместить —
                            // сбрасываем найденный путь, чтобы не держаться за старый
                            _resolvedApiPath = null;
                            RefreshAll();
                            break;

                        case "textDocument/completion": _rpc.Reply(id, Completion(p)); break;
                        case "textDocument/signatureHelp": _rpc.Reply(id, SignatureHelp(p)); break;
                        case "textDocument/semanticTokens/full": _rpc.Reply(id, SemanticTokens(p)); break;
                        case "textDocument/hover": _rpc.Reply(id, Hover(p)); break;
                        case "textDocument/definition": _rpc.Reply(id, Definition(p)); break;
                        case "textDocument/documentSymbol": _rpc.Reply(id, DocumentSymbols(p)); break;
                        case "workspace/symbol": _rpc.Reply(id, WorkspaceSymbols(p)); break;

                        default:
                            // запросы, которых не умеем, честно отклоняем; нотификации молча пропускаем
                            if (id != null) _rpc.Error(id, -32601, $"метод не поддерживается: {method}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    if (id != null) _rpc.Error(id, -32603, ex.Message);
                }
            }
        }

        // ===================================================================
        // Настройки клиента: где искать манифест и модули
        // ===================================================================
        // Без них сервер знает только корень воркспейса — а его задаёт IDE, и в
        // Unity-проекте это корень ВСЕГО проекта: модули лежат глубоко в
        // StreamingAssets, манифест рядом с ними. Клиент (LSP4IJ,
        // расширение VS Code) может указать всё явно; пути — абсолютные либо
        // относительно корня воркспейса.

        private string _cfgApiManifest;   // salamander.apiManifest
        private string _cfgModulesRoot;   // salamander.modulesRoot
        private string _cfgBuildFile;     // salamander.buildFile
        private string _resolvedApiPath;  // кэш найденного манифеста (обход не на каждый рефреш)

        private void ApplySettings(JObject settings)
        {
            if (settings == null) return;
            // принимаем и плоский вид ({"salamander.apiManifest": ...}), и вложенный
            // ({"salamander": {"apiManifest": ...}}) — клиенты шлют по-разному
            var s = settings["salamander"] as JObject;

            string Get(string key)
            {
                var v = (string)(settings[key] ?? settings["salamander." + key] ?? s?[key]);
                return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
            }

            _cfgApiManifest = Get("apiManifest");
            _cfgModulesRoot = Get("modulesRoot");
            _cfgBuildFile = Get("buildFile");
            _resolvedApiPath = null;

            if (_cfgApiManifest != null) Console.Error.WriteLine($"salamander-lsp: apiManifest = {_cfgApiManifest}");
            if (_cfgModulesRoot != null) Console.Error.WriteLine($"salamander-lsp: modulesRoot = {_cfgModulesRoot}");
            if (_cfgBuildFile != null) Console.Error.WriteLine($"salamander-lsp: buildFile = {_cfgBuildFile}");
        }

        /// <summary>
        /// Путь из настройки: абсолютный как есть, относительный — от корня
        /// воркспейса. Нормализация обязательна: настройки заполняет человек, и
        /// "\C:\mods" или "file:///c:/mods" иначе молча превращаются в
        /// несуществующий путь (см. ModuleLoader.NormalizeUserPath).
        /// </summary>
        private string ResolveConfigured(string value)
        {
            if (value == null || _root == null) return null;
            try
            {
                string v = ModuleLoader.NormalizeUserPath(value);
                if (string.IsNullOrWhiteSpace(v)) return null;
                return Path.IsPathRooted(v) ? Path.GetFullPath(v)
                                            : Path.GetFullPath(Path.Combine(_root, v));
            }
            catch { return null; }
        }

        /// <summary>Корень поиска модулей: настройка, иначе корень воркспейса.</summary>
        private string ModulesRoot() => ResolveConfigured(_cfgModulesRoot) ?? _root;

        private JObject Initialize(JObject p)
        {
            _root = null;
            // современные клиенты (Rider/LSP4IJ) шлют workspaceFolders, rootUri — null
            if (p?["workspaceFolders"] is JArray folders && folders.Count > 0)
            {
                var wf = (string)folders[0]?["uri"];
                if (!string.IsNullOrEmpty(wf)) _root = UriToPath(wf);
            }
            if (_root == null)
            {
                var rootUri = (string)p?["rootUri"];
                if (!string.IsNullOrEmpty(rootUri)) _root = UriToPath(rootUri);
            }
            if (_root == null)
            {
                var rootPath = (string)p?["rootPath"];
                if (!string.IsNullOrEmpty(rootPath)) _root = Path.GetFullPath(rootPath);
            }
            _root ??= Directory.GetCurrentDirectory();
            Console.Error.WriteLine($"salamander-lsp: корень воркспейса: {_root}");

            ApplySettings(p?["initializationOptions"] as JObject);

            return new JObject
            {
                ["capabilities"] = new JObject
                {
                    ["textDocumentSync"] = 1, // Full: документ приходит целиком
                    ["completionProvider"] = new JObject { ["triggerCharacters"] = new JArray(".", " ") },
                    ["signatureHelpProvider"] = new JObject { ["triggerCharacters"] = new JArray("(", ",") },
                    ["semanticTokensProvider"] = new JObject
                    {
                        ["legend"] = new JObject
                        {
                            ["tokenTypes"] = new JArray(SemanticClassifier.TokenTypes),
                            ["tokenModifiers"] = new JArray(),
                        },
                        ["full"] = true,
                    },
                    ["hoverProvider"] = true,
                    ["definitionProvider"] = true,
                    ["documentSymbolProvider"] = true,
                    ["workspaceSymbolProvider"] = true,
                },
                ["serverInfo"] = new JObject { ["name"] = "salamander-lsp", ["version"] = "1.0" },
            };
        }

        // ===================================================================
        // Воркспейс: индекс + компиляция + публикация диагностик
        // ===================================================================

        private string GetText(string absPath)
        {
            if (_open.TryGetValue(absPath, out var live)) return live;
            try { return File.ReadAllText(absPath); }
            catch { return null; }
        }

        /// <summary>
        /// Где лежит salamander-api.json: настройка клиента → корень воркспейса →
        /// ближайший к корню в подпапках. Найденный путь кэшируется: обход дерева
        /// на каждое нажатие клавиши — то, из-за чего сервер и «не видел» манифест
        /// в большом проекте. Кэш сбрасывается на смене настроек и на
        /// didChangeWatchedFiles.
        /// </summary>
        private string ResolveApiManifestPath()
        {
            const string Name = "salamander-api.json";

            var configured = ResolveConfigured(_cfgApiManifest);
            if (configured != null) return configured;   // сказали явно — не спорим, даже если файла нет

            if (_resolvedApiPath != null && File.Exists(_resolvedApiPath)) return _resolvedApiPath;

            string root = ModulesRoot();
            string atRoot = Path.Combine(root, Name);
            if (File.Exists(atRoot)) return _resolvedApiPath = atRoot;

            // манифест обычно экспортируется в StreamingAssets/<modsFolder>,
            // а не в корень воркспейса — ищем ближайший к корню
            var found = ModuleLoader.FindNearestFile(root, Name);
            if (found != null) return _resolvedApiPath = found;

            _resolvedApiPath = null;
            return atRoot;   // путь для сообщения «не найден»
        }

        private void RefreshAll()
        {
            if (!_initialized || _root == null) return;

            // Корень модулей может быть задан неверно, и раньше это проявлялось
            // только жалобой «манифест не найден»: обход несуществующей папки
            // молча возвращает пусто. Сообщение указывало не на причину.
            string modulesRoot = ModulesRoot();
            bool rootMissing = !Directory.Exists(modulesRoot);

            // --- синтакс-индекс всех *.sal (диск + оверлеи), с кэшем по хэшу ---
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // обход с ограничением глубины и пропуском Library/Temp/obj/.git:
            // корнем может оказаться весь Unity-проект, и голый AllDirectories
            // прочёсывал бы десятки тысяч файлов на КАЖДЫЙ рефреш
            foreach (var f in ModuleLoader.EnumerateFiles(modulesRoot, "*.sal"))
            {
                var abs = Path.GetFullPath(f);
                seen.Add(abs);
                _ls.Index.Update(abs, GetText(abs));
            }
            foreach (var kv in _open)
                if (kv.Key.EndsWith(".sal", StringComparison.OrdinalIgnoreCase) && seen.Add(kv.Key))
                    _ls.Index.Update(kv.Key, kv.Value);
            _ls.Index.RetainOnly(seen);

            // --- компиляция воркспейса тем же путём, что DslCheck ---
            var diagsByFile = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
            JArray Bucket(string file)
            {
                if (!diagsByFile.TryGetValue(file, out var arr)) diagsByFile[file] = arr = new JArray();
                return arr;
            }

            string apiPath = ResolveApiManifestPath();
            Semantics.HostRegistry registry;
            int apiVersion = 1;
            _ls.Api = null;
            if (File.Exists(apiPath))
            {
                try
                {
                    var apiText = File.ReadAllText(apiPath);
                    registry = ApiManifest.Import(apiText, out apiVersion);
                    _ls.Api = JsonConvert.DeserializeObject<ApiManifest>(apiText);
                }
                catch (Exception ex)
                {
                    registry = new Semantics.HostRegistry();
                    Bucket(apiPath).Add(LspDiag(1, 1, 1, 1, "E0400", "salamander-api.json не читается: " + ex.Message));
                }
            }
            else
            {
                registry = new Semantics.HostRegistry();
                // если корня нет, манифест не мог быть найден по определению —
                // жаловаться на манифест значит увести человека не туда
                if (!rootMissing)
                    Bucket(apiPath).Add(LspDiag(1, 1, 1, 2, "W0401",
                        "salamander-api.json не найден — события и API хоста неизвестны " +
                        "(запустите игру в редакторе один раз, манифест экспортируется автоматически; " +
                        "если он лежит в другом месте — укажите настройку salamander.apiManifest)."));
                Console.Error.WriteLine($"salamander-lsp: манифест не найден, искали от {modulesRoot}");
            }

            if (rootMissing)
            {
                Bucket(apiPath).Add(LspDiag(1, 1, 1, 2, "W0402",
                    $"Папка модулей не найдена: {modulesRoot}. Ни один .sal не проиндексирован. " +
                    "Проверьте salamander.modulesRoot — путь абсолютный либо относительно корня " +
                    "воркспейса; ведущий слэш перед буквой диска (\\C:\\...) Проводник прощает, " +
                    "а файловая система нет."));
                Console.Error.WriteLine($"salamander-lsp: папка модулей не найдена: {modulesRoot}");
            }

            // «ешь то, что дал сборщик»: если он экспортировал salamander-build.json
            // (упорядоченный список папок модулей) — берём РОВНО его; обход папки
            // остаётся дев-режимом без сборщика (общая логика — Dsl.Tooling)
            var ws = WorkspaceLoader.Load(modulesRoot, ResolveConfigured(_cfgBuildFile));
            foreach (var err in ws.LoadErrors)
                Bucket(Path.GetFullPath(err.Key)).Add(LspDiag(1, 1, 1, 1, "E0401", err.Value));

            if (ws.Modules.Count == 0)
                Console.Error.WriteLine(
                    $"salamander-lsp: модулей не найдено под {modulesRoot} — " +
                    "укажите salamander.modulesRoot или положите salamander-build.json.");

            // оверлеи: несохранённые правки важнее диска
            var modules = WorkspaceCompiler.WithOverlays(ws,
                abs => _open.TryGetValue(Path.GetFullPath(abs), out var live) ? live : null);

            // порядок загрузки файлов — для подсказки о версиях члена (слои
            // before/after и replace зависят от него, а индекс обходит папки по пути)
            var fileRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var fileLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in ScriptCompiler.LoadOrder(modules))
                foreach (var (logical, _) in m.Files)
                    if (logical != null && ws.LogicalToPath.TryGetValue(logical, out var absPath))
                    {
                        string full = Path.GetFullPath(absPath);
                        if (fileRank.ContainsKey(full)) continue;
                        fileRank[full] = fileRank.Count;
                        fileLabel[full] = logical; // «мод/путь.sal»: у базы и мода файлы часто тёзки
                    }
            _ls.FileOrder = key => key != null && fileRank.TryGetValue(key, out var rank) ? rank : int.MaxValue;
            _ls.FileLabel = key => key != null && fileLabel.TryGetValue(key, out var label) ? label : null;

            if (modules.Count > 0)
            {
                var result = ScriptCompiler.Compile(registry, apiVersion, modules);
                foreach (var d in result.Diagnostics)
                {
                    string abs = ws.LogicalToPath.TryGetValue(d.File, out var a) ? Path.GetFullPath(a) : d.File;
                    int severity = d.Severity == Severity.Error ? 1
                                 : d.Severity == Severity.Warning ? 2 : 3;
                    int line = Math.Max(1, d.Line);
                    int col = Math.Max(1, d.Column);
                    Bucket(abs).Add(LspDiag(line, col, TextUtil.WordLenAt(GetText(abs), line, col), severity, d.Code, d.Message));
                }
            }

            // --- публикация (пустой массив очищает старое) ---
            var toPublish = new HashSet<string>(_published, StringComparer.OrdinalIgnoreCase);
            foreach (var f in diagsByFile.Keys) toPublish.Add(f);
            _published.Clear();
            foreach (var file in toPublish)
            {
                diagsByFile.TryGetValue(file, out var arr);
                _rpc.Notify("textDocument/publishDiagnostics", new JObject
                {
                    ["uri"] = PathToUri(file),
                    ["diagnostics"] = arr ?? new JArray(),
                });
                if (arr != null && arr.Count > 0) _published.Add(file);
            }
        }

        private static JToken LspDiag(int line, int col, int len, int severity, string code, string message)
        {
            return new JObject
            {
                ["range"] = Range0(line, col, line, col + Math.Max(1, len)),
                ["severity"] = severity, // 1 error, 2 warning, 3 info
                ["code"] = code,
                ["source"] = "salamander",
                ["message"] = message,
            };
        }

        private static JObject Range0(int line1, int col1, int line2, int col2) => new JObject
        {
            ["start"] = new JObject { ["line"] = line1 - 1, ["character"] = col1 - 1 },
            ["end"] = new JObject { ["line"] = line2 - 1, ["character"] = col2 - 1 },
        };

        // ===================================================================
        // Протокол поверх языкового сервиса
        // ===================================================================

        private static (string path, int line1, int col1) Pos(JObject p) =>
            (UriToPath((string)p["textDocument"]["uri"]),
             (int)p["position"]["line"] + 1,
             (int)p["position"]["character"] + 1);

        private JToken Completion(JObject p)
        {
            var (path, line1, col1) = Pos(p);
            var items = new JArray();
            foreach (var c in _ls.Complete(path, line1, col1))
            {
                var it = new JObject { ["label"] = c.Label, ["kind"] = (int)c.Kind };
                if (c.Detail != null) it["detail"] = c.Detail;
                if (c.Documentation != null) it["documentation"] = new JObject { ["kind"] = "markdown", ["value"] = c.Documentation };
                if (c.InsertText != null) { it["insertText"] = c.InsertText; if (c.IsSnippet) it["insertTextFormat"] = 2; }
                items.Add(it);
            }
            return items;
        }

        private JToken SignatureHelp(JObject p)
        {
            var (path, line1, col1) = Pos(p);
            var s = _ls.SignatureHelp(path, line1, col1);
            if (s == null) return null;

            var ps = new JArray();
            foreach (var pl in s.Parameters) ps.Add(new JObject { ["label"] = pl });
            var sig = new JObject { ["label"] = s.Label, ["parameters"] = ps };
            if (s.Documentation != null) sig["documentation"] = new JObject { ["kind"] = "markdown", ["value"] = s.Documentation };
            return new JObject
            {
                ["signatures"] = new JArray(sig),
                ["activeSignature"] = 0,
                ["activeParameter"] = s.ActiveParameter,
            };
        }

        private JToken SemanticTokens(JObject p)
        {
            var path = UriToPath((string)p["textDocument"]["uri"]);
            var spans = _ls.Classify(path);
            if (spans == null) return new JObject { ["data"] = new JArray() };

            // дельта-кодирование протокола
            var data = new JArray();
            int lastLine = 1, lastCol = 1;
            foreach (var s in spans)
            {
                int dLine = s.Line - lastLine;
                int dCol = dLine == 0 ? s.Col - lastCol : s.Col - 1;
                data.Add(dLine); data.Add(dCol); data.Add(s.Length); data.Add(s.Type); data.Add(0);
                lastLine = s.Line; lastCol = s.Col;
            }
            return new JObject { ["data"] = data };
        }

        private JToken Hover(JObject p)
        {
            var (path, line1, col1) = Pos(p);
            var md = _ls.Hover(path, line1, col1);
            if (md == null) return null;
            return new JObject
            {
                ["contents"] = new JObject { ["kind"] = "markdown", ["value"] = md },
            };
        }

        private JToken Definition(JObject p)
        {
            var (path, line1, col1) = Pos(p);
            var loc = _ls.Definition(path, line1, col1);
            return loc == null ? null : Location(loc.File, loc.Line, loc.Col, loc.Length);
        }

        private static JObject Location(string absPath, int line1, int col1, int len) => new JObject
        {
            ["uri"] = PathToUri(absPath),
            ["range"] = Range0(line1, col1, line1, col1 + Math.Max(1, len)),
        };

        private JToken DocumentSymbols(JObject p)
        {
            var path = UriToPath((string)p["textDocument"]["uri"]);
            if (!_ls.Index.TryGet(path, out var fi)) return new JArray();

            var arr = new JArray();
            foreach (var d in fi.Decls)
            {
                var node = SymbolNode(d);
                foreach (var ch in d.Children)
                    ((JArray)node["children"]).Add(SymbolNode(ch));
                arr.Add(node);
            }
            return arr;
        }

        private static JObject SymbolNode(DeclSymbol s)
        {
            int kind = s.Kind switch
            {
                "class" => 5, "trigger" => 5, "listener" => 5,
                "enum" => 10, "member" => 22,
                "field" => 8, "const" => 14,
                "func" => 12, "action" => 12, "event" => 24,
                _ => 23, // блок-архетип
            };
            var range = Range0(s.Line, s.Col, s.Line, s.Col + Math.Max(1, s.Name?.Length ?? 1));
            return new JObject
            {
                ["name"] = s.Name ?? "?",
                ["detail"] = s.Kind,
                ["kind"] = kind,
                ["range"] = range,
                ["selectionRange"] = range,
                ["children"] = new JArray(),
            };
        }

        private JToken WorkspaceSymbols(JObject p)
        {
            string query = ((string)p?["query"] ?? "").ToLowerInvariant();
            var arr = new JArray();
            foreach (var kv in _ls.Index.Files)
            {
                foreach (var d in kv.Value.Decls)
                {
                    if (query.Length == 0 || d.Name.ToLowerInvariant().Contains(query))
                        arr.Add(new JObject
                        {
                            ["name"] = d.Name,
                            ["kind"] = d.Kind == "enum" ? 10 : 5,
                            ["location"] = Location(kv.Key, d.Line, d.Col, d.Name.Length),
                            ["containerName"] = d.Kind,
                        });
                    foreach (var ch in d.Children)
                        if (query.Length > 0 && ch.Name.ToLowerInvariant().Contains(query))
                            arr.Add(new JObject
                            {
                                ["name"] = ch.Name,
                                ["kind"] = ch.Kind == "event" ? 24 : ch.Kind == "field" ? 8 : 12,
                                ["location"] = Location(kv.Key, ch.Line, ch.Col, ch.Name.Length),
                                ["containerName"] = $"{d.Kind} {d.Name}",
                            });
                }
            }
            return arr;
        }

        // ===================================================================
        // URI <-> путь
        // ===================================================================

        private static string UriToPath(string uri)
        {
            try
            {
                // Uri.LocalPath на "file:///c%3A/..." от VS Code отдаёт "/c:/...",
                // и корень воркспейса вместе с ключами открытых буферов уезжал в
                // несуществующий "c:\c:\..." — см. ModuleLoader.PathFromFileUri
                string path = ModuleLoader.PathFromFileUri(uri);
                return Path.GetFullPath(path ?? new Uri(uri).LocalPath);
            }
            catch { return uri; }
        }

        private static string PathToUri(string path)
        {
            try { return new Uri(Path.GetFullPath(path)).AbsoluteUri; }
            catch { return "file:///" + path.Replace('\\', '/'); }
        }
    }
}
