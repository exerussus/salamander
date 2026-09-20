using System;
using System.Collections.Generic;
using System.Text;
using Dsl.Tooling;
using UnityEngine;

namespace Dsl.Ide
{
    /// <summary>Цвет фрагмента кода. Индекс в палитре.</summary>
    public enum SalColor : byte
    {
        None = 0,   // базовый цвет текста (тег не пишется)
        Keyword,
        Type,
        Class,
        Function,
        Property,
        Variable,
        String,
        Number,
        Comment,
        Namespace,
        EnumMember,
        Decorator,
        Count,
    }

    /// <summary>
    /// Палитра подсветки: hex на каждый цвет. Значения приходят из USS
    /// (кастомные свойства --sal-*), дефолты — страховка без стилей.
    /// </summary>
    public sealed class CodePalette
    {
        public readonly string[] Hex = new string[(int)SalColor.Count];

        public static CodePalette Default()
        {
            var p = new CodePalette();
            p.Hex[(int)SalColor.Keyword] = "C586C0";
            p.Hex[(int)SalColor.Type] = "4EC9B0";
            p.Hex[(int)SalColor.Class] = "4EC9B0";
            p.Hex[(int)SalColor.Function] = "DCDCAA";
            p.Hex[(int)SalColor.Property] = "9CDCFE";
            p.Hex[(int)SalColor.Variable] = null;       // обычный текст
            p.Hex[(int)SalColor.String] = "CE9178";
            p.Hex[(int)SalColor.Number] = "B5CEA8";
            p.Hex[(int)SalColor.Comment] = "6A9955";
            p.Hex[(int)SalColor.Namespace] = "4FC1FF";
            p.Hex[(int)SalColor.EnumMember] = "4FC1FF";
            p.Hex[(int)SalColor.Decorator] = "DCDCAA";
            return p;
        }

        public void Set(SalColor c, Color color) => Hex[(int)c] = ColorUtility.ToHtmlStringRGB(color);

        public static SalColor FromSemantic(int tokenType)
        {
            switch (tokenType)
            {
                case SemanticClassifier.TtKeyword: return SalColor.Keyword;
                case SemanticClassifier.TtType: return SalColor.Type;
                case SemanticClassifier.TtClass: return SalColor.Class;
                case SemanticClassifier.TtFunction: return SalColor.Function;
                case SemanticClassifier.TtProperty: return SalColor.Property;
                case SemanticClassifier.TtVariable: return SalColor.Variable;
                case SemanticClassifier.TtString: return SalColor.String;
                case SemanticClassifier.TtNumber: return SalColor.Number;
                case SemanticClassifier.TtComment: return SalColor.Comment;
                case SemanticClassifier.TtNamespace: return SalColor.Namespace;
                case SemanticClassifier.TtEnumMember: return SalColor.EnumMember;
                case SemanticClassifier.TtDecorator:
                case SemanticClassifier.TtEvent: return SalColor.Decorator;
                default: return SalColor.None;
            }
        }
    }

    /// <summary>
    /// Построчная подсветка с кэшем: правка пересчитывает только изменённые
    /// строки (и следующие, пока не сойдётся состояние «внутри /* */»), затем
    /// склеивает готовые rich-text строки. Правила раскраски слов — общие с LSP
    /// (<see cref="SemanticClassifier.ClassifyIdentifier"/>), поэтому цвета в
    /// IDE игры и в VS Code совпадают.
    /// </summary>
    public sealed class LineHighlighter
    {
        public readonly struct Span
        {
            public readonly int Start;
            public readonly int Length;
            public readonly SalColor Color;

            public Span(int start, int length, SalColor color)
            {
                Start = start;
                Length = length;
                Color = color;
            }
        }

        private sealed class Line
        {
            public string Text;
            public bool StartsInComment;
            public bool EndsInComment;
            public string Rich;
        }

        private static readonly HashSet<string> Keywords = new HashSet<string>(EngineDocs.Keywords, StringComparer.Ordinal);

        private readonly List<Line> _lines = new List<Line>();
        private readonly List<Span> _spans = new List<Span>(64);
        private readonly StringBuilder _sb = new StringBuilder(4096);
        private SemanticClassifier _classifier;
        private CodePalette _palette = CodePalette.Default();

        /// <summary>Большие файлы: без раскраски, только текст (набор остаётся отзывчивым).</summary>
        public int MaxHighlightChars = 400_000;

        /// <summary>true — раскраска изменится (таблицы имён другие), кэш строк сброшен.</summary>
        public bool SetClassifier(SemanticClassifier classifier)
        {
            bool same = _classifier != null && classifier != null && _classifier.Fingerprint == classifier.Fingerprint;
            _classifier = classifier;
            if (same) return false;
            _lines.Clear(); // таблицы имён сменились — пересчитать всё
            return true;
        }

        public void SetPalette(CodePalette palette)
        {
            _palette = palette ?? CodePalette.Default();
            _lines.Clear();
        }

        /// <summary>Состояние «внутри блочного комментария» в начале строки (0-based) — для эвристик ввода.</summary>
        public bool StartsInComment(int lineIndex) =>
            lineIndex >= 0 && lineIndex < _lines.Count && _lines[lineIndex].StartsInComment;

        /// <summary>Rich-text всего документа для слоя подсветки.</summary>
        public string Build(string text)
        {
            text ??= "";
            if (text.Length > MaxHighlightChars)
            {
                _lines.Clear();
                _sb.Clear();
                AppendEscaped(_sb, text, 0, text.Length);
                return Finish(text);
            }

            string[] lines = text.Split('\n');

            // общий префикс/суффикс со старым кэшем — пересчитываем только середину
            int prefix = 0;
            while (prefix < lines.Length && prefix < _lines.Count && string.Equals(lines[prefix], _lines[prefix].Text, StringComparison.Ordinal))
                prefix++;
            int suffix = 0;
            while (suffix < lines.Length - prefix && suffix < _lines.Count - prefix &&
                   string.Equals(lines[lines.Length - 1 - suffix], _lines[_lines.Count - 1 - suffix].Text, StringComparison.Ordinal))
                suffix++;

            var old = new List<Line>(_lines);
            _lines.Clear();
            for (int i = 0; i < prefix; i++) _lines.Add(old[i]);

            bool state = prefix > 0 && _lines[prefix - 1].EndsInComment;
            int midEnd = lines.Length - suffix;
            for (int i = prefix; i < midEnd; i++)
            {
                var ln = MakeLine(lines[i], state);
                _lines.Add(ln);
                state = ln.EndsInComment;
            }
            // суффикс: переиспользуем, пока входное состояние совпадает с кэшированным
            for (int k = 0; k < suffix; k++)
            {
                var cached = old[old.Count - suffix + k];
                if (cached.StartsInComment == state)
                {
                    _lines.Add(cached);
                    state = cached.EndsInComment;
                }
                else
                {
                    var ln = MakeLine(cached.Text, state);
                    _lines.Add(ln);
                    state = ln.EndsInComment;
                }
            }

            _sb.Clear();
            for (int i = 0; i < _lines.Count; i++)
            {
                if (i > 0) _sb.Append('\n');
                _sb.Append(_lines[i].Rich);
            }
            return Finish(text);
        }

        private string Finish(string text)
        {
            // Label не резервирует высоту финальной пустой строки — держим её nbsp,
            // иначе каретка на последней строке уедет за пределы слоя подсветки
            if (text.Length == 0 || text[text.Length - 1] == '\n') _sb.Append(' ');
            return _sb.ToString();
        }

        private Line MakeLine(string text, bool startsInComment)
        {
            _spans.Clear();
            bool ends = TokenizeLine(text, startsInComment, _classifier, _spans);
            var sb = new StringBuilder(text.Length + _spans.Count * 24);
            int pos = 0;
            foreach (var s in _spans)
            {
                string hex = _palette.Hex[(int)s.Color];
                AppendEscaped(sb, text, pos, s.Start - pos);
                if (hex != null) sb.Append("<color=#").Append(hex).Append('>');
                AppendEscaped(sb, text, s.Start, s.Length);
                if (hex != null) sb.Append("</color>");
                pos = s.Start + s.Length;
            }
            AppendEscaped(sb, text, pos, text.Length - pos);
            return new Line { Text = text, StartsInComment = startsInComment, EndsInComment = ends, Rich = sb.ToString() };
        }

        // '<' в коде сломал бы разбор rich-text — экранируем через noparse
        // (метрики текста при этом не меняются, в отличие от невидимых символов)
        private static void AppendEscaped(StringBuilder sb, string text, int start, int length)
        {
            int end = start + length;
            for (int i = start; i < end; i++)
            {
                char ch = text[i];
                if (ch == '<') sb.Append("<noparse><</noparse>");
                else if (ch == '\r') continue;
                else sb.Append(ch);
            }
        }

        // ───────────────────────────── токенизатор строки ─────────────────────────────

        private enum Prev : byte { None, Dot, EventKw, Ident, Other }

        /// <summary>
        /// Раскраска одной строки. Возвращает «строка закончилась внутри /* */».
        /// Строки и комментарии — сырым сканом, слова — по правилам классификатора.
        /// </summary>
        public static bool TokenizeLine(string line, bool inComment, SemanticClassifier cls, List<Span> spans)
        {
            int i = 0, n = line.Length;
            var prev = Prev.None;
            string chain = null; // путь через точку, заканчивающийся предыдущим словом

            if (inComment)
            {
                int end = line.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0) { if (n > 0) spans.Add(new Span(0, n, SalColor.Comment)); return true; }
                spans.Add(new Span(0, end + 2, SalColor.Comment));
                i = end + 2;
            }

            while (i < n)
            {
                char c = line[i];
                if (c == '/' && i + 1 < n && line[i + 1] == '/')
                {
                    spans.Add(new Span(i, n - i, SalColor.Comment));
                    return false;
                }
                if (c == '/' && i + 1 < n && line[i + 1] == '*')
                {
                    int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) { spans.Add(new Span(i, n - i, SalColor.Comment)); return true; }
                    spans.Add(new Span(i, end + 2 - i, SalColor.Comment));
                    i = end + 2;
                    continue;
                }
                if (c == '"' || (c == '$' && i + 1 < n && line[i + 1] == '"'))
                {
                    i = ScanString(line, i, cls, spans);
                    prev = Prev.Other;
                    chain = null;
                    continue;
                }
                if (char.IsDigit(c))
                {
                    int s = i;
                    i = ScanNumber(line, i);
                    spans.Add(new Span(s, i - s, SalColor.Number));
                    prev = Prev.Other;
                    chain = null;
                    continue;
                }
                if (c == '_' || char.IsLetter(c))
                {
                    int s = i;
                    while (i < n && TextUtil.IsWordChar(line[i])) i++;
                    string word = line.Substring(s, i - s);
                    bool afterDot = prev == Prev.Dot;
                    string myChain = afterDot ? (chain != null ? chain + "." + word : null) : word;

                    SalColor color;
                    if (!afterDot && Keywords.Contains(word))
                    {
                        color = SalColor.Keyword;
                        prev = word == "event" ? Prev.EventKw : Prev.Other;
                        chain = null;
                        spans.Add(new Span(s, i - s, color));
                        continue;
                    }
                    int j = i;
                    while (j < n && (line[j] == ' ' || line[j] == '\t')) j++;
                    bool beforeParen = j < n && line[j] == '(';
                    if (cls != null)
                        color = CodePalette.FromSemantic(cls.ClassifyIdentifier(word, prev == Prev.EventKw, afterDot,
                            beforeParen, afterDot ? myChain : null));
                    else
                        color = prev == Prev.EventKw ? SalColor.Decorator
                              : afterDot ? (beforeParen ? SalColor.Function : SalColor.Property)
                              : beforeParen ? SalColor.Function : SalColor.Variable;
                    if (color != SalColor.None) spans.Add(new Span(s, i - s, color));
                    prev = Prev.Ident;
                    chain = myChain;
                    continue;
                }
                if (c == '.')
                {
                    // ".." — диапазон, не доступ к члену
                    if (i + 1 < n && line[i + 1] == '.') { prev = Prev.Other; chain = null; i += 2; continue; }
                    prev = prev == Prev.Ident ? Prev.Dot : Prev.Other;
                    if (prev != Prev.Dot) chain = null;
                    i++;
                    continue;
                }
                if (!char.IsWhiteSpace(c)) { prev = Prev.Other; chain = null; }
                i++;
            }
            return false;
        }

        private static int ScanNumber(string line, int i)
        {
            int n = line.Length;
            while (i < n && char.IsDigit(line[i])) i++;
            // дробная часть: точка допустима только перед цифрой — «1..5» остаётся двумя числами
            if (i + 1 < n && line[i] == '.' && char.IsDigit(line[i + 1]))
            {
                i++;
                while (i < n && char.IsDigit(line[i])) i++;
            }
            if (i < n && (line[i] == 'd' || line[i] == 'D' || line[i] == 'f' || line[i] == 'F')) i++;
            return i;
        }

        // "строка" / $"интерполяция {код}" до кавычки или конца строки; дырки {…} красятся как код
        private static int ScanString(string line, int i, SemanticClassifier cls, List<Span> spans)
        {
            int n = line.Length;
            bool interp = line[i] == '$';
            int segStart = i;
            i += interp ? 2 : 1;
            while (i < n)
            {
                char c = line[i];
                if (c == '\\' && i + 1 < n) { i += 2; continue; }
                if (c == '"') { i++; break; }
                if (interp && c == '{')
                {
                    if (i + 1 < n && line[i + 1] == '{') { i += 2; continue; }
                    // строка до '{' включительно
                    spans.Add(new Span(segStart, i + 1 - segStart, SalColor.String));
                    int holeStart = i + 1;
                    int holeEnd = holeStart;
                    while (holeEnd < n && line[holeEnd] != '}' && line[holeEnd] != '"') holeEnd++;
                    if (holeEnd > holeStart)
                    {
                        var inner = new List<Span>();
                        TokenizeLine(line.Substring(holeStart, holeEnd - holeStart), false, cls, inner);
                        foreach (var s in inner) spans.Add(new Span(s.Start + holeStart, s.Length, s.Color));
                    }
                    i = holeEnd;
                    segStart = i;
                    if (i < n && line[i] == '}') i++;
                    continue;
                }
                i++;
            }
            if (i > segStart) spans.Add(new Span(segStart, i - segStart, SalColor.String));
            return i;
        }

        /// <summary>
        /// Внутри строки или комментария ли позиция (col — 0-based в строке lineText).
        /// Учитывает состояние начала строки (многострочные /* */).
        /// </summary>
        public static bool IsInCommentOrString(string lineText, int col, bool startsInComment)
        {
            bool inBlock = startsInComment, inString = false, interp = false, inHole = false;
            for (int i = 0; i < col && i < lineText.Length; i++)
            {
                char c = lineText[i];
                if (inBlock)
                {
                    if (c == '*' && i + 1 < lineText.Length && lineText[i + 1] == '/') { inBlock = false; i++; }
                    continue;
                }
                if (inHole)
                {
                    // дырка $"…{код}…" — это код, подсказки в ней уместны
                    if (c == '}') inHole = false;
                    else if (c == '"') { inHole = false; inString = false; }
                    continue;
                }
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                    else if (interp && c == '{')
                    {
                        if (i + 1 < lineText.Length && lineText[i + 1] == '{') i++;
                        else inHole = true;
                    }
                    continue;
                }
                if (c == '"') { inString = true; interp = i > 0 && lineText[i - 1] == '$'; }
                else if (c == '/' && i + 1 < lineText.Length && lineText[i + 1] == '/') return true;
                else if (c == '/' && i + 1 < lineText.Length && lineText[i + 1] == '*') { inBlock = true; i++; }
            }
            return inBlock || (inString && !inHole);
        }
    }
}
