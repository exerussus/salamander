using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dsl.Compilation;
using Dsl.Tooling;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dsl.Ide
{
    /// <summary>Отпечаток файла на диске (время записи + размер): дешёвый детектор внешних правок.</summary>
    public readonly struct FileStamp : IEquatable<FileStamp>
    {
        public readonly long Ticks;
        public readonly long Length;

        public FileStamp(long ticks, long length)
        {
            Ticks = ticks;
            Length = length;
        }

        /// <summary>Файла нет (или источник не файловый).</summary>
        public static readonly FileStamp None = new FileStamp(0, -1);

        public bool IsNone => Ticks == 0 && Length == -1;

        public static FileStamp Of(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? new FileStamp(fi.LastWriteTimeUtc.Ticks, fi.Length) : None;
            }
            catch { return None; }
        }

        public bool Equals(FileStamp o) => Ticks == o.Ticks && Length == o.Length;
        public override bool Equals(object obj) => obj is FileStamp o && Equals(o);
        public override int GetHashCode() => Ticks.GetHashCode() * 31 + Length.GetHashCode();
    }

    /// <summary>
    /// Откуда IDE берёт модули и куда пишет. Ключ файла — строка, стабильная
    /// в пределах воркспейса: у файлового — полный путь, у памяти — "mem:" +
    /// логическое имя. Реализации: папка на диске (<see cref="FileSystemWorkspace"/>),
    /// память (<see cref="MemoryWorkspace"/>), живая игра (BootstrapWorkspace).
    /// </summary>
    public interface IScriptWorkspace
    {
        string DisplayName { get; }

        /// <summary>Папка модулей или null (воркспейс не файловый).</summary>
        string Root { get; }

        bool CanCreateFiles { get; }

        /// <summary>Снимок модулей. LogicalToPath отображает логические имена в КЛЮЧИ этого воркспейса.</summary>
        WorkspaceModules Load();

        /// <summary>Канонический ключ для пути/ключа (нормализация внешнего пути). null — не распознан.</summary>
        string NormalizeKey(string pathOrKey);

        /// <summary>Текст файла; null — нет такого.</summary>
        string Read(string key);

        bool Write(string key, string text, out string error);

        /// <summary>Отпечаток на диске; FileStamp.None — не файл.</summary>
        FileStamp GetStamp(string key);

        /// <summary>Меняется при любом изменении набора модулей/файлов извне. Дёргается раз в пару секунд.</summary>
        long GetChangeToken();

        /// <summary>Короткое имя для вкладки/статуса.</summary>
        string DisplayNameOf(string key);

        /// <summary>Абсолютный путь на диске или null.</summary>
        string FilePathOf(string key);

        /// <summary>Создать файл в модуле и прописать его в module.json. Возвращает ключ или null.</summary>
        string CreateFile(string moduleName, string relativePath, out string error);

        /// <summary>.sal в папке модуля, которых нет в его module.json (не компилируются).</summary>
        IEnumerable<string> ListUnlistedFiles(string moduleName);
    }

    /// <summary>
    /// Папка модулей на диске (редактор, десктоп-игра с модами). Изменения
    /// ловит FileSystemWatcher (дёшево даже когда корень — весь Assets); где
    /// вотчера нет — редкий обход папки.
    /// </summary>
    public sealed class FileSystemWorkspace : IScriptWorkspace, IDisposable
    {
        private readonly string _root;
        private readonly string _buildFile;
        private readonly IModuleReader _reader;
        private WorkspaceModules _last;

        private FileSystemWatcher _watcher;
        private bool _watchFailed;
        private long _changes;          // растёт из потока вотчера (Interlocked)
        private long _scanToken;        // запасной путь без вотчера
        private int _scanSkip;

        /// <param name="reader">
        /// Чем читать модули: свой формат пака подставляется сюда, и тогда IDE
        /// видит его ровно так же, как LSP и чекер с тем же читателем.
        /// null — <see cref="WorkspaceLoader.DefaultReader"/> (module.json).
        /// </param>
        public FileSystemWorkspace(string root, string buildFile = null, IModuleReader reader = null)
        {
            _root = string.IsNullOrEmpty(root) ? null : Path.GetFullPath(root);
            _buildFile = buildFile;
            _reader = reader;
        }

        /// <summary>Читатель модулей этого воркспейса (никогда не null).</summary>
        public IModuleReader Reader => _reader ?? WorkspaceLoader.DefaultReader ?? ModuleJsonReader.Instance;

        public string DisplayName => _root == null ? "нет папки" : Path.GetFileName(_root.TrimEnd('/', '\\'));
        public string Root => _root;
        public bool CanCreateFiles => true;

        public WorkspaceModules Load()
        {
            var ws = _root != null && Directory.Exists(_root)
                ? WorkspaceLoader.Load(_root, _buildFile, _reader)
                : new WorkspaceModules();
            // ключи файлового воркспейса — полные пути
            var keys = new List<string>(ws.LogicalToPath.Keys);
            foreach (var k in keys) ws.LogicalToPath[k] = Path.GetFullPath(ws.LogicalToPath[k]);
            _last = ws;
            return ws;
        }

        public string NormalizeKey(string pathOrKey)
        {
            if (string.IsNullOrEmpty(pathOrKey)) return null;
            try { return Path.GetFullPath(pathOrKey); }
            catch { return null; }
        }

        public string Read(string key)
        {
            try { return File.Exists(key) ? File.ReadAllText(key) : null; }
            catch { return null; }
        }

        public bool Write(string key, string text, out string error)
        {
            return WriteFile(key, text, out error);
        }

        /// <summary>Запись исходника: сохраняет BOM, если он был, и пишет через временный файл.</summary>
        public static bool WriteFile(string path, string text, out string error)
        {
            error = null;
            try
            {
                bool bom = false;
                if (File.Exists(path))
                {
                    using (var fs = File.OpenRead(path))
                    {
                        var head = new byte[3];
                        bom = fs.Read(head, 0, 3) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
                    }
                }
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string tmp = path + ".saltmp";
                File.WriteAllText(tmp, text ?? "", new UTF8Encoding(bom));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        public FileStamp GetStamp(string key) => FileStamp.Of(key);

        public long GetChangeToken()
        {
            if (_root == null || !Directory.Exists(_root)) return 0;
            if (EnsureWatcher()) return System.Threading.Interlocked.Read(ref _changes);
            // без вотчера — полный обход не чаще раза в ~10 с (зовут раз в 2 с)
            if (_scanSkip-- > 0) return _scanToken;
            _scanSkip = 4;
            return _scanToken = ScanToken();
        }

        private bool EnsureWatcher()
        {
            if (_watcher != null) return true;
            if (_watchFailed) return false;
            try
            {
                _watcher = new FileSystemWatcher(_root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                };
                _watcher.Changed += OnFsEvent;
                _watcher.Created += OnFsEvent;
                _watcher.Deleted += OnFsEvent;
                _watcher.Renamed += (_, e) => { if (Relevant(e.FullPath) || Relevant(e.OldFullPath)) Bump(); };
                _watcher.Error += (_, __) => Bump(); // переполнение буфера — перечитать всё
                _watcher.EnableRaisingEvents = true;
                return true;
            }
            catch
            {
                // платформа без FileSystemWatcher — работаем опросом
                _watchFailed = true;
                try { _watcher?.Dispose(); } catch { }
                _watcher = null;
                return false;
            }
        }

        private void OnFsEvent(object sender, FileSystemEventArgs e)
        {
            if (Relevant(e.FullPath)) Bump();
        }

        private void Bump() => System.Threading.Interlocked.Increment(ref _changes);

        // только то, что меняет набор модулей: исходники, манифесты, папки
        private bool Relevant(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            string name = Path.GetFileName(path);
            if (name.Length == 0 || name[0] == '.') return false;
            if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.EndsWith(".sal", StringComparison.OrdinalIgnoreCase)) return true;
            // имя манифеста знает читатель: у своего формата пака оно своё
            try { if (Reader.IsManifestFile(path)) return true; }
            catch { }
            if (string.Equals(name, WorkspaceLoader.BuildFileName, StringComparison.OrdinalIgnoreCase)) return true;
            return string.IsNullOrEmpty(Path.GetExtension(name)); // папка модуля переименована/удалена
        }

        private long ScanToken()
        {
            unchecked
            {
                long h = 17;
                foreach (var f in ModuleLoader.EnumerateFiles(_root, "*.sal"))
                    h = h * 31 + Mix(f);
                // манифесты: имя знает читатель, поэтому берём все .json и спрашиваем его
                foreach (var f in ModuleLoader.EnumerateFiles(_root, "*.json"))
                    if (Reader.IsManifestFile(f)) h = h * 31 + Mix(f);
                string build = _buildFile ?? Path.Combine(_root, WorkspaceLoader.BuildFileName);
                if (File.Exists(build)) h = h * 31 + Mix(build);
                return h;
            }
        }

        public void Dispose()
        {
            try { _watcher?.Dispose(); } catch { }
            _watcher = null;
        }

        private static long Mix(string path)
        {
            var s = FileStamp.Of(path);
            unchecked { return path.GetHashCode() * 397L ^ s.Ticks ^ (s.Length << 20); }
        }

        public string DisplayNameOf(string key) => Path.GetFileName(key);

        public string FilePathOf(string key) => key;

        public string CreateFile(string moduleName, string relativePath, out string error)
        {
            error = null;
            if (_last == null) Load();
            if (!_last.ModuleDirs.TryGetValue(moduleName ?? "", out var dir))
            {
                error = $"модуль «{moduleName}» не найден";
                return null;
            }
            return CreateInModuleDir(dir, relativePath, out error, Reader);
        }

        /// <summary>Создать .sal в папке модуля и прописать его в манифесте.</summary>
        public static string CreateInModuleDir(string moduleDir, string relativePath, out string error,
                                               IModuleReader reader = null)
        {
            error = null;
            string rel = (relativePath ?? "").Trim().Replace('\\', '/').TrimStart('/');
            if (rel.Length == 0) { error = "пустое имя файла"; return null; }
            if (!rel.EndsWith(".sal", StringComparison.OrdinalIgnoreCase)) rel += ".sal";
            string full;
            try
            {
                string root = Path.GetFullPath(moduleDir);
                full = Path.GetFullPath(Path.Combine(root, rel));
                string prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString()) ? root : root + Path.DirectorySeparatorChar;
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { error = "путь выходит за папку модуля"; return null; }
            }
            catch (Exception e) { error = e.Message; return null; }
            if (File.Exists(full)) { error = "такой файл уже есть"; return null; }

            string name = Path.GetFileNameWithoutExtension(full);
            if (!WriteFile(full, $"// {name}\n", out error)) return null;
            var r = reader ?? WorkspaceLoader.DefaultReader ?? ModuleJsonReader.Instance;
            if (!r.AddFile(moduleDir, rel, out string warn))
                error = warn; // файл создан, но в манифест не попал — вызывающий покажет предупреждение
            return full;
        }

        public IEnumerable<string> ListUnlistedFiles(string moduleName)
        {
            if (_last == null || !_last.ModuleDirs.TryGetValue(moduleName ?? "", out var dir))
                return Array.Empty<string>();
            try { return Reader.ListUnlisted(dir); }
            catch { return Array.Empty<string>(); }
        }
    }

    /// <summary>
    /// Модули в памяти: WebGL/мобильные (нет файловой системы у модов), модули,
    /// вшитые в сцену, тесты. Правки живут в памяти воркспейса.
    /// </summary>
    public sealed class MemoryWorkspace : IScriptWorkspace
    {
        public const string KeyPrefix = "mem:";

        private readonly List<ModuleSourceSet> _modules = new List<ModuleSourceSet>();
        private readonly Dictionary<string, string> _texts = new Dictionary<string, string>(StringComparer.Ordinal);
        private long _token = 1;

        public string DisplayName { get; set; } = "в памяти";
        public string Root => null;
        public bool CanCreateFiles => true;

        public MemoryWorkspace(IEnumerable<ModuleSourceSet> modules = null)
        {
            if (modules != null) Replace(modules);
        }

        /// <summary>Заменить набор модулей (например, после перезагрузки из источника игры).</summary>
        public void Replace(IEnumerable<ModuleSourceSet> modules)
        {
            _modules.Clear();
            _texts.Clear();
            foreach (var m in modules)
            {
                if (m?.Manifest == null) continue;
                var copy = WorkspaceCompiler.Copy(m);
                // манифест — своя копия: создание файла дописывает список исходников,
                // и чужой объект (модуль игры) при этом трогать нельзя
                copy.Manifest = JsonConvert.DeserializeObject<ModuleManifest>(JsonConvert.SerializeObject(m.Manifest));
                _modules.Add(copy);
                foreach (var (name, text) in copy.Files) _texts[KeyPrefix + name] = text;
            }
            _token++;
        }

        /// <summary>Модули с текущими правками — например, для перекомпиляции в игре.</summary>
        public List<ModuleSourceSet> Snapshot()
        {
            var r = new List<ModuleSourceSet>(_modules.Count);
            foreach (var m in _modules)
                r.Add(WorkspaceCompiler.Copy(m, logical => _texts.TryGetValue(KeyPrefix + logical, out var t) ? t : null));
            return r;
        }

        public WorkspaceModules Load()
        {
            var ws = new WorkspaceModules();
            foreach (var m in Snapshot())
            {
                ws.Modules.Add(m);
                foreach (var (name, _) in m.Files) ws.LogicalToPath[name] = KeyPrefix + name;
            }
            return ws;
        }

        public string NormalizeKey(string pathOrKey)
        {
            if (string.IsNullOrEmpty(pathOrKey)) return null;
            return pathOrKey.StartsWith(KeyPrefix, StringComparison.Ordinal) ? pathOrKey : KeyPrefix + pathOrKey;
        }

        public string Read(string key) => _texts.TryGetValue(key, out var t) ? t : null;

        public bool Write(string key, string text, out string error)
        {
            error = null;
            if (!_texts.ContainsKey(key)) { error = "файла нет в воркспейсе"; return false; }
            _texts[key] = text ?? "";
            return true;
        }

        public FileStamp GetStamp(string key) => FileStamp.None;

        public long GetChangeToken() => _token;

        public string DisplayNameOf(string key)
        {
            string s = key.StartsWith(KeyPrefix, StringComparison.Ordinal) ? key.Substring(KeyPrefix.Length) : key;
            int slash = s.LastIndexOf('/');
            return slash >= 0 ? s.Substring(slash + 1) : s;
        }

        public string FilePathOf(string key) => null;

        public string CreateFile(string moduleName, string relativePath, out string error)
        {
            error = null;
            var set = _modules.Find(m => m.Manifest.Name == moduleName);
            if (set == null) { error = $"модуль «{moduleName}» не найден"; return null; }
            string rel = (relativePath ?? "").Trim().Replace('\\', '/').TrimStart('/');
            if (rel.Length == 0 || rel.Contains("..")) { error = "недопустимое имя файла"; return null; }
            if (!rel.EndsWith(".sal", StringComparison.OrdinalIgnoreCase)) rel += ".sal";
            string logical = $"{moduleName}/{rel}";
            string key = KeyPrefix + logical;
            if (_texts.ContainsKey(key)) { error = "такой файл уже есть"; return null; }
            string text = $"// {Path.GetFileNameWithoutExtension(rel)}\n";
            set.Files.Add((logical, text));
            var sources = new List<string>(set.Manifest.Sources ?? Array.Empty<string>()) { rel };
            set.Manifest.Sources = sources.ToArray();
            _texts[key] = text;
            _token++;
            return key;
        }

        public IEnumerable<string> ListUnlistedFiles(string moduleName) { yield break; }
    }
}
