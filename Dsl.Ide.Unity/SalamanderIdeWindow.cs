using System;
using System.IO;
using Dsl.Unity;
using UnityEngine;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace Dsl.Ide
{
    /// <summary>
    /// IDE Salamander внутри игры (плеер-билд и Play Mode). Повесьте на объект
    /// сцены, укажите бутстрап скриптов и PanelSettings (или готовый UIDocument) —
    /// окно открывается горячей клавишей (по умолчанию F9) или Toggle() из кода.
    ///
    /// API берётся из живого реестра бутстрапа, модули — из его папки (десктоп)
    /// или из памяти (WebGL/мобильные). «Применить в игре» перекомпилирует и
    /// перезагружает скрипты; логи скриптов — во вкладке «Консоль».
    /// Сессия и несохранённые правки — в Application.persistentDataPath.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SalamanderIdeWindow : MonoBehaviour
    {
        [Header("Источник скриптов")]
        [Tooltip("Бутстрап движка скриптов. Пусто — первый найденный на сцене.")]
        [SerializeField] private ScriptHostBootstrap _bootstrap;

        [Tooltip("Папка модулей вместо папки бутстрапа (абсолютный путь). Пусто — папка бутстрапа.")]
        [SerializeField] private string _modsRootOverride = "";

        [Header("UI")]
        [Tooltip("Готовый UIDocument. Пусто — создаётся свой на этом объекте с PanelSettings ниже.")]
        [SerializeField] private UIDocument _document;

        [Tooltip("PanelSettings для своего UIDocument (с темой — Default Runtime Theme). Обязательны, если UIDocument не задан.")]
        [SerializeField] private PanelSettings _panelSettings;

        [Tooltip("Порядок сортировки панели IDE — поверх игрового UI.")]
        [SerializeField] private int _sortingOrder = 1000;

        [Tooltip("Свой USS вместо встроенного (Resources/SalamanderIde/SalIde).")]
        [SerializeField] private StyleSheet _styleSheet;

        [Tooltip("Моноширинный шрифт кода (FontAsset). На WebGL/мобильных обязателен: системных шрифтов там нет.")]
        [SerializeField] private FontAsset _codeFontAsset;

        [Tooltip("Моноширинный шрифт кода (обычный Font), если нет FontAsset.")]
        [SerializeField] private Font _codeFont;

        [Tooltip("Шрифт оболочки IDE (панели, кнопки). Пусто — шрифт темы панели; для русского интерфейса " +
                 "нужен шрифт с кириллицей.")]
        [SerializeField] private FontAsset _uiFontAsset;

        [Header("Поведение")]
        [SerializeField] private bool _openOnStart;

        [Tooltip("Клавиша открытия/закрытия (Input System или старый Input Manager — что есть в проекте).")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.F9;

        [Tooltip("Показывать курсор, пока IDE открыта (и вернуть прежнее состояние при закрытии).")]
        [SerializeField] private bool _manageCursor = true;

        private SalIdeView _view;
        private BootstrapApplyTarget _apply;
        private ScriptHostBootstrap _bound;   // бутстрап, под который собраны цель, API и воркспейс
        private bool _boundKnown;             // _bound осмыслен (в т.ч. когда движка не было вовсе)
        private float _nextBindCheck;
        private bool _ownsDocument;
        private bool _visible;
        private bool _prevCursorVisible;
        private CursorLockMode _prevLock;

        /// <summary>IDE открыта/закрыта (например, чтобы глушить игровой ввод).</summary>
        public event Action<bool> VisibilityChanged;

        public bool Visible => _visible;
        public SalIdeView View => _view;

        private void Awake()
        {
            if (_bootstrap == null) _bootstrap = FindAnyObjectByType<ScriptHostBootstrap>();
            if (_document == null)
            {
                _document = gameObject.AddComponent<UIDocument>();
                _ownsDocument = true;
                if (_panelSettings == null)
                {
                    // без ассета PanelSettings — временные, без темы: окно работает, но
                    // стандартные контролы (скроллбары) без оформления. Лучше задать ассет.
                    _panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
                    _panelSettings.scaleMode = PanelScaleMode.ConstantPhysicalSize;
                    Debug.LogWarning("[salamander-ide] PanelSettings не заданы — созданы временные без темы. " +
                                     "Назначьте ассет PanelSettings с Default Runtime Theme.");
                }
                _document.panelSettings = _panelSettings;
            }
            _document.sortingOrder = _sortingOrder;
        }

        private void Start()
        {
            SetVisible(_openOnStart);
        }

        private void Build()
        {
            var root = _document != null ? _document.rootVisualElement : null;
            if (root == null) return;
            if (_view != null)
            {
                // выключение/включение объекта пересоздаёт корень UIDocument, и окно
                // остаётся вне панели — без этого IDE больше не появлялась
                if (_view.panel == null || _view.parent != root) root.Add(_view);
                EnsureApplyTarget();
                return;
            }

            string storageDir = Path.Combine(Application.persistentDataPath, "SalamanderIde");
            var host = new DefaultIdeHost();
            var storage = new FileIdeStorage(storageDir, (what, e) => host.Log(IdeLogLevel.Warning, "storage", what, e));
            var workspace = BootstrapWorkspace.Create(_bootstrap, _modsRootOverride);
            IApiSource api = _bootstrap != null ? (IApiSource)new BootstrapApiSource(_bootstrap) : new FixedApiSource(ApiModel.Empty);

            FontDefinition? font = null;
            if (_codeFontAsset != null) font = FontDefinition.FromSDFFont(_codeFontAsset);
            else if (_codeFont != null) font = FontDefinition.FromFont(_codeFont);

            _view = new SalIdeView(new IdeOptions
            {
                Host = host,
                Storage = storage,
                Workspace = workspace,
                Api = api,
                StyleSheet = _styleSheet,
                CodeFont = font,
            });
            // воркспейс и API собраны под этот бутстрап (возможно, под его отсутствие)
            _bound = _bootstrap;
            _boundKnown = true;
            if (_uiFontAsset != null) _view.style.unityFontDefinition = FontDefinition.FromSDFFont(_uiFontAsset);
            _view.style.position = Position.Absolute;
            _view.style.left = 0;
            _view.style.top = 0;
            _view.style.right = 0;
            _view.style.bottom = 0;

            EnsureApplyTarget();

            // закрыть — кнопкой в тулбаре (в игре нет рамки окна)
            var close = new Button(() => SetVisible(false)) { text = "Закрыть", tooltip = $"Закрыть IDE ({_toggleKey})" };
            close.AddToClassList("sal-btn");
            _view.ToolbarExtras.Add(close);

            root.Add(_view);
        }

        /// <summary>
        /// Привязка к движку. Бутстрап мог смениться вместе со сценой, поэтому
        /// цель «Применить» — а с ней API и воркспейс — пересобирается, как только
        /// прежний объект уничтожен: иначе кнопка осталась бы привязанной к
        /// мёртвому движку, а подсказки и модули — к набору прежней сцены.
        /// </summary>
        private void EnsureApplyTarget()
        {
            if (_view == null) return;
            if (_apply != null && _apply.IsAlive && ReferenceEquals(_bound, _bootstrap)) return;

            if (_bootstrap == null) _bootstrap = FindAnyObjectByType<ScriptHostBootstrap>();
            if (_bootstrap == null) // (в т.ч. «поддельный» null уничтоженного объекта)
            {
                if (_apply != null) { _apply.Dispose(); _apply = null; _view.SetApplyTarget(null); }
                return;
            }
            if (ReferenceEquals(_bound, _bootstrap) && _apply != null) return;

            bool changed = _boundKnown && !ReferenceEquals(_bound, _bootstrap);
            _apply?.Dispose();
            _apply = new BootstrapApplyTarget(_bootstrap, () => _view?.Workspace, (k, m) => _view?.Log(k, m));
            _view.SetApplyTarget(_apply);
            _bound = _bootstrap;
            _boundKnown = true;
            if (changed) Rebind();
        }

        /// <summary>Движок сменился: подсказки и модули — от нового бутстрапа.</summary>
        private void Rebind()
        {
            _view.SetApiSource(new BootstrapApiSource(_bootstrap));
            if (_view.HasUnsavedChanges)
            {
                // молча подменять воркспейс нельзя — несохранённые правки пропали бы
                _view.Log(IdeLogKind.Warning, "Движок скриптов сменился (новая сцена). Сохраните или отмените правки, " +
                                              "чтобы IDE перешла на его модули.");
                return;
            }
            _view.SetWorkspace(BootstrapWorkspace.Create(_bootstrap, _modsRootOverride));
        }

        /// <summary>Открыть/закрыть IDE.</summary>
        public void Toggle() => SetVisible(!_visible);

        public void SetVisible(bool visible)
        {
            if (visible) Build();
            if (_view == null) return;
            if (_visible == visible && _view.style.display.value == (visible ? DisplayStyle.Flex : DisplayStyle.None)) return;
            _visible = visible;
            _view.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            // закрытая IDE не работает: ни опроса диска, ни компиляции, ни тиков
            _view.SetActive(visible);
            if (!visible) _view.PersistSession();

            if (_manageCursor)
            {
                if (visible)
                {
                    _prevCursorVisible = UnityEngine.Cursor.visible;
                    _prevLock = UnityEngine.Cursor.lockState;
                    UnityEngine.Cursor.visible = true;
                    UnityEngine.Cursor.lockState = CursorLockMode.None;
                }
                else
                {
                    UnityEngine.Cursor.visible = _prevCursorVisible;
                    UnityEngine.Cursor.lockState = _prevLock;
                }
            }
            if (visible) _view.schedule.Execute(() => _view.Editor.FocusCode());
            VisibilityChanged?.Invoke(visible);
        }

        private void OnEnable()
        {
            // объект включили обратно: UIDocument пересоздал корень, окно надо вернуть,
            // иначе открытая IDE исчезала, а первый F9 «съедался» (она числилась открытой)
            if (_visible) Build();
        }

        private void Update()
        {
            if (TogglePressed()) Toggle();

            // движок мог подняться позже окна или смениться вместе со сценой
            if (_visible && _view != null && Time.unscaledTime >= _nextBindCheck)
            {
                _nextBindCheck = Time.unscaledTime + 1f;
                EnsureApplyTarget();
            }
        }

        private bool TogglePressed()
        {
            if (_toggleKey == KeyCode.None) return false;
#if SAL_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null)
            {
                var key = ToInputSystemKey(_toggleKey);
                if (key != UnityEngine.InputSystem.Key.None) return kb[key].wasPressedThisFrame;
            }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
            return Input.GetKeyDown(_toggleKey);
#else
            return false;
#endif
        }

#if SAL_INPUT_SYSTEM
        private static UnityEngine.InputSystem.Key ToInputSystemKey(KeyCode code)
        {
            if (code >= KeyCode.F1 && code <= KeyCode.F12)
                return UnityEngine.InputSystem.Key.F1 + (code - KeyCode.F1);
            switch (code)
            {
                case KeyCode.BackQuote: return UnityEngine.InputSystem.Key.Backquote;
                case KeyCode.Insert: return UnityEngine.InputSystem.Key.Insert;
                case KeyCode.Home: return UnityEngine.InputSystem.Key.Home;
                case KeyCode.End: return UnityEngine.InputSystem.Key.End;
                case KeyCode.PageUp: return UnityEngine.InputSystem.Key.PageUp;
                case KeyCode.PageDown: return UnityEngine.InputSystem.Key.PageDown;
                case KeyCode.Pause: return UnityEngine.InputSystem.Key.Pause;
                case KeyCode.ScrollLock: return UnityEngine.InputSystem.Key.ScrollLock;
            }
            if (code >= KeyCode.A && code <= KeyCode.Z)
                return UnityEngine.InputSystem.Key.A + (code - KeyCode.A);
            return UnityEngine.InputSystem.Key.None;
        }
#endif

        private void OnApplicationPause(bool paused)
        {
            if (paused) _view?.PersistSession();
        }

        private void OnApplicationQuit() => _view?.PersistSession();

        private void OnDestroy()
        {
            _apply?.Dispose();
            _apply = null;
            if (_view != null)
            {
                _view.Dispose();
                _view.RemoveFromHierarchy();
                _view = null;
            }
            if (_manageCursor && _visible)
            {
                UnityEngine.Cursor.visible = _prevCursorVisible;
                UnityEngine.Cursor.lockState = _prevLock;
            }
            if (_ownsDocument && _document != null) Destroy(_document);
        }
    }
}
