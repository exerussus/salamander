using System.Collections.Generic;
using System.IO;
using Dsl.Compilation;
using Dsl.Runtime;
using Dsl.Semantics;
using UnityEngine;

namespace Dsl.Unity
{
    /// <summary>
    /// Точка входа скриптового движка в игре. Один экземпляр на сцену/сервер.
    ///
    /// Скрипты исполняются ТОЛЬКО на стороне авторитета: переопределите
    /// IsAuthority в наследнике под свой сетевой стек. Пример для FishNet:
    ///
    ///   public sealed class NetworkScriptHost : ScriptHostBootstrap
    ///   {
    ///       [SerializeField] private FishNet.Object.NetworkObject _netObject;
    ///       protected override bool IsAuthority => _netObject.IsServerInitialized;
    ///   }
    ///
    /// В одиночной игре (в т.ч. WebGL) базовый класс работает как есть:
    /// клиент сам себе сервер.
    /// </summary>
    public class ScriptHostBootstrap : MonoBehaviour
    {
        [Header("Модули")]
        [Tooltip("Подпапка StreamingAssets: сюда экспортируется salamander-api.json для тулинга " +
                 "(чекер/LSP); движок отсюда НИЧЕГО не грузит сам")]
        [SerializeField] private string _modsFolder = "Scripts";

        /// <summary>
        /// Источник модулей — СБОРЩИК ИГРЫ. Движок сам ничего не ищет и не
        /// собирает: назначьте провайдер до Awake (или переопределите
        /// LoadModules). Сборщик может использовать утилиты
        /// UnitySourceProvider/ModuleLoader — но зовёт их он, не движок.
        /// </summary>
        public System.Func<System.Collections.Generic.List<ModuleSourceSet>> SourceProvider;

        [Tooltip("Модули, вшитые в ЭТУ сцену/карту (как триггеры карты в W3 или скрипты миссии в Arma). " +
                 "Каждый = манифест (module.json как TextAsset) + его исходники. Ассеты едут в билд только " +
                 "вместе со сценой, которая на них ссылается, и работают только пока эта сцена загружена.")]
        [SerializeField] private EmbeddedModule[] _embeddedModules;

        [Tooltip("Версия скриптового API игры; несовпадение в манифесте модуля = ошибка загрузки")]
        [SerializeField] private int _apiVersion = 1;

        [Header("Горячая перезагрузка")]
        [SerializeField] private bool _watchForChanges = true;
        [Tooltip("Пауза после последнего изменения файла перед перекомпиляцией, сек")]
        [SerializeField] private float _reloadDebounce = 0.3f;

        [Header("Инструменты")]
        [Tooltip("В редакторе выгружать salamander-api.json рядом с модулями: его читают CLI-чекер и расширение VS Code")]
        [SerializeField] private bool _exportApiManifest = true;

        private ScriptEngine _engine;
        private HostRegistry _registry;
        private int _updateEventId = -1;

        private FileSystemWatcher _watcher;
        private volatile bool _pendingDirty; // ставится из потока вотчера
        private bool _dirty;                  // главный поток
        private float _dirtyAt;

        public ScriptEngine Engine => _engine;
        public HostRegistry Registry => _registry;

        /// <summary>Модуль, вшитый в сцену: манифест + исходники как TextAsset-ы.</summary>
        [System.Serializable]
        public sealed class EmbeddedModule
        {
            public TextAsset manifest;   // содержимое module.json
            public TextAsset[] sources;  // .sal-файлы в порядке из манифеста
        }

        /// <summary>Скрипты крутятся только когда истинно (сервер/одиночка).</summary>
        protected virtual bool IsAuthority => true;

        protected string ModsPath => Path.Combine(Application.streamingAssetsPath, _modsFolder);

        /// <summary>
        /// Собирает модули для компиляции. По умолчанию: общая папка (если
        /// включена) + вшитые в сцену модули. Переопределите, чтобы грузить
        /// скрипты откуда угодно (Addressables, сеть, база данных карты).
        ///
        /// Привязка к карте (как в W3/Arma) достигается так: положите скрипты
        /// карты в _embeddedModules этого компонента в нужной сцене. Ассеты попадут
        /// в билд только со своей сценой, а
        /// движок живёт на этом GameObject — при выгрузке сцены он уничтожается
        /// вместе со всеми файберами. Никакого общего/статического состояния
        /// между картами нет: каждый бутстрап держит свой ScriptEngine.
        /// </summary>
        protected virtual List<ModuleSourceSet> LoadModules()
        {
            var modules = new List<ModuleSourceSet>();

            // единственный внешний источник — сборщик игры
            var provided = SourceProvider?.Invoke();
            if (provided != null) modules.AddRange(provided);

            if (_embeddedModules != null)
            {
                foreach (var em in _embeddedModules)
                {
                    if (em == null || em.manifest == null) continue;
                    try
                    {
                        modules.Add(UnitySourceProvider.FromTextAssets(em.manifest, em.sources ?? System.Array.Empty<TextAsset>()));
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogError($"[script] Вшитый модуль '{em.manifest.name}' не загрузился: {ex.Message}");
                    }
                }
            }

            return modules;
        }

        // ===================================================================

        protected virtual void Awake()
        {
            _registry = BuildRegistry(out _updateEventId);
            ExportApiManifestIfNeeded();

            _engine = new ScriptEngine(_registry);
            _engine.OnLog += m => Debug.Log($"[script] {m}");
            _engine.OnWarn += m => Debug.LogWarning($"[script] {m}");
            _engine.OnError += m => Debug.LogError($"[script] {m}");

            CompileAndLoad();

            // хот-релоад: за источниками следит тот, кто их дал — сборщик
            // включает слежку явно (WatchPath = папка с исходниками)
            if (_watchForChanges && WatchPath != null) StartWatcher();
        }

        /// <summary>
        /// Переопределите и зарегистрируйте здесь классы/свойства/методы/события
        /// вашей игры (UnitApi, SpawnApi, событие OnUnitDamageTaken и т.д.).
        /// Метод должен только РЕГИСТРИРОВАТЬ (ничего не исполнять): он вызывается
        /// и при экспорте манифеста в режиме редактирования, вне Play.
        /// </summary>
        protected virtual void ConfigureHost(HostRegistry registry) { }

        /// <summary>Строит реестр так же, как Awake — общий путь для рантайма и экспорта.</summary>
        private HostRegistry BuildRegistry(out int updateEventId)
        {
            var reg = new HostRegistry();
            // Update — встроенное событие тика; регистрируем до пользовательских
            updateEventId = reg.DefineEvent("Update", TypeRef.Float, TypeRef.Float);
            ConfigureHost(reg);
            return reg;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Выгрузить salamander-api.json без входа в Play. ПКМ по компоненту в
        /// инспекторе → «Export API manifest». Удобно для сборки модкита:
        /// манифест отдаётся модерам вместе с CLI-чекером и расширением.
        /// </summary>
        [ContextMenu("Export API manifest")]
        private void ExportApiManifestNow()
        {
            var reg = BuildRegistry(out _);
            if (!Directory.Exists(ModsPath)) Directory.CreateDirectory(ModsPath);
            string path = Path.Combine(ModsPath, "salamander-api.json");
            File.WriteAllText(path, Compilation.ApiManifest.Export(reg, _apiVersion));
            Debug.Log($"[script] salamander-api.json выгружен: {path}");
            UnityEditor.AssetDatabase.Refresh();
        }
#endif

        protected virtual void Update()
        {
            // событие вотчера пришло из чужого потока — штампуем время здесь, на главном
            if (_pendingDirty)
            {
                _pendingDirty = false;
                _dirty = true;
                _dirtyAt = UnityEngine.Time.unscaledTime;
            }
            if (_dirty && UnityEngine.Time.unscaledTime - _dirtyAt >= _reloadDebounce)
            {
                _dirty = false;
                CompileAndLoad(); // при ошибке старая программа продолжает работать
            }

            if (!IsAuthority || _engine == null || !_engine.IsLoaded) return;

            float dt = UnityEngine.Time.deltaTime;
            _engine.Tick(dt);
            _engine.Raise(_updateEventId)
                   .AddFloat((float)_engine.Time)
                   .AddFloat(dt)
                   .Commit();
        }

        protected virtual void OnDestroy()
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        // ===================================================================

        /// <summary>
        /// Выгружает salamander-api.json (манифест API хоста) в папку модулей.
        /// Только в редакторе: его читают CLI-чекер и расширение VS Code, чтобы
        /// компилировать и дополнять скрипты вне игры. Источник истины — этот
        /// же HostRegistry, поэтому манифест никогда не расходится с игрой.
        /// </summary>
        private void ExportApiManifestIfNeeded()
        {
#if UNITY_EDITOR
            if (!_exportApiManifest) return;
            try
            {
                if (!Directory.Exists(ModsPath)) Directory.CreateDirectory(ModsPath);
                string json = Compilation.ApiManifest.Export(_registry, _apiVersion);
                File.WriteAllText(Path.Combine(ModsPath, "salamander-api.json"), json);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[script] Не удалось выгрузить salamander-api.json: {ex.Message}");
            }
#endif
        }

        /// <summary>Компиляция всех модулей; при успехе — атомарная замена программы.</summary>
        public void CompileAndLoad()
        {
            var modules = LoadModules();
            var result = ScriptCompiler.Compile(_registry, _apiVersion, modules);

            foreach (var d in result.Diagnostics)
            {
                if (d.Severity == Text.Severity.Error) Debug.LogError($"[script] {d}");
                else if (d.Severity == Text.Severity.Warning) Debug.LogWarning($"[script] {d}");
                else Debug.Log($"[script] {d}");
            }

            if (result.Success)
            {
                _engine.LoadProgram(result.Program);
                Debug.Log($"[script] Программа загружена: модулей {result.Program.Modules.Length}, " +
                          $"триггеров {result.Program.Triggers.Length}, функций {result.Program.Functions.Length}.");
            }
            else
            {
                Debug.LogError("[script] Компиляция не удалась — работает предыдущая версия (если была).");
            }
        }

        /// <summary>Папка для слежки хот-релоада; null — не следить. Назначает сборщик.</summary>
        public string WatchPath;

        private void StartWatcher()
        {
            if (WatchPath == null || !Directory.Exists(WatchPath)) return;
            _watcher = new FileSystemWatcher(WatchPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            // события приходят из чужого потока — только флаг, работа в Update
            _watcher.Changed += OnFsEvent;
            _watcher.Created += OnFsEvent;
            _watcher.Deleted += OnFsEvent;
            _watcher.Renamed += (_, __) => MarkDirty();
            _watcher.EnableRaisingEvents = true;
        }

        private void OnFsEvent(object sender, FileSystemEventArgs e) => MarkDirty();

        private void MarkDirty() => _pendingDirty = true; // никакого Unity API из чужого потока
    }
}
