using System;

namespace Dsl.Hosting
{
    /// <summary>
    /// Помечает C#-тип как ДАННЫЕ для Salamander:
    /// - класс с обычными (instance) [SalamanderProperty] → класс-сущность
    ///   (хэндлы, доступ вида unit.health). Может иметь и ноль свойств
    ///   (непрозрачный хэндл-тип);
    /// - енум → скриптовый енум (значения обязаны идти 0..N-1).
    ///
    /// Тип НЕ регистрируется сам — укажите его в host.Register(...); выигрыш в
    /// том, что описания живут на самом типе. Для API (вызовы Класс.Метод(...))
    /// используйте [SalamanderApi] на НЕстатическом классе — так у каждой
    /// комнаты/сессии будет свой экземпляр состояния, без статики в коде.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, Inherited = false)]
    public sealed class SalamanderClassAttribute : Attribute
    {
        public string Summary { get; }
        public SalamanderClassAttribute(string summary = null) => Summary = summary;
    }

    /// <summary>
    /// Помечает НЕстатический класс как API-класс: его [SalamanderMethod]-методы
    /// становятся вызовами Класс.Метод(...) в скриптах. Регистрируется от
    /// ЭКЗЕМПЛЯРА через host.RegisterApi(new MyApi(room)) — методы биндятся к
    /// этому экземпляру, поэтому состояние (ссылки на мир, комнату, сервисы)
    /// живёт в нём, а не в статике. Несколько комнат = несколько движков с
    /// собственными экземплярами API, ничего общего.
    ///
    /// «Статичность» вызова в DSL — это лишь синтаксис (нет объекта-приёмника);
    /// в C# за ним стоит обычный объект.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class SalamanderApiAttribute : Attribute
    {
        public string Summary { get; }
        public SalamanderApiAttribute(string summary = null) => Summary = summary;
    }

    /// <summary>
    /// Обычное (instance) свойство класса-сущности, видимое скриптам как
    /// unit.имя. Тип берётся из свойства; если есть set — свойство доступно и на
    /// запись. summary уходит в манифест как пояснение (doc).
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public sealed class SalamanderPropertyAttribute : Attribute
    {
        public string Summary { get; }
        public SalamanderPropertyAttribute(string summary = null) => Summary = summary;
    }

    /// <summary>
    /// Метод API-класса (помеченного [SalamanderApi]), видимый скриптам как
    /// Класс.Метод(...). Обычно это instance-метод — он биндится к экземпляру,
    /// переданному в host.RegisterApi(instance). Имена и типы параметров берутся
    /// из сигнатуры; пояснения к параметрам — через [SalamanderParam].
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class SalamanderMethodAttribute : Attribute
    {
        public string Summary { get; }
        public SalamanderMethodAttribute(string summary = null) => Summary = summary;
    }

    /// <summary>
    /// Необязательное пояснение к параметру метода. Имя параметра берётся из
    /// сигнатуры — здесь только текст doc.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    public sealed class SalamanderParamAttribute : Attribute
    {
        public string Doc { get; }
        public SalamanderParamAttribute(string doc) => Doc = doc;
    }
}
