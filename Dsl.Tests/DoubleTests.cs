using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// double как полноценный числовой тип: литералы 1.5d, C#-подобная иерархия
    /// int→float→double (расширение вверх, вниз только явно), коллекции,
    /// интерполяция, интеграция с хостовым API (главная причина типа).
    /// </summary>
    public sealed class DoubleTests
    {
        public sealed class Unit { public string Name; }

        private sealed class NullResolver : ISaveEntityResolver
        {
            public long GetStableId(object entity) => 0;
            public object ResolveStableId(long id) => null;
        }

        private HostBuilder _host;
        private EventRef<Unit> _onPing;
        private List<string> _log;
        private double _lastDouble;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _lastDouble = 0;
            _host = new HostBuilder();
            _host.Class<Unit>().Prop("name", u => u.Name);
            var api = _host.Api("Api");
            api.Act("Note", (string s) => _log.Add(s));
            // хостовые методы с double — ради чего тип и заводился
            api.Act("Take", (double d) => _lastDouble = d);
            api.Fn("Precise", () => 0.1 + 0.2);            // double-возврат
            api.Fn("Scale", (double x, double k) => x * k); // double-аргументы
            _onPing = _host.Event<Unit>("OnPing");
        }

        private CompilationResult Compile(string body)
        {
            var src = "trigger T { event OnPing(Unit u) { " + body + " } }";
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "t", ApiVersion = 1, Sources = new[] { "t.sal" } },
            };
            set.Files.Add(("t/t.sal", src));
            return ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet> { set });
        }

        private static string Dump(CompilationResult r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            return sb.ToString();
        }

        private static bool Has(CompilationResult r, string code)
        {
            foreach (var d in r.Diagnostics) if (d.Code == code) return true;
            return false;
        }

        private void Run(string body)
        {
            var r = Compile(body);
            Assert.IsTrue(r.Success, Dump(r));
            var e = new ScriptEngine(_host.Registry);
            e.OnError += m => Assert.Fail(m);
            e.LoadProgram(r.Program);
            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());
        }

        [Test]
        public void DoubleLiteral_AndInterpolation()
        {
            Run(@"double d = 1.5d; Api.Note($""{d}"");");
            Assert.AreEqual(new[] { "1.5" }, _log);
        }

        [Test]
        public void HostReturnsDouble_PrecisionKept()
        {
            // 0.1+0.2 в double != 0.3 — но именно этой точности и хотим
            Run(@"double d = Api.Precise(); Api.Note($""{d}"");");
            Assert.AreEqual(1, _log.Count);
            Assert.IsTrue(_log[0].StartsWith("0.30000000000000004"),
                "double сохраняет полную точность, не усечён как float: " + _log[0]);
        }

        [Test]
        public void HostTakesDouble_FromScript()
        {
            Run(@"Api.Take(3.5d);");
            Assert.AreEqual(3.5, _lastDouble, 1e-12);
        }

        [Test]
        public void FloatWidensToDouble_Implicitly()
        {
            // float-аргумент проходит в double-параметр хоста без явного каста
            Run(@"float f = 2.5; Api.Take(f);");
            Assert.AreEqual(2.5, _lastDouble, 1e-12);
        }

        [Test]
        public void IntWidensToDouble()
        {
            Run(@"Api.Take(7);");
            Assert.AreEqual(7.0, _lastDouble, 1e-12);
        }

        [Test]
        public void MixedArithmetic_ResultIsDouble()
        {
            // double * int -> double, полная точность
            Run(@"double d = 0.1d; double r = d * 3; Api.Note($""{r}"");");
            Assert.AreEqual(1, _log.Count);
            Assert.IsTrue(_log[0].StartsWith("0.30000000000000004"), _log[0]);
        }

        [Test]
        public void DoubleArithmetic_ThroughHost()
        {
            Run(@"double r = Api.Scale(1.5d, 4.0d); Api.Note($""{r}"");");
            Assert.AreEqual(new[] { "6" }, _log);
        }

        [Test]
        public void ListOfDouble_Works()
        {
            Run(@"List<double> xs = new List<double>();
                  xs.Add(1.5d); xs.Add(2.5d);
                  double sum = 0.0d;
                  for x in xs { sum = sum + x; }
                  Api.Note($""{sum}"");");
            Assert.AreEqual(new[] { "4" }, _log);
        }

        [Test]
        public void MapWithDoubleValues_Works()
        {
            Run(@"Map<string, double> m = new Map<string, double>();
                  m[""a""] = 1.25d;
                  Api.Note($""{m[""a""]}"");");
            Assert.AreEqual(new[] { "1.25" }, _log);
        }

        [Test]
        public void DoubleField_SurvivesSave()
        {
            // round-trip через поле-double триггера
            var full = ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet>
            {
                Mk(@"trigger S {
                    double acc = 0.0d;
                    event OnPing(Unit u) { acc = acc + 1.5d; Api.Note($""{acc}""); }
                }")
            });
            Assert.IsTrue(full.Success, Dump(full));

            var res = new NullResolver();
            var e1 = new ScriptEngine(_host.Registry);
            e1.LoadProgram(full.Program);
            e1.Tick(0.016f);
            _onPing.Raise(e1, new Unit()); // acc = 1.5
            byte[] save = e1.SaveState(res);

            _log.Clear();
            var e2 = new ScriptEngine(_host.Registry);
            e2.LoadProgram(full.Program);
            e2.LoadState(save, res);
            e2.Tick(0.016f);
            _onPing.Raise(e2, new Unit()); // acc = 3.0
            Assert.AreEqual(new[] { "3" }, _log);
        }

        private static ModuleSourceSet Mk(string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "t", ApiVersion = 1, Sources = new[] { "t.sal" } },
            };
            set.Files.Add(("t/t.sal", src));
            return set;
        }
    }
}
