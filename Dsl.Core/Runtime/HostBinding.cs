using Dsl.Text;
namespace Dsl.Runtime
{
    /// <summary>
    /// Мостик рантайма для хостового кода: перевод строк и сущностей между
    /// managed-миром хоста и упакованными Variant. Реализуется движком.
    /// </summary>
    public interface IHostContext
    {
        /// <summary>Язык сообщений движка (для текстов ошибок хоста).</summary>
        Language Language { get; }

        int InternString(string s);
        string ResolveString(Variant v);        // Nil -> null
        Variant WrapObject(object o);            // зарегистрировать/найти хэндл сущности
        object ResolveObject(Variant v);         // бросает ScriptError, если хэндл протух

        // ===== структуры хоста =============================================
        // Значение структуры для хостового кода — непрозрачный Variant: раскладку
        // (нулевой слот — id типа, поля дальше) знает только движок. Индекс поля
        // здесь — номер В ПОРЯДКЕ ОБЪЯВЛЕНИЯ хостом, как в HostStructInfo.Fields.

        int StructIdOf(Variant value);                             // -1, если это не структура
        Variant StructGet(Variant structValue, int fieldIndex);
        Variant StructNew(int structId, int fieldCount);
        void StructSet(Variant structValue, int fieldIndex, Variant value);
    }

    // Геттер/сеттер хостового свойства. target — уже разрешённый объект хоста.
    public delegate Variant HostGetter(IHostContext ctx, object target);
    public delegate void HostSetter(IHostContext ctx, object target, Variant value);

    // Хостовый метод API-класса. Аргументы читаются из ctx, результат кладётся туда же.
    public delegate void HostFunction(ref CallContext ctx);

    /// <summary>
    /// Представление аргументов одного вызова хостового метода. Это обычная
    /// структура, которую VM передаёт по ref: она смотрит прямо в стек значений
    /// файбера, поэтому чтение аргументов не создаёт аллокаций/боксинга.
    /// </summary>
    public struct CallContext
    {
        private readonly Variant[] _stack;
        private readonly int _argBase;
        public readonly int ArgCount;
        public readonly IHostContext Host;

        public Variant Result;
        public bool HasResult;

        public CallContext(Variant[] stack, int argBase, int argCount, IHostContext host)
        {
            _stack = stack;
            _argBase = argBase;
            ArgCount = argCount;
            Host = host;
            Result = Variant.Nil;
            HasResult = false;
        }

        public Variant Arg(int i) => _stack[_argBase + i];

        public int Int(int i) => _stack[_argBase + i].AsInt;
        public float Float(int i) => _stack[_argBase + i].ToF();
        public bool Bool(int i) => _stack[_argBase + i].AsBool;
        public string Str(int i) => Host.ResolveString(_stack[_argBase + i]);

        public T Entity<T>(int i) where T : class
        {
            var obj = Host.ResolveObject(_stack[_argBase + i]);
            return obj as T;
        }

        public void Return(Variant v) { Result = v; HasResult = true; }
        public void ReturnInt(int v) { Result = Variant.Int(v); HasResult = true; }
        public void ReturnFloat(float v) { Result = Variant.Float(v); HasResult = true; }
        public void ReturnBool(bool v) { Result = Variant.Bool(v); HasResult = true; }
        // null-строка обязана стать Nil: Variant.Str(InternString(null)) давал
        // Str(-1) — значение, для которого `s == null` в скрипте ложно, хотя ведёт
        // оно себя как пустая строка. VariantWriter<string> уже делает именно так.
        public void ReturnStr(string v)
        {
            Result = v == null ? Variant.Nil : Variant.Str(Host.InternString(v));
            HasResult = true;
        }
        public void ReturnEntity(object o) { Result = Host.WrapObject(o); HasResult = true; }
        public void ReturnNil() { Result = Variant.Nil; HasResult = true; }
    }

    /// <summary>
    /// Ошибка исполнения скрипта. Убивает текущий файбер, но не игру.
    /// Текст можно дать на двух языках — движок покажет тот, что выбран у него
    /// (<see cref="ScriptEngine.Language"/>); <see cref="System.Exception.Message"/> — русский.
    /// </summary>
    public sealed class ScriptError : System.Exception
    {
        /// <summary>Английский текст (у ошибки с одним текстом совпадает с Message).</summary>
        public string MessageEn { get; }

        public ScriptError(string message) : base(message) { MessageEn = message; }

        public ScriptError(string ru, string en) : base(ru ?? en) { MessageEn = en ?? ru; }

        /// <summary>Текст на языке <paramref name="lang"/>.</summary>
        public string MessageFor(Language lang) => lang == Language.En ? MessageEn : Message;
    }
}
