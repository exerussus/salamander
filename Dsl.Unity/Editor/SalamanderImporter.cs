using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Dsl.Unity.Editor
{
    /// <summary>
    /// Импортирует .sal как <see cref="TextAsset"/>. Без этого Unity считает
    /// незнакомое расширение обычным ассетом (DefaultAsset без .text), и .sal
    /// нельзя ни перетащить в _embeddedModules сцены (вшитые в карту скрипты),
    /// ни загрузить через Resources/Addressables.
    ///
    /// Живёт в Editor-only подсборке (Dsl.Unity.Editor): ScriptedImporter —
    /// редакторный тип, в плеер-билд не входит. Версия в атрибуте — при её
    /// повышении Unity переимпортирует все .sal.
    /// </summary>
    [ScriptedImporter(version: 1, ext: "sal")]
    public sealed class SalamanderImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            string text = File.ReadAllText(ctx.assetPath);
            var asset = new TextAsset(text) { name = Path.GetFileNameWithoutExtension(ctx.assetPath) };
            ctx.AddObjectToAsset("main", asset);
            ctx.SetMainObject(asset);
        }
    }
}
