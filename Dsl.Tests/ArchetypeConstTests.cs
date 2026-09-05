using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Константы контента у блоков-архетипов: «рецепт» сущности, объявленный
    /// модером в DSL, и два способа его забрать хостом.
    ///
    /// Две стороны одной задачи, и они дополняют друг друга:
    ///  - ЧТЕНИЕ (TryGetArchetypeConst / GetArchetypeConsts) — обязательно.
    ///    Для оружия и NPC имена полей известны хосту, для атрибутов — нет
    ///    принципиально: атрибут сам решает, в какие слои вкладывается, и может
    ///    вложиться в тот, о котором кор никогда не слышал. Значит нужно и
    ///    адресное чтение, и перечисление.
    ///  - КОНТРАКТ (ArchetypeBuilder.Const&lt;T&gt;) — опционален и закрывает
    ///    только ЗАКРЫТЫЕ наборы полей: забытая или опечатанная константа
    ///    становится ошибкой сборки, а не пустым мечом на плейтесте. Ровно как
    ///    KnownIds ловит опечатку в id.
    /// </summary>
    public sealed class ArchetypeConstTests
    {
        public sealed class Unit { public string Name; }

        private enum School { Fire, Frost }

        private HostBuilder _host;
        private ArchetypeBuilder _weapon;
        private ArchEventRef<Unit> _onHit;
        private ArchEventRef<Unit> _onApply;
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
            _host.Event<Unit>("OnPing"); // обычное событие — для триггера в тесте про ключи статиков

            // закрытый набор полей — контракт игры (объявляется отдельными тестами)
            _weapon = _host.Archetype("weapon", summary: "Механика оружия.");
            _onHit = _weapon.Event<Unit>("OnHit");

            // открытый набор — атрибут сам решает, во что вкладывается
            var attribute = _host.Archetype("attribute", summary: "Механика атрибута.");
            _onApply = attribute.Event<Unit>("OnApply");
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
        // Просьба 1: прочитать рецепт снаружи
        // ===================================================================

        [Test]
        public void Read_AllPrimitiveTypes_ByKindIdField()
        {
            var engine = Load(Compile(Mod("game", @"
                weapon sword {
                    float damage = 12.5;
                    int windup_ticks = 3;
                    bool two_handed = true;
                    string tooltip = ""Рубит"";
                    School school = School.Frost;

                    event OnHit(Unit u) { Api.Note(""hit""); }
                }")));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "damage", out var dmg));
            Assert.AreEqual(12.5f, dmg.ToF(), 1e-6f);

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "windup_ticks", out var windup));
            Assert.AreEqual(3, windup.AsInt);

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "two_handed", out var two));
            Assert.IsTrue(two.AsBool);

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "tooltip", out var tip));
            Assert.AreEqual("Рубит", engine.ResolveString(tip));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "school", out var school));
            Assert.AreEqual(VariantType.Enum, school.Type);
            Assert.AreEqual((int)School.Frost, school.EnumValue);
        }

        [Test]
        public void Read_FieldWithoutInitializer_IsNil()
        {
            var engine = Load(Compile(Mod("game", @"
                weapon sword {
                    float damage;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }")));

            // поле объявлено — значит есть в рецепте; значения нет — Nil
            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "damage", out var v));
            Assert.IsTrue(v.IsNil);
        }

        [Test]
        public void Read_UnknownKindIdOrField_ReturnsFalse()
        {
            var engine = Load(Compile(Mod("game", @"
                weapon sword { float damage = 1.0; event OnHit(Unit u) { Api.Note(""x""); } }")));

            Assert.IsFalse(engine.TryGetArchetypeConst("armor", "sword", "damage", out _));    // нет вида
            Assert.IsFalse(engine.TryGetArchetypeConst("weapon", "axe", "damage", out _));     // нет id
            Assert.IsFalse(engine.TryGetArchetypeConst("weapon", "sword", "dmage", out _));    // опечатка в поле
            Assert.IsFalse(engine.TryGetArchetypeConst(null, "sword", "damage", out _));
            Assert.IsFalse(engine.TryGetArchetypeConst("weapon", null, "damage", out _));
            Assert.IsFalse(engine.TryGetArchetypeConst("weapon", "sword", null, out _));
        }

        [Test]
        public void Read_BeforeLoadProgram_ReturnsFalse()
        {
            var engine = new ScriptEngine(_host.Registry);
            Assert.IsFalse(engine.TryGetArchetypeConst("weapon", "sword", "damage", out _));

            engine.GetArchetypeConsts("weapon", "sword", _buf);
            Assert.IsEmpty(_buf);
        }

        [Test]
        public void Read_PatchBlock_LaterInitializerWins()
        {
            // мерж-семантика архетипов: поздний блок патчит значение поля, и
            // хост обязан видеть ИТОГ — ровно то же, что видят скрипты
            var baseMod = Mod("base", @"
                weapon sword {
                    float damage = 12.0;
                    int windup_ticks = 3;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }");
            var patch = Mod("patch", @"
                weapon sword { float damage = 20.0; }", "base");

            var engine = Load(Compile(baseMod, patch));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "damage", out var dmg));
            Assert.AreEqual(20f, dmg.ToF(), 1e-6f);

            // непатченное поле осталось базовым
            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "windup_ticks", out var w));
            Assert.AreEqual(3, w.AsInt);
        }

        [Test]
        public void Read_ReflectsRuntimeWrites_ItIsTheLiveStatic()
        {
            // ИЗМЕНЯЕМОЕ поле блока — обычный статик: читается живое значение,
            // хост и скрипт видят одно и то же. Чтобы поле нельзя было тронуть
            // из обработчика, его помечают readonly (см. ReadonlyFieldTests)
            // или объявляют константой вида.
            var engine = Load(Compile(Mod("game", @"
                weapon sword {
                    int hits = 0;
                    event OnHit(Unit u) { hits = hits + 1; }
                }")));

            var hero = new Unit { Name = "H" };
            _onHit.Raise(engine, "sword", hero);
            _onHit.Raise(engine, "sword", hero);
            engine.Tick(0.016f);

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "hits", out var hits));
            Assert.AreEqual(2, hits.AsInt);
        }

        [Test]
        public void Enumerate_ReturnsFieldsInDeclarationOrder()
        {
            var engine = Load(Compile(Mod("game", @"
                weapon sword {
                    float damage = 12.0;
                    int windup_ticks = 3;
                    string tooltip = ""x"";
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }")));

            engine.GetArchetypeConsts("weapon", "sword", _buf);
            Assert.AreEqual(new[] { "damage", "windup_ticks", "tooltip" }, _buf);
        }

        [Test]
        public void Enumerate_OpenSet_HostDiscoversFieldsItNeverDeclared()
        {
            // ради этого перечисление и нужно: вид 'attribute' не объявляет НИ
            // ОДНОЙ константы, а атрибут из пака вкладывается в слой diplomacy,
            // о котором кор никогда не слышал
            var engine = Load(Compile(Mod("pack", @"
                attribute stubbornness {
                    float diplomacy = 0.35;
                    float trade = -0.1;
                    event OnApply(Unit u) { Api.Note(""applied""); }
                }")));

            engine.GetArchetypeConsts("attribute", "stubbornness", _buf);
            Assert.AreEqual(new[] { "diplomacy", "trade" }, _buf);

            float sum = 0f;
            foreach (var field in _buf)
            {
                Assert.IsTrue(engine.TryGetArchetypeConst("attribute", "stubbornness", field, out var v));
                sum += v.ToF();
            }
            Assert.AreEqual(0.25f, sum, 1e-5f);
        }

        [Test]
        public void Enumerate_PatchBlockAddsField_ShowsUpOnce()
        {
            var baseMod = Mod("base", @"
                attribute stubbornness {
                    float diplomacy = 0.35;
                    event OnApply(Unit u) { Api.Note(""a""); }
                }");
            // патч и переопределяет старое поле, и добавляет новое
            var patch = Mod("patch", @"
                attribute stubbornness {
                    float diplomacy = 0.5;
                    float trade = 0.2;
                }", "base");

            var engine = Load(Compile(baseMod, patch));

            engine.GetArchetypeConsts("attribute", "stubbornness", _buf);
            Assert.AreEqual(new[] { "diplomacy", "trade" }, _buf, "переопределение — тот же слот, не второе имя");

            Assert.IsTrue(engine.TryGetArchetypeConst("attribute", "stubbornness", "diplomacy", out var d));
            Assert.AreEqual(0.5f, d.ToF(), 1e-6f);
        }

        [Test]
        public void Enumerate_UnknownEntity_ClearsBuffer()
        {
            var engine = Load(Compile(Mod("game", @"
                weapon sword { float damage = 1.0; event OnHit(Unit u) { Api.Note(""x""); } }")));

            _buf.Add("мусор от прошлого вызова");
            engine.GetArchetypeConsts("weapon", "axe", _buf);
            Assert.IsEmpty(_buf);

            _buf.Add("мусор");
            engine.GetArchetypeConsts("armor", "sword", _buf);
            Assert.IsEmpty(_buf);
        }

        [Test]
        public void Read_EntityWithoutFields_EnumeratesEmpty()
        {
            var engine = Load(Compile(Mod("game", @"
                weapon sword { event OnHit(Unit u) { Api.Note(""x""); } }")));

            engine.GetArchetypeConsts("weapon", "sword", _buf);
            Assert.IsEmpty(_buf);
            Assert.IsFalse(engine.TryGetArchetypeConst("weapon", "sword", "damage", out _));
        }

        [Test]
        public void Read_QuotedIdWithDotsAndColons_ParsesKeyCorrectly()
        {
            // ключ статика — "a:вид:id.поле", а id может быть строковым литералом
            // с точками и двоеточиями: разбор обязан резать по ПЕРВОМУ ':' после
            // "a:" и по ПОСЛЕДНЕЙ точке, иначе рецепт таких сущностей теряется
            var engine = Load(Compile(Mod("pack", @"
                attribute ""pack.diplomacy:v2"" {
                    float weight = 0.75;
                    event OnApply(Unit u) { Api.Note(""a""); }
                }")));

            engine.GetArchetypeConsts("attribute", "pack.diplomacy:v2", _buf);
            Assert.AreEqual(new[] { "weight" }, _buf);

            Assert.IsTrue(engine.TryGetArchetypeConst("attribute", "pack.diplomacy:v2", "weight", out var w));
            Assert.AreEqual(0.75f, w.ToF(), 1e-6f);
        }

        [Test]
        public void Read_SameIdInTwoKinds_DoesNotCollide()
        {
            var engine = Load(Compile(Mod("game", @"
                weapon sword { float damage = 12.0; event OnHit(Unit u) { Api.Note(""w""); } }
                attribute sword { float damage = 99.0; event OnApply(Unit u) { Api.Note(""a""); } }")));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "damage", out var w));
            Assert.AreEqual(12f, w.ToF(), 1e-6f);

            Assert.IsTrue(engine.TryGetArchetypeConst("attribute", "sword", "damage", out var a));
            Assert.AreEqual(99f, a.ToF(), 1e-6f);
        }

        [Test]
        public void Read_ClassAndTriggerFields_AreNotArchetypeConsts()
        {
            // у класса ключ статика "c:...", у триггера "t:..." — в индекс
            // рецептов они попадать не должны
            var engine = Load(Compile(Mod("game", @"
                class Balance { int damage = 7; }
                trigger T { int damage = 9; event OnPing(Unit u) { Api.Note(""p""); } }
                weapon sword { float damage = 12.0; event OnHit(Unit u) { Api.Note(""w""); } }")));

            engine.GetArchetypeConsts("weapon", "sword", _buf);
            Assert.AreEqual(new[] { "damage" }, _buf);

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "damage", out var v));
            Assert.AreEqual(12f, v.ToF(), 1e-6f);
        }

        [Test]
        public void Reload_RebuildsIndex_NoStaleRecipes()
        {
            var engine = Load(Compile(Mod("game", @"
                weapon sword { float damage = 12.0; event OnHit(Unit u) { Api.Note(""w""); } }")));
            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "damage", out _));

            // хот-релоад: сущность переехала, полей стало другое количество
            var second = Compile(Mod("game", @"
                weapon axe {
                    float damage = 30.0;
                    int windup_ticks = 5;
                    event OnHit(Unit u) { Api.Note(""a""); }
                }"));
            Assert.IsTrue(second.Success, Dump(second));
            engine.LoadProgram(second.Program);
            engine.Tick(0.016f);

            Assert.IsFalse(engine.TryGetArchetypeConst("weapon", "sword", "damage", out _));
            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "axe", "damage", out var d));
            Assert.AreEqual(30f, d.ToF(), 1e-6f);

            engine.GetArchetypeConsts("weapon", "axe", _buf);
            Assert.AreEqual(new[] { "damage", "windup_ticks" }, _buf);
        }

        // ===================================================================
        // Просьба 2: объявленный контракт констант (закрытые наборы)
        // ===================================================================

        [Test]
        public void Contract_MissingRequiredConst_IsCompileError()
        {
            _weapon.Const<float>("damage", required: true)
                   .Const<int>("windup_ticks");

            // «меч без damage компилируется чисто и молча ничего не делает» —
            // ровно это и должно ломать сборку
            var r = Compile(Mod("game", @"
                weapon sword {
                    int windup_ticks = 3;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0223"), Dump(r));
            StringAssert.Contains("damage", Dump(r));
        }

        [Test]
        public void Contract_TypoInConstName_SurfacesAsMissingRequired()
        {
            _weapon.Const<float>("damage", required: true);

            var r = Compile(Mod("game", @"
                weapon sword {
                    float dmage = 12.0;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0223"), Dump(r));
            Assert.IsTrue(Has(r, "W0101"), Dump(r)); // и само лишнее поле подсвечено
        }

        [Test]
        public void Contract_AllConstsDeclared_CompilesAndReads()
        {
            _weapon.Const<float>("damage", required: true, doc: "урон удара")
                   .Const<int>("windup_ticks", doc: "замах в тиках");

            var engine = Load(Compile(Mod("game", @"
                weapon sword {
                    float damage = 12.0;
                    int windup_ticks = 3;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }")));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "damage", out var d));
            Assert.AreEqual(12f, d.ToF(), 1e-6f);
        }

        [Test]
        public void Contract_OptionalConstMissing_IsFine()
        {
            _weapon.Const<float>("damage", required: true)
                   .Const<int>("windup_ticks"); // необязательная

            var r = Compile(Mod("game", @"
                weapon sword {
                    float damage = 12.0;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));

            Assert.IsTrue(r.Success, Dump(r));
            Assert.IsFalse(Has(r, "E0223"), Dump(r));
            Assert.IsFalse(Has(r, "W0101"), Dump(r));
        }

        [Test]
        public void Contract_RequiredDeclaredWithoutValue_IsCompileError()
        {
            // 'float damage;' формально объявляет поле, но значения не даёт —
            // сущность снова «компилируется чисто и молча ничего не делает»
            _weapon.Const<float>("damage", required: true);

            var r = Compile(Mod("game", @"
                weapon sword {
                    float damage;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0223"), Dump(r));
            StringAssert.Contains("без значения", Dump(r));
        }

        [Test]
        public void Contract_WrongType_IsCompileError()
        {
            _weapon.Const<float>("damage", required: true);

            var r = Compile(Mod("game", @"
                weapon sword {
                    string damage = ""много"";
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0224"), Dump(r));
        }

        [Test]
        public void Contract_EnumConst_TypeNamesAreReadableInDiagnostics()
        {
            _weapon.Const<School>("school", required: true);

            var r = Compile(Mod("game", @"
                weapon sword {
                    int school = 0;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0224"), Dump(r));
            // контентщик обязан прочитать имя типа, а не Enum#0
            StringAssert.Contains("School", Dump(r));
            StringAssert.DoesNotContain("Enum#", Dump(r));
        }

        [Test]
        public void Contract_EnumConst_MatchingTypeCompiles()
        {
            _weapon.Const<School>("school", required: true);

            var engine = Load(Compile(Mod("game", @"
                weapon sword {
                    School school = School.Frost;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }")));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "school", out var v));
            Assert.AreEqual((int)School.Frost, v.EnumValue);
        }

        [Test]
        public void Contract_ExtraField_IsWarningNotError()
        {
            // блок вправе держать собственное состояние, поэтому лишнее поле —
            // подсказка «не опечатка ли?», а не запрет
            _weapon.Const<float>("damage", required: true);

            var r = Compile(Mod("game", @"
                weapon sword {
                    float damage = 12.0;
                    int hits = 0;
                    event OnHit(Unit u) { hits = hits + 1; }
                }"));

            Assert.IsTrue(r.Success, Dump(r));
            Assert.IsTrue(Has(r, "W0101"), Dump(r));
            foreach (var d in r.Diagnostics)
                Assert.AreNotEqual(Dsl.Text.Severity.Error, d.Severity, Dump(r));
        }

        [Test]
        public void Contract_ExtraFieldPatchedTwice_WarnsOnce()
        {
            _weapon.Const<float>("damage", required: true);

            var baseMod = Mod("base", @"
                weapon sword {
                    float damage = 12.0;
                    int hits = 0;
                    event OnHit(Unit u) { hits = hits + 1; }
                }");
            var patch = Mod("patch", @"weapon sword { int hits = 5; }", "base");

            var r = Compile(baseMod, patch);
            Assert.IsTrue(r.Success, Dump(r));

            int warnings = 0;
            foreach (var d in r.Diagnostics) if (d.Code == "W0101") warnings++;
            Assert.AreEqual(1, warnings, Dump(r));
        }

        [Test]
        public void Contract_NoConstsDeclared_ChecksNothing()
        {
            // вид 'attribute' не объявляет констант — набор открыт, любые поля
            // законны и не дают ни ошибок, ни предупреждений
            var r = Compile(Mod("pack", @"
                attribute stubbornness {
                    float diplomacy = 0.35;
                    float whatever_the_pack_invented = 1.0;
                    event OnApply(Unit u) { Api.Note(""a""); }
                }"));

            Assert.IsTrue(r.Success, Dump(r));
            Assert.IsFalse(Has(r, "W0101"), Dump(r));
            Assert.IsFalse(Has(r, "E0223"), Dump(r));
        }

        [Test]
        public void Contract_CheckedOnMergedEntity_PatchMaySupplyConst()
        {
            // проверяется ИТОГ мержа, а не отдельный блок: базовый блок вправе
            // не объявлять поле, если его добавляет патч
            _weapon.Const<float>("damage", required: true);

            var baseMod = Mod("base", @"
                weapon sword { event OnHit(Unit u) { Api.Note(""hit""); } }");
            var patch = Mod("patch", @"weapon sword { float damage = 12.0; }", "base");

            var r = Compile(baseMod, patch);
            Assert.IsTrue(r.Success, Dump(r));
        }

        [Test]
        public void Contract_EntityWithoutBlocks_IsNotChecked()
        {
            // вид объявляет обязательную константу, но контента нет вовсе —
            // это не ошибка компиляции (покрытие манифеста сверяет сборщик)
            _weapon.Const<float>("damage", required: true);

            var r = Compile(Mod("game", @"
                attribute a1 { float x = 1.0; event OnApply(Unit u) { Api.Note(""a""); } }"));

            Assert.IsTrue(r.Success, Dump(r));
        }

        [Test]
        public void Contract_DuplicateConstRegistration_Throws()
        {
            _weapon.Const<float>("damage", required: true);
            Assert.Throws<System.ArgumentException>(() => _weapon.Const<int>("damage"));
        }

        [Test]
        public void Contract_WorksWithKnownIdsTogether()
        {
            _weapon.KnownIds("sword", "axe").Const<float>("damage", required: true);

            var r = Compile(Mod("game", @"
                weapon spear {
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0202"), Dump(r));  // id не из набора
            Assert.IsTrue(Has(r, "E0223"), Dump(r));  // и damage забыт
        }

        // ===================================================================
        // Манифест: контракт обязан доезжать до инструментов вне игры
        // ===================================================================

        [Test]
        public void Manifest_RoundTrips_Consts()
        {
            _weapon.KnownIds("sword")
                   .Const<float>("damage", required: true, doc: "урон удара")
                   .Const<int>("windup_ticks")
                   .Const<string>("tooltip")
                   .Const<double>("precise_scale");

            string json = ApiManifest.Export(_host.Registry, 1);
            StringAssert.Contains("\"consts\"", json);
            StringAssert.Contains("\"windup_ticks\"", json);
            StringAssert.Contains("\"double\"", json); // double не должен уезжать в "void"

            var imported = ApiManifest.Import(json, out int apiVersion);
            Assert.AreEqual(1, apiVersion);
            Assert.IsTrue(imported.TryGetArchetypeKind("weapon", out var kind));
            Assert.AreEqual(4, kind.Consts.Count);

            Assert.AreEqual("damage", kind.Consts[0].Name);
            Assert.IsTrue(kind.Consts[0].Required);
            Assert.AreEqual(TypeKind.Float, kind.Consts[0].Type.Kind);
            Assert.AreEqual("урон удара", kind.Consts[0].Doc);

            Assert.IsFalse(kind.Consts[1].Required);
            Assert.AreEqual(TypeKind.Int, kind.Consts[1].Type.Kind);
            Assert.AreEqual(TypeKind.Str, kind.Consts[2].Type.Kind);
            Assert.AreEqual(TypeKind.Double, kind.Consts[3].Type.Kind);
        }

        [Test]
        public void Manifest_ImportedRegistry_EnforcesSameContract()
        {
            // инструменты (DslCheck, LSP) компилируют против импортированного
            // манифеста — контракт обязан ловить ту же ошибку вне игры
            _weapon.Const<float>("damage", required: true);

            var imported = ApiManifest.Import(ApiManifest.Export(_host.Registry, 1), out _);

            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "game", ApiVersion = 1, Sources = new[] { "game.sal" } },
            };
            set.Files.Add(("game/game.sal", @"
                weapon sword { event OnHit(Unit u) { Api.Note(""hit""); } }"));

            var r = ScriptCompiler.Compile(imported, 1, new List<ModuleSourceSet> { set });
            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0223"), Dump(r));
        }

        [Test]
        public void Manifest_NoConsts_OmitsTheField()
        {
            string json = ApiManifest.Export(_host.Registry, 1);
            StringAssert.DoesNotContain("\"consts\"", json);

            var imported = ApiManifest.Import(json, out _);
            Assert.IsTrue(imported.TryGetArchetypeKind("attribute", out var kind));
            Assert.AreEqual(0, kind.Consts.Count);
        }

        // ===================================================================
        // Сейв: рецепт живёт в статиках, значит переживает загрузку состояния
        // ===================================================================

        private sealed class NoEntities : ISaveEntityResolver
        {
            public long GetStableId(object entity) => 0;
            public object ResolveStableId(long id) => null;
        }

        [Test]
        public void SaveLoad_KeepsPatchedRecipe()
        {
            var prog = Compile(Mod("game", @"
                weapon sword {
                    float damage = 12.0;
                    int hits = 0;
                    event OnHit(Unit u) { hits = hits + 1; damage = 20.0; }
                }"));
            var engine = Load(prog);

            var hero = new Unit { Name = "H" };
            _onHit.Raise(engine, "sword", hero);
            engine.Tick(0.016f);

            var res = new NoEntities();
            byte[] save = engine.SaveState(res);

            var restored = new ScriptEngine(_host.Registry);
            restored.OnError += m => Assert.Fail(m);
            restored.LoadProgram(prog.Program);
            restored.LoadState(save, res);

            Assert.IsTrue(restored.TryGetArchetypeConst("weapon", "sword", "damage", out var d));
            Assert.AreEqual(20f, d.ToF(), 1e-6f, "статик восстановлен — рецепт читается из него же");
            Assert.IsTrue(restored.TryGetArchetypeConst("weapon", "sword", "hits", out var h));
            Assert.AreEqual(1, h.AsInt);
        }
    }
}
