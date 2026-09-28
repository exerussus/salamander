using System;
using System.Collections.Generic;
using Dsl.Codegen;

namespace Dsl.Runtime
{
    /// <summary>Что делать с файбером, который стоит ВНУТРИ изменённой функции.</summary>
    public enum HotSwapFiberPolicy : byte
    {
        /// <summary>Дорабатывает старую версию до конца; все новые вызовы — уже в новую.</summary>
        FinishOnOldCode,
        /// <summary>Убить такие файберы (как Engine.Kill); остальные не трогаются.</summary>
        Kill,
    }

    /// <summary>Итог <see cref="ScriptEngine.TryHotSwap"/>.</summary>
    public sealed class HotSwapReport
    {
        /// <summary>Замена применена. false — программа несовместима, в движке всё как было.</summary>
        public bool Applied;
        /// <summary>Почему не применено (null, если применено).</summary>
        public string Reason;
        /// <summary>Функции с новым телом (имена чанков: "Mods.Balance.Get", "main.OnBuild"...).</summary>
        public readonly List<string> ChangedFunctions = new List<string>();
        /// <summary>Функции, у которых сдвинулись только номера строк (перенесены без влияния на файберы).</summary>
        public int LinesOnlyUpdated;
        /// <summary>Кадров, перенаправленных на отставное тело (FinishOnOldCode).</summary>
        public int FramesOnOldCode;
        /// <summary>Файберов убито (политика Kill).</summary>
        public int FibersKilled;
        /// <summary>Изменились инициализаторы полей: readonly-поля пересчитаны, изменяемые сохранены.</summary>
        public bool ReadOnlyFieldsReinitialized;
        /// <summary>Ничего не поменялось вовсе (тот же код) — замена ничего не делала.</summary>
        public bool NoChanges => Applied && ChangedFunctions.Count == 0 && LinesOnlyUpdated == 0;

        internal static HotSwapReport Reject(string reason) => new HotSwapReport { Applied = false, Reason = reason };

        public override string ToString()
        {
            if (!Applied) return "горячая замена невозможна: " + Reason;
            if (NoChanges) return "код не изменился";
            var sb = new System.Text.StringBuilder();
            sb.Append("горячая замена: ");
            sb.Append(ChangedFunctions.Count == 0 ? "тела не менялись" : string.Join(", ", ChangedFunctions));
            if (LinesOnlyUpdated > 0) sb.Append($"; строк сдвинуто у {LinesOnlyUpdated} функций");
            if (FramesOnOldCode > 0) sb.Append($"; дорабатывают старую версию: {FramesOnOldCode} кадров");
            if (FibersKilled > 0) sb.Append($"; убито файберов: {FibersKilled}");
            if (ReadOnlyFieldsReinitialized) sb.Append("; readonly-поля пересчитаны");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Горячая замена ТЕЛ функций без перезагрузки программы.
    ///
    /// Условие — неизменная раскладка: тот же набор функций с теми же
    /// сигнатурами, те же поля (ключи и слоты), триггеры, listener-ы, архетипы
    /// и таблицы обработчиков. Меняться могут только тела (и номера строк).
    /// Компилятор детерминирован, поэтому при такой правке все индексы у новой
    /// программы те же, что у загруженной, и подмена сводится к
    /// Functions[i] = новое тело: вызовы, обработчики событий и цепочки слоёв
    /// ссылаются на функцию по индексу и сразу идут в новую версию. Поля,
    /// подписки, флаги триггеров и файберы не трогаются.
    ///
    /// Единственное, что пересчитывается, — строковые литералы: их порядок
    /// в пуле зависит от кода, поэтому пул новой программы дописывается к
    /// старому, а PushStr в новых телах перенумеровывается.
    ///
    /// Файбер, стоящий внутри изменённой функции (например, в wait), хранит
    /// позицию в СТАРОМ байткоде. Старое тело уезжает в хвост Functions, и
    /// кадр перенаправляется туда — файбер дорабатывает старую версию, а все
    /// её вызовы, как и любые другие, идут по индексам в живые функции.
    /// Не начатый кадр (ip = 0, файбер ещё в очереди) сразу получает новое тело.
    /// </summary>
    public sealed partial class ScriptEngine
    {
        /// <summary>Программа загружена (есть во что подменять тела).</summary>
        public bool HasProgram => _prog != null;

        /// <summary>С какого индекса литералы интернированы после заморозки статического сегмента.</summary>
        private int _hotLitFrom = int.MaxValue;

        /// <summary>
        /// Заменить тела изменившихся функций, не перезагружая программу. При
        /// несовместимости (правка не только тел) ничего не делает и возвращает
        /// причину — тогда нужна обычная <see cref="LoadProgram"/>. Нельзя звать
        /// изнутри исполнения скрипта (из хостового метода, вызванного скриптом).
        /// </summary>
        public HotSwapReport TryHotSwap(CompiledProgram next, HotSwapFiberPolicy policy = HotSwapFiberPolicy.FinishOnOldCode)
        {
            if (next == null) return HotSwapReport.Reject("нет новой программы");
            if (_prog == null) return HotSwapReport.Reject("программа ещё не загружена");
            if (_current != null) return HotSwapReport.Reject("замена изнутри исполняющегося скрипта");

            string why = LayoutMismatch(_prog, next);
            if (why != null) return HotSwapReport.Reject(why);

            var report = new HotSwapReport { Applied = true };
            var old = _prog;
            int live = old.LiveFunctions;

            // --- пул литералов: старый как есть + новые строки в конец ---
            var lits = new List<string>(old.StringLiterals);
            var litIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < lits.Count; i++)
                if (lits[i] != null && !litIndex.ContainsKey(lits[i])) litIndex[lits[i]] = i;
            int MapLit(int newIdx)
            {
                string s = LitAt(next.StringLiterals, newIdx);
                if (s != null && litIndex.TryGetValue(s, out int at)) return at;
                lits.Add(s);
                if (s != null) litIndex[s] = lits.Count - 1;
                return lits.Count - 1;
            }

            // --- какие функции изменились ---
            var bodyChanged = new bool[live];
            var functions = new List<Chunk>(live + 8);
            for (int i = 0; i < live; i++)
            {
                var o = old.Functions[i];
                var n = next.Functions[i];
                switch (Compare(o, old.StringLiterals, n, next.StringLiterals))
                {
                    case CodeDiff.Same:
                        functions.Add(o);
                        break;
                    case CodeDiff.LinesOnly:
                        // те же инструкции — позиции файберов остаются верными
                        functions.Add(Relocate(n, MapLit));
                        report.LinesOnlyUpdated++;
                        break;
                    default:
                        functions.Add(Relocate(n, MapLit));
                        bodyChanged[i] = true;
                        report.ChangedFunctions.Add(n.Name ?? ("#" + i));
                        break;
                }
            }

            // --- кадры внутри изменённых тел: в хвост (или убить) ---
            // отставные тела прошлых замен переживают эту, только если на них
            // ещё кто-то стоит; хвост собирается заново и кадры перенумеровываются
            var retiredIndex = new Dictionary<Chunk, int>();
            var toKill = new List<Fiber>();
            for (int s = 0; s < _fibers.SlotCount; s++)
            {
                var f = _fibers.Slot(s);
                if (f == null || f.State == FiberState.Free) continue;
                bool kill = false;
                for (int k = 0; k < f.FrameCount; k++)
                {
                    int fn = f.Frames[k].Func;
                    bool retiredAlready = fn >= live;
                    bool inChanged = !retiredAlready && fn >= 0 && bodyChanged[fn]
                                     && !CanStartNewBody(f.Frames[k], old.Functions[fn], functions[fn]);
                    if (!retiredAlready && !inChanged) continue;
                    if (inChanged && policy == HotSwapFiberPolicy.Kill) { kill = true; break; }
                }
                if (kill) toKill.Add(f);
            }

            foreach (var f in toKill) { _fibers.Return(f); report.FibersKilled++; }

            for (int s = 0; s < _fibers.SlotCount; s++)
            {
                var f = _fibers.Slot(s);
                if (f == null || f.State == FiberState.Free) continue;
                for (int k = 0; k < f.FrameCount; k++)
                {
                    ref var fr = ref f.Frames[k];
                    Chunk oldBody;
                    if (fr.Func >= live) oldBody = old.Functions[fr.Func];                 // отставное с прошлой замены
                    else if (fr.Func >= 0 && bodyChanged[fr.Func]
                             && !CanStartNewBody(fr, old.Functions[fr.Func], functions[fr.Func]))
                        oldBody = old.Functions[fr.Func];
                    else continue;                                                          // ещё не начат — начнёт новое тело
                    if (!retiredIndex.TryGetValue(oldBody, out int at))
                    {
                        at = functions.Count;
                        functions.Add(oldBody);
                        retiredIndex[oldBody] = at;
                    }
                    fr.Func = at;
                    report.FramesOnOldCode++;
                }
            }

            // --- новая программа: старые таблицы (они равны новым) + новые тела ---
            var merged = old.CloneForHotSwap();
            merged.Functions = functions.ToArray();
            merged.LiveFunctionCount = functions.Count > live ? live : 0;
            merged.StringLiterals = lits.ToArray();

            // интернировать дописанные литералы; они в динамическом сегменте
            // строк, поэтому Collect метит их явно (см. MarkHotLiterals)
            if (lits.Count > _litIds.Length)
            {
                var ids = new int[lits.Count];
                Array.Copy(_litIds, ids, _litIds.Length);
                for (int i = _litIds.Length; i < ids.Length; i++) ids[i] = Strings.Intern(lits[i]);
                if (_hotLitFrom > _litIds.Length) _hotLitFrom = _litIds.Length;
                _litIds = ids;
            }

            _prog = merged;
            _vm.Prog = merged;
            _vm.LitIds = _litIds;

            // инициализаторы полей поменялись: readonly — рецепт, берётся из
            // ТЕКУЩЕЙ программы (как при загрузке сейва); изменяемые — состояние,
            // его не трогаем
            if (bodyChanged[CompiledProgram.InitFuncIndex])
            {
                ReinitReadOnlyStatics();
                report.ReadOnlyFieldsReinitialized = true;
            }
            return report;
        }

        /// <summary>
        /// Не начатый кадр (ip = 0: файбер создан и ждёт в очереди) можно отдать
        /// новому телу — но только при том же числе локалей: под них уже размечен
        /// стек, и с большим числом новое тело писало бы поверх стека операндов.
        /// </summary>
        private static bool CanStartNewBody(Frame fr, Chunk oldBody, Chunk newBody) =>
            fr.Ip == 0 && oldBody.LocalCount == newBody.LocalCount;

        /// <summary>Прогнать &lt;init&gt; заново и оставить из результата только readonly-слоты.</summary>
        private void ReinitReadOnlyStatics()
        {
            var keep = (Variant[])_statics.Clone();
            RunInit();
            var ro = _prog.StaticReadOnly;
            for (int i = 0; i < _statics.Length; i++)
            {
                bool isRo = ro != null && i < ro.Length && ro[i];
                if (!isRo) _statics[i] = keep[i];
            }
        }

        /// <summary>Литералы, дописанные горячей заменой, живут в динамическом сегменте — не дать их подмести.</summary>
        private void MarkHotLiterals()
        {
            for (int i = _hotLitFrom; i < _litIds.Length; i++) Strings.Mark(_litIds[i]);
        }

        // ===================================================================
        // Сравнение программ
        // ===================================================================

        private enum CodeDiff : byte { Same, LinesOnly, Body }

        private static CodeDiff Compare(Chunk o, string[] oLits, Chunk n, string[] nLits)
        {
            if (o.Code.Length != n.Code.Length || o.LocalCount != n.LocalCount) return CodeDiff.Body;
            bool lines = !string.Equals(o.File, n.File, StringComparison.Ordinal);
            for (int i = 0; i < o.Code.Length; i++)
            {
                var a = o.Code[i];
                var b = n.Code[i];
                if (a.Op != b.Op || a.B != b.B) return CodeDiff.Body;
                if (a.Op == OpCode.PushStr)
                {
                    if (!string.Equals(LitAt(oLits, a.A), LitAt(nLits, b.A), StringComparison.Ordinal)) return CodeDiff.Body;
                }
                else if (a.A != b.A) return CodeDiff.Body;
                if (a.Line != b.Line) lines = true;
            }
            return lines ? CodeDiff.LinesOnly : CodeDiff.Same;
        }

        private static string LitAt(string[] lits, int i) => (uint)i < (uint)lits.Length ? lits[i] : null;

        private static Chunk Relocate(Chunk n, Func<int, int> mapLit)
        {
            var code = new Instr[n.Code.Length];
            for (int i = 0; i < code.Length; i++)
            {
                var ins = n.Code[i];
                if (ins.Op == OpCode.PushStr) ins.A = mapLit(ins.A);
                code[i] = ins;
            }
            return new Chunk { Name = n.Name, File = n.File, ParamCount = n.ParamCount, LocalCount = n.LocalCount, Code = code };
        }

        /// <summary>
        /// null — раскладка совпадает (меняться могут только тела функций);
        /// иначе — что именно изменилось, человеческим языком.
        /// </summary>
        private static string LayoutMismatch(CompiledProgram o, CompiledProgram n)
        {
            int live = o.LiveFunctions;
            if (n.LiveFunctions != live || n.Functions.Length != n.LiveFunctions)
                return $"изменился набор функций ({live} → {n.LiveFunctions}): добавлен или удалён член, слой или обработчик";
            for (int i = 0; i < live; i++)
            {
                var a = o.Functions[i];
                var b = n.Functions[i];
                if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal))
                    return $"изменился набор функций: на месте '{a.Name}' теперь '{b.Name}'";
                if (a.ParamCount != b.ParamCount)
                    return $"у '{a.Name}' изменилось число параметров ({a.ParamCount} → {b.ParamCount})";
            }

            if (o.StaticCount != n.StaticCount) return "изменился набор полей";
            if (!SameSeq(o.StaticKeys, n.StaticKeys, out int si))
                return $"изменились поля (ключ '{At(o.StaticKeys, si)}' → '{At(n.StaticKeys, si)}')";
            if (!SameSeq(o.StaticReadOnly, n.StaticReadOnly, out si)) return $"поле '{At(o.StaticKeys, si)}' стало/перестало быть readonly";
            if (!SameSeq(o.StaticDeclared, n.StaticDeclared, out si)) return $"поле '{At(o.StaticKeys, si)}' объявлено/убрано в скрипте";
            int dn = Math.Max(o.StaticDefaults?.Length ?? 0, n.StaticDefaults?.Length ?? 0);
            for (int i = 0; i < dn; i++)
                if (!SameDefault(o, n, i)) return $"изменился дефолт вида у поля '{At(o.StaticKeys, i)}'";

            if (!SameSeq(o.Modules, n.Modules, out _)) return "изменился набор модулей";

            if (o.Triggers.Length != n.Triggers.Length) return "изменился набор триггеров";
            for (int i = 0; i < o.Triggers.Length; i++)
            {
                var a = o.Triggers[i]; var b = n.Triggers[i];
                if (a.Name != b.Name || a.Module != b.Module || a.ModuleIndex != b.ModuleIndex || a.ActionFuncIndex != b.ActionFuncIndex)
                    return $"изменился триггер '{a.Name}'";
            }
            if (!SameTable(o.EventHandlers, n.EventHandlers, (x, y) => x.TriggerId == y.TriggerId && x.FuncIndex == y.FuncIndex))
                return "изменились обработчики событий (добавлен или удалён event)";

            if (o.Listeners.Length != n.Listeners.Length) return "изменился набор listener";
            for (int i = 0; i < o.Listeners.Length; i++)
            {
                var a = o.Listeners[i]; var b = n.Listeners[i];
                if (a.Name != b.Name || a.Module != b.Module || a.FieldCount != b.FieldCount
                    || a.InitFuncIndex != b.InitFuncIndex || a.OnSubscribeFunc != b.OnSubscribeFunc
                    || a.OnUnsubscribeFunc != b.OnUnsubscribeFunc || !SameSeq(a.FieldNames, b.FieldNames, out _))
                    return $"изменился listener '{a.Name}'";
            }
            if (!SameTable(o.ListenerHandlerFunc, n.ListenerHandlerFunc, (x, y) => x == y))
                return "изменились обработчики listener";
            if (!SameSeq(o.EventHasListenerHandlers, n.EventHasListenerHandlers, out _))
                return "изменились обработчики listener";

            if (o.ArchetypeKinds.Length != n.ArchetypeKinds.Length) return "изменились виды архетипов";
            for (int k = 0; k < o.ArchetypeKinds.Length; k++)
            {
                var a = o.ArchetypeKinds[k]; var b = n.ArchetypeKinds[k];
                if (a.Name != b.Name || a.EventCount != b.EventCount || !SameSeq(a.EventNames, b.EventNames, out _))
                    return $"изменился вид архетипа '{a.Name}'";
                if (!SameSeq(a.Ids, b.Ids, out int ii))
                    return $"изменился набор сущностей вида '{a.Name}' (id '{At(a.Ids, ii)}' → '{At(b.Ids, ii)}')";
                if (!SameTable(a.Handlers, b.Handlers, (x, y) => x.Func == y.Func && x.ModuleIndex == y.ModuleIndex))
                    return $"изменились обработчики у сущностей вида '{a.Name}' (добавлен или удалён event)";
            }

            int en = o.ScriptEnums?.Length ?? 0;
            if (en != (n.ScriptEnums?.Length ?? 0)) return "изменился набор енумов";
            for (int i = 0; i < en; i++)
            {
                var a = o.ScriptEnums[i]; var b = n.ScriptEnums[i];
                if (a.Id != b.Id || a.Name != b.Name || !SameSeq(a.Members, b.Members, out _))
                    return $"изменился енум '{a.Name}'";
            }
            return null;
        }

        private static string At(string[] a, int i) => a != null && (uint)i < (uint)a.Length ? a[i] : "∅";

        private static bool SameSeq<T>(T[] a, T[] b, out int firstDiff)
        {
            int la = a?.Length ?? 0, lb = b?.Length ?? 0;
            int n = Math.Min(la, lb);
            var eq = EqualityComparer<T>.Default;
            for (int i = 0; i < n; i++)
                if (!eq.Equals(a[i], b[i])) { firstDiff = i; return false; }
            firstDiff = n;
            return la == lb;
        }

        private static bool SameTable<T>(T[][] a, T[][] b, Func<T, T, bool> eq)
        {
            int la = a?.Length ?? 0, lb = b?.Length ?? 0;
            if (la != lb) return false;
            for (int i = 0; i < la; i++)
            {
                var ra = a[i]; var rb = b[i];
                int na = ra?.Length ?? 0, nb = rb?.Length ?? 0;
                if (na != nb) return false;
                for (int j = 0; j < na; j++) if (!eq(ra[j], rb[j])) return false;
            }
            return true;
        }

        /// <summary>Дефолты вида: строковый лежит индексом в пуле — сравниваем сами строки.</summary>
        private static bool SameDefault(CompiledProgram o, CompiledProgram n, int i)
        {
            var a = o.StaticDefaults != null && i < o.StaticDefaults.Length ? o.StaticDefaults[i] : Variant.Nil;
            var b = n.StaticDefaults != null && i < n.StaticDefaults.Length ? n.StaticDefaults[i] : Variant.Nil;
            if (a.Type == VariantType.Str && b.Type == VariantType.Str)
                return LitAt(o.StringLiterals, a.StrId) == LitAt(n.StringLiterals, b.StrId);
            return a.Equals(b);
        }
    }
}
