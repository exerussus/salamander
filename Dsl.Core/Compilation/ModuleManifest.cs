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
        ///  wait/wait until/spawn запрещены компилятором (карточная игра), бюджет
        ///  тика его не откладывает и не режет.
        /// Синхронный модуль не может зависеть от кооперативного (E0306), а
        /// кооперативный не может ждать внутри сущности синхронного (E0252) —
        /// иначе гарантия «всё до конца» протекла бы через чужую wait-функцию.
        /// </summary>
        [JsonProperty("execution")] public string Execution = "cooperative";

        public bool IsSynchronous =>
            string.Equals(Execution, "synchronous", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Состояние скриптов модуля (необязательно, по умолчанию — как раньше):
        ///  "full" / не указано — поля, коллекции в полях, Engine.Enable/Disable/Attach разрешены;
        ///  "none" — модуль без состояния: всё состояние живёт у хоста (ECS), поэтому
        ///  откат хода на стороне хоста полный. Компилятор отклоняет изменяемые поля
        ///  (E0253), поля-коллекции даже readonly (E0253), операции Engine, меняющие
        ///  состояние движка (E0254), члены от модулей с состоянием в его сущностях
        ///  (E0255). Требует execution: synchronous (E0308) и зависимостей тоже "none"
        ///  (E0309): иначе состояние протекло бы через приостановленный файбер или чужой код.
        /// </summary>
        [JsonProperty("state")] public string State;

        public bool IsStateless =>
            string.Equals(State, "none", System.StringComparison.OrdinalIgnoreCase);

        internal bool HasValidState =>
            State == null || IsStateless || string.Equals(State, "full", System.StringComparison.OrdinalIgnoreCase);

        public static ModuleManifest Parse(string json) =>
            JsonConvert.DeserializeObject<ModuleManifest>(json);
    }

    /// <summary>Модуль, готовый к компиляции: манифест + загруженные исходники.</summary>
    public sealed class ModuleSourceSet
    {
        public ModuleManifest Manifest;
        public List<(string name, string text)> Files = new List<(string, string)>();

        /// <summary>
        /// Пустой набор — НАМЕРЕННО: сборщик сам разобрал пак и знает, что
        /// скриптов в нём нет (пак с одними данными — локации, фразы). Тогда
        /// W0300 «ни одного исходника» не выдаётся: он ловит опечатку в ключе
        /// манифеста, а здесь манифест читал не компилятор, и опечатки быть не могло.
        /// </summary>
        public bool EmptyIsIntended;
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

        /// <summary>
        /// Что объявлено в скриптах (для подсказок IDE/LSP): строится, если
        /// дошло до чекера — то есть и при семантических ошибках, но не при
        /// синтаксических (тогда null: держите таблицу прошлой компиляции).
        /// </summary>
        public ScriptSymbolTable Symbols;
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

        /// <summary>Язык сообщений — <see cref="HostRegistry.Language"/> хоста (по умолчанию ru).</summary>
        public static CompilationResult Compile(HostRegistry host, int hostApiVersion,
                                                List<ModuleSourceSet> modules,
                                                bool quarantineBrokenModules)
            => Compile(host, hostApiVersion, modules, quarantineBrokenModules, host?.Language ?? Language.Ru);

        /// <summary>
        /// Компиляция с явным языком сообщений (<paramref name="language"/>): тексты
        /// диагностик, причины карантина. Коды (E0101…) от языка не зависят.
        /// </summary>
        public static CompilationResult Compile(HostRegistry host, int hostApiVersion,
                                                List<ModuleSourceSet> modules,
                                                bool quarantineBrokenModules,
                                                Language language)
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
                    excluded.Add(new ExcludedModule { Name = Loc.T(language, "<без имени>", "<unnamed>"), Reason = Loc.T(language, "в module.json нет поля name (E0300)", "module.json has no name field (E0300)") });
                foreach (var d in duplicates)
                    excluded.Add(new ExcludedModule { Name = d, Reason = Loc.T(language, "модуль с таким именем уже загружен — лишний экземпляр пропущен (E0301)",
                        "a module with this name is already loaded — the extra copy was skipped (E0301)") });
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

            ScriptSymbolTable symbols = null; // последнего прохода, дошедшего до чекера

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
                    Symbols = symbols,
                };
            }

            // каждая итерация либо возвращает результат, либо исключает ≥1 модуль
            // или файл, поэтому цикл конечен
            for (int guard = byName.Count + fileOwner.Count + 1; guard >= 0; guard--)
            {
                var files = new List<SourceText>();
                var diag = new DiagnosticBag(files, language);

                for (int i = 0; i < namelessCount; i++)
                    diag.Error("E0300", Loc.T(language, "Манифест модуля без имени.", "Module manifest without a name."), SourcePos.None);
                foreach (var d in duplicates)
                    diag.Error("E0301", Loc.T(language, $"Модуль '{d}' загружен дважды.", $"Module '{d}' is loaded twice."), SourcePos.None);

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
                        MarkBad(name, Loc.T(language, $"apiVersion {man.ApiVersion}, у игры {hostApiVersion}",
                            $"apiVersion {man.ApiVersion}, the game has {hostApiVersion}"));
                        diag.Error("E0302",
                            Loc.T(language, $"Модуль '{name}': apiVersion {man.ApiVersion}, у игры {hostApiVersion}. " +
                            "Обновите модуль под текущее API.",
                                $"Module '{name}': apiVersion {man.ApiVersion}, the game has {hostApiVersion}. " +
                                "Update the module to the current API."), SourcePos.None);
                    }
                }

                foreach (var name in alive)
                {
                    var man = byName[name].Manifest;
                    foreach (var dep in man.Dependencies ?? System.Array.Empty<string>())
                        if (!alive.Contains(dep))
                        {
                            MarkBad(name, Loc.T(language, $"зависимость '{dep}' не загружена", $"dependency '{dep}' is not loaded"));
                            diag.Error("E0303", Loc.T(language, $"Модуль '{name}' зависит от '{dep}', который не загружен.",
                                $"Module '{name}' depends on '{dep}', which is not loaded."), SourcePos.None);
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
                            MarkBad(name, Loc.T(language, $"синхронный модуль зависит от кооперативного '{dep}'",
                                $"synchronous module depends on the cooperative '{dep}'"));
                            diag.Error("E0306",
                                Loc.T(language, $"Синхронный модуль '{name}' не может зависеть от кооперативного '{dep}': " +
                                "его функции могут содержать wait. Сделайте зависимость тоже синхронной.",
                                    $"The synchronous module '{name}' cannot depend on the cooperative '{dep}': " +
                                    "its functions may contain wait. Make the dependency synchronous too."),
                                SourcePos.None);
                        }
                }

                // модуль без состояния ("state": "none"): неизвестное значение — ошибка, а не
                // молчаливое «как раньше» (опечатка "stateless" иначе выключила бы все проверки);
                // ждать нельзя (файбер в wait держит локали между ходами), зависеть — только
                // от таких же (иначе состояние протекло бы через чужие функции)
                foreach (var name in alive)
                {
                    var man = byName[name].Manifest;
                    if (!man.HasValidState)
                    {
                        MarkBad(name, Loc.T(language, $"неизвестное значение state '{man.State}'", $"unknown state value '{man.State}'"));
                        diag.Error("E0307",
                            Loc.T(language, $"Модуль '{name}': неизвестное значение \"state\": \"{man.State}\". Допустимо \"full\" или \"none\".",
                                $"Module '{name}': unknown value \"state\": \"{man.State}\". Allowed: \"full\" or \"none\"."),
                            SourcePos.None);
                        continue;
                    }
                    if (!man.IsStateless) continue;
                    if (!man.IsSynchronous)
                    {
                        MarkBad(name, Loc.T(language, "модуль без состояния не синхронный", "stateless module is not synchronous"));
                        diag.Error("E0308",
                            Loc.T(language, $"Модуль '{name}': \"state\": \"none\" требует \"execution\": \"synchronous\" — " +
                            "файбер в wait хранит локали между ходами, а это состояние вне хоста.",
                                $"Module '{name}': \"state\": \"none\" requires \"execution\": \"synchronous\" — " +
                                "a fiber in wait keeps its locals between turns, and that is state outside the host."),
                            SourcePos.None);
                    }
                    foreach (var dep in man.Dependencies ?? System.Array.Empty<string>())
                        if (alive.Contains(dep) && !byName[dep].Manifest.IsStateless)
                        {
                            MarkBad(name, Loc.T(language, $"модуль без состояния зависит от модуля с состоянием '{dep}'",
                                $"stateless module depends on the stateful module '{dep}'"));
                            diag.Error("E0309",
                                Loc.T(language, $"Модуль '{name}' (\"state\": \"none\") не может зависеть от '{dep}' с состоянием: " +
                                "его функции могут менять поля. Объявите у зависимости тоже \"state\": \"none\".",
                                    $"Module '{name}' (\"state\": \"none\") cannot depend on the stateful '{dep}': " +
                                    "its functions may change fields. Declare \"state\": \"none\" in the dependency too."),
                                SourcePos.None);
                        }
                }

                // ----- топологическая сортировка (циклы = ошибка) -----
                List<ModuleSourceSet> order = null;
                if (bad.Count == 0)
                {
                    order = TopoSort(byName, alive, diag, out var cyclic);
                    foreach (var name in cyclic) MarkBad(name, Loc.T(language, "циклическая зависимость модулей", "cyclic module dependency"));
                }

                if (bad.Count > 0)
                {
                    if (!quarantineBrokenModules)
                        return Finish(null, diag.Items);
                    if (!ExcludeAndContinue(byName, alive, bad, excluded, language))
                        return Finish(null, diag.Items);
                    continue;
                }

                // ----- лексер + парсер -----
                var moduleAsts = new List<ModuleAst>();
                foreach (var m in order)
                {
                    var visible = new HashSet<string> { m.Manifest.Name };
                    foreach (var dep in m.Manifest.Dependencies ?? System.Array.Empty<string>()) visible.Add(dep);

                    var ast = new ModuleAst { Name = m.Manifest.Name, Visible = visible, Synchronous = m.Manifest.IsSynchronous, Stateless = m.Manifest.IsStateless };
                    // Модуль без единого исходника компилируется «успешно» и молча ничего не
                    // делает. Самая частая причина — опечатка в ключе манифеста: неизвестные
                    // ключи JSON игнорируются, список файлов остаётся пустым, а чекер
                    // отвечает «OK». Это не ошибка (пустой модуль законен), но и не тишина.
                    if (m.Files.Count == 0 && !m.EmptyIsIntended)
                        diag.Warning("W0300",
                            Loc.T(language, $"Модуль '{m.Manifest.Name}' не содержит ни одного исходника: список \"sources\" в module.json " +
                            "пуст или отсутствует (проверьте имя ключа и пути) — модуль ничего не делает.",
                                $"Module '{m.Manifest.Name}' has no source files: the \"sources\" list in module.json " +
                                "is empty or missing (check the key name and paths) — the module does nothing."),
                            SourcePos.None);
                    foreach (var (name, text) in m.Files)
                    {
                        // выкинутый карантином файл: модуль жив, а его нет
                        if (name != null && dropped.Contains(name)) continue;

                        var src = new SourceText(files.Count, name, text);
                        files.Add(src);

                        var lexer = new Lexer(src.Text, src.FileId, diag);
                        var tokens = lexer.Tokenize();
                        var file = new Parser(tokens, src.FileId, diag, lexer.DocComments).ParseFile();
                        ast.Files.Add(file);
                    }
                    moduleAsts.Add(ast);
                }

                // ----- семантика -----
                if (!diag.HasErrors)
                {
                    var checker = new Checker(host, diag);
                    var sem = checker.Check(moduleAsts);

                    // таблица для инструментов: даже с семантическими ошибками она
                    // точнее синтаксического индекса (типы, мерж, namespace)
                    try { symbols = ScriptSymbolTable.Build(host, sem, files, order); }
                    catch (Exception) { symbols = null; /* подсказки не должны ронять компиляцию */ }

                    // ----- байткод -----
                    if (!diag.HasErrors)
                    {
                        var program = new BytecodeCompiler(host, sem, files) { Language = language }.Compile(moduleAsts);
                        return Finish(program, diag.Items);
                    }
                }

                if (!quarantineBrokenModules)
                    return Finish(null, diag.Items);

                // карантин по файлу: выкидываем файлы с ошибками, модули живут.
                // Синтаксическая ошибка останавливает проход до чекера, поэтому
                // сначала выпадают файлы с ошибками парсера, следующим проходом —
                // семантические, в том числе у тех, кто ссылался на выкинутое.
                if (fileScope && DropBrokenFiles(diag.Items, fileOwner, alive, dropped, carried, excludedFiles, language))
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
                        guilty[mod] = Loc.T(language, $"ошибка компиляции {d.Code} ({d.File}:{d.Line})",
                            $"compilation error {d.Code} ({d.File}:{d.Line})");
                }

                if (guilty.Count == 0 || !ExcludeAndContinue(byName, alive, guilty, excluded, language))
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
                                            List<ExcludedFile> excludedFiles,
                                            Language language)
        {
            var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var d in items)
            {
                if (d.Severity != Severity.Error || d.File == null) continue;
                if (dropped.Contains(d.File) || reasons.ContainsKey(d.File)) continue;
                if (!fileOwner.TryGetValue(d.File, out var owner) || !alive.Contains(owner)) continue;
                reasons[d.File] = Loc.T(language, $"ошибка компиляции {d.Code} ({d.File}:{d.Line})", $"compilation error {d.Code} ({d.File}:{d.Line})");
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
                                               List<ExcludedModule> excluded,
                                               Language language)
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
                            toDrop[name] = Loc.T(language, $"зависит от исключённого модуля '{dep}'", $"depends on the excluded module '{dep}'");
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
                        diag.Error("E0304", diag.T($"Циклическая зависимость модулей через '{name}'.", $"Cyclic module dependency via '{name}'."), SourcePos.None);
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
