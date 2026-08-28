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
