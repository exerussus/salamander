using System;
using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Text;
using Dsl.Tooling;
using UnityEngine;
using UnityEngine.UIElements;

namespace Dsl.Ide
{
    // ═══════════════════════════ вкладки ═══════════════════════════

    /// <summary>Полоса вкладок открытых файлов: имя, точка «не сохранено», крестик. Средняя кнопка — закрыть.</summary>
    public sealed class TabStrip : VisualElement
    {
        private readonly ScrollView _scroll;
        private readonly IdeChrome _chrome;
        public event Action<IdeDocument> Selected;
        public event Action<IdeDocument> CloseRequested;

        public TabStrip(IdeChrome chrome)
        {
            _chrome = chrome;
            AddToClassList("sal-tabs");
            chrome.Register(this, ChromeSlot.Panel, ChromeSlot.TextDim, ChromeSlot.Border);
            _scroll = new ScrollView(ScrollViewMode.Horizontal);
            _scroll.AddToClassList("sal-tabs-scroll");
            _scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            Add(_scroll);
        }

        public void Rebuild(IReadOnlyList<IdeDocument> docs, IdeDocument active)
        {
            _scroll.Clear();
            foreach (var d in docs)
            {
                var doc = d;
                bool isActive = doc == active;
                var tab = new VisualElement();
                tab.AddToClassList("sal-tab");
                if (isActive) tab.AddToClassList("sal-tab--active");
                if (doc.Logical == null) tab.AddToClassList("sal-tab--loose");
                tab.tooltip = doc.Key;
                var name = new Label(doc.DisplayName + (doc.HasConflict ? " (!)" : ""));
                name.AddToClassList("sal-tab-name");
                tab.Add(name);
                var dirty = new Label(doc.Dirty ? "●" : "");
                dirty.AddToClassList("sal-tab-dirty");
                tab.Add(dirty);
                var close = new Button(() => CloseRequested?.Invoke(doc)) { text = "×" };
                close.AddToClassList("sal-tab-close");
                tab.Add(close);
                tab.RegisterCallback<PointerUpEvent>(e =>
                {
                    if (e.button == 2) { CloseRequested?.Invoke(doc); e.StopPropagation(); }   // средняя кнопка
                    else if (e.button == 0 && !(e.target is Button)) Selected?.Invoke(doc);
                });
                _chrome.Register(tab, () => isActive ? ChromeSlot.Background : ChromeSlot.Panel,
                                 isActive ? ChromeSlot.Text : ChromeSlot.TextDim, ChromeSlot.Border, hover: !isActive);
                _scroll.Add(tab);
                if (isActive) schedule.Execute(() => { if (tab.panel != null) _scroll.ScrollTo(tab); });
            }
        }

        /// <summary>Обновить только признаки «изменён» без пересборки.</summary>
        public void RefreshDirty(IReadOnlyList<IdeDocument> docs)
        {
            int i = 0;
            foreach (var tab in _scroll.Children())
            {
                if (i >= docs.Count) break;
                if (tab.childCount >= 2 && tab[1] is Label dot) dot.text = docs[i].Dirty ? "●" : "";
                i++;
            }
        }
    }

    // ═══════════════════════════ файлы модулей ═══════════════════════════

    /// <summary>Дерево воркспейса: модули и их файлы; файлы вне module.json — приглушённо.</summary>
    public sealed class ExplorerPanel : VisualElement
    {
        private readonly ScrollView _scroll;
        private readonly IdeChrome _chrome;
        private readonly HashSet<string> _collapsed = new HashSet<string>(StringComparer.Ordinal);

        public event Action<string> OpenRequested;          // ключ файла
        public event Action<string> NewFileRequested;       // имя модуля

        public ExplorerPanel(IdeChrome chrome)
        {
            _chrome = chrome;
            AddToClassList("sal-explorer");
            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.AddToClassList("sal-panel-scroll");
            Add(_scroll);
        }

        public void Rebuild(IScriptWorkspace ws, WorkspaceModules snapshot, string activeKey, bool canCreate)
        {
            _scroll.Clear();
            if (snapshot == null || snapshot.Modules.Count == 0)
            {
                var empty = new Label(ws?.Root == null ? "Модули не загружены." : "Модулей не найдено в\n" + ws.Root);
                empty.AddToClassList("sal-empty");
                _scroll.Add(empty);
            }
            if (snapshot == null) return;

            foreach (var set in snapshot.Modules)
            {
                string module = set.Manifest?.Name ?? "?";
                var header = new VisualElement();
                header.AddToClassList("sal-tree-module");
                bool collapsed = _collapsed.Contains(module);
                var arrow = new Label(collapsed ? "+" : "−");
                arrow.AddToClassList("sal-tree-arrow");
                header.Add(arrow);
                var title = new Label(module);
                title.AddToClassList("sal-tree-module-name");
                header.Add(title);
                if (set.Manifest != null && set.Manifest.IsSynchronous)
                {
                    var badge = new Label("sync");
                    badge.AddToClassList("sal-badge");
                    header.Add(badge);
                }
                if (canCreate)
                {
                    var add = new Button(() => NewFileRequested?.Invoke(module)) { text = "+", tooltip = "Новый файл в модуле" };
                    add.AddToClassList("sal-tree-add");
                    header.Add(add);
                }
                header.RegisterCallback<PointerUpEvent>(e =>
                {
                    if (e.target is Button) return;
                    if (!_collapsed.Remove(module)) _collapsed.Add(module);
                    Rebuild(ws, snapshot, activeKey, canCreate);
                });
                _chrome.Register(header, ChromeSlot.None, ChromeSlot.Text, hover: true);
                _scroll.Add(header);
                if (collapsed) continue;

                foreach (var (logical, _) in set.Files)
                {
                    if (!snapshot.LogicalToPath.TryGetValue(logical, out var key)) continue;
                    string rel = logical.StartsWith(module + "/", StringComparison.Ordinal) ? logical.Substring(module.Length + 1) : logical;
                    _scroll.Add(FileRow(rel, key, key == activeKey, dim: false));
                }
                if (ws != null)
                    foreach (var key in ws.ListUnlistedFiles(module))
                        _scroll.Add(FileRow(ws.DisplayNameOf(key) + "  (не в module.json)", key, key == activeKey, dim: true));
            }

            // ошибки загрузки модулей — тут же, иначе «почему модуля нет» не понять
            foreach (var err in snapshot.LoadErrors)
            {
                var l = new Label("! " + err.Value);
                l.AddToClassList("sal-tree-error");
                l.tooltip = err.Key;
                _scroll.Add(l);
            }
        }

        private VisualElement FileRow(string text, string key, bool active, bool dim)
        {
            var row = new Label(text);
            row.AddToClassList("sal-tree-file");
            if (active) row.AddToClassList("sal-tree-file--active");
            if (dim) row.AddToClassList("sal-tree-file--dim");
            row.tooltip = key;
            row.RegisterCallback<PointerUpEvent>(e => { if (e.button == 0) OpenRequested?.Invoke(key); });
            _chrome.Register(row, () => active ? ChromeSlot.Selection : ChromeSlot.None,
                             dim ? ChromeSlot.TextDim : ChromeSlot.Text, hover: !active);
            return row;
        }
    }

    // ═══════════════════════════ структура файла ═══════════════════════════

    /// <summary>Декларации текущего файла и их члены (из синтакс-индекса). Клик — переход.</summary>
    public sealed class OutlinePanel : VisualElement
    {
        private readonly ScrollView _scroll;
        private readonly IdeChrome _chrome;
        public event Action<int, int> GoToRequested; // 1-based строка, колонка

        public OutlinePanel(IdeChrome chrome)
        {
            _chrome = chrome;
            AddToClassList("sal-outline");
            _scroll = new ScrollView(ScrollViewMode.Vertical);
            _scroll.AddToClassList("sal-panel-scroll");
            Add(_scroll);
        }

        public void Rebuild(FileSymbols symbols)
        {
            _scroll.Clear();
            if (symbols == null || symbols.Decls.Count == 0)
            {
                var empty = new Label("Нет деклараций.");
                empty.AddToClassList("sal-empty");
                _scroll.Add(empty);
                return;
            }
            foreach (var d in symbols.Decls)
            {
                _scroll.Add(Row(d, 0));
                foreach (var ch in d.Children) _scroll.Add(Row(ch, 1));
            }
        }

        private VisualElement Row(DeclSymbol s, int depth)
        {
            var row = new VisualElement();
            row.AddToClassList("sal-outline-row");
            row.style.paddingLeft = 6 + depth * 14;
            var kind = new Label(s.Kind);
            kind.AddToClassList("sal-outline-kind");
            kind.AddToClassList("sal-outline-kind--" + (depth == 0 ? "decl" : s.Kind));
            row.Add(kind);
            var name = new Label(s.Name);
            name.AddToClassList("sal-outline-name");
            row.Add(name);
            int line = s.Line, col = s.Col;
            row.RegisterCallback<PointerUpEvent>(e => { if (e.button == 0) GoToRequested?.Invoke(line, col); });
            _chrome.Register(row, ChromeSlot.None, ChromeSlot.Text, hover: true);
            return row;
        }
    }

    // ═══════════════════════════ проблемы ═══════════════════════════

    public sealed class ProblemItem
    {
        public string Key;      // ключ документа или null (не файл)
        public string File;     // что показать
        public Diagnostic D;
    }

    /// <summary>Все диагностики последней компиляции воркспейса; фильтры по важности и «только текущий файл».</summary>
    public sealed class ProblemsPanel : VisualElement
    {
        private readonly ListView _list;
        private readonly List<ProblemItem> _all = new List<ProblemItem>();
        private readonly List<ProblemItem> _shown = new List<ProblemItem>();
        private string _currentKey;

        public bool ShowErrors = true, ShowWarnings = true, ShowInfos = true, CurrentOnly;
        public event Action<ProblemItem> Activated;

        public int Errors { get; private set; }
        public int Warnings { get; private set; }
        public int Infos { get; private set; }

        public ProblemsPanel()
        {
            AddToClassList("sal-problems");
            _list = new ListView(_shown, 20f, MakeRow, BindRow)
            {
                selectionType = SelectionType.Single,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
            };
            _list.AddToClassList("sal-problems-list");
            _list.selectionChanged += sel =>
            {
                foreach (var o in sel)
                {
                    Activated?.Invoke((ProblemItem)o);
                    // снять выбор — повторный клик по той же строке снова переходит
                    schedule.Execute(() => _list.ClearSelection());
                    break;
                }
            };
            Add(_list);
        }

        public void Set(List<ProblemItem> items, string currentKey)
        {
            _all.Clear();
            if (items != null) _all.AddRange(items);
            Errors = Warnings = Infos = 0;
            foreach (var p in _all)
            {
                if (p.D.Severity == Severity.Error) Errors++;
                else if (p.D.Severity == Severity.Warning) Warnings++;
                else Infos++;
            }
            SetCurrent(currentKey);
        }

        public void SetCurrent(string currentKey)
        {
            _currentKey = currentKey;
            Refresh();
        }

        public void Refresh()
        {
            _shown.Clear();
            foreach (var p in _all)
            {
                if (CurrentOnly && p.Key != _currentKey) continue;
                var s = p.D.Severity;
                if (s == Severity.Error && !ShowErrors) continue;
                if (s == Severity.Warning && !ShowWarnings) continue;
                if (s == Severity.Info && !ShowInfos) continue;
                _shown.Add(p);
            }
            _list.ClearSelection();
            _list.RefreshItems();
        }

        private static VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("sal-problem");
            var glyph = new Label();
            glyph.AddToClassList("sal-problem-glyph");
            var text = new Label();
            text.AddToClassList("sal-problem-text");
            var where = new Label();
            where.AddToClassList("sal-problem-where");
            row.Add(glyph);
            row.Add(text);
            row.Add(where);
            return row;
        }

        private void BindRow(VisualElement el, int i)
        {
            var p = _shown[i];
            var glyph = (Label)el[0];
            glyph.text = p.D.Severity == Severity.Error ? "●" : p.D.Severity == Severity.Warning ? "▲" : "○";
            glyph.EnableInClassList("sal-sev-error", p.D.Severity == Severity.Error);
            glyph.EnableInClassList("sal-sev-warning", p.D.Severity == Severity.Warning);
            glyph.EnableInClassList("sal-sev-info", p.D.Severity == Severity.Info);
            ((Label)el[1]).text = $"{p.D.Code}  {p.D.Message}";
            ((Label)el[2]).text = p.D.Line > 0 ? $"{p.File}:{p.D.Line}:{p.D.Column}" : p.File;
        }
    }

    // ═══════════════════════════ консоль ═══════════════════════════

    public enum IdeLogKind { Info, Warning, Error, Compile }

    /// <summary>Лог скриптов и перезагрузок (Engine.Log/Warn/Error из игры, итоги «Применить»).</summary>
    public sealed class ConsolePanel : VisualElement
    {
        private const int Max = 2000;
        private readonly ListView _list;
        private readonly List<(IdeLogKind kind, string text)> _lines = new List<(IdeLogKind, string)>();

        public ConsolePanel()
        {
            AddToClassList("sal-console");
            _list = new ListView(_lines, 18f, () =>
            {
                var l = new Label();
                l.AddToClassList("sal-console-line");
                return l;
            }, (el, i) =>
            {
                var l = (Label)el;
                var (kind, text) = _lines[i];
                l.text = text;
                l.EnableInClassList("sal-sev-error", kind == IdeLogKind.Error);
                l.EnableInClassList("sal-sev-warning", kind == IdeLogKind.Warning);
                l.EnableInClassList("sal-console-compile", kind == IdeLogKind.Compile);
            })
            {
                selectionType = SelectionType.Single,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
            };
            _list.AddToClassList("sal-console-list");
            Add(_list);
        }

        public int Count => _lines.Count;

        private bool _refreshScheduled;

        public void Append(IdeLogKind kind, string text)
        {
            string stamp = DateTime.Now.ToString("HH:mm:ss");
            foreach (var line in (text ?? "").Split('\n'))
                _lines.Add((kind, $"{stamp}  {line}"));
            if (_lines.Count > Max) _lines.RemoveRange(0, _lines.Count - Max);
            // скрипт может логировать каждый кадр — перерисовываем список раз в кадр
            if (_refreshScheduled || panel == null) return;
            _refreshScheduled = true;
            schedule.Execute(() =>
            {
                _refreshScheduled = false;
                _list.RefreshItems();
                if (_lines.Count > 0) _list.ScrollToItem(_lines.Count - 1);
            });
        }

        public void ClearLines()
        {
            _lines.Clear();
            _list.RefreshItems();
        }
    }
}
