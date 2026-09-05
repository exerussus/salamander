using System.Collections.Generic;
using System.IO;
using Dsl.Compilation;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Поиск модулей и файлов инструментами. Корень задаёт IDE, и в реальном
    /// Unity-проекте это корень ВСЕГО проекта: модули лежат глубоко в
    /// StreamingAssets, а рядом — Library/ и Temp/ с десятками тысяч файлов.
    /// Поэтому обход вглубь обязан быть с ограничением глубины, с пропуском
    /// служебных папок и устойчивым к недоступным каталогам.
    /// </summary>
    public sealed class ModuleLoaderTests
    {
        private string _root;
        private readonly List<string> _errors = new List<string>();

        [SetUp]
        public void SetUp()
        {
            _errors.Clear();
            _root = Path.Combine(Path.GetTempPath(), "sal-loader-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }

        private void Module(string relDir, string name)
        {
            string dir = Path.Combine(_root, relDir.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.Combine(dir, "src"));
            File.WriteAllText(Path.Combine(dir, "module.json"),
                "{\"name\":\"" + name + "\",\"apiVersion\":1,\"sources\":[\"src/a.sal\"]}");
            File.WriteAllText(Path.Combine(dir, "src", "a.sal"), "class C { int x = 1; }");
        }

        private void File_(string rel, string text = "{}")
        {
            string path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        private List<string> Names(List<ModuleSourceSet> sets)
        {
            var names = new List<string>();
            foreach (var s in sets) names.Add(s.Manifest.Name);
            return names;
        }

        private void OnError(string file, string msg) => _errors.Add(file + ": " + msg);

        // ===================================================================

        [Test]
        public void LoadFromFolder_StaysOneLevel_AsBefore()
        {
            // контракт CLI-чекера («папка модулей») не меняем
            Module("direct_child", "direct");
            Module("deep/nested", "deep");

            var sets = ModuleLoader.LoadFromFolder(_root, OnError);
            Assert.AreEqual(new[] { "direct" }, Names(sets));
        }

        [Test]
        public void LoadFromTree_FindsModulesDeepInTheProject()
        {
            // именно эта раскладка и не работала: Unity-проект как корень
            Module("Assets/StreamingAssets/Core/core.weapons", "core.weapons");
            Module("Assets/StreamingAssets/Core/Mods/pack_a", "pack_a");

            // порядок — ординальный по пути ("Mods" < "core.weapons"); итоговый
            // порядок мержа всё равно задаёт топосортировка по dependencies
            var sets = ModuleLoader.LoadFromTree(_root, OnError);
            CollectionAssert.AreEquivalent(new[] { "core.weapons", "pack_a" }, Names(sets));
            Assert.IsEmpty(_errors);
        }

        [Test]
        public void LoadFromTree_SkipsUnityAndBuildJunk()
        {
            Module("Assets/mods/real", "real");
            Module("Library/PackageCache/ghost", "ghost");
            Module("Temp/ghost2", "ghost2");
            Module("obj/ghost3", "ghost3");
            Module(".git/ghost4", "ghost4");
            Module("node_modules/ghost5", "ghost5");

            var sets = ModuleLoader.LoadFromTree(_root, OnError);
            Assert.AreEqual(new[] { "real" }, Names(sets));
        }

        [Test]
        public void LoadFromTree_DoesNotDescendIntoAModule()
        {
            // подпапки модуля — его исходники, а не вложенные модули
            Module("mods/outer", "outer");
            Module("mods/outer/inner", "inner");

            var sets = ModuleLoader.LoadFromTree(_root, OnError);
            Assert.AreEqual(new[] { "outer" }, Names(sets));
        }

        [Test]
        public void LoadFromTree_RespectsDepthLimit()
        {
            Module("a/b/c/deep", "deep");

            Assert.IsEmpty(Names(ModuleLoader.LoadFromTree(_root, OnError, null, maxDepth: 2)));
            Assert.AreEqual(new[] { "deep" }, Names(ModuleLoader.LoadFromTree(_root, OnError, null, maxDepth: 4)));
        }

        [Test]
        public void LoadFromTree_OrderIsDeterministic()
        {
            // порядок модулей задаёт порядок мержа-переопределения — он обязан
            // быть одинаковым от запуска к запуску
            Module("mods/b_mod", "b");
            Module("mods/a_mod", "a");
            Module("other/c_mod", "c");

            var first = Names(ModuleLoader.LoadFromTree(_root, OnError));
            var second = Names(ModuleLoader.LoadFromTree(_root, OnError));
            Assert.AreEqual(first, second);
            Assert.AreEqual(new[] { "a", "b", "c" }, first);
        }

        [Test]
        public void LoadFromTree_MissingRoot_IsEmptyNotThrow()
        {
            var sets = ModuleLoader.LoadFromTree(Path.Combine(_root, "нет-такой-папки"), OnError);
            Assert.IsEmpty(sets);
        }

        // ===================================================================

        [Test]
        public void FindNearestFile_PrefersShallowest()
        {
            File_("deep/deeper/salamander-api.json");
            File_("near/salamander-api.json");

            string found = ModuleLoader.FindNearestFile(_root, "salamander-api.json");
            StringAssert.Contains(Path.Combine("near", "salamander-api.json"), found);
        }

        [Test]
        public void FindNearestFile_ChecksRootItself()
        {
            File_("salamander-api.json");
            Assert.AreEqual(
                Path.Combine(Path.GetFullPath(_root), "salamander-api.json"),
                ModuleLoader.FindNearestFile(_root, "salamander-api.json"));
        }

        [Test]
        public void FindNearestFile_SkipsJunkAndReturnsNullWhenAbsent()
        {
            File_("Library/salamander-api.json");
            Assert.IsNull(ModuleLoader.FindNearestFile(_root, "salamander-api.json"));
        }

        [Test]
        public void EnumerateFiles_SkipsJunkAndIsSorted()
        {
            File_("Assets/mods/m/src/b.sal", "class B {}");
            File_("Assets/mods/m/src/a.sal", "class A {}");
            File_("Library/cached.sal", "class Ghost {}");
            File_("obj/tmp.sal", "class Ghost2 {}");

            var files = ModuleLoader.EnumerateFiles(_root, "*.sal");
            Assert.AreEqual(2, files.Count, string.Join("\n", files));
            StringAssert.EndsWith("a.sal", files[0]);
            StringAssert.EndsWith("b.sal", files[1]);
        }

        [Test]
        public void IsIgnoredDirectory_CoversDotFoldersAndCaseInsensitive()
        {
            Assert.IsTrue(ModuleLoader.IsIgnoredDirectory(".git"));
            Assert.IsTrue(ModuleLoader.IsIgnoredDirectory(".idea"));
            Assert.IsTrue(ModuleLoader.IsIgnoredDirectory("library"));
            Assert.IsTrue(ModuleLoader.IsIgnoredDirectory("OBJ"));
            Assert.IsFalse(ModuleLoader.IsIgnoredDirectory("Assets"));
            Assert.IsFalse(ModuleLoader.IsIgnoredDirectory("core.weapons"));
        }

        [Test]
        public void LoadFromTree_ReportsBrokenManifest_ButKeepsGoing()
        {
            Module("mods/good", "good");
            File_("mods/broken/module.json", "{ это не json");

            var sets = ModuleLoader.LoadFromTree(_root, OnError);
            Assert.AreEqual(new[] { "good" }, Names(sets));
            Assert.AreEqual(1, _errors.Count, string.Join("\n", _errors));
            StringAssert.Contains("broken", _errors[0]);
        }
    }
}
