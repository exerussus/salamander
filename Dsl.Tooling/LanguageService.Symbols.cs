using System;
using System.Collections.Generic;
using System.Text;
using Dsl.Compilation;

namespace Dsl.Tooling
{
    /// <summary>
    /// Семантическая половина языкового сервиса: подсказки по таблице символов
    /// компилятора (<see cref="CompilationResult.Symbols"/>). Она знает типы,
    /// сигнатуры, итог мержа, namespace и — главное — модули, которых нет в
    /// воркспейсе (игра, зависимости мода): их подкладывает владелец сервиса
    /// в компиляцию как справочные. Синтакс-индекс остаётся запасным путём:
    /// свежий, ещё не скомпилированный текст и файл с синтаксической ошибкой.
    /// </summary>
    public sealed partial class LanguageService
    {
        /// <summary>
        /// Таблица символов последней компиляции, дошедшей до чекера (null —
        /// подсказки только по синтакс-индексу). Обновляйте через
        /// <see cref="UpdateSymbols"/>: компиляция с синтаксической ошибкой
        /// таблицы не даёт, и прошлую выбрасывать незачем.
        /// </summary>
        public ScriptSymbolTable Symbols;

        /// <summary>Модуль файла по ключу (для видимости имён, как у компилятора); null — без фильтра.</summary>
        public Func<string, string> ModuleOfFile;

        /// <summary>
        /// Логическое имя файла из таблицы символов ("мод/путь.sal") → ключ,
        /// по которому владелец откроет файл (путь у LSP, ключ документа у IDE).
        /// null или null в ответе — отдаётся логическое имя как есть.
        /// </summary>
        public Func<string, string> FileOfLogical;

        /// <summary>Взять таблицу из результата компиляции, если она там есть.</summary>
        public void UpdateSymbols(CompilationResult result)
        {
            if (result?.Symbols != null) Symbols = result.Symbols;
        }

        private string ModuleAt(string file) => file == null ? null : ModuleOfFile?.Invoke(file);

        /// <summary>Итог разрешения имени: скриптовый тип (семантика и/или индекс) или пространство имён.</summary>
        private struct Resolved
        {
            public DeclSymbol Decl;       // из синтакс-индекса (свежие позиции файлов воркспейса)
            public string DeclFile;
            public ScriptTypeInfo Type;   // из таблицы символов (типы, сигнатуры, справочные модули)
            public string Ns;
        }

        /// <summary>
        /// Имя или путь из точки файла: области namespace курсора от внутренней
        /// к глобальной; на каждом уровне — таблица символов, затем индекс.
        /// </summary>
        private Resolved Resolve(string file, int line1, string dotted)
        {
            string ns = NamespaceAt(file, line1);
            string mod = ModuleAt(file);
            for (var scope = ns; ; scope = ParentNamespace(scope))
            {
                string full = scope == null ? dotted : scope + "." + dotted;
                var r = new Resolved { Type = Symbols?.Find(full, mod) };
                r.Decl = FindDecl(full, out r.DeclFile);
                if (r.Type != null || r.Decl != null) return r;
                if ((Symbols != null && Symbols.IsNamespace(full, mod)) || IsNamespacePath(full))
                    return new Resolved { Ns = full };
                if (scope == null) return default;
            }
        }

        /// <summary>Семантика объявления, внутри которого курсор (класс, триггер, архетип...).</summary>
        private ScriptTypeInfo EnclosingType(string file, int line1)
        {
            if (Symbols == null) return null;
            var encl = Index.EnclosingDecl(file, line1);
            if (encl == null) return null;
            switch (encl.Kind)
            {
                case "class": case "trigger": case "listener": case "enum":
                    return Symbols.Find(encl.Name);
                default:
                    return Symbols.FindArchetype(encl.Kind, encl.Name);
            }
        }

        /// <summary>Члены, доступные снаружи через «Тип.»: у классов — поля и функции, у енумов — элементы.</summary>
        private static bool VisibleFromOutside(ScriptTypeInfo t, ScriptMemberInfo m)
        {
            if (t.Kind == "enum") return m.Kind == "member";
            if (t.Kind != "class") return false; // поля триггеров и listener-ов приватны (E0161/E0179)
            return m.Kind == "field" || m.Kind == "const" || m.Kind == "func";
        }

        private static CompletionKind KindOf(ScriptMemberInfo m)
        {
            switch (m.Kind)
            {
                case "func": return CompletionKind.Method;
                case "const": return CompletionKind.Constant;
                case "member": return CompletionKind.EnumMember;
                case "event": case "action": return CompletionKind.Event;
                default: return CompletionKind.Field;
            }
        }

        private static CompletionKind KindOf(ScriptTypeInfo t) =>
            t.Kind == "enum" ? CompletionKind.Enum : CompletionKind.Class;

        /// <summary>Пункты подсказки для членов типа (снаружи — только доступные).</summary>
        private void AddMemberItems(List<CompletionItem> items, ScriptTypeInfo t, bool fromOutside, HashSet<string> seen = null)
        {
            foreach (var m in t.Members)
            {
                if (fromOutside && !VisibleFromOutside(t, m)) continue;
                if (!fromOutside && (m.Kind == "event" || m.Kind == "action")) continue; // их не вызывают из кода
                if (seen != null && !seen.Add(m.Name)) continue;
                bool call = m.Kind == "func";
                items.Add(new CompletionItem
                {
                    Label = m.Name,
                    Kind = KindOf(m),
                    Detail = m.Signature,
                    Documentation = MemberDocMd(t, m, withSignature: false),
                    InsertText = call ? ApiFormat.CallSnippet(m.Name, m.ParamLabels()) : null,
                    IsSnippet = call,
                });
            }
        }

        /// <summary>Описание члена для hover/подсказки: сигнатура, «///», чей и откуда.</summary>
        private static string MemberDocMd(ScriptTypeInfo t, ScriptMemberInfo m, bool withSignature)
        {
            var sb = new StringBuilder();
            if (withSignature) sb.Append("```\n").Append(m.Signature).Append("\n```\n");
            if (!string.IsNullOrEmpty(m.Doc)) sb.Append(m.Doc).Append("\n\n");
            sb.Append('_').Append(t.Title);
            if (m.Module != null) sb.Append(" · модуль ").Append(m.Module);
            if (m.Versions > 1) sb.Append(" · версий: ").Append(m.Versions);
            if (m.FromContract) sb.Append(" · из контракта вида");
            sb.Append('_');
            return sb.ToString();
        }

        /// <summary>Описание объявления для hover: заголовок, «///», модуль, число блоков и членов.</summary>
        private static string TypeDocMd(ScriptTypeInfo t)
        {
            var sb = new StringBuilder();
            sb.Append("```\n").Append(t.Title).Append("\n```\n");
            if (!string.IsNullOrEmpty(t.Doc)) sb.Append(t.Doc).Append("\n\n");
            sb.Append('_');
            if (t.Module != null) sb.Append("модуль ").Append(t.Module);
            if (t.Blocks > 1) sb.Append(" · блоков: ").Append(t.Blocks);
            int fields = 0, funcs = 0;
            foreach (var m in t.Members)
            {
                if (m.Kind == "field" || m.Kind == "const") fields++;
                else if (m.IsCallable) funcs++;
            }
            if (t.Kind == "enum") sb.Append(" · элементов: ").Append(t.Members.Count);
            else
            {
                if (fields > 0) sb.Append(" · полей: ").Append(fields);
                if (funcs > 0) sb.Append(" · функций: ").Append(funcs);
            }
            sb.Append('_');
            return sb.ToString();
        }

        private SymbolLocation LocOf(string logicalFile, int line, int col, int len)
        {
            if (logicalFile == null) return null;
            string key = FileOfLogical?.Invoke(logicalFile) ?? logicalFile;
            return Loc(key, line, col, len);
        }

        /// <summary>
        /// Семантический hover. Слово с путём перед ним ("Mods.Cfg.Get") — член
        /// найденного типа или сам тип; голое слово — член объемлющего объявления
        /// (своя функция, поле) или тип по областям. null — не скриптовое имя.
        /// </summary>
        private string SymbolHover(string file, int line1, string lineText, string word, int wordCol)
        {
            string dotted = DottedWord(lineText, word, wordCol);
            if (dotted != word)
            {
                string owner = dotted.Substring(0, dotted.Length - word.Length - 1);
                var ro = Resolve(file, line1, owner);
                if (ro.Type != null)
                {
                    var m = ro.Type.FindMember(word);
                    if (m != null) return MemberDocMd(ro.Type, m, withSignature: true);
                }
            }
            else if (Symbols != null)
            {
                var encl = EnclosingType(file, line1);
                var own = encl?.FindMember(word);
                if (own != null) return MemberDocMd(encl, own, withSignature: true);
            }

            var r = Resolve(file, line1, dotted);
            if (r.Type != null) return TypeDocMd(r.Type);
            if (r.Ns != null) return $"**namespace {r.Ns}**";
            if (r.Decl != null) return $"**{r.Decl.Kind} {r.Decl.Name}**" + (r.Decl.Doc != null ? "\n\n" + r.Decl.Doc : "");
            return null;
        }

        /// <summary>Подсказка параметров для скриптовой функции: "Mods.Cfg.Get(" или своя "Helper(".</summary>
        private SignatureInfo SymbolSignature(string file, int line1, string owner, string method, int commas)
        {
            ScriptTypeInfo t;
            if (owner.Length > 0)
            {
                var r = Resolve(file, line1, owner);
                t = r.Type;
                if (t == null) return null;
            }
            else t = EnclosingType(file, line1);
            var m = t?.FindMember(method, "func");
            if (m == null) return null;
            var labels = m.ParamLabels();
            return new SignatureInfo
            {
                Label = m.Signature,
                Documentation = MemberDocMd(t, m, withSignature: false),
                Parameters = labels,
                ActiveParameter = Math.Min(commas, Math.Max(0, labels.Count - 1)),
            };
        }

        /// <summary>Переход к определению по таблице символов (в том числе в справочные модули).</summary>
        private SymbolLocation SymbolDefinition(string file, int line1, string lineText, string word, int wordCol)
        {
            string dotted = DottedWord(lineText, word, wordCol);
            if (dotted != word)
            {
                string owner = dotted.Substring(0, dotted.Length - word.Length - 1);
                var ro = Resolve(file, line1, owner);
                // член типа: индекс знает свежие позиции файлов воркспейса
                if (ro.Decl != null)
                    foreach (var ch in ro.Decl.Children)
                        if (ch.Name == word) return Loc(ro.DeclFile, ch.Line, ch.Col, word.Length);
                var m = ro.Type?.FindMember(word);
                if (m != null) return LocOf(m.File, m.Line, m.Col, word.Length);
            }
            var r = Resolve(file, line1, dotted);
            if (r.Decl != null) return Loc(r.DeclFile, r.Decl.Line, r.Decl.Col, word.Length);
            if (r.Type != null) return LocOf(r.Type.File, r.Type.Line, r.Type.Col, word.Length);
            if (r.Ns != null && Symbols != null)
                foreach (var t in Symbols.Types)
                    if (t.Namespace != null && (t.Namespace == r.Ns || t.Namespace.StartsWith(r.Ns + ".", StringComparison.Ordinal)))
                        return LocOf(t.File, t.Line, t.Col, word.Length);
            return null;
        }

        /// <summary>
        /// Голое слово: скриптовые имена по таблице символов — глобальные, корни
        /// namespace, короткие имена своих областей и члены объемлющего объявления.
        /// </summary>
        private void AddScopeItems(List<CompletionItem> items, string file, int line1, HashSet<string> seen)
        {
            if (Symbols == null) return;
            string mod = ModuleAt(file);
            string curNs = NamespaceAt(file, line1);

            var encl = EnclosingType(file, line1);
            if (encl != null) AddMemberItems(items, encl, fromOutside: false, seen);

            foreach (var t in Symbols.Types)
            {
                if (t.IsArchetype || !Symbols.IsVisible(mod, t.Module)) continue;
                if (t.Namespace == null)
                {
                    if (seen.Add(t.Name))
                        items.Add(new CompletionItem { Label = t.Name, Kind = KindOf(t), Detail = t.Title, Documentation = t.Doc });
                    continue;
                }
                int dot = t.Namespace.IndexOf('.');
                string root = dot < 0 ? t.Namespace : t.Namespace.Substring(0, dot);
                if (seen.Add(root))
                    items.Add(new CompletionItem { Label = root, Kind = CompletionKind.Module, Detail = "namespace " + root });
                for (var scope = curNs; scope != null; scope = ParentNamespace(scope))
                {
                    if (t.Namespace == scope)
                    {
                        if (seen.Add(t.ShortName))
                            items.Add(new CompletionItem { Label = t.ShortName, Kind = KindOf(t), Detail = t.Title, Documentation = t.Doc });
                    }
                    else if (t.Namespace.StartsWith(scope + ".", StringComparison.Ordinal))
                    {
                        string rest = t.Namespace.Substring(scope.Length + 1);
                        int d2 = rest.IndexOf('.');
                        string seg = d2 < 0 ? rest : rest.Substring(0, d2);
                        if (seen.Add(seg))
                            items.Add(new CompletionItem { Label = seg, Kind = CompletionKind.Module, Detail = $"namespace {scope}.{seg}" });
                    }
                }
            }
        }

        /// <summary>"Ns." — вложенные пространства имён и объявления в нём по таблице символов.</summary>
        private void AddNamespaceItems(List<CompletionItem> items, string file, string nsPath, HashSet<string> seen)
        {
            if (Symbols == null) return;
            string mod = ModuleAt(file);
            string prefix = nsPath + ".";
            foreach (var t in Symbols.Types)
            {
                if (t.IsArchetype || t.Namespace == null || !Symbols.IsVisible(mod, t.Module)) continue;
                if (t.Namespace == nsPath)
                {
                    if (seen.Add(t.ShortName))
                        items.Add(new CompletionItem { Label = t.ShortName, Kind = KindOf(t), Detail = t.Title, Documentation = t.Doc });
                }
                else if (t.Namespace.StartsWith(prefix, StringComparison.Ordinal))
                {
                    string rest = t.Namespace.Substring(prefix.Length);
                    int dot = rest.IndexOf('.');
                    string seg = dot < 0 ? rest : rest.Substring(0, dot);
                    if (seen.Add(seg))
                        items.Add(new CompletionItem { Label = seg, Kind = CompletionKind.Module, Detail = $"namespace {nsPath}.{seg}" });
                }
            }
        }
    }
}
