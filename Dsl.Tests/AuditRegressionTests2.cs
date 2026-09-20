using System;
using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Регрессии второго аудита (сентябрь 2026). Как и в AuditRegressionTests, каждый
    /// тест воспроизводит КОНКРЕТНЫЙ дефект: падение процесса, исключение в код игры,
    /// тихую порчу состояния или отключение всех скриптов из-за одного мода.
    /// </summary>
    public sealed class AuditRegressionTests2
    {
        public sealed class Unit { public string Name = "u"; }

        private sealed class NullResolver : ISaveEntityResolver
        {
            public long GetStableId(object entity) => 0L;
            public object ResolveStableId(long id) => null;
        }

        private HostBuilder _host;
        private EventRef<Unit> _onPing;
        private EventRef<Unit> _onHit;
        private List<string> _log;
        private List<string> _errors;
        private List<string> _warns;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _errors = new List<string>();
            _warns = new List<string>();
            _host = new HostBuilder();
            _host.Class<Unit>().Prop("name", u => u.Name);
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));
            _onPing = _host.Event<Unit>("OnPing");
            _onHit = _host.Event<Unit>("OnHit");
        }

        private static ModuleSourceSet Mod(string name, string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = name, ApiVersion = 1, Sources = new[] { "t.sal" } },
            };
            set.Files.Add(((name ?? "nameless") + "/t.sal", src));
            return set;
        }

        private CompilationResult Compile(string src) =>
            ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet> { Mod("t", src) });

        private static string Dump(CompilationResult r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            return sb.ToString();
        }

        private ScriptEngine Load(string src)
        {
            var r = Compile(src);
            Assert.IsTrue(r.Success, Dump(r));
            var e = new ScriptEngine(_host.Registry);
            e.OnError += _errors.Add;
            e.OnWarn += _warns.Add;
            e.LoadProgram(r.Program);
            return e;
        }

        // ===== рекурсия событий через хост роняла процесс ====================

        // Обработчик зовёт метод игры, тот поднимает то же событие: каждый виток —
        // новые кадры нативного стека C#. MaxCallDepth этого не видит, и раньше
        // цепочка кончалась StackOverflowException, которую поймать нельзя.
        [Test, Timeout(20000)]
        public void RecursiveRaiseThroughHost_IsRefused_NotStackOverflow()
        {
            ScriptEngine eng = null;
            int calls = 0;
            _host.Api("X").Act("Again", (Unit u) => { calls++; _onPing.Raise(eng, u); });
            eng = Load(@"trigger T { event OnPing(Unit u) { X.Again(u); } }");

            _onPing.Raise(eng, new Unit());

            Assert.AreEqual(eng.MaxNestedRunDepth, calls, "цепочка обязана оборваться ровно на пределе вложенности");
            Assert.AreEqual(1, _errors.Count, "ровно одно сообщение — про отклонённый запуск");
            StringAssert.Contains("вложенность", _errors[0]);

            // движок жив и после этого работает как обычно
            calls = 0;
            _onPing.Raise(eng, new Unit());
            Assert.AreEqual(eng.MaxNestedRunDepth, calls);
        }

        // ===== общий снапшот подписок портился вложенным обходом =============

        // OnUnsubscribe → хост → Raise события с listener-обработчиками очищал общий
        // список посреди внешнего foreach: InvalidOperationException улетала в код
        // игры, вторая подписка оставалась висеть, хэндл мёртвого юнита — валидным.
        [Test]
        public void InvalidateEntity_WithReentrantRaiseInOnUnsubscribe_DetachesAllAndInvalidates()
        {
            ScriptEngine eng = null;
            var other = new Unit { Name = "other" };
            _host.Api("X").Act("HitOther", () => _onHit.Raise(eng, other));
            eng = Load(@"
                listener Life {
                    event OnUnsubscribe() { Api.Note($""unsub {self.name}""); X.HitOther(); }
                    event OnHit(Unit u) { Api.Note($""hit {self.name}""); }
                }
                trigger Setup { event OnPing(Unit u) { Engine.Attach(Life, u); Engine.Attach(Life, u); } }");

            var a = new Unit { Name = "A" };
            _onPing.Raise(eng, a);
            _onPing.Raise(eng, other);
            var handle = eng.WrapObject(a);

            Assert.DoesNotThrow(() => eng.InvalidateEntity(a));

            Assert.AreEqual(2, eng.GetStats().LiveSubscriptions, "обе подписки A сняты, обе подписки other живы");
            Assert.IsFalse(eng.Entities.IsValid(handle), "хэндл мёртвого объекта обязан протухнуть");
            Assert.AreEqual(2, _log.FindAll(s => s == "unsub A").Count);
            Assert.IsEmpty(_errors);
        }

        // ===== карантин не срабатывал на дубликат и безымянный манифест ======

        [Test]
        public void Quarantine_DuplicateModuleName_DoesNotKillTheWholeBuild()
        {
            const string ok = "trigger {0} {{ event OnPing(Unit u) {{ }} }}";
            var r = ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet>
            {
                Mod("base", string.Format(ok, "T")),
                Mod("mymod", string.Format(ok, "A")),
                Mod("mymod", string.Format(ok, "B")),
            }, quarantineBrokenModules: true);

            Assert.IsTrue(r.Success, Dump(r));
            Assert.AreEqual(2, r.Program.Modules.Length, "base и первый mymod");
            Assert.AreEqual(1, r.Excluded.Count);
            Assert.AreEqual("mymod", r.Excluded[0].Name);
            StringAssert.Contains("E0301", r.Excluded[0].Reason);
        }

        [Test]
        public void Quarantine_NamelessManifest_DoesNotKillTheWholeBuild()
        {
            var r = ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet>
            {
                Mod("base", "trigger T { event OnPing(Unit u) { } }"),
                Mod(null, "trigger A { event OnPing(Unit u) { } }"),
            }, quarantineBrokenModules: true);

            Assert.IsTrue(r.Success, Dump(r));
            Assert.AreEqual(1, r.Excluded.Count);
            StringAssert.Contains("E0300", r.Excluded[0].Reason);
        }

        // без карантина контракт прежний: это ошибка сборки
        [Test]
        public void WithoutQuarantine_DuplicateModuleName_IsStillAnError()
        {
            var r = ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet>
            {
                Mod("m", "trigger A { event OnPing(Unit u) { } }"),
                Mod("m", "trigger B { event OnPing(Unit u) { } }"),
            });
            Assert.IsFalse(r.Success);
            StringAssert.Contains("E0301", Dump(r));
        }

        // ===== неудачная загрузка сейва оставляла движок сломанным ==========

        [Test]
        public void FailedLoadState_LeavesEngineUsable_AndClockUntouched()
        {
            const string src = @"
                class S { List<int> l = new List<int>(); }
                trigger T { event OnPing(Unit u) { S.l.Add(1); Api.Note($""count={S.l.count}""); } }";

            var e1 = Load(src);
            _onPing.Raise(e1, new Unit());
            e1.Tick(100f);
            var save = e1.SaveState(new NullResolver());
            var broken = new byte[save.Length - 6];
            Array.Copy(save, broken, broken.Length);

            var e2 = Load(src);
            e2.Tick(1f);
            _log.Clear();

            Assert.Throws<SaveStateException>(() => e2.LoadState(broken, new NullResolver()));

            Assert.AreEqual(1.0, e2.Time, 1e-9, "часы не должны остаться со временем из непрочитанного сейва");
            _onPing.Raise(e2, new Unit());
            Assert.IsEmpty(_errors, "статики обязаны быть переинициализированы, а не держать хэндлы на снесённые коллекции");
            Assert.AreEqual(new[] { "count=1" }, _log);
        }

        // ===== Nil в числовом слоте ломал типизацию ========================

        // `Nil + 1` VM считала как float: в int-переменной оказывался Float(1.0),
        // и его биты (1065353216) дальше шли как индекс.
        [Test]
        public void LocalWithoutInitializer_IsTypedZero()
        {
            var e = Load(@"trigger T { event OnPing(Unit u) {
                int x; x = x + 1;
                float f; bool b;
                List<int> l = new List<int>(); l.Add(10); l.Add(20);
                Api.Note($""{l[x]} {f} {b}"");
            } }");
            _onPing.Raise(e, new Unit());
            Assert.IsEmpty(_errors);
            Assert.AreEqual(new[] { "20 0 false" }, _log);
        }

        [Test]
        public void StaticReadBeforeItsInitializer_IsTypedZero()
        {
            var e = Load(@"
                class C { int a = C.b + 1; int b = 5; }
                trigger T { event OnPing(Unit u) {
                    List<int> l = new List<int>(); l.Add(10); l.Add(20);
                    Api.Note($""{C.a} {l[C.a]}"");
                } }");
            _onPing.Raise(e, new Unit());
            Assert.IsEmpty(_errors);
            Assert.AreEqual(new[] { "1 20" }, _log);
        }

        // ===== сборка строк залипала на каждом тике =========================

        // DynamicCount считал отметку максимума, а не живые строки: один всплеск
        // выше порога — и условие сборки оставалось истинным навсегда.
        [Test]
        public void StringSweep_AfterBurst_CountsLiveStringsOnly()
        {
            var e = Load(@"trigger T { event OnPing(Unit u) { for i in 0..6000 { var s = $""x{i}""; } } }");
            _onPing.Raise(e, new Unit());
            Assert.Greater(e.Strings.DynamicCount, e.StringSweepThreshold);

            e.Tick(0.016f); // сборка
            Assert.AreEqual(6000, e.GetStats().StringsSwept);
            Assert.AreEqual(0, e.Strings.DynamicCount, "после свипа живых динамических строк нет");

            e.Tick(0.016f);
            Assert.AreEqual(0, e.GetStats().StringsSwept);

            // освободившиеся id переиспользуются, счётчик остаётся честным
            _onPing.Raise(e, new Unit());
            Assert.AreEqual(6000, e.Strings.DynamicCount);
        }

        // Цепочка коллизий хэша: "Aa", "BB" и "C#" дают один хэш (h*31 + c).
        // Удаление из головы, середины и хвоста не должно терять соседей.
        [Test]
        public void StringTable_HashCollisionChain_SurvivesSweeps()
        {
            var t = new StringTable();
            t.FreezeStatics();
            int a = t.Intern("Aa"), b = t.Intern("BB"), c = t.Intern("C#");
            Assert.AreEqual(3, t.DynamicCount);

            t.BeginSweep(); t.Mark(a); t.Mark(c);           // уходит середина
            Assert.AreEqual(1, t.EndSweep());
            Assert.AreEqual(a, t.Intern("Aa"));
            Assert.AreEqual(c, t.Intern("C#"));
            Assert.AreEqual(2, t.DynamicCount);

            t.BeginSweep(); t.Mark(c);                      // уходит голова
            Assert.AreEqual(1, t.EndSweep());
            Assert.AreEqual(c, t.Intern("C#"));
            Assert.AreEqual("C#", t.Get(c));

            int b2 = t.Intern("BB");                        // снова в цепочке
            Assert.AreEqual("BB", t.Get(b2));
            Assert.AreEqual(b2, t.Intern("BB"));

            t.BeginSweep();                                 // уходит всё
            Assert.AreEqual(2, t.EndSweep());
            Assert.AreEqual(0, t.DynamicCount);
            Assert.AreEqual("Aa", t.Get(t.Intern("Aa")));
        }

        // ===== NaN в dt отравлял часы навсегда ==============================

        [Test]
        public void Tick_WithInvalidDt_DoesNotPoisonTheClock()
        {
            var e = Load(@"trigger T { event OnPing(Unit u) { wait 1.0; Api.Note(""woke""); } }");
            _onPing.Raise(e, new Unit());

            e.Tick(float.NaN);
            e.Tick(float.PositiveInfinity);
            e.Tick(-5f);
            Assert.AreEqual(0.0, e.Time, 1e-9);
            Assert.AreEqual(3, _warns.Count);

            e.Tick(1.5f);
            Assert.AreEqual(new[] { "woke" }, _log);
        }

        // ===== 17-й аргумент Raise давал IndexOutOfRange в код игры ==========

        [Test]
        public void Raise_WithMoreThanSixteenArguments_DoesNotThrow()
        {
            var e = Load(@"trigger T { event OnPing(Unit u) { Api.Note(""ok""); } }");
            Assert.DoesNotThrow(() =>
            {
                var scope = e.Raise(_onPing.Id).AddEntity(new Unit());
                for (int i = 0; i < 40; i++) scope = scope.AddInt(i);
                scope.Commit();
            });
            Assert.AreEqual(new[] { "ok" }, _log);
        }

        // ===== модуль без исходников молча «успешно» компилировался ==========

        [Test]
        public void ModuleWithoutSources_CompilesButWarns()
        {
            var empty = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "empty", ApiVersion = 1 },
            };
            var r = ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet> { empty });
            Assert.IsTrue(r.Success, Dump(r));
            StringAssert.Contains("W0300", Dump(r));
        }
    }
}
