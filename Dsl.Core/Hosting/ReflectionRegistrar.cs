using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Dsl.Hosting
{
    /// <summary>
    /// Регистрация типов по атрибутам. Вся рефлексия — ОДИН раз при регистрации:
    /// для каждого метода/свойства строится строго типизированный делегат
    /// (Delegate.CreateDelegate) и вызывается соответствующая обобщённая перегрузка
    /// fluent-слоя (ApiBuilder.Fn/Act, ClassBuilder.Prop, HostBuilder.Enum) через
    /// MakeGenericMethod. В рантайме вызовы идут по тому же zero-alloc пути, что и
    /// ручной fluent — без рефлексии и боксинга.
    ///
    /// Два входа:
    ///  - Register(type): [SalamanderClass] → класс-сущность (свойства) или енум;
    ///  - RegisterApiInstance(instance): [SalamanderApi] → API из ЭКЗЕМПЛЯРА,
    ///    методы биндятся к нему (у каждой комнаты свой экземпляр, без статики).
    /// </summary>
    internal static class ReflectionRegistrar
    {
        // ===================================================================
        // Register(type): сущность / енум
        // ===================================================================

        public static void Register(HostBuilder host, Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            if (type.GetCustomAttribute<SalamanderApiAttribute>() != null)
                throw new ArgumentException(
                    $"{type.Name} помечен [SalamanderApi] — регистрируйте его от экземпляра: " +
                    "host.RegisterApi(new " + type.Name + "(...)).");

            var cls = type.GetCustomAttribute<SalamanderClassAttribute>();
            if (cls == null)
                throw new ArgumentException(
                    $"Тип {type.Name} не помечен [SalamanderClass] — нечего регистрировать.");

            if (type.IsEnum) { RegisterEnum(host, type, cls.Summary); return; }

            // сущность отдаёт данные через свойства; методы — это API ([SalamanderApi])
            bool anyMethod = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Any(m => m.GetCustomAttribute<SalamanderMethodAttribute>() != null);
            if (anyMethod)
                throw new ArgumentException(
                    $"{type.Name}: [SalamanderMethod] на классе-сущности недопустим. " +
                    "Для API пометьте отдельный класс [SalamanderApi] и регистрируйте его " +
                    "экземпляром через host.RegisterApi(instance) — так у каждой комнаты своё состояние.");

            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<SalamanderPropertyAttribute>() != null)
                .ToArray();
            RegisterEntity(host, type, cls.Summary, props);
        }

        // ===================================================================
        // RegisterApiInstance(instance): API из экземпляра
        // ===================================================================

        public static void RegisterApiInstance(HostBuilder host, object instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            var type = instance.GetType();

            var api = type.GetCustomAttribute<SalamanderApiAttribute>();
            if (api == null)
                throw new ArgumentException(
                    $"{type.Name} не помечен [SalamanderApi]. Для сущностей/енумов используйте host.Register(type).");

            string apiName = type.Name;
            var builder = host.Api(apiName);
            host.Registry.DescribeApi(apiName, api.Summary);

            var methods = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<SalamanderMethodAttribute>() != null);

            foreach (var m in methods)
            {
                object target = m.IsStatic ? null : instance; // instance-метод биндится к экземпляру
                RegisterMethod(builder, m, target);
            }
        }

        // ===================================================================
        // Регистрация одного метода через fluent Fn/Act
        // ===================================================================

        private static void RegisterMethod(ApiBuilder builder, MethodInfo m, object target)
        {
            var ps = m.GetParameters();
            var paramTypes = ps.Select(p => p.ParameterType).ToArray();
            bool isVoid = m.ReturnType == typeof(void);

            // MethodDoc: имена параметров из сигнатуры, doc — из [SalamanderParam]
            var doc = Sig.Doc(m.GetCustomAttribute<SalamanderMethodAttribute>()?.Summary);
            foreach (var p in ps)
                doc.P(ScriptName(p.Name), p.GetCustomAttribute<SalamanderParamAttribute>()?.Doc);

            // делегат: instance-метод биндится к target, статический — без приёмника
            Type delType = isVoid ? ActionType(paramTypes) : FuncType(paramTypes, m.ReturnType);
            Delegate del = target != null
                ? Delegate.CreateDelegate(delType, target, m)
                : Delegate.CreateDelegate(delType, m);

            string scriptName = ScriptName(m.Name);

            if (isVoid && paramTypes.Length == 0)
            {
                var act0 = typeof(ApiBuilder).GetMethod(
                    nameof(ApiBuilder.Act),
                    new[] { typeof(string), typeof(Action), typeof(MethodDoc) });
                act0.Invoke(builder, new object[] { scriptName, del, doc });
                return;
            }

            string fluentName = isVoid ? nameof(ApiBuilder.Act) : nameof(ApiBuilder.Fn);
            int genArgs = isVoid ? paramTypes.Length : paramTypes.Length + 1;
            var open = typeof(ApiBuilder).GetMethods()
                .First(x => x.Name == fluentName
                         && x.IsGenericMethodDefinition
                         && x.GetGenericArguments().Length == genArgs);
            var typeArgs = isVoid ? paramTypes : paramTypes.Append(m.ReturnType).ToArray();
            open.MakeGenericMethod(typeArgs).Invoke(builder, new object[] { scriptName, del, doc });
        }

        // ===================================================================
        // Енум / сущность
        // ===================================================================

        private static void RegisterEnum(HostBuilder host, Type type, string summary)
        {
            var m = typeof(HostBuilder).GetMethods()
                .First(x => x.Name == nameof(HostBuilder.Enum) && x.IsGenericMethodDefinition);
            m.MakeGenericMethod(type).Invoke(host, new object[] { null, summary });
        }

        private static void RegisterEntity(HostBuilder host, Type type, string summary, PropertyInfo[] props)
        {
            if (!type.IsClass)
                throw new ArgumentException(
                    $"{type.Name}: класс-сущность должен быть ссылочным типом (class), а не struct.");

            var classOpen = typeof(HostBuilder).GetMethods()
                .First(x => x.Name == nameof(HostBuilder.Class) && x.IsGenericMethodDefinition);
            object builder = classOpen.MakeGenericMethod(type).Invoke(host, new object[] { null, summary });
            var builderType = builder.GetType();

            var propMethods = builderType.GetMethods().Where(x => x.Name == "Prop").ToArray();
            var prop3 = propMethods.First(x => x.GetParameters().Length == 3);
            var prop4 = propMethods.First(x => x.GetParameters().Length == 4);

            foreach (var p in props)
            {
                var getM = p.GetGetMethod();
                if (getM == null)
                    throw new ArgumentException($"{type.Name}.{p.Name}: свойство без get не поддерживается.");
                var tv = p.PropertyType;
                string name = ScriptName(p.Name);
                string doc = p.GetCustomAttribute<SalamanderPropertyAttribute>()?.Summary;

                var getter = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(type, tv), getM);

                var setM = p.GetSetMethod();
                if (setM != null)
                {
                    var setter = Delegate.CreateDelegate(typeof(Action<,>).MakeGenericType(type, tv), setM);
                    prop4.MakeGenericMethod(tv).Invoke(builder, new object[] { name, getter, setter, doc });
                }
                else
                {
                    prop3.MakeGenericMethod(tv).Invoke(builder, new object[] { name, getter, doc });
                }
            }
        }

        // ===================================================================
        // Вспомогательное
        // ===================================================================

        private static string ScriptName(string csName) => csName;

        private static Type ActionType(Type[] args)
        {
            if (args.Length == 0) return typeof(Action);
            var open = Type.GetType("System.Action`" + args.Length)
                       ?? throw new NotSupportedException($"Action на {args.Length} аргументов не поддержан.");
            return open.MakeGenericType(args);
        }

        private static Type FuncType(Type[] args, Type ret)
        {
            var all = new Type[args.Length + 1];
            Array.Copy(args, all, args.Length);
            all[args.Length] = ret;
            var open = Type.GetType("System.Func`" + all.Length)
                       ?? throw new NotSupportedException($"Func на {args.Length} аргументов не поддержан.");
            return open.MakeGenericType(all);
        }
    }
}
