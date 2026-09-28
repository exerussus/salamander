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
    /// повышении Unity переимпортирует все .sal (2: значок саламандры).
    /// </summary>
    [ScriptedImporter(version: 2, ext: "sal")]
    public sealed class SalamanderImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            string text = File.ReadAllText(ctx.assetPath);
            var asset = new TextAsset(text) { name = Path.GetFileNameWithoutExtension(ctx.assetPath) };
            // Значок в окне Project. Текстура — скрытый подобъект ассета: иначе
            // ссылка на неё не переживёт перезапуск редактора.
            var icon = SalamanderIcon.Create();
            ctx.AddObjectToAsset("icon", icon);
            ctx.AddObjectToAsset("main", asset, icon);
            ctx.SetMainObject(asset);
        }
    }
}
