using System.Collections.Generic;
using Dsl.Text;
using Dsl.Semantics;

namespace Dsl.Syntax
{
    // ===== базовый узел =====================================================

    public abstract class Node
    {
        public SourcePos Pos;
    }

    // ===== синтаксис типов (как написано в исходнике) =======================

    public abstract class TypeSyntax : Node { }

    /// <summary>Простое имя типа: int, float, Unit, DamageType, string, Fiber...</summary>
    public sealed class NameType : TypeSyntax
    {
        public string Name;
    }

    /// <summary>T[]</summary>
    public sealed class ArrayTypeSyntax : TypeSyntax
    {
        public TypeSyntax Elem;
    }

    /// <summary>List&lt;T&gt; / Map&lt;K,V&gt;</summary>
    public sealed class GenericTypeSyntax : TypeSyntax
    {
        public string Name;
        public List<TypeSyntax> Args = new List<TypeSyntax>();
    }

    // ===== файл и декларации ================================================

    public sealed class ScriptFile : Node
    {
        public int FileId;
        public List<Decl> Decls = new List<Decl>();
    }

    public abstract class Decl : Node
    {
        public string Name;
        public string Module; // заполняется загрузчиком: какому модулю принадлежит
    }

    public sealed class EnumDecl : Decl
    {
        public List<string> Members = new List<string>();
    }

    public sealed class ClassDecl : Decl
    {
        public List<Member> Members = new List<Member>();
    }

    public sealed class TriggerDecl : Decl
    {
        public bool StartDisabled;
        public List<Member> Members = new List<Member>();
    }

    /// <summary>
    /// listener — шаблон подписки на события КОНКРЕТНОЙ сущности (субъект —
    /// первый параметр события). Сам не активен: подключается только через
    /// Engine.Attach(Имя, entity); у каждой подписки свой блок полей.
    /// </summary>
    public sealed class ListenerDecl : Decl
    {
        public List<Member> Members = new List<Member>();
    }

    /// <summary>
    /// Блок-архетип: механика КОНКРЕТНОЙ игровой сущности по шаблону вида,
    /// объявленного хостом (spell/item/hero/...). Kind — имя вида, Name — id
    /// сущности (тот же, что в контентных манифестах игры). Хост поднимает
    /// события адресно по (вид, id). Поля — статики, как у class.
    /// </summary>
    public sealed class ArchetypeDecl : Decl
    {
        public string Kind;
        public List<Member> Members = new List<Member>();
    }

    // ===== члены классов/триггеров =========================================

    public abstract class Member : Node
    {
        public string Name;
    }

    public sealed class FieldMember : Member
    {
        public TypeSyntax DeclType;
        public Expr Init;      // может быть null
        public bool IsConst;

        // аннотации семантики:
        public TypeRef Type;
        public int StaticSlot = -1;    // индекс в таблице статиков (для не-const)
        public Dsl.Runtime.Variant ConstValue; // для const — свёрнутое значение
        public bool ConstResolved;
    }

    public enum FuncKind : byte { Func, Action, Event }

    public sealed class Param : Node
    {
        public TypeSyntax DeclType;
        public string Name;
        public TypeRef Type;   // аннотация
        public int Slot = -1;  // локальный слот
    }

    public sealed class FuncMember : Member
    {
        public FuncKind Kind;
        public List<Param> Params = new List<Param>();
        public TypeSyntax RetType;    // null => void
        public Block Body;

        // аннотации семантики:
        public TypeRef ReturnType;
        public int FuncIndex = -1;    // глобальный индекс скомпилированной функции
        public int LocalCount;        // сколько слотов локалей (params + var-ы)
        public Decl Owner;            // класс/триггер-владелец
        public int EventId = -1;      // для Kind==Event: id хостового события
    }

    // ===== стейтменты =======================================================

    public abstract class Stmt : Node { }

    public sealed class Block : Stmt
    {
        public List<Stmt> Stmts = new List<Stmt>();
    }

    public sealed class VarDeclStmt : Stmt
    {
        public TypeSyntax DeclType; // null => var (вывод из Init)
        public string Name;
        public Expr Init;

        public TypeRef Type;   // аннотация
        public int Slot = -1;
    }

    public sealed class AssignStmt : Stmt
    {
        public Expr Target;    // Ident / Member / Index
        public TokenKind Op;   // Assign / PlusAssign / ...
        public Expr Value;
    }

    public sealed class ExprStmt : Stmt
    {
        public Expr Expr;
    }

    public sealed class IfStmt : Stmt
    {
        public Expr Cond;
        public Block Then;
        public Stmt Else;      // Block или IfStmt или null
    }

    public sealed class WhileStmt : Stmt
    {
        public Expr Cond;
        public Block Body;
    }

    public sealed class ForRangeStmt : Stmt
    {
        public string Var;
        public Expr From;
        public Expr To;        // верхняя граница исключается
        public Block Body;
        public int VarSlot = -1;
        public int LimitSlot = -1; // скрытый слот под кэш верхней границы
    }

    public sealed class ForEachStmt : Stmt
    {
        public string Var;
        public string Var2;        // for k, v in map — имя второй переменной (или null)
        public Expr Coll;
        public Block Body;
        public int VarSlot = -1;
        public int Var2Slot = -1;
        // скрытая ТРОЙКА слотов подряд (VM адресует базой): idx, coll, bufId
        public int IndexSlot = -1;
        public int CollSlot = -1;
        public int BufSlot = -1;
        public bool IsMap;
        public TypeRef ElemType;   // тип первой переменной (элемент / ключ)
        public TypeRef Elem2Type;  // тип второй (значение Map)
    }

    public sealed class BreakStmt : Stmt { }
    public sealed class ContinueStmt : Stmt { }

    public sealed class ReturnStmt : Stmt
    {
        public Expr Value; // может быть null
    }

    public sealed class WaitStmt : Stmt
    {
        public Expr Seconds;
    }

    public sealed class WaitUntilStmt : Stmt
    {
        public Expr Cond;
    }

    // ===== выражения ========================================================

    public abstract class Expr : Node
    {
        public TypeRef Type; // заполняется чекером
    }

    public enum LiteralKind : byte { Bool, Int, Float, Str, Null }

    public sealed class LiteralExpr : Expr
    {
        public LiteralKind LKind;
        public bool BoolValue;
        public long IntValue;
        public double FloatValue;
        public string StrValue;

        public int ConstIndex = -1; // индекс в пуле констант (заполняет компилятор)
    }

    /// <summary>Интерполяция: конкатенация частей (литералы + выражения).</summary>
    public sealed class InterpExpr : Expr
    {
        public List<Expr> Parts = new List<Expr>();
    }

    public enum IdentKind : byte
    {
        Unresolved, Local, StaticField, AttachField,
        ClassRef, TriggerRef, ListenerRef, EnumTypeRef, ApiClassRef, EngineRef, HostTypeRef,
    }

    public sealed class IdentExpr : Expr
    {
        public string Name;
        public IdentKind IdKind;
        public int Slot = -1;   // Local slot / static slot
        public object Sym;      // ссылка на символ (ClassSymbol/TriggerSymbol/...) при необходимости
    }

    /// <summary>module::Name — квалификация именем модуля.</summary>
    public sealed class QualifiedExpr : Expr
    {
        public string Module;
        public string Name;
        public IdentKind IdKind;
        public int Slot = -1;
        public object Sym;
    }

    public enum MemberKind : byte
    {
        Unresolved, HostProperty, StaticField, EnumValue, CollLen,
    }

    public sealed class MemberExpr : Expr
    {
        public Expr Target;
        public string Name;

        public MemberKind MKind;
        public int Id = -1;     // propId / static slot / enum value
        public bool ReadOnly;   // для HostProperty
        public object Sym;      // FieldSymbol для StaticField (константы и слоты)
    }

    public sealed class IndexExpr : Expr
    {
        public Expr Target;
        public Expr Index;
    }

    public enum CallKind : byte { Unresolved, ScriptFunc, HostMethod, Engine, Builtin }

    public sealed class CallExpr : Expr
    {
        public Expr Callee;    // Ident (свой func) / Member (Qual.func) / Qualified
        public List<Expr> Args = new List<Expr>();

        public CallKind CKind;
        public int TargetIndex = -1;  // funcIndex / hostFnId / EngineOp
        public bool ReturnsValue;     // есть ли осмысленный результат на стеке
    }

    /// <summary>'pass;' — осознанно пустой стейтмент (в т.ч. чтобы «убить» обработчик при переопределении).</summary>
    public sealed class PassStmt : Stmt { }

    /// <summary>'self' — сущность, к которой привязана текущая подписка listener.</summary>
    public sealed class SelfExpr : Expr { }

    public sealed class SpawnExpr : Expr
    {
        public CallExpr Call;  // то, что запускается как отдельный файбер
    }

    public sealed class NewArrayExpr : Expr
    {
        public TypeSyntax ElemType;
        public Expr Size;
        public TypeRef ElemTypeRef;
    }

    public sealed class NewListExpr : Expr
    {
        public TypeSyntax ElemType;
        public TypeRef ElemTypeRef;
    }

    public sealed class NewMapExpr : Expr
    {
        public TypeSyntax KeyType;
        public TypeSyntax ValType;
        public TypeRef KeyTypeRef;
        public TypeRef ValTypeRef;
    }

    public sealed class ArrayLitExpr : Expr
    {
        public List<Expr> Elems = new List<Expr>();
        public TypeRef ElemTypeRef;
    }

    public sealed class BinaryExpr : Expr
    {
        public Expr Left;
        public TokenKind Op;
        public Expr Right;
    }

    public sealed class UnaryExpr : Expr
    {
        public TokenKind Op;
        public Expr Operand;
    }

    /// <summary>Неявное преобразование int→float, вставляется чекером.</summary>
    public sealed class ConvertExpr : Expr
    {
        public Expr Inner;
    }
}
