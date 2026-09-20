using System;
using System.Collections.Generic;
using System.IO;
using Dsl.Compilation;
using Dsl.Semantics;
using Dsl.Text;
using Dsl.Tooling;
using Dsl.Unity;
using UnityEngine;

namespace Dsl.Ide
{
    /// <summary>
    /// API для IDE прямо из работающей игры: реестр бутстрапа — компилятору,
    /// экспортированный из него манифест — подсказкам. Файл salamander-api.json
    /// не нужен. Реестр появляется после RunDsl — до тех пор API пуст, опрос
    /// подхватит его, как только движок поднимется.
    /// </summary>
    public sealed class BootstrapApiSource : IApiSource
    {
        private readonly ScriptHostBootstrap _bootstrap;
        private HostRegistry _seen;

        public ApiModel Current { get; private set; } = ApiModel.Empty;
        public string Error { get; private set; }

        public BootstrapApiSource(ScriptHostBootstrap bootstrap)
        {
            _bootstrap = bootstrap;
            Poll();
        }

        public bool Poll()
        {
            var reg = _bootstrap != null ? _bootstrap.Registry : null;
            if (reg == null || ReferenceEquals(reg, _seen)) return false;
            try
            {
                Current = ApiModel.FromRegistry(reg, _bootstrap.ApiVersion, "API игры (живой реестр)");
                Error = null;
                // только после успеха: иначе один сбой (хост дорегистрировал API прямо
                // в этот кадр) навсегда оставил бы IDE без API — реестр-то тот же самый
                _seen = reg;
                return true;
            }
            catch (Exception e)
            {
                // живой реестр отдавать нельзя: он принадлежит игре и меняется на
                // главном потоке, а читал бы его поток компиляции. Держим прежнюю
                // модель и пробуем снова на следующем опросе.
                Error = "экспорт API для подсказок не удался: " + e.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// «Применить» в игре. Правит IDE не обязательно то, что грузит бутстрап,
    /// поэтому набор для компиляции собирается по ситуации:
    ///
    ///   • воркспейс — та же папка, из которой грузится бутстрап → CompileAndLoad():
    ///     диск и есть источник истины (правки уже сохранены), хот-релоад работает;
    ///   • воркспейс в памяти или другая папка → модули воркспейса накладываются
    ///     на набор бутстрапа (по имени модуля) и компилируется результат. Пока в
    ///     движке крутится такой набор, хот-релоад по папке приостановлен: иначе
    ///     первое же событие файловой системы откатило бы применённое.
    ///
    /// Итог компиляции и логи скриптов уходят в консоль IDE.
    /// </summary>
    public sealed class BootstrapApplyTarget : IIdeApplyTarget, IDisposable
    {
        private readonly ScriptHostBootstrap _bootstrap;
        private readonly Func<IScriptWorkspace> _workspace;
        private readonly Action<IdeLogKind, string> _log;
        private Dsl.Runtime.ScriptEngine _subscribed;

        public BootstrapApplyTarget(ScriptHostBootstrap bootstrap, Func<IScriptWorkspace> workspace, Action<IdeLogKind, string> log)
        {
            _bootstrap = bootstrap;
            _workspace = workspace;
            _log = log;
            if (_bootstrap != null)
            {
                _bootstrap.Compiled += OnCompiled;
                _bootstrap.Started += SubscribeEngine;
                SubscribeEngine();
            }
        }

        /// <summary>Бутстрап ещё жив (сцена не выгружена, объект не уничтожен).</summary>
        public bool IsAlive => _bootstrap != null;

        public bool CanApply => _bootstrap != null && _bootstrap.Engine != null;

        public string ApplyLabel => "Применить в игре";

        public void Apply()
        {
            if (!CanApply) { _log?.Invoke(IdeLogKind.Warning, "Движок скриптов не запущен — применять некуда."); return; }
            SubscribeEngine();
            var ws = _workspace?.Invoke();

            if (UsesBootstrapFolder(ws))
            {
                _bootstrap.HotReloadSuspended = false;
                _bootstrap.CompileAndLoad();
                return;
            }

            // сбой чтения НЕ должен трогать работающую программу: перезагрузка из
            // папки бутстрапа откатила бы уже применённые правки, а набор без
            // модулей игры убил бы все файберы
            var overrides = LoadWorkspaceModules(ws, out var addable);
            if (overrides == null)
            {
                _log?.Invoke(IdeLogKind.Error, "Правки не применены: модули воркспейса не прочитались.");
                return;
            }

            List<ModuleSourceSet> baseSet;
            try { baseSet = _bootstrap.CollectModules(); }
            catch (Exception e)
            {
                _log?.Invoke(IdeLogKind.Error, "Правки не применены: модули игры не прочитались — " + e.Message);
                return;
            }

            var merged = MergeModules(baseSet, overrides, addable);
            ReportSkipped(baseSet, overrides, addable);
            var result = _bootstrap.CompileAndLoadFrom(merged);

            // хот-релоад перечитывает ТОЛЬКО источники бутстрапа. Если в движке
            // теперь не они (правки из памяти, лишние модули), первое же событие
            // файловой системы откатило бы применённое — ставим паузу. Компиляция
            // не удалась — в движке осталась прежняя программа, пауза не нужна.
            _bootstrap.HotReloadSuspended = result != null && result.Success && !SameModules(baseSet, merged);
        }

        /// <summary>Сказать вслух про модули воркспейса, которые в игру не поехали.</summary>
        private void ReportSkipped(List<ModuleSourceSet> baseSet, List<ModuleSourceSet> overrides, HashSet<string> addable)
        {
            if (addable == null || _log == null) return;
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in baseSet ?? new List<ModuleSourceSet>())
                if (!string.IsNullOrEmpty(m?.Manifest?.Name)) known.Add(m.Manifest.Name);

            List<string> skipped = null;
            foreach (var m in overrides)
            {
                string name = m?.Manifest?.Name;
                if (string.IsNullOrEmpty(name) || known.Contains(name) || addable.Contains(name)) continue;
                (skipped ??= new List<string>()).Add(name);
            }
            if (skipped != null)
                _log(IdeLogKind.Warning, "Не применены (их нет в игре и они вне папки модулей игры): " + string.Join(", ", skipped));
        }

        /// <summary>
        /// Наборы дают одну и ту же программу? Сравниваются ровно те поля, по
        /// которым бутстрап считает отпечаток исходников для хот-релоада.
        /// </summary>
        private static bool SameModules(List<ModuleSourceSet> a, List<ModuleSourceSet> b)
        {
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                ModuleSourceSet x = a[i], y = b[i];
                if (ReferenceEquals(x, y)) continue;
                if (x?.Manifest == null || y?.Manifest == null) return false;
                if (!string.Equals(x.Manifest.Name, y.Manifest.Name, StringComparison.Ordinal)) return false;
                if (!string.Equals(x.Manifest.Execution, y.Manifest.Execution, StringComparison.Ordinal)) return false;
                if (x.Manifest.ApiVersion != y.Manifest.ApiVersion) return false;
                var da = x.Manifest.Dependencies ?? Array.Empty<string>();
                var db = y.Manifest.Dependencies ?? Array.Empty<string>();
                if (da.Length != db.Length) return false;
                for (int d = 0; d < da.Length; d++)
                    if (!string.Equals(da[d], db[d], StringComparison.Ordinal)) return false;
                if (x.Files.Count != y.Files.Count) return false;
                for (int f = 0; f < x.Files.Count; f++)
                {
                    if (!string.Equals(x.Files[f].name, y.Files[f].name, StringComparison.Ordinal)) return false;
                    if (!string.Equals(x.Files[f].text, y.Files[f].text, StringComparison.Ordinal)) return false;
                }
            }
            return true;
        }

        /// <summary>IDE правит ровно ту папку, из которой грузится бутстрап?</summary>
        private bool UsesBootstrapFolder(IScriptWorkspace ws)
        {
            if (ws == null) return true; // нечего накладывать — обычная перезагрузка
            if (!_bootstrap.LoadsFromModsFolder) return false;
            return SamePath(ws.Root, _bootstrap.ModsDirectory);
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                a = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                b = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch { return false; }
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Модули воркспейса и те из них, которые МОЖНО добавить в игру как новые.
        /// Добавлять всё подряд нельзя: корнем воркспейса в редакторе может быть
        /// весь Assets, где лежат и чужие module.json (примеры, тесты, старые
        /// копии) — запускать их в живой игре никто не просил. Новым считается
        /// только модуль из папки модулей самой игры; остальные могут лишь
        /// заменить одноимённый модуль игры.
        /// </summary>
        private List<ModuleSourceSet> LoadWorkspaceModules(IScriptWorkspace ws, out HashSet<string> addable)
        {
            addable = null;
            // набор в памяти собран из источников самой игры — всё в нём «своё»
            if (ws is MemoryWorkspace mem) return mem.Snapshot();

            WorkspaceModules loaded;
            try { loaded = ws.Load(); }
            catch (Exception e)
            {
                _log?.Invoke(IdeLogKind.Error, "Модули воркспейса не прочитались: " + e.Message);
                return null;
            }
            if (loaded == null) return null;

            string mods = _bootstrap.LoadsFromModsFolder ? _bootstrap.ModsDirectory : null;
            addable = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(mods))
            {
                foreach (var pair in loaded.ModuleDirs)
                    if (IsInside(pair.Value, mods)) addable.Add(pair.Key);
            }
            return loaded.Modules;
        }

        /// <summary>Путь лежит внутри папки (или совпадает с ней)?</summary>
        private static bool IsInside(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            try
            {
                string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase)) return true;
                return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Набор бутстрапа с заменой одноимённых модулей на модули IDE. Порядок и
        /// состав базового набора сохраняются один в один (включая безымянные и
        /// дубликаты — их компилятор разберёт сам); модуль, которого в игре нет,
        /// дописывается в конец, только если его имя есть в <paramref name="addable"/>
        /// (null — разрешено всё). Публичный: тем же способом собирает набор свой
        /// IIdeApplyTarget у хоста.
        /// </summary>
        public static List<ModuleSourceSet> MergeModules(List<ModuleSourceSet> baseSet, List<ModuleSourceSet> overrides,
                                                         HashSet<string> addable = null)
        {
            if (overrides == null) return baseSet ?? new List<ModuleSourceSet>();
            var byName = new Dictionary<string, ModuleSourceSet>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var m in overrides)
            {
                string name = m?.Manifest?.Name;
                if (string.IsNullOrEmpty(name) || byName.ContainsKey(name)) continue;
                byName[name] = m;
                order.Add(name);
            }

            var result = new List<ModuleSourceSet>();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in baseSet ?? new List<ModuleSourceSet>())
            {
                if (m == null) continue;
                string name = m.Manifest?.Name;
                if (!string.IsNullOrEmpty(name) && taken.Add(name) && byName.TryGetValue(name, out var o)) result.Add(o);
                else result.Add(m);
            }
            foreach (var name in order)
                if (!taken.Contains(name) && (addable == null || addable.Contains(name))) result.Add(byName[name]);
            return result;
        }

        private void SubscribeEngine()
        {
            var engine = _bootstrap != null ? _bootstrap.Engine : null;
            if (engine == null || ReferenceEquals(engine, _subscribed)) return;
            Unsubscribe();
            _subscribed = engine;
            engine.OnLog += OnScriptLog;
            engine.OnWarn += OnScriptWarn;
            engine.OnError += OnScriptError;
        }

        private void Unsubscribe()
        {
            if (_subscribed == null) return;
            _subscribed.OnLog -= OnScriptLog;
            _subscribed.OnWarn -= OnScriptWarn;
            _subscribed.OnError -= OnScriptError;
            _subscribed = null;
        }

        private void OnScriptLog(string m) => _log?.Invoke(IdeLogKind.Info, m);
        private void OnScriptWarn(string m) => _log?.Invoke(IdeLogKind.Warning, m);
        private void OnScriptError(string m) => _log?.Invoke(IdeLogKind.Error, m);

        private void OnCompiled(CompilationResult r)
        {
            if (r == null || _log == null) return;
            int errors = 0, warnings = 0;
            foreach (var d in r.Diagnostics)
            {
                if (d.Severity == Severity.Error) errors++;
                else if (d.Severity == Severity.Warning) warnings++;
            }
            if (r.Success)
                _log(IdeLogKind.Compile, $"Скрипты перезагружены: модулей {r.Program.Modules.Length}, " +
                                         $"триггеров {r.Program.Triggers.Length}" + (warnings > 0 ? $", предупреждений {warnings}." : "."));
            else
                _log(IdeLogKind.Error, $"Перезагрузка не удалась: ошибок {errors} — работает предыдущая версия.");
            foreach (var ex in r.Excluded)
                _log(IdeLogKind.Error, $"Модуль «{ex.Name}» исключён: {ex.Reason}");
            foreach (var d in r.Diagnostics)
                if (d.Severity == Severity.Error)
                    _log(IdeLogKind.Error, d.ToString());
        }

        public void Dispose()
        {
            // HotReloadSuspended намеренно не снимаем: применённая программа должна
            // пережить закрытие IDE, а включённый хот-релоад откатил бы её к диску
            Unsubscribe();
            if (_bootstrap != null)
            {
                _bootstrap.Compiled -= OnCompiled;
                _bootstrap.Started -= SubscribeEngine;
            }
        }
    }

    public static class BootstrapWorkspace
    {
        /// <summary>
        /// Воркспейс для IDE в игре. Модули из папки на диске (редактор, десктоп) —
        /// файловый: сохранение пишет файлы, а хот-релоад бутстрапа их подхватывает.
        /// Иначе (WebGL, мобильные, модули только из SourceProvider/сцены) — в памяти:
        /// правки живут в сессии IDE и применяются компиляцией набора из памяти.
        /// </summary>
        public static IScriptWorkspace Create(ScriptHostBootstrap bootstrap, string rootOverride = null)
        {
            string root = !string.IsNullOrEmpty(rootOverride) ? rootOverride
                        : bootstrap != null && bootstrap.LoadsFromModsFolder ? bootstrap.ModsDirectory : null;
            if (!string.IsNullOrEmpty(root) && CanUseFiles() && Directory.Exists(root) && IsWritable(root))
                return new FileSystemWorkspace(root);
            if (!string.IsNullOrEmpty(rootOverride) && CanUseFiles() && !Directory.Exists(rootOverride))
                Debug.LogWarning("[salamander-ide] Указанной папки модулей нет, правки останутся в памяти: " + rootOverride);

            var mem = new MemoryWorkspace { DisplayName = "модули игры (в памяти)" };
            if (bootstrap != null)
            {
                try { mem.Replace(bootstrap.CollectModules() ?? new List<ModuleSourceSet>()); }
                catch (Exception e) { Debug.LogWarning("[salamander-ide] модули игры не прочитались: " + e.Message); }
            }
            return mem;
        }

        /// <summary>
        /// Есть ли у платформы обычная файловая система для модулей: на WebGL и
        /// Android StreamingAssets — вообще не путь (архив/jar), на iOS — папка
        /// внутри подписанного бандла, куда писать нельзя.
        /// </summary>
        public static bool CanUseFiles() =>
            Application.platform != RuntimePlatform.WebGLPlayer &&
            Application.platform != RuntimePlatform.Android &&
            Application.platform != RuntimePlatform.IPhonePlayer;

        /// <summary>
        /// Папка доступна на запись? Редактировать в IDE то, что не сохранить, —
        /// хуже, чем честно уйти в память: правки хотя бы применятся к игре.
        /// </summary>
        private static bool IsWritable(string dir)
        {
            string probe = Path.Combine(dir, ".salamander-ide-probe");
            try
            {
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch
            {
                try { if (File.Exists(probe)) File.Delete(probe); } catch { }
                Debug.LogWarning("[salamander-ide] Папка модулей только для чтения — правки живут в памяти: " + dir);
                return false;
            }
        }
    }
}
