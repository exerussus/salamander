using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Dsl.Ide
{
    /// <summary>
    /// Модальный диалог внутри IDE (вопрос с кнопками, ввод строки). Свой, а не
    /// EditorUtility: работает и в игре. Enter — кнопка по умолчанию, Esc — отмена.
    /// </summary>
    public sealed class IdeDialog : VisualElement
    {
        private readonly VisualElement _box;
        private readonly Label _title;
        private readonly Label _message;
        private readonly TextField _input;
        private readonly VisualElement _buttons;
        private Action<int> _onButton;
        private int _default, _cancel;
        // второй вопрос, пока открыт первый (например, «сохранить все» с двумя конфликтами)
        private readonly Queue<Action> _queue = new Queue<Action>();

        public bool IsOpen { get; private set; }

        public IdeDialog(IdeChrome chrome)
        {
            AddToClassList("sal-dialog-layer");
            style.position = Position.Absolute;
            style.left = 0; style.right = 0; style.top = 0; style.bottom = 0;
            style.display = DisplayStyle.None;

            _box = new VisualElement();
            _box.AddToClassList("sal-dialog");
            chrome?.Register(_box, ChromeSlot.Raised, ChromeSlot.Text, ChromeSlot.Border);
            Add(_box);

            _title = new Label();
            _title.AddToClassList("sal-dialog-title");
            _box.Add(_title);
            _message = new Label();
            _message.AddToClassList("sal-dialog-message");
            _box.Add(_message);
            _input = new TextField();
            _input.AddToClassList("sal-dialog-input");
            _box.Add(_input);
            _buttons = new VisualElement();
            _buttons.AddToClassList("sal-dialog-buttons");
            _box.Add(_buttons);

            RegisterCallback<KeyDownEvent>(OnKey, TrickleDown.TrickleDown);
            RegisterCallback<NavigationSubmitEvent>(e => { e.StopPropagation(); focusController?.IgnoreEvent(e); }, TrickleDown.TrickleDown);
        }

        /// <summary>Вопрос. onResult(индекс кнопки); Esc = cancelIndex.</summary>
        public void Ask(string title, string message, string[] buttons, int defaultIndex, int cancelIndex, Action<int> onResult)
        {
            Open(title, message, buttons, defaultIndex, cancelIndex, false, null, i => onResult?.Invoke(i));
        }

        /// <summary>Ввод строки. onResult(текст) или null при отмене.</summary>
        public void Prompt(string title, string message, string initial, Action<string> onResult)
        {
            Open(title, message, new[] { "ОК", "Отмена" }, 0, 1, true, initial,
                i => onResult?.Invoke(i == 0 ? (_input.value ?? "") : null));
        }

        private void Open(string title, string message, string[] buttons, int def, int cancel, bool withInput, string initial, Action<int> onButton)
        {
            if (IsOpen)
            {
                // не затираем чужой колбэк — ждём своей очереди
                _queue.Enqueue(() => Open(title, message, buttons, def, cancel, withInput, initial, onButton));
                return;
            }
            _title.text = title ?? "";
            _message.text = message ?? "";
            _message.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
            _input.style.display = withInput ? DisplayStyle.Flex : DisplayStyle.None;
            _input.value = initial ?? "";
            _onButton = onButton;
            _default = def;
            _cancel = cancel;
            _buttons.Clear();
            for (int i = 0; i < buttons.Length; i++)
            {
                int idx = i;
                var b = new Button(() => Close(idx)) { text = buttons[i] };
                b.AddToClassList("sal-btn");
                if (i == def) b.AddToClassList("sal-btn--primary");
                _buttons.Add(b);
            }
            style.display = DisplayStyle.Flex;
            IsOpen = true;
            schedule.Execute(() =>
            {
                if (withInput) { _input.Focus(); _input.SelectAll(); }
                else if (_buttons.childCount > 0) _buttons[Mathf.Clamp(def, 0, _buttons.childCount - 1)].Focus();
            });
        }

        private void Close(int index)
        {
            if (!IsOpen) return;
            IsOpen = false;
            style.display = DisplayStyle.None;
            var cb = _onButton;
            _onButton = null;
            cb?.Invoke(index);
            if (_queue.Count > 0 && !IsOpen) _queue.Dequeue()();
        }

        private void OnKey(KeyDownEvent e)
        {
            if (!IsOpen) return;
            if (e.keyCode == KeyCode.Escape) { Close(_cancel); Stop(e); }
            else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { Close(_default); Stop(e); }
            else if (e.keyCode == KeyCode.None && (e.character == '\n' || e.character == '\r')) Stop(e);
        }

        private void Stop(EventBase e)
        {
            e.StopImmediatePropagation();
            focusController?.IgnoreEvent(e);
        }
    }
}
