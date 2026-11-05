using System.Collections.Generic;
using Dsl.Text;

namespace Dsl.Hosting
{
    /// <summary>
    /// Человекочитаемое описание метода/события: краткое summary и параметры
    /// с именами и пояснениями. Типы параметров берутся из лямбды/сигнатуры —
    /// здесь только имена и doc. Число .P(...) должно совпасть с числом
    /// параметров, иначе регистрация упадёт с понятной ошибкой.
    ///
    /// <code>
    /// Sig.Doc("Восстанавливает здоровье юниту.")
    ///    .P("target")
    ///    .P("amount", "сколько HP вернуть")
    ///
    /// // двуязычно: подсказки покажутся на языке из настройки "language"
    /// Sig.Doc(ru: "Восстанавливает здоровье юниту.", en: "Restores unit health.")
    ///    .P("target")
    ///    .P("amount", ru: "сколько HP вернуть", en: "HP to restore")
    /// </code>
    /// </summary>
    public sealed class MethodDoc
    {
        public DocText Summary { get; }
        internal readonly List<string> Names = new List<string>();
        internal readonly List<DocText> Docs = new List<DocText>();

        internal MethodDoc(DocText summary) { Summary = summary; }

        /// <summary>Добавить параметр: имя и (необязательно) пояснение. Порядок = порядок параметров.</summary>
        public MethodDoc P(string name, DocText doc = null)
        {
            Names.Add(name);
            Docs.Add(doc);
            return this;
        }

        /// <summary>Добавить параметр с пояснением на двух языках.</summary>
        public MethodDoc P(string name, string ru, string en) => P(name, new DocText(ru, en));

        public string[] NameArray() => Names.ToArray();
        public DocText[] DocArray() => Docs.ToArray();
    }

    /// <summary>Точка входа для описаний: Sig.Doc("...").P("a").P("b", "...").</summary>
    public static class Sig
    {
        public static MethodDoc Doc(DocText summary) => new MethodDoc(summary);

        /// <summary>Описание на двух языках: <c>Sig.Doc(ru: "…", en: "…")</c>.</summary>
        public static MethodDoc Doc(string ru, string en) => new MethodDoc(new DocText(ru, en));
    }
}
