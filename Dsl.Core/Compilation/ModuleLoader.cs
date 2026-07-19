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

            var set = new ModuleSourceSet { Manifest = manifest };
            string moduleName = manifest?.Name ?? Path.GetFileName(dir);

            foreach (var rel in manifest.Sources ?? Array.Empty<string>())
            {
                string full = Path.Combine(dir, rel);
                if (!File.Exists(full))
                {
                    onError?.Invoke(manifestPath, $"модуль '{moduleName}': файл из манифеста не найден: {rel}");
                    continue;
                }
                string logical = $"{moduleName}/{rel.Replace('\\', '/')}";
                set.Files.Add((logical, File.ReadAllText(full)));
                if (logicalToAbsolute != null)
                    logicalToAbsolute[logical] = Path.GetFullPath(full);
            }
            return set;
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
