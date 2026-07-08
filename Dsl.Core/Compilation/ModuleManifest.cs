using System.Collections.Generic;
using Dsl.Codegen;
using Dsl.Semantics;
using Dsl.Syntax;
using Dsl.Text;
using Newtonsoft.Json;

namespace Dsl.Compilation
{
    /// <summary>
    /// Манифест модуля (module.json):
    /// {
    ///   "name": "mymod",
    ///   "version": "1.0.0",
    ///   "apiVersion": 1,
    ///   "dependencies": ["core_scripts"],
    ///   "sources": ["src/enums.script", "src/triggers.script"]  // порядок важен
    /// }
    /// Глобы не поддерживаются осознанно: порядок файлов определяет порядок
    /// обработчиков, поэтому он должен быть явным и детерминированным.
    /// </summary>
    public sealed class ModuleManifest
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("version")] public string Version = "0.0.0";
        [JsonProperty("apiVersion")] public int ApiVersion;
        [JsonProperty("dependencies")] public string[] Dependencies = System.Array.Empty<string>();
        [JsonProperty("sources")] public string[] Sources = System.Array.Empty<string>();

        /// <summary>
        /// Режим исполнения модуля:
        ///  "cooperative" (по умолчанию) — файберы, wait/spawn разрешены (стратегия);
        ///  "synchronous" — без файберов: обработчик исполняется целиком до конца,
        ///  wait/wait until/spawn запрещены компилятором (карточная игра).
        /// Синхронный модуль не может зависеть от кооперативного (иначе гарантия
        /// «всё до конца» протекла бы через чужую wait-функцию).
        /// </summary>
        [JsonProperty("execution")] public string Execution = "cooperative";

        public bool IsSynchronous =>
            string.Equals(Execution, "synchronous", System.StringComparison.OrdinalIgnoreCase);

        public static ModuleManifest Parse(string json) =>
            JsonConvert.DeserializeObject<ModuleManifest>(json);
    }

    /// <summary>Модуль, готовый к компиляции: манифест + загруженные исходники.</summary>
    public sealed class ModuleSourceSet
    {
        public ModuleManifest Manifest;
        public List<(string name, string text)> Files = new List<(string, string)>();
    }

    public sealed class CompilationResult
    {
        public CompiledProgram Program;          // null при ошибках
        public IReadOnlyList<Diagnostic> Diagnostics;
        public bool Success => Program != null;
    }

    /// <summary>
    /// Драйвер компиляции: топосортировка модулей по зависимостям →
    /// лексер/парсер → чекер → байткод. Любая ошибка на любом этапе
    /// оставляет Program == null; хост при hot-reload просто не меняет
    /// работающую программу.
    /// </summary>
    public static class ScriptCompiler
    {
        public static CompilationResult Compile(HostRegistry host, int hostApiVersion,
                                                List<ModuleSourceSet> modules)
        {
            var files = new List<SourceText>();
            var diag = new DiagnosticBag(files);

            // ----- проверка манифестов -----
            var byName = new Dictionary<string, ModuleSourceSet>();
            foreach (var m in modules)
            {
                if (m.Manifest == null || string.IsNullOrEmpty(m.Manifest.Name))
                {
                    diag.Error("E0300", "Манифест модуля без имени.", SourcePos.None);
                    continue;
                }
                if (byName.ContainsKey(m.Manifest.Name))
                {
                    diag.Error("E0301", $"Модуль '{m.Manifest.Name}' загружен дважды.", SourcePos.None);
                    continue;
                }
                if (m.Manifest.ApiVersion != hostApiVersion)
                {
                    diag.Error("E0302",
                        $"Модуль '{m.Manifest.Name}': apiVersion {m.Manifest.ApiVersion}, у игры {hostApiVersion}. " +
                        "Обновите модуль под текущее API.",
                        SourcePos.None);
                    continue;
                }
                byName[m.Manifest.Name] = m;
            }

            foreach (var m in byName.Values)
                foreach (var dep in m.Manifest.Dependencies)
                    if (!byName.ContainsKey(dep))
                        diag.Error("E0303", $"Модуль '{m.Manifest.Name}' зависит от '{dep}', который не загружен.", SourcePos.None);

            // синхронный модуль может звать функции зависимостей — если зависимость
            // кооперативная и её func делает wait, синхронный обработчик приостановится.
            // Закрываем дыру статически: sync может зависеть только от sync.
            foreach (var m in byName.Values)
            {
                if (!m.Manifest.IsSynchronous) continue;
                foreach (var dep in m.Manifest.Dependencies)
                    if (byName.TryGetValue(dep, out var d) && !d.Manifest.IsSynchronous)
                        diag.Error("E0306",
                            $"Синхронный модуль '{m.Manifest.Name}' не может зависеть от кооперативного '{dep}': " +
                            "его функции могут содержать wait. Сделайте зависимость тоже синхронной.",
                            SourcePos.None);
            }

            if (diag.HasErrors)
                return new CompilationResult { Diagnostics = diag.Items };

            // ----- топологическая сортировка (циклы = ошибка) -----
            var order = TopoSort(byName, diag);
            if (diag.HasErrors)
                return new CompilationResult { Diagnostics = diag.Items };

            // ----- лексер + парсер -----
            var moduleAsts = new List<ModuleAst>();
            foreach (var m in order)
            {
                var visible = new HashSet<string> { m.Manifest.Name };
                foreach (var dep in m.Manifest.Dependencies) visible.Add(dep);

                var ast = new ModuleAst { Name = m.Manifest.Name, Visible = visible, Synchronous = m.Manifest.IsSynchronous };
                foreach (var (name, text) in m.Files)
                {
                    var src = new SourceText(files.Count, name, text);
                    files.Add(src);

                    var tokens = new Lexer(src.Text, src.FileId, diag).Tokenize();
                    var file = new Parser(tokens, src.FileId, diag).ParseFile();
                    ast.Files.Add(file);
                }
                moduleAsts.Add(ast);
            }

            if (diag.HasErrors)
                return new CompilationResult { Diagnostics = diag.Items };

            // ----- семантика -----
            var checker = new Checker(host, diag);
            var sem = checker.Check(moduleAsts);

            if (diag.HasErrors)
                return new CompilationResult { Diagnostics = diag.Items };

            // ----- байткод -----
            var program = new BytecodeCompiler(host, sem, files).Compile(moduleAsts);
            return new CompilationResult { Program = program, Diagnostics = diag.Items };
        }

        private static List<ModuleSourceSet> TopoSort(Dictionary<string, ModuleSourceSet> byName, DiagnosticBag diag)
        {
            var result = new List<ModuleSourceSet>();
            var state = new Dictionary<string, int>(); // 0 нет, 1 в обходе, 2 готов

            // стабильный порядок обхода — по имени
            var names = new List<string>(byName.Keys);
            names.Sort(System.StringComparer.Ordinal);

            bool Visit(string name)
            {
                if (state.TryGetValue(name, out int s))
                {
                    if (s == 1)
                    {
                        diag.Error("E0304", $"Циклическая зависимость модулей через '{name}'.", SourcePos.None);
                        return false;
                    }
                    return true;
                }
                state[name] = 1;
                var m = byName[name];
                foreach (var dep in m.Manifest.Dependencies)
                    if (byName.ContainsKey(dep) && !Visit(dep))
                        return false;
                state[name] = 2;
                result.Add(m);
                return true;
            }

            foreach (var n in names)
                if (!Visit(n))
                    break;

            return result;
        }
    }
}
