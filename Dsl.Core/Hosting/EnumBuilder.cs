using System;

namespace Dsl.Hosting
{
    /// <summary>
    /// Описания элементов енума. Тип уже зарегистрирован — здесь только текст,
    /// кодировать нечего:
    /// <code>
    /// host.Enum&lt;Slot&gt;(summary: "Слоты характеристик.")
    ///     .Member(Slot.MoveSpeed,   "Скорость передвижения, м/с.")
    ///     .Member(Slot.AttackSpeed, "Множитель времён оружия. Меньше — быстрее.");
    /// </code>
    ///
    /// Элемент енума — то место, где автор рецепта спрашивает про ЕДИНИЦЫ
    /// («Slot.MoveSpeed — это м/с или клетки за тик?»), а summary всего енума
    /// на такой вопрос не отвечает: это одна строка на три десятка элементов,
    /// и при наведении на элемент её никто не видит.
    /// </summary>
    public sealed class EnumBuilder<TEnum> where TEnum : struct, Enum
    {
        private readonly HostBuilder _host;
        private readonly int _id;

        internal EnumBuilder(HostBuilder host, int id)
        {
            _host = host;
            _id = id;
        }

        /// <summary>Пояснение к элементу: уходит в манифест и в подсказку редактора.</summary>
        public EnumBuilder<TEnum> Member(TEnum value, string doc)
        {
            if (string.IsNullOrWhiteSpace(doc))
                throw new ArgumentException(
                    $"Пустое пояснение к элементу '{typeof(TEnum).Name}.{value}': " +
                    "либо напишите текст, либо не вызывайте Member.", nameof(doc));
            _host.Registry.SetEnumMemberDoc(_id, Convert.ToInt32(value), doc);
            return this;
        }

        /// <summary>Назад к общему построителю: host.Enum&lt;A&gt;().Member(...).Host.Class&lt;Unit&gt;()</summary>
        public HostBuilder Host => _host;

        /// <summary>
        /// Следующий енум — чтобы привычная цепочка Enum&lt;A&gt;().Enum&lt;B&gt;()
        /// продолжала работать после того, как Enum стал возвращать построитель.
        /// </summary>
        public EnumBuilder<TNext> Enum<TNext>(string name = null, string summary = null)
            where TNext : struct, Enum
            => _host.Enum<TNext>(name, summary);
    }
}
