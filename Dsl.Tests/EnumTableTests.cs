using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Text;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// host.EnumTable: енум из таблицы, а не из объявления C#. Значения C# любые
    /// (с пропусками, 128+ из открытого реестра), имена и порядок в скрипте — по
    /// спискам, пояснение — на элемент. Значение в скрипте = индекс в таблице.
    /// </summary>
    public sealed class EnumTableTests
    {
        public enum Field { Score = 0, Time = 1, ModA = 128, ModB = 200 }

        private HostBuilder _host;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _host = new HostBuilder();
            _log = new List<string>();
        }

        private void Register()
        {
            _host.EnumTable("MatchField", "Поля матча.",
                new[] { Field.Time, Field.Score, Field.ModB, Field.ModA },
                new[] { "time", "score", "modB", "modA" },
                new DocText[] { "секунды", null, DocText.Of("второй мод", "second mod"), "первый мод" });
        }

        private CompilationResult Compile(string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "game", ApiVersion = 1, Sources = new[] { "game.sal" } },
            };
            set.Files.Add(("game/game.sal", src));
            return ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet> { set });
        }

        [Test]
        public void ArbitraryValues_RoundTripThroughScript()
        {
            Register();
            _host.Api("Api")
                 .Act("Note", (Field f) => _log.Add(f.ToString()))
                 .Fn("Get", (int i) => i == 0 ? Field.ModB : Field.ModA);
            var ev = _host.Event<Field>("OnField");

            var r = Compile(@"
                trigger T {
                    event OnField(MatchField f) {
                        Api.Note(f);
                        Api.Note(MatchField.modA);
                        if (Api.Get(0) == MatchField.modB) { Api.Note(MatchField.time); }
                    }
                }");
            Assert.IsTrue(r.Success, string.Join("\n", r.Diagnostics));

            var engine = new ScriptEngine(_host.Registry);
            engine.OnError += m => Assert.Fail(m);
            engine.LoadProgram(r.Program);
            ev.Raise(engine, Field.ModB);
            engine.Tick(0.016f);

            Assert.AreEqual(new[] { "ModB", "ModA", "Time" }, _log);
        }

        [Test]
        public void OrderAndDocs_FollowTheTable_AndReachManifest()
        {
            Register();
            Assert.IsTrue(_host.Registry.TryGetEnum("MatchField", out var e));
            Assert.AreEqual(new[] { "time", "score", "modB", "modA" }, e.Names);
            Assert.AreEqual("секунды", e.DocOf(0)?.Ru);
            Assert.IsNull(e.DocOf(1));
            Assert.AreEqual("second mod", e.DocOf(2)?.En);

            string json = ApiManifest.Export(_host.Registry, 1);
            StringAssert.Contains("первый мод", json);
            var imported = ApiManifest.Import(json, out _);
            Assert.IsTrue(imported.TryGetEnum("MatchField", out var ie));
            Assert.AreEqual("первый мод", ie.DocOf(3)?.Ru);
            Assert.AreEqual("Поля матча.", ie.Summary?.Ru);
        }

        [Test]
        public void StringDocsOverload()
        {
            _host.EnumTable("F", null, new[] { Field.ModA }, new[] { "a" }, new List<string> { "doc a" });
            Assert.IsTrue(_host.Registry.TryGetEnum("F", out var e));
            Assert.AreEqual("doc a", e.DocOf(0)?.Ru);
        }

        [Test]
        public void NoDocs()
        {
            _host.EnumTable("F", null, new[] { Field.ModA, Field.ModB }, new[] { "a", "b" });
            Assert.IsTrue(_host.Registry.TryGetEnum("F", out var e));
            Assert.IsNull(e.DocOf(0));
        }

        [Test]
        public void Validation()
        {
            Assert.Throws<System.ArgumentException>(() =>
                _host.EnumTable("F", null, new[] { Field.ModA }, new[] { "a", "b" }), "разная длина");
            Assert.Throws<System.ArgumentException>(() =>
                _host.EnumTable("F", null, new[] { Field.ModA, Field.ModA }, new[] { "a", "b" }), "дубль значения");
            Assert.Throws<System.ArgumentException>(() =>
                _host.EnumTable("F", null, new[] { Field.ModA, Field.ModB }, new[] { "a", "a" }), "дубль имени");
            Assert.Throws<System.ArgumentException>(() =>
                _host.EnumTable("F", null, new[] { Field.ModA }, new[] { "1a" }), "не идентификатор");
            Assert.Throws<System.ArgumentException>(() =>
                _host.EnumTable("F", null, new[] { Field.ModA }, new[] { "wait" }), "ключевое слово");
            Assert.Throws<System.ArgumentException>(() =>
                _host.EnumTable("F", null, new[] { Field.ModA }, new[] { "a" }, new DocText[] { "x", "y" }), "длина пояснений");
            Assert.Throws<System.ArgumentException>(() =>
                _host.EnumTable("F", null, new Field[0], new string[0]), "пустая таблица");
            Assert.IsFalse(_host.Registry.TryGetEnum("F", out _), "после ошибок енум не зарегистрирован наполовину");
        }
    }
}
