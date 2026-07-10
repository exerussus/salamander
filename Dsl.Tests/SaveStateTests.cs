using System;
using System.Collections.Generic;
using Dsl.Codegen;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Контракт сериализации: полный снапшот рантайма переживает выгрузку и
    /// восстановление в новом движке с той же программой — включая файберы
    /// посреди wait, подписки listener и entity-хэндлы через стабильные id.
    /// </summary>
    public sealed class SaveStateTests
    {
        public sealed class Unit
        {
            public long Id;
            public string Name;
        }

        private sealed class Resolver : ISaveEntityResolver
        {
            public readonly Dictionary<long, object> World = new Dictionary<long, object>();
            public long GetStableId(object entity) => entity is Unit u ? u.Id : 0;
            public object ResolveStableId(long id) => World.TryGetValue(id, out var o) ? o : null;
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
            _host.Class<Unit>().Prop("name", u => u.Name);
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));
            _onPing = _host.Event<Unit>("OnPing");
            _onHit = _host.Event<Unit, float>("OnHit");
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

        private ScriptEngine NewEngine(CompiledProgram prog)
        {
            var e = new ScriptEngine(_host.Registry);
            e.OnError += m => Assert.Fail(m);
            e.LoadProgram(prog);
            return e;
        }

        [Test]
        public void MidWaitFiber_ResumesAfterLoad()
        {
            var r = Compile(@"trigger T {
                int n = 0;
                event OnPing(Unit u) { n = n + 1; Api.Note($""start {n}""); wait 5.0; Api.Note($""end {n}""); }
            }");
            Assert.IsTrue(r.Success);
            var res = new Resolver();

            var e1 = NewEngine(r.Program);
            e1.Tick(0.016f);
            _onPing.Raise(e1, new Unit { Id = 1 });
            Assert.AreEqual(new[] { "start 1" }, _log);
            byte[] save = e1.SaveState(res);

            _log.Clear();
            var e2 = NewEngine(r.Program);   // та же программа → тот же отпечаток
            e2.LoadState(save, res);
            e2.Tick(2.0f);
            Assert.AreEqual(0, _log.Count, "5 секунд ещё не прошло");
            e2.Tick(3.1f);
            Assert.AreEqual(new[] { "end 1" }, _log, "файбер проснулся с сохранённым состоянием");
            Assert.AreEqual(0, e2.GetStats().LiveFibers);
        }

        [Test]
        public void Statics_DynamicStrings_Collections_Survive()
        {
            var r = Compile(@"trigger T {
                int n = 0;
                string label = """";
                List<int> nums = new List<int>();
                event OnPing(Unit u) {
                    if (n == 0) {
                        n = 41;
                        label = $""hp:{n}"";      // динамическая строка
                        nums.Add(7); nums.Add(9);
                    } else {
                        Api.Note($""{n} {label} {nums.count} {nums[1]}"");
                    }
                }
            }");
            Assert.IsTrue(r.Success);
            var res = new Resolver();

            var e1 = NewEngine(r.Program);
            e1.Tick(0.016f);
            _onPing.Raise(e1, new Unit { Id = 1 });
            byte[] save = e1.SaveState(res);

            var e2 = NewEngine(r.Program);
            e2.LoadState(save, res);
            e2.Tick(0.016f);
            _onPing.Raise(e2, new Unit { Id = 1 });

            Assert.AreEqual(new[] { "41 hp:41 2 9" }, _log);
        }

        [Test]
        public void EntityHandles_RemapViaStableIds()
        {
            var r = Compile(@"trigger T {
                Unit hero = null;
                event OnPing(Unit u) {
                    if (hero == null) { hero = u; }
                    else {
                        if (Engine.IsValid(hero)) { Api.Note($""alive {hero.name}""); }
                        else { Api.Note(""dead""); }
                    }
                }
            }");
            Assert.IsTrue(r.Success);

            var res = new Resolver();
            var oldWorldHero = new Unit { Id = 42, Name = "Old" };

            var e1 = NewEngine(r.Program);
            e1.Tick(0.016f);
            _onPing.Raise(e1, oldWorldHero); // hero запомнен в статике
            byte[] save = e1.SaveState(res);

            // новый мир: НОВЫЙ объект с тем же стабильным id
            var newWorldHero = new Unit { Id = 42, Name = "New" };
            res.World[42] = newWorldHero;

            var e2 = NewEngine(r.Program);
            e2.LoadState(save, res);
            e2.Tick(0.016f);
            _onPing.Raise(e2, new Unit { Id = 7, Name = "other" });

            Assert.AreEqual(new[] { "alive New" }, _log, "хэндл в статике ремапнут на новый объект");
        }

        [Test]
        public void DeadEntity_BecomesStaleHandle()
        {
            var r = Compile(@"trigger T {
                Unit hero = null;
                event OnPing(Unit u) {
                    if (hero == null) { hero = u; }
                    else {
                        if (Engine.IsValid(hero)) { Api.Note(""alive""); } else { Api.Note(""dead""); }
                    }
                }
            }");
            Assert.IsTrue(r.Success);
            var res = new Resolver();

            var e1 = NewEngine(r.Program);
            e1.Tick(0.016f);
            _onPing.Raise(e1, new Unit { Id = 42 });
            byte[] save = e1.SaveState(res);

            // мир пуст — id 42 не разрезолвится
            var e2 = NewEngine(r.Program);
            e2.LoadState(save, res);
            e2.Tick(0.016f);
            _onPing.Raise(e2, new Unit { Id = 7 });

            Assert.AreEqual(new[] { "dead" }, _log, "нерезолвнутый хэндл протух — семантика «юнит умер»");
        }

        [Test]
        public void Subscriptions_SurviveWithFields()
        {
            var r = Compile(@"
                listener Counter {
                    int hits = 0;
                    event OnHit(Unit u, float d) { hits = hits + 1; Api.Note($""{hits}""); }
                }
                trigger Setup {
                    Subscription s;
                    event OnPing(Unit u) { s = Engine.Attach(Counter, u); }
                }");
            Assert.IsTrue(r.Success);
            var res = new Resolver();
            var hero1 = new Unit { Id = 5, Name = "H" };

            var e1 = NewEngine(r.Program);
            e1.Tick(0.016f);
            _onPing.Raise(e1, hero1);
            _onHit.Raise(e1, hero1, 1f);           // hits = 1
            byte[] save = e1.SaveState(res);

            var hero2 = new Unit { Id = 5, Name = "H2" };
            res.World[5] = hero2;

            var e2 = NewEngine(r.Program);
            e2.LoadState(save, res);
            Assert.AreEqual(1, e2.GetStats().LiveSubscriptions);
            e2.Tick(0.016f);
            _onHit.Raise(e2, hero2, 1f);           // hits продолжает: 2

            Assert.AreEqual(new[] { "1", "2" }, _log, "поле подписки пережило сейв");
        }

        [Test]
        public void Subscription_TargetGone_IsDropped()
        {
            var r = Compile(@"
                listener W { event OnHit(Unit u, float d) { Api.Note(""x""); } }
                trigger Setup { event OnPing(Unit u) { Engine.Attach(W, u); } }");
            Assert.IsTrue(r.Success);
            var res = new Resolver();

            var e1 = NewEngine(r.Program);
            e1.Tick(0.016f);
            _onPing.Raise(e1, new Unit { Id = 9 });
            byte[] save = e1.SaveState(res);

            var e2 = NewEngine(r.Program); // мир пуст — цель не разрезолвится
            e2.LoadState(save, res);
            Assert.AreEqual(0, e2.GetStats().LiveSubscriptions, "подписка на исчезнувшую цель дропнута");
        }

        [Test]
        public void FingerprintMismatch_Throws()
        {
            var r1 = Compile(@"trigger T { event OnPing(Unit u) { Api.Note(""a""); } }");
            var r2 = Compile(@"trigger T { event OnPing(Unit u) { Api.Note(""b""); } }"); // другой литерал
            Assert.IsTrue(r1.Success && r2.Success);
            var res = new Resolver();

            var e1 = NewEngine(r1.Program);
            e1.Tick(0.016f);
            byte[] save = e1.SaveState(res);

            var e2 = NewEngine(r2.Program);
            Assert.Throws<SaveStateException>(() => e2.LoadState(save, res));
        }

        [Test]
        public void SaveInsideScript_Throws()
        {
            ScriptEngine captured = null;
            var res = new Resolver();
            _host.Api("Sys").Act("TrySave", () =>
            {
                var ex = Assert.Throws<InvalidOperationException>(() => captured.SaveState(res));
                StringAssert.Contains("между тиками", ex.Message);
            });
            var r = Compile(@"trigger T { event OnPing(Unit u) { Sys.TrySave(); } }");
            Assert.IsTrue(r.Success);

            captured = NewEngine(r.Program);
            captured.Tick(0.016f);
            _onPing.Raise(captured, new Unit { Id = 1 });
        }

        [Test]
        public void StringIdResolver_Helper_Works()
        {
            var byName = new StringIdResolver(o => ((Unit)o).Name);
            var u = new Unit { Name = "boss_01" };
            long id = byName.GetStableId(u);
            Assert.AreNotEqual(0, id);

            var loaded = new StringIdResolver();
            var u2 = new Unit { Name = "boss_01" };
            loaded.RegisterForLoad("boss_01", u2);
            Assert.AreSame(u2, loaded.ResolveStableId(id));
            Assert.IsNull(loaded.ResolveStableId(StringIdResolver.Hash("other")));
        }

        [Test]
        public void TimersAndTime_PreserveRemaining()
        {
            var r = Compile(@"trigger T { event OnPing(Unit u) { wait 10.0; Api.Note(""fired""); } }");
            Assert.IsTrue(r.Success);
            var res = new Resolver();

            var e1 = NewEngine(r.Program);
            e1.Tick(0.016f);
            _onPing.Raise(e1, new Unit { Id = 1 });
            e1.Tick(4.0f); // прошло 4 из 10
            byte[] save = e1.SaveState(res);

            var e2 = NewEngine(r.Program);
            e2.LoadState(save, res);
            e2.Tick(5.0f);
            Assert.AreEqual(0, _log.Count, "прошло 9 из 10 — рано");
            e2.Tick(1.1f);
            Assert.AreEqual(new[] { "fired" }, _log, "остаток таймера сохранился точно");
        }
    }
}
