using System;

namespace Dsl.Text
{
    /// <summary>
    /// Language of compiler diagnostics, runtime messages and documentation.
    /// Only two are supported on purpose: English as the international base,
    /// Russian for convenience. Default is <see cref="Ru"/> — projects without
    /// the setting behave exactly as before.
    /// <para>Язык диагностик, сообщений рантайма и справки. По умолчанию — русский.</para>
    /// </summary>
    public enum Language : byte
    {
        Ru = 0,
        En = 1,
    }

    /// <summary>Выбор текста по языку и разбор значения настройки <c>"language"</c>.</summary>
    public static class Loc
    {
        /// <summary>Текст на нужном языке; если его нет — на другом.</summary>
        public static string T(Language lang, string ru, string en)
            => lang == Language.En ? (en ?? ru) : (ru ?? en);

        /// <summary>Код языка для конфигов: "ru" / "en".</summary>
        public static string Code(Language lang) => lang == Language.En ? "en" : "ru";

        /// <summary>
        /// Разбирает "ru" / "en" (регистр и пробелы по краям не важны).
        /// null и пустая строка — false, язык остаётся по умолчанию.
        /// </summary>
        public static bool TryParse(string value, out Language lang)
        {
            lang = Language.Ru;
            if (string.IsNullOrWhiteSpace(value)) return false;
            switch (value.Trim().ToLowerInvariant())
            {
                case "ru": lang = Language.Ru; return true;
                case "en": lang = Language.En; return true;
                default: return false;
            }
        }

        /// <summary>
        /// Как <see cref="TryParse"/>, но неизвестное значение — исключение с понятным текстом
        /// (на обоих языках: язык ещё не известен). null/пусто — <paramref name="fallback"/>.
        /// </summary>
        public static Language Parse(string value, Language fallback = Language.Ru)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            if (TryParse(value, out var lang)) return lang;
            throw new FormatException(
                $"Unknown language '{value}': expected \"en\" or \"ru\". / " +
                $"Неизвестный язык '{value}': допустимо \"en\" или \"ru\".");
        }
    }

    /// <summary>
    /// Текст справки на двух языках. Обычная строка неявно превращается в текст
    /// без языка — он показывается при любой настройке, поэтому существующий код
    /// регистрации компилируется без изменений:
    /// <code>
    /// Sig.Doc("Восстанавливает здоровье.")                         // один текст для обоих языков
    /// Sig.Doc(ru: "Восстанавливает здоровье.", en: "Restores health.")
    /// cls.Prop("Hp", u => u.Hp, DocText.Of(ru: "Здоровье", en: "Health"));
    /// </code>
    /// Если нужного языка нет, берётся другой: частично переведённый хост не теряет подсказок.
    /// null — справки нет (как раньше null-строка).
    /// </summary>
    public sealed class DocText : IEquatable<DocText>
    {
        public readonly string Ru;
        public readonly string En;

        public DocText(string ru, string en)
        {
            Ru = ru;
            En = en;
        }

        public static DocText Of(string ru, string en) => new DocText(ru, en);

        /// <summary>
        /// Справка из основного текста и необязательного английского (атрибуты):
        /// без английского — текст без языка; без обоих — null.
        /// </summary>
        public static DocText Maybe(string main, string en)
        {
            if (string.IsNullOrEmpty(en)) return main;          // null → null, иначе нейтральный
            return new DocText(string.IsNullOrEmpty(main) ? null : main, en);
        }

        /// <summary>Массив строк (без языка) → массив справок; null → null.</summary>
        public static DocText[] FromArray(string[] texts)
        {
            if (texts == null) return null;
            var r = new DocText[texts.Length];
            for (int i = 0; i < texts.Length; i++) r[i] = texts[i];
            return r;
        }

        /// <summary>Текст без языка: одинаков для ru и en.</summary>
        public static DocText Neutral(string text) => new DocText(text, text);

        public static implicit operator DocText(string text) => text == null ? null : new DocText(text, text);

        /// <summary>Текст на языке <paramref name="lang"/> или null — в том числе для null-справки.</summary>
        public static string Get(DocText doc, Language lang) => doc?.Get(lang);

        public bool IsEmpty => string.IsNullOrEmpty(Ru) && string.IsNullOrEmpty(En);

        /// <summary>Тексты для языков различаются (заданы раздельно).</summary>
        public bool IsBilingual => !string.Equals(Ru, En, StringComparison.Ordinal);

        /// <summary>Текст на языке <paramref name="lang"/>; если его нет — на другом; если нет никакого — null.</summary>
        public string Get(Language lang)
        {
            string a = lang == Language.En ? En : Ru;
            string b = lang == Language.En ? Ru : En;
            return !string.IsNullOrEmpty(a) ? a : (!string.IsNullOrEmpty(b) ? b : null);
        }

        /// <summary>Склеить с суффиксом (на каждом языке свой).</summary>
        public static DocText Append(DocText doc, string ruSuffix, string enSuffix)
        {
            string Join(string a, string s) => string.IsNullOrEmpty(a) ? s : a + " " + s;
            return new DocText(Join(doc?.Get(Language.Ru), ruSuffix), Join(doc?.Get(Language.En), enSuffix));
        }

        public bool Equals(DocText other) =>
            !ReferenceEquals(other, null) &&
            string.Equals(Ru, other.Ru, StringComparison.Ordinal) &&
            string.Equals(En, other.En, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is DocText d && Equals(d);

        public override int GetHashCode() =>
            ((Ru?.GetHashCode() ?? 0) * 397) ^ (En?.GetHashCode() ?? 0);

        public static bool operator ==(DocText a, DocText b) => ReferenceEquals(a, b) || (a is object && a.Equals(b));
        public static bool operator !=(DocText a, DocText b) => !(a == b);

        /// <summary>Для отладки: русский (или единственный) текст.</summary>
        public override string ToString() => Get(Language.Ru) ?? "";
    }
}
