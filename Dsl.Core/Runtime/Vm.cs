using System;
using Dsl.Codegen;

namespace Dsl.Runtime
{
    public enum RunResult : byte
    {
        Completed,   // файбер дошёл до конца (или был убит)
        Waited,      // приостановлен wait N — движок положит в таймеры
        Yielded,     // приостановлен до следующего тика (wait until)
        OutOfBudget, // исчерпан бюджет инструкций этого запуска — состояние сохранено, можно продолжить
        Errored,     // скриптовая ошибка — файбер мёртв, отчёт ушёл хосту
    }

    /// <summary>
    /// Интерпретатор байткода. Исполняет ОДИН файбер до завершения или
    /// приостановки. Горячий путь без аллокаций: стек значений растёт
    /// амортизированно и переживает возврат файбера в пул; строки собираются
    /// в scratch-буфере StringTable.
    /// </summary>
    public sealed class Vm
    {
        private readonly ScriptEngine _engine;

        internal CompiledProgram Prog;
        internal Variant[] Statics;
        internal int[] LitIds; // индекс литерала -> интернированный id

        /// <summary>Сколько инструкций исполнил последний Run — движок списывает это с бюджета тика.</summary>
        public int LastInstructionCount => _instr;
        private int _instr;

        public Vm(ScriptEngine engine)
        {
            _engine = engine;
        }

        /// <summary>
        /// Исполнить файбер, но не более <paramref name="cap"/> инструкций за этот
        /// вызов. Если файбер не завершился и не приостановился сам (wait/yield),
        /// а упёрся в cap — состояние сохраняется, возвращается OutOfBudget, и
        /// движок решает: продолжить позже или убить (политика бюджета).
        /// </summary>
        public RunResult Run(Fiber f, int cap)
        {
            _instr = 0;
            try
            {
                return RunCore(f, cap);
            }
            catch (ScriptError ex)
            {
                _engine.ReportFiberError(f, ex.Message);
                return RunResult.Errored;
            }
            catch (Exception ex)
            {
                // защита движка: любые неожиданные исключения хостовых биндингов
                // тоже гасят только файбер
                _engine.ReportFiberError(f, "Внутренняя ошибка: " + ex.Message);
                return RunResult.Errored;
            }
        }

        private RunResult RunCore(Fiber f, int cap)
        {
            var stack = f.Stack;
            int sp = f.Sp;

            // локальный помощник: push с ростом стека (struct-замыкание, без аллокаций)
            void Push(Variant v)
            {
                if (sp >= stack.Length)
                {
                    f.EnsureStack(sp + 1);
                    stack = f.Stack;
                }
                stack[sp++] = v;
            }

            while (f.FrameCount > 0)
            {
                ref var frame = ref f.Frames[f.FrameCount - 1];
                var chunk = Prog.Functions[frame.Func];
                var code = chunk.Code;
                int ip = frame.Ip;

                try
                {
                    while (true)
                    {
                    if (_instr >= cap)
                    {
                        // бюджет запуска исчерпан — сохраняем состояние и отдаём
                        // управление движку; файбер можно продолжить позже
                        frame.Ip = ip;
                        f.Sp = sp;
                        return RunResult.OutOfBudget;
                    }
                    _instr++;

                    var ins = code[ip++];
                    switch (ins.Op)
                    {
                        case OpCode.Nop: break;

                        // ----- константы -----
                        case OpCode.PushNil: Push(Variant.Nil); break;
                        case OpCode.PushTrue: Push(Variant.Bool(true)); break;
                        case OpCode.PushFalse: Push(Variant.Bool(false)); break;
                        case OpCode.PushInt: Push(Variant.Int(ins.A)); break;
                        case OpCode.PushFloat: Push(Variant.Float(BitConverter.Int32BitsToSingle(ins.A))); break;
                        case OpCode.PushStr: Push(Variant.Str(LitIds[ins.A])); break;
                        case OpCode.PushEnum: Push(Variant.Enum(ins.A, ins.B)); break;

                        // ----- локали / статики -----
                        case OpCode.LoadLocal: Push(stack[frame.Base + ins.A]); break;
                        case OpCode.StoreLocal: stack[frame.Base + ins.A] = stack[--sp]; break;
                        case OpCode.LoadStatic: Push(Statics[ins.A]); break;
                        case OpCode.StoreStatic: Statics[ins.A] = stack[--sp]; break;

                        // поля текущей подписки listener (блок висит на файбере)
                        case OpCode.LoadAttach: Push(f.AttachFields[ins.A]); break;
                        case OpCode.StoreAttach: f.AttachFields[ins.A] = stack[--sp]; break;
                        case OpCode.PushSelf: Push(f.AttachSelf); break;

                        // ----- хостовые свойства -----
                        case OpCode.LoadField:
                        {
                            var objV = stack[--sp];
                            var obj = _engine.Entities.Resolve(objV);
                            if (obj == null) throw new ScriptError("Чтение свойства у null.");
                            Push(_engine.Host.Getter(ins.A)(_engine, obj));
                            break;
                        }
                        case OpCode.StoreField:
                        {
                            var value = stack[--sp];
                            var objV = stack[--sp];
                            var obj = _engine.Entities.Resolve(objV);
                            if (obj == null) throw new ScriptError("Запись свойства у null.");
                            var setter = _engine.Host.Setter(ins.A);
                            if (setter == null) throw new ScriptError("Свойство только для чтения.");
                            setter(_engine, obj, value);
                            break;
                        }

                        // ----- арифметика (динамическая по типу) -----
                        case OpCode.Add:
                        {
                            var b = stack[--sp]; var a = stack[--sp];
                            if (a.Type == VariantType.Int && b.Type == VariantType.Int)
                                Push(Variant.Int(a.AsInt + b.AsInt));
                            else
                                Push(Variant.Float(a.ToF() + b.ToF()));
                            break;
                        }
                        case OpCode.Sub:
                        {
                            var b = stack[--sp]; var a = stack[--sp];
                            if (a.Type == VariantType.Int && b.Type == VariantType.Int)
                                Push(Variant.Int(a.AsInt - b.AsInt));
                            else
                                Push(Variant.Float(a.ToF() - b.ToF()));
                            break;
                        }
                        case OpCode.Mul:
                        {
                            var b = stack[--sp]; var a = stack[--sp];
                            if (a.Type == VariantType.Int && b.Type == VariantType.Int)
                                Push(Variant.Int(a.AsInt * b.AsInt));
                            else
                                Push(Variant.Float(a.ToF() * b.ToF()));
                            break;
                        }
                        case OpCode.Div:
                        {
                            var b = stack[--sp]; var a = stack[--sp];
                            if (a.Type == VariantType.Int && b.Type == VariantType.Int)
                            {
                                if (b.AsInt == 0) throw new ScriptError("Деление на ноль.");
                                Push(Variant.Int(a.AsInt / b.AsInt));
                            }
                            else
                            {
                                Push(Variant.Float(a.ToF() / b.ToF())); // float: inf допустим
                            }
                            break;
                        }
                        case OpCode.Mod:
                        {
                            var b = stack[--sp]; var a = stack[--sp];
                            if (b.AsInt == 0) throw new ScriptError("Остаток от деления на ноль.");
                            Push(Variant.Int(a.AsInt % b.AsInt));
                            break;
                        }
                        case OpCode.Neg:
                        {
                            var a = stack[--sp];
                            if (a.Type == VariantType.Int) Push(Variant.Int(-a.AsInt));
                            else Push(Variant.Float(-a.ToF()));
                            break;
                        }

                        // ----- сравнения -----
                        case OpCode.Eq:
                        {
                            var b = stack[--sp]; var a = stack[--sp];
                            Push(Variant.Bool(a.Equals(b)));
                            break;
                        }
                        case OpCode.Ne:
                        {
                            var b = stack[--sp]; var a = stack[--sp];
                            Push(Variant.Bool(!a.Equals(b)));
                            break;
                        }
                        case OpCode.Lt:
                        {
                            var b = stack[--sp]; var a = stack[--sp];
                            if (a.Type == VariantType.Int && b.Type == VariantType.Int)
                                Push(Variant.Bool(a.AsInt < b.AsInt));
                            else
                                Push(Variant.Bool(a.ToF() < b.ToF()));
                            break;
                        }
                        case OpCode.Le:
                        {
                            var b = stack[--sp]; var a = stack[--sp];
                            if (a.Type == VariantType.Int && b.Type == VariantType.Int)
                                Push(Variant.Bool(a.AsInt <= b.AsInt));
                            else
                                Push(Variant.Bool(a.ToF() <= b.ToF()));
                            break;
                        }
                        case OpCode.Gt:
                        {
                            var b = stack[--sp]; var a = stack[--sp];
                            if (a.Type == VariantType.Int && b.Type == VariantType.Int)
                                Push(Variant.Bool(a.AsInt > b.AsInt));
                            else
                                Push(Variant.Bool(a.ToF() > b.ToF()));
                            break;
                        }
                        case OpCode.Ge:
                        {
                            var b = stack[--sp]; var a = stack[--sp];
                            if (a.Type == VariantType.Int && b.Type == VariantType.Int)
                                Push(Variant.Bool(a.AsInt >= b.AsInt));
                            else
                                Push(Variant.Bool(a.ToF() >= b.ToF()));
                            break;
                        }
                        case OpCode.Not:
                        {
                            var a = stack[--sp];
                            Push(Variant.Bool(!a.AsBool));
                            break;
                        }

                        // ----- строки -----
                        case OpCode.Concat:
                        {
                            int n = ins.A;
                            var strings = _engine.Strings;
                            strings.BeginBuild();
                            for (int i = sp - n; i < sp; i++)
                                AppendVariant(strings, stack[i]);
                            sp -= n;
                            Push(Variant.Str(strings.EndBuildIntern()));
                            break;
                        }

                        // ----- управление -----
                        case OpCode.Jump: ip = ins.A; break;
                        case OpCode.JumpIfFalse:
                        {
                            var c = stack[--sp];
                            if (!c.AsBool) ip = ins.A;
                            break;
                        }
                        case OpCode.JumpIfTrue:
                        {
                            var c = stack[--sp];
                            if (c.AsBool) ip = ins.A;
                            break;
                        }
                        case OpCode.Pop: sp--; break;

                        // ----- вызовы -----
                        case OpCode.CallScript:
                        {
                            int funcIdx = ins.A;
                            int argc = ins.B;
                            var callee = Prog.Functions[funcIdx];

                            int newBase = sp - argc;
                            int needed = newBase + callee.LocalCount;
                            if (needed > stack.Length)
                            {
                                f.EnsureStack(needed);
                                stack = f.Stack;
                            }
                            // не переданные аргументами локали — Nil
                            for (int i = newBase + argc; i < newBase + callee.LocalCount; i++)
                                stack[i] = Variant.Nil;
                            sp = newBase + callee.LocalCount;

                            frame.Ip = ip;          // сохранить позицию вызывающего
                            f.Sp = sp;
                            f.PushFrame(funcIdx, newBase);
                            goto FrameSwitch;
                        }

                        case OpCode.CallHost:
                        {
                            int argc = ins.B;
                            int argBase = sp - argc;
                            f.Sp = sp;
                            var ctx = new CallContext(stack, argBase, argc, _engine);
                            _engine.Host.Function(ins.A)(ref ctx);
                            sp = argBase;
                            Push(ctx.HasResult ? ctx.Result : Variant.Nil);
                            break;
                        }

                        case OpCode.CallEngine:
                        {
                            int argc = ins.B;
                            int argBase = sp - argc;
                            f.Sp = sp;
                            var res = _engine.ExecEngineOp((EngineOp)ins.A, f, argBase, argc);
                            sp = argBase;
                            Push(res);
                            if (f.KillRequested)
                            {
                                frame.Ip = ip;
                                f.Sp = sp;
                                return RunResult.Completed; // самоуничтожение через Engine.Kill
                            }
                            stack = f.Stack; // движок мог активировать рост? нет, но безопасно
                            break;
                        }

                        case OpCode.Spawn:
                        {
                            int argc = ins.B;
                            int argBase = sp - argc;
                            f.Sp = sp;
                            var handle = _engine.SpawnFiber(ins.A, stack, argBase, argc, f.TriggerId);
                            sp = argBase;
                            Push(handle);
                            break;
                        }

                        // ----- ожидание -----
                        case OpCode.Wait:
                        {
                            var secs = stack[--sp];
                            f.PendingWaitSeconds = secs.ToF();
                            frame.Ip = ip;
                            f.Sp = sp;
                            return RunResult.Waited;
                        }
                        case OpCode.YieldTick:
                        {
                            frame.Ip = ip;
                            f.Sp = sp;
                            return RunResult.Yielded;
                        }

                        case OpCode.Return:
                        {
                            var ret = ins.A == 1 ? stack[--sp] : Variant.Nil;
                            sp = frame.Base;
                            f.FrameCount--;
                            if (f.FrameCount == 0)
                            {
                                f.Sp = sp;
                                return RunResult.Completed;
                            }
                            Push(ret); // результат — вызывающему
                            f.Sp = sp;
                            goto FrameSwitch;
                        }

                        // ----- коллекции -----
                        case OpCode.NewArray:
                        {
                            var size = stack[--sp];
                            Push(_engine.Collections.NewArray(size.AsInt));
                            break;
                        }
                        case OpCode.NewList: Push(_engine.Collections.NewList()); break;
                        case OpCode.NewMap: Push(_engine.Collections.NewMap()); break;

                        case OpCode.ArrayLit:
                        {
                            int n = ins.A;
                            var arr = _engine.Collections.NewArray(n);
                            for (int i = 0; i < n; i++)
                                _engine.Collections.Set(arr, Variant.Int(i), stack[sp - n + i]);
                            sp -= n;
                            Push(arr);
                            break;
                        }

                        case OpCode.Index:
                        {
                            var idx = stack[--sp];
                            var coll = stack[--sp];
                            Push(_engine.Collections.Get(coll, idx));
                            break;
                        }
                        case OpCode.StoreIndex:
                        {
                            var value = stack[--sp];
                            var idx = stack[--sp];
                            var coll = stack[--sp];
                            _engine.Collections.Set(coll, idx, value);
                            break;
                        }
                        case OpCode.Len:
                        {
                            var coll = stack[--sp];
                            Push(Variant.Int(_engine.Collections.Len(coll)));
                            break;
                        }
                        case OpCode.ListAdd:
                        {
                            var value = stack[--sp];
                            var coll = stack[--sp];
                            _engine.Collections.ListAdd(coll, value);
                            Push(Variant.Nil); // вызовы всегда оставляют одно значение
                            break;
                        }
                        case OpCode.ListClear:
                        {
                            var coll = stack[--sp];
                            _engine.Collections.ListClear(coll);
                            Push(Variant.Nil);
                            break;
                        }
                        case OpCode.MapHas:
                        {
                            var key = stack[--sp];
                            var coll = stack[--sp];
                            Push(Variant.Bool(_engine.Collections.MapHas(coll, key)));
                            break;
                        }
                        case OpCode.MapRemove:
                        {
                            var key = stack[--sp];
                            var coll = stack[--sp];
                            _engine.Collections.MapRemove(coll, key);
                            Push(Variant.Nil);
                            break;
                        }

                        case OpCode.IntToFloat:
                        {
                            var a = stack[--sp];
                            Push(Variant.Float(a.AsInt));
                            break;
                        }

                        default:
                            throw new ScriptError($"Неизвестный опкод {ins.Op}.");
                    }
                    }
                }
                catch
                {
                    // фиксируем позицию для точного стектрейса и отдаём наверх
                    if (f.FrameCount > 0)
                        f.Frames[f.FrameCount - 1].Ip = ip;
                    f.Sp = sp;
                    throw;
                }

            FrameSwitch: ;
            }

            f.Sp = sp;
            return RunResult.Completed;
        }

        private void AppendVariant(StringTable strings, Variant v)
        {
            switch (v.Type)
            {
                case VariantType.Str: strings.Append(strings.Get(v.StrId)); break;
                case VariantType.Int: strings.AppendInt(v.AsInt); break;
                case VariantType.Float: strings.AppendFloat(v.AsFloat); break;
                case VariantType.Bool: strings.AppendBool(v.AsBool); break;
                case VariantType.Nil: strings.Append("null"); break;
                case VariantType.Enum:
                {
                    var name = _engine.EnumMemberName(v.EnumTypeId, v.EnumValue);
                    if (name != null) strings.Append(name);
                    else strings.AppendInt(v.EnumValue);
                    break;
                }
                default:
                    strings.Append("<");
                    strings.Append(v.Type.ToString());
                    strings.Append(">");
                    break;
            }
        }

        /// <summary>Скриптовый стектрейс файбера (для отчёта об ошибке).</summary>
        public string BuildTrace(Fiber f)
        {
            var sb = new System.Text.StringBuilder(); // путь ошибки — аллокации допустимы
            for (int i = f.FrameCount - 1; i >= 0; i--)
            {
                var fr = f.Frames[i];
                var chunk = Prog.Functions[fr.Func];
                int at = fr.Ip > 0 ? fr.Ip - 1 : 0;
                int line = at < chunk.Code.Length ? chunk.Code[at].Line : 0;
                sb.Append("  в ").Append(chunk.Name)
                  .Append(" (").Append(chunk.File).Append(':').Append(line).Append(")\n");
            }
            return sb.ToString();
        }
    }
}
