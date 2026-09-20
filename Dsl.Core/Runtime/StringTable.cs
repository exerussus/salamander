using System;
using System.Collections.Generic;
using System.Globalization;

namespace Dsl.Runtime
{
    /// <summary>
    /// Интернированные строки рантайма. Id стабильны (Variant хранит id).
    /// Два сегмента: статический (литералы программы, живут до перезагрузки)
    /// и динамический (строки, рождённые конкатенацией/хостом) — динамика
    /// подметается mark-and-sweep по требованию движка.
    ///
    /// Сборка строк идёт через общий char-буфер (grow-only): числа пишутся
    /// в него напрямую без ToString/боксинга; готовый span хэшируется и ищется
    /// в таблице БЕЗ создания managed-строки — новая string рождается только
    /// если такой строки ещё не было.
    /// </summary>
    public sealed class StringTable
    {
        private string[] _slots;
        private int _count;
        private readonly Stack<int> _free = new Stack<int>();
        private int _staticCount; // граница статического сегмента
        private int _liveDynamic; // сколько динамических строк ЖИВО сейчас

        // hash -> ПЕРВЫЙ id с этим хэшем; остальные — по цепочке _nextInBucket (коллизии
        // редки). Интрузивная цепочка вместо List<int> на каждый хэш: раньше на каждую
        // уникальную строку рождался List, а после свипа пустой List навсегда оставался
        // в словаре — на долгой сессии с $"hp: {x}" словарь рос без предела.
        private readonly Dictionary<int, int> _byHash = new Dictionary<int, int>();
        private int[] _nextInBucket;

        // scratch-буфер сборки
        private char[] _buf;
        private int _len;

        // пометки для свипа
        private bool[] _marks;

        public StringTable(int slotCapacity = 256, int scratchCapacity = 512)
        {
            _slots = new string[slotCapacity];
            _nextInBucket = new int[slotCapacity];
            _buf = new char[scratchCapacity];
        }

        public int Count => _count;
        /// <summary>
        /// Число ЖИВЫХ динамических строк. Раньше здесь стояло `_count - _staticCount`,
        /// а _count — отметка максимума, после свипа она не падает (id уходят во
        /// free-list). Один всплеск выше порога — и условие сборки в Tick оставалось
        /// истинным навсегда: полный mark-and-sweep на каждом кадре при нуле мусора.
        /// </summary>
        public int DynamicCount => _liveDynamic;
        /// <summary>Граница статического сегмента (id ниже — литералы программы, стабильны при том же отпечатке).</summary>
        public int StaticCount => _staticCount;

        /// <summary>Зафиксировать статический сегмент (после интернирования литералов).</summary>
        public void FreezeStatics() { _staticCount = _count; _liveDynamic = 0; }

        public string Get(int id) => (uint)id < (uint)_slots.Length ? _slots[id] : null;

        /// <summary>Интернировать готовую managed-строку (путь хоста; литералы при загрузке).</summary>
        public int Intern(string s)
        {
            if (s == null) return -1;
            int hash = HashOf(s, 0, s.Length);
            if (TryFind(hash, s, 0, s.Length, out int id)) return id;
            return AddSlot(s, hash);
        }

        // ===== сборка без аллокаций ========================================

        public void BeginBuild() => _len = 0;

        public void Append(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            Ensure(_len + s.Length);
            s.CopyTo(0, _buf, _len, s.Length);
            _len += s.Length;
        }

        public void AppendChar(char c)
        {
            Ensure(_len + 1);
            _buf[_len++] = c;
        }

        public void AppendBool(bool b) => Append(b ? "true" : "false");

        public void AppendInt(int v)
        {
            if (v == 0) { AppendChar('0'); return; }
            if (v == int.MinValue) { Append("-2147483648"); return; }
            if (v < 0) { AppendChar('-'); v = -v; }

            Ensure(_len + 11);
            int start = _len;
            while (v > 0)
            {
                _buf[_len++] = (char)('0' + v % 10);
                v /= 10;
            }
            // разворачиваем записанные задом наперёд цифры
            for (int i = start, j = _len - 1; i < j; i++, j--)
            {
                (_buf[i], _buf[j]) = (_buf[j], _buf[i]);
            }
        }

        /// <summary>
        /// float → символы без ToString: до 4 знаков после точки, хвостовые
        /// нули отрезаются. Для «неудобных» значений (NaN/Inf/очень большие)
        /// падаем на ToString — это редкий путь, аллокация допустима.
        /// </summary>
        public void AppendFloat(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f) || f >= 1e9f || f <= -1e9f)
            {
                Append(f.ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (f < 0) { AppendChar('-'); f = -f; }

            int whole = (int)f;
            float frac = f - whole;
            int scaled = (int)MathF.Round(frac * 10000f); // 4 знака
            if (scaled >= 10000)
            {
                // округление перекинуло разряд: 0.99999 → «1», а не «0.9999».
                // Раньше здесь стоял кламп до 9999, из-за чего лог показывал
                // не то число, которое лежит в переменной.
                whole++;
                scaled = 0;
            }

            AppendInt(whole);
            if (scaled <= 0) return;

            AppendChar('.');
            // ведущие нули дробной части
            int div = 1000;
            while (div > 0)
            {
                int digit = scaled / div;
                AppendDigit(digit);
                scaled -= digit * div;
                div /= 10;
                if (scaled == 0) break; // хвостовые нули не пишем
            }
        }

        /// <summary>double выводим полным invariant-представлением: тип берут ради
        /// точности, ручное усечение до 4 знаков (как у float) здесь во вред.</summary>
        public void AppendDouble(double d) => Append(d.ToString(CultureInfo.InvariantCulture));

        private void AppendDigit(int d)
        {
            Ensure(_len + 1);
            _buf[_len++] = (char)('0' + d);
        }

        /// <summary>Завершить сборку: найти или создать интернированную строку.</summary>
        public int EndBuildIntern()
        {
            int hash = HashOf(_buf, 0, _len);
            if (TryFindSpan(hash, _buf, _len, out int id)) return id;
            var s = new string(_buf, 0, _len); // строка рождается только здесь
            return AddSlot(s, hash);
        }

        private void Ensure(int need)
        {
            if (need <= _buf.Length) return;
            int cap = _buf.Length * 2;
            while (cap < need) cap *= 2;
            Array.Resize(ref _buf, cap);
        }

        // ===== внутреннее хранилище ========================================

        private int AddSlot(string s, int hash)
        {
            int id;
            if (_free.Count > 0)
            {
                id = _free.Pop();
            }
            else
            {
                if (_count >= _slots.Length)
                {
                    Array.Resize(ref _slots, _slots.Length * 2);
                    Array.Resize(ref _nextInBucket, _slots.Length);
                }
                id = _count;
            }
            _slots[id] = s;
            if (id >= _count) _count = id + 1;
            if (id >= _staticCount) _liveDynamic++;

            // новый id встаёт в ХВОСТ цепочки: порядок просмотра кандидатов тот же, что был у List
            _nextInBucket[id] = -1;
            if (_byHash.TryGetValue(hash, out int cur))
            {
                while (_nextInBucket[cur] >= 0) cur = _nextInBucket[cur];
                _nextInBucket[cur] = id;
            }
            else
            {
                _byHash[hash] = id;
            }
            return id;
        }

        private bool TryFind(int hash, string s, int offset, int len, out int id)
        {
            if (_byHash.TryGetValue(hash, out int cand))
            {
                for (; cand >= 0; cand = _nextInBucket[cand])
                {
                    var c = _slots[cand];
                    if (c != null && c.Length == len && string.CompareOrdinal(c, 0, s, offset, len) == 0)
                    {
                        id = cand;
                        return true;
                    }
                }
            }
            id = -1;
            return false;
        }

        private bool TryFindSpan(int hash, char[] buf, int len, out int id)
        {
            if (_byHash.TryGetValue(hash, out int cand))
            {
                for (; cand >= 0; cand = _nextInBucket[cand])
                {
                    var c = _slots[cand];
                    if (c == null || c.Length != len) continue;
                    bool eq = true;
                    for (int i = 0; i < len; i++)
                        if (c[i] != buf[i]) { eq = false; break; }
                    if (eq) { id = cand; return true; }
                }
            }
            id = -1;
            return false;
        }

        private static int HashOf(string s, int offset, int len)
        {
            unchecked
            {
                int h = 17;
                for (int i = 0; i < len; i++) h = h * 31 + s[offset + i];
                return h;
            }
        }

        private static int HashOf(char[] s, int offset, int len)
        {
            unchecked
            {
                int h = 17;
                for (int i = 0; i < len; i++) h = h * 31 + s[offset + i];
                return h;
            }
        }

        /// <summary>Убрать id из цепочки его хэша; опустевшая запись словаря удаляется.</summary>
        private void Unlink(int hash, int id)
        {
            if (!_byHash.TryGetValue(hash, out int head)) return;
            if (head == id)
            {
                int next = _nextInBucket[id];
                if (next >= 0) _byHash[hash] = next;
                else _byHash.Remove(hash);
            }
            else
            {
                int prev = head;
                while (prev >= 0 && _nextInBucket[prev] != id) prev = _nextInBucket[prev];
                if (prev >= 0) _nextInBucket[prev] = _nextInBucket[id];
            }
            _nextInBucket[id] = -1;
        }

        // ===== mark-and-sweep динамического сегмента =======================

        public void BeginSweep()
        {
            if (_marks == null || _marks.Length < _count) _marks = new bool[_slots.Length];
            Array.Clear(_marks, 0, _marks.Length);
        }

        public void Mark(int id)
        {
            if ((uint)id < (uint)_count) _marks[id] = true;
        }

        /// <summary>Убирает непомеченные динамические строки; id уходят во free-list.</summary>
        public int EndSweep()
        {
            int removed = 0;
            for (int id = _staticCount; id < _count; id++)
            {
                if (_slots[id] == null || _marks[id]) continue;

                int hash = HashOf(_slots[id], 0, _slots[id].Length);
                Unlink(hash, id);
                _slots[id] = null;
                _free.Push(id);
                _liveDynamic--;
                removed++;
            }
            return removed;
        }
    }
}
