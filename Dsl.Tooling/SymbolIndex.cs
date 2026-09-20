using System;
using System.Collections.Generic;
using Dsl.Syntax;
using Dsl.Text;

namespace Dsl.Tooling
{
    /// <summary>Декларация файла (или её член) из настоящего парсера.</summary>
    public sealed class DeclSymbol
    {
        public string Name;
        public string Kind;    // class/trigger/listener/enum/<вид архетипа>/field/const/func/event/action/member
        public int Line;       // 1-based
        public int Col;        // 1-based
        public readonly List<DeclSymbol> Children = new List<DeclSymbol>();
    }

    /// <summary>Декларации одного файла + хэш текста, по которому индекс не пересобирается зря.</summary>
    public sealed class FileSymbols
    {
        public int Hash;
        public readonly List<DeclSymbol> Decls = new List<DeclSymbol>();
    }

    /// <summary>
    /// Синтакс-индекс воркспейса: ключ файла (абсолютный путь у LSP и файловой
    /// IDE, логическое имя у IDE в памяти) → его декларации. Не потокобезопасен:
    /// владелец обновляет и читает его из одного потока.
    /// </summary>
    public sealed class WorkspaceIndex
    {
        private readonly Dictionary<string, FileSymbols> _files =
            new Dictionary<string, FileSymbols>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Файлы в порядке индексации (порядок влияет на «первое совпадение» в go-to).</summary>
        public IEnumerable<KeyValuePair<string, FileSymbols>> Files => _files;

        public int Count => _files.Count;

        public bool TryGet(string key, out FileSymbols symbols) => _files.TryGetValue(key, out symbols);

        public bool Remove(string key) => _files.Remove(key);

        public void Clear() => _files.Clear();

        /// <summary>Убрать из индекса всё, чего нет в keep.</summary>
        public void RetainOnly(ICollection<string> keep)
        {
            var stale = new List<string>();
            foreach (var key in _files.Keys)
                if (!keep.Contains(key)) stale.Add(key);
            foreach (var key in stale) _files.Remove(key);
        }

        /// <summary>Переиндексировать файл, если текст поменялся. null — удалить из индекса.</summary>
        public void Update(string key, string text)
        {
            if (text == null) { _files.Remove(key); return; }
            int hash = text.GetHashCode();
            if (_files.TryGetValue(key, out var cached) && cached.Hash == hash) return;
            var fi = Parse(key, text);
            fi.Hash = hash;
            _files[key] = fi;
        }

        /// <summary>Объемлющая декларация верхнего уровня: ближайшая, начатая не ниже строки.</summary>
        public DeclSymbol EnclosingDecl(string key, int line1)
        {
            if (!_files.TryGetValue(key, out var fi)) return null;
            DeclSymbol best = null;
            foreach (var d in fi.Decls)
                if (d.Line <= line1 && (best == null || d.Line > best.Line))
                    best = d;
            return best;
        }

        /// <summary>Декларации одного текста. Синтакс-мусор не бросает — отдаёт то, что успел разобрать парсер.</summary>
        public static FileSymbols Parse(string name, string text)
        {
            var fi = new FileSymbols();
            try
            {
                var src = new SourceText(0, name, text);
                var bag = new DiagnosticBag(new[] { src });
                var lexer = new Lexer(text, 0, bag);
                var parser = new Parser(lexer.Tokenize(), 0, bag);
                var file = parser.ParseFile();

                foreach (var d in file.Decls)
                {
                    if (d == null) continue;
                    var sym = new DeclSymbol { Name = d.Name, Line = d.Pos.Line, Col = d.Pos.Column };
                    List<Member> members = null;
                    switch (d)
                    {
                        case ClassDecl c: sym.Kind = "class"; members = c.Members; break;
                        case TriggerDecl t: sym.Kind = "trigger"; members = t.Members; break;
                        case ListenerDecl l: sym.Kind = "listener"; members = l.Members; break;
                        case ArchetypeDecl a: sym.Kind = a.Kind; members = a.Members; break;
                        case EnumDecl e:
                            sym.Kind = "enum";
                            foreach (var m in e.Members)
                                sym.Children.Add(new DeclSymbol { Name = m, Kind = "member", Line = d.Pos.Line, Col = d.Pos.Column });
                            break;
                        default: continue;
                    }
                    if (members != null)
                        foreach (var m in members)
                            switch (m)
                            {
                                case FieldMember f:
                                    sym.Children.Add(new DeclSymbol
                                    {
                                        Name = f.Name,
                                        Kind = f.IsConst ? "const" : "field",
                                        Line = f.Pos.Line,
                                        Col = f.Pos.Column,
                                    });
                                    break;
                                case FuncMember fn:
                                    sym.Children.Add(new DeclSymbol
                                    {
                                        Name = fn.Name,
                                        Kind = fn.Kind == FuncKind.Event ? "event"
                                             : fn.Kind == FuncKind.Action ? "action" : "func",
                                        Line = fn.Pos.Line,
                                        Col = fn.Pos.Column,
                                    });
                                    break;
                            }
                    fi.Decls.Add(sym);
                }
            }
            catch { /* синтакс-мусор не должен ронять индекс */ }
            return fi;
        }
    }
}
