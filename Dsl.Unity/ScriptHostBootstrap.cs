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
                 "(чекер/LSP). Если включён 'Загружать из папки' — отсюда же берутся коровые модули игры")]
        [SerializeField] private string _modsFolder = "Scripts";

        [Tooltip("Коровая загрузка: сканировать папку модулей (StreamingAssets/<modsFolder>) и грузить всё, " +
                 "что там лежит (каждая подпапка с module.json — модуль). Базовые скрипты игры. " +
                 "НЕ мешает внешнему SourceProvider и вшитым модулям — все три источника складываются.")]
        [SerializeField] private bool _loadFromModsFolder = true;

        [Tooltip("Следить за папкой модулей для хот-релоада (только когда включена загрузка из папки)")]
        [SerializeField] private bool _watchModsFolder = true;

        /// <summary>
        /// Внешний источник модулей от СБОРЩИКА ИГРЫ (моды, Addressables, сеть).
        /// Складывается с коровой загрузкой из папки и вшитыми модулями — не
        /// заменяет их. Назначьте до Awake (или переопределите LoadModules).
        /// </summary>
        public System.Func<System.Collections.Generic.List<ModuleSourceSet>> SourceProvider;

        [Tooltip("Модули, вшитые в ЭТУ сцену/карту (как триггеры карты в W3 или скрипты миссии в Arma). " +
                 "Каждый = манифест (module.json как TextAsset) + его исходники. Ассеты едут в билд только " +
                 "вместе со сценой, которая на них ссылается, и работают только пока эта сцена загружена.")]
        [SerializeField] private EmbeddedModule[] _embeddedModules;

        [Tooltip("Версия скриптового API игры; несовпадение в манифесте модуля = ошибка загрузки")]
        [SerializeField] private int _apiVersion = 1;

        [Header("Авто-старт")]
        [Tooltip("Поднять DSL в Awake автоматически. Выключите, если хост собирает реестр по фазам " +
                 "(регистрирует фичи/виды) и запускает движок вручную через RunDsl().")]
        [SerializeField] private bool _autoRun = true;

        [Tooltip("Тикать движок в Update автоматически. Выключите, если хост гоняет тик сам " +
                 "(свой игровой луп, фиксированный шаг, сетевой такт) — тогда зовите UpdateDsl() вручную.")]
        [SerializeField] private bool _autoUpdate = true;

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
        /// Собирает модули для компиляции из ТРЁХ складывающихся источников:
        /// (1) коровая папка модулей игры — StreamingAssets/&lt;modsFolder&gt;, под
        /// флагом _loadFromModsFolder; (2) внешний SourceProvider сборщика (моды,
        /// Addressables, сеть); (3) вшитые в сцену _embeddedModules (скрипты
        /// карты). Ни один не исключает другой. Переопределите, чтобы полностью
        /// заменить логику сбора.
        ///
        /// Привязка к карте (как в W3/Arma): положите скрипты карты в
        /// _embeddedModules этого компонента в нужной сцене. Ассеты попадут в
        /// билд только со своей сценой, а движок живёт на этом GameObject — при
        /// выгрузке сцены он уничтожается вместе со всеми файберами. Общего или
        /// статического состояния между картами нет: каждый бутстрап держит свой
        /// ScriptEngine.
        /// </summary>
        protected virtual List<ModuleSourceSet> LoadModules()
        {
            var modules = new List<ModuleSourceSet>();

            // 1) коровая загрузка: папка модулей игры (под флагом)
            if (_loadFromModsFolder && Directory.Exists(ModsPath))
                modules.AddRange(UnitySourceProvider.LoadFromFolder(ModsPath));

            // 2) внешний источник сборщика (моды, Addressables, сеть) — складывается
            var provided = SourceProvider?.Invoke();
            if (provided != null) modules.AddRange(provided);

            // 3) вшитые в сцену модули (скрипты карты)
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
            if (_autoRun) RunDsl();
        }

        public void RunDsl()
        {
            _registry = BuildRegistry(out _updateEventId);
            ExportApiManifestIfNeeded();

            _engine = new ScriptEngine(_registry);
            _engine.OnLog += m => Debug.Log($"[script] {m}");
            _engine.OnWarn += m => Debug.LogWarning($"[script] {m}");
            _engine.OnError += m => Debug.LogError($"[script] {m}");

            CompileAndLoad();

            // хот-релоад: следим за коровой папкой (если грузим из неё) и/или за
            // путём, назначенным сборщиком. Первый источник, у которого есть путь.
            if (_watchForChanges)
            {
                string watch = (_loadFromModsFolder && _watchModsFolder && Directory.Exists(ModsPath))
                    ? ModsPath
                    : WatchPath;
                if (watch != null) StartWatcher(watch);
            }
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

        /// <summary>
        /// Один шаг движка: разгребает отложенный хот-релоад (штампует время на
        /// главном потоке), тикает файберы и поднимает событие Update. Публичный,
        /// чтобы хост мог тикать сам (свой луп, фиксированный шаг, сетевой такт),
        /// выключив _autoUpdate.
        /// </summary>
        public void UpdateDsl()
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

        protected virtual void Update()
        {
            if (!_autoUpdate) return;
            UpdateDsl();
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

        /// <summary>Папка для слежки хот-релоада, назначенная сборщиком (в дополнение к коровой папке).</summary>
        public string WatchPath;

        private void StartWatcher(string path)
        {
            if (path == null || !Directory.Exists(path)) return;
            _watcher = new FileSystemWatcher(path)
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
