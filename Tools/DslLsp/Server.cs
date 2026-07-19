using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dsl.Compilation;
using Dsl.Syntax;
using Dsl.Text;
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

        // манифест API — прямое чтение json (комплишены/hover), реестр — через Import (компиляция)
        private ApiManifest _api;

        // ===== синтакс-индекс: настоящий парсер вместо регэксов =============

        private sealed class Sym
        {
            public string Name;
            public string Kind;    // class/trigger/listener/enum/<вид архетипа>/field/const/func/event/action/member
            public int Line;       // 1-based
            public int Col;        // 1-based
            public readonly List<Sym> Children = new List<Sym>();
        }

        private sealed class FileIndex
        {
            public int Hash;
            public readonly List<Sym> Decls = new List<Sym>();
        }

        private readonly Dictionary<string, FileIndex> _index = new Dictionary<string, FileIndex>(StringComparer.OrdinalIgnoreCase);

        public Server(Rpc rpc) => _rpc = rpc;

        // ===================================================================
        // Главный цикл
        // ===================================================================

        public void Run()
        {
            while (true)
            {
                var msg = _rpc.Read();
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
                            ["tokenTypes"] = new JArray(TokenTypes),
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

        private void RefreshAll()
        {
            if (!_initialized || _root == null) return;

            // --- синтакс-индекс всех *.sal (диск + оверлеи), с кэшем по хэшу ---
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.EnumerateFiles(_root, "*.sal", SearchOption.AllDirectories))
                {
                    var abs = Path.GetFullPath(f);
                    seen.Add(abs);
                    IndexFile(abs, GetText(abs));
                }
            }
            catch { /* корень мог исчезнуть — не падаем */ }
            foreach (var kv in _open)
                if (kv.Key.EndsWith(".sal", StringComparison.OrdinalIgnoreCase) && seen.Add(kv.Key))
                    IndexFile(kv.Key, kv.Value);
            var stale = new List<string>();
            foreach (var key in _index.Keys)
                if (!seen.Contains(key)) stale.Add(key);
            foreach (var key in stale) _index.Remove(key);

            // --- компиляция воркспейса тем же путём, что DslCheck ---
            var diagsByFile = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
            JArray Bucket(string file)
            {
                if (!diagsByFile.TryGetValue(file, out var arr)) diagsByFile[file] = arr = new JArray();
                return arr;
            }

            string apiPath = Path.Combine(_root, "salamander-api.json");
            Semantics.HostRegistry registry;
            int apiVersion = 1;
            _api = null;
            if (File.Exists(apiPath))
            {
                try
                {
                    var apiText = File.ReadAllText(apiPath);
                    registry = ApiManifest.Import(apiText, out apiVersion);
                    _api = JsonConvert.DeserializeObject<ApiManifest>(apiText);
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
                Bucket(apiPath).Add(LspDiag(1, 1, 1, 2, "W0401",
                    "salamander-api.json не найден — события и API хоста неизвестны " +
                    "(запустите игру в редакторе один раз, манифест экспортируется автоматически)."));
            }

            var logicalToAbs = new Dictionary<string, string>();
            Action<string, string> onLoadError =
                (file, message) => Bucket(Path.GetFullPath(file)).Add(LspDiag(1, 1, 1, 1, "E0401", message));
            // «ешь то, что дал сборщик»: если он экспортировал salamander-build.json
            // (упорядоченный список папок модулей) — берём РОВНО его; обход папки
            // остаётся дев-режимом без сборщика
            string buildPath = Path.Combine(_root, "salamander-build.json");
            List<ModuleSourceSet> modules;
            if (File.Exists(buildPath))
            {
                try
                {
                    var build = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(buildPath));
                    var dirs = new List<string>();
                    foreach (var t in build["modules"] ?? new Newtonsoft.Json.Linq.JArray())
                        dirs.Add(Path.GetFullPath(Path.Combine(_root, (string)t)));
                    modules = ModuleLoader.LoadFromList(dirs, onLoadError, logicalToAbs);
                }
                catch (Exception ex)
                {
                    onLoadError(buildPath, "salamander-build.json не читается — " + ex.Message);
                    modules = new List<ModuleSourceSet>();
                }
            }
            else
            {
                modules = ModuleLoader.LoadFromFolder(_root, onLoadError, logicalToAbs);
            }

            // оверлеи: несохранённые правки важнее диска
            foreach (var set in modules)
            {
                for (int i = 0; i < set.Files.Count; i++)
                {
                    var (logical, _) = set.Files[i];
                    if (logicalToAbs.TryGetValue(logical, out var abs)
                        && _open.TryGetValue(Path.GetFullPath(abs), out var live))
                        set.Files[i] = (logical, live);
                }
            }

            if (modules.Count > 0)
            {
                var result = ScriptCompiler.Compile(registry, apiVersion, modules);
                foreach (var d in result.Diagnostics)
                {
                    string abs = logicalToAbs.TryGetValue(d.File, out var a) ? Path.GetFullPath(a) : d.File;
                    int severity = d.Severity == Severity.Error ? 1
                                 : d.Severity == Severity.Warning ? 2 : 3;
                    int line = Math.Max(1, d.Line);
                    int col = Math.Max(1, d.Column);
                    Bucket(abs).Add(LspDiag(line, col, WordLenAt(GetText(abs), line, col), severity, d.Code, d.Message));
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

        private static int WordLenAt(string text, int line, int col)
        {
            var l = GetLine(text, line);
            if (l == null || col - 1 >= l.Length) return 1;
            int i = col - 1, n = 0;
            while (i + n < l.Length && (char.IsLetterOrDigit(l[i + n]) || l[i + n] == '_')) n++;
            return Math.Max(1, n);
        }

        private static string GetLine(string text, int line1)
        {
            if (text == null) return null;
            int cur = 1, start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == '\n')
                {
                    if (cur == line1)
                    {
                        int end = i;
                        if (end > start && text[end - 1] == '\r') end--;
                        return text.Substring(start, end - start);
                    }
                    cur++;
                    start = i + 1;
                }
            }
            return null;
        }

        // ===================================================================
        // Синтакс-индекс: декларации и члены из настоящего парсера
        // ===================================================================

        private void IndexFile(string absPath, string text)
        {
            if (text == null) { _index.Remove(absPath); return; }
            int hash = text.GetHashCode();
            if (_index.TryGetValue(absPath, out var cached) && cached.Hash == hash) return;

            var fi = new FileIndex { Hash = hash };
            try
            {
                var src = new SourceText(0, absPath, text);
                var bag = new DiagnosticBag(new[] { src });
                var lexer = new Lexer(text, 0, bag);
                var parser = new Parser(lexer.Tokenize(), 0, bag);
                var file = parser.ParseFile();

                foreach (var d in file.Decls)
                {
                    if (d == null) continue;
                    var sym = new Sym { Name = d.Name, Line = d.Pos.Line, Col = d.Pos.Column };
                    List<Member> members = null;
                    switch (d)
                    {
                        case ClassDecl c: sym.Kind = "class"; members = c.Members; break;
                        case TriggerDecl t: sym.Kind = "trigger"; members = t.Members; break;
                        case ListenerDecl l: sym.Kind = "listener"; members = l.Members; break;
                        case ArchetypeDecl a: sym.Kind = a.Kind; members = a.Members; break;
                        case EnumDecl e:
                            sym.Kind = "enum";
                            foreach (var m in e.Members)
                                sym.Children.Add(new Sym { Name = m, Kind = "member", Line = d.Pos.Line, Col = d.Pos.Column });
                            break;
                        default: continue;
                    }
                    if (members != null)
                        foreach (var m in members)
                            switch (m)
                            {
                                case FieldMember f:
                                    sym.Children.Add(new Sym
                                    {
                                        Name = f.Name,
                                        Kind = f.IsConst ? "const" : "field",
                                        Line = f.Pos.Line,
                                        Col = f.Pos.Column,
                                    });
                                    break;
                                case FuncMember fn:
                                    sym.Children.Add(new Sym
                                    {
                                        Name = fn.Name,
                                        Kind = fn.Kind == FuncKind.Event ? "event"
                                             : fn.Kind == FuncKind.Action ? "action" : "func",
                                        Line = fn.Pos.Line,
                                        Col = fn.Pos.Column,
                                    });
                                    break;
                            }
                    fi.Decls.Add(sym);
                }
            }
            catch { /* синтакс-мусор не должен ронять индекс */ }

            _index[absPath] = fi;
        }

        private Sym EnclosingDecl(string absPath, int line1)
        {
            if (!_index.TryGetValue(absPath, out var fi)) return null;
            Sym best = null;
            foreach (var d in fi.Decls)
                if (d.Line <= line1 && (best == null || d.Line > best.Line))
                    best = d;
            return best;
        }

        // ===================================================================
        // Комплишены
        // ===================================================================

        private JToken Completion(JObject p)
        {
            var path = UriToPath((string)p["textDocument"]["uri"]);
            int line1 = (int)p["position"]["line"] + 1;
            int col1 = (int)p["position"]["character"] + 1;
            var lineText = GetLine(GetText(path), line1) ?? "";
            var before = lineText.Substring(0, Math.Min(col1 - 1, lineText.Length));

            var items = new JArray();
            void Add(string label, int kind, string detail, string doc = null, string insert = null, bool snippet = false)
            {
                var it = new JObject { ["label"] = label, ["kind"] = kind };
                if (detail != null) it["detail"] = detail;
                if (doc != null) it["documentation"] = new JObject { ["kind"] = "markdown", ["value"] = doc };
                if (insert != null) { it["insertText"] = insert; if (snippet) it["insertTextFormat"] = 2; }
                items.Add(it);
            }

            // 1) Engine.<...>
            var mDot = System.Text.RegularExpressions.Regex.Match(before, @"(\w+)\.\w*$");
            if (mDot.Success)
            {
                string target = mDot.Groups[1].Value;
                if (target == "Engine")
                {
                    foreach (var em in EngineDocs.Methods)
                        Add(em.Name, 2, em.Signature, em.Summary,
                            insert: CallSnippet(em.Name, ParamLabels(em)), snippet: true);
                    return items;
                }
                // API хоста
                if (_api?.Apis != null)
                    foreach (var api in _api.Apis)
                        if (api.Name == target)
                        {
                            foreach (var me in api.Methods)
                                Add(me.Name, 2, MethodSig(api.Name, me), MethodDocMd(me),
                                    insert: CallSnippet(me.Name, ParamLabels(me)), snippet: true);
                            return items;
                        }
                // енумы (манифест + скриптовые)
                if (_api?.Enums != null)
                    foreach (var en in _api.Enums)
                        if (en.Name == target)
                        {
                            foreach (var mem in en.Members) Add(mem, 20, en.Name);
                            return items;
                        }
                foreach (var fi in _index.Values)
                    foreach (var d in fi.Decls)
                        if (d.Name == target && (d.Kind == "enum" || d.Kind == "class"))
                        {
                            foreach (var ch in d.Children)
                                Add(ch.Name,
                                    ch.Kind == "func" ? 2 : ch.Kind == "member" ? 20 : ch.Kind == "const" ? 14 : 5,
                                    $"{d.Kind} {d.Name}",
                                    insert: ch.Kind == "func" ? $"{ch.Name}($1)$0" : null,
                                    snippet: ch.Kind == "func");
                            return items;
                        }

                // цель — ЗНАЧЕНИЕ (локаль/параметр/поле). Тип угадываем по
                // объявлению «Тип имя» выше по файлу (event OnDeath(Unit killer...)
                // или Unit u = ...); нашли класс хоста — его свойства, нет —
                // объединение свойств всех сущностей (лучше, чем тишина)
                if (_api?.Classes != null && _api.Classes.Length > 0)
                {
                    var cls = GuessValueClass(GetText(path), line1, col1, target);
                    if (cls != null)
                    {
                        foreach (var pr in cls.Props)
                            Add(pr.Name, 10, $"{cls.Name}.{pr.Name}: {pr.Type}", pr.Doc);
                        return items;
                    }
                    foreach (var c in _api.Classes)
                        foreach (var pr in c.Props)
                            Add(pr.Name, 10, $"{c.Name}: {pr.Type}", pr.Doc);
                }
                return items;
            }

            // 2) event <...> — набор событий зависит от того, в чём мы стоим
            if (System.Text.RegularExpressions.Regex.IsMatch(before, @"\bevent\s+\w*$"))
            {
                var encl = EnclosingDecl(path, line1);
                ApiManifest.EventDef[] events = _api?.Events;
                if (encl != null && _api?.Archetypes != null)
                    foreach (var k in _api.Archetypes)
                        if (k.Name == encl.Kind) { events = k.Events; break; }
                if (events != null)
                    foreach (var ev in events)
                        Add(ev.Name, 23, EventSig(ev), ev.Summary,
                            insert: EventSnippet(ev), snippet: true);
                if (encl != null && encl.Kind == "listener")
                {
                    Add("OnSubscribe", 23, "при Engine.Attach", null, "OnSubscribe()\n{\n\t$0\n}", true);
                    Add("OnUnsubscribe", 23, "при detach (без wait/spawn)", null, "OnUnsubscribe()\n{\n\t$0\n}", true);
                }
                return items;
            }

            // 3) голый идентификатор: ключевые слова + типы + глобалы + API
            foreach (var kw in EngineDocs.Keywords) Add(kw, 14, null);
            foreach (var tp in EngineDocs.Types) Add(tp, 7, null);
            Add("Engine", 9, "встроенный класс движка");
            if (_api?.Apis != null) foreach (var api in _api.Apis) Add(api.Name, 9, api.Summary ?? "API игры");
            if (_api?.Enums != null) foreach (var en in _api.Enums) Add(en.Name, 13, en.Summary ?? "enum хоста");
            if (_api?.Classes != null) foreach (var c in _api.Classes) Add(c.Name, 7, c.Summary ?? "сущность игры");
            foreach (var fi in _index.Values)
                foreach (var d in fi.Decls)
                    Add(d.Name, d.Kind == "enum" ? 13 : 7, d.Kind);
            return items;
        }

        /// <summary>
        /// Тип значения по ближайшему объявлению «Тип имя» выше курсора:
        /// параметры событий/функций и локали с явным типом. Возвращает класс
        /// хоста из манифеста или null.
        /// </summary>
        private ApiManifest.ClassDef GuessValueClass(string text, int line1, int col1, string name)
        {
            if (text == null || _api?.Classes == null) return null;
            int cursor = OffsetOf(text, line1, col1);
            var rx = new System.Text.RegularExpressions.Regex($@"\b([A-Za-z_]\w*)\s+{System.Text.RegularExpressions.Regex.Escape(name)}\b");
            string best = null;
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(text))
            {
                if (m.Index <= cursor) best = m.Groups[1].Value; // ближайшее ДО курсора побеждает
                else if (best == null) { best = m.Groups[1].Value; break; } // иначе первое после
            }
            if (best == null) return null;
            foreach (var c in _api.Classes)
                if (c.Name == best) return c;
            return null;
        }

        private static int OffsetOf(string text, int line1, int col1)
        {
            int line = 1, i = 0;
            while (i < text.Length && line < line1)
            {
                if (text[i] == '\n') line++;
                i++;
            }
            return Math.Min(text.Length, i + Math.Max(0, col1 - 1));
        }

        /// <summary>Типизированные плейсхолдеры аргументов для табуляции по вызову.</summary>
        private static string CallSnippet(string name, List<string> paramLabels)
        {
            if (paramLabels.Count == 0) return name + "()$0";
            var sb = new StringBuilder(name).Append('(');
            for (int i = 0; i < paramLabels.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("${").Append(i + 1).Append(':').Append(paramLabels[i].Replace("}", "\\}")).Append('}');
            }
            return sb.Append(")$0").ToString();
        }

        private static List<string> ParamLabels(EngineMethod em)
        {
            var r = new List<string>();
            foreach (var (name, type) in em.Params) r.Add($"{type} {name}");
            return r;
        }

        private static List<string> ParamLabels(ApiManifest.MethodDef m)
        {
            var r = new List<string>();
            foreach (var pd in m.Params) r.Add($"{pd.Type} {pd.Name}");
            return r;
        }

        // ===================================================================
        // SignatureHelp: активная сигнатура + подсветка текущего аргумента
        // ===================================================================

        private JToken SignatureHelp(JObject p)
        {
            var path = UriToPath((string)p["textDocument"]["uri"]);
            int line1 = (int)p["position"]["line"] + 1;
            int col1 = (int)p["position"]["character"] + 1;
            var lineText = GetLine(GetText(path), line1) ?? "";
            var before = lineText.Substring(0, Math.Min(col1 - 1, lineText.Length));

            // до внутренней незакрытой '(' (строки грубо вычищаем)
            var clean = System.Text.RegularExpressions.Regex.Replace(before, "\"(?:\\\\.|[^\"])*\"?", m => new string(' ', m.Length));
            int depth = 0, open = -1, commas = 0;
            for (int i = clean.Length - 1; i >= 0; i--)
            {
                char c = clean[i];
                if (c == ')') depth++;
                else if (c == '(')
                {
                    if (depth == 0) { open = i; break; }
                    depth--;
                }
            }
            if (open < 0) return null;
            for (int i = open + 1, d2 = 0; i < clean.Length; i++)
            {
                char c = clean[i];
                if (c == '(') d2++;
                else if (c == ')') d2--;
                else if (c == ',' && d2 == 0) commas++;
            }

            var head = clean.Substring(0, open);
            var m2 = System.Text.RegularExpressions.Regex.Match(head, "(?:(\\w+)\\.)?(\\w+)\\s*$");
            if (!m2.Success) return null;
            string owner = m2.Groups[1].Value;
            string method = m2.Groups[2].Value;

            string label = null, doc = null;
            List<string> plabels = null;
            if (owner == "Engine")
            {
                foreach (var em in EngineDocs.Methods)
                    if (em.Name == method) { label = em.Signature; doc = em.Summary; plabels = ParamLabels(em); break; }
            }
            else if (owner.Length > 0 && _api?.Apis != null)
            {
                foreach (var api in _api.Apis)
                    if (api.Name == owner)
                        foreach (var me in api.Methods)
                            if (me.Name == method)
                            { label = MethodSig(api.Name, me); doc = MethodDocMd(me); plabels = ParamLabels(me); break; }
            }
            if (label == null) return null;

            var ps = new JArray();
            foreach (var pl in plabels) ps.Add(new JObject { ["label"] = pl });
            var sig = new JObject { ["label"] = label, ["parameters"] = ps };
            if (doc != null) sig["documentation"] = new JObject { ["kind"] = "markdown", ["value"] = doc };
            return new JObject
            {
                ["signatures"] = new JArray(sig),
                ["activeSignature"] = 0,
                ["activeParameter"] = Math.Min(commas, Math.Max(0, plabels.Count - 1)),
            };
        }

        // ===================================================================
        // Семантическая подсветка: раскраска приходит с сервера — работает в
        // любом клиенте (Rider без TextMate тоже цветной)
        // ===================================================================

        private static readonly string[] TokenTypes =
        {
            "keyword", "type", "class", "function", "property", "variable",
            "string", "number", "comment", "event", "namespace", "enumMember",
        };
        private const int TtKeyword = 0, TtType = 1, TtClass = 2, TtFunction = 3, TtProperty = 4,
                          TtVariable = 5, TtString = 6, TtNumber = 7, TtComment = 8, TtEvent = 9,
                          TtNamespace = 10;

        private JToken SemanticTokens(JObject p)
        {
            var path = UriToPath((string)p["textDocument"]["uri"]);
            var text = GetText(path);
            if (text == null) return new JObject { ["data"] = new JArray() };

            var spans = new List<(int line, int col, int len, int type)>();

            // 1) строки и комментарии — сырым проходом (лексер их не отдаёт);
            //    дырки интерполяции {expr} собираем отдельно — это КОД
            var holes = new List<(int line, int col, string src)>();
            ScanStringsAndComments(text, spans, holes);

            // 2) остальное — токенами настоящего лексера
            var declNames = new HashSet<string>(StringComparer.Ordinal);
            var kindNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fi in _index.Values)
                foreach (var d in fi.Decls)
                {
                    declNames.Add(d.Name);
                    if (d.Kind != "class" && d.Kind != "trigger" && d.Kind != "listener" && d.Kind != "enum")
                        kindNames.Add(d.Kind); // слова-виды архетипов (spell/item/...)
                }
            if (_api?.Archetypes != null)
                foreach (var k in _api.Archetypes) kindNames.Add(k.Name);
            var apiNames = new HashSet<string>(StringComparer.Ordinal) { "Engine" };
            if (_api?.Apis != null) foreach (var a in _api.Apis) apiNames.Add(a.Name);
            var typeNames = new HashSet<string>(EngineDocs.Types, StringComparer.Ordinal);
            if (_api?.Classes != null) foreach (var c in _api.Classes) typeNames.Add(c.Name);
            if (_api?.Enums != null) foreach (var e in _api.Enums) typeNames.Add(e.Name);

            // общая классификация токена (главный текст и дырки интерполяции)
            int Classify(TokenKind kind, string txt, TokenKind prev, TokenKind next)
            {
                if (kind.ToString().StartsWith("Kw")) return TtKeyword;
                if (kind == TokenKind.Int || kind == TokenKind.Float) return TtNumber;
                if (kind != TokenKind.Ident) return -1; // пунктуация — цвет темы
                if (prev == TokenKind.KwEvent) return TtEvent;
                if (prev == TokenKind.Dot) return next == TokenKind.LParen ? TtFunction : TtProperty;
                if (next == TokenKind.LParen) return TtFunction;
                if (apiNames.Contains(txt)) return TtNamespace;
                if (kindNames.Contains(txt)) return TtKeyword;   // spell/item — читаются как слова языка
                if (typeNames.Contains(txt)) return TtType;
                if (declNames.Contains(txt)) return TtClass;
                return TtVariable;
            }

            try
            {
                var bag = new DiagnosticBag(new[] { new SourceText(0, path, text) });
                var toks = new Lexer(text, 0, bag).Tokenize();
                for (int i = 0; i < toks.Count; i++)
                {
                    var t = toks[i];
                    if (t.Kind == TokenKind.String || t.Kind == TokenKind.InterpString) continue; // покрашены сканером
                    string txt = t.Text ?? "";
                    if (txt.Length == 0) continue;
                    int type = Classify(t.Kind,
                        txt,
                        i > 0 ? toks[i - 1].Kind : TokenKind.Eof,
                        i + 1 < toks.Count ? toks[i + 1].Kind : TokenKind.Eof);
                    if (type >= 0) spans.Add((t.Pos.Line, t.Pos.Column, txt.Length, type));
                }
            }
            catch { /* битый синтаксис не должен гасить подсветку строк/комментариев */ }

            // дырки интерполяции: лексим содержимое как обычный код
            foreach (var (hLine, hCol, src) in holes)
            {
                try
                {
                    var hbag = new DiagnosticBag(new[] { new SourceText(0, path, src) });
                    var htoks = new Lexer(src, 0, hbag).Tokenize();
                    for (int i = 0; i < htoks.Count; i++)
                    {
                        var t = htoks[i];
                        if (t.Kind == TokenKind.String || t.Kind == TokenKind.InterpString) continue;
                        string txt = t.Text ?? "";
                        if (txt.Length == 0) continue;
                        int type = Classify(t.Kind,
                            txt,
                            i > 0 ? htoks[i - 1].Kind : TokenKind.Eof,
                            i + 1 < htoks.Count ? htoks[i + 1].Kind : TokenKind.Eof);
                        // дырки однострочные: строка та же, колонка со смещением
                        if (type >= 0) spans.Add((hLine, hCol + t.Pos.Column - 1, txt.Length, type));
                    }
                }
                catch { }
            }

            // 3) сортировка и дельта-кодирование протокола
            spans.Sort((a, b) => a.line != b.line ? a.line - b.line : a.col - b.col);
            var data = new JArray();
            int lastLine = 1, lastCol = 1;
            foreach (var (line, col, len, type) in spans)
            {
                int dLine = line - lastLine;
                int dCol = dLine == 0 ? col - lastCol : col - 1;
                data.Add(dLine); data.Add(dCol); data.Add(len); data.Add(type); data.Add(0);
                lastLine = line; lastCol = col;
            }
            return new JObject { ["data"] = data };
        }

        /// <summary>
        /// Строки (обычные и $"...") и комментарии // и /* */ — по сырому тексту,
        /// построчными кусками; дырки интерполяции {expr} отдаются отдельно.
        /// </summary>
        private static void ScanStringsAndComments(string text, List<(int, int, int, int)> spans,
                                                   List<(int line, int col, string src)> holes)
        {
            int line = 1, col = 1;
            int i = 0, n = text.Length;
            void Advance(char c) { if (c == '\n') { line++; col = 1; } else col++; }

            while (i < n)
            {
                char c = text[i];
                if (c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    int startCol = col, startLine = line, len = 0;
                    while (i < n && text[i] != '\n') { len++; Advance(text[i]); i++; }
                    spans.Add((startLine, startCol, len, TtComment));
                }
                else if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    int segLine = line, segCol = col, segLen = 0;
                    while (i < n)
                    {
                        bool end = text[i] == '*' && i + 1 < n && text[i + 1] == '/';
                        if (text[i] == '\n')
                        {
                            if (segLen > 0) spans.Add((segLine, segCol, segLen, TtComment));
                            Advance(text[i]); i++;
                            segLine = line; segCol = col; segLen = 0;
                            continue;
                        }
                        segLen++; Advance(text[i]); i++;
                        if (end) { segLen++; Advance(text[i]); i++; break; }
                    }
                    if (segLen > 0) spans.Add((segLine, segCol, segLen, TtComment));
                }
                else if (c == '"' || (c == '$' && i + 1 < n && text[i + 1] == '"'))
                {
                    bool interp = c == '$';
                    int segLine = line, segCol = col, segLen = 0;
                    void Flush() { if (segLen > 0) spans.Add((segLine, segCol, segLen, TtString)); segLen = 0; }

                    if (interp) { segLen++; Advance(text[i]); i++; }
                    segLen++; Advance(text[i]); i++; // открывающая кавычка
                    while (i < n && text[i] != '\n')
                    {
                        if (text[i] == '\\' && i + 1 < n) { segLen += 2; Advance(text[i]); i++; Advance(text[i]); i++; continue; }
                        if (interp && text[i] == '{' && i + 1 < n && text[i + 1] == '{')
                        { segLen += 2; Advance(text[i]); i++; Advance(text[i]); i++; continue; }
                        if (interp && text[i] == '{')
                        {
                            // скобка — ещё строка, содержимое дырки — код
                            segLen++; Advance(text[i]); i++;
                            Flush();
                            int hLine = line, hCol = col;
                            var sb = new StringBuilder();
                            while (i < n && text[i] != '}' && text[i] != '"' && text[i] != '\n')
                            { sb.Append(text[i]); Advance(text[i]); i++; }
                            if (sb.Length > 0) holes.Add((hLine, hCol, sb.ToString()));
                            segLine = line; segCol = col; segLen = 0;
                            if (i < n && text[i] == '}') { segLen++; Advance(text[i]); i++; }
                            continue;
                        }
                        bool close = text[i] == '"';
                        segLen++; Advance(text[i]); i++;
                        if (close) break;
                    }
                    Flush();
                }
                else { Advance(c); i++; }
            }
        }

        private static string MethodSig(string owner, ApiManifest.MethodDef m)
        {
            var ps = new List<string>();
            foreach (var pd in m.Params) ps.Add($"{pd.Type} {pd.Name}");
            return $"{owner}.{m.Name}({string.Join(", ", ps)}) -> {m.Returns}";
        }

        private static string MethodDocMd(ApiManifest.MethodDef m)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(m.Summary)) sb.AppendLine(m.Summary);
            foreach (var pd in m.Params)
                if (!string.IsNullOrEmpty(pd.Doc)) sb.AppendLine($"- `{pd.Name}` — {pd.Doc}");
            return sb.Length == 0 ? null : sb.ToString();
        }

        private static string EventSig(ApiManifest.EventDef ev)
        {
            var ps = new List<string>();
            foreach (var pd in ev.Params) ps.Add($"{pd.Type} {pd.Name}");
            return $"event {ev.Name}({string.Join(", ", ps)})";
        }

        private static string EventSnippet(ApiManifest.EventDef ev)
        {
            var ps = new List<string>();
            foreach (var pd in ev.Params) ps.Add($"{pd.Type} {pd.Name}");
            return $"{ev.Name}({string.Join(", ", ps)})\n{{\n\t$0\n}}";
        }

        // ===================================================================
        // Hover
        // ===================================================================

        private JToken Hover(JObject p)
        {
            var path = UriToPath((string)p["textDocument"]["uri"]);
            int line1 = (int)p["position"]["line"] + 1;
            int col1 = (int)p["position"]["character"] + 1;
            var lineText = GetLine(GetText(path), line1) ?? "";
            var (word, wordCol) = WordAt(lineText, col1);
            if (word == null) return null;

            string md = null;
            bool afterEngine = HasPrefix(lineText, wordCol, "Engine.");

            if (afterEngine)
            {
                foreach (var em in EngineDocs.Methods)
                    if (em.Name == word) { md = $"```\n{em.Signature}\n```\n{em.Summary}"; break; }
            }
            if (md == null && _api?.Apis != null)
                foreach (var api in _api.Apis)
                    if (HasPrefix(lineText, wordCol, api.Name + "."))
                        foreach (var me in api.Methods)
                            if (me.Name == word)
                            { md = $"```\n{MethodSig(api.Name, me)}\n```\n{MethodDocMd(me) ?? ""}"; break; }
            if (md == null && _api?.Events != null)
                foreach (var ev in _api.Events)
                    if (ev.Name == word)
                    { md = $"```\n{EventSig(ev)}\n```\n{ev.Summary ?? "Событие игры."}"; break; }
            if (md == null && _api?.Classes != null)
            {
                // свойство сущности (u.name): показываем, если имя уникально среди классов
                ApiManifest.ClassDef ownerCls = null; ApiManifest.PropDef prop = null; int hits = 0;
                foreach (var c in _api.Classes)
                    foreach (var pr in c.Props)
                        if (pr.Name == word) { hits++; ownerCls = c; prop = pr; }
                if (hits == 1)
                    md = $"```\n{ownerCls.Name}.{prop.Name}: {prop.Type}{(prop.ReadOnly ? " (только чтение)" : "")}\n```\n{prop.Doc ?? ""}";
            }
            if (md == null)
            {
                foreach (var fi in _index.Values)
                    foreach (var d in fi.Decls)
                        if (d.Name == word) { md = $"**{d.Kind} {d.Name}**"; break; }
            }

            if (md == null) return null;
            return new JObject
            {
                ["contents"] = new JObject { ["kind"] = "markdown", ["value"] = md },
            };
        }

        private static (string word, int col1) WordAt(string line, int col1)
        {
            if (line.Length == 0) return (null, 0);
            int i = Math.Min(col1 - 1, line.Length - 1);
            bool IsW(char c) => char.IsLetterOrDigit(c) || c == '_';
            if (!IsW(line[i]) && i > 0 && IsW(line[i - 1])) i--;
            if (!IsW(line[i])) return (null, 0);
            int s = i; while (s > 0 && IsW(line[s - 1])) s--;
            int e = i; while (e + 1 < line.Length && IsW(line[e + 1])) e++;
            return (line.Substring(s, e - s + 1), s + 1);
        }

        private static bool HasPrefix(string line, int wordCol1, string prefix)
        {
            int end = wordCol1 - 1;
            int start = end - prefix.Length;
            return start >= 0 && string.CompareOrdinal(line, start, prefix, 0, prefix.Length) == 0;
        }

        // ===================================================================
        // Definition / символы
        // ===================================================================

        private JToken Definition(JObject p)
        {
            var path = UriToPath((string)p["textDocument"]["uri"]);
            int line1 = (int)p["position"]["line"] + 1;
            int col1 = (int)p["position"]["character"] + 1;
            var lineText = GetLine(GetText(path), line1) ?? "";
            var (word, _) = WordAt(lineText, col1);
            if (word == null) return null;
            int ix = word.LastIndexOf(':');
            if (ix >= 0) word = word.Substring(ix + 1); // module::Name -> Name

            // 1) член объемлющей декларации (локальные func/поля этого файла)
            var encl = EnclosingDecl(path, line1);
            if (encl != null)
                foreach (var ch in encl.Children)
                    if (ch.Name == word)
                        return Location(path, ch.Line, ch.Col, word.Length);

            // 2) глобальные декларации по всем файлам
            foreach (var kv in _index)
                foreach (var d in kv.Value.Decls)
                    if (d.Name == word)
                        return Location(kv.Key, d.Line, d.Col, word.Length);

            // 3) уникальный член где угодно (func класса, событие)
            string foundFile = null; Sym found = null; int hits = 0;
            foreach (var kv in _index)
                foreach (var d in kv.Value.Decls)
                    foreach (var ch in d.Children)
                        if (ch.Name == word) { hits++; foundFile = kv.Key; found = ch; }
            if (hits == 1)
                return Location(foundFile, found.Line, found.Col, word.Length);

            return null;
        }

        private static JObject Location(string absPath, int line1, int col1, int len) => new JObject
        {
            ["uri"] = PathToUri(absPath),
            ["range"] = Range0(line1, col1, line1, col1 + Math.Max(1, len)),
        };

        private JToken DocumentSymbols(JObject p)
        {
            var path = UriToPath((string)p["textDocument"]["uri"]);
            if (!_index.TryGetValue(path, out var fi)) return new JArray();

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

        private static JObject SymbolNode(Sym s)
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
            foreach (var kv in _index)
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
            try { return Path.GetFullPath(new Uri(uri).LocalPath); }
            catch { return uri; }
        }

        private static string PathToUri(string path)
        {
            try { return new Uri(Path.GetFullPath(path)).AbsoluteUri; }
            catch { return "file:///" + path.Replace('\\', '/'); }
        }
    }
}
