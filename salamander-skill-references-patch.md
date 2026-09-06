# Патч reference-файлов скилла `salamander`

Обновление под новые фичи движка: **`readonly`-поля**, **константы вида
(`Const<T>` / `ConstOr<T>` с дефолтами)**, **составные имена API-классов
(`Api.Weapon.Cut(...)`)**, **константы API-классов
(`Api.Parts.Grip.sword_01`)** и **структуры хоста (`new Damage(slash: 21)`)**.
`SKILL.md` обновляется отдельно — через карточку предложения скилла; эти два
файла заменяются вручную.

Патч кумулятивный: если предыдущая версия ещё не применена, применяйте эту —
она включает всё, что было в ней.

Заодно исправлены **две ошибки, которые в текстах были и раньше**:

1. §9.6 утверждал, что переопределяющий блок «НЕ видит поля и функции исходного»
   и что состоянием надо делиться через `class`. Это неверно уже сейчас: все
   блоки с одним `(вид, id)` сливаются в ОДНУ мерж-сущность, переопределение
   идёт по-членно, и ранние обработчики видят итог.
2. В §9.6 и в таблице кодов `E0199` описан как «неизвестное событие вида ИЛИ
   блок без событий». «Нет такого события у вида» давно переехало на **E0217**;
   `E0199` — только «мерж-сущность без событий».

---

## 1. `references/language.md`

### 1.1 Заменить раздел `### class` (целиком)

````markdown
### class

Static-only container: constants, fields, functions. No instances, no `this`.
Reserved name `Engine` cannot be redefined (E0100).

```
class Balance
{
    const float BOSS_THRESHOLD = 0.35;   // folded into bytecode, no slot
    readonly int bossHp = 750;           // has a slot; assignable only here
    int wavesSpawned = 0;                // ordinary state
    func NextWave() -> int { wavesSpawned = wavesSpawned + 1; return 6; }
}
```

Access from other code as `Balance.BOSS_THRESHOLD`, `Balance.NextWave()`. Fields are
shared program-wide (static).
````

### 1.2 Вставить новый подраздел сразу ПОСЛЕ `### enum` (перед `## 4.`)

````markdown
### Field modifiers: `const`, `readonly`, plain

Three ways to store a value, and the difference shows up in saves and in what the
host can read:

| | slot | host can read | assignable | serialized |
|---|---|---|---|---|
| `const float K = 2.5;` | no, folded into bytecode | **no** | never | — |
| `readonly float damage = 12.5;` | yes | yes | only in the declaration | **no** |
| `float damage = 12.5;` | yes | yes | anywhere | **yes** |

- `const` accepts only a literal or an enum member, and only inside a `class`
  (E0108 in a trigger/listener, E0205 in an archetype block). It is inlined at
  every use site, so nothing outside the VM can observe it.
- `readonly` is a **contextual** keyword: it is a modifier only directly before a
  type, so a field or local actually NAMED `readonly` still compiles. Allowed in
  `class`, `trigger`, `listener` and archetype blocks — the mechanics are the same
  everywhere. Assigning to one is E0225 (bare name) / E0226 (through a dot).
- Ordinary fields are state. In an archetype block they are per-content-id
  statics, not per-instance — all swords share them.

`readonly` is about "does not change after load", NOT "cannot be overridden": a
later block may declare its own value (that is a declaration, not an assignment),
which is how mods rebalance. Merging TIGHTENS: if any block wrote `readonly`, the
field is readonly for every block.

The save consequence is the point of the modifier. `readonly` fields are not
written to a save and not restored from one — they are re-initialized from the
current program on every load, so editing `readonly float damage = 12.0` to `20.0`
reaches players who load an OLD save, not just new games. Ordinary fields keep
their saved value, as before.
````

### 1.3 Заменить раздел `## 9.6 Archetype blocks` (целиком)

````markdown
## 9.6 Archetype blocks: per-content-id mechanics (spell/item/...)

The HOST declares entity kinds (spell, item, hero, ...), their event sets and
optionally their expected constants; kinds are DATA, not language keywords. A
script block describes the mechanics AND the data of ONE content entity, addressed
by the same id the game's content manifests use:

```
spell fireball
{
    readonly int damage = 3;             // recipe: the host reads it, saves ignore it
    int casts = 0;                       // state: STATIC per (kind, id), and saved

    event OnCast(Unit caster, float power) { casts = casts + 1; ... }
    event OnObtain(Unit unit) { }
}

item "sword.v2"                          // id may be an identifier or a string
{
    event OnPick(Unit unit, Item it) { }
}
```

Semantics:
- The game raises events AT a specific (kind, id): only that entity's handler
  runs; no block or no handler → silence. Handlers are fibers (wait/spawn fine,
  synchronous-module rules still apply).
- Ids are interned to dense ints at compile: the host resolves the string once
  at content load (`engine.ResolveArchetype("spell", "fireball")` → int handle)
  and raises by handle — the hot path is pure array indexing, no strings/hashes.
- **Merge semantics (modding).** ALL blocks with the same (kind, id) — in one file
  or across modules — merge into ONE entity. Override is per member, later wins:
  an event replaces that handler, a field re-uses the SAME slot with the later
  initializer, a function's later version is what every call binds to. Earlier
  handlers see the merged result, so a block may be a pure data patch:
  `spell fireball { readonly int damage = 5; }`. Kill an implementation by
  overriding it with an empty body: `pass;` or a bodyless prototype
  `event OnCast(Unit c, float p);`. Same-name duplicates are an error only WITHIN
  one block; changing a field's type on override is E0207.
- Blocks are not symbols: you cannot reference `spell fireball` from script
  code; only the host addresses them.

### Reading the data back (host side)

Fields of a block are the entity's "recipe" and the host reads them by name:
`engine.TryGetArchetypeConst(kind, id, field, out Variant v)` and
`engine.GetArchetypeConsts(kind, id, into)` to enumerate when the field set is
open (attributes that plug into layers the core never heard of). See
references/host-api.md.

### Kind contract: expected constants and defaults

The host MAY declare which fields a kind's entities have — the same opt-in idiom
as known ids. Nothing declared → the set is open and the checker says nothing.

```csharp
host.Archetype("weapon")
    .Const<float>("damage", required: true)   // block MUST declare it
    .ConstOr<int>("windup_ticks", 3)          // block may skip it → 3 is used
    .ConstOr<string>("tooltip", null);
```

- A field from the contract is **readonly by definition** — writing the modifier
  in the block is allowed but not required, and the E0225 message names the kind
  so the restriction isn't magic.
- A constant WITH a default exists on every entity of the kind even if no block
  declares it, and scripts read it by name like any other field:
  `weapon dagger { event OnHit(Unit u) { UnitApi.Windup(windup_ticks); } }`.
- A constant WITHOUT a default is not materialized: referencing it without
  declaring it is a compile error, deliberately — otherwise it would silently read
  as `0`/`false`/`null`.
- `required` and a default are mutually exclusive (registration throws).
- The contract is checked on the MERGED entity, so a patch module may supply a
  constant the base block omitted.

Statement `pass;` is a no-op usable anywhere; a `;` instead of a body is allowed
for void functions/events/actions (E0203 for non-void).

Error codes: E0198 unknown kind, E0199 merged entity with no events at all,
E0217 no such event on the kind, E0200/E0201 event signature mismatch, E0202
unknown content id, E0203 prototype for non-void, E0204 `action` in a block,
E0205 `const` in a block (use a `readonly` field), E0207 override changes a
field's type or const-ness, E0223 missing (or valueless) required constant,
E0224 constant's type disagrees with the kind's, E0225/E0226 assignment to a
readonly field or kind constant, W0101 field outside the declared constant set.
````

### 1.4 Заменить таблицу в `## 12. Error-code table (common)`

Добавить строки (порядок по коду) и оставить остальные как есть:

````markdown
| E0108 | `const` inside a trigger (use a `readonly` field, or move it to a `class`) |
| E0205 | `const` inside an archetype block (use a `readonly` field) |
| E0207 | override changes a field's type, or swaps const/non-const |
| E0217 | no such event on this archetype kind |
| E0223 | required kind constant missing, or declared with no value |
| E0224 | field's type disagrees with the kind's declared constant |
| E0225 | assignment to a `readonly` field / kind constant (by bare name) |
| E0226 | assignment to a `readonly` field / kind constant (through a dot) |
| E0229 | `new Struct(...)`: field name expected |
| E0230 | `new Struct(...)`: `:` expected — struct fields are set by name, never positionally |
| E0231 | `new Struct(...)`: `)` expected |
| E0232 | no such field on this struct (read) |
| E0233 | the host does not declare this struct type — scripts cannot declare one |
| E0234 | no such field on this struct (construction) |
| E0235 | the same struct field is given twice |
| E0236 | assignment to a struct field — struct values are immutable |
| E0237 | `==` / `!=` on structs — compare the fields you care about |
| E0238 | no such constant on this API class |
| E0239 | an API constant used as a call — read it without parentheses |
| W0101 | (warning) field is outside the kind's declared constant set — likely a typo |
````

### 1.5 Заменить одну строку в `## 13. Deliberate v1 limitations`

Было:

```
- `const` values must be literals (no `const X = Y * 2`).
```

Стало:

````markdown
- `const` values must be literals or enum members (no `const X = Y * 2`), and
  `const` lives only in a `class`. For a constant the host must be able to read —
  content recipes above all — use a `readonly` field: it has a slot, is readable
  through `TryGetArchetypeConst`, and still cannot be assigned outside its
  declaration.
- Kind constants have no inheritance: defaults are declared per kind, not per
  prototype chain ("all one-handed swords default to windup 3" is not expressible
  yet).
- Struct fields are literals or enum members only — no entities, no collections,
  no nested structs — and a struct has no default literal, so `ConstOr<Damage>`
  is impossible (`Const<Damage>(required: true)` is fine).
````

### 1.6 Вставить новый подраздел сразу ПОСЛЕ «Field modifiers» (см. 1.2)

````markdown
### Host structs: several numbers that travel together

When a recipe is not one number but a set — damage by type, a cost, resistances —
the **host declares a struct** and the script builds values of it. Scripts cannot
declare struct types (E0233): the type is the game's contract, so a mod cannot
invent a `Damage` with a ninth field the game has no way to read.

```
weapon sword { readonly Damage damage = new Damage(slash: 21.0); }
weapon mace  { readonly Damage damage = new Damage(blunt: 21.0, fire: 5.0); }
```

- Arguments are **named only** and may come in any order (positional → E0230).
  This is the one place in the language with named arguments: a struct of eight
  numbers is unreadable positionally and breaks when a field is inserted.
- Fields left out take the default the host declared — no need to write the zeros.
- The value is **immutable**: `damage.slash = 5` is E0236. That is what settles
  "value or reference semantics" — aliasing an immutable value is unobservable,
  so there is nothing to copy.

Otherwise it is an ordinary value: locals, fields (`readonly` or mutable),
parameters and results of script functions AND of host methods, class properties,
event arguments, `List<Damage>`, `Map<string, Damage>`. A mutable field holding a
struct is state and round-trips through saves; a `readonly` one does not, like any
declared value.

Not allowed: `==`/`!=` (E0237 — comparison would go by handle, so two identical
values would come out unequal; compare the fields you care about), and a struct as
a `Map` key (E0122) for the same reason.

Which structs exist and what fields they have is in `salamander-api.json`
(`structs`), and the editor completes field names inside `new Damage(`.
````

### 1.7 Вставить новый подраздел в раздел про вызовы API

````markdown
### Dotted API names

An API class may be registered under a dotted name, and scripts then call it with
the dots written out:

```
Api.Weapon.Cut(target, 3.0);
Api.Armor.Absorb(target);
```

`Api.Weapon` is one name, not a namespace object: the prefix has no members of its
own and cannot be stored in a variable — only called through. Plain names
(`UnitApi.Heal(...)`) work exactly as before.
````

### 1.8 Вставить новый подраздел сразу ПОСЛЕ «Dotted API names» (см. 1.7)

````markdown
### API constants

An API class may also expose named VALUES — content ids, keys, tags. They are read
WITHOUT parentheses:

```
readonly string grip = Api.PartsCatalog.Weapon.Grip.sword_1h_just_01;
```

A constant is folded into a literal at compile time: there is no call at runtime,
which is the point — an id catalogue is dozens of names, and a zero-argument method
returning a literal would mean a delegate and a host call per read, plus it reads as
"something is computed here".

The value is a literal (`bool/int/float/double/string`) or an enum member, and its
declared type is the type of the expression. A name is either a method or a
constant, never both.

Nearby errors are deliberately distinct: a constant called with parentheses is
**E0239** ("read it without parentheses"), a method read without them is **E0162**
("it can only be called"), and an actual typo is **E0238**.

Which constants exist is in `salamander-api.json` (`consts` next to `methods`), so
the editor completes them after the dot and shows the host's `doc` on hover.
````

---

## 2. `references/host-api.md`

### 2.1 Заменить раздел `## Archetype kinds (per-content-id mechanics)` (целиком)

````markdown
## Archetype kinds (per-content-id mechanics)

Declare entity kinds whose mechanics scripts describe as `spell <id> { ... }`
blocks (see language.md §9.6):

```csharp
var spell = host.Archetype("spell", summary: "Spell mechanics.");
var onCast   = spell.Event<Unit, float>("OnCast", Sig.Doc("Cast.").P("caster","who").P("power","how hard"));
var onObtain = spell.Event<Unit>("OnObtain");
spell.KnownIds(gameData.AllSpellIds);   // optional: typo'd block ids become compile errors

// optional: the kind's DATA contract — which fields its entities carry
spell.Const<int>("damage", required: true, doc: "damage per cast")
     .ConstOr<int>("cooldown_ticks", 30)          // block may omit it → 30
     .ConstOr<string>("tooltip", null);

// content load (cold): resolve once, cache on the game object
int fireball = engine.ResolveArchetype("spell", "fireball");   // -1 = no code

// gameplay (hot): pure int indexing, no strings
if (fireball >= 0) onCast.Raise(engine, fireball, caster, 7.5f);
onCast.Raise(engine, "fireball", caster, 7.5f);                // string overload = cold path

// collector / validation:
engine.HasArchetype("spell", "fireball");
var ids = new List<string>(); engine.GetArchetypeIds("spell", ids); // all ids with code
```

Kind events are a namespace of their own (usable only inside that kind's
blocks). If the game also wants a global "any spell cast", declare a regular
`host.Event<...>` next to it and raise both.

### Reading content constants

Fields declared in a block are the entity's recipe. Two reads, both cold-path
(content load / validation), neither allocating:

```csharp
// addressed — when the field set is YOUR contract (weapons, NPCs):
if (engine.TryGetArchetypeConst("weapon", "sword", "damage", out var v))
    weapon.Damage = v.ToF();            // string → engine.ResolveString(v)

// enumerated — when the set is OPEN and unknowable to the core: an attribute
// declares which layers it plugs into, and a pack's attribute may plug into
// "diplomacy", which the core has never heard of
engine.GetArchetypeConsts("attribute", "stubbornness", fieldsBuffer);
foreach (var field in fieldsBuffer)
    engine.TryGetArchetypeConst("attribute", "stubbornness", field, out var value);

// did the block declare it, or is this the kind's default?
engine.ImplementsConst("weapon", "sword", "damage");
```

The value read is the live static, i.e. already the merge result: a patch block's
initializer wins, exactly as scripts see it. Keys are parsed once in
`LoadProgram`; the read itself is two indexations plus a short name scan.

`GetArchetypeConsts` returns the ENTITY's effective field set — fields the block
declared plus those seeded from the kind's defaults, in declaration order
(contract fields first). It includes mutable state fields too, since the language
draws no line there beyond `readonly`.

### Contract enforcement

A non-empty `Const`/`ConstOr` set makes the checker validate every block of that
kind, the same way `KnownIds` validates ids:

- missing required constant, or one declared with no value → **E0223**;
- field's type disagrees with the declared one → **E0224**;
- field outside the declared set → warning **W0101** (probable typo, but a block
  may legitimately keep its own state, so it is not an error);
- assignment to a contract constant → **E0225/E0226**: a kind constant is
  **readonly by definition**, so the block only ever gives it a value.

Declare contracts only for CLOSED sets (weapons, NPCs — the game's own schema).
Where the field set is open by nature, declare nothing and use enumeration.

Contracts and defaults are exported into `salamander-api.json`, so the CLI checker
and the LSP enforce exactly the same rules outside the running game.
````

### 2.2 В разделе `## The manifest: salamander-api.json` добавить в схему

Рядом с описанием `archetypes` (после блока с `events`) — секция `consts`:

````markdown
Archetype kinds carry their event set, optional `knownIds`, and optional `consts`
(the data contract). A constant's `default` is written by VALUE, and an enum
default by MEMBER NAME so renumbering the enum cannot silently shift it:

```json
{
  "archetypes": [
    { "name": "weapon", "summary": "Weapon mechanics.",
      "knownIds": ["axe", "sword"],
      "consts": [
        { "name": "damage", "type": "float", "required": true,
          "hasDefault": false, "default": null, "doc": "damage per hit" },
        { "name": "windup_ticks", "type": "int", "required": false,
          "hasDefault": true, "default": 3 },
        { "name": "school", "type": "School", "required": false,
          "hasDefault": true, "default": "Frost" }
      ],
      "events": [
        { "name": "OnHit", "params": [ { "name": "target", "type": "Unit" } ] }
      ] }
  ]
}
```

`hasDefault` is what distinguishes "defaults to null" from "has no default" —
`default` alone cannot say it.
````

### 2.3 В разделе `## Save / load` добавить в «Rules and semantics»

Новый пункт, первым в списке:

````markdown
- **`readonly` fields and kind constants are NOT serialized.** They are declared
  values, not state, so they are re-initialized from the CURRENT program on every
  load (`LoadState` runs `<init>` before applying the snapshot). This is what makes
  a balance patch reach existing saves: editing `readonly float damage = 12.0` to
  `20.0`, or changing a kind's `ConstOr` default, changes the value for players who
  load an old save. A field that used to be mutable and became `readonly` simply
  has its saved value dropped — silently, and not reported as a missing static.
  Ordinary fields are state and round-trip as before.
````

### 2.4 Вставить новый раздел ПОСЛЕ `## Archetype kinds` (см. 2.1)

````markdown
## Host structs (a named immutable set of fields)

When a recipe is a set rather than a single number, declare a struct. The type is
the game's contract: scripts build values of it but cannot declare one.

```csharp
host.Struct<Damage>("Damage", "Damage by type.")
    .Field("pierce", (Damage d) => d.Pierce)
    .Field("slash",  (Damage d) => d.Slash)
    .Field("blunt",  (Damage d) => d.Blunt)
    .Field("fire",   (Damage d) => d.Fire, 0f, doc: "fire damage")
    .Build(v => new Damage(v.Float("pierce"), v.Float("slash"),
                           v.Float("blunt"), v.Float("fire")));
```

```
weapon sword { readonly Damage damage = new Damage(slash: 21.0); }
```

Field types are literals (`bool/int/float/double/string`) or enum members only —
entities, collections and nested structs are rejected at registration. Each field
may carry a default; fields the script leaves out take it.

**Two independent directions, both optional.**

- `.Build(factory)` lets the engine turn a script value into a C# one: needed for
  `engine.ReadStruct<T>` and for **taking a struct as a host method parameter**.
- the getter in `.Field(name, x => x.Field)` lets the engine turn a C# value into a
  script one: needed to **give a struct back to the script** — a method result, a
  class property, an event argument.

Declare only the direction you use. The other one is not a silent zero: it throws
with the exact call to add (`.Build(...)`) or the field missing a getter.

```csharp
host.Api("Fx")
    .Act("Apply",  (Unit u, Damage d) => u.Hit(d))     // script → game
    .Fn("Double",  (Damage d) => d.Scaled(2f));        // game → script

host.Class<Unit>().Prop("lastHit", u => u.LastHit);    // property
host.Event<Unit, Damage>("OnHurt");                    // event argument
```

Reading a value the script built:

```csharp
engine.TryGetArchetypeConst("weapon", "sword", "damage", out var v);
var d = engine.ReadStruct<Damage>(v);          // needs .Build(...)
engine.TryGetStructField(v, "slash", out var slash);   // works without it
engine.GetStructFields(v, buffer);             // field names, declaration order
engine.GetStructTypeName(v);                   // null when v is not a struct
```

Registration order matters: declare the struct **before** anything that mentions
it — properties, methods and events capture the reader/writer at registration.
Type names are one namespace: `Class`/`Enum`/`Struct` reject a name already taken
by another of the three.

Costs and caveats, worth knowing before designing around it:

- a struct value is a `Variant` array with the type id in slot 0, so the collection
  store, the GC and the save format need to know nothing about structs;
- **handing a struct to the script allocates** — a method result or a property read
  builds a value each time. Don't read such a property in a loop over a thousand
  units; fetch it once;
- taking a struct as a parameter allocates nothing: fields are read lazily from the
  value;
- `ConstOr<Damage>` is impossible (a default must be a literal, and a struct has no
  literal). `Const<Damage>(name, required: true)` in a kind contract is fine.

In the manifest structs live in a top-level `"structs"` array, written **before**
`"classes"` — a class property may be a struct, a struct field can never be a class:

```json
"structs": [
  { "name": "Damage", "summary": "Damage by type.",
    "fields": [ { "name": "slash", "type": "float", "default": 0.0 } ] }
]
```
````

### 2.5 Вставить новый раздел ПОСЛЕ `## Registering by attributes`

````markdown
## Dotted API names

An API class can be registered under a dotted name, so calls group themselves in
the editor under one head:

```csharp
host.Api("Api.Weapon").Fn("Cut", (Unit u, float power) => u.Cut(power));
host.RegisterApi(new WeaponApi(room), "Api.Weapon");      // instance API
[SalamanderApi(name: "Api.Weapon")] public sealed class WeaponApi { ... }
```

```
Api.Weapon.Cut(target, 3.0);
```

Every segment must be an identifier; the name is validated at registration. The
head (`Api`, `Api.Weapon`) is not an object — it cannot be called or used as a
value, and a local variable named `Api` simply shadows it with no false match.
Flat and dotted names coexist, including under a shared head.

`DefineMethod` **throws** on a duplicate method name within one API class instead
of silently overwriting the earlier registration; the same name in different API
classes is fine.
````

### 2.6 Вставить новый раздел ПОСЛЕ «Dotted API names» (см. 2.5)

````markdown
## API constants (named values, no call)

Games need to hand scripts named VALUES as well as calls — content ids, keys, tags:

```csharp
host.Api("Api.PartsCatalog.Weapon.Grip")
    .Const("sword_1h_just_01", "weapon.grip.sword_1h.just.01", doc: "one-handed grip")
    .Const("axe_2h_heavy_01",  "weapon.grip.axe_2h.heavy.01");
```

```
readonly string grip = Api.PartsCatalog.Weapon.Grip.sword_1h_just_01;
```

The value is a literal (`bool/int/float/double/string`) or an enum member — the
same encoder as struct field defaults and kind constant defaults. Anything else is
rejected at registration; hand those out through a method.

Why not a zero-argument method returning a literal, which is what this used to be:

- it reads as "something is computed here", which is a lie in a recipe;
- every read costs a host call through a delegate, and there is nothing to compute;
- the registry grows a `HostMethodInfo` plus a delegate per name — a parts catalogue
  is dozens of them.

A constant occupies no delegate slot and the compiler folds it into a literal, so no
`CallHost` survives in the bytecode.

Within one API class a name is either a method or a constant — registering the same
name as both throws, in either order.

In the manifest constants sit next to methods:

```json
"apis": [
  { "name": "Api.PartsCatalog.Weapon.Grip",
    "methods": [],
    "consts": [
      { "name": "sword_1h_just_01", "type": "string",
        "value": "weapon.grip.sword_1h.just.01", "doc": "one-handed grip" }
    ] }
]
```

The `doc` of a constant — like `summary` on events and methods, `doc` on kind
constants, struct fields and class properties — is what the editor shows on hover.
````
