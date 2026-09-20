using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace Dsl.Ide
{
    /// <summary>
    /// Сессия IDE, переживающая перезагрузку домена, перезапуск редактора и игры:
    /// открытые вкладки, каретки, раскладка панелей и — главное — бэкапы
    /// несохранённых буферов (hot-exit). Хранится в <see cref="IIdeStorage"/>.
    /// </summary>
    public sealed class IdeSessionState
    {
        public int SchemaVersion = 1;
        public List<IdeSessionDoc> Docs = new List<IdeSessionDoc>();
        public string Active;

        public float SidebarWidth = 220f;
        public float BottomHeight = 170f;
        public bool SidebarVisible = true;
        public bool BottomVisible = true;
        public string SidebarTab = "files";
        public string BottomTab = "problems";

        public bool ProblemsCurrentOnly;
        public bool ShowErrors = true;
        public bool ShowWarnings = true;
        public bool ShowInfos = true;

        public string ApiManifestPath;
        public string WorkspaceRoot;

        public IdeSessionState() { }
    }

    public sealed class IdeSessionDoc
    {
        public string Key;
        public int Caret;
        public int Select;
        public float ScrollX;
        public float ScrollY;

        /// <summary>Ключ бэкапа несохранённого буфера в хранилище (null — буфер чистый).</summary>
        public string Buffer;

        /// <summary>Хэш сохранённой версии, от которой сделан бэкап: не совпал с диском — конфликт.</summary>
        public string BaseHash;

        public IdeSessionDoc() { }
    }

    public static class IdeSession
    {
        public const string StateKey = "session.json";

        public static IdeSessionState Load(IIdeStorage storage)
        {
            try
            {
                string json = storage?.Read(StateKey);
                if (!string.IsNullOrEmpty(json))
                    return JsonConvert.DeserializeObject<IdeSessionState>(json) ?? new IdeSessionState();
            }
            catch { /* битая сессия — начинаем с чистой */ }
            return new IdeSessionState();
        }

        public static void Save(IIdeStorage storage, IdeSessionState state)
        {
            if (storage == null || state == null) return;
            storage.Write(StateKey, JsonConvert.SerializeObject(state, Formatting.Indented));
        }

        /// <summary>Ключ бэкапа буфера по ключу документа (стабильный хэш — путь может быть длинным).</summary>
        public static string BufferKey(string docKey) => "buffers/" + Hash(docKey) + ".sal";

        /// <summary>FNV-1a 64 — стабилен между запусками (string.GetHashCode — нет).</summary>
        public static string Hash(string text)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                foreach (char c in text ?? "")
                {
                    h ^= c;
                    h *= 1099511628211UL;
                }
                return h.ToString("x16");
            }
        }
    }
}
