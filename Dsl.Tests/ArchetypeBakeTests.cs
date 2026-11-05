using System;
using System.Collections.Generic;
using Dsl.Codegen;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// host.Kind&lt;T&gt;() + engine.Bake&lt;T&gt;(): вид архетипа описывается C#-типом
    /// один раз, данные запекаются в плотную таблицу (индекс = хэндл
    /// ResolveArchetype), с отпечатками, стороной полей и diff между версиями.
    /// </summary>
    public sealed class ArchetypeBakeTests
    {
        public enum School { Fire, Frost, Storm }

        public struct Dmg
        {
            public float Fire, Frost;
            public Dmg(float fire, float frost) { Fire = fire; Frost = frost; }
        }

        [SalamanderKind("spell", "Заклинание.")]
        public struct SpellDef
        {
            [KindField(doc: "Стоимость в ОД", required: true)] public int Cost;
            [KindField(defaultValue: 1)] public int Range;
            [KindField(defaultValue: 0.5)] public float Burn;
            [KindField] public double Scale;
            [KindField(defaultValue: false)] public bool Channel;
            [KindField(name: "visual_key")] public string Visual;
            [KindField(defaultValue: School.Fire)] public School School;
            [KindField(refKind: "spell")] public int Upgrade;
            [KindField(side: Side.Server, defaultValue: 0)] public int LootChance;
            [KindField(side: Side.Server)] public Dmg Damage;
            public int NotAField; // без атрибута — не часть вида
        }

        public struct Plain { [KindField] public int Hp; }

        private HostBuilder _host;
        private ArchEventRef<int> _onCast;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _host = new HostBuilder();
            _host.Api("Api").Act("Log", (string s) => _log.Add(s));
            _host.Enum<School>();
            _host.Struct<Dmg>("Dmg")
                 .Field("fire", (Dmg d) => d.Fire)
                 .Field("frost", (Dmg d) => d.Frost)
                 .Build(v => new Dmg(v.Float("fire"), v.Float("frost")));
            _onCast = _host.Kind<SpellDef>().Event<int>("OnCast");
        }

        private CompilationResult CompileRaw(params (string name, string[] deps, string src)[] mods)
        {
            var list = new List<ModuleSourceSet>();
            foreach (var m in mods)
            {
                var set = new ModuleSourceSet
                {
                    Manifest = new ModuleManifest
                    {
                        Name = m.name, ApiVersion = 1, Dependencies = m.deps ?? new string[0],
                        Sources = new[] { "a.sal" },
                    },
                };
                set.Files.Add((m.name + "/a.sal", m.src));
                list.Add(set);
            }
            return ScriptCompiler.Compile(_host.Registry, 1, list);
        }

        private CompiledProgram Compile(params (string name, string[] deps, string src)[] mods)
        {
            var r = CompileRaw(mods);
            var sb = new System.Text.StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            Assert.IsTrue(r.Success, sb.ToString());
            return r.Program;
        }

        private ScriptEngine Load(string src) => Load(Compile(("game", null, src)));

        private ScriptEngine Load(CompiledProgram p)
        {
            var e = new ScriptEngine(_host.Registry);
            e.LoadProgram(p);
            return e;
        }

        private const string Base = @"
spell fireball {
    int cost = 3; int range = 4; float burn = 2.5; double scale = 1.25; bool channel = true;
    string visual_key = ""fx_fire""; School school = School.Fire; string upgrade = ""inferno"";
    int lootChance = 30; Dmg damage = new Dmg(fire: 10.0);
    event OnCast(int n) { Api.Log($""cast {n} cost {cost}""); }
}
spell frost { int cost = 2; School school = School.Frost; }
spell inferno { int cost = 7; }
";

        private static bool Has(CompilationResult r, string code)
        {
            foreach (var d in r.Diagnostics) if (d.Code == code) return true;
            return false;
        }

        // ----- регистрация ---------------------------------------------------

        [Test]
        public void Kind_RegistersContract_RequiredAndTypesAreChecked()
        {
            Assert.IsTrue(Has(CompileRaw(("g", null, "spell a { int range = 2; }")), "E0223"), "нет обязательного cost");
            Assert.IsTrue(Has(CompileRaw(("g", null, "spell a { string cost = \"x\"; }")), "E0224"), "не тот тип");
        }

        [Test]
        public void Kind_KeepsEvents()
        {
            var e = Load(Base);
            _onCast.Raise(e, e.ResolveArchetype("spell", "fireball"), 1);
            CollectionAssert.AreEqual(new[] { "cast 1 cost 3" }, _log);
        }

        [Test]
        public void Kind_WithoutAttribute_UsesLowercaseTypeName()
        {
            _host.Kind<Plain>();
            var e = Load(Compile(("g", null, "spell s { int cost = 1; }\nplain orc { int hp = 40; }")));
            var t = e.Bake<Plain>();
            Assert.AreEqual("plain", t.Kind);
            Assert.AreEqual(40, t[0].Hp);
        }

        public struct Bad1 { [KindField(required: true, defaultValue: 1)] public int X; }
        public struct Bad2 { [KindField(refKind: "spell")] public string X; }
        public struct Bad3 { public int X; }

        [Test]
        public void Kind_RejectsInvalidSchemas()
        {
            Assert.Throws<ArgumentException>(() => _host.Kind<Bad1>());
            Assert.Throws<ArgumentException>(() => _host.Kind<Bad2>());
            Assert.Throws<ArgumentException>(() => _host.Kind<Bad3>());
            Assert.Throws<InvalidOperationException>(() => _host.Kind<SpellDef>(), "второй раз");
        }

        [Test]
        public void Bake_WithoutKind_ExplainsWhatToCall()
        {
            var e = Load(Base);
            var ex = Assert.Throws<InvalidOperationException>(() => e.Bake<Plain>());
            StringAssert.Contains("host.Kind<Plain>()", ex.Message);
        }

        // ----- запекание -----------------------------------------------------

        [Test]
        public void Bake_ReadsAllFieldTypes_AndDefaults()
        {
            var e = Load(Base);
            var t = e.Bake<SpellDef>();
            Assert.AreEqual(3, t.Count);

            int fb = t.IndexOf("fireball");
            ref readonly var s = ref t[fb];
            Assert.AreEqual(3, s.Cost);
            Assert.AreEqual(4, s.Range);
            Assert.AreEqual(2.5f, s.Burn);
            Assert.AreEqual(1.25, s.Scale);
            Assert.IsTrue(s.Channel);
            Assert.AreEqual("fx_fire", s.Visual);
            Assert.AreEqual(School.Fire, s.School);
            Assert.AreEqual(t.IndexOf("inferno"), s.Upgrade);
            Assert.AreEqual(30, s.LootChance);
            Assert.AreEqual(10f, s.Damage.Fire);

            ref readonly var f = ref t[t.IndexOf("frost")];
            Assert.AreEqual(1, f.Range, "дефолт вида");
            Assert.AreEqual(0.5f, f.Burn);
            Assert.AreEqual(School.Frost, f.School);
            Assert.IsNull(f.Visual, "необязательная строка без дефолта");
            Assert.AreEqual(-1, f.Upgrade, "ссылка без значения — -1, не 0");
            Assert.AreEqual(0, f.NotAField);
        }

        [Test]
        public void Bake_IndexIsArchetypeHandle_InDeclarationOrder()
        {
            var e = Load(Base);
            var t = e.Bake<SpellDef>();
            var ids = new List<string>();
            e.GetArchetypeIds("spell", ids);
            for (int i = 0; i < t.Count; i++)
            {
                Assert.AreEqual(ids[i], t.IdOf(i));
                Assert.AreEqual(e.ResolveArchetype("spell", t.IdOf(i)), i);
            }
            Assert.AreEqual(-1, t.IndexOf("nope"));
        }

        [Test]
        public void Bake_SeesMergedValue_FromPatchModule()
        {
            var e = Load(Compile(
                ("base", null, "spell fireball { int cost = 3; }"),
                ("balance", new[] { "base" }, "spell fireball { int cost = 5; }")));
            Assert.AreEqual(5, e.Bake<SpellDef>()[0].Cost);
        }

        [Test]
        public void Bake_ContractFields_AreReadonly_EvenWithoutModifier()
        {
            var r = CompileRaw(("g", null, "spell a { int cost = 1; event OnCast(int n) { cost = 2; } }"));
            Assert.IsFalse(r.Success, "присваивание контрактному полю — ошибка (оно readonly)");
        }

        [Test]
        public void Bake_UnknownReference_ListsAllProblems()
        {
            var e = Load("spell a { int cost = 1; string upgrade = \"ghost\"; }\nspell b { int cost = 1; string upgrade = \"phantom\"; }");
            var ex = Assert.Throws<BakeException>(() => e.Bake<SpellDef>());
            Assert.AreEqual(2, ex.Problems.Count);
            StringAssert.Contains("ghost", ex.Message);
            StringAssert.Contains("phantom", ex.Message);
        }

        [Test]
        public void Bake_EmptyReference_IsMinusOne()
        {
            var e = Load("spell a { int cost = 1; string upgrade = \"\"; }");
            Assert.AreEqual(-1, e.Bake<SpellDef>()[0].Upgrade);
        }

        [Test]
        public void Bake_KindWithoutBlocks_IsEmptyTable()
        {
            _host.Kind<Plain>();
            var e = Load(Base);
            Assert.AreEqual(0, e.Bake<Plain>().Count);
        }

        // ----- стороны -------------------------------------------------------

        [Test]
        public void SharedOnly_LeavesServerFieldsDefault()
        {
            var e = Load(Base);
            var client = e.Bake<SpellDef>(BakeSide.SharedOnly);
            ref readonly var s = ref client[client.IndexOf("fireball")];
            Assert.AreEqual(3, s.Cost);
            Assert.AreEqual(0, s.LootChance);
            Assert.AreEqual(0f, s.Damage.Fire);
            Assert.AreEqual(BakeSide.SharedOnly, client.Side);
            Assert.AreEqual(Side.Server, FieldNamed(client, "lootChance").Side);
        }

        [Test]
        public void SharedOnly_Hash_IgnoresServerValues()
        {
            var a = Load(Base).Bake<SpellDef>(BakeSide.SharedOnly);
            var b = Load(Base.Replace("lootChance = 30", "lootChance = 99")).Bake<SpellDef>(BakeSide.SharedOnly);
            Assert.AreEqual(a.ContentHash, b.ContentHash, "серверный патч не меняет клиентский блоб");
            var sa = Load(Base).Bake<SpellDef>();
            var sb = Load(Base.Replace("lootChance = 30", "lootChance = 99")).Bake<SpellDef>();
            Assert.AreNotEqual(sa.ContentHash, sb.ContentHash);
        }

        private static KindFieldInfo FieldNamed<T>(ArchetypeTable<T> t, string name)
        {
            foreach (var f in t.Fields) if (f.Name == name) return f;
            return null;
        }

        // ----- отпечатки -----------------------------------------------------

        [Test]
        public void Hashes_AreStable_AcrossEngines()
        {
            var a = Load(Base).Bake<SpellDef>();
            var b = Load(Base).Bake<SpellDef>();
            Assert.AreEqual(a.LayoutHash, b.LayoutHash);
            Assert.AreEqual(a.ContentHash, b.ContentHash);
        }

        [Test]
        public void Hashes_ValueChange_KeepsLayout_NewId_ChangesLayout()
        {
            var a = Load(Base).Bake<SpellDef>();
            var v = Load(Base.Replace("cost = 3", "cost = 4")).Bake<SpellDef>();
            Assert.AreEqual(a.LayoutHash, v.LayoutHash);
            Assert.AreNotEqual(a.ContentHash, v.ContentHash);
            var n = Load(Base + "\nspell spark { int cost = 1; }").Bake<SpellDef>();
            Assert.AreNotEqual(a.LayoutHash, n.LayoutHash);
        }

        // ----- diff ----------------------------------------------------------

        [Test]
        public void Diff_ChangedFields()
        {
            var a = Load(Base).Bake<SpellDef>();
            var b = Load(Base.Replace("int cost = 3; int range = 4", "int cost = 4; int range = 5")).Bake<SpellDef>();
            var d = ArchetypeTable<SpellDef>.Diff(a, b);
            Assert.AreEqual(1, d.Changes.Count, d.ToString());
            Assert.AreEqual(ArchetypeChangeKind.Changed, d.Changes[0].Change);
            Assert.AreEqual("fireball", d.Changes[0].Id);
            CollectionAssert.AreEqual(new[] { "cost", "range" }, d.Changes[0].Fields);
            Assert.IsFalse(d.IndicesShifted);
            Assert.IsFalse(d.LayoutChanged);
        }

        [Test]
        public void Diff_AddedAndRemoved_ShiftIndices()
        {
            var a = Load(Base).Bake<SpellDef>();
            var b = Load("spell spark { int cost = 1; }\n" + Base.Replace("spell frost { int cost = 2; School school = School.Frost; }", "")).Bake<SpellDef>();
            var d = ArchetypeTable<SpellDef>.Diff(a, b);
            var kinds = new Dictionary<string, ArchetypeChangeKind>();
            foreach (var c in d.Changes) kinds[c.Id] = c.Change;
            Assert.AreEqual(ArchetypeChangeKind.Added, kinds["spark"]);
            Assert.AreEqual(ArchetypeChangeKind.Removed, kinds["frost"]);
            Assert.IsFalse(kinds.ContainsKey("fireball"), "fireball не менялся, сдвинулся только индекс");
            Assert.IsTrue(d.IndicesShifted);
            Assert.IsTrue(d.LayoutChanged);
        }

        [Test]
        public void Diff_AfterHotSwap_SeesReinitializedReadonly()
        {
            var e = Load(Base);
            var before = e.Bake<SpellDef>();
            var rep = e.TryHotSwap(Compile(("game", null, Base.Replace("cost = 3", "cost = 6"))));
            Assert.IsTrue(rep.Applied, rep.ToString());
            var after = e.Bake<SpellDef>();
            Assert.AreEqual(6, after[after.IndexOf("fireball")].Cost);
            var d = ArchetypeTable<SpellDef>.Diff(before, after);
            Assert.AreEqual("fireball: Changed [cost]", d.ToString().Substring("spell: ".Length));
        }

        [Test]
        public void Diff_ServerOnlyChange_IsInvisibleForClientTables()
        {
            var a = Load(Base).Bake<SpellDef>(BakeSide.SharedOnly);
            var b = Load(Base.Replace("lootChance = 30", "lootChance = 1")).Bake<SpellDef>(BakeSide.SharedOnly);
            Assert.IsTrue(ArchetypeTable<SpellDef>.Diff(a, b).IsEmpty);
            Assert.Throws<ArgumentException>(() => ArchetypeTable<SpellDef>.Diff(a, Load(Base).Bake<SpellDef>()));
        }
    }
}
