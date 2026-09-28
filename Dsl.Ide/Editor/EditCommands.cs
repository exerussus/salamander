using System;
using System.Text;

namespace Dsl.Ide
{
    /// <summary>Результат команды редактирования: новый текст и выделение (anchor — неподвижный край).</summary>
    public readonly struct EditResult
    {
        public readonly string Text;
        public readonly int Anchor;
        public readonly int Caret;

        public EditResult(string text, int anchor, int caret)
        {
            Text = text;
            Anchor = anchor;
            Caret = caret;
        }
    }

    /// <summary>
    /// Команды редактора как чистые функции над текстом: отступы, комментарии,
    /// дублирование, перенос строк, умные Enter/Backspace/скобки. Без UI —
    /// поэтому проверяются тестами и одинаково ведут себя в игре и в редакторе.
    /// </summary>
    public static class EditCommands
    {
        public const string IndentUnit = "    ";

        // ───────────────────────── строки ─────────────────────────

        public static int LineStart(string t, int i)
        {
            i = Math.Max(0, Math.Min(i, t.Length));
            while (i > 0 && t[i - 1] != '\n') i--;
            return i;
        }

        public static int LineEnd(string t, int i)
        {
            i = Math.Max(0, Math.Min(i, t.Length));
            while (i < t.Length && t[i] != '\n') i++;
            return i;
        }

        /// <summary>Ведущие пробелы/табы строки, содержащей позицию.</summary>
        public static string LineIndent(string t, int i)
        {
            int ls = LineStart(t, i);
            int e = ls;
            while (e < t.Length && (t[e] == ' ' || t[e] == '\t')) e++;
            return t.Substring(ls, e - ls);
        }

        /// <summary>
        /// Диапазон строк, задетых выделением [a, b): начала первой и конца последней.
        /// Выделение, кончающееся в самом начале строки, эту строку не захватывает.
        /// </summary>
        public static void SelectedLines(string t, int a, int b, out int first, out int lastEnd)
        {
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            if (hi > lo && hi > 0 && t[hi - 1] == '\n') hi--;
            first = LineStart(t, lo);
            lastEnd = LineEnd(t, hi);
        }

        // ───────────────────────── отступы ─────────────────────────

        /// <summary>Tab / Shift+Tab по строкам выделения. Без многострочного выделения Tab — вставка отступа.</summary>
        public static EditResult? Indent(string t, int anchor, int caret, bool outdent)
        {
            bool multiline = anchor != caret && t.IndexOf('\n', Math.Min(anchor, caret), Math.Abs(anchor - caret)) >= 0;
            if (!outdent && !multiline)
            {
                // вставка до следующей позиции табуляции (кратной 4) — вместо выделения
                int lo = Math.Min(anchor, caret), hi = Math.Max(anchor, caret);
                int col = lo - LineStart(t, lo);
                string pad = new string(' ', IndentUnit.Length - col % IndentUnit.Length);
                string r = t.Substring(0, lo) + pad + t.Substring(hi);
                int c = lo + pad.Length;
                return new EditResult(r, c, c);
            }

            SelectedLines(t, anchor, caret, out int first, out int lastEnd);
            string[] lines = t.Substring(first, lastEnd - first).Split('\n');
            var deltas = new int[lines.Length];
            var starts = new int[lines.Length];   // начала строк в НОВОМ тексте (относительно first)
            var sb = new StringBuilder(t.Length + 64);
            for (int k = 0; k < lines.Length; k++)
            {
                if (k > 0) sb.Append('\n');
                starts[k] = sb.Length;
                string line = lines[k];
                if (outdent)
                {
                    int remove = 0;
                    if (line.StartsWith("\t")) remove = 1;
                    else while (remove < IndentUnit.Length && remove < line.Length && line[remove] == ' ') remove++;
                    sb.Append(line, remove, line.Length - remove);
                    deltas[k] = -remove;
                }
                else if (line.Trim().Length == 0)
                {
                    sb.Append(line); // пустые строки не засоряем пробелами
                }
                else
                {
                    sb.Append(IndentUnit).Append(line);
                    deltas[k] = IndentUnit.Length;
                }
            }
            string res = t.Substring(0, first) + sb + t.Substring(lastEnd);

            int Map(int p)
            {
                if (p < first) return p;
                if (p > lastEnd) return p + (res.Length - t.Length);
                // строка k и колонка внутри блока
                int rel = p - first, k = 0, acc = 0;
                while (k < lines.Length - 1 && rel > acc + lines[k].Length) { acc += lines[k].Length + 1; k++; }
                int col = rel - acc;
                return first + starts[k] + Math.Max(0, col + deltas[k]);
            }
            return new EditResult(res, Clamp(Map(anchor), res), Clamp(Map(caret), res));
        }

        private static int Clamp(int p, string t) => Math.Max(0, Math.Min(p, t.Length));

        // ───────────────────────── комментарии ─────────────────────────

        /// <summary>Ctrl+/: закомментировать строки выделения (или раскомментировать, если все уже с //).</summary>
        public static EditResult ToggleComment(string t, int anchor, int caret)
        {
            SelectedLines(t, anchor, caret, out int first, out int lastEnd);
            string block = t.Substring(first, lastEnd - first);
            string[] lines = block.Split('\n');

            bool allCommented = true;
            int minIndent = int.MaxValue;
            foreach (var l in lines)
            {
                if (l.Trim().Length == 0) continue;
                int ind = 0;
                while (ind < l.Length && (l[ind] == ' ' || l[ind] == '\t')) ind++;
                minIndent = Math.Min(minIndent, ind);
                if (!l.Substring(ind).StartsWith("//")) allCommented = false;
            }
            if (minIndent == int.MaxValue) { minIndent = 0; allCommented = false; }

            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) sb.Append('\n');
                string l = lines[i];
                if (l.Trim().Length == 0) { sb.Append(l); continue; }
                if (allCommented)
                {
                    int ind = 0;
                    while (ind < l.Length && (l[ind] == ' ' || l[ind] == '\t')) ind++;
                    int cut = 2 + (ind + 2 < l.Length && l[ind + 2] == ' ' ? 1 : 0);
                    sb.Append(l, 0, ind).Append(l, ind + cut, l.Length - ind - cut);
                }
                else
                {
                    sb.Append(l, 0, minIndent).Append("// ").Append(l, minIndent, l.Length - minIndent);
                }
            }
            string res = t.Substring(0, first) + sb + t.Substring(lastEnd);
            int end = first + sb.Length;
            // выделяем обработанные строки целиком — повторное Ctrl+/ вернёт как было
            if (anchor != caret) return new EditResult(res, first, end);
            // каретка сдвигается вместе со своей строкой, но не уезжает выше её начала
            int moved = caret >= first ? Math.Max(first, caret + (res.Length - t.Length)) : caret;
            return new EditResult(res, Clamp(moved, res), Clamp(moved, res));
        }

        // ───────────────────────── строки целиком ─────────────────────────

        /// <summary>Ctrl+D: дублировать выделение или текущую строку.</summary>
        public static EditResult Duplicate(string t, int anchor, int caret)
        {
            if (anchor != caret)
            {
                int lo = Math.Min(anchor, caret), hi = Math.Max(anchor, caret);
                string sel = t.Substring(lo, hi - lo);
                string r = t.Substring(0, hi) + sel + t.Substring(hi);
                return new EditResult(r, hi, hi + sel.Length);
            }
            int ls = LineStart(t, caret), le = LineEnd(t, caret);
            string line = t.Substring(ls, le - ls);
            string res = t.Substring(0, le) + "\n" + line + t.Substring(le);
            int c = caret + line.Length + 1;
            return new EditResult(res, c, c);
        }

        /// <summary>Ctrl+Shift+K: удалить строки выделения.</summary>
        public static EditResult DeleteLines(string t, int anchor, int caret)
        {
            SelectedLines(t, anchor, caret, out int first, out int lastEnd);
            int cutEnd = lastEnd < t.Length ? lastEnd + 1 : lastEnd;
            int cutStart = first;
            if (cutEnd == t.Length && first > 0 && lastEnd == t.Length) cutStart = first - 1; // последняя строка: съедаем перевод перед ней
            string res = t.Substring(0, cutStart) + t.Substring(cutEnd);
            int c = Clamp(cutStart == first ? first : LineStart(res, cutStart), res);
            return new EditResult(res, c, c);
        }

        /// <summary>Alt+↑/↓: перенести строки выделения на одну вверх/вниз.</summary>
        public static EditResult? MoveLines(string t, int anchor, int caret, int dir)
        {
            SelectedLines(t, anchor, caret, out int first, out int lastEnd);
            if (dir < 0)
            {
                if (first == 0) return null;
                int prevStart = LineStart(t, first - 1);
                string prev = t.Substring(prevStart, first - 1 - prevStart);
                string block = t.Substring(first, lastEnd - first);
                string res = t.Substring(0, prevStart) + block + "\n" + prev + t.Substring(lastEnd);
                int d = -(prev.Length + 1);
                return new EditResult(res, Clamp(anchor + d, res), Clamp(caret + d, res));
            }
            else
            {
                if (lastEnd >= t.Length) return null;
                int nextEnd = LineEnd(t, lastEnd + 1);
                string next = t.Substring(lastEnd + 1, nextEnd - lastEnd - 1);
                string block = t.Substring(first, lastEnd - first);
                string res = t.Substring(0, first) + next + "\n" + block + t.Substring(nextEnd);
                int d = next.Length + 1;
                return new EditResult(res, Clamp(anchor + d, res), Clamp(caret + d, res));
            }
        }

        // ───────────────────────── ввод ─────────────────────────

        /// <summary>
        /// Enter: перенос с отступом текущей строки; после '{' — на уровень глубже;
        /// между '{' и '}' — раскрыть блок (закрывающая скобка на своей строке).
        /// </summary>
        public static EditResult SmartNewline(string t, int anchor, int caret)
        {
            int lo = Math.Min(anchor, caret), hi = Math.Max(anchor, caret);
            string indent = LineIndent(t, lo);
            // отступ не длиннее позиции каретки в строке
            int col = lo - LineStart(t, lo);
            if (indent.Length > col) indent = indent.Substring(0, col);

            int before = lo - 1;
            while (before >= LineStart(t, lo) && (t[before] == ' ' || t[before] == '\t')) before--;
            bool afterOpen = before >= 0 && before >= LineStart(t, lo) && (t[before] == '{' || t[before] == '(' || t[before] == '[');
            int after = hi;
            while (after < t.Length && (t[after] == ' ' || t[after] == '\t')) after++;
            bool beforeClose = after < t.Length && (t[after] == '}' || t[after] == ')' || t[after] == ']');

            string ins;
            int caretOff;
            if (afterOpen && beforeClose)
            {
                ins = "\n" + indent + IndentUnit + "\n" + indent;
                caretOff = 1 + indent.Length + IndentUnit.Length;
                string r1 = t.Substring(0, lo) + ins + t.Substring(after);
                return new EditResult(r1, lo + caretOff, lo + caretOff);
            }
            ins = "\n" + indent + (afterOpen ? IndentUnit : "");
            // хвостовые пробелы перед кареткой не переносим в конец строки
            int cut = lo;
            while (cut > LineStart(t, lo) && (t[cut - 1] == ' ' || t[cut - 1] == '\t') && cut - 1 > before) cut--;
            string r = t.Substring(0, cut) + ins + t.Substring(hi);
            int c = cut + ins.Length;
            return new EditResult(r, c, c);
        }

        /// <summary>Shift+Enter: новая строка под текущей с её отступом (каретка на неё).</summary>
        public static EditResult LineBelow(string t, int caret)
        {
            int le = LineEnd(t, caret);
            string indent = LineIndent(t, caret);
            string r = t.Substring(0, le) + "\n" + indent + t.Substring(le);
            int c = le + 1 + indent.Length;
            return new EditResult(r, c, c);
        }

        /// <summary>
        /// Backspace: удаляет пару «()», «[]», «{}», «""», если каретка ровно между
        /// ними; в ведущих пробелах удаляет до предыдущей позиции табуляции.
        /// null — пусть работает обычный Backspace.
        /// </summary>
        public static EditResult? SmartBackspace(string t, int anchor, int caret)
        {
            if (anchor != caret || caret == 0) return null;
            char prev = t[caret - 1];
            char next = caret < t.Length ? t[caret] : '\0';
            if ((prev == '(' && next == ')') || (prev == '[' && next == ']') ||
                (prev == '{' && next == '}') || (prev == '"' && next == '"'))
            {
                string r = t.Substring(0, caret - 1) + t.Substring(caret + 1);
                return new EditResult(r, caret - 1, caret - 1);
            }
            int ls = LineStart(t, caret);
            int col = caret - ls;
            if (col > 0 && prev == ' ')
            {
                // только если слева от каретки одни пробелы
                for (int i = ls; i < caret; i++) if (t[i] != ' ') return null;
                int remove = col % IndentUnit.Length;
                if (remove == 0) remove = IndentUnit.Length;
                remove = Math.Min(remove, col);
                if (remove <= 1) return null;
                string r = t.Substring(0, caret - remove) + t.Substring(caret);
                return new EditResult(r, caret - remove, caret - remove);
            }
            return null;
        }

        /// <summary>
        /// Набор символа с автопарами: ( [ { " вставляются парой (каретка между),
        /// закрывающий символ под кареткой «проскакивается», '}' в пустой строке
        /// снимает уровень отступа. null — обычная вставка.
        /// </summary>
        public static EditResult? TypeChar(string t, int anchor, int caret, char c, bool inCommentOrString)
        {
            int lo = Math.Min(anchor, caret), hi = Math.Max(anchor, caret);

            // «проскок» закрывающего символа
            if (lo == hi && hi < t.Length && t[hi] == c && (c == ')' || c == ']' || c == '}' || c == '"'))
                return new EditResult(t, hi + 1, hi + 1);

            if (inCommentOrString)
                return null; // в строке/комментарии пары не плодим: кавычка закрывает строку одиночной

            string pair = c == '(' ? "()" : c == '[' ? "[]" : c == '{' ? "{}" : c == '"' ? "\"\"" : null;
            if (pair != null)
            {
                // выделение оборачивается парой
                if (lo != hi)
                {
                    string sel = t.Substring(lo, hi - lo);
                    string r0 = t.Substring(0, lo) + pair[0] + sel + pair[1] + t.Substring(hi);
                    return new EditResult(r0, lo + 1, lo + 1 + sel.Length);
                }
                // перед словом пару не ставим: «(foo» чаще начало выражения
                char next = hi < t.Length ? t[hi] : '\0';
                if (char.IsLetterOrDigit(next) || next == '_') return null;
                string r = t.Substring(0, lo) + pair + t.Substring(hi);
                return new EditResult(r, lo + 1, lo + 1);
            }

            if (c == '}' && lo == hi)
            {
                int ls = LineStart(t, lo);
                for (int i = ls; i < lo; i++) if (t[i] != ' ' && t[i] != '\t') return null;
                int col = lo - ls;
                if (col == 0) return null;
                int remove = Math.Min(col, col % IndentUnit.Length == 0 ? IndentUnit.Length : col % IndentUnit.Length);
                string r = t.Substring(0, lo - remove) + "}" + t.Substring(lo);
                return new EditResult(r, lo - remove + 1, lo - remove + 1);
            }
            return null;
        }

        // ───────────────────────── скобки ─────────────────────────

        /// <summary>
        /// Парная скобка к скобке в позиции index (или сразу слева от неё).
        /// Строки и комментарии пропускаются. -1 — пары нет; bracketAt — где сама скобка.
        /// </summary>
        public static int MatchBracket(string t, int index, out int bracketAt)
        {
            bracketAt = -1;
            if (string.IsNullOrEmpty(t)) return -1;
            int at = -1;
            if (index < t.Length && IsBracket(t[index])) at = index;
            else if (index > 0 && index - 1 < t.Length && IsBracket(t[index - 1])) at = index - 1;
            if (at < 0) return -1;

            // скобка внутри строки/комментария пары не ищет
            var mask = CodeMask(t);
            if (!mask[at]) return -1;
            bracketAt = at;

            char open = t[at];
            bool forward = open == '(' || open == '[' || open == '{';
            char close = Pair(open);
            int depth = 0;
            if (forward)
            {
                for (int i = at; i < t.Length; i++)
                {
                    if (!mask[i]) continue;
                    if (t[i] == open) depth++;
                    else if (t[i] == close && --depth == 0) return i;
                }
            }
            else
            {
                for (int i = at; i >= 0; i--)
                {
                    if (!mask[i]) continue;
                    if (t[i] == open) depth++;
                    else if (t[i] == close && --depth == 0) return i;
                }
            }
            return -1;
        }

        private static bool IsBracket(char c) => c == '(' || c == ')' || c == '[' || c == ']' || c == '{' || c == '}';

        private static char Pair(char c)
        {
            switch (c)
            {
                case '(': return ')';
                case ')': return '(';
                case '[': return ']';
                case ']': return '[';
                case '{': return '}';
                default: return '{';
            }
        }

        /// <summary>Маска «это код» (не строка и не комментарий) по каждому символу.</summary>
        public static bool[] CodeMask(string t)
        {
            var m = new bool[t.Length];
            int i = 0, n = t.Length;
            while (i < n)
            {
                char c = t[i];
                if (c == '/' && i + 1 < n && t[i + 1] == '/')
                {
                    while (i < n && t[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < n && t[i + 1] == '*')
                {
                    i += 2;
                    while (i < n && !(t[i] == '*' && i + 1 < n && t[i + 1] == '/')) i++;
                    i = Math.Min(n, i + 2);
                    continue;
                }
                if (c == '"')
                {
                    bool interp = i > 0 && t[i - 1] == '$';
                    i++;
                    while (i < n && t[i] != '"' && t[i] != '\n')
                    {
                        if (t[i] == '\\') { i += 2; continue; }
                        if (interp && t[i] == '{' && i + 1 < n && t[i + 1] != '{')
                        {
                            // дырка интерполяции — код
                            i++;
                            while (i < n && t[i] != '}' && t[i] != '"' && t[i] != '\n') { m[i] = true; i++; }
                            if (i < n && t[i] == '}') i++;
                            continue;
                        }
                        i++;
                    }
                    if (i < n && t[i] == '"') i++;
                    continue;
                }
                m[i] = true;
                i++;
            }
            return m;
        }
    }
}
