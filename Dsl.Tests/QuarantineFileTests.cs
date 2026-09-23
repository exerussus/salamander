using System.Collections.Generic;
using System.Text;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using Dsl.Text;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Карантин по файлу (<see cref="QuarantineScope.File"/>): при правиле «один
    /// файл — одна сущность» опечатка в одном файле стоит этого файла, а не пака и
    /// всех, кто от пака зависит. Режим задаёт хост, включает карантин вызывающий.
    /// </summary>
    public sealed class QuarantineFileTests
    {
        public sealed class Unit { public string Name = "u"; }

        private HostBuilder _host;
        private EventRef<Unit> _onPing;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _log = new List<string>();
            _host = new HostBuilder();
            _host.Class<Unit>().Prop("name", u => u.Name);
            _host.Api("Api").Act("Note", (string s) => _log.Add(s));
            _onPing = _host.Event<Unit>("OnPing");
        }

        // ===== харнесс =====================================================

        private static ModuleSourceSet Mod(string name, string[] deps, params (string file, string text)[] files)
        {
            var sources = new string[files.Length];
            for (int i = 0; i < files.Length; i++) sources[i] = files[i].file;
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest
                {
                    Name = name, ApiVersion = 1,
                    Dependencies = deps ?? System.Array.Empty<string>(),
                    Sources = sources,
                },
            };
            foreach (var (file, text) in files) set.Files.Add((name + "/" + file, text));
            return set;
        }

        private CompilationResult Quarantined(params ModuleSourceSet[] mods) =>
            ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet>(mods), quarantineBrokenModules: true);

        private ScriptEngine Load(CompilationResult r)
        {
            Assert.IsTrue(r.Success, Dump(r));
            var e = new ScriptEngine(_host.Registry);
            e.LoadProgram(r.Program);
            return e;
        }

        private static string Dump(CompilationResult r)
        {
            var sb = new StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            foreach (var m in r.Excluded) sb.AppendLine("module " + m);
            foreach (var f in r.ExcludedFiles) sb.AppendLine("file " + f);
            return sb.ToString();
        }

        private static List<string> ExcludedFileNames(CompilationResult r)
        {
            var list = new List<string>();
            foreach (var f in r.ExcludedFiles) list.Add(f.File);
            return list;
        }

        private static bool HasErrorIn(CompilationResult r, string file)
        {
            foreach (var d in r.Diagnostics)
                if (d.Severity == Severity.Error && d.File == file) return true;
            return false;
        }

        private static bool HasCode(CompilationResult r, string code)
        {
            foreach (var d in r.Diagnostics)
                if (d.Code == code) return true;
            return false;
        }

        // Сценарий из просьбы: в a — цел u, синтаксис сломан в v, неизвестное имя в w;
        // b зависит от a: x зовёт U, z зовёт V.
        private static ModuleSourceSet ModA() => Mod("a", null,
            ("u.sal", @"class U { func One() -> int { return 1; } }"),
            ("v.sal", @"class V { func Two() -> int { return 2 } }"),
            ("w.sal", @"trigger W { event OnPing(Unit u) { Api.Note($""w{Nope.X()}""); } }"));

        private static ModuleSourceSet ModB() => Mod("b", new[] { "a" },
            ("x.sal", @"trigger X { event OnPing(Unit u) { Api.Note($""x{U.One()}""); } }"),
            ("z.sal", @"trigger Z { event OnPing(Unit u) { Api.Note($""z{V.Two()}""); } }"));

        // ===== опция =======================================================

        [Test]
        public void Option_DefaultsToModule_AndBuilderSetsIt()
        {
            Assert.AreEqual(QuarantineScope.Module, new HostRegistry().Quarantine);
            Assert.AreSame(_host, _host.Quarantine(QuarantineScope.File));
            Assert.AreEqual(QuarantineScope.File, _host.Registry.Quarantine);
        }

        // ===== режим File ==================================================

        [Test]
        public void FileScope_BrokenFileCostsTheFile_NotTheModule()
        {
            _host.Quarantine(QuarantineScope.File);

            var r = Quarantined(ModA(), ModB());

            Assert.IsTrue(r.Success, Dump(r));
            Assert.AreEqual(0, r.Excluded.Count, "ни один модуль не исключён: " + Dump(r));
            CollectionAssert.AreEqual(new[] { "a/v.sal", "a/w.sal", "b/z.sal" }, ExcludedFileNames(r), Dump(r));
            Assert.AreEqual("a", r.ExcludedFiles[0].Module);
            Assert.AreEqual("b", r.ExcludedFiles[2].Module);
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, r.Program.Modules);

            // уцелевшие файлы работают: x зовёт U из a
            var e = Load(r);
            _onPing.Raise(e, new Unit());
            CollectionAssert.AreEqual(new[] { "x1" }, _log);
        }

        [Test]
        public void FileScope_ErrorsOfDroppedFilesSurviveInDiagnostics()
        {
            _host.Quarantine(QuarantineScope.File);

            var r = Quarantined(ModA(), ModB());

            Assert.IsTrue(r.Success, Dump(r));
            foreach (var file in new[] { "a/v.sal", "a/w.sal", "b/z.sal" })
                Assert.IsTrue(HasErrorIn(r, file), $"ошибка {file} потерялась:\n" + Dump(r));
            foreach (var d in r.Diagnostics)
                if (d.Severity == Severity.Error)
                {
                    Assert.IsFalse(string.IsNullOrEmpty(d.Code), d.ToString());
                    Assert.IsFalse(string.IsNullOrEmpty(d.Message), d.ToString());
                    Assert.Greater(d.Line, 0, d.ToString());
                }
            StringAssert.Contains("a/v.sal:", r.ExcludedFiles[0].Reason);
            StringAssert.StartsWith("ошибка компиляции E", r.ExcludedFiles[0].Reason);
        }

        [Test]
        public void FileScope_ModuleLevelTroubleStillExcludesModuleWithDependents()
        {
            _host.Quarantine(QuarantineScope.File);
            var good = Mod("base", null, ("t.sal", @"trigger B { event OnPing(Unit u) { Api.Note(""base""); } }"));
            var orphan = Mod("orphan", new[] { "missing" }, ("t.sal", @"trigger O { event OnPing(Unit u) { } }"));
            var addon = Mod("addon", new[] { "orphan" }, ("t.sal", @"trigger A { event OnPing(Unit u) { } }"));
            var old = Mod("legacy", null, ("t.sal", @"trigger L { event OnPing(Unit u) { } }"));
            old.Manifest.ApiVersion = 0;

            var r = Quarantined(good, orphan, addon, old);

            Assert.IsTrue(r.Success, Dump(r));
            var names = new List<string>();
            foreach (var ex in r.Excluded) names.Add(ex.Name);
            CollectionAssert.AreEquivalent(new[] { "orphan", "addon", "legacy" }, names);
            Assert.AreEqual(0, r.ExcludedFiles.Count, Dump(r));
            CollectionAssert.AreEqual(new[] { "base" }, r.Program.Modules);
        }

        [Test]
        public void FileScope_ModuleThatLostAllFiles_StaysAlive_NoW0300()
        {
            _host.Quarantine(QuarantineScope.File);
            var lib = Mod("lib", null, ("only.sal", @"class L { func F() -> int { return 1 } }"));
            var user = Mod("user", new[] { "lib" }, ("t.sal", @"trigger T { event OnPing(Unit u) { Api.Note(""user""); } }"));

            var r = Quarantined(lib, user);

            Assert.IsTrue(r.Success, Dump(r));
            Assert.AreEqual(0, r.Excluded.Count, Dump(r));
            CollectionAssert.AreEqual(new[] { "lib/only.sal" }, ExcludedFileNames(r));
            CollectionAssert.AreEquivalent(new[] { "lib", "user" }, r.Program.Modules);
            Assert.IsFalse(HasCode(r, "W0300"), "исходники были, их снял карантин:\n" + Dump(r));

            var e = Load(r);
            _onPing.Raise(e, new Unit());
            CollectionAssert.AreEqual(new[] { "user" }, _log);
        }

        [Test]
        public void FileScope_EmptyManifest_StillWarnsW0300()
        {
            _host.Quarantine(QuarantineScope.File);
            var empty = Mod("empty", null);

            var r = Quarantined(empty);

            Assert.IsTrue(r.Success, Dump(r));
            Assert.IsTrue(HasCode(r, "W0300"), Dump(r));
        }

        [Test]
        public void FileScope_DiagnosticOverflow_StillDropsOnlyTheNoisyFile()
        {
            _host.Quarantine(QuarantineScope.File);
            var noisy = new StringBuilder("trigger N { event OnPing(Unit u) {\n");
            for (int i = 0; i < 700; i++) noisy.Append("Api.Note(nope").Append(i).Append(");\n");
            noisy.Append("} }");
            var mod = Mod("m", null,
                ("noisy.sal", noisy.ToString()),
                ("ok.sal", @"trigger Ok { event OnPing(Unit u) { Api.Note(""ok""); } }"));

            var r = Quarantined(mod);

            Assert.IsTrue(r.Success, Dump(r));
            CollectionAssert.AreEqual(new[] { "m/noisy.sal" }, ExcludedFileNames(r));
            Assert.AreEqual(0, r.Excluded.Count);
            var e = Load(r);
            _onPing.Raise(e, new Unit());
            CollectionAssert.AreEqual(new[] { "ok" }, _log);
        }

        // Цикл конечен на любом входе: каждый следующий файл зовёт класс предыдущего,
        // а первый сломан — файлы выпадают по одному за проход, до последнего.
        [Test, Timeout(20000)]
        public void FileScope_LongCascade_Terminates_AndDropsTheWholeChain()
        {
            _host.Quarantine(QuarantineScope.File);
            const int n = 40;
            var files = new (string, string)[n + 1];
            files[0] = ("c0.sal", @"class C0 { func F() -> int { return 0 } }");
            for (int i = 1; i < n; i++)
                files[i] = ($"c{i}.sal", $"class C{i} {{ func F() -> int {{ return C{i - 1}.F() + 1; }} }}");
            files[n] = ("ok.sal", @"trigger Ok { event OnPing(Unit u) { Api.Note(""ok""); } }");
            var chain = Mod("chain", null, files);
            var tail = Mod("tail", new[] { "chain" },
                ("t.sal", $"trigger T {{ event OnPing(Unit u) {{ Api.Note($\"t{{C{n - 1}.F()}}\"); }} }}"));

            var r = Quarantined(chain, tail);

            Assert.IsTrue(r.Success, Dump(r));
            Assert.AreEqual(n + 1, r.ExcludedFiles.Count, Dump(r));
            for (int i = 0; i < n; i++)
                Assert.AreEqual($"chain/c{i}.sal", r.ExcludedFiles[i].File);
            Assert.AreEqual("tail/t.sal", r.ExcludedFiles[n].File);
            Assert.AreEqual(0, r.Excluded.Count);

            var e = Load(r);
            _onPing.Raise(e, new Unit());
            CollectionAssert.AreEqual(new[] { "ok" }, _log);
        }

        // ===== мерж-семантика ==============================================

        [Test]
        public void FileScope_BrokenPatch_DropsOnlyThePatch_EntityFallsBackToBase()
        {
            _host.Quarantine(QuarantineScope.File);
            var weapon = _host.Archetype("weapon");
            weapon.Event<Unit>("OnHit");
            weapon.Const<float>("damage", required: true);

            var core = Mod("core", null, ("sword.sal", @"
                weapon sword {
                    float damage = 12.0;
                    event OnHit(Unit u) { Api.Note(""hit""); }
                }"));
            var mod = Mod("mod", new[] { "core" }, ("sword.sal", @"weapon sword { float damage = ; }"));

            var r = Quarantined(core, mod);

            Assert.IsTrue(r.Success, Dump(r));
            CollectionAssert.AreEqual(new[] { "mod/sword.sal" }, ExcludedFileNames(r));
            var e = Load(r);
            Assert.IsTrue(e.TryGetArchetypeConst("weapon", "sword", "damage", out var d));
            Assert.AreEqual(12f, d.ToF(), 1e-6f);
        }

        // База сломана, патч цел: патч становится единственным объявлением. Нет
        // обязательной константы — патч выпадает следующим проходом (E0223), и
        // сущности нет целиком, а не «полусущность» из одного патча.
        [Test]
        public void FileScope_BrokenBase_PatchWithoutRequired_IsDroppedNextPass()
        {
            _host.Quarantine(QuarantineScope.File);
            var weapon = _host.Archetype("weapon");
            weapon.Event<Unit>("OnHit");
            weapon.Const<float>("damage", required: true)
                  .Const<int>("windup_ticks");

            var core = Mod("core", null,
                ("sword.sal", @"weapon sword { float damage = 12.0 event OnHit(Unit u) { } }"),
                ("axe.sal", @"weapon axe { float damage = 20.0; event OnHit(Unit u) { Api.Note(""axe""); } }"));
            var mod = Mod("mod", new[] { "core" },
                ("sword.sal", @"weapon sword { int windup_ticks = 3; event OnHit(Unit u) { Api.Note(""mod""); } }"));

            var r = Quarantined(core, mod);

            Assert.IsTrue(r.Success, Dump(r));
            CollectionAssert.AreEqual(new[] { "core/sword.sal", "mod/sword.sal" }, ExcludedFileNames(r));
            Assert.IsTrue(HasCode(r, "E0223"), Dump(r));
            var e = Load(r);
            Assert.IsFalse(e.TryGetArchetypeConst("weapon", "sword", "damage", out _));
            Assert.IsTrue(e.TryGetArchetypeConst("weapon", "axe", "damage", out var axe));
            Assert.AreEqual(20f, axe.ToF(), 1e-6f);
        }

        // ===== режим Module и сборка без карантина не изменились ===========

        [Test]
        public void ModuleScope_Default_ExcludesModuleWithDependents_AsBefore()
        {
            var r = Quarantined(ModA(), ModB(), Mod("base", null, ("t.sal", @"trigger B { event OnPing(Unit u) { } }")));

            Assert.IsTrue(r.Success, Dump(r));
            var names = new List<string>();
            foreach (var ex in r.Excluded) names.Add(ex.Name);
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, names);
            Assert.AreEqual(0, r.ExcludedFiles.Count);
            CollectionAssert.AreEqual(new[] { "base" }, r.Program.Modules);
        }

        [Test]
        public void WithoutQuarantine_FileScopeChangesNothing()
        {
            _host.Quarantine(QuarantineScope.File);

            var r = ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet> { ModA(), ModB() });

            Assert.IsFalse(r.Success);
            Assert.AreEqual(0, r.Excluded.Count);
            Assert.AreEqual(0, r.ExcludedFiles.Count);
            Assert.IsTrue(HasErrorIn(r, "a/v.sal"), Dump(r));
        }
    }
}
