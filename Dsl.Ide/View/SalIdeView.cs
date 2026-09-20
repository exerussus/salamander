using System;
using System.Collections.Generic;
using System.IO;
using Dsl.Compilation;
using Dsl.Text;
using Dsl.Tooling;
using UnityEngine;
using UnityEngine.UIElements;

namespace Dsl.Ide
{
    /// <summary>Всё, с чем создаётся IDE. Обязателен только Host (по умолчанию — игровой).</summary>
    public sealed class IdeOptions
    {
        public IIdeHost Host;
        public IIdeStorage Storage;
        public IScriptWorkspace Workspace;
        public IApiSource Api;
        public IIdeApplyTarget ApplyTarget;
        /// <summary>USS IDE; null — из Resources (SalamanderIde/SalIde).</summary>
        public StyleSheet StyleSheet;
        /// <summary>Моноширинный шрифт кода; null — системный (на WebGL/мобильных задайте обязательно).</summary>
        public FontDefinition? CodeFont;
        public bool ShowToolbar = true;
        /// <summary>Восстановить вкладки и несохранённые буферы прошлой сессии.</summary>
        public bool RestoreSession = true;
    }

    /// <summary>
    /// IDE Salamander целиком: дерево модулей и структура файла, вкладки, редактор
    /// кода, проблемы и консоль, строка статуса. Один и тот же VisualElement
    /// работает в игре (UIDocument) и в редакторе (страница Nexus) — различается
    /// только хост (<see cref="IIdeHost"/>), хранилище и воркспейс.
    ///
    /// Компиляция — всего воркспейса с наложенными несохранёнными буферами (как в
    /// игре и LSP), поэтому ссылки между файлами и модулями не дают ложных ошибок.
    /// Подсказки — общий языковой сервис Dsl.Tooling (тот же, что в VS Code/Rider).
    /// </summary>
    public sealed class SalIdeView : VisualElement, IDisposable, ICodeAssist
    {
        private const double PollSeconds = 2.0;
        private const double SessionDelaySeconds = 1.5;

        private IIdeHost _host;
        private readonly IIdeStorage _storage;
        private IScriptWorkspace _workspace;
        private IApiSource _api;
        private IIdeApplyTarget _apply;

        private readonly LanguageService _ls = new LanguageService();
        private readonly CompileRunner _runner = new CompileRunner();
        private readonly List<IdeDocument> _docs = new List<IdeDocument>();
        private IdeDocument _active;
        private WorkspaceModules _snapshot = new WorkspaceModules();
        private long _changeToken;
        private IdeSessionState _session;
        private readonly HashSet<string> _bufferKeys = new HashSet<string>(StringComparer.Ordinal);

        private IVisualElementScheduledItem _tick;
        private bool _activeState = true;
        private double _nextPoll;
        private double _sessionDueAt = -1;
        private double _lastCompileMs;
        private bool _disposed;

        // ── UI ──
        private readonly IdeChrome _chrome = new IdeChrome();
        private readonly VisualElement _toolbar;
        private readonly VisualElement _toolbarExtras;
        private readonly Button _applyButton;
        private readonly TwoPaneSplitView _mainSplit;
        private readonly TwoPaneSplitView _centerSplit;
        private readonly VisualElement _sidebar;
        private readonly Button _sideFilesTab, _sideOutlineTab;
        private readonly ExplorerPanel _explorer;
        private readonly OutlinePanel _outline;
        private readonly TabStrip _tabs;
        private readonly VisualElement _banner;
        private readonly Label _bannerText;
        private readonly VisualElement _bannerButtons;
        private readonly SalCodeEditor _editor;
        private readonly VisualElement _emptyState;
        private readonly VisualElement _bottom;
        private readonly Button _problemsTab, _consoleTab;
        private readonly VisualElement _problemsFilters;
        private readonly ProblemsPanel _problems;
        private readonly ConsolePanel _console;
        private readonly Label _statusLeft, _statusMid, _statusRight;
        private readonly IdeDialog _dialog;
        private string _statusText;
        private IdeStatusKind _statusKind;

        /// <summary>Статус IDE (для хоста, у которого своя строка статуса).</summary>
        public event Action<string, IdeStatusKind> StatusChanged;

        public IIdeHost Host => _host;
        public IScriptWorkspace Workspace => _workspace;
        public IReadOnlyList<IdeDocument> Documents => _docs;
        public IdeDocument ActiveDocument => _active;
        public SalCodeEditor Editor => _editor;
        public IdeChrome Chrome => _chrome;

        /// <summary>Куда хост добавляет свои кнопки (открыть внешний файл, выбрать манифест…).</summary>
        public VisualElement ToolbarExtras => _toolbarExtras;

        public bool HasUnsavedChanges
        {
            get
            {
                foreach (var d in _docs) if (d.Dirty) return true;
                return false;
            }
        }

        public SalIdeView(IdeOptions options)
        {
            options ??= new IdeOptions();
            _host = options.Host ?? new DefaultIdeHost();
            _storage = options.Storage ?? new MemoryIdeStorage();
            _workspace = options.Workspace ?? new MemoryWorkspace();
            _api = options.Api ?? new FixedApiSource(ApiModel.Empty);
            _apply = options.ApplyTarget;

            AddToClassList("sal-ide");
            var uss = options.StyleSheet != null ? options.StyleSheet : Resources.Load<StyleSheet>("SalamanderIde/SalIde");
            if (uss != null) styleSheets.Add(uss);
            else _host.Log(IdeLogLevel.Warning, "ui", "SalIde.uss не найден (Resources/SalamanderIde/SalIde) — IDE без оформления");
            _chrome.Register(this, ChromeSlot.Background, ChromeSlot.Text);

            // ── тулбар ──
            _toolbar = new VisualElement();
            _toolbar.AddToClassList("sal-ide-toolbar");
            _chrome.Register(_toolbar, ChromeSlot.Panel, ChromeSlot.Text, ChromeSlot.Border);
            _toolbar.Add(ToolButton("Новый", "Новый файл в модуле (Ctrl+N)", () => NewFile(null)));
            _toolbar.Add(ToolButton("Сохранить", "Сохранить (Ctrl+S)", () => Save(_active), primary: true));
            _toolbar.Add(ToolButton("Всё", "Сохранить все (Ctrl+Shift+S)", SaveAll));
            _toolbar.Add(Separator());
            _toolbar.Add(ToolButton("Компилировать", "Компилировать воркспейс (F7, Ctrl+B)", Compile));
            _applyButton = ToolButton("Применить", "Сохранить и перезагрузить скрипты в игре", Apply, primary: true);
            _toolbar.Add(_applyButton);
            _toolbar.Add(Separator());
            _toolbar.Add(ToolButton("Найти", "Поиск (Ctrl+F), замена (Ctrl+H)", () => _editor.ShowFind(false)));
            _toolbar.Add(ToolButton("Строка…", "Перейти к строке (Ctrl+G)", GoToLinePrompt));
            _toolbarExtras = new VisualElement();
            _toolbarExtras.AddToClassList("sal-ide-toolbar-extras");
            _toolbar.Add(_toolbarExtras);
            var spacer = new VisualElement { style = { flexGrow = 1 } };
            _toolbar.Add(spacer);
            _toolbar.Add(ToolButton("Файлы", "Показать/скрыть панель файлов", ToggleSidebar));
            _toolbar.Add(ToolButton("Низ", "Показать/скрыть проблемы и консоль", ToggleBottom));
            _toolbar.style.display = options.ShowToolbar ? DisplayStyle.Flex : DisplayStyle.None;
            Add(_toolbar);

            // ── боковая панель ──
            _sidebar = new VisualElement();
            _sidebar.AddToClassList("sal-sidebar");
            _chrome.Register(_sidebar, ChromeSlot.Panel, ChromeSlot.Text, ChromeSlot.Border);
            var sideHeader = new VisualElement();
            sideHeader.AddToClassList("sal-panel-header");
            _sideFilesTab = HeaderTab("Файлы", () => ShowSidebarTab("files"));
            _sideOutlineTab = HeaderTab("Структура", () => ShowSidebarTab("outline"));
            sideHeader.Add(_sideFilesTab);
            sideHeader.Add(_sideOutlineTab);
            sideHeader.Add(new VisualElement { style = { flexGrow = 1 } });
            sideHeader.Add(SmallButton("Обновить", "Перечитать модули с диска", () => ReloadWorkspace(true)));
            _sidebar.Add(sideHeader);
            _explorer = new ExplorerPanel(_chrome);
            _explorer.OpenRequested += key => OpenFile(key);
            _explorer.NewFileRequested += NewFile;
            _sidebar.Add(_explorer);
            _outline = new OutlinePanel(_chrome);
            _outline.GoToRequested += (l, c) => { _editor.GoTo(l, c); _editor.FocusCode(); };
            _sidebar.Add(_outline);

            // ── центр: вкладки + баннер + редактор ──
            var center = new VisualElement();
            center.AddToClassList("sal-center");
            _tabs = new TabStrip(_chrome);
            _tabs.Selected += Activate;
            _tabs.CloseRequested += doc => Close(doc, null);
            center.Add(_tabs);

            _banner = new VisualElement();
            _banner.AddToClassList("sal-banner");
            _bannerText = new Label();
            _bannerText.AddToClassList("sal-banner-text");
            _banner.Add(_bannerText);
            _bannerButtons = new VisualElement();
            _bannerButtons.AddToClassList("sal-banner-buttons");
            _banner.Add(_bannerButtons);
            _banner.style.display = DisplayStyle.None;
            center.Add(_banner);

            var editorHost = new VisualElement();
            editorHost.AddToClassList("sal-editor-host");
            _editor = new SalCodeEditor(null, options.CodeFont) { Assist = this };
            _editor.TextEdited += OnEditorText;
            _editor.CaretMoved += UpdateStatusLeft;
            _editor.Command += OnEditorCommand;
            editorHost.Add(_editor);
            _emptyState = new VisualElement();
            _emptyState.AddToClassList("sal-empty-state");
            _emptyState.Add(new Label("Нет открытых файлов.\nВыберите файл слева или создайте новый (Ctrl+N)."));
            editorHost.Add(_emptyState);
            center.Add(editorHost);

            // ── низ: проблемы / консоль ──
            _bottom = new VisualElement();
            _bottom.AddToClassList("sal-bottom");
            _chrome.Register(_bottom, ChromeSlot.Panel, ChromeSlot.Text, ChromeSlot.Border);
            var bottomHeader = new VisualElement();
            bottomHeader.AddToClassList("sal-panel-header");
            _problemsTab = HeaderTab("Проблемы", () => ShowBottomTab("problems"));
            _consoleTab = HeaderTab("Консоль", () => ShowBottomTab("console"));
            bottomHeader.Add(_problemsTab);
            bottomHeader.Add(_consoleTab);
            bottomHeader.Add(new VisualElement { style = { flexGrow = 1 } });
            _problemsFilters = new VisualElement();
            _problemsFilters.AddToClassList("sal-filters");
            bottomHeader.Add(_problemsFilters);
            bottomHeader.Add(SmallButton("Очистить", "Очистить консоль", () => _console.ClearLines()));
            _bottom.Add(bottomHeader);
            _problems = new ProblemsPanel();
            _problems.Activated += OnProblem;
            _bottom.Add(_problems);
            _console = new ConsolePanel();
            _bottom.Add(_console);

            // ── раскладка ──
            _session = options.RestoreSession ? IdeSession.Load(_storage) : new IdeSessionState();
            _centerSplit = new TwoPaneSplitView(1, Mathf.Max(80f, _session.BottomHeight), TwoPaneSplitViewOrientation.Vertical);
            _centerSplit.AddToClassList("sal-split");
            _centerSplit.Add(center);
            _centerSplit.Add(_bottom);
            _mainSplit = new TwoPaneSplitView(0, Mathf.Max(120f, _session.SidebarWidth), TwoPaneSplitViewOrientation.Horizontal);
            _mainSplit.AddToClassList("sal-split");
            _mainSplit.Add(_sidebar);
            _mainSplit.Add(_centerSplit);
            Add(_mainSplit);

            // ── статус ──
            var status = new VisualElement();
            status.AddToClassList("sal-status");
            _chrome.Register(status, ChromeSlot.Panel, ChromeSlot.TextDim, ChromeSlot.Border);
            _statusLeft = new Label();
            _statusLeft.AddToClassList("sal-status-item");
            _statusMid = new Label();
            _statusMid.AddToClassList("sal-status-item");
            _statusMid.AddToClassList("sal-status-message");
            _statusRight = new Label();
            _statusRight.AddToClassList("sal-status-item");
            _statusRight.AddToClassList("sal-status-right");
            status.Add(_statusLeft);
            status.Add(_statusMid);
            status.Add(_statusRight);
            Add(status);

            _dialog = new IdeDialog(_chrome);
            Add(_dialog);

            // ── компиляция ──
            _runner.UseThreads = _host.SupportsThreads;
            _runner.Snapshot = SnapshotCompile;
            _runner.Apply = ApplyCompile;
            _runner.Report = SetStatus;
            _runner.AbortThread = t => _host.TryAbortThread(t);

            _ls.TextProvider = GetText;
            _ls.Api = _api.Current?.Manifest;   // источники грузятся в своих конструкторах — Poll уже ничего не вернёт

            BuildFilters();
            ShowSidebarTab(_session.SidebarTab ?? "files");
            ShowBottomTab(_session.BottomTab ?? "problems");
            _problems.ShowErrors = _session.ShowErrors;
            _problems.ShowWarnings = _session.ShowWarnings;
            _problems.ShowInfos = _session.ShowInfos;
            _problems.CurrentOnly = _session.ProblemsCurrentOnly;
            RefreshApplyButton();

            ReloadWorkspace(false);
            if (options.RestoreSession) RestoreSession();
            RefreshAll();

            RegisterCallback<AttachToPanelEvent>(_ => { if (_activeState) StartTick(); });
            RegisterCallback<DetachFromPanelEvent>(_ => StopTick());
            RegisterCallback<KeyDownEvent>(OnViewKey);
            schedule.Execute(() =>
            {
                if (!_session.SidebarVisible) _mainSplit.CollapseChild(0);
                if (!_session.BottomVisible) _centerSplit.CollapseChild(1);
            });
            RequestCompile(immediate: true);
        }

        // ═══════════════════════════ публичный API хоста ═══════════════════════════

        public void SetHost(IIdeHost host)
        {
            _host = host ?? new DefaultIdeHost();
            _runner.UseThreads = _host.SupportsThreads;
        }

        public void SetWorkspace(IScriptWorkspace workspace)
        {
            if (!ReferenceEquals(workspace, _workspace)) (_workspace as IDisposable)?.Dispose();
            _workspace = workspace ?? new MemoryWorkspace();
            ReloadWorkspace(true);
        }

        public void SetApiSource(IApiSource api)
        {
            _api = api ?? new FixedApiSource(ApiModel.Empty);
            OnApiChanged();
        }

        public void SetApplyTarget(IIdeApplyTarget target)
        {
            _apply = target;
            RefreshApplyButton();
        }

        /// <summary>Навязать цвета оболочки (страница Nexus — токены своей палитры).</summary>
        public void ApplyChrome(IdeChromeColors colors) => _chrome.Apply(colors);

        /// <summary>Строка в консоль IDE (логи скриптов, итог перезагрузки).</summary>
        public void Log(IdeLogKind kind, string text) => _console.Append(kind, text);

        /// <summary>Приостановить/возобновить всю периодическую работу (вкладка/окно неактивны).</summary>
        public void SetActive(bool active)
        {
            _activeState = active;
            _editor.SetActive(active);
            if (active && panel != null) { StartTick(); PollNow(); }
            else StopTick();
        }

        /// <summary>Открыть файл по пути (или ключу воркспейса) и, по желанию, встать на строку.</summary>
        public bool OpenFile(string pathOrKey, int line1 = 0, int col1 = 0)
        {
            string key = _workspace.NormalizeKey(pathOrKey);
            if (key == null) { SetStatus("Не удалось открыть: " + pathOrKey, IdeStatusKind.Error); return false; }
            var doc = _docs.Find(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            if (doc == null)
            {
                string text = ReadKey(key);
                if (text == null) { SetStatus("Файл не найден: " + pathOrKey, IdeStatusKind.Error); return false; }
                doc = new IdeDocument(key, DisplayNameOf(key)) { Logical = LogicalOf(key) };
                doc.LoadFromDisk(text, _workspace.GetStamp(key));
                _docs.Add(doc);
                _ls.Index.Update(key, doc.Text);
            }
            Activate(doc);
            if (line1 > 0) _editor.GoTo(line1, Math.Max(1, col1));
            RequestCompile(immediate: true);
            ScheduleSession();
            return true;
        }

        public bool Save(IdeDocument doc) => Save(doc, force: false, then: null);

        public void SaveAll()
        {
            int saved = 0;
            foreach (var d in _docs.ToArray())
                if (d.Dirty && Save(d, false, null)) saved++;
            SetStatus(saved == 0 ? "Нечего сохранять." : $"Сохранено файлов: {saved}.", IdeStatusKind.Ok);
        }

        /// <summary>Отбросить все несохранённые правки (хост решил закрыть без сохранения).</summary>
        public void DiscardAll()
        {
            foreach (var d in _docs)
            {
                string disk = ReadKey(d.Key);
                if (disk != null) d.LoadFromDisk(disk, _workspace.GetStamp(d.Key));
            }
            if (_active != null) BindEditor(_active);
            RefreshTabs();
            PersistSession();
        }

        /// <summary>Явный Refresh: перечитать модули и API, перекомпилировать.</summary>
        public void Refresh()
        {
            ReloadWorkspace(false);
            PollNow();
            Compile();
        }

        /// <summary>
        /// Перед перезагрузкой домена/выходом: записать сессию и бросить (или, если
        /// хост умеет, прервать) фоновую компиляцию — живой поток в пользовательской
        /// сборке не даёт Unity выгрузить домен.
        /// </summary>
        public void PrepareForShutdown()
        {
            PersistSession();
            _runner.AbortInflight();
        }

        public void Compile()
        {
            _runner.ForceRequest(Now);
            SetStatus("Компиляция…", IdeStatusKind.Info);
        }

        /// <summary>Записать сессию и бэкапы буферов сейчас (перед перезагрузкой домена, выходом).</summary>
        public void PersistSession()
        {
            if (_disposed) return;
            _sessionDueAt = -1;
            if (_active != null) CaptureEditorState(_active);
            var st = _session ?? new IdeSessionState();
            st.Docs.Clear();
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in _docs)
            {
                var sd = new IdeSessionDoc
                {
                    Key = d.Key,
                    Caret = d.CaretIndex,
                    Select = d.SelectIndex,
                    ScrollX = d.ScrollX,
                    ScrollY = d.ScrollY,
                };
                if (d.Dirty)
                {
                    sd.Buffer = IdeSession.BufferKey(d.Key);
                    sd.BaseHash = IdeSession.Hash(d.SavedText);
                    _storage.Write(sd.Buffer, d.Text);
                    live.Add(sd.Buffer);
                }
                st.Docs.Add(sd);
            }
            // бэкапы, ставшие ненужными (файл сохранён/закрыт), — удалить
            foreach (var k in _bufferKeys) if (!live.Contains(k)) _storage.Delete(k);
            _bufferKeys.Clear();
            foreach (var k in live) _bufferKeys.Add(k);

            st.Active = _active?.Key;
            if (_mainSplit.fixedPane != null && _mainSplit.fixedPane.layout.width > 10f) st.SidebarWidth = _mainSplit.fixedPane.layout.width;
            if (_centerSplit.fixedPane != null && _centerSplit.fixedPane.layout.height > 10f) st.BottomHeight = _centerSplit.fixedPane.layout.height;
            st.ShowErrors = _problems.ShowErrors;
            st.ShowWarnings = _problems.ShowWarnings;
            st.ShowInfos = _problems.ShowInfos;
            st.ProblemsCurrentOnly = _problems.CurrentOnly;
            st.WorkspaceRoot = _workspace?.Root;
            if (_api is ManifestFileApiSource mf) st.ApiManifestPath = mf.Path;
            _session = st;
            IdeSession.Save(_storage, st);
        }

        public void Dispose()
        {
            if (_disposed) return;
            PersistSession();
            _disposed = true;
            StopTick();
            _runner.Dispose();
            _editor.Dispose();
            (_workspace as IDisposable)?.Dispose();
        }

        // ═══════════════════════════ документы ═══════════════════════════

        private void Activate(IdeDocument doc)
        {
            if (doc == null) return;
            if (doc == _active) { _editor.FocusCode(); return; } // повторный клик по вкладке — не сбрасывать каретку и прокрутку
            if (_active != null) CaptureEditorState(_active);
            _active = doc;
            BindEditor(doc);
            RefreshTabs();
            RefreshExplorer();
            RefreshOutline();
            RefreshBanner();
            _problems.SetCurrent(doc.Key);
            UpdateStatusLeft();
            ScheduleSession();
        }

        private void BindEditor(IdeDocument doc, bool focus = true)
        {
            _editor.SetHistory(doc.History);
            _editor.Load(doc.Text, doc.CaretIndex, doc.SelectIndex, new Vector2(doc.ScrollX, doc.ScrollY));
            _editor.ReadOnly = false;
            if (doc.DiagnosticsVersion == doc.Version) _editor.SetDiagnostics(doc.Diagnostics);
            _editor.style.display = DisplayStyle.Flex;
            _emptyState.style.display = DisplayStyle.None;
            if (focus) _editor.FocusCode();
        }

        private void CaptureEditorState(IdeDocument doc)
        {
            doc.CaretIndex = _editor.CaretIndex;
            doc.SelectIndex = _editor.SelectIndex;
            var s = _editor.ScrollOffset;
            doc.ScrollX = s.x;
            doc.ScrollY = s.y;
        }

        private void OnEditorText(string text)
        {
            if (_active == null) return;
            bool wasDirty = _active.Dirty;
            _active.SetText(text);
            if (wasDirty != _active.Dirty) _tabs.RefreshDirty(_docs);
            RequestCompile(immediate: false);
            ScheduleSession();
        }

        /// <summary>Закрыть вкладку; при несохранённых правках — спросить. done(true) — закрыта.</summary>
        public void Close(IdeDocument doc, Action<bool> done)
        {
            if (doc == null) { done?.Invoke(true); return; }
            if (doc == _active) CaptureEditorState(doc);
            if (!doc.Dirty) { CloseNow(doc); done?.Invoke(true); return; }
            _dialog.Ask("Несохранённые изменения", $"В «{doc.DisplayName}» есть несохранённые изменения.",
                new[] { "Сохранить", "Не сохранять", "Отмена" }, 0, 2, r =>
                {
                    if (r == 2) { done?.Invoke(false); return; }
                    if (r == 0)
                    {
                        // сохранение могло не состояться (ошибка записи, отказ перезаписать) — тогда вкладка остаётся
                        Save(doc, false, ok => { if (ok) CloseNow(doc); done?.Invoke(ok); });
                        return;
                    }
                    CloseNow(doc);
                    done?.Invoke(true);
                });
        }

        private void CloseNow(IdeDocument doc)
        {
            int i = _docs.IndexOf(doc);
            if (i < 0) return;
            _docs.RemoveAt(i);
            // вне модулей файл в индексе не нужен
            if (doc.Logical == null) _ls.Index.Remove(doc.Key);
            if (_active == doc)
            {
                _active = null;
                if (_docs.Count > 0) Activate(_docs[Math.Min(i, _docs.Count - 1)]);
                else ShowEmpty();
            }
            RefreshTabs();
            PersistSession();
            RequestCompile(immediate: true);
        }

        private void ShowEmpty()
        {
            _editor.style.display = DisplayStyle.None;
            _emptyState.style.display = DisplayStyle.Flex;
            RefreshOutline();
            RefreshBanner();
            UpdateStatusLeft();
        }

        private bool Save(IdeDocument doc, bool force, Action<bool> then)
        {
            if (doc == null) { then?.Invoke(false); return false; }
            // файл поменялся на диске с момента чтения — не затирать молча
            if (!force && (doc.HasConflict || ExternalChanged(doc, out _)))
            {
                doc.ConflictText ??= ReadKey(doc.Key) ?? "";
                RefreshBanner();
                _dialog.Ask("Файл изменён на диске",
                    $"«{doc.DisplayName}» изменён другой программой после открытия. Перезаписать его вашей версией?",
                    new[] { "Перезаписать", "Отмена" }, 1, 1,
                    r => { if (r == 0) Save(doc, true, then); else then?.Invoke(false); });
                return false;
            }
            if (!_workspace.Write(doc.Key, doc.TextForDisk(), out string err))
            {
                SetStatus($"Не удалось сохранить {doc.DisplayName}: {err}", IdeStatusKind.Error);
                then?.Invoke(false);
                return false;
            }
            doc.MarkSaved(_workspace.GetStamp(doc.Key));
            string path = _workspace.FilePathOf(doc.Key);
            if (path != null)
            {
                try { _host.OnFileSaved(path); }
                catch (Exception e) { _host.Log(IdeLogLevel.Warning, "save", "хост не обработал сохранение", e); }
            }
            RefreshTabs();
            RefreshBanner();
            SetStatus("Сохранено: " + doc.DisplayName, IdeStatusKind.Ok);
            PersistSession();
            RequestCompile(immediate: true);
            then?.Invoke(true);
            return true;
        }

        private bool ExternalChanged(IdeDocument doc, out string disk)
        {
            disk = null;
            var st = _workspace.GetStamp(doc.Key);
            if (st.IsNone && doc.DiskStamp.IsNone) return false; // не файловый воркспейс или новый файл
            if (st.Equals(doc.DiskStamp)) return false;
            disk = ReadKey(doc.Key);
            if (disk == null) return false; // файл удалён — сохранение его просто воссоздаст
            return !string.Equals(IdeDocument.Normalize(disk), doc.SavedText, StringComparison.Ordinal);
        }

        /// <summary>Новый файл в модуле: имя спрашивается в диалоге.</summary>
        public void NewFile(string module)
        {
            if (!_workspace.CanCreateFiles) { SetStatus("Этот воркспейс не умеет создавать файлы.", IdeStatusKind.Warning); return; }
            if (_snapshot.Modules.Count == 0) { SetStatus("Нет модулей: создайте папку с module.json.", IdeStatusKind.Warning); return; }
            module ??= _active?.Logical != null ? _active.Logical.Split('/')[0] : _snapshot.Modules[0].Manifest?.Name;
            _dialog.Prompt("Новый файл", $"Модуль «{module}». Путь внутри модуля (например src/new.sal):", "src/new.sal", rel =>
            {
                if (string.IsNullOrWhiteSpace(rel)) return;
                string key = _workspace.CreateFile(module, rel, out string err);
                if (key == null) { SetStatus("Файл не создан: " + err, IdeStatusKind.Error); return; }
                if (err != null) SetStatus(err, IdeStatusKind.Warning);
                ReloadWorkspace(true);
                OpenFile(key);
            });
        }

        private void GoToLinePrompt()
        {
            if (_active == null) return;
            _dialog.Prompt("Перейти к строке", "Строка[:колонка]", "", s =>
            {
                if (string.IsNullOrWhiteSpace(s)) return;
                var parts = s.Split(':');
                if (int.TryParse(parts[0].Trim(), out int line))
                {
                    int col = parts.Length > 1 && int.TryParse(parts[1].Trim(), out int c) ? c : 1;
                    _editor.GoTo(line, col);
                }
            });
        }

        private void OnEditorCommand(EditorCommand cmd)
        {
            switch (cmd)
            {
                case EditorCommand.Save: Save(_active); break;
                case EditorCommand.SaveAll: SaveAll(); break;
                case EditorCommand.GoToLine: GoToLinePrompt(); break;
                case EditorCommand.Compile: Compile(); break;
                case EditorCommand.NewFile: NewFile(null); break;
            }
        }

        private void OnViewKey(KeyDownEvent e)
        {
            // команды, пока фокус вне кода (дерево, проблемы)
            bool mod = e.actionKey || e.ctrlKey || e.commandKey;
            if (mod && e.keyCode == KeyCode.S) { if (e.shiftKey) SaveAll(); else Save(_active); e.StopPropagation(); }
            else if (e.keyCode == KeyCode.F7) { Compile(); e.StopPropagation(); }
        }

        private void Apply()
        {
            if (_apply == null) return;
            if (!_apply.CanApply)
            {
                SetStatus("Применять некуда: движок скриптов не запущен.", IdeStatusKind.Warning);
                RefreshApplyButton();
                return;
            }
            SaveAll();
            if (HasUnsavedChanges)
            {
                SetStatus("Сначала сохраните изменения — применять нечего.", IdeStatusKind.Warning);
                return;
            }
            try
            {
                _apply.Apply();
                SetStatus("Скрипты отправлены на перезагрузку.", IdeStatusKind.Info);
            }
            catch (Exception e)
            {
                SetStatus("Не удалось применить: " + e.Message, IdeStatusKind.Error);
            }
        }

        // ═══════════════════════════ воркспейс ═══════════════════════════

        private void ReloadWorkspace(bool announce)
        {
            try
            {
                _snapshot = _workspace.Load() ?? new WorkspaceModules();
                _changeToken = _workspace.GetChangeToken();
            }
            catch (Exception e)
            {
                _snapshot = new WorkspaceModules();
                _host.Log(IdeLogLevel.Error, "workspace", "модули не загрузились", e);
            }
            // логические имена открытых документов — по новому снимку
            foreach (var d in _docs) d.Logical = LogicalOf(d.Key);
            RebuildIndex();
            RefreshExplorer();
            RequestCompile(immediate: true);
            if (announce) SetStatus($"Модули перечитаны: {_snapshot.Modules.Count}.", IdeStatusKind.Info);
        }

        private void RebuildIndex()
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var set in _snapshot.Modules)
                foreach (var (logical, text) in set.Files)
                {
                    if (!_snapshot.LogicalToPath.TryGetValue(logical, out var key)) continue;
                    keep.Add(key);
                    var open = FindDoc(key);
                    _ls.Index.Update(key, open != null ? open.Text : text);
                }
            foreach (var d in _docs)
            {
                keep.Add(d.Key);
                _ls.Index.Update(d.Key, d.Text);
            }
            _ls.Index.RetainOnly(keep);
            _editor.SetClassifier(_ls.CreateClassifier());
        }

        private IdeDocument FindDoc(string key) =>
            _docs.Find(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

        private string LogicalOf(string key)
        {
            foreach (var kv in _snapshot.LogicalToPath)
                if (string.Equals(kv.Value, key, StringComparison.OrdinalIgnoreCase)) return kv.Key;
            return null;
        }

        private string ReadKey(string key)
        {
            string t = _workspace.Read(key);
            if (t != null) return t;
            // файл вне воркспейса (открыт по пути) — читаем с диска напрямую
            try { return File.Exists(key) ? File.ReadAllText(key) : null; }
            catch { return null; }
        }

        private string DisplayNameOf(string key)
        {
            try { return _workspace.DisplayNameOf(key); }
            catch { return key; }
        }

        private string GetText(string key)
        {
            var d = FindDoc(key);
            if (d != null) return d.Text;
            return ReadKey(key);
        }

        // ═══════════════════════════ периодическое ═══════════════════════════

        private static double Now => Time.realtimeSinceStartupAsDouble;

        private void StartTick()
        {
            if (_disposed) return;
            if (_tick != null) { _tick.Resume(); return; }
            _tick = schedule.Execute(Tick).Every(100);
        }

        private void StopTick() => _tick?.Pause();

        private void Tick()
        {
            if (_disposed || !_activeState) return;
            double now = Now;
            _runner.Tick(now);
            if (now >= _nextPoll) { _nextPoll = now + PollSeconds; PollNow(); }
            if (_sessionDueAt > 0 && now >= _sessionDueAt) PersistSession();
        }

        private void PollNow()
        {
            // API хоста поменялся (манифест переэкспортирован, игра перерегистрировала API)
            try { if (_api.Poll()) OnApiChanged(); }
            catch (Exception e) { _host.Log(IdeLogLevel.Warning, "api", "опрос манифеста", e); }

            // набор модулей/файлов поменялся извне (git, другой редактор, игра)
            long token;
            try { token = _workspace.GetChangeToken(); }
            catch { token = _changeToken; }
            if (token != _changeToken) ReloadWorkspace(false);

            // открытые файлы, изменённые на диске
            foreach (var d in _docs.ToArray()) CheckExternal(d);

            // движок мог подняться или умереть (сцена перезагрузилась, Play Mode)
            RefreshApplyButton();
        }

        private void CheckExternal(IdeDocument d)
        {
            var st = _workspace.GetStamp(d.Key);
            if (st.IsNone || st.Equals(d.DiskStamp)) return;
            string disk = ReadKey(d.Key);
            if (disk == null) return;
            string norm = IdeDocument.Normalize(disk);
            if (string.Equals(norm, d.SavedText, StringComparison.Ordinal)) { d.DiskStamp = st; return; }
            if (!d.Dirty)
            {
                // правок нет — тихо перечитать, сохранив каретку
                CaptureIfActive(d);
                int caret = d.CaretIndex, sel = d.SelectIndex;
                var scroll = new Vector2(d.ScrollX, d.ScrollY);
                d.LoadFromDisk(disk, st);
                d.CaretIndex = Math.Min(caret, d.Text.Length);
                d.SelectIndex = Math.Min(sel, d.Text.Length);
                d.ScrollX = scroll.x;
                d.ScrollY = scroll.y;
                // перечитали по опросу — фокус не забираем (его мог держать поиск, диалог, игра)
                if (d == _active) BindEditor(d, focus: _editor.HasFocus);
                _ls.Index.Update(d.Key, d.Text);
                SetStatus($"«{d.DisplayName}» обновлён с диска.", IdeStatusKind.Info);
                RequestCompile(immediate: true);
            }
            else
            {
                d.ConflictText = disk;   // сырой текст: у файла может быть CRLF
                d.DiskStamp = st;
                SetStatus($"«{d.DisplayName}» изменён на диске, а у вас несохранённые правки.", IdeStatusKind.Warning);
            }
            RefreshTabs();
            RefreshBanner();
        }

        private void CaptureIfActive(IdeDocument d)
        {
            if (d == _active) CaptureEditorState(d);
        }

        private void OnApiChanged()
        {
            _ls.Api = _api.Current?.Manifest;
            _editor.SetClassifier(_ls.CreateClassifier());
            UpdateStatusRight();
            if (_api.Error != null) SetStatus(_api.Error, IdeStatusKind.Warning);
            RequestCompile(immediate: true);
        }

        private void ScheduleSession()
        {
            if (_sessionDueAt < 0) _sessionDueAt = Now + SessionDelaySeconds;
        }

        // ═══════════════════════════ компиляция ═══════════════════════════

        private void RequestCompile(bool immediate) => _runner.Request(Now, immediate);

        private CompileJob SnapshotCompile()
        {
            var model = _api.Current ?? ApiModel.Empty;
            var job = new CompileJob
            {
                Registry = model.Registry,
                ApiVersion = model.ApiVersion,
                DocVersions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                LogicalToKey = new Dictionary<string, string>(StringComparer.Ordinal),
            };
            foreach (var d in _docs) job.DocVersions[d.Key] = d.Version;
            foreach (var kv in _snapshot.LogicalToPath) job.LogicalToKey[kv.Key] = kv.Value;

            // модули воркспейса с несохранёнными буферами поверх диска
            var modules = WorkspaceCompiler.WithOverlays(_snapshot, key => FindDoc(key)?.Text);

            // открытые файлы вне модулей: в свой модуль (как лишний файл) или отдельным модулем
            foreach (var d in _docs)
            {
                if (d.Logical != null) continue;
                AddLooseDocument(d, modules, job);
            }
            job.Modules = modules;
            return job;
        }

        private void AddLooseDocument(IdeDocument d, List<ModuleSourceSet> modules, CompileJob job)
        {
            string path = _workspace.FilePathOf(d.Key);
            string moduleDir = path != null ? WorkspaceLoader.FindModuleDirOf(path) : null;
            if (moduleDir != null)
            {
                // модуль уже в воркспейсе — файл добавляется к нему лишним (в игре он не компилируется,
                // но проверить его полезно; предупреждение об этом — в баннере)
                foreach (var kv in _snapshot.ModuleDirs)
                {
                    if (!string.Equals(Path.GetFullPath(kv.Value), Path.GetFullPath(moduleDir), StringComparison.OrdinalIgnoreCase)) continue;
                    var set = modules.Find(m => m.Manifest?.Name == kv.Key);
                    if (set == null) break;
                    string logical = kv.Key + "/" + Relative(moduleDir, path);
                    set.Files.Add((logical, d.Text));
                    job.LogicalToKey[logical] = d.Key;
                    return;
                }
                // модуль вне воркспейса — грузим его с диска целиком, этот файл — из буфера
                var map = new Dictionary<string, string>();
                var loaded = ModuleLoader.LoadModuleDir(moduleDir, null, map);
                if (loaded != null && modules.Find(m => m.Manifest?.Name == loaded.Manifest?.Name) == null)
                {
                    bool found = false;
                    for (int i = 0; i < loaded.Files.Count; i++)
                    {
                        var (logical, _) = loaded.Files[i];
                        if (map.TryGetValue(logical, out var abs) &&
                            string.Equals(Path.GetFullPath(abs), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                        {
                            loaded.Files[i] = (logical, d.Text);
                            job.LogicalToKey[logical] = d.Key;
                            found = true;
                        }
                        else if (abs != null) job.LogicalToKey[logical] = Path.GetFullPath(abs);
                    }
                    if (!found)
                    {
                        string logical = (loaded.Manifest?.Name ?? "module") + "/" + Relative(moduleDir, path);
                        loaded.Files.Add((logical, d.Text));
                        job.LogicalToKey[logical] = d.Key;
                    }
                    modules.Add(loaded);
                    return;
                }
            }
            // совсем одиночный файл: свой модуль "editor" (кооперативный)
            string name = "editor_" + Math.Abs(IdeSession.Hash(d.Key).GetHashCode() % 100000);
            var single = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = name, ApiVersion = job.ApiVersion, Execution = "cooperative" },
            };
            string lg = name + "/" + d.DisplayName;
            single.Files.Add((lg, d.Text));
            job.LogicalToKey[lg] = d.Key;
            modules.Add(single);
        }

        private static string Relative(string dir, string path)
        {
            string d = Path.GetFullPath(dir).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            string p = Path.GetFullPath(path);
            return p.StartsWith(d, StringComparison.OrdinalIgnoreCase) ? p.Substring(d.Length).Replace('\\', '/') : Path.GetFileName(p);
        }

        private void ApplyCompile(CompileJob job)
        {
            _lastCompileMs = job.Milliseconds;
            var result = job.Result;
            var byKey = new Dictionary<string, List<Diagnostic>>(StringComparer.OrdinalIgnoreCase);
            var items = new List<ProblemItem>();
            foreach (var d in result.Diagnostics)
            {
                string key = d.File != null && job.LogicalToKey.TryGetValue(d.File, out var k) ? k : null;
                if (key != null)
                {
                    if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<Diagnostic>();
                    list.Add(d);
                }
                items.Add(new ProblemItem
                {
                    Key = key,
                    File = key != null ? DisplayNameOf(key) : (d.File ?? ""),
                    D = d,
                });
            }

            foreach (var doc in _docs)
            {
                if (!job.DocVersions.TryGetValue(doc.Key, out int ver)) continue;
                doc.Diagnostics.Clear();
                if (byKey.TryGetValue(doc.Key, out var list)) doc.Diagnostics.AddRange(list);
                doc.DiagnosticsVersion = ver;
            }
            // подчёркивания — только если буфер не менялся за время компиляции (иначе позиции врут,
            // а свежая компиляция уже в очереди)
            if (_active != null && _active.DiagnosticsVersion == _active.Version)
                _editor.SetDiagnostics(_active.Diagnostics);

            _problems.Set(items, _active?.Key);
            RefreshProblemsHeader();

            // синтакс-индекс по тому, что реально компилировалось
            foreach (var set in job.Modules)
                foreach (var (logical, text) in set.Files)
                    if (job.LogicalToKey.TryGetValue(logical, out var key)) _ls.Index.Update(key, text);
            _editor.SetClassifier(_ls.CreateClassifier());
            RefreshOutline();

            int errors = _problems.Errors, warnings = _problems.Warnings;
            if (result.Success && warnings == 0) SetStatus($"OK — компилируется ({job.Milliseconds:0} мс).", IdeStatusKind.Ok);
            else if (result.Success) SetStatus($"Компилируется, предупреждений: {warnings}.", IdeStatusKind.Warning);
            else SetStatus($"Ошибок: {errors}, предупреждений: {warnings}.", IdeStatusKind.Error);
            UpdateStatusRight();
        }

        // ═══════════════════════════ ICodeAssist ═══════════════════════════

        List<CompletionItem> ICodeAssist.Complete(int line1, int col1) =>
            _active == null ? null : _ls.Complete(_active.Key, line1, col1);

        SignatureInfo ICodeAssist.Signature(int line1, int col1) =>
            _active == null ? null : _ls.SignatureHelp(_active.Key, line1, col1);

        string ICodeAssist.Hover(int line1, int col1) =>
            _active == null ? null : _ls.Hover(_active.Key, line1, col1);

        bool ICodeAssist.GoToDefinition(int line1, int col1)
        {
            if (_active == null) return false;
            // индекс активного файла — по живому буферу, иначе строки деклараций врут
            _ls.Index.Update(_active.Key, _active.Text);
            var loc = _ls.Definition(_active.Key, line1, col1);
            if (loc == null) { SetStatus("Определение не найдено.", IdeStatusKind.Info); return false; }
            if (string.Equals(loc.File, _active.Key, StringComparison.OrdinalIgnoreCase))
                _editor.GoTo(loc.Line, loc.Col);
            else
                OpenFile(loc.File, loc.Line, loc.Col);
            return true;
        }

        // ═══════════════════════════ сессия ═══════════════════════════

        private void RestoreSession()
        {
            int restored = 0, conflicts = 0;
            foreach (var sd in _session.Docs)
            {
                if (string.IsNullOrEmpty(sd.Key)) continue;
                string key = _workspace.NormalizeKey(sd.Key) ?? sd.Key;
                string disk = ReadKey(key);
                string buffer = sd.Buffer != null ? _storage.Read(sd.Buffer) : null;
                if (sd.Buffer != null) _bufferKeys.Add(sd.Buffer);
                if (disk == null && buffer == null) continue; // файл исчез, правок не было — вкладку не поднимаем

                var doc = new IdeDocument(key, DisplayNameOf(key)) { Logical = LogicalOf(key) };
                doc.LoadFromDisk(disk ?? "", _workspace.GetStamp(key));
                if (buffer != null)
                {
                    doc.RestoreBuffer(buffer);
                    restored++;
                    // диск поменялся с момента бэкапа — не выбираем молча, показываем конфликт
                    if (disk != null && sd.BaseHash != null && sd.BaseHash != IdeSession.Hash(IdeDocument.Normalize(disk)))
                    {
                        doc.ConflictText = disk;
                        conflicts++;
                    }
                }
                doc.CaretIndex = Mathf.Clamp(sd.Caret, 0, doc.Text.Length);
                doc.SelectIndex = Mathf.Clamp(sd.Select, 0, doc.Text.Length);
                doc.ScrollX = sd.ScrollX;
                doc.ScrollY = sd.ScrollY;
                _docs.Add(doc);
                _ls.Index.Update(key, doc.Text);
            }
            var active = _docs.Find(d => string.Equals(d.Key, _session.Active, StringComparison.OrdinalIgnoreCase)) ??
                         (_docs.Count > 0 ? _docs[0] : null);
            if (active != null) Activate(active);
            else ShowEmpty();
            if (restored > 0)
                SetStatus($"Восстановлены несохранённые правки: {restored}" + (conflicts > 0 ? $", конфликтов с диском: {conflicts}." : "."),
                          conflicts > 0 ? IdeStatusKind.Warning : IdeStatusKind.Info);
        }

        // ═══════════════════════════ UI ═══════════════════════════

        private void RefreshAll()
        {
            RefreshTabs();
            RefreshExplorer();
            RefreshOutline();
            RefreshBanner();
            RefreshProblemsHeader();
            UpdateStatusLeft();
            UpdateStatusRight();
            if (_active == null) ShowEmpty();
        }

        private void RefreshTabs() => _tabs.Rebuild(_docs, _active);

        private void RefreshExplorer() => _explorer.Rebuild(_workspace, _snapshot, _active?.Key, _workspace.CanCreateFiles);

        private void RefreshOutline()
        {
            FileSymbols fs = null;
            if (_active != null) _ls.Index.TryGet(_active.Key, out fs);
            _outline.Rebuild(fs);
        }

        private void RefreshBanner()
        {
            _bannerButtons.Clear();
            var d = _active;
            if (d == null) { _banner.style.display = DisplayStyle.None; return; }
            if (d.HasConflict)
            {
                _bannerText.text = "Файл изменён на диске, а в редакторе есть несохранённые правки.";
                _bannerButtons.Add(SmallButton("Взять с диска", "Отбросить свои правки и загрузить версию с диска", () =>
                {
                    string disk = d.ConflictText;
                    d.LoadFromDisk(disk, _workspace.GetStamp(d.Key));
                    BindEditor(d);
                    RefreshTabs();
                    RefreshBanner();
                    RequestCompile(true);
                }));
                _bannerButtons.Add(SmallButton("Оставить мои", "Оставить правки; сохранение перезапишет файл", () =>
                {
                    // «сохранённой» считаем версию диска, буфер остаётся изменённым —
                    // следующее сохранение перезапишет файл без повторного вопроса
                    d.AcceptDiskAsBase(d.ConflictText, _workspace.GetStamp(d.Key));
                    RefreshTabs();
                    RefreshBanner();
                }));
                _banner.EnableInClassList("sal-banner--warning", true);
                _banner.style.display = DisplayStyle.Flex;
                return;
            }
            if (d.Logical == null)
            {
                _bannerText.text = "Файл не входит ни в один module.json воркспейса — в игре он не компилируется.";
                _banner.EnableInClassList("sal-banner--warning", false);
                _banner.style.display = DisplayStyle.Flex;
                return;
            }
            _banner.style.display = DisplayStyle.None;
        }

        private void OnProblem(ProblemItem p)
        {
            if (p?.Key == null) return;
            if (_active == null || !string.Equals(_active.Key, p.Key, StringComparison.OrdinalIgnoreCase))
                OpenFile(p.Key, p.D.Line, p.D.Column);
            else
                _editor.GoTo(p.D.Line, p.D.Column);
            _editor.FocusCode();
        }

        private void BuildFilters()
        {
            _problemsFilters.Clear();
            _problemsFilters.Add(FilterToggle("● ", () => _problems.ShowErrors, v => _problems.ShowErrors = v, "sal-sev-error", () => _problems.Errors));
            _problemsFilters.Add(FilterToggle("▲ ", () => _problems.ShowWarnings, v => _problems.ShowWarnings = v, "sal-sev-warning", () => _problems.Warnings));
            _problemsFilters.Add(FilterToggle("○ ", () => _problems.ShowInfos, v => _problems.ShowInfos = v, "sal-sev-info", () => _problems.Infos));
            _problemsFilters.Add(FilterToggle("Текущий файл", () => _problems.CurrentOnly, v => _problems.CurrentOnly = v, null, null));
        }

        private Button FilterToggle(string label, Func<bool> get, Action<bool> set, string cls, Func<int> count)
        {
            Button b = null;
            b = new Button(() =>
            {
                set(!get());
                _problems.Refresh();
                RefreshProblemsHeader();
                ScheduleSession();
            });
            b.AddToClassList("sal-filter");
            if (cls != null) b.AddToClassList(cls);
            b.userData = (Func<string>)(() => label + (count != null ? count().ToString() : ""));
            b.text = label;
            b.RegisterCallback<AttachToPanelEvent>(_ => RefreshFilter(b, get));
            b.clicked += () => RefreshFilter(b, get);
            _filterRefreshers.Add(() => RefreshFilter(b, get));
            return b;
        }

        private readonly List<Action> _filterRefreshers = new List<Action>();

        private static void RefreshFilter(Button b, Func<bool> get)
        {
            if (b.userData is Func<string> text) b.text = text();
            b.EnableInClassList("sal-filter--off", !get());
        }

        private void RefreshProblemsHeader()
        {
            foreach (var r in _filterRefreshers) r();
            _problemsTab.text = _problems.Errors + _problems.Warnings > 0
                ? $"Проблемы ({_problems.Errors + _problems.Warnings})"
                : "Проблемы";
        }

        private void ShowSidebarTab(string tab)
        {
            bool files = tab != "outline";
            _explorer.style.display = files ? DisplayStyle.Flex : DisplayStyle.None;
            _outline.style.display = files ? DisplayStyle.None : DisplayStyle.Flex;
            _sideFilesTab.EnableInClassList("sal-header-tab--active", files);
            _sideOutlineTab.EnableInClassList("sal-header-tab--active", !files);
            if (_session != null) _session.SidebarTab = files ? "files" : "outline";
        }

        private void ShowBottomTab(string tab)
        {
            bool problems = tab != "console";
            _problems.style.display = problems ? DisplayStyle.Flex : DisplayStyle.None;
            _console.style.display = problems ? DisplayStyle.None : DisplayStyle.Flex;
            _problemsFilters.style.display = problems ? DisplayStyle.Flex : DisplayStyle.None;
            _problemsTab.EnableInClassList("sal-header-tab--active", problems);
            _consoleTab.EnableInClassList("sal-header-tab--active", !problems);
            if (_session != null) _session.BottomTab = problems ? "problems" : "console";
        }

        /// <summary>Показать консоль (например, после «Применить»).</summary>
        public void ShowConsole() => ShowBottomTab("console");

        private void ToggleSidebar()
        {
            _session.SidebarVisible = !_session.SidebarVisible;
            if (_session.SidebarVisible) _mainSplit.UnCollapse(); else _mainSplit.CollapseChild(0);
            ScheduleSession();
        }

        private void ToggleBottom()
        {
            _session.BottomVisible = !_session.BottomVisible;
            if (_session.BottomVisible) _centerSplit.UnCollapse(); else _centerSplit.CollapseChild(1);
            ScheduleSession();
        }

        private void RefreshApplyButton()
        {
            bool has = _apply != null;
            _applyButton.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
            if (!has) return;
            _applyButton.text = string.IsNullOrEmpty(_apply.ApplyLabel) ? "Применить" : _apply.ApplyLabel;
            // движок может быть ещё не поднят или уже уничтожен — кнопка это показывает,
            // иначе нажатие молча ничего не делало
            bool can = false;
            try { can = _apply.CanApply; } catch { }
            _applyButton.SetEnabled(can);
            _applyButton.tooltip = can ? "" : "Движок скриптов не запущен";
        }

        private void UpdateStatusLeft()
        {
            if (_active == null) { _statusLeft.text = ""; return; }
            int caret = _editor.CaretIndex, sel = _editor.SelectIndex;
            TextUtil.LineColAt(_editor.Text, caret, out int line, out int col);
            _statusLeft.text = caret != sel ? $"Стр {line}, стлб {col} (выделено {Math.Abs(caret - sel)})" : $"Стр {line}, стлб {col}";
        }

        private void UpdateStatusRight()
        {
            var m = _api.Current ?? ApiModel.Empty;
            string api = m.IsEmpty ? "API игры не задан" : $"API v{m.ApiVersion}: {m.Label}";
            string ws = _workspace?.DisplayName ?? "";
            string ms = _lastCompileMs > 0 ? $" · {_lastCompileMs:0} мс" : "";
            _statusRight.text = $"{ws} · {api}{ms}";
        }

        private void SetStatus(string text, IdeStatusKind kind)
        {
            _statusText = text;
            _statusKind = kind;
            _statusMid.text = text ?? "";
            _statusMid.EnableInClassList("sal-status--ok", kind == IdeStatusKind.Ok);
            _statusMid.EnableInClassList("sal-status--warning", kind == IdeStatusKind.Warning);
            _statusMid.EnableInClassList("sal-status--error", kind == IdeStatusKind.Error);
            try { _host.OnStatus(text, kind); }
            catch { /* статус хоста — не повод ронять IDE */ }
            StatusChanged?.Invoke(text, kind);
        }

        private Button ToolButton(string text, string hint, Action onClick, bool primary = false)
        {
            var b = new Button(onClick) { text = text, tooltip = hint };
            b.AddToClassList("sal-btn");
            if (primary) b.AddToClassList("sal-btn--primary");
            _chrome.Register(b, primary ? ChromeSlot.Accent : ChromeSlot.Raised,
                             primary ? ChromeSlot.AccentText : ChromeSlot.Text, ChromeSlot.Border, hover: !primary);
            return b;
        }

        private Button SmallButton(string text, string hint, Action onClick)
        {
            var b = new Button(onClick) { text = text, tooltip = hint };
            b.AddToClassList("sal-btn");
            b.AddToClassList("sal-btn--small");
            _chrome.Register(b, ChromeSlot.Raised, ChromeSlot.Text, ChromeSlot.Border, hover: true);
            return b;
        }

        private Button HeaderTab(string text, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.AddToClassList("sal-header-tab");
            return b;
        }

        private static VisualElement Separator()
        {
            var s = new VisualElement();
            s.AddToClassList("sal-toolbar-separator");
            return s;
        }
    }
}
