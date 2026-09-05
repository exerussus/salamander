using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Дефолты констант вида (ConstOr): вид перестаёт быть только шаблоном
    /// событий и становится СХЕМОЙ сущности — объявляет поля с типами и
    /// значениями по умолчанию, а блок их переопределяет. Поле с дефолтом есть
    /// у КАЖДОЙ сущности вида: его видит и хост, и сам скрипт, даже если блок
    /// ничего не объявлял.
    ///
    /// Константа вида readonly по определению: значение задаётся объявлением, а
    /// присвоить его нельзя — иначе засеянное дефолтом поле оказалось бы
    /// изменяемым состоянием и уехало бы в сейв вместо рецепта.
    /// </summary>
    public sealed class ArchetypeDefaultTests
    {
        public sealed class Unit { public long Id; public string Name; }

        private enum School { Fire, Frost }

        private sealed class Resolver : ISaveEntityResolver
        {
            public long GetStableId(object entity) => entity is Unit u ? u.Id : 0;
            public object ResolveStableId(long id) => null;
        }

        private HostBuilder _host;
        private ArchetypeBuilder _weapon;
        private ArchEventRef<Unit> _onHit;
        private List<string> _log;
        private readonly List<string> _buf = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _host = new HostBuilder();
            _host.Enum<School>();
            _host.Class<Unit>().Prop("name", u => u.Name);
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));

            _weapon = _host.Archetype("weapon");
            _onHit = _weapon.Event<Unit>("OnHit");
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
        // Дефолт есть у каждой сущности вида
        // ===================================================================

        [Test]
        public void Default_BlockDidNotDeclare_HostReadsDefault()
        {
            _weapon.ConstOr<int>("windup_ticks", 3);

            var engine = Load(Compile(Mod("game", @"
                weapon dagger { event OnHit(Unit u) { Api.Note(""hit""); } }")));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "dagger", "windup_ticks", out var v));
            Assert.AreEqual(3, v.AsInt);
        }

        [Test]
        public void Default_ScriptReadsFieldItNeverDeclared()
        {
            // ради этого засев и существует: контентщик не копипастит
            // windup_ticks = 3 в сорок блоков, но пользуется полем как своим
            _weapon.ConstOr<int>("windup_ticks", 3);

            var engine = Load(Compile(Mod("game", @"
                weapon dagger { event OnHit(Unit u) { Api.Note($""windup {windup_ticks}""); } }")));

            _onHit.Raise(engine, "dagger", new Unit { Name = "H" });
            engine.Tick(0.016f);
            Assert.AreEqual(new[] { "windup 3" }, _log);
        }

        [Test]
        public void Default_BlockDeclares_ItsValueWins()
        {
            _weapon.ConstOr<int>("windup_ticks", 3);

            var engine = Load(Compile(Mod("game", @"
                weapon greatsword {
                    int windup_ticks = 9;
                    event OnHit(Unit u) { Api.Note($""{windup_ticks}""); }
                }")));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "greatsword", "windup_ticks", out var v));
            Assert.AreEqual(9, v.AsInt);

            _onHit.Raise(engine, "greatsword", new Unit());
            engine.Tick(0.016f);
            Assert.AreEqual(new[] { "9" }, _log);
        }

        [Test]
        public void Default_PatchModuleOverrides_LaterWins()
        {
            _weapon.ConstOr<int>("windup_ticks", 3);

            var baseMod = Mod("base", @"
                weapon sword { event OnHit(Unit u) { Api.Note(""hit""); } }");
            var patch = Mod("patch", @"weapon sword { int windup_ticks = 5; }", "base");

            var engine = Load(Compile(baseMod, patch));
            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "windup_ticks", out var v));
            Assert.AreEqual(5, v.AsInt);
        }

        [Test]
        public void Default_EveryLiteralType()
        {
            _weapon.ConstOr<float>("damage", 12.5f)
                   .ConstOr<int>("windup", 3)
                   .ConstOr<bool>("two_handed", true)
                   .ConstOr<double>("precise", 0.125)
                   .ConstOr<string>("tooltip", "Рубит")
                   .ConstOr<School>("school", School.Frost);

            var engine = Load(Compile(Mod("game", @"
                weapon sword { event OnHit(Unit u) { Api.Note(""hit""); } }")));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "damage", out var dmg));
            Assert.AreEqual(12.5f, dmg.ToF(), 1e-6f);
            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "windup", out var w));
            Assert.AreEqual(3, w.AsInt);
            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "two_handed", out var th));
            Assert.IsTrue(th.AsBool);
            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "precise", out var pr));
            Assert.AreEqual(0.125, pr.ToD(), 1e-12);
            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "tooltip", out var tip));
            Assert.AreEqual("Рубит", engine.ResolveString(tip));
            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "school", out var sc));
            Assert.AreEqual((int)School.Frost, sc.EnumValue);
        }

        [Test]
        public void Default_NullString_IsReadableAsNil()
        {
            _weapon.ConstOr<string>("tooltip", null);

            var engine = Load(Compile(Mod("game", @"
                weapon sword { event OnHit(Unit u) { Api.Note(""hit""); } }")));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "tooltip", out var v));
            Assert.IsTrue(v.IsNil);
            Assert.IsNull(engine.ResolveString(v));
        }

        [Test]
        public void Default_StringDefault_SurvivesHotReload()
        {
            // строковый дефолт хранится индексом в пуле литералов, а настоящий id
            // раздаёт StringTable при КАЖДОЙ загрузке программы — стоит это закрепить
            _weapon.ConstOr<string>("tooltip", "Рубит");

            var first = Compile(Mod("game", @"
                weapon sword { event OnHit(Unit u) { Api.Note(""a""); } }"));
            var engine = Load(first);

            var second = Compile(Mod("game", @"
                weapon sword {
                    string other = ""совсем другая строка"";
                    event OnHit(Unit u) { Api.Note(""b""); }
                }"));
            Assert.IsTrue(second.Success, Dump(second));
            engine.LoadProgram(second.Program);
            engine.Tick(0.016f);

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "tooltip", out var v));
            Assert.AreEqual("Рубит", engine.ResolveString(v));
        }

        [Test]
        public void Default_EntityWithoutAnyBlock_IsNotMaterialized()
        {
            // сущности без кода в программе нет вовсе — дефолт брать некуда и незачем,
            // у игры он свой и так
            _weapon.ConstOr<int>("windup_ticks", 3);

            var engine = Load(Compile(Mod("game", @"
                weapon sword { event OnHit(Unit u) { Api.Note(""hit""); } }")));

            Assert.IsFalse(engine.TryGetArchetypeConst("weapon", "mace", "windup_ticks", out _));
        }

        [Test]
        public void Default_ConstWithoutDefault_IsNotSeeded()
        {
            // необязательная константа БЕЗ дефолта не засевается намеренно: иначе
            // необъявленное поле молча читалось бы как 0 — тот самый тихий ноль
            _weapon.Const<int>("windup_ticks");

            var engine = Load(Compile(Mod("game", @"
                weapon sword { event OnHit(Unit u) { Api.Note(""hit""); } }")));

            Assert.IsFalse(engine.TryGetArchetypeConst("weapon", "sword", "windup_ticks", out _));

            var r = Compile(Mod("game2", @"
                weapon axe { event OnHit(Unit u) { Api.Note($""{windup_ticks}""); } }"));
            Assert.IsFalse(r.Success, "обращение к необъявленному полю обязано быть ошибкой");
        }

        // ===================================================================
        // Перечисление и ImplementsConst
        // ===================================================================

        [Test]
        public void Enumerate_IncludesDefaults_ContractFirstThenOwnFields()
        {
            _weapon.ConstOr<float>("damage", 1.0f)
                   .ConstOr<int>("windup", 3);

            var engine = Load(Compile(Mod("game", @"
                weapon sword {
                    int windup = 9;
                    int hits = 0;
                    event OnHit(Unit u) { hits = hits + 1; }
                }")));

            // сперва поля контракта (в порядке объявления хостом), потом свои
            engine.GetArchetypeConsts("weapon", "sword", _buf);
            Assert.AreEqual(new[] { "damage", "windup", "hits" }, _buf);
        }

        [Test]
        public void ImplementsConst_TellsDefaultFromDeclared()
        {
            _weapon.ConstOr<float>("damage", 1.0f)
                   .ConstOr<int>("windup", 3);

            var engine = Load(Compile(Mod("game", @"
                weapon sword {
                    float damage = 12.0;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }")));

            Assert.IsTrue(engine.ImplementsConst("weapon", "sword", "damage"), "объявлено блоком");
            Assert.IsFalse(engine.ImplementsConst("weapon", "sword", "windup"), "взят дефолт вида");
            Assert.IsFalse(engine.ImplementsConst("weapon", "sword", "нет_такого"));
            Assert.IsFalse(engine.ImplementsConst("weapon", "нет_такого", "damage"));
        }

        [Test]
        public void ImplementsConst_PatchBlockCounts()
        {
            _weapon.ConstOr<int>("windup", 3);

            var baseMod = Mod("base", @"
                weapon sword { event OnHit(Unit u) { Api.Note(""hit""); } }");
            var patch = Mod("patch", @"weapon sword { int windup = 5; }", "base");

            var engine = Load(Compile(baseMod, patch));
            Assert.IsTrue(engine.ImplementsConst("weapon", "sword", "windup"));
        }

        // ===================================================================
        // Константа вида readonly по определению
        // ===================================================================

        [Test]
        public void ContractConst_IsReadonly_EvenWithoutTheModifier()
        {
            _weapon.Const<float>("damage", required: true);

            var r = Compile(Mod("game", @"
                weapon sword {
                    float damage = 12.0;
                    event OnHit(Unit u) { damage = 1.0; }
                }"));

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0225"), Dump(r));
            // ошибка обязана назвать источник: модификатора в коде нет
            StringAssert.Contains("константа вида 'weapon'", Dump(r));
        }

        [Test]
        public void SeededDefault_IsReadonlyToo()
        {
            _weapon.ConstOr<int>("windup", 3);

            var r = Compile(Mod("game", @"
                weapon sword { event OnHit(Unit u) { windup = 99; } }"));

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0225"), Dump(r));
        }

        [Test]
        public void ContractConst_ExplicitReadonlyModifier_IsAllowed()
        {
            // модификатор у контрактного поля просто дублирует контракт —
            // писать можно, смысл тот же
            _weapon.Const<float>("damage", required: true);

            var engine = Load(Compile(Mod("game", @"
                weapon sword {
                    readonly float damage = 12.0;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }")));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "damage", out var v));
            Assert.AreEqual(12f, v.ToF(), 1e-6f);
        }

        [Test]
        public void Default_WrongTypeInBlock_IsStillCaught()
        {
            _weapon.ConstOr<int>("windup", 3);

            var r = Compile(Mod("game", @"
                weapon sword {
                    string windup = ""три"";
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0207") || Has(r, "E0224"), Dump(r));
        }

        [Test]
        public void Default_DoesNotTriggerTypoWarning()
        {
            // засеянное поле не объявлено в скриптах — W0101 про него молчит
            _weapon.ConstOr<int>("windup", 3);

            var r = Compile(Mod("game", @"
                weapon sword { event OnHit(Unit u) { Api.Note(""hit""); } }"));

            Assert.IsTrue(r.Success, Dump(r));
            Assert.IsFalse(Has(r, "W0101"), Dump(r));
        }

        [Test]
        public void RequiredWithDefault_IsRegistrationError()
        {
            _weapon.Const<float>("damage", required: true);
            Assert.Throws<System.ArgumentException>(() => _weapon.ConstOr<float>("damage", 1f));

            var other = _host.Archetype("armor");
            other.Event<Unit>("OnEquip");
            // required и дефолт — противоречие даже под разными именами методов
            other.Const<float>("weight", required: true);
            Assert.Throws<System.ArgumentException>(() => other.ConstOr<float>("weight", 1f));
        }

        [Test]
        public void EntityDefaultAsConst_IsRejectedAtRegistration()
        {
            Assert.Throws<System.ArgumentException>(() => _weapon.ConstOr<Unit>("owner", null));
        }

        // ===================================================================
        // Сейв: дефолт — рецепт, а не состояние
        // ===================================================================

        [Test]
        public void Save_DefaultIsRebalanced_NotRestoredFromSave()
        {
            _weapon.ConstOr<int>("windup", 3);
            var prog = Compile(Mod("game", @"
                weapon sword {
                    int hits = 0;
                    event OnHit(Unit u) { hits = hits + 1; }
                }"));
            var engine = Load(prog);
            _onHit.Raise(engine, "sword", new Unit { Id = 1 });
            engine.Tick(0.016f);

            var res = new Resolver();
            byte[] save = engine.SaveState(res);

            // игра выпустила патч: дефолт вида изменился
            var host2 = new HostBuilder();
            host2.Enum<School>();
            host2.Class<Unit>().Prop("name", u => u.Name);
            host2.Api("Api").Act("Note", (string s) => _log.Add(s));
            var weapon2 = host2.Archetype("weapon");
            weapon2.Event<Unit>("OnHit");
            weapon2.ConstOr<int>("windup", 7);

            var prog2 = ScriptCompiler.Compile(host2.Registry, 1, new List<ModuleSourceSet>
            {
                Mod("game", @"
                weapon sword {
                    int hits = 0;
                    event OnHit(Unit u) { hits = hits + 1; }
                }"),
            });
            Assert.IsTrue(prog2.Success, Dump(prog2));

            var loaded = new ScriptEngine(host2.Registry);
            loaded.OnError += m => Assert.Fail(m);
            loaded.LoadProgram(prog2.Program);
            loaded.LoadState(save, res);

            Assert.IsTrue(loaded.TryGetArchetypeConst("weapon", "sword", "windup", out var w));
            Assert.AreEqual(7, w.AsInt, "дефолт обязан взяться из новой версии игры");
            Assert.IsTrue(loaded.TryGetArchetypeConst("weapon", "sword", "hits", out var h));
            Assert.AreEqual(1, h.AsInt);
        }

        // ===================================================================
        // Манифест: дефолт обязан доезжать до инструментов вне игры
        // ===================================================================

        [Test]
        public void Manifest_RoundTrips_Defaults()
        {
            _weapon.Const<float>("damage", required: true, doc: "урон удара")
                   .ConstOr<int>("windup", 3, doc: "замах")
                   .ConstOr<bool>("two_handed", true)
                   .ConstOr<double>("precise", 0.125)
                   .ConstOr<string>("tooltip", "Рубит")
                   .ConstOr<string>("subtitle", null)
                   .ConstOr<School>("school", School.Frost);

            string json = ApiManifest.Export(_host.Registry, 1);
            StringAssert.Contains("\"default\"", json);
            StringAssert.Contains("\"Frost\"", json); // енум именем, а не индексом

            var imported = ApiManifest.Import(json, out _);
            Assert.IsTrue(imported.TryGetArchetypeKind("weapon", out var kind));
            Assert.AreEqual(7, kind.Consts.Count);

            Assert.IsFalse(kind.ConstByName["damage"].HasDefault);
            Assert.IsTrue(kind.ConstByName["damage"].Required);

            Assert.AreEqual(3, kind.ConstByName["windup"].DefaultValue.AsInt);
            Assert.IsTrue(kind.ConstByName["two_handed"].DefaultValue.AsBool);
            Assert.AreEqual(0.125, kind.ConstByName["precise"].DefaultValue.ToD(), 1e-12);
            Assert.AreEqual("Рубит", kind.ConstByName["tooltip"].DefaultStr);

            Assert.IsTrue(kind.ConstByName["subtitle"].HasDefault, "дефолт-null — это тоже дефолт");
            Assert.IsNull(kind.ConstByName["subtitle"].DefaultStr);

            Assert.AreEqual((int)School.Frost, kind.ConstByName["school"].DefaultValue.EnumValue);
        }

        [Test]
        public void Manifest_ImportedRegistry_SeedsDefaultsToo()
        {
            // инструменты вне игры обязаны компилировать блок, который
            // пользуется дефолтным полем, ничего не объявив
            _weapon.ConstOr<int>("windup", 3);
            var imported = ApiManifest.Import(ApiManifest.Export(_host.Registry, 1), out _);

            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "game", ApiVersion = 1, Sources = new[] { "g.sal" } },
            };
            set.Files.Add(("game/g.sal", @"
                weapon sword { event OnHit(Unit u) { Api.Note($""{windup}""); } }"));

            var r = ScriptCompiler.Compile(imported, 1, new List<ModuleSourceSet> { set });
            Assert.IsTrue(r.Success, Dump(r));
        }
    }
}
