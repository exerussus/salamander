using System.Collections.Generic;
using Dsl.Hosting;
using Dsl.Semantics;
using Dsl.Unity;
using UnityEngine;

namespace Dsl.Examples.Rpg
{
    public enum Team { Heroes, Monsters }
    public enum DamageType { Physical, Fire, Ice }

    /// <summary>Игровая сущность симуляции. Скрипты видят её как хэндл типа Unit.</summary>
    public sealed class Unit
    {
        public string Name;
        public Team Team;
        public float Health;
        public float MaxHealth;
        public float Damage;
        public float DamageMult = 1f;   // множитель урона (скрипты бафают через UnitApi.Buff)
        public float AttackCooldown;
        public float Timer;
        public DamageType DmgType;
        public bool IsBoss;

        public bool Alive => Health > 0f;
    }

    /// <summary>
    /// ХОСТ-СТОРОНА примера: чистая симуляция РПГ-боя в консоль.
    ///
    /// Как развернуть:
    ///  1) Dsl.Core и Dsl.Unity лежат в Assets (плюс пакет Newtonsoft из UPM);
    ///  2) этот файл — куда угодно в Assets (asmdef-ы autoReferenced);
    ///  3) папку StreamingAssets из Examples/UnityRpg скопировать в Assets
    ///     (появится Assets/StreamingAssets/Scripts/rpg_demo/...);
    ///  4) пустой GameObject → добавить компонент RpgSimulation → Play.
    ///
    /// Регистрация API — через fluent HostBuilder (Dsl.Hosting): типы выводятся
    /// из лямбд, енумы и события типизированы, никаких Variant и кастов.
    /// Сырой уровень (HostRegistry напрямую) показан в Examples/DemoHost —
    /// он остаётся доступен для случаев, которые обёртка не покрывает.
    ///
    /// Разделение ответственности:
    ///  - ХОСТ владеет состоянием (юниты, hp, кулдауны) и «физикой» боя:
    ///    кто, кого и когда бьёт. После каждого факта он поднимает событие.
    ///  - СКРИПТЫ (StreamingAssets/Scripts/rpg_demo/src/battle.script) реагируют:
    ///    комментируют бой, вводят ярость босса, зовут подкрепление. Их можно
    ///    править прямо во время Play — хот-релоад подхватит.
    /// </summary>
    public sealed class RpgSimulation : ScriptHostBootstrap
    {
        [Header("Симуляция")]
        [SerializeField] private float _battleStartDelay = 1f;

        private readonly List<Unit> _units = new List<Unit>();

        // типизированные события: id и упаковка аргументов внутри
        private EventRef _evBattleStarted;
        private EventRef<Unit> _evUnitSpawned;
        private EventRef<Unit, Unit, float, DamageType> _evDamage;
        private EventRef<Unit, Unit> _evDied;

        private float _clock;
        private bool _started;
        private bool _finished;

        // ===================================================================
        // 1. РЕГИСТРАЦИЯ: всё, что скрипты видят от игры
        // ===================================================================

        protected override void ConfigureHost(HostRegistry registry)
        {
            var host = new HostBuilder(registry);

            // типы объявляются ДО использования в свойствах/методах/событиях;
            // summary и doc уходят в salamander-api.json → подсказки в редакторе
            host.Enum<Team>(summary: "Сторона в бою.")
                .Enum<DamageType>(summary: "Тип наносимого урона.");

            host.Class<Unit>(summary: "Боевой юнит.")
                .Prop("name",      u => u.Name,      doc: "имя юнита")
                .Prop("health",    u => u.Health,    doc: "текущее HP")
                .Prop("maxHealth", u => u.MaxHealth, doc: "максимальное HP");

            host.Api("UnitApi")
                .Fn("IsBoss", (Unit u) => u != null && u.IsBoss,
                    Sig.Doc("Является ли юнит боссом.").P("unit", "проверяемый юнит"))
                .Fn("Team", (Unit u) => u?.Team ?? Team.Heroes,
                    Sig.Doc("Сторона, за которую сражается юнит.").P("unit"))
                .Act("Heal", (Unit u, float amount) =>
                    {
                        if (u != null && u.Alive)
                            u.Health = Mathf.Min(u.MaxHealth, u.Health + amount);
                    },
                    Sig.Doc("Восстанавливает здоровье юниту (не выше максимума).")
                       .P("target", "кого лечить").P("amount", "сколько HP вернуть"))
                .Act("Buff", (Unit u, float damageMult) =>
                    {
                        if (u != null) u.DamageMult = damageMult;
                    },
                    Sig.Doc("Устанавливает множитель урона юнита.")
                       .P("target").P("damageMult", "множитель урона (1.0 — базовый)"));

            host.Api("BattleApi")
                .Fn("SpawnUnit", (string name, Team team, float hp, float dmg) =>
                        SpawnUnit(name, team, hp, dmg, DamageType.Physical, cooldown: 1.1f, isBoss: false),
                    Sig.Doc("Создаёт юнита и вводит его в бой.")
                       .P("name", "имя нового юнита").P("team", "сторона")
                       .P("hp", "стартовое и максимальное HP").P("damage", "базовый урон"))
                .Fn("AliveCount", (Team team) =>
                    {
                        int n = 0;
                        foreach (var u in _units)
                            if (u.Alive && u.Team == team) n++;
                        return n;
                    },
                    Sig.Doc("Сколько живых юнитов у стороны.").P("team"));

            _evBattleStarted = host.Event("OnBattleStarted", Sig.Doc("Бой начался."));
            _evUnitSpawned   = host.Event<Unit>("OnUnitSpawned",
                Sig.Doc("Юнит введён в бой.").P("unit", "появившийся юнит"));
            _evDamage        = host.Event<Unit, Unit, float, DamageType>("OnUnitDamageTaken",
                Sig.Doc("Юнит получил урон.")
                   .P("source", "кто ударил").P("target", "кого ударили")
                   .P("amount", "нанесённый урон").P("type", "тип урона"));
            _evDied          = host.Event<Unit, Unit>("OnUnitDied",
                Sig.Doc("Юнит погиб (хэндлы ещё валидны внутри обработчика).")
                   .P("unit", "погибший").P("killer", "добивший"));
        }

        // ===================================================================
        // 2. СИМУЛЯЦИЯ: хост владеет боем, скрипты получают события
        // ===================================================================

        protected override void Update()
        {
            base.Update(); // хот-релоад + Engine.Tick(dt) + событие Update(time, dt)

            if (!IsAuthority || Engine == null || !Engine.IsLoaded) return;

            float dt = UnityEngine.Time.deltaTime;
            _clock += dt;

            if (!_started)
            {
                if (_clock >= _battleStartDelay) StartBattle();
                return;
            }
            if (_finished) return;

            SimulateCombat(dt);
        }

        private void StartBattle()
        {
            _started = true;
            _evBattleStarted.Raise(Engine);

            // герои
            SpawnUnit("Паладин", Team.Heroes, 130f, 11f, DamageType.Physical, 1.0f, false);
            SpawnUnit("Лучница", Team.Heroes, 85f, 14f, DamageType.Physical, 1.2f, false);
            SpawnUnit("Маг", Team.Heroes, 70f, 19f, DamageType.Fire, 1.6f, false);

            // монстры
            SpawnUnit("Гоблин-разведчик", Team.Monsters, 55f, 7f, DamageType.Physical, 0.9f, false);
            SpawnUnit("Гоблин-громила", Team.Monsters, 75f, 9f, DamageType.Physical, 1.1f, false);
            SpawnUnit("Огр-вождь", Team.Monsters, 280f, 17f, DamageType.Physical, 1.8f, isBoss: true);
        }

        private Unit SpawnUnit(string name, Team team, float hp, float dmg,
                               DamageType dmgType, float cooldown, bool isBoss)
        {
            var u = new Unit
            {
                Name = name,
                Team = team,
                Health = hp,
                MaxHealth = hp,
                Damage = dmg,
                DmgType = dmgType,
                AttackCooldown = cooldown,
                Timer = cooldown * 0.5f,
                IsBoss = isBoss,
            };
            _units.Add(u);
            // вложенный Raise безопасен: SpawnUnit зовётся и из скриптовых файберов
            _evUnitSpawned.Raise(Engine, u);
            return u;
        }

        private void SimulateCombat(float dt)
        {
            // снапшот количества: скрипты могут наспавнить юнитов прямо во время
            // обработки наших событий — новички вступают со следующего кадра
            int count = _units.Count;
            for (int i = 0; i < count; i++)
            {
                var attacker = _units[i];
                if (!attacker.Alive) continue;

                attacker.Timer -= dt;
                if (attacker.Timer > 0f) continue;
                attacker.Timer += attacker.AttackCooldown;

                var target = PickTarget(attacker.Team);
                if (target == null) { Finish(); return; }

                float amount = attacker.Damage * attacker.DamageMult;
                target.Health -= amount;

                _evDamage.Raise(Engine, attacker, target, amount, attacker.DmgType);

                if (!target.Alive)
                {
                    // порядок важен: сперва событие (обработчикам нужен живой хэндл),
                    // затем инвалидация — все хэндлы протухают, IsValid → false
                    _evDied.Raise(Engine, target, attacker);
                    Engine.InvalidateEntity(target);

                    if (PickTarget(Team.Heroes) == null || PickTarget(Team.Monsters) == null)
                    {
                        Finish();
                        return;
                    }
                }
            }
        }

        /// <summary>Цель для команды attackerTeam: живой враг с минимальным hp (фокус).</summary>
        private Unit PickTarget(Team attackerTeam)
        {
            Unit best = null;
            foreach (var u in _units)
            {
                if (!u.Alive || u.Team == attackerTeam) continue;
                if (best == null || u.Health < best.Health) best = u;
            }
            return best;
        }

        private void Finish()
        {
            if (_finished) return;
            _finished = true;
            Debug.Log("[host] Бой окончен — симуляция остановлена (движок скриптов продолжает тикать).");
        }
    }
}
