using System.Text;

namespace Dsl.Tooling
{
    /// <summary>
    /// Разворачивание сниппета LSP ($1, ${1:текст}, $0, \} ) в обычный текст для
    /// редакторов без режима сниппетов (встроенная IDE). Каретка встаёт на первую
    /// табуляцию ($1 или ${1:…}, текст по умолчанию выделяется), иначе на $0,
    /// иначе в конец. Перевод строки продолжает отступ текущей строки, '\t' —
    /// единица отступа.
    /// </summary>
    public static class SnippetText
    {
        public static string Expand(string snippet, string lineIndent, string indentUnit,
                                    out int caret, out int selectionLength)
        {
            caret = -1;
            selectionLength = 0;
            int zeroAt = -1;
            int bestStop = int.MaxValue;
            var sb = new StringBuilder(snippet?.Length ?? 0);
            if (string.IsNullOrEmpty(snippet)) { caret = 0; return ""; }
            lineIndent ??= "";
            indentUnit ??= "    ";

            int i = 0, n = snippet.Length;
            while (i < n)
            {
                char c = snippet[i];
                if (c == '\\' && i + 1 < n && (snippet[i + 1] == '}' || snippet[i + 1] == '$' || snippet[i + 1] == '\\'))
                {
                    sb.Append(snippet[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == '$' && i + 1 < n && char.IsDigit(snippet[i + 1]))
                {
                    int j = i + 1, num = 0;
                    while (j < n && char.IsDigit(snippet[j])) { num = num * 10 + (snippet[j] - '0'); j++; }
                    Mark(num, sb.Length, 0, ref zeroAt, ref bestStop, ref caret, ref selectionLength);
                    i = j;
                    continue;
                }
                if (c == '$' && i + 2 < n && snippet[i + 1] == '{' && char.IsDigit(snippet[i + 2]))
                {
                    int j = i + 2, num = 0;
                    while (j < n && char.IsDigit(snippet[j])) { num = num * 10 + (snippet[j] - '0'); j++; }
                    var def = new StringBuilder();
                    if (j < n && snippet[j] == ':')
                    {
                        j++;
                        while (j < n && snippet[j] != '}')
                        {
                            if (snippet[j] == '\\' && j + 1 < n) { def.Append(snippet[j + 1]); j += 2; continue; }
                            def.Append(snippet[j]);
                            j++;
                        }
                    }
                    if (j < n && snippet[j] == '}') j++;
                    Mark(num, sb.Length, def.Length, ref zeroAt, ref bestStop, ref caret, ref selectionLength);
                    sb.Append(def);
                    i = j;
                    continue;
                }
                if (c == '\n') { sb.Append('\n').Append(lineIndent); i++; continue; }
                if (c == '\t') { sb.Append(indentUnit); i++; continue; }
                sb.Append(c);
                i++;
            }

            if (caret < 0)
            {
                caret = zeroAt >= 0 ? zeroAt : sb.Length;
                selectionLength = 0;
            }
            return sb.ToString();
        }

        private static void Mark(int num, int at, int len, ref int zeroAt, ref int bestStop, ref int caret, ref int sel)
        {
            if (num == 0) { if (zeroAt < 0) zeroAt = at; return; }
            if (num < bestStop) { bestStop = num; caret = at; sel = len; }
        }
    }
}
