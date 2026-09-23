using System;
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

    /// <summary>Модуль, исключённый карантином, и почему.</summary>
    public sealed class ExcludedModule
    {
        public string Name;
        public string Reason;
        public override string ToString() => $"{Name}: {Reason}";
    }

    /// <summary>
    /// Файл, исключённый карантином по файлу (<see cref="QuarantineScope.File"/>),
    /// и почему. Модуль-владелец при этом жив.
    /// </summary>
    public sealed class ExcludedFile
    {
        public string Module;
        /// <summary>Логическое имя "&lt;модуль&gt;/&lt;путь&gt;" — как в <see cref="Diagnostic.File"/>.</summary>
        public string File;
        public string Reason;
        public override string ToString() => $"{File}: {Reason}";
    }

    public sealed class CompilationResult
    {
        public CompiledProgram Program;          // null при ошибках
        public IReadOnlyList<Diagnostic> Diagnostics;

        /// <summary>
        /// Что выкинул карантин. Непусто только при quarantineBrokenModules: тогда
        /// Program может быть успешной, но собранной БЕЗ этих модулей.
        /// </summary>
        public IReadOnlyList<ExcludedModule> Excluded = System.Array.Empty<ExcludedModule>();

        /// <summary>
        /// Файлы, которые выкинул карантин по файлу (<see cref="HostRegistry.Quarantine"/>
        /// == <see cref="QuarantineScope.File"/>). Их модули живы. Ошибки этих файлов —
        /// в <see cref="Diagnostics"/>, даже когда Program собрана.
        /// </summary>
        public IReadOnlyList<ExcludedFile> ExcludedFiles = System.Array.Empty<ExcludedFile>();

        public bool Success => Program != null;
    }

    /// <summary>
    /// Драйвер компиляции: топосортировка модулей по зависимостям →
    /// лексер/парсер → чекер → байткод.
    ///
    /// Два режима. По умолчанию (инструменты: DslCheck, LSP) любая ошибка
    /// оставляет Program == null: модер обязан видеть свои ошибки, а не молча
    /// лишиться модуля. В режиме карантина (игра) сбойный модуль исключается
    /// ВМЕСТЕ С ЗАВИСИМЫМИ, остальное собирается — иначе один чужой мод с
    /// опечаткой или устаревшим apiVersion выключает все моды и базовые скрипты.
    ///
    /// Мерж-семантика при этом работает в нужную сторону сама: мод-патч является
    /// зависимым от базы, поэтому его исключение снимает только патч, а сущность
    /// откатывается к ранней версии.
    ///
    /// Гранулярность карантина задаёт хост (<see cref="HostRegistry.Quarantine"/>).
    /// При <see cref="QuarantineScope.File"/> ошибка в исходнике стоит ФАЙЛА: он
    /// исключается, модуль и его зависимые собираются дальше; файл, сломавшийся без
    /// выкинутого, выпадает следующим проходом — пока сборка не сойдётся. Модуль с
    /// зависимыми по-прежнему уходит за беды без файла (E0300–E0306) и за ошибки,
    /// которые ни к одному файлу не привязаны.
    /// </summary>
    public static class ScriptCompiler
    {
        public static CompilationResult Compile(HostRegistry host, int hostApiVersion,
                                                List<ModuleSourceSet> modules)
            => Compile(host, hostApiVersion, modules, quarantineBrokenModules: false);

        public static CompilationResult Compile(HostRegistry host, int hostApiVersion,
                                                List<ModuleSourceSet> modules,
                                                bool quarantineBrokenModules)
        {
            var excluded = new List<ExcludedModule>();

            // ----- индексация модулей по имени -----
            var byName = new Dictionary<string, ModuleSourceSet>(StringComparer.Ordinal);
            int namelessCount = 0;
            var duplicates = new List<string>();
            foreach (var m in modules ?? new List<ModuleSourceSet>())
            {
                if (m?.Manifest == null || string.IsNullOrEmpty(m.Manifest.Name)) { namelessCount++; continue; }
                if (byName.ContainsKey(m.Manifest.Name)) { duplicates.Add(m.Manifest.Name); continue; }
                byName[m.Manifest.Name] = m;
            }

            // Карантин: дубликат имени и манифест без имени — беда ОДНОГО модуля, а не
            // всей сборки. Раньше E0300/E0301 выдавались на каждом проходе без привязки
            // к файлу, «виновный» не вычислялся, и одна папка «mymod — копия» в каталоге
            // модов отключала ВСЕ скрипты, включая базовые. Теперь лишний экземпляр
            // (первый по порядку загрузки остаётся) и безымянный манифест уходят в
            // Excluded с причиной. Без карантина поведение прежнее — ошибка сборки.
            if (quarantineBrokenModules)
            {
                for (int i = 0; i < namelessCount; i++)
                    excluded.Add(new ExcludedModule { Name = "<без имени>", Reason = "в module.json нет поля name (E0300)" });
                foreach (var d in duplicates)
                    excluded.Add(new ExcludedModule { Name = d, Reason = "модуль с таким именем уже загружен — лишний экземпляр пропущен (E0301)" });
                namelessCount = 0;
                duplicates.Clear();
            }

            var alive = new HashSet<string>(byName.Keys, StringComparer.Ordinal);

            // ----- карантин по файлу: состояние между проходами -----
            // fileOwner — логическое имя файла → модуль; dropped — выкинутые файлы;
            // carried — их диагностики: каждый проход собирает DiagnosticBag заново,
            // и без переноса причина исключения пропала бы вместе с проходом.
            bool fileScope = quarantineBrokenModules && host != null && host.Quarantine == QuarantineScope.File;
            var fileOwner = new Dictionary<string, string>(StringComparer.Ordinal);
            var dropped = new HashSet<string>(StringComparer.Ordinal);
            var carried = new List<Diagnostic>();
            var excludedFiles = new List<ExcludedFile>();
            if (fileScope)
                foreach (var m in modules ?? new List<ModuleSourceSet>())
                {
                    if (m?.Manifest?.Name == null || !byName.TryGetValue(m.Manifest.Name, out var owner) || owner != m) continue;
                    foreach (var f in m.Files)
                        if (f.name != null && !fileOwner.ContainsKey(f.name))
                            fileOwner[f.name] = m.Manifest.Name;
                }

            CompilationResult Finish(CompiledProgram program, IReadOnlyList<Diagnostic> items)
            {
                IReadOnlyList<Diagnostic> all = items;
                if (carried.Count > 0)
                {
                    var merged = new List<Diagnostic>(carried.Count + items.Count);
                    merged.AddRange(carried);
                    merged.AddRange(items);
                    all = merged;
                }
                return new CompilationResult
                {
                    Program = program,
                    Diagnostics = all,
                    Excluded = excluded,
                    ExcludedFiles = excludedFiles,
                };
            }

            // каждая итерация либо возвращает результат, либо исключает ≥1 модуль
            // или файл, поэтому цикл конечен
            for (int guard = byName.Count + fileOwner.Count + 1; guard >= 0; guard--)
            {
                var files = new List<SourceText>();
                var diag = new DiagnosticBag(files);

                for (int i = 0; i < namelessCount; i++)
                    diag.Error("E0300", "Манифест модуля без имени.", SourcePos.None);
                foreach (var d in duplicates)
                    diag.Error("E0301", $"Модуль '{d}' загружен дважды.", SourcePos.None);

                // ----- проверка манифестов над ЖИВЫМ множеством -----
                var bad = new Dictionary<string, string>(StringComparer.Ordinal);
                void MarkBad(string name, string reason)
                {
                    if (!bad.ContainsKey(name)) bad[name] = reason;
                }

                foreach (var name in alive)
                {
                    var man = byName[name].Manifest;
                    if (man.ApiVersion != hostApiVersion)
                    {
                        MarkBad(name, $"apiVersion {man.ApiVersion}, у игры {hostApiVersion}");
                        diag.Error("E0302",
                            $"Модуль '{name}': apiVersion {man.ApiVersion}, у игры {hostApiVersion}. " +
                            "Обновите модуль под текущее API.", SourcePos.None);
                    }
                }

                foreach (var name in alive)
                {
                    var man = byName[name].Manifest;
                    foreach (var dep in man.Dependencies ?? System.Array.Empty<string>())
                        if (!alive.Contains(dep))
                        {
                            MarkBad(name, $"зависимость '{dep}' не загружена");
                            diag.Error("E0303", $"Модуль '{name}' зависит от '{dep}', который не загружен.", SourcePos.None);
                        }
                }

                // синхронный модуль может звать функции зависимостей — если зависимость
                // кооперативная и её func делает wait, синхронный обработчик приостановится.
                // Закрываем дыру статически: sync может зависеть только от sync.
                foreach (var name in alive)
                {
                    var man = byName[name].Manifest;
                    if (!man.IsSynchronous) continue;
                    foreach (var dep in man.Dependencies ?? System.Array.Empty<string>())
                        if (alive.Contains(dep) && !byName[dep].Manifest.IsSynchronous)
                        {
                            MarkBad(name, $"синхронный модуль зависит от кооперативного '{dep}'");
                            diag.Error("E0306",
                                $"Синхронный модуль '{name}' не может зависеть от кооперативного '{dep}': " +
                                "его функции могут содержать wait. Сделайте зависимость тоже синхронной.",
                                SourcePos.None);
                        }
                }

                // ----- топологическая сортировка (циклы = ошибка) -----
                List<ModuleSourceSet> order = null;
                if (bad.Count == 0)
                {
                    order = TopoSort(byName, alive, diag, out var cyclic);
                    foreach (var name in cyclic) MarkBad(name, "циклическая зависимость модулей");
                }

                if (bad.Count > 0)
                {
                    if (!quarantineBrokenModules)
                        return Finish(null, diag.Items);
                    if (!ExcludeAndContinue(byName, alive, bad, excluded))
                        return Finish(null, diag.Items);
                    continue;
                }

                // ----- лексер + парсер -----
                var moduleAsts = new List<ModuleAst>();
                foreach (var m in order)
                {
                    var visible = new HashSet<string> { m.Manifest.Name };
                    foreach (var dep in m.Manifest.Dependencies ?? System.Array.Empty<string>()) visible.Add(dep);

                    var ast = new ModuleAst { Name = m.Manifest.Name, Visible = visible, Synchronous = m.Manifest.IsSynchronous };
                    // Модуль без единого исходника компилируется «успешно» и молча ничего не
                    // делает. Самая частая причина — опечатка в ключе манифеста: неизвестные
                    // ключи JSON игнорируются, список файлов остаётся пустым, а чекер
                    // отвечает «OK». Это не ошибка (пустой модуль законен), но и не тишина.
                    if (m.Files.Count == 0)
                        diag.Warning("W0300",
                            $"Модуль '{m.Manifest.Name}' не содержит ни одного исходника: список \"sources\" в module.json " +
                            "пуст или отсутствует (проверьте имя ключа и пути) — модуль ничего не делает.",
                            SourcePos.None);
                    foreach (var (name, text) in m.Files)
                    {
                        // выкинутый карантином файл: модуль жив, а его нет
                        if (name != null && dropped.Contains(name)) continue;

                        var src = new SourceText(files.Count, name, text);
                        files.Add(src);

                        var tokens = new Lexer(src.Text, src.FileId, diag).Tokenize();
                        var file = new Parser(tokens, src.FileId, diag).ParseFile();
                        ast.Files.Add(file);
                    }
                    moduleAsts.Add(ast);
                }

                // ----- семантика -----
                if (!diag.HasErrors)
                {
                    var checker = new Checker(host, diag);
                    var sem = checker.Check(moduleAsts);

                    // ----- байткод -----
                    if (!diag.HasErrors)
                    {
                        var program = new BytecodeCompiler(host, sem, files).Compile(moduleAsts);
                        return Finish(program, diag.Items);
                    }
                }

                if (!quarantineBrokenModules)
                    return Finish(null, diag.Items);

                // карантин по файлу: выкидываем файлы с ошибками, модули живут.
                // Синтаксическая ошибка останавливает проход до чекера, поэтому
                // сначала выпадают файлы с ошибками парсера, следующим проходом —
                // семантические, в том числе у тех, кто ссылался на выкинутое.
                if (fileScope && DropBrokenFiles(diag.Items, fileOwner, alive, dropped, carried, excludedFiles))
                    continue;

                // ошибки в исходниках: виновника определяем по имени файла
                // (логическое имя — "<модуль>/<путь>")
                var guilty = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var d in diag.Items)
                {
                    if (d.Severity != Severity.Error) continue;
                    string mod = ModuleOfFile(d.File);
                    if (mod == null || !alive.Contains(mod)) continue;
                    if (!guilty.ContainsKey(mod))
                        guilty[mod] = $"ошибка компиляции {d.Code} ({d.File}:{d.Line})";
                }

                if (guilty.Count == 0 || !ExcludeAndContinue(byName, alive, guilty, excluded))
                    return Finish(null, diag.Items);
            }

            return Finish(null, System.Array.Empty<Diagnostic>());
        }

        /// <summary>
        /// Карантин по файлу: исключить файлы с ошибками (владелец жив, файл ещё не
        /// выкинут) и перенести их диагностики в <paramref name="carried"/>.
        /// false — ни одна ошибка к такому файлу не привязана (только &lt;unknown&gt;,
        /// &lt;compiler&gt; и т.п.): решать дальше по модулю.
        /// </summary>
        private static bool DropBrokenFiles(IReadOnlyList<Diagnostic> items,
                                            Dictionary<string, string> fileOwner,
                                            HashSet<string> alive,
                                            HashSet<string> dropped,
                                            List<Diagnostic> carried,
                                            List<ExcludedFile> excludedFiles)
        {
            var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var d in items)
            {
                if (d.Severity != Severity.Error || d.File == null) continue;
                if (dropped.Contains(d.File) || reasons.ContainsKey(d.File)) continue;
                if (!fileOwner.TryGetValue(d.File, out var owner) || !alive.Contains(owner)) continue;
                reasons[d.File] = $"ошибка компиляции {d.Code} ({d.File}:{d.Line})";
                order.Add(d.File);
            }
            if (order.Count == 0) return false;

            foreach (var d in items)
                if (d.File != null && reasons.ContainsKey(d.File))
                    carried.Add(d);
            foreach (var f in order)
            {
                dropped.Add(f);
                excludedFiles.Add(new ExcludedFile { Module = fileOwner[f], File = f, Reason = reasons[f] });
            }
            return true;
        }

        private static string ModuleOfFile(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            int slash = file.IndexOf('/');
            return slash > 0 ? file.Substring(0, slash) : null;
        }

        /// <summary>
        /// Исключить сбойные модули и всех, кто от них зависит (транзитивно).
        /// false — исключать оказалось нечего, повторять попытку бессмысленно.
        /// </summary>
        private static bool ExcludeAndContinue(Dictionary<string, ModuleSourceSet> byName,
                                               HashSet<string> alive,
                                               Dictionary<string, string> bad,
                                               List<ExcludedModule> excluded)
        {
            var toDrop = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in bad)
                if (alive.Contains(kv.Key)) toDrop[kv.Key] = kv.Value;
            if (toDrop.Count == 0) return false;

            // транзитивное замыкание по обратным рёбрам: зависимый без своей
            // зависимости всё равно не соберётся
            bool grown = true;
            while (grown)
            {
                grown = false;
                foreach (var name in alive)
                {
                    if (toDrop.ContainsKey(name)) continue;
                    var deps = byName[name].Manifest.Dependencies ?? System.Array.Empty<string>();
                    foreach (var dep in deps)
                        if (toDrop.ContainsKey(dep))
                        {
                            toDrop[name] = $"зависит от исключённого модуля '{dep}'";
                            grown = true;
                            break;
                        }
                }
            }

            foreach (var kv in toDrop)
            {
                alive.Remove(kv.Key);
                excluded.Add(new ExcludedModule { Name = kv.Key, Reason = kv.Value });
            }
            return true;
        }

        /// <summary>
        /// Порядок загрузки модулей — тот же, что у компиляции: зависимости раньше
        /// зависимых, независимые — по имени. Нужен инструментам (порядок версий
        /// члена в подсказках редактора). Ничего не диагностирует: модули с
        /// циклами, дублями имён и без имени просто уходят в конец.
        /// </summary>
        public static List<ModuleSourceSet> LoadOrder(List<ModuleSourceSet> modules)
        {
            var byName = new Dictionary<string, ModuleSourceSet>(StringComparer.Ordinal);
            var rest = new List<ModuleSourceSet>();
            foreach (var m in modules ?? new List<ModuleSourceSet>())
            {
                if (m?.Manifest == null || string.IsNullOrEmpty(m.Manifest.Name) || byName.ContainsKey(m.Manifest.Name))
                {
                    if (m != null) rest.Add(m);
                    continue;
                }
                byName[m.Manifest.Name] = m;
            }
            var alive = new HashSet<string>(byName.Keys, StringComparer.Ordinal);
            var order = TopoSort(byName, alive, new DiagnosticBag(new List<SourceText>()), out _);
            var placed = new HashSet<ModuleSourceSet>(order);
            foreach (var m in byName.Values) if (!placed.Contains(m)) order.Add(m);
            order.AddRange(rest);
            return order;
        }

        private static List<ModuleSourceSet> TopoSort(Dictionary<string, ModuleSourceSet> byName,
                                                      HashSet<string> alive,
                                                      DiagnosticBag diag,
                                                      out List<string> cyclic)
        {
            var result = new List<ModuleSourceSet>();
            var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 нет, 1 в обходе, 2 готов
            var cycles = new List<string>();

            // стабильный порядок обхода — по имени
            var names = new List<string>(alive);
            names.Sort(StringComparer.Ordinal);

            bool Visit(string name)
            {
                if (state.TryGetValue(name, out int s))
                {
                    if (s == 1)
                    {
                        diag.Error("E0304", $"Циклическая зависимость модулей через '{name}'.", SourcePos.None);
                        if (!cycles.Contains(name)) cycles.Add(name);
                        return false;
                    }
                    return true;
                }
                state[name] = 1;
                var m = byName[name];
                foreach (var dep in m.Manifest.Dependencies ?? System.Array.Empty<string>())
                    if (alive.Contains(dep) && !Visit(dep))
                    {
                        if (!cycles.Contains(name)) cycles.Add(name);
                        return false;
                    }
                state[name] = 2;
                result.Add(m);
                return true;
            }

            foreach (var n in names)
                if (!Visit(n))
                    break;

            cyclic = cycles;
            return result;
        }
    }
}
