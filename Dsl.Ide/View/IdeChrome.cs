using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Dsl.Ide
{
    /// <summary>Роль цвета в оболочке IDE (панели, текст, акценты). Кодовая область красится отдельно — USS.</summary>
    public enum ChromeSlot : byte
    {
        None, Background, Panel, Raised, Border, Text, TextDim, Accent, AccentText, Selection, Hover, Error, Warning, Ok,
    }

    /// <summary>Цвета оболочки, которые хост может навязать (страница Nexus — из токенов своей палитры).</summary>
    public struct IdeChromeColors
    {
        public Color Background, Panel, Raised, Border, Text, TextDim, Accent, AccentText, Selection, Hover, Error, Warning, Ok;

        public Color Get(ChromeSlot s)
        {
            switch (s)
            {
                case ChromeSlot.Background: return Background;
                case ChromeSlot.Panel: return Panel;
                case ChromeSlot.Raised: return Raised;
                case ChromeSlot.Border: return Border;
                case ChromeSlot.Text: return Text;
                case ChromeSlot.TextDim: return TextDim;
                case ChromeSlot.Accent: return Accent;
                case ChromeSlot.AccentText: return AccentText;
                case ChromeSlot.Selection: return Selection;
                case ChromeSlot.Hover: return Hover;
                case ChromeSlot.Error: return Error;
                case ChromeSlot.Warning: return Warning;
                case ChromeSlot.Ok: return Ok;
                default: return Color.clear;
            }
        }
    }

    /// <summary>
    /// Перекраска оболочки поверх USS. Пока цвета не заданы, ничего не пишется
    /// inline — работают правила SalIde.uss (вид по умолчанию, игра). После
    /// Apply зарегистрированные элементы красятся заданными цветами; ховер у
    /// интерактивных — через события указателя (inline-фон перекрыл бы :hover).
    /// </summary>
    public sealed class IdeChrome
    {
        private sealed class Entry
        {
            public VisualElement El;
            public Func<ChromeSlot> Bg;
            public ChromeSlot Fg;
            public ChromeSlot Border;
            public bool Hoverable;
            public bool Hovered;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly Dictionary<VisualElement, Entry> _byElement = new Dictionary<VisualElement, Entry>();
        private IdeChromeColors _colors;
        private bool _enabled;

        public bool Enabled => _enabled;
        public IdeChromeColors Colors => _colors;

        public void Register(VisualElement el, ChromeSlot bg = ChromeSlot.None, ChromeSlot fg = ChromeSlot.None,
                             ChromeSlot border = ChromeSlot.None, bool hover = false)
            => Register(el, bg == ChromeSlot.None ? null : (Func<ChromeSlot>)(() => bg), fg, border, hover);

        public void Register(VisualElement el, Func<ChromeSlot> bg, ChromeSlot fg = ChromeSlot.None,
                             ChromeSlot border = ChromeSlot.None, bool hover = false)
        {
            if (el == null) return;
            var e = new Entry { El = el, Bg = bg, Fg = fg, Border = border, Hoverable = hover };
            if (_byElement.TryGetValue(el, out var old)) _entries.Remove(old);
            _entries.Add(e);
            _byElement[el] = e;
            if (hover)
            {
                el.RegisterCallback<PointerEnterEvent>(_ => { e.Hovered = true; Paint(e); });
                el.RegisterCallback<PointerLeaveEvent>(_ => { e.Hovered = false; Paint(e); });
            }
            // элемент ушёл из дерева — забываем (списки перестраиваются)
            el.RegisterCallback<DetachFromPanelEvent>(_ =>
            {
                if (_byElement.TryGetValue(el, out var cur) && cur == e)
                {
                    _byElement.Remove(el);
                    _entries.Remove(e);
                }
            });
            Paint(e);
        }

        public void Apply(IdeChromeColors colors)
        {
            _colors = colors;
            _enabled = true;
            foreach (var e in _entries.ToArray()) Paint(e);
        }

        /// <summary>Перекрасить после смены состояния (активная вкладка, выбранная строка).</summary>
        public void Repaint(VisualElement el)
        {
            if (el != null && _byElement.TryGetValue(el, out var e)) Paint(e);
        }

        private void Paint(Entry e)
        {
            if (!_enabled) return;
            var s = e.El.style;
            if (e.Bg != null)
            {
                var slot = e.Hoverable && e.Hovered ? ChromeSlot.Hover : e.Bg();
                s.backgroundColor = slot == ChromeSlot.None ? new StyleColor(StyleKeyword.Null) : new StyleColor(_colors.Get(slot));
            }
            if (e.Fg != ChromeSlot.None) s.color = _colors.Get(e.Fg);
            if (e.Border != ChromeSlot.None)
            {
                var c = _colors.Get(e.Border);
                s.borderTopColor = c;
                s.borderBottomColor = c;
                s.borderLeftColor = c;
                s.borderRightColor = c;
            }
        }
    }
}
