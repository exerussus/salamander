using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dsl.Compilation;
using Dsl.Ide;
using Dsl.Semantics;
using Dsl.Tooling;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Логика IDE, не зависящая от UI: команды редактора, подсветка, история,
    /// документ, воркспейсы, насос компиляции. То, что ломалось в старой
    /// странице (Tab удалял выделение, ложные ошибки из-за компиляции файла в
    /// одиночку, потеря правок), закреплено здесь регрессиями.
    /// </summary>
    public sealed class IdeTests
    {
        // ───────────────────────── команды редактора ─────────────────────────

        [Test]
        public void Tab_OnMultilineSelection_IndentsInsteadOfDeleting()
        {
            string t = "a\nb\nc";
            var r = EditCommands.Indent(t, 0, 3, outdent: false).Value; // выделены "a\nb"
            Assert.AreEqual("    a\n    b\nc", r.Text);
            Assert.AreEqual(4, r.Anchor);                                // край выделения сдвинулся с первой строкой
            Assert.AreEqual(3 + 8, r.Caret);
        }

        [Test]
        public void ShiftTab_Outdents_AndTabWithoutSelection_PadsToTabStop()
        {
            string t = "    a\n  b";
            var r = EditCommands.Indent(t, 0, t.Length, outdent: true).Value;
            Assert.AreEqual("a\nb", r.Text);

            var ins = EditCommands.Indent("ab", 1, 1, outdent: false).Value;
            Assert.AreEqual("a   b", ins.Text); // до позиции табуляции 4
            Assert.AreEqual(4, ins.Caret);
        }

        [Test]
        public void ToggleComment_AddsAtCommonIndent_AndRemoves()
        {
            string t = "    x = 1;\n        y = 2;";
            var on = EditCommands.ToggleComment(t, 0, t.Length);
            Assert.AreEqual("    // x = 1;\n    //     y = 2;", on.Text);
            var off = EditCommands.ToggleComment(on.Text, on.Anchor, on.Caret);
            Assert.AreEqual(t, off.Text);
        }

        [Test]
        public void Enter_BetweenBraces_ExpandsBlock()
        {
            string t = "    if (x) {}";
            int caret = t.IndexOf('}');
            var r = EditCommands.SmartNewline(t, caret, caret);
            Assert.AreEqual("    if (x) {\n        \n    }", r.Text);
            Assert.AreEqual("    if (x) {\n        ".Length, r.Caret);
        }

        [Test]
        public void Backspace_RemovesAutoPair_AndIndentUnit()
        {
            var r = EditCommands.SmartBackspace("f()", 2, 2).Value;
            Assert.AreEqual("f", r.Text);
            var ind = EditCommands.SmartBackspace("x       ", 8, 8);
            Assert.IsNull(ind, "пробелы после текста — обычный Backspace");
            var ws = EditCommands.SmartBackspace("        x", 8, 8).Value;
            Assert.AreEqual("    x", ws.Text, "в отступе — до предыдущей позиции табуляции");
        }

        [Test]
        public void TypeChar_Pairs_SkipsClosing_WrapsSelection_Dedents()
        {
            Assert.AreEqual("()", EditCommands.TypeChar("", 0, 0, '(', false).Value.Text);
            var skip = EditCommands.TypeChar("()", 1, 1, ')', false).Value;
            Assert.AreEqual("()", skip.Text);
            Assert.AreEqual(2, skip.Caret);
            Assert.IsNull(EditCommands.TypeChar("x", 0, 0, '(', false), "перед словом пару не ставим");
            Assert.IsNull(EditCommands.TypeChar("", 0, 0, '"', true), "в строке — обычная кавычка");
            var wrap = EditCommands.TypeChar("abc", 0, 3, '(', false).Value;
            Assert.AreEqual("(abc)", wrap.Text);
            var dedent = EditCommands.TypeChar("{\n        ", 10, 10, '}', false).Value;
            Assert.AreEqual("{\n    }", dedent.Text);
        }

        [Test]
        public void Lines_MoveDuplicateDelete()
        {
            string t = "a\nb\nc";
            Assert.AreEqual("b\na\nc", EditCommands.MoveLines(t, 2, 2, -1).Value.Text);
            Assert.AreEqual("b\na\nc", EditCommands.MoveLines(t, 0, 0, 1).Value.Text);
            Assert.IsNull(EditCommands.MoveLines(t, 0, 0, -1));
            Assert.AreEqual("a\nb\nb\nc", EditCommands.Duplicate(t, 2, 2).Text);
            Assert.AreEqual("a\nc", EditCommands.DeleteLines(t, 2, 2).Text);
            Assert.AreEqual("a\nb", EditCommands.DeleteLines(t, 4, 4).Text);
        }

        [Test]
        public void MatchBracket_IgnoresStringsAndComments()
        {
            string t = "f(\")\" /* ) */, g())";
            int m = EditCommands.MatchBracket(t, 1, out int at);
            Assert.AreEqual(1, at);
            Assert.AreEqual(t.Length - 1, m);
        }

        // ───────────────────────── подсветка ─────────────────────────

        [Test]
        public void Highlighter_BlockCommentState_CarriesAcrossLines_AndIncrementalMatchesFull()
        {
            var inc = new LineHighlighter();
            string a = "x = 1;\n/* start\nstill\nend */ y = 2;\n";
            string first = inc.Build(a);
            string b = a.Replace("x = 1;", "x = 10;");
            string incremental = inc.Build(b);
            string full = new LineHighlighter().Build(b);
            Assert.AreEqual(full, incremental);
            Assert.IsFalse(first == incremental);

            // закрыть комментарий раньше — следующие строки перекрашиваются
            string c = b.Replace("/* start", "/* start */");
            Assert.AreEqual(new LineHighlighter().Build(c), inc.Build(c));
        }

        [Test]
        public void Highlighter_EscapesAngleBrackets_AndColorsInterpolationHoles()
        {
            var spans = new List<LineHighlighter.Span>();
            LineHighlighter.TokenizeLine("s = $\"hp {u.health} of {max}\";", false, null, spans);
            bool holeProperty = false;
            foreach (var sp in spans)
                if (sp.Color == SalColor.Property) holeProperty = true;
            Assert.IsTrue(holeProperty, "u.health внутри {…} — код");

            string rich = new LineHighlighter().Build("if (a < b) {}");
            StringAssert.Contains("<noparse><</noparse>", rich);
        }

        [Test]
        public void CommentOrString_KnowsBlockCommentsAndHoles()
        {
            Assert.IsTrue(LineHighlighter.IsInCommentOrString("still inside", 3, startsInComment: true));
            Assert.IsFalse(LineHighlighter.IsInCommentOrString("*/ code", 5, startsInComment: true));
            Assert.IsFalse(LineHighlighter.IsInCommentOrString("$\"a {x.", 7, false), "дырка интерполяции — код");
            Assert.IsTrue(LineHighlighter.IsInCommentOrString("\"text", 3, false));
        }

        // ───────────────────────── документ и история ─────────────────────────

        [Test]
        public void Document_KeepsCrlf_OnSave_AndTracksDirty()
        {
            var d = new IdeDocument("k", "a.sal");
            d.LoadFromDisk("a\r\nb\r\n", FileStamp.None);
            Assert.AreEqual("a\nb\n", d.Text);
            Assert.IsFalse(d.Dirty);
            d.SetText("a\nc\n");
            Assert.IsTrue(d.Dirty);
            Assert.AreEqual("a\r\nc\r\n", d.TextForDisk());
            d.SetText("a\nb\n");
            Assert.IsFalse(d.Dirty, "вернули как было — не изменён");
        }

        [Test]
        public void Undo_CoalescesTyping_BreaksOnCommand()
        {
            var h = new UndoHistory();
            h.RecordEdit("", 0, 1, 0.0);
            h.RecordEdit("a", 1, 1, 0.1);
            h.RecordEdit("ab", 2, 1, 0.2);               // слитный набор — один шаг
            h.RecordEdit("abc", 3, 4, 0.3, atomic: true); // команда — свой шаг
            Assert.IsTrue(h.TryUndo("abc    ", 7, out var e1));
            Assert.AreEqual("abc", e1.Text);
            Assert.IsTrue(h.TryUndo("abc", 3, out var e2));
            Assert.AreEqual("", e2.Text);
            Assert.IsTrue(h.TryRedo("", 0, out var r1));
            Assert.AreEqual("abc", r1.Text);
        }

        [Test]
        public void SessionHash_IsStable()
        {
            Assert.AreEqual(IdeSession.Hash("abc"), IdeSession.Hash("abc"));
            Assert.IsFalse(IdeSession.Hash("abc") == IdeSession.Hash("abd"));
            StringAssert.StartsWith("buffers/", IdeSession.BufferKey("C:/x.sal"));
        }

        // ───────────────────────── воркспейсы ─────────────────────────

        [Test]
        public void FileWorkspace_CreateFile_RegistersInManifest()
        {
            string root = Path.Combine(Path.GetTempPath(), "sal-ide-" + Guid.NewGuid().ToString("N"));
            try
            {
                string mod = Path.Combine(root, "core");
                Directory.CreateDirectory(Path.Combine(mod, "src"));
                File.WriteAllText(Path.Combine(mod, "module.json"), "{ \"name\": \"core\", \"apiVersion\": 1, \"sources\": [\"src/a.sal\"] }");
                File.WriteAllText(Path.Combine(mod, "src", "a.sal"), "class A { }\n");

                using (var ws = new FileSystemWorkspace(root))
                {
                    var snap = ws.Load();
                    Assert.AreEqual(1, snap.Modules.Count);
                    string key = ws.CreateFile("core", "src/b", out string err);
                    Assert.IsNull(err);
                    Assert.IsTrue(File.Exists(key));
                    StringAssert.Contains("src/b.sal", File.ReadAllText(Path.Combine(mod, "module.json")));
                    Assert.AreEqual(2, ws.Load().Modules[0].Files.Count);
                    Assert.IsNull(ws.CreateFile("core", "../escape.sal", out err));
                }

                // глобы ModuleLoader не раскрывает, поэтому маска в списке файл не
                // подхватит — дописываем путь явно, в тот же ключ ("scripts")
                string m2 = Path.Combine(root, "m2");
                Directory.CreateDirectory(m2);
                string mani = Path.Combine(m2, "module.json");
                File.WriteAllText(mani, "{ \"name\": \"x\", \"scripts\": [\"**\"] }");
                Assert.IsTrue(ModuleJsonReader.Instance.AddFile(m2, "src/c.sal", out _));
                string after = File.ReadAllText(mani);
                StringAssert.Contains("src/c.sal", after);
                StringAssert.Contains("**", after);
                Assert.IsTrue(ModuleJsonReader.Instance.AddFile(m2, "src/c.sal", out _));
                Assert.AreEqual(after, File.ReadAllText(mani)); // повторно — не дублируем
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Test]
        public void ModuleReader_ListsUnlisted_AndSaysNothingOnMasks()
        {
            string root = Path.Combine(Path.GetTempPath(), "sal-rd-" + Guid.NewGuid().ToString("N"));
            try
            {
                string mod = Path.Combine(root, "core");
                Directory.CreateDirectory(Path.Combine(mod, "src"));
                File.WriteAllText(Path.Combine(mod, "src", "a.sal"), "");
                File.WriteAllText(Path.Combine(mod, "src", "b.sal"), "");
                string mani = Path.Combine(mod, "module.json");

                File.WriteAllText(mani, "{ \"name\": \"core\", \"apiVersion\": 1, \"sources\": [\"src/a.sal\"] }");
                var unlisted = new List<string>(ModuleJsonReader.Instance.ListUnlisted(mod));
                Assert.AreEqual(1, unlisted.Count);
                StringAssert.Contains("b.sal", unlisted[0]);

                // маска: «вне списка» ничего не значит — молчим, а не объявляем всё лишним
                File.WriteAllText(mani, "{ \"name\": \"core\", \"apiVersion\": 1, \"scripts\": [\"src/*.sal\"] }");
                Assert.AreEqual(0, new List<string>(ModuleJsonReader.Instance.ListUnlisted(mod)).Count);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Test]
        public void CustomReader_MakesWorkspaceSeeAnotherPackFormat()
        {
            string root = Path.Combine(Path.GetTempPath(), "sal-pack-" + Guid.NewGuid().ToString("N"));
            try
            {
                string pack = Path.Combine(root, "pack");
                Directory.CreateDirectory(pack);
                File.WriteAllText(Path.Combine(pack, "pack.json"), "{}");
                File.WriteAllText(Path.Combine(pack, "main.sal"), "class A { }\n");

                // читателем по умолчанию модуля тут нет: манифест называется иначе
                Assert.AreEqual(0, WorkspaceLoader.Load(root).Modules.Count);

                var reader = new PackReader();
                var ws = WorkspaceLoader.Load(root, null, reader);
                Assert.AreEqual(1, ws.Modules.Count);
                Assert.AreEqual("pack", ws.Modules[0].Manifest.Name);
                Assert.AreEqual(1, ws.Modules[0].Files.Count);
                Assert.IsTrue(ws.ModuleDirs.ContainsKey("pack"));
                Assert.IsTrue(ws.LogicalToPath.ContainsKey("pack/main.sal"));

                // тот же читатель — и файловый воркспейс IDE видит ровно то же
                using (var fs = new FileSystemWorkspace(root, null, reader))
                    Assert.AreEqual(1, fs.Load().Modules.Count);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        /// <summary>Пак своего формата: манифест pack.json, исходники — все .sal рядом.</summary>
        private sealed class PackReader : IModuleReader
        {
            private const string Manifest = "pack.json";

            public bool IsModuleDir(string dir) => File.Exists(Path.Combine(dir, Manifest));

            public bool IsManifestFile(string path) =>
                string.Equals(Path.GetFileName(path), Manifest, StringComparison.OrdinalIgnoreCase);

            public ModuleSourceSet ReadModule(string dir, Action<string, string> onError,
                                              Dictionary<string, string> logicalToAbsolute)
            {
                if (!IsModuleDir(dir)) return null;
                string name = Path.GetFileName(dir);
                var set = new ModuleSourceSet { Manifest = new ModuleManifest { Name = name, ApiVersion = 1 } };
                foreach (var f in ModuleLoader.EnumerateFiles(dir, "*.sal", 4))
                {
                    string logical = name + "/" + Path.GetFileName(f);
                    set.Files.Add((logical, File.ReadAllText(f)));
                    if (logicalToAbsolute != null) logicalToAbsolute[logical] = Path.GetFullPath(f);
                }
                return set;
            }

            public IEnumerable<string> ListUnlisted(string dir) => Array.Empty<string>(); // всё .sal уже в паке

            public bool AddFile(string dir, string relativePath, out string warning)
            {
                warning = null;
                return true; // список не ведётся — файл подхватится сам
            }
        }

        [Test]
        public void FileWorkspace_Write_PreservesBom()
        {
            string p = Path.Combine(Path.GetTempPath(), "sal-bom-" + Guid.NewGuid().ToString("N") + ".sal");
            try
            {
                File.WriteAllText(p, "x", new UTF8Encoding(true));
                Assert.IsTrue(FileSystemWorkspace.WriteFile(p, "y", out _));
                var bytes = File.ReadAllBytes(p);
                Assert.AreEqual(0xEF, (int)bytes[0]);
                Assert.AreEqual((byte)'y', bytes[3]);
            }
            finally { try { File.Delete(p); } catch { } }
        }

        [Test]
        public void MemoryWorkspace_Edits_DoNotTouchSourceModules()
        {
            var original = new ModuleSourceSet { Manifest = new ModuleManifest { Name = "m", Sources = new[] { "a.sal" } } };
            original.Files.Add(("m/a.sal", "class A { }"));
            var ws = new MemoryWorkspace(new[] { original });
            string key = ws.NormalizeKey("m/a.sal");
            Assert.IsTrue(ws.Write(key, "class B { }", out _));
            Assert.IsNotNull(ws.CreateFile("m", "b", out _));
            Assert.AreEqual("class A { }", original.Files[0].text);
            Assert.AreEqual(1, original.Manifest.Sources.Length);
            var snap = ws.Snapshot();
            Assert.AreEqual("class B { }", snap[0].Files[0].text);
            Assert.AreEqual(2, snap[0].Files.Count);
        }

        // ───────────────────────── компиляция ─────────────────────────

        [Test]
        public void CompileRunner_Synchronous_DebouncesAndApplies()
        {
            var set = new ModuleSourceSet { Manifest = new ModuleManifest { Name = "m", ApiVersion = 1 } };
            set.Files.Add(("m/a.sal", "class A { func F() -> int { return nope; } }"));
            int snapshots = 0;
            CompileJob applied = null;
            var runner = new CompileRunner
            {
                UseThreads = false,
                Snapshot = () =>
                {
                    snapshots++;
                    return new CompileJob { Registry = new HostRegistry(), ApiVersion = 1, Modules = new List<ModuleSourceSet> { set } };
                },
                Apply = j => applied = j,
            };
            runner.Request(0.0);
            runner.Request(0.1);             // сдвигает дедлайн
            runner.Tick(0.2);                // ещё рано
            Assert.AreEqual(0, snapshots);
            runner.Tick(1.0);
            Assert.AreEqual(1, snapshots);
            Assert.IsNotNull(applied);
            Assert.IsFalse(applied.Result.Success);
            runner.Tick(2.0);                // запросов нет — ничего
            Assert.AreEqual(1, snapshots);
        }

        // ───────────────────────── «Применить в игре» ────────────────────────

        [Test]
        public void MergeModules_OverridesByName_KeepsOrder_AddsNew()
        {
            var game = new List<ModuleSourceSet> { Mod("core", "old"), Mod("quests", "old") };
            var ide = new List<ModuleSourceSet> { Mod("quests", "new"), Mod("extra", "new") };

            var merged = ApplySupport.Merge(game, ide);

            Assert.AreEqual(3, merged.Count);
            Assert.AreEqual("core", merged[0].Manifest.Name);
            Assert.AreEqual("quests", merged[1].Manifest.Name);
            Assert.AreEqual("extra", merged[2].Manifest.Name);
            Assert.AreEqual("old", merged[0].Files[0].text);   // чужие модули игры не тронуты
            Assert.AreEqual("new", merged[1].Files[0].text);   // правка IDE заменила модуль игры
            Assert.AreEqual("new", merged[2].Files[0].text);   // новый модуль дописан в конец
        }

        [Test]
        public void MergeModules_KeepsUnnamedGameModules_AndFiltersAdditions()
        {
            var unnamed = new ModuleSourceSet { Manifest = new ModuleManifest { Name = null, ApiVersion = 1 } };
            var game = new List<ModuleSourceSet> { Mod("core", "old"), unnamed };
            var ide = new List<ModuleSourceSet> { Mod("core", "new"), Mod("sample", "x"), Mod("mymod", "y") };

            // добавить в игру можно только «mymod»: он лежит в папке модулей игры
            var merged = ApplySupport.Merge(game, ide, new HashSet<string>(new[] { "mymod" }));

            Assert.AreEqual(3, merged.Count);
            Assert.AreEqual("new", merged[0].Files[0].text);
            Assert.IsTrue(ReferenceEquals(unnamed, merged[1]), "безымянный модуль игры проходит как есть");
            Assert.AreEqual("mymod", merged[2].Manifest.Name);
        }

        [Test]
        public void MergeModules_WithoutOverrides_ReturnsGameSet()
        {
            var game = new List<ModuleSourceSet> { Mod("core", "old") };
            Assert.IsTrue(ReferenceEquals(game, ApplySupport.Merge(game, null)));
            Assert.AreEqual(1, ApplySupport.Merge(null, new List<ModuleSourceSet> { Mod("a", "x") }).Count);
        }

        private static ModuleSourceSet Mod(string name, string text)
        {
            var m = new ModuleSourceSet { Manifest = new ModuleManifest { Name = name, ApiVersion = 1 } };
            m.Files.Add(($"{name}/main.sal", text));
            return m;
        }
    }
}
