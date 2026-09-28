using System;
using System.Collections.Generic;
using System.Text;
using Dsl.Text;
using Dsl.Tooling;
using UnityEngine;
using UnityEngine.UIElements;

namespace Dsl.Ide
{
    /// <summary>Что редактор спрашивает у владельца: языковые подсказки по 1-based позиции в текущем тексте.</summary>
    public interface ICodeAssist
    {
        List<CompletionItem> Complete(int line1, int col1);
        SignatureInfo Signature(int line1, int col1);
        /// <summary>Markdown или null.</summary>
        string Hover(int line1, int col1);
        /// <summary>true — переход выполнен (владелец открыл файл/сдвинул каретку).</summary>
        bool GoToDefinition(int line1, int col1);
    }

    public enum EditorCommand { Save, SaveAll, GoToLine, Compile, NewFile }

    /// <summary>
    /// Кодовая область Salamander: подсветка под прозрачным TextField (каретка,
    /// выделение, буфер обмена и IME — нативные, в редакторе и в игре), номера
    /// строк, подчёркивание ошибок, текущая строка, парные скобки, поиск,
    /// автодополнение, подсказка параметров, hover, умный ввод.
    ///
    /// Геометрия берётся у самого TextField (GetCursorPositionFromStringIndex),
    /// поэтому табы, кириллица и не-моноширинный запасной шрифт не сбивают
    /// попапы и подчёркивания. Весь внешний вид — в USS (классы .sal-*).
    ///
    ///   this (.sal-code-editor)
    ///   ├── gutter (.sal-gutter) › gutterText           номера строк, скролл синхронизирован
    ///   ├── ScrollView (.sal-code-scroll)
    ///   │   └── stack (.sal-code-stack)
    ///   │       ├── backLayer (.sal-code-back)          текущая строка, совпадения поиска, скобки
    ///   │       ├── highlight Label (.sal-code-highlight) rich-text, задаёт размер
    ///   │       ├── input TextField (.sal-code-input)   поверх, текст прозрачный
    ///   │       └── frontLayer (.sal-code-front)        подчёркивания диагностик
    ///   ├── FindBar, CompletionPopup, InfoPopup ×2      поверх (absolute)
    /// </summary>
    public sealed class SalCodeEditor : VisualElement, IDisposable
    {
        private static readonly string[] PaletteProps =
        {
            null, "--sal-keyword", "--sal-type", "--sal-class", "--sal-function", "--sal-property",
            "--sal-variable", "--sal-string", "--sal-number", "--sal-comment", "--sal-namespace",
            "--sal-enum-member", "--sal-decorator",
        };
        private static readonly CustomStyleProperty<Color>[] PaletteStyle = BuildPaletteStyle();

        private static CustomStyleProperty<Color>[] BuildPaletteStyle()
        {
            var r = new CustomStyleProperty<Color>[PaletteProps.Length];
            for (int i = 1; i < PaletteProps.Length; i++) r[i] = new CustomStyleProperty<Color>(PaletteProps[i]);
            return r;
        }

        private static readonly CustomStyleProperty<Color> GutterErrorProp = new CustomStyleProperty<Color>("--sal-gutter-error");
        private static readonly CustomStyleProperty<Color> GutterWarningProp = new CustomStyleProperty<Color>("--sal-gutter-warning");

        // ── элементы ──
        private readonly VisualElement _gutter;
        private readonly Label _gutterText;
        private readonly ScrollView _scroll;
        private readonly VisualElement _stack;
        private readonly VisualElement _back;
        private readonly VisualElement _front;
        private readonly Label _highlight;
        private readonly TextField _input;
        private TextElement _inputText;
        private readonly VisualElement _currentLine;
        private readonly VisualElement _bracketA;
        private readonly VisualElement _bracketB;
        private readonly List<VisualElement> _squiggles = new List<VisualElement>();
        private readonly List<VisualElement> _findMarks = new List<VisualElement>();
        private readonly CompletionPopup _completion;
        private readonly InfoPopup _signature;
        private readonly InfoPopup _hover;
        private readonly FindBar _find;

        // ── состояние ──
        private readonly LineHighlighter _hl = new LineHighlighter();
        private CodePalette _palette = CodePalette.Default();
        private string _text = "";
        private int[] _lineStarts = { 0 };
        private bool _lineStartsValid = true;
        private int _gutterLines = -1;
        private string _gutterErrorHex = "F14C4C", _gutterWarningHex = "CCA700";
        private UndoHistory _history = new UndoHistory();
        private readonly List<Diagnostic> _diags = new List<Diagnostic>();
        private bool _diagsCurrent;
        private int _completionStart = -1;
        private List<int> _matches = new List<int>();
        private int _matchIndex = -1;
        private string _matchesText;   // для какого текста посчитаны совпадения
        private int _lastCaret, _lastSelect;
        private IVisualElementScheduledItem _tick;
        private bool _active = true;
        private bool _decorScheduled;
        private Font _ownedFont;
        private float _lineHeight = 17f;
        private bool _cursorAtBottom;
        private bool _metricsValid;
        private Vector2 _hoverPos;
        private double _hoverSince = -1;
        private bool _pointerInside;
        private int _signatureGen;   // отложенный показ подсказки параметров не должен пережить её закрытие

        // ── публичное ──
        public ICodeAssist Assist;
        public event Action<string> TextEdited;
        public event Action CaretMoved;
        public event Action<EditorCommand> Command;

        public string Text => _text;
        public int CaretIndex => _input.cursorIndex;
        public int SelectIndex => _input.selectIndex;
        public bool HasFocus => _input.focusController?.focusedElement == _input;

        public bool ReadOnly
        {
            get => _input.isReadOnly;
            set => _input.isReadOnly = value;
        }

        public Vector2 ScrollOffset
        {
            get => _scroll.scrollOffset;
            set => _scroll.scrollOffset = value;
        }

        public SalCodeEditor(StyleSheet styles = null, FontDefinition? codeFont = null)
        {
            AddToClassList("sal-code-editor");
            if (styles != null) styleSheets.Add(styles);
            focusable = false;

            // Шрифт: переданный хостом (обязателен на WebGL/мобильных — там нет системных
            // шрифтов), иначе системный моноширинный. Ставим на корень: наследуют все слои.
            if (codeFont.HasValue) style.unityFontDefinition = codeFont.Value;
            else
            {
                _ownedFont = Font.CreateDynamicFontFromOSFont(
                    new[] { "Consolas", "Cascadia Mono", "Menlo", "DejaVu Sans Mono", "Liberation Mono", "Courier New" }, 13);
                if (_ownedFont != null) style.unityFontDefinition = FontDefinition.FromFont(_ownedFont);
            }

            _gutter = new VisualElement();
            _gutter.AddToClassList("sal-gutter");
            _gutterText = new Label { enableRichText = true, pickingMode = PickingMode.Ignore };
            _gutterText.AddToClassList("sal-gutter-text");
            _gutter.Add(_gutterText);
            Add(_gutter);

            _scroll = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            _scroll.AddToClassList("sal-code-scroll");
            Add(_scroll);

            _stack = new VisualElement();
            _stack.AddToClassList("sal-code-stack");
            _scroll.Add(_stack);

            _back = new VisualElement { pickingMode = PickingMode.Ignore };
            _back.AddToClassList("sal-code-back");
            _stack.Add(_back);
            _currentLine = Decor(_back, "sal-current-line");
            _bracketA = Decor(_back, "sal-bracket");
            _bracketB = Decor(_back, "sal-bracket");

            _highlight = new Label { enableRichText = true, pickingMode = PickingMode.Ignore };
            _highlight.AddToClassList("sal-code-highlight");
            _stack.Add(_highlight);

            _input = new TextField { multiline = true };
            _input.AddToClassList("sal-code-input");
            _input.selectAllOnFocus = false;
            _input.selectAllOnMouseUp = false;
            _input.doubleClickSelectsWord = true;
            _input.tripleClickSelectsLine = true;
            _input.verticalScrollerVisibility = ScrollerVisibility.Hidden; // скроллит внешний ScrollView
            _stack.Add(_input);

            _front = new VisualElement { pickingMode = PickingMode.Ignore };
            _front.AddToClassList("sal-code-front");
            _stack.Add(_front);

            _find = new FindBar();
            Add(_find);
            _completion = new CompletionPopup();
            Add(_completion);
            _signature = new InfoPopup("sal-info--signature");
            Add(_signature);
            _hover = new InfoPopup("sal-info--hover");
            Add(_hover);

            _input.RegisterValueChangedCallback(OnNativeChange);
            // TrickleDown: перехватываем ДО внутреннего TextElement (иначе Tab уведёт фокус,
            // а Enter вставит перенос раньше нас)
            _input.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            // в игре Tab/Enter/Esc приходят ещё и навигационными событиями — фокус не отдаём
            RegisterCallback<NavigationMoveEvent>(OnNavigation, TrickleDown.TrickleDown);
            RegisterCallback<NavigationSubmitEvent>(OnNavigation, TrickleDown.TrickleDown);
            RegisterCallback<NavigationCancelEvent>(OnNavigation, TrickleDown.TrickleDown);
            _input.RegisterCallback<PointerMoveEvent>(OnPointerMove);
            _input.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _input.RegisterCallback<PointerLeaveEvent>(_ => { _pointerInside = false; _hoverSince = -1; _hover.Hide(); });
            _input.RegisterCallback<PointerUpEvent>(OnPointerUp);
            _input.RegisterCallback<GeometryChangedEvent>(_ => { _metricsValid = false; ScheduleDecor(); });
            // клик по пустоте под текстом — фокус в поле
            _scroll.RegisterCallback<PointerDownEvent>(e => { if (e.target == _scroll || e.target == _stack) _input.Focus(); });
            RegisterCallback<PointerDownEvent>(OnAnyPointerDown, TrickleDown.TrickleDown);
            _scroll.verticalScroller.valueChanged += OnScrolled;
            _scroll.horizontalScroller.valueChanged += OnScrolled;
            RegisterCallback<CustomStyleResolvedEvent>(OnCustomStyle);
            RegisterCallback<AttachToPanelEvent>(_ => { if (_active) StartTick(); });
            RegisterCallback<DetachFromPanelEvent>(_ => StopTick());

            _completion.Committed += CommitCompletion;
            _find.QueryChanged += OnFindQuery;
            _find.Next += () => FindStep(1);
            _find.Previous += () => FindStep(-1);
            _find.ReplaceOne += ReplaceCurrent;
            _find.ReplaceAll += ReplaceAllMatches;
            _find.Closed += () => { _matches.Clear(); ScheduleDecor(); _input.Focus(); };

            Rebuild(userEdit: false);
        }

        private static VisualElement Decor(VisualElement parent, string cls)
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            e.AddToClassList(cls);
            e.style.position = Position.Absolute;
            e.style.display = DisplayStyle.None;
            parent.Add(e);
            return e;
        }

        // ═══════════════════════════ публичный API ═══════════════════════════

        /// <summary>Привязать историю правок документа (у каждого файла своя).</summary>
        public void SetHistory(UndoHistory history) => _history = history ?? new UndoHistory();

        /// <summary>Программная загрузка текста (открытие файла, смена вкладки): без истории и без TextEdited.</summary>
        public void Load(string text, int caret, int select, Vector2 scroll)
        {
            ClosePopups();
            _input.SetValueWithoutNotify(IdeDocument.Normalize(text));
            Rebuild(userEdit: false);
            _diags.Clear();
            _diagsCurrent = false;
            _gutterLines = -1;      // маркеры прошлого файла не должны остаться
            UpdateGutter();
            int c = Mathf.Clamp(caret, 0, _text.Length), s = Mathf.Clamp(select, 0, _text.Length);
            _input.SelectRange(c, s);
            _lastCaret = c;
            _lastSelect = s;
            // прокрутку ставим, когда новый текст разложен: до этого скроллер
            // обрезает значение по размеру ПРЕДЫДУЩЕГО документа
            void RestoreScroll(GeometryChangedEvent _)
            {
                _stack.UnregisterCallback<GeometryChangedEvent>(RestoreScroll);
                _scroll.scrollOffset = scroll;
                ScheduleDecor();
            }
            _stack.RegisterCallback<GeometryChangedEvent>(RestoreScroll);
            schedule.Execute(() => { _scroll.scrollOffset = scroll; ScheduleDecor(); }).StartingIn(32);
        }

        public void SetClassifier(SemanticClassifier classifier)
        {
            if (_hl.SetClassifier(classifier)) _highlight.text = _hl.Build(_text);
        }

        /// <summary>Диагностики текущего текста (версия должна совпадать — иначе позиции врут).</summary>
        public void SetDiagnostics(IReadOnlyList<Diagnostic> diags)
        {
            _diags.Clear();
            if (diags != null) _diags.AddRange(diags);
            _diagsCurrent = true;
            _gutterLines = -1; // перерисовать маркеры в номерах строк
            UpdateGutter();
            ScheduleDecor();
        }

        /// <summary>Фокус в поле кода.</summary>
        public void FocusCode() => _input.Focus();

        /// <summary>Выделение (anchor — неподвижный край) + прокрутка к каретке.</summary>
        public void Select(int anchor, int caret, bool reveal = true)
        {
            int c = Mathf.Clamp(caret, 0, _text.Length), a = Mathf.Clamp(anchor, 0, _text.Length);
            _input.Focus();
            _input.SelectRange(c, a);
            _history.Break();
            if (reveal) Reveal(c);
            ScheduleDecor();
        }

        /// <summary>Каретка на 1-based строку/колонку (формат диагностик).</summary>
        public void GoTo(int line1, int col1)
        {
            int off = TextUtil.OffsetOfClamped(_text, Math.Max(1, line1), Math.Max(1, col1));
            if (off < 0) off = _text.Length;
            Select(off, off);
        }

        public void ShowFind(bool withReplace)
        {
            string seed = null;
            int a = _input.selectIndex, c = _input.cursorIndex;
            if (a != c) seed = _text.Substring(Math.Min(a, c), Math.Abs(a - c));
            _find.Open(withReplace, seed);
        }

        /// <summary>Пауза периодической работы (вкладка/окно неактивны) — «выключено значит выключено».</summary>
        public void SetActive(bool active)
        {
            _active = active;
            if (active && panel != null) StartTick();
            else { StopTick(); ClosePopups(); }
        }

        public void ClosePopups()
        {
            _signatureGen++;
            _completion.Close();
            _signature.Hide();
            _hover.Hide();
            _completionStart = -1;
        }

        public void Dispose()
        {
            StopTick();
            if (_ownedFont != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_ownedFont);
                else UnityEngine.Object.DestroyImmediate(_ownedFont);
                _ownedFont = null;
            }
        }

        // ═══════════════════════════ текст ═══════════════════════════

        private void OnNativeChange(ChangeEvent<string> evt)
        {
            string before = evt.previousValue ?? "";
            string after = evt.newValue ?? "";
            // вставка из буфера обмена может принести \r\n — внутри IDE переводы строк только \n
            if (after.IndexOf('\r') >= 0)
            {
                int caret = _input.cursorIndex;
                int removedBefore = 0;
                for (int i = 0; i < caret && i < after.Length; i++) if (after[i] == '\r') removedBefore++;
                after = IdeDocument.Normalize(after);
                _input.SetValueWithoutNotify(after);
                _input.SelectRange(caret - removedBefore, caret - removedBefore);
            }
            _history.RecordEdit(before, _lastCaret, after.Length - before.Length, Time.realtimeSinceStartupAsDouble);
            Rebuild(userEdit: true);
            AfterTyped(after.Length == before.Length + 1 && _input.cursorIndex > 0 && _input.cursorIndex <= after.Length
                ? after[_input.cursorIndex - 1] : '\0', after.Length - before.Length);
        }

        /// <summary>Применить правку команды (атомарный шаг истории).</summary>
        private void ApplyEdit(EditResult r, bool typing = false)
        {
            string before = _text;
            if (!string.Equals(before, r.Text, StringComparison.Ordinal))
            {
                _history.RecordEdit(before, _input.cursorIndex, r.Text.Length - before.Length,
                    Time.realtimeSinceStartupAsDouble, atomic: !typing);
                _input.SetValueWithoutNotify(r.Text);
                Rebuild(userEdit: true);
            }
            _input.SelectRange(r.Caret, r.Anchor);
            _lastCaret = r.Caret;
            _lastSelect = r.Anchor;
            Reveal(r.Caret);
        }

        private void Rebuild(bool userEdit)
        {
            _text = _input.value ?? "";
            _lineStartsValid = false;
            _highlight.text = _hl.Build(_text);
            _diagsCurrent = false; // позиции подчёркиваний после правки врут — до новой компиляции
            UpdateGutter();
            if (_find.IsOpen) RecomputeMatches(keepIndex: true);
            ScheduleDecor();
            if (userEdit) TextEdited?.Invoke(_text);
        }

        private int[] LineStarts()
        {
            if (_lineStartsValid) return _lineStarts;
            var list = new List<int>(Math.Max(16, _text.Length / 30)) { 0 };
            for (int i = 0; i < _text.Length; i++) if (_text[i] == '\n') list.Add(i + 1);
            _lineStarts = list.ToArray();
            _lineStartsValid = true;
            return _lineStarts;
        }

        private int LineOf(int index)
        {
            var ls = LineStarts();
            int lo = 0, hi = ls.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (ls[mid] <= index) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        private void LineCol(int index, out int line1, out int col1)
        {
            int l = LineOf(index);
            line1 = l + 1;
            col1 = index - LineStarts()[l] + 1;
        }

        private bool InCommentOrString(int index)
        {
            int l = LineOf(index);
            int ls = LineStarts()[l];
            int le = EditCommands.LineEnd(_text, ls);
            return LineHighlighter.IsInCommentOrString(_text.Substring(ls, le - ls), index - ls, _hl.StartsInComment(l));
        }

        // ═══════════════════════════ клавиатура ═══════════════════════════

        private void OnKeyDown(KeyDownEvent e)
        {
            _hover.Hide();
            _hoverSince = -1;
            bool mod = e.actionKey || e.ctrlKey || e.commandKey;
            int anchor = _input.selectIndex, caret = _input.cursorIndex;

            // Модель событий шлёт ПАРУ: keycode-событие, затем символьное. Enter/Tab/Ctrl+Space
            // обрабатываем на keycode — символьного двойника глотаем, иначе лишний перенос/таб/пробел.
            if (e.keyCode == KeyCode.None)
            {
                char ch = e.character;
                if (ch == '\n' || ch == '\r' || ch == '\t' || (ch == ' ' && mod) || ch == 27) { Stop(e); return; }
                if (ReadOnly || ch < 32 || (mod && !e.altKey)) return;
                var typed = EditCommands.TypeChar(_text, anchor, caret, ch, InCommentOrString(Math.Min(anchor, caret)));
                if (typed.HasValue)
                {
                    ApplyEdit(typed.Value, typing: true);
                    AfterTyped(ch, 1);
                    Stop(e);
                }
                return; // обычная вставка — нативно, дальше OnNativeChange
            }

            if (_completion.HandleKey(e)) { Stop(e); return; }

            if (e.keyCode == KeyCode.Escape)
            {
                if (_signature.IsOpen || _hover.IsOpen) { HideSignature(); _hover.Hide(); Stop(e); return; }
                if (_find.IsOpen) { _find.Close(); Stop(e); return; }
                return;
            }

            if (mod && !e.altKey)
            {
                switch (e.keyCode)
                {
                    case KeyCode.Z: if (e.shiftKey) DoRedo(); else DoUndo(); Stop(e); return;
                    case KeyCode.Y: DoRedo(); Stop(e); return;
                    case KeyCode.Space:
                        if (e.shiftKey) ShowSignature(); else OpenCompletion();
                        Stop(e); return;
                    case KeyCode.Slash:
                        if (!ReadOnly) ApplyEdit(EditCommands.ToggleComment(_text, anchor, caret));
                        Stop(e); return;
                    case KeyCode.D:
                        if (!ReadOnly) ApplyEdit(EditCommands.Duplicate(_text, anchor, caret));
                        Stop(e); return;
                    case KeyCode.K:
                        if (e.shiftKey && !ReadOnly) { ApplyEdit(EditCommands.DeleteLines(_text, anchor, caret)); Stop(e); }
                        return;
                    case KeyCode.S: Command?.Invoke(e.shiftKey ? EditorCommand.SaveAll : EditorCommand.Save); Stop(e); return;
                    case KeyCode.F: ShowFind(false); Stop(e); return;
                    case KeyCode.H: ShowFind(true); Stop(e); return;
                    case KeyCode.G: Command?.Invoke(EditorCommand.GoToLine); Stop(e); return;
                    case KeyCode.B: Command?.Invoke(EditorCommand.Compile); Stop(e); return;
                    case KeyCode.N: Command?.Invoke(EditorCommand.NewFile); Stop(e); return;
                    case KeyCode.Return:
                    case KeyCode.KeypadEnter:
                        if (!ReadOnly) ApplyEdit(EditCommands.LineBelow(_text, caret));
                        Stop(e); return;
                }
                return; // A/C/V/X, слова стрелками — нативно
            }

            if (e.altKey && !mod && (e.keyCode == KeyCode.UpArrow || e.keyCode == KeyCode.DownArrow))
            {
                if (!ReadOnly)
                {
                    var moved = EditCommands.MoveLines(_text, anchor, caret, e.keyCode == KeyCode.UpArrow ? -1 : 1);
                    if (moved.HasValue) ApplyEdit(moved.Value);
                }
                Stop(e);
                return;
            }

            switch (e.keyCode)
            {
                case KeyCode.F3:
                    if (_find.IsOpen || _find.Query.Length > 0) FindStep(e.shiftKey ? -1 : 1);
                    Stop(e); return;
                case KeyCode.F12:
                    GoToDefinitionAtCaret();
                    Stop(e); return;
                case KeyCode.F7:
                    Command?.Invoke(EditorCommand.Compile);
                    Stop(e); return;
                case KeyCode.Tab:
                    if (!ReadOnly)
                    {
                        var ind = EditCommands.Indent(_text, anchor, caret, e.shiftKey);
                        if (ind.HasValue) ApplyEdit(ind.Value);
                    }
                    Stop(e); return;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    if (!ReadOnly)
                        ApplyEdit(e.shiftKey ? EditCommands.LineBelow(_text, caret) : EditCommands.SmartNewline(_text, anchor, caret));
                    Stop(e); return;
                case KeyCode.Backspace:
                    if (ReadOnly) return;
                    var bs = EditCommands.SmartBackspace(_text, anchor, caret);
                    if (bs.HasValue)
                    {
                        ApplyEdit(bs.Value, typing: true);
                        AfterTyped('\0', -1);
                        Stop(e);
                    }
                    return;
            }
        }

        private void Stop(EventBase e)
        {
            e.StopImmediatePropagation();
            _input.focusController?.IgnoreEvent(e);
        }

        private void OnNavigation(EventBase e)
        {
            // навигация (Tab/Enter/Esc в игровой панели) не должна уводить фокус из кода
            if (e.target is VisualElement ve && (ve == _input || _input.Contains(ve)))
            {
                e.StopPropagation();
                _input.focusController?.IgnoreEvent(e);
            }
        }

        private void DoUndo()
        {
            if (ReadOnly) return;
            if (_history.TryUndo(_text, _input.cursorIndex, out var entry)) ApplyRestored(entry);
        }

        private void DoRedo()
        {
            if (ReadOnly) return;
            if (_history.TryRedo(_text, _input.cursorIndex, out var entry)) ApplyRestored(entry);
        }

        private void ApplyRestored(UndoHistory.Entry entry)
        {
            ClosePopups();
            _input.SetValueWithoutNotify(entry.Text);
            Rebuild(userEdit: true);
            int c = Mathf.Clamp(entry.Caret, 0, _text.Length);
            _input.SelectRange(c, c);
            _lastCaret = _lastSelect = c;
            Reveal(c);
        }

        // ═══════════════════════════ автодополнение и подсказки ═══════════════════════════

        private void AfterTyped(char ch, int delta)
        {
            int caret = _input.cursorIndex;
            _lastCaret = caret;
            _lastSelect = _input.selectIndex;

            if (_completion.IsOpen)
            {
                if (ch == '.') { OpenCompletion(); }
                else if (caret <= _completionStart || (ch != '\0' && !TextUtil.IsWordChar(ch))) _completion.Close();
                else if (!_completion.Filter(_text.Substring(_completionStart, caret - _completionStart))) _completion.Close();
                else PlaceCompletion();
            }
            else if (delta == 1 && ch != '\0' && !InCommentOrString(caret))
            {
                if (ch == '.') OpenCompletion();
                else if (TextUtil.IsWordChar(ch))
                {
                    int ws = TextUtil.WordStartBefore(_text, caret);
                    if (caret - ws >= 2 && !char.IsDigit(_text[ws])) OpenCompletion();
                }
                else if (ch == ' ')
                {
                    // «event » и «new » — сразу список событий/типов
                    int e = caret - 1, s = TextUtil.WordStartBefore(_text, e);
                    string w = _text.Substring(s, e - s);
                    if (w == "event" || w == "new") OpenCompletion();
                }
            }

            if (ch == '(' || ch == ',') ShowSignature();
            else if (_signature.IsOpen) ShowSignature(); // обновить активный параметр / закрыть за скобкой
        }

        private void OpenCompletion()
        {
            if (Assist == null) return;
            int caret = _input.cursorIndex;
            LineCol(caret, out int line1, out int col1);
            List<CompletionItem> items;
            try { items = Assist.Complete(line1, col1); }
            catch { items = null; }
            if (items == null || items.Count == 0) { _completion.Close(); return; }
            _completionStart = TextUtil.WordStartBefore(_text, caret);
            _completion.SetItems(items);
            if (!_completion.Filter(_text.Substring(_completionStart, caret - _completionStart))) { _completion.Close(); return; }
            HideSignature();
            PlaceCompletion();
        }

        private void PlaceCompletion()
        {
            var r = IndexRect(Math.Max(0, _completionStart));
            var p = _stack.ChangeCoordinatesTo(this, new Vector2(r.x, r.yMax));
            _completion.Show(new Vector2(Mathf.Max(0f, Mathf.Min(p.x, layout.width - 320f)), p.y));
            // не влез снизу — над строкой (высота известна после layout)
            schedule.Execute(() =>
            {
                if (!_completion.IsOpen) return;
                float h = _completion.layout.height;
                if (p.y + h > layout.height && p.y - _lineHeight - h > 0)
                    _completion.style.top = p.y - _lineHeight - h;
            });
        }

        private void CommitCompletion(CompletionItem item)
        {
            _completion.Close();
            if (item == null || ReadOnly) return;
            int caret = _input.cursorIndex;
            int start = Mathf.Clamp(_completionStart < 0 ? caret : _completionStart, 0, _text.Length);
            int end = TextUtil.WordEndAfter(_text, caret);
            string insert = item.InsertText ?? item.Label;
            int caretInIns, selLen = 0;
            if (item.IsSnippet)
            {
                string indent = EditCommands.LineIndent(_text, start);
                insert = SnippetText.Expand(insert, indent, EditCommands.IndentUnit, out caretInIns, out selLen);
            }
            else caretInIns = insert.Length;
            string res = _text.Substring(0, start) + insert + _text.Substring(end);
            int c = start + caretInIns;
            ApplyEdit(new EditResult(res, c + selLen, c));
            _input.Focus();
            _completionStart = -1;
            if (insert.IndexOf('(') >= 0 && insert.IndexOf('\n') < 0) ShowSignature();
        }

        private void ShowSignature()
        {
            int gen = ++_signatureGen;
            if (Assist == null) return;
            int caret = _input.cursorIndex;
            LineCol(caret, out int line1, out int col1);
            SignatureInfo sig;
            try { sig = Assist.Signature(line1, col1); }
            catch { sig = null; }
            if (sig == null) { _signature.Hide(); return; }

            var sb = new StringBuilder();
            string label = sig.Label ?? "";
            // активный параметр — жирным: ищем i-й параметр после '('
            int paren = label.IndexOf('(');
            int at = -1, len = 0;
            if (paren >= 0 && sig.ActiveParameter >= 0 && sig.ActiveParameter < sig.Parameters.Count)
            {
                int from = paren + 1;
                for (int i = 0; i <= sig.ActiveParameter && from <= label.Length; i++)
                {
                    int f = label.IndexOf(sig.Parameters[i], from, StringComparison.Ordinal);
                    if (f < 0) { at = -1; break; }
                    at = f;
                    len = sig.Parameters[i].Length;
                    from = f + len;
                }
            }
            if (at >= 0)
                sb.Append(RichText.Escape(label.Substring(0, at))).Append("<b><color=#FFFFFF>")
                  .Append(RichText.Escape(label.Substring(at, len))).Append("</color></b>")
                  .Append(RichText.Escape(label.Substring(at + len)));
            else sb.Append(RichText.Escape(label));
            if (!string.IsNullOrEmpty(sig.Documentation)) sb.Append('\n').Append(RichText.FromMarkdown(sig.Documentation));

            string rich = sb.ToString();
            schedule.Execute(() =>
            {
                if (panel == null || gen != _signatureGen) return;
                var r = IndexRect(Mathf.Min(caret, _text.Length));
                var p = _stack.ChangeCoordinatesTo(this, new Vector2(r.x, r.y));
                _signature.Show(rich, p, above: true, _lineHeight);
            }).StartingIn(16);
        }

        private void HideSignature()
        {
            _signatureGen++;
            _signature.Hide();
        }

        private void GoToDefinitionAtCaret()
        {
            if (Assist == null) return;
            LineCol(_input.cursorIndex, out int line1, out int col1);
            try { Assist.GoToDefinition(line1, col1); }
            catch { /* подсказки не должны ронять редактор */ }
        }

        // ═══════════════════════════ мышь ═══════════════════════════

        private void OnPointerMove(PointerMoveEvent e)
        {
            var p = _input.ChangeCoordinatesTo(_stack, e.localPosition);
            if (_hover.IsOpen && (p - _hoverPos).sqrMagnitude > 64f) _hover.Hide();
            _hoverPos = p;
            _hoverSince = Time.realtimeSinceStartupAsDouble;
        }

        private void OnPointerUp(PointerUpEvent e)
        {
            _completion.Close();
            _history.Break();
            // Ctrl/Cmd+клик — переход к определению (каретка уже встала под курсор)
            if (e.actionKey || e.ctrlKey || e.commandKey)
                schedule.Execute(GoToDefinitionAtCaret);
        }

        private void OnAnyPointerDown(PointerDownEvent e)
        {
            if (!_completion.IsOpen) return;
            if (e.target is VisualElement ve && (ve == _completion || _completion.Contains(ve))) return;
            _completion.Close();
        }

        private void UpdateHover(double now)
        {
            if (!_pointerInside || _hoverSince < 0 || _hover.IsOpen || now - _hoverSince < 0.55) return;
            _hoverSince = -1;
            int index = PointToIndex(_hoverPos);
            if (index < 0 || index >= _text.Length) return;

            var sb = new StringBuilder();
            // диагностика под курсором — первой: ради неё и наводят
            if (_diagsCurrent)
                foreach (var d in _diags)
                {
                    int off = TextUtil.OffsetOfClamped(_text, d.Line, d.Column);
                    if (off < 0) continue;
                    int len = Math.Max(1, TextUtil.WordEndAfter(_text, off) - off);
                    if (index < off || index >= off + len) continue;
                    string color = d.Severity == Severity.Error ? _gutterErrorHex : d.Severity == Severity.Warning ? _gutterWarningHex : "9D9D9D";
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append("<color=#").Append(color).Append('>').Append(d.Code).Append("</color> ").Append(RichText.Escape(d.Message));
                }
            if (Assist != null && TextUtil.IsWordChar(_text[index]))
            {
                LineCol(index, out int line1, out int col1);
                string md = null;
                try { md = Assist.Hover(line1, col1); }
                catch { }
                if (!string.IsNullOrEmpty(md))
                {
                    if (sb.Length > 0) sb.Append("\n\n");
                    sb.Append(RichText.FromMarkdown(md));
                }
            }
            if (sb.Length == 0) return;
            var r = IndexRect(index);
            var p = _stack.ChangeCoordinatesTo(this, new Vector2(_hoverPos.x, r.yMax));
            _hover.Show(sb.ToString(), p, above: false, _lineHeight);
        }

        // ═══════════════════════════ поиск ═══════════════════════════

        private void OnFindQuery() => RecomputeMatches(keepIndex: false);

        private void RecomputeMatches(bool keepIndex)
        {
            _matches = FindBar.FindAll(_text, _find.Query, _find.MatchCase, _find.WholeWord);
            _matchesText = _text;
            if (!keepIndex || _matchIndex >= _matches.Count)
            {
                // ближайшее совпадение от каретки
                int caret = Math.Min(_input.cursorIndex, _input.selectIndex);
                _matchIndex = _matches.Count == 0 ? -1 : 0;
                for (int i = 0; i < _matches.Count; i++) if (_matches[i] >= caret) { _matchIndex = i; break; }
            }
            _find.SetCount(_matchIndex, _matches.Count);
            ScheduleDecor();
            if (!keepIndex && _matchIndex >= 0) Reveal(_matches[_matchIndex]);
        }

        private void FindStep(int dir)
        {
            // текст мог измениться (в т.ч. пока панель закрыта) — позиции пересчитываем,
            // и на первом шаге встаём на ближайшее совпадение, а не перескакиваем через него
            if (!ReferenceEquals(_matchesText, _text) || _matches.Count == 0)
            {
                RecomputeMatches(false);
                if (_matches.Count == 0) return;
                if (_matchIndex >= 0) { SelectMatch(); return; }
            }
            if (_matches.Count == 0) return;
            _matchIndex = ((_matchIndex + dir) % _matches.Count + _matches.Count) % _matches.Count;
            SelectMatch();
        }

        private void SelectMatch()
        {
            if (_matchIndex < 0 || _matchIndex >= _matches.Count) return;
            int s = _matches[_matchIndex];
            _input.SelectRange(s + _find.Query.Length, s);
            _lastCaret = s + _find.Query.Length;
            _lastSelect = s;
            Reveal(s);
            _find.SetCount(_matchIndex, _matches.Count);
            ScheduleDecor();
        }

        private void ReplaceCurrent()
        {
            if (ReadOnly || _matchIndex < 0 || _matchIndex >= _matches.Count) return;
            int s = _matches[_matchIndex];
            string rep = _find.Replacement;
            string res = _text.Substring(0, s) + rep + _text.Substring(s + _find.Query.Length);
            ApplyEdit(new EditResult(res, s + rep.Length, s + rep.Length));
            RecomputeMatches(keepIndex: false);
            // следующее совпадение — строго после вставленного текста (замена может содержать запрос)
            int after = s + rep.Length;
            _matchIndex = -1;
            for (int i = 0; i < _matches.Count; i++) if (_matches[i] >= after) { _matchIndex = i; break; }
            if (_matchIndex < 0 && _matches.Count > 0) _matchIndex = 0;
            SelectMatch();
        }

        private void ReplaceAllMatches()
        {
            if (ReadOnly || _matches.Count == 0) return;
            string q = _find.Query, rep = _find.Replacement;
            var sb = new StringBuilder(_text.Length);
            int pos = 0;
            foreach (int s in _matches)
            {
                sb.Append(_text, pos, s - pos).Append(rep);
                pos = s + q.Length;
            }
            sb.Append(_text, pos, _text.Length - pos);
            int caret = Math.Min(_input.cursorIndex, sb.Length);
            ApplyEdit(new EditResult(sb.ToString(), caret, caret));
            RecomputeMatches(keepIndex: false);
        }

        // ═══════════════════════════ геометрия ═══════════════════════════

        // внутренний TextElement поля ввода (не подпись поля — та тоже TextElement)
        private TextElement InputText =>
            _inputText ??= _input.Q(className: "unity-base-text-field__input")?.Q<TextElement>() ?? _input.Q<TextElement>();

        private void EnsureMetrics()
        {
            if (_metricsValid) return;
            var te = InputText;
            if (te == null) return;
            var sel = _input.textSelection;
            Vector2 p0 = sel.GetCursorPositionFromStringIndex(0);
            var ls = LineStarts();
            float lh = -1f;
            if (ls.Length >= 2)
            {
                Vector2 p1 = sel.GetCursorPositionFromStringIndex(ls[1]);
                lh = p1.y - p0.y;
            }
            if (!(lh > 1f))
            {
                var one = _highlight.MeasureTextSize("M", 0, MeasureMode.Undefined, 0, MeasureMode.Undefined);
                var two = _highlight.MeasureTextSize("M\nM", 0, MeasureMode.Undefined, 0, MeasureMode.Undefined);
                lh = Mathf.Max(1f, two.y - one.y);
            }
            _lineHeight = lh;
            // позиция курсора — верх строки или её низ: определяем по первой строке
            _cursorAtBottom = p0.y > lh * 0.5f;
            _metricsValid = te.layout.width > 0f;
        }

        /// <summary>Прямоугольник позиции в координатах stack: x, верх строки, ширина 0, высота строки.</summary>
        private Rect IndexRect(int index)
        {
            EnsureMetrics();
            var te = InputText;
            if (te == null) return new Rect(0, 0, 0, _lineHeight);
            index = Mathf.Clamp(index, 0, _text.Length);
            Vector2 p = _input.textSelection.GetCursorPositionFromStringIndex(index);
            var local = te.contentRect.position + p;
            var inStack = te.ChangeCoordinatesTo(_stack, local);
            float top = _cursorAtBottom ? inStack.y - _lineHeight : inStack.y;
            return new Rect(inStack.x, top, 0f, _lineHeight);
        }

        /// <summary>Индекс символа под точкой (координаты stack); -1 — мимо текста.</summary>
        private int PointToIndex(Vector2 p)
        {
            var ls = LineStarts();
            if (ls.Length == 0) return -1;
            int lo = 0, hi = ls.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (IndexRect(ls[mid]).y <= p.y) lo = mid; else hi = mid - 1;
            }
            var lineTop = IndexRect(ls[lo]).y;
            if (p.y < lineTop || p.y > lineTop + _lineHeight) return -1;
            int start = ls[lo], end = EditCommands.LineEnd(_text, start);
            // символ, чья левая граница ≤ x < правой
            int a = start, b = end;
            while (a < b)
            {
                int mid = (a + b + 1) / 2;
                if (IndexRect(mid).x <= p.x) a = mid; else b = mid - 1;
            }
            if (a >= end) return -1;
            return a;
        }

        private void Reveal(int index)
        {
            // после layout: позиция каретки нового текста известна только тогда
            schedule.Execute(() =>
            {
                var r = IndexRect(index);
                var vp = _scroll.contentViewport.layout;
                var off = _scroll.scrollOffset;
                if (r.y < off.y) off.y = r.y;
                else if (r.y + _lineHeight > off.y + vp.height) off.y = r.y + _lineHeight - vp.height;
                if (r.x < off.x + 16f) off.x = Mathf.Max(0f, r.x - 48f);
                else if (r.x > off.x + vp.width - 16f) off.x = r.x - vp.width + 64f;
                _scroll.scrollOffset = off;
                ScheduleDecor();
            }).StartingIn(16);
        }

        private void OnScrolled(float _)
        {
            _gutterText.style.top = -_scroll.scrollOffset.y;
            _hover.Hide();
            _hoverSince = -1;
            if (_completion.IsOpen) _completion.Close();
            if (_signature.IsOpen) HideSignature();
            ScheduleDecor();
        }

        private void ScheduleDecor()
        {
            if (_decorScheduled) return;
            _decorScheduled = true;
            // геометрия текста пересчитывается в layout — украшения ставим кадром позже,
            // иначе позиции берутся от старого текста
            schedule.Execute(() => { _decorScheduled = false; RefreshDecorations(); }).StartingIn(16);
        }

        private void RefreshDecorations()
        {
            if (panel == null) return;
            _metricsValid = false;
            EnsureMetrics();
            int caret = _input.cursorIndex;
            float width = Mathf.Max(_stack.layout.width, _scroll.contentViewport.layout.width);

            // текущая строка
            var lr = IndexRect(EditCommands.LineStart(_text, caret));
            Place(_currentLine, 0f, lr.y, width, _lineHeight);

            // парные скобки
            int match = EditCommands.MatchBracket(_text, caret, out int at);
            if (match >= 0 && at >= 0)
            {
                PlaceChar(_bracketA, at);
                PlaceChar(_bracketB, match);
            }
            else
            {
                _bracketA.style.display = DisplayStyle.None;
                _bracketB.style.display = DisplayStyle.None;
            }

            // подчёркивания диагностик
            int used = 0;
            if (_diagsCurrent)
            {
                foreach (var d in _diags)
                {
                    if (used >= 100) break;
                    if (d.Line <= 0) continue;
                    int off = TextUtil.OffsetOfClamped(_text, d.Line, d.Column);
                    if (off < 0) continue;
                    int end = Math.Max(off + 1, TextUtil.WordEndAfter(_text, off));
                    end = Math.Min(end, EditCommands.LineEnd(_text, off) == off ? off + 1 : EditCommands.LineEnd(_text, off));
                    var a = IndexRect(off);
                    var b = IndexRect(Math.Min(end, _text.Length));
                    float w = b.y == a.y && b.x > a.x ? b.x - a.x : Mathf.Max(6f, _lineHeight * 0.5f);
                    var bar = Pool(_squiggles, _front, used++, "sal-squiggle");
                    bar.EnableInClassList("sal-squiggle--error", d.Severity == Severity.Error);
                    bar.EnableInClassList("sal-squiggle--warning", d.Severity == Severity.Warning);
                    bar.EnableInClassList("sal-squiggle--info", d.Severity == Severity.Info);
                    Place(bar, a.x, a.y + _lineHeight - 2f, w, 2f);
                }
            }
            for (int k = used; k < _squiggles.Count; k++) _squiggles[k].style.display = DisplayStyle.None;

            // совпадения поиска — только видимые
            used = 0;
            if (_find.IsOpen && _matches.Count > 0)
            {
                var off = _scroll.scrollOffset;
                float vh = _scroll.contentViewport.layout.height;
                int qlen = _find.Query.Length;
                int firstLine = LineAtY(off.y - _lineHeight), lastLine = LineAtY(off.y + vh + _lineHeight);
                var ls = LineStarts();
                int from = ls[Mathf.Clamp(firstLine, 0, ls.Length - 1)];
                int to = lastLine + 1 < ls.Length ? ls[lastLine + 1] : _text.Length;
                for (int i = 0; i < _matches.Count && used < 300; i++)
                {
                    int s = _matches[i];
                    if (s < from || s > to) continue;
                    var a = IndexRect(s);
                    var b = IndexRect(Math.Min(s + qlen, _text.Length));
                    float w = b.y == a.y ? Mathf.Max(2f, b.x - a.x) : 8f;
                    var m = Pool(_findMarks, _back, used++, "sal-find-match");
                    m.EnableInClassList("sal-find-match--current", i == _matchIndex);
                    Place(m, a.x, a.y, w, _lineHeight);
                }
            }
            for (int k = used; k < _findMarks.Count; k++) _findMarks[k].style.display = DisplayStyle.None;
        }

        private int LineAtY(float y)
        {
            var ls = LineStarts();
            int lo = 0, hi = ls.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (IndexRect(ls[mid]).y <= y) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        private void PlaceChar(VisualElement e, int index)
        {
            var a = IndexRect(index);
            var b = IndexRect(Math.Min(index + 1, _text.Length));
            float w = b.y == a.y && b.x > a.x ? b.x - a.x : 8f;
            Place(e, a.x, a.y, w, _lineHeight);
        }

        private static void Place(VisualElement e, float x, float y, float w, float h)
        {
            e.style.left = x;
            e.style.top = y;
            e.style.width = w;
            e.style.height = h;
            e.style.display = DisplayStyle.Flex;
        }

        private static VisualElement Pool(List<VisualElement> pool, VisualElement parent, int i, string cls)
        {
            while (pool.Count <= i)
            {
                var e = new VisualElement { pickingMode = PickingMode.Ignore };
                e.AddToClassList(cls);
                e.style.position = Position.Absolute;
                parent.Add(e);
                pool.Add(e);
            }
            return pool[i];
        }

        // ═══════════════════════════ номера строк ═══════════════════════════

        private void UpdateGutter()
        {
            int lines = LineStarts().Length;
            if (lines == _gutterLines) return;
            _gutterLines = lines;

            // строки с ошибками/предупреждениями — цветом номера
            Dictionary<int, Severity> marks = null;
            if (_diagsCurrent && _diags.Count > 0)
            {
                marks = new Dictionary<int, Severity>();
                foreach (var d in _diags)
                {
                    if (d.Line <= 0) continue;
                    if (!marks.TryGetValue(d.Line, out var s) || d.Severity > s) marks[d.Line] = d.Severity;
                }
            }
            var sb = new StringBuilder(lines * 5);
            for (int i = 1; i <= lines; i++)
            {
                if (i > 1) sb.Append('\n');
                if (marks != null && marks.TryGetValue(i, out var sev) && sev != Severity.Info)
                    sb.Append("<color=#").Append(sev == Severity.Error ? _gutterErrorHex : _gutterWarningHex).Append('>')
                      .Append(i).Append("</color>");
                else sb.Append(i);
            }
            _gutterText.text = sb.ToString();
        }

        // ═══════════════════════════ стиль и тик ═══════════════════════════

        private void OnCustomStyle(CustomStyleResolvedEvent e)
        {
            var cs = e.customStyle;
            var p = CodePalette.Default();
            bool any = false;
            for (int i = 1; i < PaletteStyle.Length; i++)
                if (cs.TryGetValue(PaletteStyle[i], out var c)) { p.Set((SalColor)i, c); any = true; }
            if (cs.TryGetValue(GutterErrorProp, out var ge)) _gutterErrorHex = ColorUtility.ToHtmlStringRGB(ge);
            if (cs.TryGetValue(GutterWarningProp, out var gw)) _gutterWarningHex = ColorUtility.ToHtmlStringRGB(gw);
            if (!any) return;
            _palette = p;
            _hl.SetPalette(_palette);
            _highlight.text = _hl.Build(_text);
            _gutterLines = -1;
            UpdateGutter();
        }

        private void StartTick()
        {
            if (_tick != null) { _tick.Resume(); return; }
            _tick = schedule.Execute(Tick).Every(50);
        }

        private void StopTick() => _tick?.Pause();

        private void Tick()
        {
            if (!_active) return;
            int c = _input.cursorIndex, s = _input.selectIndex;
            if (c != _lastCaret || s != _lastSelect)
            {
                bool jumped = Math.Abs(c - _lastCaret) > 1;
                bool moved = c != _lastCaret;
                _lastCaret = c;
                _lastSelect = s;
                // TextField занимает всю высоту содержимого и сам не скроллит —
                // за кареткой (стрелки, PageUp/Down, клик, ввод) следим мы
                if (moved) Reveal(c);
                if (jumped) _history.Break(); // клик/переход — новая группа правок
                if (_completion.IsOpen && (c < _completionStart || c > TextUtil.WordEndAfter(_text, Math.Max(0, _completionStart))))
                    _completion.Close();
                if (_signature.IsOpen) ShowSignature();
                ScheduleDecor();
                CaretMoved?.Invoke();
            }
            UpdateHover(Time.realtimeSinceStartupAsDouble);
        }
    }
}
