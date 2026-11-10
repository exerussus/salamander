using System;
using System.Collections.Generic;
using System.Text;
using Dsl.Json;

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
                    return FromJson(JsonNode.Parse(json)) ?? new IdeSessionState();
            }
            catch { /* битая сессия — начинаем с чистой */ }
            return new IdeSessionState();
        }

        public static void Save(IIdeStorage storage, IdeSessionState state)
        {
            if (storage == null || state == null) return;
            storage.Write(StateKey, ToJson(state).ToJson(indented: true));
        }

        // Ключи — имена полей (так сессию писал Newtonsoft: старые session.json
        // читаются). Чтение мягкое: поле не того типа пропускается, а не роняет
        // всю сессию — потерять вкладки из-за одного поля обиднее, чем это поле.

        public static JsonObject ToJson(IdeSessionState st)
        {
            var docs = new JsonArray();
            foreach (var d in st.Docs ?? new List<IdeSessionDoc>())
            {
                if (d == null) continue;
                docs.Add(new JsonObject
                {
                    ["Key"] = d.Key, ["Caret"] = d.Caret, ["Select"] = d.Select,
                    ["ScrollX"] = d.ScrollX, ["ScrollY"] = d.ScrollY,
                    ["Buffer"] = d.Buffer, ["BaseHash"] = d.BaseHash,
                });
            }
            return new JsonObject
            {
                ["SchemaVersion"] = st.SchemaVersion,
                ["Docs"] = docs,
                ["Active"] = st.Active,
                ["SidebarWidth"] = st.SidebarWidth,
                ["BottomHeight"] = st.BottomHeight,
                ["SidebarVisible"] = st.SidebarVisible,
                ["BottomVisible"] = st.BottomVisible,
                ["SidebarTab"] = st.SidebarTab,
                ["BottomTab"] = st.BottomTab,
                ["ProblemsCurrentOnly"] = st.ProblemsCurrentOnly,
                ["ShowErrors"] = st.ShowErrors,
                ["ShowWarnings"] = st.ShowWarnings,
                ["ShowInfos"] = st.ShowInfos,
                ["ApiManifestPath"] = st.ApiManifestPath,
                ["WorkspaceRoot"] = st.WorkspaceRoot,
            };
        }

        public static IdeSessionState FromJson(JsonNode node)
        {
            if (!(node is JsonObject o)) return null;
            var st = new IdeSessionState();
            foreach (var kv in o)
            {
                var v = kv.Value as JsonValue;
                switch (JsonRead.Key(kv.Key))
                {
                    case "schemaversion": Int(v, ref st.SchemaVersion); break;
                    case "active": Str(v, ref st.Active); break;
                    case "sidebarwidth": Float(v, ref st.SidebarWidth); break;
                    case "bottomheight": Float(v, ref st.BottomHeight); break;
                    case "sidebarvisible": Bool(v, ref st.SidebarVisible); break;
                    case "bottomvisible": Bool(v, ref st.BottomVisible); break;
                    case "sidebartab": Str(v, ref st.SidebarTab); break;
                    case "bottomtab": Str(v, ref st.BottomTab); break;
                    case "problemscurrentonly": Bool(v, ref st.ProblemsCurrentOnly); break;
                    case "showerrors": Bool(v, ref st.ShowErrors); break;
                    case "showwarnings": Bool(v, ref st.ShowWarnings); break;
                    case "showinfos": Bool(v, ref st.ShowInfos); break;
                    case "apimanifestpath": Str(v, ref st.ApiManifestPath); break;
                    case "workspaceroot": Str(v, ref st.WorkspaceRoot); break;
                    case "docs":
                        if (!(kv.Value is JsonArray arr)) break;
                        foreach (var item in arr)
                        {
                            if (!(item is JsonObject dobj)) continue;
                            var d = new IdeSessionDoc();
                            foreach (var dk in dobj)
                            {
                                var dv = dk.Value as JsonValue;
                                switch (JsonRead.Key(dk.Key))
                                {
                                    case "key": Str(dv, ref d.Key); break;
                                    case "caret": Int(dv, ref d.Caret); break;
                                    case "select": Int(dv, ref d.Select); break;
                                    case "scrollx": Float(dv, ref d.ScrollX); break;
                                    case "scrolly": Float(dv, ref d.ScrollY); break;
                                    case "buffer": Str(dv, ref d.Buffer); break;
                                    case "basehash": Str(dv, ref d.BaseHash); break;
                                }
                            }
                            st.Docs.Add(d);
                        }
                        break;
                }
            }
            return st;
        }

        private static void Str(JsonValue v, ref string field)
        {
            if (v == null) return;
            if (v.IsNull) field = null;
            else if (v.Kind == JsonKind.String) field = v.AsString;
        }

        private static void Int(JsonValue v, ref int field)
        {
            if (v != null && v.TryGetInt32(out int i)) field = i;
        }

        private static void Float(JsonValue v, ref float field)
        {
            if (v != null && v.Kind == JsonKind.Number) field = (float)v.DoubleValue;
        }

        private static void Bool(JsonValue v, ref bool field)
        {
            if (v != null && v.Kind == JsonKind.Bool) field = (bool)v;
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
