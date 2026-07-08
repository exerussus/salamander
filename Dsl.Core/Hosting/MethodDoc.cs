using System.Collections.Generic;

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
    /// </code>
    /// </summary>
    public sealed class MethodDoc
    {
        public string Summary { get; }
        internal readonly List<string> Names = new List<string>();
        internal readonly List<string> Docs = new List<string>();

        internal MethodDoc(string summary) { Summary = summary; }

        /// <summary>Добавить параметр: имя и (необязательно) пояснение. Порядок = порядок параметров.</summary>
        public MethodDoc P(string name, string doc = null)
        {
            Names.Add(name);
            Docs.Add(doc);
            return this;
        }

        public string[] NameArray() => Names.ToArray();
        public string[] DocArray() => Docs.ToArray();
    }

    /// <summary>Точка входа для описаний: Sig.Doc("...").P("a").P("b", "...").</summary>
    public static class Sig
    {
        public static MethodDoc Doc(string summary) => new MethodDoc(summary);
    }
}
