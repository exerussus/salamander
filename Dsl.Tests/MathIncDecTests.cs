using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Встроенный Math и стейтменты ++/--/%=. Math — чистые функции: тип
    /// результата — общий тип аргументов, вместо NaN/бесконечности/переполнения
    /// ошибка скрипта. ++/-- — только стейтменты (десахар в «+= 1»), цель числовая.
    /// </summary>
    public sealed class MathIncDecTests
    {
        public sealed class Unit
        {
            public string Name;
            public int Hp;
        }

        private HostBuilder _host;
        private EventRef<Unit> _onPing;
        private EventRef<Unit, float> _onHit;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _host = new HostBuilder();
            _host.Class<Unit>()
                .Prop("name", u => u.Name)
                .Prop("hp", u => u.Hp, (u, v) => u.Hp = v);
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));
            _onPing = _host.Event<Unit>("OnPing");
            _onHit = _host.Event<Unit, float>("OnHit");
        }

        private CompilationResult Compile(string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "game", ApiVersion = 1, Sources = new[] { "game.sal" } },
            };
            set.Files.Add(("game/game.sal", src));
            return ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet> { set });
        }

        private static string Dump(CompilationResult r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            return sb.ToString();
        }

        private static int Count(CompilationResult r, string code)
        {
            int n = 0;
            foreach (var d in r.Diagnostics) if (d.Code == code) n++;
            return n;
        }

        /// <summary>Тело обработчика OnPing → то, что он записал через Api.Note.</summary>
        private List<string> Run(string body, string extra = "", Unit unit = null)
        {
            var r = Compile(extra + "\ntrigger T { event OnPing(Unit u) { " + body + " } }");
            Assert.IsTrue(r.Success, Dump(r));
            var engine = new ScriptEngine(_host.Registry);
            engine.OnError += m => Assert.Fail(m);
            engine.LoadProgram(r.Program);
            _log.Clear();
            _onPing.Raise(engine, unit ?? new Unit());
            return new List<string>(_log);
        }

        /// <summary>Ошибка скрипта во время обработчика (а не компиляции).</summary>
        private string RunError(string body)
        {
            var r = Compile("trigger T { event OnPing(Unit u) { " + body + " } }");
            Assert.IsTrue(r.Success, Dump(r));
            var engine = new ScriptEngine(_host.Registry);
            string error = null;
            engine.OnError += m => error = m;
            engine.LoadProgram(r.Program);
            _onPing.Raise(engine, new Unit());
            Assert.IsNotNull(error, "ожидалась ошибка скрипта");
            return error;
        }

        private CompilationResult CompileBody(string body) =>
            Compile("trigger T { event OnPing(Unit u) { " + body + " } }");

        // ===================================================================
        // Math: значения и типы
        // ===================================================================

        [Test]
        public void MinMax_KeepInts_WidenMixed()
        {
            Assert.AreEqual(new[] { "3 7 1.5 2" },
                Run(@"Api.Note($""{Math.Min(3, 7)} {Math.Max(3, 7)} {Math.Min(2, 1.5)} {Math.Max(1.5d, 2)}"");"));

            Assert.IsTrue(CompileBody("int a = Math.Min(3, 7);").Success, "int, int → int");
            Assert.AreEqual(1, Count(CompileBody("int b = Math.Min(3, 1.5);"), "E0151"),
                "int и float → float, в int без явного округления не кладётся");
        }

        [Test]
        public void Clamp_AllNumericTypes()
        {
            Assert.AreEqual(new[] { "10 0 0.25 -3" },
                Run(@"Api.Note($""{Math.Clamp(15, 0, 10)} {Math.Clamp(-1.5, 0, 1)} {Math.Clamp(0.25, 0, 1)} {Math.Clamp(-3, -5, 5)}"");"));
        }

        [Test]
        public void Clamp_MinAboveMax_IsAScriptError()
        {
            StringAssert.Contains("Math.Clamp", RunError("int x = Math.Clamp(5, 10, 0);"));
        }

        [Test]
        public void Abs_Sign()
        {
            Assert.AreEqual(new[] { "5 2.5 -1 0 1" },
                Run(@"Api.Note($""{Math.Abs(-5)} {Math.Abs(-2.5)} {Math.Sign(-3)} {Math.Sign(0.0)} {Math.Sign(7.5)}"");"));
        }

        [Test]
        public void Abs_OfIntMinValue_IsAScriptError_NotAnInternalOne()
        {
            string e = RunError("int m = -2147483647 - 1; int a = Math.Abs(m);");
            StringAssert.Contains("Math.Abs", e);
            StringAssert.DoesNotContain("Внутренняя ошибка", e);
        }

        [Test]
        public void FloorCeilRound_ReturnInt_RoundHalfAwayFromZero()
        {
            Assert.AreEqual(new[] { "-2 2 3 -3 2 7" },
                Run(@"Api.Note($""{Math.Floor(-1.5)} {Math.Ceil(1.2)} {Math.Round(2.5)} {Math.Round(-2.5)} {Math.Round(2.4)} {Math.Floor(7)}"");"));
            Assert.IsTrue(CompileBody("int f = Math.Floor(2.7); int c = Math.Ceil(2.1d); int r = Math.Round(0.5);").Success,
                "Floor/Ceil/Round — всегда int");
        }

        [Test]
        public void Round_OutOfIntRange_IsAScriptError()
        {
            StringAssert.Contains("не помещается в int", RunError("int r = Math.Round(3000000000.0);"));
        }

        [Test]
        public void Sqrt_Pow_Lerp()
        {
            Assert.AreEqual(new[] { "3 1024 2.5 10 0" },
                Run(@"Api.Note($""{Math.Sqrt(9)} {Math.Pow(2, 10)} {Math.Lerp(0, 10, 0.25)} {Math.Lerp(0, 10, 2)} {Math.Lerp(0, 10, -1)}"");"));
            Assert.AreEqual(1, Count(CompileBody("int s = Math.Sqrt(9);"), "E0151"), "корень из int — float");
        }

        [Test]
        public void Double_StaysDouble()
        {
            Assert.AreEqual(new[] { "1.4142135623730951" }, Run(@"double s = Math.Sqrt(2.0d); Api.Note($""{s}"");"));
        }

        [Test]
        public void Sqrt_OfNegative_IsAScriptError_NotNaN()
        {
            StringAssert.Contains("Math.Sqrt", RunError("float s = Math.Sqrt(-1.0);"));
        }

        [Test]
        public void Pi_IsAFloatConstant()
        {
            Assert.AreEqual(new[] { "3.1416" }, Run(@"float p = Math.PI; Api.Note($""{p}"");"));
            Assert.AreEqual(1, Count(CompileBody("float p = Math.PI();"), "E0239"), "константа со скобками");
        }

        [Test]
        public void Math_InFieldInitializer()
        {
            Assert.AreEqual(new[] { "4" }, Run(@"Api.Note($""{C.r}"");", "class C { readonly float r = Math.Sqrt(16.0); }"));
        }

        [Test]
        public void Math_Diagnostics()
        {
            Assert.AreEqual(1, Count(CompileBody("float x = Math.Min;"), "E0162"), "метод без скобок");
            Assert.AreEqual(1, Count(CompileBody("int x = Math.Foo(1);"), "E0249"), "нет такой функции");
            Assert.AreEqual(1, Count(CompileBody("float x = Math.Nope;"), "E0249"), "нет такой константы");
            Assert.AreEqual(1, Count(CompileBody("int x = Math.Min(1);"), "E0250"), "не то число аргументов");
            Assert.AreEqual(1, Count(CompileBody(@"int x = Math.Min(""a"", 1);"), "E0251"), "не число");
        }

        // ===================================================================
        // Math: своё имя Math важнее встроенного
        // ===================================================================

        [Test]
        public void HostApiNamedMath_WinsOverBuiltin()
        {
            _host.Api("Math").Fn("Min", (int a) => a * 10);
            Assert.AreEqual(new[] { "30" }, Run(@"Api.Note($""{Math.Min(3)}"");"));
        }

        [Test]
        public void ScriptClassNamedMath_WinsOverBuiltin()
        {
            Assert.AreEqual(new[] { "101" },
                Run(@"Api.Note($""{Math.Min(1)}"");", "class Math { func Min(int a) -> int { return a + 100; } }"));
        }

        // ===================================================================
        // ++ / -- / %=
        // ===================================================================

        [Test]
        public void IncDec_Locals_PrefixAndPostfix()
        {
            Assert.AreEqual(new[] { "6" }, Run(@"int x = 5; x++; x++; x--; ++x; --x; Api.Note($""{x}"");"));
        }

        [Test]
        public void IncDec_FloatAndDouble()
        {
            Assert.AreEqual(new[] { "2.5 0" }, Run(@"float f = 1.5; f++; double d = 1.0d; d--; Api.Note($""{f} {d}"");"));
        }

        [Test]
        public void IncDec_Fields_ArrayElements_HostProperty()
        {
            // поле класса через точку, своё поле триггера, элемент массива, свойство хоста
            var r = Compile(@"
                class C { int n = 0; }
                trigger T {
                    int own = 0;
                    event OnPing(Unit u) {
                        C.n++; C.n++; own++;
                        int[] a = new int[3]; a[1]++; a[1]++;
                        u.hp++;
                        Api.Note($""{C.n} {a[1]} {own}"");
                    }
                }");
            Assert.IsTrue(r.Success, Dump(r));
            var engine = new ScriptEngine(_host.Registry);
            engine.OnError += m => Assert.Fail(m);
            engine.LoadProgram(r.Program);
            var unit = new Unit { Hp = 10 };
            _log.Clear();
            _onPing.Raise(engine, unit);

            Assert.AreEqual(new[] { "2 2 1" }, _log);
            Assert.AreEqual(11, unit.Hp, "свойство хоста: прочитать, прибавить, записать");
        }

        [Test]
        public void IncDec_ListenerField()
        {
            var r = Compile(@"
                listener W { int hits = 0; event OnHit(Unit u, float d) { hits++; Api.Note($""{hits}""); } }
                trigger S { event OnPing(Unit u) { Engine.Attach(W, u); } }");
            Assert.IsTrue(r.Success, Dump(r));
            var engine = new ScriptEngine(_host.Registry);
            engine.OnError += m => Assert.Fail(m);
            engine.LoadProgram(r.Program);
            var a = new Unit();
            engine.Entities.Register(a);
            _onPing.Raise(engine, a);
            _log.Clear();
            _onHit.Raise(engine, a, 1f);
            _onHit.Raise(engine, a, 1f);
            Assert.AreEqual(new[] { "1", "2" }, _log);
        }

        [Test]
        public void PercentAssign()
        {
            Assert.AreEqual(new[] { "2" }, Run(@"int m = 17; m %= 5; Api.Note($""{m}"");"));
            Assert.AreEqual(1, Count(CompileBody("float f = 3.0; f %= 2;"), "E0215"), "% — только int % int, и %= тоже");
        }

        [Test]
        public void MinusMinus_WithSpaces_IsStillDoubleNegation()
        {
            Assert.AreEqual(new[] { "7" }, Run(@"int z = 5 - -2; Api.Note($""{z}"");"));
        }

        [Test]
        public void IncDec_Diagnostics()
        {
            Assert.AreEqual(1, Count(CompileBody(@"string s = ""a""; s++;"), "E0247"), "строка — не число (без правила «s += 1» склеило бы \"a1\")");
            Assert.AreEqual(1, Count(CompileBody("bool b = true; b--;"), "E0247"));
            Assert.AreEqual(1, Count(CompileBody("int x = 1; int y = x++;"), "E0248"), "значения у ++ нет");
            Assert.AreEqual(1, Count(CompileBody("int x = 1; int y = 0; y = ++x;"), "E0248"));
            Assert.AreEqual(1, Count(CompileBody("int x = 1; (x + 1)++;"), "E0248"), "не цель присваивания");

            var ro = Compile(@"trigger T { readonly int r = 1; event OnPing(Unit u) { r++; } }");
            Assert.AreEqual(1, Count(ro, "E0225"), "readonly — ровно одна ошибка\n" + Dump(ro));
        }
    }
}
