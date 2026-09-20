using System;
using System.Collections.Generic;

namespace Dsl.Tooling
{
    /// <summary>
    /// Текстовые утилиты языкового сервиса. Позиции — 1-based строка/колонка,
    /// как у диагностик компилятора (Diagnostic.Line/Column). Общие для LSP и
    /// встроенной IDE: одна реализация — одно поведение на краях строк.
    /// </summary>
    public static class TextUtil
    {
        public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>Строка по 1-based номеру (без '\r'); null — такой строки нет.</summary>
        public static string GetLine(string text, int line1)
        {
            if (text == null) return null;
            int cur = 1, start = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length || text[i] == '\n')
                {
                    if (cur == line1)
                    {
                        int end = i;
                        if (end > start && text[end - 1] == '\r') end--;
                        return text.Substring(start, end - start);
                    }
                    cur++;
                    start = i + 1;
                }
            }
            return null;
        }

        /// <summary>Смещение по 1-based строке/колонке (колонка за концом строки не обрезается).</summary>
        public static int OffsetOf(string text, int line1, int col1)
        {
            int line = 1, i = 0;
            while (i < text.Length && line < line1)
            {
                if (text[i] == '\n') line++;
                i++;
            }
            return Math.Min(text.Length, i + Math.Max(0, col1 - 1));
        }

        /// <summary>
        /// Смещение по 1-based строке/колонке с обрезкой колонки концом строки —
        /// для перехода к диагностике: колонка за концом не утаскивает на следующую строку.
        /// -1 — строки нет.
        /// </summary>
        public static int OffsetOfClamped(string text, int line1, int col1)
        {
            if (text == null) return -1;
            int line = 1, i = 0;
            while (line < line1)
            {
                int nl = text.IndexOf('\n', i);
                if (nl < 0) return -1;
                i = nl + 1;
                line++;
            }
            int lineEnd = text.IndexOf('\n', i);
            if (lineEnd < 0) lineEnd = text.Length;
            if (lineEnd > i && text[lineEnd - 1] == '\r') lineEnd--;
            return Math.Min(lineEnd, i + Math.Max(0, col1 - 1));
        }

        /// <summary>1-based строка и колонка по смещению.</summary>
        public static void LineColAt(string text, int offset, out int line1, out int col1)
        {
            line1 = 1;
            col1 = 1;
            if (text == null) return;
            offset = Math.Max(0, Math.Min(offset, text.Length));
            int lineStart = 0;
            for (int i = 0; i < offset; i++)
                if (text[i] == '\n') { line1++; lineStart = i + 1; }
            col1 = offset - lineStart + 1;
        }

        /// <summary>Длина идентификатора с 1-based позиции (минимум 1) — ширина подсветки диагностики.</summary>
        public static int WordLenAt(string text, int line, int col)
        {
            var l = GetLine(text, line);
            if (l == null || col - 1 >= l.Length) return 1;
            int i = col - 1, n = 0;
            if (i < 0) return 1;
            while (i + n < l.Length && IsWordChar(l[i + n])) n++;
            return Math.Max(1, n);
        }

        /// <summary>Слово под 1-based колонкой (или сразу слева от неё) и его начальная колонка.</summary>
        public static (string word, int col1) WordAt(string line, int col1)
        {
            if (string.IsNullOrEmpty(line)) return (null, 0);
            int i = Math.Min(col1 - 1, line.Length - 1);
            if (i < 0) i = 0;
            if (!IsWordChar(line[i]) && i > 0 && IsWordChar(line[i - 1])) i--;
            if (!IsWordChar(line[i])) return (null, 0);
            int s = i; while (s > 0 && IsWordChar(line[s - 1])) s--;
            int e = i; while (e + 1 < line.Length && IsWordChar(line[e + 1])) e++;
            return (line.Substring(s, e - s + 1), s + 1);
        }

        /// <summary>Стоит ли prefix непосредственно перед словом, начинающимся с 1-based колонки.</summary>
        public static bool HasPrefix(string line, int wordCol1, string prefix)
        {
            int end = wordCol1 - 1;
            int start = end - prefix.Length;
            return start >= 0 && string.CompareOrdinal(line, start, prefix, 0, prefix.Length) == 0;
        }

        /// <summary>
        /// Путь через точку, заканчивающийся словом под курсором: для "Weapon" в
        /// "Api.Parts.Weapon.Cut(...)" вернёт "Api.Parts.Weapon". Работает по
        /// тексту строки — hover лексер не гоняет.
        /// </summary>
        public static string DottedPrefixInLine(string line, int wordCol1, string word)
        {
            int i = wordCol1 - 1;                       // индекс первого символа слова
            var head = new List<string>();
            while (i >= 1 && line[i - 1] == '.')
            {
                int e = i - 2;                          // последний символ предыдущего сегмента
                if (e < 0) break;
                int s = e;
                while (s >= 0 && IsWordChar(line[s])) s--;
                if (s == e) break;                      // перед точкой не идентификатор
                head.Insert(0, line.Substring(s + 1, e - s));
                i = s + 1;
            }
            if (head.Count == 0) return word;
            head.Add(word);
            return string.Join(".", head);
        }

        /// <summary>Начало идентификатора, заканчивающегося на offset (сам offset, если слева не буква).</summary>
        public static int WordStartBefore(string text, int offset)
        {
            int s = Math.Max(0, Math.Min(offset, text?.Length ?? 0));
            while (s > 0 && IsWordChar(text[s - 1])) s--;
            return s;
        }

        /// <summary>Конец идентификатора, начинающегося на offset.</summary>
        public static int WordEndAfter(string text, int offset)
        {
            int e = Math.Max(0, Math.Min(offset, text?.Length ?? 0));
            while (e < text.Length && IsWordChar(text[e])) e++;
            return e;
        }
    }
}
