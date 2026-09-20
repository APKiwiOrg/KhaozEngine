# KhaozEngine.Catalog.GameTypes

The thirteen game-shaped content types most RPG-shaped worlds re-author from scratch, as stable ids, stable
keys, ordered field schemas, row codecs and one registration call each over `KhaozEngine.Catalog`. GPU-free,
zero third-party dependencies, part of the `KhaozEngine.Foundation` umbrella.

**All thirteen types are here. Six of them now ship a validator**: `food`, `equip_stat_line`, `store`,
`monster_drop`, `gathering_node` and `tool_tier`. The remaining validators and the cross-type sweep are
still only the `KGT` codes they will emit.

The engine's own six types (`tag`, `item`, `stat`, `loot_table`, `loot_entry`, `base_socket`) are the shapes
every catalog needs. These thirteen are the next layer up: the shapes a world with food, equipment, shops,
drops, gathering, crafting, tools and tuning knobs writes anyway. Two worlds that author the same facts
store them under the same keys and read them with the same code.

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

The finding-code table, banded by content type, ascending by type id, a hundred to a band, with 1300 held
for a cross-type sweep. A code is a stable token a counter, a test and a runbook key on, and it is never
reused or renumbered, which is why the whole table is written down before the rules that emit the codes.
The engine folds a game-band finding into `KEC0040` and puts the `KGT` code in the message, so this is what
an operator reads off a refused publish.

## Validators

A validator is the engine's `IContentValidator`: it is handed its own type id and a read-only view of the
whole candidate, it ACCUMULATES rather than stopping at the first defect, and it never throws for a content
reason. The engine runs it last in the sweep and folds every finding into `KEC0040` with the `KGT` code in
the message.

Six are here so far, each a nested `Validator` on its type class:

| Type | Rules | Codes |
| --- | --- | --- |
| `food` | a heal above zero, a delay at or above zero, one row per item | `KGT0101` to `KGT0103` |
| `equip_stat_line` | one line per profile and stat, no tied draw position within a profile | `KGT0201`, `KGT0202` |
| `store` | one npc kind per store, no negative rate, no rate the price path overflows on | `KGT0301` to `KGT0303` |
| `monster_drop` | one creature kind names one table | `KGT0501` |
| `gathering_node` | at least one life, a reachable level, a yield still in play | `KGT0601` to `KGT0603` |
| `tool_tier` | one tier per rank within a family, both scales above zero | `KGT1001` to `KGT1003` |

Four take nothing at all. `StoreContentType.Validator` takes the REGISTRY, because the one thing it needs
from outside its own type is where the engine `item` type keeps its `value`, and **that index is read off
the live registration at validation time, never off a schema the validator built at type load.** A static
index is this build's idea of the item type rather than the one the candidate was registered against, and an
engine release that moved an item field would leave the rule silently reading its neighbour.

`StoreContentType.BasisPointDenominator` is 10,000 and `LargestSafeRateBasisPoints(largestItemValue)` is the
widest rate that prices every item in a catalog inside the 32 bit range a price is carried in. The ceiling
rule reads the ITEM rows because a rate alone cannot overflow.

Every rule here is unit-neutral. A duration field is named for the game's own clock and the rules about one
are about its SIGN, so the same validator serves `Ticks` and `Seconds` unchanged.

**The remaining validators and the cross-type sweep are not here yet.** A game passes its own or passes null
for the other seven types.

## Usage

```csharp
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;

var registry = new ContentTypeRegistry();
EngineContentTypes.Register(registry);

const ContentDurationUnit Unit = ContentDurationUnit.Ticks;
FoodContentType.Register(registry, Unit, new FoodContentType.Validator());
StoreContentType.Register(registry, new StoreContentType.Validator(registry));
MonsterDropContentType.Register(registry, new MonsterDropContentType.Validator());
```

Each `Register` supplies the id, the key, the band, the type's own visibility and chunk slots, the schema
for the unit and the codec over it. **The visibility and the chunk slots are not a caller's to choose**:
`monster_drop` registered as `Client` would put every drop row's key in the client manifest, and a wrong
slot count moves every content address, so two worlds authoring the same facts would stop agreeing about
where they live. Neither fails loudly.

The validator is a parameter the caller passes. Six types ship one as `Validator` on the type class, and a
game passes that, one of its own, a wrapper over both, or null.

A game registers only the types it authors. Nothing here registers itself, because which of the thirteen a
world uses, which validator each one carries and what a row's numbers mean are all the game's.
