using System;
using System.Collections.Generic;
using Dsl.Runtime;
using Dsl.Semantics;

namespace Dsl.Hosting
{
    /// <summary>
    /// Проводка структуры через границу «скрипт ↔ C#»: фабрика значения и
    /// геттеры полей. Живёт отдельно от StructBuilder, потому что запись в
    /// TypeMap делается один раз в host.Struct&lt;T&gt;() — ДО того, как вызовут
    /// .Field(...)/.Build(...). Читатель и писатель в TypeMap смотрят сюда, так
    /// что порядок объявления остаётся свободным.
    /// </summary>
    public sealed class StructIO<T>
    {
        private static readonly bool IsRefType = !typeof(T).IsValueType;

        private readonly List<string> _names = new List<string>();
        private readonly List<Func<IHostContext, T, Variant>> _writers =
            new List<Func<IHostContext, T, Variant>>();

        internal readonly int Id;
        internal readonly HostStructInfo Info;
        internal Func<StructValue, T> Factory;

        internal StructIO(int id, HostStructInfo info)
        {
            Id = id;
            Info = info;
        }

        private string Name => Info?.Name ?? typeof(T).Name;

        /// <summary>Поле с геттером: структуру можно и принимать, и отдавать.</summary>
        internal void AddField(string name, Func<IHostContext, T, Variant> writer)
        {
            _names.Add(name);
            _writers.Add(writer);
        }

        /// <summary>Поле объявлено, но читать его из C#-значения нечем.</summary>
        internal void AddFieldWithoutGetter(string name)
        {
            _names.Add(name);
            _writers.Add(null);
        }

        // ===== скрипт → C# =================================================

        internal T Read(IHostContext h, Variant v)
        {
            int id = h.StructIdOf(v);
            if (id != Id)
                throw new ScriptError(id < 0
                    ? $"Ожидалась структура {Name}, а значение ею не является."
                    : $"Ожидалась структура {Name}, а пришла другая структура.");

            if (Factory == null)
                throw new InvalidOperationException(
                    $"Структуру '{Name}' нельзя принять параметром хостового метода: " +
                    $"не задана фабрика C#-значения. Добавьте " +
                    $"host.Struct<{typeof(T).Name}>(...).Build(v => new {typeof(T).Name}(...)).");

            return Factory(new StructValue(Info, v, h));
        }

        // ===== C# → скрипт =================================================

        internal Variant Write(IHostContext h, T value)
        {
            if (IsRefType && (object)value == null)
                throw new ScriptError(
                    $"Вместо структуры {Name} хост вернул null: у структур нет пустого значения.");

            for (int i = 0; i < _writers.Count; i++)
            {
                if (_writers[i] != null) continue;
                throw new InvalidOperationException(
                    $"Структуру '{Name}' нельзя отдать скрипту: поле '{_names[i]}' объявлено " +
                    $"без геттера. Объявите его как " +
                    $".Field(\"{_names[i]}\", x => x.{_names[i]}, ...) — тогда движок сможет " +
                    "собрать значение обратно.");
            }

            var v = h.StructNew(Id, _writers.Count);
            for (int i = 0; i < _writers.Count; i++)
                h.StructSet(v, i, _writers[i](h, value));
            return v;
        }
    }
}
