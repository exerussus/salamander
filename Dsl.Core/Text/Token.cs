using System.Collections.Generic;

namespace Dsl.Text
{
    public enum TokenKind : byte
    {
        // служебные
        Eof,
        Invalid,

        // литералы / идентификаторы
        Ident,
        Int,
        Float,
        String,        // обычная строка, Value = string
        InterpString,  // интерполяция, Value = InterpTemplate

        // ключевые слова
        KwClass, KwTrigger, KwDisabled, KwEnum, KwListener, KwSelf, KwPass,
        KwFunc, KwAction, KwEvent, KwConst, KwVar,
        KwIf, KwElse, KwWhile, KwFor, KwIn,
        KwBreak, KwContinue, KwReturn,
        KwWait, KwUntil, KwSpawn, KwNew,
        KwTrue, KwFalse, KwNull,

        // разделители
        LBrace, RBrace, LParen, RParen, LBracket, RBracket,
        Comma, Semicolon, Dot, DotDot, ColonColon, Arrow,

        // операторы
        Assign, PlusAssign, MinusAssign, StarAssign, SlashAssign,
        Plus, Minus, Star, Slash, Percent,
        Lt, Le, Gt, Ge, EqEq, NotEq,
        AndAnd, OrOr, Not,
    }

    /// <summary>
    /// Один фрагмент интерполированной строки: либо готовый литеральный текст,
    /// либо исходный код выражения внутри {...} с абсолютной позицией,
    /// чтобы под-парсер выдавал корректные строки/колонки в ошибках.
    /// </summary>
    public readonly struct InterpPart
    {
        public readonly bool IsExpr;
        public readonly string Text;   // литерал (если !IsExpr) или исходник выражения (если IsExpr)
        public readonly SourcePos Pos;  // позиция начала (для IsExpr)

        public InterpPart(bool isExpr, string text, SourcePos pos)
        {
            IsExpr = isExpr;
            Text = text;
            Pos = pos;
        }
    }

    public sealed class InterpTemplate
    {
        public readonly List<InterpPart> Parts = new List<InterpPart>();
    }

    /// <summary>
    /// Токен. Text — сырой срез (для идентификаторов/чисел),
    /// Value — уже разобранное значение для строк (string / InterpTemplate).
    /// </summary>
    public readonly struct Token
    {
        public readonly TokenKind Kind;
        public readonly string Text;
        public readonly object Value;
        public readonly SourcePos Pos;

        public Token(TokenKind kind, string text, object value, SourcePos pos)
        {
            Kind = kind;
            Text = text;
            Value = value;
            Pos = pos;
        }

        public override string ToString() => $"{Kind} '{Text}' @ {Pos}";
    }
}
