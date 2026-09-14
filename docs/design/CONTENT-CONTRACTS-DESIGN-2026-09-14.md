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
