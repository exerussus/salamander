using System;
using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Runtime;
using Dsl.Text;

namespace Dsl.Ide
{
    /// <summary>
    /// Общая часть любой цели «Применить»: как собрать набор модулей для движка и
    /// как рассказать про итог компиляции. Хоста здесь нет — только Dsl.Core,
    /// поэтому своя цель (другой движок, свой загрузчик) пользуется тем же кодом,
    /// не завися от Dsl.Unity.
    /// </summary>
    public static class ApplySupport
    {
        /// <summary>
        /// Набор игры с заменой одноимённых модулей на модули IDE. Порядок и состав
        /// базового набора сохраняются один в один (включая безымянные и дубликаты —
        /// их разберёт компилятор); модуль, которого в игре нет, дописывается в
        /// конец, только если его имя есть в <paramref name="addable"/> (null —
        /// разрешено всё).
        ///
        /// Добавлять всё подряд нельзя: корнем воркспейса может быть целый Assets,
        /// где лежат и чужие модули (примеры, тесты, старые копии), — запускать их
        /// в живой игре никто не просил.
        /// </summary>
        public static List<ModuleSourceSet> Merge(List<ModuleSourceSet> baseSet, List<ModuleSourceSet> overrides,
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

        /// <summary>
        /// Наборы дают одну и ту же программу? Сравниваются ровно те поля, по
        /// которым хост считает отпечаток исходников для хот-релоада: имя, режим
        /// исполнения, версия API, зависимости и тексты файлов.
        /// </summary>
        public static bool SameModules(List<ModuleSourceSet> a, List<ModuleSourceSet> b)
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

        /// <summary>Итог перезагрузки скриптов — в консоль IDE (строкой, а не стеной диагностик).</summary>
        public static void ReportCompilation(CompilationResult r, Action<IdeLogKind, string> log)
        {
            if (r == null || log == null) return;
            int errors = 0, warnings = 0;
            foreach (var d in r.Diagnostics)
            {
                if (d.Severity == Severity.Error) errors++;
                else if (d.Severity == Severity.Warning) warnings++;
            }
            if (r.Success)
                log(IdeLogKind.Compile, $"Скрипты перезагружены: модулей {r.Program.Modules.Length}, " +
                                        $"триггеров {r.Program.Triggers.Length}" + (warnings > 0 ? $", предупреждений {warnings}." : "."));
            else
                log(IdeLogKind.Error, $"Перезагрузка не удалась: ошибок {errors} — работает предыдущая версия.");
            foreach (var ex in r.Excluded)
                log(IdeLogKind.Error, $"Модуль «{ex.Name}» исключён: {ex.Reason}");
            foreach (var d in r.Diagnostics)
                if (d.Severity == Severity.Error)
                    log(IdeLogKind.Error, d.ToString());
        }
    }

    /// <summary>
    /// Логи скриптов (Engine.Log/Warn/Error) — в консоль IDE. Движок пересоздаётся
    /// при перезапуске хоста, поэтому мост переподписывается по <see cref="Attach"/>
    /// и снимает подписку с прежнего: без этого мёртвый движок держал бы ссылку на
    /// закрытую IDE.
    /// </summary>
    public sealed class ScriptLogBridge : IDisposable
    {
        private readonly Action<IdeLogKind, string> _log;
        private ScriptEngine _engine;

        public ScriptLogBridge(Action<IdeLogKind, string> log) => _log = log;

        /// <summary>Подписаться на этот движок (повторный вызов с тем же — ничего не делает).</summary>
        public void Attach(ScriptEngine engine)
        {
            if (engine == null || ReferenceEquals(engine, _engine)) return;
            Dispose();
            _engine = engine;
            engine.OnLog += OnLog;
            engine.OnWarn += OnWarn;
            engine.OnError += OnError;
        }

        private void OnLog(string m) => _log?.Invoke(IdeLogKind.Info, m);
        private void OnWarn(string m) => _log?.Invoke(IdeLogKind.Warning, m);
        private void OnError(string m) => _log?.Invoke(IdeLogKind.Error, m);

        public void Dispose()
        {
            if (_engine == null) return;
            _engine.OnLog -= OnLog;
            _engine.OnWarn -= OnWarn;
            _engine.OnError -= OnError;
            _engine = null;
        }
    }
}
