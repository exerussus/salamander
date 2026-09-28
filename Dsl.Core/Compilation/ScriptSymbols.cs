using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dsl.Runtime;
using Dsl.Semantics;
using Dsl.Syntax;
using Dsl.Text;

namespace Dsl.Compilation
{
    /// <summary>
    /// Что объявлено в скриптах — глазами инструментов: классы, триггеры,
    /// listener-ы, енумы и архетипы со всеми членами, типами, сигнатурами,
    /// модулем, файлом, строкой и «///»-описанием. Строится чекером на каждой
    /// компиляции (<see cref="CompilationResult.Symbols"/>), поэтому знает всё
    /// то же, что компилятор: итог мержа блоков, типы после разрешения имён,
    /// namespace, зависимости модулей. Чистые данные — ничего живого.
    /// </summary>
    public sealed class ScriptSymbolTable
    {
        public readonly List<ScriptTypeInfo> Types = new List<ScriptTypeInfo>();

        /// <summary>Модуль → его прямые зависимости (видимость имён та же, что у компилятора).</summary>
        public readonly Dictionary<string, string[]> ModuleDependencies = new Dictionary<string, string[]>(StringComparer.Ordinal);

        /// <summary>Пространства имён (и все их префиксы) → модули, где объявлены.</summary>
        public readonly Dictionary<string, HashSet<string>> Namespaces = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        private Dictionary<string, List<ScriptTypeInfo>> _byName;

        /// <summary>Виден ли модуль declModule из fromModule (сам модуль или прямая зависимость). fromModule null — виден всё.</summary>
        public bool IsVisible(string fromModule, string declModule)
        {
            if (fromModule == null || declModule == null || fromModule == declModule) return true;
            // модуль, которого нет в этой компиляции (свежий файл вне модулей) — не фильтруем
            if (!ModuleDependencies.TryGetValue(fromModule, out var deps)) return true;
            foreach (var d in deps) if (d == declModule) return true;
            return false;
        }

        /// <summary>Скриптовый тип (не архетип) по полному имени, видимый из модуля.</summary>
        public ScriptTypeInfo Find(string fullName, string fromModule = null)
        {
            if (fullName == null) return null;
            if (_byName == null)
            {
                _byName = new Dictionary<string, List<ScriptTypeInfo>>(StringComparer.Ordinal);
                foreach (var t in Types)
                {
                    if (t.IsArchetype) continue;
                    if (!_byName.TryGetValue(t.Name, out var list)) _byName[t.Name] = list = new List<ScriptTypeInfo>();
                    list.Add(t);
                }
            }
            if (!_byName.TryGetValue(fullName, out var found)) return null;
            foreach (var t in found) if (IsVisible(fromModule, t.Module)) return t;
            return null;
        }

        /// <summary>Архетип по (вид, id).</summary>
        public ScriptTypeInfo FindArchetype(string kind, string id)
        {
            foreach (var t in Types)
                if (t.IsArchetype && t.Kind == kind && t.Name == id) return t;
            return null;
        }

        public bool IsNamespace(string path, string fromModule = null)
        {
            if (path == null || !Namespaces.TryGetValue(path, out var mods)) return false;
            foreach (var m in mods) if (IsVisible(fromModule, m)) return true;
            return false;
        }

        // ===================================================================
        // Построение из результата чекера
        // ===================================================================

        internal static ScriptSymbolTable Build(HostRegistry host, CheckResult sem, IReadOnlyList<SourceText> files,
                                                IEnumerable<ModuleSourceSet> modules)
        {
            var t = new ScriptSymbolTable();
            foreach (var m in modules)
                if (m?.Manifest?.Name != null)
                    t.ModuleDependencies[m.Manifest.Name] = m.Manifest.Dependencies ?? Array.Empty<string>();
            if (sem == null) return t;

            var fmt = new TypeNames(host, sem.ScriptEnums);

            foreach (var sym in sem.Globals.All)
            {
                switch (sym)
                {
                    case ClassSymbol cs:
                        t.Add(TypeOf("class", cs.Name, cs.Module, cs.Decl, cs.Decls, files),
                              fmt, files, cs.Fields, cs.FieldDecls, cs.AllFuncDecls, null);
                        break;
                    case TriggerSymbol ts:
                        t.Add(TypeOf(ts.StartDisabled ? "disabled trigger" : "trigger", ts.Name, ts.Module, ts.Decl, ts.Decls, files),
                              fmt, files, ts.Fields, ts.FieldDecls, ts.AllFuncDecls, ts.Events);
                        break;
                    case ListenerSymbol ls:
                    {
                        // OnSubscribe/OnUnsubscribe живут отдельно от хостовых событий
                        var evs = new List<FuncMember>(ls.Events);
                        if (ls.OnSubscribe != null) evs.Add(ls.OnSubscribe);
                        if (ls.OnUnsubscribe != null) evs.Add(ls.OnUnsubscribe);
                        t.Add(TypeOf("listener", ls.Name, ls.Module, ls.Decl, ls.Decls, files),
                              fmt, files, ls.Fields, ls.FieldDecls, ls.AllFuncDecls, evs);
                        break;
                    }
                    case EnumSymbol es:
                    {
                        var info = TypeOf("enum", es.Name, es.Module, es.Decl, new[] { es.Decl }, files);
                        if (es.Decl != null)
                            foreach (var name in es.Decl.Members)
                                info.Members.Add(new ScriptMemberInfo
                                {
                                    Kind = "member", Name = name, Type = es.Name,
                                    File = info.File, Line = info.Line, Col = info.Col, Module = es.Module,
                                });
                        t.Types.Add(info);
                        break;
                    }
                }
            }

            foreach (var a in sem.Archetypes)
            {
                var info = TypeOf(a.Kind, a.Id, a.Module, a.Decl, a.Decls, files);
                info.IsArchetype = true;
                t.Add(info, fmt, files, a.Fields, a.FieldDecls, a.AllFuncDecls, a.Events);
            }

            // пространства имён — по полным именам объявлений
            foreach (var ty in t.Types)
            {
                if (ty.Namespace == null) continue;
                for (string p = ty.Namespace; p != null; p = Parent(p))
                {
                    if (!t.Namespaces.TryGetValue(p, out var mods)) t.Namespaces[p] = mods = new HashSet<string>(StringComparer.Ordinal);
                    if (ty.Module != null) mods.Add(ty.Module);
                }
            }
            return t;
        }

        private static string Parent(string ns)
        {
            int cut = ns.LastIndexOf('.');
            return cut < 0 ? null : ns.Substring(0, cut);
        }

        private static ScriptTypeInfo TypeOf<TDecl>(string kind, string name, string module, TDecl first,
                                                   IEnumerable<TDecl> decls, IReadOnlyList<SourceText> files)
            where TDecl : Decl
        {
            var d = first;
            var info = new ScriptTypeInfo
            {
                Kind = kind,
                Name = name,
                Namespace = d?.Namespace,
                Module = module,
                File = FileOf(files, d?.Pos ?? SourcePos.None),
                Line = d?.Pos.Line ?? 0,
                Col = d?.Pos.Column ?? 0,
            };
            // описание — последнее из блоков, где оно есть (мод может переписать)
            if (decls != null)
                foreach (var b in decls)
                {
                    if (b == null) continue;
                    info.Blocks++;
                    if (!string.IsNullOrEmpty(b.Doc)) info.Doc = b.Doc;
                }
            return info;
        }

        private void Add(ScriptTypeInfo info, TypeNames fmt, IReadOnlyList<SourceText> files,
                         Dictionary<string, FieldSymbol> fields, List<FieldMember> fieldDecls,
                         List<FuncMember> funcDecls, List<FuncMember> events)
        {
            Types.Add(info);

            // поля: итог мержа (тип, const, readonly) + позиция первого объявления и последнее описание
            var fieldOrder = new List<string>();
            var firstDecl = new Dictionary<string, FieldMember>(StringComparer.Ordinal);
            var fieldDoc = new Dictionary<string, string>(StringComparer.Ordinal);
            if (fieldDecls != null)
                foreach (var fd in fieldDecls)
                {
                    if (!firstDecl.ContainsKey(fd.Name)) { firstDecl[fd.Name] = fd; fieldOrder.Add(fd.Name); }
                    if (!string.IsNullOrEmpty(fd.Doc)) fieldDoc[fd.Name] = fd.Doc;
                }
            if (fields != null)
                foreach (var name in fields.Keys)
                    if (!firstDecl.ContainsKey(name)) fieldOrder.Add(name); // засеяно контрактом вида
            foreach (var name in fieldOrder)
            {
                if (fields == null || !fields.TryGetValue(name, out var fs)) continue;
                firstDecl.TryGetValue(name, out var fd);
                fieldDoc.TryGetValue(name, out var doc);
                info.Members.Add(new ScriptMemberInfo
                {
                    Kind = fs.IsConst ? "const" : "field",
                    Name = name,
                    Type = fmt.Of(fs.Type),
                    ReadOnly = fs.IsReadOnly,
                    ConstValue = fs.IsConst ? fmt.Value(fs) : null,
                    FromContract = fd == null,
                    File = fd != null ? FileOf(files, fd.Pos) : info.File,
                    Line = fd?.Pos.Line ?? info.Line,
                    Col = fd?.Pos.Column ?? info.Col,
                    Module = fd?.DeclModule ?? info.Module,
                    Doc = doc,
                });
            }

            // функции/события/action: одна запись на член; версии всех блоков
            // сливаются — позиция первой, описание последней с описанием
            var byKey = new Dictionary<string, ScriptMemberInfo>(StringComparer.Ordinal);
            void AddFunc(FuncMember fn)
            {
                if (fn == null || fn.Synth != null) return;
                string kind = fn.Kind == FuncKind.Event ? "event" : fn.Kind == FuncKind.Action ? "action" : "func";
                string key = kind + ":" + fn.Name;
                if (!byKey.TryGetValue(key, out var mi))
                {
                    mi = new ScriptMemberInfo
                    {
                        Kind = kind,
                        Name = fn.Name,
                        Type = fn.ReturnType == null || fn.ReturnType.Kind == TypeKind.Void ? null : fmt.Of(fn.ReturnType),
                        File = FileOf(files, fn.Pos),
                        Line = fn.Pos.Line,
                        Col = fn.Pos.Column,
                        Module = fn.Owner?.Module ?? info.Module,
                    };
                    foreach (var p in fn.Params) mi.Params.Add(new ScriptParamInfo { Name = p.Name, Type = fmt.Of(p.Type) });
                    byKey[key] = mi;
                    info.Members.Add(mi);
                }
                mi.Versions++;
                if (!string.IsNullOrEmpty(fn.Doc)) mi.Doc = fn.Doc;
            }
            if (funcDecls != null) foreach (var fn in funcDecls) AddFunc(fn);
            if (events != null) foreach (var fn in events) AddFunc(fn);
        }

        private static string FileOf(IReadOnlyList<SourceText> files, SourcePos pos) =>
            files != null && pos.FileId >= 0 && pos.FileId < files.Count ? files[pos.FileId].Name : null;

        /// <summary>Имена типов для людей: хостовые — по реестру, скриптовые енумы — по своим таблицам.</summary>
        private sealed class TypeNames
        {
            private readonly HostRegistry _host;
            private readonly Dictionary<int, EnumSymbol> _enums = new Dictionary<int, EnumSymbol>();

            public TypeNames(HostRegistry host, List<EnumSymbol> scriptEnums)
            {
                _host = host;
                if (scriptEnums != null) foreach (var e in scriptEnums) _enums[e.Id] = e;
            }

            public string Of(TypeRef t)
            {
                if (t == null) return "void";
                switch (t.Kind)
                {
                    case TypeKind.Enum:
                        if (_enums.TryGetValue(t.EnumId, out var se)) return se.Name;
                        break;
                    case TypeKind.Array: return Of(t.Elem) + "[]";
                    case TypeKind.List: return "List<" + Of(t.Elem) + ">";
                    case TypeKind.Map: return "Map<" + Of(t.Key) + ", " + Of(t.Val) + ">";
                    case TypeKind.Error: return "?";
                }
                return _host != null ? ApiManifest.TypeToString(_host, t) : t.Kind.ToString();
            }

            public string Value(FieldSymbol fs)
            {
                if (fs.ConstStr != null) return "\"" + fs.ConstStr + "\"";
                var v = fs.ConstValue;
                switch (v.Type)
                {
                    case VariantType.Bool: return v.AsBool ? "true" : "false";
                    case VariantType.Int: return v.AsInt.ToString(CultureInfo.InvariantCulture);
                    case VariantType.Float: return v.AsFloat.ToString("0.0###", CultureInfo.InvariantCulture);
                    case VariantType.Double: return v.AsDouble.ToString("0.0###", CultureInfo.InvariantCulture) + "d";
                    case VariantType.Enum:
                    {
                        string en = null;
                        Dictionary<string, int> members = null;
                        if (_enums.TryGetValue(v.EnumTypeId, out var se)) { en = se.Name; members = se.Members; }
                        else if (_host != null && _host.TryGetEnumById(v.EnumTypeId, out var he)) { en = he.Name; members = he.Members; }
                        if (members != null)
                            foreach (var kv in members)
                                if (kv.Value == v.EnumValue) return en + "." + kv.Key;
                        return (en ?? "enum") + "#" + v.EnumValue;
                    }
                    default: return null;
                }
            }
        }
    }

    /// <summary>Объявление скрипта: class / trigger / listener / enum / блок-архетип.</summary>
    public sealed class ScriptTypeInfo
    {
        /// <summary>"class", "trigger", "disabled trigger", "listener", "enum" или вид архетипа ("window", "spell").</summary>
        public string Kind;
        /// <summary>Полное имя ("Mods.Buffs.Cfg"); у архетипа — id.</summary>
        public string Name;
        public string Namespace;
        public bool IsArchetype;
        /// <summary>Модуль первого блока.</summary>
        public string Module;
        /// <summary>Логическое имя файла первого блока ("мод/путь.sal") и позиция в нём.</summary>
        public string File;
        public int Line;
        public int Col;
        /// <summary>«///»-описание (последнее из блоков, где оно есть).</summary>
        public string Doc;
        /// <summary>Сколько блоков слились в эту сущность.</summary>
        public int Blocks;
        public readonly List<ScriptMemberInfo> Members = new List<ScriptMemberInfo>();

        public string ShortName => Namespace == null ? Name : Name.Substring(Namespace.Length + 1);

        /// <summary>Заголовок для подсказки: "class Mods.Cfg", "window main".</summary>
        public string Title => Kind + " " + Name;

        public ScriptMemberInfo FindMember(string name, string kind = null)
        {
            foreach (var m in Members)
                if (m.Name == name && (kind == null || m.Kind == kind)) return m;
            return null;
        }
    }

    /// <summary>Член объявления: поле, const, func, event, action или элемент енума.</summary>
    public sealed class ScriptMemberInfo
    {
        /// <summary>"field", "const", "func", "event", "action", "member" (элемент енума).</summary>
        public string Kind;
        public string Name;
        /// <summary>Тип поля или возвращаемый тип функции (null — void).</summary>
        public string Type;
        public bool ReadOnly;
        /// <summary>Значение const в записи языка ("3", "\"x\"", "Kind.Fire").</summary>
        public string ConstValue;
        /// <summary>Поле засеяно контрактом вида архетипа, в скриптах не объявлено.</summary>
        public bool FromContract;
        public readonly List<ScriptParamInfo> Params = new List<ScriptParamInfo>();
        /// <summary>Сколько версий члена по всем блокам (ядро, слои, переопределения).</summary>
        public int Versions;
        public string File;
        public int Line;
        public int Col;
        public string Module;
        public string Doc;

        public bool IsCallable => Kind == "func" || Kind == "event" || Kind == "action";

        /// <summary>Сигнатура в записи языка: "func Get(int a) -> string", "readonly float width", "const int MAX = 3".</summary>
        public string Signature
        {
            get
            {
                var sb = new StringBuilder();
                switch (Kind)
                {
                    case "member":
                        return (Type ?? "") + "." + Name;
                    case "field":
                        if (ReadOnly) sb.Append("readonly ");
                        sb.Append(Type).Append(' ').Append(Name);
                        return sb.ToString();
                    case "const":
                        sb.Append("const ").Append(Type).Append(' ').Append(Name);
                        if (ConstValue != null) sb.Append(" = ").Append(ConstValue);
                        return sb.ToString();
                    default:
                        sb.Append(Kind).Append(' ').Append(Name).Append('(');
                        for (int i = 0; i < Params.Count; i++)
                        {
                            if (i > 0) sb.Append(", ");
                            sb.Append(Params[i].Type).Append(' ').Append(Params[i].Name);
                        }
                        sb.Append(')');
                        if (Type != null) sb.Append(" -> ").Append(Type);
                        return sb.ToString();
                }
            }
        }

        /// <summary>Подписи параметров для подсказки: "int a".</summary>
        public List<string> ParamLabels()
        {
            var r = new List<string>(Params.Count);
            foreach (var p in Params) r.Add(p.Type + " " + p.Name);
            return r;
        }
    }

    public sealed class ScriptParamInfo
    {
        public string Name;
        public string Type;
    }
}
