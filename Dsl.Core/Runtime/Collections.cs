using System.Collections.Generic;

namespace Dsl.Runtime
{
    /// <summary>
    /// Все скриптовые коллекции живут здесь, Variant хранит только id.
    /// Слоты пулятся; ёмкость буферов сохраняется при переиспользовании.
    /// Время жизни: до перезагрузки программы (Clear) — отдельного GC
    /// коллекций в v1 нет, это осознанное упрощение.
    /// </summary>
    public sealed class CollectionStore
    {
        // ----- массивы (фиксированная длина) -----
        private Variant[][] _arrays = new Variant[64][];
        private int _arrayCount;
        private readonly Stack<int> _freeArrays = new Stack<int>();

        // ----- списки -----
        private sealed class ListData
        {
            public Variant[] Buf = new Variant[8];
            public int Count;
        }
        private ListData[] _lists = new ListData[64];
        private int _listCount;
        private readonly Stack<int> _freeLists = new Stack<int>();

        // ----- мапы -----
        private Dictionary<Variant, Variant>[] _maps = new Dictionary<Variant, Variant>[32];
        private int _mapCount;
        private readonly Stack<int> _freeMaps = new Stack<int>();

        // ===== создание ====================================================

        public Variant NewArray(int size)
        {
            if (size < 0) throw new ScriptError("Отрицательный размер массива.");
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
                if (_arrayCount >= _arrays.Length) System.Array.Resize(ref _arrays, _arrays.Length * 2);
                id = _arrayCount++;
                _arrays[id] = new Variant[size];
            }
            return Variant.Coll(VariantType.Array, id);
        }

        public Variant NewList()
        {
            int id;
            if (_freeLists.Count > 0)
            {
                id = _freeLists.Pop();
                _lists[id].Count = 0; // ёмкость сохраняем
            }
            else
            {
                if (_listCount >= _lists.Length) System.Array.Resize(ref _lists, _lists.Length * 2);
                id = _listCount++;
                _lists[id] = new ListData();
            }
            return Variant.Coll(VariantType.List, id);
        }

        public Variant NewMap()
        {
            int id;
            if (_freeMaps.Count > 0)
            {
                id = _freeMaps.Pop();
                _maps[id].Clear();
            }
            else
            {
                if (_mapCount >= _maps.Length) System.Array.Resize(ref _maps, _maps.Length * 2);
                id = _mapCount++;
                _maps[id] = new Dictionary<Variant, Variant>();
            }
            return Variant.Coll(VariantType.Map, id);
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

        public void ListClear(Variant coll) => ListOf(coll).Count = 0;

        public bool MapHas(Variant coll, Variant key) => MapOf(coll).ContainsKey(key);

        public void MapRemove(Variant coll, Variant key) => MapOf(coll).Remove(key);

        // ===== сериализация ================================================
        // Доступ для SaveState/LoadState: перечисление живых коллекций и
        // прямое чтение/заполнение содержимого (минуя скриптовые операции).

        internal void CollectLive(List<(VariantType kind, int id)> into)
        {
            into.Clear();
            var freeA = new HashSet<int>(_freeArrays);
            for (int i = 0; i < _arrayCount; i++)
                if (!freeA.Contains(i) && _arrays[i] != null) into.Add((VariantType.Array, i));
            var freeL = new HashSet<int>(_freeLists);
            for (int i = 0; i < _listCount; i++)
                if (!freeL.Contains(i) && _lists[i] != null) into.Add((VariantType.List, i));
            var freeM = new HashSet<int>(_freeMaps);
            for (int i = 0; i < _mapCount; i++)
                if (!freeM.Contains(i) && _maps[i] != null) into.Add((VariantType.Map, i));
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

        /// <summary>Приблизительное число живых коллекций (для статистики).</summary>
        public int LiveCount =>
            (_arrayCount - _freeArrays.Count) +
            (_listCount - _freeLists.Count) +
            (_mapCount - _freeMaps.Count);

        // ===== обход для GC строк ==========================================

        /// <summary>Пометить все строки, живущие в коллекциях (для свипа StringTable).</summary>
        public void MarkStrings(StringTable strings)
        {
            for (int i = 0; i < _arrayCount; i++)
            {
                var a = _arrays[i];
                if (a == null) continue;
                for (int j = 0; j < a.Length; j++)
                    if (a[j].Type == VariantType.Str) strings.Mark(a[j].StrId);
            }
            for (int i = 0; i < _listCount; i++)
            {
                var l = _lists[i];
                if (l == null) continue;
                for (int j = 0; j < l.Count; j++)
                    if (l.Buf[j].Type == VariantType.Str) strings.Mark(l.Buf[j].StrId);
            }
            for (int i = 0; i < _mapCount; i++)
            {
                var m = _maps[i];
                if (m == null) continue;
                foreach (var kv in m)
                {
                    if (kv.Key.Type == VariantType.Str) strings.Mark(kv.Key.StrId);
                    if (kv.Value.Type == VariantType.Str) strings.Mark(kv.Value.StrId);
                }
            }
        }

        public void Clear()
        {
            for (int i = 0; i < _arrayCount; i++) _arrays[i] = null;
            for (int i = 0; i < _listCount; i++) if (_lists[i] != null) _lists[i].Count = 0;
            for (int i = 0; i < _mapCount; i++) _maps[i]?.Clear();
            _arrayCount = 0;
            _freeArrays.Clear();
            _freeLists.Clear();
            _freeMaps.Clear();
            // списки/мапы остаются в пуле с сохранённой ёмкостью
            for (int i = _listCount - 1; i >= 0; i--) _freeLists.Push(i);
            for (int i = _mapCount - 1; i >= 0; i--) _freeMaps.Push(i);
        }

        // ===== доступ ======================================================

        private Variant[] ArrayOf(Variant v)
        {
            int id = v.CollId;
            if ((uint)id >= (uint)_arrayCount || _arrays[id] == null)
                throw new ScriptError("Массив не существует.");
            return _arrays[id];
        }

        private ListData ListOf(Variant v)
        {
            if (v.Type != VariantType.List) throw new ScriptError("Ожидался List.");
            int id = v.CollId;
            if ((uint)id >= (uint)_listCount || _lists[id] == null)
                throw new ScriptError("Список не существует.");
            return _lists[id];
        }

        private Dictionary<Variant, Variant> MapOf(Variant v)
        {
            if (v.Type != VariantType.Map) throw new ScriptError("Ожидался Map.");
            int id = v.CollId;
            if ((uint)id >= (uint)_mapCount || _maps[id] == null)
                throw new ScriptError("Map не существует.");
            return _maps[id];
        }
    }
}
