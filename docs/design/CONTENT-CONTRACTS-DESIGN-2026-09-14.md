# Content contracts: the shared ground under Scope A and Scope B

**Status:** APPROVED BY THE OWNER AT GATE 0 on 2026-09-15, and BINDING ON BOTH SPECS. AMENDED after both
specs were drafted, under the section 18 rule that a spec brings a needed change back here first: 4.7 gains
an asset reference value kind, 5.1 gains a one-time import exception that 6.5 now cross-references, 3.2
gains `KhaozEngine.ItemInstances.Journal`, 9.8 and 9.9 make the affix list canonical ascending by mod id,
9.6 restates the payload cap against the deepest legal item rather than the typical one, and 10.5 says the
fail-closed rule binds the content version and not the code a row names. Nothing here is implemented yet.
This document exists so the two parallel specs cannot contradict each other. Scope A is the versioned
content catalog, [#882](https://github.com/APKiwiOrg/KhaozEngine/issues/882). Scope B is
owned item instances, affixes, sockets, crafting and the stat evaluation base,
[#884](https://github.com/APKiwiOrg/KhaozEngine/issues/884). Consumers are
[Grimhollow #208](https://github.com/APKiwiOrg/Grimhollow/issues/208) and
[Ruinborne #465](https://github.com/APKiwiOrg/Ruinborne/issues/465). Neither spec is implemented before
the owner approves both.

Every rule below is written to be coded against: types, widths, byte order, ranges and reserved values.
Each contract section carries the rule, the engine precedent it follows or deliberately departs from with
the file cited, a short rationale, an explicit note on whether the decision is expensive to change once
data exists, and the gate 0 answer wherever the draft carried a question.

The file-and-line citations come from three read-only surveys taken on 2026-09-14 against
KhaozEngine `1eb60de7`, Grimhollow `36f0139a` and Ruinborne `1b85e1aa`. Where a survey and this document
disagree, the survey is the fact and this document is the decision.

## 1. Purpose, scope, and the two programs

### 1.1 What this document is

Scope A and Scope B are being specified at the same time by different authors. They share an id space, a
byte format, a version stamp, a validator, a visibility vocabulary and a connect door. If each spec
invents its own, the first integration discovers a contradiction that costs a re-spec. So the shared
pieces are decided here, once, before either spec is written.

A spec may refine anything below. A spec may not contradict anything below. Section 18 says what happens
when one needs to.

### 1.2 What this document is not

It is not a design for either program. It says nothing about the authoring store's schema, the publish
pipeline's stages, the crafting primitive set, the affix table shape, the pack transport or the admin
console. Those are the specs' work. Where a contract constrains one of them, it says so and stops.

### 1.3 Owner decisions, restated

Every decision below is the owner's, taken during scoping on 2026-09-14. Each names where it was said.
These are binding on both specs and on this document. Where a contract below had to choose something the
owner did not decide, the choice is marked as a recommendation and repeated in section 17.

1. The content system is ENGINE owned, and it is authored in a DATABASE rather than in git files.
   (#882 body, "Owner decisions", 2026-09-14.)
2. Plan for about 50,000 item definitions. The earlier figure of 1,000,000 was a deliberate stress test,
   and the design must still hold at that number. (#882 comment 3, 2026-09-14.)
3. Owned items number in the MILLIONS, and many carry crafted properties. Instances and affix crafting
   are the heavy part of the work, not the catalog. (#882 comment 3 and #884 body, 2026-09-14.)
4. Per-instance item properties are required. The owner's worked example is a `Greatsword` owned by one
   player carrying a `fine crafted` property worth +10 strength and durability 90 of 100.
   (#882 comment 2, 2026-09-14.)
5. An item may carry up to six affix rolls, plus enchantments, durability, quality and similar.
   (#884 body, 2026-09-14.)
6. All tunable content is versioned TOGETHER: items and their values, stats, affix and rarity tables,
   stores and drop tables, and the skilling data (level requirements, gathering nodes, recipes, tool
   tiers). Stores and drop tables living in the same versioned content as items was called out as
   important. (#882 body and #882 comment 2, 2026-09-14.)
7. A new item whose model and art already ship with the client needs NO client release.
   (#882 body and comment 2, 2026-09-14.)
8. v1 applies a new content version at SERVER RESTART. There is no live apply.
   (#882 body and comment 2, 2026-09-14.)
9. There are separate worlds players hop between eventually. There is one world for now, and no staging
   environment. (#882 comment 2, 2026-09-14.)
10. Publishing builds an immutable, compressed, content-addressed pack. The join message carries only the
    version identity. Clients fetch a missing pack over HTTP or a CDN and cache it by hash. Servers load
    the same pack rather than querying SQL. This replaced the original chunked-push-on-join proposal,
    which does not scale. (#882 comment 1, 2026-09-14.)
11. Banks hold up to about 1,000 stacks. (#884 body, 2026-09-14.)
12. Sockets: yes. Links: no. (#884 body, 2026-09-14.)
13. Stat depth comparable to PoE eventually. v1 must be a strong base for it, not the full system.
    (#884 body, 2026-09-14.)
14. Crafting operations are engine primitives composed in DATA, plus game-registered CODE for exotic
    operations. The full currency, fossil and essence breadth is not in v1. (#884 body, 2026-09-14.)
15. Crafting outcomes must come from a SECRET random source, and journal events record the RESOLVED item
    rather than a seed. Grimhollow's constant gameplay seeds are the motivating defect
    ([Grimhollow #214](https://github.com/APKiwiOrg/Grimhollow/issues/214)). (#882 comment 3, 2026-09-14.)
16. Paged containers and coalesced commits are in scope, because a journal commit rewrites a changed
    section WHOLE and a bank of affixed items would amplify writes. (#882 comment 3, 2026-09-14.)
17. An instance that fails validation is QUARANTINED, never deleted. (#884 body, 2026-09-14.)
18. A missing or invalid active content version fails the boot CLOSED. There is no runtime fallback to
    code defaults. (#882 body, 2026-09-14.)

### 1.4 Owner framing, 2026-09-15

At gate 0 the owner framed the game as a mix of OSRS, Tibia, Path of Exile and Mortal Online, and framed
the item work against that mix.

- **PoE-level complexity is the eventual target, not the v1 target.** The number of mods and the variance
  of items are meant to reach that depth over time. What is built now is the SCAFFOLDING able to carry it,
  extended gradually as the game progresses. PoE's own mods are never copied verbatim, so neither spec may
  assume a PoE mod table, a PoE currency set or PoE names as data.
- **The item model has to be right now, because it is the part that must be extensible.** A model that
  cannot grow a field is the failure being designed against, which is why section 9's tagged encoding, the
  reserved ranges in 4.3 and 9.2 and the reserved flags field in 9.9 are contract rather than convenience.
- **One source of truth for item definitions, and it is the DATABASE.** Seeding aside, a definition exists
  in the authoring store and nowhere else. There is no code-constant catalog, no authored file and no
  per-consumer copy the database is reconciled against.
- **Server authoritative, edited in each game's admin app.** Tags, rolls, rollable mods, values, the item's
  3D model reference and every other field are authored there, which is what the field schema of section
  4.7 exists to render, validate and audit.

### 1.5 Coverage of the four reference games

The mix is a coverage test these contracts have to pass, so each reference game's item is mapped onto the
model once, here, and neither spec has to re-derive it.

**OSRS.** An item is a definition plus a count and carries no per-instance state at all, so its instance id
is 0 and its payload is empty. That is byte for byte what `ItemStack(int ItemId, int Count)` stores today
(section 6.1, `KhaozEngine.Items/ItemContainer.cs:9-17`), so an OSRS-shaped catalog costs nothing over the
current format. This is the compatibility hinge the rest of the model hangs on.

**Tibia.** An item with charges, or with an upgrade tier, is a plain stack plus one or two small fields
from the ENGINE range of section 9.2. A charge count and a tier are each a single-byte varint below 128, so
a charged wand is a definition, a count and a payload of a few bytes. It gets an instance id because its
payload is non-empty (section 6.2), and it needs no mod, no roll and no socket to be expressed.

**Mortal Online.** An item crafted from chosen materials is an instance whose payload names the MATERIAL
content ids rather than the numbers derived from them, each a varint int32 content reference (section 6.3).
The resulting stats are computed by the content evaluator of section 13 from those ids. Storing the inputs
rather than the outputs is what makes a materials rebalance a publish instead of a rewrite of every crafted
item, and it is the same argument that makes a roll a position rather than a value (section 6.4).

**Path of Exile.** A rare carrying a rarity, several rolled affixes and a socket holding another item is
the worked byte example of section 9.8. It is the deepest of the four and the one the payload cap in 9.6 is
sized against.

## 2. Facts the contracts must honour today

Nothing below is a proposal. It is what the three surveys found in the code on 2026-09-14, and every
contract in this document is built to fit it or to change it deliberately and say so.

### 2.1 The engine's item model is two integers

`ItemStack` is `readonly record struct ItemStack(int ItemId, int Count)`, engine report `a-engine.md:21-33`
citing `KhaozEngine.Items/ItemContainer.cs:9-17`. Item id 0 is the empty id and never a real item.
There is no instance id, no payload, no durability, no affix and no socket field.

`ItemContainerCodec` is a 3 byte header (a `byte` version = 1, a `ushort` slot count) plus 10 bytes per
OCCUPIED slot (`ushort` slot index, `int` item id, `int` count), sparse and strictly ascending by slot,
`a-engine.md:83-101` citing `KhaozEngine.Items/ItemContainerCodec.cs:16-19`. The format is positional with
no tagged or optional fields, so a future field means a new version byte and a new reader
(`a-engine.md:1292-1301`). `Validate` returns a `string?` quarantine reason rather than throwing, and
`TryDecode` answers false (`a-engine.md:103-124`). The `ushort` slot index caps a container at 65,535 slots,
and `Encode`'s cast is unchecked (`a-engine.md:1591-1601`, a discovered-work candidate).

### 2.2 The game-message cap is 1,024 bytes and there is no fragmentation layer

`TileProtocol.MaxGameMessageBytes = 1024`, `a-engine.md:791-800` citing
`KhaozEngine.TileWorld.Netcode/TileProtocol.Frames.cs:65`. Over it the encoder THROWS and the decoder
REFUSES. There is no fragmentation or reassembly anywhere in the tile netcode, verified by grep
(`a-engine.md:818-838`). The one precedent for a larger logical payload is hand-rolled application-level
chunking on the reliable ordered channel, done once for combat events at
`TileWorld.Netcode/TileWorldServer.Tick.cs:241-259`.

The binding consumer arithmetic: Grimhollow's 56 slot bank message is 3 + 560 = 563 bytes today, and the
cap is reached at 102 occupied slots, `b-grimhollow.md:1026-1054`. At 56 slots the budget is about 18
bytes per slot before the cap. That is the tightest existing constraint on any per-instance payload that
has to travel in a container sync.

### 2.3 The journal rewrites a section whole, and its caps are hard

From `a-engine.md:437-491`, citing `KhaozEngine.WorldStore/Journal/JournalLimits.cs`:

| Limit | Value | Const line |
|---|---|---|
| Projection section bytes | 2 MiB | `JournalLimits.cs:16` |
| Projection sections per stream | 64 | `JournalLimits.cs:12` |
| Aggregate projection bytes per stream | 8 MiB | `JournalLimits.cs:21` |
| Event payload bytes | 256 KiB | `JournalLimits.cs:14` |
| Aggregate commit bytes | 8 MiB | `JournalLimits.cs:19` |

A `JournalProjectionWrite` is a WHOLE-SECTION REPLACEMENT and never a patch
(`a-engine.md:437-460`, `KhaozEngine.WorldStore/Journal/JournalProjectionWrite.cs:5-39`). A host may
configure the caps only DOWNWARD. Every byte array in the journal API is cloned on the way in and cloned
again on EVERY property read (`a-engine.md:1602-1616`, `JournalLimits.cs:107` and `:137`), and the
recorded SQLite baseline allocates 42,365 bytes per operation
(`a-engine.md:607-641`). Grimhollow's own bank is one section under the same 2 MB cap,
`b-grimhollow.md:503-520` citing `Grimhollow.Shared/Persistence/GrimhollowJournalContracts.cs:127`.

### 2.4 The connect door is a nest of labelled layers

`HandshakeToken` layers are `[magic 5][labelLen:byte][label utf8][inner]`, magic `00 4B 45 56 31`, and
`ConnectionGate.Wrap` composes gates outside-in: version, world, the game's token auth, then the ban check
(`a-engine.md:839-879`, `KhaozEngine.Netcode/HandshakeToken.cs:16-33`,
`KhaozEngine.Netcode/ConnectionGate.cs:159-180`). Refusal tokens are STABLE WIRE TOKENS the client
localizes itself, for example `ke:world-mismatch:<serverHash>|<clientHash>`.
`WorldIdentityGateAuthenticator` is the template: unwrap one layer, compare ordinal, refuse with a token
carrying both sides, otherwise delegate inward.

Grimhollow already carries four layers, protocol version, world hash, catalog hash and skilling config
hash, `b-grimhollow.md:420-434`. The two game hashes are SHA-256 truncated to 16 lower hex characters
(`b-grimhollow.md:404-417`, `Grimhollow.Shared/Skilling/SkillingConfig.cs:510-511`).

### 2.5 The engine's 64 bit id allocator is node prefixed

`NetIdAllocator` packs `(node << 48) | counter`, `CounterBits = 48`, `NodeBits = 16`, counter starting at
1, never recycled, throwing rather than wrapping at `2^48 - 1`, with `NextValue` persisted per node so the
allocator resumes above every id ever handed out (`a-engine.md:1158-1175`,
`KhaozEngine.Replication/NetIdAllocator.cs:14-60`). It is not thread safe and is called from the owning
server thread.

### 2.6 `TileWorldHash` is the canonicalisation precedent

SHA-256 rendered as lower hex, a `public const int SchemeVersion = 2` folded into every digest, a domain
string per digest (`ketw/`, `ketw/foliage/`, `ketw/catalogs/`, `ketw/worldcat/`), invariant-culture number
formatting, LENGTH-PREFIXED strings written `"{len}:{value} "` with a bare `"- "` for null, collections
sorted by id before digesting, and authored tag ORDER preserved because sorting would call two different
files one archetype (`a-engine.md:1183-1237`, `KhaozEngine.TileWorld/TileWorldHash.cs:19-185`).

### 2.7 Localization is `StringId` over .resx satellite catalogs

`StringId` wraps one non-empty string with ordinal equality and DELIBERATELY has no implicit conversion
from `string`. `IStringCatalog.Get(key)` NEVER throws for a missing key: it returns the key itself as a
visible placeholder. `LocalizationManager` is built from a `System.Resources.ResourceManager`, so catalog
loading today is .resx satellite assemblies with no JSON loader, no hot reload and no per-pack merge
(`a-engine.md:1097-1140`, `KhaozEngine.App/StringId.cs:11-41`, `KhaozEngine.App/IStringCatalog.cs:9-49`).
There is no enforced key convention in the engine. Grimhollow's is `item.<key>.name` and
`item.<key>.examine`, with two keys already off convention, `item.pinelogs.name` and `item.oaklogs.name`
(`b-grimhollow.md:765-772`).

### 2.8 Both random sources are deliberately predictable, and there is no seam

`DeterministicRng` is a sealed class whose full `State` is public for save and resume and which promises
the same seed gives the same sequence forever (`a-engine.md:1238-1258`,
`KhaozEngine.Primitives/DeterministicRng.cs:16-72`). `TileActorRandom.For(seed, netId, tick)` derives every
draw from three values a client can observe or guess. There is NO `IRandom` interface, no seam between
them, and no cryptographically seeded gameplay source anywhere in the engine
(`a-engine.md:1259-1269`, `a-engine.md:1572-1588`). Grimhollow seeds every gameplay stream with an integer
literal (`b-grimhollow.md:786-836`) and Ruinborne's loot rolls come from an UNSEEDED `System.Random`
created once at server start (`c-ruinborne.md:718-734`, `Ruinborne.Server/Program.cs:473`).

### 2.9 `StatSet` is dense float channels with two modifier kinds

The fold is `Value = (Base + sum Flat) * max(1 + sum Percent, MinimumScale)`, exactly two modifier kinds
on one struct, both summed additively, with the percent sum becoming ONE multiplier
(`a-engine.md:164-183`, `KhaozEngine.Stats/StatSet.cs:232-233`, `KhaozEngine.Stats/StatModifier.cs:13`).
A stat is a dense int CHANNEL INDEX into a `float[]`, fixed at construction, with no string id, no
registry and no metadata (`a-engine.md:229-237`). Source insertion order is load bearing because float
addition is not associative, and a `Dictionary` was deliberately rejected (`a-engine.md:212-228`). There is
no multiplicative kind, no override, no clamp and no condition (`a-engine.md:1558-1571`).

### 2.10 Grimhollow's `ValidateContainer` throws on an unknown or reslotted item

`GrimhollowJournalContracts.ValidateContainer` is a HARD REFUSAL, not a degrade: it throws on an unknown
item id, a non-positive count, a stacked non-stackable, a duplicate stack, a worn slot with count above
one, a duplicate worn item, and a worn item whose roster slot does not equal the slot it sits in
(`b-grimhollow.md:545-556`, `Grimhollow.Shared/Persistence/GrimhollowJournalContracts.cs:483-524`).
Retiring or reslotting an item therefore makes every stored container carrying it undecodable. That is
the single load-bearing consumer constraint on the remap rules in section 8.

### 2.11 Grimhollow's connect door carries hashes the content version must absorb

Today the fourth layer is the skilling config hash only, and the economy table arrives AFTER the door as a
game message so a disagreement about prices is not a refusal at all (`b-grimhollow.md:443-493`). The
in-flight `feature/item-drop` branch changes the fourth layer to a HYPHEN-JOINED pair,
`GrimhollowGameDataHash.Current => Combine(GrimhollowSkilling.Current.Hash,
GrimhollowItemProperties.Current.Hash)`, joined rather than rehashed so an operator reading a refusal can
see which half moved, and never using a colon because `GrimhollowConfigGate.TryParseMismatch` splits its
refusal token on colons (`b-grimhollow.md:1332-1358`). It also introduces a THIRD authored content
mechanism, `assets/config/items.jsonc`, with a `tradable` flag per live item and a fail-closed
every-live-item-needs-a-row rule. Section 7.6 says how the engine absorbs both.

### 2.12 Ruinborne's item id is a string across seven tables, and its instance id is a derived GUID

`item_def.item_id` is `NVARCHAR(64)` and the clustered primary key, and the same string is the foreign key
on `character_inventory`, `item_stat`, `weapon_def`, `item_ability_modifier`, `loot_table_entry`,
`world_entity` and `economy_ledger`, seven tables (`c-ruinborne.md:859-875`). There is no integer id and no
allocator. The wire index is DERIVED from list position in one byte, and the order differs between the SQL
source and the code-default source (`c-ruinborne.md:517-536`). `character_inventory.journal_item_id` is a
deterministic version-5 GUID, `OwnedItemId`, derived from `$"{characterId}/{inventoryId}"` and friends, and
it is what the client names in a use or destroy request so a stale catalog position cannot authorize a use
(`c-ruinborne.md:949-962`). `instance_json` exists as a column and has NEVER been written: its only live
meaning is a null-ness flag that suppresses stack merging (`c-ruinborne.md:113-132`,
`c-ruinborne.md:565-589`).

The lowered stack cap is the lesson that shapes section 8's rule kind 4: lowering `item_def.max_stack`
leaves every over-cap owned stack intact, invisible and permanent, because the merge candidate query is
`AND [quantity] < @max` and nothing ever re-checks existing rows (`c-ruinborne.md:537-564`).

## 3. Package naming and layering

### 3.1 The naming problem

`KhaozEngine.Content` already exists and is NOT a content catalog. It is a JSON config loader
(`ConfigLoader.Load<T>`) plus a JSON Schema validator, 143 lines of source across two files, depending on
`JsonSchema.Net` (engine report `a-engine.md:250-299`). It has literally zero occurrences of "hash" or
"version" outside its csproj. Overloading the name would put a 50,000 definition versioned catalog and a
disk-then-embedded config reader behind one package id, and the engine report already flags the collision
as a naming decision to take in the contracts rather than after (`a-engine.md:1651-1655`).

**Rule.** The new packages do NOT reuse `KhaozEngine.Content`, and `KhaozEngine.Content` is not renamed,
deprecated or absorbed. It keeps doing its job. The new names are chosen so that a reader scanning the
README catalog cannot confuse the two.

### 3.2 The package set

| Package | Contains | Umbrella |
|---|---|---|
| `KhaozEngine.Catalog` | The type registry, id spaces, the row codec seam, the validator contract, the manifest and chunk hash, the remap rule format, the read-side runtime (arrays indexed by id, atomic swap). Pure .NET. | `Foundation` |
| `KhaozEngine.Catalog.Authoring` | The authoring store contract, draft and change set, audit, publish, diff. Pure .NET, no SQL. | `Server` |
| `KhaozEngine.Catalog.Sqlite` | The SQLite authoring provider. Sits on `KhaozEngine.Sqlite`. | none, opt-in sibling |
| `KhaozEngine.Catalog.SqlServer` | The SQL Server authoring provider. | none, opt-in sibling |
| `KhaozEngine.ItemInstances` | Scope B's instance record, the tagged field codec, sockets, the container page codec, the generator and the crafting framework. Depends on `KhaozEngine.Items` and `KhaozEngine.Catalog`. | `Foundation` |
| `KhaozEngine.ItemInstances.Journal` | The container page commit builder and the tick-bounded commit batch, which turn instance operations into `JournalCommit`s. Depends on `KhaozEngine.ItemInstances` and `KhaozEngine.WorldStore`. | `Server` |
| `KhaozEngine.Catalog.Netcode` | `ContentIdentityGateAuthenticator` and the content-version handshake layer. Depends on `KhaozEngine.Netcode` and `KhaozEngine.Catalog`. | `Server` |

**The random seam is not in this set and is not a new package.** `IRandomSource` and its two
implementations go in the EXISTING `KhaozEngine.Primitives`, which already owns `DeterministicRng` and
sits below every package in the table, so both programs reach the seam without a new dependency edge
(section 14.1 carries the rule and the cycle it avoids).

**Engine precedent.** The layering rules are the README's, surveyed at `a-engine.md:1373-1407`. Three of
them bind here. A pure catalog with no SQL belongs in `Foundation` beside `Items` and `Stats`, and is
"Pure .NET" as long as it defines its own codec rather than borrowing `Content`'s JsonSchema.Net.
Anything with a SQL provider is an opt-in SIBLING pair and is NEVER bundled in an umbrella, stated twice
in the README (lines 101 and 104) and followed by both `WorldStore` and `Commerce`. `Foundation` cannot
reference `Server`-side packages, which is why the handshake gate is its own small package rather than a
type inside `KhaozEngine.Catalog`, and why the commit builder is its own package rather than a type inside
`KhaozEngine.ItemInstances`. Composing a `JournalCommit` needs `KhaozEngine.WorldStore`, which is a server
package, and the other half of the same split is the client: a client decodes an instance payload, and it
must be able to do that with no journal type and no store dependency anywhere in its graph.

**Rationale.** `Catalog` is the word the engine already uses for this shape (`IProductCatalog`,
`TileWorldCatalogs`), it does not collide with `Content`, and it reads correctly in a package table next
to `Items` and `Stats`. Splitting authoring away from the read side matters because a game CLIENT needs
the read side and must never pull a database dependency: the client half of the pack has to decode
without any authoring type in the graph.

**Options weighed.**

| Option | Name clarity | No-rename cost | Client graph | Total |
|---|---|---|---|---|
| Rename `Content` to `Content.Config`, take `Content` for the catalog | 8 | 2 | 8 | 18 |
| One `KhaozEngine.GameContent` package holding everything | 5 | 9 | 3 | 17 |
| `KhaozEngine.Catalog` set above | 9 | 9 | 9 | 27 |

Recommendation: the `Catalog` set. The rename option is the only one that reads better in isolation, and
it costs every consumer a breaking package id change for a cosmetic gain.

**Expensive to change once data exists: no, because** a package name change is a compile-time break on
consumers and carries no data migration. It is expensive to change once RELEASED, which is a different
and lesser cost, and the reason to settle it here.

**Open question for the owner.** None. The naming is the contracts author's call, recorded here rather
than in section 17, which holds only the questions that went to the owner.

## 4. Content type registry

### 4.1 What a content type is

A content type is a named family of rows sharing one id space, one row codec, one validator and one
visibility default. Item bases are a type. Stats are a type. Mods are a type. A game's stores are a type.

### 4.2 Registration

```
RegisterContentType(
    ushort  typeId,          // stable numeric id, see 4.3
    string  typeKey,         // stable string key, see 5.3 character rules
    IContentRowCodec codec,  // encode and decode one row
    IContentValidator validator,
    ContentFieldSchema schema,             // the type's fields, see 4.7
    ContentVisibility defaultVisibility,   // Client or ServerOnly, see 11
    int chunkSlots)          // see 4.5
```

Registration happens ONCE, at process start, before any pack is loaded. The registry is frozen when the
first pack loads and a later registration throws. That is the same shape as
`ReplicationRegistry.Register<T>(typeId, write, read)`, which the tile protocol uses to bind a component
type id to a codec pair (`a-engine.md:880-916` citing `TileProtocol.Components.cs:122`).

### 4.3 Type id: `ushort`, with reserved ranges

| Range | Owner | Count |
|---|---|---|
| `0` | Reserved, never a valid type id | 1 |
| `1` to `255` | ENGINE types (item base, stat, loot table) | 255 |
| `256` to `1023` | SCOPE B types (mod, rarity rule, unique template, socket type, crafting currency, rare name word) | 768 |
| `1024` to `65535` | GAME types (stores, gathering nodes, recipes, tool tiers, skill curves) | 64,512 |

A `ushort` matches `TileProtocol`'s game-message `kind` and the journal's projection schema version width,
and 65,535 types is far past any plausible need. The three-way split exists so an engine release adding a
type can never collide with a game's, which is the failure `ReplicationRegistry.FirstExtensionTypeId`
already prevents for components (`a-engine.md:880-916`).

**Registration order independence.** The registry is a map keyed by type id, not a list, and NOTHING
anywhere derives an ordinal from registration order. This is a direct response to Ruinborne's wire index,
which is a list POSITION cast to a byte and differs between the SQL source and the code-default source for
the same five items (`c-ruinborne.md:517-536`). Two processes that register the same types in different
orders must produce byte-identical packs and byte-identical manifests. The manifest hash sorts by type id
before digesting, following `TileWorldHash`'s sort-before-digest rule (`a-engine.md:1219-1222`).

### 4.4 What a game may and may not do at registration

MAY: register a type in the game range, supply its own row codec and validator, set its default
visibility, set its chunk size, and register a validator for an ENGINE type that runs AFTER the engine's
own (an additional constraint, never a relaxation).

MAY NOT: register into the engine or Scope B ranges, replace an engine type's codec, weaken an engine
validator, change a type's id or key after the first publish, or register the same type id twice.

### 4.5 Chunk size rule

A chunk is a contiguous id range within one type, and `chunkSlots` is the number of id SLOTS per chunk, not
the number of populated rows. It must be a power of two between 256 and 65,536, and the default is 4,096.
Chunk boundaries are therefore `floor(id / chunkSlots)`, computable without a lookup.

The reason it is a slot count rather than a row count: a chunk's identity must not move when a row is
added to it. If chunks held N ROWS, inserting one definition would renumber every chunk after it and
change every downstream hash, which is exactly the Ruinborne list-position failure in another costume.
With fixed id ranges, adding a definition rewrites ONE chunk and leaves every other chunk hash untouched,
which is what makes "download size after a one-item edit" a small number rather than the whole pack.

At 50,000 item definitions and 4,096 slots per chunk, an item base type is about 13 chunks. At 1,000,000
it is 245.

**Expensive to change once data exists: yes, because** the chunk size is folded into the manifest hash
through the chunk list, so changing it renumbers every chunk and invalidates every cached client pack.
It is changeable at a cost (one full republish and one full client re-download), so it is expensive
rather than impossible.

**Open question for the owner.** None. The default is a recommendation the specs may tune per type with
measurements from the proof spikes both issues require.

### 4.6 The tag vocabulary is a content type

**Tags are content, not strings.** A `tag` content type registers in the ENGINE range of 4.3. Its rows
carry int ids and string keys under section 5's rules, and item bases, mods, stats, stores and drop tables
reference a tag by ID. A row's tags are an ORDERED LIST of tag ids, authored order preserved and never
sorted, for the reason section 9.5 gives for socket order and `TileWorldHash` gives for
`TileObjectArchetype.Tags` (`TileWorldHash.cs:117-120`, `a-engine.md:1224-1226`): the order is authored
information, and sorting it calls two different rows one row.

One mechanism, covering every case the three surveys found:

- Spawn and drop weighting by tag, so a drop table names a tag rather than enumerating ids.
- Store rates by item class, the same lookup from the other side.
- Tool families. Grimhollow's `CanBeAHatchet` and `CanBeAPickaxe` predicates are CODE today
  (`b-grimhollow.md:56-59`), and a tag reference replaces each predicate with a row.
- Ability and modifier grouping. Ruinborne's `item_ability_modifier` holds comma-joined tag STRINGS in a
  column (`c-ruinborne.md:67-72`), which is a set membership test spelled as a substring search.

Families (5.2) and tags answer different questions and both stay. A family is a contiguous ID BLOCK, so
membership is two comparisons and the runtime array stays dense. A tag is a MANY-TO-MANY label, so one row
carries several and one tag spans several families. A sword sits in the sword family and carries the
`two_handed` and `metal` tags.

**Strings are never the tag representation.** Not in a pack, not in a payload, not on the wire, not in a
durable row. A tag KEY exists for authoring and for a log line, exactly as every other content key does
(5.3).

**Expensive to change once data exists: no for adding a tag, yes for the representation.** Adding a tag is
an ordinary publish and putting one on a row is an edit. Moving to strings, or dropping the ordering rule,
restates every row that carries tags and every digest taken over them.

### 4.7 Every type registers a field schema

Registration carries a FIELD SCHEMA DESCRIPTOR beside the codec, and it is not optional. The schema lists,
for every field the type's rows carry:

| Schema entry | Meaning |
|---|---|
| Name | The field name, under section 5.3's character rules, and the name section 12.1 derives a localization key from. |
| Value kind | One of: int, scaled int with its scale (13.1), bool, key reference to a named content type, tag list (4.6), localized text key (a MARKER carrying no stored value, defined below), asset reference (defined below), opaque bytes. |
| Reference target | For a key reference, the content type key it points at. For a tag list, that it is tags. Empty otherwise. |
| Content visibility | `Client` or `ServerOnly`, per section 11, defaulting to the type's own default. |
| Required | Whether a live row must carry a value. |

**The `localized text key` kind is a MARKER. It carries NO value in the row and NO bytes in the chunk**,
because the key is DERIVED at encode and at decode time from the type key, the content key and the field
name, by section 12.1's concatenation. An authored override does not exist, and there is no place to put
one.

The entry is still in the schema because three readers need to know the field is there. The editor shows
the derived key READ-ONLY beside the field, so an author sees exactly what a translator will be handed. The
audit and the publish diff know the field exists, so a type gaining a text field is a visible schema change
rather than a silent one. The text chunk builder of 12.4 knows which strings the ROW OWNS, which is how it
decides what goes into each per-language chunk.

The rationale for storing nothing is 12.1's, restated at the schema: an authored key rots independently of
the row it names, and Grimhollow has two keys out of thirty-five that already dropped the underscore their
item key carries, which is enough to force every downstream tool into a lookup where a concatenation would
have done (`b-grimhollow.md:765-772`). A stored key would give every type that carries text its own copy of
that failure, so the kind stores nothing and the derivation is the only path to a key.

**The `asset reference` kind is a varint length followed by that many UTF-8 bytes**, character set `a-z`,
`0-9`, `_`, `.`, `/` and `-`, at most 128 bytes. It is never a content key and it is never localized. It
exists because some fields name a FILE rather than a row: an item's icon, its world mesh and its held mesh
carry values shaped like `kit/unknown_item.glb` (`b-grimhollow.md:709`). Such a value cannot be a key,
because a key is `a-z0-9_` with no dot and no slash (5.3), and it must not fall back to opaque bytes,
because of the first reader in the list below. A generic editor cannot tell that opaque bytes are a path,
so it renders a hex box where an author wants a file name with a typeahead, and every console would then
carry a hand-maintained list of which opaque fields are really paths, which is the bespoke-screen-per-type
outcome this schema exists to prevent. The engine neither resolves nor loads the value: it is a name the
game's own asset layer looks up, and the codec checks the character set and the length and nothing else.

**Expensive to change once data exists: no for widening the character set or the byte cap, yes for
narrowing either.** Widening is additive, and every row already written still reads. Narrowing invalidates
rows an author already published, which is a retire and a re-author rather than a schema edit.

Three consumers read it, and they are why it exists rather than being a convenience:

- **A game's admin console renders a generic editor from it and validates client side against it.** That is
  what makes the owner's "edited in each game's admin app" (1.4) one editor rather than a bespoke screen per
  type. A new type gets its editor by registering.
- **The authoring store's audit records field-level BEFORE and AFTER values through it.** Ruinborne's audit
  is the lesson: its rows record THAT a row changed and carry no values at all, so an audit row cannot
  answer what the value was (`c-ruinborne.md:328-346`). A schema-driven audit names the field and both
  sides of it.
- **The publish diff is computed over it**, so "what changed between version 46 and 47" is a field-level
  answer rather than a chunk hash inequality.

**The row codec is checked against the schema at registration.** The codec declares the fields it writes,
registration compares that set against the schema, and a mismatch in either direction throws before the
registry freezes (4.2). A codec and a schema that drift give an editor writing a field nothing reads and an
audit naming a field nobody edited, so the check belongs at registration rather than at publish, and it
costs one comparison per type at process start.

**Expensive to change once data exists: no for adding a field, yes for removing one.** Adding a field is a
publish plus a codec version. Removing one strands every row already carrying it, which is the retire path
of section 8 rather than a schema edit.

## 5. Id spaces and allocation

### 5.1 The definition id

Every content row carries an `int` id, unique WITHIN its content type. Id `0` is reserved and means "no
content", matching `ItemStack`'s rule that item id 0 is the empty id and never a real item
(`KhaozEngine.Items/ItemContainer.cs:9-17`, `a-engine.md:21-33`) and `GroundMaterial`'s reservation of 0
for void (`TileWorldCatalogs.cs:25`, `a-engine.md:1176-1180`). Negative ids are invalid.

Ids are allocated by the AUTHORING STORE, never by an importer, a code constant or a file order. An id is
never reused and never deleted. A definition that leaves play is RETIRED, which is a flag on the row plus
a remap rule (section 8), and the row stays in the pack forever so a stored stack still decodes.
Grimhollow already does exactly this by hand: 18 of its 35 item ids are retired and the comment states
the rule, that retired ids are never removed so a stored stack still decodes and the player upgrade can
find it (`b-grimhollow.md:30-36` citing `GrimhollowItems.cs:133-151`).

**The one exception is a bulk import into an EMPTY database.** An import run against a store that holds no
rows for any content type MAY carry explicit ids, and the store ADOPTS them rather than allocating over
them. Immediately afterwards it sets both its reserved and its issued high-water mark above the highest
imported id PER TYPE, so every subsequent id comes from the allocator under the ordinary rule. No other
write path may ever name an id, and an import into a database that already holds a row may not name one
either. The empty-database condition is what makes the exception safe: it makes an import once-ever per
database, so there is no second import to disagree with the first and nothing to renumber afterwards. The
never-reuse guarantee is untouched, because the marks move PAST the imported range rather than into it.

This exception is also what makes 6.5's promise reachable. Grimhollow's 35 item ids already sit inside
every stored `ItemContainer` blob, and `GrimhollowJournalContracts.ValidateContainer` throws on an unknown
item id (`b-grimhollow.md:545-556`), so an import that allocated fresh ids would be a player who cannot log
in. **Expensive to change once data exists: no while every store is still empty, yes the moment one has
imported**, because a store that adopted ids cannot afterwards be told it should have allocated them
without renumbering the durable data that names them.

**Why `int` and not `long`.** `ItemStack.ItemId` is an `int` and `TileGroundItem.ItemId` is an `int`.
Widening either is a wire break and a codec break across two packages and every consumer. 2.1 billion
definitions per type is four orders of magnitude past the owner's 1,000,000 stress figure.

**Expensive to change once data exists: yes, because** every stored container, every journal projection,
every ground item and every remap rule names a definition by this id. Changing its width or its
reservation of 0 rewrites every durable byte in every consumer.

### 5.2 Families and contiguous id blocks

A FAMILY is an author-declared grouping within one content type whose members are allocated ids from one
contiguous block, so a runtime check "is this id in the sword family" is two comparisons rather than a set
lookup. Grimhollow's `CanBeAHatchet` and `CanBeAPickaxe` predicates are the shape this replaces
(`b-grimhollow.md:56-59` citing `GrimhollowItems.cs:164-170`), as is Ruinborne's bare-varchar `item_type`
column that `stat_def.sql`'s own comment complains about (`c-ruinborne.md:63-66`).

Rules:

- A family reserves a block at creation. Block size is declared then, must be a power of two between 16
  and 65,536, and cannot be changed later.
- A block is aligned to its own size. The block holding ids `[base, base + size)` has
  `base % size == 0`. Alignment makes membership `(id & ~(size - 1)) == base`.
- **Block boundaries do NOT have to align to chunk boundaries**, and the contract says so explicitly so
  neither spec assumes otherwise. A chunk is a transport and hashing unit sized for download economics
  (section 4.5). A family is an authoring and gameplay unit sized for how many swords there will be.
  Coupling them would force one of the two to be the wrong size. A family smaller than a chunk shares a
  chunk with other families, and a family larger than a chunk spans several. Both are fine.
- **When a block fills, a SECOND block is reserved for the same family**, and the family carries an
  ordered list of blocks rather than one. The alternative, growing the block in place, is impossible once
  the next block is allocated, and reserving huge blocks up front wastes the dense-array runtime the
  server depends on. A family with several blocks costs one comparison pair per block on a membership
  test, and the runtime caches the list, so the cost is a short loop rather than a set lookup.
- A family may not be deleted. It is retired like a definition.

**Expensive to change once data exists: yes for the block model, no for adding a block.** Reserving an
extra block is an ordinary publish. Changing a family's declared block size after it holds ids would move
every id in it.

### 5.3 String keys

Every content row also carries a string KEY, unique within its type. The key is what an author, a config
file, a debug console, an admin page, a localization key and a cross-content reference use. It is NOT what
durable player data or the wire use, which is always the int id.

- Character set: `a-z`, `0-9` and `_`. Lower case only, no leading digit, no leading or trailing
  underscore, no double underscore.
- Maximum length: 64 characters. That matches Ruinborne's `NVARCHAR(64)` primary key
  (`c-ruinborne.md:49-60`) so an adoption never has to truncate, and it is half the journal's 128
  character identifier cap (`JournalLimits.cs:23`, `a-engine.md:361-372`).
- Comparison is ORDINAL, always. Never culture-aware, never case-insensitive. The engine's typed string
  ids all do this (`StringId`, `AccountId`, `CurrencyId`, `a-engine.md:1097-1110` and `:679-687`), and
  both SQL providers pin key columns to a binary collation precisely because a case-insensitive database
  default silently merged two accounts once (`a-engine.md:749-757`). A content key column in either
  authoring provider MUST be `COLLATE Latin1_General_100_BIN2` on SQL Server and `TEXT COLLATE BINARY` on
  SQLite.
- A key is IMMUTABLE once published. Renaming is not an edit: it is a retire plus a new row plus a remap
  rule. Grimhollow's config keys already behave this way, pinned by a test asserting each literal
  (`b-grimhollow.md:1158-1184`).

**Expensive to change once data exists: yes for immutability, no for the character set.** Widening the
character set later is additive and safe. Narrowing it, or allowing a rename, breaks every localization
key, every icon filename and every authored cross-reference derived from a key.

### 5.4 Legacy ids for keep-legacy mods

Scope B's keep-legacy rule copies a mod to a new id that can never be generated or crafted again
(#884 body, item 11). Those legacy ids live in the SAME id space as ordinary mods, allocated by the same
authoring store, and are distinguished ONLY by a `Legacy` flag on the row plus the remap rule that moved
items onto them. There is no separate legacy id range.

The reason is that a separate range is a second id space, and every reader then has to know which space an
id came from before it can look the row up. A flag on the row is read by exactly the two places that care
(the generator, which skips legacy rows, and the crafting guard, which refuses them) and is invisible
everywhere else. It also means a legacy mod's stat lines and display text resolve through the ordinary
path with no special case.

### 5.5 What a consumer's existing string id becomes

**Contract: a consumer's existing string item id is a KEY, not an ID.** Ruinborne's
`item_def.item_id NVARCHAR(64)` becomes the engine content key. The int32 definition id is NEW and is
allocated by the authoring store at import. This is stated here because it is the single most likely place
the two adoption specs would diverge: Scope A could reasonably read "Ruinborne already has ids" and keep
them, and Scope B could reasonably read "instances reference an int32" and invent a mapping table.

What follows from it, with the survey's evidence:

- Ruinborne's seven foreign keys onto `item_def.item_id` (`c-ruinborne.md:859-867`) keep working during
  adoption, because the key survives. The int id is added beside it.
- Ruinborne's derived one-byte wire index (`c-ruinborne.md:517-536`) is DELETED and replaced by the int
  definition id. That removes the 255-item ceiling, the two disagreeing orders, and the renumber-on-drop
  hazard that forces the whole-catalog rejection at `ItemCatalogContentLoader.cs:137-148`.
- Grimhollow's `public const int ItemId.*` constants (`b-grimhollow.md:24-29`) are already int ids in the
  right shape, and its `ConfigKeys` table is already the key table. Its adoption is an import that
  preserves both, so no stored container changes value.
- The ADOPTION PLAN for each consumer belongs to that consumer's adopt issue and to the two specs. This
  contract owns only the mapping rule.

**Expensive to change once data exists: yes, because** choosing the other way (string as the durable id)
would put a 64 character string in every container slot, every ground item and every journal projection
entry, which the 1,024 byte message cap alone rules out.

**Decided at gate 0:** the engine-allocated int id is shown to authors, read-only beside the key
(section 17, decision 3).

## 6. How instances reference content

### 6.1 The two identifiers on an owned item

| Field | Type | Meaning |
|---|---|---|
| definition id | `int` | The content row. 0 is empty, per section 5.1. |
| instance id | `long` | The owned item's durable identity. 0 means NO INSTANCE. |

A definition id with instance id 0 is a plain stack, exactly what `ItemStack(int, int)` is today. That is
the contract's compatibility hinge: every existing stored container is already expressible in the new
model with no per-slot cost, because instance id 0 is the absence of one.

### 6.2 Instance id allocation

The instance id is node prefixed exactly like `NetIdAllocator`: `(node << 48) | counter`, 16 node bits and
48 counter bits, counter starting at 1, never recycled, throwing rather than wrapping at `2^48 - 1`, with
the packed high-water mark persisted per node so an allocator resumes above every id ever handed out
(`KhaozEngine.Replication/NetIdAllocator.cs:14-60`, `a-engine.md:1158-1175`). Node 0 makes a
single-process server's ids numerically identical to a plain counter, which is the property that lets a
solo host and a sharded host share one format.

The engine report's own conclusion is that this is the scheme an owned-item id at millions of rows should
copy, and that `2^48` per node is the headroom argument already written down (`a-engine.md:1173-1175`).
Scope B SHOULD reuse the `NetIdAllocator` type rather than writing a second one, but it needs a SEPARATE
INSTANCE of it with its own persisted high-water mark, because a net id and an item instance id are
different spaces that must not share a counter.

**A range is RESERVED durably BEFORE any id in it is issued.** The allocator persists the new high-water
mark first and only then hands out ids from below it, so the worst a crash can do is skip a block of ids
that were never issued, and it can never reissue one. The ORDER is the contract, not the batch size:
persisting after issuing leaves a window in which a crash hands the next boot an id it has already put on
an item, and a duplicate instance id is the single failure this section exists to prevent. `NetIdAllocator`
persists its packed high-water mark for exactly this reason (`a-engine.md:1158-1175`), and the order is
written down here because a batching optimisation is where it gets quietly inverted.

**Which items get an instance id.** Any item carrying properties, per the owner's ruling that per-instance
properties are required and that an item may carry up to six affix rolls, enchantments, durability and
quality (#882 comment 2 and #884 body, 2026-09-14). Concretely: an item gets an instance id when its
encoded property payload is non-empty, or its definition declares durability, sockets or any per-instance
field. An item with no properties gets instance id 0 and stays a plain stack forever. The rule is stated
as a property of the ITEM rather than of the definition so a definition can gain a property later without
retroactively giving every stored copy an id it does not have.

**Expensive to change once data exists: yes, because** the instance id is the durable name a trade, a
craft, a socket reference and a journal event all use. Changing its width or its allocation scheme would
have to rewrite every owned row in every consumer.

### 6.3 Property fields carry content ids as varint int32

Inside an instance payload, a reference to a content row (a mod id, a socket type id, a rare name word id,
a stat id) is encoded as an int32 in VARINT form, not as four fixed bytes. Definition ids are allocated
densely from low numbers, so a varint costs 1 byte below 128 and 2 bytes below 16,384. At 50,000
definitions the common case is 2 to 3 bytes rather than 4, and the saving multiplies by six affixes times
a thousand bank stacks.

The varint definition is in section 15 and is shared by every format in these two programs.

### 6.4 Rolls

A roll is stored as a `ushort` POSITION in the mod tier's range, not as the rolled value. Position 0 is the
bottom of the range and 65,535 is the top.

**Why a position and not a value.** Scope B's keep-legacy rule has two options when a published version
changes a mod's ranges, and the default is RESCALE: existing rolls keep their POSITION in the new range
(#884 body, item 11). That is only expressible if the position is what was stored. Storing the value would
make a rescale a re-roll, which is the thing the owner's rule exists to avoid.

**The mapping formula, integer math, identical on both sides.**

```
value = min + (int)(((long)position * (max - min) + 32767) / 65535)
```

where `min` and `max` are the tier's inclusive integer bounds from content, `position` is the stored
`ushort`, and the `+ 32767` is round-half-up on the division. Every term is integer. There is no float
anywhere on this path, which is section 13's determinism rule.

Worked example. A tier with `min = 10`, `max = 40`, so `max - min = 30`.

| position | numerator | value |
|---|---|---|
| 0 | `0 * 30 + 32767 = 32767` | `10 + 0 = 10` |
| 16384 | `491520 + 32767 = 524287` | `10 + 8 = 18` |
| 32768 | `983040 + 32767 = 1015807` | `10 + 15 = 25` |
| 65535 | `1966050 + 32767 = 1998817` | `10 + 30 = 40` |

Both ends of the range are reachable, the midpoint lands exactly halfway, and the server and client
compute the same integer from the same two inputs with no tolerance and no rounding mode to disagree
about. A tier whose `max` equals its `min` yields that value for every position, with no division by
anything but the constant 65,535.

**Expensive to change once data exists: yes, because** every stored roll is a position interpreted through
this formula. Changing the formula silently restates every item in the world. Changing the width from
`ushort` would do the same.

**Decided at gate 0:** a rescale is silent, and nothing in the byte format turns on it (section 17,
decision 4).

### 6.5 How the two consumers' existing identities map

This contract owns the MAPPING RULE. The plan belongs to the adoption specs.

**Ruinborne** carries two per-row identities today: `character_inventory.inventory_id BIGINT IDENTITY`,
the database key, and `character_inventory.journal_item_id UNIQUEIDENTIFIER`, a derived version-5 GUID
called `OwnedItemId` that the client names in a use or destroy request (`c-ruinborne.md:949-962`). The
mapping rule is:

- The GUID is the one on the wire and in the journal, and its derivation is load bearing for migration and
  for spill rows (`c-ruinborne.md:956-960`). It is therefore the identity the engine instance id REPLACES,
  not the one it sits beside. An engine long added beside the GUID would be a THIRD identity, which the
  survey names as the failure mode (`c-ruinborne.md:961-962`).
- A migration allocates one engine instance id per existing owned row with a non-null property payload,
  and records the old GUID on the row for one release so an in-flight client request naming a GUID still
  resolves. Rows with no properties get instance id 0 and need no allocation at all.
- Ruinborne's `instance_json` column has never been written (`c-ruinborne.md:113-132`), so there is no
  legacy payload to decode. That is a gift: the instance payload arrives with a clean slate.
- Ruinborne #299 is a PRECONDITION on the whole thing, and the contract records it as such: the PostDeploy
  duplicate-row collapse partitions by `(character_id, item_id)` with no `instance_json` term and runs on
  every redeploy, so the moment two differently rolled copies of one non-stackable item exist they collapse
  into one (`c-ruinborne.md:939-948`). Instances cannot land in Ruinborne before that repair is fixed.

**Grimhollow** has int item ids already, so its definition ids map one to one and no stored container
changes value. That one-to-one mapping rides 5.1's empty-database import exception, which is the only path
by which the existing ids survive the move into the authoring store. Its items carry no per-instance state
at all today, so every existing stack maps to instance id 0. The first Grimhollow item that gains a
property is the first instance id it allocates.

**Expensive to change once data exists: yes for Ruinborne, no for Grimhollow.** Ruinborne's is a data
migration over millions of rows with a dual-read window. Grimhollow's is a no-op.

### 6.6 Dropped instances ride the region ground streams

Grimhollow's drop design puts a dropped stack on a REGION GROUND STREAM as a `loot-created` event and takes
it back through the unchanged `loot-claim` path (Grimhollow's
`docs/design/ITEM-DROP-DESIGN-2026-09-14.md` section 5, "Durable ground sources": one atomic commit across
the player's `bag-replaced` and one `loot-created` per ground stack on
`grimhollow/loot/hollowmere/ground/{rx}_{rz}`, with claims and expiry staying on the paths goblin loot
already uses). That design lands BEFORE these contracts (section 17, decision 12), so the contract is
written against it rather than around it.

**Contract: a ground stack carries the instance with it.**

- A ground stack whose item has an instance carries the INSTANCE ID and the FULL PAYLOAD, both in the
  durable `loot-created` event and in the engine's ground item spawn. Neither is re-derived at claim time
  and neither is left out of the event.
- The payload is FILTERED PER VIEWER by section 11 when it is replicated. What is durable is the whole
  payload. What a viewer receives is what their level allows, through the one function 11.2 names, and a
  ground item is no different from an equipped one in that respect.
- A claim MOVES the same instance id into the claimant's container. It does not mint a new one. The
  claimant is not special-cased either, so the dropper reclaiming their own drop takes the same path a
  stranger does.

Identity therefore survives a drop and a pickup, which is what keeps a dropped item traceable and what stops
a drop-and-claim cycle from laundering one. `TileGroundItem.ItemId` is an `int` today (section 5.1), so the
instance id and the payload are ADDITIVE fields on the ground item rather than a widening of an existing
one, and an ordinary stack with no instance carries 0 and an empty payload for no cost.

## 7. Version identity and the per-page stamp

### 7.1 A content version is a number AND a hash

Publishing assigns two things to a content version:

- **The version number**, a monotonic `int` starting at 1, incremented by exactly 1 per publish, never
  reused, never skipped. This is the ORDERING.
- **The manifest hash**, a SHA-256 over a canonical description of the version, lower hex, 64 characters.
  This is the IDENTITY.

Both travel together everywhere a version is named. They answer different questions and neither
substitutes for the other.

### 7.2 The page stamp is the NUMBER

A durable container page, and any other durable record carrying content ids, stamps the content VERSION
NUMBER it was last brought up to date with. Not the hash.

**Why the hash cannot do this job.** A remap rule applies to any page whose stamp is OLDER than the rule's
version (section 8). "Older" is a comparison, and a digest has no order: two hashes tell you they differ
and nothing else. A stamp that cannot be compared forces the loader to either keep a full history table
mapping every hash ever published to its ordinal, or to re-apply every rule ever written on every load.
The number makes it one `if (stamp < rule.Version)`.

Weighted comparison, because this one is genuinely contested (a hash is the more obvious choice for an
identity, and it is what Grimhollow's door carries today).

| Criterion | Number | Hash | Both on the page |
|---|---|---|---|
| Orders a remap rule application | 10 | 1 | 10 |
| Bytes on every page | 9 (4) | 5 (32, or 8 truncated) | 4 |
| Detects a page stamped by a DIFFERENT publish line | 2 | 10 | 10 |
| Survives a rollback republish | 8 | 6 | 6 |
| Simplicity at the read site | 9 | 7 | 5 |
| Total | 38 | 29 | 35 |

Recommendation: the NUMBER on the page, the HASH on the connect door and in the manifest. The "both"
column scores well and loses on bytes: 36 bytes on every page of every container of millions of owned
items, to detect a case (two publish lines against one durable store) the owner has explicitly ruled out
for v1 by having no staging environment (#882 comment 2, 2026-09-14). Section 17 records the decision and
the condition to revisit it: the moment staging arrives.

**Expensive to change once data exists: yes, because** the stamp is a field on every durable page, and
both the loader's remap decision and the rewrite-on-commit rule read it.

### 7.3 The manifest hash

Following `TileWorldHash` in every particular (`KhaozEngine.TileWorld/TileWorldHash.cs:19-185`,
`a-engine.md:1183-1237`):

- Algorithm SHA-256, rendered lower hex through `Convert.ToHexStringLower`, 64 characters.
- A `public const int SchemeVersion` folded into the digest, starting at 1, bumped on any
  canonicalisation change, on purpose.
- Domain separated. The domain string is `kec/` and the manifest sub-domain is `kec/manifest/`. The chunk
  digest, the client manifest digest and the server manifest digest each get their own sub-domain, so a
  head gating on one can never accidentally agree with a head gating on another. That last property is
  `TileWorldHash.OfWorldAndCatalogs`'s stated reason for existing (`a-engine.md:1206-1209`).
- Every number formatted through `CultureInfo.InvariantCulture`, because `StringBuilder.Append(int)` uses
  the CURRENT culture and a negative number digests differently under a culture with its own minus sign
  (`TileWorldHash.cs:171-176`).
- Every string LENGTH PREFIXED as `"{len}:{value} "`, with a bare `"- "` for null, so a delimiter inside
  an authored key cannot make two different manifests digest the same (`TileWorldHash.cs:178-185`).
- Collections SORTED before digesting, by type id then by chunk index.

The canonical manifest text is, in order: the sub-domain plus scheme version, the version number, the
format generation, the minimum server build, the minimum client build, then for each content type sorted
by type id, the type id, the type key, the chunk count, and for each chunk in index order the chunk index
and its chunk hash.

The CHUNK HASH is SHA-256 under its own sub-domain `kec/chunk/`, with the scheme version folded in like
every other digest in these two programs (section 15), taken over the chunk's uncompressed canonical bytes
and rendered lower hex. It is the chunk's content address, so a chunk that did not change between two
versions has the same hash and a client already holding it fetches nothing. That is the mechanism behind
the owner's "download size after a one-item edit" budget (#882 body, item 12).

The CLIENT manifest is built the same way over the client-visible chunks only (section 11), so it has its
own hash and is never equal to the server manifest's.

### 7.4 Minimum builds

Two different questions live here, and they are answered by two different numbers with two different owners.

**`MinimumServerBuild` and `MinimumClientBuild` are CONSUMER-SUPPLIED build ordinals**, each an `int` and
each monotonic. The GAME decides what its build number means and bumps it on any release that changes art,
protocol or codec. The engine never interprets the number beyond comparing it, which is what lets one field
carry both of the statements a publisher needs to make:

- "This item needs art that ships in client build N." A new item whose model and art already ship needs no
  client release (#882 body, item 7), and this is how a publisher states the case where it does not.
- "This codec needs reader build N." A game type whose row codec gained a field an older reader cannot skip
  is the same statement about code rather than art.

Collapsing those two into one ordinal is deliberate. A game already has exactly one number that orders its
releases, and asking it for a second art-only one would be asking it to track which release each asset
landed in, which no consumer does today.

**`FormatGeneration` is the ENGINE's own `int`, and the engine reader checks it itself.** It sits beside the
two minimums in the manifest and is incremented ONLY when an engine-owned row codec or the pack format
gains a field an older reader cannot skip. It is not a game's to set and not a game's to bump, which is why
it cannot be folded into the consumer ordinals: a game that changed nothing of its own would otherwise have
no way to say that the engine underneath it did.

The refusal behaviours are unchanged by the split:

- A server whose build is BELOW `MinimumServerBuild` refuses to load the version and fails the boot
  closed, per the owner's fail-closed rule (#882 body, item 8). It does not fall back to an older version,
  because an operator who published a version intends it to be live and a silent downgrade is how a
  fleet ends up serving two different catalogs.
- A client whose build is BELOW `MinimumClientBuild` is refused at the connect door with a distinct
  refusal token, so the client can tell the player to update rather than showing a generic mismatch.
- A reader whose supported `FormatGeneration` is below the manifest's refuses on the same side and in the
  same way, the server at boot and the client at the door, with no consumer involvement at all.
- A reader ABOVE either minimum is always fine. Forward compatibility within a generation comes from the
  tagged encoding in section 9, and a minimum build or a format generation is the explicit statement that
  the tagged escape hatch was not enough this time.

### 7.5 The connect door layer

A new labelled `HandshakeToken` layer carries the content identity, gated by a
`ContentIdentityGateAuthenticator` modelled directly on `WorldIdentityGateAuthenticator`
(`KhaozEngine.Netcode/ConnectionGate.cs:53-96`): unwrap one layer, compare ORDINAL, refuse with a stable
wire token carrying both sides, otherwise delegate inward. Nothing needs inventing, which is the engine
report's own finding (`a-engine.md:872-879`).

The layer's value is `<versionNumber>|<clientManifestHash>`, the decimal version number and the 64
character lower hex client manifest hash joined by a pipe. Both, because the number alone cannot detect a
client that rebuilt a pack wrongly and the hash alone cannot tell an operator which side is behind.

Refusal token format, following `ke:world-mismatch:<serverHash>|<clientHash>`
(`KhaozEngine.Netcode/HandshakeToken.cs:23`):

```
ke:content-mismatch:<serverVersion>|<serverHash>|<clientVersion>|<clientHash>
ke:content-client-too-old:<minimumClientBuild>
```

The pipe is the separator INSIDE the payload and the colon separates the token's own fields, matching the
existing world-mismatch shape. A client presenting no layer at all unwraps to the empty label and is
refused with empty client fields, which is exactly what `GrimhollowConfigGate` already does
(`b-grimhollow.md:449-456`).

Layer ORDER in the nest, outermost first: protocol version, world, CONTENT, the game's token auth, the ban
check. Content sits inside world and outside auth for the same reason world sits inside version: a
disagreement about content is a cheaper and more specific refusal than a failed credential, and the ban
check must stay innermost because it needs the subject the token produced
(`KhaozEngine.Netcode/ConnectionGate.cs:154-180`, `a-engine.md:839-871`).

### 7.6 How Grimhollow's joined config hash is replaced

Grimhollow's door carries four layers today, and the fourth is the skilling config hash
(`b-grimhollow.md:420-434`). The `feature/item-drop` branch, which lands first (section 17, decision 12),
changes that fourth layer's value to `GrimhollowGameDataHash.Current`, a HYPHEN-JOINED pair of the skilling
hash and a new item-properties hash, joined rather than rehashed so an operator reading a refusal can see
which half moved, and with no colon in it because `GrimhollowConfigGate.TryParseMismatch` splits on colons
(`b-grimhollow.md:1341-1350`).

**Contract.** In phase 1 of Grimhollow's adoption, the joined game-data hash is REPLACED by the content
version layer of section 7.5, and `GrimhollowConfigGate` is deleted rather than re-pointed. Specifically:

- `skilling.jsonc` and `items.jsonc` both become content types, per the owner's ruling that the skilling
  data and the item properties are versioned content (#882 body). Once they are content, their per-file
  hashes have nothing left to describe.
- The operator-legibility property the hyphen join was built for is preserved and improved: the refusal
  token in 7.5 carries BOTH sides' version numbers, so an operator sees "server 47, client 44" rather than
  having to diff two digests. That is strictly more legible than knowing which of two files moved.
- **The `feature/item-drop` branch lands FIRST**, decided by the owner at gate 0, so the joined hash stands
  as shipped until adoption phase 1 replaces it. This contract does not ask that branch to change. It also
  does NOT adopt the hyphen join into the engine: an engine layer that joins two sub-hashes would need a
  rule for how many sub-hashes there are, and the content version number answers the same question with one
  comparable integer. Landing the branch first is what keeps its `assets/config/items.jsonc` a migration
  absorbed AT adoption, rather than a third authored content mechanism appearing after the contracts and
  needing migration the moment it lands.
- The `tradable` flag from `items.jsonc` becomes a per-definition field on the engine item base type, and
  the fail-closed every-live-item-needs-a-row rule becomes the publish validator's coverage check
  (section 10). The retired-items-answer-untradable rule becomes the retire policy in section 8.

**Expensive to change once data exists: no for the door, yes for the manifest hash.** The door layer is
negotiated fresh on every connect and carries nothing durable. The manifest hash is recorded against every
published version, so changing the algorithm or the canonicalisation requires a scheme version bump and
re-digesting every version, which is why `SchemeVersion` exists.

**Decided at gate 0:** a client that is behind on content is refused at the door rather than admitted
read-only while it fetches (section 17, decision 6).

## 8. Remap rule format

### 8.1 Shape

Remap rules are APPEND ONLY. A rule is never edited and never deleted, and the list is a permanent part of
every published version. There is no precedent for this in the engine at all: `grep -i remap` over the
tree returns only GPU, mesh, texture and input remapping (`a-engine.md:1457-1467`). The nearest relatives
are `RotateStoreEpochAsync`, which invalidates every outstanding projection cursor after a restore
(`KhaozEngine.WorldStore/Journal/IMutationJournalMaintenance.cs:10`), and `SqliteJournalSchema`'s gated
forward migration. So this format is new, and it is designed to be boring.

Each rule carries:

| Field | Type | Meaning |
|---|---|---|
| `Sequence` | `int` | Global, monotonic across ALL rules of all types. The apply order. |
| `IntroducedIn` | `int` | The content version number the rule was published in. |
| `TypeId` | `ushort` | The content type the rule operates on. |
| `Kind` | `byte` | See 8.2. |
| `FromId` | `int` | The id being remapped. |
| `ToId` | `int` | The destination id, or 0 where the kind has none. |
| `Payload` | bytes | Optional, at most 64 bytes, kind-specific. |

### 8.2 The v1 rule kinds

**Kind 1, `ReplacedBy`.** `FromId` is retired and every reference to it becomes `ToId`. Counts, positions
and any payload carry over unchanged. This is the ordinary rename or re-base case.

**Kind 2, `Retired`.** `FromId` leaves play. The payload's first byte is the POLICY:

- `0x01` placeholder. The reference is kept as-is and the item is displayed through a placeholder. It is
  not usable, not tradable and not droppable. This is the quarantine presentation of section 10 reached
  by a different road, and it uses the same placeholder so a player sees one consistent thing.
- `0x02` replacement. Bytes 1 to 4 are an int32 destination id, and the behaviour is kind 1.

There is deliberately no "delete" policy. The owner's rule is that a definition is never deleted and is
retired instead (#882 body, item 2), and Grimhollow's 18 retired ids exist precisely so a stored stack
still decodes (`b-grimhollow.md:30-36`).

**Kind 3, `MovedToLegacy`.** `FromId` is a mod whose ranges changed under the keep-legacy option, and
`ToId` is the legacy copy that can never be generated or crafted again (#884 body, item 11, and section
5.4 here). Every existing item carrying `FromId` moves to `ToId` at load. Items generated after the
publish carry the new `FromId` with its new ranges. The two coexist forever.

**Kind 4, `StackCapLowered`.** `FromId` is a definition whose stack cap fell. `ToId` is 0. The payload is
an int32 new cap. The POLICY, stated here because Ruinborne's is the cautionary tale:

- An existing over-cap stack is LEGAL. It is not split, not truncated, not deleted and not refused on
  load.
- It may only SHRINK. Any operation that would leave it at or below the new cap is allowed. Any operation
  that would leave it above its current count is refused.
- Once it is at or below the new cap, the ordinary cap applies and it can never go back up.

Ruinborne's version of this is silent and permanent: the merge candidate query is
`AND [quantity] < @max`, so an over-cap row is excluded from candidacy rather than repaired, nothing
anywhere re-checks existing rows, there is no CHECK constraint on the quantity column and no repair pass
(`c-ruinborne.md:537-564`). The failure is not that over-cap stacks exist. It is that nothing knows they
do, so they never converge and never surface. Kind 4 makes the state explicit, bounded and self-healing.

### 8.3 Application

A rule applies to any durable page whose stamp is STRICTLY OLDER than `IntroducedIn`. Rules apply in
`Sequence` order, all of them, in one pass. After the pass the page's effective stamp is the active
version.

**Idempotence is required, and it is a property of the RULE SET rather than of one rule.** Applying the
whole ordered set twice to the same bytes must produce the same bytes as applying it once. That is what
makes a crash between "apply" and "commit" safe, and it is what makes a page that was already brought
forward by another path cost nothing. Concretely it forbids two shapes: a rule whose `ToId` is the
`FromId` of an earlier rule in the same set (which would chain twice on a second pass), and a rule whose
effect depends on a value it also changes. The publish validator MUST reject both, by walking the full
ordered set and checking that no rule's `ToId` appears as any earlier rule's `FromId` for the same type.

A rule is a no-op on a page that holds no reference to `FromId`, which is the common case, so the pass is
a scan rather than a rewrite for almost every page.

### 8.4 Byte encoding

Little endian throughout, varints as defined in section 15:

```
[Sequence: varint int32]
[IntroducedIn: varint int32]
[TypeId: uint16 LE]
[Kind: byte]
[FromId: varint int32]
[ToId: varint int32]
[PayloadLength: byte, 0 to 64]
[Payload: PayloadLength bytes]
```

A typical rule is 9 to 12 bytes. Ten thousand rules is about 110 KB, which is a rounding error against a
pack.

### 8.5 Where rules travel

Rules travel IN THE PACK, in ONE dedicated chunk per manifest, at a reserved chunk address outside any
content type's id space. That chunk holds the FULL rule list from sequence 1, not a delta.

The full list rather than a delta, because a page can be arbitrarily old. A player returning after a year
has a stamp from twenty versions back, and a delta pack would require the loader to hold every
intervening version's rule chunk to bring that page forward. Since the list is append only and small, the
whole of it is cheaper than the bookkeeping to avoid it.

The rule chunk is in BOTH the server and the client manifest, and its contents are identical in both. A
client needs it to bring a locally cached page forward and to render an old item correctly. It carries no
server-only information by construction, because a rule is (id, id, kind) and never a value.

**Expensive to change once data exists: yes, because** the rule list is the only record of how an old page
becomes a current one. A change to the rule encoding or the apply order restates the history of every
durable page in the world. The `Kind` byte is the extension point: a new kind is additive and old readers
that meet it must fail closed rather than skip it, which is what the engine's `FormatGeneration` (7.4) is
for.

**Decided at gate 0:** placeholder items are never auto-converted to a refund, and a deliberate
`ReplacedBy` rule is the only path (section 17, decision 7).

### 8.6 A retire is irreversible for migrated pages

Once a page has been brought forward past a `Retired` or a `ReplacedBy` rule, the old id is GONE from that
page. Nothing records that it was ever there, because a remap is a rewrite rather than a log.

A later rule pointing back is not the way out, and the idempotence check of 8.3 already forbids it. A rule
whose `ToId` is an earlier rule's `FromId` for the same type is rejected by the publish validator, and that
is exactly the shape an undo would take. So there is no undo.

**A rollback that wants the definition back MINTS A NEW ID carrying the old values.** It is an ordinary new
row with an ordinary new id and a new key under 5.3's immutability rule, plus a `ReplacedBy` rule moving
whatever the owner wants moved onto it. The version NUMBER keeps climbing throughout: a rollback is a new
version and never a return to an old one (#882 body), which is the same property that lets the page stamp
be an ordering comparison in 7.2.

Pages that were never brought forward are a different case and need nothing special. They still hold the old
id, the full ordered rule set still applies to them, and they arrive where every other page arrives.

**Expensive to change once data exists: yes, because** this irreversibility is what keeps the rule set
idempotent, and idempotence is what makes a crash between apply and commit safe (8.3, 10.3).

## 9. Tagged field encoding

### 9.1 The format

An instance payload is a sequence of fields, each:

```
[Kind: varint uint16][Length: varint int32][Bytes: Length bytes]
```

That is the only structure. There is no header, no magic and no version byte on a payload, because a
payload never travels alone: it is always inside a container page, a ground item component or a journal
event, each of which carries its own version byte (section 15). An EMPTY payload is zero bytes, which is
what a plain stack has.

The engine's only existing tagged binary format is `JournalCanonicalizer`, which writes numeric field tags
behind a four-character magic and a `ushort` format version
(`KhaozEngine.WorldStore/Journal/JournalCanonicalizer.cs:37-79`, `a-engine.md:1480-1491`). Every other
codec in the tree is POSITIONAL, which is why unknown fields cannot be skipped and truncation is a
whole-message refusal there. This format departs from the positional house style deliberately, and section
9.5 says what it buys.

### 9.2 Property kind ids and reserved ranges

| Range | Owner | Varint cost |
|---|---|---|
| `0` | Reserved, never a valid kind | n/a |
| `1` to `127` | ENGINE generic instance fields (item level, durability, quality, flags, bound-to) | 1 byte |
| `128` to `1023` | SCOPE B fields (rarity, affixes, sockets, enchantments, rare name words) | 2 bytes |
| `1024` to `65535` | GAME fields | 2 to 3 bytes |

The two-byte cost on the Scope B range is paid ONCE per payload per field, not once per affix, because the
affix list is a single field holding all of them. A six-affix item pays it once.

### 9.3 Canonical form, and why byte equality is property equality

Three rules, all enforced by the encoder and all checked by the decoder:

1. Fields appear in STRICTLY ASCENDING order by kind id.
2. No kind appears twice.
3. Every varint is MINIMAL. `0x81 0x00` is not a legal encoding of 1.

Together these make the encoding canonical: one set of properties has exactly one byte sequence. That is
load bearing for Scope B's stacking rule, which is that items stack only when the definition is stackable
AND their properties are identical (#884 body, item 4). With a canonical form, "identical properties" is a
`ReadOnlySpan<byte>.SequenceEqual` over two payloads. Without it, it is a structural comparison that has to
decode both sides, which at bank-merge volumes is the difference between a memcmp and a parse.

`ItemContainerCodec` already takes the same shape at the container level: entries are strictly ascending by
slot and `Validate` rejects disorder (`KhaozEngine.Items/ItemContainerCodec.cs:96`, `a-engine.md:103-124`).
`TileWorldHash` sorts collections before digesting for the same reason
(`TileWorldHash.cs:95-96`, `a-engine.md:1219-1222`).

### 9.4 Unknown kinds are preserved verbatim

A decoder that meets a kind it does not know keeps the field's exact bytes and its position in the ordering,
and re-emits them unchanged on the next encode. It does not drop them, does not reorder them and does not
reinterpret them.

This is what lets a client built against content build N read, display and re-save an item carrying a field
only build N+1 knows, and it is the forward-compatibility half that `MinimumClientBuild` (section 7.4) is
the fallback for. It is also what lets a game add a field without the engine's container codec changing at
all.

The preserved bytes participate in byte equality, so two items differing only in an unknown field do not
stack. That is the conservative answer and the right one: the decoder does not know whether the unknown
field is meaningful.

### 9.5 Sockets

Sockets are ONE field kind holding an ordered list:

```
[Count: varint][ for each socket:
    [SocketTypeId: varint int32]      // 0 means no type restriction
    [ContainedDefinitionId: varint int32]  // 0 means the socket is empty
    [ContainedInstanceId: varint uint64]   // 0 means empty, or a contained item with no instance
    [NestedLength: varint int32]
    [Nested: NestedLength bytes]      // a payload in this same format
]
```

**The socket carries the contained item's INSTANCE ID, and that is the owner's decision at gate 0.** A
socketed item keeps its own identity while it sits in the socket, so unsocketing RESTORES that identity
rather than minting a new one, and an item duplicated by a bug or an exploit stays traceable to the instance
it was copied from. The field is 0 when the socket is empty, and also 0 when the contained item has no
instance of its own, which is the ordinary case for a plain gem (section 6.2). The cost is one varint per
occupied socket, and the field is UNSIGNED per section 15, because the id is node prefixed and a node id of
65,535 sets the high bit, so a signed declaration would zig-zag every id on that node into ten bytes.

**Nesting is ONE LEVEL ONLY and the DECODER enforces it.** A nested payload containing a socket field is
malformed and the decoder returns a reason rather than recursing. This is a hard structural limit rather
than a convention, because recursion is the one way a 45 byte payload becomes a denial of service, and
because the owner ruled sockets in and links out (#884 body), which is exactly the one-level shape.

Socket order is AUTHORED and preserved, never sorted. This mirrors `TileObjectArchetype.Tags`, whose
authored order is deliberately kept out of the sort for the catalog digest because sorting them would call
two different files one archetype (`TileWorldHash.cs:117-120`, `a-engine.md:1224-1226`). A player who puts
a gem in the third socket expects it to stay there.

### 9.6 Maximum payload size

**`MaxInstancePayloadBytes = 512`.**

The two caps it has to live under, with the arithmetic:

- **The journal section cap, 2 MiB** (`JournalLimits.cs:16`, `a-engine.md:437-491`). Scope B pages a
  container at about 100 slots per page, each page its own journal section (#884 body, item 6). A worst
  case page is 100 slots each carrying a 512 byte payload plus about 14 bytes of slot overhead, so about
  52.6 KB. That is 2.5 percent of the section cap and comfortably under the 256 KiB event payload cap too,
  which matters because a page rewrite is a whole-section replacement every time (`a-engine.md:437-460`).
- **The game message cap, 1,024 bytes** (`TileProtocol.Frames.cs:65`, `a-engine.md:791-800`). A 512 byte
  payload plus the 4 byte game-message header plus a slot entry leaves about 500 bytes of headroom, so a
  SINGLE-ITEM message (a craft result, a pickup, a ground item spawn) is always one frame with room to
  spare. A PAGE sync is not one frame and never can be, so it uses the application-level chunking
  precedent at `TileWorldServer.Tick.cs:241-259`, which is the one place the engine already sends a
  logical payload across several reliable ordered frames (`a-engine.md:818-838`).

512 sits between two item sizes rather than above one, and both numbers matter. The 45 bytes worked out in
9.8 is a TYPICAL rare, three affixes and one socket. The DEEPEST item the v1 field kinds can express is
about 410 bytes: six affixes, a full socket set with every socket holding a nested item, enchantments, and
a rare name assembled from word ids. So 512 is a guard rail at about 1.25 times the deepest legal item and
about 11 times the typical one, and anyone sizing a page or a message budget should plan against the 410
rather than the 45. A larger cap would buy nothing and would let one pathological item consume a page. A
smaller one, say 256, would fit the typical item with room over and would REFUSE the deepest one, which
turns a legal roll into an encode failure and leaves no room at all for the game-range fields a consumer
has not thought of yet.

**Expensive to change once data exists: no to raise, yes to lower.** Raising the cap is backward
compatible: every existing payload is still legal. Lowering it strands items that are already over it, and
the only safe way to lower it would be a remap rule kind that does not exist.

### 9.7 Truncation and malformed handling

The decoder NEVER throws. It returns `false` plus a reason string, exactly like
`ItemContainerCodec.TryDecode` and `Validate`, whose `string?` return is null for fine and a quarantine
reason otherwise (`KhaozEngine.Items/ItemContainerCodec.cs:49-104`, `a-engine.md:103-124`). Every frame
decoder in `TileProtocol` is total for the same reason: the bytes come from a remote peer
(`a-engine.md:814-817`).

The reasons, each a stable token so a counter can be keyed on it:

| Reason | Condition |
|---|---|
| `payload-too-long` | The payload exceeds `MaxInstancePayloadBytes`. |
| `field-truncated` | A field's declared length runs past the end of the payload. |
| `kind-out-of-order` | A kind is not strictly greater than its predecessor. |
| `kind-duplicate` | The same kind appears twice. |
| `varint-not-minimal` | A varint is longer than its value needs. |
| `varint-overflow` | A varint does not terminate within 5 bytes. |
| `socket-nesting` | A nested payload contains a socket field. |
| `field-malformed` | A known kind's bytes do not match its own shape. |

A payload that fails for any reason is QUARANTINED per section 10, never discarded, and never
reinterpreted as a shorter valid payload. That last clause matters: a decoder that stops at the first bad
field and keeps what it read would silently strip affixes off an item, which is worse than showing a
placeholder.

### 9.8 A worked byte example

A rare Greatsword at item level 68, durability 90 of 100, three affixes and one socket holding an item that
has an instance id of its own. The definition id lives in the container slot entry, not the payload, so the
payload is properties only.

Field kinds used: 2 item level (engine), 5 durability (engine), 130 rarity (Scope B), 131 affixes (Scope
B), 132 sockets (Scope B). They appear in that order, which is ascending, as rule 9.3.1 requires.

```
02 01 44                                 kind 2  len 1   item level 68
05 02 5A 64                              kind 5  len 2   durability 90 of 100
82 01 01 03                              kind 130 len 1  rarity 3 (rare)
83 01 12                                 kind 131 len 18 affixes
   03                                       count 3
   5B 01 33 33 00                           mod 91,   tier 1, position 13107, flags 0
   84 02 02 FF FF 00                        mod 260,  tier 2, position 65535, flags 0
   F2 20 03 CC CC 00                        mod 4210, tier 3, position 52428, flags 0
84 01 0A                                 kind 132 len 10 sockets
   01                                       count 1
   07                                       socket type 7
   C1 06                                    contains definition 833
   E9 20                                    contains instance 4,201
   03                                       nested payload length 3
      02 01 37                              nested: kind 2 len 1 item level 55
```

Forty-five bytes total. The arithmetic is written out because two of the five fields carry a field this
document added after the first draft.

- Item level is 3 bytes, durability 4, rarity 4. None of those moved.
- The affix field's payload is the count byte plus three entries. An entry is a mod id varint, a tier byte,
  a position as a `uint16` LE and the reserved flags varint of 9.9, so the three entries are 5, 6 and 6
  bytes and the payload is `1 + 5 + 6 + 6 = 18`, the `12` in the length byte. With the two-byte kind varint
  and the length byte the whole field is 21 bytes, up from 18 before the flags field existed. The entries
  are written ascending by mod id, as 9.9 requires, so this block may be copied into a golden file as it
  stands.
- The socket field's payload is the count byte, the socket type varint, the contained definition varint,
  the contained instance varint, the nested length byte and the 3 nested bytes:
  `1 + 1 + 2 + 2 + 1 + 3 = 10`, the `0A` in the length byte. With its kind varint and length byte the field
  is 13 bytes, up from 11 before the instance id was carried.
- Total: `3 + 4 + 4 + 21 + 13 = 45`.

The varints in it: `82 01` is 130, `83 01` is 131, `84 01` is 132, `F2 20` is 4210, `84 02` is 260,
`C1 06` is 833, and every single-byte value below 128 is itself. `E9 20` is instance id 4,201: its low seven
bits are 105, `69` with the continuation bit set giving `E9`, followed by 32, `20`. No value in this example
is zig-zagged, because every field in it is declared unsigned (section 15).

Reading the second affix, `84 02 02 FF FF 00`: mod id 260, tier 2, position 65,535, which is the top of the
tier's range, and flags 0 as v1 requires. If that tier runs 10 to 40, section 6.4's formula gives
`10 + (65535 * 30 + 32767) / 65535 = 10 + 30 = 40`.

Forty-five bytes against the 512 cap is under 9 percent. Against Grimhollow's current 18-bytes-per-slot
bank budget (`b-grimhollow.md:1044-1054`) it is 2.5 times over, which is the concrete number saying a bank
of affixed items cannot sync as one message and must be paged. That is not a surprise, it is the arithmetic
behind Scope B's paged-container requirement.

### 9.9 Affix entries reserve a flags field

The affix list is SCOPE B's field and its entry layout is Scope B's to specify. This contract requires one
thing of that layout: **every affix entry ends with a FLAGS varint, reserved and written as 0 in v1.** A
decoder meeting a non-zero flags value in v1 reports `field-malformed` (9.7).

The reason is that the per-instance facts about an affix arrive later and are not knowable now. Prefix or
suffix, implicit, crafted, fractured, and whatever else the eventual depth of 1.4 asks for, are facts about
ONE ROLL ON ONE ITEM, so none of them can live on the mod row and all of them would otherwise need a format
change to arrive. One reserved varint, a single `00` byte per affix while it stays 0, carries the first
seven of them for nothing and the rest for one more byte.

This is the argument of 9.4 applied one level down, where the tagged encoding cannot help. An affix entry
sits INSIDE a length-prefixed field, so a decoder that does not know an entry grew cannot skip the
difference, and preserving an unknown field verbatim does nothing for it. The flags field is the entry-level
version of the escape hatch the payload already has.

This contract requires one thing of the LIST as well: **affix entries are ordered ASCENDING BY MOD ID, and
a mod id appears at most once in one list.** Rule 9.3.1 orders FIELDS and says nothing about entries inside
one, so without this the same three affixes encode six ways and byte equality stops being property equality
(9.3), which is what a stack check and a page digest both rest on. The example in 9.8 is written in that
order for the same reason. Changing the entry order later costs what changing 9.3's field order costs:
every stored payload is already canonical under the old rule, so the new rule rewrites every page that
carries an affix.

**Expensive to change once data exists: yes, because** adding it later means two affix entry layouts told
apart by the content version a page was stamped with, which is the positional-codec migration this whole
format exists to avoid.

## 10. Validation outcomes

### 10.1 The three outcomes

Every durable record carrying content ids resolves, on load, to exactly one of three outcomes. There is no
fourth, and in particular there is no "dropped".

**Valid.** Every id resolves in the active version, every field parses, every cross-reference holds. The
record is used as read and the page is not dirtied.

**Remapped.** The page's stamp is older than at least one remap rule and the rules changed something
(section 8.3). The record is usable. The page is marked DIRTY IN MEMORY and is rewritten with the new stamp
on its NEXT ordinary commit, not eagerly.

**Quarantined.** Something did not resolve or did not parse. The record is kept, unusable.

### 10.2 Quarantine, precisely

A quarantined record's BYTES ARE KEPT VERBATIM in a wrapper record carrying the original bytes, a reason
code (the tokens of section 9.7, plus `unknown-definition` and `unknown-content-reference`), and the
content version number the record was stamped with when it failed. Nothing is truncated, nothing is
normalized and nothing is re-encoded.

The item is displayed as a PLACEHOLDER. It is unusable, untradeable and undroppable. It cannot be equipped,
socketed, crafted with, sold or destroyed. It can be moved between slots, because moving it does not depend
on understanding it and a player who cannot move it cannot tidy a bag.

An alert fires, and both halves are named here so a spec cannot invent its own:

- Counter `khaoz.content.quarantined_records`, dimensioned by content type id and reason code.
- Log line under category `ContentValidation`, at Warning, naming the reason code, the stamped version, the
  active version and the record's owning stream key. It NEVER logs the payload bytes or a raw account id,
  which is the journal design's operations rule (`DURABLE-PLAYER-JOURNAL-DESIGN-2026-09-06.md:657-660`).

The precedent is `ItemContainerCodec.Validate` returning a `string?` quarantine reason rather than
throwing, whose doc says false is the caller's cue to seat a fresh container
(`KhaozEngine.Items/ItemContainerCodec.cs:43-44`, `a-engine.md:103-124`). This contract goes further: a
fresh container is not acceptable for an owned item, because seating a fresh one loses it. Grimhollow's
current behaviour is the opposite extreme, a hard THROW on an unknown item id
(`GrimhollowJournalContracts.cs:483-524`, `b-grimhollow.md:545-556`), which turns one bad id into a player
who cannot log in. Quarantine sits between the two on purpose.

### 10.3 Why remapped pages are lazy

A remapped page is rewritten on its next commit, not on load.

The eager alternative is to rewrite every touched page at boot. It was considered and rejected on the
journal's own numbers: a commit rewrites a changed section WHOLE
(`JournalProjectionWrite`, `a-engine.md:437-460`), the recorded SQLite baseline is 698 commits per second
with 42,365 bytes allocated per operation
(`a-engine.md:607-641`), and the admitted queue admits one operation per stream at a time with a default
depth of 8 (`a-engine.md:492-534`). Eagerly rewriting every player's pages on the first boot after a
content publish would be a write storm proportional to the whole player base, arriving exactly when the
server is coldest, for a change that is invisible until someone logs in.

Lazy costs one thing: a page can sit remapped-in-memory across a session and be lost on a crash, which
simply means it is remapped again on the next load. That is safe because the rule set is idempotent
(section 8.3).

### 10.4 The one validator

ONE validator implementation is shared by publish, server boot and tests. Its contract shape:

```
ContentValidationReport Validate(ContentSnapshot candidate, ContentSnapshot? previous, IReadOnlyList<RemapRule> rules)
```

- INPUT is a complete candidate snapshot, the PREVIOUS published snapshot or null, and the full ordered
  rule set. Nothing is read from a database, a file or an ambient static inside it.
- `previous` is NULL at boot and in tests, where there is nothing to compare against and nothing to load,
  and is the LAST PUBLISHED version at publish, loaded by the caller and handed in.
- OUTPUT is a report: a bool plus an ordered list of findings, each carrying a content type id, an id, a
  code and a message. It accumulates rather than stopping at the first, following
  `JsonSchemaValidator.ValidationReport(bool IsValid, IReadOnlyList<string> Errors)` and its
  run-to-the-end sweep (`KhaozEngine.Content/JsonSchemaValidator.cs:11-101`, `a-engine.md:275-292`).
- NO SIDE EFFECTS. It does not log, does not mutate the candidate, does not touch a counter and does not
  throw for content reasons. A throw from it is a bug in the validator.

**The publish-time checks it must include, as a minimum.** A spec may add to this list and may not take
anything off it:

- Every content REFERENCE resolves. A field the type's schema declares as a key reference (4.7) names a
  live row of the type it names.
- Every KEY is unique within its type and well formed under 5.3's character rules.
- A definition declaring DURABILITY or SOCKETS is not stackable (#884 body, item 4). The two are
  contradictory, because stacking is byte equality over payloads and both of those are per instance.
- No `ServerOnly` FIELD appears in a `Client` chunk (11.3).
- The remap rule set is IDEMPOTENT, by the walk 8.3 describes: no rule's `ToId` is any earlier rule's
  `FromId` for the same type.
- Every LIVE definition carries every field its schema marks required (4.7). This is the engine's form of
  Grimhollow's every-live-item-needs-a-row rule, which fails the config load closed when a live item has no
  properties row (`b-grimhollow.md:1316-1358`).

**The CHANGE-SHAPED checks run only when `previous` is non-null, and each is documented PUBLISH-ONLY.**
Every one of them compares the candidate against the last published version, so at boot and in a test,
where `previous` is null, they do not run and cannot appear in the report. That is a property of the
argument rather than a mode flag, which is what keeps one implementation honest across the three callers:

- A KEY changed on a row that is already published. 5.3 makes a key immutable once published, and a rename
  is a retire plus a new row plus a remap rule, so this is the check that enforces it.
- A registered type's id, key or CHUNK SIZE changed after that type's first publish (4.3, 4.5). A chunk
  size change repaginates every chunk hash and every page stamp derived from it.
- A mod's TIERS were reordered. A stored affix entry is a mod id, a tier byte and a position (9.9), so
  reordering tiers repoints every affix already in the world at a range it was never rolled in.

Nothing above weakens the two rules that make the validator testable. It still has NO SIDE EFFECTS, and it
still performs NO AMBIENT READS: `previous` is an ARGUMENT, loaded by the caller that has a store to load
it from, never a lookup the validator performs. A test that wants a change-shaped finding constructs both
snapshots in memory and passes the pair.

Because it is pure and takes its whole world as an argument, a test builds a snapshot in memory and asserts
on findings, publish runs it before writing anything, and boot runs it against the loaded pack. Ruinborne's
catalog loader is the shape this avoids: validation lives inside the SQL read delegate, so an
`ArgumentException` from a bad row is routed to the connectivity failure handler and reported as
`Item catalog: SQL read failed`, which sends an operator looking at the network
(`c-ruinborne.md:176-196`, `c-ruinborne.md:481-501`).

### 10.5 Fail closed at boot

A missing or invalid active content version FAILS THE BOOT. There is no runtime fallback to code defaults
(#882 body, item 8). The process exits non-zero with the validator's findings on stderr.

This is stated as a contract because the consumer precedent is the opposite: Ruinborne's loader falls back
to five hardcoded code defaults on any failure, announces it with a `Console.WriteLine`, and serves a
different catalog than the database holds with no metric, no exit code and no refusal to admit joins
(`c-ruinborne.md:176-196`, `c-ruinborne.md:1030-1038`). A silent fallback catalog is worse than an outage,
because an outage is noticed.

**What the rule binds is the CONTENT VERSION.** The boot fails when the active version is missing, when
its pack cannot be read, when it does not pass the validator of 10.4, or when it is below one of the
minimum builds of 7.4. It does not bind the game CODE a content row happens to name. A row that names a
registration this process does not have, a game-registered crafting operation or a validator hook that
ships in a later binary, LOADS, and the operation is refused at the moment of USE with a stable reason
code and a counter, not at boot. The reason is the deployment order: content publishes and code deploys
move on separate clocks, so failing the boot for a missing registration lets one unusable row take a whole
server down for a deploy that has not happened yet, and it puts the content operator in a position to halt
the fleet by saving a draft. A refusal at use costs one unusable operation, which is visible in the counter
and recoverable by deploying the code. The distinction holds in the other direction too: the version and
its pack are checked once, at boot, by the process that is about to serve them, so nothing here weakens the
no-runtime-fallback rule above.

**Expensive to change once data exists: no.** All three outcomes and the fail-closed rule are runtime
behaviour over a fixed byte format. The QUARANTINE WRAPPER's own encoding is durable, so it gets a version
byte like everything else in section 15.

## 11. Visibility vocabularies

### 11.1 Two vocabularies, deliberately different

**Property visibility**, per property KIND, three levels ordered least to most visible:

| Level | Meaning |
|---|---|
| `ServerOnly` | Never leaves the server. Not in any snapshot, not in any tooltip. |
| `OwnerOnly` | Sent to the item's owner. Not to anyone else. |
| `Everyone` | Sent to any viewer who can see the item at all. |

**Content visibility**, per content TYPE and per FIELD, two levels:

| Level | Meaning |
|---|---|
| `Client` | Travels in the client manifest. |
| `ServerOnly` | Server manifest only. |

They are different because they answer different questions. A property level is about ONE PLAYER'S item
relative to ONE VIEWER. A content level is about whether a row belongs in the pack a client downloads at
all. Collapsing them into one enum would force a drop table (server only content) and an unidentified
affix (owner only property) to share a word, and they have nothing to do with each other.

### 11.2 The replication rule

A viewer receives a field if and only if its kind's visibility is AT OR BELOW the viewer's level for that
item. The viewer's level is `Everyone` normally and `OwnerOnly` when the viewer owns the item. Nothing is
ever `ServerOnly` on the wire.

**Tooltips follow the same rule, using the same evaluation.** There is exactly one function answering "may
this viewer see this field", and both the replication filter and the tooltip builder call it. A tooltip
that computed its own answer is how a client eventually renders something the server never sent.

This is new machinery and the contract says so. Every existing filter in the engine is PER ENTITY, a
`HashSet<long>` of net ids handed to `SnapshotWriter.WriteFiltered`, and the component write delegates
registered at `TileProtocol.Components.cs:122` take `(value, BinaryWriter)` with NO viewer argument, so a
component physically cannot serialize differently per recipient today (`a-engine.md:952-981`,
`a-engine.md:1497-1514`). The one per-recipient rule that exists, `PickupState.OwnerNetId`, gates the
COLLECT OFFER rather than the replication. So Scope B needs either a viewer-aware write delegate or a
per-viewer projection of the payload before it reaches the writer, and this contract owns only the RULE,
not which of the two.

The cheap implementation note, because it affects the byte format and therefore belongs here: since fields
are sorted ascending by kind and the ranges in section 9.2 are fixed, a spec MAY assign kind ids so that
visibility is monotonic in the kind id, which makes the owner-only projection a prefix truncation rather
than a re-encode. This contract does not require that, because it would couple the kind ranges to the
visibility vocabulary forever, and it is worth measuring first.

### 11.3 The two manifest rules

- The CLIENT manifest OMITS every `ServerOnly` chunk, and omits every `ServerOnly` FIELD from the rows in
  the chunks it does carry. Its hash is computed over what it actually contains (section 7.3), so a client
  can verify what it downloaded.
- The publish validator REFUSES a `ServerOnly` field appearing in a `Client` chunk. That is a publish-time
  error, not a warning and not a strip, because a silent strip means the field's absence is indistinguishable
  from an authoring mistake.

Drop tables and stores are the motivating cases. The owner put them in the same versioned content as items
and called it important (#882 comment 2), and a drop table is exactly the content a client must not have.

**Expensive to change once data exists: yes for the level count, no for a field's assigned level.**
Changing a field from `Everyone` to `OwnerOnly` is a publish. Adding a fourth property level changes the
comparison every replication and tooltip site performs.

**Decided at gate 0:** an unidentified item is its own mechanic built on `OwnerOnly`, so there is no
fourth visibility level (section 17, decision 8).

## 12. Localization key conventions

### 12.1 The derivation

A content string's key is derived MECHANICALLY from the content type key, the row key and the field name:

```
<type key>.<content key>.<field>
```

Examples, using the two consumers' real content: `item.stone_sword.name`, `item.stone_sword.examine`,
`mod.fine_crafted.line`, `rareword.gloom.text`, `store.general.name`, `node.oak_tree.examine`.

Derivation rather than authoring, because an authored key rots independently of the row it names.
Grimhollow's does already: `ItemPineLogs = "item.pinelogs.name"` and `ItemOakLogs = "item.oaklogs.name"`
drop the underscore every other key keeps, so the key is NOT derivable from the item's config key today
(`b-grimhollow.md:765-772`). Two rows out of thirty-five is enough to make every downstream tool do a
lookup instead of a concatenation.

### 12.2 Character rules

The type key and the content key follow section 5.3 (`a-z`, `0-9`, `_`, ordinal, 64 characters). The field
name follows the same set and is drawn from a fixed per-type list declared at registration, so `name`,
`examine` and `line` are the type's vocabulary rather than free text. The dot is therefore unambiguous:
it never appears inside a segment, so a key splits on it exactly.

Total key length is bounded at 192 characters, which is three 64 character segments plus the two dots.
`StringId` accepts any non-empty string with ordinal equality (`KhaozEngine.App/StringId.cs:11-41`), so the
bound is this contract's, not the engine's.

### 12.3 Composed names

A name assembled from localized parts (a rare item's prefix, base and suffix) is a TEMPLATE in content plus
KEYS for the parts. The template is a localizable string in its own right, because word order differs by
language and a concatenation in code cannot be translated.

```
template key:  item.name_template.rare        -> "{0} {1} {2}"
parts:         rareword.gloom.text, item.greatsword.name, rareword.of_bloodshed.text
```

Resolution goes through `IStringCatalog.Format`, which is `string.Format(CurrentUICulture, Get(key),
args)`, and a malformed template falls back to the unformatted template rather than taking the process
down. That fallback exists because a template is translator-authored CONTENT arriving as data rather than
a caller bug, and Gui resolves inside the frame loop with nothing above it to catch
(`KhaozEngine.App/IStringCatalog.cs:28-49`, `a-engine.md:1097-1112`). Content-authored templates are
exactly the case that doc was written for, so the contract adopts `SafeFormat` on this path explicitly.

### 12.4 Where content strings live and how they layer

Per-language TEXT CHUNKS in the pack, one chunk set per language, listed in the manifest with their own
chunk hashes so a client downloads only the languages it wants (#882 body, item 6 and item 9).

They are loaded into an `IStringCatalog` LAYERED OVER the game's existing .resx catalog:

1. The content catalog is asked first.
2. On a miss, the game's .resx catalog is asked.
3. On a miss there, the standard behaviour applies: `Get` returns THE KEY ITSELF as a visible non-fatal
   placeholder, never a throw (`KhaozEngine.App/IStringCatalog.cs:12-17`).

Content first, because content is the thing that ships without a client release (#882 comment 2), so a
content string must be able to override a stale shipped one. The engine's catalog loading today is .resx
satellite assemblies with no JSON loader, no hot reload and no per-pack merge
(`a-engine.md:1128-1133`), so the layered catalog is new code and the contract names the order so both
specs assume the same one.

### 12.5 Grimhollow's two non-conforming keys

`item.pinelogs.name` and `item.oaklogs.name` are RENAMED at adoption to `item.pine_logs.name` and
`item.oak_logs.name`. They are not special-cased, not aliased and not exempted.

A rename is one line in the .resx and one constant in `GrimhollowStrings.cs`, and the existing reflection
test walks every declared key constant against the shipped catalog so a half-done rename goes red
immediately (`b-grimhollow.md:773-780`). Special-casing them would mean the engine's derivation carries a
per-consumer exception table forever, for two rows. The rename is tracked in
[Grimhollow #222](https://github.com/APKiwiOrg/Grimhollow/issues/222).

**Expensive to change once data exists: no.** Localization keys name TEXT, not durable player state. A key
change is a content edit plus a catalog edit, and a miss degrades to a visible placeholder rather than a
failure. That is precisely why the rename is affordable and the special case is not.

**Decided at gate 0:** the engine ships per-language text chunks for languages the game does not, so
content can be translated ahead of the client (section 17, decision 9).

## 13. Stat definition shape

### 13.1 A stat is content

| Field | Type | Notes |
|---|---|---|
| `StatId` | `int` | Section 5's id space, type `stat`. 0 is none. |
| `Key` | string | Section 5.3 rules. |
| `Scale` | `int` | A fixed power of ten. The stored integer is the value times `Scale`. |
| `Min`, `Max` | `int` | Inclusive clamp, in scaled units. |
| `Tags` | ordered list of tag ids | The `tag` content type of section 4.6. Authored order preserved. |
| `DisplayFormatKey` | string | A localization key, section 12. |

The VALUE KIND is integer with a fixed scale, always. `Scale = 100` gives two decimal places, which is what
a percent-of-a-percent needs. There is no float stat and no float modifier anywhere in the content system.

### 13.2 Combine kinds and the evaluation formula

Three kinds, and exactly three:

- `Flat`, summed.
- `Increased`, an ADDITIVE percent pool. All `Increased` from every source are summed, then applied once.
- `More`, a MULTIPLICATIVE percent. Each `More` applies as its own factor.

The formula, in integer math, evaluated per stat in this order:

```
flat      = Base + sum(Flat)                                     // scaled units
increased = 10000 + sum(IncreasedBasisPoints)                    // 10000 == 100 percent
value     = floordiv(flat * increased + 5000, 10000)             // round half up
for each More m, in ascending (SourceOrder, ModifierIndex):
    value = floordiv(value * (10000 + m.BasisPoints) + 5000, 10000)
value     = clamp(value, Min, Max)
```

Percentages are BASIS POINTS, integers where 10,000 is 100 percent. `+ 5000` before the divide is round
half up, the same shape as the roll formula in section 6.4, so there is one rounding rule in the whole
system rather than two.

**Every division in the fold is FLOOR division, toward negative infinity, and that is the rule rather than
a detail of one implementation.** `floordiv(x + 5000, 10000)` is round half up for EVERY SIGN. C# integer
division truncates toward zero instead, which makes `/` the wrong operator here and makes a debuff round
differently from the buff of the same size.

The negative case, worked. A stat whose `flat * increased` comes to `-14000`, that is `-1.4` scaled units:
`floor((-14000 + 5000) / 10000) = floor(-9000 / 10000) = floor(-0.9) = -1`, the nearest integer with the
tie going up. C# `(-9000) / 10000` gives `0`, so a plain `/` loses a whole unit of a penalty and reports no
penalty at all. Negative values are ordinary here, because a `Flat` modifier may be negative and a stat's
`Min` may sit below zero.

The implementation is `Math.DivRem` with a negative-remainder adjustment, subtracting one from the quotient
when the remainder is non-zero and its sign differs from the divisor's, or any equivalent that computes the
same integer. It is NEVER `Math.Round`, which takes a floating point argument and would put a `double` on
the determinism path that 13.4 exists to keep clear of it.

**Section 6.4's roll formula has no negative case and is unaffected.** `position` is a `ushort` and
`max - min` is non-negative by the tier's own bounds, so its numerator is never negative and truncation and
floor agree on every input it can be given. It is written with `/` and stays that way.

The `More` loop order is FIXED and stated because multiplication of integers with rounding at each step is
NOT associative: `(a * x) * y` and `(a * y) * x` can differ by one unit. Ordering by (source order,
modifier index) makes the fold reproducible. That is the same reason `StatSet` documents its insertion
order as load bearing and deliberately rejected a `Dictionary`
(`KhaozEngine.Stats/StatSet.cs:22-31`, `a-engine.md:212-228`), and this contract inherits the argument
while changing the arithmetic from float to integer.

Intermediate arithmetic is done in `long` and the result is checked into `int` before the clamp, so a
pathological modifier set saturates at the clamp rather than overflowing.

### 13.3 The relation to today's `StatSet`

`StatSet` today is dense `float` channels with exactly two modifier kinds, folded as
`(Base + sum Flat) * max(1 + sum Percent, MinimumScale)`, with the channel index allocated and named
entirely by the game and no stat id, no registry and no metadata
(`KhaozEngine.Stats/StatSet.cs:232-233`, `KhaozEngine.Stats/StatModifier.cs:13`, `a-engine.md:164-237`).

**Contract: the new evaluator REPLACES `StatSet` for content-driven stats. `StatSet` stays, unchanged, for
games that do not adopt content stats.** The two coexist as siblings, and a game uses one or the other for
a given stat, never both.

The contested alternative is to keep `StatSet` and have a game map stat ids to channels, so it is scored.

| Criterion | Map ids onto `StatSet` | New integer evaluator | 
|---|---|---|
| Determinism between client and server | 2 | 10 |
| Supports `More` without a breaking change to `StatModifier` | 3 | 10 |
| Clamp, tags and conditions | 2 | 9 |
| Cost to build | 9 | 4 |
| Keeps the bit-for-bit add-then-remove restore property | 6 | 9 |
| Consumer churn | 8 | 4 |
| Total | 30 | 46 |

Recommendation: the new evaluator. The mapping option wins on cost and loses on the two things that
matter. Adding a `More` kind to `StatModifier` is a breaking change to a shipped struct, and the engine's
own survey says so (`a-engine.md:1558-1571`). More decisively, `StatSet` is float, and float is exactly
what section 13.4 forbids on this path.

`StatSet` is NOT deprecated by this. It remains the right kernel for a game whose stats are a handful of
channels with no client-server agreement requirement, and its remarks about fold order and running totals
stay true.

### 13.4 The determinism rule

**No floating point anywhere a client and a server must agree.** Every stat value, every modifier, every
roll, every threshold and every displayed number on this path is an integer.

The reason is not style. A client computes a tooltip and a server computes a hit, and if the two disagree
by one unit in the last place, the player sees a number that is not the number that was used. Float
addition is not associative, so the disagreement is a function of ORDER, which means it appears only for
some items and only sometimes, which is the worst shape a bug can have.

This forbids: `float`, `double`, `MathF.*` on a value path, a percent stored as a fraction, and a display
formatter that divides by the scale in floating point. It permits float on a purely PRESENTATION path that
feeds no decision, for example an animation lerp driven by a stat, as long as the stat itself arrived as an
integer.

**Expensive to change once data exists: yes.** The scale, the basis-point convention and the rounding rule
are baked into every stored roll and every authored range. Changing any of them restates every number in
the game.

**Decided at gate 0:** `Increased` and `More` keep their names (section 17, decision 10).

## 14. Random source contract

### 14.1 The seam

```csharp
public interface IRandomSource
{
    int    NextInt(int minInclusive, int maxExclusive);
    ulong  NextULong();
    ushort NextRollPosition();          // uniform over 0..65535, section 6.4
    void   NextBytes(Span<byte> destination);
}
```

`NextInt` throws when `maxExclusive <= minInclusive`, which is a caller bug rather than a draw. There is
NO `Seed` property, no `State` property, no `CreateDerived` and no way to ask an instance what it will do
next. That is the whole point: a seam whose seed is readable is a seam a crafting system can leak.

Nothing here returns a float, which is section 13.4's rule applied to the random path. A weighted choice is
made with `NextInt` over an integer weight total.

**The engine has no seam at all today**, verified by grep: two concrete types, `DeterministicRng` and
`TileActorRandom`, with nothing between them (`a-engine.md:1259-1269`). So this is new, and it is
deliberately narrow.

**`IRandomSource` lives in `KhaozEngine.Primitives`**, not in either new package. `Primitives` already
owns `DeterministicRng` (`KhaozEngine.Primitives/DeterministicRng.cs`), which `SeededRandomSource` wraps,
and it sits below every package either program names, so a seam declared there is reachable from both with
no new dependency edge and no new package (section 3.2).

Declaring it in `KhaozEngine.ItemInstances`, beside the crafting code that does most of the rolling, would
close a cycle. Scope A's `KhaozEngine.Catalog` is a `Foundation` package and needs the seam itself, for the
loot draw it owns and for the decoder fuzzing that feeds it random bytes, and `ItemInstances` already
depends on `Catalog` (section 3.2). `Catalog` referencing `ItemInstances` back for the interface points the
graph both ways, which is not a thing to discover at implementation time.

### 14.2 The two implementations

Both ship in `KhaozEngine.Primitives` beside the seam, so taking `IRandomSource` never costs a second
package reference.

**`CryptographicRandomSource`**, for hosted servers. Seeded from the OS through
`System.Security.Cryptography.RandomNumberGenerator`, which is the only cryptographic randomness in the
engine today and exists solely in the two Identity PKCE helpers
(`KhaozEngine.Identity.Discord/Pkce.cs:14`, `KhaozEngine.Identity.Oidc/Pkce.cs:13`,
`a-engine.md:1264-1269`). Uniform integer draws use rejection sampling rather than modulo, because modulo
bias on a crafting roll is a real edge a player can farm.

**`SeededRandomSource`**, for tests and for any deterministic replay. It WRAPS `DeterministicRng`
(`KhaozEngine.Primitives/DeterministicRng.cs:16-72`) rather than reimplementing a generator, so the
engine keeps exactly one seeded stream definition and the two known vectors already pinning that stream in
the test suite keep doing their job. Its constructor takes the seed. The seed is not readable back off the
instance, so a test that wants to assert on a seed asserts on the one it passed in.

### 14.3 The journal rule

**A journal event records the RESOLVED OUTCOME and never a seed, a state or a draw index.** A craft event
carries the item that came out. A drop event carries what dropped.

This is the owner's decision (#882 comment 3, 2026-09-14) and it is also the only shape that survives the
journal's own replay model: a replay returns the ORIGINAL receipt and result rather than re-running
anything (`a-engine.md:535-564`), so an event carrying a seed would have to be re-rolled to be meaningful
and a re-roll on replay is a duplication bug wearing a hat.

### 14.4 Where consumers get one

**A constructor parameter, never an ambient static, never a service locator, never a default.** A type
that rolls takes `IRandomSource` in its constructor and holds it. A type with no `IRandomSource` cannot
roll, which is the property that makes "does this class have gameplay randomness" answerable by reading its
signature.

There is no engine-provided default instance, because a default is how a production server ends up on the
test source. Both consumers are already shaped for this and both are adopters:

- **Grimhollow** passes a plain `int seed` constructor parameter to every roller already
  (`GrimhollowLoot`, `GrimhollowCombatRules`, `GrimhollowGathering`, `MonsterSpawners.Install`), with the
  four production seeds being integer literals in one file and a comment beside them saying the literal
  must not survive to a shared server (`b-grimhollow.md:786-836`). The adoption is a parameter type change
  from `int seed` to `IRandomSource`, and the comment's own stated answer becomes the cryptographic
  implementation. Tracked as [Grimhollow #214](https://github.com/APKiwiOrg/Grimhollow/issues/214). No test
  depends on the production literals, and every test already passes its own seed through the same
  parameter, so the tests move to `SeededRandomSource` one construction at a time
  (`b-grimhollow.md:837-850`).
- **Ruinborne** creates one UNSEEDED `System.Random` at server start and passes it to `LootRoll.Roll`
  (`Ruinborne.Server/Program.cs:473`, `Ruinborne.Core/Loot/LootRoll.cs:18-33`,
  `c-ruinborne.md:718-734`). `LootRoll`'s own doc already says the RNG is injected purely so tests can seed
  it, so the seam is the right shape and only the type changes. Unseeded is better than a constant literal
  and still wrong: `System.Random`'s sequence is not guaranteed stable across .NET releases, which is the
  stated reason `TileActorRandom` exists at all (`a-engine.md:1252-1258`).

**Expensive to change once data exists: no.** No seed, state or draw index is ever durable, by rule 14.3.
That is precisely what makes the source swappable at any time.

**Decided at gate 0:** a hosted server may run the seeded source, behind an explicit host option that logs
a Warning line on every boot it is set (section 17, decision 11).

## 15. Integer and encoding rules shared by every format

These apply to every format either program defines: the pack chunk, the manifest, the remap rule, the
instance payload, the container page, the quarantine wrapper and every wire message.

**Endianness.** LITTLE ENDIAN, written and read through `System.Buffers.Binary.BinaryPrimitives` with the
endianness in the method name (`WriteInt32LittleEndian`, `ReadUInt16LittleEndian`). Both sides, always.
`ItemContainerCodec` is the cautionary detail: its READ side is explicit `BinaryPrimitives` and its WRITE
side relies on `BinaryWriter`'s documented little endian, an asymmetry the survey flags
(`KhaozEngine.Items/ItemContainerCodec.cs:61-63`, `a-engine.md:83-101`). `BitConverter` is FORBIDDEN,
because it is host endian: `TileProtocol`'s game-message `kind` uses it and is the one latent
inconsistency in the tree (`TileProtocol.Frames.cs:179`, `a-engine.md:1629-1635`).

**Varint.** Unsigned LEB128: seven value bits per byte, low group first, high bit set on every byte but
the last, at most five bytes for a 32 bit value and ten for a 64 bit one. A field DECLARED SIGNED is
zig-zag encoded first, `(n << 1) ^ (n >> 31)` for 32 bit, so 0 is 0, -1 is 1, 1 is 2, and a negative number
does not cost ten bytes. Content ids, instance ids, kind ids, lengths, counts and roll positions are all
declared unsigned and are never zig-zagged, so a small id costs one byte. Encodings must be MINIMAL, and a
non-minimal or non-terminating varint is a decode failure with the reasons named in section 9.7.

**Version field first, always.** The FIRST field of every standalone format is its version, and a version
number is bumped and never reused. Three shapes exist in the tree and the contract picks one:
`ItemContainerCodec.Version` is a `public const byte` at byte 0 whose doc says bump it and never reuse it
(`ItemContainerCodec.cs:16`), `JournalProjectionCursor.FormatVersion` is a private const byte checked in
`TryDecode` returning false, and `JournalCanonicalizer.CurrentFormatVersion` is a `public const ushort`
passed as a parameter so a caller can pin an older format (`a-engine.md:1302-1313`). **Use the `ushort`,
public, as a named constant.** A byte is not enough headroom for a format that will outlive several
content generations, and the public constant is what a test pins.

A mismatched version is a REFUSAL of the whole record with a reason, never a best-effort partial read. An
instance PAYLOAD carries no version of its own, because it is never standalone (section 9.1) and its
forward compatibility comes from the tagged encoding.

**Magic prefixes.** A format STORED STANDALONE (a pack chunk file, a manifest file, a quarantine wrapper)
carries a four-character ASCII magic before its version, following `JournalCanonicalizer`'s three magics
`KJIF`, `KJEF` and `KJNF` (`JournalCanonicalizer.cs:37-79`). The content magics are `KECC` for a chunk,
`KECM` for a manifest, `KECR` for the remap rule chunk and `KECQ` for a quarantine wrapper. A format that
is always EMBEDDED in a larger versioned record carries no magic, because the enclosing record already
identified it and a magic there is four wasted bytes per row.

**Digests.** SHA-256 for everything, rendered LOWER HEX when it appears as text, raw 32 bytes when it
appears as a column or a field. This is uniform across the tree already: `TileWorldHash` uses
`Convert.ToHexStringLower` (`TileWorldHash.cs:23`), `JournalValidation.Hash` is `SHA256.HashData`
(`JournalLimits.cs:135`), and both journal providers store fingerprints as `binary(32)`
(`a-engine.md:1228-1237`). Truncation to 16 hex characters, which both Grimhollow hashes do
(`b-grimhollow.md:404-417`), is NOT adopted: a content identity is compared by machines rather than typed
by humans, and 64 characters costs nothing on a connect token.

**Domain separation.** EVERY digest is domain separated, and the domain string includes the scheme
version, following `TileWorldHash`'s `Domain = "ketw/"` plus a per-digest sub-domain plus
`SchemeVersion` folded in as the first line (`TileWorldHash.cs:19-20`, `a-engine.md:1191-1197`). The
content domain is `kec/`. No two digests in these two programs share a sub-domain, so a head comparing one
can never accidentally agree with a head comparing another.

**Text inside a digest.** Length prefixed as `"{len}:{value} "` with a bare `"- "` for null, and every
number formatted through `CultureInfo.InvariantCulture` (`TileWorldHash.cs:171-185`). Both rules exist
because of real failures the survey records: a delimiter inside an authored name colliding two different
catalogs, and `StringBuilder.Append(int)` using the current culture so a negative coordinate digests
differently under a culture with its own minus sign.

**Expensive to change once data exists: yes for endianness, the varint definition and the digest algorithm.
No for adding a magic or bumping a version.** The first three are read by every durable byte. The last two
are the mechanisms for changing things safely.

## 16. Decisions that are expensive to change once data exists

Consolidated from the per-section notes above. "Expensive" means the change requires rewriting durable
bytes that already exist in a consumer's production database, rather than a recompile or a republish.

| Decision | Section | Cost if changed later |
|---|---|---|
| Definition id is `int32`, 0 reserved as none | 5.1 | Every container slot, ground item, journal projection and remap rule names it. |
| Definition ids are never reused and never deleted | 5.1 | A reused id silently turns one stored item into another. |
| A family's declared block size | 5.2 | Moves every id in the family. |
| String keys are immutable once published | 5.3 | Every localization key, icon filename and authored cross-reference derives from them. |
| A consumer's existing string id becomes a KEY, not an ID | 5.5 | The other choice puts 64 characters in every durable slot. |
| Instance id is `int64`, node prefixed 16 plus 48 | 6.2 | The durable name a trade, a craft and a socket reference use. |
| Instance id 0 means no instance | 6.1 | It is what makes every existing plain stack cost zero extra bytes. |
| Rolls are a `ushort` POSITION, not a value | 6.4 | The other choice makes a rescale a re-roll. |
| The roll-to-value formula and its rounding | 6.4 | Silently restates every item in the world. |
| The page stamp is the version NUMBER | 7.2 | Read by the loader's remap decision and by the rewrite-on-commit rule. |
| Manifest hash algorithm and canonicalisation | 7.3 | Needs a `SchemeVersion` bump and a re-digest of every published version. |
| Chunk size is a power-of-two SLOT count | 4.5 | Renumbers every chunk and invalidates every cached client pack. |
| Remap rules are append only, in one global sequence | 8.1 | The only record of how an old page becomes a current one. |
| The remap rule encoding and apply order | 8.4 | Restates the history of every durable page. |
| The over-cap stack policy (legal, may only shrink) | 8.2 | The alternative strands or destroys player property. |
| Tagged payload: kind ranges, ascending order, no duplicates, minimal varints | 9.2, 9.3 | Byte equality is the stacking rule, so any of these changes what stacks. |
| Socket nesting is one level, decoder enforced | 9.5 | A format that permitted deeper nesting cannot be narrowed without stranding items. |
| `MaxInstancePayloadBytes = 512` | 9.6 | Raising is safe. Lowering strands items already over it. |
| Unknown field kinds are preserved verbatim | 9.4 | The only forward compatibility the format has. |
| The quarantine wrapper keeps original bytes verbatim | 10.2 | Anything else loses the item. |
| Stat scale, basis points, and the rounding rule | 13.1, 13.2 | Restates every number in the game. |
| Integers only where client and server must agree | 13.4 | The disagreement is order dependent, so it appears only sometimes. |
| Little endian, varint definition, SHA-256 | 15 | Read by every durable byte in both programs. |
| Tag ids are the tag representation in packs and payloads | 4.6 | A string tag anywhere durable is a second representation to migrate. |
| A published field in a type's schema | 4.7 | Removing it strands every row that carried it, so a field is retired, never removed. |
| Affix entries carry a flags varint | 9.9 | Adding it later changes the byte length of every stored affix. |

Everything NOT in this table is cheap by comparison: package names, visibility levels assigned to a
field, localization keys, the random source implementation, the validator's findings, the alert names and
every runtime policy in section 10.

## 17. Owner decisions at gate 0 (2026-09-15)

Every question this document carried into gate 0 was answered on 2026-09-15, and the RECOMMENDED DEFAULT
was taken in all twelve. Entry 13 is a decision the owner added rather than an answer to a question the
draft asked. Each entry states the decision, names the section it binds, and keeps what the other answer
would have cost, so a later revisit does not have to reconstruct the reasoning.

1. **A socketed item does NOT gain experience or levels while socketed, in v1.** (From #884, "Open for the
   owner at the first design gate", section 9.5.) The other answer makes a socketed item a high-frequency
   write path, so the nested payload would need a checkpoint policy and the container page commit would
   stop being driven by player action. Nothing in the byte format turns on it, because a nested payload can
   already carry an experience field, so v2 can revisit it without a format change.
2. **Socket types DO restrict what a socket accepts, and the restriction is CONTENT on the socket type row
   rather than code.** (From #884's same list, section 9.5.) Had the answer been no, the `SocketTypeId`
   field would have stayed in the format with every value 0 and the socket type content type would have
   registered with no rows. Either answer left the format alone, which is why it was safe to defer.
3. **The engine-allocated int definition id IS shown to authors, read-only beside the key.** (Section 5.5.)
   Hiding it would send an operator reading a quarantine reason or a log line to an id they cannot look up,
   so the admin console would have needed an id-to-key search instead.
4. **A rescale is SILENT.** (Section 6.4.) A rescale is a balance edit and the item's displayed value simply
   changes. Telling the player would have cost nothing in the byte format: the remap rule already carries
   the sequence number and the version a notification needs.
5. **The page stamp carries the version NUMBER only.** (Section 7.2.) The hash would have added 36 bytes to
   every page across millions of owned items, to detect a case (two publish lines against one durable store)
   that is ruled out while there is no staging environment. **Revisit the moment staging arrives**, because
   adding the hash later is a page format change.
6. **A client that is BEHIND on content is REFUSED at the door**, not admitted read-only while it fetches.
   (Section 7.5.) Admitting it needs a per-connection content version and a partial replication rule, which
   is a large amount of new machinery for a case the fetch-then-rejoin loop already covers.
7. **A retired definition's placeholder items are NOT auto-converted to a refund.** (Section 8.5.) A
   deliberate `ReplacedBy` rule pointing at whatever the owner decides to give is the only path. Automating
   it would give the engine a policy about what player property is worth, which it should not have.
8. **An unidentified item is ITS OWN MECHANIC, built on `OwnerOnly` rather than replacing it.**
   (Section 11.3.) Unidentified has to hide fields from the OWNER too, which is a fourth level the three in
   11.1 deliberately do not have. A fourth level had to be decided here or not at all, because the level
   count is in section 16's expensive table.
9. **The engine DOES ship per-language text chunks for languages the game does not.** (Section 12.4.) The
   chunks are per language and independently addressed, so the cost is a chunk nobody fetches. The other
   answer stops a translation shipping ahead of a client release, which partly defeats the
   new-content-without-a-client-release decision.
10. **`Increased` and `More` keep their names.** (Section 13.4.) They are the clearest available names for
    additive-pool versus multiplicative, and a rename costs nothing durable, so this stays the cheapest
    decision in the list to revisit.
11. **A hosted server MAY run the seeded random source**, behind an explicit host option that logs a Warning
    line on every boot it is set. (Section 14.4.) Forbidding it makes a production-shaped repro of a
    crafting bug impossible.
12. **Grimhollow's `feature/item-drop` branch lands FIRST**, before these contracts, and is absorbed in
    Grimhollow adoption phase 1. (Sections 6.6 and 7.6, evidence at `b-grimhollow.md:1316-1379`.) This was
    a sequencing question about two repos' release order rather than a design one. Had the contracts landed
    first, the branch's `assets/config/items.jsonc` would have become a third authored content mechanism
    needing migration immediately rather than at adoption.
13. **A socket entry carries the CONTAINED INSTANCE ID.** (Sections 9.5 and 9.8.) The owner added this at
    gate 0. A socketed item keeps its own identity while it is socketed, unsocketing restores that identity
    rather than minting a new one, and an item duplicated by a bug stays traceable to the instance it came
    from. It costs one varint per occupied socket, two bytes in the worked example.

## 18. How the two specs consume this document

A spec may REFINE anything here and may not CONTRADICT anything here. Refining means narrowing a range,
naming a concrete type where this document names a shape, choosing a chunk size within the stated bounds,
or adding a field kind inside its own reserved range. Contradicting means changing a width, a reserved
value, a byte order, an ordering rule, a formula or a vocabulary. When a spec finds that it NEEDS a
contradiction, the change comes back HERE first: this document is amended, the amendment says what moved
and why, and both specs are re-read against the new text before either is approved. A spec that quietly
diverges is the exact failure this document exists to prevent, and a divergence found at integration costs
a re-spec of both halves rather than an edit of one paragraph.
