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

    /// <summary>
    /// Именованное значение у API-класса: идентификатор контента, ключ, тег.
    /// Читается без скобок и сворачивается компилятором в литерал — ни делегата
    /// в реестре, ни хостового вызова в байткоде.
    /// </summary>
    public sealed class HostApiConstInfo
    {
        public int Index;        // порядок объявления хостом — для манифеста и подсказок
        public string Name;
        public TypeRef Type;
        public Variant Value;
        public string ValueStr;  // строки отдельно: их id раздаёт StringTable при загрузке
        public string Doc;
    }

    public sealed class HostApiInfo
    {
        public string Name;
        public string Summary;
        public readonly Dictionary<string, HostMethodInfo> Methods = new Dictionary<string, HostMethodInfo>();
        public bool TryGetMethod(string n, out HostMethodInfo m) => Methods.TryGetValue(n, out m);

        // список — для манифеста и подсказок (порядок объявления), словарь — для поиска
        public readonly List<HostApiConstInfo> Consts = new List<HostApiConstInfo>();
        public readonly Dictionary<string, HostApiConstInfo> ConstByName = new Dictionary<string, HostApiConstInfo>();
        public bool TryGetConst(string n, out HostApiConstInfo c) => ConstByName.TryGetValue(n, out c);
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
    /// Ожидаемая константа вида — «контракт контента»: какое поле обязан (или
    /// может) объявить блок этого вида. Роль та же, что у KnownIds для id:
    /// объявленный набор непуст — чекер сверяет с ним поля каждого блока.
    /// Ничего не объявлено — набор ОТКРЫТ (так живут атрибуты: имена слотов
    /// знает сам атрибут, кор о них не слышал), проверять нечего.
    /// </summary>
    public sealed class ArchetypeConstInfo
    {
        public int Index;        // порядок объявления хостом
        public string Name;
        public TypeRef Type;
        public bool Required;    // нет в блоке → ошибка компиляции, а не пустая сущность на плейтесте
        public string Doc;

        /// <summary>
        /// Дефолт: блок вправе поле не объявлять — оно всё равно есть, видно
        /// скриптам и читается хостом. Взаимоисключающ с Required (иначе
        /// «обязательно, но есть значение по умолчанию» — противоречие).
        /// </summary>
        public bool HasDefault;
        public Variant DefaultValue;   // bool/int/float/double/enum
        public string DefaultStr;      // string (null-строка — это HasDefault со DefaultStr == null)
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
        /// <summary>Ожидаемые константы (опционально): непусто — чекер сверяет поля блоков.</summary>
        public readonly List<ArchetypeConstInfo> Consts = new List<ArchetypeConstInfo>();
        public readonly Dictionary<string, ArchetypeConstInfo> ConstByName = new Dictionary<string, ArchetypeConstInfo>();
    }

    /// <summary>Поле структуры: имя, тип, значение по умолчанию (если в new его не задали).</summary>
    public sealed class HostStructFieldInfo
    {
        public int Index;            // позиция в значении
        public string Name;
        public TypeRef Type;
        public Variant Default;      // bool/int/float/double/enum; для string — DefaultStr
        public string DefaultStr;
        public string Doc;
    }

    /// <summary>
    /// Структура хоста: именованный НЕИЗМЕНЯЕМЫЙ набор полей, который скрипт
    /// собирает через «new Damage(slash: 21)». Объявляет её игра, а не скрипт:
    /// это контракт, который игра потом читает обратно.
    ///
    /// Значение в рантайме — обычный массив Variant фиксированной длины, поэтому
    /// хранилище, сборщик и сейв не знают о структурах ничего: им это массив.
    /// Неизменяемость (полю нельзя присвоить) снимает вопрос о семантике
    /// присваивания: алиасинг ненаблюдаем, копировать нечего.
    /// </summary>
    public sealed class HostStructInfo
    {
        public int Id;
        public string Name;
        public string Summary;
        public readonly List<HostStructFieldInfo> Fields = new List<HostStructFieldInfo>();
        public readonly Dictionary<string, HostStructFieldInfo> FieldByName = new Dictionary<string, HostStructFieldInfo>();
        public bool TryGetField(string n, out HostStructFieldInfo f) => FieldByName.TryGetValue(n, out f);
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

        // Id раньше выводился из Count перезаписываемого словаря: повторная
        // регистрация имени НЕ увеличивала Count, поэтому следующий (уже другой)
        // тип получал тот же Id. Два разных Entity/Enum становились неразличимы
        // для чекера — Item проходил туда, где ждут Unit, а падало это позже и в
        // другом месте как InvalidCastException → «Внутренняя ошибка».
        // Плюс id скриптовых енумов считаются как EnumCount + n, так что сдвиг
        // ломал и их. Поэтому дубль теперь — явная ошибка регистрации.
        public int DefineEnum(string name, string summary, string[] members)
        {
            if (_enums.ContainsKey(name))
                throw new System.InvalidOperationException(
                    $"Енум '{name}' уже зарегистрирован. Регистрируйте каждый тип ровно один раз.");
            if (_classes.ContainsKey(name) || _structByName.ContainsKey(name))
                throw new System.InvalidOperationException(
                    $"Имя '{name}' уже занято классом или структурой хоста.");
            var info = new HostEnumInfo { Id = _enums.Count, Name = name, Names = members, Summary = summary };
            for (int i = 0; i < members.Length; i++) info.Members[members[i]] = i;
            _enums[name] = info;
            return info.Id;
        }

        public int DefineClass(string name) => DefineClass(name, null);

        public int DefineClass(string name, string summary)
        {
            if (_classes.ContainsKey(name))
                throw new System.InvalidOperationException(
                    $"Класс '{name}' уже зарегистрирован. Регистрируйте каждый тип ровно один раз.");
            // имя типа в скрипте одно на всех: молча разрешить дубль — значит
            // отдать разрешение имени на откуп порядку проверок в чекере
            if (_enums.ContainsKey(name) || _structByName.ContainsKey(name))
                throw new System.InvalidOperationException(
                    $"Имя '{name}' уже занято енумом или структурой хоста.");
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

        // Составные имена API ("Api.Weapon"): точка — часть ИМЕНИ, а не оператор.
        // Хранилище было готово (ключ — произвольная строка), добавляется только
        // множество префиксов: чекеру нужно опознать «Api» как узел пространства
        // имён, даже если под таким именем API не зарегистрирован.
        private readonly HashSet<string> _apiNamespaces = new HashSet<string>(System.StringComparer.Ordinal);

        public HostApiInfo DefineApiClass(string name)
        {
            if (!_apis.TryGetValue(name, out var api))
            {
                ValidateApiName(name);
                api = new HostApiInfo { Name = name };
                _apis[name] = api;
                // "Api.Weapon.Melee" → узлы "Api" и "Api.Weapon"
                for (int i = name.IndexOf('.'); i > 0; i = name.IndexOf('.', i + 1))
                    _apiNamespaces.Add(name.Substring(0, i));
            }
            return api;
        }

        private static void ValidateApiName(string name)
        {
            bool bad = string.IsNullOrEmpty(name);
            if (!bad)
                foreach (var seg in name.Split('.'))
                {
                    if (seg.Length == 0) { bad = true; break; }
                    if (!(char.IsLetter(seg[0]) || seg[0] == '_')) { bad = true; break; }
                    foreach (var c in seg)
                        if (!(char.IsLetterOrDigit(c) || c == '_')) { bad = true; break; }
                    if (bad) break;
                }
            if (bad)
                throw new System.ArgumentException(
                    $"Недопустимое имя API '{name}'. Имя — один или несколько сегментов-идентификаторов " +
                    "через точку: \"WeaponApi\", \"Api.Weapon\".");
        }

        /// <summary>
        /// Имя — узел пространства имён API ("Api" при зарегистрированном
        /// "Api.Weapon"), но не сам API. Нужно разрешению идентификаторов:
        /// голая голова составного имени не должна читаться как ошибка.
        /// </summary>
        public bool IsApiNamespace(string name) => _apiNamespaces.Contains(name);

        /// <summary>Имена API, начинающиеся с "prefix." — для подсказок и диагностики.</summary>
        public IEnumerable<string> ApiNamesUnder(string prefix)
        {
            string head = prefix + ".";
            foreach (var n in _apis.Keys)
                if (n.StartsWith(head, System.StringComparison.Ordinal)) yield return n;
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
            // Дубль молча затирал первую регистрацию, и находилось это по «эта
            // строка рецепта ничего не делает». Перегрузок в языке нет: два
            // одноимённых метода — всегда ошибка проводки, а не намерение.
            if (api.Methods.ContainsKey(method))
                throw new System.InvalidOperationException(
                    $"Метод '{method}' уже зарегистрирован у API '{apiClass}'. " +
                    "Перегрузок в языке нет — дайте методам разные имена.");
            if (api.ConstByName.ContainsKey(method))
                throw new System.InvalidOperationException(
                    $"У API '{apiClass}' уже есть константа '{method}': имя одно на всех, " +
                    "методом и константой одновременно оно быть не может.");
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

        /// <summary>
        /// Именованное значение у API-класса: <c>Api.Parts.Grip.sword_01</c>.
        /// Раньше это выражалось методом без аргументов, возвращающим литерал —
        /// то есть делегатом в реестре и хостовым вызовом на каждое обращение,
        /// хотя вычислять нечего. Константа не занимает слот в _functions и
        /// сворачивается компилятором в литерал.
        /// </summary>
        public HostApiConstInfo DefineApiConst(string apiClass, string name, TypeRef type,
                                               Variant value, string valueStr, string doc = null)
        {
            var api = DefineApiClass(apiClass);
            if (api.ConstByName.ContainsKey(name))
                throw new System.InvalidOperationException(
                    $"Константа '{name}' уже зарегистрирована у API '{apiClass}'.");
            if (api.Methods.ContainsKey(name))
                throw new System.InvalidOperationException(
                    $"У API '{apiClass}' уже есть метод '{name}': имя одно на всех, " +
                    "методом и константой одновременно оно быть не может.");

            var info = new HostApiConstInfo
            {
                Index = api.Consts.Count,
                Name = name,
                Type = type ?? TypeRef.Error,
                Value = value,
                ValueStr = valueStr,
                Doc = doc,
            };
            api.Consts.Add(info);
            api.ConstByName[name] = info;
            return info;
        }

        public int DefineEvent(string name, params TypeRef[] paramTypes)
            => DefineEvent(name, paramTypes, null, null, null);

        public int DefineEvent(string name, TypeRef[] paramTypes, string summary, string[] paramNames, string[] paramDocs)
        {
            // дубль оставлял в _eventList мёртвый слот и незаметно менял EventCount
            if (_events.ContainsKey(name))
                throw new System.InvalidOperationException(
                    $"Событие '{name}' уже зарегистрировано. Имя события должно быть уникальным.");
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

        // ===== структуры ====================================================

        private readonly List<HostStructInfo> _structs = new List<HostStructInfo>();
        private readonly Dictionary<string, HostStructInfo> _structByName = new Dictionary<string, HostStructInfo>();

        public int StructCount => _structs.Count;
        public HostStructInfo GetStruct(int id) => _structs[id];
        public bool TryGetStruct(string name, out HostStructInfo s) => _structByName.TryGetValue(name, out s);
        public bool TryGetStructById(int id, out HostStructInfo s)
        {
            if ((uint)id >= (uint)_structs.Count) { s = null; return false; }
            s = _structs[id];
            return true;
        }
        public IEnumerable<HostStructInfo> AllStructs => _structs;

        public int DefineStruct(string name, string summary = null)
        {
            if (_structByName.ContainsKey(name))
                throw new System.InvalidOperationException(
                    $"Структура '{name}' уже зарегистрирована. Регистрируйте каждый тип ровно один раз.");
            if (_classes.ContainsKey(name) || _enums.ContainsKey(name))
                throw new System.InvalidOperationException(
                    $"Имя '{name}' уже занято классом или енумом хоста.");
            var s = new HostStructInfo { Id = _structs.Count, Name = name, Summary = summary };
            _structs.Add(s);
            _structByName[name] = s;
            return s.Id;
        }

        public int DefineStructField(int structId, string name, TypeRef type,
                                     Variant defaultValue = default, string defaultStr = null, string doc = null)
        {
            var s = _structs[structId];
            if (s.FieldByName.ContainsKey(name))
                throw new System.ArgumentException($"Поле '{name}' уже объявлено у структуры '{s.Name}'.");
            var f = new HostStructFieldInfo
            {
                Index = s.Fields.Count,
                Name = name,
                Type = type ?? TypeRef.Error,
                Default = defaultValue,
                DefaultStr = defaultStr,
                Doc = doc,
            };
            s.Fields.Add(f);
            s.FieldByName[name] = f;
            return f.Index;
        }

        public TypeRef StructType(string name) =>
            _structByName.TryGetValue(name, out var s) ? TypeRef.StructOf(s.Id) : TypeRef.Error;

        // Фабрика C#-значения из полей: нужна только engine.ReadStruct<T>.
        // Хранится как object, чтобы реестр не зависел от Dsl.Hosting.
        private readonly Dictionary<int, System.Delegate> _structFactories = new Dictionary<int, System.Delegate>();

        public void SetStructFactory(int structId, System.Delegate factory)
        {
            _ = _structs[structId];               // проверка диапазона
            _structFactories[structId] = factory;
        }

        public System.Delegate StructFactory(int structId)
            => _structFactories.TryGetValue(structId, out var f) ? f : null;

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

        /// <summary>
        /// Объявить ожидаемую константу вида (поле блока-архетипа). Дубль —
        /// ошибка регистрации, как и у событий: молча перезаписанное объявление
        /// дало бы контракт, которого никто не писал.
        /// </summary>
        public int DefineArchetypeConst(int kindId, string name, TypeRef type,
                                        bool required = false, string doc = null)
            => DefineArchetypeConst(kindId, name, type, required, doc, false, Variant.Nil, null);

        public int DefineArchetypeConst(int kindId, string name, TypeRef type,
                                        bool required, string doc,
                                        bool hasDefault, Variant defaultValue, string defaultStr)
        {
            var k = _archKinds[kindId];
            if (k.ConstByName.ContainsKey(name))
                throw new System.ArgumentException($"Константа '{name}' уже объявлена у вида '{k.Name}'.");
            if (required && hasDefault)
                throw new System.ArgumentException(
                    $"Константа '{name}' вида '{k.Name}': required и дефолт взаимоисключающи — " +
                    "либо блок обязан её объявить, либо есть значение по умолчанию.");
            var c = new ArchetypeConstInfo
            {
                Index = k.Consts.Count,
                Name = name,
                Type = type ?? TypeRef.Error,
                Required = required,
                Doc = doc,
                HasDefault = hasDefault,
                DefaultValue = defaultValue,
                DefaultStr = defaultStr,
            };
            k.Consts.Add(c);
            k.ConstByName[name] = c;
            return c.Index;
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

        /// <summary>Сколько хостовых делегатов зарегистрировано (константы их не занимают).</summary>
        public int FunctionCount => _functions.Count;
    }
}
