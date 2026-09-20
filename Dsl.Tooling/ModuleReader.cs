using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dsl.Compilation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dsl.Tooling
{
    /// <summary>
    /// Как инструменты читают модули с диска. По умолчанию — папка с module.json
    /// и явным списком исходников (<see cref="ModuleJsonReader"/>), то есть ровно
    /// то, что делает <see cref="ModuleLoader"/>.
    ///
    /// Свой формат пака подставляется читателем, а не форком Dsl.Tooling: один и
    /// тот же читатель отдаётся <see cref="WorkspaceLoader.Load"/> в IDE, в LSP и
    /// в чекере — и все трое видят пак одинаково.
    ///
    /// Реализация обязана быть без состояния и потокобезопасной на чтение: её
    /// зовут и из фонового потока компиляции IDE.
    /// </summary>
    public interface IModuleReader
    {
        /// <summary>
        /// Папка — модуль? Обход воркспейса внутрь модуля не заходит, поэтому
        /// именно этот ответ решает, где кончается модуль и начинается его нутро.
        /// </summary>
        bool IsModuleDir(string dir);

        /// <summary>
        /// Этот файл — манифест модуля? Спрашивает вотчер: правка манифеста
        /// меняет состав модуля так же, как правка исходника. Только проверка
        /// имени, без обращения к диску: зовут на каждое событие файловой системы.
        /// </summary>
        bool IsManifestFile(string path);

        /// <summary>
        /// Папка модуля → набор исходников в порядке загрузки; null — не модуль или
        /// манифест не читается (причина уходит в onError(файл, сообщение)).
        /// <paramref name="logicalToAbsolute"/>, если задан, пополняется картой
        /// «логическое имя исходника → путь на диске»: по ней диагностики
        /// возвращаются в реальные файлы.
        /// </summary>
        ModuleSourceSet ReadModule(string dir, Action<string, string> onError,
                                   Dictionary<string, string> logicalToAbsolute);

        /// <summary>
        /// Исходники в папке модуля, которых нет в манифесте (IDE показывает их
        /// как неподключённые). Пусто, если манифест задан масками: там «вне
        /// списка» ничего не значит.
        /// </summary>
        IEnumerable<string> ListUnlisted(string dir);

        /// <summary>
        /// Дописать новый файл (путь относительно папки модуля) в манифест модуля.
        /// false — не вышло, <paramref name="warning"/> объясняет почему; сам файл
        /// к этому моменту уже создан, так что это предупреждение, а не отказ.
        /// </summary>
        bool AddFile(string dir, string relativePath, out string warning);
    }

    /// <summary>
    /// Читатель по умолчанию: module.json со списком исходников. Ключ списка —
    /// "scripts", если он в манифесте уже есть, иначе "sources" (что сегодня и
    /// читает <see cref="ModuleLoader"/>).
    /// </summary>
    public sealed class ModuleJsonReader : IModuleReader
    {
        public const string ManifestFileName = "module.json";

        /// <summary>Читатель без состояния — хватает одного на процесс.</summary>
        public static readonly ModuleJsonReader Instance = new ModuleJsonReader();

        /// <summary>Глубина поиска исходников в папке модуля.</summary>
        public int ScanDepth = 4;

        public bool IsModuleDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try { return File.Exists(Path.Combine(dir, ManifestFileName)); }
            catch { return false; }
        }

        public bool IsManifestFile(string path) =>
            !string.IsNullOrEmpty(path) &&
            string.Equals(Path.GetFileName(path), ManifestFileName, StringComparison.OrdinalIgnoreCase);

        public ModuleSourceSet ReadModule(string dir, Action<string, string> onError,
                                          Dictionary<string, string> logicalToAbsolute) =>
            ModuleLoader.LoadModuleDir(dir, onError, logicalToAbsolute);

        public IEnumerable<string> ListUnlisted(string dir)
        {
            var listed = ListedPaths(dir);
            if (listed == null) yield break; // манифест с масками или нечитаемый — сказать нечего

            List<string> files;
            try { files = ModuleLoader.EnumerateFiles(dir, "*.sal", ScanDepth); }
            catch { yield break; }
            foreach (var f in files)
            {
                string full;
                try { full = Path.GetFullPath(f); }
                catch { continue; }
                if (!listed.Contains(full)) yield return full;
            }
        }

        public bool AddFile(string dir, string relativePath, out string warning)
        {
            warning = null;
            string manifestPath = Path.Combine(dir ?? "", ManifestFileName);
            string rel = (relativePath ?? "").Replace('\\', '/').TrimStart('/');
            try
            {
                var jo = JObject.Parse(File.ReadAllText(manifestPath));
                string key = jo["scripts"] != null ? "scripts" : "sources";
                if (!(jo[key] is JArray arr)) { arr = new JArray(); jo[key] = arr; }
                foreach (var t in arr)
                {
                    string s = ((string)t ?? "").Replace('\\', '/');
                    // глобы ModuleLoader не раскрывает осознанно (порядок файлов =
                    // порядок обработчиков), так что маска файл не подхватит —
                    // дописываем путь явно
                    if (string.Equals(s, rel, StringComparison.OrdinalIgnoreCase)) return true;
                }
                arr.Add(rel);
                File.WriteAllText(manifestPath, jo.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));
                return true;
            }
            catch (Exception e)
            {
                warning = "файл создан, но " + ManifestFileName + " не обновлён: " + e.Message;
                return false;
            }
        }

        /// <summary>Абсолютные пути из списка манифеста; null — масок список или манифест не читается.</summary>
        private static HashSet<string> ListedPaths(string dir)
        {
            string manifestPath = Path.Combine(dir ?? "", ManifestFileName);
            JArray arr;
            try
            {
                var jo = JObject.Parse(File.ReadAllText(manifestPath));
                arr = (jo["scripts"] ?? jo["sources"]) as JArray;
            }
            catch { return null; }
            if (arr == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in arr)
            {
                string s = (string)t ?? "";
                if (s.IndexOf('*') >= 0 || s.IndexOf('?') >= 0) return null;
                try { listed.Add(Path.GetFullPath(Path.Combine(dir, s))); }
                catch { /* мусор в списке — просто не совпадёт ни с чем */ }
            }
            return listed;
        }
    }
}
