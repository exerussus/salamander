using System;

namespace Dsl.Text
{
    /// <summary>
    /// Позиция символа в исходном файле. Хранит абсолютное смещение,
    /// а также строку/колонку (1-based) для человекочитаемых сообщений.
    /// Это value-type, чтобы таскать её по AST без аллокаций.
    /// </summary>
    public readonly struct SourcePos : IEquatable<SourcePos>
    {
        public readonly int FileId;   // индекс исходника в компиляции
        public readonly int Offset;   // смещение от начала файла
        public readonly int Line;     // строка, 1-based
        public readonly int Column;   // колонка, 1-based

        public SourcePos(int fileId, int offset, int line, int column)
        {
            FileId = fileId;
            Offset = offset;
            Line = line;
            Column = column;
        }

        public static readonly SourcePos None = new SourcePos(-1, 0, 0, 0);

        public bool Equals(SourcePos other) =>
            FileId == other.FileId && Offset == other.Offset;

        public override bool Equals(object obj) => obj is SourcePos p && Equals(p);
        public override int GetHashCode() => (FileId * 397) ^ Offset;
        public override string ToString() => $"{Line}:{Column}";
    }

    /// <summary>
    /// Исходный файл: логическое имя (для сообщений) и его текст.
    /// FileId присваивается при регистрации в компиляции.
    /// </summary>
    public sealed class SourceText
    {
        public readonly int FileId;
        public readonly string Name;   // напр. "mymod/src/triggers.script"
        public readonly string Text;

        public SourceText(int fileId, string name, string text)
        {
            FileId = fileId;
            Name = name ?? "<unknown>";
            Text = text ?? string.Empty;
        }
    }
}
