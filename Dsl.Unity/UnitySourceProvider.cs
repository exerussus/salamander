using System.Collections.Generic;
using System.IO;
using Dsl.Compilation;
using UnityEngine;

namespace Dsl.Unity
{
    /// <summary>
    /// Загрузка модулей для компиляции.
    /// Два пути: папка на диске (StreamingAssets/моды — годится для хот-релоада)
    /// и TextAsset-ы (контент, зашитый в билд/бандлы).
    /// </summary>
    public static class UnitySourceProvider
    {
        /// <summary>
        /// Сканирует папку: каждый подкаталог с module.json — модуль.
        /// Пути в manifest.sources — относительно папки модуля.
        /// </summary>
        public static List<ModuleSourceSet> LoadFromFolder(string rootPath)
        {
            return ModuleLoader.LoadFromFolder(rootPath,
                (file, message) => Debug.LogError($"[script] {file}: {message}"));
        }

        /// <summary>
        /// Модуль из TextAsset-ов (например, из Resources или Addressables).
        /// Порядок sources должен совпадать с порядком в манифесте.
        /// </summary>
        public static ModuleSourceSet FromTextAssets(TextAsset manifestJson, IReadOnlyList<TextAsset> sources)
        {
            var manifest = ModuleManifest.Parse(manifestJson.text);
            var set = new ModuleSourceSet { Manifest = manifest };
            for (int i = 0; i < sources.Count; i++)
            {
                string logical = i < manifest.Sources.Length ? manifest.Sources[i] : sources[i].name;
                set.Files.Add(($"{manifest.Name}/{logical}", sources[i].text));
            }
            return set;
        }
    }
}
