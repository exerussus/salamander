using System;
using System.IO;
using System.Linq;
using Dsl.Compilation;
using Dsl.Tooling;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// salamander-build.json с РАСКРЫТЫМИ модулями: сборщик хоста сам разобрал
    /// свой формат пака и отдал инструменту манифест и файлы. Инструмент
    /// (LSP, чекер, IDE) при этом формата module.json хоста не знает вовсе.
    /// </summary>
    public sealed class BuildFileTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "sal-build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); } catch { /* временная папка — не повод падать */ }
        }

        private string Write(string rel, string text)
        {
            string p = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
            return p;
        }

        private static JObject Module(string name, string dir, params (string logical, string path)[] files)
        {
            var arr = new JArray();
            foreach (var (logical, path) in files) arr.Add(new JObject { ["logical"] = logical, ["path"] = path });
            return new JObject
            {
                ["dir"] = dir,
                ["manifest"] = new JObject
                {
                    ["name"] = name, ["version"] = "1.0.0", ["apiVersion"] = 1,
                    ["dependencies"] = new JArray(), ["execution"] = "cooperative",
                },
                ["files"] = arr,
            };
        }

        [Test]
        public void Expanded_ModulesComeFromBuildFile_NotFromModuleJson()
        {
            // module.json хоста — в формате, которого апстрим не понимает (ключ scripts, маска)
            Write("packs/core/module.json", "{ \"name\": \"core\", \"apiVersion\": 1, \"scripts\": [\"**\"] }");
            string a = Write("packs/core/content/a.sal", "class Util { func F() -> int { return 1; } }\n");
            string b = Write("packs/core/content/b.sal", "class Use { func G() -> int { return Util.F(); } }\n");
            Write("packs/data/module.json", "{ \"name\": \"data\", \"apiVersion\": 1 }");

            var build = new JObject
            {
                ["modules"] = new JArray
                {
                    Module("core", "packs/core", ("core/content/a.sal", "packs/core/content/a.sal"), ("core/content/b.sal", b)),
                    Module("data", "packs/data"),
                },
            };
            string buildPath = Write("salamander-build.json", build.ToString());

            var ws = WorkspaceLoader.Load(_root, buildPath);
            Assert.AreEqual(buildPath, ws.BuildFile);
            Assert.IsEmpty(ws.LoadErrors, string.Join("\n", ws.LoadErrors));
            Assert.AreEqual(2, ws.Modules.Count);
            Assert.AreEqual(new[] { "core/content/a.sal", "core/content/b.sal" }, ws.Modules[0].Files.Select(f => f.name).ToArray());
            Assert.AreEqual("core/content/a.sal", ws.LogicalOf(a), "относительный путь — от папки build-файла");
            Assert.AreEqual("core/content/b.sal", ws.LogicalOf(b), "абсолютный путь — как есть");
            Assert.AreEqual(Path.GetFullPath(Path.Combine(_root, "packs", "core")), ws.ModuleDirs["core"]);
            Assert.IsTrue(ws.Modules[1].EmptyIsIntended, "пустой files — модуль без скриптов намеренно");

            var result = ScriptCompiler.Compile(new Dsl.Semantics.HostRegistry(), 1, WorkspaceCompiler.WithOverlays(ws, null));
            Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics));
            Assert.IsFalse(result.Diagnostics.Any(d => d.Code == "W0300"), "намеренно пустой пак не должен давать W0300");
        }

        [Test]
        public void Expanded_ErrorsInFileReachTheRightPath()
        {
            string bad = Write("p/m/x.sal", "class X { func F() -> int { return 1 } }\n");
            var build = new JObject { ["modules"] = new JArray { Module("m", "p/m", ("m/x.sal", bad)) } };
            string buildPath = Write("salamander-build.json", build.ToString());

            var ws = WorkspaceLoader.Load(_root, buildPath);
            var result = ScriptCompiler.Compile(new Dsl.Semantics.HostRegistry(), 1, WorkspaceCompiler.WithOverlays(ws, null));
            Assert.IsFalse(result.Success);
            var err = result.Diagnostics.First(d => d.Severity == Dsl.Text.Severity.Error);
            Assert.AreEqual("m/x.sal", err.File);
            Assert.AreEqual(bad, ws.LogicalToPath[err.File], "диагностика возвращается в настоящий файл");
        }

        [Test]
        public void Expanded_MissingFile_IsLoadError_ModuleStays()
        {
            string ok = Write("p/m/ok.sal", "class Ok { }\n");
            var build = new JObject
            {
                ["modules"] = new JArray { Module("m", "p/m", ("m/ok.sal", ok), ("m/gone.sal", "p/m/gone.sal")) },
            };
            string buildPath = Write("salamander-build.json", build.ToString());

            var ws = WorkspaceLoader.Load(_root, buildPath);
            Assert.AreEqual(1, ws.Modules.Count);
            Assert.AreEqual(1, ws.Modules[0].Files.Count);
            Assert.IsFalse(ws.Modules[0].EmptyIsIntended);
            Assert.AreEqual(1, ws.LoadErrors.Count);
            StringAssert.EndsWith("gone.sal", ws.LoadErrors[0].Key);
        }

        [Test]
        public void Expanded_WithoutManifestName_IsSkippedWithError()
        {
            var build = new JObject { ["modules"] = new JArray { new JObject { ["dir"] = "x", ["files"] = new JArray() } } };
            string buildPath = Write("salamander-build.json", build.ToString());
            var ws = WorkspaceLoader.Load(_root, buildPath);
            Assert.AreEqual(0, ws.Modules.Count);
            Assert.AreEqual(1, ws.LoadErrors.Count);
        }

        [Test]
        public void StringEntries_StillReadByReader()
        {
            Write("mods/core/module.json", "{ \"name\": \"core\", \"apiVersion\": 1, \"sources\": [\"a.sal\"] }");
            Write("mods/core/a.sal", "class A { }\n");
            string buildPath = Write("salamander-build.json", "{ \"modules\": [\"mods/core\"] }");
            var ws = WorkspaceLoader.Load(_root, buildPath);
            Assert.AreEqual(1, ws.Modules.Count);
            Assert.AreEqual("core/a.sal", ws.Modules[0].Files[0].name);
        }

        [Test]
        public void UnityProject_LibraryBuildFile_FoundFromModulesRoot()
        {
            Directory.CreateDirectory(Path.Combine(_root, "ProjectSettings"));
            string sal = Write("Assets/StreamingAssets/Core/core/content/a.sal", "class A { }\n");
            Write("Assets/StreamingAssets/Core/core/module.json", "{ \"name\": \"core\", \"apiVersion\": 1, \"scripts\": [\"**\"] }");
            var build = new JObject { ["modules"] = new JArray { Module("core", Path.GetDirectoryName(Path.GetDirectoryName(sal)), ("core/content/a.sal", sal)) } };
            string lib = Write("Library/Salamander/salamander-build.json", build.ToString());

            string modulesRoot = Path.Combine(_root, "Assets", "StreamingAssets", "Core");
            Assert.AreEqual(Path.GetFullPath(lib), Path.GetFullPath(WorkspaceLoader.FindBuildFile(modulesRoot)));

            var ws = WorkspaceLoader.Load(modulesRoot);
            Assert.AreEqual(Path.GetFullPath(lib), Path.GetFullPath(ws.BuildFile));
            Assert.AreEqual(1, ws.Modules.Count);
            Assert.AreEqual(1, ws.Modules[0].Files.Count);
        }

        [Test]
        public void UnityProject_WithoutLibraryFile_FallsBackToScan()
        {
            Directory.CreateDirectory(Path.Combine(_root, "ProjectSettings"));
            Write("Assets/StreamingAssets/Core/core/module.json", "{ \"name\": \"core\", \"apiVersion\": 1, \"sources\": [\"a.sal\"] }");
            Write("Assets/StreamingAssets/Core/core/a.sal", "class A { }\n");
            string modulesRoot = Path.Combine(_root, "Assets", "StreamingAssets", "Core");
            Assert.IsNull(WorkspaceLoader.FindBuildFile(modulesRoot));
            var ws = WorkspaceLoader.Load(modulesRoot);
            Assert.IsNull(ws.BuildFile);
            Assert.AreEqual(1, ws.Modules.Count);
        }

        [Test]
        public void BuildFileAtModulesRoot_WinsOverLibrary()
        {
            Directory.CreateDirectory(Path.Combine(_root, "ProjectSettings"));
            Write("Library/Salamander/salamander-build.json", "{ \"modules\": [] }");
            string atRoot = Write("Assets/Mods/salamander-build.json", "{ \"modules\": [] }");
            Assert.AreEqual(atRoot, WorkspaceLoader.FindBuildFile(Path.Combine(_root, "Assets", "Mods")));
        }

        [Test]
        public void EmptyModuleFromModuleJson_StillWarns()
        {
            var set = new ModuleSourceSet { Manifest = new ModuleManifest { Name = "typo", ApiVersion = 1 } };
            var r = ScriptCompiler.Compile(new Dsl.Semantics.HostRegistry(), 1, new System.Collections.Generic.List<ModuleSourceSet> { set });
            Assert.IsTrue(r.Diagnostics.Any(d => d.Code == "W0300"), "опечатку в ключе манифеста W0300 ловит, как раньше");
            var copy = WorkspaceCompiler.Copy(new ModuleSourceSet { Manifest = set.Manifest, EmptyIsIntended = true });
            Assert.IsTrue(copy.EmptyIsIntended, "Copy (оверлеи IDE/LSP) флаг не теряет");
        }
    }
}
