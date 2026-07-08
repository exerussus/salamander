using System;
using System.Collections.Generic;
using Dsl.Compilation;
using Dsl.Hosting;
using Dsl.Runtime;
using Dsl.Semantics;
using NUnit.Framework;

namespace Dsl.Tests
{
    /// <summary>
    /// Контракт fluent-слоя (Dsl.Hosting): регистрация лямбдами без Variant
    /// должна давать ровно то же поведение, что сырой HostRegistry.
    /// </summary>
    public sealed class HostBuilderTests
    {
        private enum FTeam { Red, Blue }
        private enum BadEnum { A = 1, B = 2 } // значения не 0..N-1 — регистрация обязана падать

        private sealed class FUnit
        {
            public float Health;
            public FTeam Team;
            public float Mana;
        }

        private static CompilationResult Compile(HostRegistry reg, string src)
        {
            var set = new ModuleSourceSet
            {
                Manifest = new ModuleManifest { Name = "test", ApiVersion = 1, Sources = new[] { "t.sal" } },
            };
            set.Files.Add(("test/t.sal", src));
            return ScriptCompiler.Compile(reg, 1, new List<ModuleSourceSet> { set });
        }

        [Test]
        public void Fluent_EndToEnd_PropsMethodsEnumsEvents()
        {
            var sunk = new List<float>();

            var host = new HostBuilder();
            host.Enum<FTeam>();

            host.Class<FUnit>("Unit")
                .Prop("health", u => u.Health)
                .Prop("team", u => u.Team)                        // readonly енум-свойство
                .Prop("mana", u => u.Mana, (u, v) => u.Mana = v); // read-write float

            host.Api("Api")
                .Act("Sink", (float f) => sunk.Add(f))
                .Fn("Twice", (float f) => f * 2f)
                .Fn("SameTeam", (FUnit u, FTeam t) => u.Team == t);

            var onPing = host.Event<FUnit, float>("OnPing");

            var r = Compile(host.Registry, @"trigger T {
                event OnPing(Unit u, float x) {
                    Api.Sink(Api.Twice(x));
                    if (u.team == FTeam.Red && Api.SameTeam(u, FTeam.Red)) {
                        Api.Sink(u.health);
                    }
                    u.mana = 5.5;
                }
            }");
            Assert.IsTrue(r.Success, Dump(r));

            var engine = new ScriptEngine(host.Registry);
            engine.OnError += m => Assert.Fail(m);
            engine.LoadProgram(r.Program);

            var unit = new FUnit { Health = 40f, Team = FTeam.Red, Mana = 0f };
            onPing.Raise(engine, unit, 3f);

            Assert.AreEqual(2, sunk.Count);
            Assert.AreEqual(6f, sunk[0], 1e-4f);   // Twice(3)
            Assert.AreEqual(40f, sunk[1], 1e-4f);  // health через свойство
            Assert.AreEqual(5.5f, unit.Mana, 1e-4f, "rw-свойство должно записаться из скрипта");
        }

        [Test]
        public void Fluent_EnumMemberNames_MatchCSharp()
        {
            string logged = null;

            var host = new HostBuilder();
            host.Enum<FTeam>();
            host.Class<FUnit>("Unit").Prop("team", u => u.Team);
            var onPing = host.Event<FUnit>("OnPing");

            var r = Compile(host.Registry, @"trigger T {
                event OnPing(Unit u) { Engine.Log($""team={u.team}""); }
            }");
            Assert.IsTrue(r.Success, Dump(r));

            var engine = new ScriptEngine(host.Registry);
            engine.OnLog += m => logged = m;
            engine.LoadProgram(r.Program);

            onPing.Raise(engine, new FUnit { Team = FTeam.Blue });
            Assert.AreEqual("team=Blue", logged, "интерполяция печатает имя элемента C#-енума");
        }

        [Test]
        public void NonContiguousEnum_ThrowsAtRegistration()
        {
            var host = new HostBuilder();
            Assert.Throws<ArgumentException>(() => host.Enum<BadEnum>());
        }

        [Test]
        public void UnregisteredType_ThrowsWithHint()
        {
            var host = new HostBuilder();
            // FUnit не объявлен через Class<FUnit>() — регистрация метода обязана
            // упасть сразу, с подсказкой, а не в рантайме
            var ex = Assert.Throws<InvalidOperationException>(() =>
                host.Api("Api").Fn("Bad", (FUnit u) => 1));
            StringAssert.Contains("Class<FUnit>", ex.Message);
        }

        [Test]
        public void Sig_Doc_FlowsIntoExportedManifest()
        {
            var host = new HostBuilder();
            host.Class<FUnit>("Unit", "боевой юнит")
                .Prop("health", u => u.Health, doc: "текущее HP");
            host.Api("Api")
                .Act("Heal", (FUnit u, float amount) => u.Health += amount,
                    Sig.Doc("Восстанавливает HP.").P("target", "кого лечить").P("amount", "сколько"));

            string json = Compilation.ApiManifest.Export(host.Registry, 1);

            StringAssert.Contains("Восстанавливает HP.", json);
            StringAssert.Contains("\"target\"", json);
            StringAssert.Contains("кого лечить", json);
            StringAssert.Contains("боевой юнит", json);
            StringAssert.Contains("текущее HP", json);

            // round-trip: импорт восстанавливает реестр и типы сходятся
            var reg2 = Compilation.ApiManifest.Import(json, out int ver);
            Assert.AreEqual(1, ver);
            Assert.IsTrue(reg2.TryGetApi("Api", out var api));
            Assert.IsTrue(api.TryGetMethod("Heal", out var m));
            Assert.AreEqual("Восстанавливает HP.", m.Summary);
            Assert.AreEqual("target", m.ParamNames[0]);
            Assert.AreEqual("сколько", m.ParamDocs[1]);
        }

        [Test]
        public void Sig_Doc_ArityMismatch_Throws()
        {
            var host = new HostBuilder();
            host.Class<FUnit>("Unit");
            // два .P на одноаргументный метод — ошибка при регистрации
            var ex = Assert.Throws<ArgumentException>(() =>
                host.Api("Api").Fn("IsLow", (FUnit u) => u.Health < 10f,
                    Sig.Doc("x").P("a").P("b")));
            StringAssert.Contains("параметров", ex.Message);
        }

        [Test]
        public void ManyParams_AboveFour_Work()
        {
            int sum = -1;

            var host = new HostBuilder();
            // 6-аргументный метод и 5-аргументное событие — путь перегрузок >4
            host.Api("Api").Act<int, int, int, int, int, int>("Sum6",
                (a, b, c, d, e, f) => sum = a + b + c + d + e + f);
            var big = host.Event<int, int, int, int, int>("OnBig");

            var r = Compile(host.Registry, @"trigger T {
                event OnBig(int a, int b, int c, int d, int e) {
                    Api.Sum6(a, b, c, d, e, 100);
                }
            }");
            Assert.IsTrue(r.Success, Dump(r));

            var engine = new ScriptEngine(host.Registry);
            engine.OnError += m => Assert.Fail(m);
            engine.LoadProgram(r.Program);
            big.Raise(engine, 1, 2, 3, 4, 5);

            Assert.AreEqual(115, sum); // 1+2+3+4+5 + 100
        }

        private static string Dump(CompilationResult r)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var d in r.Diagnostics) sb.AppendLine(d.ToString());
            return sb.ToString();
        }
    }
}
