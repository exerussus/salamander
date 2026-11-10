using System;
using System.Collections.Generic;
using System.IO;
using Dsl.Compilation;
using Dsl.Text;
using Dsl.Json;

namespace Dsl.Tools
{
    /// <summary>
    /// Command-line checker for Salamander scripts: the same compiler the game
    /// uses, run outside the game.
    ///
    ///   dotnet DslCheck.dll &lt;modules folder&gt; [--api &lt;salamander-api.json&gt;] [--json]
    ///
    /// salamander-api.json is the host API manifest (the game exports it
    /// automatically, see ScriptHostBootstrap). By default it is looked up in
    /// the root of the modules folder. --json prints diagnostics as an array
    /// with absolute paths; that mode is used by the VS Code extension.
    ///
    /// Exit code: 0 — no errors, 1 — errors present, 2 — bad invocation.
    /// </summary>
    public static class Program
    {
        private sealed class JsonDiag
        {
            public string File;
            public int Line;
            public int Column;
            public string Severity;
            public string Code;
            public string Message;

            public JsonObject ToJson() => new JsonObject
            {
                ["file"] = File, ["line"] = Line, ["column"] = Column,
                ["severity"] = Severity, ["code"] = Code, ["message"] = Message,
            };
        }

        public static int Main(string[] args)
        {
            // UTF-8 without BOM: JSON output and non-ASCII diagnostics must survive
            // pipes and consoles (especially on Windows) intact.
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);

            string root = null;
            string apiPath = null;
            bool json = false;
            string langArg = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--api":
                        if (++i >= args.Length) return Usage();
                        apiPath = args[i];
                        break;
                    case "--json":
                        json = true;
                        break;
                    case "--language":
                    case "--lang":
                        if (++i >= args.Length) return Usage();
                        langArg = args[i];
                        if (!Loc.TryParse(langArg, out _)) return Usage();
                        break;
                    default:
                        if (root != null) return Usage();
                        root = args[i];
                        break;
                }
            }

            if (root == null) return Usage();
            root = Path.GetFullPath(root);
            apiPath ??= Path.Combine(root, "salamander-api.json");

            var extra = new List<JsonDiag>(); // load-level / manifest errors

            // язык: --language > "language" в salamander-build.json > SALAMANDER_LANGUAGE > ru
            string buildPath = Path.Combine(root, "salamander-build.json");
            Language lang = Language.Ru;
            if (Loc.TryParse(Environment.GetEnvironmentVariable("SALAMANDER_LANGUAGE"), out var envLang)) lang = envLang;
            if (langArg != null) Loc.TryParse(langArg, out lang);
            else if (File.Exists(buildPath))
            {
                try
                {
                    var b = JsonNode.Parse(File.ReadAllText(buildPath));
                    if (Loc.TryParse(b["language"]?.AsString, out var fromBuild)) lang = fromBuild;
                }
                catch { /* ошибка формата сообщится ниже, при чтении модулей */ }
            }
            _lang = lang;

            // ----- API registry from the manifest -----
            Semantics.HostRegistry registry;
            int apiVersion;
            if (File.Exists(apiPath))
            {
                try
                {
                    registry = ApiManifest.Import(File.ReadAllText(apiPath), out apiVersion, lang);
                }
                catch (Exception ex)
                {
                    Emit(extra, apiPath, "E0400", T("salamander-api.json не читается: " + ex.Message,
                                                    "salamander-api.json could not be read: " + ex.Message));
                    return Output(json, extra, null, null);
                }
            }
            else
            {
                registry = new Semantics.HostRegistry { Language = lang };
                apiVersion = 1;
                Emit(extra, apiPath, "W0401",
                    T("salamander-api.json не найден — события и API хоста неизвестны " +
                      "(запустите игру в редакторе один раз, манифест экспортируется).",
                      "salamander-api.json not found — host events and API are unknown " +
                      "(run the game once in the editor so it gets exported)."),
                    warning: true);
            }

            // ----- modules -----
            var logicalToAbs = new Dictionary<string, string>();
            Action<string, string> onLoadError = (file, message) => Emit(extra, file, "E0401", message);
            // «ешь то, что дал сборщик»: если он экспортировал salamander-build.json
            // (упорядоченный список папок модулей) — берём РОВНО его; обход папки
            // остаётся дев-режимом без сборщика
            List<ModuleSourceSet> modules;
            if (File.Exists(buildPath))
            {
                try
                {
                    var build = JsonRead.Object(JsonNode.Parse(File.ReadAllText(buildPath), lang), "salamander-build.json", lang)
                                ?? new JsonObject();
                    var dirs = new List<string>();
                    foreach (var t in JsonRead.Array(build["modules"], "salamander-build.json: modules", lang) ?? new JsonArray())
                        dirs.Add(Path.GetFullPath(Path.Combine(root, JsonRead.String(t, "salamander-build.json: modules[]", lang) ?? "")));
                    modules = ModuleLoader.LoadFromList(dirs, onLoadError, logicalToAbs, lang);
                }
                catch (Exception ex)
                {
                    onLoadError(buildPath, T("salamander-build.json не читается — " + ex.Message,
                                             "salamander-build.json cannot be read — " + ex.Message));
                    modules = new List<ModuleSourceSet>();
                }
            }
            else
            {
                modules = ModuleLoader.LoadFromFolder(root, onLoadError, logicalToAbs, lang);
            }

            if (modules.Count == 0)
                Emit(extra, root, "W0402", T("в папке не найдено модулей (подпапок с module.json).",
                                             "no modules found in the folder (a subfolder with module.json)."), warning: true);

            // ----- compile -----
            var result = ScriptCompiler.Compile(registry, apiVersion, modules, false, lang);
            return Output(json, extra, result, logicalToAbs);
        }

        private static Language _lang;

        private static string T(string ru, string en) => Loc.T(_lang, ru, en);

        private static void Emit(List<JsonDiag> into, string file, string code, string message, bool warning = false)
        {
            into.Add(new JsonDiag
            {
                File = file,
                Line = 1,
                Column = 1,
                Severity = warning ? "warning" : "error",
                Code = code,
                Message = message,
            });
        }

        private static int Output(bool json, List<JsonDiag> extra,
                                  CompilationResult result, Dictionary<string, string> logicalToAbs)
        {
            var all = new List<JsonDiag>(extra);
            bool hasErrors = false;

            foreach (var d in extra)
                if (d.Severity == "error") hasErrors = true;

            if (result != null)
            {
                foreach (var d in result.Diagnostics)
                {
                    string file = d.File;
                    if (logicalToAbs != null && logicalToAbs.TryGetValue(file, out var abs))
                        file = abs;
                    all.Add(new JsonDiag
                    {
                        File = file,
                        Line = Math.Max(1, d.Line),
                        Column = Math.Max(1, d.Column),
                        Severity = d.Severity == Severity.Error ? "error"
                                 : d.Severity == Severity.Warning ? "warning" : "info",
                        Code = d.Code,
                        Message = d.Message,
                    });
                    if (d.Severity == Severity.Error) hasErrors = true;
                }
            }

            if (json)
            {
                var arr = new JsonArray();
                foreach (var d in all) arr.Add(d.ToJson());
                Console.WriteLine(arr.ToJson());
            }
            else
            {
                foreach (var d in all)
                    Console.WriteLine($"{d.File}:{d.Line}:{d.Column}: {d.Severity} {d.Code}: {d.Message}");
                if (all.Count == 0)
                    Console.WriteLine(T("OK: ошибок нет.", "OK: no errors."));
                else if (!hasErrors)
                    Console.WriteLine(T("OK: только предупреждения.", "OK: warnings only."));
            }

            return hasErrors ? 1 : 0;
        }

        private static int Usage()
        {
            Console.Error.WriteLine("Usage: dslcheck <modules folder> [--api <salamander-api.json>] [--json] [--language en|ru]");
            return 2;
        }
    }
}
