# KhaozEngine.Catalog.GameTypes

The thirteen game-shaped content types most RPG-shaped worlds re-author from scratch, as stable ids, stable
keys, ordered field schemas, row codecs and one registration call each over `KhaozEngine.Catalog`. GPU-free,
zero third-party dependencies, part of the `KhaozEngine.Foundation` umbrella.

**All thirteen types are here with their schemas, codecs, registration, validators and the cross-type
sweep.** `GameContentTypes.Register` puts the whole set on a registry in one call.

The engine's own seven types (`tag`, `item`, `stat`, `loot_table`, `loot_entry`, `base_socket`,
`item_category`) are the shapes every catalog needs. These thirteen are the next layer up: the shapes a world
with food, equipment, shops, drops, gathering, crafting, tools and tuning knobs writes anyway. Two worlds that
author the same facts store them under the same keys and read them with the same code.

**The package owns no vocabulary.** A skill, a station, a repeat mode, an equip slot, a weapon archetype, an
npc kind and a creature kind are all raw numbers here. There is no enum, no name, no icon, no roster and no
balance number, which is the split `KhaozEngine.Items`, `KhaozEngine.Stats` and `KhaozEngine.Skills` already
draw: kernel in the engine, meaning in the game.

## The thirteen

`GameContentTypeIds` is the whole id table, ascending and contiguous from the game band floor:

| Id | Key | What one row is |
| --- | --- | --- |
| 1024 | `food` | what eating an item restores, and how long it holds the next attack up |
| 1025 | `equip_profile` | where an equippable is worn, how it swings, how long a swing takes |
| 1026 | `equip_stat_line` | one stat value of one profile |
| 1027 | `store` | one shopkeeper kind and the two rates it trades at |
| 1028 | `store_shelf` | one shelf of one store, in draw order |
| 1029 | `monster_drop` | a creature kind and the `loot_table` it rolls |
| 1030 | `gathering_node` | one standing source and what a swing at it pays |
| 1031 | `recipe` | one processing step, its skill, its station, its experience |
| 1032 | `recipe_input` | one input of one recipe |
| 1033 | `recipe_output` | one output of one recipe |
| 1034 | `tool_tier` | one tier of one ranked tool family |
| 1035 | `skill_curve` | one skill's own knobs |
| 1036 | `game_tuning` | one global knob, a scaled integer |

The ids start at 1024 because that is where `ContentRegistrationBand.Game` starts. A game that registers
these spends that block on them and authors its own types above it.

**`equip_profile` registers under `EngineContentTypes.EquipProfileTypeKey`, never a copy of the spelling.**
The engine `item` type carries an `equip_profile` key reference that is late bound: the engine writes the key
down and registers no type under it. A type under any other key is one nothing ever points at, and every
item's `equip_profile` would have to stay 0.

## What a type class carries

Every type is a static class with the same shape, so a game registers any of them the same way:

- `DefaultChunkSlots`, the id slots per chunk, which is the unit of re-download.
- `DefaultVisibility`, the type-level visibility. Only `monster_drop` is `ServerOnly`, because a client that
  can read a drop table knows every roll before it happens.
- One `<Name>Field` constant per field, the STORED spelling, which is also what a localization key is derived
  from. A duration field has both spellings as constants and a method taking the unit.
- `CreateSchema()`, or `CreateSchema(ContentDurationUnit)` on the four types with a duration.
- A nested `Codec`, the engine's positional row walk with nothing added.
- `Register(registry, validator)`, or `Register(registry, unit, validator)` on the four with a duration,
  which supplies the id, the key, the band, the visibility, the chunk slots, the schema and the codec. The
  validator is the CALLER's, and a type that ships one names it `Validator` on the type class.

Positions are deliberately NOT public. A reader resolves a field by name through `ContentFieldLookup`, once,
against the schema the loaded runtime was actually built from.

## `ContentDurationUnit`

```csharp
public enum ContentDurationUnit { Ticks, Seconds }
```

A duration is the one fact in the package a game cannot inherit. A world stepping a fixed tick stores ticks
and a wall-clock world stores seconds, so the game declares the unit and every schema factory takes it.

**It picks the field NAME and nothing else.** Four fields have two spellings:

| Type | `Ticks` | `Seconds` |
| --- | --- | --- |
| `food` | `attack_delay_ticks` | `attack_delay_seconds` |
| `equip_profile` | `attack_ticks` | `attack_seconds` |
| `gathering_node` | `respawn_ticks` | `respawn_seconds` |
| `recipe` | `base_ticks` | `base_seconds` |

Field order, kinds, reference targets, visibility, required flags, scales and the row codec are IDENTICAL
under either unit, so the choice costs a name in the generic editor and the localization key derived from
it, and never a byte of layout. There is no conversion anywhere here: reading a stored number as a span of
time is the game's, because only the game knows how long its tick is.

## Reaching a field

A reader indexes a row by position, and the position is not a game's to write down as a literal. A duration
field is named for the game's unit, and a reordered or renamed field leaves a number pointing at a
neighbour, which answers.

`ContentFieldLookup` is in `KhaozEngine.Catalog`, because nothing about it is game shaped:

```csharp
int healsIndex = ContentFieldLookup.IndexIn(runtime, foodType, FoodContentType.HealsField);
int valueIndex = ContentFieldLookup.IndexIn(runtime, tuningType, GameTuningContentType.ValueField, out int scale);
```

**A field the schema lacks is a REFUSAL, not a miss.** It throws, naming the type key and the field, so a
publish that moved a field stops a boot rather than pricing a world at zero and carrying on. Run it at reader
construction, which on a server is before a socket is open.

## `GameContentFindings`

The finding-code table, banded by content type, ascending by type id, a hundred to a band, with 1300 the
cross-type sweep's. A code is a stable token a counter, a test and a runbook key on, and it is never
reused or renumbered, which is why the whole table is written down before the rules that emit the codes.
The engine folds a game-band finding into `KEC0040` and puts the `KGT` code in the message, so this is what
an operator reads off a refused publish.

## Validators

A validator is the engine's `IContentValidator`: it is handed its own type id and a read-only view of the
whole candidate, it ACCUMULATES rather than stopping at the first defect, and it never throws for a content
reason. The engine runs it last in the sweep and folds every finding into `KEC0040` with the `KGT` code in
the message.

Eleven of the thirteen ship one, each a nested `Validator` on its type class:

| Type | Rules | Codes |
| --- | --- | --- |
| `food` | a heal above zero, a delay at or above zero, one row per item | `KGT0101` to `KGT0103` |
| `equip_stat_line` | one line per profile and stat, no tied draw position within a profile | `KGT0201`, `KGT0202` |
| `store` | one npc kind per store, no negative rate, no rate the price path overflows on | `KGT0301` to `KGT0303` |
| `store_shelf` | one draw position per store, one shelf per item, no item that has left play | `KGT0401` to `KGT0403` |
| `monster_drop` | one creature kind names one table | `KGT0501` |
| `gathering_node` | at least one life, a reachable level, a yield still in play | `KGT0601` to `KGT0603` |
| `recipe` | one list position, a payable and open skill, a station the game names, a positive duration and rate, a reachable level, a known repeat mode, a primary item still in play | `KGT0701` to `KGT0710` |
| `recipe_input` | one list position per recipe, a count above zero, no item that has left play | `KGT0801` to `KGT0803` |
| `recipe_output` | the same three rules on the product side | `KGT0901` to `KGT0903` |
| `tool_tier` | one tier per rank within a family, both scales above zero | `KGT1001` to `KGT1003` |
| `skill_curve` | one row per skill, a skill the game knows, a rate above zero when it is set | `KGT1101` to `KGT1103` |

**`equip_profile` and `game_tuning` ship none, deliberately.** `equip_profile` carries three durable numbers
and no rule a schema does not already make. Every rule about `game_tuning` is a statement about the SET of
rows or about a type that READS a knob, both of which are the cross-type sweep's, and the one rule a single
tuning row could carry, that its knob name is unique, is already the engine's `KEC0002`.

Eight of the eleven take nothing at all.

`StoreContentType.Validator` takes the REGISTRY, because the one thing it needs from outside its own type is
where the engine `item` type keeps its `value`, and **that index is read off the live registration at
validation time, never off a schema the validator built at type load.** A static index is this build's idea
of the item type rather than the one the candidate was registered against, and an engine release that moved
an item field would leave the rule silently reading its neighbour.

`StoreContentType.BasisPointDenominator` is 10,000 and `LargestSafeRateBasisPoints(largestItemValue)` is the
widest rate that prices every item in a catalog inside the 32 bit range a price is carried in. The ceiling
rule reads the ITEM rows because a rate alone cannot overflow.

Every rule here is unit-neutral. A duration field is named for the game's own clock and the rules about one
are about its SIGN, so the same validator serves `Ticks` and `Seconds` unchanged.

**`recipe_input` and `recipe_output` share one rule set read twice.** The two are separate types and carry
separate codes so a report names which side of the recipe is wrong, but the control flow is written once and
each side supplies its own codes, positions and nouns. Two copies of it would let a correctness fix land on
whichever side the next person happened to be editing.

### The game seams

Two validators need an answer only a game has, and both take it as a predicate over the raw stored number
rather than as an enum, a roster or a list.

`RecipeValidatorOptions` carries four, every one required:

| Member | Answers | Code when false |
| --- | --- | --- |
| `IsKnownRepeatMode` | does the game stand for this repeat mode number | `KGT0710` |
| `IsPayableSkill` | can experience be paid into this skill at all | `KGT0702` |
| `IsOpenSkill` | is a skill the game already accepted OPEN in this build | `KGT0703` |
| `IsNameableStation` | does the game stand for this station number | `KGT0707` |

Two SEPARATE skill predicates, because a number nothing stands for and a real skill that is not open yet
are different defects with different fixes, and they carry different codes. `IsOpenSkill` is asked only of a
skill `IsPayableSkill` already accepted, so a game never has to answer it for a number that means nothing.

`RecipeValidatorOptions.NoStation` is 0 and is never offered to `IsNameableStation`: zero is the absence
convention everywhere else in the catalog, so a row naming it is `KGT0706` rather than `KGT0707`.

`SkillCurveContentType.Validator` takes one predicate, `isKnownSkill`. It is a different question from
`IsPayableSkill`: a curve row is a statement about ANY skill the game has, including one no recipe may be
listed under. A message from it names the raw NUMBER, because a name would be the game's vocabulary.

A game whose numbers are a byte-backed enum checks the range BEFORE it casts. A cast from a long is
unchecked and would fold 256 onto the first constant rather than refusing it.

## One call for all thirteen

`GameContentTypes.Register` is to these thirteen what `EngineContentTypes.Register` is to the engine's seven.
It takes the unit and ONE options object holding every answer the package needs from the game, and every
member of it is required, so a game cannot forget a seam and leave a rule silently never firing.

```csharp
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;

var registry = new ContentTypeRegistry();
EngineContentTypes.Register(registry);

GameContentTypes.Register(registry, ContentDurationUnit.Ticks, new GameContentOptions
{
    Recipe = new RecipeValidatorOptions
    {
        IsKnownRepeatMode = value => value is >= 0 and <= 1,
        IsPayableSkill = value => MyGame.Skills.TakesRecipes(value),
        IsOpenSkill = value => MyGame.Skills.IsOpen(value),
        IsNameableStation = value => value is >= 1 and <= 3,
    },
    IsKnownSkill = value => MyGame.Skills.Exists(value),
    Sweep = new GameContentSweepOptions
    {
        MaxLevelKnob = "max_level",
        MaxChanceKnob = "gathering_max_chance_bp",
        RequiredKnobs = MyGame.Tuning.EveryKnobThisBuildReads,
    },
});
```

The per-type `Register` calls stay public. Registering only the types a world actually authors is the
ordinary case, and a game that wants a validator of its own on one type needs the narrow call.

## The cross-type sweep

`GameContentChecks` holds the seven rules no single type can state, because each one reads a row of one type
against a row of another or against a global knob.

| Code | Rule | Needs |
| --- | --- | --- |
| `KGT1301` | a shelf item a player cannot trade | the engine `item` type |
| `KGT1302` | a node quoting a chance over the global ceiling | `MaxChanceKnob` |
| `KGT1303` | a node gated on a level over the global cap | `MaxLevelKnob` |
| `KGT1304` | a recipe gated on a level over the global cap | `MaxLevelKnob` |
| `KGT1305` | a recipe with no live `recipe_output` row | nothing |
| `KGT1306` | a tool tier whose item does not carry its family tag | the engine `item` type |
| `KGT1307` | a knob the build reads with no row in a table that carries the rest | `RequiredKnobs` |

**`GameContentSweepOptions` names the knobs and nothing else.** A tuning row's key is one world's vocabulary,
so the sweep holds the rules and a game says which rows they read: `MaxLevelKnob`, `MaxChanceKnob` and
`RequiredKnobs`. **A null name disables that rule and an empty list disables the required-knob rule**, so a
world with no level cap has no rule to run rather than a rule that refuses everything or passes everything.
`GameContentSweepOptions.None` is every knob-driven rule off, and the three that read no knob still run.

`KGT1307` is ASYMMETRIC on purpose. A required name with no row is a finding, because the boot would fall
back to a default the pack does not name, and a row whose name is not required is IGNORED, because a knob a
newer build writes must not stop an older server loading the pack. It is also gated on the table being
authored at all: a candidate with no tuning rows makes no claim, and a boot falls back wholesale rather than
half way.

**The sweep rides ONE registration slot.** The engine takes one `IContentValidator` per type and hands each
of them the WHOLE candidate, and it offers a game no whole-registry slot of its own: the one pass that is
not per-type, the item-instances band, is engine code reached through a band registration a game cannot
join. So a whole-registry check mounted on all thirteen would report every defect thirteen times. It goes on
the lowest game id, `food`, COMPOSED over that type's own validator, and runs once.
`GameContentTypes.Register` wires that up, and `GameContentChecks` is public with a `beside` parameter for a
game registering by hand.

Both rules that read an ITEM row resolve their field positions off the live registry at validation time,
for the same reason the store's ceiling rule does.

Every knob is read out of the CANDIDATE. Nothing here reads a running process: a sweep that did would pass
or fail the same pack differently depending on what a server happened to have loaded.

### A game's own whole-catalog rules

`GameContentSweepOptions.GameRules` is the hook for a cross-type rule the package does not hold. Each one is
the engine's own `IContentValidator`, and it runs in the sweep's slot AFTER every rule of the package's own,
handed the same slot type id, the same candidate and the same findings list:

```csharp
Sweep = new GameContentSweepOptions
{
    MaxLevelKnob = "max_level",
    GameRules = [new MyGame.Content.QuestItemsAreNeverSold()],
},
```

A game finding reaches the report the way a package finding does, folded into `KEC0040` under the slot's type
key with the game's own code in the message. Empty, which is the default, changes nothing. **The hook rides
the sweep and cannot run without it**, which is why it is not a second slot: a game rule mounted on a type of
its own would run once per mounting, and one mounted on `food` in place of `GameContentChecks` would drop the
package's sweep. A rule that throws is the slot's throw, reported as one `KEC0040` with none of the slot's
findings kept, so a rule that can fail on content adds a finding instead. A null list or a null member is
refused when `GameContentChecks` is built.

## Registering by hand

```csharp
const ContentDurationUnit Unit = ContentDurationUnit.Ticks;
FoodContentType.Register(registry, Unit, new FoodContentType.Validator());
StoreContentType.Register(registry, new StoreContentType.Validator(registry));
MonsterDropContentType.Register(registry, validator: null);
```

**This sample drops the cross-type sweep, silently.** `food`'s slot is where the sweep rides, and passing
`FoodContentType.Validator` alone registers food's own rules and nothing else. A game registering by hand
passes `new GameContentChecks(registry, sweepOptions, new FoodContentType.Validator())` there instead.

Each `Register` supplies the id, the key, the band, the type's own visibility and chunk slots, the schema
for the unit and the codec over it. **The visibility and the chunk slots are not a caller's to choose**:
`monster_drop` registered as `Client` would put every drop row's key in the client manifest, and a wrong
slot count moves every content address, so two worlds authoring the same facts would stop agreeing about
where they live. Neither fails loudly.

The validator is a parameter the caller passes. Eleven types ship one as `Validator` on the type class, and
a game passes that, one of its own, a wrapper over both, or null. `GameContentTypes.Register` is the path
for a game that wants the whole set.

A game registers only the types it authors. Nothing here registers itself, because which of the thirteen a
world uses, which validator each one carries and what a row's numbers mean are all the game's.
