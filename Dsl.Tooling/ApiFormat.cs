using System;
using System.Collections.Generic;
using System.Text;
using Dsl.Compilation;

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

        public static string MethodDocMd(ApiManifest.MethodDef m)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(m.Summary)) sb.AppendLine(m.Summary);
            foreach (var pd in m.Params)
                if (!string.IsNullOrEmpty(pd.Doc)) sb.AppendLine($"- `{pd.Name}` — {pd.Doc}");
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>Описание узла составного имени API, если игра его задала.</summary>
        public static string NamespaceSummary(ApiManifest api, string name)
        {
            if (api?.ApiNamespaces == null) return null;
            foreach (var ns in api.ApiNamespaces)
                if (ns.Name == name) return string.IsNullOrEmpty(ns.Summary) ? null : ns.Summary;
            return null;
        }

        /// <summary>Пояснение к элементу енума: параллельный массив, может отсутствовать.</summary>
        public static string MemberDoc(ApiManifest.EnumDef en, int index)
            => en.MemberDocs != null && (uint)index < (uint)en.MemberDocs.Length
                ? en.MemberDocs[index]
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

        public static List<string> ParamLabels(EngineMethod em)
        {
            var r = new List<string>();
            foreach (var (name, type) in em.Params) r.Add($"{type} {name}");
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
