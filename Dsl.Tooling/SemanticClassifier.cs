using System;
using System.Collections.Generic;
using System.Text;
using Dsl.Compilation;
using Dsl.Text;

namespace Dsl.Tooling
{
    /// <summary>
    /// Семантическая раскраска: какого вида каждое слово кода. Правила общие для
    /// LSP (semanticTokens) и встроенной IDE (её построчный подсветчик зовёт
    /// <see cref="ClassifyIdentifier"/> с теми же таблицами имён), поэтому цвета
    /// в VS Code/Rider и в игре не расходятся.
    ///
    /// Экземпляр — снимок таблиц имён на момент создания (манифест + индекс);
    /// после смены манифеста или индекса создайте новый.
    /// </summary>
    public sealed class SemanticClassifier
    {
        // Порядок ЗНАЧИМ: LSP-клиент адресует типы индексами в этом массиве, поэтому
        // новые добавляются только в конец, а неиспользуемые не выкидываются.
        public static readonly string[] TokenTypes =
        {
            "keyword", "type", "class", "function", "property", "variable",
            "string", "number", "comment", "event", "namespace", "enumMember",
            "decorator",
        };
        public const int TtKeyword = 0, TtType = 1, TtClass = 2, TtFunction = 3, TtProperty = 4,
                         TtVariable = 5, TtString = 6, TtNumber = 7, TtComment = 8, TtEvent = 9,
                         TtNamespace = 10, TtEnumMember = 11, TtDecorator = 12;

        private readonly HashSet<string> _declNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _kindNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _apiNames = new HashSet<string>(StringComparer.Ordinal) { "Engine" };
        private readonly HashSet<string> _constPaths = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _typeNames = new HashSet<string>(EngineDocs.Types, StringComparer.Ordinal);

        public SemanticClassifier(ApiManifest api, WorkspaceIndex index)
        {
            if (index != null)
                foreach (var fi in index.Files)
                    foreach (var d in fi.Value.Decls)
                    {
                        _declNames.Add(d.Name);
                        if (d.Kind != "class" && d.Kind != "trigger" && d.Kind != "listener" && d.Kind != "enum")
                            _kindNames.Add(d.Kind); // слова-виды архетипов (spell/item/...)
                    }
            if (api?.Archetypes != null)
                foreach (var k in api.Archetypes) _kindNames.Add(k.Name);
            // Составные имена API ("Api.Weapon") лексер видит как отдельные
            // токены Api . Weapon — поэтому в множество кладём и полное имя, и
            // все его префиксы: узлом пути является каждый сегмент
            if (api?.Apis != null)
                foreach (var a in api.Apis)
                {
                    _apiNames.Add(a.Name);
                    for (int d = a.Name.IndexOf('.'); d > 0; d = a.Name.IndexOf('.', d + 1))
                        _apiNames.Add(a.Name.Substring(0, d));
                    // константы API кладём ПОЛНЫМ путём: "sword_01" само по себе может
                    // быть чем угодно, красить по короткому имени нельзя
                    foreach (var c in a.Consts ?? Array.Empty<ApiManifest.ApiConstDef>())
                        _constPaths.Add(a.Name + "." + c.Name);
                }
            if (api?.Classes != null) foreach (var c in api.Classes) _typeNames.Add(c.Name);
            if (api?.Enums != null) foreach (var e in api.Enums) _typeNames.Add(e.Name);
            if (api?.Structs != null) foreach (var st in api.Structs) _typeNames.Add(st.Name);

            // встроенный Math красится как Engine — если его имя не занято скриптом
            // или типом игры (тогда компилятор тоже возьмёт их, а не встроенный)
            if (!_declNames.Contains("Math") && !_typeNames.Contains("Math"))
            {
                _apiNames.Add("Math");
                _constPaths.Add("Math.PI");
            }

            Fingerprint = Mix(1, _declNames) ^ Mix(2, _kindNames) ^ Mix(3, _apiNames) ^ Mix(4, _constPaths) ^ Mix(5, _typeNames);
        }

        /// <summary>
        /// Отпечаток таблиц имён. Одинаковый — раскраска не изменится, и редактор
        /// не перекрашивает файл заново (новый классификатор создаётся на каждую
        /// компиляцию, а имена меняются редко).
        /// </summary>
        public long Fingerprint { get; }

        // не зависит от порядка элементов множества; tag разводит одинаковые имена в разных таблицах
        private static long Mix(int tag, HashSet<string> set)
        {
            unchecked
            {
                long acc = set.Count * 1000003L + tag;
                foreach (var name in set)
                {
                    long h = 1469598103934665603L ^ tag;
                    foreach (char ch in name ?? "") { h ^= ch; h *= 1099511628211L; }
                    acc += h * 31 + (h >> 17);
                }
                return acc * (2 * tag + 1);
            }
        }

        /// <summary>Слово-вид архетипа (spell/item/…): читается как слово языка.</summary>
        public bool IsKindWord(string word) => _kindNames.Contains(word);

        /// <summary>
        /// Вид идентификатора (не ключевого слова). afterEventKeyword — перед ним
        /// стоит `event`; afterDot — перед ним точка; beforeParen — за ним '(';
        /// dottedPath — полный путь через точку, заканчивающийся этим словом
        /// (null, если цепочка не из идентификаторов).
        /// </summary>
        public int ClassifyIdentifier(string txt, bool afterEventKeyword, bool afterDot, bool beforeParen, string dottedPath)
        {
            // Имя обработчика намеренно НЕ "event" и не "function": тип event
            // редакторы красят так же, как метод, и обработчик сливался с
            // вызовами вроде Api.Cut(). А обработчик и не вызывается из
            // скрипта — его поднимает игра, это точка подключения, поэтому
            // decorator и по смыслу ближе, и цвет у него отдельный.
            if (afterEventKeyword) return TtDecorator;
            if (afterDot)
            {
                // сегмент составного имени API: "Weapon" в Api.Weapon.Cut(...).
                // Отличаем от свойства по ПОЛНОМУ пути, а не по соседям, иначе
                // любое поле с таким именем перекрасилось бы заодно
                if (dottedPath != null && _apiNames.Contains(dottedPath)) return TtNamespace;
                // константа API — не свойство сущности: у неё нет владельца-значения,
                // и цвет именованного значения ближе по смыслу
                if (dottedPath != null && _constPaths.Contains(dottedPath)) return TtEnumMember;
                return beforeParen ? TtFunction : TtProperty;
            }
            if (beforeParen) return TtFunction;
            if (_apiNames.Contains(txt)) return TtNamespace;
            if (_kindNames.Contains(txt)) return TtKeyword;   // spell/item — читаются как слова языка
            if (_typeNames.Contains(txt)) return TtType;
            if (_declNames.Contains(txt)) return TtClass;
            return TtVariable;
        }

        // общая классификация токена лексера (главный текст и дырки интерполяции)
        private int Classify(TokenKind kind, string txt, TokenKind prev, TokenKind next, string dottedPath)
        {
            if (IsKeywordKind(kind)) return TtKeyword;
            // double-литерал (1.5d) — тоже число: раньше он оставался без цвета
            if (kind == TokenKind.Int || kind == TokenKind.Float || kind == TokenKind.Double) return TtNumber;
            if (kind != TokenKind.Ident) return -1; // пунктуация — цвет темы
            // readonly — контекстное слово: лексер отдаёт его идентификатором, но
            // перед «Тип имя» это модификатор, и красить его надо как ключевое слово
            if (txt == "readonly" && next == TokenKind.Ident) return TtKeyword;
            // before/after/replace — тоже контекстные: модификатор только перед event/func/action
            if ((txt == "before" || txt == "after" || txt == "replace")
                && (next == TokenKind.KwEvent || next == TokenKind.KwFunc || next == TokenKind.KwAction))
                return TtKeyword;
            // base(...) — вызов предыдущей версии члена; «func base()» — объявление, не слово
            if (txt == "base" && next == TokenKind.LParen && prev != TokenKind.Dot && prev != TokenKind.KwFunc)
                return TtKeyword;
            return ClassifyIdentifier(txt, prev == TokenKind.KwEvent, prev == TokenKind.Dot, next == TokenKind.LParen, dottedPath);
        }

        private static readonly bool[] KeywordKinds = BuildKeywordKinds();

        private static bool[] BuildKeywordKinds()
        {
            var values = (TokenKind[])Enum.GetValues(typeof(TokenKind));
            int max = 0;
            foreach (var v in values) max = Math.Max(max, (int)v);
            var r = new bool[max + 1];
            foreach (var v in values) r[(int)v] = v.ToString().StartsWith("Kw", StringComparison.Ordinal);
            return r;
        }

        public static bool IsKeywordKind(TokenKind kind) =>
            (uint)kind < (uint)KeywordKinds.Length && KeywordKinds[(int)kind];

        /// <summary>
        /// Полный путь через точку, заканчивающийся токеном i: для "Weapon" в
        /// Api.Weapon.Cut(...) вернёт "Api.Weapon". null — токен не продолжает
        /// цепочку идентификаторов. Нужно, чтобы отличать сегмент составного
        /// имени API от обычного свойства: по соседним токенам они неразличимы.
        /// </summary>
        public static string DottedPathEndingAt(IReadOnlyList<Token> ts, int i)
        {
            if (i < 2 || ts[i].Kind != TokenKind.Ident || ts[i - 1].Kind != TokenKind.Dot) return null;

            var parts = new List<string> { ts[i].Text ?? "" };
            int j = i - 2;
            while (j >= 0 && ts[j].Kind == TokenKind.Ident)
            {
                parts.Add(ts[j].Text ?? "");
                if (j - 1 < 0 || ts[j - 1].Kind != TokenKind.Dot) { j = -1; break; } // дошли до головы
                j -= 2;
            }
            if (j >= 0) return null; // цепочка началась не с идентификатора

            parts.Reverse();
            return string.Join(".", parts);
        }

        /// <summary>
        /// Раскраска всего документа: строки и комментарии — сырым проходом,
        /// остальное — токенами настоящего лексера, дырки интерполяции — как код.
        /// Результат отсортирован по (строка, колонка).
        /// </summary>
        public List<ClassifiedSpan> ClassifyDocument(string name, string text)
        {
            var spans = new List<(int line, int col, int len, int type)>();

            // 1) строки и комментарии — сырым проходом (лексер их не отдаёт);
            //    дырки интерполяции {expr} собираем отдельно — это КОД
            var holes = new List<(int line, int col, string src)>();
            ScanStringsAndComments(text, spans, holes);

            // 2) остальное — токенами настоящего лексера
            try
            {
                var bag = new DiagnosticBag(new[] { new SourceText(0, name, text) });
                var toks = new Lexer(text, 0, bag).Tokenize();
                for (int i = 0; i < toks.Count; i++)
                {
                    var t = toks[i];
                    if (t.Kind == TokenKind.String || t.Kind == TokenKind.InterpString) continue; // покрашены сканером
                    string txt = t.Text ?? "";
                    if (txt.Length == 0) continue;
                    int type = Classify(t.Kind,
                        txt,
                        i > 0 ? toks[i - 1].Kind : TokenKind.Eof,
                        i + 1 < toks.Count ? toks[i + 1].Kind : TokenKind.Eof,
                        DottedPathEndingAt(toks, i));
                    if (type >= 0) spans.Add((t.Pos.Line, t.Pos.Column, txt.Length, type));
                }
            }
            catch { /* битый синтаксис не должен гасить подсветку строк/комментариев */ }

            // дырки интерполяции: лексим содержимое как обычный код
            foreach (var (hLine, hCol, src) in holes)
            {
                try
                {
                    var hbag = new DiagnosticBag(new[] { new SourceText(0, name, src) });
                    var htoks = new Lexer(src, 0, hbag).Tokenize();
                    for (int i = 0; i < htoks.Count; i++)
                    {
                        var t = htoks[i];
                        if (t.Kind == TokenKind.String || t.Kind == TokenKind.InterpString) continue;
                        string txt = t.Text ?? "";
                        if (txt.Length == 0) continue;
                        int type = Classify(t.Kind,
                            txt,
                            i > 0 ? htoks[i - 1].Kind : TokenKind.Eof,
                            i + 1 < htoks.Count ? htoks[i + 1].Kind : TokenKind.Eof,
                            DottedPathEndingAt(htoks, i));
                        // дырки однострочные: строка та же, колонка со смещением
                        if (type >= 0) spans.Add((hLine, hCol + t.Pos.Column - 1, txt.Length, type));
                    }
                }
                catch { }
            }

            // 3) сортировка по позиции
            spans.Sort((a, b) => a.line != b.line ? a.line - b.line : a.col - b.col);
            var result = new List<ClassifiedSpan>(spans.Count);
            foreach (var (line, col, len, type) in spans) result.Add(new ClassifiedSpan(line, col, len, type));
            return result;
        }

        /// <summary>
        /// Строки (обычные и $"...") и комментарии // и /* */ — по сырому тексту,
        /// построчными кусками; дырки интерполяции {expr} отдаются отдельно.
        /// </summary>
        private static void ScanStringsAndComments(string text, List<(int, int, int, int)> spans,
                                                   List<(int line, int col, string src)> holes)
        {
            int line = 1, col = 1;
            int i = 0, n = text.Length;
            void Advance(char c) { if (c == '\n') { line++; col = 1; } else col++; }

            while (i < n)
            {
                char c = text[i];
                if (c == '/' && i + 1 < n && text[i + 1] == '/')
                {
                    int startCol = col, startLine = line, len = 0;
                    while (i < n && text[i] != '\n') { len++; Advance(text[i]); i++; }
                    spans.Add((startLine, startCol, len, TtComment));
                }
                else if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    int segLine = line, segCol = col, segLen = 0;
                    while (i < n)
                    {
                        bool end = text[i] == '*' && i + 1 < n && text[i + 1] == '/';
                        if (text[i] == '\n')
                        {
                            if (segLen > 0) spans.Add((segLine, segCol, segLen, TtComment));
                            Advance(text[i]); i++;
                            segLine = line; segCol = col; segLen = 0;
                            continue;
                        }
                        segLen++; Advance(text[i]); i++;
                        if (end) { segLen++; Advance(text[i]); i++; break; }
                    }
                    if (segLen > 0) spans.Add((segLine, segCol, segLen, TtComment));
                }
                else if (c == '"' || (c == '$' && i + 1 < n && text[i + 1] == '"'))
                {
                    bool interp = c == '$';
                    int segLine = line, segCol = col, segLen = 0;
                    void Flush() { if (segLen > 0) spans.Add((segLine, segCol, segLen, TtString)); segLen = 0; }

                    if (interp) { segLen++; Advance(text[i]); i++; }
                    segLen++; Advance(text[i]); i++; // открывающая кавычка
                    while (i < n && text[i] != '\n')
                    {
                        if (text[i] == '\\' && i + 1 < n) { segLen += 2; Advance(text[i]); i++; Advance(text[i]); i++; continue; }
                        if (interp && text[i] == '{' && i + 1 < n && text[i + 1] == '{')
                        { segLen += 2; Advance(text[i]); i++; Advance(text[i]); i++; continue; }
                        if (interp && text[i] == '{')
                        {
                            // скобка — ещё строка, содержимое дырки — код
                            segLen++; Advance(text[i]); i++;
                            Flush();
                            int hLine = line, hCol = col;
                            var sb = new StringBuilder();
                            while (i < n && text[i] != '}' && text[i] != '"' && text[i] != '\n')
                            { sb.Append(text[i]); Advance(text[i]); i++; }
                            if (sb.Length > 0) holes.Add((hLine, hCol, sb.ToString()));
                            segLine = line; segCol = col; segLen = 0;
                            if (i < n && text[i] == '}') { segLen++; Advance(text[i]); i++; }
                            continue;
                        }
                        bool close = text[i] == '"';
                        segLen++; Advance(text[i]); i++;
                        if (close) break;
                    }
                    Flush();
                }
                else { Advance(c); i++; }
            }
        }
    }
}
