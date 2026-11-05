using Dsl.Text;

namespace Dsl.Ide
{
    /// <summary>
    /// Язык интерфейса встроенной IDE (кнопки, статусы, диалоги, сообщения) и
    /// подсказок в ней. Задаётся через <see cref="IdeOptions.Language"/> или
    /// <see cref="SalIdeView.Language"/>; компиляция в IDE идёт на том же языке.
    /// Общий на процесс: IDE — один UI, а у движка и компиляции язык свой.
    /// </summary>
    public static class IdeText
    {
        public static Language Language { get; set; } = Language.Ru;

        /// <summary>Текст на языке IDE.</summary>
        public static string T(string ru, string en) => Loc.T(Language, ru, en);
    }
}
