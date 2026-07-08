using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Контракт listener: подписка на события КОНКРЕТНОЙ сущности (субъект —
    /// первый параметр), свои поля на подписку (пулируются), self, wait/spawn
    /// с гибелью файберов на detach, OnSubscribe/OnUnsubscribe, авто-detach
    /// при смерти цели.
    /// </summary>
    public sealed class ListenerTests
    {
        public sealed class Unit { public string Name; }

        private HostBuilder _host;
        private EventRef<Unit, float> _onHit;
        private EventRef<Unit> _onPing;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _host = new HostBuilder();
            _host.Class<Unit>().Prop("name", u => u.Name);
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));
            _onHit = _host.Event<Unit, float>("OnHit");
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

        private ScriptEngine Load(CompilationResult r)
        {
            Assert.IsTrue(r.Success, Dump(r));
            var engine = new ScriptEngine(_host.Registry);
            engine.OnError += m => Assert.Fail(m);
            engine.LoadProgram(r.Program);
            engine.Tick(0.016f);
            return engine;
        }

        // Подписка через триггер (listener сам не активен) + диспатч только цели.
        [Test]
        public void Attach_DispatchesOnlyToBoundEntity()
        {
            var r = Compile(@"
                listener Watcher {
                    event OnHit(Unit u, float dmg) { Api.Note($""hit {u.name}""); }
                }
                trigger Setup {
                    event OnPing(Unit u) { Engine.Attach(Watcher, u); }
                }");
            var engine = Load(r);

            var a = new Unit { Name = "A" };
            var b = new Unit { Name = "B" };
            var ha = engine.Entities.Register(a);
            var hb = engine.Entities.Register(b);

            _onPing.Raise(engine, a);      // подписали Watcher на A
            _onHit.Raise(engine, a, 5f);   // событие ПРО A — обработчик сработал
            _onHit.Raise(engine, b, 5f);   // событие про B — подписки нет, тишина

            Assert.AreEqual(1, _log.Count);
            Assert.AreEqual("hit A", _log[0]);
            Assert.AreEqual(1, engine.GetStats().LiveSubscriptions);
            _ = ha; _ = hb;
        }

        // self + поля: у каждой подписки СВОЙ счётчик; multi-attach независим.
        [Test]
        public void Fields_ArePerSubscription_MultiAttachIndependent()
        {
            var r = Compile(@"
                listener Counter {
                    int hits = 0;
                    event OnHit(Unit u, float dmg) {
                        hits = hits + 1;
                        Api.Note($""{self.name}:{hits}"");
                    }
                }
                trigger Setup {
                    event OnPing(Unit u) {
                        Engine.Attach(Counter, u);
                        Engine.Attach(Counter, u); // вторая независимая подписка
                    }
                }");
            var engine = Load(r);

            var a = new Unit { Name = "A" };
            engine.Entities.Register(a);
            _onPing.Raise(engine, a);

            _onHit.Raise(engine, a, 1f);
            _onHit.Raise(engine, a, 1f);

            // две подписки × два события; счётчики у каждой свои
            Assert.AreEqual(new[] { "A:1", "A:1", "A:2", "A:2" }, _log);
            Assert.AreEqual(2, engine.GetStats().LiveSubscriptions);
        }

        // Пул: после detach блок возвращается, при новом attach поля СБРОШЕНЫ.
        [Test]
        public void Pool_ResetsFields_OnReattach()
        {
            var r = Compile(@"
                listener Counter {
                    int hits = 10;
                    event OnHit(Unit u, float dmg) { hits = hits + 1; Api.Note($""{hits}""); }
                }
                trigger Setup {
                    Subscription s;
                    event OnPing(Unit u) {
                        if (Engine.IsSubscribed(s)) {
                            Engine.Detach(s);
                            s = Engine.Attach(Counter, u); // переиспользует блок из пула
                        } else {
                            s = Engine.Attach(Counter, u);
                        }
                    }
                }");
            var engine = Load(r);

            var a = new Unit { Name = "A" };
            engine.Entities.Register(a);

            _onPing.Raise(engine, a);          // attach #1
            _onHit.Raise(engine, a, 1f);       // 11
            _onHit.Raise(engine, a, 1f);       // 12
            _onPing.Raise(engine, a);          // detach + attach #2 (пул)
            _onHit.Raise(engine, a, 1f);       // снова 11 — поле сброшено к инициализатору

            Assert.AreEqual(new[] { "11", "12", "11" }, _log);
            Assert.AreEqual(1, engine.GetStats().LiveSubscriptions);
        }

        // wait/spawn живут в подписке; Detach убивает её файберы.
        [Test]
        public void Detach_KillsSubscriptionFibers()
        {
            var r = Compile(@"
                listener Regen {
                    event OnSubscribe() { spawn Loop(); }
                    event OnHit(Unit u, float dmg) { }
                    func Loop() {
                        while (true) { Api.Note($""tick {self.name}""); wait 1.0; }
                    }
                }
                trigger Setup {
                    Subscription s;
                    event OnPing(Unit u) {
                        if (Engine.IsSubscribed(s)) { Engine.Detach(s); }
                        else { s = Engine.Attach(Regen, u); }
                    }
                }");
            var engine = Load(r);

            var a = new Unit { Name = "A" };
            engine.Entities.Register(a);

            _onPing.Raise(engine, a);      // attach: OnSubscribe спавнит цикл
            engine.Tick(0.016f);           // цикл стартует: tick A
            engine.Tick(1.1f);             // проснулся: tick A (второй)
            int before = _log.Count;
            Assert.GreaterOrEqual(before, 2);

            _onPing.Raise(engine, a);      // detach — файбер цикла должен умереть
            Assert.AreEqual(0, engine.GetStats().LiveSubscriptions);

            engine.Tick(1.1f);
            engine.Tick(1.1f);
            Assert.AreEqual(before, _log.Count, "после detach цикл не должен тикать");
            Assert.AreEqual(0, engine.GetStats().LiveFibers);
        }

        // OnSubscribe/OnUnsubscribe вызываются; авто-detach при смерти цели.
        [Test]
        public void AutoDetach_OnEntityInvalidate_CallsOnUnsubscribe()
        {
            var r = Compile(@"
                listener Life {
                    event OnSubscribe() { Api.Note($""sub {self.name}""); }
                    event OnUnsubscribe() { Api.Note($""unsub {self.name}""); }
                    event OnHit(Unit u, float dmg) { }
                }
                trigger Setup {
                    event OnPing(Unit u) { Engine.Attach(Life, u); }
                }");
            var engine = Load(r);

            var a = new Unit { Name = "A" };
            engine.Entities.Register(a);
            _onPing.Raise(engine, a);
            Assert.AreEqual(1, engine.GetStats().LiveSubscriptions);

            engine.InvalidateEntity(a); // смерть цели → авто-detach, OnUnsubscribe видит живой хэндл

            Assert.AreEqual(new[] { "sub A", "unsub A" }, _log);
            Assert.AreEqual(0, engine.GetStats().LiveSubscriptions);
        }

        [Test]
        public void DetachAll_RemovesAllMatching()
        {
            var r = Compile(@"
                listener W { event OnHit(Unit u, float dmg) { Api.Note(""x""); } }
                trigger Setup {
                    event OnPing(Unit u) {
                        Engine.Attach(W, u);
                        Engine.Attach(W, u);
                        Engine.DetachAll(W, u);
                    }
                }");
            var engine = Load(r);

            var a = new Unit { Name = "A" };
            engine.Entities.Register(a);
            _onPing.Raise(engine, a);
            _onHit.Raise(engine, a, 1f);

            Assert.AreEqual(0, _log.Count);
            Assert.AreEqual(0, engine.GetStats().LiveSubscriptions);
        }

        // ===== ошибки компиляции =====

        [Test]
        public void SelfOutsideListener_IsError()
        {
            var r = Compile(@"trigger T { event OnPing(Unit u) { Api.Note(self.name); } }");
            Assert.IsTrue(Has(r, "E0177"), Dump(r));
        }

        [Test]
        public void WaitInOnUnsubscribe_IsError()
        {
            var r = Compile(@"
                listener L {
                    event OnUnsubscribe() { wait 1.0; }
                    event OnHit(Unit u, float dmg) { }
                }");
            Assert.IsTrue(Has(r, "E0178"), Dump(r));
        }

        [Test]
        public void ListenerWithoutHostEvents_IsError()
        {
            var r = Compile(@"listener L { event OnSubscribe() { } }");
            Assert.IsTrue(Has(r, "E0171"), Dump(r));
        }

        [Test]
        public void FirstParamNotEntity_IsError()
        {
            _host.Event<float, float>("OnTimer"); // первый параметр — не сущность
            var r = Compile(@"listener L { event OnTimer(float a, float b) { } }");
            Assert.IsTrue(Has(r, "E0173"), Dump(r));
        }

        [Test]
        public void ListenerMembers_PrivateOutside()
        {
            var r = Compile(@"
                listener L { int x = 0; event OnHit(Unit u, float d) { } }
                trigger T { event OnPing(Unit u) { Api.Note($""{L.x}""); } }");
            Assert.IsTrue(Has(r, "E0179"), Dump(r));
        }

        [Test]
        public void ActionInListener_IsError()
        {
            var r = Compile(@"listener L { action Do() { } event OnHit(Unit u, float d) { } }");
            Assert.IsTrue(Has(r, "E0176"), Dump(r));
        }
    }
}
