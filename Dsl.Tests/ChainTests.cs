using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Мерж-цепочки членов: слои before/after, replace, base() и гейт модулей.
    /// Все версии одного члена (события, функции, action, OnSubscribe) — одна
    /// цепочка в порядке загрузки; ядро — последняя обычная версия или replace,
    /// слои ложатся вокруг него, replace стирает всё, что было раньше, а
    /// выключенный модуль прозрачен.
    /// </summary>
    public sealed class ChainTests
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
        private EventRef<string> _onCmd;
        private ArchEventRef<Unit, float> _onCast;
        private List<string> _log;

        // выключатель модулей: сам ни от кого не зависит, модули — строки
        private const string CtlSrc = @"
            trigger Ctl {
                event OnCmd(string c) {
                    if (c == ""-game"") { Engine.DisableModule(""game""); }
                    if (c == ""+game"") { Engine.EnableModule(""game""); }
                    if (c == ""-a"") { Engine.DisableModule(""modA""); }
                    if (c == ""+a"") { Engine.EnableModule(""modA""); }
                    if (c == ""-b"") { Engine.DisableModule(""modB""); }
                    if (c == ""+b"") { Engine.EnableModule(""modB""); }
                }
            }";

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _host = new HostBuilder();
            _host.Class<Unit>().Prop("name", u => u.Name);
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));
            _onPing = _host.Event<Unit>("OnPing");
            _onHit = _host.Event<Unit, float>("OnHit");
            _onCmd = _host.Event<string>("OnCmd");
            var spell = _host.Archetype("spell");
            _onCast = spell.Event<Unit, float>("OnCast");
            spell.Event<Unit>("OnObtain");
        }

        private static ModuleSourceSet Mod(string name, string src, params string[] deps)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest
                {
                    Name = name, ApiVersion = 1,
                    Dependencies = deps, Sources = new[] { name + ".sal" },
                },
            };
            set.Files.Add((name + "/" + name + ".sal", src));
            return set;
        }

        private CompilationResult Compile(params ModuleSourceSet[] mods)
            => ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet>(mods));

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

        private List<string> Cast(ScriptEngine engine)
        {
            _log.Clear();
            _onCast.Raise(engine, "fireball", new Unit(), 1f);
            return new List<string>(_log);
        }

        private List<string> Ping(ScriptEngine engine, Unit u = null)
        {
            _log.Clear();
            _onPing.Raise(engine, u ?? new Unit());
            return new List<string>(_log);
        }

        private const string GameSpell = @"
            spell fireball { event OnCast(Unit c, float p) { Api.Note(""core""); } }";

        // ===================================================================
        // Порядок слоёв
        // ===================================================================

        [Test]
        public void BeforeAfter_WrapTheCore_InLoadOrder()
        {
            var engine = Load(Compile(
                Mod("game", GameSpell),
                Mod("modA", @"spell fireball {
                    before event OnCast(Unit c, float p) { Api.Note(""A.before""); }
                    after event OnCast(Unit c, float p) { Api.Note(""A.after""); }
                }", "game"),
                Mod("modB", @"spell fireball {
                    before event OnCast(Unit c, float p) { Api.Note(""B.before""); }
                    after event OnCast(Unit c, float p) { Api.Note(""B.after""); }
                }", "modA")));

            Assert.AreEqual(new[] { "A.before", "B.before", "core", "A.after", "B.after" }, Cast(engine),
                "слои в порядке загрузки: before до ядра, after после, ядро ровно один раз");
        }

        [Test]
        public void PlainOverride_ReplacesOnlyCore_LayersSurvive()
        {
            var engine = Load(Compile(
                Mod("game", GameSpell),
                Mod("modA", @"spell fireball { after event OnCast(Unit c, float p) { Api.Note(""A.after""); } }", "game"),
                Mod("modB", @"spell fireball { event OnCast(Unit c, float p) { Api.Note(""B.core""); } }", "modA")));

            Assert.AreEqual(new[] { "B.core", "A.after" }, Cast(engine),
                "обычное переопределение меняет ядро, слой другого мода остаётся");
        }

        [Test]
        public void Pass_KillsOnlyCore_LayersSurvive()
        {
            var engine = Load(Compile(
                Mod("game", GameSpell),
                Mod("modA", @"spell fireball { after event OnCast(Unit c, float p) { Api.Note(""A.after""); } }", "game"),
                Mod("modB", @"spell fireball { event OnCast(Unit c, float p) { pass; } }", "modA")));

            Assert.AreEqual(new[] { "A.after" }, Cast(engine));
        }

        [Test]
        public void Replace_WipesEverythingEarlier_LaterLayersStay()
        {
            var engine = Load(Compile(
                Mod("game", GameSpell),
                Mod("modA", @"spell fireball {
                    before event OnCast(Unit c, float p) { Api.Note(""A.before""); }
                    after event OnCast(Unit c, float p) { Api.Note(""A.after""); }
                }", "game"),
                Mod("modB", @"spell fireball { replace event OnCast(Unit c, float p) { Api.Note(""B.replace""); } }", "modA"),
                Mod("modC", @"spell fireball { after event OnCast(Unit c, float p) { Api.Note(""C.after""); } }", "modB")));

            Assert.AreEqual(new[] { "B.replace", "C.after" }, Cast(engine),
                "replace стёр ядро и слои раньше себя; слой, загруженный позже, лёг поверх");
        }

        [Test]
        public void ReplacePrototype_SilencesMemberCompletely()
        {
            var engine = Load(Compile(
                Mod("game", GameSpell),
                Mod("modA", @"spell fireball { after event OnCast(Unit c, float p) { Api.Note(""A.after""); } }", "game"),
                Mod("modB", @"spell fireball { replace event OnCast(Unit c, float p); }", "modA")));

            Assert.AreEqual(new string[0], Cast(engine));
        }

        [Test]
        public void ReplaceInsideBlock_AppliesBeforeTheBlocksOwnLayers()
        {
            var engine = Load(Compile(
                Mod("game", GameSpell),
                Mod("modA", @"spell fireball {
                    after event OnCast(Unit c, float p) { Api.Note(""A.after""); }
                    replace event OnCast(Unit c, float p) { Api.Note(""A.replace""); }
                }", "game")));

            Assert.AreEqual(new[] { "A.replace", "A.after" }, Cast(engine),
                "блок атомарен: replace стирает то, что было ДО блока, слои блока — поверх");
        }

        [Test]
        public void OwnerModuleReplace_IsAStaticBoundary()
        {
            // replace в модуле-владельце выключить отдельно нельзя — всё до него мертво
            var r = Compile(Mod("game", @"
                spell fireball { event OnCast(Unit c, float p) { Api.Note(""v1""); } }
                spell fireball { after event OnCast(Unit c, float p) { Api.Note(""v1.after""); } }
                spell fireball { replace event OnCast(Unit c, float p) { Api.Note(""v2""); } }"));
            var engine = Load(r);
            Assert.AreEqual(new[] { "v2" }, Cast(engine));
            foreach (var ch in r.Program.Functions)
                StringAssert.DoesNotContain("[цепочка]", ch.Name,
                    "после границы остался один replace — функция-цепочка не нужна");
        }

        [Test]
        public void LayerWithoutCore_RunsAlone_AndCountsAsImplementation()
        {
            // у вида spell события обязательны (E0199) — слой считается реализацией
            var engine = Load(Compile(
                Mod("modA", @"spell fireball { after event OnCast(Unit c, float p) { Api.Note(""A.after""); } }")));
            Assert.AreEqual(new[] { "A.after" }, Cast(engine));
        }

        // ===================================================================
        // base()
        // ===================================================================

        [Test]
        public void Base_CallsPreviousVersion_AroundIt()
        {
            var engine = Load(Compile(
                Mod("game", GameSpell),
                Mod("modA", @"spell fireball {
                    event OnCast(Unit c, float p) { Api.Note(""pre""); base(c, p); Api.Note(""post""); }
                }", "game")));

            Assert.AreEqual(new[] { "pre", "core", "post" }, Cast(engine));
        }

        [Test]
        public void Base_ThroughSeveralVersions_WithResultAndChangedArgs()
        {
            var engine = Load(Compile(
                Mod("game", @"
                    class Balance { func Dmg(int x) -> int { return x; } }
                    trigger T { event OnPing(Unit u) { Api.Note($""{Balance.Dmg(3)}""); } }"),
                Mod("modA", @"class Balance { func Dmg(int x) -> int { return base(x * 2) + 1; } }", "game"),
                Mod("modB", @"class Balance { func Dmg(int x) -> int { return base(x) + 100; } }", "modA")));

            Assert.AreEqual(new[] { "107" }, Ping(engine), "(3*2)+1+100: каждая версия зовёт предыдущую");
        }

        [Test]
        public void Base_InsideOneModule_IsADirectCall_NoSyntheticCode()
        {
            var r = Compile(Mod("game", @"
                trigger T { event OnPing(Unit u) { Api.Note(""v1""); } }
                trigger T { event OnPing(Unit u) { base(u); Api.Note(""v2""); } }"));
            var engine = Load(r);
            Assert.AreEqual(new[] { "v1", "v2" }, Ping(engine));
            foreach (var ch in r.Program.Functions)
                StringAssert.DoesNotContain("[", ch.Name, "один модуль, без слоёв — ни цепочки, ни селектора");
        }

        [Test]
        public void Base_SkipsVersionOfDisabledModule()
        {
            var engine = Load(Compile(
                Mod("game", GameSpell),
                Mod("modA", @"spell fireball { event OnCast(Unit c, float p) { Api.Note(""A""); } }", "game"),
                Mod("modB", @"spell fireball { event OnCast(Unit c, float p) { Api.Note(""B""); base(c, p); } }", "modA"),
                Mod("ctl", CtlSrc)));

            Assert.AreEqual(new[] { "B", "A" }, Cast(engine));
            _onCmd.Raise(engine, "-a");
            Assert.AreEqual(new[] { "B", "core" }, Cast(engine), "base() прошёл мимо выключенного модуля");
        }

        // ===================================================================
        // Функции, action, listener
        // ===================================================================

        [Test]
        public void FunctionLayers_KeepTheCoreResult()
        {
            var engine = Load(Compile(Mod("game", @"
                class Util { func V(int x) -> int { return x + 1; } }
                class Util {
                    before func V(int x) { Api.Note(""b""); }
                    after func V(int x) { Api.Note(""a""); }
                }
                trigger T { event OnPing(Unit u) { Api.Note($""{Util.V(1)}""); } }")));

            Assert.AreEqual(new[] { "b", "a", "2" }, Ping(engine),
                "слой без результата допустим; вызывающий получает результат ядра");
        }

        [Test]
        public void Spawn_OfChainedFunction_RunsTheWholeChain()
        {
            var engine = Load(Compile(Mod("game", @"
                class U { func F() { Api.Note(""core""); } }
                class U { before func F() { Api.Note(""b""); } }
                trigger T { event OnPing(Unit u) { spawn U.F(); } }")));

            Ping(engine);
            engine.Tick(0.016f);
            Assert.AreEqual(new[] { "b", "core" }, _log);
        }

        [Test]
        public void TriggerAction_Layers()
        {
            var engine = Load(Compile(Mod("game", @"
                trigger T {
                    event OnPing(Unit u) { Engine.ActivateTrigger(T); }
                    action Do() { Api.Note(""do""); }
                }
                trigger T {
                    before action Do() { Api.Note(""b""); }
                    after action Do() { Api.Note(""a""); }
                }")));

            Ping(engine);
            engine.Tick(0.016f);
            Assert.AreEqual(new[] { "b", "do", "a" }, _log);
        }

        [Test]
        public void Listener_EventAndSubscribeLayers_SeeSelfAndFields()
        {
            var engine = Load(Compile(Mod("game", @"
                listener W {
                    int n = 1;
                    event OnSubscribe() { Api.Note(""sub""); }
                    event OnHit(Unit u, float d) { n = n + 1; Api.Note($""core{n}""); }
                }
                listener W {
                    after event OnSubscribe() { Api.Note($""sub+{self.name}""); }
                    before event OnHit(Unit u, float d) { Api.Note($""b{n}""); }
                }
                trigger S { event OnPing(Unit u) { Engine.Attach(W, u); } }")));

            var a = new Unit { Name = "A" };
            engine.Entities.Register(a);
            Ping(engine, a);
            engine.Tick(0.016f);
            Assert.AreEqual(new[] { "sub", "sub+A" }, _log);

            _log.Clear();
            _onHit.Raise(engine, a, 1f);
            Assert.AreEqual(new[] { "b1", "core2" }, _log, "слой видит поля ТОЙ ЖЕ подписки");
        }

        // ===================================================================
        // Файберы и сейвы
        // ===================================================================

        private const string WaitInBefore = @"
            trigger T { event OnPing(Unit u) { Api.Note(""core""); } }
            trigger T { before event OnPing(Unit u) { Api.Note(""b""); wait 1.0; } }";

        [Test]
        public void WaitInBeforeLayer_DelaysTheCore()
        {
            var engine = Load(Compile(Mod("game", WaitInBefore)));
            Assert.AreEqual(new[] { "b" }, Ping(engine));
            engine.Tick(0.5f);
            Assert.AreEqual(new[] { "b" }, _log, "цепочка — один файбер: ядро ждёт слой");
            engine.Tick(0.6f);
            Assert.AreEqual(new[] { "b", "core" }, _log);
        }

        [Test]
        public void SaveLoad_FiberPausedInsideLayer_Resumes()
        {
            var r = Compile(Mod("game", WaitInBefore));
            var e1 = Load(r);
            var res = new Resolver();
            Assert.AreEqual(new[] { "b" }, Ping(e1, new Unit { Id = 1 }));
            byte[] save = e1.SaveState(res);

            _log.Clear();
            var e2 = new ScriptEngine(_host.Registry);
            e2.OnError += m => Assert.Fail(m);
            e2.LoadProgram(r.Program);
            e2.LoadState(save, res);
            e2.Tick(1.1f);
            Assert.AreEqual(new[] { "core" }, _log, "кадры цепочки и слоя восстановлены как обычные");
        }

        // ===================================================================
        // Гейт модулей: выключенного модуля как будто нет
        // ===================================================================

        [Test]
        public void DisabledModule_ItsLayerIsSkipped()
        {
            var engine = Load(Compile(
                Mod("game", GameSpell),
                Mod("modA", @"spell fireball { after event OnCast(Unit c, float p) { Api.Note(""A.after""); } }", "game"),
                Mod("ctl", CtlSrc)));

            Assert.AreEqual(new[] { "core", "A.after" }, Cast(engine));
            _onCmd.Raise(engine, "-a");
            Assert.AreEqual(new[] { "core" }, Cast(engine), "слой выключенного мода не тянет за собой базу");
            _onCmd.Raise(engine, "+a");
            Assert.AreEqual(new[] { "core", "A.after" }, Cast(engine));
        }

        [Test]
        public void DisabledModule_ItsReplaceNoLongerWipes()
        {
            var engine = Load(Compile(
                Mod("game", GameSpell),
                Mod("modA", @"spell fireball { after event OnCast(Unit c, float p) { Api.Note(""A.after""); } }", "game"),
                Mod("modB", @"spell fireball { replace event OnCast(Unit c, float p) { Api.Note(""B.replace""); } }", "modA"),
                Mod("ctl", CtlSrc)));

            Assert.AreEqual(new[] { "B.replace" }, Cast(engine));
            _onCmd.Raise(engine, "-b");
            Assert.AreEqual(new[] { "core", "A.after" }, Cast(engine), "стёртое возвращается");
            _onCmd.Raise(engine, "-a");
            Assert.AreEqual(new[] { "core" }, Cast(engine));
        }

        [Test]
        public void DisabledModule_ItsPlainOverrideFallsBackToBase()
        {
            var engine = Load(Compile(
                Mod("game", GameSpell + @"
                    class Util { func V() -> int { return 1; } }
                    trigger P { event OnPing(Unit u) { Api.Note($""{Util.V()}""); } }"),
                Mod("modA", @"
                    spell fireball { event OnCast(Unit c, float p) { Api.Note(""A""); } }
                    class Util { func V() -> int { return 2; } }", "game"),
                Mod("ctl", CtlSrc)));

            Assert.AreEqual(new[] { "A" }, Cast(engine));
            Assert.AreEqual(new[] { "2" }, Ping(engine));
            _onCmd.Raise(engine, "-a");
            Assert.AreEqual(new[] { "core" }, Cast(engine), "событие: вернулась версия базы");
            Assert.AreEqual(new[] { "1" }, Ping(engine), "функция: вернулась версия базы");
        }

        [Test]
        public void DisabledOwnerModule_SilencesTheEntity()
        {
            var engine = Load(Compile(
                Mod("game", GameSpell),
                Mod("modA", @"spell fireball { after event OnCast(Unit c, float p) { Api.Note(""A.after""); } }", "game"),
                Mod("ctl", CtlSrc)));

            _onCmd.Raise(engine, "-game");
            Assert.AreEqual(new string[0], Cast(engine),
                "сущностью владеет модуль первого блока — как у триггеров и listener");
        }

        // ===================================================================
        // Без слоёв — байткод прежний
        // ===================================================================

        [Test]
        public void NoLayers_SameModuleOverrides_ProduceNoSyntheticFunctions()
        {
            var r = Compile(Mod("game", @"
                class U { func V() -> int { return 1; } }
                class U { func V() -> int { return 2; } }
                trigger T { event OnPing(Unit u) { Api.Note($""a{U.V()}""); } }
                trigger T { event OnPing(Unit u) { Api.Note($""b{U.V()}""); } }
                spell fireball { event OnCast(Unit c, float p) { Api.Note(""x""); } }
                spell fireball { event OnCast(Unit c, float p) { Api.Note(""y""); } }"));
            var engine = Load(r);
            Assert.AreEqual(new[] { "b2" }, Ping(engine));
            Assert.AreEqual(new[] { "y" }, Cast(engine));
            foreach (var ch in r.Program.Functions)
                StringAssert.DoesNotContain("[", ch.Name);
        }

        [Test]
        public void StackTrace_NamesTheLayer()
        {
            var r = Compile(Mod("game", @"
                trigger T { event OnPing(Unit u) { Api.Note(""core""); } }
                trigger T { after event OnPing(Unit u) { int[] a = new int[1]; int x = a[5]; } }"));
            Assert.IsTrue(r.Success, Dump(r));
            var engine = new ScriptEngine(_host.Registry);
            var errors = new List<string>();
            engine.OnError += m => errors.Add(m);
            engine.LoadProgram(r.Program);
            _onPing.Raise(engine, new Unit());
            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains("T.OnPing [after]", errors[0]);
            StringAssert.Contains("T.OnPing [цепочка]", errors[0]);
        }

        // ===================================================================
        // Контекст проверки и адресат ошибок (найдено независимой проверкой)
        // ===================================================================

        [Test]
        public void ModLayer_SeesTheModsOwnClasses()
        {
            // тела мерж-сущности проверяются на первом блоке; без собственного
            // контекста слой мода разрешал бы имена по видимости базы и не видел ModUtil
            var engine = Load(Compile(
                Mod("game", GameSpell),
                Mod("modA", @"
                    class ModUtil { func Tag() -> string { return ""A""; } }
                    spell fireball { after event OnCast(Unit c, float p) { Api.Note(ModUtil.Tag()); } }", "game")));

            Assert.AreEqual(new[] { "core", "A" }, Cast(engine));
        }

        [Test]
        public void E0242_IgnoresOwnerCoresTheChainCannotReach()
        {
            // в своём модуле сигнатуру поздней версии менять можно было и раньше;
            // чужой слой не должен делать это ошибкой (ранняя версия недосягаема)
            var r = Compile(
                Mod("game", @"
                    class Balance { func Get() -> int { return 1; } }
                    class Balance { func Get() -> float { return 1.5; } }"),
                Mod("modA", @"class Balance { after func Get() { Api.Note(""got""); } }", "game"));
            Assert.IsTrue(r.Success, Dump(r));
        }

        [Test]
        public void E0242_BlamesTheLaterVersion_EvenWhenTheBaseHasOnlyALayer()
        {
            var r = Compile(
                Mod("game", @"class U { before func V(int x) { } }"),
                Mod("modA", @"class U { func V(float x) { } }", "game"));
            Assert.IsTrue(Has(r, "E0242"), Dump(r));
            foreach (var d in r.Diagnostics)
                if (d.Code == "E0242")
                    StringAssert.StartsWith("modA/", d.File);
        }

        [Test]
        public void E0242_ReportedOnce_WhenChainAndBaseSeeTheSameMismatch()
        {
            var r = Compile(
                Mod("game", @"class U { func V(int x) -> int { return x; } }"),
                Mod("modA", @"class U { func V(float x) -> int { return base(x); } }", "game"));
            int n = 0;
            foreach (var d in r.Diagnostics) if (d.Code == "E0242") n++;
            Assert.AreEqual(1, n, Dump(r));
        }

        // ===================================================================
        // Контекстные слова
        // ===================================================================

        [Test]
        public void ContextualWords_RemainOrdinaryIdentifiers()
        {
            var engine = Load(Compile(Mod("game", @"
                class Words {
                    int before = 1;
                    int after = 2;
                    func replace() -> int { return before + after; }
                    func base() -> int { return 10; }
                    func Use() -> int { int after = 3; return replace() + base() + after; }
                }
                trigger T { event OnPing(Unit u) { Api.Note($""{Words.Use()}""); } }")));

            Assert.AreEqual(new[] { "16" }, Ping(engine), "своя функция base() важнее слова base");
        }

        // ===================================================================
        // Диагностики
        // ===================================================================

        [Test]
        public void E0241_ModifierBeforeField()
        {
            var r = Compile(Mod("game", @"class U { after int x = 1; }"));
            Assert.IsTrue(Has(r, "E0241"), Dump(r));
        }

        [Test]
        public void E0242_LayerSignatureMismatch()
        {
            var r = Compile(Mod("game", @"
                class U { func V(int x) -> int { return x; } }
                class U { after func V(float x) { } }"));
            Assert.IsTrue(Has(r, "E0242"), Dump(r));
        }

        [Test]
        public void E0242_CrossModuleOverrideWithOtherSignature()
        {
            // версия мода вызывается гейтом теми же аргументами, что и база
            var r = Compile(
                Mod("game", @"class U { func V(int x) -> int { return x; } }"),
                Mod("modA", @"class U { func V(int x, int y) -> int { return x + y; } }", "game"));
            Assert.IsTrue(Has(r, "E0242"), Dump(r));
            foreach (var d in r.Diagnostics)
                if (d.Code == "E0242")
                    StringAssert.StartsWith("modA/", d.File, "виноват разошедшийся мод, а не база (карантин по файлу)");
        }

        [Test]
        public void E0242_BaseIntoOtherSignature()
        {
            var r = Compile(Mod("game", @"
                class U { func V(int x) -> int { return x; } }
                class U { func V(float x) -> int { return base(x); } }"));
            Assert.IsTrue(Has(r, "E0242"), Dump(r));
        }

        [Test]
        public void E0243_BaseInLayer()
        {
            var r = Compile(Mod("game", GameSpell + @"
                spell fireball { before event OnCast(Unit c, float p) { base(c, p); } }"));
            Assert.IsTrue(Has(r, "E0243"), Dump(r));
        }

        [Test]
        public void E0244_BaseInReplace()
        {
            var r = Compile(Mod("game", GameSpell + @"
                spell fireball { replace event OnCast(Unit c, float p) { base(c, p); } }"));
            Assert.IsTrue(Has(r, "E0244"), Dump(r));
        }

        [Test]
        public void E0245_BaseWithoutEarlierVersion()
        {
            var r = Compile(Mod("game", @"
                spell fireball { event OnCast(Unit c, float p) { base(c, p); } }"));
            Assert.IsTrue(Has(r, "E0245"), Dump(r));
        }

        [Test]
        public void E0245_BaseInFieldInitializer()
        {
            var r = Compile(Mod("game", @"class U { int x = base(); }"));
            Assert.IsTrue(Has(r, "E0245"), Dump(r));
        }

        [Test]
        public void E0246_OnlyLayersOnFunctionWithResult()
        {
            var r = Compile(Mod("game", @"class U { before func V() -> int { return 1; } }"));
            Assert.IsTrue(Has(r, "E0246"), Dump(r));
        }

        [Test]
        public void E0113_SameLayerTwiceInOneBlock()
        {
            var r = Compile(Mod("game", @"
                trigger T {
                    before event OnPing(Unit u) { }
                    before event OnPing(Unit u) { }
                }"));
            Assert.IsTrue(Has(r, "E0113"), Dump(r));
        }

        [Test]
        public void CoreAndBothLayers_InOneBlock_AreFine_ButTwoCoresAreNot()
        {
            var ok = Compile(Mod("game", @"
                trigger T {
                    before event OnPing(Unit u) { Api.Note(""b""); }
                    event OnPing(Unit u) { Api.Note(""c""); }
                    after event OnPing(Unit u) { Api.Note(""a""); }
                }"));
            Assert.AreEqual(new[] { "b", "c", "a" }, Ping(Load(ok)));

            var dup = Compile(Mod("game", @"
                trigger T {
                    event OnPing(Unit u) { }
                    replace event OnPing(Unit u) { }
                }"));
            Assert.IsTrue(Has(dup, "E0113"), "ядро и replace в одном блоке — одно место\n" + Dump(dup));

            var actions = Compile(Mod("game", @"
                trigger T {
                    event OnPing(Unit u) { }
                    action Do() { }
                    action Do() { }
                }"));
            Assert.IsTrue(Has(actions, "E0118"), Dump(actions));
        }
    }
}
