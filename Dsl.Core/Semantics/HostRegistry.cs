using System.Collections.Generic;
using Dsl.Runtime;

namespace Dsl.Semantics
{
    public sealed class HostEnumInfo
    {
        public int Id;
        public string Name;
        public readonly Dictionary<string, int> Members = new Dictionary<string, int>();
        public string[] Names;
        public string Summary;
    }

    public sealed class HostPropInfo
    {
        public int Id;
        public string Name;
        public TypeRef Type;
        public bool ReadOnly;
        public string Doc;
    }

    public sealed class HostClassInfo
    {
        public int Id;
        public string Name;
        public string Summary;
        public readonly Dictionary<string, HostPropInfo> Props = new Dictionary<string, HostPropInfo>();
        public bool TryGetProp(string n, out HostPropInfo p) => Props.TryGetValue(n, out p);
    }

    public sealed class HostMethodInfo
    {
        public int HostFnId;
        public string Name;
        public TypeRef[] Params;
        public TypeRef Ret;          // Void, если метод ничего не возвращает
        public string Summary;
        public string[] ParamNames;  // может быть null — тогда синтезируются argN
        public string[] ParamDocs;   // может быть null
    }

    public sealed class HostApiInfo
    {
        public string Name;
        public string Summary;
        public readonly Dictionary<string, HostMethodInfo> Methods = new Dictionary<string, HostMethodInfo>();
        public bool TryGetMethod(string n, out HostMethodInfo m) => Methods.TryGetValue(n, out m);
    }

    /// <summary>Событие вида архетипа (своё пространство имён внутри вида).</summary>
    public sealed class ArchetypeEventInfo
    {
        public int LocalId;          // индекс внутри вида
        public string Name;
        public TypeRef[] Params;
        public string Summary;
        public string[] ParamNames;
        public string[] ParamDocs;
    }

    /// <summary>
    /// Вид игровой сущности, механики которой скрипты описывают блоками
    /// «вид id { event ... }» (spell/item/hero/...). Объявляется хостом;
    /// для языка виды — данные, а не ключевые слова.
    /// </summary>
    public sealed class ArchetypeKindInfo
    {
        public int Id;
        public string Name;
        public string Summary;
        public readonly List<ArchetypeEventInfo> Events = new List<ArchetypeEventInfo>();
        public readonly Dictionary<string, ArchetypeEventInfo> EventByName = new Dictionary<string, ArchetypeEventInfo>();
        /// <summary>Известные игре id (опционально): непусто — чекер ловит опечатки в id блоков.</summary>
        public HashSet<string> KnownIds;
    }

    public sealed class HostEventInfo
    {
        public int Id;
        public string Name;
        public TypeRef[] Params;
        public string Summary;
        public string[] ParamNames;
        public string[] ParamDocs;
    }

    /// <summary>
    /// Единая точка регистрации всего, что хост открывает скриптам. Заполняется
    /// один раз на старте (не горячий путь). Хранит и сигнатуры (для чекера),
    /// и делегаты (для VM). "Engine" здесь НЕ регистрируется — это встроенный
    /// класс, распознаваемый чекером и VM отдельно.
    /// </summary>
    public sealed class HostRegistry
    {
        private readonly Dictionary<string, HostEnumInfo> _enums = new Dictionary<string, HostEnumInfo>();
        private readonly Dictionary<string, HostClassInfo> _classes = new Dictionary<string, HostClassInfo>();
        private readonly Dictionary<string, HostApiInfo> _apis = new Dictionary<string, HostApiInfo>();
        private readonly Dictionary<string, HostEventInfo> _events = new Dictionary<string, HostEventInfo>();

        private readonly List<HostGetter> _getters = new List<HostGetter>();
        private readonly List<HostSetter> _setters = new List<HostSetter>();
        private readonly List<HostFunction> _functions = new List<HostFunction>();
        private readonly List<HostEventInfo> _eventList = new List<HostEventInfo>();

        public int EnumCount => _enums.Count;
        public int EventCount => _eventList.Count;

        // ===== регистрация (вызывает хост) ==================================

        public int DefineEnum(string name, params string[] members)
            => DefineEnum(name, null, members);

        public int DefineEnum(string name, string summary, string[] members)
        {
            var info = new HostEnumInfo { Id = _enums.Count, Name = name, Names = members, Summary = summary };
            for (int i = 0; i < members.Length; i++) info.Members[members[i]] = i;
            _enums[name] = info;
            return info.Id;
        }

        public int DefineClass(string name) => DefineClass(name, null);

        public int DefineClass(string name, string summary)
        {
            var info = new HostClassInfo { Id = _classes.Count, Name = name, Summary = summary };
            _classes[name] = info;
            return info.Id;
        }

        public int DefineProperty(string className, string propName, TypeRef type,
                                  bool readOnly, HostGetter getter, HostSetter setter, string doc = null)
        {
            if (!_classes.TryGetValue(className, out var cls))
                throw new System.InvalidOperationException($"Класс хоста '{className}' не зарегистрирован.");
            int id = _getters.Count;
            _getters.Add(getter);
            _setters.Add(setter); // может быть null для read-only
            cls.Props[propName] = new HostPropInfo { Id = id, Name = propName, Type = type, ReadOnly = readOnly, Doc = doc };
            return id;
        }

        public HostApiInfo DefineApiClass(string name)
        {
            if (!_apis.TryGetValue(name, out var api))
            {
                api = new HostApiInfo { Name = name };
                _apis[name] = api;
            }
            return api;
        }

        /// <summary>Задать краткое описание API-класса (уходит в манифест).</summary>
        public void DescribeApi(string name, string summary)
        {
            if (summary != null) DefineApiClass(name).Summary = summary;
        }

        public int DefineMethod(string apiClass, string method, TypeRef[] paramTypes, TypeRef ret, HostFunction fn)
            => DefineMethod(apiClass, method, paramTypes, ret, fn, null, null, null);

        public int DefineMethod(string apiClass, string method, TypeRef[] paramTypes, TypeRef ret, HostFunction fn,
                                string summary, string[] paramNames, string[] paramDocs)
        {
            var api = DefineApiClass(apiClass);
            int id = _functions.Count;
            _functions.Add(fn);
            api.Methods[method] = new HostMethodInfo
            {
                HostFnId = id,
                Name = method,
                Params = paramTypes ?? System.Array.Empty<TypeRef>(),
                Ret = ret ?? TypeRef.Void,
                Summary = summary,
                ParamNames = paramNames,
                ParamDocs = paramDocs,
            };
            return id;
        }

        public int DefineEvent(string name, params TypeRef[] paramTypes)
            => DefineEvent(name, paramTypes, null, null, null);

        public int DefineEvent(string name, TypeRef[] paramTypes, string summary, string[] paramNames, string[] paramDocs)
        {
            var info = new HostEventInfo
            {
                Id = _eventList.Count,
                Name = name,
                Params = paramTypes ?? System.Array.Empty<TypeRef>(),
                Summary = summary,
                ParamNames = paramNames,
                ParamDocs = paramDocs,
            };
            _events[name] = info;
            _eventList.Add(info);
            return info.Id;
        }

        // ===== удобные конструкторы типов (для хоста) =======================

        public TypeRef ClassType(string name) =>
            _classes.TryGetValue(name, out var c) ? TypeRef.Entity(c.Id) : TypeRef.Error;

        public TypeRef EnumType(string name) =>
            _enums.TryGetValue(name, out var e) ? TypeRef.EnumOf(e.Id) : TypeRef.Error;

        // ===== доступ для чекера ============================================

        public bool TryGetEnum(string n, out HostEnumInfo e) => _enums.TryGetValue(n, out e);
        public bool TryGetClass(string n, out HostClassInfo c) => _classes.TryGetValue(n, out c);
        public bool TryGetApi(string n, out HostApiInfo a) => _apis.TryGetValue(n, out a);
        public bool TryGetEvent(string n, out HostEventInfo e) => _events.TryGetValue(n, out e);

        // ===== виды архетипов ==============================================

        private readonly List<ArchetypeKindInfo> _archKinds = new List<ArchetypeKindInfo>();
        private readonly Dictionary<string, ArchetypeKindInfo> _archKindByName = new Dictionary<string, ArchetypeKindInfo>();

        public int ArchetypeKindCount => _archKinds.Count;
        public ArchetypeKindInfo GetArchetypeKind(int id) => _archKinds[id];
        public bool TryGetArchetypeKind(string name, out ArchetypeKindInfo k) => _archKindByName.TryGetValue(name, out k);

        public int DefineArchetypeKind(string name, string summary = null)
        {
            if (_archKindByName.TryGetValue(name, out var existing))
            {
                if (summary != null) existing.Summary = summary;
                return existing.Id;
            }
            var k = new ArchetypeKindInfo { Id = _archKinds.Count, Name = name, Summary = summary };
            _archKinds.Add(k);
            _archKindByName[name] = k;
            return k.Id;
        }

        public int DefineArchetypeEvent(int kindId, string name, TypeRef[] paramTypes,
                                        string summary = null, string[] paramNames = null, string[] paramDocs = null)
        {
            var k = _archKinds[kindId];
            if (k.EventByName.ContainsKey(name))
                throw new System.ArgumentException($"Событие '{name}' уже объявлено у вида '{k.Name}'.");
            var e = new ArchetypeEventInfo
            {
                LocalId = k.Events.Count,
                Name = name,
                Params = paramTypes ?? System.Array.Empty<TypeRef>(),
                Summary = summary,
                ParamNames = paramNames,
                ParamDocs = paramDocs,
            };
            k.Events.Add(e);
            k.EventByName[name] = e;
            return e.LocalId;
        }

        /// <summary>Список id, известных игре (для валидации блоков чекером). null/пусто — любые id.</summary>
        public void SetArchetypeKnownIds(int kindId, System.Collections.Generic.IEnumerable<string> ids)
        {
            var k = _archKinds[kindId];
            k.KnownIds = ids == null ? null : new HashSet<string>(ids, System.StringComparer.Ordinal);
        }
        public IReadOnlyList<HostEventInfo> Events => _eventList;

        // ===== перечисление (для экспорта манифеста API) ====================

        public IEnumerable<HostEnumInfo> AllEnums => _enums.Values;
        public IEnumerable<HostClassInfo> AllClasses => _classes.Values;
        public IEnumerable<HostApiInfo> AllApis => _apis.Values;

        public bool TryGetClassById(int id, out HostClassInfo cls)
        {
            foreach (var c in _classes.Values) if (c.Id == id) { cls = c; return true; }
            cls = null; return false;
        }

        public bool TryGetEnumById(int id, out HostEnumInfo e)
        {
            foreach (var x in _enums.Values) if (x.Id == id) { e = x; return true; }
            e = null; return false;
        }

        // ===== доступ для VM ================================================

        public HostGetter Getter(int propId) => _getters[propId];
        public HostSetter Setter(int propId) => _setters[propId];
        public HostFunction Function(int hostFnId) => _functions[hostFnId];
    }
}
