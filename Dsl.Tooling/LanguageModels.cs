using System.Collections.Generic;

namespace Dsl.Tooling
{
    /// <summary>
    /// Вид подсказки. Значения совпадают с CompletionItemKind протокола LSP —
    /// сервер отдаёт их числом как есть, встроенная IDE красит по ним.
    /// </summary>
    public enum CompletionKind
    {
        Method = 2,
        Function = 3,
        Field = 5,
        Class = 7,
        Module = 9,
        Property = 10,
        Enum = 13,
        Keyword = 14,
        EnumMember = 20,
        Constant = 21,
        Struct = 22,
        Event = 23,
    }

    public sealed class CompletionItem
    {
        public string Label;
        public CompletionKind Kind;
        public string Detail;          // сигнатура/тип (может быть null)
        public string Documentation;   // markdown (может быть null)
        public string InsertText;      // null — вставляется Label
        public bool IsSnippet;         // InsertText в синтаксисе сниппетов LSP ($1, ${1:x}, $0)
    }

    public sealed class SignatureInfo
    {
        public string Label;
        public string Documentation;   // markdown или null
        public List<string> Parameters = new List<string>();
        public int ActiveParameter;
    }

    /// <summary>Место в файле: ключ файла + 1-based позиция и длина.</summary>
    public sealed class SymbolLocation
    {
        public string File;
        public int Line;
        public int Col;
        public int Length;
    }

    /// <summary>Отрезок семантической раскраски: 1-based позиция, длина, индекс в SemanticClassifier.TokenTypes.</summary>
    public readonly struct ClassifiedSpan
    {
        public readonly int Line;
        public readonly int Col;
        public readonly int Length;
        public readonly int Type;

        public ClassifiedSpan(int line, int col, int length, int type)
        {
            Line = line;
            Col = col;
            Length = length;
            Type = type;
        }
    }
}
