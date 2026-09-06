using System.Collections.Generic;
using System.Globalization;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Структуры хоста: именованный НЕИЗМЕНЯЕМЫЙ набор полей, который скрипт
    /// собирает через «new Damage(slash: 21)», а игра читает обратно.
    ///
    /// Ключевые решения, которые тут и закрепляются:
    ///  - тип объявляет ХОСТ, скрипт только конструирует — это контракт игры,
    ///    а не пользовательский тип, иначе мод объявил бы свою Damage с девятым
    ///    полем и игра не смогла бы её прочитать;
    ///  - значение НЕИЗМЕНЯЕМО: полю нельзя присвоить. Именно это снимает вопрос
    ///    «значимая семантика или ссылочная» — у неизменяемого значения алиасинг
    ///    ненаблюдаем, копировать нечего;
    ///  - под капотом это массив Variant, нулевой слот — id типа. Поэтому
    ///    хранилище, сборщик мусора и сейв о структурах не знают ничего, а
    ///    значение при этом самоописуемо.
    /// </summary>
    public sealed class StructTests
    {
        public sealed class Unit { public long Id; public string Name; public Damage LastHit; }

        public enum School { Fire, Frost }

        /// <summary>C#-сторона: то, что игра получает обратно.</summary>
        public readonly struct Damage
        {
            public readonly float Pierce, Slash, Blunt, Fire;
            public Damage(float pierce, float slash, float blunt, float fire)
            {
                Pierce = pierce; Slash = slash; Blunt = blunt; Fire = fire;
            }
        }

        /// <summary>Вторая структура — для случаев «одна сторона границы не объявлена».</summary>
        public readonly struct Resist
        {
            public readonly float Fire;
            public Resist(float fire) { Fire = fire; }
        }

        private sealed class Resolver : ISaveEntityResolver
        {
            public long GetStableId(object entity) => entity is Unit u ? u.Id : 0;
            public object ResolveStableId(long id) => null;
        }

        private HostBuilder _host;
        private EventRef<Unit> _onPing;
        private EventRef<Unit, Damage> _onHurt;
        private ArchEventRef<Unit> _onHit;
        private List<string> _log;
        private List<string> _errors;
        private readonly List<string> _buf = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _errors = new List<string>();
            _host = new HostBuilder();
            _host.Enum<School>();

            // структура объявляется ДО всего, что её упоминает: свойства, методы
            // и события берут читателя/писателя прямо на регистрации
            _host.Struct<Damage>("Damage", "Урон по типам.")
                 .Field("pierce", (Damage d) => d.Pierce)
                 .Field("slash", (Damage d) => d.Slash)
                 .Field("blunt", (Damage d) => d.Blunt)
                 .Field("fire", (Damage d) => d.Fire)
                 .Build(v => new Damage(v.Float("pierce"), v.Float("slash"),
                                        v.Float("blunt"), v.Float("fire")));

            _host.Class<Unit>().Prop("name", u => u.Name).Prop("lastHit", u => u.LastHit);
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));
            _onPing = _host.Event<Unit>("OnPing");
            _onHurt = _host.Event<Unit, Damage>("OnHurt");
            _onHit = _host.Archetype("weapon").Event<Unit>("OnHit");
        }

        private static ModuleSourceSet Mod(string name, string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = name, ApiVersion = 1, Sources = new[] { name + ".sal" } },
            };
            set.Files.Add((name + "/" + name + ".sal", src));
            return set;
        }

        private CompilationResult Compile(string src)
            => ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet> { Mod("game", src) });

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

        /// <summary>Как Load, но ошибки рантайма копятся в _errors, а не валят тест.</summary>
        private ScriptEngine LoadTolerant(CompilationResult r)
        {
            Assert.IsTrue(r.Success, Dump(r));
            var engine = new ScriptEngine(_host.Registry);
            engine.OnError += m => _errors.Add(m);
            engine.LoadProgram(r.Program);
            engine.Tick(0.016f);
            return engine;
        }

        private void Fire(ScriptEngine engine)
        {
            _onPing.Raise(engine, new Unit { Id = 1, Name = "H" });
            engine.Tick(0.016f);
        }

        /// <summary>Ожидания форматируем инвариантно — движок печатает числа так же.</summary>
        private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);

        // ===================================================================
        // Ради чего всё затевалось
        // ===================================================================

        [Test]
        public void Recipe_OneField_RestAreDefaults()
        {
            var engine = Load(Compile(@"
                weapon sword {
                    readonly Damage damage = new Damage(slash: 21.0);
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "damage", out var v));
            var d = engine.ReadStruct<Damage>(v);

            Assert.AreEqual(21f, d.Slash, 1e-6f);
            Assert.AreEqual(0f, d.Pierce, 1e-6f, "остальные поля обязаны взять дефолт");
            Assert.AreEqual(0f, d.Blunt, 1e-6f);
            Assert.AreEqual(0f, d.Fire, 1e-6f);
        }

        [Test]
        public void Recipe_TwoFields_OrderDoesNotMatter()
        {
            var engine = Load(Compile(@"
                weapon mace {
                    readonly Damage damage = new Damage(fire: 5.0, blunt: 21.0);
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "mace", "damage", out var v));
            var d = engine.ReadStruct<Damage>(v);
            Assert.AreEqual(21f, d.Blunt, 1e-6f);
            Assert.AreEqual(5f, d.Fire, 1e-6f);
            Assert.AreEqual(0f, d.Slash, 1e-6f);
        }

        [Test]
        public void Recipe_NoFieldsAtAll_IsAllDefaults()
        {
            var engine = Load(Compile(@"
                weapon fist {
                    readonly Damage damage = new Damage();
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "fist", "damage", out var v));
            var d = engine.ReadStruct<Damage>(v);
            Assert.AreEqual(0f, d.Slash + d.Pierce + d.Blunt + d.Fire, 1e-6f);
        }

        [Test]
        public void ScriptReadsFields()
        {
            var engine = Load(Compile(@"
                class Balance {
                    readonly Damage hit = new Damage(slash: 12.5, fire: 2.0);
                }
                trigger T {
                    event OnPing(Unit u) { Api.Note($""{Balance.hit.slash} {Balance.hit.fire} {Balance.hit.blunt}""); }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "12.5 2 0" }, _log);
        }

        [Test]
        public void FieldsFlowIntoExpressionsAndCalls()
        {
            _host.Api("Math").Fn("Sum", (float a, float b) => a + b);

            var engine = Load(Compile(@"
                class Balance { readonly Damage hit = new Damage(slash: 3.0, blunt: 4.0); }
                trigger T {
                    event OnPing(Unit u) {
                        float total = Balance.hit.slash + Balance.hit.blunt;
                        Api.Note($""{total} {Math.Sum(Balance.hit.slash, 1.0)}"");
                    }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "7 4" }, _log);
        }

        [Test]
        public void StructValue_PassesThroughLocalsAndFunctions()
        {
            var engine = Load(Compile(@"
                class Balance { readonly Damage hit = new Damage(slash: 9.0); }
                trigger T {
                    func Show(Damage d) { Api.Note($""{d.slash}""); }
                    event OnPing(Unit u) {
                        Damage local = Balance.hit;
                        Show(local);
                    }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "9" }, _log);
        }

        [Test]
        public void ComputedFieldValues_AreAllowed()
        {
            // аргумент — обычное выражение, не только литерал
            var engine = Load(Compile(@"
                class Balance {
                    const float K = 2.0;
                    readonly Damage hit = new Damage(slash: 10.0 * K);
                }
                trigger T { event OnPing(Unit u) { Api.Note($""{Balance.hit.slash}""); } }"));
            Fire(engine);

            Assert.AreEqual(new[] { "20" }, _log);
        }

        // ===================================================================
        // Неизменяемость и границы
        // ===================================================================

        [Test]
        public void FieldAssignment_IsCompileError()
        {
            var r = Compile(@"
                class Balance { readonly Damage hit = new Damage(slash: 1.0); }
                trigger T { event OnPing(Unit u) { Balance.hit.slash = 5.0; } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0236"), Dump(r));
            StringAssert.Contains("неизменяемы", Dump(r));
        }

        [Test]
        public void UnknownField_InConstruction_IsReported()
        {
            var r = Compile(@"
                class Balance { readonly Damage hit = new Damage(slashh: 1.0); }
                trigger T { event OnPing(Unit u) { Api.Note(""x""); } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0234"), Dump(r));
        }

        [Test]
        public void UnknownField_OnRead_IsReported()
        {
            var r = Compile(@"
                class Balance { readonly Damage hit = new Damage(slash: 1.0); }
                trigger T { event OnPing(Unit u) { Api.Note($""{Balance.hit.holy}""); } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0232"), Dump(r));
        }

        [Test]
        public void DuplicateField_IsReported()
        {
            var r = Compile(@"
                class Balance { readonly Damage hit = new Damage(slash: 1.0, slash: 2.0); }
                trigger T { event OnPing(Unit u) { Api.Note(""x""); } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0235"), Dump(r));
        }

        [Test]
        public void UnknownStructType_IsReported()
        {
            var r = Compile(@"
                class Balance { readonly Damage hit = new Resist(slash: 1.0); }
                trigger T { event OnPing(Unit u) { Api.Note(""x""); } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0233"), Dump(r));
        }

        [Test]
        public void WrongFieldType_IsReported()
        {
            var r = Compile(@"
                class Balance { readonly Damage hit = new Damage(slash: ""много""); }
                trigger T { event OnPing(Unit u) { Api.Note(""x""); } }");

            Assert.IsFalse(r.Success, Dump(r));
        }

        [Test]
        public void Comparison_IsForbidden()
        {
            // сравнение шло бы по хэндлу: две одинаковые Damage оказались бы неравны
            var r = Compile(@"
                class Balance {
                    readonly Damage a = new Damage(slash: 1.0);
                    readonly Damage b = new Damage(slash: 1.0);
                }
                trigger T { event OnPing(Unit u) { if (Balance.a == Balance.b) { Api.Note(""x""); } } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0237"), Dump(r));
        }

        [Test]
        public void StructAsMapKey_IsForbidden()
        {
            var r = Compile(@"
                class Balance { Map<Damage, float> m = new Map<Damage, float>(); }
                trigger T { event OnPing(Unit u) { Api.Note(""x""); } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0122"), Dump(r));
        }

        [Test]
        public void PositionalArguments_AreRejected()
        {
            // именованные аргументы существуют только тут; позиционный вызов
            // у структуры с восемью числами нечитаем и ломается вставкой поля
            var r = Compile(@"
                class Balance { readonly Damage hit = new Damage(21.0); }
                trigger T { event OnPing(Unit u) { Api.Note(""x""); } }");

            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0230"), Dump(r));
        }

        [Test]
        public void ScriptCannotDeclareStructType()
        {
            // объявляет игра: иначе мод объявил бы свою Damage, и хост её не прочёл бы
            var r = Compile(@"
                struct Damage { float slash; }
                trigger T { event OnPing(Unit u) { Api.Note(""x""); } }");

            Assert.IsFalse(r.Success, Dump(r));
        }

        // ===================================================================
        // Дефолты полей: не только нули
        // ===================================================================

        [Test]
        public void NonZeroDefaults_OfEveryLiteralType()
        {
            _host.Struct<object>("Profile")
                 .Field<float>("weight", 1.5f)
                 .Field<int>("ticks", 7)
                 .Field<bool>("enabled", true)
                 .Field<string>("label", "по умолчанию")
                 .Field<School>("school", School.Frost);

            var engine = Load(Compile(@"
                weapon sword {
                    readonly Profile p = new Profile(ticks: 9);
                    event OnHit(Unit u) { Api.Note($""{p.weight} {p.ticks} {p.enabled} {p.label}""); }
                }"));

            _onHit.Raise(engine, "sword", new Unit { Id = 1 });
            engine.Tick(0.016f);
            Assert.AreEqual(new[] { "1.5 9 true по умолчанию" }, _log);

            // енум-дефолт читается хостом
            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "p", out var v));
            Assert.IsTrue(engine.TryGetStructField(v, "school", out var school));
            Assert.AreEqual((int)School.Frost, school.EnumValue);
            Assert.IsTrue(engine.TryGetStructField(v, "label", out var label));
            Assert.AreEqual("по умолчанию", engine.ResolveString(label));
        }

        // ===================================================================
        // Чтение хостом
        // ===================================================================

        [Test]
        public void Host_ReadsFieldsByName_AndEnumeratesThem()
        {
            var engine = Load(Compile(@"
                weapon sword {
                    readonly Damage damage = new Damage(slash: 21.0, fire: 3.0);
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "damage", out var v));

            Assert.AreEqual("Damage", engine.GetStructTypeName(v));

            engine.GetStructFields(v, _buf);
            Assert.AreEqual(new[] { "pierce", "slash", "blunt", "fire" }, _buf);

            Assert.IsTrue(engine.TryGetStructField(v, "slash", out var slash));
            Assert.AreEqual(21f, slash.ToF(), 1e-6f);
            Assert.IsFalse(engine.TryGetStructField(v, "holy", out _));
        }

        [Test]
        public void Host_NonStructValue_IsRecognised()
        {
            var engine = Load(Compile(@"
                weapon sword {
                    readonly float plain = 1.0;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "plain", out var v));
            Assert.IsNull(engine.GetStructTypeName(v));
            Assert.IsFalse(engine.TryGetStructField(v, "slash", out _));
            Assert.Throws<System.InvalidOperationException>(() => engine.ReadStruct<Damage>(v));
        }

        [Test]
        public void Host_WithoutFactory_SaysWhereToGo()
        {
            _host.Struct<object>("Resist").Field<float>("fire");

            var engine = Load(Compile(@"
                weapon sword {
                    readonly Resist r = new Resist(fire: 1.0);
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "r", out var v));
            // поля читаются и без фабрики
            Assert.IsTrue(engine.TryGetStructField(v, "fire", out var f));
            Assert.AreEqual(1f, f.ToF(), 1e-6f);

            var ex = Assert.Throws<System.InvalidOperationException>(() => engine.ReadStruct<object>(v));
            StringAssert.Contains("Build", ex.Message);
        }

        // ===================================================================
        // Граница «скрипт ↔ C#»: структура как аргумент и как результат
        // ===================================================================
        // Внутрь структура едет через фабрику из .Build(...), наружу — через
        // геттеры полей у .Field(...). Обе стороны необязательны: кто не нужен,
        // тот не объявляется, а понятная ошибка всплывает только при попытке
        // воспользоваться недостающей стороной.

        [Test]
        public void HostMethod_TakesStruct()
        {
            _host.Api("Fx").Act("Apply", (Damage d) => _log.Add($"{F(d.Slash)}/{F(d.Fire)}"));

            var engine = Load(Compile(@"
                class Balance { readonly Damage hit = new Damage(slash: 3.0, fire: 1.0); }
                trigger T {
                    event OnPing(Unit u) {
                        Fx.Apply(new Damage(slash: 2.0));
                        Fx.Apply(Balance.hit);
                    }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "2/0", "3/1" }, _log);
        }

        [Test]
        public void HostMethod_TakesStruct_AmongOtherArguments()
        {
            _host.Api("Fx").Act("Hit",
                (Unit u, Damage d, float mul) => _log.Add($"{u.Name} {F(d.Slash * mul)}"));

            var engine = Load(Compile(@"
                trigger T {
                    event OnPing(Unit u) { Fx.Hit(u, new Damage(slash: 4.0), 1.5); }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "H 6" }, _log);
        }

        [Test]
        public void HostMethod_ReturnsStruct()
        {
            _host.Api("Fx").Fn("Double",
                (Damage d) => new Damage(d.Pierce * 2, d.Slash * 2, d.Blunt * 2, d.Fire * 2));

            var engine = Load(Compile(@"
                class Balance { readonly Damage hit = new Damage(slash: 3.0, fire: 1.0); }
                trigger T {
                    event OnPing(Unit u) {
                        Damage d = Fx.Double(Balance.hit);
                        Api.Note($""{d.slash} {d.fire} {d.blunt}"");
                    }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "6 2 0" }, _log);
        }

        [Test]
        public void HostMethod_RoundTrip_ValueSurvivesBothDirections()
        {
            _host.Api("Fx")
                 .Fn("Make", (float slash) => new Damage(0f, slash, 0f, 0f))
                 .Act("Apply", (Damage d) => _log.Add(F(d.Slash)));

            var engine = Load(Compile(@"
                trigger T { event OnPing(Unit u) { Fx.Apply(Fx.Make(7.5)); } }"));
            Fire(engine);

            Assert.AreEqual(new[] { "7.5" }, _log);
        }

        [Test]
        public void Event_CarriesStruct()
        {
            var engine = Load(Compile(@"
                trigger T {
                    event OnHurt(Unit u, Damage d) { Api.Note($""{u.name} {d.slash} {d.fire}""); }
                }"));

            _onHurt.Raise(engine, new Unit { Id = 1, Name = "H" }, new Damage(0f, 4f, 0f, 2f));
            engine.Tick(0.016f);

            Assert.AreEqual(new[] { "H 4 2" }, _log);
        }

        [Test]
        public void Property_ReturnsStruct()
        {
            var engine = Load(Compile(@"
                trigger T { event OnPing(Unit u) { Api.Note($""{u.lastHit.blunt}""); } }"));

            _onPing.Raise(engine, new Unit { Id = 1, Name = "H", LastHit = new Damage(0f, 0f, 5f, 0f) });
            engine.Tick(0.016f);

            Assert.AreEqual(new[] { "5" }, _log);
        }

        [Test]
        public void ReturnedStruct_IsOrdinaryValue_AndSurvivesSweep()
        {
            // отданное скрипту значение — обычная коллекция: сборщик видит его
            // через статики, ничего особенного про структуры не зная
            _host.Api("Fx").Fn("Make", (float slash) => new Damage(0f, slash, 0f, 0f));

            var engine = Load(Compile(@"
                class Keep { Damage last = new Damage(); }
                trigger T {
                    event OnPing(Unit u) {
                        if (Keep.last.slash > 0.0) { Api.Note($""{Keep.last.slash}""); }
                        else { Keep.last = Fx.Make(5.0); }
                    }
                }"));

            Fire(engine);
            engine.Collect();
            Fire(engine);

            Assert.AreEqual(new[] { "5" }, _log);
        }

        [Test]
        public void ArchetypeContract_CanRequireStructConst()
        {
            // Const<Damage> в контракте вида работает — в отличие от ConstOr,
            // которому нужен литерал по умолчанию, а литерала структуры нет
            var armor = _host.Archetype("armor");
            armor.Const<Damage>("resist", required: true);
            armor.Event<Unit>("OnBlock");

            var ok = Compile(@"armor plate {
                readonly Damage resist = new Damage(pierce: 5.0);
                event OnBlock(Unit u) { Api.Note(""block""); }
            }");
            Assert.IsTrue(ok.Success, Dump(ok));

            var missing = Compile(@"armor cloth {
                event OnBlock(Unit u) { Api.Note(""block""); }
            }");
            Assert.IsFalse(missing.Success, Dump(missing));

            // а вот дефолта у структуры быть не может: дефолт — это литерал
            var ex = Assert.Throws<System.ArgumentException>(
                () => armor.ConstOr<Damage>("weak", default));
            StringAssert.Contains("Const<T>", ex.Message);
        }

        [Test]
        public void StructsLiveInCollections()
        {
            // структура — обычное значение: лежит в списке и в map, ходит по циклу
            _host.Api("Fx").Act("Apply", (Damage d) => _log.Add(F(d.Slash)));

            var engine = Load(Compile(@"
                trigger T {
                    event OnPing(Unit u) {
                        List<Damage> all = new List<Damage>();
                        all.Add(new Damage(slash: 1.0));
                        all.Add(new Damage(slash: 2.0));
                        for d in all { Fx.Apply(d); }

                        Map<string, Damage> byName = new Map<string, Damage>();
                        byName[""sword""] = new Damage(slash: 3.0);
                        Fx.Apply(byName[""sword""]);
                    }
                }"));
            Fire(engine);

            Assert.AreEqual(new[] { "1", "2", "3" }, _log);
        }

        [Test]
        public void WrongStructType_InArgument_IsCompileError()
        {
            _host.Struct<Resist>("Resist")
                 .Field("fire", (Resist r) => r.Fire)
                 .Build(v => new Resist(v.Float("fire")));
            _host.Api("Fx").Act("Apply", (Damage d) => _log.Add("x"));

            var r = Compile(@"
                trigger T { event OnPing(Unit u) { Fx.Apply(new Resist(fire: 1.0)); } }");

            Assert.IsFalse(r.Success, Dump(r));
        }

        [Test]
        public void Argument_WithoutFactory_SaysWhatToAdd()
        {
            // поля объявлены, а собирать C#-значение нечем — .Build не вызван
            _host.Struct<Resist>("Resist").Field("fire", (Resist r) => r.Fire);
            _host.Api("Fx").Act("Apply", (Resist r) => _log.Add("никогда"));

            var engine = LoadTolerant(Compile(@"
                trigger T { event OnPing(Unit u) { Fx.Apply(new Resist(fire: 1.0)); } }"));
            Fire(engine);

            Assert.IsEmpty(_log);
            Assert.AreEqual(1, _errors.Count, string.Join("\n", _errors));
            StringAssert.Contains("Build", _errors[0]);
        }

        [Test]
        public void Result_WithoutGetters_SaysWhichFieldIsMissingOne()
        {
            // фабрика есть (можно принимать), геттеров нет (нельзя отдавать)
            _host.Struct<Resist>("Resist")
                 .Field<float>("fire")
                 .Build(v => new Resist(v.Float("fire")));
            _host.Api("Fx").Fn("Zero", () => new Resist(0f));

            var engine = LoadTolerant(Compile(@"
                trigger T { event OnPing(Unit u) { Resist r = Fx.Zero(); Api.Note($""{r.fire}""); } }"));
            Fire(engine);

            Assert.IsEmpty(_log);
            Assert.AreEqual(1, _errors.Count, string.Join("\n", _errors));
            StringAssert.Contains("fire", _errors[0]);
            StringAssert.Contains("геттер", _errors[0]);
        }

        [Test]
        public void StructValue_ReadsFieldsLazily_WithoutCopying()
        {
            // фабрика читает поля прямо из значения: важно, что она видит именно
            // то, что собрал скрипт, а не снимок неизвестной свежести
            _host.Struct<Resist>("Resist")
                 .Field("fire", (Resist r) => r.Fire)
                 .Build(v =>
                 {
                     Assert.AreEqual("Resist", v.TypeName);
                     Assert.AreEqual(3f, v.Float("fire"), 1e-6f);
                     Assert.Throws<System.ArgumentException>(() => v.Get("frost"));
                     return new Resist(v.Float("fire"));
                 });
            _host.Api("Fx").Act("Apply", (Resist r) => _log.Add(F(r.Fire)));

            var engine = Load(Compile(@"
                trigger T { event OnPing(Unit u) { Fx.Apply(new Resist(fire: 3.0)); } }"));
            Fire(engine);

            Assert.AreEqual(new[] { "3" }, _log);
        }

        // ===================================================================
        // Сейв и мерж — структура ведёт себя как обычное readonly-значение
        // ===================================================================

        [Test]
        public void PatchBlock_OverridesWholeValue()
        {
            var baseMod = Mod("base", @"
                weapon sword {
                    readonly Damage damage = new Damage(slash: 21.0);
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }");
            var patch = new ModuleSourceSet
            {
                Manifest = new ModuleManifest
                {
                    Name = "patch", ApiVersion = 1,
                    Dependencies = new[] { "base" }, Sources = new[] { "patch.sal" },
                },
            };
            patch.Files.Add(("patch/patch.sal",
                @"weapon sword { readonly Damage damage = new Damage(slash: 30.0, fire: 4.0); }"));

            var r = ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet> { baseMod, patch });
            var engine = Load(r);

            Assert.IsTrue(engine.TryGetArchetypeConst("weapon", "sword", "damage", out var v));
            var d = engine.ReadStruct<Damage>(v);
            Assert.AreEqual(30f, d.Slash, 1e-6f);
            Assert.AreEqual(4f, d.Fire, 1e-6f);
        }

        [Test]
        public void Save_ReadonlyStruct_IsRebalancedNotRestored()
        {
            var v1 = Compile(@"
                weapon sword {
                    readonly Damage damage = new Damage(slash: 21.0);
                    int hits = 0;
                    event OnHit(Unit u) { hits = hits + 1; }
                }");
            var engine = Load(v1);
            _onHit.Raise(engine, "sword", new Unit { Id = 1 });
            engine.Tick(0.016f);

            var res = new Resolver();
            byte[] save = engine.SaveState(res);

            var v2 = Compile(@"
                weapon sword {
                    readonly Damage damage = new Damage(slash: 40.0);
                    int hits = 0;
                    event OnHit(Unit u) { hits = hits + 1; }
                }");
            Assert.IsTrue(v2.Success, Dump(v2));
            var loaded = new ScriptEngine(_host.Registry);
            loaded.OnError += m => Assert.Fail(m);
            loaded.LoadProgram(v2.Program);
            loaded.LoadState(save, res);

            Assert.IsTrue(loaded.TryGetArchetypeConst("weapon", "sword", "damage", out var v));
            Assert.AreEqual(40f, loaded.ReadStruct<Damage>(v).Slash, 1e-6f,
                "readonly не сериализуется — значение из НОВОЙ программы");
            Assert.IsTrue(loaded.TryGetArchetypeConst("weapon", "sword", "hits", out var hits));
            Assert.AreEqual(1, hits.AsInt);
        }

        [Test]
        public void Save_MutableStructField_RoundTrips()
        {
            // изменяемое поле со структурой — обычное состояние, оно сохраняется
            var prog = Compile(@"
                weapon sword {
                    Damage last = new Damage(slash: 1.0);
                    event OnHit(Unit u) { last = new Damage(slash: 7.0, fire: 2.0); }
                }");
            var engine = Load(prog);
            _onHit.Raise(engine, "sword", new Unit { Id = 1 });
            engine.Tick(0.016f);

            var res = new Resolver();
            var loaded = new ScriptEngine(_host.Registry);
            loaded.OnError += m => Assert.Fail(m);
            loaded.LoadProgram(prog.Program);
            loaded.LoadState(engine.SaveState(res), res);

            Assert.IsTrue(loaded.TryGetArchetypeConst("weapon", "sword", "last", out var v));
            var d = loaded.ReadStruct<Damage>(v);
            Assert.AreEqual(7f, d.Slash, 1e-6f);
            Assert.AreEqual(2f, d.Fire, 1e-6f);
        }

        // ===================================================================
        // Манифест
        // ===================================================================

        [Test]
        public void Manifest_RoundTrips_Structs()
        {
            _host.Struct<object>("Profile")
                 .Field<float>("weight", 1.5f)
                 .Field<int>("ticks", 7)
                 .Field<bool>("enabled", true)
                 .Field<string>("label", "по умолчанию")
                 .Field<School>("school", School.Frost);

            string json = ApiManifest.Export(_host.Registry, 1);
            StringAssert.Contains("\"structs\"", json);
            StringAssert.Contains("\"Frost\"", json); // енум именем, а не индексом

            var imported = ApiManifest.Import(json, out _);
            Assert.IsTrue(imported.TryGetStruct("Damage", out var dmg));
            Assert.AreEqual(4, dmg.Fields.Count);
            Assert.AreEqual("pierce", dmg.Fields[0].Name);

            Assert.IsTrue(imported.TryGetStruct("Profile", out var prof));
            Assert.AreEqual(1.5f, prof.FieldByName["weight"].Default.ToF(), 1e-6f);
            Assert.AreEqual(7, prof.FieldByName["ticks"].Default.AsInt);
            Assert.IsTrue(prof.FieldByName["enabled"].Default.AsBool);
            Assert.AreEqual("по умолчанию", prof.FieldByName["label"].DefaultStr);
            Assert.AreEqual((int)School.Frost, prof.FieldByName["school"].Default.EnumValue);
        }

        [Test]
        public void Manifest_RoundTrips_StructsInSignatures()
        {
            // структура в параметре, в результате, в свойстве класса и в аргументе
            // события — всё это едет через манифест, значит структуры обязаны
            // объявляться в нём РАНЬШЕ классов и методов
            _host.Api("Fx").Fn("Double",
                (Damage d) => new Damage(d.Pierce * 2, d.Slash * 2, d.Blunt * 2, d.Fire * 2));

            var imported = ApiManifest.Import(ApiManifest.Export(_host.Registry, 1), out _);

            var r = ScriptCompiler.Compile(imported, 1, new List<ModuleSourceSet>
            {
                Mod("game", @"
                    trigger T {
                        event OnHurt(Unit u, Damage d) {
                            Damage x = Fx.Double(d);
                            Api.Note($""{x.slash} {u.lastHit.blunt}"");
                        }
                    }"),
            });
            Assert.IsTrue(r.Success, Dump(r));
        }

        [Test]
        public void Manifest_ImportedRegistry_CompilesConstruction()
        {
            var imported = ApiManifest.Import(ApiManifest.Export(_host.Registry, 1), out _);

            var r = ScriptCompiler.Compile(imported, 1, new List<ModuleSourceSet>
            {
                Mod("game", @"
                    weapon sword {
                        readonly Damage damage = new Damage(slash: 21.0);
                        event OnHit(Unit u) { Api.Note($""{damage.slash}""); }
                    }"),
            });
            Assert.IsTrue(r.Success, Dump(r));
        }

        // ===================================================================
        // Регистрация
        // ===================================================================

        [Test]
        public void DuplicateStructName_IsRejected()
        {
            Assert.Throws<System.InvalidOperationException>(() => _host.Struct<object>("Damage"));
        }

        [Test]
        public void NameClashWithClassOrEnum_IsRejected()
        {
            Assert.Throws<System.InvalidOperationException>(() => _host.Struct<object>("Unit"));
            Assert.Throws<System.InvalidOperationException>(() => _host.Struct<object>("School"));
        }

        [Test]
        public void NameClash_TheOtherWayRound_IsRejectedToo()
        {
            // имя типа в скрипте одно на всех: молчаливый дубль решался бы
            // порядком проверок в чекере, а это не то, на что стоит полагаться
            Assert.Throws<System.InvalidOperationException>(() => _host.Class<object>("Damage"));
            Assert.Throws<System.InvalidOperationException>(
                () => _host.Registry.DefineEnum("Damage", null, new[] { "A" }));
        }

        [Test]
        public void DuplicateFieldName_IsRejected()
        {
            var b = _host.Struct<object>("Resist").Field<float>("fire");
            Assert.Throws<System.ArgumentException>(() => b.Field<float>("fire"));
        }

        [Test]
        public void EntityFieldType_IsRejected()
        {
            Assert.Throws<System.ArgumentException>(
                () => _host.Struct<object>("Bad").Field<Unit>("owner"));
        }
    }
}
