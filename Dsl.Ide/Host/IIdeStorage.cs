using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dsl.Ide
{
    /// <summary>
    /// Личное хранилище IDE: сессия (открытые вкладки, каретки), бэкапы
    /// несохранённых буферов, копия манифеста. Ключ — относительное имя файла
    /// ("session.json", "buffers/ab12.sal"). В редакторе — UserSettings
    /// страницы, в игре — Application.persistentDataPath.
    /// </summary>
    public interface IIdeStorage
    {
        /// <summary>Содержимое или null, если записи нет.</summary>
        string Read(string key);

        void Write(string key, string value);

        void Delete(string key);
    }

    /// <summary>Хранилище-папка. Сбои ввода-вывода не бросает: хранилище — удобство, не повод ронять IDE.</summary>
    public sealed class FileIdeStorage : IIdeStorage
    {
        private readonly string _root;
        private readonly Action<string, Exception> _onError;

        public string Root => _root;

        public FileIdeStorage(string root, Action<string, Exception> onError = null)
        {
            _root = Path.GetFullPath(root);
            _onError = onError;
        }

        private string PathOf(string key)
        {
            // ключ — только относительное имя внутри корня: никаких '..' и абсолютных путей
            string rel = (key ?? "").Replace('\\', '/').TrimStart('/');
            if (rel.Length == 0 || rel.Contains("..")) throw new ArgumentException("недопустимый ключ хранилища: " + key);
            return Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        public string Read(string key)
        {
            try
            {
                string p = PathOf(key);
                return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null;
            }
            catch (Exception e)
            {
                _onError?.Invoke("чтение " + key, e);
                return null;
            }
        }

        public void Write(string key, string value)
        {
            try
            {
                string p = PathOf(key);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                // сначала во временный файл, потом замена: обрыв записи не оставляет полуфайл
                string tmp = p + ".tmp";
                File.WriteAllText(tmp, value ?? "", new UTF8Encoding(false));
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
            }
            catch (Exception e)
            {
                _onError?.Invoke("запись " + key, e);
            }
        }

        public void Delete(string key)
        {
            try
            {
                string p = PathOf(key);
                if (File.Exists(p)) File.Delete(p);
            }
            catch (Exception e)
            {
                _onError?.Invoke("удаление " + key, e);
            }
        }
    }

    /// <summary>Хранилище в памяти: тесты и платформы без файловой системы.</summary>
    public sealed class MemoryIdeStorage : IIdeStorage
    {
        private readonly Dictionary<string, string> _data = new Dictionary<string, string>(StringComparer.Ordinal);

        public string Read(string key) => _data.TryGetValue(key, out var v) ? v : null;

        public void Write(string key, string value) => _data[key] = value ?? "";

        public void Delete(string key) => _data.Remove(key);
    }
}
