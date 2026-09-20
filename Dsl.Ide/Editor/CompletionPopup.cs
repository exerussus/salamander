using System;
using System.Collections.Generic;
using Dsl.Tooling;
using UnityEngine;
using UnityEngine.UIElements;

namespace Dsl.Ide
{
    /// <summary>
    /// Попап автодополнения: список у каретки + панель документации выбранного
    /// пункта. Фильтрует и ранжирует сам (префикс → без регистра → «горбы»
    /// camelCase → вхождение), сервис отдаёт полный набор для контекста.
    /// Вид — классы .sal-popup* в USS.
    /// </summary>
    public sealed class CompletionPopup : VisualElement
    {
        private const float RowHeight = 20f;
        private const int MaxVisibleRows = 10;

        private readonly ListView _list;
        private readonly Label _doc;
        private readonly List<CompletionItem> _all = new List<CompletionItem>();
        private readonly List<CompletionItem> _items = new List<CompletionItem>();
        private readonly List<(CompletionItem item, int score, int order)> _scratch = new List<(CompletionItem, int, int)>();

        public bool IsOpen { get; private set; }
        public event Action<CompletionItem> Committed;

        public CompletionItem Selected =>
            _list.selectedIndex >= 0 && _list.selectedIndex < _items.Count ? _items[_list.selectedIndex] : null;

        public CompletionPopup()
        {
            AddToClassList("sal-popup");
            style.position = Position.Absolute;
            style.display = DisplayStyle.None;
            pickingMode = PickingMode.Position;

            _list = new ListView(_items, RowHeight, MakeRow, BindRow)
            {
                selectionType = SelectionType.Single,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
            };
            _list.AddToClassList("sal-popup-list");
            _list.selectionChanged += _ => UpdateDoc();
            Add(_list);

            _doc = new Label { enableRichText = true };
            _doc.AddToClassList("sal-popup-doc");
            Add(_doc);
        }

        public void SetItems(List<CompletionItem> items)
        {
            _all.Clear();
            if (items != null) _all.AddRange(items);
        }

        /// <summary>Отфильтровать по набранному префиксу. false — показывать нечего.</summary>
        public bool Filter(string prefix)
        {
            prefix ??= "";
            _scratch.Clear();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _all.Count; i++)
            {
                var it = _all[i];
                if (string.IsNullOrEmpty(it.Label)) continue;
                int score = Score(it.Label, prefix);
                if (score < 0) continue;
                // одно имя из разных источников (свой класс и хостовый) — показываем раз
                if (!seen.Add(it.Label + "\u0001" + (int)it.Kind)) continue;
                _scratch.Add((it, score, i));
            }
            if (prefix.Length > 0)
                _scratch.Sort((a, b) => a.score != b.score ? a.score - b.score
                                      : a.item.Label.Length != b.item.Label.Length ? a.item.Label.Length - b.item.Label.Length
                                      : a.order - b.order);
            _items.Clear();
            foreach (var s in _scratch) _items.Add(s.item);
            if (_items.Count == 0) return false;
            // единственный пункт, уже набранный целиком, — подсказывать нечего
            if (_items.Count == 1 && string.Equals(_items[0].Label, prefix, StringComparison.Ordinal)) return false;
            _list.RefreshItems();
            _list.selectedIndex = 0;
            _list.ScrollToItem(0);
            _list.style.height = Mathf.Min(_items.Count, MaxVisibleRows) * RowHeight + 2f;
            UpdateDoc();
            return true;
        }

        /// <summary>-1 — не подходит; меньше — лучше.</summary>
        public static int Score(string label, string prefix)
        {
            if (prefix.Length == 0) return 0;
            if (label.StartsWith(prefix, StringComparison.Ordinal)) return 0;
            if (label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 1;
            if (CamelMatch(label, prefix)) return 2;
            if (label.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0) return 3;
            return -1;
        }

        // «горбы»: IsTE → IsTriggerEnabled, gvc → GetValueClass (первая буква и заглавные)
        private static bool CamelMatch(string label, string prefix)
        {
            int p = 0;
            for (int i = 0; i < label.Length && p < prefix.Length; i++)
            {
                bool hump = i == 0 || char.IsUpper(label[i]) || label[i - 1] == '_';
                if (hump && char.ToLowerInvariant(label[i]) == char.ToLowerInvariant(prefix[p])) p++;
                else if (p > 0 && !hump && char.ToLowerInvariant(label[i]) == char.ToLowerInvariant(prefix[p]) &&
                         i > 0 && char.ToLowerInvariant(label[i - 1]) == char.ToLowerInvariant(prefix[p - 1])) p++;
            }
            return p == prefix.Length;
        }

        public void Show(Vector2 position)
        {
            style.left = position.x;
            style.top = position.y;
            style.display = DisplayStyle.Flex;
            IsOpen = true;
        }

        public void Close()
        {
            style.display = DisplayStyle.None;
            IsOpen = false;
        }

        /// <summary>true — клавиша съедена попапом.</summary>
        public bool HandleKey(KeyDownEvent e)
        {
            if (!IsOpen) return false;
            switch (e.keyCode)
            {
                case KeyCode.DownArrow: Move(1); return true;
                case KeyCode.UpArrow: Move(-1); return true;
                case KeyCode.PageDown: Move(MaxVisibleRows - 1); return true;
                case KeyCode.PageUp: Move(-(MaxVisibleRows - 1)); return true;
                case KeyCode.Escape: Close(); return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                case KeyCode.Tab:
                    var sel = Selected;
                    if (sel != null) Committed?.Invoke(sel);
                    else Close();
                    return true;
            }
            return false;
        }

        private void Move(int delta)
        {
            if (_items.Count == 0) return;
            int i = Mathf.Clamp(_list.selectedIndex + delta, 0, _items.Count - 1);
            _list.selectedIndex = i;
            _list.ScrollToItem(i);
        }

        private void UpdateDoc()
        {
            var it = Selected;
            string text = "";
            if (it != null)
            {
                if (!string.IsNullOrEmpty(it.Detail)) text = "<color=#DCDCAA>" + RichText.Escape(it.Detail) + "</color>";
                if (!string.IsNullOrEmpty(it.Documentation))
                    text += (text.Length > 0 ? "\n" : "") + RichText.FromMarkdown(it.Documentation);
            }
            _doc.text = text;
            _doc.style.display = text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private VisualElement MakeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("sal-popup-item");
            var icon = new Label();
            icon.AddToClassList("sal-popup-item-icon");
            var name = new Label();
            name.AddToClassList("sal-popup-item-label");
            var detail = new Label();
            detail.AddToClassList("sal-popup-item-detail");
            row.Add(icon);
            row.Add(name);
            row.Add(detail);
            // одиночный клик = выбор; индекс кладётся в userData при бинде (строки переиспользуются)
            row.RegisterCallback<PointerUpEvent>(_ =>
            {
                if (row.userData is int i && i >= 0 && i < _items.Count)
                    Committed?.Invoke(_items[i]);
            });
            return row;
        }

        private static readonly string[] KindClasses =
        {
            "sal-kind-method", "sal-kind-field", "sal-kind-class", "sal-kind-module", "sal-kind-property",
            "sal-kind-enum", "sal-kind-keyword", "sal-kind-member", "sal-kind-constant", "sal-kind-struct", "sal-kind-event",
        };

        private static int KindIndex(CompletionKind k)
        {
            switch (k)
            {
                case CompletionKind.Method:
                case CompletionKind.Function: return 0;
                case CompletionKind.Field: return 1;
                case CompletionKind.Class: return 2;
                case CompletionKind.Module: return 3;
                case CompletionKind.Property: return 4;
                case CompletionKind.Enum: return 5;
                case CompletionKind.Keyword: return 6;
                case CompletionKind.EnumMember: return 7;
                case CompletionKind.Constant: return 8;
                case CompletionKind.Struct: return 9;
                case CompletionKind.Event: return 10;
                default: return 6;
            }
        }

        // ASCII-значки: шрифт кода в игре может не иметь символов-пиктограмм
        private static string Glyph(CompletionKind k)
        {
            switch (k)
            {
                case CompletionKind.Method:
                case CompletionKind.Function: return "m";
                case CompletionKind.Field: return "f";
                case CompletionKind.Property: return "p";
                case CompletionKind.Class: return "C";
                case CompletionKind.Struct: return "S";
                case CompletionKind.Module: return "N";
                case CompletionKind.Enum: return "E";
                case CompletionKind.EnumMember: return "e";
                case CompletionKind.Constant: return "c";
                case CompletionKind.Event: return "@";
                default: return "k";
            }
        }

        private void BindRow(VisualElement el, int i)
        {
            el.userData = i;
            var it = _items[i];
            var icon = (Label)el[0];
            var name = (Label)el[1];
            icon.text = Glyph(it.Kind);
            name.text = it.Label;
            ((Label)el[2]).text = RichText.PlainFirstLine(it.Detail ?? "");
            int kind = KindIndex(it.Kind);
            for (int k = 0; k < KindClasses.Length; k++)
            {
                name.EnableInClassList(KindClasses[k], k == kind);
                icon.EnableInClassList(KindClasses[k], k == kind);
            }
        }
    }
}
