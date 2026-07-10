using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Контракт снапшот-итерации: for-in видит коллекцию на момент входа,
    /// менять её в теле безопасно; для Map удалённые ключи пропускаются,
    /// значения (вторая переменная) читаются живыми. Буферы живут на файбере:
    /// переживают wait и сейв, освобождаются на выходе и при смерти файбера.
    /// </summary>
    public sealed class IterationTests
    {
        public sealed class Unit { public long Id; }

        private sealed class Resolver : ISaveEntityResolver
        {
            public long GetStableId(object e) => e is Unit u ? u.Id : 0;
            public object ResolveStableId(long id) => null;
        }

        private HostBuilder _host;
        private EventRef<Unit> _onPing;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _host = new HostBuilder();
            _host.Class<Unit>();
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));
            _onPing = _host.Event<Unit>("OnPing");
        }

        private CompilationResult Compile(string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "test", ApiVersion = 1, Sources = new[] { "t.sal" } },
            };
            set.Files.Add(("test/t.sal", src));
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

        private ScriptEngine Run(CompilationResult r)
        {
            Assert.IsTrue(r.Success, Dump(r));
            var engine = new ScriptEngine(_host.Registry);
            engine.OnError += m => Assert.Fail(m);
            engine.LoadProgram(r.Program);
            engine.Tick(0.016f);
            _onPing.Raise(engine, new Unit { Id = 1 });
            return engine;
        }

        // Удаление ТЕКУЩЕГО ключа в теле — безопасно, обходятся все.
        [Test]
        public void Map_RemoveCurrentKey_SafeAndComplete()
        {
            var engine = Run(Compile(@"trigger T {
                event OnPing(Unit u) {
                    Map<string, int> m = new Map<string, int>();
                    m[""a""] = 1; m[""b""] = 2; m[""c""] = 3;
                    int visited = 0;
                    for k in m { visited = visited + 1; m.Remove(k); }
                    Api.Note($""{visited} {m.count}"");
                }
            }"));
            Assert.AreEqual(new[] { "3 0" }, _log);
        }

        // Добавленное во время цикла НЕ итерируется (снапшот ключей на входе).
        [Test]
        public void Map_AddedDuringLoop_NotVisited()
        {
            var engine = Run(Compile(@"trigger T {
                event OnPing(Unit u) {
                    Map<int, int> m = new Map<int, int>();
                    m[1] = 10; m[2] = 20; m[3] = 30;
                    int visited = 0;
                    for k in m { visited = visited + 1; m[k + 100] = 0; }
                    Api.Note($""{visited} {m.count}"");
                }
            }"));
            Assert.AreEqual(new[] { "3 6" }, _log);
        }

        // Пара k, v: v — живое значение map[k]; суммы сходятся без опоры на порядок.
        [Test]
        public void Map_PairForm_BindsLiveValues()
        {
            var engine = Run(Compile(@"trigger T {
                event OnPing(Unit u) {
                    Map<string, int> m = new Map<string, int>();
                    m[""a""] = 1; m[""b""] = 2; m[""c""] = 3;
                    int sum = 0;
                    for k, v in m { sum = sum + v; }
                    Api.Note($""{sum}"");
                }
            }"));
            Assert.AreEqual(new[] { "6" }, _log);
        }

        // List: Clear в первой итерации — снапшот всё равно обойдёт все элементы.
        [Test]
        public void List_ClearInsideLoop_VisitsSnapshot()
        {
            var engine = Run(Compile(@"trigger T {
                event OnPing(Unit u) {
                    List<int> l = new List<int>();
                    l.Add(1); l.Add(2); l.Add(3);
                    int sum = 0;
                    for x in l { sum = sum + x; l.Clear(); }
                    Api.Note($""{sum} {l.count}"");
                }
            }"));
            Assert.AreEqual(new[] { "6 0" }, _log);
        }

        // List: добавления в цикле не итерируются (иначе — бесконечный цикл).
        [Test]
        public void List_AddDuringLoop_NotVisited()
        {
            var engine = Run(Compile(@"trigger T {
                event OnPing(Unit u) {
                    List<int> l = new List<int>();
                    l.Add(1); l.Add(2); l.Add(3);
                    int visited = 0;
                    for x in l { visited = visited + 1; l.Add(99); }
                    Api.Note($""{visited} {l.count}"");
                }
            }"));
            Assert.AreEqual(new[] { "3 6" }, _log);
        }

        // break и return сквозь for-in: буферы закрываются, дальше всё работает.
        [Test]
        public void BreakAndReturn_KeepIterationBalanced()
        {
            var engine = Run(Compile(@"trigger T {
                event OnPing(Unit u) {
                    List<int> l = new List<int>();
                    l.Add(1); l.Add(2); l.Add(3);
                    int n = First(l);
                    for x in l { n = n + x; break; }
                    // после break/return снапшоты закрыты — новый цикл работает как обычно
                    int sum = 0;
                    for x in l { sum = sum + x; }
                    Api.Note($""{n} {sum}"");
                }
                func First(List<int> l) -> int {
                    for x in l { return x; } // return изнутри for-in
                    return -1;
                }
            }"));
            Assert.AreEqual(new[] { "2 6" }, _log);
        }

        // wait внутри цикла по мапе: буфер живёт на файбере через приостановки.
        [Test]
        public void Wait_InsideMapLoop_SurvivesTicks()
        {
            var r = Compile(@"trigger T {
                event OnPing(Unit u) {
                    Map<int, int> m = new Map<int, int>();
                    m[1] = 1; m[2] = 2;
                    int sum = 0;
                    for k, v in m { sum = sum + v; wait 1.0; }
                    Api.Note($""{sum}"");
                }
            }");
            Assert.IsTrue(r.Success, Dump(r));
            var engine = new ScriptEngine(_host.Registry);
            engine.OnError += m => Assert.Fail(m);
            engine.LoadProgram(r.Program);
            engine.Tick(0.016f);
            _onPing.Raise(engine, new Unit { Id = 1 });
            Assert.AreEqual(0, _log.Count, "цикл спит на первом wait");
            engine.Tick(1.1f);
            engine.Tick(1.1f);
            Assert.AreEqual(new[] { "3" }, _log);
            Assert.AreEqual(0, engine.GetStats().LiveFibers);
        }

        // Сейв посреди итерации (на wait внутри цикла) — доитерируется после лоада.
        [Test]
        public void SaveLoad_MidIteration_Completes()
        {
            var r = Compile(@"trigger T {
                event OnPing(Unit u) {
                    List<int> l = new List<int>();
                    l.Add(10); l.Add(20); l.Add(30);
                    int sum = 0;
                    for x in l { sum = sum + x; wait 1.0; }
                    Api.Note($""{sum}"");
                }
            }");
            Assert.IsTrue(r.Success, Dump(r));
            var res = new Resolver();

            var e1 = new ScriptEngine(_host.Registry);
            e1.OnError += m => Assert.Fail(m);
            e1.LoadProgram(r.Program);
            e1.Tick(0.016f);
            _onPing.Raise(e1, new Unit { Id = 1 });
            e1.Tick(1.1f); // прошли первый элемент, спим внутри цикла
            byte[] save = e1.SaveState(res);

            var e2 = new ScriptEngine(_host.Registry);
            e2.OnError += m => Assert.Fail(m);
            e2.LoadProgram(r.Program);
            e2.LoadState(save, res);
            e2.Tick(1.1f);
            e2.Tick(1.1f);

            Assert.AreEqual(new[] { "60" }, _log, "итерация продолжилась после сейва с того же места");
        }

        // Вложенные снапшоты: карта в списке.
        [Test]
        public void Nested_Iterations_Work()
        {
            var engine = Run(Compile(@"trigger T {
                event OnPing(Unit u) {
                    List<int> l = new List<int>();
                    l.Add(1); l.Add(2);
                    Map<int, int> m = new Map<int, int>();
                    m[10] = 100; m[20] = 200;
                    int sum = 0;
                    for x in l {
                        for k, v in m { sum = sum + x * v; }
                    }
                    Api.Note($""{sum}"");
                }
            }"));
            // (1+2) * (100+200) = 900
            Assert.AreEqual(new[] { "900" }, _log);
        }

        // ===== ошибки компиляции =====

        [Test]
        public void TwoVars_OnList_IsError()
        {
            var r = Compile(@"trigger T {
                event OnPing(Unit u) {
                    List<int> l = new List<int>();
                    for a, b in l { }
                }
            }");
            Assert.IsTrue(Has(r, "E0206"), Dump(r));
        }

        [Test]
        public void TwoVars_OnRange_IsError()
        {
            var r = Compile(@"trigger T {
                event OnPing(Unit u) { for a, b in 0..3 { } }
            }");
            Assert.IsTrue(Has(r, "E0072"), Dump(r));
        }
    }
}
