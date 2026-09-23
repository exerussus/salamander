using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Dsl.Compilation;

namespace Dsl.Tooling
{
    /// <summary>
    /// Языковой сервис Salamander: автодополнение, подсказка параметров, hover,
    /// переход к определению — по манифесту API хоста и синтакс-индексу
    /// воркспейса. Движок-независим (никакого Unity и никакого протокола):
    /// LSP-сервер переводит результаты в JSON-RPC, встроенная IDE — в свой UI.
    ///
    /// Состояние (манифест, индекс, источник текстов) принадлежит владельцу
    /// сервиса; сам сервис ничего не читает с диска. Не потокобезопасен.
    /// </summary>
    public sealed class LanguageService
    {
        /// <summary>Манифест API хоста; null — манифеста нет (язык и Engine.* всё равно подсказываются).</summary>
        public ApiManifest Api;

        /// <summary>Синтакс-индекс всех файлов воркспейса.</summary>
        public readonly WorkspaceIndex Index = new WorkspaceIndex();

        /// <summary>Текст файла по ключу (оверлей несохранённого буфера или диск); null — нет файла.</summary>
        public Func<string, string> TextProvider = _ => null;

        /// <summary>
        /// Ранг файла в порядке загрузки (модуль → файл) — по нему подсказка
        /// раскладывает версии члена. null или int.MaxValue — порядок индекса
        /// (приблизительный: так обходит папки владелец индекса).
        /// </summary>
        public Func<string, int> FileOrder;

        /// <summary>
        /// Как назвать файл в подсказке («мод/путь.sal»): при раскладке «одна
        /// сущность — один файл» у базы и мода одинаковые имена файлов, и без
        /// модуля «sword.sal:4 → sword.sal:3» не различить. null — сам ключ
        /// (путь сокращается до имени файла).
        /// </summary>
        public Func<string, string> FileLabel;

        private string GetText(string file) => TextProvider?.Invoke(file);

        /// <summary>
        /// Встроенный Math уступает любому своему имени Math — API, классу или
        /// енуму игры и скриптовой декларации, — ровно как компилятор.
        /// </summary>
        private bool MathShadowed()
        {
            if (Api != null)
            {
                foreach (var a in Api.Apis ?? Array.Empty<ApiManifest.ApiDef>()) if (a.Name == "Math") return true;
                foreach (var c in Api.Classes ?? Array.Empty<ApiManifest.ClassDef>()) if (c.Name == "Math") return true;
                foreach (var e in Api.Enums ?? Array.Empty<ApiManifest.EnumDef>()) if (e.Name == "Math") return true;
            }
            foreach (var fi in Index.Files)
                foreach (var d in fi.Value.Decls)
                    if (d.Name == "Math") return true;
            return false;
        }

        // ===================================================================
        // Автодополнение
        // ===================================================================

        public List<CompletionItem> Complete(string file, int line1, int col1)
        {
            var lineText = TextUtil.GetLine(GetText(file), line1) ?? "";
            var before = lineText.Substring(0, Math.Min(col1 - 1, lineText.Length));

            var items = new List<CompletionItem>();
            void Add(string label, CompletionKind kind, string detail, string doc = null, string insert = null, bool snippet = false)
            {
                items.Add(new CompletionItem
                {
                    Label = label,
                    Kind = kind,
                    Detail = detail,
                    Documentation = doc,
                    InsertText = insert,
                    IsSnippet = insert != null && snippet,
                });
            }

            // 1) Engine.<...>  /  Api.Weapon.<...>
            // владелец может быть составным именем API — забираем всю цепочку
            var mDot = Regex.Match(before, @"((?:\w+\.)*\w+)\.\w*$");
            if (mDot.Success)
            {
                string target = mDot.Groups[1].Value;
                if (target == "Engine")
                {
                    foreach (var em in EngineDocs.Methods)
                        Add(em.Name, CompletionKind.Method, em.Signature, em.Summary,
                            insert: ApiFormat.CallSnippet(em.Name, ApiFormat.ParamLabels(em)), snippet: true);
                    return items;
                }
                if (target == "Math" && !MathShadowed())
                {
                    foreach (var mm in EngineDocs.MathMethods)
                        Add(mm.Name, CompletionKind.Method, mm.Signature, mm.Summary,
                            insert: ApiFormat.CallSnippet(mm.Name, ApiFormat.ParamLabels(mm)), snippet: true);
                    foreach (var (cn, ct, cd) in EngineDocs.MathConsts)
                        Add(cn, CompletionKind.Constant, $"Math.{cn}: {ct}", cd);
                    return items;
                }
                // API хоста: сначала методы самого API (если такое имя есть),
                // затем следующие сегменты составных имён под этим префиксом —
                // "Api." предлагает Weapon/Parts, "Api.Weapon." предлагает методы
                bool anyApi = false;
                if (Api?.Apis != null)
                {
                    foreach (var api in Api.Apis)
                        if (api.Name == target)
                        {
                            anyApi = true;
                            foreach (var me in api.Methods)
                                Add(me.Name, CompletionKind.Method, ApiFormat.MethodSig(api.Name, me), ApiFormat.MethodDocMd(me),
                                    insert: ApiFormat.CallSnippet(me.Name, ApiFormat.ParamLabels(me)), snippet: true);
                            // константы: без скобок, поэтому и вставляются как есть
                            foreach (var c in api.Consts ?? Array.Empty<ApiManifest.ApiConstDef>())
                                Add(c.Name, CompletionKind.Constant, ApiFormat.ConstSig(api.Name, c), c.Doc);
                        }

                    string prefix = target + ".";
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var api in Api.Apis)
                    {
                        if (!api.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        string rest = api.Name.Substring(prefix.Length);
                        int dot = rest.IndexOf('.');
                        string seg = dot < 0 ? rest : rest.Substring(0, dot);
                        if (seg.Length == 0 || !seen.Add(seg)) continue;
                        anyApi = true;
                        string nodeName = target + "." + seg;
                        Add(seg, dot < 0 ? CompletionKind.Module : CompletionKind.Function,
                            dot < 0 ? api.Name : nodeName,
                            dot < 0 ? api.Summary
                                    : ApiFormat.NamespaceSummary(Api, nodeName) ?? "пространство имён API");
                    }
                }
                if (anyApi) return items;
                // енумы (манифест + скриптовые)
                if (Api?.Enums != null)
                    foreach (var en in Api.Enums)
                        if (en.Name == target)
                        {
                            for (int i = 0; i < en.Members.Length; i++)
                                Add(en.Members[i], CompletionKind.EnumMember, $"{en.Name}.{en.Members[i]}", ApiFormat.MemberDoc(en, i));
                            return items;
                        }
                foreach (var fi in Index.Files)
                    foreach (var d in fi.Value.Decls)
                        if (d.Name == target && (d.Kind == "enum" || d.Kind == "class"))
                        {
                            foreach (var ch in d.Children)
                                Add(ch.Name,
                                    ch.Kind == "func" ? CompletionKind.Method
                                    : ch.Kind == "member" ? CompletionKind.EnumMember
                                    : ch.Kind == "const" ? CompletionKind.Keyword
                                    : CompletionKind.Field,
                                    $"{d.Kind} {d.Name}",
                                    insert: ch.Kind == "func" ? $"{ch.Name}($1)$0" : null,
                                    snippet: ch.Kind == "func");
                            return items;
                        }

                // цель — ЗНАЧЕНИЕ (локаль/параметр/поле). Тип угадываем по
                // объявлению «Тип имя» выше по файлу (event OnDeath(Unit killer...)
                // или Unit u = ...); нашли класс хоста — его свойства, нет —
                // объединение свойств всех сущностей (лучше, чем тишина)
                if (Api?.Classes != null && Api.Classes.Length > 0)
                {
                    var cls = GuessValueClass(GetText(file), line1, col1, target);
                    if (cls != null)
                    {
                        foreach (var pr in cls.Props)
                            Add(pr.Name, CompletionKind.Property, $"{cls.Name}.{pr.Name}: {pr.Type}", pr.Doc);
                        foreach (var me in cls.Methods ?? Array.Empty<ApiManifest.MethodDef>())
                            Add(me.Name, CompletionKind.Method, ApiFormat.MethodSig(cls.Name, me), ApiFormat.MethodDocMd(me),
                                insert: ApiFormat.CallSnippet(me.Name, ApiFormat.ParamLabels(me)), snippet: true);
                        return items;
                    }
                    foreach (var c in Api.Classes)
                    {
                        foreach (var pr in c.Props)
                            Add(pr.Name, CompletionKind.Property, $"{c.Name}: {pr.Type}", pr.Doc);
                        foreach (var me in c.Methods ?? Array.Empty<ApiManifest.MethodDef>())
                            Add(me.Name, CompletionKind.Method, ApiFormat.MethodSig(c.Name, me), ApiFormat.MethodDocMd(me),
                                insert: ApiFormat.CallSnippet(me.Name, ApiFormat.ParamLabels(me)), snippet: true);
                    }
                }
                return items;
            }

            // 1.5) new Damage(<...>) — поля структуры: именованные аргументы это
            // единственное место в языке, где они есть, и подсказать их важнее всего
            var mNewArgs = Regex.Match(before, @"\bnew\s+(\w+)\s*\(([^()]*)$");
            if (mNewArgs.Success && Api?.Structs != null)
            {
                string typeName = mNewArgs.Groups[1].Value;
                string already = mNewArgs.Groups[2].Value;
                foreach (var st in Api.Structs)
                {
                    if (st.Name != typeName) continue;
                    foreach (var f in st.Fields)
                    {
                        // уже заданные поля не предлагаем повторно
                        if (Regex.IsMatch(already, @"\b" + Regex.Escape(f.Name) + @"\s*:"))
                            continue;
                        Add(f.Name, CompletionKind.Field, $"{f.Name}: {f.Type}" + (f.Default == null ? "" : $" = {f.Default}"),
                            f.Doc, insert: f.Name + ": $0", snippet: true);
                    }
                    return items;
                }
            }

            // 1.6) после "new " — типы, которые можно сконструировать
            if (Regex.IsMatch(before, @"\bnew\s+\w*$"))
            {
                if (Api?.Structs != null)
                    foreach (var st in Api.Structs)
                        Add(st.Name, CompletionKind.Struct, "структура", st.Summary,
                            insert: st.Name + "($0)", snippet: true);
                Add("List", CompletionKind.Struct, "new List<T>()", null, "List<${1:float}>()", true);
                Add("Map", CompletionKind.Struct, "new Map<K, V>()", null, "Map<${1:string}, ${2:float}>()", true);
                return items;
            }

            // 2) event <...> — набор событий зависит от того, в чём мы стоим
            if (Regex.IsMatch(before, @"\bevent\s+\w*$"))
            {
                var encl = Index.EnclosingDecl(file, line1);
                ApiManifest.EventDef[] events = Api?.Events;
                if (encl != null && Api?.Archetypes != null)
                    foreach (var k in Api.Archetypes)
                        if (k.Name == encl.Kind) { events = k.Events; break; }
                if (events != null)
                    foreach (var ev in events)
                        Add(ev.Name, CompletionKind.Event, ApiFormat.EventSig(ev), ev.Summary,
                            insert: ApiFormat.EventSnippet(ev), snippet: true);
                if (encl != null && encl.Kind == "listener")
                {
                    Add("OnSubscribe", CompletionKind.Event, "при Engine.Attach", null, "OnSubscribe()\n{\n\t$0\n}", true);
                    Add("OnUnsubscribe", CompletionKind.Event, "при detach (без wait/spawn)", null, "OnUnsubscribe()\n{\n\t$0\n}", true);
                }
                return items;
            }

            // 3) голый идентификатор: ключевые слова + типы + глобалы + API
            foreach (var kw in EngineDocs.Keywords) Add(kw, CompletionKind.Keyword, null);
            foreach (var (chainWord, chainDoc) in EngineDocs.ChainWords) Add(chainWord, CompletionKind.Keyword, null, chainDoc);
            foreach (var tp in EngineDocs.Types) Add(tp, CompletionKind.Class, null);
            Add("Engine", CompletionKind.Module, "встроенный класс движка");
            if (!MathShadowed()) Add("Math", CompletionKind.Module, "встроенная математика: Min, Max, Clamp, Lerp, Round, …");
            if (Api?.Apis != null) foreach (var api in Api.Apis) Add(api.Name, CompletionKind.Module, api.Summary ?? "API игры");
            if (Api?.Structs != null) foreach (var st in Api.Structs) Add(st.Name, CompletionKind.Struct, st.Summary ?? "структура");
            if (Api?.Enums != null) foreach (var en in Api.Enums) Add(en.Name, CompletionKind.Enum, en.Summary ?? "enum хоста");
            if (Api?.Classes != null) foreach (var c in Api.Classes) Add(c.Name, CompletionKind.Class, c.Summary ?? "сущность игры");
            foreach (var fi in Index.Files)
                foreach (var d in fi.Value.Decls)
                    Add(d.Name, d.Kind == "enum" ? CompletionKind.Enum : CompletionKind.Class, d.Kind);
            return items;
        }

        /// <summary>
        /// Тип значения по ближайшему объявлению «Тип имя» выше курсора:
        /// параметры событий/функций и локали с явным типом. Возвращает класс
        /// хоста из манифеста или null.
        /// </summary>
        public ApiManifest.ClassDef GuessValueClass(string text, int line1, int col1, string name)
        {
            if (text == null || Api?.Classes == null) return null;
            int cursor = TextUtil.OffsetOf(text, line1, col1);
            var rx = new Regex($@"\b([A-Za-z_]\w*)\s+{Regex.Escape(name)}\b");
            string best = null;
            foreach (Match m in rx.Matches(text))
            {
                if (m.Index <= cursor) best = m.Groups[1].Value; // ближайшее ДО курсора побеждает
                else if (best == null) { best = m.Groups[1].Value; break; } // иначе первое после
            }
            if (best == null) return null;
            foreach (var c in Api.Classes)
                if (c.Name == best) return c;
            return null;
        }

        // ===================================================================
        // Подсказка параметров: активная сигнатура + текущий аргумент
        // ===================================================================

        public SignatureInfo SignatureHelp(string file, int line1, int col1)
        {
            var lineText = TextUtil.GetLine(GetText(file), line1) ?? "";
            var before = lineText.Substring(0, Math.Min(col1 - 1, lineText.Length));

            // до внутренней незакрытой '(' (строки грубо вычищаем)
            var clean = Regex.Replace(before, "\"(?:\\\\.|[^\"])*\"?", m => new string(' ', m.Length));
            int depth = 0, open = -1, commas = 0;
            for (int i = clean.Length - 1; i >= 0; i--)
            {
                char c = clean[i];
                if (c == ')') depth++;
                else if (c == '(')
                {
                    if (depth == 0) { open = i; break; }
                    depth--;
                }
            }
            if (open < 0) return null;
            for (int i = open + 1, d2 = 0; i < clean.Length; i++)
            {
                char c = clean[i];
                if (c == '(') d2++;
                else if (c == ')') d2--;
                else if (c == ',' && d2 == 0) commas++;
            }

            var head = clean.Substring(0, open);
            var m2 = Regex.Match(head, "(?:((?:\\w+\\.)*\\w+)\\.)?(\\w+)\\s*$");
            if (!m2.Success) return null;
            string owner = m2.Groups[1].Value;
            string method = m2.Groups[2].Value;

            string label = null, doc = null;
            List<string> plabels = null;
            if (owner == "Engine")
            {
                foreach (var em in EngineDocs.Methods)
                    if (em.Name == method) { label = em.Signature; doc = em.Summary; plabels = ApiFormat.ParamLabels(em); break; }
            }
            else if (owner == "Math" && !MathShadowed())
            {
                foreach (var mm in EngineDocs.MathMethods)
                    if (mm.Name == method) { label = mm.Signature; doc = mm.Summary; plabels = ApiFormat.ParamLabels(mm); break; }
            }
            else if (owner.Length > 0 && Api?.Apis != null)
            {
                foreach (var api in Api.Apis)
                    if (api.Name == owner)
                        foreach (var me in api.Methods)
                            if (me.Name == method)
                            { label = ApiFormat.MethodSig(api.Name, me); doc = ApiFormat.MethodDocMd(me); plabels = ApiFormat.ParamLabels(me); break; }
            }
            if (label == null && owner.Length > 0 && Api?.Classes != null)
            {
                // владелец — не API, а значение: basket.AddPerk(<тут>). Тип берём
                // по объявлению выше по файлу, иначе — по уникальности имени метода
                var vcls = GuessValueClass(GetText(file), line1, col1, owner);
                foreach (var c in Api.Classes)
                {
                    if (vcls != null && c.Name != vcls.Name) continue;
                    foreach (var me in c.Methods ?? Array.Empty<ApiManifest.MethodDef>())
                        if (me.Name == method)
                        { label = ApiFormat.MethodSig(c.Name, me); doc = ApiFormat.MethodDocMd(me); plabels = ApiFormat.ParamLabels(me); break; }
                    if (label != null) break;
                }
            }
            if (label == null) return null;

            return new SignatureInfo
            {
                Label = label,
                Documentation = doc,
                Parameters = plabels,
                ActiveParameter = Math.Min(commas, Math.Max(0, plabels.Count - 1)),
            };
        }

        // ===================================================================
        // Hover (markdown)
        // ===================================================================

        public string Hover(string file, int line1, int col1)
        {
            var lineText = TextUtil.GetLine(GetText(file), line1) ?? "";
            var (word, wordCol) = TextUtil.WordAt(lineText, col1);
            if (word == null) return null;

            // мерж-цепочка: слово before/after/replace/base — справка и версии члена;
            // имя члена в объявлении — версии дописываются к обычной подсказке
            var chainWordMd = ChainWordHover(file, line1, lineText, word, wordCol);
            if (chainWordMd != null) return chainWordMd;
            string versionsMd = MemberVersionsHover(file, line1, lineText, word, wordCol);

            string md = null;
            bool afterEngine = TextUtil.HasPrefix(lineText, wordCol, "Engine.");

            if (afterEngine)
            {
                foreach (var em in EngineDocs.Methods)
                    if (em.Name == word) { md = $"```\n{em.Signature}\n```\n{em.Summary}"; break; }
            }
            // встроенный Math: методы, PI и само имя
            if (md == null && !MathShadowed())
            {
                if (TextUtil.HasPrefix(lineText, wordCol, "Math."))
                {
                    foreach (var mm in EngineDocs.MathMethods)
                        if (mm.Name == word) { md = $"```\n{mm.Signature}\n```\n{mm.Summary}"; break; }
                    if (md == null)
                        foreach (var (cn, ct, cd) in EngineDocs.MathConsts)
                            if (cn == word) { md = $"```\nMath.{cn}: {ct}\n```\n{cd}"; break; }
                }
                else if (word == "Math" && !TextUtil.HasPrefix(lineText, wordCol, "."))
                    md = "```\nMath\n```\nВстроенная математика: Min, Max, Clamp, Abs, Sign, Floor, Ceil, Round, Sqrt, Pow, Lerp и Math.PI. " +
                         "Тип результата — общий тип аргументов (int → float → double).";
            }
            if (md == null && Api?.Apis != null)
                foreach (var api in Api.Apis)
                    if (TextUtil.HasPrefix(lineText, wordCol, api.Name + "."))
                        foreach (var me in api.Methods)
                            if (me.Name == word)
                            { md = $"```\n{ApiFormat.MethodSig(api.Name, me)}\n```\n{ApiFormat.MethodDocMd(me) ?? ""}"; break; }
            // сам путь: узел составного имени или API-класс. Узел — первое, что
            // человек набирает, и до сих пор наведение на нём молчало
            if (md == null && Api?.Apis != null)
            {
                string dotted = TextUtil.DottedPrefixInLine(lineText, wordCol, word);
                foreach (var api in Api.Apis)
                {
                    if (api.Name != dotted) continue;
                    md = $"```\napi {api.Name}\n```\n{api.Summary ?? "API игры."}";
                    break;
                }
                if (md == null)
                {
                    int under = 0;
                    foreach (var api in Api.Apis)
                        if (api.Name.StartsWith(dotted + ".", StringComparison.Ordinal)) under++;
                    if (under > 0)
                        md = $"```\nAPI: {dotted}.…\n```\n"
                           + (ApiFormat.NamespaceSummary(Api, dotted) ?? "Пространство имён API.")
                           + $"\n\nAPI под этим именем: {under}.";
                }
            }

            // элемент енума — там, где спрашивают про ЕДИНИЦЫ ("Slot.MoveSpeed —
            // это м/с или клетки за тик?"); имя енума перед точкой снимает
            // неоднозначность одинаковых имён элементов в разных енумах
            if (md == null && Api?.Enums != null)
                foreach (var en in Api.Enums)
                {
                    if (!TextUtil.HasPrefix(lineText, wordCol, en.Name + ".")) continue;
                    for (int i = 0; i < en.Members.Length; i++)
                    {
                        if (en.Members[i] != word) continue;
                        md = $"```\n{en.Name}.{word}\n```\n{ApiFormat.MemberDoc(en, i) ?? en.Summary ?? ""}";
                        break;
                    }
                    if (md != null) break;
                }

            // сам енум: summary типа плюс сколько в нём элементов
            if (md == null && Api?.Enums != null)
                foreach (var en in Api.Enums)
                    if (en.Name == word)
                    {
                        md = $"```\nenum {en.Name}\n```\n{en.Summary ?? $"Енум игры, элементов: {en.Members.Length}."}";
                        break;
                    }

            // константа API — читается без скобок, поэтому и ищется по префиксу пути
            if (md == null && Api?.Apis != null)
                foreach (var api in Api.Apis)
                {
                    if (api.Consts == null || !TextUtil.HasPrefix(lineText, wordCol, api.Name + ".")) continue;
                    foreach (var c in api.Consts)
                        if (c.Name == word)
                        { md = $"```\n{ApiFormat.ConstSig(api.Name, c)}\n```\n{c.Doc ?? ""}"; break; }
                    if (md != null) break;
                }

            // внутри блока вида: его события и объявленные игрой константы —
            // ровно то, что автор рецепта видит перед собой
            if (md == null && Api?.Archetypes != null)
            {
                var encl = Index.EnclosingDecl(file, line1);
                if (encl != null)
                    foreach (var k in Api.Archetypes)
                    {
                        if (k.Name != encl.Kind) continue;
                        foreach (var ev in k.Events ?? Array.Empty<ApiManifest.EventDef>())
                            if (ev.Name == word)
                            { md = $"```\n{ApiFormat.EventSig(ev)}\n```\n{ev.Summary ?? $"Событие вида {k.Name}."}"; break; }
                        if (md == null)
                            foreach (var c in k.Consts ?? Array.Empty<ApiManifest.ConstDef>())
                                if (c.Name == word)
                                {
                                    // дефолт показываем СЛОВАМИ: "= 3" в сигнатуре читалось бы
                                    // как текущее значение, а блок его переопределяет
                                    string note = $"Константа вида `{k.Name}`"
                                        + (c.Required ? ", обязательная."
                                           : c.HasDefault ? $", по умолчанию `{ApiFormat.Literal(c.Default)}`." : ".");
                                    md = $"```\nreadonly {c.Type} {c.Name}\n```\n{note}\n\n{c.Doc ?? ""}";
                                    break;
                                }
                        break;
                    }
            }

            if (md == null && Api?.Events != null)
                foreach (var ev in Api.Events)
                    if (ev.Name == word)
                    { md = $"```\n{ApiFormat.EventSig(ev)}\n```\n{ev.Summary ?? "Событие игры."}"; break; }

            // поле структуры: в new Damage(...) тип известен точно, иначе — если
            // имя поля уникально среди всех структур
            if (md == null && Api?.Structs != null)
            {
                string ctorType = null;
                var mCtor = Regex.Match(
                    lineText.Substring(0, Math.Min(wordCol - 1, lineText.Length)), @"\bnew\s+(\w+)\s*\([^()]*$");
                if (mCtor.Success) ctorType = mCtor.Groups[1].Value;

                ApiManifest.StructDef ownerSt = null; ApiManifest.StructFieldDef fld = null; int hits = 0;
                foreach (var st in Api.Structs)
                {
                    if (ctorType != null && st.Name != ctorType) continue;
                    foreach (var f in st.Fields ?? Array.Empty<ApiManifest.StructFieldDef>())
                        if (f.Name == word) { hits++; ownerSt = st; fld = f; }
                }
                if (hits == 1)
                    md = $"```\n{ownerSt.Name}.{fld.Name}: {fld.Type}\n```\n" +
                         $"Поле структуры, по умолчанию `{ApiFormat.Literal(fld.Default)}`.\n\n{fld.Doc ?? ""}";
            }
            if (md == null && Api?.Classes != null)
            {
                // метод объекта (basket.AddPerk) — по тому же правилу, что и свойство
                ApiManifest.ClassDef mOwner = null; ApiManifest.MethodDef meth = null; int mHits = 0;
                foreach (var c in Api.Classes)
                    foreach (var me in c.Methods ?? Array.Empty<ApiManifest.MethodDef>())
                        if (me.Name == word) { mHits++; mOwner = c; meth = me; }
                if (mHits == 1)
                    md = $"```\n{ApiFormat.MethodSig(mOwner.Name, meth)}\n```\n{ApiFormat.MethodDocMd(meth) ?? ""}";
            }
            if (md == null && Api?.Classes != null)
            {
                // свойство сущности (u.name): показываем, если имя уникально среди классов
                ApiManifest.ClassDef ownerCls = null; ApiManifest.PropDef prop = null; int hits = 0;
                foreach (var c in Api.Classes)
                    foreach (var pr in c.Props)
                        if (pr.Name == word) { hits++; ownerCls = c; prop = pr; }
                if (hits == 1)
                    md = $"```\n{ownerCls.Name}.{prop.Name}: {prop.Type}{(prop.ReadOnly ? " (только чтение)" : "")}\n```\n{prop.Doc ?? ""}";
            }
            if (md == null)
            {
                foreach (var fi in Index.Files)
                    foreach (var d in fi.Value.Decls)
                        if (d.Name == word) { md = $"**{d.Kind} {d.Name}**"; break; }
            }

            if (versionsMd != null) md = md == null ? versionsMd : md + "\n\n---\n\n" + versionsMd;
            return md;
        }

        // ===================================================================
        // Мерж-цепочки в подсказке: версии одного члена по всем блокам
        // ===================================================================

        private static readonly Regex MemberDeclRx =
            new Regex(@"(?:\b(before|after|replace)\s+)?\b(event|func|action)\s+(\w+)", RegexOptions.Compiled);

        /// <summary>Наведение на before/after/replace перед event/func/action или на base(.</summary>
        private string ChainWordHover(string file, int line1, string lineText, string word, int wordCol)
        {
            string doc = null;
            foreach (var (w, d) in EngineDocs.ChainWords) if (w == word) { doc = d; break; }
            if (doc == null) return null;

            if (word == "base")
            {
                // base( — вызов, а не объявление и не член значения
                int after = wordCol - 1 + word.Length;
                while (after < lineText.Length && lineText[after] == ' ') after++;
                if (after >= lineText.Length || lineText[after] != '(') return null;
                string head = lineText.Substring(0, wordCol - 1).TrimEnd();
                if (head.EndsWith(".") || Regex.IsMatch(head, @"\bfunc$")) return null;

                // член, в теле которого стоит base(): ближайшее объявление выше.
                // Своя функция с именем base важнее слова — тогда это обычный вызов
                var encl = Index.EnclosingDecl(file, line1);
                DeclSymbol member = null;
                if (encl != null)
                    foreach (var ch in encl.Children)
                    {
                        if (ch.Kind == "func" && ch.Name == "base") return null;
                        if ((ch.Kind == "event" || ch.Kind == "func" || ch.Kind == "action")
                            && ch.Line <= line1 && (member == null || ch.Line > member.Line))
                            member = ch;
                    }
                string listing = member != null ? VersionsListing(file, line1, member.Name, member.Kind) : null;
                return "```\nbase(...)\n```\n" + doc + (listing != null ? "\n\n---\n\n" + listing : "");
            }

            foreach (Match m in MemberDeclRx.Matches(lineText))
            {
                if (!m.Groups[1].Success || m.Groups[1].Index + 1 != wordCol) continue;
                string listing = VersionsListing(file, line1, m.Groups[3].Value, m.Groups[2].Value);
                return $"```\n{word} {m.Groups[2].Value}\n```\n" + doc + (listing != null ? "\n\n---\n\n" + listing : "");
            }
            return null;
        }

        /// <summary>Наведение на имя члена в его объявлении: версии, если их больше одной или есть слой.</summary>
        private string MemberVersionsHover(string file, int line1, string lineText, string word, int wordCol)
        {
            foreach (Match m in MemberDeclRx.Matches(lineText))
            {
                if (m.Groups[3].Value != word || m.Groups[3].Index + 1 != wordCol) continue;
                return VersionsListing(file, line1, word, m.Groups[2].Value, onlyIfLayered: true);
            }
            return null;
        }

        private sealed class MemberVersion
        {
            public string File;
            public int Rank;      // порядок загрузки файла (FileOrder) или int.MaxValue
            public int IndexPos;  // порядок в индексе — запасной ключ
            public int DeclLine;  // строка блока: версии одного блока идут вместе
            public int Line;
            public string Mode;   // null / before / after / replace
        }

        /// <summary>
        /// Все версии члена сущности, в которой стоит курсор (тот же вид и имя
        /// блока), в порядке мержа и то, что выполнится в итоге. Гейт модулей
        /// здесь не учитывается: подсказка статическая.
        /// </summary>
        private string VersionsListing(string file, int line1, string member, string memberKind, bool onlyIfLayered = false)
        {
            var encl = Index.EnclosingDecl(file, line1);
            if (encl == null || member == null) return null;

            var versions = new List<MemberVersion>();
            int pos = 0;
            foreach (var kv in Index.Files)
            {
                int rank = FileOrder != null ? FileOrder(kv.Key) : int.MaxValue;
                foreach (var d in kv.Value.Decls)
                {
                    if (d.Kind != encl.Kind || d.Name != encl.Name) continue;
                    foreach (var ch in d.Children)
                        if (ch.Name == member && ch.Kind == memberKind)
                            versions.Add(new MemberVersion
                            {
                                File = kv.Key, Rank = rank, IndexPos = pos,
                                DeclLine = d.Line, Line = ch.Line, Mode = ch.Mode,
                            });
                }
                pos++;
            }
            if (versions.Count == 0) return null;
            if (onlyIfLayered && versions.Count == 1 && versions[0].Mode == null) return null;

            // порядок мержа: файл → блок; внутри блока ядро (или replace) раньше слоёв
            versions.Sort((a, b) =>
            {
                if (a.Rank != b.Rank) return a.Rank.CompareTo(b.Rank);
                if (a.IndexPos != b.IndexPos) return a.IndexPos.CompareTo(b.IndexPos);
                if (a.DeclLine != b.DeclLine) return a.DeclLine.CompareTo(b.DeclLine);
                bool ca = a.Mode == null || a.Mode == "replace", cb = b.Mode == null || b.Mode == "replace";
                if (ca != cb) return ca ? -1 : 1;
                return a.Line.CompareTo(b.Line);
            });

            var befores = new List<MemberVersion>();
            var afters = new List<MemberVersion>();
            MemberVersion core = null;
            foreach (var v in versions)
            {
                switch (v.Mode)
                {
                    case "replace": befores.Clear(); afters.Clear(); core = v; break;
                    case "before": befores.Add(v); break;
                    case "after": afters.Add(v); break;
                    default: core = v; break;
                }
            }

            string Where(MemberVersion v) => $"{FileLabel?.Invoke(v.File) ?? ShortName(v.File)}:{v.Line}";
            var sb = new System.Text.StringBuilder();
            sb.Append($"**Версии `{memberKind} {member}`** в `{encl.Kind} {encl.Name}`");
            sb.Append(FileOrder != null ? " — в порядке загрузки:\n\n" : " — в порядке файлов индекса (точный порядок загрузки задаёт сборка):\n\n");
            for (int i = 0; i < versions.Count; i++)
            {
                var v = versions[i];
                bool here = v.File == file && v.Line == line1;
                sb.Append($"{i + 1}. `{(v.Mode != null ? v.Mode + " " : "")}{memberKind}` — {Where(v)}{(here ? " ← здесь" : "")}\n");
            }

            var run = new List<string>();
            foreach (var v in befores) run.Add($"before {Where(v)}");
            run.Add(core != null ? $"**ядро** {Where(core)}" : "*(ядра нет)*");
            foreach (var v in afters) run.Add($"after {Where(v)}");
            sb.Append("\nВыполнится: ").Append(string.Join(" → ", run)).Append('.');
            sb.Append("\n\nВыключенный модуль прозрачен: его версии пропускаются, его replace ничего не стирает.");
            return sb.ToString();
        }

        private static string ShortName(string key)
        {
            if (string.IsNullOrEmpty(key)) return "?";
            int cut = Math.Max(key.LastIndexOf('/'), key.LastIndexOf('\\'));
            return cut >= 0 ? key.Substring(cut + 1) : key;
        }

        // ===================================================================
        // Переход к определению
        // ===================================================================

        public SymbolLocation Definition(string file, int line1, int col1)
        {
            var lineText = TextUtil.GetLine(GetText(file), line1) ?? "";
            var (word, _) = TextUtil.WordAt(lineText, col1);
            if (word == null) return null;
            int ix = word.LastIndexOf(':');
            if (ix >= 0) word = word.Substring(ix + 1); // module::Name -> Name

            // 1) член объемлющей декларации (локальные func/поля этого файла)
            var encl = Index.EnclosingDecl(file, line1);
            if (encl != null)
                foreach (var ch in encl.Children)
                    if (ch.Name == word)
                        return Loc(file, ch.Line, ch.Col, word.Length);

            // 2) глобальные декларации по всем файлам
            foreach (var kv in Index.Files)
                foreach (var d in kv.Value.Decls)
                    if (d.Name == word)
                        return Loc(kv.Key, d.Line, d.Col, word.Length);

            // 3) уникальный член где угодно (func класса, событие)
            string foundFile = null; DeclSymbol found = null; int hits = 0;
            foreach (var kv in Index.Files)
                foreach (var d in kv.Value.Decls)
                    foreach (var ch in d.Children)
                        if (ch.Name == word) { hits++; foundFile = kv.Key; found = ch; }
            if (hits == 1)
                return Loc(foundFile, found.Line, found.Col, word.Length);

            return null;
        }

        private static SymbolLocation Loc(string file, int line, int col, int len) =>
            new SymbolLocation { File = file, Line = line, Col = col, Length = Math.Max(1, len) };

        // ===================================================================
        // Семантическая раскраска
        // ===================================================================

        /// <summary>Таблицы имён для раскраски по текущему манифесту и индексу.</summary>
        public SemanticClassifier CreateClassifier() => new SemanticClassifier(Api, Index);

        /// <summary>Раскраска всего файла, отсортированная по позиции.</summary>
        public List<ClassifiedSpan> Classify(string file)
        {
            var text = GetText(file);
            if (text == null) return null;
            return CreateClassifier().ClassifyDocument(file, text);
        }
    }
}
