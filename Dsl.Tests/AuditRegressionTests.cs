using System;
using System.Collections.Generic;
using System.IO;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Регрессии по аудиту. Каждый тест воспроизводит КОНКРЕТНЫЙ дефект, который
    /// раньше вешал процесс, тихо портил состояние или пропускал ошибку. Если
    /// какой-то из них зависнет — значит починка откатилась: тесты с зависанием
    /// помечены Timeout, чтобы прогон падал, а не вис.
    /// </summary>
    public sealed class AuditRegressionTests
    {
        public sealed class Unit { public string Name = "u"; }

        private sealed class NullResolver : ISaveEntityResolver
        {
            public long GetStableId(object entity) => 0L;
            public object ResolveStableId(long id) => null;
        }

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

        private CompilationResult CompileSource(string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "t", ApiVersion = 1, Sources = new[] { "t.sal" } },
            };
            set.Files.Add(("t/t.sal", src));
            return ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet> { set });
        }

        private CompilationResult CompileBody(string body) =>
            CompileSource("trigger T { event OnPing(Unit u) { " + body + " } }");

        private static ModuleSourceSet Mod(string name, string src, params string[] deps)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest
                {
                    Name = name, ApiVersion = 1,
                    Dependencies = deps ?? Array.Empty<string>(),
                    Sources = new[] { "t.sal" },
                },
            };
            set.Files.Add((name + "/t.sal", src));
            return set;
        }

        private static string Dump(CompilationResult r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            return sb.ToString();
        }

        private static bool HasCode(CompilationResult r, string code)
        {
            foreach (var d in r.Diagnostics)
                if (d.Code == code) return true;
            return false;
        }

        private ScriptEngine Load(CompilationResult r, out List<string> errors)
        {
            Assert.IsTrue(r.Success, Dump(r));
            var errs = new List<string>();
            var e = new ScriptEngine(_host.Registry);
            e.OnError += errs.Add;
            e.LoadProgram(r.Program);
            errors = errs;
            return e;
        }

        // ===== C-01: парсер зацикливался на теле объявления ==================

        // Оператор, оставленный в теле триггера мимо event/func, — самая частая
        // опечатка новичка. ParseMember не потреблял ни одного токена и никогда
        // не возвращал null, поэтому цикл членов крутился вечно, набивая
        // DiagnosticBag до OutOfMemory. Вешало редактор Unity, LSP и CI.
        [Test, Timeout(15000)]
        public void StatementInTriggerBody_FailsFast_NotHangs()
        {
            var r = CompileSource(@"trigger T { Engine.Log(""hi""); }");
            Assert.IsFalse(r.Success, "оператор вне event/func — ошибка компиляции");
            Assert.IsTrue(HasCode(r, "E0219"), Dump(r));
        }

        [Test, Timeout(15000)]
        public void GarbageInDeclarationBody_AlwaysTerminates()
        {
            foreach (var src in new[]
                     {
                         "class A { + }",
                         "trigger T { 1 }",
                         "listener L { if }",
                         "trigger T { event OnPing(Unit u) {} ,,, }",
                         "class A { foo",           // недописанный член, как во время набора
                     })
            {
                var r = CompileSource(src);
                Assert.IsFalse(r.Success, "ожидалась ошибка: " + src);
            }
        }

        // Рекурсивный спуск не имел предела глубины: тысячи '(' давали
        // StackOverflowException, а её в .NET поймать нельзя — падает процесс.
        [Test, Timeout(30000)]
        public void DeeplyNestedInput_ReportsDepth_NotStackOverflow()
        {
            var r = CompileSource("trigger T { event OnPing(Unit u) { var x = "
                                  + new string('(', 4000) + "1; } }");
            Assert.IsFalse(r.Success);
            Assert.IsTrue(HasCode(r, "E0220"), Dump(r));
        }

        // Водопад сообщений на битом файле не должен раздувать список без предела.
        [Test, Timeout(15000)]
        public void DiagnosticsAreCapped()
        {
            var sb = new System.Text.StringBuilder("class A {");
            for (int i = 0; i < 300; i++) sb.Append(" + ;");   // ~4 сообщения на каждый мусорный «член»
            sb.Append(" }");

            var r = CompileSource(sb.ToString());
            Assert.IsFalse(r.Success);
            Assert.LessOrEqual(r.Diagnostics.Count, 501, "сообщения обязаны быть ограничены");
            Assert.IsTrue(HasCode(r, "E0221"), "должна быть финальная запись о переполнении");
        }

        // ===== C-02: вложенный Run обнулял бюджет внешнего файбера ===========

        // Engine.Attach прогоняет инициализацию полей подписки через ТОТ ЖЕ Vm.
        // Пока счётчик инструкций был общим полем, каждый вложенный запуск
        // обнулял его — и цикл с Attach внутри не упирался ни в
        // MaxInstructionsPerFiberRun, ни в StuckInstructionLimit. Главный поток
        // висел навсегда.
        [Test, Timeout(30000)]
        public void AttachInTightLoop_IsStillKilledBySpinGuard()
        {
            var e = Load(CompileSource(@"
listener L { int n = 0; event OnPing(Unit u) { n = n + 1; } }
trigger T { event OnPing(Unit u) { while (true) { Engine.Detach(Engine.Attach(L, u)); } } }"),
                out var errors);

            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());   // раньше не возвращалось никогда

            Assert.AreEqual(1, errors.Count, string.Join("\n", errors));
            StringAssert.Contains("цикл без ожидания", errors[0]);
            Assert.AreEqual(0, e.GetStats().LiveFibers, "файбер не остался висеть");
        }

        // ===== C-03: NaN отравлял кучу таймеров ==============================

        // NaN нельзя ни извлечь из мин-кучи (NaN <= time ложно), ни выбрать при
        // просеивании вниз (NaN < x ложно): он оставался там навсегда и, сидя в
        // корне, блокировал пробуждение ВСЕХ файберов.
        [Test]
        public void WaitWithNaN_IsScriptError_AndOtherTimersKeepFiring()
        {
            var e = Load(CompileSource(@"
trigger Bad  { event OnPing(Unit u) { float z = 0.0; wait z / z; } }
trigger Good { event OnPing(Unit u) { wait 1.0; Api.Note(""woke""); } }"),
                out var errors);

            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());

            Assert.AreEqual(1, errors.Count, string.Join("\n", errors));
            StringAssert.Contains("wait", errors[0]);

            e.Tick(1.5f);   // куча не отравлена — обычный wait просыпается
            CollectionAssert.Contains(_log, "woke");
        }

        // ===== C-04: коллекции никогда не освобождаются ======================

        // Потолок работает МЕЖДУ сборками и считает все занятые слоты, включая
        // недостижимые, но ещё не подметённые: сборку нельзя звать изнутри опкода
        // (VM держит вершину стека в локальной переменной, Fiber.Sp там устаревший).
        // Поэтому переполнение — честный отказ, а не тихий рост до OutOfMemory.
        [Test]
        public void CollectionBudget_FailsLoudly()
        {
            var e = Load(CompileBody(@"int i = 0; while (i < 100) { var t = new List<int>(); i = i + 1; }"),
                out var errors);
            e.Collections.MaxLiveCollections = 10;

            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());

            Assert.AreEqual(1, errors.Count, string.Join("\n", errors));
            StringAssert.Contains("лимит коллекций", errors[0]);
        }

        // ===== H-03: свип строк не видел снапшот for-in ======================

        // Элемент, удалённый из исходной коллекции внутри цикла (что язык прямо
        // разрешает), жил только в буфере файбера. Свип освобождал его id, и id
        // переиспользовался под другую строку.
        [Test]
        public void IterationSnapshot_SurvivesStringSweep()
        {
            var e = Load(CompileSource(@"
class S { List<string> loot = new List<string>(); }
trigger T
{
    event OnPing(Unit u)
    {
        S.loot.Add(""alpha"" + ""1"");
        S.loot.Add(""beta"" + ""2"");
        for item in S.loot { S.loot.Clear(); wait 0.1; Api.Note(item); }
    }
}"), out var errors);

            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());   // дошли до первого wait

            e.CollectStrings();             // "beta2" жив ТОЛЬКО в снапшоте цикла
            for (int i = 0; i < 5; i++) e.Tick(0.2f);

            Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
            CollectionAssert.Contains(_log, "alpha1");
            CollectionAssert.Contains(_log, "beta2");
        }

        // ===== H-04: LoadState доверял содержимому сейва =====================

        [Test]
        public void TruncatedSave_ThrowsSaveStateException_AndLeavesEngineClean()
        {
            var e = Load(CompileBody(@"wait 5.0; Api.Note(""late"");"), out _);
            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());

            var res = new NullResolver();
            byte[] good = e.SaveState(res);
            Assert.Greater(good.Length, 32);

            var truncated = new byte[good.Length / 2];
            Array.Copy(good, truncated, truncated.Length);

            Assert.Throws<SaveStateException>(() => e.LoadState(truncated, res));
            Assert.AreEqual(0, e.GetStats().LiveFibers, "после сбоя движок обязан быть пуст, а не полуразобран");
        }

        // ===== H-05: path traversal через module.json ========================

        [Test]
        public void ManifestSourceOutsideModuleFolder_IsRejected()
        {
            string root = Path.Combine(Path.GetTempPath(), "sal_audit_" + Guid.NewGuid().ToString("N"));
            string mod = Path.Combine(root, "mod");
            Directory.CreateDirectory(mod);
            try
            {
                File.WriteAllText(Path.Combine(root, "secret.txt"), "SECRET_MARKER");
                File.WriteAllText(Path.Combine(mod, "module.json"),
                    "{\"name\":\"m\",\"apiVersion\":1,\"sources\":[\"../secret.txt\"]}");

                var errs = new List<string>();
                var set = ModuleLoader.LoadModuleDir(mod, (file, msg) => errs.Add(msg));

                Assert.IsNotNull(set);
                Assert.AreEqual(0, set.Files.Count, "файл за пределами папки модуля читаться не должен");
                Assert.AreEqual(1, errs.Count, string.Join("\n", errs));
                StringAssert.Contains("за пределы папки модуля", errs[0]);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void NullManifest_IsReportedNotThrown()
        {
            string mod = Path.Combine(Path.GetTempPath(), "sal_audit_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mod);
            try
            {
                File.WriteAllText(Path.Combine(mod, "module.json"), "null");
                var errs = new List<string>();
                var set = ModuleLoader.LoadModuleDir(mod, (file, msg) => errs.Add(msg));
                Assert.IsNull(set);
                Assert.AreEqual(1, errs.Count);
            }
            finally
            {
                Directory.Delete(mod, true);
            }
        }

        // ===== H-06: повторная регистрация типа схлопывала id ================

        [Test]
        public void DuplicateTypeRegistration_ThrowsInsteadOfCollidingIds()
        {
            var h = new HostBuilder();
            h.Class<Unit>();
            Assert.Throws<InvalidOperationException>(() => h.Class<Unit>());
        }

        // ===== H-07: не все пути возвращают значение =========================

        [Test]
        public void NonVoidFuncWithoutReturnOnAllPaths_IsError()
        {
            var r = CompileSource(@"
class C { func F(int x) -> int { if (x > 0) { return 1; } } }
trigger T { event OnPing(Unit u) { Api.Note(""x""); } }");
            Assert.IsFalse(r.Success);
            Assert.IsTrue(HasCode(r, "E0222"), Dump(r));
        }

        [Test]
        public void NonVoidFuncWithReturnOnAllPaths_Compiles()
        {
            var r = CompileSource(@"
class C
{
    func Branchy(int x) -> int { if (x > 0) { return 1; } else { return 2; } }
    func Endless() -> int { while (true) { } }
    func Straight() -> int { return 7; }
}
trigger T { event OnPing(Unit u) { Api.Note(""x""); } }");
            Assert.IsTrue(r.Success, Dump(r));
        }

        // ===== M-01: рекурсия упиралась в память, а не в лимит ===============

        [Test, Timeout(30000)]
        public void InfiniteRecursion_IsScriptError_NotOutOfMemory()
        {
            var e = Load(CompileSource(@"
class R { func Go(int n) -> int { return Go(n + 1); } }
trigger T { event OnPing(Unit u) { Api.Note($""{R.Go(0)}""); } }"), out var errors);

            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());

            Assert.AreEqual(1, errors.Count, string.Join("\n", errors));
            StringAssert.Contains("вложенность вызовов", errors[0]);
        }

        // ===== M-10: const int молча обрезался, const double не поддерживался =

        [Test]
        public void ConstIntOutOfRange_IsError()
        {
            var r = CompileSource(@"
class C { const int BIG = 5000000000; }
trigger T { event OnPing(Unit u) { Api.Note(""x""); } }");
            Assert.IsFalse(r.Success);
            Assert.IsTrue(HasCode(r, "E0152"), Dump(r));
        }

        [Test]
        public void ConstDouble_IsSupported()
        {
            var r = CompileSource(@"
class C { const double K = 1.5d; }
trigger T { event OnPing(Unit u) { Api.Note($""{C.K}""); } }");
            Assert.IsTrue(r.Success, Dump(r));
        }

        // ===== M-11: хостовый null-строка не был null для скрипта ============

        // Именно сырой путь: fluent-обёртка null уже обрабатывала правильно,
        // а CallContext.ReturnStr давал Str(-1) — «не null и не строка».
        [Test]
        public void HostReturningNullString_ComparesEqualToNull()
        {
            _host.Registry.DefineMethod("Api", "Nothing",
                new Dsl.Semantics.TypeRef[0], Dsl.Semantics.TypeRef.Str,
                (ref CallContext c) => c.ReturnStr(null));

            var e = Load(CompileBody(
                @"if (Api.Nothing() == null) { Api.Note(""is-null""); } else { Api.Note(""not-null""); }"),
                out var errors);

            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());

            Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
            CollectionAssert.Contains(_log, "is-null");
        }

        // ===== M-12: int.MinValue / -1 давало «Внутренняя ошибка» ============

        [Test]
        public void IntMinValueDividedByMinusOne_IsClearScriptError()
        {
            var e = Load(CompileBody(@"int a = -2147483647 - 1; int b = -1; Api.Note($""{a / b}"");"),
                out var errors);

            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());

            Assert.AreEqual(1, errors.Count, string.Join("\n", errors));
            StringAssert.Contains("Переполнение", errors[0]);
        }

        [Test]
        public void IntMinValueModuloMinusOne_IsZero()
        {
            var e = Load(CompileBody(@"int a = -2147483647 - 1; int b = -1; Api.Note($""{a % b}"");"),
                out var errors);

            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());

            Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
            CollectionAssert.Contains(_log, "0");
        }

        // ===== M-13: незакрытый /* глотал остаток файла молча ================

        [Test]
        public void UnterminatedBlockComment_IsReported()
        {
            var r = CompileSource(@"trigger T { event OnPing(Unit u) { Api.Note(""x""); } } /* хвост");
            Assert.IsFalse(r.Success);
            Assert.IsTrue(HasCode(r, "E0218"), Dump(r));
        }

        // ===== C-04: сборщик коллекций =======================================

        // Временные коллекции больше не вечные: недостижимое освобождается.
        [Test]
        public void UnreachableCollections_AreCollected()
        {
            var e = Load(CompileBody(@"int i = 0; while (i < 50) { var t = new List<int>(); t.Add(i); i = i + 1; }"),
                out var errors);
            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());

            Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
            Assert.GreaterOrEqual(e.GetStats().Collections, 50, "коллекции созданы");

            var swept = e.Collect();
            Assert.GreaterOrEqual(swept.Collections, 50, "недостижимое освобождено");
            Assert.AreEqual(0, e.GetStats().Collections, "живых не осталось");
        }

        // Достижимое из статика — живёт, включая вложенную коллекцию.
        [Test]
        public void ReachableCollections_SurviveWithNesting()
        {
            var e = Load(CompileSource(@"
class S { List<int> keep = new List<int>(); }
trigger T
{
    event OnPing(Unit u)
    {
        S.keep.Add(1);
        var junk = new List<int>();
        junk.Add(2);
        Api.Note($""{S.keep.count}"");
    }
}"), out var errors);

            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());
            Assert.AreEqual(0, errors.Count, string.Join("\n", errors));

            e.Collect();
            Assert.AreEqual(1, e.GetStats().Collections, "остался только статик-список");

            _log.Clear();
            _onPing.Raise(e, new Unit());          // содержимое пережило сборку
            Assert.AreEqual(new[] { "2" }, _log);
        }

        // Коллекция, живая ТОЛЬКО через снапшот for-in (источник очищен) — тот же
        // корень, на котором раньше спотыкался свип строк.
        [Test]
        public void CollectionAliveOnlyViaIterationSnapshot_Survives()
        {
            var e = Load(CompileSource(@"
class S { List<int> src = new List<int>(); }
trigger T
{
    event OnPing(Unit u)
    {
        S.src.Add(10); S.src.Add(20);
        for x in S.src { S.src.Clear(); wait 0.1; Api.Note($""{x}""); }
    }
}"), out var errors);

            e.Tick(0.016f);
            _onPing.Raise(e, new Unit());   // дошли до первого wait
            e.Collect();                    // снапшот обязан удержать элементы
            for (int i = 0; i < 5; i++) e.Tick(0.2f);

            Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
            Assert.AreEqual(new[] { "10", "20" }, _log);
        }

        // ===== Миграция сейва ================================================

        // Вставка поля в середину сдвигает слоты: значения должны ехать по ИМЕНАМ.
        [Test]
        public void InsertedField_DoesNotShiftSavedValues()
        {
            var before = CompileSource(@"
class S { int a = 1; int b = 2; }
trigger T { event OnPing(Unit u) { S.a = 10; S.b = 20; Api.Note($""{S.a} {S.b}""); } }");
            var after = CompileSource(@"
class S { int a = 1; int mid = 99; int b = 2; }
trigger T { event OnPing(Unit u) { Api.Note($""{S.a} {S.b} {S.mid}""); } }");
            Assert.IsTrue(before.Success, Dump(before));
            Assert.IsTrue(after.Success, Dump(after));

            var res = new NullResolver();
            var e1 = Load(before, out _);
            e1.Tick(0.016f);
            _onPing.Raise(e1, new Unit());
            Assert.AreEqual(new[] { "10 20" }, _log);
            byte[] save = e1.SaveState(res);

            _log.Clear();
            var e2 = Load(after, out _);
            var report = e2.LoadState(save, res);
            CollectionAssert.Contains(report.NewStatics, "c:S.mid");

            e2.Tick(0.016f);
            _onPing.Raise(e2, new Unit());
            Assert.AreEqual(new[] { "10 20 99" }, _log, "значения нашлись по именам, а не по индексам");
        }

        // Добавление триггера сдвигает id — флаг обязан ехать по имени.
        [Test]
        public void AddedTrigger_DoesNotBreakTriggerFlags()
        {
            var before = CompileSource(@"
disabled trigger Cheats { event OnPing(Unit u) { Api.Note(""cheat""); } }
trigger T { event OnPing(Unit u) { Api.Note(""t""); } }");
            var after = CompileSource(@"
trigger Extra { event OnPing(Unit u) { Api.Note(""extra""); } }
disabled trigger Cheats { event OnPing(Unit u) { Api.Note(""cheat""); } }
trigger T { event OnPing(Unit u) { Api.Note(""t""); } }");
            Assert.IsTrue(before.Success, Dump(before));
            Assert.IsTrue(after.Success, Dump(after));

            var res = new NullResolver();
            var e1 = Load(before, out _);
            Assert.IsTrue(e1.SetTriggerEnabled("Cheats", true));
            byte[] save = e1.SaveState(res);

            var e2 = Load(after, out _);
            e2.LoadState(save, res);

            bool cheatsOn = false, found = false;
            foreach (var st in e2.GetTriggerStats())
                if (st.Name == "Cheats") { cheatsOn = st.Enabled; found = true; }
            Assert.IsTrue(found, "триггер Cheats на месте");
            Assert.IsTrue(cheatsOn, "флаг переехал по имени, несмотря на сдвиг id");
        }

        // ===== M-08: карантин модулей ========================================

        [Test]
        public void BrokenModule_IsQuarantined_RestCompiles()
        {
            var good = Mod("base", @"trigger B { event OnPing(Unit u) { Api.Note(""base""); } }");
            var bad = Mod("evil", @"trigger E { Engine.Log(""оператор мимо event""); }");

            var r = ScriptCompiler.Compile(_host.Registry, 1,
                new List<ModuleSourceSet> { good, bad }, quarantineBrokenModules: true);

            Assert.IsTrue(r.Success, Dump(r));
            Assert.AreEqual(1, r.Excluded.Count, "исключён ровно один модуль");
            Assert.AreEqual("evil", r.Excluded[0].Name);
            Assert.AreEqual(1, r.Program.Modules.Length);
            Assert.AreEqual("base", r.Program.Modules[0]);
        }

        [Test]
        public void DependentOfBrokenModule_IsExcludedToo()
        {
            var bad = Mod("evil", @"trigger E { Engine.Log(""битый""); }");
            var dep = Mod("addon", @"trigger A { event OnPing(Unit u) { Api.Note(""addon""); } }", "evil");
            var good = Mod("base", @"trigger B { event OnPing(Unit u) { Api.Note(""base""); } }");

            var r = ScriptCompiler.Compile(_host.Registry, 1,
                new List<ModuleSourceSet> { bad, dep, good }, quarantineBrokenModules: true);

            Assert.IsTrue(r.Success, Dump(r));
            var names = new List<string>();
            foreach (var ex in r.Excluded) names.Add(ex.Name);
            CollectionAssert.Contains(names, "evil");
            CollectionAssert.Contains(names, "addon");
            Assert.AreEqual(1, r.Program.Modules.Length);
            Assert.AreEqual("base", r.Program.Modules[0]);
        }

        [Test]
        public void ApiVersionMismatch_QuarantinesOnlyThatModule()
        {
            var good = Mod("base", @"trigger B { event OnPing(Unit u) { Api.Note(""base""); } }");
            var old = Mod("legacy", @"trigger L { event OnPing(Unit u) { Api.Note(""old""); } }");
            old.Manifest.ApiVersion = 0; // мод под прошлую версию API

            var r = ScriptCompiler.Compile(_host.Registry, 1,
                new List<ModuleSourceSet> { good, old }, quarantineBrokenModules: true);

            Assert.IsTrue(r.Success, Dump(r));
            Assert.AreEqual(1, r.Excluded.Count);
            Assert.AreEqual("legacy", r.Excluded[0].Name);
        }

        // Инструментам (DslCheck, LSP) карантин не нужен: модер обязан видеть
        // свои ошибки, а не молча лишиться модуля.
        [Test]
        public void WithoutQuarantine_AnyErrorFailsWholeBuild()
        {
            var good = Mod("base", @"trigger B { event OnPing(Unit u) { Api.Note(""base""); } }");
            var bad = Mod("evil", @"trigger E { Engine.Log(""битый""); }");

            var r = ScriptCompiler.Compile(_host.Registry, 1, new List<ModuleSourceSet> { good, bad });

            Assert.IsFalse(r.Success);
            Assert.AreEqual(0, r.Excluded.Count);
        }
    }
}
