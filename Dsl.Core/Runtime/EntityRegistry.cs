using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Dsl.Runtime
{
    /// <summary>
    /// Мост «объект хоста ↔ Variant.Entity». Хэндл = (index, version):
    /// после Invalidate версия слота растёт, и все старые хэндлы протухают.
    /// Скрипт, обратившийся к протухшему хэндлу, получает ScriptError
    /// (гибнет его файбер), а не NullReference в движке.
    /// </summary>
    public sealed class EntityRegistry
    {
        private sealed class RefComparer : IEqualityComparer<object>
        {
            public static readonly RefComparer Instance = new RefComparer();
            bool IEqualityComparer<object>.Equals(object a, object b) => ReferenceEquals(a, b);
            int IEqualityComparer<object>.GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
        }

        private object[] _objs;
        private int[] _versions;
        private int _count;
        private readonly Stack<int> _free = new Stack<int>();
        private readonly Dictionary<object, int> _index = new Dictionary<object, int>(RefComparer.Instance);

        public EntityRegistry(int capacity = 256)
        {
            _objs = new object[capacity];
            _versions = new int[capacity];
        }

        /// <summary>Обернуть объект в хэндл (или вернуть уже существующий). null → Nil.</summary>
        public Variant Register(object o)
        {
            if (o == null) return Variant.Nil;
            if (_index.TryGetValue(o, out int existing))
                return Variant.Entity(existing, _versions[existing]);

            int idx;
            if (_free.Count > 0) idx = _free.Pop();
            else
            {
                if (_count >= _objs.Length)
                {
                    System.Array.Resize(ref _objs, _objs.Length * 2);
                    System.Array.Resize(ref _versions, _versions.Length * 2);
                }
                idx = _count++;
            }
            _objs[idx] = o;
            _index[o] = idx;
            return Variant.Entity(idx, _versions[idx]);
        }

        /// <summary>Хэндл живого объекта, если он зарегистрирован (для авто-detach подписок).</summary>
        public bool TryGetHandle(object o, out Variant v)
        {
            if (o != null && _index.TryGetValue(o, out int idx))
            {
                v = Variant.Entity(idx, _versions[idx]);
                return true;
            }
            v = Variant.Nil;
            return false;
        }

        /// <summary>Достать объект по хэндлу. Nil → null; протухший хэндл → ScriptError.</summary>
        public object Resolve(Variant v)
        {
            if (v.IsNil) return null;
            if (v.Type != VariantType.Entity)
                throw new ScriptError("Ожидалась сущность.");
            int idx = v.Index;
            if ((uint)idx >= (uint)_count || _versions[idx] != v.Version || _objs[idx] == null)
                throw new ScriptError("Обращение к уничтоженной сущности (протухший хэндл).");
            return _objs[idx];
        }

        public bool IsValid(Variant v)
        {
            if (v.Type != VariantType.Entity) return false;
            int idx = v.Index;
            return (uint)idx < (uint)_count && _versions[idx] == v.Version && _objs[idx] != null;
        }

        /// <summary>Хост сообщает: объект умер. Все выданные хэндлы протухают.</summary>
        public void Invalidate(object o)
        {
            if (o == null || !_index.TryGetValue(o, out int idx)) return;
            _index.Remove(o);
            _objs[idx] = null;
            _versions[idx]++;
            _free.Push(idx);
        }

        public void Clear()
        {
            for (int i = 0; i < _count; i++)
            {
                if (_objs[i] == null) continue;
                _objs[i] = null;
                _versions[i]++;
            }
            _index.Clear();
            _free.Clear();
            _count = 0;
        }
    }
}
