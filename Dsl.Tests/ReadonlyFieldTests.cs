using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Модификатор readonly: слот есть (хост читает, скрипт читает), но значение
    /// задаётся ТОЛЬКО в объявлении. Разрешён везде, где есть поля — class,
    /// trigger, listener, блок-архетип: механика одна и та же, исключений в
    /// языке нет.
    ///
    /// Главное следствие — сейв. readonly-поле не сериализуется и всегда
    /// переинициализируется из текущей программы, поэтому балансный патч
    /// доезжает до СТАРЫХ сохранений. Изменяемое поле — состояние, оно
    /// восстанавливается как раньше.
    /// </summary>
    public sealed class ReadonlyFieldTests
    {
        public sealed class Unit { public long Id; public string Name; }

        private sealed class Resolver : ISaveEntityResolver
        {
            public long GetStableId(object entity) => entity is Unit u ? u.Id : 0;
            public object ResolveStableId(long id) => null;
        }

        private HostBuilder _host;
        private EventRef<Unit> _onPing;
        private ArchEventRef<Unit> _onHit;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _host = new HostBuilder();
            _host.Class<Unit>().Prop("name", u => u.Name);
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));
            _onPing = _host.Event<Unit>("OnPing");
            _onHit = _host.Archetype("weapon").Event<Unit>("OnHit");
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

        // ===================================================================
        // Модификатор работает во всех декларациях
        // ===================================================================

        [Test]
        public void Readonly_InClass_ForbidsAssignment()
        {
            var r = Compile(Mod("game", @"
                class Balance {
                    readonly int bossHp = 750;
                    func Break() { bossHp = 1; }
                }
                trigger T { event OnPing(Unit u) { Balance.Break(); } }"));

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0225"), Dump(r));
            StringAssert.Contains("bossHp", Dump(r));
        }

        [Test]
        public void Readonly_InClass_ForbidsAssignmentFromOutside()
        {
            var r = Compile(Mod("game", @"
                class Balance { readonly int bossHp = 750; }
                trigger T { event OnPing(Unit u) { Balance.bossHp = 1; } }"));

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0226"), Dump(r));
        }

        [Test]
        public void Readonly_InTrigger_ForbidsAssignment()
        {
            var r = Compile(Mod("game", @"
                trigger T {
                    readonly float delay = 10.0;
                    event OnPing(Unit u) { delay = 1.0; }
                }"));

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0225"), Dump(r));
        }

        [Test]
        public void Readonly_InListener_ForbidsAssignment()
        {
            var r = Compile(Mod("game", @"
                listener L {
                    readonly float rate = 0.5;
                    event OnPing(Unit u) { rate = 1.0; }
                }"));

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0225"), Dump(r));
        }

        [Test]
        public void Readonly_InArchetypeBlock_ForbidsAssignment()
        {
            var r = Compile(Mod("game", @"
                weapon sword {
                    readonly float damage = 12.0;
                    event OnHit(Unit u) { damage = 1.0; }
                }"));

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0225"), Dump(r));
            StringAssert.Contains("readonly-полю", Dump(r), "вид ничего не объявлял — источник именно модификатор");
        }

        [Test]
        public void Readonly_CompoundAssignment_IsAlsoForbidden()
        {
            // 'x += 1' десахарится в 'x = x + 1' — проверка обязана срабатывать и там
            var r = Compile(Mod("game", @"
                trigger T {
                    readonly int n = 1;
                    event OnPing(Unit u) { n += 1; }
                }"));

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0225"), Dump(r));
        }

        [Test]
        public void Readonly_IsReadableEverywhere()
        {
            var engine = Load(Compile(Mod("game", @"
                class Balance { readonly int bossHp = 750; }
                trigger T {
                    readonly float mult = 2.0;
                    event OnPing(Unit u) { Api.Note($""{Balance.bossHp} {mult}""); }
                }")));

            _onPing.Raise(engine, new Unit { Name = "H" });
            engine.Tick(0.016f);
            Assert.AreEqual(new[] { "750 2" }, _log);
        }

        [Test]
        public void Readonly_InArchetype_IsReadableByHost()
        {
            var engine = Load(Compile(Mod("game", @"
                weapon sword {
                    readonly float damage = 12.5;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }")));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "damage", out var v));
            Assert.AreEqual(12.5f, v.ToF(), 1e-6f);
        }

        // ===================================================================
        // Контекстность слова: 'readonly' остаётся законным идентификатором
        // ===================================================================

        [Test]
        public void Readonly_IsContextual_StillUsableAsFieldName()
        {
            // если бы это было настоящее ключевое слово, скрипты с таким именем
            // перестали бы компилироваться на ровном месте
            var engine = Load(Compile(Mod("game", @"
                class C {
                    int readonly = 5;
                    func Get() -> int { return readonly; }
                }
                trigger T { event OnPing(Unit u) { Api.Note($""{C.Get()}""); } }")));

            _onPing.Raise(engine, new Unit { Name = "H" });
            engine.Tick(0.016f);
            Assert.AreEqual(new[] { "5" }, _log);
        }

        [Test]
        public void Readonly_IsContextual_StillUsableAsLocalName()
        {
            var engine = Load(Compile(Mod("game", @"
                trigger T {
                    event OnPing(Unit u) {
                        int readonly = 7;
                        Api.Note($""{readonly}"");
                    }
                }")));

            _onPing.Raise(engine, new Unit { Name = "H" });
            engine.Tick(0.016f);
            Assert.AreEqual(new[] { "7" }, _log);
        }

        // ===================================================================
        // Мерж: readonly «липкое»
        // ===================================================================

        [Test]
        public void Readonly_IsSticky_PatchCanTightenButNotLoosen()
        {
            // патч объявил поле readonly — присваивание в БАЗЕ становится ошибкой.
            // Иначе патч, забывший модификатор, молча снимал бы защиту
            var baseMod = Mod("base", @"
                trigger T {
                    int n = 1;
                    event OnPing(Unit u) { n = 5; }
                }");
            var patch = Mod("patch", @"trigger T { readonly int n = 2; }", "base");

            var r = Compile(baseMod, patch);
            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0225"), Dump(r));
        }

        [Test]
        public void Readonly_PatchOverridesValue_ItIsDeclarationNotAssignment()
        {
            // переопределение в другом блоке — это ОБЪЯВЛЕНИЕ, оно законно:
            // иначе моды не могли бы править баланс
            var baseMod = Mod("base", @"
                weapon sword {
                    readonly float damage = 12.0;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }");
            var patch = Mod("patch", @"weapon sword { readonly float damage = 20.0; }", "base");

            var engine = Load(Compile(baseMod, patch));
            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "damage", out var v));
            Assert.AreEqual(20f, v.ToF(), 1e-6f);
        }

        // ===================================================================
        // Сейв: readonly не сериализуется — патч доезжает до старых сохранений
        // ===================================================================

        private const string BalanceV1 = @"
            weapon sword {
                readonly float damage = 12.0;
                int hits = 0;
                event OnHit(Unit u) { hits = hits + 1; }
            }";

        private const string BalanceV2 = @"
            weapon sword {
                readonly float damage = 20.0;
                int hits = 0;
                event OnHit(Unit u) { hits = hits + 1; }
            }";

        [Test]
        public void Save_ReadonlyIsRebalanced_MutableStateSurvives()
        {
            var v1 = Compile(Mod("game", BalanceV1));
            var engine = Load(v1);

            var hero = new Unit { Id = 1, Name = "H" };
            _onHit.Raise(engine, "sword", hero);
            _onHit.Raise(engine, "sword", hero);
            engine.Tick(0.016f);

            var res = new Resolver();
            byte[] save = engine.SaveState(res);

            // дизайнер поправил баланс и выпустил патч
            var v2 = Compile(Mod("game", BalanceV2));
            Assert.IsTrue(v2.Success, Dump(v2));
            var loaded = new ScriptEngine(_host.Registry);
            loaded.OnError += m => Assert.Fail(m);
            loaded.LoadProgram(v2.Program);
            loaded.LoadState(save, res);

            Assert.IsTrue(loaded.TryGetArchetypeConst("weapon", "sword", "damage", out var dmg));
            Assert.AreEqual(20f, dmg.ToF(), 1e-6f, "readonly обязан взяться из НОВОЙ программы");

            Assert.IsTrue(loaded.TryGetArchetypeConst("weapon", "sword", "hits", out var hits));
            Assert.AreEqual(2, hits.AsInt, "изменяемое поле — состояние, оно из сейва");
        }

        [Test]
        public void Save_ReadonlyChangedToMutable_DoesNotReportMissingField()
        {
            // поле стало readonly в новой версии: значение из сейва отбрасывается
            // молча — это обновление баланса, а не потеря данных
            var v1 = Compile(Mod("game", @"
                weapon sword {
                    float damage = 12.0;
                    event OnHit(Unit u) { damage = 99.0; }
                }"));
            var engine = Load(v1);
            _onHit.Raise(engine, "sword", new Unit { Id = 1 });
            engine.Tick(0.016f);

            var res = new Resolver();
            byte[] save = engine.SaveState(res);

            var v2 = Compile(Mod("game", @"
                weapon sword {
                    readonly float damage = 20.0;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));
            Assert.IsTrue(v2.Success, Dump(v2));
            var loaded = new ScriptEngine(_host.Registry);
            loaded.LoadProgram(v2.Program);
            var report = loaded.LoadState(save, res);

            Assert.IsEmpty(report.MissingStatics, report.ToString());
            Assert.IsTrue(loaded.TryGetArchetypeConst("weapon", "sword", "damage", out var v));
            Assert.AreEqual(20f, v.ToF(), 1e-6f);
        }

        [Test]
        public void Save_RoundTrip_SameProgram_KeepsEverything()
        {
            var prog = Compile(Mod("game", BalanceV1));
            var engine = Load(prog);
            _onHit.Raise(engine, "sword", new Unit { Id = 1 });
            engine.Tick(0.016f);

            var res = new Resolver();
            var loaded = new ScriptEngine(_host.Registry);
            loaded.OnError += m => Assert.Fail(m);
            loaded.LoadProgram(prog.Program);
            var report = loaded.LoadState(engine.SaveState(res), res);

            Assert.IsTrue(report.IsClean, report.ToString());
            Assert.IsTrue(loaded.TryGetArchetypeConst("weapon", "sword", "damage", out var d));
            Assert.AreEqual(12f, d.ToF(), 1e-6f);
            Assert.IsTrue(loaded.TryGetArchetypeConst("weapon", "sword", "hits", out var h));
            Assert.AreEqual(1, h.AsInt);
        }
    }
}
