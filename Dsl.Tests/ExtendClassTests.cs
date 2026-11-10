using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// host.ExtendClass&lt;T&gt;(): дописать члены к классу, объявленному в ДРУГОМ
    /// месте регистрации того же хоста. Движок объявляет UiBox со своими методами,
    /// модуль игры (отдельная сборка, движок о ней не знает) добавляет свои.
    ///
    /// Контракт: дописанное неотличимо от объявленного сразу (чекер, рантайм,
    /// манифест с документацией); класса нет / тип не тот / имя занято — исключение,
    /// тихой перезаписи нет.
    /// </summary>
    public sealed class ExtendClassTests
    {
        public sealed class UiBox
        {
            public readonly List<string> Log = new List<string>();
            public string Title = "box";
        }

        public sealed class Other { }

        private HostBuilder _host;
        private EventRef<UiBox> _onBuild;

        [SetUp]
        public void SetUp()
        {
            _host = new HostBuilder();

            // «движок»
            _host.Class<UiBox>("UiBox", "Контейнер интерфейса.")
                 .Prop("title", b => b.Title)
                 .Act("Label", (UiBox b, string s) => b.Log.Add("label:" + s));
            _onBuild = _host.Event<UiBox>("BuildUi");

            // «модуль игры» — позже, отдельным вызовом
            _host.ExtendClass<UiBox>()
                 .Act("Card", (UiBox b, string id) => b.Log.Add("card:" + id),
                      Sig.Doc("Карточка по id.").P("id", "идентификатор карточки"))
                 .Fn("CardChip", (UiBox b, string id, int n) => id + "x" + n,
                      Sig.Doc("Фишка карточки.").P("id", "идентификатор").P("n", "количество"))
                 .Prop("cards", b => b.Log.Count, doc: "сколько всего добавлено");
        }

        private static ModuleSourceSet Mod(string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "game", ApiVersion = 1, Sources = new[] { "game.sal" } },
            };
            set.Files.Add(("game/game.sal", src));
            return set;
        }

        private CompilationResult Compile(string src)
            => ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet> { Mod(src) });

        private static string Dump(CompilationResult r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            return sb.ToString();
        }

        [Test]
        public void ExtendedMembers_CompileAndRun_LikeDeclaredOnes()
        {
            var r = Compile(@"
                trigger T {
                    event BuildUi(UiBox b) {
                        b.Label(b.title);
                        b.Card(""a"");
                        b.Label(b.CardChip(""c"", 3));
                        b.Label($""{b.cards}"");
                    }
                }");
            Assert.IsTrue(r.Success, Dump(r));

            var engine = new ScriptEngine(_host.Registry);
            engine.OnError += m => Assert.Fail(m);
            engine.LoadProgram(r.Program);
            var box = new UiBox();
            _onBuild.Raise(engine, box);
            engine.Tick(0.016f);

            Assert.AreEqual(new[] { "label:box", "card:a", "label:cx3", "label:3" }, box.Log);
        }

        [Test]
        public void Checker_SeesExtendedMethodSignature()
        {
            var r = Compile(@"
                trigger T { event BuildUi(UiBox b) { b.Card(1, 2); } }");
            Assert.IsFalse(r.Success, "неверные аргументы дописанного метода должен ловить чекер");
        }

        [Test]
        public void Manifest_CarriesExtendedMembersWithDocs()
        {
            string json = ApiManifest.Export(_host.Registry, 1);
            StringAssert.Contains("Карточка по id.", json);
            StringAssert.Contains("идентификатор карточки", json);
            StringAssert.Contains("Фишка карточки.", json);
            StringAssert.Contains("сколько всего добавлено", json);

            var imported = ApiManifest.Import(json, out _);
            Assert.IsTrue(imported.TryGetClass("UiBox", out var cls));
            Assert.IsTrue(cls.TryGetMethod("Card", out var card));
            Assert.AreEqual("Карточка по id.", card.Summary?.Ru);
            Assert.AreEqual("идентификатор карточки", card.ParamDocs[0]?.Ru);
            Assert.IsTrue(cls.TryGetMethod("Label", out _), "свои методы движка на месте");
            Assert.IsTrue(cls.TryGetProp("cards", out _));
            Assert.AreEqual("Контейнер интерфейса.", cls.Summary?.Ru, "summary класса расширение не трогает");
        }

        [Test]
        public void HasClass()
        {
            Assert.IsTrue(_host.Registry.HasClass("UiBox"));
            Assert.IsFalse(_host.Registry.HasClass("Nope"));
            Assert.IsFalse(_host.Registry.HasClass(null));
        }

        [Test]
        public void UnknownClass_Throws()
        {
            var ex = Assert.Throws<System.InvalidOperationException>(() => _host.ExtendClass<Other>());
            StringAssert.Contains("Other", ex.Message);
            StringAssert.Contains("не объявлен", ex.Message);
        }

        [Test]
        public void WrongCSharpType_Throws()
        {
            var ex = Assert.Throws<System.InvalidOperationException>(() => _host.ExtendClass<Other>("UiBox"));
            StringAssert.Contains("UiBox", ex.Message);
            StringAssert.Contains("Other", ex.Message);
        }

        [Test]
        public void RawRegistryClass_CannotBeExtended()
        {
            _host.Registry.DefineClass("Raw");
            var ex = Assert.Throws<System.InvalidOperationException>(() => _host.ExtendClass<Other>("Raw"));
            StringAssert.Contains("Raw", ex.Message);
        }

        [Test]
        public void DuplicateMethod_ThrowsWithClassAndMethod()
        {
            var ex = Assert.Throws<System.InvalidOperationException>(() =>
                _host.ExtendClass<UiBox>().Act("Label", (UiBox b, string s) => { }));
            StringAssert.Contains("UiBox", ex.Message);
            StringAssert.Contains("Label", ex.Message);
        }

        [Test]
        public void DuplicateProperty_Throws_NoSilentOverwrite()
        {
            var ex = Assert.Throws<System.InvalidOperationException>(() =>
                _host.ExtendClass<UiBox>().Prop("title", b => "other"));
            StringAssert.Contains("UiBox", ex.Message);
            StringAssert.Contains("title", ex.Message);
        }

        [Test]
        public void MethodNamedLikeProperty_Throws()
        {
            Assert.Throws<System.InvalidOperationException>(() =>
                _host.ExtendClass<UiBox>().Act("title", (UiBox b) => { }));
        }

        [Test]
        public void Messages_InEnglish()
        {
            _host.UseLanguage(Dsl.Text.Language.En);
            var ex = Assert.Throws<System.InvalidOperationException>(() => _host.ExtendClass<Other>());
            StringAssert.Contains("not declared", ex.Message);
        }
    }
}
