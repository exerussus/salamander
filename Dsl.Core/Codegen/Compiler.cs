using System.Collections.Generic;
using Dsl.Runtime;
using Dsl.Semantics;
using Dsl.Syntax;
using Dsl.Text;

namespace Dsl.Codegen
{
    /// <summary>
    /// Транслятор аннотированного AST в байткод. Работает после успешной
    /// семантики, поэтому почти не диагностирует — только генерирует.
    /// Инварианты: каждое ВЫРАЖЕНИЕ оставляет на стеке ровно одно значение
    /// (вызовы void кладут Nil); каждый СТЕЙТМЕНТ оставляет стек как был.
    /// </summary>
    public sealed class BytecodeCompiler
    {
        private readonly HostRegistry _host;
        private readonly CheckResult _sem;
        private readonly IReadOnlyList<SourceText> _files;

        // пул строковых литералов программы
        private readonly List<string> _stringLits = new List<string>();
        private readonly Dictionary<string, int> _stringLitIds = new Dictionary<string, int>();

        // состояние текущей функции
        private List<Instr> _code;
        private int _line;
        private string _fileName;

        private sealed class LoopCtx
        {
            public readonly List<int> Breaks = new List<int>();
            public readonly List<int> Continues = new List<int>();
            public bool OwnsIter; // for-in держит буфер снапшота — break/return обязаны IterEnd
        }
        private readonly List<LoopCtx> _loops = new List<LoopCtx>();

        public BytecodeCompiler(HostRegistry host, CheckResult sem, IReadOnlyList<SourceText> files)
        {
            _host = host;
            _sem = sem;
            _files = files;
        }

        public CompiledProgram Compile(List<ModuleAst> modules)
        {
            var chunks = new List<Chunk>(_sem.Funcs.Count + _sem.Listeners.Count);

            // [0] — синтетический <init>: инициализаторы статических полей по порядку
            chunks.Add(CompileInit());

            for (int i = 1; i < _sem.Funcs.Count; i++)
                chunks.Add(CompileFunc(_sem.Funcs[i]));

            // таблица триггеров + карта модулей
            var moduleIndex = new Dictionary<string, int>();
            for (int i = 0; i < _sem.ModuleNames.Count; i++)
                moduleIndex[_sem.ModuleNames[i]] = i;

            var triggers = new TriggerRuntimeInfo[_sem.Triggers.Count];
            var triggerIdByName = new Dictionary<string, int>();
            for (int i = 0; i < _sem.Triggers.Count; i++)
            {
                var t = _sem.Triggers[i];
                triggers[i] = new TriggerRuntimeInfo
                {
                    Id = i,
                    Name = t.Name,
                    Module = t.Module,
                    ModuleIndex = moduleIndex.TryGetValue(t.Module, out var mi) ? mi : 0,
                    StartDisabled = t.StartDisabled,
                    ActionFuncIndex = t.Action?.FuncIndex ?? -1,
                };
                triggerIdByName[t.Name] = i; // при конфликте имён из разных модулей победит последний — поиск по имени best-effort
            }

            // таблица обработчиков: [eventId] -> в детерминированном порядке
            var perEvent = new List<EventHandlerRef>[_host.EventCount];
            for (int e = 0; e < perEvent.Length; e++) perEvent[e] = new List<EventHandlerRef>();
            // блоки одного триггера слиты: одноимённые обработчики — переопределение,
            // в диспатч попадает только ПОЗДНЯЯ версия каждого события
            var trigEventWinner = new Dictionary<int, int>(); // eventId -> funcIndex
            foreach (var t in _sem.Triggers)
            {
                trigEventWinner.Clear();
                foreach (var ev in t.Events)
                    if (ev.EventId >= 0)
                        trigEventWinner[ev.EventId] = ev.FuncIndex; // поздний блок побеждает
                foreach (var kv in trigEventWinner)
                    perEvent[kv.Key].Add(new EventHandlerRef { TriggerId = t.RuntimeId, FuncIndex = kv.Value });
            }

            var handlers = new EventHandlerRef[perEvent.Length][];
            for (int e = 0; e < perEvent.Length; e++) handlers[e] = perEvent[e].ToArray();

            // listener-ы: рантайм-инфо + init-чанк сброса полей + карта обработчиков
            var listeners = new ListenerRuntimeInfo[_sem.Listeners.Count];
            var listenerHandlerFunc = new int[_sem.Listeners.Count][];
            var eventHasListeners = new bool[_host.EventCount];
            for (int i = 0; i < _sem.Listeners.Count; i++)
            {
                var ls = _sem.Listeners[i];

                int initIdx = -1;
                if (ls.FieldCount > 0)
                {
                    // сброс ВСЕХ полей (пул переиспользует массивы — затираем старые значения)
                    chunks.Add(CompileListenerInit(ls));
                    initIdx = chunks.Count - 1;
                }

                listeners[i] = new ListenerRuntimeInfo
                {
                    Id = i,
                    Name = ls.Name,
                    Module = ls.Module,
                    ModuleIndex = moduleIndex.TryGetValue(ls.Module, out var lmi) ? lmi : 0,
                    FieldCount = ls.FieldCount,
                    InitFuncIndex = initIdx,
                    OnSubscribeFunc = ls.OnSubscribe?.FuncIndex ?? -1,
                    OnUnsubscribeFunc = ls.OnUnsubscribe?.FuncIndex ?? -1,
                };

                var map = new int[_host.EventCount];
                for (int e = 0; e < map.Length; e++) map[e] = -1;
                foreach (var ev in ls.Events)
                {
                    if (ev.EventId < 0) continue;
                    map[ev.EventId] = ev.FuncIndex;
                    eventHasListeners[ev.EventId] = true;
                }
                listenerHandlerFunc[i] = map;
            }

            // архетипы: интернируем id в плотные индексы. Блоки одного (вид, id)
            // уже слиты чекером в одну сущность; события в списке идут в порядке
            // объявления — поздний обработчик перезаписывает слот (later-wins)
            var archKinds = new ArchetypeKindRuntime[_host.ArchetypeKindCount];
            var archIdLists = new List<string>[archKinds.Length];
            var archHandlerLists = new List<ArchHandler[]>[archKinds.Length];
            for (int k = 0; k < archKinds.Length; k++)
            {
                var info = _host.GetArchetypeKind(k);
                archKinds[k] = new ArchetypeKindRuntime
                {
                    Name = info.Name,
                    EventCount = info.Events.Count,
                    IdIndex = new Dictionary<string, int>(System.StringComparer.Ordinal),
                };
                archIdLists[k] = new List<string>();
                archHandlerLists[k] = new List<ArchHandler[]>();
            }
            foreach (var asym in _sem.Archetypes)
            {
                if (asym.KindId < 0) continue; // вид не найден — ошибка уже выдана
                var kr = archKinds[asym.KindId];
                if (!kr.IdIndex.TryGetValue(asym.Id, out int ai))
                {
                    ai = archIdLists[asym.KindId].Count;
                    kr.IdIndex[asym.Id] = ai;
                    archIdLists[asym.KindId].Add(asym.Id);
                    var fresh = new ArchHandler[kr.EventCount];
                    for (int e = 0; e < fresh.Length; e++) fresh[e] = new ArchHandler { Func = -1, ModuleIndex = 0 };
                    archHandlerLists[asym.KindId].Add(fresh);
                }
                var slot = archHandlerLists[asym.KindId][ai];
                foreach (var ev in asym.Events)
                {
                    // мерж-сущность собирает обработчики из разных модулей —
                    // гейт Enable/DisableModule должен бить по владельцу события
                    var evModule = ev.Owner is ArchetypeDecl ad ? ad.Module : asym.Module;
                    int ami = moduleIndex.TryGetValue(evModule, out var amiv) ? amiv : 0;
                    slot[ev.EventId] = new ArchHandler { Func = ev.FuncIndex, ModuleIndex = ami };
                }
            }
            for (int k = 0; k < archKinds.Length; k++)
            {
                archKinds[k].Ids = archIdLists[k].ToArray();
                archKinds[k].Handlers = archHandlerLists[k].ToArray();
            }

            // скриптовые енумы (для печати имён в логах)
            var scriptEnums = new ScriptEnumInfo[_sem.ScriptEnums.Count];
            for (int i = 0; i < scriptEnums.Length; i++)
            {
                var es = _sem.ScriptEnums[i];
                scriptEnums[i] = new ScriptEnumInfo
                {
                    Id = es.Id,
                    Name = es.Name,
                    Members = es.Decl.Members.ToArray(),
                };
            }

            return new CompiledProgram
            {
                Functions = chunks.ToArray(),
                StaticCount = _sem.StaticFields.Count,
                StringLiterals = _stringLits.ToArray(),
                Triggers = triggers,
                EventHandlers = handlers,
                Modules = _sem.ModuleNames.ToArray(),
                ScriptEnums = scriptEnums,
                Listeners = listeners,
                ListenerHandlerFunc = listenerHandlerFunc,
                EventHasListenerHandlers = eventHasListeners,
                ArchetypeKinds = archKinds,
                TriggerIdByName = triggerIdByName,
                ClassNames = _sem.ClassNames,
            };
        }

        // ===================================================================
        // функции
        // ===================================================================

        private Chunk CompileInit()
        {
            _code = new List<Instr>();
            _fileName = "<init>";
            _line = 0;

            foreach (var fs in _sem.StaticFields)
            {
                var f = fs.Decl;
                if (f.Init == null) continue; // дефолт — Nil (читается как 0/false/null)
                SetLine(f.Pos);
                EmitExpr(f.Init);
                Emit(OpCode.StoreStatic, fs.Slot);
            }

            // переопределения полей архетипов: тот же слот, поздний блок
            // перезаписывает значение (порядок объявления сохранён)
            foreach (var fs in _sem.StaticInitOverrides)
            {
                var f = fs.Decl;
                if (f.Init == null) continue;
                SetLine(f.Pos);
                EmitExpr(f.Init);
                Emit(OpCode.StoreStatic, fs.Slot);
            }
            Emit(OpCode.Return, 0);

            return new Chunk
            {
                Name = "<init>",
                File = "<init>",
                ParamCount = 0,
                LocalCount = 0,
                Code = _code.ToArray(),
            };
        }

        /// <summary>
        /// Сброс блока полей подписки к декларированным значениям. Пишет ВСЕ слоты
        /// (Init==null → Nil): пул переиспользует массивы, старые значения затираются.
        /// Только выражения-инициализаторы — wait здесь невозможен по построению.
        /// </summary>
        private Chunk CompileListenerInit(ListenerSymbol ls)
        {
            _code = new List<Instr>();
            _fileName = "<listener-init>";
            _line = 0;

            var ordered = new FieldSymbol[ls.FieldCount];
            foreach (var fs in ls.Fields.Values) ordered[fs.Slot] = fs;

            foreach (var fs in ordered)
            {
                if (fs == null) continue; // дыр не бывает, страховка
                var f = fs.Decl;
                SetLine(f.Pos);
                if (f.Init != null) EmitExpr(f.Init);
                else Emit(OpCode.PushNil);
                Emit(OpCode.StoreAttach, fs.Slot);
            }
            Emit(OpCode.Return, 0);

            return new Chunk
            {
                Name = ls.Name + ".<init>",
                File = "<listener-init>",
                ParamCount = 0,
                LocalCount = 0,
                Code = _code.ToArray(),
            };
        }

        private Chunk CompileFunc(FuncMember fn)
        {
            _code = new List<Instr>();
            _fileName = FileNameOf(fn.Pos);
            _line = fn.Pos.Line;
            _loops.Clear();

            if (fn.Body != null) EmitBlock(fn.Body); // прототип «...;» — пустая реализация
            Emit(OpCode.Return, 0); // страховка на «стекание» с конца

            string ownerName = fn.Owner?.Name ?? "?";
            return new Chunk
            {
                Name = ownerName + "." + fn.Name,
                File = _fileName,
                ParamCount = fn.Params.Count,
                LocalCount = fn.LocalCount,
                Code = _code.ToArray(),
            };
        }

        private string FileNameOf(SourcePos pos)
        {
            if (pos.FileId >= 0 && _files != null && pos.FileId < _files.Count)
                return _files[pos.FileId].Name;
            return "<unknown>";
        }

        // ===================================================================
        // низкоуровневые помощники
        // ===================================================================

        private void SetLine(SourcePos pos)
        {
            if (pos.Line > 0) _line = pos.Line;
        }

        private int Emit(OpCode op, int a = 0, int b = 0)
        {
            _code.Add(new Instr(op, a, b, _line));
            return _code.Count - 1;
        }

        private int EmitJump(OpCode op)
        {
            _code.Add(new Instr(op, -1, 0, _line));
            return _code.Count - 1;
        }

        private void Patch(int at, int target)
        {
            var i = _code[at];
            i.A = target;
            _code[at] = i;
        }

        private int HereLabel => _code.Count;

        private int LitId(string s)
        {
            if (_stringLitIds.TryGetValue(s, out var id)) return id;
            id = _stringLits.Count;
            _stringLits.Add(s);
            _stringLitIds[s] = id;
            return id;
        }

        // ===================================================================
        // стейтменты
        // ===================================================================

        private void EmitBlock(Block b)
        {
            foreach (var s in b.Stmts) EmitStmt(s);
        }

        private void EmitStmt(Stmt s)
        {
            SetLine(s.Pos);
            switch (s)
            {
                case PassStmt _:
                    break; // осознанно ничего

                case Block b: EmitBlock(b); break;

                case VarDeclStmt v:
                    if (v.Init != null) EmitExpr(v.Init);
                    else Emit(OpCode.PushNil);
                    Emit(OpCode.StoreLocal, v.Slot);
                    break;

                case AssignStmt a: EmitAssign(a); break;

                case ExprStmt e:
                    EmitExpr(e.Expr);
                    Emit(OpCode.Pop);
                    break;

                case IfStmt i: EmitIf(i); break;
                case WhileStmt w: EmitWhile(w); break;
                case ForRangeStmt fr: EmitForRange(fr); break;
                case ForEachStmt fe: EmitForEach(fe); break;

                case BreakStmt _:
                    if (_loops.Count > 0)
                        _loops[_loops.Count - 1].Breaks.Add(EmitJump(OpCode.Jump));
                    break;

                case ContinueStmt _:
                    if (_loops.Count > 0)
                        _loops[_loops.Count - 1].Continues.Add(EmitJump(OpCode.Jump));
                    break;

                case ReturnStmt r:
                    // выходим из функции насквозь через все for-in — закрываем их буферы
                    foreach (var lc in _loops)
                        if (lc.OwnsIter) Emit(OpCode.IterEnd);
                    if (r.Value != null) { EmitExpr(r.Value); Emit(OpCode.Return, 1); }
                    else Emit(OpCode.Return, 0);
                    break;

                case WaitStmt w:
                    EmitExpr(w.Seconds);
                    Emit(OpCode.Wait);
                    break;

                case WaitUntilStmt wu:
                {
                    // десахар: while (!cond) { yield_tick; }
                    int start = HereLabel;
                    EmitExpr(wu.Cond);
                    int exit = EmitJump(OpCode.JumpIfTrue);
                    Emit(OpCode.YieldTick);
                    Emit(OpCode.Jump, start);
                    Patch(exit, HereLabel);
                    break;
                }
            }
        }

        private void EmitAssign(AssignStmt a)
        {
            switch (a.Target)
            {
                case IdentExpr id when id.IdKind == IdentKind.Local:
                    EmitExpr(a.Value);
                    Emit(OpCode.StoreLocal, id.Slot);
                    break;

                case IdentExpr id when id.IdKind == IdentKind.StaticField:
                    EmitExpr(a.Value);
                    Emit(OpCode.StoreStatic, ((FieldSymbol)id.Sym).Slot);
                    break;

                case IdentExpr id when id.IdKind == IdentKind.AttachField:
                    EmitExpr(a.Value);
                    Emit(OpCode.StoreAttach, ((FieldSymbol)id.Sym).Slot);
                    break;

                case MemberExpr me when me.MKind == MemberKind.StaticField:
                    EmitExpr(a.Value);
                    Emit(OpCode.StoreStatic, ((FieldSymbol)me.Sym).Slot);
                    break;

                case MemberExpr me when me.MKind == MemberKind.HostProperty:
                    EmitExpr(me.Target);   // [obj]
                    EmitExpr(a.Value);     // [obj, value]
                    Emit(OpCode.StoreField, me.Id);
                    break;

                case IndexExpr ix:
                    EmitExpr(ix.Target);   // [coll]
                    EmitExpr(ix.Index);    // [coll, idx]
                    EmitExpr(a.Value);     // [coll, idx, value]
                    Emit(OpCode.StoreIndex);
                    break;
            }
        }

        private void EmitIf(IfStmt i)
        {
            EmitExpr(i.Cond);
            int jElse = EmitJump(OpCode.JumpIfFalse);
            EmitBlock(i.Then);
            if (i.Else != null)
            {
                int jEnd = EmitJump(OpCode.Jump);
                Patch(jElse, HereLabel);
                EmitStmt(i.Else);
                Patch(jEnd, HereLabel);
            }
            else
            {
                Patch(jElse, HereLabel);
            }
        }

        private void EmitWhile(WhileStmt w)
        {
            int start = HereLabel;
            EmitExpr(w.Cond);
            int exit = EmitJump(OpCode.JumpIfFalse);

            var ctx = new LoopCtx();
            _loops.Add(ctx);
            EmitBlock(w.Body);
            _loops.RemoveAt(_loops.Count - 1);

            Emit(OpCode.Jump, start);
            Patch(exit, HereLabel);
            foreach (var br in ctx.Breaks) Patch(br, HereLabel);
            foreach (var co in ctx.Continues) Patch(co, start);
        }

        private void EmitForRange(ForRangeStmt fr)
        {
            // i = from; limit = to;
            EmitExpr(fr.From);
            Emit(OpCode.StoreLocal, fr.VarSlot);
            EmitExpr(fr.To);
            Emit(OpCode.StoreLocal, fr.LimitSlot);

            int start = HereLabel;
            Emit(OpCode.LoadLocal, fr.VarSlot);
            Emit(OpCode.LoadLocal, fr.LimitSlot);
            Emit(OpCode.Lt);
            int exit = EmitJump(OpCode.JumpIfFalse);

            var ctx = new LoopCtx();
            _loops.Add(ctx);
            EmitBlock(fr.Body);
            _loops.RemoveAt(_loops.Count - 1);

            int inc = HereLabel; // continue приходит сюда
            Emit(OpCode.LoadLocal, fr.VarSlot);
            Emit(OpCode.PushInt, 1);
            Emit(OpCode.Add);
            Emit(OpCode.StoreLocal, fr.VarSlot);
            Emit(OpCode.Jump, start);

            Patch(exit, HereLabel);
            foreach (var br in ctx.Breaks) Patch(br, HereLabel);
            foreach (var co in ctx.Continues) Patch(co, inc);
        }

        private void EmitForEach(ForEachStmt fe)
        {
            // Снапшот-итерация: коллекция копируется в буфер ФАЙБЕРА на входе —
            // менять её в теле безопасно. Map: снапшотятся ключи, удалённые
            // пропускаются (проверка в IterNext), значения читаются живыми.
            EmitExpr(fe.Coll);
            Emit(OpCode.StoreLocal, fe.CollSlot);   // источник: map[k] и скип удалённых
            Emit(OpCode.LoadLocal, fe.CollSlot);
            Emit(OpCode.IterBegin, fe.BufSlot);     // pop coll → снапшот; locals[Buf] = bufId
            Emit(OpCode.PushInt, 0);
            Emit(OpCode.StoreLocal, fe.IndexSlot);

            int start = HereLabel; // continue приходит сюда (инкремент внутри IterNext)
            Emit(OpCode.IterNext, fe.IndexSlot, fe.VarSlot);
            int exit = EmitJump(OpCode.JumpIfFalse);

            if (fe.Var2 != null)
            {
                // v = coll[k] — живое значение; ключ гарантированно жив (IterNext проверил)
                Emit(OpCode.LoadLocal, fe.CollSlot);
                Emit(OpCode.LoadLocal, fe.VarSlot);
                Emit(OpCode.Index);
                Emit(OpCode.StoreLocal, fe.Var2Slot);
            }

            var ctx = new LoopCtx { OwnsIter = true };
            _loops.Add(ctx);
            EmitBlock(fe.Body);
            _loops.RemoveAt(_loops.Count - 1);

            Emit(OpCode.Jump, start);

            Patch(exit, HereLabel);
            foreach (var br in ctx.Breaks) Patch(br, HereLabel); // break сюда — до IterEnd
            Emit(OpCode.IterEnd);
            foreach (var co in ctx.Continues) Patch(co, start);
        }

        // ===================================================================
        // выражения
        // ===================================================================

        private void EmitExpr(Expr e)
        {
            SetLine(e.Pos);
            switch (e)
            {
                case LiteralExpr lit: EmitLiteral(lit); break;
                case InterpExpr ip: EmitInterp(ip); break;
                case IdentExpr id: EmitIdent(id); break;
                case QualifiedExpr q: EmitQualified(q); break;
                case MemberExpr me: EmitMember(me); break;
                case IndexExpr ix:
                    EmitExpr(ix.Target);
                    EmitExpr(ix.Index);
                    Emit(OpCode.Index);
                    break;
                case CallExpr call: EmitCall(call); break;
                case SpawnExpr sp: EmitSpawn(sp); break;
                case SelfExpr _: Emit(OpCode.PushSelf); break;
                case NewArrayExpr na:
                    EmitExpr(na.Size);
                    Emit(OpCode.NewArray);
                    break;
                case NewListExpr _: Emit(OpCode.NewList); break;
                case NewMapExpr _: Emit(OpCode.NewMap); break;
                case ArrayLitExpr al:
                    foreach (var el in al.Elems) EmitExpr(el);
                    Emit(OpCode.ArrayLit, al.Elems.Count);
                    break;
                case BinaryExpr b: EmitBinary(b); break;
                case UnaryExpr u:
                    EmitExpr(u.Operand);
                    Emit(u.Op == Text.TokenKind.Minus ? OpCode.Neg : OpCode.Not);
                    break;
                case ConvertExpr cv:
                    EmitExpr(cv.Inner);
                    Emit(OpCode.IntToFloat);
                    break;
                default:
                    Emit(OpCode.PushNil);
                    break;
            }
        }

        private void EmitLiteral(LiteralExpr lit)
        {
            switch (lit.LKind)
            {
                case LiteralKind.Bool:
                    Emit(lit.BoolValue ? OpCode.PushTrue : OpCode.PushFalse);
                    break;
                case LiteralKind.Int:
                    Emit(OpCode.PushInt, (int)lit.IntValue);
                    break;
                case LiteralKind.Float:
                    Emit(OpCode.PushFloat, System.BitConverter.SingleToInt32Bits((float)lit.FloatValue));
                    break;
                case LiteralKind.Str:
                    Emit(OpCode.PushStr, LitId(lit.StrValue));
                    break;
                default:
                    Emit(OpCode.PushNil);
                    break;
            }
        }

        private void EmitInterp(InterpExpr ip)
        {
            if (ip.Parts.Count == 1 && ip.Parts[0].Type != null
                && ip.Parts[0].Type.Kind == TypeKind.Str)
            {
                EmitExpr(ip.Parts[0]); // $"{s}" — уже строка
                return;
            }
            foreach (var p in ip.Parts) EmitExpr(p);
            Emit(OpCode.Concat, ip.Parts.Count);
        }

        private void EmitIdent(IdentExpr id)
        {
            switch (id.IdKind)
            {
                case IdentKind.Local:
                    Emit(OpCode.LoadLocal, id.Slot);
                    break;
                case IdentKind.StaticField:
                {
                    var fs = (FieldSymbol)id.Sym;
                    if (fs.IsConst) EmitConst(fs);
                    else Emit(OpCode.LoadStatic, fs.Slot);
                    break;
                }
                case IdentKind.AttachField:
                    Emit(OpCode.LoadAttach, ((FieldSymbol)id.Sym).Slot);
                    break;
                case IdentKind.TriggerRef:
                case IdentKind.ListenerRef:
                    Emit(OpCode.PushInt, id.Slot); // runtime id триггера/listener как аргумент Engine.*
                    break;
                default:
                    Emit(OpCode.PushNil);
                    break;
            }
        }

        private void EmitQualified(QualifiedExpr q)
        {
            if (q.IdKind == IdentKind.TriggerRef || q.IdKind == IdentKind.ListenerRef)
                Emit(OpCode.PushInt, q.Slot);
            else
                Emit(OpCode.PushNil);
        }

        private void EmitConst(FieldSymbol fs)
        {
            if (fs.ConstStr != null)
            {
                Emit(OpCode.PushStr, LitId(fs.ConstStr));
                return;
            }
            var v = fs.ConstValue;
            switch (v.Type)
            {
                case VariantType.Bool: Emit(v.AsBool ? OpCode.PushTrue : OpCode.PushFalse); break;
                case VariantType.Int: Emit(OpCode.PushInt, v.AsInt); break;
                case VariantType.Float: Emit(OpCode.PushFloat, System.BitConverter.SingleToInt32Bits(v.AsFloat)); break;
                case VariantType.Enum: Emit(OpCode.PushEnum, v.EnumTypeId, v.EnumValue); break;
                default: Emit(OpCode.PushNil); break;
            }
        }

        private void EmitMember(MemberExpr me)
        {
            switch (me.MKind)
            {
                case MemberKind.EnumValue:
                    Emit(OpCode.PushEnum, me.Type.EnumId, me.Id);
                    break;
                case MemberKind.StaticField:
                {
                    var fs = (FieldSymbol)me.Sym;
                    if (fs.IsConst) EmitConst(fs);
                    else Emit(OpCode.LoadStatic, fs.Slot);
                    break;
                }
                case MemberKind.HostProperty:
                    EmitExpr(me.Target);
                    Emit(OpCode.LoadField, me.Id);
                    break;
                case MemberKind.CollLen:
                    EmitExpr(me.Target);
                    Emit(OpCode.Len);
                    break;
                default:
                    Emit(OpCode.PushNil);
                    break;
            }
        }

        private void EmitCall(CallExpr call)
        {
            switch (call.CKind)
            {
                case CallKind.ScriptFunc:
                    foreach (var a in call.Args) EmitExpr(a);
                    Emit(OpCode.CallScript, call.TargetIndex, call.Args.Count);
                    break;

                case CallKind.HostMethod:
                    foreach (var a in call.Args) EmitExpr(a);
                    Emit(OpCode.CallHost, call.TargetIndex, call.Args.Count);
                    break;

                case CallKind.Engine:
                    foreach (var a in call.Args) EmitExpr(a);
                    Emit(OpCode.CallEngine, call.TargetIndex, call.Args.Count);
                    break;

                case CallKind.Builtin:
                {
                    // цель (коллекция) — первый на стеке, затем аргументы
                    var me = (MemberExpr)call.Callee;
                    EmitExpr(me.Target);
                    foreach (var a in call.Args) EmitExpr(a);
                    switch ((BuiltinOp)call.TargetIndex)
                    {
                        case BuiltinOp.ListAdd: Emit(OpCode.ListAdd); break;
                        case BuiltinOp.ListClear: Emit(OpCode.ListClear); break;
                        case BuiltinOp.MapHas: Emit(OpCode.MapHas); break;
                        case BuiltinOp.MapRemove: Emit(OpCode.MapRemove); break;
                    }
                    break;
                }

                default:
                    Emit(OpCode.PushNil);
                    break;
            }
        }

        private void EmitSpawn(SpawnExpr sp)
        {
            foreach (var a in sp.Call.Args) EmitExpr(a);
            Emit(OpCode.Spawn, sp.Call.TargetIndex, sp.Call.Args.Count);
        }

        private void EmitBinary(BinaryExpr b)
        {
            switch (b.Op)
            {
                case Text.TokenKind.AndAnd:
                {
                    EmitExpr(b.Left);
                    int jf = EmitJump(OpCode.JumpIfFalse);
                    EmitExpr(b.Right);
                    int jEnd = EmitJump(OpCode.Jump);
                    Patch(jf, HereLabel);
                    Emit(OpCode.PushFalse);
                    Patch(jEnd, HereLabel);
                    return;
                }
                case Text.TokenKind.OrOr:
                {
                    EmitExpr(b.Left);
                    int jt = EmitJump(OpCode.JumpIfTrue);
                    EmitExpr(b.Right);
                    int jEnd = EmitJump(OpCode.Jump);
                    Patch(jt, HereLabel);
                    Emit(OpCode.PushTrue);
                    Patch(jEnd, HereLabel);
                    return;
                }
                case Text.TokenKind.Plus when b.Type != null && b.Type.Kind == TypeKind.Str:
                {
                    // сплющиваем цепочку a + b + c в один Concat(n)
                    var parts = new List<Expr>();
                    FlattenConcat(b, parts);
                    foreach (var p in parts) EmitExpr(p);
                    Emit(OpCode.Concat, parts.Count);
                    return;
                }
            }

            EmitExpr(b.Left);
            EmitExpr(b.Right);
            switch (b.Op)
            {
                case Text.TokenKind.Plus: Emit(OpCode.Add); break;
                case Text.TokenKind.Minus: Emit(OpCode.Sub); break;
                case Text.TokenKind.Star: Emit(OpCode.Mul); break;
                case Text.TokenKind.Slash: Emit(OpCode.Div); break;
                case Text.TokenKind.Percent: Emit(OpCode.Mod); break;
                case Text.TokenKind.EqEq: Emit(OpCode.Eq); break;
                case Text.TokenKind.NotEq: Emit(OpCode.Ne); break;
                case Text.TokenKind.Lt: Emit(OpCode.Lt); break;
                case Text.TokenKind.Le: Emit(OpCode.Le); break;
                case Text.TokenKind.Gt: Emit(OpCode.Gt); break;
                case Text.TokenKind.Ge: Emit(OpCode.Ge); break;
                default: Emit(OpCode.Nop); break;
            }
        }

        private static void FlattenConcat(Expr e, List<Expr> into)
        {
            if (e is BinaryExpr b && b.Op == Text.TokenKind.Plus
                && b.Type != null && b.Type.Kind == TypeKind.Str)
            {
                FlattenConcat(b.Left, into);
                FlattenConcat(b.Right, into);
            }
            else
            {
                into.Add(e);
            }
        }
    }
}
