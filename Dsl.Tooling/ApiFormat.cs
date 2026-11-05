using System;
using System.Collections.Generic;
using System.Text;
using Dsl.Compilation;
using Dsl.Text;

namespace Dsl.Tooling
{
    /// <summary>
    /// Как элементы манифеста API выглядят в подсказках: сигнатуры, документация
    /// (markdown), сниппеты вызовов. Одно место — один вид в VS Code, Rider и
    /// встроенной IDE.
    /// </summary>
    public static class ApiFormat
    {
        public static string MethodSig(string owner, ApiManifest.MethodDef m)
        {
            var ps = new List<string>();
            foreach (var pd in m.Params) ps.Add($"{pd.Type} {pd.Name}");
            return $"{owner}.{m.Name}({string.Join(", ", ps)}) -> {m.Returns}";
        }

        public static string MethodDocMd(ApiManifest.MethodDef m, Language lang = Language.Ru)
        {
            var sb = new StringBuilder();
            string summary = m.Summary?.Get(lang);
            if (!string.IsNullOrEmpty(summary)) sb.AppendLine(summary);
            foreach (var pd in m.Params)
            {
                string doc = pd.Doc?.Get(lang);
                if (!string.IsNullOrEmpty(doc)) sb.AppendLine($"- `{pd.Name}` — {doc}");
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>Описание узла составного имени API, если игра его задала.</summary>
        public static string NamespaceSummary(ApiManifest api, string name, Language lang = Language.Ru)
        {
            if (api?.ApiNamespaces == null) return null;
            foreach (var ns in api.ApiNamespaces)
                if (ns.Name == name) return ns.Summary?.Get(lang);
            return null;
        }

        /// <summary>Пояснение к элементу енума: параллельный массив, может отсутствовать.</summary>
        public static string MemberDoc(ApiManifest.EnumDef en, int index, Language lang = Language.Ru)
            => en.MemberDocs != null && (uint)index < (uint)en.MemberDocs.Length
                ? en.MemberDocs[index]?.Get(lang)
                : null;

        public static string ConstSig(string owner, ApiManifest.ApiConstDef c)
            => $"{owner}.{c.Name}: {c.Type} = {Literal(c.Value)}";

        /// <summary>Значение из манифеста так, как его написали бы в скрипте.</summary>
        public static string Literal(object v)
        {
            if (v == null) return "null";
            if (v is string s) return "\"" + s + "\"";
            if (v is bool b) return b ? "true" : "false";
            return Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string EventSig(ApiManifest.EventDef ev)
        {
            var ps = new List<string>();
            foreach (var pd in ev.Params) ps.Add($"{pd.Type} {pd.Name}");
            return $"event {ev.Name}({string.Join(", ", ps)})";
        }

        public static string EventSnippet(ApiManifest.EventDef ev)
        {
            var ps = new List<string>();
            foreach (var pd in ev.Params) ps.Add($"{pd.Type} {pd.Name}");
            return $"{ev.Name}({string.Join(", ", ps)})\n{{\n\t$0\n}}";
        }

        /// <summary>Типизированные плейсхолдеры аргументов для табуляции по вызову.</summary>
        public static string CallSnippet(string name, List<string> paramLabels)
        {
            if (paramLabels.Count == 0) return name + "()$0";
            var sb = new StringBuilder(name).Append('(');
            for (int i = 0; i < paramLabels.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append("${").Append(i + 1).Append(':').Append(paramLabels[i].Replace("}", "\\}")).Append('}');
            }
            return sb.Append(")$0").ToString();
        }

        public static List<string> ParamLabels(EngineMethod em, Language lang = Language.Ru)
        {
            var r = new List<string>();
            foreach (var (name, type) in em.Params) r.Add($"{EngineDocs.TypeLabel(type, lang)} {name}");
            return r;
        }

        public static List<string> ParamLabels(ApiManifest.MethodDef m)
        {
            var r = new List<string>();
            foreach (var pd in m.Params) r.Add($"{pd.Type} {pd.Name}");
            return r;
        }
    }
}
