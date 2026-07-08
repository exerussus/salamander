namespace Dsl.Runtime
{
    /// <summary>
    /// Мостик рантайма для хостового кода: перевод строк и сущностей между
    /// managed-миром хоста и упакованными Variant. Реализуется движком.
    /// </summary>
    public interface IHostContext
    {
        int InternString(string s);
        string ResolveString(Variant v);        // Nil -> null
        Variant WrapObject(object o);            // зарегистрировать/найти хэндл сущности
        object ResolveObject(Variant v);         // бросает ScriptError, если хэндл протух
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
        public void ReturnStr(string v) { Result = Variant.Str(Host.InternString(v)); HasResult = true; }
        public void ReturnEntity(object o) { Result = Host.WrapObject(o); HasResult = true; }
        public void ReturnNil() { Result = Variant.Nil; HasResult = true; }
    }

    /// <summary>Ошибка исполнения скрипта. Убивает текущий файбер, но не игру.</summary>
    public sealed class ScriptError : System.Exception
    {
        public ScriptError(string message) : base(message) { }
    }
}
