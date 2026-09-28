using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Пояснения к элементам енума: host.Enum&lt;Slot&gt;().Member(Slot.MoveSpeed, "…").
    ///
    /// Зачем именно у элементов: енум был единственным объявлением хоста без
    /// описаний — методы, их аргументы, события, константы вида, поля структур,
    /// свойства сущностей и константы API их уже несут. А элемент енума — то
    /// место, где автор рецепта спрашивает про ЕДИНИЦЫ («Slot.MoveSpeed — м/с
    /// или клетки за тик?»). Складывать единицы в summary всего енума нельзя:
    /// это одна строка на три десятка элементов, и при наведении на элемент её
    /// никто не увидит.
    ///
    /// Хранение — параллельный массив рядом с Names (значение элемента = индекс),
    /// как и в манифесте. Кодировать нечего: это чистый текст.
    /// </summary>
    public sealed class EnumDocTests
    {
        public enum Slot { MoveSpeed, AttackSpeed, Armor }

        public enum Team { Red, Blue }

        [SalamanderClass("Параметры оружия.")]
        public enum WeaponParameter
        {
            [SalamanderMember("Тиков до полного заряда.")] ChargeToFullTicks,
            [SalamanderMember("Множитель урона на полном заряде.")] ChargeDamageMul,
            Reserved,
        }

        private HostBuilder _host;

        [SetUp]
        public void SetUp() => _host = new HostBuilder();

        private static HostEnumInfo EnumOf(HostRegistry r, string name)
        {
            Assert.IsTrue(r.TryGetEnum(name, out var e), $"енум '{name}' не зарегистрирован");
            return e;
        }

        // ===================================================================
        // Ради чего всё затевалось
        // ===================================================================

        [Test]
        public void Member_DocsAreStoredByValue()
        {
            _host.Enum<Slot>(summary: "Слоты характеристик.")
                 .Member(Slot.MoveSpeed, "Скорость передвижения, м/с.")
                 .Member(Slot.AttackSpeed, "Множитель времён оружия. Меньше — быстрее.");

            var e = EnumOf(_host.Registry, "Slot");
            Assert.AreEqual("Слоты характеристик.", e.Summary);
            Assert.AreEqual("Скорость передвижения, м/с.", e.DocOf((int)Slot.MoveSpeed));
            Assert.AreEqual("Множитель времён оружия. Меньше — быстрее.", e.DocOf((int)Slot.AttackSpeed));
            Assert.IsNull(e.DocOf((int)Slot.Armor), "неописанный элемент остаётся без пояснения");
        }

        [Test]
        public void EnumWithoutDocs_StaysExactlyAsBefore()
        {
            _host.Enum<Team>();

            var e = EnumOf(_host.Registry, "Team");
            Assert.IsNull(e.Docs, "массива нет вовсе, пока не описан ни один элемент");
            Assert.IsNull(e.DocOf(0));
        }

        [Test]
        public void ChainAfterEnum_KeepsWorking()
        {
            // Enum стал возвращать построитель — привычные цепочки не должны сломаться
            _host.Enum<Slot>().Enum<Team>();
            _host.Enum<WeaponParameter>().Host.Api("Api").Act("Note", (string s) => { });

            Assert.AreEqual(3, _host.Registry.EnumCount);
        }

        // ===================================================================
        // Явный путь и путь по атрибутам
        // ===================================================================

        [Test]
        public void ExplicitDefineEnum_TakesParallelDocs()
        {
            var r = new HostRegistry();
            r.DefineEnum("Slot", "Слоты.",
                new[] { "MoveSpeed", "AttackSpeed", "Armor" },
                new[] { "м/с", null, "плоское снижение урона" });

            var e = EnumOf(r, "Slot");
            Assert.AreEqual("м/с", e.DocOf(0));
            Assert.IsNull(e.DocOf(1), "null внутри массива — это «пояснения нет»");
            Assert.AreEqual("плоское снижение урона", e.DocOf(2));
        }

        [Test]
        public void ExplicitDefineEnum_LengthMismatch_IsRejected()
        {
            var r = new HostRegistry();
            var ex = Assert.Throws<System.ArgumentException>(
                () => r.DefineEnum("Slot", null, new[] { "A", "B" }, new[] { "только одно" }));
            StringAssert.Contains("параллельно", ex.Message);
        }

        [Test]
        public void AttributePath_ReadsDocsFromTheFieldsThemselves()
        {
            _host.Register(typeof(WeaponParameter));

            var e = EnumOf(_host.Registry, "WeaponParameter");
            Assert.AreEqual("Параметры оружия.", e.Summary);
            Assert.AreEqual("Тиков до полного заряда.", e.DocOf((int)WeaponParameter.ChargeToFullTicks));
            Assert.AreEqual("Множитель урона на полном заряде.", e.DocOf((int)WeaponParameter.ChargeDamageMul));
            Assert.IsNull(e.DocOf((int)WeaponParameter.Reserved));
        }

        // ===================================================================
        // Ошибки регистрации
        // ===================================================================

        [Test]
        public void DescribingTheSameMemberTwice_IsRejected()
        {
            // молча затирать пояснение неоткуда узнать — как и с дублем метода
            var b = _host.Enum<Slot>().Member(Slot.MoveSpeed, "первое");
            Assert.Throws<System.InvalidOperationException>(() => b.Member(Slot.MoveSpeed, "второе"));
        }

        [Test]
        public void EmptyDoc_IsRejected()
        {
            var b = _host.Enum<Slot>();
            Assert.Throws<System.ArgumentException>(() => b.Member(Slot.Armor, "   "));
        }

        [Test]
        public void UnknownMemberValue_IsRejected()
        {
            _host.Enum<Slot>();
            var id = _host.Registry.EnumCount - 1;
            Assert.Throws<System.ArgumentException>(
                () => _host.Registry.SetEnumMemberDoc(id, 99, "нет такого элемента"));
        }

        // ===================================================================
        // Манифест
        // ===================================================================

        [Test]
        public void Manifest_RoundTrips_MemberDocs()
        {
            _host.Enum<Slot>(summary: "Слоты характеристик.")
                 .Member(Slot.MoveSpeed, "Скорость передвижения, м/с.")
                 .Member(Slot.Armor, "Плоское снижение урона.");

            string json = ApiManifest.Export(_host.Registry, 1);
            StringAssert.Contains("\"memberDocs\"", json);
            StringAssert.Contains("Скорость передвижения, м/с.", json);

            var imported = ApiManifest.Import(json, out _);
            var e = EnumOf(imported, "Slot");
            Assert.AreEqual("Скорость передвижения, м/с.", e.DocOf((int)Slot.MoveSpeed));
            Assert.IsNull(e.DocOf((int)Slot.AttackSpeed));
            Assert.AreEqual("Плоское снижение урона.", e.DocOf((int)Slot.Armor));
        }

        [Test]
        public void Manifest_WithoutDocs_HasNoKeyAtAll()
        {
            // правка дополняющая: манифест игры без пояснений выглядит как раньше
            _host.Enum<Team>();

            string json = ApiManifest.Export(_host.Registry, 1);
            StringAssert.DoesNotContain("memberDocs", json);

            var imported = ApiManifest.Import(json, out _);
            Assert.IsNull(EnumOf(imported, "Team").Docs);
        }

        [Test]
        public void Manifest_OldFileWithoutDocs_StillLoads()
        {
            // ровно тот json, что писала предыдущая версия
            const string json = @"{
                ""apiVersion"": 1,
                ""enums"": [ { ""name"": ""Team"", ""members"": [ ""Red"", ""Blue"" ] } ],
                ""classes"": [], ""apis"": [], ""events"": []
            }";

            var r = ApiManifest.Import(json, out int v);
            Assert.AreEqual(1, v);
            var e = EnumOf(r, "Team");
            Assert.AreEqual(new[] { "Red", "Blue" }, e.Names);
            Assert.IsNull(e.Docs);
        }

        [Test]
        public void Manifest_MismatchedDocsArray_IsReported()
        {
            const string json = @"{
                ""apiVersion"": 1,
                ""enums"": [ { ""name"": ""Team"", ""members"": [ ""Red"", ""Blue"" ],
                              ""memberDocs"": [ ""красные"" ] } ],
                ""classes"": [], ""apis"": [], ""events"": []
            }";

            var ex = Assert.Throws<System.FormatException>(() => ApiManifest.Import(json, out _));
            StringAssert.Contains("параллельно", ex.Message);
        }

        // ===================================================================
        // Пояснения ничего не меняют в компиляции
        // ===================================================================

        [Test]
        public void Docs_DoNotAffectCompilationOrValues()
        {
            _host.Enum<Slot>().Member(Slot.AttackSpeed, "Множитель времён оружия.");
            _host.Class<Unit>().Prop("name", u => u.Name);
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));
            var onPing = _host.Event<Unit>("OnPing");

            var r = ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet>
            {
                Mod("game", @"
                    trigger T {
                        event OnPing(Unit u) {
                            Slot s = Slot.AttackSpeed;
                            if (s == Slot.AttackSpeed) { Api.Note(""ok""); }
                        }
                    }"),
            });
            Assert.IsTrue(r.Success, Dump(r));

            var engine = new ScriptEngine(_host.Registry);
            engine.OnError += m => Assert.Fail(m);
            engine.LoadProgram(r.Program);
            engine.Tick(0.016f);
            onPing.Raise(engine, new Unit { Name = "H" });
            engine.Tick(0.016f);

            Assert.AreEqual(new[] { "ok" }, _log);
        }

        public sealed class Unit { public string Name; }

        private readonly List<string> _log = new List<string>();

        private static ModuleSourceSet Mod(string name, string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = name, ApiVersion = 1, Sources = new[] { name + ".sal" } },
            };
            set.Files.Add((name + "/" + name + ".sal", src));
            return set;
        }

        private static string Dump(CompilationResult r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            return sb.ToString();
        }
    }
}
