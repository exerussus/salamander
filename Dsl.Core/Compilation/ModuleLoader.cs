using System;
using System.Collections.Generic;
using System.IO;

namespace Dsl.Compilation
{
    /// <summary>
    /// Загрузка модулей из папки (каждый подкаталог с module.json — модуль).
    /// Живёт в Core, потому что нужна не только Unity-адаптеру, но и внешним
    /// инструментам: CLI-чекеру и, через него, редактору.
    /// </summary>
    public static class ModuleLoader
    {
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
                string manifestPath = Path.Combine(dir, "module.json");
                if (!File.Exists(manifestPath)) continue;

                ModuleManifest manifest;
                try
                {
                    manifest = ModuleManifest.Parse(File.ReadAllText(manifestPath));
                }
                catch (Exception ex)
                {
                    onError?.Invoke(manifestPath, "некорректный JSON манифеста — " + ex.Message);
                    continue;
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
                result.Add(set);
            }
            return result;
        }
    }
}
