using System.Collections.Generic;
using System.Text;

namespace Dsl.Text
{
    /// <summary>
    /// Лексер работает за один проход по строке. Запускается только на этапе
    /// загрузки/сборки, поэтому здесь допустимы StringBuilder и List —
    /// это не горячий путь рантайма.
    /// </summary>
    public sealed class Lexer
    {
        private readonly string _src;
        private readonly int _fileId;
        private readonly DiagnosticBag _diag;

        private int _pos;
        private int _line;
        private int _col;

        private static readonly Dictionary<string, TokenKind> Keywords =
            new Dictionary<string, TokenKind>
        {
            { "class", TokenKind.KwClass },
            { "trigger", TokenKind.KwTrigger },
            { "listener", TokenKind.KwListener },
            { "self", TokenKind.KwSelf },
            { "pass", TokenKind.KwPass },
            { "disabled", TokenKind.KwDisabled },
            { "enum", TokenKind.KwEnum },
            { "func", TokenKind.KwFunc },
            { "action", TokenKind.KwAction },
            { "event", TokenKind.KwEvent },
            { "const", TokenKind.KwConst },
            { "var", TokenKind.KwVar },
            { "if", TokenKind.KwIf },
            { "else", TokenKind.KwElse },
            { "while", TokenKind.KwWhile },
            { "for", TokenKind.KwFor },
            { "in", TokenKind.KwIn },
            { "break", TokenKind.KwBreak },
            { "continue", TokenKind.KwContinue },
            { "return", TokenKind.KwReturn },
            { "wait", TokenKind.KwWait },
            { "until", TokenKind.KwUntil },
            { "spawn", TokenKind.KwSpawn },
            { "new", TokenKind.KwNew },
            { "true", TokenKind.KwTrue },
            { "false", TokenKind.KwFalse },
            { "null", TokenKind.KwNull },
        };

        public Lexer(string src, int fileId, DiagnosticBag diag, int startOffset = 0, int startLine = 1, int startCol = 1)
        {
            _src = src ?? string.Empty;
            _fileId = fileId;
            _diag = diag;
            _pos = startOffset;
            _line = startLine;
            _col = startCol;
        }

        private char Cur => _pos < _src.Length ? _src[_pos] : '\0';
        private char Peek(int k = 1) => _pos + k < _src.Length ? _src[_pos + k] : '\0';
        private bool End => _pos >= _src.Length;
        private SourcePos Here => new SourcePos(_fileId, _pos, _line, _col);

        private void Advance()
        {
            if (_pos >= _src.Length) return;
            if (_src[_pos] == '\n') { _line++; _col = 1; }
            else _col++;
            _pos++;
        }

        /// <summary>Прогоняет весь файл в список токенов, завершая Eof.</summary>
        public List<Token> Tokenize()
        {
            var result = new List<Token>();
            while (true)
            {
                var t = Next();
                result.Add(t);
                if (t.Kind == TokenKind.Eof) break;
            }
            return result;
        }

        private Token Next()
        {
            SkipTrivia();
            if (End) return new Token(TokenKind.Eof, "", null, Here);

            char c = Cur;
            SourcePos start = Here;

            if (c == '_' || IsLetter(c)) return LexIdentOrKeyword(start);
            if (IsDigit(c)) return LexNumber(start);
            if (c == '"') return LexString(start, false);
            if (c == '$' && Peek() == '"') return LexString(start, true);

            return LexSymbol(start);
        }

        private void SkipTrivia()
        {
            while (!End)
            {
                char c = Cur;
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { Advance(); continue; }

                // // однострочный комментарий
                if (c == '/' && Peek() == '/')
                {
                    while (!End && Cur != '\n') Advance();
                    continue;
                }
                // /* ... */ блочный комментарий
                if (c == '/' && Peek() == '*')
                {
                    Advance(); Advance();
                    while (!End && !(Cur == '*' && Peek() == '/')) Advance();
                    if (!End) { Advance(); Advance(); }
                    continue;
                }
                break;
            }
        }

        private Token LexIdentOrKeyword(SourcePos start)
        {
            int s = _pos;
            while (!End && (Cur == '_' || IsLetter(Cur) || IsDigit(Cur))) Advance();
            string text = _src.Substring(s, _pos - s);
            if (Keywords.TryGetValue(text, out var kw))
                return new Token(kw, text, null, start);
            return new Token(TokenKind.Ident, text, null, start);
        }

        private Token LexNumber(SourcePos start)
        {
            int s = _pos;
            while (!End && IsDigit(Cur)) Advance();

            bool isFloat = false;
            // дробная часть только если после точки идёт цифра (иначе это '.' или '..')
            if (Cur == '.' && IsDigit(Peek()))
            {
                isFloat = true;
                Advance(); // '.'
                while (!End && IsDigit(Cur)) Advance();
            }

            string text = _src.Substring(s, _pos - s);
            return new Token(isFloat ? TokenKind.Float : TokenKind.Int, text, null, start);
        }

        private Token LexString(SourcePos start, bool interpolated)
        {
            if (interpolated) Advance(); // '$'
            Advance();                    // открывающая '"'

            var template = interpolated ? new InterpTemplate() : null;
            var lit = new StringBuilder();

            while (!End && Cur != '"')
            {
                char c = Cur;

                if (c == '\\')
                {
                    Advance();
                    lit.Append(ReadEscape());
                    continue;
                }

                if (interpolated && c == '{')
                {
                    // выгружаем накопленный литерал
                    if (lit.Length > 0)
                    {
                        template.Parts.Add(new InterpPart(false, lit.ToString(), start));
                        lit.Clear();
                    }
                    Advance(); // '{'
                    SourcePos exprStart = Here;
                    int es = _pos;
                    int depth = 1;
                    while (!End && depth > 0)
                    {
                        if (Cur == '{') depth++;
                        else if (Cur == '}') { depth--; if (depth == 0) break; }
                        Advance();
                    }
                    string exprSrc = _src.Substring(es, _pos - es);
                    template.Parts.Add(new InterpPart(true, exprSrc, exprStart));
                    if (Cur == '}') Advance();
                    continue;
                }

                lit.Append(c);
                Advance();
            }

            if (End)
            {
                _diag?.Error("E0002", "Незакрытая строковая литеральная константа.", start);
            }
            else
            {
                Advance(); // закрывающая '"'
            }

            if (interpolated)
            {
                if (lit.Length > 0)
                    template.Parts.Add(new InterpPart(false, lit.ToString(), start));
                return new Token(TokenKind.InterpString, "", template, start);
            }

            return new Token(TokenKind.String, "", lit.ToString(), start);
        }

        private string ReadEscape()
        {
            char e = Cur;
            Advance();
            switch (e)
            {
                case 'n': return "\n";
                case 't': return "\t";
                case 'r': return "\r";
                case '"': return "\"";
                case '\\': return "\\";
                case '{': return "{";
                case '}': return "}";
                default: return e.ToString();
            }
        }

        private Token LexSymbol(SourcePos start)
        {
            char c = Cur;
            char n = Peek();

            // двухсимвольные
            switch (c)
            {
                case '-' when n == '>': Advance(); Advance(); return Tk(TokenKind.Arrow, "->", start);
                case '.' when n == '.': Advance(); Advance(); return Tk(TokenKind.DotDot, "..", start);
                case ':' when n == ':': Advance(); Advance(); return Tk(TokenKind.ColonColon, "::", start);
                case '=' when n == '=': Advance(); Advance(); return Tk(TokenKind.EqEq, "==", start);
                case '!' when n == '=': Advance(); Advance(); return Tk(TokenKind.NotEq, "!=", start);
                case '<' when n == '=': Advance(); Advance(); return Tk(TokenKind.Le, "<=", start);
                case '>' when n == '=': Advance(); Advance(); return Tk(TokenKind.Ge, ">=", start);
                case '&' when n == '&': Advance(); Advance(); return Tk(TokenKind.AndAnd, "&&", start);
                case '|' when n == '|': Advance(); Advance(); return Tk(TokenKind.OrOr, "||", start);
                case '+' when n == '=': Advance(); Advance(); return Tk(TokenKind.PlusAssign, "+=", start);
                case '-' when n == '=': Advance(); Advance(); return Tk(TokenKind.MinusAssign, "-=", start);
                case '*' when n == '=': Advance(); Advance(); return Tk(TokenKind.StarAssign, "*=", start);
                case '/' when n == '=': Advance(); Advance(); return Tk(TokenKind.SlashAssign, "/=", start);
            }

            // односимвольные
            Advance();
            switch (c)
            {
                case '{': return Tk(TokenKind.LBrace, "{", start);
                case '}': return Tk(TokenKind.RBrace, "}", start);
                case '(': return Tk(TokenKind.LParen, "(", start);
                case ')': return Tk(TokenKind.RParen, ")", start);
                case '[': return Tk(TokenKind.LBracket, "[", start);
                case ']': return Tk(TokenKind.RBracket, "]", start);
                case ',': return Tk(TokenKind.Comma, ",", start);
                case ';': return Tk(TokenKind.Semicolon, ";", start);
                case '.': return Tk(TokenKind.Dot, ".", start);
                case '=': return Tk(TokenKind.Assign, "=", start);
                case '+': return Tk(TokenKind.Plus, "+", start);
                case '-': return Tk(TokenKind.Minus, "-", start);
                case '*': return Tk(TokenKind.Star, "*", start);
                case '/': return Tk(TokenKind.Slash, "/", start);
                case '%': return Tk(TokenKind.Percent, "%", start);
                case '<': return Tk(TokenKind.Lt, "<", start);
                case '>': return Tk(TokenKind.Gt, ">", start);
                case '!': return Tk(TokenKind.Not, "!", start);
            }

            _diag?.Error("E0001", $"Неожиданный символ '{c}'.", start);
            return Tk(TokenKind.Invalid, c.ToString(), start);
        }

        private static Token Tk(TokenKind k, string text, SourcePos pos) => new Token(k, text, null, pos);

        private static bool IsLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        private static bool IsDigit(char c) => c >= '0' && c <= '9';
    }
}
