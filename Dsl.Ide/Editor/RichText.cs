using System.Text;

namespace Dsl.Ide
{
    /// <summary>
    /// Markdown подсказок языкового сервиса → rich-text UI Toolkit. Поддержано
    /// ровно то, что сервис пишет: ```-блоки (сигнатуры), `код`, **жирный**,
    /// пункты "- ". Всё остальное — как есть; '<' экранируется.
    /// </summary>
    public static class RichText
    {
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('<') < 0) return s ?? "";
            return s.Replace("<", "<noparse><</noparse>");
        }

        public static string FromMarkdown(string md, string codeHex = "DCDCAA", string inlineCodeHex = "9CDCFE")
        {
            if (string.IsNullOrEmpty(md)) return "";
            var sb = new StringBuilder(md.Length + 64);
            var lines = md.Replace("\r\n", "\n").Split('\n');
            bool inCode = false;
            bool firstOut = true;
            foreach (var raw in lines)
            {
                string line = raw;
                if (line.TrimStart().StartsWith("```"))
                {
                    inCode = !inCode;
                    continue;
                }
                if (!firstOut) sb.Append('\n');
                firstOut = false;
                if (inCode)
                {
                    sb.Append("<color=#").Append(codeHex).Append('>').Append(Escape(line)).Append("</color>");
                    continue;
                }
                if (line.StartsWith("- ")) line = "• " + line.Substring(2);
                AppendInline(sb, line, inlineCodeHex);
            }
            // хвостовые пустые строки не нужны
            while (sb.Length > 0 && sb[sb.Length - 1] == '\n') sb.Length--;
            return sb.ToString();
        }

        private static void AppendInline(StringBuilder sb, string line, string inlineCodeHex)
        {
            int i = 0, n = line.Length;
            bool bold = false;
            while (i < n)
            {
                char c = line[i];
                if (c == '`')
                {
                    int end = line.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        sb.Append("<color=#").Append(inlineCodeHex).Append('>')
                          .Append(Escape(line.Substring(i + 1, end - i - 1))).Append("</color>");
                        i = end + 1;
                        continue;
                    }
                }
                if (c == '*' && i + 1 < n && line[i + 1] == '*')
                {
                    sb.Append(bold ? "</b>" : "<b>");
                    bold = !bold;
                    i += 2;
                    continue;
                }
                if (c == '<') sb.Append("<noparse><</noparse>");
                else sb.Append(c);
                i++;
            }
            if (bold) sb.Append("</b>");
        }

        /// <summary>Markdown → простой текст (без разметки) — для однострочных подписей.</summary>
        public static string PlainFirstLine(string md)
        {
            if (string.IsNullOrEmpty(md)) return "";
            foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
            {
                string l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("```")) continue;
                return l.Replace("**", "").Replace("`", "");
            }
            return "";
        }
    }
}
