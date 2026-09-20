using UnityEngine;
using UnityEngine.UIElements;

namespace Dsl.Ide
{
    /// <summary>
    /// Всплывающая справка: подсказка параметров над кареткой и hover у курсора
    /// мыши. Только rich-text; tooltip UI Toolkit в игре не работает, поэтому
    /// своё окошко. Вид — .sal-info в USS.
    /// </summary>
    public sealed class InfoPopup : VisualElement
    {
        private readonly Label _text;

        public bool IsOpen { get; private set; }

        public InfoPopup(string extraClass = null)
        {
            AddToClassList("sal-info");
            if (!string.IsNullOrEmpty(extraClass)) AddToClassList(extraClass);
            style.position = Position.Absolute;
            style.display = DisplayStyle.None;
            pickingMode = PickingMode.Ignore;
            _text = new Label { enableRichText = true, pickingMode = PickingMode.Ignore };
            _text.AddToClassList("sal-info-text");
            Add(_text);
        }

        /// <summary>Показать rich-text. anchor — точка привязки в координатах родителя; above — над ней.</summary>
        public void Show(string richText, Vector2 anchor, bool above, float lineHeight)
        {
            if (string.IsNullOrEmpty(richText)) { Hide(); return; }
            _text.text = richText;
            style.display = DisplayStyle.Flex;
            IsOpen = true;
            // размер известен только после layout — позиционируем в следующем кадре
            style.left = Mathf.Max(0f, anchor.x);
            style.top = above ? Mathf.Max(0f, anchor.y - lineHeight * 3f) : anchor.y;
            schedule.Execute(() => Place(anchor, above, lineHeight));
        }

        private void Place(Vector2 anchor, bool above, float lineHeight)
        {
            if (!IsOpen || parent == null) return;
            var box = layout;
            var host = parent.layout;
            float x = Mathf.Clamp(anchor.x, 0f, Mathf.Max(0f, host.width - box.width - 4f));
            float y = above ? anchor.y - box.height - 2f : anchor.y + 2f;
            // не влезает сверху — под строку, снизу — над ней
            if (above && y < 0f) y = anchor.y + lineHeight + 2f;
            if (!above && y + box.height > host.height) y = Mathf.Max(0f, anchor.y - lineHeight - box.height - 2f);
            style.left = x;
            style.top = y;
        }

        public void Hide()
        {
            if (!IsOpen) return;
            style.display = DisplayStyle.None;
            IsOpen = false;
        }
    }
}
