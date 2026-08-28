using System.Collections.Generic;

namespace Dsl.Runtime
{
    /// <summary>
    /// Все скриптовые коллекции живут здесь, Variant хранит id + версию слота.
    ///
    /// ВРЕМЯ ЖИЗНИ: коллекции собираются mark-and-sweep вместе со строками
    /// (ScriptEngine.Collect). Корни те же, что у строк: статики, стеки живых
    /// файберов, снапшоты for-in, поля активных подписок, аргументы поднимаемого
    /// события. Достижимость трассируется транзитивно — коллекция внутри
    /// коллекции остаётся живой.
    ///
    /// Циклов в графе быть не может: тип пишется в исходнике буквально, поэтому
    /// рекурсивный тип невыразим (для List&lt;T&gt;, содержащего себя, нужно
    /// T = List&lt;T&gt; — бесконечное имя), а ключ Map коллекцией быть не может.
    /// Значит по любому ребру глубина типа строго убывает и граф — DAG.
    ///
    /// ВЕРСИИ. Слот переиспользуется, поэтому голого id мало: пропущенный
    /// сборщиком хэндл указывал бы на ЧУЖУЮ живую коллекцию и молча портил
    /// состояние (в том числе в сейве). Версия слота растёт при освобождении, и
    /// протухший хэндл даёт честную ScriptError вместо тихой подмены. Это же
    /// делает громкой единственную оставшуюся ловушку: хост, придержавший
    /// Variant коллекции между тиками, — такой хэндл движку не корень.
    /// </summary>
    public sealed class CollectionStore
    {
        /// <summary>
        /// Потолок числа ЖИВЫХ коллекций (после сборки). Срабатывание означает,
        /// что достижимых коллекций действительно столько — обычно это корень,
        /// который держит их все (список, в который пишут каждый кадр).
        /// </summary>
        public int MaxLiveCollections = 1 << 20;

        /// <summary>
        /// Потолок длины одного массива. Без него `new T[n]` с произвольным n
        /// уходит прямо в new Variant[n] и даёт OutOfMemoryException, которая
        /// ловится общим catch как бесполезная «Внутренняя ошибка».
        /// </summary>
        public int MaxArrayLength = 1 << 22;

        /// <summary>Сколько коллекций освободил последний свип (для статистики и тестов).</summary>
        public int LastSweptCount { get; private set; }

        // ----- массивы (фиксированная длина) -----
        private Variant[][] _arrays = new Variant[64][];
        private int[] _arrayVer = new int[64];
        private bool[] _arrayLive = new bool[64];
        private bool[] _arrayMark = new bool[64];
        private int _arrayCount;
        private readonly Stack<int> _freeArrays = new Stack<int>();

        // ----- списки -----
        private sealed class ListData
        {
            public Variant[] Buf = new Variant[8];
            public int Count;
        }
        private ListData[] _lists = new ListData[64];
        private int[] _listVer = new int[64];
        private bool[] _listLive = new bool[64];
        private bool[] _listMark = new bool[64];
        private int _listCount;
        private readonly Stack<int> _freeLists = new Stack<int>();

        // ----- мапы -----
        private Dictionary<Variant, Variant>[] _maps = new Dictionary<Variant, Variant>[32];
        private int[] _mapVer = new int[32];
        private bool[] _mapLive = new bool[32];
        private bool[] _mapMark = new bool[32];
        private int _mapCount;
        private readonly Stack<int> _freeMaps = new Stack<int>();

        private int _liveCount;

        /// <summary>
        /// Жёсткий потолок МЕЖДУ сборками: считаются все занятые слоты, включая
        /// уже недостижимые, но ещё не подметённые.
        ///
        /// Сборку отсюда звать НЕЛЬЗЯ, и это важно. Создание коллекции происходит
        /// внутри опкода VM, а VM держит вершину стека в локальной переменной и
        /// пишет её в Fiber.Sp только на приостановках и вызовах. Обход корней в
        /// этот момент увидел бы устаревший Sp и не пометил бы значения,
        /// положенные на стек с последней записи, — то есть освободил бы живое.
        /// Поэтому здесь только отказ, а сборка идёт из Tick, между файберами.
        /// </summary>
        private void EnsureCollectionBudget()
        {
            if (_liveCount < MaxLiveCollections) return;
            throw new ScriptError(
                $"Исчерпан лимит коллекций ({MaxLiveCollections}) между сборками. Если это не утечка " +
                "через долгоживущий корень, снизьте ScriptEngine.CollectionSweepThreshold — сборка идёт в конце тика.");
        }

        private static void Grow<T>(ref T[] a, int need)
        {
            if (need < a.Length) return;
            int cap = a.Length * 2;
            while (cap <= need) cap *= 2;
            System.Array.Resize(ref a, cap);
        }

        // ===== создание ====================================================

        public Variant NewArray(int size)
        {
            if (size < 0) throw new ScriptError("Отрицательный размер массива.");
            if (size > MaxArrayLength)
                throw new ScriptError($"Размер массива {size} превышает предел {MaxArrayLength}.");
            EnsureCollectionBudget();
            int id;
            if (_freeArrays.Count > 0)
            {
                id = _freeArrays.Pop();
                // при переиспользовании подгоняем длину точно (массив — фикс. длина)
                if (_arrays[id] == null || _arrays[id].Length != size)
                    _arrays[id] = new Variant[size];
                else
                    System.Array.Clear(_arrays[id], 0, size);
            }
            else
            {
                Grow(ref _arrays, _arrayCount);
                Grow(ref _arrayVer, _arrayCount);
                Grow(ref _arrayLive, _arrayCount);
                Grow(ref _arrayMark, _arrayCount);
                id = _arrayCount++;
                _arrays[id] = new Variant[size];
            }
            _arrayLive[id] = true;
            _liveCount++;
            return Variant.Coll(VariantType.Array, id, _arrayVer[id]);
        }

        public Variant NewList()
        {
            EnsureCollectionBudget();
            int id;
            if (_freeLists.Count > 0)
            {
                id = _freeLists.Pop();
                _lists[id].Count = 0; // ёмкость сохраняем
            }
            else
            {
                Grow(ref _lists, _listCount);
                Grow(ref _listVer, _listCount);
                Grow(ref _listLive, _listCount);
                Grow(ref _listMark, _listCount);
                id = _listCount++;
                _lists[id] = new ListData();
            }
            _listLive[id] = true;
            _liveCount++;
            return Variant.Coll(VariantType.List, id, _listVer[id]);
        }

        public Variant NewMap()
        {
            EnsureCollectionBudget();
            int id;
            if (_freeMaps.Count > 0)
            {
                id = _freeMaps.Pop();
                _maps[id].Clear();
            }
            else
            {
                Grow(ref _maps, _mapCount);
                Grow(ref _mapVer, _mapCount);
                Grow(ref _mapLive, _mapCount);
                Grow(ref _mapMark, _mapCount);
                id = _mapCount++;
                _maps[id] = new Dictionary<Variant, Variant>();
            }
            _mapLive[id] = true;
            _liveCount++;
            return Variant.Coll(VariantType.Map, id, _mapVer[id]);
        }

        // ===== операции ====================================================

        public int Len(Variant coll)
        {
            switch (coll.Type)
            {
                case VariantType.Array: return ArrayOf(coll).Length;
                case VariantType.List: return ListOf(coll).Count;
                case VariantType.Map: return MapOf(coll).Count;
                case VariantType.Nil: throw new ScriptError("Обращение к null-коллекции.");
                default: throw new ScriptError("Значение не является коллекцией.");
            }
        }

        public Variant Get(Variant coll, Variant idx)
        {
            switch (coll.Type)
            {
                case VariantType.Array:
                {
                    var a = ArrayOf(coll);
                    int i = idx.AsInt;
                    if ((uint)i >= (uint)a.Length)
                        throw new ScriptError($"Индекс {i} вне границ массива (длина {a.Length}).");
                    return a[i];
                }
                case VariantType.List:
                {
                    var l = ListOf(coll);
                    int i = idx.AsInt;
                    if ((uint)i >= (uint)l.Count)
                        throw new ScriptError($"Индекс {i} вне границ списка (count {l.Count}).");
                    return l.Buf[i];
                }
                case VariantType.Map:
                {
                    var m = MapOf(coll);
                    return m.TryGetValue(idx, out var v) ? v : Variant.Nil; // отсутствие ключа = null
                }
                case VariantType.Nil:
                    throw new ScriptError("Обращение к null-коллекции.");
                default:
                    throw new ScriptError("Значение не является коллекцией.");
            }
        }

        public void Set(Variant coll, Variant idx, Variant value)
        {
            switch (coll.Type)
            {
                case VariantType.Array:
                {
                    var a = ArrayOf(coll);
                    int i = idx.AsInt;
                    if ((uint)i >= (uint)a.Length)
                        throw new ScriptError($"Индекс {i} вне границ массива (длина {a.Length}).");
                    a[i] = value;
                    return;
                }
                case VariantType.List:
                {
                    var l = ListOf(coll);
                    int i = idx.AsInt;
                    if ((uint)i >= (uint)l.Count)
                        throw new ScriptError($"Индекс {i} вне границ списка (count {l.Count}).");
                    l.Buf[i] = value;
                    return;
                }
                case VariantType.Map:
                    MapOf(coll)[idx] = value;
                    return;
                case VariantType.Nil:
                    throw new ScriptError("Обращение к null-коллекции.");
                default:
                    throw new ScriptError("Значение не является коллекцией.");
            }
        }

        public void ListAdd(Variant coll, Variant value)
        {
            var l = ListOf(coll);
            if (l.Count >= l.Buf.Length)
                System.Array.Resize(ref l.Buf, l.Buf.Length * 2);
            l.Buf[l.Count++] = value;
        }

        public void ListClear(Variant coll)
        {
            var l = ListOf(coll);
            // затираем хвост: иначе выброшенные элементы остаются достижимы для
            // сборщика через Buf и держат живыми уже ненужные коллекции и строки
            System.Array.Clear(l.Buf, 0, l.Count);
            l.Count = 0;
        }

        public bool MapHas(Variant coll, Variant key) => MapOf(coll).ContainsKey(key);

        public void MapRemove(Variant coll, Variant key) => MapOf(coll).Remove(key);

        // ===== сборка мусора ================================================

        /// <summary>Сбросить пометки перед обходом корней.</summary>
        internal void BeginSweep()
        {
            System.Array.Clear(_arrayMark, 0, _arrayMark.Length);
            System.Array.Clear(_listMark, 0, _listMark.Length);
            System.Array.Clear(_mapMark, 0, _mapMark.Length);
        }

        /// <summary>
        /// Пометить коллекцию. true — пометили ВПЕРВЫЕ, содержимое надо обойти.
        /// Мёртвый или протухший хэндл молча игнорируется: сборка не место для ошибок.
        /// </summary>
        internal bool MarkCollection(Variant v)
        {
            switch (v.Type)
            {
                case VariantType.Array:
                {
                    int id = v.CollId;
                    if ((uint)id >= (uint)_arrayCount || !_arrayLive[id] || _arrayVer[id] != v.CollVersion) return false;
                    if (_arrayMark[id]) return false;
                    _arrayMark[id] = true;
                    return true;
                }
                case VariantType.List:
                {
                    int id = v.CollId;
                    if ((uint)id >= (uint)_listCount || !_listLive[id] || _listVer[id] != v.CollVersion) return false;
                    if (_listMark[id]) return false;
                    _listMark[id] = true;
                    return true;
                }
                case VariantType.Map:
                {
                    int id = v.CollId;
                    if ((uint)id >= (uint)_mapCount || !_mapLive[id] || _mapVer[id] != v.CollVersion) return false;
                    if (_mapMark[id]) return false;
                    _mapMark[id] = true;
                    return true;
                }
                default:
                    return false;
            }
        }

        /// <summary>Освободить всё живое, но не помеченное. Возвращает число освобождённых.</summary>
        internal int EndSweep()
        {
            int freed = 0;
            for (int i = 0; i < _arrayCount; i++)
                if (_arrayLive[i] && !_arrayMark[i]) { FreeArray(i); freed++; }
            for (int i = 0; i < _listCount; i++)
                if (_listLive[i] && !_listMark[i]) { FreeList(i); freed++; }
            for (int i = 0; i < _mapCount; i++)
                if (_mapLive[i] && !_mapMark[i]) { FreeMap(i); freed++; }
            LastSweptCount = freed;
            return freed;
        }

        private void FreeArray(int id)
        {
            _arrays[id] = null;      // фиксированная длина, ёмкость переиспользовать нечего
            _arrayLive[id] = false;
            _arrayVer[id]++;         // все выданные хэндлы протухают
            _freeArrays.Push(id);
            _liveCount--;
        }

        private void FreeList(int id)
        {
            var l = _lists[id];
            System.Array.Clear(l.Buf, 0, l.Count); // не держим содержимое живым
            l.Count = 0;                           // сам буфер оставляем: ёмкость переиспользуется
            _listLive[id] = false;
            _listVer[id]++;
            _freeLists.Push(id);
            _liveCount--;
        }

        private void FreeMap(int id)
        {
            _maps[id].Clear();
            _mapLive[id] = false;
            _mapVer[id]++;
            _freeMaps.Push(id);
            _liveCount--;
        }

        // ===== сериализация ================================================
        // Доступ для SaveState/LoadState: перечисление живых коллекций и
        // прямое чтение/заполнение содержимого (минуя скриптовые операции).

        internal void CollectLive(List<(VariantType kind, int id)> into)
        {
            into.Clear();
            for (int i = 0; i < _arrayCount; i++) if (_arrayLive[i]) into.Add((VariantType.Array, i));
            for (int i = 0; i < _listCount; i++) if (_listLive[i]) into.Add((VariantType.List, i));
            for (int i = 0; i < _mapCount; i++) if (_mapLive[i]) into.Add((VariantType.Map, i));
        }

        /// <summary>Жив ли хэндл (id в границах, слот занят, версия совпала).</summary>
        public bool IsAlive(Variant v)
        {
            switch (v.Type)
            {
                case VariantType.Array:
                    return (uint)v.CollId < (uint)_arrayCount && _arrayLive[v.CollId] && _arrayVer[v.CollId] == v.CollVersion;
                case VariantType.List:
                    return (uint)v.CollId < (uint)_listCount && _listLive[v.CollId] && _listVer[v.CollId] == v.CollVersion;
                case VariantType.Map:
                    return (uint)v.CollId < (uint)_mapCount && _mapLive[v.CollId] && _mapVer[v.CollId] == v.CollVersion;
                default:
                    return false;
            }
        }

        internal Variant[] GetArrayData(int id) => _arrays[id];

        internal void GetListData(int id, out Variant[] buf, out int count)
        {
            var l = _lists[id];
            buf = l.Buf;
            count = l.Count;
        }

        internal Dictionary<Variant, Variant> GetMapData(int id) => _maps[id];

        internal void LoadListAdd(int id, Variant v)
        {
            var l = _lists[id];
            if (l.Count >= l.Buf.Length) System.Array.Resize(ref l.Buf, l.Buf.Length * 2);
            l.Buf[l.Count++] = v;
        }

        internal void LoadMapSet(int id, Variant k, Variant v) => _maps[id][k] = v;

        // ===== датчики =====================================================

        /// <summary>Число живых коллекций.</summary>
        public int LiveCount => _liveCount;

        public void Clear()
        {
            for (int i = 0; i < _arrayCount; i++) if (_arrayLive[i]) FreeArray(i);
            for (int i = 0; i < _listCount; i++) if (_listLive[i]) FreeList(i);
            for (int i = 0; i < _mapCount; i++) if (_mapLive[i]) FreeMap(i);
            // счётчики слотов и версии НЕ сбрасываем: так хэндлы, выданные до
            // перезагрузки программы, остаются мёртвыми, а не воскресают чужими
        }

        // ===== доступ ======================================================

        private Variant[] ArrayOf(Variant v)
        {
            int id = v.CollId;
            if ((uint)id >= (uint)_arrayCount || !_arrayLive[id] || _arrays[id] == null)
                throw new ScriptError("Массив не существует.");
            if (_arrayVer[id] != v.CollVersion)
                throw new ScriptError("Обращение к освобождённой коллекции (протухший хэндл массива).");
            return _arrays[id];
        }

        private ListData ListOf(Variant v)
        {
            if (v.Type != VariantType.List) throw new ScriptError("Ожидался List.");
            int id = v.CollId;
            if ((uint)id >= (uint)_listCount || !_listLive[id] || _lists[id] == null)
                throw new ScriptError("Список не существует.");
            if (_listVer[id] != v.CollVersion)
                throw new ScriptError("Обращение к освобождённой коллекции (протухший хэндл списка).");
            return _lists[id];
        }

        private Dictionary<Variant, Variant> MapOf(Variant v)
        {
            if (v.Type != VariantType.Map) throw new ScriptError("Ожидался Map.");
            int id = v.CollId;
            if ((uint)id >= (uint)_mapCount || !_mapLive[id] || _maps[id] == null)
                throw new ScriptError("Map не существует.");
            if (_mapVer[id] != v.CollVersion)
                throw new ScriptError("Обращение к освобождённой коллекции (протухший хэндл Map).");
            return _maps[id];
        }
    }
}
