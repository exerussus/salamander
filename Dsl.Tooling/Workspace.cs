using System;
using System.Collections.Generic;
using System.IO;
using Dsl.Compilation;
using Newtonsoft.Json.Linq;

namespace Dsl.Tooling
{
    /// <summary>
    /// Модули воркспейса, загруженные с диска: наборы исходников в порядке
    /// загрузки, карта «логическое имя → файл», папки модулей и ошибки загрузки.
    /// Снимок неизменяем по договорённости: компиляция с правками строит копии
    /// наборов (<see cref="WorkspaceCompiler.WithOverlays"/>), сам снимок не трогает.
    /// </summary>
    public sealed class WorkspaceModules
    {
        public readonly List<ModuleSourceSet> Modules = new List<ModuleSourceSet>();

        /// <summary>Логическое имя исходника ("mod/src/x.sal") → абсолютный путь.</summary>
        public readonly Dictionary<string, string> LogicalToPath = new Dictionary<string, string>();

        /// <summary>Имя модуля → абсолютная папка модуля (с module.json).</summary>
        public readonly Dictionary<string, string> ModuleDirs = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Ошибки загрузки: (файл, сообщение) в порядке появления.</summary>
        public readonly List<KeyValuePair<string, string>> LoadErrors = new List<KeyValuePair<string, string>>();

        /// <summary>Использованный salamander-build.json или null (обход папки).</summary>
        public string BuildFile;

        /// <summary>Логическое имя по абсолютному пути; null — файл не входит ни в один модуль.</summary>
        public string LogicalOf(string absPath)
        {
            if (string.IsNullOrEmpty(absPath)) return null;
            string full;
            try { full = Path.GetFullPath(absPath); }
            catch { return null; }
            foreach (var kv in LogicalToPath)
            {
                string p;
                try { p = Path.GetFullPath(kv.Value); }
                catch { continue; }
                if (string.Equals(p, full, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            }
            return null;
        }
    }

    public static class WorkspaceLoader
    {
        public const string ApiManifestFileName = "salamander-api.json";
        public const string BuildFileName = "salamander-build.json";

        /// <summary>
        /// Где лежит salamander-api.json: в корне, иначе ближайший к корню в
        /// подпапках (манифест обычно выгружается в StreamingAssets/&lt;modsFolder&gt;).
        /// null — не найден.
        /// </summary>
        public static string FindApiManifest(string root)
        {
            if (string.IsNullOrEmpty(root)) return null;
            string atRoot = Path.Combine(root, ApiManifestFileName);
            if (File.Exists(atRoot)) return atRoot;
            return ModuleLoader.FindNearestFile(root, ApiManifestFileName);
        }

        /// <summary>
        /// Модули воркспейса. «Ешь то, что дал сборщик»: если есть
        /// salamander-build.json (упорядоченный список папок модулей) — берём
        /// РОВНО его; иначе обход папки вглубь (дев-режим без сборщика).
        /// </summary>
        /// <param name="modulesRoot">Корень поиска модулей.</param>
        /// <param name="buildFile">Явный build-файл; null — &lt;root&gt;/salamander-build.json.</param>
        public static WorkspaceModules Load(string modulesRoot, string buildFile = null)
        {
            var ws = new WorkspaceModules();
            Action<string, string> onError = (file, message) =>
                ws.LoadErrors.Add(new KeyValuePair<string, string>(file, message));

            string buildPath = buildFile ?? (modulesRoot == null ? null : Path.Combine(modulesRoot, BuildFileName));
            if (buildPath != null && File.Exists(buildPath))
            {
                ws.BuildFile = buildPath;
                try
                {
                    var build = JObject.Parse(File.ReadAllText(buildPath));
                    var dirs = new List<string>();
                    // пути в build-файле — относительно ЕГО папки: так его можно
                    // положить и в корень проекта, и рядом с модулями
                    string baseDir = Path.GetDirectoryName(Path.GetFullPath(buildPath)) ?? modulesRoot;
                    foreach (var t in build["modules"] ?? new JArray())
                        dirs.Add(Path.GetFullPath(Path.Combine(baseDir, (string)t)));
                    foreach (var dir in dirs) AddModule(ws, dir, onError);
                }
                catch (Exception ex)
                {
                    onError(buildPath, "salamander-build.json не читается — " + ex.Message);
                    ws.Modules.Clear();
                }
                return ws;
            }

            // обход ВГЛУБЬ: корень задаёт IDE, и в Unity-проекте модули лежат
            // в StreamingAssets/..., а не прямыми детьми корня
            foreach (var dir in FindModuleDirs(modulesRoot))
                AddModule(ws, dir, onError);
            return ws;
        }

        /// <summary>Папки модулей под корнем — тем же обходом, что ModuleLoader.LoadFromTree.</summary>
        public static List<string> FindModuleDirs(string rootPath, int maxDepth = ModuleLoader.DefaultScanDepth)
        {
            var moduleDirs = new List<string>();
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath)) return moduleDirs;

            var level = new List<string> { Path.GetFullPath(rootPath) };
            for (int depth = 0; depth <= maxDepth && level.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var dir in level)
                {
                    bool isModule;
                    try { isModule = File.Exists(Path.Combine(dir, "module.json")); }
                    catch { continue; }

                    if (isModule) { moduleDirs.Add(dir); continue; } // внутрь модуля не идём

                    if (depth == maxDepth) continue;
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(dir))
                            if (!ModuleLoader.IsIgnoredDirectory(Path.GetFileName(sub))) next.Add(sub);
                    }
                    catch { }
                }
                level = next;
            }
            moduleDirs.Sort(StringComparer.Ordinal);
            return moduleDirs;
        }

        /// <summary>Ближайшая вверх папка с module.json (не выше maxUp уровней); null — файл вне модуля.</summary>
        public static string FindModuleDirOf(string filePath, int maxUp = 6)
        {
            try
            {
                var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "");
                for (int depth = 0; dir != null && depth <= maxUp; depth++, dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "module.json"))) return dir.FullName;
            }
            catch { /* недопустимый путь — вне модуля */ }
            return null;
        }

        private static void AddModule(WorkspaceModules ws, string dir, Action<string, string> onError)
        {
            var set = ModuleLoader.LoadModuleDir(dir, onError, ws.LogicalToPath);
            if (set == null) return;
            ws.Modules.Add(set);
            string name = set.Manifest?.Name ?? Path.GetFileName(dir);
            if (!ws.ModuleDirs.ContainsKey(name)) ws.ModuleDirs[name] = Path.GetFullPath(dir);
        }
    }

    public static class WorkspaceCompiler
    {
        /// <summary>
        /// Наборы модулей с наложенными несохранёнными правками: overlay(путь) →
        /// живой текст или null (брать с диска). Исходный снимок не мутируется —
        /// его можно переиспользовать между компиляциями и отдавать в другой поток.
        /// </summary>
        public static List<ModuleSourceSet> WithOverlays(WorkspaceModules ws, Func<string, string> overlay)
        {
            var result = new List<ModuleSourceSet>(ws.Modules.Count);
            foreach (var set in ws.Modules)
                result.Add(Copy(set, logical =>
                    overlay != null && ws.LogicalToPath.TryGetValue(logical, out var abs) ? overlay(abs) : null));
            return result;
        }

        /// <summary>Копия набора; replace(логическое имя) → новый текст или null.</summary>
        public static ModuleSourceSet Copy(ModuleSourceSet set, Func<string, string> replace = null)
        {
            var copy = new ModuleSourceSet { Manifest = set.Manifest };
            foreach (var (name, text) in set.Files)
            {
                string live = replace?.Invoke(name);
                copy.Files.Add((name, live ?? text));
            }
            return copy;
        }
    }
}
