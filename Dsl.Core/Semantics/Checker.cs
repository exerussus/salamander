using System.Collections.Generic;
using Dsl.Codegen;
using Dsl.Runtime;
using Dsl.Syntax;
using Dsl.Text;

namespace Dsl.Semantics
{
    /// <summary>AST одного модуля + множество видимых из него модулей.</summary>
    public sealed class ModuleAst
    {
        public string Name;
        public HashSet<string> Visible;   // сам модуль + прямые зависимости
        public List<ScriptFile> Files = new List<ScriptFile>();
        public bool Synchronous;          // из манифеста: запрещает wait/spawn
    }

    /// <summary>Результат семантики — всё, что нужно компилятору байткода.</summary>
    public sealed class CheckResult
    {
        public GlobalSymbols Globals;
        public List<FuncMember> Funcs;          // [0] = null (место под <init>)
        public List<TriggerSymbol> Triggers;    // индекс = runtime id триггера
        public List<ListenerSymbol> Listeners;  // индекс = runtime id listener
        public List<ArchetypeSymbol> Archetypes; // мерж-сущности в порядке первого появления
        public List<FieldSymbol> StaticInitOverrides; // поздние инициализаторы переопределённых полей (по порядку)
        public List<EnumSymbol> ScriptEnums;
        public List<FieldSymbol> StaticFields;  // индекс = static slot; порядок = порядок инициализации
        public HashSet<string> ClassNames;
        public List<string> ModuleNames;
    }

    /// <summary>
    /// Семантический анализ: два прохода. Первый собирает все объявления,
    /// раздаёт id/слоты и проверяет структурные правила (сигнатуры событий,
    /// непустые триггеры, ровно один action Do и т.д.). Второй типизирует тела,
    /// раскладывает локали по слотам и вставляет неявные int→float.
    /// </summary>
    public sealed class Checker
    {
        private readonly HostRegistry _host;
        private readonly DiagnosticBag _diag;

        private readonly GlobalSymbols _globals = new GlobalSymbols();
        private readonly List<FuncMember> _funcs = new List<FuncMember> { null }; // [0] = <init>
        private readonly List<TriggerSymbol> _triggers = new List<TriggerSymbol>();
        private readonly List<ListenerSymbol> _listeners = new List<ListenerSymbol>();
        private readonly List<ArchetypeSymbol> _archetypes = new List<ArchetypeSymbol>();
        private readonly Dictionary<string, ArchetypeSymbol> _archByKey = new Dictionary<string, ArchetypeSymbol>();
        private readonly List<FieldSymbol> _staticInitOverrides = new List<FieldSymbol>();
        private readonly Dictionary<string, TriggerSymbol> _trigByName = new Dictionary<string, TriggerSymbol>();
        private readonly Dictionary<string, ListenerSymbol> _lsnByName = new Dictionary<string, ListenerSymbol>();
        private readonly Dictionary<string, ClassSymbol> _clsByName = new Dictionary<string, ClassSymbol>();
        private readonly List<EnumSymbol> _scriptEnums = new List<EnumSymbol>();
        private readonly List<FieldSymbol> _staticFields = new List<FieldSymbol>();
        private readonly HashSet<string> _classNames = new HashSet<string>();
        private readonly List<string> _moduleNames = new List<string>();

        private static readonly HashSet<string> Reserved = new HashSet<string>
        {
            "Engine", "List", "Map", "Fiber",
            "int", "float", "bool", "string", "void", "var",
        };

        // ===== текущий контекст второго прохода =====
        private ModuleAst _module;
        private Dictionary<string, FieldSymbol> _ownerFields;
        private Dictionary<string, FuncMember> _ownerFuncs;
        private FuncMember _fn;
        private readonly List<Dictionary<string, LocalVar>> _scopes = new List<Dictionary<string, LocalVar>>();
        private int _localCount;
        private int _loopDepth;
        private bool _inInitializer;
        private ListenerSymbol _listener;   // контекст тела listener (self, attach-поля)
        private bool _noWaitHandler;        // тело OnUnsubscribe: wait/spawn запрещены

        private sealed class LocalVar
        {
            public int Slot;
            public TypeRef Type;
        }

        public Checker(HostRegistry host, DiagnosticBag diag)
        {
            _host = host;
            _diag = diag;
        }

        public CheckResult Check(List<ModuleAst> modules)
        {
            foreach (var m in modules)
            {
                _moduleNames.Add(m.Name);
                foreach (var f in m.Files)
                    foreach (var d in f.Decls)
                        d.Module = m.Name;
            }

            // проход 1: объявления
            foreach (var m in modules)
            {
                _module = m;
                foreach (var f in m.Files)
                    foreach (var d in f.Decls)
                        CollectDecl(d);
            }

            // проход 2: тела
            foreach (var m in modules)
            {
                _module = m;
                foreach (var f in m.Files)
                    foreach (var d in f.Decls)
                        CheckDeclBodies(d);
            }

            // мерж-сущность обязана иметь хотя бы одно событие; отдельный блок
            // может быть и чистым патчем полей — проверяем итог, а не блок
            foreach (var asym in _archetypes)
                if (asym.Events.Count == 0 && asym.Decls.Count > 0)
                    _diag.Error("E0199",
                        $"'{asym.Kind} {asym.Id}' не содержит ни одного события вида (ни в одном из блоков).",
                        asym.Decls[0].Pos);

            foreach (var tr in _triggers)
                if (tr.Events.Count == 0 && tr.Decls.Count > 0)
                    _diag.Error("E0109",
                        $"Триггер '{tr.Name}' не содержит ни одного обработчика event (ни в одном из блоков). " +
                        "Триггер обязан на что-то реагировать; если это набор функций/данных — объявите class.",
                        tr.Decls[0].Pos);
            foreach (var lsn in _listeners)
                if (lsn.Events.Count == 0 && lsn.Decls.Count > 0)
                    _diag.Error("E0171",
                        $"listener '{lsn.Name}' должен обрабатывать хотя бы одно хостовое событие " +
                        "(по его первому параметру выводится тип цели подписки).", lsn.Decls[0].Pos);

            return new CheckResult
            {
                Globals = _globals,
                Funcs = _funcs,
                Triggers = _triggers,
                Listeners = _listeners,
                Archetypes = _archetypes,
                StaticInitOverrides = _staticInitOverrides,
                ScriptEnums = _scriptEnums,
                StaticFields = _staticFields,
                ClassNames = _classNames,
                ModuleNames = _moduleNames,
            };
        }

        // ===================================================================
        // ПРОХОД 1: сбор объявлений
        // ===================================================================

        private void CollectDecl(Decl d)
        {
            if (Reserved.Contains(d.Name))
            {
                _diag.Error("E0100", $"Имя '{d.Name}' зарезервировано.", d.Pos);
                return;
            }
            if (_host.TryGetClass(d.Name, out _) || _host.TryGetApi(d.Name, out _) || _host.TryGetEnum(d.Name, out _))
            {
                _diag.Error("E0101", $"Имя '{d.Name}' уже занято хостом (класс/API/енум).", d.Pos);
                return;
            }

            switch (d)
            {
                case EnumDecl e: CollectEnum(e); break;
                case ClassDecl c: CollectClass(c); break;
                case TriggerDecl t: CollectTrigger(t); break;
                case ListenerDecl l: CollectListener(l); break;
                case ArchetypeDecl a: CollectArchetype(a); break;
            }
        }

        private void CollectEnum(EnumDecl e)
        {
            var sym = new EnumSymbol
            {
                Name = e.Name,
                Module = e.Module,
                Id = _host.EnumCount + _scriptEnums.Count,
                Decl = e,
            };
            for (int i = 0; i < e.Members.Count; i++)
            {
                if (sym.Members.ContainsKey(e.Members[i]))
                    _diag.Error("E0102", $"Повтор элемента '{e.Members[i]}' в енуме '{e.Name}'.", e.Pos);
                else
                    sym.Members[e.Members[i]] = i;
            }
            if (!_globals.Add(sym))
                _diag.Error("E0103", $"Имя '{e.Name}' уже объявлено в модуле '{e.Module}'.", e.Pos);
            else
                _scriptEnums.Add(sym);
        }

        private void CollectClass(ClassDecl c)
        {
            if (!_clsByName.TryGetValue(c.Name, out var sym))
            {
                sym = new ClassSymbol { Name = c.Name, Module = c.Module, Decl = c };
                if (!_globals.Add(sym))
                {
                    _diag.Error("E0104", $"Имя '{c.Name}' уже объявлено в модуле '{c.Module}'.", c.Pos);
                    return;
                }
                _clsByName[c.Name] = sym;
                _classNames.Add(c.Name);
            }
            sym.Decls.Add(c);

            var blockFields = new HashSet<string>();
            var blockFuncs = new HashSet<string>();
            foreach (var m in c.Members)
            {
                switch (m)
                {
                    case FieldMember f:
                        CollectMergedField(sym.Fields, sym.FieldDecls, f, blockFields);
                        break;
                    case FuncMember fn when fn.Kind == FuncKind.Event:
                        _diag.Error("E0105", "'event' разрешён только внутри trigger.", fn.Pos);
                        break;
                    case FuncMember fn when fn.Kind == FuncKind.Action:
                        _diag.Error("E0106", "'action' разрешён только внутри trigger.", fn.Pos);
                        break;
                    case FuncMember fn:
                        CollectMergedFunc(sym.Funcs, sym.AllFuncDecls, fn, c, blockFuncs);
                        break;
                }
            }
        }

        private void CollectTrigger(TriggerDecl t)
        {
            if (!_trigByName.TryGetValue(t.Name, out var sym))
            {
                sym = new TriggerSymbol
                {
                    Name = t.Name,
                    Module = t.Module,
                    Decl = t,
                    RuntimeId = _triggers.Count,
                    StartDisabled = t.StartDisabled,
                };
                if (!_globals.Add(sym))
                {
                    _diag.Error("E0107", $"Имя '{t.Name}' уже объявлено в модуле '{t.Module}'.", t.Pos);
                    return;
                }
                _trigByName[t.Name] = sym;
                _triggers.Add(sym);
            }
            else
            {
                sym.StartDisabled = t.StartDisabled; // поздний блок решает стартовый флаг
            }
            sym.Decls.Add(t);

            var blockFields = new HashSet<string>();
            var blockEvents = new HashSet<string>();
            var blockFuncs = new HashSet<string>();
            bool blockHasAction = false;

            foreach (var m in t.Members)
            {
                switch (m)
                {
                    case FieldMember f:
                        if (f.IsConst)
                            _diag.Error("E0108", "const внутри trigger не поддерживается — вынесите в class.", f.Pos);
                        else
                            CollectMergedField(sym.Fields, sym.FieldDecls, f, blockFields);
                        break;

                    case FuncMember fn when fn.Kind == FuncKind.Event:
                        CollectEvent(sym, fn, t, blockEvents);
                        break;

                    case FuncMember fn when fn.Kind == FuncKind.Action:
                        if (blockHasAction)
                        {
                            _diag.Error("E0118", $"Триггер '{t.Name}' уже содержит action Do.", fn.Pos);
                            break;
                        }
                        blockHasAction = true;
                        CollectAction(sym, fn, t);
                        break;

                    case FuncMember fn:
                        CollectMergedFunc(sym.Funcs, sym.AllFuncDecls, fn, t, blockFuncs);
                        break;
                }
            }
        }

        private void CollectArchetype(ArchetypeDecl a)
        {
            if (!_host.TryGetArchetypeKind(a.Kind, out var kind))
            {
                _diag.Error("E0198",
                    $"Игра не объявляет вид сущности '{a.Kind}'. Доступные виды перечислены в манифесте API.",
                    a.Pos);
                return; // без вида проверять события не по чему
            }

            // опциональная валидация id по списку известных игре (ловит опечатки)
            if (kind.KnownIds != null && kind.KnownIds.Count > 0 && !kind.KnownIds.Contains(a.Name))
                _diag.Error("E0202",
                    $"У игры нет сущности вида '{a.Kind}' с id '{a.Name}' (проверьте манифест контента).",
                    a.Pos);

            // все блоки одного (вид, id) сливаются в ОДНУ сущность: переопределение
            // по-членно, поздний выигрывает; дубли ловим только ВНУТРИ блока
            string key = kind.Id + ":" + a.Name;
            if (!_archByKey.TryGetValue(key, out var sym))
            {
                sym = new ArchetypeSymbol { Kind = a.Kind, Id = a.Name, Module = a.Module, Decl = a, KindId = kind.Id };
                _archByKey[key] = sym;
                _archetypes.Add(sym); // порядок первого появления
            }
            sym.Decls.Add(a);

            var blockFields = new HashSet<string>();
            var blockEvents = new HashSet<string>();
            var blockFuncs = new HashSet<string>();

            foreach (var m in a.Members)
            {
                switch (m)
                {
                    case FieldMember f:
                        if (f.IsConst)
                            _diag.Error("E0205", "const внутри блока-архетипа не поддерживается — вынесите в class.", f.Pos);
                        else
                            CollectMergedField(sym.Fields, sym.FieldDecls, f, blockFields);
                        break;

                    case FuncMember fn when fn.Kind == FuncKind.Event:
                        CollectArchetypeEvent(sym, kind, fn, a, blockEvents);
                        break;

                    case FuncMember fn when fn.Kind == FuncKind.Action:
                        _diag.Error("E0204", "Блок-архетип не имеет action — события поднимает игра адресно.", fn.Pos);
                        break;

                    case FuncMember fn:
                        CollectMergedFunc(sym.Funcs, sym.AllFuncDecls, fn, a, blockFuncs);
                        break;
                }
            }
        }



        private void CollectArchetypeEvent(ArchetypeSymbol sym, ArchetypeKindInfo kind, FuncMember fn, ArchetypeDecl a, HashSet<string> blockNames)
        {
            fn.Owner = a;
            fn.ReturnType = TypeRef.Void;
            foreach (var p in fn.Params) p.Type = ResolveType(p.DeclType);

            if (!kind.EventByName.TryGetValue(fn.Name, out var ev))
            {
                _diag.Error("E0199",
                    $"У вида '{kind.Name}' нет события '{fn.Name}'. События вида перечислены в манифесте API.",
                    fn.Pos);
                return;
            }
            if (!blockNames.Add(fn.Name))
            {
                _diag.Error("E0113", $"Повторное объявление '{fn.Name}' в этом блоке.", fn.Pos);
                return;
            }
            // одноимённое событие в ДРУГОМ блоке — переопределение (later-wins в мерже)
            if (ev.Params.Length != fn.Params.Count)
            {
                _diag.Error("E0200",
                    $"Событие '{fn.Name}' вида '{kind.Name}' имеет {ev.Params.Length} параметров, в обработчике {fn.Params.Count}.",
                    fn.Pos);
                return;
            }
            for (int i = 0; i < ev.Params.Length; i++)
            {
                if (!ev.Params[i].Same(fn.Params[i].Type))
                    _diag.Error("E0201",
                        $"Параметр #{i + 1} обработчика '{fn.Name}' имеет тип {fn.Params[i].Type}, у события вида — {ev.Params[i]}.",
                        fn.Params[i].Pos);
            }

            fn.EventId = ev.LocalId; // локальный id внутри вида
            fn.FuncIndex = _funcs.Count;
            _funcs.Add(fn);
            sym.Events.Add(fn);
        }

        private void CollectListener(ListenerDecl l)
        {
            if (!_lsnByName.TryGetValue(l.Name, out var sym))
            {
                sym = new ListenerSymbol
                {
                    Name = l.Name,
                    Module = l.Module,
                    Decl = l,
                    RuntimeId = _listeners.Count,
                };
                if (!_globals.Add(sym))
                {
                    _diag.Error("E0107", $"Имя '{l.Name}' уже объявлено в модуле '{l.Module}'.", l.Pos);
                    return;
                }
                _lsnByName[l.Name] = sym;
                _listeners.Add(sym);
            }
            sym.Decls.Add(l);

            var blockFields = new HashSet<string>();
            var blockEvents = new HashSet<string>();
            var blockFuncs = new HashSet<string>();

            foreach (var m in l.Members)
            {
                switch (m)
                {
                    case FieldMember f:
                        if (f.IsConst)
                            _diag.Error("E0108", "const внутри listener не поддерживается — вынесите в class.", f.Pos);
                        else
                            CollectListenerField(sym, f, blockFields);
                        break;

                    case FuncMember fn when fn.Kind == FuncKind.Event:
                        CollectListenerEvent(sym, fn, l, blockEvents);
                        break;

                    case FuncMember fn when fn.Kind == FuncKind.Action:
                        _diag.Error("E0176",
                            "listener не имеет action — точкой входа служит подписка (Engine.Attach).", fn.Pos);
                        break;

                    case FuncMember fn:
                        CollectMergedFunc(sym.Funcs, sym.AllFuncDecls, fn, l, blockFuncs);
                        break;
                }
            }

        }

        private void CollectListenerField(ListenerSymbol sym, FieldMember f, HashSet<string> blockNames)
        {
            if (!blockNames.Add(f.Name))
            {
                _diag.Error("E0111", $"Повторное объявление поля '{f.Name}'.", f.Pos);
                return;
            }
            var type = ResolveType(f.DeclType);
            f.Type = type;

            if (sym.Fields.TryGetValue(f.Name, out var existing))
            {
                // переопределение из другого блока: ТОТ ЖЕ слот подписки;
                // init-чанк сброса собирается по итоговым Decl → поздний инициализатор побеждает
                if (!existing.Type.Same(type))
                {
                    _diag.Error("E0207",
                        $"Переопределение поля '{f.Name}' с другим типом: было {existing.Type}, стало {type}.", f.Pos);
                    return;
                }
                if (f.Init != null)
                    sym.Fields[f.Name] = new FieldSymbol { Name = f.Name, IsConst = false, Type = type, Decl = f, Slot = existing.Slot };
                sym.FieldDecls.Add(f);
                return;
            }

            // слот в блоке ПОДПИСКИ (не статик): у каждой подписки свой массив полей
            var fs = new FieldSymbol { Name = f.Name, IsConst = false, Type = type, Decl = f, Slot = sym.FieldCount++ };
            sym.Fields[f.Name] = fs;
            sym.FieldDecls.Add(f);
        }

        private void CollectListenerEvent(ListenerSymbol sym, FuncMember fn, ListenerDecl l, HashSet<string> blockNames)
        {
            if (!blockNames.Add(fn.Name))
            {
                _diag.Error("E0113", $"Повторное объявление '{fn.Name}' в этом блоке.", fn.Pos);
                return;
            }
            fn.Owner = l;
            fn.ReturnType = TypeRef.Void;
            foreach (var p in fn.Params) p.Type = ResolveType(p.DeclType);

            // OnSubscribe/OnUnsubscribe — служебные, не хостовые
            if (fn.Name == "OnSubscribe" || fn.Name == "OnUnsubscribe")
            {
                if (fn.Params.Count > 0)
                    _diag.Error("E0172", $"'{fn.Name}' не принимает параметров.", fn.Pos);
                fn.FuncIndex = _funcs.Count;
                _funcs.Add(fn);
                sym.AllFuncDecls.Add(fn); // вытесненная версия тоже проверяется и компилируется
                if (fn.Name == "OnSubscribe") sym.OnSubscribe = fn;
                else sym.OnUnsubscribe = fn; // поздний блок заменяет
                return;
            }

            if (!_host.TryGetEvent(fn.Name, out var ev))
            {
                _diag.Error("E0114", $"Событие '{fn.Name}' не зарегистрировано хостом.", fn.Pos);
                return;
            }
            if (ev.Params.Length != fn.Params.Count)
            {
                _diag.Error("E0115",
                    $"Событие '{fn.Name}' объявлено хостом с {ev.Params.Length} параметрами, в обработчике {fn.Params.Count}.",
                    fn.Pos);
                return;
            }
            for (int i = 0; i < ev.Params.Length; i++)
            {
                if (!ev.Params[i].Same(fn.Params[i].Type))
                {
                    _diag.Error("E0116",
                        $"Параметр #{i + 1} обработчика '{fn.Name}' имеет тип {fn.Params[i].Type}, у события — {ev.Params[i]}.",
                        fn.Params[i].Pos);
                }
            }

            // субъект подписки — ВСЕГДА первый параметр, и он должен быть сущностью
            if (ev.Params.Length == 0 || ev.Params[0].Kind != TypeKind.Entity)
            {
                _diag.Error("E0173",
                    $"Событие '{fn.Name}' не подходит для listener: первый параметр должен быть сущностью " +
                    "(субъект подписки). Хост объявляет отдельные события под каждую роль " +
                    "(OnUnitDamageTaken(target,...) / OnUnitDealtDamage(source,...)).", fn.Pos);
                return;
            }
            var target = ev.Params[0];
            if (sym.TargetType == null) sym.TargetType = target;
            else if (!sym.TargetType.Same(target))
            {
                _diag.Error("E0174",
                    $"listener '{sym.Name}': события про разные типы цели ({sym.TargetType} и {target}) — " +
                    "разделите на два listener.", fn.Pos);
                return;
            }

            fn.EventId = ev.Id;
            fn.FuncIndex = _funcs.Count;
            _funcs.Add(fn);
            sym.Events.Add(fn);
        }

        // ===================================================================
        // Мерж-семантика: блоки с одним именем (trigger/class/listener) или
        // одним (вид, id) у архетипов — ОДНА сущность; члены переопределяются
        // по-имённо, поздний выигрывает, дубли ловим только внутри блока
        // ===================================================================

        private void CollectMergedField(Dictionary<string, FieldSymbol> fields, List<FieldMember> fieldDecls,
                                        FieldMember f, HashSet<string> blockNames)
        {
            if (!blockNames.Add(f.Name))
            {
                _diag.Error("E0111", $"Повторное объявление поля '{f.Name}'.", f.Pos);
                return;
            }
            var type = ResolveType(f.DeclType);
            f.Type = type;

            if (fields.TryGetValue(f.Name, out var existing))
            {
                // переопределение из другого блока
                if (existing.IsConst != f.IsConst)
                {
                    _diag.Error("E0207", $"Переопределение '{f.Name}': нельзя менять const и обычное поле местами.", f.Pos);
                    return;
                }
                if (!existing.Type.Same(type))
                {
                    _diag.Error("E0207",
                        $"Переопределение поля '{f.Name}' с другим типом: было {existing.Type}, стало {type}.", f.Pos);
                    return;
                }
                if (f.IsConst)
                {
                    // позднее значение константы видят ВСЕ использования:
                    // тела проверяются после полного сбора деклараций
                    var ns = new FieldSymbol { Name = f.Name, IsConst = true, Type = type, Decl = f };
                    FoldConst(f, ns);
                    fields[f.Name] = ns;
                }
                else
                {
                    // тот же статик-слот; поздний инициализатор перезапишет в <init>
                    f.StaticSlot = existing.Slot;
                    if (f.Init != null)
                        _staticInitOverrides.Add(new FieldSymbol { Name = f.Name, Type = type, Decl = f, Slot = existing.Slot });
                }
                fieldDecls.Add(f);
                return;
            }

            var nsym = new FieldSymbol { Name = f.Name, IsConst = f.IsConst, Type = type, Decl = f };
            if (f.IsConst)
            {
                FoldConst(f, nsym);
            }
            else
            {
                nsym.Slot = _staticFields.Count;
                f.StaticSlot = nsym.Slot;
                _staticFields.Add(nsym);
            }
            fields[f.Name] = nsym;
            fieldDecls.Add(f);
        }

        private void CollectMergedFunc(Dictionary<string, FuncMember> funcs, List<FuncMember> all,
                                       FuncMember fn, Decl owner, HashSet<string> blockNames)
        {
            if (!blockNames.Add(fn.Name))
            {
                _diag.Error("E0113", $"Повторное объявление функции '{fn.Name}'.", fn.Pos);
                return;
            }
            fn.Owner = owner;
            fn.ReturnType = fn.RetType == null ? TypeRef.Void : ResolveType(fn.RetType);
            foreach (var p in fn.Params) p.Type = ResolveType(p.DeclType);
            fn.FuncIndex = _funcs.Count;
            _funcs.Add(fn);
            funcs[fn.Name] = fn;   // поздний блок заменяет — все вызовы вяжутся на итог
            all.Add(fn);           // вытесненные версии тоже проверяются и компилируются
        }

        private void CheckMergedInits(List<FieldMember> decls)
        {
            foreach (var fm in decls)
            {
                if (fm.IsConst || fm.Init == null) continue;
                _inInitializer = true;
                var ti = CheckExpr(ref fm.Init);
                _inInitializer = false;
                CoerceAssign(ref fm.Init, fm.Type, ti, fm.Pos, "инициализатор поля");
            }
        }


        /// <summary>const: только литерал или элемент енума (v1).</summary>
        private void FoldConst(FieldMember f, FieldSymbol sym)
        {
            if (f.Init is LiteralExpr lit)
            {
                switch (lit.LKind)
                {
                    case LiteralKind.Bool when f.Type.Kind == TypeKind.Bool:
                        sym.ConstValue = Variant.Bool(lit.BoolValue); return;
                    case LiteralKind.Int when f.Type.Kind == TypeKind.Int:
                        sym.ConstValue = Variant.Int((int)lit.IntValue); return;
                    case LiteralKind.Int when f.Type.Kind == TypeKind.Float:
                        sym.ConstValue = Variant.Float(lit.IntValue); return;
                    case LiteralKind.Float when f.Type.Kind == TypeKind.Float:
                        sym.ConstValue = Variant.Float((float)lit.FloatValue); return;
                    case LiteralKind.Str when f.Type.Kind == TypeKind.Str:
                        sym.ConstStr = lit.StrValue; return;
                }
            }
            if (f.Init is MemberExpr me && me.Target is IdentExpr te && f.Type.Kind == TypeKind.Enum)
            {
                if (TryResolveEnumType(te.Name, out int enumId, out var members)
                    && enumId == f.Type.EnumId && members.TryGetValue(me.Name, out int val))
                {
                    sym.ConstValue = Variant.Enum(enumId, val);
                    return;
                }
            }
            _diag.Error("E0112",
                "const в v1 принимает только литерал или элемент енума соответствующего типа.", f.Pos);
        }


        private void CollectEvent(TriggerSymbol sym, FuncMember fn, TriggerDecl t, HashSet<string> blockNames)
        {
            if (!blockNames.Add(fn.Name))
            {
                _diag.Error("E0113", $"Повторное объявление обработчика '{fn.Name}' в этом блоке.", fn.Pos);
                return;
            }
            // одноимённый обработчик в ДРУГОМ блоке — переопределение (later-wins в мерже)
            fn.Owner = t;
            fn.ReturnType = TypeRef.Void;
            foreach (var p in fn.Params) p.Type = ResolveType(p.DeclType);

            if (!_host.TryGetEvent(fn.Name, out var ev))
            {
                _diag.Error("E0114", $"Событие '{fn.Name}' не зарегистрировано хостом.", fn.Pos);
                return;
            }
            if (ev.Params.Length != fn.Params.Count)
            {
                _diag.Error("E0115",
                    $"Событие '{fn.Name}' объявлено хостом с {ev.Params.Length} параметрами, в обработчике {fn.Params.Count}.",
                    fn.Pos);
                return;
            }
            for (int i = 0; i < ev.Params.Length; i++)
            {
                if (!ev.Params[i].Same(fn.Params[i].Type))
                {
                    _diag.Error("E0116",
                        $"Параметр #{i + 1} обработчика '{fn.Name}' имеет тип {fn.Params[i].Type}, у события — {ev.Params[i]}.",
                        fn.Params[i].Pos);
                }
            }

            fn.EventId = ev.Id;
            fn.FuncIndex = _funcs.Count;
            _funcs.Add(fn);
            sym.Events.Add(fn);
        }

        private void CollectAction(TriggerSymbol sym, FuncMember fn, TriggerDecl t)
        {
            fn.Owner = t;
            fn.ReturnType = TypeRef.Void;
            if (fn.Name != "Do")
                _diag.Error("E0117", "В v1 у триггера ровно одно действие и оно называется 'Do'.", fn.Pos);
            if (fn.Params.Count > 0)
                _diag.Error("E0119", "action Do() не принимает параметров.", fn.Pos);
            if (fn.RetType != null)
                _diag.Error("E0120", "action Do() не возвращает значение.", fn.Pos);

            fn.FuncIndex = _funcs.Count;
            _funcs.Add(fn);
            sym.AllFuncDecls.Add(fn);   // вытесненные версии тоже компилируются
            sym.Action = fn;            // поздний блок заменяет
        }

        // ===================================================================
        // Типы
        // ===================================================================

        private TypeRef ResolveType(TypeSyntax ts)
        {
            switch (ts)
            {
                case null:
                    return TypeRef.Error;

                case NameType n:
                    switch (n.Name)
                    {
                        case "bool": return TypeRef.Bool;
                        case "int": return TypeRef.Int;
                        case "float": return TypeRef.Float;
                        case "string": return TypeRef.Str;
                        case "Fiber": return TypeRef.Fiber;
                        case "Subscription": return TypeRef.Subscription;
                    }
                    if (_host.TryGetClass(n.Name, out var hc)) return TypeRef.Entity(hc.Id);
                    if (TryResolveEnumType(n.Name, out int eid, out _)) return TypeRef.EnumOf(eid);
                    _diag.Error("E0121", $"Неизвестный тип '{n.Name}'.", n.Pos);
                    return TypeRef.Error;

                case ArrayTypeSyntax a:
                    return TypeRef.ArrayOf(ResolveType(a.Elem));

                case GenericTypeSyntax g when g.Name == "List" && g.Args.Count == 1:
                    return TypeRef.ListOf(ResolveType(g.Args[0]));

                case GenericTypeSyntax g when g.Name == "Map" && g.Args.Count == 2:
                {
                    var k = ResolveType(g.Args[0]);
                    if (!(k.Kind == TypeKind.Int || k.Kind == TypeKind.Str
                          || k.Kind == TypeKind.Enum || k.Kind == TypeKind.Entity))
                        _diag.Error("E0122", "Ключ Map: int, string, енум или сущность.", g.Pos);
                    return TypeRef.MapOf(k, ResolveType(g.Args[1]));
                }

                case GenericTypeSyntax g:
                    _diag.Error("E0123", $"Неизвестный обобщённый тип '{g.Name}<...>'.", g.Pos);
                    return TypeRef.Error;

                default:
                    return TypeRef.Error;
            }
        }

        /// <summary>Ищет енум по имени: сперва хостовые, потом скриптовые (по видимости).</summary>
        private bool TryResolveEnumType(string name, out int enumId, out Dictionary<string, int> members)
        {
            if (_host.TryGetEnum(name, out var he))
            {
                enumId = he.Id;
                members = he.Members;
                return true;
            }
            if (_globals.ResolveSimple(name, _module.Visible, out var s) == ResolveResult.Found
                && s is EnumSymbol es)
            {
                enumId = es.Id;
                members = es.Members;
                return true;
            }
            enumId = -1;
            members = null;
            return false;
        }

        // ===================================================================
        // ПРОХОД 2: тела
        // ===================================================================

        private void CheckDeclBodies(Decl d)
        {
            switch (d)
            {
                case ClassDecl c:
                {
                    // мерж-сущность: тела один раз (на первом блоке), против итоговых таблиц
                    if (_clsByName.TryGetValue(c.Name, out var cs) && cs.Decls.Count > 0 && cs.Decls[0] == c)
                    {
                        _ownerFields = cs.Fields;
                        _ownerFuncs = cs.Funcs;
                        CheckMergedInits(cs.FieldDecls);
                        foreach (var fn in cs.AllFuncDecls) CheckFuncBody(fn);
                    }
                    break;
                }
                case TriggerDecl t:
                {
                    if (_trigByName.TryGetValue(t.Name, out var tr) && tr.Decls.Count > 0 && tr.Decls[0] == t)
                    {
                        _ownerFields = tr.Fields;
                        _ownerFuncs = tr.Funcs;
                        CheckMergedInits(tr.FieldDecls);
                        foreach (var fn in tr.AllFuncDecls) CheckFuncBody(fn); // funcs + все версии action
                        foreach (var ev in tr.Events) CheckFuncBody(ev);       // все версии обработчиков
                    }
                    break;
                }
                case ArchetypeDecl a:
                {
                    // блоки одного (вид, id) — одна мерж-сущность; тела проверяем ОДИН
                    // раз (на первом блоке), когда все члены уже собраны: ранние
                    // обработчики видят поля/функции, объявленные в поздних блоках
                    foreach (var asym in _archetypes)
                    {
                        if (asym.Decls.Count == 0 || asym.Decls[0] != a) continue;
                        _ownerFields = asym.Fields;
                        _ownerFuncs = asym.Funcs;
                        CheckMergedInits(asym.FieldDecls);
                        foreach (var fn in asym.AllFuncDecls) CheckFuncBody(fn); // и вытесненные — они компилируются
                        foreach (var ev in asym.Events) CheckFuncBody(ev);
                        break;
                    }
                    break;
                }
                case ListenerDecl l:
                {
                    if (_lsnByName.TryGetValue(l.Name, out var ls) && ls.Decls.Count > 0 && ls.Decls[0] == l)
                    {
                        _listener = ls; // включает self и attach-адресацию полей
                        _ownerFields = ls.Fields;
                        _ownerFuncs = ls.Funcs;
                        CheckMergedInits(ls.FieldDecls);
                        foreach (var fn in ls.AllFuncDecls)
                        {
                            // блок полей уходит в пул сразу после OnUnsubscribe —
                            // wait/spawn в нём запрещены (в любой версии)
                            _noWaitHandler = fn.Kind == FuncKind.Event && fn.Name == "OnUnsubscribe";
                            CheckFuncBody(fn);
                        }
                        _noWaitHandler = false;
                        foreach (var ev in ls.Events) CheckFuncBody(ev);
                        _listener = null;
                    }
                    break;
                }
            }
        }


        private void CheckFuncBody(FuncMember fn)
        {
            _fn = fn;
            _scopes.Clear();
            _localCount = 0;
            _loopDepth = 0;

            PushScope();
            foreach (var p in fn.Params)
            {
                p.Slot = _localCount++;
                if (!DeclareLocal(p.Name, p.Slot, p.Type))
                    _diag.Error("E0130", $"Повтор имени параметра '{p.Name}'.", p.Pos);
            }

            if (fn.Body == null)
            {
                // прототип «...(...);» — пустая реализация; для не-void непонятно,
                // что возвращать, поэтому только void (события, action, void-func)
                if (fn.ReturnType.Kind != TypeKind.Void)
                    _diag.Error("E0203",
                        $"У '{fn.Name}' есть возвращаемый тип {fn.ReturnType} — прототип без тела допустим только для void.",
                        fn.Pos);
            }
            else
                CheckBlock(fn.Body);
            PopScope();

            fn.LocalCount = _localCount;
        }

        // ===== области видимости локалей =====

        private void PushScope() => _scopes.Add(new Dictionary<string, LocalVar>());
        private void PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

        private bool DeclareLocal(string name, int slot, TypeRef type)
        {
            // строгий режим: затенение запрещено — ищем во всех объемлющих
            for (int i = 0; i < _scopes.Count; i++)
                if (_scopes[i].ContainsKey(name)) return false;
            _scopes[_scopes.Count - 1][name] = new LocalVar { Slot = slot, Type = type };
            return true;
        }

        private LocalVar FindLocal(string name)
        {
            for (int i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i].TryGetValue(name, out var v)) return v;
            return null;
        }

        // ===== стейтменты =====

        private void CheckBlock(Block b)
        {
            PushScope();
            foreach (var s in b.Stmts) CheckStmt(s);
            PopScope();
        }

        private void CheckStmt(Stmt s)
        {
            switch (s)
            {
                case Block b: CheckBlock(b); break;

                case VarDeclStmt v:
                {
                    TypeRef declared = v.DeclType != null ? ResolveType(v.DeclType) : null;
                    TypeRef initT = null;
                    if (v.Init != null) initT = CheckExpr(ref v.Init);

                    if (declared == null)
                    {
                        // var: вывод из инициализатора
                        if (initT == null || initT.Kind == TypeKind.Nil || initT.Kind == TypeKind.Void)
                        {
                            _diag.Error("E0131", "Не удаётся вывести тип 'var' из этого инициализатора.", v.Pos);
                            initT = TypeRef.Error;
                        }
                        v.Type = initT;
                    }
                    else
                    {
                        v.Type = declared;
                        if (v.Init != null)
                            CoerceAssign(ref v.Init, declared, initT, v.Pos, "инициализатор");
                    }

                    v.Slot = _localCount++;
                    if (!DeclareLocal(v.Name, v.Slot, v.Type))
                        _diag.Error("E0132", $"Переменная '{v.Name}' уже объявлена (затенение запрещено).", v.Pos);
                    break;
                }

                case AssignStmt a: CheckAssign(a); break;

                case ExprStmt e:
                {
                    CheckExpr(ref e.Expr);
                    break;
                }

                case IfStmt i:
                {
                    var t = CheckExpr(ref i.Cond);
                    RequireBool(t, i.Cond.Pos, "условие if");
                    CheckBlock(i.Then);
                    if (i.Else != null) CheckStmt(i.Else);
                    break;
                }

                case WhileStmt w:
                {
                    var t = CheckExpr(ref w.Cond);
                    RequireBool(t, w.Cond.Pos, "условие while");
                    _loopDepth++;
                    CheckBlock(w.Body);
                    _loopDepth--;
                    break;
                }

                case ForRangeStmt fr:
                {
                    var ft = CheckExpr(ref fr.From);
                    var tt = CheckExpr(ref fr.To);
                    if (ft.Kind != TypeKind.Int) _diag.Error("E0133", "Нижняя граница диапазона должна быть int.", fr.From.Pos);
                    if (tt.Kind != TypeKind.Int) _diag.Error("E0134", "Верхняя граница диапазона должна быть int.", fr.To.Pos);

                    fr.VarSlot = _localCount++;
                    fr.LimitSlot = _localCount++; // скрытый кэш верхней границы

                    PushScope();
                    if (!DeclareLocal(fr.Var, fr.VarSlot, TypeRef.Int))
                        _diag.Error("E0135", $"Переменная '{fr.Var}' уже объявлена.", fr.Pos);
                    _loopDepth++;
                    CheckBlock(fr.Body);
                    _loopDepth--;
                    PopScope();
                    break;
                }

                case ForEachStmt fe:
                {
                    var ct = CheckExpr(ref fe.Coll);
                    TypeRef elem = TypeRef.Error;
                    TypeRef elem2 = TypeRef.Error;
                    if (ct.Kind == TypeKind.Array || ct.Kind == TypeKind.List)
                    {
                        elem = ct.Elem;
                        if (fe.Var2 != null)
                            _diag.Error("E0206",
                                "Вторая переменная цикла допустима только для Map (ключ, значение).", fe.Pos);
                    }
                    else if (ct.Kind == TypeKind.Map)
                    {
                        // снапшот КЛЮЧЕЙ на входе; удалённые в теле — пропускаются;
                        // значения (вторая переменная) читаются живыми через map[k]
                        fe.IsMap = true;
                        elem = ct.Key;
                        elem2 = ct.Val;
                    }
                    else if (!ct.IsError)
                        _diag.Error("E0136", "for-in обходит массивы, List и Map.", fe.Coll.Pos);

                    fe.ElemType = elem;
                    fe.Elem2Type = elem2;
                    fe.VarSlot = _localCount++;
                    if (fe.Var2 != null) fe.Var2Slot = _localCount++;
                    // скрытая тройка ПОДРЯД — VM адресует базой IndexSlot
                    fe.IndexSlot = _localCount++;
                    fe.CollSlot = _localCount++;
                    fe.BufSlot = _localCount++;

                    PushScope();
                    if (!DeclareLocal(fe.Var, fe.VarSlot, elem))
                        _diag.Error("E0137", $"Переменная '{fe.Var}' уже объявлена.", fe.Pos);
                    if (fe.Var2 != null && !DeclareLocal(fe.Var2, fe.Var2Slot, elem2))
                        _diag.Error("E0137", $"Переменная '{fe.Var2}' уже объявлена.", fe.Pos);
                    _loopDepth++;
                    CheckBlock(fe.Body);
                    _loopDepth--;
                    PopScope();
                    break;
                }

                case BreakStmt br:
                    if (_loopDepth == 0) _diag.Error("E0138", "'break' вне цикла.", br.Pos);
                    break;

                case ContinueStmt co:
                    if (_loopDepth == 0) _diag.Error("E0139", "'continue' вне цикла.", co.Pos);
                    break;

                case ReturnStmt r:
                {
                    var expected = _fn.ReturnType;
                    if (r.Value == null)
                    {
                        if (expected.Kind != TypeKind.Void)
                            _diag.Error("E0140", $"Функция возвращает {expected}, а return пуст.", r.Pos);
                    }
                    else
                    {
                        var t = CheckExpr(ref r.Value);
                        if (expected.Kind == TypeKind.Void)
                            _diag.Error("E0141", "Функция ничего не возвращает, а return со значением.", r.Pos);
                        else
                            CoerceAssign(ref r.Value, expected, t, r.Pos, "return");
                    }
                    break;
                }

                case PassStmt _:
                    break; // осознанно пусто

                case WaitStmt w:
                {
                    if (_module != null && _module.Synchronous)
                        _diag.Error("E0170", "'wait' запрещён в синхронном модуле (execution: synchronous). " +
                                             "Обработчик должен исполняться целиком до конца.", w.Pos);
                    if (_noWaitHandler)
                        _diag.Error("E0178", "'wait' запрещён в OnUnsubscribe — подписка уничтожается немедленно.", w.Pos);
                    var t = CheckExpr(ref w.Seconds);
                    if (!t.IsNumeric && !t.IsError)
                        _diag.Error("E0142", "'wait' ожидает число секунд.", w.Seconds.Pos);
                    if (t.Kind == TypeKind.Int)
                        w.Seconds = Convert(w.Seconds); // секунды всегда float
                    break;
                }

                case WaitUntilStmt wu:
                {
                    if (_module != null && _module.Synchronous)
                        _diag.Error("E0170", "'wait until' запрещён в синхронном модуле (execution: synchronous).", wu.Pos);
                    if (_noWaitHandler)
                        _diag.Error("E0178", "'wait until' запрещён в OnUnsubscribe.", wu.Pos);
                    var t = CheckExpr(ref wu.Cond);
                    RequireBool(t, wu.Cond.Pos, "условие 'wait until'");
                    break;
                }
            }
        }

        private void CheckAssign(AssignStmt a)
        {
            // составное присваивание: десахарим в обычное — target op= v → target = target op v.
            // ВНИМАНИЕ: подвыражения цели вычисляются дважды (для чтения и записи);
            // побочные эффекты в индексах/цепочках свойств отработают два раза.
            if (a.Op != TokenKind.Assign)
            {
                var binOp = a.Op switch
                {
                    TokenKind.PlusAssign => TokenKind.Plus,
                    TokenKind.MinusAssign => TokenKind.Minus,
                    TokenKind.StarAssign => TokenKind.Star,
                    _ => TokenKind.Slash,
                };
                a.Value = new BinaryExpr { Left = a.Target, Op = binOp, Right = a.Value, Pos = a.Pos };
                a.Op = TokenKind.Assign;
            }

            var vt = CheckExpr(ref a.Value);
            var tt = CheckLValue(a.Target);
            var val = a.Value;
            CoerceAssign(ref val, tt, vt, a.Pos, "присваивание");
            a.Value = val;
        }

        /// <summary>Проверка выражения-цели присваивания; возвращает его тип.</summary>
        private TypeRef CheckLValue(Expr target)
        {
            switch (target)
            {
                case IdentExpr id:
                {
                    var e = (Expr)id;
                    var t = CheckExpr(ref e); // Ident не подменяется
                    if (id.IdKind == IdentKind.Local) return t;
                    if (id.IdKind == IdentKind.AttachField) return t; // поле подписки listener
                    if (id.IdKind == IdentKind.StaticField)
                    {
                        if (id.Sym is FieldSymbol fs && fs.IsConst)
                        {
                            _diag.Error("E0143", $"Нельзя присвоить константе '{id.Name}'.", id.Pos);
                            return TypeRef.Error;
                        }
                        return t;
                    }
                    _diag.Error("E0144", $"'{id.Name}' не является присваиваемым значением.", id.Pos);
                    return TypeRef.Error;
                }

                case MemberExpr me:
                {
                    var e = (Expr)me;
                    var t = CheckExpr(ref e);
                    switch (me.MKind)
                    {
                        case MemberKind.HostProperty when me.ReadOnly:
                            _diag.Error("E0145", $"Свойство '{me.Name}' только для чтения.", me.Pos);
                            return TypeRef.Error;
                        case MemberKind.HostProperty:
                            return t;
                        case MemberKind.StaticField when me.Sym is FieldSymbol fs && fs.IsConst:
                            _diag.Error("E0146", $"Нельзя присвоить константе '{me.Name}'.", me.Pos);
                            return TypeRef.Error;
                        case MemberKind.StaticField:
                            return t;
                        default:
                            _diag.Error("E0147", "Этому выражению нельзя присваивать.", me.Pos);
                            return TypeRef.Error;
                    }
                }

                case IndexExpr ix:
                {
                    var e = (Expr)ix;
                    return CheckExpr(ref e); // типизация индекса уже проверит контейнер/ключ
                }

                default:
                    _diag.Error("E0148", "Этому выражению нельзя присваивать.", target.Pos);
                    return TypeRef.Error;
            }
        }

        // ===== выражения =====

        private void RequireBool(TypeRef t, SourcePos pos, string what)
        {
            if (t.Kind != TypeKind.Bool && !t.IsError)
                _diag.Error("E0150", $"{what} должно иметь тип bool, получен {t}.", pos);
        }

        private static ConvertExpr Convert(Expr inner) =>
            new ConvertExpr { Inner = inner, Type = TypeRef.Float, Pos = inner.Pos };

        /// <summary>Проверка присваиваемости + вставка int→float при необходимости.</summary>
        private void CoerceAssign(ref Expr value, TypeRef target, TypeRef valueT, SourcePos pos, string what)
        {
            if (target == null || valueT == null || target.IsError || valueT.IsError) return;
            if (target.Kind == TypeKind.Float && valueT.Kind == TypeKind.Int)
            {
                value = Convert(value);
                return;
            }
            if (!target.AcceptsValueOf(valueT))
                _diag.Error("E0151", $"Несовместимые типы: {what} ожидает {target}, получен {valueT}.", pos);
        }

        private TypeRef CheckExpr(ref Expr e)
        {
            switch (e)
            {
                case LiteralExpr lit: return CheckLiteral(lit);
                case InterpExpr ip: return CheckInterp(ip);
                case IdentExpr id: return CheckIdent(id);
                case QualifiedExpr q: return CheckQualified(q);
                case MemberExpr me: return CheckMember(me);
                case IndexExpr ix: return CheckIndex(ix);
                case CallExpr call: return CheckCall(call);
                case SpawnExpr sp: return CheckSpawn(sp);
                case SelfExpr se: return CheckSelf(se);
                case NewArrayExpr na: return CheckNewArray(na);
                case NewListExpr nl:
                    nl.ElemTypeRef = ResolveType(nl.ElemType);
                    return nl.Type = TypeRef.ListOf(nl.ElemTypeRef);
                case NewMapExpr nm:
                {
                    nm.KeyTypeRef = ResolveType(nm.KeyType);
                    nm.ValTypeRef = ResolveType(nm.ValType);
                    return nm.Type = TypeRef.MapOf(nm.KeyTypeRef, nm.ValTypeRef);
                }
                case ArrayLitExpr al: return CheckArrayLit(al);
                case BinaryExpr b: return CheckBinary(b);
                case UnaryExpr u: return CheckUnary(u);
                case ConvertExpr cv: return cv.Type;
                default: return TypeRef.Error;
            }
        }

        private TypeRef CheckLiteral(LiteralExpr lit)
        {
            switch (lit.LKind)
            {
                case LiteralKind.Bool: return lit.Type = TypeRef.Bool;
                case LiteralKind.Str: return lit.Type = TypeRef.Str;
                case LiteralKind.Null: return lit.Type = TypeRef.Nil;
                case LiteralKind.Float: return lit.Type = TypeRef.Float;
                case LiteralKind.Int:
                    if (lit.IntValue < int.MinValue || lit.IntValue > int.MaxValue)
                        _diag.Error("E0152", "Целочисленная константа вне диапазона int.", lit.Pos);
                    return lit.Type = TypeRef.Int;
                default: return lit.Type = TypeRef.Error;
            }
        }

        private TypeRef CheckInterp(InterpExpr ip)
        {
            for (int i = 0; i < ip.Parts.Count; i++)
            {
                var p = ip.Parts[i];
                var t = CheckExpr(ref p);
                ip.Parts[i] = p;
                bool ok = t.Kind == TypeKind.Str || t.Kind == TypeKind.Int || t.Kind == TypeKind.Float
                          || t.Kind == TypeKind.Bool || t.Kind == TypeKind.Enum || t.IsError;
                if (!ok)
                    _diag.Error("E0153", $"В интерполяцию нельзя подставить значение типа {t}.", p.Pos);
            }
            return ip.Type = TypeRef.Str;
        }

        private TypeRef CheckIdent(IdentExpr id)
        {
            // 1) локаль
            var loc = FindLocal(id.Name);
            if (loc != null)
            {
                id.IdKind = IdentKind.Local;
                id.Slot = loc.Slot;
                return id.Type = loc.Type;
            }

            // 2) поле/константа владельца
            if (_ownerFields != null && _ownerFields.TryGetValue(id.Name, out var fs))
            {
                // в listener поля живут в блоке ПОДПИСКИ, а не в статиках
                id.IdKind = _listener != null ? IdentKind.AttachField : IdentKind.StaticField;
                id.Slot = fs.Slot;
                id.Sym = fs;
                return id.Type = fs.Type;
            }

            // 3) встроенный Engine
            if (id.Name == "Engine")
            {
                id.IdKind = IdentKind.EngineRef;
                return id.Type = TypeRef.Error; // сам по себе значения не имеет
            }

            // 4) хостовые API / классы / енумы
            if (_host.TryGetApi(id.Name, out _))
            {
                id.IdKind = IdentKind.ApiClassRef;
                return id.Type = TypeRef.Error;
            }
            if (_host.TryGetEnum(id.Name, out var he))
            {
                id.IdKind = IdentKind.EnumTypeRef;
                id.Slot = he.Id;
                return id.Type = TypeRef.Error;
            }
            if (_host.TryGetClass(id.Name, out _))
            {
                id.IdKind = IdentKind.HostTypeRef;
                _diag.Error("E0154", $"'{id.Name}' — тип, а не значение.", id.Pos);
                return id.Type = TypeRef.Error;
            }

            // 5) глобальные скриптовые символы по видимости
            switch (_globals.ResolveSimple(id.Name, _module.Visible, out var sym))
            {
                case ResolveResult.Found:
                    return AnnotateGlobalIdent(id, sym);
                case ResolveResult.Ambiguous:
                    _diag.Error("E0155",
                        $"Имя '{id.Name}' объявлено в нескольких видимых модулях — уточните: module::{id.Name}.",
                        id.Pos);
                    return id.Type = TypeRef.Error;
                default:
                    _diag.Error("E0156", $"Неизвестное имя '{id.Name}'.", id.Pos);
                    return id.Type = TypeRef.Error;
            }
        }

        private TypeRef AnnotateGlobalIdent(IdentExpr id, Symbol sym)
        {
            switch (sym)
            {
                case ClassSymbol cs:
                    id.IdKind = IdentKind.ClassRef;
                    id.Sym = cs;
                    return id.Type = TypeRef.Error;
                case TriggerSymbol tr:
                    id.IdKind = IdentKind.TriggerRef;
                    id.Slot = tr.RuntimeId;
                    id.Sym = tr;
                    return id.Type = TypeRef.Error; // триггер — не значение (только аргумент Engine.*)
                case ListenerSymbol lsn:
                    id.IdKind = IdentKind.ListenerRef;
                    id.Slot = lsn.RuntimeId;
                    id.Sym = lsn;
                    return id.Type = TypeRef.Error; // listener — не значение (только аргумент Engine.Attach/DetachAll)
                case EnumSymbol es:
                    id.IdKind = IdentKind.EnumTypeRef;
                    id.Slot = es.Id;
                    id.Sym = es;
                    return id.Type = TypeRef.Error;
                default:
                    return id.Type = TypeRef.Error;
            }
        }

        private TypeRef CheckQualified(QualifiedExpr q)
        {
            if (!_module.Visible.Contains(q.Module))
            {
                _diag.Error("E0157", $"Модуль '{q.Module}' не входит в зависимости текущего модуля.", q.Pos);
                return q.Type = TypeRef.Error;
            }
            if (!_globals.TryResolveQualified(q.Module, q.Name, out var sym))
            {
                _diag.Error("E0158", $"В модуле '{q.Module}' нет символа '{q.Name}'.", q.Pos);
                return q.Type = TypeRef.Error;
            }
            switch (sym)
            {
                case ClassSymbol cs: q.IdKind = IdentKind.ClassRef; q.Sym = cs; break;
                case TriggerSymbol tr: q.IdKind = IdentKind.TriggerRef; q.Slot = tr.RuntimeId; q.Sym = tr; break;
                case ListenerSymbol lsq: q.IdKind = IdentKind.ListenerRef; q.Slot = lsq.RuntimeId; q.Sym = lsq; break;
                case EnumSymbol es: q.IdKind = IdentKind.EnumTypeRef; q.Slot = es.Id; q.Sym = es; break;
            }
            return q.Type = TypeRef.Error; // как и Ident: типы/классы — не значения
        }

        private TypeRef CheckMember(MemberExpr me)
        {
            // особые цели: EnumType.Member / Class.field / Api.метод (в вызове) / Engine.метод
            IdentKind targetKind = IdentKind.Unresolved;
            object targetSym = null;
            int targetSlot = -1;

            if (me.Target is IdentExpr tid)
            {
                var te = (Expr)tid;
                CheckExpr(ref te);
                targetKind = tid.IdKind;
                targetSym = tid.Sym;
                targetSlot = tid.Slot;
            }
            else if (me.Target is QualifiedExpr tq)
            {
                var te = (Expr)tq;
                CheckExpr(ref te);
                targetKind = tq.IdKind;
                targetSym = tq.Sym;
                targetSlot = tq.Slot;
            }
            else
            {
                var te = me.Target;
                var tt = CheckExpr(ref te);
                me.Target = te;
                return ResolveInstanceMember(me, tt);
            }

            switch (targetKind)
            {
                case IdentKind.EnumTypeRef:
                {
                    Dictionary<string, int> members = (targetSym as EnumSymbol)?.Members;
                    if (members == null && _host.TryGetEnumById(targetSlot, out var hostEnum))
                        members = hostEnum.Members;

                    if (members == null || !members.TryGetValue(me.Name, out int val))
                    {
                        _diag.Error("E0159", $"В енуме нет элемента '{me.Name}'.", me.Pos);
                        return me.Type = TypeRef.Error;
                    }
                    me.MKind = MemberKind.EnumValue;
                    me.Id = val;
                    return me.Type = TypeRef.EnumOf(targetSlot);
                }

                case IdentKind.ClassRef:
                {
                    var cs = (ClassSymbol)targetSym;
                    if (cs.Fields.TryGetValue(me.Name, out var fs))
                    {
                        me.MKind = MemberKind.StaticField;
                        me.Id = fs.Slot;
                        me.Sym = fs;
                        return me.Type = fs.Type;
                    }
                    // функции класса — только как цель вызова (обрабатывает CheckCall)
                    _diag.Error("E0160", $"В классе '{cs.Name}' нет поля '{me.Name}'.", me.Pos);
                    return me.Type = TypeRef.Error;
                }

                case IdentKind.TriggerRef:
                    _diag.Error("E0161", "Поля триггера приватны и снаружи недоступны.", me.Pos);
                    return me.Type = TypeRef.Error;

                case IdentKind.ListenerRef:
                    _diag.Error("E0179", "Члены listener приватны и снаружи недоступны (состояние живёт в подписке).", me.Pos);
                    return me.Type = TypeRef.Error;

                case IdentKind.ApiClassRef:
                case IdentKind.EngineRef:
                    _diag.Error("E0162", $"'{me.Name}' — метод; его можно только вызвать.", me.Pos);
                    return me.Type = TypeRef.Error;

                default:
                {
                    // обычное значение: свойства сущности / length / count
                    return ResolveInstanceMember(me, me.Target.Type ?? TypeRef.Error);
                }
            }
        }

        private TypeRef ResolveInstanceMember(MemberExpr me, TypeRef targetT)
        {
            if (targetT.IsError) return me.Type = TypeRef.Error;

            switch (targetT.Kind)
            {
                case TypeKind.Entity:
                {
                    if (_host.TryGetClassById(targetT.HostTypeId, out var cls)
                        && cls.TryGetProp(me.Name, out var prop))
                    {
                        me.MKind = MemberKind.HostProperty;
                        me.Id = prop.Id;
                        me.ReadOnly = prop.ReadOnly;
                        return me.Type = prop.Type;
                    }
                    _diag.Error("E0163", $"У сущности нет свойства '{me.Name}'.", me.Pos);
                    return me.Type = TypeRef.Error;
                }

                case TypeKind.Array when me.Name == "length":
                case TypeKind.List when me.Name == "count":
                case TypeKind.Map when me.Name == "count":
                    me.MKind = MemberKind.CollLen;
                    return me.Type = TypeRef.Int;

                default:
                    _diag.Error("E0164", $"У значения типа {targetT} нет члена '{me.Name}'.", me.Pos);
                    return me.Type = TypeRef.Error;
            }
        }

        private TypeRef CheckIndex(IndexExpr ix)
        {
            var tt = CheckExpr(ref ix.Target);
            var it = CheckExpr(ref ix.Index);

            if (tt.Kind == TypeKind.Array || tt.Kind == TypeKind.List)
            {
                if (it.Kind != TypeKind.Int && !it.IsError)
                    _diag.Error("E0165", "Индекс массива/списка должен быть int.", ix.Index.Pos);
                return ix.Type = tt.Elem;
            }
            if (tt.Kind == TypeKind.Map)
            {
                if (!tt.Key.AcceptsValueOf(it) && !it.IsError)
                    _diag.Error("E0166", $"Ключ Map имеет тип {tt.Key}, получен {it}.", ix.Index.Pos);
                return ix.Type = tt.Val;
            }
            if (!tt.IsError)
                _diag.Error("E0167", $"Индексирование не поддерживается для типа {tt}.", ix.Pos);
            return ix.Type = TypeRef.Error;
        }

        private TypeRef CheckSpawn(SpawnExpr sp)
        {
            if (_module != null && _module.Synchronous)
                _diag.Error("E0170", "'spawn' запрещён в синхронном модуле (execution: synchronous) — " +
                                     "параллельных файберов нет.", sp.Pos);
            if (_inInitializer)
                _diag.Error("E0168", "'spawn' нельзя использовать в инициализаторе поля.", sp.Pos);
            if (_noWaitHandler)
                _diag.Error("E0178", "'spawn' запрещён в OnUnsubscribe — подписка уничтожается немедленно.", sp.Pos);

            var ct = CheckCall(sp.Call);
            if (sp.Call.CKind != CallKind.ScriptFunc)
                _diag.Error("E0169", "'spawn' запускает только скриптовые функции.", sp.Pos);
            _ = ct; // возвращаемое значение игнорируется
            return sp.Type = TypeRef.Fiber;
        }

        private TypeRef CheckSelf(SelfExpr se)
        {
            if (_listener == null)
            {
                _diag.Error("E0177", "'self' доступен только внутри listener.", se.Pos);
                return se.Type = TypeRef.Error;
            }
            // TargetType может быть null, если события listener не прошли проверку —
            // тогда ошибка уже выдана, не каскадим
            return se.Type = _listener.TargetType ?? TypeRef.Error;
        }

        private TypeRef CheckNewArray(NewArrayExpr na)
        {
            na.ElemTypeRef = ResolveType(na.ElemType);
            var st = CheckExpr(ref na.Size);
            if (st.Kind != TypeKind.Int && !st.IsError)
                _diag.Error("E0170", "Размер массива должен быть int.", na.Size.Pos);
            return na.Type = TypeRef.ArrayOf(na.ElemTypeRef);
        }

        private TypeRef CheckArrayLit(ArrayLitExpr al)
        {
            if (al.Elems.Count == 0)
            {
                _diag.Error("E0208", "Пустой литерал массива: тип неопределим — используйте new T[0].", al.Pos);
                return al.Type = TypeRef.Error;
            }
            var first = al.Elems[0];
            var elemT = CheckExpr(ref first);
            al.Elems[0] = first;

            for (int i = 1; i < al.Elems.Count; i++)
            {
                var e = al.Elems[i];
                var t = CheckExpr(ref e);
                al.Elems[i] = e;

                if (elemT.Kind == TypeKind.Int && t.Kind == TypeKind.Float) elemT = TypeRef.Float;
                else if (!elemT.AcceptsValueOf(t) && !t.IsError)
                    _diag.Error("E0172", $"Элемент #{i + 1} имеет тип {t}, ожидался {elemT}.", e.Pos);
            }

            // при элементном типе float — подтягиваем int-элементы
            if (elemT.Kind == TypeKind.Float)
            {
                for (int i = 0; i < al.Elems.Count; i++)
                    if (al.Elems[i].Type != null && al.Elems[i].Type.Kind == TypeKind.Int)
                        al.Elems[i] = Convert(al.Elems[i]);
            }

            al.ElemTypeRef = elemT;
            return al.Type = TypeRef.ArrayOf(elemT);
        }

        private TypeRef CheckUnary(UnaryExpr u)
        {
            var t = CheckExpr(ref u.Operand);
            if (u.Op == TokenKind.Minus)
            {
                if (!t.IsNumeric && !t.IsError)
                    _diag.Error("E0173", $"Унарный минус неприменим к {t}.", u.Pos);
                return u.Type = t;
            }
            // Not
            RequireBool(t, u.Pos, "операнд '!'");
            return u.Type = TypeRef.Bool;
        }

        private TypeRef CheckBinary(BinaryExpr b)
        {
            var lt = CheckExpr(ref b.Left);
            var rt = CheckExpr(ref b.Right);

            switch (b.Op)
            {
                case TokenKind.AndAnd:
                case TokenKind.OrOr:
                    RequireBool(lt, b.Left.Pos, "операнд логического оператора");
                    RequireBool(rt, b.Right.Pos, "операнд логического оператора");
                    return b.Type = TypeRef.Bool;

                case TokenKind.EqEq:
                case TokenKind.NotEq:
                {
                    bool ok =
                        (lt.IsNumeric && rt.IsNumeric) ||
                        lt.Same(rt) ||
                        (lt.Kind == TypeKind.Nil && rt.IsRefLike) ||
                        (rt.Kind == TypeKind.Nil && lt.IsRefLike) ||
                        lt.IsError || rt.IsError;
                    if (!ok)
                        _diag.Error("E0174", $"Нельзя сравнивать {lt} и {rt}.", b.Pos);
                    return b.Type = TypeRef.Bool;
                }

                case TokenKind.Lt:
                case TokenKind.Le:
                case TokenKind.Gt:
                case TokenKind.Ge:
                    if ((!lt.IsNumeric || !rt.IsNumeric) && !lt.IsError && !rt.IsError)
                        _diag.Error("E0175", $"Сравнение порядка применимо к числам, получены {lt} и {rt}.", b.Pos);
                    return b.Type = TypeRef.Bool;

                case TokenKind.Plus:
                    // конкатенация строк: если любая сторона строка
                    if (lt.Kind == TypeKind.Str || rt.Kind == TypeKind.Str)
                    {
                        CheckConcatOperand(lt, b.Left.Pos);
                        CheckConcatOperand(rt, b.Right.Pos);
                        return b.Type = TypeRef.Str;
                    }
                    goto case TokenKind.Minus;

                case TokenKind.Minus:
                case TokenKind.Star:
                case TokenKind.Slash:
                {
                    if ((!lt.IsNumeric || !rt.IsNumeric) && !lt.IsError && !rt.IsError)
                    {
                        _diag.Error("E0176", $"Арифметика неприменима к {lt} и {rt}.", b.Pos);
                        return b.Type = TypeRef.Error;
                    }
                    if (lt.Kind == TypeKind.Int && rt.Kind == TypeKind.Int)
                        return b.Type = TypeRef.Int;

                    if (b.Left.Type != null && b.Left.Type.Kind == TypeKind.Int) b.Left = Convert(b.Left);
                    if (b.Right.Type != null && b.Right.Type.Kind == TypeKind.Int) b.Right = Convert(b.Right);
                    return b.Type = TypeRef.Float;
                }

                case TokenKind.Percent:
                    if ((lt.Kind != TypeKind.Int || rt.Kind != TypeKind.Int) && !lt.IsError && !rt.IsError)
                        _diag.Error("E0177", "Оператор % определён для int % int.", b.Pos);
                    return b.Type = TypeRef.Int;

                default:
                    return b.Type = TypeRef.Error;
            }
        }

        private void CheckConcatOperand(TypeRef t, SourcePos pos)
        {
            bool ok = t.Kind == TypeKind.Str || t.Kind == TypeKind.Int || t.Kind == TypeKind.Float
                      || t.Kind == TypeKind.Bool || t.Kind == TypeKind.Enum || t.IsError;
            if (!ok)
                _diag.Error("E0178", $"Значение типа {t} нельзя вклеить в строку.", pos);
        }

        // ===== вызовы =====

        private struct EngineSig
        {
            public EngineOp Op;
            public EngineArg[] Args;
            public TypeRef Ret;
        }

        private enum EngineArg : byte { TriggerRef, FiberArg, StrArg, EntityAny, ListenerRef, SubArg }

        private static readonly Dictionary<string, EngineSig> EngineSigs = BuildEngineSigs();

        private static Dictionary<string, EngineSig> BuildEngineSigs()
        {
            EngineSig S(EngineOp op, TypeRef ret, params EngineArg[] args) =>
                new EngineSig { Op = op, Args = args, Ret = ret };

            return new Dictionary<string, EngineSig>
            {
                ["EnableTrigger"] = S(EngineOp.EnableTrigger, TypeRef.Void, EngineArg.TriggerRef),
                ["DisableTrigger"] = S(EngineOp.DisableTrigger, TypeRef.Void, EngineArg.TriggerRef),
                ["IsTriggerEnabled"] = S(EngineOp.IsTriggerEnabled, TypeRef.Bool, EngineArg.TriggerRef),
                ["ActivateTrigger"] = S(EngineOp.ActivateTrigger, TypeRef.Fiber, EngineArg.TriggerRef),
                ["KillAll"] = S(EngineOp.KillAll, TypeRef.Void, EngineArg.TriggerRef),
                ["Kill"] = S(EngineOp.Kill, TypeRef.Void, EngineArg.FiberArg),
                ["IsAlive"] = S(EngineOp.IsAlive, TypeRef.Bool, EngineArg.FiberArg),
                ["EnableModule"] = S(EngineOp.EnableModule, TypeRef.Void, EngineArg.StrArg),
                ["DisableModule"] = S(EngineOp.DisableModule, TypeRef.Void, EngineArg.StrArg),
                ["IsModuleEnabled"] = S(EngineOp.IsModuleEnabled, TypeRef.Bool, EngineArg.StrArg),
                ["IsModuleLoaded"] = S(EngineOp.IsModuleLoaded, TypeRef.Bool, EngineArg.StrArg),
                ["Time"] = S(EngineOp.Time, TypeRef.Float),
                ["DeltaTime"] = S(EngineOp.DeltaTime, TypeRef.Float),
                ["Log"] = S(EngineOp.Log, TypeRef.Void, EngineArg.StrArg),
                ["Warn"] = S(EngineOp.Warn, TypeRef.Void, EngineArg.StrArg),
                ["Error"] = S(EngineOp.Error, TypeRef.Void, EngineArg.StrArg),
                ["IsValid"] = S(EngineOp.IsValid, TypeRef.Bool, EngineArg.EntityAny),
                ["Attach"] = S(EngineOp.Attach, TypeRef.Subscription, EngineArg.ListenerRef, EngineArg.EntityAny),
                ["Detach"] = S(EngineOp.Detach, TypeRef.Void, EngineArg.SubArg),
                ["DetachAll"] = S(EngineOp.DetachAll, TypeRef.Void, EngineArg.ListenerRef, EngineArg.EntityAny),
                ["IsSubscribed"] = S(EngineOp.IsSubscribed, TypeRef.Bool, EngineArg.SubArg),
                ["TriggerExists"] = S(EngineOp.TriggerExists, TypeRef.Bool, EngineArg.StrArg),
                ["ClassExists"] = S(EngineOp.ClassExists, TypeRef.Bool, EngineArg.StrArg),
            };
        }

        private TypeRef CheckCall(CallExpr call)
        {
            // 1) вызов собственной функции: Foo(...)
            if (call.Callee is IdentExpr own)
            {
                var loc = FindLocal(own.Name);
                if (loc == null && _ownerFuncs != null && _ownerFuncs.TryGetValue(own.Name, out var fn))
                    return BindScriptCall(call, fn);

                _diag.Error("E0180", $"Функция '{own.Name}' не найдена в текущем классе/триггере.", own.Pos);
                CheckArgsLoose(call);
                return call.Type = TypeRef.Error;
            }

            // 2) вызов через точку: Target.Name(...)
            if (call.Callee is MemberExpr me)
            {
                // цель — идентификатор класса/API/Engine или module::Class
                if (me.Target is IdentExpr tid)
                {
                    var te = (Expr)tid;
                    var tvt = CheckExpr(ref te);
                    // идентификатор-ЗНАЧЕНИЕ (локаль, поле) типа-коллекции —
                    // это встроенные методы (.Add/.Clear/.Has/.Remove), не namespace
                    if (tid.IdKind == IdentKind.Local
                        || tid.IdKind == IdentKind.StaticField
                        || tid.IdKind == IdentKind.AttachField)
                    {
                        me.Target = te;
                        return BindBuiltinCall(call, me, tvt);
                    }
                    return DispatchDottedCall(call, me, tid.IdKind, tid.Sym, tid.Name);
                }
                if (me.Target is QualifiedExpr tq)
                {
                    var te = (Expr)tq;
                    CheckExpr(ref te);
                    return DispatchDottedCall(call, me, tq.IdKind, tq.Sym, tq.Module + "::" + tq.Name);
                }

                // цель — значение (коллекция): встроенные методы
                var targetE = me.Target;
                var tt = CheckExpr(ref targetE);
                me.Target = targetE;
                return BindBuiltinCall(call, me, tt);
            }

            _diag.Error("E0181", "Это выражение нельзя вызвать.", call.Pos);
            CheckArgsLoose(call);
            return call.Type = TypeRef.Error;
        }

        private TypeRef DispatchDottedCall(CallExpr call, MemberExpr me, IdentKind kind, object sym, string targetName)
        {
            switch (kind)
            {
                case IdentKind.EngineRef:
                {
                    if (!EngineSigs.TryGetValue(me.Name, out var sig))
                    {
                        _diag.Error("E0182", $"У Engine нет метода '{me.Name}'.", me.Pos);
                        CheckArgsLoose(call);
                        return call.Type = TypeRef.Error;
                    }
                    return BindEngineCall(call, sig, me.Pos);
                }

                case IdentKind.ApiClassRef:
                {
                    if (!_host.TryGetApi(targetName, out var api) || !api.TryGetMethod(me.Name, out var m))
                    {
                        _diag.Error("E0183", $"У '{targetName}' нет метода '{me.Name}'.", me.Pos);
                        CheckArgsLoose(call);
                        return call.Type = TypeRef.Error;
                    }
                    return BindHostCall(call, m);
                }

                case IdentKind.ClassRef:
                {
                    var cs = (ClassSymbol)sym;
                    if (!cs.Funcs.TryGetValue(me.Name, out var fn))
                    {
                        _diag.Error("E0184", $"В классе '{cs.Name}' нет функции '{me.Name}'.", me.Pos);
                        CheckArgsLoose(call);
                        return call.Type = TypeRef.Error;
                    }
                    return BindScriptCall(call, fn);
                }

                case IdentKind.TriggerRef:
                    _diag.Error("E0185", "Функции триггера приватны — вызвать их снаружи нельзя.", me.Pos);
                    CheckArgsLoose(call);
                    return call.Type = TypeRef.Error;

                case IdentKind.ListenerRef:
                    _diag.Error("E0179", "Функции listener приватны — вызвать их снаружи нельзя.", me.Pos);
                    CheckArgsLoose(call);
                    return call.Type = TypeRef.Error;

                default:
                    _diag.Error("E0186", $"'{targetName}.{me.Name}' нельзя вызвать.", me.Pos);
                    CheckArgsLoose(call);
                    return call.Type = TypeRef.Error;
            }
        }

        private void CheckArgsLoose(CallExpr call)
        {
            for (int i = 0; i < call.Args.Count; i++)
            {
                var a = call.Args[i];
                CheckExpr(ref a);
                call.Args[i] = a;
            }
        }

        private TypeRef BindScriptCall(CallExpr call, FuncMember fn)
        {
            if (call.Args.Count != fn.Params.Count)
                _diag.Error("E0187", $"'{fn.Name}' принимает {fn.Params.Count} аргументов, передано {call.Args.Count}.", call.Pos);

            int n = System.Math.Min(call.Args.Count, fn.Params.Count);
            for (int i = 0; i < n; i++)
            {
                var a = call.Args[i];
                var t = CheckExpr(ref a);
                CoerceAssign(ref a, fn.Params[i].Type, t, a.Pos, $"аргумент #{i + 1}");
                call.Args[i] = a;
            }
            for (int i = n; i < call.Args.Count; i++) { var a = call.Args[i]; CheckExpr(ref a); call.Args[i] = a; }

            call.CKind = CallKind.ScriptFunc;
            call.TargetIndex = fn.FuncIndex;
            call.ReturnsValue = fn.ReturnType.Kind != TypeKind.Void;
            return call.Type = fn.ReturnType;
        }

        private TypeRef BindHostCall(CallExpr call, HostMethodInfo m)
        {
            if (call.Args.Count != m.Params.Length)
                _diag.Error("E0188", $"'{m.Name}' принимает {m.Params.Length} аргументов, передано {call.Args.Count}.", call.Pos);

            int n = System.Math.Min(call.Args.Count, m.Params.Length);
            for (int i = 0; i < n; i++)
            {
                var a = call.Args[i];
                var t = CheckExpr(ref a);
                CoerceAssign(ref a, m.Params[i], t, a.Pos, $"аргумент #{i + 1}");
                call.Args[i] = a;
            }
            for (int i = n; i < call.Args.Count; i++) { var a = call.Args[i]; CheckExpr(ref a); call.Args[i] = a; }

            call.CKind = CallKind.HostMethod;
            call.TargetIndex = m.HostFnId;
            call.ReturnsValue = m.Ret.Kind != TypeKind.Void;
            return call.Type = m.Ret;
        }

        private TypeRef BindEngineCall(CallExpr call, EngineSig sig, SourcePos pos)
        {
            if (call.Args.Count != sig.Args.Length)
                _diag.Error("E0189", $"Метод Engine принимает {sig.Args.Length} аргументов, передано {call.Args.Count}.", pos);

            int n = System.Math.Min(call.Args.Count, sig.Args.Length);
            for (int i = 0; i < n; i++)
            {
                var a = call.Args[i];
                switch (sig.Args[i])
                {
                    case EngineArg.TriggerRef:
                    {
                        // аргумент-триггер: принимаем только ссылку на триггер
                        CheckExpr(ref a);
                        bool ok = (a is IdentExpr ai && ai.IdKind == IdentKind.TriggerRef)
                               || (a is QualifiedExpr aq && aq.IdKind == IdentKind.TriggerRef);
                        if (!ok)
                            _diag.Error("E0190", "Здесь ожидается имя триггера.", a.Pos);
                        break;
                    }
                    case EngineArg.FiberArg:
                    {
                        var t = CheckExpr(ref a);
                        if (t.Kind != TypeKind.Fiber && !t.IsError)
                            _diag.Error("E0191", $"Ожидался Fiber, получен {t}.", a.Pos);
                        break;
                    }
                    case EngineArg.StrArg:
                    {
                        var t = CheckExpr(ref a);
                        if (t.Kind != TypeKind.Str && !t.IsError)
                            _diag.Error("E0192", $"Ожидалась строка, получен {t}.", a.Pos);
                        break;
                    }
                    case EngineArg.EntityAny:
                    {
                        var t = CheckExpr(ref a);
                        if (t.Kind != TypeKind.Entity && t.Kind != TypeKind.Nil && !t.IsError)
                            _diag.Error("E0193", $"Ожидалась сущность, получен {t}.", a.Pos);
                        break;
                    }
                    case EngineArg.ListenerRef:
                    {
                        CheckExpr(ref a);
                        bool okL = (a is IdentExpr ali && ali.IdKind == IdentKind.ListenerRef)
                                || (a is QualifiedExpr alq && alq.IdKind == IdentKind.ListenerRef);
                        if (!okL)
                            _diag.Error("E0196", "Здесь ожидается имя listener.", a.Pos);
                        break;
                    }
                    case EngineArg.SubArg:
                    {
                        var t = CheckExpr(ref a);
                        if (t.Kind != TypeKind.Sub && t.Kind != TypeKind.Nil && !t.IsError)
                            _diag.Error("E0197", $"Ожидалась Subscription, получен {t}.", a.Pos);
                        break;
                    }
                }
                call.Args[i] = a;
            }
            for (int i = n; i < call.Args.Count; i++) { var a = call.Args[i]; CheckExpr(ref a); call.Args[i] = a; }

            call.CKind = CallKind.Engine;
            call.TargetIndex = (int)sig.Op;
            call.ReturnsValue = sig.Ret.Kind != TypeKind.Void;
            return call.Type = sig.Ret;
        }

        private TypeRef BindBuiltinCall(CallExpr call, MemberExpr me, TypeRef targetT)
        {
            BuiltinOp op;
            TypeRef ret = TypeRef.Void;
            TypeRef argT = null;

            if (targetT.Kind == TypeKind.List && me.Name == "Add") { op = BuiltinOp.ListAdd; argT = targetT.Elem; }
            else if (targetT.Kind == TypeKind.List && me.Name == "Clear") { op = BuiltinOp.ListClear; }
            else if (targetT.Kind == TypeKind.Map && me.Name == "Has") { op = BuiltinOp.MapHas; argT = targetT.Key; ret = TypeRef.Bool; }
            else if (targetT.Kind == TypeKind.Map && me.Name == "Remove") { op = BuiltinOp.MapRemove; argT = targetT.Key; }
            else
            {
                if (!targetT.IsError)
                    _diag.Error("E0194", $"У значения типа {targetT} нет метода '{me.Name}'.", me.Pos);
                CheckArgsLoose(call);
                return call.Type = TypeRef.Error;
            }

            int expected = argT == null ? 0 : 1;
            if (call.Args.Count != expected)
                _diag.Error("E0195", $"'{me.Name}' принимает {expected} аргументов.", call.Pos);

            if (argT != null && call.Args.Count >= 1)
            {
                var a = call.Args[0];
                var t = CheckExpr(ref a);
                CoerceAssign(ref a, argT, t, a.Pos, "аргумент");
                call.Args[0] = a;
            }

            call.CKind = CallKind.Builtin;
            call.TargetIndex = (int)op;
            call.ReturnsValue = ret.Kind != TypeKind.Void;
            return call.Type = ret;
        }
    }

}
