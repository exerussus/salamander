using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Dsl.Compilation;
using Dsl.Semantics;

namespace Dsl.Ide
{
    /// <summary>Снимок входов одной компиляции и её результат.</summary>
    public sealed class CompileJob
    {
        // входы — собираются на главном потоке, дальше только читаются
        public HostRegistry Registry;
        public int ApiVersion;
        public List<ModuleSourceSet> Modules;
        /// <summary>Версии открытых буферов на момент снимка (ключ документа → Version).</summary>
        public Dictionary<string, int> DocVersions;
        /// <summary>Логическое имя → ключ документа (для раскладки диагностик по файлам).</summary>
        public Dictionary<string, string> LogicalToKey;

        // выходы
        public CompilationResult Result;
        public string Error;
        public double Milliseconds;
    }

    /// <summary>
    /// Дебаунс-насос компиляции: снимок (главный поток) → компиляция → применение
    /// (главный поток). Одна компиляция в полёте; пока она идёт, новые запросы
    /// копятся в один.
    ///
    /// Фон — выделенный поток. Он НИКОГДА не прерывается сам: Thread.Abort нет ни
    /// в IL2CPP, ни в CoreCLR. Зависший (дольше HangSeconds) поток бросается —
    /// результат его игнорируется; прервать его может только хост, который это
    /// умеет (редактор на Mono перед перезагрузкой домена). Два зависания подряд
    /// останавливают фоновую компиляцию: компилятор, виснущий на этом тексте,
    /// нельзя плодить потоками. Без потоков (WebGL) компиляция идёт прямо в тике.
    /// </summary>
    public sealed class CompileRunner : IDisposable
    {
        public double DebounceSeconds = 0.35;
        public double HangSeconds = 10.0;
        public int MaxAbandoned = 2;

        public bool UseThreads = true;
        public Func<CompileJob> Snapshot;              // главный поток; null — компилировать нечего
        public Action<CompileJob> Apply;               // главный поток
        public Action<string, IdeStatusKind> Report;   // главный поток
        public Func<Thread, bool> AbortThread;         // хост: прервать поток (редактор/Mono), иначе null

        private bool _dirty;
        private double _dueAt;
        private Inflight _inflight;
        private readonly List<Thread> _abandoned = new List<Thread>();
        private bool _stalled;

        private sealed class Inflight
        {
            public CompileJob Job;
            public Thread Thread;
            public double StartedAt;
            public volatile bool Done;
        }

        public bool Busy => _inflight != null;
        public bool Stalled => _stalled;

        /// <summary>Запросить компиляцию. Каждый вызов сдвигает дедлайн: старт — через Debounce после последнего.</summary>
        public void Request(double now, bool immediate = false)
        {
            _dirty = true;
            _dueAt = now + (immediate ? 0.0 : DebounceSeconds);
        }

        /// <summary>Явная команда «Компилировать»: снимает остановку после зависаний.</summary>
        public void ForceRequest(double now)
        {
            _stalled = false;
            // зависшие потоки уже не оживить (в игре их не прервать) — забываем о них,
            // иначе счётчик снова остановит компиляцию на первом же тике
            _abandoned.Clear();
            Request(now, immediate: true);
        }

        /// <summary>Шаг насоса (главный поток).</summary>
        public void Tick(double now)
        {
            // 1) итог фоновой компиляции
            if (_inflight != null)
            {
                if (_inflight.Done)
                {
                    var done = _inflight;
                    _inflight = null;
                    Deliver(done.Job);
                }
                else if (now - _inflight.StartedAt > HangSeconds)
                {
                    var hung = _inflight;
                    _inflight = null;
                    bool aborted = false;
                    try { aborted = AbortThread != null && AbortThread(hung.Thread); }
                    catch { /* хост не смог — поток просто бросаем */ }
                    if (!aborted) _abandoned.Add(hung.Thread);
                    Report?.Invoke($"Компилятор Salamander завис (> {HangSeconds:0} с) — результат отброшен.", IdeStatusKind.Warning);
                }
            }

            _abandoned.RemoveAll(t => t == null || !t.IsAlive);
            if (_abandoned.Count >= MaxAbandoned && !_stalled)
            {
                _stalled = true;
                Report?.Invoke("Фоновая компиляция остановлена: компилятор завис несколько раз подряд. " +
                               "Нажмите «Компилировать», чтобы попробовать снова.", IdeStatusKind.Error);
            }

            // 2) старт новой
            if (!_dirty || now < _dueAt || _inflight != null || _stalled) return;
            _dirty = false;
            CompileJob job = null;
            try { job = Snapshot?.Invoke(); }
            catch (Exception e) { Report?.Invoke("Не удалось собрать входы компиляции: " + e.Message, IdeStatusKind.Error); }
            if (job == null) return;

            if (!UseThreads)
            {
                Run(job);
                Deliver(job);
                return;
            }

            var inflight = new Inflight { Job = job, StartedAt = now };
            inflight.Thread = new Thread(() =>
            {
                Run(job);
                inflight.Done = true; // volatile: результат job опубликован до флага
            })
            {
                IsBackground = true,
                Name = "Salamander IDE compile",
            };
            _inflight = inflight;
            try { inflight.Thread.Start(); }
            catch (Exception e)
            {
                // потоки недоступны (платформа) — дальше синхронно
                _inflight = null;
                UseThreads = false;
                Report?.Invoke("Фоновые потоки недоступны, компиляция в основном потоке: " + e.Message, IdeStatusKind.Warning);
                Run(job);
                Deliver(job);
            }
        }

        /// <summary>Чистая компиляция: только Dsl.Core, никакого Unity API и состояния IDE.</summary>
        private static void Run(CompileJob job)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                job.Result = ScriptCompiler.Compile(job.Registry ?? new HostRegistry(), job.ApiVersion,
                                                    job.Modules ?? new List<ModuleSourceSet>());
            }
            catch (ThreadAbortException)
            {
                job.Error = "прервано";
            }
            catch (Exception e)
            {
                job.Error = e.GetType().Name + ": " + e.Message;
            }
            job.Milliseconds = sw.Elapsed.TotalMilliseconds;
        }

        private void Deliver(CompileJob job)
        {
            if (job.Error != null)
            {
                Report?.Invoke("Сбой компилятора: " + job.Error, IdeStatusKind.Error);
                return;
            }
            try { Apply?.Invoke(job); }
            catch (Exception e) { Report?.Invoke("Ошибка применения результата: " + e.Message, IdeStatusKind.Error); }
        }

        /// <summary>Бросить текущую компиляцию (перезагрузка домена, закрытие). Хост может прервать поток.</summary>
        public void AbortInflight()
        {
            var cur = _inflight;
            _inflight = null;
            if (cur == null) return;
            bool aborted = false;
            try { aborted = AbortThread != null && AbortThread(cur.Thread); }
            catch { }
            if (!aborted) _abandoned.Add(cur.Thread);
        }

        public void Dispose()
        {
            AbortInflight();
            // брошенные потоки — фоновые (IsBackground) и не держат процесс; хост,
            // умеющий прерывать, получает последнюю возможность
            foreach (var t in _abandoned)
            {
                try { if (t.IsAlive) AbortThread?.Invoke(t); }
                catch { }
            }
            _abandoned.Clear();
            Snapshot = null;
            Apply = null;
            Report = null;
        }
    }
}
