# Content contracts: the shared ground under Scope A and Scope B

**Status:** CONTRACTS DRAFT, awaiting owner gate 0. Nothing here is implemented and nothing is approved.
This document exists so the two parallel specs cannot contradict each other. Scope A is the versioned
content catalog, [#882](https://github.com/APKiwiOrg/KhaozEngine/issues/882). Scope B is owned item
instances, affixes, sockets, crafting and the stat evaluation base,
[#884](https://github.com/APKiwiOrg/KhaozEngine/issues/884). Consumers are
[Grimhollow #208](https://github.com/APKiwiOrg/Grimhollow/issues/208) and
[Ruinborne #465](https://github.com/APKiwiOrg/Ruinborne/issues/465). Neither spec is implemented before
the owner approves both.

Every rule below is written to be coded against: types, widths, byte order, ranges and reserved values.
Each contract section carries the rule, the engine precedent it follows or deliberately departs from with
the file cited, a short rationale, an explicit note on whether the decision is expensive to change once
data exists, and any question that has to go back to the owner.

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
| `KhaozEngine.Catalog.Netcode` | `ContentIdentityGateAuthenticator` and the content-version handshake layer. Depends on `KhaozEngine.Netcode` and `KhaozEngine.Catalog`. | `Server` |

**Engine precedent.** The layering rules are the README's, surveyed at `a-engine.md:1373-1407`. Three of
them bind here. A pure catalog with no SQL belongs in `Foundation` beside `Items` and `Stats`, and is
"Pure .NET" as long as it defines its own codec rather than borrowing `Content`'s JsonSchema.Net.
Anything with a SQL provider is an opt-in SIBLING pair and is NEVER bundled in an umbrella, stated twice
in the README (lines 101 and 104) and followed by both `WorldStore` and `Commerce`. `Foundation` cannot
reference `Server`-side packages, which is why the handshake gate is its own small package rather than a
type inside `KhaozEngine.Catalog`.

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

**Open question for the owner.** None. The naming is the contracts author's call, and section 17 records
it as a decision rather than a question.

## 4. Content type registry

### 4.1 What a content type is

A content type is a named family of rows sharing one id space, one row codec, one validator and one
visibility default. Item bases are a type. Stats are a type. Mods are a type. A game's stores are a type.

### 4.2 Registration

```
RegisterContentType(
    ushort  typeId,          // stable numeric id, see 4.3
    string  typeKey,         // stable string key, see 5.5 character rules
    IContentRowCodec codec,  // encode and decode one row
    IContentValidator validator,
    ContentVisibility defaultVisibility,   // Client or ServerOnly, see 11
    int chunkRows)           // see 4.5
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

A chunk is a contiguous id range within one type, and `chunkRows` is the number of id SLOTS per chunk, not
the number of populated rows. It must be a power of two between 256 and 65,536, and the default is 4,096.
Chunk boundaries are therefore `floor(id / chunkRows)`, computable without a lookup.

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

**Open question for the owner.** Whether an engine-allocated int id is exposed to authors at all, or
whether the admin console shows keys only. Recommended default: show the id read-only beside the key,
because an operator reading a quarantine reason or a log line sees ids, and a console that cannot show
one makes the log unreadable.

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

**Open question for the owner.** Whether a rescale is silent or whether the player is told. Recommended
default: silent, because a rescale is a balance edit and the item's displayed value simply changes. If the
owner wants it visible, the remap rule already carries the sequence number and version needed to build a
notification, and nothing in the byte format has to change.

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
changes value. Its items carry no per-instance state at all today, so every existing stack maps to
instance id 0. The first Grimhollow item that gains a property is the first instance id it allocates.

**Expensive to change once data exists: yes for Ruinborne, no for Grimhollow.** Ruinborne's is a data
migration over millions of rows with a dual-read window. Grimhollow's is a no-op.

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
for v1 by having no staging environment (#882 comment 2, 2026-09-14). Section 17 carries this as the
question to revisit if staging arrives.

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
minimum server build, the minimum client build, then for each content type sorted by type id, the type id,
the type key, the chunk count, and for each chunk in index order the chunk index and its chunk hash.

The CHUNK HASH is plain SHA-256 of the chunk's uncompressed canonical bytes, lower hex, under sub-domain
`kec/chunk/`. It is the chunk's content address, so a chunk that did not change between two versions has
the same hash and a client already holding it fetches nothing. That is the mechanism behind the owner's
"download size after a one-item edit" budget (#882 body, item 12).

The CLIENT manifest is built the same way over the client-visible chunks only (section 11), so it has its
own hash and is never equal to the server manifest's.

### 7.4 Minimum builds

The manifest carries `MinimumServerBuild` and `MinimumClientBuild`, each an `int`. A build number here is
the engine content SCHEME generation, not a version string and not a game version: it increments when a
content type's row codec gains a field an older reader cannot skip.

- A server whose build is BELOW `MinimumServerBuild` refuses to load the version and fails the boot
  closed, per the owner's fail-closed rule (#882 body, item 8). It does not fall back to an older version,
  because an operator who published a version intends it to be live and a silent downgrade is how a
  fleet ends up serving two different catalogs.
- A client whose build is BELOW `MinimumClientBuild` is refused at the connect door with a distinct
  refusal token, so the client can tell the player to update rather than showing a generic mismatch.
- A reader ABOVE either minimum is always fine. Forward compatibility within a build generation comes from
  the tagged encoding in section 9, and the minimum build is the explicit statement that the tagged escape
  hatch was not enough this time.

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
(`b-grimhollow.md:420-434`). The in-flight `feature/item-drop` branch changes that fourth layer's value to
`GrimhollowGameDataHash.Current`, a HYPHEN-JOINED pair of the skilling hash and a new item-properties
hash, joined rather than rehashed so an operator reading a refusal can see which half moved, and with no
colon in it because `GrimhollowConfigGate.TryParseMismatch` splits on colons
(`b-grimhollow.md:1341-1350`).

**Contract.** In phase 1 of Grimhollow's adoption, the joined game-data hash is REPLACED by the content
version layer of section 7.5, and `GrimhollowConfigGate` is deleted rather than re-pointed. Specifically:

- `skilling.jsonc` and `items.jsonc` both become content types, per the owner's ruling that the skilling
  data and the item properties are versioned content (#882 body). Once they are content, their per-file
  hashes have nothing left to describe.
- The operator-legibility property the hyphen join was built for is preserved and improved: the refusal
  token in 7.5 carries BOTH sides' version numbers, so an operator sees "server 47, client 44" rather than
  having to diff two digests. That is strictly more legible than knowing which of two files moved.
- Until phase 1 lands, the joined hash stands as shipped. This contract does not ask the in-flight branch
  to change, and it explicitly does NOT adopt the hyphen join into the engine: an engine layer that joins
  two sub-hashes would need a rule for how many sub-hashes there are, and the content version number
  answers the same question with one comparable integer.
- The `tradable` flag from `items.jsonc` becomes a per-definition field on the engine item base type, and
  the fail-closed every-live-item-needs-a-row rule becomes the publish validator's coverage check
  (section 10). The retired-items-answer-untradable rule becomes the retire policy in section 8.

**Expensive to change once data exists: no for the door, yes for the manifest hash.** The door layer is
negotiated fresh on every connect and carries nothing durable. The manifest hash is recorded against every
published version, so changing the algorithm or the canonicalisation requires a scheme version bump and
re-digesting every version, which is why `SchemeVersion` exists.

**Open question for the owner.** Whether a client that is BEHIND on content should be refused or should be
allowed in read-only while it fetches. Recommended default: refused, matching every other gate in the nest
and the owner's server-restart apply model. A fetch-then-rejoin loop is the client's own business and needs
no server state.

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
that meet it must fail closed rather than skip it, which is what `MinimumClientBuild` is for.

**Open question for the owner.** Whether a retired definition's placeholder items should be automatically
converted to a currency refund at some later version. Recommended default: no, and leave it to a
deliberate `ReplacedBy` rule pointing at whatever the owner decides to give. Automation here is a policy
the engine should not have.
