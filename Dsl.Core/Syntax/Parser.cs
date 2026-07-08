using System.Collections.Generic;
using Dsl.Text;

namespace Dsl.Syntax
{
    /// <summary>
    /// Рекурсивно-нисходящий парсер. Приоритеты бинарных операторов заданы
    /// таблицей (Pratt). Работает на этапе сборки — аллокации здесь допустимы.
    /// </summary>
    public sealed class Parser
    {
        private readonly List<Token> _t;
        private readonly int _fileId;
        private readonly DiagnosticBag _diag;
        private int _i;

        public Parser(List<Token> tokens, int fileId, DiagnosticBag diag)
        {
            _t = tokens;
            _fileId = fileId;
            _diag = diag;
        }

        private Token Cur => _t[_i];
        private Token Peek(int k = 1)
        {
            int j = _i + k;
            return j < _t.Count ? _t[j] : _t[_t.Count - 1];
        }
        private bool Is(TokenKind k) => Cur.Kind == k;

        private Token Advance() { var t = Cur; if (_i < _t.Count - 1) _i++; return t; }

        private bool Match(TokenKind k)
        {
            if (Cur.Kind == k) { Advance(); return true; }
            return false;
        }

        private Token Expect(TokenKind k, string code, string what)
        {
            if (Cur.Kind == k) return Advance();
            _diag.Error(code, $"Ожидалось {what}, встречено '{Cur.Text}'.", Cur.Pos);
            return Cur; // не двигаемся, чтобы верхний уровень мог восстановиться
        }

        // ===== верхний уровень =============================================

        public ScriptFile ParseFile()
        {
            var file = new ScriptFile { FileId = _fileId, Pos = Cur.Pos };
            while (!Is(TokenKind.Eof))
            {
                var d = ParseDecl();
                if (d != null) file.Decls.Add(d);
                else { if (!Is(TokenKind.Eof)) Advance(); } // защита от зацикливания
            }
            return file;
        }

        /// <summary>Разобрать одно выражение до конца потока (для интерполяции).</summary>
        public Expr ParseExpressionOnly()
        {
            var e = ParseExpr();
            if (!Is(TokenKind.Eof))
                _diag.Error("E0003", "Лишние символы в выражении интерполяции.", Cur.Pos);
            return e;
        }

        private Decl ParseDecl()
        {
            switch (Cur.Kind)
            {
                case TokenKind.KwEnum: return ParseEnum();
                case TokenKind.KwClass: return ParseClass();
                case TokenKind.KwTrigger: return ParseTrigger(false);
                case TokenKind.KwListener: return ParseListener();
                case TokenKind.KwDisabled:
                    Advance();
                    if (!Is(TokenKind.KwTrigger))
                    {
                        _diag.Error("E0004", "После 'disabled' ожидался 'trigger'.", Cur.Pos);
                        return null;
                    }
                    return ParseTrigger(true);
                case TokenKind.Ident when (Peek().Kind == TokenKind.Ident || Peek().Kind == TokenKind.String)
                                           && Peek(2).Kind == TokenKind.LBrace:
                    // блок-архетип: вид — контекстный идентификатор (объявлен хостом),
                    // id — идентификатор или строка (как в контентных манифестах)
                    return ParseArchetype();

                default:
                    _diag.Error("E0005",
                        $"Ожидалось объявление class / trigger / enum, встречено '{Cur.Text}'.", Cur.Pos);
                    return null;
            }
        }

        private EnumDecl ParseEnum()
        {
            var pos = Advance().Pos; // 'enum'
            var name = Expect(TokenKind.Ident, "E0006", "имя енума").Text;
            var e = new EnumDecl { Name = name, Pos = pos };
            Expect(TokenKind.LBrace, "E0007", "'{'");
            while (!Is(TokenKind.RBrace) && !Is(TokenKind.Eof))
            {
                var m = Expect(TokenKind.Ident, "E0008", "имя элемента енума").Text;
                e.Members.Add(m);
                if (!Match(TokenKind.Comma)) break;
            }
            Expect(TokenKind.RBrace, "E0009", "'}'");
            return e;
        }

        private ClassDecl ParseClass()
        {
            var pos = Advance().Pos; // 'class'
            var name = Expect(TokenKind.Ident, "E0010", "имя класса").Text;
            var c = new ClassDecl { Name = name, Pos = pos };
            Expect(TokenKind.LBrace, "E0011", "'{'");
            while (!Is(TokenKind.RBrace) && !Is(TokenKind.Eof))
            {
                var m = ParseMember();
                if (m != null) c.Members.Add(m);
                else break;
            }
            Expect(TokenKind.RBrace, "E0012", "'}'");
            return c;
        }

        private TriggerDecl ParseTrigger(bool disabled)
        {
            var pos = Advance().Pos; // 'trigger'
            var name = Expect(TokenKind.Ident, "E0013", "имя триггера").Text;
            var tr = new TriggerDecl { Name = name, StartDisabled = disabled, Pos = pos };
            Expect(TokenKind.LBrace, "E0014", "'{'");
            while (!Is(TokenKind.RBrace) && !Is(TokenKind.Eof))
            {
                var m = ParseMember();
                if (m != null) tr.Members.Add(m);
                else break;
            }
            Expect(TokenKind.RBrace, "E0015", "'}'");
            return tr;
        }

        private ListenerDecl ParseListener()
        {
            var pos = Advance().Pos; // 'listener'
            var name = Expect(TokenKind.Ident, "E0013", "имя listener").Text;
            var l = new ListenerDecl { Name = name, Pos = pos };
            Expect(TokenKind.LBrace, "E0014", "'{'");
            while (!Is(TokenKind.RBrace) && !Is(TokenKind.Eof))
            {
                var m = ParseMember();
                if (m != null) l.Members.Add(m);
                else break;
            }
            Expect(TokenKind.RBrace, "E0015", "'}'");
            return l;
        }

        private ArchetypeDecl ParseArchetype()
        {
            var kindTok = Advance(); // вид (spell/item/...)
            string id;
            if (Is(TokenKind.String)) id = Advance().Text;
            else id = Expect(TokenKind.Ident, "E0069", "id сущности (идентификатор или строка)").Text;

            var a = new ArchetypeDecl { Kind = kindTok.Text, Name = id, Pos = kindTok.Pos };
            Expect(TokenKind.LBrace, "E0014", "'{'");
            while (!Is(TokenKind.RBrace) && !Is(TokenKind.Eof))
            {
                var m = ParseMember();
                if (m != null) a.Members.Add(m);
                else break;
            }
            Expect(TokenKind.RBrace, "E0015", "'}'");
            return a;
        }

        private Member ParseMember()
        {
            switch (Cur.Kind)
            {
                case TokenKind.KwConst: return ParseConst();
                case TokenKind.KwFunc: return ParseFunc(FuncKind.Func);
                case TokenKind.KwAction: return ParseFunc(FuncKind.Action);
                case TokenKind.KwEvent: return ParseFunc(FuncKind.Event);
                default: return ParseField();
            }
        }

        private FieldMember ParseConst()
        {
            var pos = Advance().Pos; // 'const'
            var ty = ParseType();
            var name = Expect(TokenKind.Ident, "E0016", "имя константы").Text;
            Expect(TokenKind.Assign, "E0017", "'='");
            var init = ParseExpr();
            Expect(TokenKind.Semicolon, "E0018", "';'");
            return new FieldMember { Name = name, DeclType = ty, Init = init, IsConst = true, Pos = pos };
        }

        private FieldMember ParseField()
        {
            var pos = Cur.Pos;
            var ty = ParseType();
            var name = Expect(TokenKind.Ident, "E0019", "имя поля").Text;
            Expr init = null;
            if (Match(TokenKind.Assign)) init = ParseExpr();
            Expect(TokenKind.Semicolon, "E0020", "';'");
            return new FieldMember { Name = name, DeclType = ty, Init = init, IsConst = false, Pos = pos };
        }

        private FuncMember ParseFunc(FuncKind kind)
        {
            var pos = Advance().Pos; // func/action/event
            var name = Expect(TokenKind.Ident, "E0021", "имя").Text;
            var fn = new FuncMember { Name = name, Kind = kind, Pos = pos };

            Expect(TokenKind.LParen, "E0022", "'('");
            if (!Is(TokenKind.RParen))
            {
                do
                {
                    var pty = ParseType();
                    var pname = Expect(TokenKind.Ident, "E0023", "имя параметра").Text;
                    fn.Params.Add(new Param { DeclType = pty, Name = pname, Pos = pty.Pos });
                }
                while (Match(TokenKind.Comma));
            }
            Expect(TokenKind.RParen, "E0024", "')'");

            if (Match(TokenKind.Arrow))
            {
                if (kind == FuncKind.Event)
                    _diag.Error("E0025", "Обработчик event не может иметь возвращаемый тип.", Cur.Pos);
                fn.RetType = ParseType();
            }

            if (Match(TokenKind.Semicolon))
                fn.Body = null; // прототип без тела (только для void — проверит чекер)
            else
                fn.Body = ParseBlock();
            return fn;
        }

        // ===== типы =========================================================

        private TypeSyntax ParseType()
        {
            var pos = Cur.Pos;
            var name = Expect(TokenKind.Ident, "E0026", "имя типа").Text;

            TypeSyntax result;
            if (Is(TokenKind.Lt))
            {
                Advance(); // '<'
                var g = new GenericTypeSyntax { Name = name, Pos = pos };
                do { g.Args.Add(ParseType()); } while (Match(TokenKind.Comma));
                Expect(TokenKind.Gt, "E0027", "'>'");
                result = g;
            }
            else
            {
                result = new NameType { Name = name, Pos = pos };
            }

            // постфиксные [] — массивы (в т.ч. многомерные-в-один-уровень)
            while (Is(TokenKind.LBracket) && Peek().Kind == TokenKind.RBracket)
            {
                Advance(); Advance();
                result = new ArrayTypeSyntax { Elem = result, Pos = pos };
            }
            return result;
        }

        /// <summary>Пытается разобрать тип; при неудаче откатывает позицию.</summary>
        private TypeSyntax TryParseType(out int savedI)
        {
            savedI = _i;
            if (!Is(TokenKind.Ident)) return null;
            // тихий разбор без диагностик: временно ловим через ручную проверку
            var pos = Cur.Pos;
            var name = Advance().Text;
            TypeSyntax result;
            if (Is(TokenKind.Lt))
            {
                Advance();
                var g = new GenericTypeSyntax { Name = name, Pos = pos };
                while (true)
                {
                    var arg = TryParseType(out _);
                    if (arg == null) { _i = savedI; return null; }
                    g.Args.Add(arg);
                    if (Match(TokenKind.Comma)) continue;
                    break;
                }
                if (!Match(TokenKind.Gt)) { _i = savedI; return null; }
                result = g;
            }
            else result = new NameType { Name = name, Pos = pos };

            while (Is(TokenKind.LBracket) && Peek().Kind == TokenKind.RBracket)
            {
                Advance(); Advance();
                result = new ArrayTypeSyntax { Elem = result, Pos = pos };
            }
            return result;
        }

        // ===== стейтменты ===================================================

        private Block ParseBlock()
        {
            var pos = Cur.Pos;
            Expect(TokenKind.LBrace, "E0028", "'{'");
            var b = new Block { Pos = pos };
            while (!Is(TokenKind.RBrace) && !Is(TokenKind.Eof))
            {
                var s = ParseStmt();
                if (s != null) b.Stmts.Add(s);
                else SyncStatement();
            }
            Expect(TokenKind.RBrace, "E0029", "'}'");
            return b;
        }

        private void SyncStatement()
        {
            // восстановление: до ближайшего ';' или '}'
            while (!Is(TokenKind.Semicolon) && !Is(TokenKind.RBrace) && !Is(TokenKind.Eof))
                Advance();
            Match(TokenKind.Semicolon);
        }

        private Stmt ParseStmt()
        {
            switch (Cur.Kind)
            {
                case TokenKind.LBrace: return ParseBlock();
                case TokenKind.KwVar: return ParseVarDecl();
                case TokenKind.KwIf: return ParseIf();
                case TokenKind.KwWhile: return ParseWhile();
                case TokenKind.KwFor: return ParseFor();
                case TokenKind.KwReturn: return ParseReturn();
                case TokenKind.KwBreak: { var p = Advance().Pos; Expect(TokenKind.Semicolon, "E0030", "';'"); return new BreakStmt { Pos = p }; }
                case TokenKind.KwContinue: { var p = Advance().Pos; Expect(TokenKind.Semicolon, "E0031", "';'"); return new ContinueStmt { Pos = p }; }
                case TokenKind.KwWait: return ParseWait();
                case TokenKind.KwPass:
                {
                    var p = Advance().Pos;
                    Expect(TokenKind.Semicolon, "E0070", "';'");
                    return new PassStmt { Pos = p };
                }
                default: return ParseExprOrAssign();
            }
        }

        private Stmt ParseVarDecl()
        {
            var pos = Advance().Pos; // 'var'
            var name = Expect(TokenKind.Ident, "E0032", "имя переменной").Text;
            Expect(TokenKind.Assign, "E0033", "'=' (var требует инициализатор)");
            var init = ParseExpr();
            Expect(TokenKind.Semicolon, "E0034", "';'");
            return new VarDeclStmt { Name = name, DeclType = null, Init = init, Pos = pos };
        }

        private Stmt ParseIf()
        {
            var pos = Advance().Pos; // 'if'
            Expect(TokenKind.LParen, "E0035", "'('");
            var cond = ParseExpr();
            Expect(TokenKind.RParen, "E0036", "')'");
            var then = ParseBlock();
            Stmt els = null;
            if (Match(TokenKind.KwElse))
            {
                els = Is(TokenKind.KwIf) ? (Stmt)ParseIf() : ParseBlock();
            }
            return new IfStmt { Cond = cond, Then = then, Else = els, Pos = pos };
        }

        private Stmt ParseWhile()
        {
            var pos = Advance().Pos;
            Expect(TokenKind.LParen, "E0037", "'('");
            var cond = ParseExpr();
            Expect(TokenKind.RParen, "E0038", "')'");
            var body = ParseBlock();
            return new WhileStmt { Cond = cond, Body = body, Pos = pos };
        }

        private Stmt ParseFor()
        {
            var pos = Advance().Pos; // 'for'
            var varName = Expect(TokenKind.Ident, "E0039", "имя переменной цикла").Text;
            Expect(TokenKind.KwIn, "E0040", "'in'");
            var first = ParseExpr();
            if (Match(TokenKind.DotDot))
            {
                var to = ParseExpr();
                var body = ParseBlock();
                return new ForRangeStmt { Var = varName, From = first, To = to, Body = body, Pos = pos };
            }
            else
            {
                var body = ParseBlock();
                return new ForEachStmt { Var = varName, Coll = first, Body = body, Pos = pos };
            }
        }

        private Stmt ParseReturn()
        {
            var pos = Advance().Pos;
            Expr val = null;
            if (!Is(TokenKind.Semicolon)) val = ParseExpr();
            Expect(TokenKind.Semicolon, "E0041", "';'");
            return new ReturnStmt { Value = val, Pos = pos };
        }

        private Stmt ParseWait()
        {
            var pos = Advance().Pos; // 'wait'
            if (Match(TokenKind.KwUntil))
            {
                var cond = ParseExpr();
                Expect(TokenKind.Semicolon, "E0042", "';'");
                return new WaitUntilStmt { Cond = cond, Pos = pos };
            }
            var secs = ParseExpr();
            Expect(TokenKind.Semicolon, "E0043", "';'");
            return new WaitStmt { Seconds = secs, Pos = pos };
        }

        private Stmt ParseExprOrAssign()
        {
            var pos = Cur.Pos;

            // попытка распознать типизированное объявление локали: TYPE NAME ...
            if (Is(TokenKind.Ident))
            {
                int saved = _i;
                var ty = TryParseType(out _);
                if (ty != null && Is(TokenKind.Ident))
                {
                    var name = Advance().Text;
                    Expr init = null;
                    if (Match(TokenKind.Assign)) init = ParseExpr();
                    Expect(TokenKind.Semicolon, "E0044", "';'");
                    return new VarDeclStmt { DeclType = ty, Name = name, Init = init, Pos = pos };
                }
                _i = saved; // откат — это не объявление
            }

            var target = ParseExpr();

            switch (Cur.Kind)
            {
                case TokenKind.Assign:
                case TokenKind.PlusAssign:
                case TokenKind.MinusAssign:
                case TokenKind.StarAssign:
                case TokenKind.SlashAssign:
                {
                    var op = Advance().Kind;
                    var value = ParseExpr();
                    Expect(TokenKind.Semicolon, "E0045", "';'");
                    return new AssignStmt { Target = target, Op = op, Value = value, Pos = pos };
                }
                default:
                    Expect(TokenKind.Semicolon, "E0046", "';'");
                    return new ExprStmt { Expr = target, Pos = pos };
            }
        }

        // ===== выражения (Pratt) ============================================

        private static int BinPrec(TokenKind k)
        {
            switch (k)
            {
                case TokenKind.OrOr: return 1;
                case TokenKind.AndAnd: return 2;
                case TokenKind.EqEq:
                case TokenKind.NotEq: return 3;
                case TokenKind.Lt:
                case TokenKind.Le:
                case TokenKind.Gt:
                case TokenKind.Ge: return 4;
                case TokenKind.Plus:
                case TokenKind.Minus: return 5;
                case TokenKind.Star:
                case TokenKind.Slash:
                case TokenKind.Percent: return 6;
                default: return 0;
            }
        }

        private Expr ParseExpr() => ParseBinary(1);

        private Expr ParseBinary(int minPrec)
        {
            var left = ParseUnary();
            while (true)
            {
                int prec = BinPrec(Cur.Kind);
                if (prec == 0 || prec < minPrec) break;
                var opTok = Advance();
                var right = ParseBinary(prec + 1); // левая ассоциативность
                left = new BinaryExpr { Left = left, Op = opTok.Kind, Right = right, Pos = opTok.Pos };
            }
            return left;
        }

        private Expr ParseUnary()
        {
            if (Is(TokenKind.Not) || Is(TokenKind.Minus))
            {
                var op = Advance();
                var operand = ParseUnary();
                return new UnaryExpr { Op = op.Kind, Operand = operand, Pos = op.Pos };
            }
            if (Is(TokenKind.KwSpawn))
            {
                var pos = Advance().Pos;
                var inner = ParsePostfix(ParsePrimary());
                if (!(inner is CallExpr call))
                {
                    _diag.Error("E0047", "После 'spawn' ожидался вызов функции.", pos);
                    return inner;
                }
                return new SpawnExpr { Call = call, Pos = pos };
            }
            return ParsePostfix(ParsePrimary());
        }

        private Expr ParsePostfix(Expr e)
        {
            while (true)
            {
                if (Is(TokenKind.Dot))
                {
                    var pos = Advance().Pos;
                    var name = Expect(TokenKind.Ident, "E0048", "имя члена").Text;
                    e = new MemberExpr { Target = e, Name = name, Pos = pos };
                }
                else if (Is(TokenKind.LParen))
                {
                    var pos = Advance().Pos;
                    var call = new CallExpr { Callee = e, Pos = pos };
                    if (!Is(TokenKind.RParen))
                    {
                        do { call.Args.Add(ParseExpr()); } while (Match(TokenKind.Comma));
                    }
                    Expect(TokenKind.RParen, "E0049", "')'");
                    e = call;
                }
                else if (Is(TokenKind.LBracket))
                {
                    var pos = Advance().Pos;
                    var idx = ParseExpr();
                    Expect(TokenKind.RBracket, "E0050", "']'");
                    e = new IndexExpr { Target = e, Index = idx, Pos = pos };
                }
                else break;
            }
            return e;
        }

        private Expr ParsePrimary()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case TokenKind.KwTrue: Advance(); return new LiteralExpr { LKind = LiteralKind.Bool, BoolValue = true, Pos = t.Pos };
                case TokenKind.KwFalse: Advance(); return new LiteralExpr { LKind = LiteralKind.Bool, BoolValue = false, Pos = t.Pos };
                case TokenKind.KwNull: Advance(); return new LiteralExpr { LKind = LiteralKind.Null, Pos = t.Pos };
                case TokenKind.KwSelf: Advance(); return new SelfExpr { Pos = t.Pos };

                case TokenKind.Int:
                {
                    Advance();
                    long v = ParseIntLiteral(t.Text, t.Pos);
                    return new LiteralExpr { LKind = LiteralKind.Int, IntValue = v, Pos = t.Pos };
                }
                case TokenKind.Float:
                {
                    Advance();
                    double v = ParseFloatLiteral(t.Text, t.Pos);
                    return new LiteralExpr { LKind = LiteralKind.Float, FloatValue = v, Pos = t.Pos };
                }
                case TokenKind.String:
                    Advance();
                    return new LiteralExpr { LKind = LiteralKind.Str, StrValue = (string)t.Value, Pos = t.Pos };

                case TokenKind.InterpString:
                    Advance();
                    return ParseInterp((InterpTemplate)t.Value, t.Pos);

                case TokenKind.KwNew:
                    return ParseNew();

                case TokenKind.LBracket:
                    return ParseArrayLiteral();

                case TokenKind.LParen:
                {
                    Advance();
                    var inner = ParseExpr();
                    Expect(TokenKind.RParen, "E0051", "')'");
                    return inner;
                }

                case TokenKind.Ident:
                {
                    Advance();
                    // module::Name
                    if (Is(TokenKind.ColonColon))
                    {
                        Advance();
                        var member = Expect(TokenKind.Ident, "E0052", "имя после '::'").Text;
                        return new QualifiedExpr { Module = t.Text, Name = member, Pos = t.Pos };
                    }
                    return new IdentExpr { Name = t.Text, Pos = t.Pos };
                }

                default:
                    _diag.Error("E0053", $"Ожидалось выражение, встречено '{t.Text}'.", t.Pos);
                    Advance();
                    return new LiteralExpr { LKind = LiteralKind.Null, Pos = t.Pos };
            }
        }

        private Expr ParseNew()
        {
            var pos = Advance().Pos; // 'new'
            var name = Expect(TokenKind.Ident, "E0054", "имя типа после 'new'").Text;

            if (name == "List")
            {
                Expect(TokenKind.Lt, "E0055", "'<'");
                var elem = ParseType();
                Expect(TokenKind.Gt, "E0056", "'>'");
                Expect(TokenKind.LParen, "E0057", "'('");
                Expect(TokenKind.RParen, "E0058", "')'");
                return new NewListExpr { ElemType = elem, Pos = pos };
            }
            if (name == "Map")
            {
                Expect(TokenKind.Lt, "E0059", "'<'");
                var k = ParseType();
                Expect(TokenKind.Comma, "E0060", "','");
                var v = ParseType();
                Expect(TokenKind.Gt, "E0061", "'>'");
                Expect(TokenKind.LParen, "E0062", "'('");
                Expect(TokenKind.RParen, "E0063", "')'");
                return new NewMapExpr { KeyType = k, ValType = v, Pos = pos };
            }

            // массив: new T[size]
            TypeSyntax elemTy = new NameType { Name = name, Pos = pos };
            Expect(TokenKind.LBracket, "E0064", "'[' (ожидался массив new T[size])");
            var size = ParseExpr();
            Expect(TokenKind.RBracket, "E0065", "']'");
            return new NewArrayExpr { ElemType = elemTy, Size = size, Pos = pos };
        }

        private Expr ParseArrayLiteral()
        {
            var pos = Advance().Pos; // '['
            var lit = new ArrayLitExpr { Pos = pos };
            if (!Is(TokenKind.RBracket))
            {
                do { lit.Elems.Add(ParseExpr()); } while (Match(TokenKind.Comma));
            }
            Expect(TokenKind.RBracket, "E0066", "']'");
            return lit;
        }

        private Expr ParseInterp(InterpTemplate tpl, SourcePos pos)
        {
            var result = new InterpExpr { Pos = pos };
            foreach (var part in tpl.Parts)
            {
                if (!part.IsExpr)
                {
                    result.Parts.Add(new LiteralExpr { LKind = LiteralKind.Str, StrValue = part.Text, Pos = pos });
                }
                else
                {
                    var lex = new Lexer(part.Text, _fileId, _diag, 0, part.Pos.Line, part.Pos.Column);
                    var toks = lex.Tokenize();
                    var sub = new Parser(toks, _fileId, _diag);
                    result.Parts.Add(sub.ParseExpressionOnly());
                }
            }
            return result;
        }

        private long ParseIntLiteral(string text, SourcePos pos)
        {
            if (long.TryParse(text, out var v)) return v;
            _diag.Error("E0067", $"Некорректная целочисленная константа '{text}'.", pos);
            return 0;
        }

        private double ParseFloatLiteral(string text, SourcePos pos)
        {
            if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
            _diag.Error("E0068", $"Некорректная вещественная константа '{text}'.", pos);
            return 0;
        }
    }
}
