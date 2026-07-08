using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Контракт блоков-архетипов: механики игровых сущностей по шаблону вида
    /// (spell/item/...), объявленного хостом. Адресный диспатч по (вид, id),
    /// id интернированы в int; поздний блок переопределяет ранний ПО-СОБЫТИЙНО.
    /// </summary>
    public sealed class ArchetypeTests
    {
        public sealed class Unit { public string Name; }

        private HostBuilder _host;
        private ArchEventRef<Unit, float> _onCast;
        private ArchEventRef<Unit> _onObtain;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _host = new HostBuilder();
            _host.Class<Unit>().Prop("name", u => u.Name);
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));

            var spell = _host.Archetype("spell", summary: "Механика заклинания.");
            _onCast = spell.Event<Unit, float>("OnCast",
                Sig.Doc("Каст.").P("caster", "кто кастует").P("power", "сила"));
            _onObtain = spell.Event<Unit>("OnObtain");
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

        [Test]
        public void Raise_RunsOnlyAddressedBlock()
        {
            var r = Compile(Mod("game", @"
                spell fireball { event OnCast(Unit c, float p) { Api.Note($""fire {c.name} {p}""); } }
                spell icebolt  { event OnCast(Unit c, float p) { Api.Note($""ice {c.name}""); } }"));
            var engine = Load(r);
            var hero = new Unit { Name = "Hero" };

            // горячий путь: строка → int один раз, дальше чистая индексация
            int fireball = engine.ResolveArchetype("spell", "fireball");
            Assert.GreaterOrEqual(fireball, 0);
            _onCast.Raise(engine, fireball, hero, 5f);

            // холодный путь: перегрузка со строкой; неизвестный id — тишина
            _onCast.Raise(engine, "icebolt", hero, 1f);
            _onCast.Raise(engine, "unknown_id", hero, 1f);
            _onObtain.Raise(engine, fireball, hero); // OnObtain у fireball не объявлен — тишина

            Assert.AreEqual(new[] { "fire Hero 5", "ice Hero" }, _log);
        }

        [Test]
        public void Override_MergesPerEvent_LaterWins()
        {
            // база объявляет обе механики; мод переопределяет ТОЛЬКО OnCast
            var baseMod = Mod("base", @"
                spell fireball {
                    event OnCast(Unit c, float p) { Api.Note(""base cast""); }
                    event OnObtain(Unit u) { Api.Note(""base obtain""); }
                }");
            var patch = Mod("patch", @"
                spell fireball {
                    event OnCast(Unit c, float p) { Api.Note(""patch cast""); }
                }", "base");

            var engine = Load(Compile(baseMod, patch));
            var hero = new Unit { Name = "H" };
            int fb = engine.ResolveArchetype("spell", "fireball");

            _onCast.Raise(engine, fb, hero, 1f);   // переопределён патчем
            _onObtain.Raise(engine, fb, hero);     // остался от базы

            Assert.AreEqual(new[] { "patch cast", "base obtain" }, _log);
        }

        [Test]
        public void Override_KillImplementation_ViaPrototypeAndPass()
        {
            var baseMod = Mod("base", @"
                spell fireball {
                    event OnCast(Unit c, float p) { Api.Note(""cast""); }
                    event OnObtain(Unit u) { Api.Note(""obtain""); }
                }");
            // убить OnCast прототипом, OnObtain — телом с pass
            var patch = Mod("patch", @"
                spell fireball {
                    event OnCast(Unit c, float p);
                    event OnObtain(Unit u) { pass; }
                }", "base");

            var engine = Load(Compile(baseMod, patch));
            int fb = engine.ResolveArchetype("spell", "fireball");
            _onCast.Raise(engine, fb, new Unit(), 1f);
            _onObtain.Raise(engine, fb, new Unit());

            Assert.AreEqual(0, _log.Count, "обе реализации убиты переопределением");
        }

        [Test]
        public void Fields_AreStatic_PerBlock()
        {
            var r = Compile(Mod("game", @"
                spell fireball {
                    int casts = 0;
                    event OnCast(Unit c, float p) { casts = casts + 1; Api.Note($""{casts}""); }
                }"));
            var engine = Load(r);
            int fb = engine.ResolveArchetype("spell", "fireball");
            var u = new Unit();
            _onCast.Raise(engine, fb, u, 1f);
            _onCast.Raise(engine, fb, u, 1f);
            Assert.AreEqual(new[] { "1", "2" }, _log, "поле — статик, живёт между вызовами");
        }

        [Test]
        public void StringId_Works()
        {
            var r = Compile(Mod("game", @"
                spell ""fire-ball.v2"" { event OnCast(Unit c, float p) { Api.Note(""v2""); } }"));
            var engine = Load(r);
            Assert.IsTrue(engine.HasArchetype("spell", "fire-ball.v2"));
            _onCast.Raise(engine, "fire-ball.v2", new Unit(), 1f);
            Assert.AreEqual(new[] { "v2" }, _log);
        }

        [Test]
        public void CollectorApi_HasAndIds()
        {
            var r = Compile(Mod("game", @"
                spell fireball { event OnObtain(Unit u) { } }
                spell icebolt  { event OnObtain(Unit u) { } }"));
            var engine = Load(r);

            Assert.IsTrue(engine.HasArchetype("spell", "fireball"));
            Assert.IsFalse(engine.HasArchetype("spell", "meteor"));
            Assert.IsFalse(engine.HasArchetype("item", "fireball"), "вид не объявлен — false, не исключение");

            var ids = new List<string>();
            engine.GetArchetypeIds("spell", ids);
            Assert.AreEqual(new[] { "fireball", "icebolt" }, ids);
        }

        [Test]
        public void KnownIds_CatchTypos()
        {
            var host = new HostBuilder();
            host.Api("Api").Act("Noop", () => { });
            host.Archetype("spell").KnownIds("fireball", "icebolt")
                .Event<float>("OnCast");
            var set = Mod("game", @"spell firebal { event OnCast(float p) { } }"); // опечатка
            var r = ScriptCompiler.Compile(host.Registry, 1, new List<ModuleSourceSet> { set });
            Assert.IsFalse(r.Success);
            Assert.IsTrue(Has(r, "E0202"), Dump(r));
        }

        [Test]
        public void Prototype_NonVoidFunc_IsError()
        {
            var r = Compile(Mod("game", @"
                class Util { func Answer() -> int; }"));
            Assert.IsTrue(Has(r, "E0203"), Dump(r));
        }

        [Test]
        public void Errors_UnknownKind_UnknownEvent_BadSignature()
        {
            var r1 = Compile(Mod("game", @"artifact sword { event OnPick(Unit u) { } }"));
            Assert.IsTrue(Has(r1, "E0198"), Dump(r1));

            var r2 = Compile(Mod("game", @"spell fb { event OnExplode(Unit u) { } }"));
            Assert.IsTrue(Has(r2, "E0199"), Dump(r2));

            var r3 = Compile(Mod("game", @"spell fb { event OnCast(Unit c) { } }")); // мало параметров
            Assert.IsTrue(Has(r3, "E0200"), Dump(r3));

            var r4 = Compile(Mod("game", @"spell fb { event OnCast(Unit c, int p) { } }")); // не тот тип
            Assert.IsTrue(Has(r4, "E0201"), Dump(r4));

            var r5 = Compile(Mod("game", @"spell fb { int x = 0; }")); // без событий
            Assert.IsTrue(Has(r5, "E0199"), Dump(r5));
        }

        [Test]
        public void WaitInsideArchetype_WorksLikeTrigger()
        {
            var r = Compile(Mod("game", @"
                spell slowburn {
                    event OnCast(Unit c, float p) {
                        Api.Note(""start"");
                        wait 1.0;
                        Api.Note(""end"");
                    }
                }"));
            var engine = Load(r);
            int sb = engine.ResolveArchetype("spell", "slowburn");
            _onCast.Raise(engine, sb, new Unit(), 1f);
            Assert.AreEqual(new[] { "start" }, _log, "до wait — немедленно");
            engine.Tick(1.1f);
            Assert.AreEqual(new[] { "start", "end" }, _log);
        }
    }
}
