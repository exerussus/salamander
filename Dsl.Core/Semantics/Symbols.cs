using System.Collections.Generic;
using Dsl.Runtime;
using Dsl.Syntax;

namespace Dsl.Semantics
{
    public abstract class Symbol
    {
        public string Name;
        public string Module;
    }

    public sealed class FieldSymbol
    {
        public string Name;
        public bool IsConst;
        public TypeRef Type;
        public int Slot = -1;        // для не-const: индекс статического слота
        public Variant ConstValue;   // для const (bool/int/float/enum)
        public string ConstStr;      // для const string (интернируется компилятором как литерал)
        public FieldMember Decl;
    }

    public sealed class EnumSymbol : Symbol
    {
        public int Id;
        public readonly Dictionary<string, int> Members = new Dictionary<string, int>();
        public EnumDecl Decl;
    }

    public sealed class ClassSymbol : Symbol
    {
        public ClassDecl Decl;                     // первый блок
        public readonly List<ClassDecl> Decls = new List<ClassDecl>();
        public readonly Dictionary<string, FieldSymbol> Fields = new Dictionary<string, FieldSymbol>();
        public readonly Dictionary<string, FuncMember> Funcs = new Dictionary<string, FuncMember>();   // победители
        public readonly List<FieldMember> FieldDecls = new List<FieldMember>();  // все объявления полей
        public readonly List<FuncMember> AllFuncDecls = new List<FuncMember>();  // все версии функций
    }

    public sealed class TriggerSymbol : Symbol
    {
        public TriggerDecl Decl;
        public int RuntimeId = -1;
        public bool StartDisabled;
        public readonly Dictionary<string, FieldSymbol> Fields = new Dictionary<string, FieldSymbol>();
        public readonly Dictionary<string, FuncMember> Funcs = new Dictionary<string, FuncMember>();
        public FuncMember Action;                 // безымянное действие Do (или null; поздний блок заменяет)
        public readonly List<FuncMember> Events = new List<FuncMember>();        // все версии (мерж later-wins)
        public readonly List<TriggerDecl> Decls = new List<TriggerDecl>();
        public readonly List<FieldMember> FieldDecls = new List<FieldMember>();
        public readonly List<FuncMember> AllFuncDecls = new List<FuncMember>();  // funcs + все версии action
    }

    public sealed class ListenerSymbol : Symbol
    {
        public ListenerDecl Decl;
        public int RuntimeId = -1;
        public TypeRef TargetType;   // тип цели (из 1-го параметра хостовых событий)
        public int FieldCount;       // размер блока полей одной подписки
        public readonly Dictionary<string, FieldSymbol> Fields = new Dictionary<string, FieldSymbol>();
        public readonly Dictionary<string, FuncMember> Funcs = new Dictionary<string, FuncMember>();
        public readonly List<FuncMember> Events = new List<FuncMember>();   // только хостовые
        public FuncMember OnSubscribe;    // опционально (поздний блок заменяет)
        public FuncMember OnUnsubscribe;  // опционально (без wait/spawn; поздний блок заменяет)
        public readonly List<ListenerDecl> Decls = new List<ListenerDecl>();
        public readonly List<FieldMember> FieldDecls = new List<FieldMember>();
        public readonly List<FuncMember> AllFuncDecls = new List<FuncMember>();  // funcs + вытесненные OnSub/OnUnsub
    }

    /// <summary>
    /// МЕРЖ-сущность архетипа: ВСЕ блоки с одним (вид, id) — хоть в одном файле,
    /// хоть в разных модулях — сливаются в одну сущность. Переопределение
    /// по-членно, поздний выигрывает: событие заменяет обработчик, поле — тот же
    /// статик-слот с поздним инициализатором, функция — позднюю версию видят все.
    /// Ранние обработчики видят итоговые поля/функции. Блок может состоять из
    /// одних полей (патч значений). Не живёт в глобальном пространстве имён:
    /// адресуется хостом по (вид, id).
    /// </summary>
    public sealed class ArchetypeSymbol
    {
        public string Kind;
        public int KindId = -1;
        public string Id;
        public string Module;                 // модуль ПЕРВОГО блока
        public ArchetypeDecl Decl;            // первый блок (для диагностик)
        public readonly List<ArchetypeDecl> Decls = new List<ArchetypeDecl>();
        public readonly Dictionary<string, FieldSymbol> Fields = new Dictionary<string, FieldSymbol>();
        public readonly Dictionary<string, FuncMember> Funcs = new Dictionary<string, FuncMember>();   // победители
        public readonly List<FuncMember> AllFuncDecls = new List<FuncMember>();  // все версии (для проверки/компиляции тел)
        public readonly List<FuncMember> Events = new List<FuncMember>();        // все, в порядке объявления (мерж later-wins)
        public readonly List<FieldMember> FieldDecls = new List<FieldMember>();  // все объявления полей (иниты по порядку)
    }

    public enum ResolveResult : byte { NotFound, Found, Ambiguous }

    /// <summary>
    /// Глобальные имена компилируемой программы. Видимость строго через
    /// зависимости: из модуля M простое имя разрешается среди {M} ∪ deps(M).
    /// Если имя объявлено в нескольких видимых модулях — требуется module::Name.
    /// </summary>
    public sealed class GlobalSymbols
    {
        private readonly Dictionary<string, Symbol> _qualified = new Dictionary<string, Symbol>();
        // имя -> все символы с этим именем (по одному на модуль)
        private readonly Dictionary<string, List<Symbol>> _byName = new Dictionary<string, List<Symbol>>();

        private static string Key(string module, string name) => module + "::" + name;

        public IEnumerable<Symbol> All => _qualified.Values;

        /// <summary>Добавить символ. false — имя уже занято в этом модуле.</summary>
        public bool Add(Symbol sym)
        {
            string qk = Key(sym.Module, sym.Name);
            if (_qualified.ContainsKey(qk)) return false;
            _qualified[qk] = sym;

            if (!_byName.TryGetValue(sym.Name, out var list))
            {
                list = new List<Symbol>();
                _byName[sym.Name] = list;
            }
            list.Add(sym);
            return true;
        }

        /// <summary>
        /// Разрешение простого имени из модуля с данным множеством видимых модулей.
        /// </summary>
        public ResolveResult ResolveSimple(string name, HashSet<string> visibleModules, out Symbol sym)
        {
            sym = null;
            if (!_byName.TryGetValue(name, out var list)) return ResolveResult.NotFound;
            foreach (var s in list)
            {
                if (!visibleModules.Contains(s.Module)) continue;
                if (sym != null) { sym = null; return ResolveResult.Ambiguous; }
                sym = s;
            }
            return sym != null ? ResolveResult.Found : ResolveResult.NotFound;
        }

        public bool TryResolveQualified(string module, string name, out Symbol sym)
        {
            return _qualified.TryGetValue(Key(module, name), out sym);
        }
    }
}
