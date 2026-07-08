using System;
using System.Collections.Generic;
using System.IO;
using Dsl.Compilation;
using Dsl.Text;
using Newtonsoft.Json;

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
            [JsonProperty("file")] public string File;
            [JsonProperty("line")] public int Line;
            [JsonProperty("column")] public int Column;
            [JsonProperty("severity")] public string Severity;
            [JsonProperty("code")] public string Code;
            [JsonProperty("message")] public string Message;
        }

        public static int Main(string[] args)
        {
            // UTF-8 without BOM: JSON output and non-ASCII diagnostics must survive
            // pipes and consoles (especially on Windows) intact.
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);

            string root = null;
            string apiPath = null;
            bool json = false;

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

            // ----- API registry from the manifest -----
            Semantics.HostRegistry registry;
            int apiVersion;
            if (File.Exists(apiPath))
            {
                try
                {
                    registry = ApiManifest.Import(File.ReadAllText(apiPath), out apiVersion);
                }
                catch (Exception ex)
                {
                    Emit(extra, apiPath, "E0400", "salamander-api.json could not be read: " + ex.Message);
                    return Output(json, extra, null, null);
                }
            }
            else
            {
                registry = new Semantics.HostRegistry();
                apiVersion = 1;
                Emit(extra, apiPath, "W0401",
                    "salamander-api.json not found — host events and API are unknown " +
                    "(run the game once in the editor so it gets exported).",
                    warning: true);
            }

            // ----- modules -----
            var logicalToAbs = new Dictionary<string, string>();
            var modules = ModuleLoader.LoadFromFolder(root,
                (file, message) => Emit(extra, file, "E0401", message),
                logicalToAbs);

            if (modules.Count == 0)
                Emit(extra, root, "W0402", "no modules found in the folder (a subfolder with module.json).", warning: true);

            // ----- compile -----
            var result = ScriptCompiler.Compile(registry, apiVersion, modules);
            return Output(json, extra, result, logicalToAbs);
        }

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
                Console.WriteLine(JsonConvert.SerializeObject(all));
            }
            else
            {
                foreach (var d in all)
                    Console.WriteLine($"{d.File}:{d.Line}:{d.Column}: {d.Severity} {d.Code}: {d.Message}");
                if (all.Count == 0)
                    Console.WriteLine("OK: no errors.");
                else if (!hasErrors)
                    Console.WriteLine("OK: warnings only.");
            }

            return hasErrors ? 1 : 0;
        }

        private static int Usage()
        {
            Console.Error.WriteLine("Usage: dslcheck <modules folder> [--api <salamander-api.json>] [--json]");
            return 2;
        }
    }
}
