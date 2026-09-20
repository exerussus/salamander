using System;
using System.Collections.Generic;
using Dsl.Text;

namespace Dsl.Ide
{
    /// <summary>
    /// Открытый файл: буфер, сохранённая версия, отпечаток на диске, история,
    /// положение каретки и диагностики. Внутри IDE переводы строк — всегда '\n';
    /// исходный стиль (CRLF/LF) запоминается и восстанавливается при записи —
    /// иначе каждое сохранение на Windows переписывало бы весь файл в git.
    /// </summary>
    public sealed class IdeDocument
    {
        public readonly string Key;
        public string DisplayName;

        /// <summary>Логическое имя в модуле ("mod/src/a.sal") или null — файл вне модулей воркспейса.</summary>
        public string Logical;

        /// <summary>Текущий буфер (переводы строк — '\n').</summary>
        public string Text { get; private set; } = "";

        /// <summary>Что лежит на диске (после последнего чтения/записи), тоже с '\n'.</summary>
        public string SavedText { get; private set; } = "";

        /// <summary>Стиль переводов строк файла на диске.</summary>
        public string LineEnding = "\n";

        public FileStamp DiskStamp;

        /// <summary>Растёт на каждую правку буфера — по нему отбрасываются устаревшие результаты компиляции.</summary>
        public int Version { get; private set; }

        public bool Dirty => !string.Equals(Text, SavedText, StringComparison.Ordinal);

        public readonly UndoHistory History = new UndoHistory();

        // вид
        public int CaretIndex;
        public int SelectIndex;
        public float ScrollX;
        public float ScrollY;

        // внешнее изменение при несохранённых правках: версия с диска ждёт решения
        public string ConflictText;
        public bool HasConflict => ConflictText != null;

        // диагностики этого файла (последняя компиляция) и для какой версии буфера
        public readonly List<Diagnostic> Diagnostics = new List<Diagnostic>();
        public int DiagnosticsVersion = -1;

        public IdeDocument(string key, string displayName)
        {
            Key = key;
            DisplayName = displayName;
        }

        /// <summary>Загрузка с диска: буфер = сохранённая версия, история с нуля.</summary>
        public void LoadFromDisk(string diskText, FileStamp stamp)
        {
            diskText ??= "";
            LineEnding = DetectLineEnding(diskText);
            string norm = Normalize(diskText);
            SavedText = norm;
            DiskStamp = stamp;
            ConflictText = null;
            SetText(norm);
            History.Reset();
        }

        /// <summary>Правка буфера (из редактора).</summary>
        public void SetText(string text)
        {
            text = text ?? "";
            if (string.Equals(Text, text, StringComparison.Ordinal)) return;
            Text = text;
            Version++;
        }

        /// <summary>Текст для записи на диск — в исходном стиле переводов строк.</summary>
        public string TextForDisk() => LineEnding == "\n" ? Text : Text.Replace("\n", LineEnding);

        /// <summary>Файл записан: буфер стал сохранённой версией.</summary>
        public void MarkSaved(FileStamp stamp)
        {
            SavedText = Text;
            DiskStamp = stamp;
            ConflictText = null;
        }

        /// <summary>
        /// Конфликт решён в пользу буфера: базой считается версия с диска, буфер остаётся
        /// изменённым, и следующее сохранение перезапишет файл без повторного вопроса.
        /// </summary>
        public void AcceptDiskAsBase(string diskText, FileStamp stamp)
        {
            SavedText = Normalize(diskText);
            DiskStamp = stamp;
            ConflictText = null;
        }

        /// <summary>Восстановление несохранённого буфера из бэкапа (сохранённая версия — с диска).</summary>
        public void RestoreBuffer(string bufferText)
        {
            SetText(Normalize(bufferText));
        }

        public static string Normalize(string text) =>
            text == null ? "" : text.IndexOf('\r') < 0 ? text : text.Replace("\r\n", "\n").Replace('\r', '\n');

        private static string DetectLineEnding(string text)
        {
            int crlf = 0, lf = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                if (i > 0 && text[i - 1] == '\r') crlf++; else lf++;
            }
            return crlf > lf ? "\r\n" : "\n";
        }
    }
}
