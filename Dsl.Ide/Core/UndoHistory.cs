using System;
using System.Collections.Generic;

namespace Dsl.Ide
{
    /// <summary>
    /// История правок документа для Ctrl+Z / Ctrl+Y: снимки полного текста с
    /// кареткой, слитный набор склеивается в один шаг. Своя у каждого документа —
    /// переключение вкладок историю не смешивает. Пишется только пользовательскими
    /// правками; undo/redo и программная загрузка её не пишут.
    /// </summary>
    public sealed class UndoHistory
    {
        public int Capacity = 200;            // память ≈ Capacity × размер файла
        public double CoalesceSeconds = 0.8;  // пауза набора, начинающая новый шаг

        public readonly struct Entry
        {
            public readonly string Text;
            public readonly int Caret;

            public Entry(string text, int caret)
            {
                Text = text ?? "";
                Caret = caret;
            }
        }

        private readonly List<Entry> _undo = new List<Entry>();
        private readonly List<Entry> _redo = new List<Entry>();
        private double _lastEditAt = double.NegativeInfinity;
        private bool _forceBreak;

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        /// <summary>
        /// Зовётся ПЕРЕД применением правки (before — состояние до неё). Слитный
        /// набор (|delta| == 1 без пауз) склеивается; вставка, автодополнение,
        /// команды — всегда новый шаг.
        /// </summary>
        public void RecordEdit(string before, int caretBefore, int delta, double now, bool atomic = false)
        {
            _redo.Clear(); // новая правка обрывает ветку redo
            bool newStep = atomic || _forceBreak || now - _lastEditAt > CoalesceSeconds
                           || Math.Abs(delta) != 1 || _undo.Count == 0;
            _lastEditAt = now;
            _forceBreak = atomic;
            if (!newStep) return;
            _undo.Add(new Entry(before, caretBefore));
            if (_undo.Count > Capacity) _undo.RemoveAt(0);
        }

        /// <summary>Следующая правка начнёт новый шаг (например, после перемещения каретки).</summary>
        public void Break() => _forceBreak = true;

        public bool TryUndo(string current, int caret, out Entry restored)
        {
            restored = default;
            while (_undo.Count > 0)
            {
                restored = _undo[_undo.Count - 1];
                _undo.RemoveAt(_undo.Count - 1);
                _redo.Add(new Entry(current, caret));
                if (!string.Equals(restored.Text, current, StringComparison.Ordinal)) { _forceBreak = true; return true; }
                current = restored.Text; // одинаковые снимки пропускаем
            }
            return false;
        }

        public bool TryRedo(string current, int caret, out Entry restored)
        {
            restored = default;
            while (_redo.Count > 0)
            {
                restored = _redo[_redo.Count - 1];
                _redo.RemoveAt(_redo.Count - 1);
                _undo.Add(new Entry(current, caret));
                if (!string.Equals(restored.Text, current, StringComparison.Ordinal)) { _forceBreak = true; return true; }
                current = restored.Text;
            }
            return false;
        }

        public void Reset()
        {
            _undo.Clear();
            _redo.Clear();
            _lastEditAt = double.NegativeInfinity;
            _forceBreak = false;
        }
    }
}
