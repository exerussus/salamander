using System;
using System.Collections.Generic;
using System.IO;

namespace Dsl.Compilation
{
    /// <summary>
    /// Загрузка исходников модулей для ИНСТРУМЕНТОВ и для сборщика игры.
    /// Важно про границы: РАНТАЙМ движка сам ничего не ищет и не собирает —
    /// наборы модулей ему отдаёт сборщик игры (или инструмент). Эти методы —
    /// утилиты, которые сборщик/чекер/LSP зовут ЯВНО:
    ///  - LoadFromList — «ешь ровно это»: упорядоченный список папок модулей
    ///    (обычно из salamander-build.json, который экспортирует сборщик);
    ///  - LoadFromFolder — обход папки, дев-режим инструментов без сборщика.
    /// </summary>
    public static class ModuleLoader
    {
        /// <summary>Модуль из конкретной папки (папка с module.json). null — не модуль/ошибка.</summary>
        public static ModuleSourceSet LoadModuleDir(
            string dir,
            Action<string, string> onError,
            Dictionary<string, string> logicalToAbsolute = null)
        {
            string manifestPath = Path.Combine(dir, "module.json");
            if (!File.Exists(manifestPath))
            {
                onError?.Invoke(dir, "в папке нет module.json — это не модуль.");
                return null;
            }

            ModuleManifest manifest;
            try
            {
                manifest = ModuleManifest.Parse(File.ReadAllText(manifestPath));
            }
            catch (Exception ex)
            {
                onError?.Invoke(manifestPath, "некорректный JSON манифеста — " + ex.Message);
                return null;
            }

            if (manifest == null)
            {
                // JsonConvert возвращает null на содержимом "null" — без этой
                // проверки следующий же manifest.Sources даёт NullReferenceException
                onError?.Invoke(manifestPath, "манифест пуст или содержит null.");
                return null;
            }

            var set = new ModuleSourceSet { Manifest = manifest };
            string moduleName = manifest.Name ?? Path.GetFileName(dir);
            string moduleRoot = Path.GetFullPath(dir);

            foreach (var rel in manifest.Sources ?? Array.Empty<string>())
            {
                // sources приходит из module.json, то есть от автора мода. Без
                // проверки Path.Combine принимает и '..', и АБСОЛЮТНЫЙ путь
                // (последний просто отбрасывает dir) — модуль читает любой файл
                // на диске, а его содержимое утекает наружу через текст диагностик
                // (лексер и парсер цитируют исходник дословно).
                if (!TryResolveSource(moduleRoot, rel, out string full))
                {
                    onError?.Invoke(manifestPath,
                        $"модуль '{moduleName}': путь '{rel}' ведёт за пределы папки модуля — отклонён.");
                    continue;
                }
                if (!File.Exists(full))
                {
                    onError?.Invoke(manifestPath, $"модуль '{moduleName}': файл из манифеста не найден: {rel}");
                    continue;
                }
                string logical = $"{moduleName}/{rel.Replace('\\', '/')}";
                set.Files.Add((logical, File.ReadAllText(full)));
                if (logicalToAbsolute != null)
                    logicalToAbsolute[logical] = full;
            }
            return set;
        }

        /// <summary>
        /// Путь исходника из манифеста → абсолютный путь ВНУТРИ папки модуля.
        /// false, если путь пустой, абсолютный, или после нормализации уходит наружу.
        /// </summary>
        private static bool TryResolveSource(string moduleRoot, string rel, out string full)
        {
            full = null;
            if (string.IsNullOrWhiteSpace(rel)) return false;
            if (Path.IsPathRooted(rel)) return false;

            string combined;
            try { combined = Path.GetFullPath(Path.Combine(moduleRoot, rel)); }
            catch { return false; } // недопустимые символы, слишком длинный путь и т.п.

            string prefix = moduleRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? moduleRoot
                : moduleRoot + Path.DirectorySeparatorChar;
            if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

            full = combined;
            return true;
        }

        /// <summary>
        /// «Ешь ровно это»: упорядоченный список папок модулей от сборщика.
        /// Порядок списка = порядок загрузки (важен для мержа-переопределений).
        /// </summary>
        public static List<ModuleSourceSet> LoadFromList(
            IEnumerable<string> moduleDirs,
            Action<string, string> onError,
            Dictionary<string, string> logicalToAbsolute = null)
        {
            var result = new List<ModuleSourceSet>();
            foreach (var dir in moduleDirs ?? Array.Empty<string>())
            {
                var set = LoadModuleDir(dir, onError, logicalToAbsolute);
                if (set != null) result.Add(set);
            }
            return result;
        }

        /// <summary>
        /// Приводит путь, введённый человеком (настройка IDE, аргумент командной
        /// строки), к виду, который понимает файловая система: снимает кавычки и
        /// пробелы, разворачивает file:// и срезает ведущий слэш перед буквой
        /// диска — "\C:\mods" превращается в "C:\mods".
        ///
        /// Последнее — не педантизм. Проводник Windows такую запись прощает, а
        /// Path/File — нет: ведущий слэш означает «корень текущего диска», после
        /// чего "C:" становится именем папки с двоеточием, которого на диске быть
        /// не может. File.Exists молча отвечает false, и инструмент сообщает «не
        /// найдено» про путь, который человек только что открыл в Проводнике —
        /// со стороны пользователя это неотлаживаемо. Поэтому чиним на входе.
        ///
        /// UNC ("\\server\share") не трогаем: там двоеточия нет.
        /// </summary>
        public static string NormalizeUserPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;

            string v = value.Trim().Trim('"');

            if (v.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                try { v = new Uri(v).LocalPath; }
                catch { /* не разобралось как URI — оставляем текст как есть */ }
            }

            if (v.Length >= 3 && (v[0] == '/' || v[0] == '\\')
                && char.IsLetter(v[1]) && v[2] == ':')
                v = v.Substring(1);

            return v;
        }

        /// <summary>Глубина обхода по умолчанию для LoadFromTree/EnumerateFiles/FindNearestFile.</summary>
        public const int DefaultScanDepth = 8;

        // Папки, в которые обход не заходит НИКОГДА. Инструменту их содержимое
        // бесполезно, а в Unity-проекте Library/ и Temp/ — это десятки тысяч
        // файлов, из-за которых обход всего проекта переставал быть дешёвым.
        private static readonly HashSet<string> IgnoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Library", "Temp", "Logs", "UserSettings", "obj", "bin",
            "Build", "Builds", "node_modules", "publish",
        };

        /// <summary>Служебная папка, в которую инструменты не заходят (кэши IDE, сборки, VCS).</summary>
        public static bool IsIgnoredDirectory(string name)
            => string.IsNullOrEmpty(name)
               || name[0] == '.'          // .git, .vs, .idea, .vscode
               || IgnoredDirs.Contains(name);

        /// <summary>
        /// Обход с ограничением глубины, пропуском служебных папок и устойчивостью
        /// к недоступным каталогам. Порядок детерминированный (по пути).
        /// Замена Directory.EnumerateFiles(..., AllDirectories) там, где корнем
        /// может оказаться целый проект игры.
        /// </summary>
        public static List<string> EnumerateFiles(string rootPath, string searchPattern,
                                                  int maxDepth = DefaultScanDepth)
        {
            var result = new List<string>();
            if (!Directory.Exists(rootPath)) return result;

            var level = new List<string> { Path.GetFullPath(rootPath) };
            for (int depth = 0; depth <= maxDepth && level.Count > 0; depth++)
            {
                var next = new List<string>();
                level.Sort(StringComparer.Ordinal);
                foreach (var dir in level)
                {
                    try { result.AddRange(Directory.GetFiles(dir, searchPattern)); }
                    catch { /* каталог исчез или недоступен — не роняем обход */ }

                    if (depth == maxDepth) continue;
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(dir))
                            if (!IsIgnoredDirectory(Path.GetFileName(sub))) next.Add(sub);
                    }
                    catch { /* то же самое */ }
                }
                level = next;
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// Ближайший к корню файл с таким именем (сначала сам корень, потом вглубь).
        /// null — не найден. Используется для поиска salamander-api.json, когда
        /// корень воркспейса задан IDE и манифест лежит где-то в подпапках.
        /// </summary>
        public static string FindNearestFile(string rootPath, string fileName,
                                             int maxDepth = DefaultScanDepth)
        {
            if (!Directory.Exists(rootPath)) return null;

            var level = new List<string> { Path.GetFullPath(rootPath) };
            for (int depth = 0; depth <= maxDepth && level.Count > 0; depth++)
            {
                var next = new List<string>();
                level.Sort(StringComparer.Ordinal);
                foreach (var dir in level)
                {
                    string candidate = Path.Combine(dir, fileName);
                    try { if (File.Exists(candidate)) return candidate; }
                    catch { /* недоступен — пробуем дальше */ }

                    if (depth == maxDepth) continue;
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(dir))
                            if (!IsIgnoredDirectory(Path.GetFileName(sub))) next.Add(sub);
                    }
                    catch { }
                }
                level = next;
            }
            return null;
        }

        /// <summary>
        /// Модули ГДЕ-ТО под корнем: обход вглубь с пропуском служебных папок.
        /// Нужен инструментам, которым корень задаёт IDE: в Unity-проекте модули
        /// лежат в StreamingAssets/..., а не прямыми детьми корня, и одноуровневый
        /// LoadFromFolder их просто не видит.
        ///
        /// Внутрь найденного модуля обход НЕ спускается: подпапки модуля — его
        /// исходники, а не вложенные модули. Порядок — по пути (детерминирован).
        /// </summary>
        public static List<ModuleSourceSet> LoadFromTree(
            string rootPath,
            Action<string, string> onError,
            Dictionary<string, string> logicalToAbsolute = null,
            int maxDepth = DefaultScanDepth)
        {
            var result = new List<ModuleSourceSet>();
            if (!Directory.Exists(rootPath)) return result;

            var moduleDirs = new List<string>();
            var level = new List<string> { Path.GetFullPath(rootPath) };
            for (int depth = 0; depth <= maxDepth && level.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var dir in level)
                {
                    bool isModule;
                    try { isModule = File.Exists(Path.Combine(dir, "module.json")); }
                    catch { continue; }

                    if (isModule) { moduleDirs.Add(dir); continue; } // внутрь модуля не идём

                    if (depth == maxDepth) continue;
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(dir))
                            if (!IsIgnoredDirectory(Path.GetFileName(sub))) next.Add(sub);
                    }
                    catch { }
                }
                level = next;
            }

            moduleDirs.Sort(StringComparer.Ordinal);
            foreach (var dir in moduleDirs)
            {
                var set = LoadModuleDir(dir, onError, logicalToAbsolute);
                if (set != null) result.Add(set);
            }
            return result;
        }

        /// <param name="rootPath">Корень с модулями.</param>
        /// <param name="onError">Колбэк ошибок загрузки: (файл, сообщение).</param>
        /// <param name="logicalToAbsolute">
        /// Необязательная карта «логическое имя исходника → абсолютный путь» —
        /// инструментам нужно переводить диагностики обратно в реальные файлы.
        /// </param>
        public static List<ModuleSourceSet> LoadFromFolder(
            string rootPath,
            Action<string, string> onError,
            Dictionary<string, string> logicalToAbsolute = null)
        {
            var result = new List<ModuleSourceSet>();
            if (!Directory.Exists(rootPath)) return result;

            var dirs = Directory.GetDirectories(rootPath);
            Array.Sort(dirs, StringComparer.Ordinal); // детерминированный порядок обхода

            foreach (var dir in dirs)
            {
                if (!File.Exists(Path.Combine(dir, "module.json"))) continue; // не модуль — молча пропускаем
                var set = LoadModuleDir(dir, onError, logicalToAbsolute);
                if (set != null) result.Add(set);
            }
            return result;
        }
    }
}
