using System.Collections.Generic;

namespace Dsl.Text
{
    public enum Severity : byte
    {
        Info = 0,
        Warning = 1,
        Error = 2,
    }

    /// <summary>
    /// Одно сообщение компилятора. Код (напр. "E0101") стабилен и пригоден
    /// для подсветки в редакторе; File/Line/Column ведут прямо к месту.
    /// </summary>
    public readonly struct Diagnostic
    {
        public readonly Severity Severity;
        public readonly string Code;
        public readonly string Message;
        public readonly string File;
        public readonly int Line;
        public readonly int Column;

        public Diagnostic(Severity severity, string code, string message, string file, int line, int column)
        {
            Severity = severity;
            Code = code;
            Message = message;
            File = file;
            Line = line;
            Column = column;
        }

        public override string ToString()
        {
            string sev = Severity == Severity.Error ? "error"
                       : Severity == Severity.Warning ? "warning" : "info";
            return $"{File}:{Line}:{Column}: {sev} {Code}: {Message}";
        }
    }

    /// <summary>
    /// Копилка диагностик за один проход компиляции. Живёт только на этапе
    /// загрузки/сборки (не горячий путь), поэтому List здесь допустим.
    /// </summary>
    public sealed class DiagnosticBag
    {
        private readonly List<Diagnostic> _items = new List<Diagnostic>();
        private readonly IReadOnlyList<SourceText> _files;

        public DiagnosticBag(IReadOnlyList<SourceText> files)
        {
            _files = files;
        }

        public IReadOnlyList<Diagnostic> Items => _items;
        public bool HasErrors { get; private set; }

        /// <summary>
        /// Потолок собираемых сообщений. Страховка от «водопадов» на битом вводе:
        /// один сломанный файл не должен раздувать список до OutOfMemory и не должен
        /// топить настоящие ошибки. После переполнения добавляется финальная запись,
        /// сбор прекращается, но HasErrors остаётся верным.
        /// </summary>
        public int MaxItems = 500;

        private bool _overflowed;

        public void Report(Severity severity, string code, string message, SourcePos pos)
        {
            if (severity == Severity.Error) HasErrors = true;
            if (_overflowed) return;
            if (_items.Count >= MaxItems)
            {
                _overflowed = true;
                _items.Add(new Diagnostic(Severity.Error, "E0221",
                    $"Сообщений больше {MaxItems} — остальные не показаны. Исправьте первые: обычно дальше идут их следствия.",
                    "<compiler>", 0, 0));
                return;
            }

            string name = pos.FileId >= 0 && _files != null && pos.FileId < _files.Count
                ? _files[pos.FileId].Name
                : "<unknown>";
            _items.Add(new Diagnostic(severity, code, message, name, pos.Line, pos.Column));
        }

        public void Error(string code, string message, SourcePos pos) =>
            Report(Severity.Error, code, message, pos);

        public void Warning(string code, string message, SourcePos pos) =>
            Report(Severity.Warning, code, message, pos);
    }
}
