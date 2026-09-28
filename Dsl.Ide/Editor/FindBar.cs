using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Dsl.Ide
{
    /// <summary>
    /// Панель поиска/замены поверх кода (Ctrl+F / Ctrl+H). Сама ничего не ищет —
    /// поднимает события, редактор считает совпадения и подсвечивает их.
    /// Enter — следующее, Shift+Enter — предыдущее, Esc — закрыть.
    /// </summary>
    public sealed class FindBar : VisualElement
    {
        private readonly TextField _find;
        private readonly TextField _replace;
        private readonly VisualElement _replaceRow;
        private readonly Label _count;
        private readonly Button _caseBtn;
        private readonly Button _wordBtn;

        public bool MatchCase { get; private set; }
        public bool WholeWord { get; private set; }
        public bool IsOpen { get; private set; }
        public string Query => _find.value ?? "";
        public string Replacement => _replace.value ?? "";

        public event Action QueryChanged;
        public event Action Next;
        public event Action Previous;
        public event Action ReplaceOne;
        public event Action ReplaceAll;
        public event Action Closed;

        public FindBar()
        {
            AddToClassList("sal-find");
            style.position = Position.Absolute;
            style.display = DisplayStyle.None;

            var row = new VisualElement();
            row.AddToClassList("sal-find-row");
            _find = new TextField { isDelayed = false };
            _find.AddToClassList("sal-find-input");
            _find.selectAllOnFocus = true;
            _find.RegisterValueChangedCallback(_ => QueryChanged?.Invoke());
            _find.RegisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
            row.Add(_find);

            _caseBtn = Toggle("Aa", "Учитывать регистр", () => { MatchCase = !MatchCase; Sync(); QueryChanged?.Invoke(); });
            _wordBtn = Toggle("W", "Слово целиком", () => { WholeWord = !WholeWord; Sync(); QueryChanged?.Invoke(); });
            row.Add(_caseBtn);
            row.Add(_wordBtn);

            _count = new Label("0/0");
            _count.AddToClassList("sal-find-count");
            row.Add(_count);
            row.Add(Small("↑", "Предыдущее (Shift+Enter, Shift+F3)", () => Previous?.Invoke()));
            row.Add(Small("↓", "Следующее (Enter, F3)", () => Next?.Invoke()));
            row.Add(Small("×", "Закрыть (Esc)", Close));
            Add(row);

            _replaceRow = new VisualElement();
            _replaceRow.AddToClassList("sal-find-row");
            _replace = new TextField();
            _replace.AddToClassList("sal-find-input");
            _replace.RegisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
            _replaceRow.Add(_replace);
            _replaceRow.Add(Small("Заменить", "Заменить текущее", () => ReplaceOne?.Invoke()));
            _replaceRow.Add(Small("Все", "Заменить все", () => ReplaceAll?.Invoke()));
            Add(_replaceRow);
            Sync();
        }

        private static Button Small(string text, string hint, Action onClick)
        {
            var b = new Button(onClick) { text = text, tooltip = hint };
            b.AddToClassList("sal-find-button");
            return b;
        }

        private Button Toggle(string text, string hint, Action onClick)
        {
            var b = Small(text, hint, onClick);
            b.AddToClassList("sal-find-toggle");
            return b;
        }

        private void Sync()
        {
            _caseBtn.EnableInClassList("sal-find-toggle--on", MatchCase);
            _wordBtn.EnableInClassList("sal-find-toggle--on", WholeWord);
        }

        public void Open(bool withReplace, string seed)
        {
            style.display = DisplayStyle.Flex;
            _replaceRow.style.display = withReplace ? DisplayStyle.Flex : DisplayStyle.None;
            IsOpen = true;
            if (!string.IsNullOrEmpty(seed) && seed.IndexOf('\n') < 0) _find.value = seed;
            _find.Focus();
            _find.SelectAll();
            QueryChanged?.Invoke();
        }

        public void Close()
        {
            if (!IsOpen) return;
            style.display = DisplayStyle.None;
            IsOpen = false;
            Closed?.Invoke();
        }

        public void SetCount(int current, int total)
        {
            _count.text = total == 0 ? (Query.Length == 0 ? "" : "нет") : $"{current + 1}/{total}";
            _count.EnableInClassList("sal-find-count--none", total == 0 && Query.Length > 0);
        }

        private void OnKey(KeyDownEvent e)
        {
            if (e.keyCode == KeyCode.Escape) { Close(); Stop(e); return; }
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                if (e.target == _replace && !e.shiftKey) ReplaceOne?.Invoke();
                else if (e.shiftKey) Previous?.Invoke();
                else Next?.Invoke();
                Stop(e);
                return;
            }
            if (e.keyCode == KeyCode.F3) { if (e.shiftKey) Previous?.Invoke(); else Next?.Invoke(); Stop(e); return; }
            // парный символьный '\n' от Enter глотаем
            if (e.keyCode == KeyCode.None && (e.character == '\n' || e.character == '\r')) Stop(e);
        }

        private static void Stop(EventBase e)
        {
            e.StopImmediatePropagation();
            (e.target as VisualElement)?.focusController?.IgnoreEvent(e);
        }

        /// <summary>Все вхождения запроса (начала). Пустой запрос — пусто.</summary>
        public static List<int> FindAll(string text, string query, bool matchCase, bool wholeWord, int limit = 10000)
        {
            var r = new List<int>();
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text)) return r;
            var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int i = 0;
            while (r.Count < limit)
            {
                int at = text.IndexOf(query, i, cmp);
                if (at < 0) break;
                if (!wholeWord || IsWholeWord(text, at, query.Length)) r.Add(at);
                i = at + Math.Max(1, query.Length);
            }
            return r;
        }

        private static bool IsWholeWord(string t, int at, int len)
        {
            bool leftOk = at == 0 || !(char.IsLetterOrDigit(t[at - 1]) || t[at - 1] == '_');
            int e = at + len;
            bool rightOk = e >= t.Length || !(char.IsLetterOrDigit(t[e]) || t[e] == '_');
            return leftOk && rightOk;
        }
    }
}
