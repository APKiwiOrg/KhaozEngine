# Owned item instances, affixes, sockets, crafting and the stat evaluation base

**Status:** SPEC DRAFT, awaiting the owner at gate 1. Nothing here is implemented. This is Scope B,
engine program [#884](https://github.com/APKiwiOrg/KhaozEngine/issues/884), specified in parallel with
Scope A, the versioned content catalog,
[#882](https://github.com/APKiwiOrg/KhaozEngine/issues/882). Both are written against
[`CONTENT-CONTRACTS-DESIGN-2026-09-14.md`](CONTENT-CONTRACTS-DESIGN-2026-09-14.md), which the owner
approved at gate 0 on 2026-09-15 and which is BINDING on this document. Neither spec is implemented
before the owner approves both. Consumers are
[Grimhollow #208](https://github.com/APKiwiOrg/Grimhollow/issues/208) and
[Ruinborne #465](https://github.com/APKiwiOrg/Ruinborne/issues/465).

Where this document and the contracts disagree, the contracts win and this document is wrong. Section
22 is the only channel for changing that. It carries FOUR requests, all four accepted by the owner and
already folded into the contracts: the worked affix example reordered into canonical form,
`KhaozEngine.ItemInstances.Journal` recorded in the package set, the payload cap's characterisation
restated, and fail-closed at boot bound to the content version rather than to a code registration.
Section 22 also lists the third-round contract amendments this document is written against.

Every byte layout below is written to be coded from without guessing, every algorithm is written as
steps rather than as a description, and every claim about code that exists today cites a file and a
line. The file-and-line citations are against KhaozEngine `d4990be5`, Grimhollow `36f0139a` plus the
`feature/item-drop` branch at `e6c84dfc`, and Ruinborne `1b85e1aa`, through the three read-only
surveys taken on 2026-09-14. Where a survey and this document disagree, the survey is the fact.

## 1. Goals and non-goals

### 1.1 What this builds

The durable, byte-level foundation for MILLIONS of owned items that carry crafted properties, and the
stat machinery those properties feed. The owner's framing at gate 0 is a mix of OSRS, Tibia, Path of
Exile and Mortal Online on ONE item model, with PoE-scale complexity as the eventual target and v1 as
the scaffolding able to carry it (contracts 1.4). PoE's own mods, currencies and names are never
copied: what is built is the shape, and the owner authors the content.

Nine things, in the order #884's "In scope" list names them:

1. **The instance record.** A definition id plus a 64 bit instance id for any item carrying properties,
   a tagged field payload keyed by registered property kind, unknown fields preserved on re-encode,
   content ids as varints, rolls as 16 bit positions mapped through integer maths that is identical on
   both sides. Sections 3 and 12.
2. **Sockets.** An ordered socket list, each socket with an optional content socket type and an
   optional contained item that KEEPS its own instance id, nested one level only. Section 3.5.
3. **Visibility per property kind**, owner only, everyone or server only, with replication and tooltips
   going through one function. Sections 7.4 and 12.5.
4. **Stacking**, by byte equality over canonical payloads, with a definition carrying durability or
   sockets barred from stacking at publish. Section 4.6.
5. **Validation**, quarantining rather than deleting, bytes kept verbatim, placeholder presentation,
   one counter and one log line. Section 12.
6. **Containers and persistence.** `ItemStack`, `ItemContainer` and container codec version 2 carrying
   payloads, containers paged at about 100 slots with one journal section per page, back-to-back
   operations merged into one durable write, and a per-page content version stamp driving remap on
   load. Sections 4, 5 and 6.
7. **Ground items and the network.** Ground drops carrying instances, container contents syncing a page
   at a time over a fragmented reliable stream, and other players receiving only what they may see.
   Section 7.
8. **Affix content types** registered with Scope A, plus the generator that rolls from them and the
   crafting framework that transforms them. Sections 8, 9 and 10.
9. **The stat evaluation base**, integer only, three combine kinds, tag scopes and conditions, a
   deterministic source order, recomputed when a source changes and never per tick. Section 11.

### 1.2 What v1 must be, precisely

**A strong base, not the full system.** The owner's rule for the item model is the opposite of the rule
for the content: the MODEL has to be right now because it is the part that must be extensible, while
the BREADTH arrives gradually (contracts 1.4). Concretely that means the byte formats, the id spaces,
the ordering rules and the evaluation formula are settled here and are expensive to move later
(section 21), while the number of mods, the number of currencies and the depth of the crafting tree
are content the owner authors afterwards with no engine change.

### 1.3 Non-goals for v1

Named because a reader will otherwise assume them, and each is out on the owner's own decision
(#884, "Out of scope for v1"):

- **The full currency, fossil and essence catalog.** Section 10 ships the FRAMEWORK and a set of
  fourteen primitives. The rows that compose them into a currency are content, and v1 ships none.
- **A trade market and its search index.** Nothing here builds a listing, a search, an index or a
  price. The instance id is the durable name a market would eventually key on, and that is the whole
  of v1's contribution to it.
- **Player trading.** The journal already has the shape for it (one commit across two player streams
  with ordinal stream locking, `JournalCommit.cs:29`, and `PresentAtCommit`, `JournalCommit.cs:56-62`).
  Section 18 tells Grimhollow to set that flag when trading arrives. No trade path is built here.
- **Links.** Sockets are in and links are out, decided by the owner (#884 body). Nothing in the socket
  field carries an adjacency, and adding one later is a Scope B field kind rather than a format change.
- **Passive trees.** The evaluator's source kinds reserve ordinals for passives and buffs (section
  11.4) so a later program adds a source rather than changing the fold. No tree, no allocation, no
  respec.
- **Socketed item experience.** Decided at gate 0, decision 1: a socketed item does not gain experience
  or levels while socketed in v1. Nothing in the byte format turns on it, because a nested payload can
  already carry an experience field, so v2 revisits it without a format change.

## 2. Packages and layering

### 2.1 The package set

Contracts 3.2 already placed `KhaozEngine.ItemInstances` in the `Foundation` umbrella depending on
`KhaozEngine.Items` and `KhaozEngine.Catalog`. This section refines that into the full set, because one
piece of Scope B cannot live there: anything that builds a `JournalCommit` needs
`KhaozEngine.WorldStore`, and `Foundation` cannot reference a `Server`-side package (README layering,
`a-engine.md:1373-1407`).

| Package | Umbrella | New or modified | Depends on |
|---|---|---|---|
| `KhaozEngine.ItemInstances` | `Foundation` | NEW | `Items`, `Catalog`, `Primitives` |
| `KhaozEngine.ItemInstances.Journal` | `Server` | NEW | `ItemInstances`, `WorldStore` |
| `KhaozEngine.Items` | `Foundation` | MODIFIED | unchanged, still pure .NET |
| `KhaozEngine.TileWorld.Netcode` | `Server` | MODIFIED | unchanged, gains NO items dependency |
| `KhaozEngine.WorldStore` | `Server` | UNCHANGED | see 2.4 |
| `KhaozEngine.Stats` | `Foundation` | UNCHANGED | see 11.2 |

**`KhaozEngine.ItemInstances.Journal` is the one addition to the contracts' package list, and it exists
for a layering reason rather than a size reason.** The paged container's commit builder (section 6)
composes `JournalCommit`, `JournalProjectionWrite`, `JournalEvent`, `JournalStreamMutation` and
`JournalOperationIdentity`. Putting it inside `ItemInstances` would drag `WorldStore` into `Foundation`
and therefore into every client build. Putting it inside `WorldStore` would give the journal an opinion
about items, which is exactly what the journal design ruled out and what the code still honours: a grep
for `ItemStack`, `ItemContainer` or `KhaozEngine.Items` across `WorldStore`, both providers and both
journal design docs returns ZERO hits (`a-engine.md:643-661`). A third small package in the `Server`
umbrella keeps both properties.

**`KhaozEngine.TileWorld.Netcode` gains no dependency on items, deliberately.** `TileGroundItem`'s own
remark says it is "two meaning-free integers rather than a dependency on `KhaozEngine.Items`"
(`TileGroundItem.cs:7-12`), and section 7 keeps that true: the sibling component carries an opaque
`long` and an opaque `byte[]`, and the fragmentation primitive it ships is item-agnostic and serves any
oversized game message.

### 2.2 Public types, per package

`KhaozEngine.ItemInstances`, the payload and container half:

| Type | Shape | Section |
|---|---|---|
| `ItemInstancePayload` | static: `TryDecode`, `Encode`, `Validate`, `PublicView`, `SequenceEqual` | 3.2 |
| `ItemInstancePayloadBuilder` | mutable field set, encodes to canonical bytes | 3.2 |
| `InstancePropertyKind` | `public const ushort` per engine and Scope B kind | 3.3 |
| `InstancePropertyRegistry` | `Register(kind, codec, visibility, identificationMaskBit, shape, references)`, frozen at first pack load | 3.3 |
| `InstanceFieldShape`, `InstanceReferenceTarget` | where a kind's content ids sit and which type each belongs to | 3.3 |
| `InstanceKindBand` | enum `Engine`, `ScopeB`, `Game`, the caller band a registration declares | 3.3 |
| `PropertyVisibility` | enum `ServerOnly`, `OwnerOnly`, `Everyone` | 12.5 |
| `ItemSlot` | `readonly record struct (ItemStack Stack, ReadOnlyMemory<byte> Payload, bool Quarantined)` | 4.3 |
| `ItemContainerPage` | one page's slots plus its content version stamp and dirty flag | 5.3 |
| `PagedItemContainer` | page geometry, capacity gate, page lookup, dirty set | 5.6 |
| `ItemContainerPageCodec` | `Encode`, `TryDecode`, `Validate`, `Version` | 4.4, 5.3 |
| `InstanceIdAllocator` | node-prefixed, durable high-water mark, `Rotate` | 3.6 |
| `InstanceValidator` | `Validate(page, snapshot)` returning findings | 12.2 |
| `InstanceValidationReport`, `InstanceValidationFinding` | accumulating, side-effect free | 12.2 |
| `QuarantineWrapper` | `Wrap`, `TryUnwrap`, `Verify` over the `KECQ` format | 12.4 |
| `ItemInstanceVisibility` | the ONE `CanSee` function plus `PublicView` | 12.5 |

`KhaozEngine.ItemInstances`, the content and rules half:

| Type | Shape | Section |
|---|---|---|
| `ItemGenerator` | `ctor(ModCandidateTables, IRandomSource)`, `Generate(in GenerationContext)` | 9.4 |
| `GenerationContext`, `GenerationResult` | inputs and the resolved item | 9.4 |
| `ModCandidateTables` | built at boot, queried per roll | 9.2 |
| `CraftPrimitive` | enum of the fourteen v1 primitives | 10.2 |
| `CraftGuard`, `CraftGuardKind` | the guard vocabulary | 10.3 |
| `CraftPlan`, `CraftOutcome`, `CraftRefusal` | a resolved sequence and its answer | 10.4, 10.6 |
| `CraftingRegistry`, `ICraftOperation` | game-registered code operations | 10.5 |
| `ContentStatEvaluator` | the integer evaluator | 11.6 |
| `StatModifierLine`, `StatCombineKind`, `StatSourceKey`, `StatContext` | the evaluator's value types | 11.2 |
| `IStatConditionRegistry` | game conditions above the engine range | 11.3 |

**`IRandomSource`, `CryptographicRandomSource` and `SeededRandomSource` are NOT in this package.** An
earlier draft of this table declared all three as public types of `KhaozEngine.ItemInstances`, and that
closes a dependency cycle: Scope A's `KhaozEngine.Catalog` needs the seam for its own loot draw and for
its decoder fuzzing, `ItemInstances` already depends on `Catalog` (2.1), and `Catalog` referencing back
for the interface points the graph both ways. Contracts 14.1 settles it: the seam and both
implementations live in `KhaozEngine.Primitives`, which already owns `DeterministicRng` and sits below
both packages, so taking an `IRandomSource` costs no new package reference from anywhere in the fleet.
This document uses the seam and declares none of it.

`KhaozEngine.ItemInstances.Journal`:

| Type | Shape | Section |
|---|---|---|
| `ContainerCommitBuilder` | accumulates page operations, emits ONE `JournalCommit` | 6.4 |
| `ContainerSectionNames` | the `<container>/p<NN>` scheme and its parser | 5.2 |
| `ItemInstanceEvents` | the event type constants and their payload codecs | 9.5, 10.6 |
| `ContainerLoadResult` | decoded pages plus findings plus the dirty set | 5.5 |

`KhaozEngine.Items`, modified:

| Change | Shape | Section |
|---|---|---|
| `ItemStack` | gains a third component, `long InstanceId`, defaulting to 0 | 4.2 |
| `ItemContainer.SetSlotAt` | the payload-carrying codec door | 4.7 |
| `ItemContainer.TakeSlotAt` | the payload-carrying take | 4.7 |
| `ItemContainerCodec.Version` | becomes a `public const ushort` at 2, with the version 1 reader kept | 4.4 |

`KhaozEngine.TileWorld.Netcode`, modified:

| Change | Shape | Section |
|---|---|---|
| `TileGroundItemInstance` | sibling component, `long InstanceId` plus opaque `byte[] Payload` | 7.2 |
| `TileWorldServer.SpawnGroundItem` | one new overload taking the instance id and payload | 7.3 |
| `TileFragmentedMessage` | item-agnostic fragmenter and reassembler over game messages | 7.5 |

### 2.3 Where the tests live

A new `KhaozEngine.ItemInstances.Tests` project referencing ONLY `KhaozEngine.ItemInstances`,
`KhaozEngine.Items`, `KhaozEngine.Catalog` and `KhaozEngine.Primitives`. The reference set is
deliberately minimal because push CI selects test projects by the reference graph and an over-broad
reference silently degrades selection (AGENTS.md). It carries `<IsPackable>false</IsPackable>` and
pins `RootNamespace` to `KhaozEngine.Tests`, per the same rule.

The other three homes are existing projects, chosen because each already references what the tests
need and adding a reference would widen its graph:

- Journal integration (`ContainerCommitBuilder`, the section naming, a real SQLite store) goes in
  `KhaozEngine.Server.Tests`, which already references `WorldStore` and both providers.
- The ground item component, the spawn overload and the fragmenter go in
  `KhaozEngine.TileWorld.Netcode.Tests`.
- `ItemStack`, `ItemContainer` and the container codec stay in `KhaozEngine.Foundation.Tests`, beside
  the five existing `ItemContainerTests` facts (`a-engine.md:132-148`).

Scale work goes in `KhaozEngine.Benchmarks` as a new `--items` mode following the journal set's shape
exactly (config with a static `Parse`, a runner, a result with `ToJson`, an output writer for
`--output`, and a checked-in baseline under `Baselines/`), plus a structural test in
`KhaozEngine.Server.Tests` mirroring `MutationJournalBenchmarkTests`. Section 17, row 5, has the detail.

### 2.4 What the journal does NOT need

**The recommended coalescing design (section 6) requires no change to `KhaozEngine.WorldStore`, no
change to either provider schema and no change to the store conformance suite.** That is a result
rather than an accident: the option that would have needed all three is scored in 6.2 and rejected in
6.3, which states exactly what it would have cost, so the owner can choose it knowingly.

### 2.5 Sequencing against Scope A

`KhaozEngine.ItemInstances` depends on `KhaozEngine.Catalog`, which Scope A has not built. Phase 1
(section 20) needs only the catalog's READ side: an id-to-row lookup per content type, a live version
number, and the remap rule list. Those are four members. Scope B phase 1 therefore proceeds behind a
narrow `IContentSnapshot` that Scope A implements, and the two phase 1s land in either order. Every
later Scope B phase needs real content types registered, so phases 4 onward are gated on Scope A's
registry and publish path being real.

## 3. The instance record and payload

### 3.1 The record, in full

An owned item is FOUR things, and only the first two ever existed in the engine before:

| Part | Type | Where it lives | Contract |
|---|---|---|---|
| definition id | `int` | the container slot entry | 5.1, 6.1 |
| count | `int` | the container slot entry | 6.1 |
| instance id | `long` | the container slot entry | 6.1, 6.2 |
| property payload | bytes | the container slot entry | 6.1, 9.1 |

There is no fifth. In particular there is no per-item row, no per-item table and no per-item object:
an instance IS its slot entry, and moving it between containers moves those four values. That is what
makes a bank of a thousand affixed items a set of ten page blobs rather than a thousand rows, and it is
what the whole of section 5 rests on.

A definition id with instance id 0 and an empty payload is a plain stack, byte for byte what
`ItemStack(int ItemId, int Count)` stores today (`ItemContainer.cs:9-17`). That is the compatibility
hinge contracts 6.1 names, and section 4.4 shows it costing FEWER bytes on disk than the format it
replaces.

### 3.2 Payload format, restated and pinned

The payload is contracts 9.1's tagged field sequence, with no header, no magic and no version byte,
because a payload never travels alone:

```
[Kind: varint uint16][Length: varint int32][Bytes: Length bytes]  repeated, zero or more times
```

The three canonical rules of contracts 9.3 are enforced by the ENCODER and checked by the DECODER:
fields strictly ascending by kind, no kind twice, every varint minimal. Together they make one set of
properties into exactly one byte sequence, which is what turns the stacking rule of 4.6 into a
`ReadOnlySpan<byte>.SequenceEqual`.

`ItemInstancePayload.TryDecode` NEVER throws. It returns false plus one of the closed reason tokens of
contracts 9.7, following `ItemContainerCodec.TryDecode` and `Validate`, whose `string?` return is null
for fine and a quarantine reason otherwise (`ItemContainerCodec.cs:43-44` and `:74-104`). The reason
set is closed because a counter is keyed on it (12.6), so a new reason is an additive, deliberate act.

An unknown kind is kept VERBATIM, in place, and re-emitted unchanged (contracts 9.4). The decoder
records it as an opaque `(kind, bytes)` pair in the builder's ordered field list, so a re-encode
reproduces the input byte for byte. This is tested by a round trip through a decoder whose registry
deliberately omits a kind the encoder wrote (17.2).

### 3.3 Property kinds, assigned

Contracts 9.2 fixes the ranges: `0` reserved, `1` to `127` engine generic fields at one varint byte,
`128` to `1023` Scope B fields at two, `1024` to `65535` game fields. The assignments below are Scope
B's to make within those ranges, and kinds 2, 5, 130, 131 and 132 are PINNED by the worked byte example
in contracts 9.8, which this document must reproduce exactly.

**Engine range, `1` to `127`.** Generic per-instance facts any game might want, which is why they are
engine rather than Scope B.

| Kind | Name | Bytes | Default visibility |
|---|---|---|---|
| 1 | `Flags` | `[Bits: varint uint32]`, a bitfield, bit 0 `Corrupted`, bit 1 `Mirrored`, bit 2 `Fractured`, bits 3 to 31 reserved and 0 in v1 | `Everyone` |
| 2 | `ItemLevel` | `[Level: varint uint16]`, 1 to 65535 | `Everyone` |
| 3 | `Quality` | `[Quality: varint uint16]`, in whole percentage points | `Everyone` |
| 4 | `Charges` | `[Current: varint uint32][Maximum: varint uint32]` | `OwnerOnly` |
| 5 | `Durability` | `[Current: varint uint16][Maximum: varint uint16]` | `OwnerOnly` |
| 6 | `BoundTo` | `[Subject: varint uint64]`, a character or account instance subject, 0 illegal | `OwnerOnly` |
| 7 | `Materials` | `[Count: varint][ [MaterialId: varint int32][Parts: varint uint16] ] * Count`, authored order preserved | `Everyone` |
| 8 | `Tier` | `[Tier: varint uint16]`, an upgrade rank, the Tibia plus-one shape | `Everyone` |
| 9 to 127 | reserved for the engine | | |

**Scope B range, `128` to `1023`.**

| Kind | Name | Bytes | Default visibility |
|---|---|---|---|
| 128 | `Identification` | `[State: byte, 0 unidentified or 1 identified][RevealedMask: varint uint32]` | `Everyone` |
| 129 | `UniqueTemplate` | `[TemplateId: varint int32]` | `Everyone`, identification gated |
| 130 | `Rarity` | `[RarityId: byte]`, a `rarity_rule` row's ordinal | `Everyone` |
| 131 | `Affixes` | `[Count: byte][ affix entry ] * Count`, ascending by mod id | `Everyone`, identification gated |
| 132 | `Sockets` | `[Count: varint][ socket entry ] * Count`, AUTHORED order | `Everyone` |
| 133 | `Enchantments` | the SAME entry layout as 131, ascending by mod id | `Everyone`, identification gated |
| 134 | `RareName` | `[TemplateId: varint int32][WordCount: byte][WordId: varint int32] * WordCount` | `Everyone`, identification gated |
| 135 to 1023 | reserved for Scope B | | |

**Kind 132's count is a VARINT and kind 131's is a byte, and the difference is not an oversight.**
Contracts 9.5 writes the socket field as `[Count: varint]`, and a narrowing to a byte would be a width
change, which contracts 18 calls a contradiction rather than a refinement. The divergence would also be
invisible in the golden file, because the worked example writes `01` and that is both, so two
implementations written from two documents would agree until the day a socket count reached 128 and then
produce payloads that do not stack with their own twins. Kind 131's count is a byte because the affix
field is Scope B's own and the contract says nothing about it, and the byte is deliberate: it caps affixes
at 255, which is open question 7's format ceiling, and it costs one byte fewer on every affixed item in
the world.

**Note the visibility levels are NOT monotonic in the kind id**: kinds 4, 5 and 6 are `OwnerOnly` while
7 and 8 are `Everyone`. That is deliberate, and it is the reason section 7.4 declines the optional
optimisation contracts 11.2 offers (assigning kinds so an owner-only projection is a prefix truncation).
Coupling the kind ranges to the visibility vocabulary forever would have bought a memcpy over a
filtered copy, and section 7.4 shows the filtered copy is already a memcpy of the kept runs.

**Registration.**

```csharp
InstancePropertyRegistry.Register(
    InstanceKindBand band,                     // Engine, ScopeB or Game
    ushort kind,
    IInstancePropertyCodec codec,
    PropertyVisibility visibility,
    int identificationMaskBit,
    in InstanceFieldShape shape,
    ReadOnlySpan<InstanceReferenceTarget> references);
```

It runs ONCE at process start, before any pack is loaded, and the registry freezes when the first pack
loads. A later registration throws.

**`band` is the caller's own declaration of which range it is allowed to register into, and a mismatch
THROWS.** `InstanceKindBand.Engine` may register 1 to 127, `ScopeB` 128 to 1,023 and `Game` 1,024 and
above, and a caller that names a band and a kind outside it is refused at startup. 3.3 already said a game
MAY NOT register into the engine or Scope B ranges and named no mechanism, which makes it a comment rather
than a rule: without the band the first sign of a collision is an engine release landing on a kind a game
took, months later, with two codecs and stored payloads under both. `ReplicationRegistry.FirstExtensionTypeId`
is the engine's existing precedent for exactly this, one level up. `CraftingRegistry` (10.5) carries the
same argument for its 1,024 floor.

**`identificationMaskBit` is a FIXED bit, assigned at registration, and it is -1 for a kind that is not
identification gated.** It is not the kind's position in the ascending list of gated kinds. An earlier
draft derived the bit that way and it is a durable-format bug: `RevealedMask` lives inside kind 128 in
every stored payload (12.7), the derived index is computed from the live registration set and is recorded
nowhere, and 3.3 reserves engine kinds 9 to 127, so ANY future engine gated kind lands below 129 and
shifts every stored mask by one. A partially identified item would then reveal a different field than the
one the player paid to reveal, with no rule applied, no quarantine and no counter, because the bytes did
not change and nothing checks them. The v1 assignments are constants and are pinned in section 21:

| Kind | `identificationMaskBit` |
|---|---|
| 129 `UniqueTemplate` | 0 |
| 131 `Affixes` | 1 |
| 133 `Enchantments` | 2 |
| 134 `RareName` | 3 |

Bits 4 to 31 are unassigned and zero in v1. A new gated kind takes the NEXT FREE BIT, never a bit a
released engine has used, and registration throws on a duplicate bit exactly as it throws on a duplicate
kind. That makes the hazard a startup failure instead of a silent renumber. That is `ReplicationRegistry.Register`'s shape
(`TileProtocol.Components.cs:122`) and contracts 4.2's rule for the content type registry, applied one
level down. A game MAY register in the game range and MAY NOT register into the engine or Scope B
ranges, replace a registered codec, or unregister anything. Section 15.4 is why unregistering is
forbidden: it would make two previously distinct items stack and destroy one identity.

**The last two arguments are what make the remap pass and the two drift checks DERIVED rather than
hard-coded, and they are the whole reason this signature is longer than it looks.** A remap rule carries
a `TypeId` and a `FromId` (contracts 8.1) and `RemapRuleSet.Apply` lives in `KhaozEngine.Catalog`, which
cannot reference `ItemInstances` and therefore cannot know that kind 131's entries begin with a `mod` id.
Something has to say so. `ContentFieldSchema` already carries a "Reference target" for exactly this
purpose one level up (contracts 4.7), and this is that idea applied to a property kind.

```csharp
public enum InstanceSlotKind : byte { Varint = 1, Byte = 2, Fixed2 = 3, NestedPayload = 4 }
public enum InstanceCountWidth : byte { None = 0, Byte = 1, Varint = 2 }
public enum InstanceReferenceSite : byte { Header = 1, Entry = 2 }

public readonly record struct InstanceFieldShape(
    ReadOnlyMemory<InstanceSlotKind> Header,   // slots before the repeat count
    InstanceCountWidth Count,                  // None for a field that does not repeat
    ReadOnlyMemory<InstanceSlotKind> Entry);   // one repeat's slots, empty when Count is None

public readonly record struct InstanceReferenceTarget(
    string ContentTypeKey,                     // the Scope A or Scope B type key the id belongs to
    InstanceReferenceSite Site,                // Header, or Entry for once per repeat
    int SlotIndex);                            // which slot of that shape holds the id
```

The shape says WHERE a value sits in the field's bytes and the target says WHICH content type it belongs
to. A walker that has both can find, read and rewrite every content id in a payload without knowing what
any kind means. The v1 assignments:

| Kind | Shape | Reference targets |
|---|---|---|
| 1 to 6, 8 | a header of scalars, `Count` None | none |
| 7 `Materials` | `Count` Varint, entry `[Varint, Varint]` | (`item`, Entry, 0) |
| 128 `Identification` | header `[Byte, Varint]`, `Count` None | none |
| 129 `UniqueTemplate` | header `[Varint]`, `Count` None | (`unique_template`, Header, 0) |
| 130 `Rarity` | header `[Byte]`, `Count` None | (`rarity_rule`, Header, 0) |
| 131 `Affixes`, 133 `Enchantments` | `Count` Byte, entry `[Varint, Byte, Fixed2, Varint]` | (`mod`, Entry, 0) |
| 132 `Sockets` | `Count` Varint, entry `[Varint, Varint, Varint, NestedPayload]` | (`socket_type`, Entry, 0), (`item`, Entry, 1) |
| 134 `RareName` | header `[Varint]`, `Count` Byte, entry `[Varint]` | (`rarity_rule`, Header, 0), (`rare_name_word`, Entry, 0) |

Kind 134's header slot is a `rarity_rule` id rather than a `unique_template` one: it records WHICH rarity
rule's `display_format` composed this name (8.5), which is why a later `SetRarity` does not strip the
name off the item.

Three consequences, and the first two were defects in an earlier draft of this document:

- **Kind 7's material ids are `item` references and no list in this document had them.** Materials are
  input item definitions (3.8), so a retired material was invisible to both the rule pass and the
  validator. Deriving the walk from the registry found it rather than a reader finding it in production.
- **A socket's `ContainedDefinitionId` is an `item` reference at every depth**, which the closed
  enumeration also missed. It is slot 1 of kind 132's entry, so it is covered now by construction.
- **A GAME kind at or above 1,024 that carries a content id gets remap, drift detection and quarantine
  for free** by declaring its targets at registration. Without this it got none of the three, silently,
  which made a retire in a game's own type a slow data rot with no counter and no log line.

A kind that declares no references is never visited, which is every engine scalar kind above, so the walk
costs a dictionary lookup per field and nothing else on an ordinary payload.

### 3.4 The affix entry

Pinned by contracts 9.8's third affix, `84 02 02 FF FF 00`, which is mod id 260, tier 2, position
65,535 and flags 0:

```
[ModId: varint int32]     // a `mod` content row, never 0
[Tier: byte]              // the mod's AUTHORED tier ordinal, 1 to 255, never 0
[Position: uint16 LE]     // the roll position, contracts 6.4
[Flags: varint uint32]    // reserved, 0 in v1, contracts 9.9
```

Five things about it, each of which a reader would otherwise have to guess:

- **`Position` is a fixed two byte little endian `uint16`, NOT a varint.** The worked example writes
  `FF FF` for 65,535 and `CC CC` for 52,428, which is two bytes for a value a varint would spend three
  on. Positions are uniform over the whole range, so a varint would cost MORE on average, and the fixed
  width is what keeps an affix entry's length predictable.
- **`Tier` is the mod row's own authored tier ordinal, not a `mod_tier` content id.** A payload that
  named a `mod_tier` id would break the moment an author reordered a mod's tiers. The loader resolves
  the pair (mod id, tier ordinal) to the tier row, and section 8.3 requires the ordinal be unique within
  the mod and immutable once published.
- **The engine assigns tier ordinals no ordering meaning.** Whether tier 1 is the best or the worst is
  the author's convention. What gates a tier is its own `item_level_min` and `item_level_max`.
- **Prefix versus suffix is NOT in the entry.** Contracts 9.9 requires the flags field be 0 in v1 and
  reports `field-malformed` for anything else, so the distinction is read off the mod ROW's `kind`
  field. That is why the contract could reserve the whole flags varint without stranding anything.
- **The list is sorted ASCENDING BY MOD ID**, which makes the field canonical. Two items carrying the
  same three affixes at the same tiers and positions produce the same bytes regardless of the order they
  were rolled or crafted in, which is what 4.6's byte comparison needs. Section 21 records it as
  expensive to change, because the sort is baked into every stored payload.

### 3.5 The socket entry

Contracts 9.5 fixes it, including the contained instance id the owner added at gate 0 (decision 13):

```
[SocketTypeId: varint int32]           // 0 means no restriction
[ContainedDefinitionId: varint int32]  // 0 means empty
[ContainedInstanceId: varint uint64]   // 0 when empty, or when the contained item has no instance
[NestedLength: varint int32]
[Nested: NestedLength bytes]           // a payload in this same format
```

**Nesting is ONE LEVEL and the DECODER enforces it**: a nested payload containing kind 132 returns
`socket-nesting` rather than recursing (contracts 9.5). That is a structural limit, not a convention,
and it is what stops a 45 byte payload from becoming a denial of service (15.6).

**Socket ORDER is authored and never sorted**, unlike the affix list. A player who puts a gem in the
third socket expects it there, which is the same argument `TileWorldHash` makes for
`TileObjectArchetype.Tags` (`TileWorldHash.cs:117-120`). The consequence for 4.6 is exact and worth
stating: two otherwise identical items whose gems sit in different sockets do NOT stack, which is
correct, because they are different items.

**The nested budget is finite and here is the arithmetic.** A six socket item spends
`6 * (1 + 2 + 5 + 1) = 54` bytes on socket ENTRY overhead at typical id widths, plus five more on kind
132's own two byte kind varint, two byte length varint and count byte, plus 101 on its other fields,
which 3.8 counts one at a time. That is 160 bytes fixed, leaving 352 of the 512 byte cap across six
nested payloads, so about 58 bytes each. Fifty-eight bytes is one rare's worth of payload (3.8), so a six
socket item can hold six ordinary gems comfortably and cannot hold six six-affix rares. That ceiling is a v1 design consequence rather than a
defect, and raising `MaxInstancePayloadBytes` later is backward compatible by contracts 9.6. It is open
question 4.

### 3.6 Instance id allocation

Contracts 6.2 fixes the scheme: `(node << 48) | counter`, 16 node bits and 48 counter bits, counter
starting at 1, never recycled, throwing rather than wrapping, with the packed high-water mark persisted
per node, exactly `NetIdAllocator` (`NetIdAllocator.cs:14-70`). Scope B REUSES that type behind
`InstanceIdAllocator`, with its OWN persisted high-water mark, because a net id and an instance id are
different spaces that must not share a counter.

Four refinements the contract leaves to this spec:

- **The range is reserved durably BEFORE any id in it is issued**, and the ORDER is the contract rather
  than the batch size (contracts 6.2). The concrete shape: the allocator reserves a block of 4,096 ids
  by persisting `high-water + 4096` through the host's own durable store and only then hands out ids
  from below it. A crash SKIPS the unissued remainder of the block, which is free at 2^48 per node, and
  can never reissue one, which is the whole point.
- **The id is written to the wire and to disk as an UNSIGNED varint over the int64's bit pattern**, never
  zig-zagged (contracts 15: instance ids are declared unsigned). This matters because
  `NetIdAllocator.Pack(65535, counter)` sets the high bit and is a NEGATIVE `long`, so a zig-zag would
  encode it as a ten byte value with the sign flipped into the low bit. Node 0, the only shape a single
  process server has, keeps ids numerically identical to a plain counter and costs four varint bytes up
  to 268,435,455.
- **The allocator's persisted state is bound to the STORE EPOCH, and a restore rotates both.** An
  earlier draft guarded a restore with the persisted retired node list, and that guard cannot fire: a
  point-in-time restore rolls the high-water mark, the node id AND the retired list back together,
  because all three live in the store being restored, so in the restored bytes the node in use was never
  retired and the check passes. What a restore does NOT roll back is everything outside the store, which
  is exactly what collides: client caches, other shards, a ground stack on another host's region stream,
  an external system that recorded an id. So the allocator records the `store_epoch` its high-water mark
  was persisted under and REFUSES TO ISSUE when the live epoch differs. That is a comparison between a
  restored value and a value an operator rotated, rather than between two values the same restore
  rewound, which is the property the retired list could never have. The journal already owns both the
  mechanism and the runbook: `IMutationJournalMaintenance.RotateStoreEpochAsync`, and
  `DURABLE-PLAYER-JOURNAL-DESIGN-2026-09-06.md` section 10, "A point-in-time database restore must
  rotate `store_epoch` through the maintenance API before writers reopen." 15.2 is the procedure.
- **The retired node list stays, demoted to a RE-BOOT guard.** `Rotate(ushort newNodeId)` appends the old
  node id to it, and a boot refuses a node id already on the list. That catches an operator who rotates
  onto a node id this store has already used, which is an ordinary configuration mistake worth one
  refusal at boot. It is no longer the restore guard, because it cannot be one. A restore still burns one
  of 65,535 node ids.
- **Which items get an id** is contracts 6.2's rule verbatim, restated because it is easy to get
  backwards: an item gets an instance id when its encoded payload is NON-EMPTY, or its definition
  declares durability, sockets or any per-instance field. The rule is a property of the ITEM rather
  than of the definition, so a definition gaining a property later does not retroactively give every
  stored copy an id it does not have.

### 3.7 The contracts' worked example, reproduced

Contracts 9.8 is BINDING and this document must produce the same 45 bytes from the same item. It does,
and the reproduction is here so an implementer can use it as the first golden file (17.1):

```
02 01 44                                 kind 2   len 1   item level 68
05 02 5A 64                              kind 5   len 2   durability 90 of 100
82 01 01 03                              kind 130 len 1   rarity 3
83 01 12                                 kind 131 len 18  affixes
   03                                       count 3
   5B 01 33 33 00                           mod 91,   tier 1, position 13107, flags 0
   84 02 02 FF FF 00                        mod 260,  tier 2, position 65535, flags 0
   F2 20 03 CC CC 00                        mod 4210, tier 3, position 52428, flags 0
84 01 0A                                 kind 132 len 10  sockets
   01                                       count 1
   07                                       socket type 7
   C1 06                                    contains definition 833
   E9 20                                    contains instance 4201
   03                                       nested length 3
      02 01 37                              nested kind 2 len 1, item level 55
```

**The affix entries are written ASCENDING BY MOD ID, 91 then 260 then 4210, and the contract now says
so.** An earlier draft of this document reproduced them in the contract's original order, 4210, 91, 260,
and explained at length that its own canonical order sorted them. That explanation was self-contradictory
as written, since a list is not ascending "only by accident" when it is not ascending at all, and it is
moot now: change request 1 of section 22 was accepted, contracts 9.9 makes the ascending list the
CONTRACT's rule rather than this document's refinement, and contracts 9.8's block is written in that
order. So the block above may be copied into the golden file of 17.1 exactly as it stands, which was the
whole point of filing the request. The entry byte counts are unchanged either way: the three entries are
5, 6 and 6 bytes and the field payload is 18.

### 3.8 Coverage of the four reference games, with byte counts

Each row is a real item of that shape, encoded through 3.2 and seated in a container page entry through
4.4. "Payload" is the tagged field bytes. "Slot entry" is the whole entry including the payload, which
is what a page and therefore a commit actually costs. Definition ids are assumed dense from 1, so an id
under 128 costs ONE varint byte, one under 16,384 costs two and anything above costs three. The three
affixed rows below are written at a two byte id, which is the realistic case for a catalog of any size,
and the OSRS row is written at definition id 1 deliberately, to show the FLOOR: the smallest entry the
format can produce is seven bytes, and that is the number the compatibility argument rests on.

| Game | Item | Fields used | Payload | Slot entry |
|---|---|---|---|---|
| OSRS | 500 coins | none | **0** | **7** |
| Tibia | wand of vortex, 18 of 20 charges, upgrade tier 2 | 4, 8 | **7** | **15** |
| Mortal Online | crafted longsword, quality 87, durability 140 of 155, three materials in a 60/30/10 ratio | 3, 5, 7 | **20** | **30** |
| PoE | the rare Greatsword of 3.7, identified, with a rolled rare name | 2, 5, 128, 130, 131, 132, 134 | **58** | **69** |

The arithmetic, because the numbers carry the rest of the document:

- **OSRS, 0 and 7.** Instance id 0, payload empty. The entry is slot 1, entry flags 1, definition id 1,
  count 2 (500 is a two byte varint), instance id 1, payload length 1. **Seven bytes against version 1's
  fixed ten** (`ItemContainerCodec.cs:19`, `EntryBytes = 2 + 4 + 4`). An OSRS shaped bank gets SMALLER
  when instances arrive, which is the strongest single argument that the hinge in contracts 6.1 was the
  right one.
- **Tibia, 7 and 15.** `04 02 12 14` is charges 18 of 20 in four bytes and `08 01 02` is upgrade tier 2
  in three. The entry adds two varint bytes for the instance id (4,201) and one for the payload length.
  A charged, upgraded item is fifteen bytes, and it needed no mod, no roll and no socket to say so,
  which is contracts 1.5's claim about Tibia made concrete.
- **Mortal Online, 20 and 30.** The materials field stores the INPUT ids and their parts, not the stats
  derived from them, so a materials rebalance is a publish rather than a rewrite of every crafted item
  (contracts 6.3). Three materials in a 60/30/10 ratio cost eleven bytes.
- **PoE, 58 and 69.** The contracts' 45 byte example plus five bytes of identification state and eight
  of rare name. Sixty-nine bytes per bank slot, so a hundred slot page of nothing but rares is about
  6.9 KB, which is the number sections 5, 6, 7 and 16 are all sized against.

**The deepest item v1 can express, field by field.** An earlier draft of this document put it at 410
bytes and called 512 "1.25 times the deepest item", using forty byte nested payloads, while 3.5 computed
the nested budget at about 59 bytes each. Both could not be true. Here is the arithmetic so that neither
number has to be taken on trust. Every field is at its widest realistic encoding, six affixes at three
byte mod ids, six occupied sockets, and the item identified with a bound-to subject.

| Field | Kind bytes | Length bytes | Body | Total |
|---|---|---|---|---|
| 1 `Flags`, bits 0 to 2 set | 1 | 1 | 1 | 3 |
| 2 `ItemLevel`, above 127 | 1 | 1 | 2 | 4 |
| 3 `Quality` | 1 | 1 | 1 | 3 |
| 5 `Durability`, two `uint16` varints | 1 | 1 | 4 | 6 |
| 6 `BoundTo`, a subject varint | 1 | 1 | 5 | 7 |
| 128 `Identification` | 2 | 1 | 2 | 5 |
| 130 `Rarity` | 2 | 1 | 1 | 4 |
| 131 `Affixes`, count plus six seven byte entries | 2 | 1 | 43 | 46 |
| 133 `Enchantments`, count plus one entry | 2 | 1 | 8 | 11 |
| 134 `RareName`, a template id and three words | 2 | 1 | 9 | 12 |
| Subtotal, every field but the sockets | | | | **101** |
| 132 `Sockets` header: kind varint, two byte length, count | 2 | 2 | 1 | 5 |
| Six socket entries at `1 + 2 + 5 + 1` of overhead each | | | 54 | 54 |
| Fixed total | | | | **160** |
| Six nested payloads, whatever is left | | | 352 | **352** |
| `MaxInstancePayloadBytes` | | | | **512** |

Three readings come out of that and the third is the one that matters:

- A six socket item holding six FORTY byte gems is `160 + 240 = 400` bytes, 78 percent of the cap. That
  is the item the 410 figure was reaching for, and it was ten bytes out because it never counted kind
  132's own five byte header.
- The same item with its nested budget fully spent is 508 bytes, four short of the cap.
- **So the deepest item v1 can express IS the cap, by construction.** The field set can fill 512 exactly,
  and what the cap actually decides is how DEEP A GEM may be, which is precisely why
  `socket_type.max_nested_bytes` (8.7) exists: it turns that into an authored publish-time number instead
  of a refusal at the moment a player clicks socket.

Against contracts 9.6, which calls 512 "about 11 times the realistic size" and therefore "a guard rail
rather than a budget": 512 is 11 times the 45 byte worked example of contracts 9.8 and exactly 1.0 times
a maximal item, so both readings are true of different items and neither is true of both. Raising the cap
is backward compatible and lowering it is not (contracts 9.6), so nothing is at risk. Change request 3
and open question 4 are restated against this table rather than against 410.

## 4. `ItemStack`, `ItemContainer` and container codec version 2

### 4.1 The problem, stated exactly

`ItemStack` is `readonly record struct ItemStack(int ItemId, int Count)` (`ItemContainer.cs:9`), and
`ItemContainer`'s whole state is `ItemStack[] _slots` plus a `Func<int,bool> _stackable`
(`ItemContainer.cs:36-37`). Every mutating member deals in `ItemStack`: `Add`, `Remove`, `TakeAt`,
`Swap` and `SetAt` (`ItemContainer.cs:89-171`). Two facts make carrying an instance awkward:

- A payload is a variable length byte sequence, and a `byte[]` field inside a `readonly record struct`
  would make two stacks with equal bytes compare UNEQUAL, because a record struct's generated equality
  compares the reference. That silently breaks `CountOf`, every test, and the stacking rule itself.
- An instance id is a `long` and would fit the struct fine, but if it lives only on a new sibling type
  then `TakeAt(slot)` (`ItemContainer.cs:152-157`) returns a stack with no identity, and a game calling
  the member it has always called loses the item's name for a trade, a socket reference or an audit.

### 4.2 The choice, weighed

| Criterion (1 to 10) | A: third field plus a parallel payload array | B: a sibling `ItemSlot` only | C: parallel arrays only |
|---|---|---|---|
| Existing `ItemStack(int, int)` call sites still compile | 9 | 10 | 10 |
| Identity survives `TakeAt`, `Swap` and `Remove` with no new call | 10 | 3 | 2 |
| Byte equality stays the stacking test | 8 | 8 | 8 |
| No allocation for a plain stack | 9 | 4 | 9 |
| Deconstruction and equality do not move | 4 | 10 | 10 |
| One place a reader looks for a slot's whole state | 6 | 9 | 3 |
| A game cannot lose a payload by using the old member | 9 | 4 | 2 |
| Total | 55 | 48 | 44 |

**Recommendation: A.** The instance id goes on `ItemStack` as a third component defaulting to 0, and
the payload lives in a parallel array on the container, reachable through a new `ItemSlot` pair that
the codec and every move path use.

The deciding row is the last one, and it is a safety property rather than a convenience. Under B or C,
`container.TakeAt(slot)` compiles, runs, and quietly returns an item stripped of the identity a socket
reference or a durable audit needs. Under A the same call returns the identity and loses only the
payload, and losing the payload is recoverable because the definition plus the id names the item in the
journal. Option A's cost is real and is priced honestly in row five: a three component record struct
generates a three out-parameter `Deconstruct`, so any `var (id, count) = stack` in the fleet stops
compiling, and `ItemStack` equality now includes the instance id. Neither is silent. Both surface at the
first build.

### 4.3 The shapes

```csharp
public readonly record struct ItemStack(int ItemId, int Count, long InstanceId = 0)
{
    public bool IsEmpty => ItemId == 0 || Count <= 0;
    public static ItemStack Empty => default;
    public bool HasInstance => InstanceId != 0;
}

public readonly record struct ItemSlot(ItemStack Stack, ReadOnlyMemory<byte> Payload, bool Quarantined)
{
    public static ItemSlot Empty => default;
    public bool IsEmpty => Stack.IsEmpty;
}
```

`IsEmpty` and `Empty` are unchanged in meaning: a cleared slot and a never filled one are still the
same value, because `default` gives instance id 0 and an empty payload. `ItemSlot.Payload` is a
`ReadOnlyMemory<byte>` over bytes the container owns and never mutates in place, so handing one out
costs nothing and a caller cannot corrupt a container by holding one.

### 4.4 Container codec version 2, byte for byte

```
[FormatVersion: uint16 LE]             // 2. Byte 0 is also the legacy dispatch byte, see below
[PageIndex: varint uint16]             // 0 for a whole container
[FirstSlot: varint uint16]             // the container slot this page's slot 0 is
[SlotCount: uint16 LE]                 // slots in THIS page
[ContentVersion: varint int32]         // the page stamp, contracts 7.2
[EntryCount: varint int32]
then EntryCount entries, strictly ascending by Slot:
  [Slot: varint uint16]                // RELATIVE to FirstSlot
  [EntryFlags: varint uint32]          // bit 0 quarantined, bits 1 to 31 reserved and 0 in v1
  [DefinitionId: varint int32]         // never 0 on an occupied entry
  [Count: varint int32]                // always positive
  [InstanceId: varint uint64]          // the int64 bit pattern, unsigned, never zig-zagged
  [PayloadLength: varint int32]        // bounded by the SECTION cap, see below
  [Payload: PayloadLength bytes]
```

**The version dispatch, and the one rule it costs.** Contracts 15 requires a `public const ushort`
version as the first field and forbids a magic on a format that is always embedded in a larger
versioned record, which a page is: it rides inside a `JournalProjectionWrite` that already carries
`ProjectionSchema` and `ProjectionSchemaVersion` (`JournalProjectionWrite.cs:10-24`). Version 1 put a
single `byte` at offset 0 (`ItemContainerCodec.cs:16`), so a reader has to tell a v1 blob from a v2 one
before it knows how wide the version field is. **Byte 0 is the dispatch: the value 1 means the version 1
format, and anything else means a `ushort` version whose low byte is that value.** The only cost is that
container codec versions congruent to 1 modulo 256 are never assigned, so version 257 is skipped and
version 2 through 256 are free. That is 255 versions before the first skip, which outlives several
content generations, and it is written down here because a later implementer would otherwise assign 257
and break every stored v1 bank in the fleet.

**`PageIndex` and `FirstSlot` are both present and one is redundant on purpose.** The decoder checks
`FirstSlot == PageIndex * expectedPageSlots` and refuses a mismatch. The redundancy costs two bytes per
page, which is twenty bytes on a ten page bank, and it catches a page written into the wrong section,
which is section 13's row 10 and is otherwise silent.

**`EntryCount` is explicit, unlike version 1.** Version 1 derived the entry count from the blob length
because every entry was exactly ten bytes (`ItemContainerCodec.cs:57`). Version 2's entries are variable
length, so the count is declared and the decoder refuses a blob that runs out of bytes before the count
is reached, or that carries trailing bytes after it.

**`EntryFlags` is one byte per entry in v1 and it is worth it.** It is the entry level escape hatch
contracts 9.9 argued for at the affix level, applied one level up: a fact about one SLOT that is not a
fact about the item's properties has nowhere else to go, and the first such fact is already here (bit 0,
quarantined, section 12.4). Without it, a quarantined slot would have to be told apart by sniffing the
payload for the `KECQ` magic, and `K` is 0x4B, which is a perfectly legal property kind varint, so the
sniff is ambiguous. One byte per entry against ambiguity is the right trade.

**`PayloadLength` is bounded by the SECTION cap, and `MaxInstancePayloadBytes` binds a NON-quarantined
entry only.** Two bounds were in play and they contradicted: this field once declared "0 to
`MaxInstancePayloadBytes`" while 12.4 said an `OriginalLength` may exceed it. The one that is code wins
the wrong way round, so it is settled here rather than there. A quarantine wrapper carries the original
bytes VERBATIM plus eleven bytes of its own header (12.4), so an entry that quarantined for
`payload-oversize` is by construction larger than the cap it broke, and bounding the entry at
`MaxInstancePayloadBytes` would refuse to write exactly the item the wrapper exists to keep. The rule:

- A NON-quarantined entry's payload is at most `MaxInstancePayloadBytes`. The decoder refuses a larger one
  with `payload-oversize` and `SetSlotAt` throws on one (4.7).
- A QUARANTINED entry's payload is the wrapper, and its bound is the page's own: the journal's 2 MiB
  projection section cap less the rest of the page (`JournalLimits.cs:16`). 5.4 carries the arithmetic.

The case this exists for is a cap RAISE, which contracts 9.6 makes backward compatible and open question 4
expects. Engine 20.x raises the cap to 1,024, a six socket item reaches 700 bytes, and a shard still on
19.x loads the page: check 5 fires, the entry quarantines, the wrapper is about 711 bytes, and it has to
be writable or the page cannot be re-encoded at all and the whole container becomes uncommittable. One
oversize item must not cost a player their bank.

**Endianness and varints are contracts 15's**: little endian through `BinaryPrimitives` with the
endianness in the method name on BOTH sides, which fixes the asymmetry version 1 carries (its read side
is explicit `BinaryPrimitives` and its write side relies on `BinaryWriter`, `ItemContainerCodec.cs:27`
against `:61-63`, flagged at `a-engine.md:83-101`). `BitConverter` is forbidden.

### 4.5 What version 1 blobs do

**A version 2 reader reads a version 1 blob unchanged, and a version 2 writer never produces one.**
Concretely:

- `ItemContainerCodec.TryDecode` dispatches on byte 0. Value 1 runs the existing version 1 path verbatim
  (`ItemContainerCodec.cs:49-68`) and seats every slot with instance id 0, an empty payload and the
  quarantined flag clear.
- The version 1 path's `Validate` rules are kept exactly as they are, all eleven of them
  (`ItemContainerCodec.cs:74-104`), including the refusal of a blob whose declared slot count is not the
  caller's. That refusal is load bearing for Grimhollow, whose `WidenBag` and `NarrowBag` helpers exist
  precisely because of it (`b-grimhollow.md:88-102`).
- A version 1 blob carries no content version stamp. The decoded page takes stamp 0, which is older than
  every published version, so the FULL remap rule set applies to it on the first load (contracts 8.3).
  That is the correct answer and it is free: rule application is a scan that is a no-op on a page holding
  no remapped id.
- The first ordinary commit after that load writes the page back as version 2 (5.5's lazy rewrite), so a
  container migrates on the first time the player touches it and never through a migration pass.

### 4.6 The stacking rule as a byte compare

Two occupied slots MERGE when all four hold:

1. `a.DefinitionId == b.DefinitionId`.
2. The container's stackable predicate says yes for that definition. The predicate is consulted per
   operation and never cached, so a game whose rule reads its catalog sees catalog edits live
   (`ItemContainer.cs:41-42`), and that property is kept.
3. Neither entry's quarantined flag is set.
4. `a.Payload.Span.SequenceEqual(b.Payload.Span)`.

Rule 4 is the whole of "their properties are identical" (#884, item 4), and it is a `memcmp` rather than
a structural comparison only because the payload is canonical (3.2). Two items differing ONLY in a field
neither build understands do not merge, which is the conservative answer contracts 9.4 requires.

**A definition carrying durability or sockets is not stackable, and publish enforces it** (contracts
10.4). The container ALSO enforces it at runtime, because a definition can gain durability after the
items exist: an entry carrying kind 5 or kind 132 never merges, whatever the predicate says. Belt and
braces, for one span comparison.

**When two stackable entries merge, the SURVIVING instance id is the numerically lower of the two.**
Lower rather than the destination's, so the merge is commutative and a replay in the other order
produces the same id. The count saturates at `int.MaxValue` exactly as `Add` does today
(`ItemContainer.cs:106`), and the contracts 8.2 kind 4 stack cap applies on top.

**A merge DESTROYS an instance id, and that is the one place this design weakens the contracts'
traceability argument.** Contracts 9.5 wants a duplicated item traceable to the instance it was copied
from, and a merged stack has one id where there were two. The mitigation is an event rather than a
field: a merge emits `stack-merged` naming BOTH ids and the resulting count, so the destroyed id is
answerable from the journal for the retention window. That is cheap (about twenty bytes on an event
that was going to be written anyway) and it keeps the payload free of a growing list of dead ids, which
is open question 3.

### 4.7 The `SetAt` sanitising door, and its payload sibling

`SetAt(int slot, ItemStack stack)` keeps its exact current behaviour, which the doc calls "for the codec
and for nothing else" (`ItemContainer.cs:165-171`): a non-positive count or a zero id writes the empty
slot, so a malformed entry cannot smuggle a negative count in. **It gains one new behaviour: it CLEARS
the slot's payload and quarantined flag.** That is not an addition so much as the only correct reading
of what it already means, because writing a stack with no payload over a slot that had one must not
leave a stale payload behind, and a version 1 blob decode goes straight through this door.

`SetSlotAt(int slot, ItemSlot value)` is the payload carrying door. It sanitises in the same spirit and
enforces four invariants, throwing `ArgumentException` for each, because the only caller is a decoder
that has already validated and a violation here is a caller bug rather than bad data:

1. An empty stack writes `ItemSlot.Empty`, clearing the payload, the instance id and the flag.
2. `Payload.Length` is at most `MaxInstancePayloadBytes`.
3. A non-empty payload requires a non-zero instance id, and an empty payload with a non-zero instance id
   is allowed, because a definition may declare durability and have it at full with nothing else set.
4. A payload that is not canonical is refused. The check is the decoder's own, one forward pass over at
   most 512 bytes, and it runs on every call rather than under a `Debug.Assert`, because
   `[Conditional("DEBUG")]` members do not exist in the Release configuration CI builds (AGENTS.md) and
   a door that only guards on a developer machine is not a door.

**Invariants 2 and 4 are SKIPPED when `value.Quarantined` is set**, and the wrapper's own checks stand in
for them. A quarantine wrapper is not a payload: it is not canonical, it is not meant to be, and it may be
larger than `MaxInstancePayloadBytes` because the thing it preserves may have been (4.4). So a quarantined
slot is checked instead by `QuarantineWrapper.Verify`, which reads the four magic bytes, the version and
the declared `OriginalLength` and refuses anything else. Invariants 1 and 3 still bind, because an empty
stack and a missing instance id are caller bugs whatever the flag says. Without this exception the door
refuses the wrapper and the quarantine path is unreachable at exactly the moment it is needed, which is
the failure this paragraph exists to prevent.

`TakeSlotAt(int slot)` is `TakeAt`'s payload carrying sibling: it returns the whole `ItemSlot` and seats
`ItemSlot.Empty`. `Swap(a, b)` swaps the payload array entries alongside the stacks, so the existing one
line tuple swap becomes two.

## 5. Paged containers

### 5.1 Why, in one paragraph of arithmetic

A journal commit rewrites a changed projection section WHOLE and never patches it
(`JournalProjectionWrite.cs:10-29`, README line 114). Grimhollow's bank is ONE section
(`b-grimhollow.md:503-520`), 56 slots today. At the owner's 1,000 stacks of affixed items and 69 bytes
per entry (3.8), one bank section is about 69 KB, so every single deposit, withdraw, craft or reorder
would rewrite 69 KB. Twenty crafts in a row would write 1.38 MB to say that one item changed. Paging at
100 slots turns each of those writes into 6.9 KB, and section 6's batching turns twenty of them into
one. The two together are a factor of about two hundred, and they are the reason the owner put paged
containers and coalesced commits in scope in the same sentence (#882 comment 3).

### 5.2 Page geometry and section naming

**A page is 100 slots.** `ContainerPageSlots = 100`, a `public const int`.

One hundred rather than 128, deliberately. The power of two buys a shift instead of a divide, which a
compiler turns into a multiply either way, and it costs legibility everywhere a human reads a page
number: under 100, slot 743 is page 7 slot 43 and an operator reading a log line or a section name can
do the arithmetic in their head. Contracts 4.5's power-of-two rule binds CONTENT chunk sizes and says
nothing about container pages, so 100 is free. It is open question 2.

**Section names are `<container>/p<NN>`**, zero padded to two digits, with three or more digits used
unpadded above page 99. `bank/p00` through `bank/p09` for a 1,000 slot bank, `bag/p00` for a 30 slot
bag, `worn/p00` for an 11 slot worn set. `JournalProjectionWrite.sectionName` is an identifier capped at
128 characters over `[A-Za-z0-9._:/-]` (`JournalProjectionWrite.cs:13`, `JournalLimits.cs:86-98`), and
the slash is in that set, so nothing needs escaping. `ContainerSectionNames.Parse` is the one place the
name is taken apart, and it is what section 13 row 10 uses to check a page against the section it
arrived in.

### 5.3 The page header and its stamp

The page header is container codec version 2's header (4.4), and the field that matters here is
`ContentVersion`, the page stamp. Contracts 7.2 fixes it as the version NUMBER rather than the hash, for
a reason that is entirely about this section: a remap rule applies to a page whose stamp is OLDER than
the rule's version, older is a comparison, and a digest has no order. The comparison is one
`if (stamp < rule.IntroducedIn)` per rule per page.

`ItemContainerPage` holds the decoded slots, the stamp, a dirty flag and the page index. The dirty flag
is set by exactly two things: an operation that changed a slot, and a remap that changed an id (5.5).
Nothing else dirties a page, and in particular READING one never does.

### 5.4 Fitting inside the journal's caps

Four caps bind, and all four have headroom. Values from `JournalLimits.cs:9-23`.

| Cap | Value | What Scope B puts against it | Headroom |
|---|---|---|---|
| Projection sections per stream | 64 | 17 for Grimhollow at a 1,000 slot bank | 47 sections, about 4,700 more bank slots |
| Projection section bytes | 2 MiB | 6.9 KB typical, 52 KB worst case | 40 times, at the worst case |
| Aggregate projection bytes per stream | 8 MiB | about 70 KB for a full 1,000 stack bank of rares | 100 times |
| Projection writes per operation | 64 | 1 for a craft, 2 for a cross page move | 32 times, at a cross page move |

**The 64 section cap is the real ceiling on bank size, and here is the number.** Grimhollow has eight
sections today (`profile`, `skills`, `bag`, `worn`, `bank`, `quests`, `processing-preferences`,
`migrations`, `b-grimhollow.md:503-520`). Paging replaces `bank` with N pages and leaves `bag` and
`worn` at one page each, so the count is `7 + N`. At N = 10 that is 17, leaving 47. The maximum bank a
Grimhollow stream can hold is therefore `(64 - 7) * 100 = 5,700` slots, five and a half times what the
owner asked for. A game wanting more pages than that needs a second stream, which the journal already
supports (a commit may touch 16 streams, `JournalLimits.cs:9`), and this document does not build it
because nothing needs it.

**The worst case page is 52 KB and cannot reach the 2 MiB cap.** One hundred entries at the maximum
entry size (slot 1, flags 1, definition 3, count 3, instance 10, length 2, payload 512) is
`100 * 532 = 53,200` bytes plus a nine byte header. That is 2.5 percent of the section cap and 20
percent of the 256 KiB event payload cap, so section 13's row 8 is a row whose answer is "structurally
impossible, and here is the arithmetic".

**The quarantine exception is bounded too, and here is that arithmetic.** A quarantined entry's payload is
bounded by this section cap rather than by `MaxInstancePayloadBytes` (4.4), so the 52 KB above is the
ordinary case rather than the ceiling. The exceptional case is a page every one of whose entries
quarantines a payload written under a RAISED cap: at 1,024 bytes of original, eleven bytes of wrapper
header and twenty of entry overhead, that is `100 * 1,055 = 105,500` bytes, 5 percent of the section cap.
A page cannot approach 2 MiB without an entry whose original is itself megabyte-sized, which no writer at
any cap produces, so the exception widens the worst case by a factor of two and leaves it two orders of
magnitude clear.

### 5.5 Loading a container

`ContainerLoadResult Load(IReadOnlyList<JournalProjectionSection> sections, IContentSnapshot snapshot)`,
one pass, no store reads, no ambient state, following the one-validator shape contracts 10.4 sets for
the content side.

1. For each section whose name parses as this container's, decode the page (4.4). A page that fails to
   decode at the PAGE level (bad version, bad header, truncated, entries out of order) is a whole page
   failure and is quarantined as a unit, because a page that cannot be parsed has no entries to keep.
2. For each decoded page, apply the remap rule set (contracts 8.3): every rule whose `IntroducedIn` is
   strictly greater than the page stamp, in `Sequence` order, in one pass. A rule that changes nothing
   is a scan rather than a rewrite, which is almost every rule on almost every page. The pass visits
   every id the REGISTRY's reference targets name (3.3), never a list written in this document: the
   entry's own definition id, every id inside every registered field, and, through kind 132's
   `NestedPayload` slot, every id inside every socket's nested payload along with that socket's own
   `ContainedDefinitionId`. Nothing is skipped for being nested. A gem socketed into a sword before a
   re-base is rewritten by the same rule that rewrites the same gem lying in a bag slot, which is the
   property that stops an item surviving three publishes invisibly and then quarantining on the day a
   player unsockets it.
3. If any rule changed anything, mark the page DIRTY and set its in-memory stamp to the active version.
   Do NOT write it (5.6).
4. Validate each entry (12.2). A failed check quarantines that ENTRY and leaves the rest of the page
   alone, for a structural failure and for an unresolved content reference alike (12.3). The one check
   that does not is 12, the over-cap count, which contracts 8.2 kind 4 declares legal.
5. Return the pages, the accumulated findings and the dirty set.

**A nested rewrite recomputes two lengths above it, and that is why the pass RE-ENCODES rather than
patches bytes in place.** A `ReplacedBy` rule can change a varint's WIDTH: a nested payload carrying mod
91 is one byte shorter than the same payload carrying mod 4210. So after a rule changes anything the
re-encode recomputes, innermost first, the nested payload's own canonical bytes, then the socket entry's
`NestedLength`, then the kind 132 field's `Length`, then the entry's `PayloadLength` in the page (4.4).
It also restores CANONICAL ORDER, because a `ReplacedBy` can move an affix's mod id past its neighbour
and the list is ascending by mod id (3.4), and because a re-sort that did not happen would leave a page
whose items no longer stack with their own twins. Contracts 8.3 forbids a rule whose `ToId` is an earlier
rule's `FromId` for the same type, so one pass is enough and the result is idempotent, which is test 8.

**Remapped pages are rewritten LAZILY, on the next ordinary commit, and contracts 10.3 says why.**
Eagerly rewriting every touched page at boot would be a write storm proportional to the whole player
base arriving exactly when the server is coldest, against a recorded 698 commits per second on SQLite at
1,000 players (`a-engine.md:607-641`), which is an observed offered load rather than a measured ceiling
and is budget 13's starting point. The cost of lazy is that a page can sit remapped in memory across a session and
be lost on a crash, which simply means it is remapped again on the next load, and that is safe because
the rule set is idempotent (contracts 8.3).

**A page whose stamp is NEWER than the active version is not an error and is not quarantined.** Section
13 row 2 has the policy: run the ordinary validator, use the entries that resolve, quarantine the ones
that do not, and never lower the stamp, so the page is already correct when the newer version returns.

### 5.6 Which page an operation touches

The rule is the same for every operation: **the pages holding the slots it changed, and no others.**

| Operation | Pages | Projection writes |
|---|---|---|
| Deposit into the first free slot | the page holding that slot | 1 |
| Deposit that merges into an existing stack | the page holding that stack | 1 |
| Withdraw | the source page, plus the destination page if it is a different container | 1 or 2 |
| Craft in place | the page holding the target, plus the page holding the consumed currency | 1 or 2 |
| Move within one page | that page | 1 |
| Move across two pages of one container | both pages | 2 |
| Reorder a whole container | every page it touched | up to 64 |
| A remapped page, rewritten lazily | that page, folded into whatever commit comes next | 0 extra |

**A move across pages is ONE commit with TWO projection writes on the SAME stream.** That is already
legal: `JournalCommit` sorts projections by (stream, section), rejects a duplicate (stream, section)
pair, and requires every projection's stream be touched by the commit and carry at least one event
(`JournalCommit.cs:37`, `:125-138`). One stream, two sections, one event. Atomicity is the database
transaction's, so an item cannot be in both pages or neither.

**A page a remap dirtied rides the next commit for free.** The commit builder (6.4) asks the container
for its dirty set and includes those pages alongside the ones the operation changed. That is what makes
the lazy rewrite cost nothing: it never causes a commit, it only joins one.

### 5.7 Capacity is a gate, not a size

Ruinborne's model is the one to fit, and it is not a size at all (`c-ruinborne.md:894-905`): capacity is
per-character data on `player_character.bag_capacity`, it limits SLOTS rather than items, a grant that
merges entirely into existing stacks succeeds at or past capacity, and an over-capacity bag is never
trimmed and only refused new slots (`BagCapacityRules.cs:13-29`).

`PagedItemContainer` splits the two concepts that `ItemContainer` conflates:

- **Slot space** is the page geometry, fixed at construction, `PageCount * ContainerPageSlots`. It is
  the address space, and it never shrinks.
- **Capacity** is a separate mutable integer, the number of OCCUPIED slots a grant may leave behind. It
  is a gate consulted by `Add` and by nothing else.

The rules, which are Ruinborne's restated as engine behaviour:

1. A grant that opens a NEW slot is refused when occupancy is at or above capacity.
2. A grant that merges entirely into existing stacks is allowed at any occupancy.
3. Lowering capacity below current occupancy is LEGAL. The container loads intact, is never trimmed, and
   is refused new slots until occupancy falls below capacity. That is the shrink-only rule of 12.3, and
   it is the generalisation of contracts 8.2 kind 4's over-cap stack policy.
4. Capacity is never read from content. It is a per-owner number the game sets, because it is player
   progression rather than balance data.

**Ruinborne's explicit cell index with holes is expressible with no engine machinery at all.** A page's
entries are SPARSE and strictly ascending by slot (4.4), so a hole is the absence of an entry and costs
zero bytes, exactly as version 1's sparse form already did. Holes survive a load, a save and a remap
because nothing in the format or the loader compacts. The dense renumber Ruinborne's PostDeploy repair
explicitly refuses to do (`c-ruinborne.md:665-684`, "It NEVER closes a hole") is not something this
container can do by accident.

**Ruinborne's equipped-row-keeps-its-cell rule needs one game field and no engine change.** An equipped
item sits in the WORN container and reserves a cell in the BAG container, which is why Ruinborne has
deliberately no unique index on `(character_id, bag_order)` (`c-ruinborne.md:876-893`). The engine's
answer: the worn entry carries a game range property kind (>= 1024) holding the home bag slot, and the
bag container's occupancy counts its own occupied slots only, which is exactly `BagCapacityRules.UsedSlots`
counting non-equipped rows (`BagCapacityRules.cs:59-66`). The engine needs to know nothing about it.

## 6. Coalesced commits

### 6.1 What the journal actually does today

Five facts, all verified, and the design has to fit all five rather than four:

1. **One operation identity per commit.** `JournalCommit(identity, streams, projections, resultSchema,
   resultSchemaVersion, resultData, presentAtCommit, queueBehindAdmitted)` takes ONE
   `JournalOperationIdentity` (`JournalCommit.cs:14-22`), which is `(Guid operationId, string
   authenticatedScope, string actionKind, byte[] normalizedIntent)` (`JournalOperationIdentity.cs:9`).
2. **Replay is by operation id plus intent.** `ResolveOperationAsync(identity, ct)` answers `NotFound`,
   `Replayed` or `OperationConflict`, and a retained replay returns the ORIGINAL receipt and result
   (WorldStore README line 71, `a-engine.md:535-564`).
3. **The admitted queue is per stream and its default depth is 8.**
   `JournalAdmittedState.Refusal` answers `Backpressure` at
   `JournalExecutorOptions.StreamQueueDepth` and `VersionConflict` when the expected version is not the
   ADMITTED head (`JournalAdmittedState.cs:41-50`, `JournalExecutorOptions.cs:14`). An operation is sent
   to the store only when it is at the head of every one of its queues (`JournalAdmittedState.cs:82-88`).
4. **A projection write is a whole section replacement, never a patch** (`JournalProjectionWrite.cs:10-29`).
5. **`PresentAtCommit` changes nothing inside the executor.** The operation still queues, still reserves,
   still blocks what is behind it and still moves the admitted view. It tells the CONSUMER not to present
   early (`JournalCommit.cs:56-62`).

Nothing in the tree coalesces. A grep for `coalesc` across the WorldStore packages returns two SQL
`COALESCE(` calls and nothing else (`a-engine.md:1530-1546`). Grimhollow has none either: every mutation
is its own commit with its own operation id, and the single deferral in the whole repo is a health
checkpoint parking ONE mutation per account (`b-grimhollow.md:640-651`).

### 6.2 The three options

**A. A game-side intent batch.** The game applies N logical operations to an in-memory working copy and
emits ONE `JournalCommit` with one identity, N events and one projection write per touched page.

**B. An executor-side merge of queued operations into one commit carrying several identities.** The
executor, when an operation reaches the head of its queues, folds the operations behind it into the same
store call.

**C. A write-behind of dirty pages with a bounded window.** Events commit promptly, one per operation,
and the page PROJECTION is deferred and written by a later commit. On a crash the projection lags and
the loader replays the event tail over it.

| Criterion (1 to 10) | A: intent batch | B: executor merge | C: write-behind |
|---|---|---|---|
| One operation identity per commit is preserved | 10 | 3 | 10 |
| Idempotent replay by operation id is unchanged | 10 | 4 | 10 |
| `KhaozEngine.WorldStore` and both providers unchanged | 10 | 2 | 9 |
| The admitted view still presents on the tick that caused it | 9 | 8 | 6 |
| Write amplification actually removed | 7 | 9 | 10 |
| Crash exposure beyond the admitted window already accepted | 9 | 7 | 3 |
| The consumer load path is unchanged | 10 | 10 | 2 |
| Work to build | 8 | 3 | 4 |
| Total | 73 | 46 | 54 |

**Recommendation: A, with the batch window fixed at ONE SERVER TICK.**

### 6.3 Why B loses, precisely

B scores worst on the three rows that are not negotiable, and the reason is worth stating so the owner
can overrule it knowingly. Merging two identities into one commit means `journal_operation` holds more
than one row per commit, which means: `JournalCommit` takes a LIST of identities rather than one
(`JournalCommit.cs:14-22`), `OwnedByteCount` sums several intents (`JournalCommit.cs:108-114`), both
provider schemas gain a commit group column plus a migration (`JournalSchemaV1.sql`,
`SqliteJournalSchema.cs:20-112`, both of which carry `CurrentVersion = 2` and a NAMED required
migration today), `SqlServerJournalWriteBatch.BuildCommit`'s fixed statement order grows a loop
(`SqlServerJournalWriteBatch.cs:22-28`), `ResolveOperationAsync` resolves any identity in a group to the
group's receipt, the admitted layer's `AdmittedAfterVersions` bookkeeping becomes per identity
(`JournalAdmittedState.cs:60-80`), and the shared store conformance suite gains a multi-identity case
(`MutationJournalStoreConformance.cs`, 22 facts). That is a release of its own in the journal, and it
buys a saving option A already gets for free.

C loses on two rows that are structural rather than costly. It changes the consumer's LOAD path from
"read the projections" to "read the projections and replay the event tail over them", which needs a
reducer per event type on the load path in every consumer, and Grimhollow's load path today reads
projections only (`b-grimhollow.md:652-667`). And it opens a crash window in which durable events exist
that the projection does not reflect, which is a correct event-sourcing shape and a new failure mode for
every operator and every admin screen.

### 6.4 The recommendation, in full

`ContainerCommitBuilder`, in `KhaozEngine.ItemInstances.Journal`:

```csharp
var batch = ContainerCommitBuilder.Open(streamKey, actionKind, scope, containers);
batch.Apply(operation);          // repeated, against an in-memory working copy
JournalCommit commit = batch.Close(identityFactory);
```

What `Close` emits:

- **ONE `JournalOperationIdentity`, and what its `normalizedIntent` holds depends on whose identity it
  is.** For a SERVER-minted batch it is the canonical encoding of the ORDERED operation list:
  `[Count: varint][ per operation: [Kind: varint][Parameters] ]`, little endian, minimal varints, exactly
  the rules of contracts 15. For a CLIENT-headed batch (6.5) it is the client operation's OWN canonical
  intent ALONE, in 10.6's shape, and the server-caused operations riding behind it contribute NO intent
  bytes at all: they are carried by the events and by the page bytes. Canonical in both cases, because the
  intent is what the journal hashes to detect a conflicting replay (`JournalValidation.Hash` is
  `SHA256.HashData`, `JournalLimits.cs:135`), so two encodings of one batch must produce one byte
  sequence.
- **ONE `JournalEvent` per logical operation**, in order. The audit trail is not collapsed, only the
  projection is. Event sizes are per TYPE and each is written out where the type is: an `item-generated`
  event is about 72 bytes on a rare (9.5) and an `item-crafted` event is about 127, because it carries a
  before AND an after payload (10.6). At the cap of 128 events per operation (`JournalLimits.cs:10`) a
  batch is bounded at 128 operations and about 16 KB of events, which is 6 percent of the 256 KiB event
  payload cap (`JournalLimits.cs:14`).
- **ONE `JournalProjectionWrite` per touched page**, carrying the page's FINAL bytes, plus the pages the
  container's dirty set names from a lazy remap (5.6).
- **ONE result**, the resolved state the consumer presents.

**The batch window is one server tick, and it closes on the FIRST of:**

1. the tick boundary,
2. an operation that would touch a SECOND stream, because a cross stream operation is a different atomic
   unit and must not be widened by an unrelated batch,
3. an operation that sets `PresentAtCommit`, which is value moving between accounts and must not share
   an identity with anything else,
4. a CLIENT originated operation, see 6.5,
5. 128 events, 64 projection writes, or the 8 MiB aggregate commit cap (`JournalLimits.cs:10, 11, 19`).

A tick boundary rather than a timer, and that is the load bearing choice. A tick is a bounded, natural,
already-existing window in which everything either happened or did not, the admitted view moves on the
tick that produced the change exactly as it does today, and there is no new state living across a
boundary that an operator would have to reason about. A timer would buy more merging and would put
durable player property in a window whose length is a configuration value.

### 6.5 Which operations may share an identity

**A batch NEVER merges two CLIENT originated operations.** A client operation's id is supplied by the
client and is what it resubmits after a reconnect (`GrimhollowMutationPayloads.TryRead` reads a 16 byte
operation id off every mutation payload, `b-grimhollow.md:594-604`), and `ResolveOperationAsync` is keyed
on ONE id. Merging two client ids would leave one of them unresolvable, so the second click after a
reconnect would replay as a fresh action.

**Batching therefore applies to SERVER CAUSED work**, which is exactly the amplifying case the owner
named: a held craft, a processing run, a gathering run, a drop expiry sweep. Grimhollow's
`PlayerMutation.ServerCause` already mints its own durable cause id
(`GrimhollowPlayerJournal.Contracts.cs:263`), so the batch identity is a server minted id and needs no
client cooperation. A client operation commits on its own identity, and because the admitted layer
layers projection writes in admission order (`JournalAdmittedState.cs:75-79`), a click landing in the
middle of a held craft builds its page over the batch's admitted view and commits behind it.

**One client operation MAY be the head of a batch of the server caused work it directly causes.** A
withdraw that also triggers a quest advance is one identity, the client's, because the server work
happened because of the client's action and has no separate identity to lose.

**A client-headed batch's intent is the CLIENT's action and nothing else, and that is the load bearing
half of the exception.** The client resubmits after a reconnect with the intent it built from its own
click, and that intent cannot name the server work the click caused, because the client never saw it: the
quest advance, the expiry sweep, the achievement. If the batch's intent were the whole ordered list, the
resubmit would hash differently, `ResolveOperationAsync` would answer `OperationConflict`
(`InMemoryMutationJournalStore.cs:47`, `:57-60`), and the consumer would treat a COMMITTED withdraw as a
failed one: `Withdraw` rolls the admitted view back and supersedes everything queued behind it
transitively (`JournalAdmittedState.cs:117-147`), the player is told the action failed, and a re-click
applies it twice. Scoping the intent to the client's own operation makes the resubmit hash identically
and resolve `Replayed`, which is what 6.6 claims and what 6.5's exception is worth having.

**The server-caused half is not lost by being outside the intent.** An intent is a REPLAY KEY rather than
a record: what happened is the ordered `JournalEvent` list and the page bytes, both durable, both read by
an auditor. Nothing reads `normalizedIntent` except the fingerprint.

### 6.6 Crash and replay semantics of the recommendation

Stated case by case, because this is the part that is easy to get almost right.

- **Crash BEFORE admission.** Nothing happened. A client operation resubmits by its id, resolves
  `NotFound`, and the action is validated and applied fresh. Server caused work is simply lost, which is
  the trade the admitted-state design already took (`JOURNAL-ADMITTED-STATE-DESIGN-2026-09-13.md`,
  section 6).
- **Crash AFTER admission, BEFORE commit.** The whole batch dies with the process. The player's pages
  revert to their last committed bytes. No value is duplicated because nothing was written. **The
  instance ids the batch allocated are SKIPPED and never reissued**, because the allocator persisted its
  high-water mark before issuing (3.6), so a crafted item that was never committed leaves a gap in the
  id space and nothing else.
- **Crash AFTER commit, BEFORE the response reaches the client.** A client originated batch resubmits by
  its id and resolves `Replayed`, returning the ORIGINAL receipt and result, so the client sees the same
  outcome and nothing is applied twice. A server caused batch is durable and the next load reads the new
  page bytes.
- **Terminal store failure.** The admitted layer rolls every touched stream back to its committed
  baseline plus the operations still queued AHEAD of the failure, refuses everything queued BEHIND it
  transitively without sending any of it to the store, and attaches one `JournalCorrection` naming the
  streams, the projection SECTION KEYS to resync and the superseded operation ids
  (`JournalAdmittedState.cs:117-147`). Because a batch writes whole pages, the section keys in that
  correction are exactly the pages the consumer must resync, so the correction's existing shape is the
  page resync list with no addition.
- **Transient retry.** Corrects nothing. The batch is still admitted, its view still stands, and the
  store is still being asked. That is the journal's stated fail-closed behaviour and it is unchanged.
- **Replay with a DIFFERENT intent under the same id.** `OperationConflict`, unchanged, and 6.4's split
  is what "different" means. A CLIENT operation resubmitted with different parameters, a different slot, a
  different currency or a different target instance id, hashes differently and is refused. The
  server-caused operations behind it are NOT in the hash, so a resubmit that omits them, or a second
  attempt whose server work differs because the world moved, still resolves `Replayed` and returns the
  original receipt. A SERVER-minted batch carries the whole ordered list and has no resubmitter at all,
  because its id is minted per attempt (`GrimhollowPlayerJournal.Contracts.cs:263`), so a conflicting
  replay of one is a bug rather than a reconnect.

### 6.7 What it is worth

The arithmetic that justifies the section, using 3.8's 69 byte entry and 5.4's 6.9 KB page.

| Workload | Today, one section per container | Paged, one commit each | Paged and batched |
|---|---|---|---|
| One craft on a 1,000 stack bank | 69 KB | 6.9 KB | 6.9 KB |
| Twenty crafts in a held action | 1,380 KB | 138 KB | **9.4 KB** |
| Twenty crafts, as journal events | 20 events | 20 events | 20 events |
| Commits issued | 20 | 20 | **1** |

Twenty crafts go from 1,380 KB and twenty commits to 9.4 KB and one, a factor of about 146 on bytes and
20 on commits. The 9.4 KB is one 6.9 KB page plus twenty `item-crafted` events at about 127 bytes each
(10.6), which is the before-and-after event rather than the 40 byte figure an earlier draft used and the
83 byte figure it used elsewhere. Section 16 turns these into measured budgets, and budget 4 carries the
same arithmetic.

## 7. Ground items and the network

### 7.1 What exists

`TileGroundItem` is five raw ints, `ItemId`, `Count`, `X`, `Z`, `Plane`, whose remark is explicit that it
is "two meaning-free integers rather than a dependency on `KhaozEngine.Items`"
(`TileGroundItem.cs:7-12`). Its codec writes twenty bytes with no length prefix and nothing declared,
and its reader CLAMPS a non-positive count rather than rejecting, on the stated grounds that every bit
pattern is a meaningful id to some world (`TileProtocol.Components.cs:321-344`). It is registered as
extension type id `FirstExtensionTypeId + 5` (`TileProtocol.Components.cs:33`, `:122`).

`SpawnGroundItem(TileCoord at, int itemId, int count, long ttlTicks)` throws on a caller bug, answers 0
on a full cell, allocates a net id, seats the component and marks the entity
`Transient { Scope = TransientScope.DurableOnly }` (`TileWorldServer.GroundItems.cs:62-98`).
`DespawnGroundItem` is idempotent by answer, which is the engine's existing anti-duplication rule for
pickup (`TileWorldServer.GroundItems.cs:113-118`).

The game message cap is `TileProtocol.MaxGameMessageBytes = 1024`, the encoder THROWS above it and the
decoder REFUSES (`TileProtocol.Frames.cs:65`, `:173-174`, `:211`). There is no fragmentation or
reassembly anywhere in the tile netcode, verified by grep (`a-engine.md:818-838`). The single precedent
for a larger logical payload is hand-rolled application-level chunking on the reliable ordered channel,
done once for combat events (`TileWorldServer.Tick.cs:241-259`).

### 7.2 Where the instance rides: a sibling component

| Criterion (1 to 10) | Widen `TileGroundItem` | Sibling `TileGroundItemInstance` |
|---|---|---|
| Wire cost for a plain drop, which is almost every drop | 4 | 10 |
| An already-shipped client keeps decoding | 1 | 10 |
| `TryGetGroundItem`'s signature and meaning survive | 3 | 9 |
| The codec's no-length-prefix property survives | 2 | 8 |
| One place a reader looks for a drop | 8 | 6 |
| A spawn with no instance allocates nothing | 8 | 10 |
| Total | 26 | 53 |

**Recommendation: a sibling, `TileGroundItemInstance`.**

The deciding row is the second and it is not a preference. `WriteGroundItem` writes exactly twenty bytes
with no declared length, so a reader built against today's protocol consumes twenty bytes and then reads
the next component's type id. Adding a field to that component makes every already-shipped client
misparse the rest of the entity. A sibling is a NEW extension type id, and `SnapshotWriter` length
prefixes extension components precisely so an older client can skip an id it never registered
(`SnapshotWriter.cs:11-13`), so an old client skips it for free and a new one reads it.

```csharp
public struct TileGroundItemInstance : IComponent
{
    public long InstanceId;     // 0 is never seated: a drop with no instance carries no component
    public byte[] Payload;      // the PUBLIC view, see 7.4. Never mutated in place
}
```

Registered at `FirstExtensionTypeId + 8` on the default channels, with a write delegate that writes the
id as an unsigned varint and the payload length prefixed, and a read delegate that is TOTAL: a declared
length longer than the frame, or longer than `MaxInstancePayloadBytes`, answers a zero length payload
rather than throwing, following the file's own rule that every frame decoder is total because the bytes
come from a remote peer (`TileProtocol.Frames.cs:27-34`).

**The component carries no dependency on `KhaozEngine.ItemInstances`.** It holds an opaque `long` and
opaque bytes, exactly as `TileGroundItem` holds an opaque `int`. The tile netcode still does not know
what an item is.

### 7.3 Spawning, the durable event, and the claim

**`SpawnGroundItem` gains ONE overload** and the existing one delegates to it with instance id 0 and an
empty payload, so no existing call site changes:

```csharp
public long SpawnGroundItem(TileCoord at, int itemId, int count, long ttlTicks,
                            long instanceId, ReadOnlySpan<byte> payload);
```

It throws on a payload longer than `MaxInstancePayloadBytes`, and on a non-empty payload with instance
id 0, because both are caller bugs in the same class as the existing non-positive count throw
(`TileWorldServer.GroundItems.cs:64-69`). The engine does not decode the payload: the bytes came from
the server's own container, and the server is the only thing that ever writes them (15.3).

**The durable half is the region ground stream**, which Grimhollow's item-drop design lands FIRST (gate
0 decision 12). That design puts a drop on `grimhollow/loot/hollowmere/ground/{rx}_{rz}` as one atomic
commit across the player's `bag-replaced` and one `loot-created` per ground stack, with claims and
expiry on the paths goblin loot already uses (`ITEM-DROP-DESIGN-2026-09-14.md` section 5). Contracts 6.6
binds the instance to that event. The `loot-created` payload therefore gains four fields:

```
[InstanceId: varint uint64]
[PayloadLength: varint int32]
[Payload: PayloadLength bytes]        // the FULL payload, not the public view
[ContentVersion: varint int32]        // the stamp, see below
```

**The FULL payload is what is durable** (contracts 6.6: "What is durable is the whole payload"). Only
the replicated component carries the filtered view. **And the event carries a content version stamp**,
which the contract does not say in as many words and which follows from 7.2 of it: a ground stack can
sit on a region stream across a server restart and a content publish, so it is a durable record carrying
content ids and it stamps the version it was last brought up to date with. Without the stamp a claim
after a publish would have no way to know which remap rules to apply to the payload it is about to move
into a page.

**A claim MOVES the same instance id** (contracts 6.6). The `loot-claim` operation is one commit across
the loot source stream and the player stream, and the page write seats the SAME instance id and the same
payload bytes into the claimant's container. It does not mint a new id, and the claimant is not special
cased, so a dropper reclaiming their own drop takes the same path a stranger does. Identity therefore
survives a drop and a pickup, which is what stops a drop-and-claim cycle from laundering an item.

**A contested claim sets `PresentAtCommit`.** The journal design names a contested loot claim as one of
the two cases for it (`JournalCommit.cs:56-62`), and Grimhollow sets it nowhere today
(`b-grimhollow.md:618-639`). Section 18 carries that as an adoption item.

### 7.4 Per-viewer visibility

Contracts 11.2 sets the RULE and leaves the mechanism to this spec: a viewer receives a field if and only
if its kind's visibility is at or below the viewer's level for that item, tooltips use the SAME function,
and nothing is ever `ServerOnly` on the wire. The engine has no mechanism at all today: every filter is
per ENTITY, a `HashSet<long>` of net ids handed to `SnapshotWriter.WriteFiltered`
(`SnapshotWriter.cs:43`), and the component write delegates registered at `TileProtocol.Components.cs:122`
take `(value, BinaryWriter)` with NO viewer argument.

There is one existing per-recipient mechanism and it does not fit. `ReplicationChannels.OwnerOnly` scopes
a component to the client whose OWN net id equals the ENTITY's net id
(`ReplicationChannels.cs:49-53`, `ReplicationRegistry.cs:187-195`). A ground drop's entity is the drop,
whose net id is never a viewer's, so registering the sibling component `OwnerOnly` would hide it from
everyone including the person who dropped it. It works for a component on a PLAYER's own entity and for
nothing else.

| Criterion (1 to 10) | a: viewer-aware write delegate | b: per-viewer projection before the writer | c: public component plus a targeted owner message |
|---|---|---|---|
| `ServerReplicator` keeps its ONE shared capture across viewers | 2 | 4 | 10 |
| Engine API change size | 4 | 5 | 9 |
| One rule and one function, per contracts 11.2 | 9 | 9 | 8 |
| Cost per viewer per tick | 4 | 3 | 9 |
| Works for a ground drop, where the entity is not the viewer | 9 | 9 | 9 |
| Works for an equipped item on another player | 9 | 9 | 8 |
| Client complexity | 9 | 9 | 6 |
| Total | 46 | 48 | 59 |

**Recommendation: c.** The replicated component carries the PUBLIC VIEW of the payload, computed once
when the item changes rather than once per viewer, and the owner-only remainder rides a targeted game
message to the one viewer entitled to it.

The deciding row is the first. `ServerReplicator` and `AoiDeltaReplicator` build ONE capture of the world
and owner-scope it per client afterwards (`ServerReplicator.cs:155`, `AoiDeltaReplicator.cs:119`), and a
viewer-aware write delegate would make the capture unshareable, turning the delta path's cost from
O(world) plus O(viewers times changed) into O(viewers times world). Paying that on every tick of every
server to avoid a targeted message for a case that arises when an owner looks at their own item is the
wrong trade.

**The tile serve is FULL STATE today, and this section prices that rather than assuming a delta.** Step 5
of the tick builds `SnapshotWriter.WriteFiltered(world, registry, interest,
ReplicationChannels.Replicate, netId)` and sends it inside the per-slot loop, every tick, for every slot
(`TileWorldServer.Tick.cs:204-221`). `AoiDeltaReplicator` lives in `KhaozEngine.Replication` and
`KhaozEngine.TileWorld.Netcode` does not use it. So a sibling component's payload is retransmitted WHOLE,
per viewer, per tick, for as long as the drop lies there, and computing the public view once per item
rather than once per viewer saves the COMPUTE and not the BYTES. Budget 11 of section 16 measures it, at
the 250 ms tile tick rather than at a 10 Hz one. Moving the sibling component onto an AoI delta path is
the obvious next optimisation and it is not v1: v1 ships the full-state cost with the budget that says
what it is.

The second row matters too. Option c needs no engine API change at all: the targeted message is exactly
the shape `SendCombatTo(slot, interest)` already is, a second non-snapshot per-viewer filtered send driven
off the same interest set (`TileWorldServer.Tick.cs:248-259`), and `PickupState.OwnerNetId` is the
existing precedent for the engine owning the TAG and not the RULE.

**`ItemInstanceVisibility.PublicView(payload, kinds)` is the one function**, and it is a forward pass that
copies the RETAINED RUNS of the input. Because fields are already ascending and each is length prefixed,
a filtered payload is a sequence of memcpy calls over contiguous ranges with no decode, no re-sort and no
allocation beyond the output. That is why 3.3 declines contracts 11.2's optional coupling of kind ids to
visibility: the coupling would buy a single memcpy instead of two or three, forever, in exchange for
constraining every future kind assignment.

**A GROUND item has no owner viewer in v1, and that is a rule rather than an omission.** A drop's entity
is the drop, whose net id is nobody's, and the durable claim has not happened yet, so there is no viewer
this document can call the owner of a ground stack. So a ground item's PUBLIC view is its WHOLE
replicated payload: the sibling component carries `PublicView(payload, Everyone)` and there is no owner
remainder message for a drop, only for an item in a container the viewer owns. The consequence to be
explicit about is the leak that does not happen: kind 6 `BoundTo` is `OwnerOnly` (3.3), so it is stripped
before the component is written and a passer-by cannot read who a dropped item is bound to, which is a
fact about a PLAYER rather than about an item. Kinds 4 and 5, charges and durability, are stripped for
the same reason, so a rare's 58 byte payload replicates as 54 on the ground (budget 11). A game that wants
a dropper-only view builds it on the claim's own ownership tag (`PickupState.OwnerNetId`, 7.3) and a
targeted message, exactly as the owner remainder does, and the engine ships neither in v1.

The same function answers the tooltip. A tooltip builder that computed its own answer is how a client
eventually renders something the server never sent, so `CanSee` (12.5) is called by the replication
filter and by the tooltip builder and by nothing else, and 17.9 is the test that proves the two agree.
**The tooltip's TEXT resolves through `ContentStringCatalog`**, Scope A's layered `IStringCatalog` of
contracts 12.4, formatted on the `SafeFormat` path of contracts 12.3: content text chunks first, then the
game's own resx, then the key itself, with a malformed translator template falling back to the unformatted
template rather than throwing inside the frame loop. A tooltip built against the game's resx directly
would miss every content-shipped string, which is the whole reason that ordering exists.

### 7.5 Page sync over the wire

A 100 slot page of rares is about 6.9 KB (3.8), which is 6.7 times the 1,024 byte game message cap. Sizing
pages to fit one frame is not an option and the arithmetic says why: at 69 bytes an entry, a frame holds
fourteen entries, so a 1,000 slot bank would need 72 pages and blow the 64 section per stream cap (5.4).
So the page must fragment.

| Criterion (1 to 10) | 1: fragmented reliable stream | 3: per-slot deltas | 4: an HTTP side channel |
|---|---|---|---|
| Cold open of a 1,000 stack bank | 8 | 3 | 9 |
| Steady state after one craft | 4 | 10 | 2 |
| Resync after a `JournalCorrection` | 9 | 4 | 7 |
| Rides the existing reliable ordered channel | 9 | 10 | 1 |
| New engine machinery | 5 | 7 | 2 |
| Client complexity | 6 | 5 | 3 |
| Total | 41 | 39 | 24 |

**Recommendation: 1 and 3 together, because they are not alternatives.** The fragmenter is the FLOOR: a
cold open and a correction resync both need to send a whole page, and a delta cannot express "this page is
now these bytes". The delta is the OPTIMISATION that makes the steady state one frame: after a craft, the
client needs one slot, not 6.9 KB. Building only the fragmenter would make every craft a seven frame burst.
Building only the delta would leave no way to open a bank.

Option 4 is scored and dismissed for one reason beyond its total: it is a second transport with a second
auth story for data the reliable ordered channel already carries correctly, and Scope A needs an HTTP pack
fetch anyway (#882 comment 1), so the temptation to reuse it here is exactly the kind of coupling that
makes an outage in one take out the other.

**`TileFragmentedMessage`, in `KhaozEngine.TileWorld.Netcode`, is item-agnostic.** It fragments any
`ReadOnlySpan<byte>` into game messages and reassembles them, and it knows nothing about items. That is
both correct layering and the right shape for the engine, which has no fragmentation layer at all today
and will want one again.

```
[StreamId: byte]        // which logical stream, the game assigns these
[Sequence: uint16 LE]   // increments per transmission of that stream, wraps
[ChunkIndex: byte]
[ChunkCount: byte]      // 1 to 255
[Bytes: the rest]
```

Five bytes of header. The envelope is `[tag:1][kind:ushort 2][flags:1]`
(`TileProtocol.Frames.cs:77`), so a chunk carries `1024 - 4 - 5 = 1015` payload bytes and 255 chunks
carry 258 KB, which is forty times the largest page and five times the worst case page of 5.4.

Reassembly rules, all on the client:

1. The channel is `ReliableOrdered`, so a chunk cannot arrive out of order or be lost without the
   connection failing. The reassembler therefore checks for CONSISTENCY rather than reordering: a chunk
   whose `Sequence` differs from the assembly in progress DISCARDS that assembly and starts a new one,
   which is what a server restarting a page mid-transmission looks like.
2. At most four partial assemblies are held at once. A fifth evicts the oldest and increments a counter.
   That is a bounded-memory rule rather than a timer, because a timer on a reliable ordered channel
   measures nothing.
3. On the last chunk, the assembled bytes go through the SAME decoder the server encoded with, and a
   failure quarantines rather than throwing (12.4). A client that trusted its own reassembly would draw a
   bank from bytes nothing validated.
4. A partial assembly still open when the connection drops is discarded with the connection.

**The delta message** is one frame and names slots rather than pages:

```
[ContainerId: byte][PageIndex: byte][ChangedCount: byte]
[ per change: [Slot: varint uint16] then either
              [0x00] for "now empty"
              or [0x01] then the entry body of 4.4 without its Slot field ]
```

A single crafted rare changes one slot, so a craft costs `3 + 1 + 1 + 68 = 73` bytes and one frame. A
delta names the page it applies to, and the client REFUSES a delta for a page it has not fully received,
asking for a full page sync instead, so a delta can never be applied to bytes the client guessed at.

**A delta that would not fit ONE frame is never sent, and the page goes through the fragmenter instead.**
Nothing above bounds `ChangedCount`, and a page holds 100 slots, so a Sort, a multi-slot move or a deposit
that cascades across a page produces a delta far over the cap. That is not a truncated message, it is a
THROW: `EncodeGameMessage` throws above `MaxGameMessageBytes` (`TileProtocol.Frames.cs:173-174`), the
delta is sent with `SendGameMessageTo` from inside the per-viewer serve loop, and nothing in
`TileWorld.Netcode` catches around it. The combat path already paid for this exact shape and its comment
says so: the throw was inside the loop and "took the tick down for every player on the server"
(`TileWorldServer.Tick.cs:236-247`). So the builder MEASURES as it writes:

1. The budget is `MaxGameMessageBytes` less the four byte envelope (`TileProtocol.Frames.cs:77`) less the
   delta's own three byte header, so 1,017 bytes of changes.
2. A change to an OCCUPIED slot costs the slot varint plus the `0x01` tag plus the entry body, which is 70
   bytes at 3.8's rare. FOURTEEN changed rare slots fit in one frame and the fifteenth does not.
3. A change to an EMPTIED slot costs two or three bytes, so bytes are not what binds there. `ChangedCount`
   is a byte, so 255 is the format ceiling and a 100 slot page is under it either way.
4. When the next change would not fit, the builder ABANDONS the delta and sends the whole page through
   `TileFragmentedMessage`. Not a second delta frame: two deltas for one page would have to be applied in
   order by a client that may have missed the first, which is the reassembly problem the fragmenter
   already solves once.

A Sort over a 100 slot bank page of rares is therefore ONE fragmented page send of about 6.9 KB in seven
chunks, never a 6,800 byte game message and never a throw. Test 17 pins it, at fourteen changes, at
fifteen, and at a full page.

### 7.6 The ground item message vocabulary

Only one new client-to-server message is needed and it carries no bytes about an item's properties: the
take request already names the drop by net id and its durable source id
(`b-grimhollow.md:975-990`). That is 15.3's invariant in practice: **no client-to-server message in this
design carries an instance payload, and every one of them names an item by id.**

The full vocabulary, so an implementer can count the work. Kinds are the game's to assign, because
`TileProtocol` reserves the `ushort` kind space to the game and the engine only caps the frame
(`TileProtocol.Frames.cs:65`).

| Direction | Message | Payload | New |
|---|---|---|---|
| server to client | page chunk | `TileFragmentedMessage` header plus a slice of an encoded page | YES |
| server to client | page delta | the 7.5 delta, one frame | YES |
| server to client | owner remainder | the owner-only fields of one item, targeted at one viewer | YES |
| server to client | ground item component | the sibling component inside the ordinary snapshot | YES |
| client to server | page resync request | `[ContainerId: byte][PageIndex: byte]`, two bytes | YES |
| client to server | take a drop | unchanged, names the drop by net id and source id | no |
| client to server | move, deposit, withdraw, craft | unchanged shape, names slots and an operation id | no |

**The page resync request is the only new client-to-server message and it carries two bytes.** It is
what rule 3 of 7.5 leans on: a client that cannot apply a delta asks for the page rather than guessing,
and a client that receives a `JournalCorrection`'s section key list asks for each named page. The server
rate limits it at one page per client per tick, which bounds the worst case a malicious client can ask
for at one page of fragments per tick, the same shape the snapshot already costs.

**A ground item's payload is capped per ITEM and not by the frame, and an earlier draft had that
backwards.** A snapshot frame is NOT subject to the 1,024 byte game message cap: `EncodeSnapshotFrame`
allocates `SnapshotHeader + snapshot.Length` with no cap test (`TileProtocol.Frames.cs:112-125`),
`TryDecodeSnapshotFrame` has no cap either (`:132-152`), and the only throw in that file is in
`EncodeGameMessage` (`:173-174`), which a snapshot never goes through. Snapshots already exceed 1,024
bytes for an ordinary interest set today. So "twenty ground rares overflow a frame" was never a failure
mode, and correcting it exposes the one it was hiding: by 7.4 the tile serve is FULL STATE, so every
ground payload in an interest set is re-sent to every viewer on every tick, for as long as the drop lies
there. That is a bandwidth cost rather than a throw, it is budget 11 in section 16, and section 13 row 11
carries it with the detection that actually applies. `TileWorldServer`'s cell occupancy limit
(`SpawnGroundItem` answers 0 on a full cell, `TileWorldServer.GroundItems.cs:62-98`) is where a game tunes
the COUNT today, and open question 6 asks the owner whether the engine should cap ground payload BYTES per
cell as well.

## 8. Affix content types

### 8.1 What registers, and where

Eighteen content types register in the Scope B range of contracts 4.3 (`256` to `1023`), through
`RegisterContentType` exactly as contracts 4.2 shapes it, at process start and before any pack loads.
Each carries a row codec, a validator, a field schema (contracts 4.7), a default content visibility and
a chunk slot count.

**Seven of them are the types an author thinks in, and eleven are their CHILDREN.** Contracts 4.7 has
eight value kinds and none of them is a list, and Scope A's registration rule is that "any repeating child
structure in engine or game content is its own content type with a key reference to its parent, never a
blob field". An earlier draft of this section put a mod's tiers in an `opaque bytes` field and gave five
other types list-valued fields that no value kind names. Both are corrected here, the same way Scope A
split `loot_entry` off its table, and appendix A records it under C2.

| Type id | Type key | What a row is | Parent | Default visibility | Chunk slots |
|---|---|---|---|---|---|
| 256 | `mod` | one affix: its kind, its group and its display line | | `Client` | 4,096 |
| 257 | `mod_group` | an exclusivity group and how many of it one item may carry | | `Client` | 256 |
| 258 | `rarity_rule` | how many affixes a rarity permits, and its composed name template | | `Client` | 256 |
| 259 | `unique_template` | a fixed item built on a base | | `Client` | 4,096 |
| 260 | `socket_type` | what a socket accepts | | `Client` | 256 |
| 261 | `crafting_currency` | a named sequence of steps with guards | | `Client` | 4,096 |
| 262 | `rare_name_word` | one word in a rare-name position | | `Client` | 4,096 |
| 263 | `mod_tier` | one tier of one mod, with its item level gate | `mod` | `Client` | 16,384 |
| 264 | `mod_tier_weight` | one tier's spawn weight against one tag | `mod_tier` | `ServerOnly` | 65,536 |
| 265 | `stat_line` | one stat a tier grants, with its range | `mod_tier` | `Client` | 32,768 |
| 266 | `rarity_weight` | one rarity's weight against one tag | `rarity_rule` | `ServerOnly` | 1,024 |
| 267 | `rarity_kind_limit` | how many of one mod kind above 2 a rarity permits | `rarity_rule` | `Client` | 1,024 |
| 268 | `unique_line` | one mod, at one tier, that a unique template grants | `unique_template` | `Client` | 16,384 |
| 269 | `unique_socket` | one socket a unique template forces, in authored order | `unique_template` | `Client` | 4,096 |
| 270 | `socket_tag_rule` | one accept or reject tag of a socket type | `socket_type` | `Client` | 1,024 |
| 271 | `currency_step` | one step: a primitive or a game operation, with its parameters | `crafting_currency` | `Client` | 16,384 |
| 272 | `currency_guard` | one guard, on a currency's target or on one of its steps | `crafting_currency` | `Client` | 16,384 |
| 273 | `rare_name_word_weight` | one word's weight against one tag | `rare_name_word` | `ServerOnly` | 16,384 |

Eighteen of the 768 ids in the Scope B range, leaving 750. The ids are assigned in one block and never
reused, per contracts 5.1.

**A child row carries `id`, `key`, a key reference to its parent, and a `sort` where its ORDER matters.**
That shape is uniform across all eleven, so an editor, an audit row and a publish diff read the same way
for every one of them. `sort` is authored, is unique within one parent, and carries no meaning beyond
order. A child type whose rows have no order (a weight, a kind limit) has no `sort`.

**Three of the eighteen are `ServerOnly` as WHOLE TYPES, and that is the point of the split.** A weight
tells a client the exact odds of every outcome, which is farmable rather than displayable, so it is
omitted from the client chunk under contracts 11.3. In the earlier draft a weight was a sub-field of an
opaque blob whose other bytes were `Client`, and Scope A's client-chunk builder omits whole FIELDS, so it
had no way to strip the weights out of the middle of one. Three whole `ServerOnly` types make that a
publish-time property again: a client never downloads a `mod_tier_weight`, a `rarity_weight` or a
`rare_name_word_weight` row at all. Everything else about a tier is client visible, so a tooltip that says
"this tier rolls 10 to 40 at item level 60 and above" is computed from the same bytes on both sides
(contracts 13.4).

**The one multi-valued field left in any Scope B schema is `stat_line.tag_scope`, and it is the
contracts' own `tag list` value kind** (4.7), not an ad hoc list: it renders in a generic editor, it diffs
element by element in the publish diff, and it has a declared reference target. A socket type's accept and
reject tags could have been two `tag list` fields for the same reason, and are a child type instead,
because accept-versus-reject is a property of the PAIR and two fields would put it in a field NAME.

**All seven parent types are `Client` by default and that is deliberate.** A client computes tooltips from
this data and must agree with the server to the unit (contracts 13.4), so hiding a mod's ranges would make
the tooltip a second implementation. What is `ServerOnly` is the LOOT TABLE that decides which base drops,
which is Scope A's engine-range type and not one of these, plus the three weight types above.

**Nothing in the engine is PoE-specific, and this section is the place that claim has to be earned.**
Every row above is a SHAPE. There is no mod named in engine code, no currency named in engine code, no
rarity named in engine code and no tier count fixed in engine code. The engine ships the eighteen types,
their codecs and their validators, and the owner authors every row. A game that wants Tibia's model
authors three rarity rules with zero affixes each and never writes a mod row, and nothing about that is
a degraded mode.

### 8.2 `mod`, the central type

| Field | Value kind | Reference target | Visibility | Required |
|---|---|---|---|---|
| `id` | int | | `Client` | yes |
| `key` | string key | | `Client` | yes |
| `kind` | int | | `Client` | yes |
| `group_id` | key reference | `mod_group` | `Client` | no |
| `legacy` | bool | | `Client` | yes |
| `line` | localized text key | | `Client` | yes |

**Field names are snake_case on every Scope B type**, because contracts 12.1 derives a localization key
mechanically as `<type key>.<content key>.<field>` and contracts 5.3 binds it to `a-z`, `0-9` and `_`,
lower case only. A field named `DisplayFormatKey` produces `mod.fine_crafted.DisplayFormatKey`, which the
validator refuses. The contracts' own worked example names this field `line`, so this table does too, and
the derived key is `mod.fine_crafted.line`.

**`line` stores NOTHING.** The `localized text key` value kind is a MARKER (contracts 4.7): its presence
in the schema declares that the key derived from the row exists in the text chunks, and the row carries no
string. So a mod's displayed line is looked up, never stored, and a translator adding a language adds a
text chunk rather than a content edit.

`kind` is an integer the engine assigns exactly two meanings to, `1` prefix and `2` suffix, with `3` to
`255` free for the game. The engine needs the prefix and suffix split because contracts 9.9 put the
distinction on the ROW rather than in the affix entry (3.4), so the generator reads it to count against
the rarity rule's two limits. Everything above 2 is a kind the generator treats as its own counted pool,
which is how an implicit, a corruption line or a Mortal Online material line gets a slot without an
engine change.

`group_id` is contracts' exclusivity, one level of indirection rather than a raw integer, so the group
can carry a count. `mod_group` is three fields: `id`, `key` and `max_per_item` (an int, default 1). Two
mods sharing a group with `max_per_item = 1` cannot both appear, which is the ordinary exclusivity case,
and a group with `max_per_item = 2` is a family an item may carry twice. A mod with no group is
unconstrained beyond the rarity rule's counts.

`legacy` is contracts 5.4's flag, read by exactly three places: the generator skips a legacy row when
building its candidate tables (9.2), the crafting guard refuses one (10.3), and the frozen-entry rule of
10.3 refuses to rewrite an affix already sitting on a legacy row. Everywhere else a legacy mod resolves
through the ordinary path, which is the whole point of not giving it its own id range.

**A mod row carries NO tiers, no lines and no weights.** They are three child types, and they are what
the next two sections are.

### 8.3 Tiers, and why the ordinal is the payload's key

A mod's tiers are `mod_tier` rows pointing back at the mod. The payload stores `(mod id, tier ordinal)`
(3.4), NOT a `mod_tier` id, and that is the deliberate narrowing this section exists to explain: a payload
naming a `mod_tier` id would need its own remap rule for every retired tier, and a retire of one tier
would become a rule kind of its own. The ordinal is the key the bytes carry, the row is where the tier's
data lives, and the validator refuses to let the two drift.

| `mod_tier` field | Value kind | Reference target | Visibility | Required |
|---|---|---|---|---|
| `id` | int | | `Client` | yes |
| `key` | string key | | `Client` | yes |
| `mod_id` | key reference | `mod` | `Client` | yes |
| `ordinal` | int | | `Client` | yes |
| `item_level_min` | int | | `Client` | yes |
| `item_level_max` | int | | `Client` | yes |

`ordinal` is 1 to 255, unique within the mod, and IMMUTABLE once published. `item_level_min` and
`item_level_max` are inclusive, 1 to 65535, with `item_level_max = 65535` meaning no ceiling.

**The engine assigns tier ordinals no ordering meaning.** Whether ordinal 1 is the best or the worst is
the author's convention. What gates a tier is its own `item_level_min` and `item_level_max`.

**A reorder is refused at publish and it is now a CHANGE-SHAPED check.** Contracts 10.4 takes the previous
published snapshot as an argument, so the check "a mod's tier ordinals are unchanged since the last
published version" has an input to compare against, runs at publish and does not run at boot or in a test
where `previous` is null. Before that argument existed this check had nowhere to read from.

| `mod_tier_weight` field | Value kind | Reference target | Visibility | Required |
|---|---|---|---|---|
| `id` | int | | `ServerOnly` | yes |
| `key` | string key | | `ServerOnly` | yes |
| `mod_tier_id` | key reference | `mod_tier` | `ServerOnly` | yes |
| `tag_id` | key reference | `tag` | `ServerOnly` | yes |
| `weight` | int | | `ServerOnly` | yes |

**A weight is keyed by TAG, never by base id**, and a base's weight for a tier is the weight of the FIRST
tag in the base's authored tag list that has a `mod_tier_weight` row for that tier, or zero when none
does. First rather than sum, because the base's tag order is authored information (contracts 4.6) and a
sum would make the order meaningless. A tier with NO weight rows can never spawn, which is legal and is
how a tier reachable only through crafting is authored.

**The whole type is `ServerOnly`, so a client downloads no weight row at all.** That is the property 8.1
names: visibility is a whole-field and now a whole-TYPE fact, and the client chunk builder omits it by
omitting rows rather than by reaching into bytes.

### 8.4 Stat lines and ranges

A tier carries one or more `stat_line` rows, and a line is what turns a stored `ushort` position into a
number the evaluator folds (11.6).

| `stat_line` field | Value kind | Reference target | Visibility | Required |
|---|---|---|---|---|
| `id` | int | | `Client` | yes |
| `key` | string key | | `Client` | yes |
| `mod_tier_id` | key reference | `mod_tier` | `Client` | yes |
| `sort` | int | | `Client` | yes |
| `stat_id` | key reference | `stat` | `Client` | yes |
| `combine` | int | | `Client` | yes |
| `min` | int | | `Client` | yes |
| `max` | int | | `Client` | yes |
| `tag_scope` | tag list | `tag` | `Client` | no |
| `condition_id` | int | | `Client` | no |

`stat` is Scope A's engine-range type (contracts 13.1). `combine` is `1` Flat, `2` Increased, `3` More
(contracts 13.2). `min` and `max` are inclusive, in the stat's scaled units or in basis points. An empty
`tag_scope` means the stat itself (11.3), and `condition_id` 0 means unconditional (11.3).

**One roll position drives every line on the tier.** A tier with two lines ("adds 10 to 40 physical
damage" as a pair) resolves both from the SAME stored position, so the two move together and the payload
stores one `ushort` for the affix rather than one per line. That is contracts 6.4's position applied
literally, and it is what makes a six-affix rare cost eighteen bytes of affix entries (3.7). `sort` is
what makes "the tier's second line" a stable phrase across a republish.

Whether a line's units are scaled units or basis points is decided by `combine` and by nothing else:
`Flat` is in the stat's scaled units, `Increased` and `More` are in basis points where 10,000 is 100
percent (contracts 13.2). The validator checks `min <= max` and refuses `min == max` on a tier whose
only line is the one being checked ONLY when the mod's kind is a rolled kind, because a fixed line is
exactly how a unique's guaranteed value is authored (8.6).

### 8.5 `rarity_rule`

| Field | Value kind | Reference target | Visibility | Required |
|---|---|---|---|---|
| `id` | int | | `Client` | yes |
| `key` | string key | | `Client` | yes |
| `display_format` | localized text key | | `Client` | yes |
| `min_affixes` | int | | `Client` | yes |
| `max_affixes` | int | | `Client` | yes |
| `max_prefixes` | int | | `Client` | yes |
| `max_suffixes` | int | | `Client` | yes |
| `name_word_positions` | int | | `Client` | yes |
| `upgrade_from` | key reference | `rarity_rule` | `Client` | no |

`id` is 1 to 255. `min_affixes` and `max_affixes` are totals across every kind, `max_prefixes` and
`max_suffixes` are per kind for `kind` 1 and 2 of 8.2, and `name_word_positions` is 0 for an item that
keeps its base name or above 0 to roll that many words.

**`display_format` IS the composed name template of contracts 12.3, and its arguments are fixed here.**
Contracts 12.3 resolves a rare's name through a template plus localized parts, and neither spec said which
row held the template. It is this field. The derived key is `rarity_rule.<key>.display_format`, the
template it names looks like `"{0} {1} {2}"`, and the ARGUMENTS are the item's `rare_name_word` texts in
POSITION ORDER (kind 134 stores the ids in that order, 8.8) followed by the base item's own name. So a
rare with two name words formats `{0}` and `{1}` from the words and `{2}` from the base, a language whose
adjective follows its noun reorders the template rather than the payload, and a rarity with
`name_word_positions = 0` formats `{0}` from the base name alone. Resolution goes through
`ContentStringCatalog.Format`, which is contracts 12.4's layered catalog over the `SafeFormat` path of
contracts 12.3, so a translator's malformed template falls back to the unformatted template instead of
throwing inside the frame loop.

**The id is a byte and that is a format constraint, not a preference.** Kind 130 is `[RarityId: byte]`,
pinned by contracts 9.8's `82 01 01 03`, so 255 rarities is the ceiling forever. That is generous against
PoE's four and Tibia's zero, and it is recorded in section 21 because raising it is a payload format
change.

`upgrade_from` is what a `set rarity` craft primitive walks (10.2) and what a rarity-upgrade currency
composes. It is a single parent rather than a list, so the rarities form a forest and a craft that
raises a rarity has exactly one answer for what comes next.

| `rarity_kind_limit` field | Value kind | Reference target | Notes |
|---|---|---|---|
| `id`, `key` | int, string key | | |
| `rarity_rule_id` | key reference | `rarity_rule` | |
| `mod_kind` | int | | a `mod.kind` above 2 |
| `max_count` | int | | how many of that kind the rarity permits |

| `rarity_weight` field | Value kind | Reference target | Visibility |
|---|---|---|---|
| `id`, `key` | int, string key | | `ServerOnly` |
| `rarity_rule_id` | key reference | `rarity_rule` | `ServerOnly` |
| `tag_id` | key reference | `tag` | `ServerOnly` |
| `weight` | int | | `ServerOnly` |

`rarity_weight` is 8.3's first-tag-wins rule again, over rarities rather than tiers, and it is the draw
step 3 of 9.4 makes.

### 8.6 `unique_template`

| Field | Value kind | Reference target | Visibility | Required |
|---|---|---|---|---|
| `id` | int | | `Client` | yes |
| `key` | string key | | `Client` | yes |
| `base_id` | key reference | `item` | `Client` | yes |
| `name` | localized text key | | `Client` | yes |
| `item_level_min` | int | | `Client` | yes |
| `weight` | int | | `ServerOnly` | yes |

`base_id` names Scope A's `item` type, which is the base. An earlier draft wrote the target as
`item_base`, a type key nothing registers, which would have fired `KEC0007` at registry freeze and
blocked every pack carrying a unique. `id` is what payload kind 129 stores. `name` replaces the base
name and is a marker field like every localized text key. `weight` is a scalar `ServerOnly` FIELD, which
contracts 4.7 allows because visibility is per field, and needs no child type because it does not repeat.

| `unique_line` field | Value kind | Reference target | Notes |
|---|---|---|---|
| `id`, `key` | int, string key | | |
| `unique_template_id` | key reference | `unique_template` | |
| `sort` | int | | authored order |
| `mod_id` | key reference | `mod` | the mod row the line is |
| `tier_ordinal` | int | | which of that mod's tiers |

| `unique_socket` field | Value kind | Reference target | Notes |
|---|---|---|---|
| `id`, `key` | int, string key | | |
| `unique_template_id` | key reference | `unique_template` | |
| `sort` | int | | the socket's index in kind 132, authored order |
| `socket_type_id` | key reference | `socket_type` | 0 means no restriction |

**A unique is a template plus rolls, not a separate item kind.** The generated item carries kind 129
naming the template, kind 131 carrying the rolled positions for the template's lines, and kind 130
carrying the unique rarity rule. Nothing in the payload format is special. That is what lets a craft
reroll a unique's values without any primitive knowing what a unique is: `reroll values` (10.2) rewrites
positions and never touches kind 129.

**A unique's lines are NOT a second line shape: each one NAMES a mod row**, which is the one asymmetry in
the format and it is worth naming. Kind 131's entries are `(mod id, tier, position, flags)`, so a unique's
line needs a mod id to sit there. The answer is that a unique template's lines are authored as ORDINARY
MOD ROWS with a single tier, no `mod_tier_weight` row anywhere (so the generator can never roll them onto
an ordinary item) and a group that keeps them off a rare, and `unique_line` points at that mod and tier.
That costs one mod row per unique line and buys a payload with no second affix shape, no second decode
path and no second remap story. It is the single most important consequence of 3.4 for an author to
understand, so 8.10 restates it in the authoring checklist.

**`unique_socket` rows ARE the socket order.** The earlier draft's `(socket type id, count)` pairs could
not say what order the sockets sat in, and kind 132 is authored order (3.5), so one row per socket with a
`sort` is both smaller and the only shape that can seed the field.

### 8.7 `socket_type`

| Field | Value kind | Reference target | Visibility | Required |
|---|---|---|---|---|
| `id` | int | | `Client` | yes |
| `key` | string key | | `Client` | yes |
| `display_format` | localized text key | | `Client` | yes |
| `max_nested_bytes` | int | | `Client` | yes |

| `socket_tag_rule` field | Value kind | Reference target | Notes |
|---|---|---|---|
| `id`, `key` | int, string key | | |
| `socket_type_id` | key reference | `socket_type` | |
| `sort` | int | | evaluation order within its rule kind |
| `tag_id` | key reference | `tag` | |
| `rule` | int | | `1` accept, `2` reject |

Gate 0 decision 2 says socket types DO restrict, and that the restriction is content rather than code.
`socket_tag_rule` is that restriction, evaluated by the socket primitive (10.2) and by nothing else. A
contained item must carry at least one `accept` tag and none of the `reject` tags, reject is checked
FIRST and wins, so a type that accepts `gem` and rejects `corrupted` is one parent row and two child rows.
A type with no accept row accepts nothing, which is how a decorative socket is authored.

`max_nested_bytes` exists because of 3.5's arithmetic: a six socket item has about 58 bytes per nested
payload before the 512 byte cap binds, and a socket type that admits a deep item makes that ceiling a
runtime surprise. 0 means `MaxInstancePayloadBytes`. Authoring the per socket budget turns it into a
publish-time fact and a refusal at the moment of socketing rather than a refusal at the moment of
encoding.

### 8.8 `crafting_currency` and `rare_name_word`

`crafting_currency` is section 10's row and its fields, its `currency_step` children and its
`currency_guard` children are listed there (10.4), because a row with no primitive vocabulary to read it
against is a list of opaque columns. All three register here so the type ids are in one table.

| `rare_name_word` field | Value kind | Reference target | Visibility | Required |
|---|---|---|---|---|
| `id` | int | | `Client` | yes |
| `key` | string key | | `Client` | yes |
| `text` | localized text key | | `Client` | yes |
| `position` | int | | `Client` | yes |

| `rare_name_word_weight` field | Value kind | Reference target | Visibility |
|---|---|---|---|
| `id`, `key` | int, string key | | `ServerOnly` |
| `rare_name_word_id` | key reference | `rare_name_word` | `ServerOnly` |
| `tag_id` | key reference | `tag` | `ServerOnly` |
| `weight` | int | | `ServerOnly` |

`id` is what payload kind 134 stores. `text` is the word itself, a marker field whose derived key is
`rare_name_word.<key>.text`, which is the name contracts 12.3 illustrates. `position` is 1 to 255, which
slot in the name the word may fill.

A rare name is `name_word_positions` words (8.5) drawn one per position, each from the words whose
`position` matches and whose weight against the base's tags is above zero. Kind 134 stores the word ids
in POSITION ORDER, so the name is reproducible from the payload with no re-roll, which is contracts
14.3's rule applied to a name.

**The composed name is localization's problem and 8.5 says exactly whose row holds the template.** Kind
134 stores ids, `rarity_rule.display_format` holds the template, and the display layer composes them
through `ContentStringCatalog.Format`. A language whose adjective follows its noun composes the same three
ids differently, which is exactly why the words are ids rather than a string.

### 8.9 Registration, validation and what publish refuses

The eighteen validators run inside contracts 10.4's one validator and add these checks to its minimum
list. None of them relaxes anything the contract requires. The three marked PUBLISH-ONLY compare against
the `previous` snapshot contracts 10.4 now takes as an argument, so they do not run at boot or in a test
where `previous` is null.

1. Every child row's parent reference resolves, and a `currency_guard` naming a `currency_step` names one
   belonging to the SAME currency.
2. A `mod_tier`'s `item_level_min <= item_level_max`, and its `ordinal` is 1 to 255 and unique within its
   mod.
3. PUBLISH-ONLY: a mod's tier ordinals are unchanged since the previous published version. A REORDER is
   refused, because the ordinal is in every stored payload (3.4).
4. A `stat_line`'s `stat_id` resolves, its `combine` is 1, 2 or 3, and `min <= max`.
5. A `mod_group`'s `max_per_item` is at least 1.
6. A `rarity_rule`'s `min_affixes <= max_affixes`, `max_prefixes + max_suffixes >= max_affixes`, and its
   `upgrade_from` chain has no cycle.
7. A `unique_line`'s `mod_id` resolves, its `tier_ordinal` names a tier of that mod, and that mod carries
   no `mod_tier_weight` row on any tier, which is the check that keeps a unique line off an ordinary rare.
8. A `socket_type`'s accept and reject tag sets are disjoint.
9. A `rare_name_word`'s `position` is at least 1, and for every rarity rule with `name_word_positions = N`
   and every base tag reachable at that rarity, every position 1 to N has at least one word with a
   non-zero weight. That last one is the check that stops a publish producing an item whose name cannot
   be rolled, and it is the expensive one: it is a cross product over rarities, positions and tags, run
   once per publish, which is the right place for it.
10. A `crafting_currency` carries at most `max_steps` steps and `max_steps` is at most 16 (10.4), and
    every step's `sort` is unique within the currency.
11. PUBLISH-ONLY: a `rarity_rule`'s `id` is unchanged since the previous published version, because kind
    130 stores it.
12. PUBLISH-ONLY: no `mod` that any stored payload could name has lost its tier for that ordinal without
    a remap rule covering it. The validator cannot see stored payloads, so what it actually checks is the
    weaker and still useful form: every tier ordinal removed since `previous` is named by a rule in the
    set it was handed.

### 8.10 The authoring checklist, and the four reference games on these eighteen types

An author adding one affix touches one `mod` row, one `mod_tier` row per tier, one `stat_line` row per
line on each tier, and one `mod_tier_weight` row per tag each tier can spawn against. An author adding one
unique touches one `unique_template` row, one `unique_line` row per line plus the `mod` and `mod_tier`
rows those lines name, and one `unique_socket` row per forced socket. An author adding a rarity touches
one `rarity_rule` row, one `rarity_weight` row per tag, one `rarity_kind_limit` row per counted kind above
2 and, if it names items, N `rare_name_word` rows with their weights. Nothing above needs an engine
release.

**That is more rows than the earlier draft's blob and it is not more authoring.** The same facts were
always there. What changed is that each one is now a row a generic editor renders, a publish diff reports
field by field, and an audit records a before and an after for, rather than a hex box whose whole content
reads as one changed value (contracts 4.7, and `catalog_audit.before_value` is capped at 512 characters
anyway).

What each reference game authors, which is the concrete form of the claim that these are shapes rather
than PoE:

- **OSRS.** Nothing. Zero mod rows, zero rarity rules, zero uniques. Every item is a base with instance
  id 0, and all eighteen types register with an empty row set, which the validator permits because every
  cross-check above is vacuous over an empty set.
- **Tibia.** Zero mods. Item level and upgrade tier live in the engine range kinds 2 and 8 (3.3), and a
  plus-one upgrade is a craft primitive writing kind 8, not an affix. Socket types register if the game
  wants gems and stay empty if it does not.
- **Mortal Online.** Zero mods again, and this one is the interesting case. A crafted item's identity is
  its MATERIALS (kind 7), and its stats derive from them at evaluation time through game-registered
  modifiers (11.5), so the affix machinery sits unused while the instance machinery carries everything.
  That is contracts 6.3's point about storing inputs rather than derived stats, and it is why kind 7 is
  in the ENGINE range rather than in Scope B's.
- **PoE.** All eighteen, heavily. This is the only shape that uses the whole section, and the numbers
  section 9 is sized against (2,000 mods, 50,000 bases) are its numbers rather than the fleet's.

**Two authoring traps, written here because both cost a republish to undo.** First, a tier ordinal is
immutable, so inserting a new best tier at the top means appending a `mod_tier` row with the NEXT ordinal
and letting the ordinal carry no ordering meaning (3.4). Second, a unique's lines are mod rows with no
weight row, so an author who gives one a `mod_tier_weight` has quietly added it to the rare pool for every
base carrying that tag. Check 7 of 8.9 catches the second at publish. Nothing catches the first, because a
reorder is refused and an append is legal, which is the correct outcome and a surprising one to read for
the first time.

## 9. Item generator

### 9.1 What it is and is not

`ItemGenerator` turns (base, item level, source of randomness) into a payload. It does NOT decide WHICH
base drops: that is a loot table, which is Scope A's engine-range content type and is `ServerOnly`. The
seam is deliberate, because a loot roll and an affix roll are different questions and Ruinborne's
`LootRoll` already owns the first one (`c-ruinborne.md:718-734`).

**It takes its `IRandomSource` in the CONSTRUCTOR and holds it**, which is contracts 14.4 verbatim
rather than the narrowing an earlier draft of this section proposed. The contract is explicit: "A
constructor parameter, never an ambient static, never a service locator, never a default. A type that
rolls takes `IRandomSource` in its constructor and holds it. A type with no `IRandomSource` cannot roll,
which is the property that makes 'does this class have gameplay randomness' answerable by reading its
signature." A per-call source keeps that answerable at the METHOD and loses it at the TYPE, which is the
half the contract cares about: a caller anywhere in the fleet could hand a `SeededRandomSource` to the
production generator with nothing in any signature to notice.

**A replay tool builds a SECOND generator.** That is the whole cost of taking the source in the
constructor, and it is one line, because the expensive part of a generator is `ModCandidateTables` (9.2)
and the two instances SHARE it: the tables are immutable after the boot that built them. So a replay
harness constructs `new ItemGenerator(tables, new SeededRandomSource(seed))` beside the live
`new ItemGenerator(tables, new CryptographicRandomSource())`, at the cost of one object and no table
build. `ICraftOperation` follows the same rule (10.5). `IRandomSource` and both implementations live in
`KhaozEngine.Primitives` (contracts 14.1), so taking one costs no package reference.

### 9.2 The precomputed tables, and what they cost

The naive table is keyed by (base, item level) and it does not fit. At 50,000 bases and 100 item levels
that is 5,000,000 candidate arrays, and at even 500 candidates each it is tens of gigabytes. The shape
below is keyed so that the BASE COUNT contributes almost nothing, which is the whole trick.

**The input is three row sets and nothing else:** every `mod` row for its `kind`, `group_id` and
`legacy` flag, every `mod_tier` row for its ordinal and its two level bounds, and every
`mod_tier_weight` row for its tag and weight (8.3). All three are ordinary content rows, so the build is
a scan of three indexed row sets rather than a decode of a blob per mod, and a server whose pack omits
the `ServerOnly` weight type builds no tables at all, which is exactly what a client does.

**Two levels. Tag-band tables, built once, and a memoized merge per tag signature.**

1. **Bands.** Collect every distinct `item_level_min` and `item_level_max + 1` across every `mod_tier`
   row. Sort them. The intervals between consecutive values are the BANDS, and within one band no tier's
   gate changes, so the live tier set is constant. At 2,000 mods and 8 tiers each the boundary count is
   bounded by 32,000 and in practice is the authored level curve, tens rather than thousands.
2. **Tag-band tables.** For each `(tag id, band)` pair, an array of `(mod id, tier ordinal, weight)` for
   every `mod_tier` row live in that band with a `mod_tier_weight` row naming that tag at a non-zero
   weight, sorted ascending by (mod id, tier ordinal). Sorted rather than insertion ordered, because the sort is what makes the
   weighted pick reproducible from a seed and independent of pack load order (contracts 4.3).
3. **The merge, at roll time.** A base's candidate set is the merge of the tables for its tags, walked in
   the base's AUTHORED tag order, taking the first weight found for each `(mod id, tier ordinal)` and
   ignoring later ones. That is 8.3's first-tag-wins rule executed rather than precomputed.
4. **The memo.** The merge is keyed by (tag signature, band), where the tag signature is the base's
   ordered tag list interned to an int. Bases sharing a tag list share a merge. A bounded dictionary
   holds the most recent 4,096 on an LRU eviction, and the ratio is worth writing down rather than
   calling it a cache: a few hundred authored tag signatures over 64 bands is a key space of about
   19,200, so 4,096 entries is a FIFTH of it. That is deliberate. The 4,096 is a MEMORY bound, not a
   claim that the cache is complete: covering the whole key space at the entry size below would cost
   about 46 MB. What makes the hit rate high anyway is that a live server rolls a small hot set of bases
   at a small hot set of levels, so the working set at any moment is a handful of bands times the bases
   actually dropping, and a miss costs one merge over a few hundred entries, which is microseconds. A
   pathological pack degrades to a merge per roll and nothing worse.

**The arithmetic, at the owner's scale of 50,000 bases and 2,000 mods.** Take 8 tiers per mod (16,000
tiers), 5 tag weights per tier and 64 bands, with a tier spanning on average a third of them.

| Quantity | Formula | Result |
|---|---|---|
| Tag-band table entries | `16,000 tiers * 5 tags * 21 bands` | about 1.7 million |
| Bytes at 12 per entry | `1.7M * 12` | **about 20 MB** |
| Distinct tag signatures | authored, bounded by base count | a few hundred |
| Memo entries held | `4,096 * 200 entries * 12 bytes` | **about 9.8 MB at 200 entries each** |
| Base-derived memory | `50,000 * 8` for the signature intern | **400 KB** |
| Build: appends | one per table entry | 1.7 million |
| Build: sort | 1.7M entries across about 19,000 buckets | dominated by the appends |

**About 30 MB resident and a build measured in low hundreds of milliseconds**, both of which belong in
16's budget table as targets rather than as claims, because neither is measured. The 30 is `20 + 9.8 +
0.4`, and an earlier draft wrote 25 because it priced the memo at 4 MB against its own 12 bytes an entry. The number that matters
for the SHAPE is the last row of the top half: 400 KB for fifty thousand bases. A base costs eight bytes
because it never enters a table, only its tag list does. That is what makes the design survive the
owner's "millions of owned items" over a large catalog.

**A BOOT builds the tables, not a publish, and they are immutable for the life of the process.** An
earlier draft said a publish builds new tables beside the old ones and swaps the reference. That is a
live-apply mechanism and v1 does not have one: a new content version becomes active at server RESTART
(contracts 1.3 item 8), which is the owner's decision 8 and is what the page stamp and the connect door
both assume. So there is exactly ONE table set in a process, no roll can see two, and a publish makes a
version active for the NEXT boot. The beside-then-swap shape is kept as the HOOK a later live apply would
use, and `GrimhollowEconomy.Current` is that shape in a game today (`b-grimhollow.md:56-59`), but nothing
in v1 calls it. Weights are `ServerOnly` (8.3), so a CLIENT builds none of this and holds none of it.

### 9.3 The random source, restated at the call site

`IRandomSource` is contracts 14.1 verbatim and lives in `KhaozEngine.Primitives` (2.2). Three of its four
members are used here:
`NextInt(minInclusive, maxExclusive)` for a count and for a weighted pick, `NextRollPosition()` for a
`ushort` roll position, and neither `NextULong` nor `NextBytes`. A weighted pick is
`NextInt(0, weightTotal)` followed by a walk of the cumulative array, never a floating point draw and
never a rejection loop, which is contracts 13.4 and 14.1 applied together.

**Every draw the algorithm makes is a function of the affix COUNT and nothing else.** A candidate that is
filtered out is removed from the pool BEFORE the draw rather than drawn and rejected, so two items of the
same rarity on the same base at the same item level consume exactly the same number of draws. That is
Grimhollow's always-draw rule (`GrimhollowDrops.cs:35-37`, both rolls always drawn so the stream stays
position stable) generalised, and it is what makes a seeded replay of a drop session reproducible when
one item's pool differs from another's.

### 9.4 The algorithm, step by step

```csharp
public readonly record struct GenerationContext(
    int BaseId, int ItemLevel, int ForcedRarityId, int ForcedUniqueTemplateId, int Quality);

public readonly record struct GenerationResult(
    int BaseId, long InstanceId, ReadOnlyMemory<byte> Payload, int RarityId, int ContentVersion);

public sealed class ItemGenerator
{
    public ItemGenerator(ModCandidateTables tables, IRandomSource random);
    public GenerationResult Generate(in GenerationContext context);
}
```

Every step is numbered because the ORDER of the draws is the reproducibility contract.

1. **Resolve the band** from `context.ItemLevel` by binary search over the band boundaries. No draw.
2. **Resolve the unique**, if `ForcedUniqueTemplateId` is non-zero. Skip to step 9 with the template's
   `unique_line` rows as the affix list and its `unique_socket` rows as the socket list. No draw. A unique is FORCED by the
   caller rather than rolled here, because deciding that a unique drops is the loot table's job (9.1).
3. **Roll the rarity**, unless `ForcedRarityId` is non-zero. One `NextInt(0, total)` over the
   `rarity_weight` rows against the base's tags, using 8.3's first-tag-wins rule.
4. **Roll the affix count.** One `NextInt(rule.min_affixes, rule.max_affixes + 1)`.
5. **Roll the prefix and suffix split.** For each of the `count` picks in turn, decide the kind FIRST:
   one `NextInt(0, openKindTotal)` over the kinds still under their per kind cap, weighted by the number
   of candidates of that kind. Kind before mod, so a rarity permitting three prefixes and three suffixes
   does not produce six prefixes because prefixes happen to outnumber suffixes in the pool.
6. **Filter the pool** for this pick: drop every candidate whose kind is not the chosen one, whose mod id
   is already on the item, whose `mod_group` is at `max_per_item`, or whose mod row carries `legacy`. No
   draw. The filter is a forward pass over the memoized merged array, producing a cumulative weight
   array in the same order.
7. **Pick the mod and tier.** One `NextInt(0, weightTotal)` and a walk of the cumulative array. When
   several tiers of one mod are live in the band, they are separate candidates and the weights decide,
   which is how "a better tier is rarer" is authored rather than coded.
8. **Roll the position.** One `NextRollPosition()` per affix. Record `(mod id, tier ordinal, position,
   flags 0)`. Repeat steps 5 to 8 until `count` picks have been MADE. **A pick whose filtered pool is
   EMPTY still draws and discards, one `NextInt(0, 1)` in place of step 7's weighted pick and one
   `NextRollPosition()` in place of this step**, places nothing, and the item ends with fewer affixes
   than the count asked for, which is a legal outcome and is reported in the result rather than retried.
   The two discarded draws are what keep 9.3's property true: without them one item consumes fewer draws
   than another of the same rarity on the same base, and a seeded session diverges at the first item
   whose pool empties. `NextInt(0, 1)` rather than nothing because step 7's real draw is
   `NextInt(0, weightTotal)` and a weight total of zero is not a legal argument, so the discard has to be
   a defined call rather than the same call on an empty pool.
9. **Sort the affix list ascending by mod id** (3.4). No draw.
10. **Roll the rare name.** For each position 1 to `rule.name_word_positions`, one `NextInt(0, total)` over
    that position's words weighted against the base's tags through their `rare_name_word_weight` rows. Record the word ids in position order.
11. **Seat the sockets.** A unique's `unique_socket` rows in `sort` order, otherwise the base's authored socket
    declaration, in authored order, every socket empty. No draw in v1: a rolled socket COUNT is a craft
    primitive (`add socket`, 10.2) rather than a generation step, so nothing here consumes a draw the
    owner has not asked for.
12. **Assemble the payload.** Kind 2 item level, kind 3 quality when non-zero, kind 129 when a unique,
    kind 130 rarity, kind 131 affixes, kind 132 sockets when non-empty, kind 134 rare name when rolled,
    plus kind 5 durability at full when the base declares it. Encode canonically (3.2).
13. **Allocate the instance id** when the payload is non-empty, from `InstanceIdAllocator` (3.6). An
    empty payload takes id 0 and the item is a plain stack.

**Cost, per rare.** Six affixes cost six kind draws, six mod draws, six position draws and up to two name
draws, so twenty draws and six cumulative-array passes over a filtered pool of a few hundred. The
allocation is one payload buffer. Section 16 budgets it at under 20 microseconds and calls it TBD.

### 9.5 The journal event

One event type, `item-generated`, written on the commit that first seats the item somewhere durable.

```
[EventVersion: byte = 1]
[BaseId: varint int32]
[InstanceId: varint uint64]
[RarityId: byte]
[ContentVersion: varint int32]
[SourceKind: byte]              // 1 drop, 2 craft, 3 admin grant, 4 migration, 5 to 255 game
[SourceId: varint int32]        // the loot table, currency or migration id, 0 when none
[PayloadLength: varint int32]
[Payload: PayloadLength bytes]  // the FULL payload, exactly as encoded
```

**It records the resolved item and never a seed, a state or a draw index**, which is contracts 14.3
verbatim and is also the only shape that survives the journal's replay model: a replay returns the
original receipt rather than re-running anything (`a-engine.md:535-564`), so an event carrying a seed
would have to be re-rolled to mean anything.

**The full payload rather than a reference to the page.** A page is rewritten whole on every later commit
(`JournalProjectionWrite.cs:10-29`), so the page bytes at the moment of generation are not recoverable
from anything except this event. The event carries the PAYLOAD, which is 58 bytes for the rare of 3.8 and
not the 69 byte slot entry, plus fourteen of header
(`1 + 2 + 5 + 1 + 1 + 1 + 2 + 1`), so an `item-generated` event is about **72 bytes**. Against the 128
events per operation cap (`JournalLimits.cs:10`) a drop burst of twenty rares is about 1.4 KB of events,
which is inside the one-tick batch budget of 6.4. An `item-crafted` event is a different size and 10.6
counts it.

**Content version on the event is what makes the audit answerable.** A support question of the form "what
did this item look like when it dropped" is answered by decoding the event's payload against the version
it names, rather than against today's, which is the whole reason contracts 7.2's stamp is a comparable
number.

### 9.6 Distribution, and the one property the tests must pin

Two claims a test has to hold the generator to (17.6), both falsifiable:

- **Weights are proportional.** Over a large seeded run on one base and one item level, each candidate's
  share of the outcomes is its weight divided by the pool total, within a tolerance the test states as an
  integer bound rather than a float (a chi-squared style bound stated as counts, so the assertion itself
  obeys contracts 13.4).
- **Positions are uniform.** `NextRollPosition` is uniform over 0 to 65,535 by contract, so the value
  distribution through 6.4's formula is uniform over the tier's range up to rounding, and the test asserts
  both ends are reachable, which is the property contracts 6.4's worked table demonstrates.

**Modulo bias is the one real trap and the contract already handles it.** `CryptographicRandomSource` uses
rejection sampling rather than modulo (contracts 14.2), because modulo bias on an affix pool is farmable.
`SeededRandomSource` wraps `DeterministicRng`, whose own `Next(int)` uses modulo and says so
(`DeterministicRng.cs:70-76`, "negligible bias for game ranges"). That is fine for a test and would not be
fine in production, which is exactly why the two implementations differ and why gate 0 decision 11 makes
running the seeded source on a hosted server log a Warning on every boot.

## 10. Crafting framework

### 10.1 The shape, in one paragraph

The owner's decision is "engine primitives composed in data, plus game-registered code for exotic
operations" (#884). So the engine ships a CLOSED set of fourteen primitives, a guard vocabulary, and a
`crafting_currency` row that is an ordered list of (guard set, primitive, parameters). A currency is
data. A primitive is code the engine owns. An exotic operation is code the GAME owns, reached through a
registry by id, and it is handed the same working copy and the same refusal vocabulary. v1 ships the
framework and zero currency rows, which is #884's "Out of scope for v1" taken literally.

**A craft NEVER mutates in place.** It decodes the target into a working payload builder, applies its
steps, re-encodes canonically and writes the result through `SetSlotAt` (4.7). A refusal at any step
discards the builder and the durable bytes are untouched, so there is no partial craft and no rollback
path to get wrong. That is the same all-or-nothing shape `OwnedItemStacking.TryFold` already has in
Ruinborne (`c-ruinborne.md:22-26`) and `GrimhollowTrade`'s bag clone has in Grimhollow
(`b-grimhollow.md:1055-1078`).

### 10.2 The fourteen primitives

| # | Primitive | Parameters | What it does |
|---|---|---|---|
| 1 | `AddRandomMod` | kind, tier ceiling or 0 | one pick through 9.4 steps 6 to 8 against the item's own base and item level |
| 2 | `RemoveMod` | selector | removes one affix chosen by the selector, 10.3 |
| 3 | `RerollValues` | selector | new `NextRollPosition()` for every selected affix, same mods and tiers |
| 4 | `RerollMods` | kind mask | discards the selected affixes and re-runs 9.4 steps 4 to 9 for that kind |
| 5 | `SetRarity` | rarity id or 0 for `upgrade_from` | writes kind 130, then trims or fills affixes to the new rule's counts |
| 6 | `AddSocket` | socket type id or 0 | appends one empty socket to kind 132 |
| 7 | `Socket` | socket index, source slot | moves an item INTO a socket, keeping its instance id |
| 8 | `Unsocket` | socket index, destination slot | moves it back out, keeping its instance id |
| 9 | `ApplyEnchant` | mod id, tier | writes one entry into kind 133 |
| 10 | `RemoveEnchant` | selector | removes one entry from kind 133 |
| 11 | `Repair` | amount or 0 for full | raises kind 5's current toward its maximum |
| 12 | `SetQuality` | delta or absolute | writes kind 3 |
| 13 | `Identify` | | sets kind 128's state to 1 and its revealed mask to all ones |
| 14 | `SetFlag` | bit, value | writes one bit of kind 1, and refuses a bit above 2 in v1 |

Four notes an implementer needs and would otherwise guess:

- **`SetRarity` is the only primitive that can both add and remove affixes**, and it does so in a defined
  order: trim from the END of the sorted list first (highest mod id) when the new rule permits fewer, and
  fill by repeating steps 5 to 8 when it permits more and the currency asked for a fill. Trimming from the
  sorted end rather than at random makes the operation reproducible from the journal event without a draw.
- **`Socket` and `Unsocket` are the only primitives that touch TWO slots**, which makes them the only ones
  that can make a craft a two-page commit (5.6).
- **`AddRandomMod` uses the item's OWN item level** from kind 2, never the crafter's level and never the
  base's. That is what makes item level worth storing per instance.
- **`Identify` is the only primitive with no content parameters at all**, and it is in the list rather than
  being a game concern because the unidentified mechanic is engine machinery (12.7).

### 10.3 The guard vocabulary and the selector

A guard is a PRECONDITION evaluated against the working copy before a step runs. Every guard is a closed
kind plus at most two integer parameters, so a guard set encodes in a few bytes and a refusal names a
guard rather than a message.

| Kind | Guard | Parameters | True when |
|---|---|---|---|
| 1 | `RarityIs` | rarity id | kind 130 equals it |
| 2 | `RarityIsAtMost` | rarity id | kind 130's `upgrade_from` chain reaches it |
| 3 | `AffixCountAtMost` | mod kind, count | the item carries at most that many of that kind |
| 4 | `AffixCountAtLeast` | mod kind, count | likewise |
| 5 | `HasMod` | mod id | kind 131 or 133 carries it |
| 6 | `LacksMod` | mod id | it does not |
| 7 | `HasTag` | tag id | the BASE carries it |
| 8 | `ItemLevelBetween` | min, max | kind 2 is inside the range, inclusive |
| 9 | `QualityBetween` | min, max | kind 3 is inside the range |
| 10 | `SocketCountBetween` | min, max | kind 132's count is inside the range |
| 11 | `SocketEmpty` | socket index | that socket's contained definition is 0 |
| 12 | `IsIdentified` | state | kind 128's state equals it |
| 13 | `FlagIs` | bit, value | that bit of kind 1 equals it |
| 14 | `NotLegacy` | | no affix on the item names a `legacy` mod row |
| 15 | `IsCorruptible` | | kind 1 bit 0 is clear |

**`IsCorruptible` is STANDING, so no currency authors it and none can opt out.** Every primitive that
writes any part of the payload refuses an item whose kind 1 bit 0 is set, whatever the currency's guard
set says, because "corrupted" means "cannot be modified further" (question 1) and a refusal a currency
can forget is not that. It stays in the vocabulary above because the working copy reports it by kind when
it refuses, and because an authored `IsCorruptible` on a step is a legal, redundant way for an author to
document intent. The worked whetstone in 10.4 does NOT author it, and an earlier draft did, which is how
this pair got out of step.

**Guards are ANDed and there is no OR, no NOT and no nesting.** An OR is two currency rows, which is one
more authored row and no evaluator. That refusal is the same one contracts 4.6 makes about tags and the
same one the guard vocabulary makes about itself: a small closed language whose every refusal is
attributable beats an expression tree whose failures need explaining.

**`NotLegacy` is the crafting guard contracts 5.4 names**, and it is a STANDING guard rather than an
authored one: every primitive that would add a mod refuses a legacy row regardless of the currency's guard
set, and `NotLegacy` as an authored guard is the stronger statement that the item must carry none at all.

**A LEGACY AFFIX ENTRY IS FROZEN, and that is a second standing rule rather than a restatement of the
first.** No primitive and no game operation rewrites any part of an affix entry whose mod row carries
`legacy`: not its roll position, not its tier, not its flags. `RerollValues` skips it, `SetRarity`'s trim
and fill leave it where it is and count it against the rule's limits, and an `ICraftOperation` that
touches kind 131 or 133 is handed a working copy that refuses the write. The reason is contracts 5.4's
own words, "a legacy id that can never be generated or crafted again", read conservatively: an earlier
draft scoped the standing refusal to primitives that ADD a mod, which left `RerollValues` free to draw a
fresh `NextRollPosition()` against a legacy tier's preserved range, over and over, until the roll sat at
65,535. The mechanism the owner chose in order to FREEZE old rolls would have become a farm for them, and
the item would be strictly better than anything obtainable, forever.

Frozen is the conservative reading and it costs something real, so it is flagged for the owner at gate 1
rather than assumed: an item with one legacy affix can never have its OTHER affixes rerolled by any
currency that touches the whole list, because a `RerollValues(AllOfKind)` would have to skip one entry and
rewrite the rest, which it may do, and a currency authored as `RerollValues(ByIndex)` on the frozen entry
simply refuses. If the owner wants the softer rule, the place to relax it is here, and the relaxation is
"a legacy entry may be rewritten only by a primitive that cannot IMPROVE it", which is a judgement about
content rather than a property the engine can check.

**The selector**, used by primitives 2, 3 and 10, is a third closed vocabulary and the smallest one:

| Kind | Selector | Chooses |
|---|---|---|
| 1 | `ByModId` | the entry naming that mod, or a refusal when absent |
| 2 | `ByIndex` | the entry at that index of the SORTED list |
| 3 | `RandomOfKind` | one uniform draw over the entries of that kind |
| 4 | `AllOfKind` | every entry of that kind |
| 5 | `LowestTier`, 6 `HighestTier` | by tier ordinal, ties broken by lower mod id |

`RandomOfKind` is the only selector that draws, and it draws exactly once, so a currency's draw count is a
function of its step list rather than of the item it hits.

### 10.4 A `crafting_currency` row and its two children

A currency is THREE content types, not one row with two lists in it: the currency, its ordered steps and
its guards. 8.1 has the type ids and the reason.

| `crafting_currency` field | Value kind | Reference target | Notes |
|---|---|---|---|
| `id` | int | | |
| `key` | string key | | |
| `name` | localized text key | | a marker, key `crafting_currency.<key>.name` |
| `description` | localized text key | | a marker, key `crafting_currency.<key>.description` |
| `consumes_definition_id` | key reference | `item` | what is spent, empty for a free operation |
| `consumes_count` | int | | |
| `max_steps` | int | | a publish-checked ceiling, at most 16 in v1 |

| `currency_step` field | Value kind | Reference target | Notes |
|---|---|---|---|
| `id`, `key` | int, string key | | |
| `crafting_currency_id` | key reference | `crafting_currency` | |
| `sort` | int | | the step's position, unique within the currency |
| `operation` | int | | 1 to 14 is a primitive (10.2), at or above 1,024 is a game operation (10.5) |
| `parameter_a` to `parameter_d` | int | | 0 when unused. A selector occupies two, its kind then its parameter |

| `currency_guard` field | Value kind | Reference target | Notes |
|---|---|---|---|
| `id`, `key` | int, string key | | |
| `crafting_currency_id` | key reference | `crafting_currency` | |
| `currency_step_id` | key reference | `currency_step` | EMPTY means a target guard, set means a step guard |
| `sort` | int | | evaluation order within its set |
| `guard_kind` | int | | a kind of 10.3 |
| `parameter_a`, `parameter_b` | int | | 0 when unused |

**Target guards and step guards are one type told apart by one empty reference.** A guard with no
`currency_step_id` is evaluated once, before any step, against the target. A guard naming a step is
evaluated before that step. One type rather than two because the guard SCHEMA is identical and a second
type would duplicate it, and check 1 of 8.9 is what stops a guard naming a step of a different currency.

A step naming an `operation` at or above 1,024 is a GAME operation rather than a primitive (10.5). The two
share the step list so a currency can mix them, which is the point of having a registry at all.

**Worked example, and it is deliberately not a PoE currency.** A "whetstone" that repairs and adds one
quality point, refusing an unidentified item:

```
crafting_currency   whetstone:  consumes_definition_id = whetstone, consumes_count = 1, max_steps = 2
currency_guard      whetstone_target_1:  step empty,  IsIdentified(1)
currency_step       whetstone_1:  sort 1, operation 12 SetQuality, parameter_a = +1 (delta)
currency_guard      whetstone_1_g1:  step whetstone_1,  QualityBetween(0, 19)
currency_step       whetstone_2:  sort 2, operation 11 Repair, parameter_a = 0 (full)
```

Three properties of that encoding are worth naming. A step whose guard set is false is SKIPPED rather than
refusing the whole craft, so the whetstone repairs an item already at quality 20. A target guard failing
refuses the whole craft and consumes NOTHING, which is the difference between a precondition and a step
guard and is the reason both exist. And the whetstone does NOT author `IsCorruptible`, because that
refusal is standing and every currency inherits it (10.3).

### 10.5 Game operations through a registry

```csharp
public interface ICraftOperation
{
    int Id { get; }                                  // >= 1024
    CraftRefusal? Apply(ref CraftWorkingCopy copy, ReadOnlySpan<int> parameters);
}

CraftingRegistry.Register(ICraftOperation operation);   // process start, frozen at first pack load
```

**An operation that rolls takes its `IRandomSource` in ITS constructor**, per contracts 14.4 and for the
same reason the generator does (9.1): the registry holds instances rather than types, so the game
constructs its operation with the source the process is running on, and an operation with no source in
its constructor provably cannot roll. That is why `Apply` has no random parameter.

The registry follows `InstancePropertyRegistry` (3.3), which follows `ReplicationRegistry.Register`
(`TileProtocol.Components.cs:122`): registration once, at process start, frozen at the first pack load,
a later registration throws, and an operation whose `Id` is below 1,024 is refused by the registry itself
rather than by a rule in prose, which is the same caller-band argument 3.3 makes for property kinds.

**A game operation gets the working copy and the same refusal vocabulary, and it gets NO new powers.** It
cannot write an unregistered property kind, cannot exceed `MaxInstancePayloadBytes`, cannot produce a
non-canonical payload and cannot allocate an instance id, because the working copy is a builder rather
than a byte array and the encode at the end enforces all four. An operation that violates one of them
throws at encode, which is a game bug caught at the first test rather than a corrupt item in a page.

**An operation is not durable and is not versioned by the pack.** A currency row names its id, so an
operation the process does not have registered makes that currency refuse with `operation-unregistered`
at the moment it is used rather than at boot. That is a deliberate softening of contracts 10.5's
fail-closed rule, and the reason is that the rule is about a missing CONTENT VERSION rather than about a
code registration: a server that shipped without an operation is a deploy mismatch, which is loud at the
first use and would be a boot failure for every player if it were fail-closed at boot. It is open question 8.

### 10.6 The journal operation for a craft

**Action kind.** A durable string, `item-craft`, never renamed and never switched on, following
`ProcessingActionKinds`' stated rule (`b-grimhollow.md:569-604`).

**The normalized intent**, which is what the journal hashes to detect a conflicting replay
(`JournalValidation.Hash`, `JournalLimits.cs:135`):

```
[IntentVersion: byte = 1]
[CurrencyId: varint int32]
[TargetContainerId: varint uint16][TargetSlot: varint uint16]
[SourceContainerId: varint uint16][SourceSlot: varint uint16]   // the currency's own slot
[TargetInstanceId: varint uint64]                               // 0 when the target is a plain stack
[ExtraParameterCount: varint][ Extra: varint int32 ] * count    // a socket index, a selector choice
```

**The target's INSTANCE ID is in the intent and that is the load bearing field.** Without it, a replayed
craft whose slot has since been refilled by a different item would hash identically and apply to the wrong
item. With it, the same operation id carrying a different instance id is an `OperationConflict`, and the
same operation id carrying the same instance id is a `Replayed` that returns the original receipt
(`a-engine.md:535-564`). Section 15.1 is the exploit this closes.

**The resolved outcome** is one `JournalEvent`, `item-crafted`:

```
[EventVersion: byte = 1]
[CurrencyId: varint int32][InstanceId: varint uint64]
[ContentVersion: varint int32]
[BeforeLength: varint int32][Before: bytes]     // the payload as it stood
[AfterLength: varint int32][After: bytes]       // the payload as it stands
```

**An `item-crafted` event is about 127 bytes on a rare, and here is the count:**
`1 + 2 + 5 + 1 + 1 + 58 + 1 + 58`, which is the version byte, a currency id, the instance id, the content
version, and each payload with its length varint at 3.8's 58 byte rare. After alone would be 68, so
carrying the BEFORE costs **59 bytes**, not the 42 an earlier draft claimed. It is worth every one of
them: the page is rewritten whole on the next commit, so without the before bytes nothing in the durable
record can answer what a craft changed, which is the exact failure Ruinborne's audit has
(`c-ruinborne.md:328-346`, audit rows recording that a row changed and carrying no values). Contracts 4.7
makes the same argument for the content audit, and this is it applied to the instance audit. Twenty crafts
in one batch is therefore `6,900 + 20 * 127 = 9,440` bytes, which is 6.7's table and budget 4.

A REFUSED craft writes nothing durable. It is not an operation, it never reaches the journal, and the
client is answered with the `CraftRefusal` naming the guard or the primitive that refused. That is the
existing shape for a refused Grimhollow mutation, which is a silent refusal plus a resync
(`ITEM-DROP-DESIGN-2026-09-14.md` section 4).

### 10.7 Keep-legacy at publish

Contracts 5.4 and 8.2 own the mechanism. What this section owns is the AUTHORING FLOW and the default.

1. An author changes a mod tier's `Min` or `Max` in the authoring store and publishes.
2. The publish diff (contracts 4.7) reports every mod whose ranges moved, with the count of live items
   carrying it left UNKNOWN, because the engine does not index instances by mod and section 1.3 rules out
   building the index that would answer it.
3. The author chooses per mod, and the default is **RESCALE**: nothing is written, no rule is emitted, and
   every stored position maps through 6.4's formula into the new range on the next read. Gate 0 decision 4
   makes the rescale SILENT, so no notification, no flag and no event.
4. The other choice is **KEEP LEGACY**: the authoring store copies the mod to a NEW id in the same id
   space with `legacy` set (contracts 5.4), leaves the original id carrying the new ranges, and emits one
   `MovedToLegacy` remap rule (contracts 8.2 kind 3) from the original id to the legacy copy. Every affix
   entry the rule moves becomes FROZEN at that moment (10.3): its stored position is the roll it was
   made with and no craft rewrites it again, which is the whole of what "keep legacy" means to a player
   holding one.
5. Every stored page whose stamp is older than that publish moves its entries onto the legacy id at load
   (5.5 step 2), lazily, and is rewritten on its next commit. Items generated after the publish carry the
   original id with its new ranges. The two coexist forever.

**The rule set's idempotence is what makes step 5 safe, and there is one trap.** Contracts 8.3 forbids a
rule whose `ToId` is an earlier rule's `FromId` for the same type. A second keep-legacy publish on the SAME
mod would emit a rule from the original id to a second legacy copy, which is legal, and a rule from the
FIRST legacy copy to anywhere would not be. So a legacy copy is terminal in three directions at once: it
can never be generated (9.2 skips it), never be added by a craft, never have an entry naming it rewritten
by one (10.3), and never be the source of another rule. The publish validator's existing walk catches an
author who tries the last of those.

## 11. Stat evaluation base

### 11.1 What is new and what is not

Contracts 13.3 already decided the contested part: a NEW integer evaluator, `StatSet` kept unchanged
beside it for games that do not adopt content stats, and a game uses one or the other per stat and never
both. This section builds the new one. `KhaozEngine.Stats` is NOT modified, which is why 2.1 lists it
unchanged: `StatModifier(int Channel, float Flat, float Percent)` is a shipped struct and adding a
`More` kind to it is a breaking change the engine's own survey already names (`a-engine.md:1558-1571`).

`ContentStatEvaluator` lives in `KhaozEngine.ItemInstances`, not in `KhaozEngine.Stats`, for a layering
reason: it resolves stat definitions and tag ids through `IContentSnapshot`, and putting a Catalog
dependency on `Stats` would push it onto every game that uses the float kernel for a HUD bar.

### 11.2 The value types

```csharp
public readonly record struct StatModifierLine(
    int StatId, StatCombineKind Combine, int Value, int TagScopeStart, int TagScopeLength, int ConditionId);

public enum StatCombineKind : byte { Flat = 1, Increased = 2, More = 3 }

public readonly record struct StatSourceKey(byte SourceKind, int Ordinal, long InstanceId);

public readonly record struct StatContext(
    ReadOnlyMemory<int> Tags, int ConditionMask, IStatConditionRegistry? Conditions);
```

`Value` is in the stat's scaled units for `Flat` and in basis points for `Increased` and `More`
(contracts 13.2, 8.4). The tag scope is a RANGE into one shared tag array the evaluator owns rather than
a per-line array, so a source with eight lines allocates one array and eight structs, which is the
allocation shape `StatSet.AddSource` already uses (`a-engine.md:212-228`) applied one level tighter.

### 11.3 Tag scope and conditions

**A tag scope is an AND over the context's tags AND the target stat's own tags.** A line applies when
every tag in its scope is present in the UNION of `context.Tags` and the `stat` row's own `tags` field
(contracts 13.1, which Scope A registers as a tag list). An empty scope applies always, which is the
common case and costs a length check.

**The stat row's half is what `stat.tags` is for, and nothing else reads it.** Scope A authors `tags` on
every stat row and an earlier draft of this document matched a scope against the evaluation context only,
which left that field dead weight in every stat row and every client pack. With both halves, "increased
fire resistance" is one line on stat `fire_resistance` with scope `[fire]`, and it applies with no
context tag at all because the stat row itself carries `fire`. Without it the caller would have to pass
`fire` on every evaluation of that stat, which means every call site knows the taxonomy, which is the
coupling the tag system exists to remove. The context's half stays the mechanism for the cases that are
about the SITUATION rather than the stat: "increased fire damage WITH SPELLS" is scope `[fire, spell]`,
where `fire` is matched by the stat row and `spell` by the context. Scope is the mechanism for Grimhollow's `CanBeAHatchet` predicate becoming a row (contracts 4.6), and it
is the ONLY relation between a modifier and its target: there is no scope expression, no negation and no
OR, for the same reason 10.3 gives.

**A condition is an int id, and the engine owns `0` to `1023`.** Id 0 is unconditional. The engine
defines none in v1, deliberately, so the whole range stays free. A game registers above 1,023 through
`IStatConditionRegistry.Evaluate(conditionId, in StatContext)` returning a bool, and the engine never
calls it for an id it owns. The registry is consulted at RECOMPUTE time only (11.5), so a condition that
reads a moving value is a condition the game must dirty the evaluator on.

### 11.4 Sources and the deterministic source order

Contracts 13.2 fixes the `More` loop order as (source order, modifier index) and says why: integer
multiplication with rounding at each step is not associative, so `(a * x) * y` and `(a * y) * x` can
differ by one unit. This section fixes the source ORDINALS, and they are durable in the sense that
changing one changes a displayed number.

| `SourceKind` | Source | In v1 | Ordinal within the kind |
|---|---|---|---|
| 1 | base and implicit lines of the worn item | yes | the worn slot index, ascending |
| 2 | affixes of a worn item (kind 131) | yes | worn slot index, then affix index in the SORTED list |
| 3 | enchantments of a worn item (kind 133) | yes | the same |
| 4 | lines of an item SOCKETED into a worn item | yes | worn slot, then socket index in AUTHORED order |
| 5 | passives | no, reserved | |
| 6 | buffs and auras | no, reserved | |
| 7 to 255 | game sources | by registration | the game's own, and it must be deterministic |

**The fold order is (SourceKind, Ordinal, InstanceId, ModifierIndex), in that order, always.** The
instance id is in the key so that two sources that somehow tie on kind and ordinal still order, which
cannot happen through the table above and can happen through a game source. It is the same defensive
choice `TileWorldHash` makes by sorting before digesting (`a-engine.md:1219-1222`).

**Reserving 5 and 6 rather than assigning them is the whole reason this is a base rather than a system.**
A later passive tree adds a source kind and changes no fold, no format and no stored number, which is
#884's "sources: worn items and socketed items now, passives and buffs later" read as an ordering
commitment rather than a feature list.

**Socket order is authored and never sorted** (3.5), so kind 4's ordinal is the authored index. A player
who rearranges two gems can move a displayed value by one unit, which is correct and is the price of
integer rounding being honest.

### 11.5 Recompute, never per tick

`Recompute(StatSourceKey changed)` marks dirty and nothing else. A read of a stat recomputes it if dirty
and caches, which is `StatSet`'s own lazy-per-channel model (`a-engine.md:164-237`) carried across with
the arithmetic changed. What is NOT carried across is the linear source scan: `StatSet.IndexOfSource` is
O(sources) per add or remove (`a-engine.md`, `StatSet.cs:236-242`), which is fine at a handful of sources
and is not fine at eleven worn items with six affixes and six sockets each. The evaluator holds a
per-stat inverted index built as sources are added, so a dirty stat walks only the lines that touch it.

**A source changes on exactly five events**: equip, unequip, socket, unsocket, and a craft that rewrites a
worn item's payload. Nothing else dirties anything. In particular a tick does not, a movement does not,
and a condition changing state does not unless the game says so, which is #884 item 12's "recompute when a
source changes, never per tick" stated as a closed list rather than as an intention.

### 11.6 The algorithm and the API

Contracts 13.2's formula verbatim, with the steps this document owns marked:

```
1. Gather every line for this stat, in (SourceKind, Ordinal, InstanceId, ModifierIndex) order.   // 11.4
2. Drop every line whose tag scope is not a subset of context.Tags.                              // 11.3
3. Drop every line whose ConditionId is non-zero and evaluates false.                            // 11.3
4. flat      = Base + sum(Flat)                                  // long arithmetic
5. increased = 10000 + sum(IncreasedBasisPoints)
6. value     = floordiv(flat * increased + 5000, 10000)          // round half up, every sign
7. for each More m, in the step 1 order:
       value = floordiv(value * (10000 + m.Value) + 5000, 10000)
8. value     = clamp(checked int, stat.min, stat.max)
```

```csharp
public sealed class ContentStatEvaluator
{
    public ContentStatEvaluator(IContentSnapshot snapshot, IStatConditionRegistry? conditions = null);
    public void SetBase(int statId, int scaledValue);
    public void AddSource(in StatSourceKey key, ReadOnlySpan<StatModifierLine> lines, ReadOnlySpan<int> tags);
    public bool RemoveSource(in StatSourceKey key);
    public void ClearSources();
    public int Value(int statId, in StatContext context);
    public void CopyValuesTo(ReadOnlySpan<int> statIds, Span<int> destination, in StatContext context);
}
```

`AddSource` under an existing key REPLACES in place and keeps the original position, which is
`StatSet.AddSource`'s rule (`a-engine.md`, `StatSet.cs:139-143`) and is what makes an add-then-remove
cycle return the prior value EXACTLY. With integers that property is free rather than delicate, which is
the one place this evaluator is simpler than the float one it sits beside.

**Every division above is FLOOR division, and that is the contracts' rule rather than this document's
workaround.** Contracts 13.2 says it in as many words: `floordiv(x + 5000, 10000)` is round half up for
EVERY sign, while C# `/` truncates toward zero, which makes a debuff round differently from a buff of the
same size. An earlier draft of this section invented a sign-magnitude rule instead, computing the sign,
rounding the magnitude and restoring the sign, which is a second rounding rule in a system the contract
built to have one, and it was filed nowhere.

**The worked negative case, corrected.** That draft's example was `(-15000 + 5000) / 10000` "is 0 rather
than -1", which is wrong twice over: the numerator is `-10000`, and `-10000 / 10000` is exactly `-1` in
C# with nothing to truncate, so the example demonstrated nothing. The case that does demonstrate it is
`flat * increased = -14000`, a penalty of 1.4 scaled units:
`floordiv(-14000 + 5000, 10000) = floordiv(-9000, 10000) = -1`, the nearest integer with the tie going up,
where C# `(-9000) / 10000` gives `0` and reports NO penalty at all. Negative values are ordinary here,
because a `Flat` modifier may be negative and a stat's `min` may sit below zero.

The implementation is `Math.DivRem` with a negative-remainder adjustment, subtracting one from the
quotient when the remainder is non-zero and its sign differs from the divisor's, never `Math.Round`, which
takes a `double` and would put floating point on the determinism path contracts 13.4 exists to keep clear
of it. Test 17.7 pins the `-14000` case specifically, because a stat that can go negative (a resistance, a
cold damage penalty) is ordinary and the trap is silent.

**Section 6.4's roll formula is unaffected** and keeps its `/`: `position` is a `ushort` and `max - min`
is non-negative by the tier's own bounds, so its numerator is never negative and floor and truncation
agree on every input it can be given (contracts 13.2).

### 11.7 How the two consumers map onto it

**Grimhollow's `EquipStats(EquipSlot, int Accuracy, int Strength, int Defence, WeaponArchetype)`**
(`b-grimhollow.md:78-104`) is three ints on a hardcoded switch over nine item ids, marked provisional in
its own class doc. The mapping is direct and needs no new concept: three `stat` rows (`accuracy`,
`strength`, `defence`), `Scale = 1` because they are whole numbers today, `Min = 0`, `Max` the game's
ceiling, and each equipable base carries three `Flat` lines at source kind 1. `AttackTicks` maps to a
fourth stat whose `Min` is the fastest permitted speed, which turns the `WeaponArchetype` switch
(`Sword 14, Hatchet 18`) into two base rows. Nothing about Grimhollow needs `Increased`, `More`, a tag
scope or a condition on day one, and that is the point: the base carries them unused.

**Ruinborne's `item_stat(item_id, stat_id, flat, percent)`** (`c-ruinborne.md:926-948`) is a per
DEFINITION row with two float-ish columns, so two copies of an item are identical. The mapping is one
`stat` row per existing `stat_id` with `Scale = 100` (its percent column is a fraction today and the scale
is what makes it an integer), one `Flat` line and one `Increased` line per `item_stat` row, both at source
kind 1. Its `percent` becomes `Increased` rather than `More` because today it is summed into one
multiplier exactly as `StatSet` does, so `Increased` preserves the numbers and `More` would not.
`item_ability_modifier`'s comma-joined tag STRINGS (`c-ruinborne.md:67-72`) become a tag scope, which is
the one place adopting this evaluator makes a Ruinborne substring search into a set membership test.

## 12. Validation, quarantine and visibility

### 12.1 Three outcomes, no fourth

Contracts 10.1 fixes them: Valid, Remapped, Quarantined, and in particular there is no "dropped". This
section is the engine-side machinery for the third, plus the one visibility function, which sits here
rather than in section 7 because a tooltip is not a network concern.

### 12.2 The instance validator's checks

`InstanceValidator.Validate(page, snapshot)` accumulates and never throws, following contracts 10.4's
shape and `JsonSchemaValidator.ValidationReport`'s run-to-the-end sweep
(`KhaozEngine.Content/JsonSchemaValidator.cs:11-101`). It is side effect free: it does not log, does not
touch a counter and does not mutate the page. The caller does all three.

| # | Check | Class | Failure | Outcome |
|---|---|---|---|---|
| 1 | the payload is canonical: ascending kinds, no duplicate, minimal varints | structural | `field-order`, `field-duplicate`, `varint-nonminimal` | quarantine |
| 2 | every declared length lies inside the payload | structural | `truncated` | quarantine |
| 3 | a registered kind's bytes decode through its codec | structural | `field-malformed` | quarantine |
| 4 | kind 132's nested payloads carry no kind 132 | structural | `socket-nesting` | quarantine |
| 5 | the payload is at most `MaxInstancePayloadBytes` | structural | `payload-oversize` | quarantine |
| 6 | the entry's definition id, and every socket's `ContainedDefinitionId` at every depth, resolves in the active version | drift | `unknown-definition` | quarantine |
| 7 | every content id the registry's reference targets name resolves, at every depth (3.3) | drift | `unknown-content-reference` | quarantine |
| 8 | a `(mod id, tier ordinal)` pair names a live tier | drift | `unknown-content-reference` | quarantine |
| 9 | a non-empty payload has a non-zero instance id | structural | `instance-id-missing` | quarantine |
| 10 | the instance id is unique within the page | structural | `instance-id-duplicate` | quarantine |
| 11 | an entry carrying kind 5 or 132 has count 1 | structural | `stack-not-instanceable` | quarantine |
| 12 | the entry's count is at most the definition's cap, OR the over-cap shrink rule applies | policy | `over-cap` | tolerated |
| 13 | the entry's definition id, and every socket's `ContainedDefinitionId`, is not RETIRED in the active version | policy | `definition-retired` | retired |

**Checks 6 and 7 are DERIVED from the registry rather than from a list in this section.** They walk the
same `InstanceReferenceTarget` descriptors the remap pass walks (3.3), in the same recursive order, over
the same nested payloads, so a kind cannot be remapped-but-not-validated or validated-but-not-remapped.
An earlier draft wrote check 7 as "every mod id, socket type id, rarity id, template id and word id
resolves", a closed enumeration that already omitted kind 7's material ids and a socket's
`ContainedDefinitionId`, and that would have omitted every game kind at or above 1,024 forever. Check 8
stays hand-written because a tier ordinal is not a content id: it is a key INTO the row check 7 already
resolved, so it is the one relationship the descriptors cannot express.

### 12.3 What each failure does

**Contracts 10.1 owns the outcome vocabulary and this section only says which check reaches which
outcome.** Valid, Remapped, Quarantined, there is no fourth, and nothing here invents a middle state.

**A structural failure quarantines that ENTRY.** The item's bytes cannot be trusted to mean what they say,
so nothing reads them again until someone looks.

**An UNRESOLVED CONTENT REFERENCE also quarantines, and that is the contracts' call rather than this
document's.** Checks 6, 7 and 8 fail because CONTENT moved, which is what remap rules exist to handle, so
the loader's order matters: rules apply first (5.5 step 2) and the validator runs after, so a drift
finding means NO RULE COVERED IT. Contracts 10.2 names `unknown-definition` and
`unknown-content-reference` as QUARANTINE reason codes, and contracts 10.1 says a record that "did not
resolve" is Quarantined. An earlier draft of this section counted and tolerated checks 7 and 8, keeping
the field verbatim, contributing nothing to the evaluator and rendering a placeholder line while the item
stayed equipped, TRADABLE and craftable. That is a fourth outcome under a reason code the contract
assigned to the third. The argument for it, and the reason it was rejected, are recorded in appendix A
under F2.

**The consequence is worth stating rather than hiding: one forgotten remap rule quarantines every item
carrying that mod.** That is a larger blast radius than a placeholder line, and it is the trade the
contract chose deliberately. A quarantined item is VISIBLY broken and recoverable in full, because the
bytes are kept verbatim (12.4) and the first load after the missing rule publishes re-validates and
restores the item exactly. A tolerated one is INVISIBLY wrong and tradable, which is worse in the one
direction that matters: the player who sells it has already been paid when the rule lands. The first line
of defence is not this validator anyway, it is the publish validator's own retire-and-remap checks
(contracts 10.4), which are what stop the missing rule from ever shipping.

**A RETIRED definition is a thirteenth CHECK and not a fourth outcome.** Contracts 8.2's kind 2 policy
`0x01` keeps a retired id as it stands and displays the item "through a placeholder. It is not usable, not
tradable and not droppable", explicitly the same presentation as quarantine. A retired row stays in the
pack forever (contracts 5.1), so check 6 RESOLVES it and the item would otherwise be Valid and fully
usable, which is precisely the behaviour Scope A deleted a game mechanism in order to inherit: its 3.9
argues the engine needs no tradable rule for a retired item because "the placeholder presentation of
contracts 10.2 makes it undroppable and untradeable anyway". Nothing was building it. Check 13 is that
thing. It reads the active snapshot's `IsRetired(id)` and produces the `Retired` outcome, which is NOT a
quarantine: the bytes are not wrapped, the entry still decodes, the page still loads, and what changes is
the PRESENTATION and the refusals that come with it. Under contracts 10.1 the record is Valid or Remapped
as its ids say, with a presentation the content itself asked for, which is why this is a check with a
policy rather than a fourth outcome. It increments no counter and adds no log category, because contracts
10.2's counter is for quarantined records and this document does not invent a second one: the finding
rides in the validation report the caller already reads, and Scope A's publish diff is where a retire is
visible in the first place.

**Check 12 is the one tolerated failure and it is tolerated BY CONTRACT.** Contracts 8.2 kind 4's over-cap
stack rule is legal, may only shrink and is self-healing, so an over-cap count is a state the contract
declares valid rather than a drift the validator caught. It is counted and it changes nothing.

**Three placeholder presentations are player facing, so all three are `StringId`s and none is a
literal**, which is AGENTS.md's founding rule and contracts 12.1's derivation applied to the one part of
this document that renders text. The keys are engine owned and fixed:

| Key | Presented when |
|---|---|
| `khaoz.item.quarantined` | an entry failed a check and carries a `KECQ` wrapper (12.4) |
| `khaoz.item.retired` | check 13's `Retired` outcome, the contracts 8.2 kind 2 placeholder |
| `khaoz.item.unidentified` | a gated kind is hidden from the viewer by 12.7's mechanic |

All three resolve through `ContentStringCatalog`, Scope A's implementation of contracts 12.4's layered
`IStringCatalog`, which reads the pack's text chunks first, then the game's own resx, then the key itself,
and formats through the `SafeFormat` path contracts 12.3 adopts so a translator's malformed template falls
back rather than throwing inside the frame loop. They are prefixed `khaoz.` deliberately: they are ENGINE
strings rather than content rows, so contracts 12.1's derived `<type key>.<content key>.<field>` grammar
does not name them and the prefix keeps them out of its space. The engine ships the keys and NO
translation, exactly as it ships no mod row. A reason code and a stamped version are NOT player text and
are never formatted into these strings, they belong in the log line of 12.6.

Quarantine is still a strictly softer answer than the one Grimhollow has today, which is why section 18
row 4 is early in its adoption plan. `ValidateContainer` THROWS on an unknown item id
(`GrimhollowJournalContracts.cs:483-524`), turning one bad id into a player who cannot log in at all
(`b-grimhollow.md:545-556`). The same bad id under this validator is one placeholder item in an otherwise
working bag, on a stream that still loads.

### 12.4 The quarantine wrapper, `KECQ`

Contracts 10.2 requires the bytes be kept VERBATIM with a reason code and the stamped version, nothing
truncated, normalized or re-encoded. The wrapper replaces the entry's payload in the page and the entry's
`EntryFlags` bit 0 is set (4.4), which is how a reader knows without sniffing.

```
[Magic: 4 bytes 'K','E','C','Q']   // 0x4B 0x45 0x43 0x51
[Version: uint16 LE]               // 1
[ReasonCode: byte]                 // an ordinal from the closed set of 12.2
[StampedVersion: varint int32]     // the page stamp the record failed under
[OriginalLength: varint int32]
[Original: OriginalLength bytes]   // verbatim, never re-encoded
```

**A magic here and none on a payload, and the two are consistent.** Contracts 15 forbids a magic on a
format always embedded in a larger versioned record. A payload is such a format. A quarantine wrapper is
NOT: it is a payload-shaped field that must be distinguishable from a payload at a glance by a human
reading a hex dump of a page, and by a tool that never saw the entry flag. Four bytes for that, once per
quarantined entry, on a path that is by definition rare.

**The wrapper is itself durable, so it is versioned** (contracts 10.5's closing note). Version 1 is the
only version and a decoder refuses anything else rather than guessing.

**`OriginalLength` may exceed `MaxInstancePayloadBytes`**, because `payload-oversize` is a reason and
refusing to wrap the thing that failed for being too big would destroy exactly the item the wrapper
exists to keep. The page's own entry length check is what bounds it, at the 2 MiB section cap.

### 12.5 The one visibility function

```csharp
public static bool CanSee(ushort kind, PropertyVisibility viewerLevel, bool identified, uint revealedMask);
public static int  PublicView(ReadOnlySpan<byte> payload, PropertyVisibility level, bool identified,
                              uint revealedMask, Span<byte> destination);
```

`CanSee` is called by the replication filter (7.4) and by the tooltip builder and by NOTHING else, which
is contracts 11.2's rule stated as a call-site constraint. `PublicView` is the forward pass over retained
runs described in 7.4, returning the written length. Both are pure and static, so a test calls them with
no server.

The rule, in order: `ServerOnly` is never visible to anyone. `OwnerOnly` is visible when the viewer level
is `OwnerOnly`. `Everyone` is visible always. Then, and only then, the identification gate: a kind marked
an `identificationMaskBit` at registration (3.3) is hidden when `identified` is false and its bit in
`revealedMask` is clear, EVEN FROM THE OWNER. That last clause is gate 0 decision 8 and it is why
unidentified is a mechanic rather than a fourth level.

### 12.6 The counter and the log line

Contracts 10.2 names both and forbids inventing others. Counter
`khaoz.content.quarantined_records`, dimensioned by content type id and reason code. One log line under
category `ContentValidation` at Warning, naming the reason code, the stamped version, the active version
and the owning stream key, and NEVER the payload bytes or a raw account id
(`DURABLE-PLAYER-JOURNAL-DESIGN-2026-09-06.md:657-660`).

**One line per PAGE, not per entry.** A page that fails wholesale would otherwise emit a hundred identical
lines, which is how an operator learns to filter the category out. The line names the page, the reason
code with the highest count, and the counts. The counter is still incremented per record, because a
counter is what a dashboard reads and a log line is what a human reads.

### 12.7 Unidentified, built on OwnerOnly

Kind 128 is `[State: byte][RevealedMask: varint uint32]` (3.3). State 0 is unidentified. `RevealedMask`
bit N is the bit a kind was REGISTERED with, `identificationMaskBit` (3.3), never the kind's position in
the ascending list of gated kinds. Four kinds are gated in v1, at bits 0, 1, 2 and 3 for kinds 129, 131,
133 and 134, so bits 4 to 31 are unassigned and zero. The registered bit is the only shape that survives
an engine release: the mask is durable and the registration set is not, so a derived index is a number
stored under one meaning and read under another.

The mechanic: an unidentified item replicates and tooltips WITHOUT its gated kinds, to everyone including
its owner, and what a viewer sees in their place is `khaoz.item.unidentified` through
`ContentStringCatalog` (12.3), never a hardcoded string and never a blank line. The `Identify` primitive (10.2) sets state 1 and the mask to all ones. A partial reveal is
already expressible: a primitive parameterised to set one bit reveals one affix, which is an authoring
choice rather than an engine one and needs no format change.

**The unidentified item still STACKS by byte equality**, and two unidentified items with different hidden
affixes have different bytes, so they do not merge. That is the correct answer and it leaks one bit: a
player who tries to stack two unidentified items learns whether they are identical. The leak is inherent
to stacking by bytes, it is worth less than the hidden rolls are, and 15.8 records it as accepted with the
alternative priced.

## 13. Failure modes and recovery

Every row is a failure this design can actually reach. Row numbers are referenced from sections 3 to 7
and are stable.

| # | Failure | Detection | Effect | Recovery |
|---|---|---|---|---|
| 1 | A payload fails to decode | `TryDecode` returns false with a closed reason (3.2) | that ENTRY is quarantined, the rest of the page loads | the bytes are kept in a `KECQ` wrapper (12.4), an operator reads the reason from the counter dimension, a fix is a content publish or a support edit |
| 2 | A page's stamp is NEWER than the active version, after a content rollback | `stamp > snapshot.Version` at load (5.5) | NOT an error. Entries that resolve are used, entries that do not are quarantined per 12.2, the stamp is never lowered | the page is already correct when the newer version returns. Nothing is written, so the rollback is reversible |
| 3 | Crash between admission and commit of a craft | none needed, the process died | the whole batch dies. Pages revert to their last committed bytes. Instance ids the batch allocated are skipped forever (3.6) | a client operation resubmits by its id and resolves `NotFound`, then applies fresh (6.6) |
| 4 | Crash mid page rewrite | none needed | impossible to observe. A projection write is a whole section replacement inside the store's transaction (`JournalProjectionWrite.cs:10-29`), so a page is entirely old or entirely new | none required. This row exists to record that there is no torn page to repair |
| 5 | A socket references a socket type that resolves to nothing | validator check 7 (12.2) | the ENTRY quarantines, bytes kept verbatim, the item is unusable and untradeable until a rule lands (12.3) | a `ReplacedBy` rule (contracts 8.2 kind 1) points it at a live type. The next load re-validates and the item returns intact, because nothing was rewritten |
| 6 | A mod is retired with no remap rule | validator check 7 or 8 | the ENTRY quarantines. Every item carrying that mod quarantines, which is the blast radius 12.3 names | the author publishes the missing rule. Every page is re-checked on its next load, so there is no rewrite pass and no support edit. The publish validator (contracts 10.4) is what should have caught it first |
| 7 | An instance id collides after a restore from backup | the allocator's persisted `store_epoch` does not match the live one, so it refuses to issue (3.6). The retired node list cannot detect this, because a restore rolls the list back with everything else | the first allocation fails closed rather than issuing a colliding id, so the server refuses to create items until an operator acts | the 15.2 procedure, in order: quiesce, restore, rotate the store epoch, rotate the node id, reopen. Ids issued after the backup point and lost by it are never reissued |
| 8 | A page exceeds the journal's section cap | arithmetically impossible: 100 entries at the maximum size is 53,209 bytes against 2 MiB (5.4) | none | the row exists so the 2.5 percent margin is written down. A page geometry change reruns the arithmetic |
| 9 | A client never acknowledges a page sync | no acknowledgement exists, deliberately | nothing. `ReliableOrdered` means delivery or a dead connection (7.5 rule 1), and a partial assembly dies with the connection (rule 4) | the client re-requests on rejoin, at two bytes (7.6). Adding an acknowledgement would build a second reliability layer over a reliable channel |
| 10 | A page is written into the wrong section | the decoder's `FirstSlot == PageIndex * expectedPageSlots` check (4.4) | the page fails to decode and is quarantined as a unit | the redundant two bytes are what make this loud instead of silent. Recovery is the journal's, from the event tail |
| 12 | A page delta names more changes than one game message holds | the builder measures the encoded size as it writes and stops before the cap (7.5) | the delta is abandoned before it is encoded and the page is sent through the fragmenter instead, so nothing reaches `EncodeGameMessage` above the cap | none needed. The row exists because the failure it prevents is a THROW INSIDE THE PER-VIEWER SERVE LOOP, which the combat path already paid for once (`TileWorldServer.Tick.cs:236-247`) |
| 11 | Ground item payloads dominate a viewer's bandwidth | budget 11 of section 16: public view bytes times instances in interest divided by the tick length (7.4) | no throw and no overflow. A snapshot frame carries no cap (`TileProtocol.Frames.cs:112-125`), so the effect is bytes per viewer per second, repeated every tick for the life of the drop | a per-cell payload byte budget on `SpawnGroundItem` (open question 6), or moving the sibling component onto the AoI delta path. `AoiDeltaReplicator` exists in `KhaozEngine.Replication` and the tile serve does not use it (7.4) |

**Row 12 is the only row whose CURRENT behaviour would be worse than its recovery**, and it is worth
naming as the one place this design puts new pressure on an existing throw. The combat path already
learned the lesson and its comment says so in as many words, that the throw was inside the serve loop and
cost every player on the server rather than the one viewer (`TileWorldServer.Tick.cs:236-247`). The page
delta puts the same pressure on the same encoder, which is why 7.5 makes the size test part of BUILDING
the message rather than a rule an implementer is trusted to remember. **Row 11 was written as that same
throw in an earlier draft and is not one**, because a snapshot frame is not capped (7.6). It is a
bandwidth row, and its answer is a budget rather than a catch.

## 14. Versioning and rollback

Four version numbers are in play and they move independently. Confusing two of them is the most likely
reading error in this document, so they are tabulated.

| Version | Where | Moves when | Read by |
|---|---|---|---|
| Container codec version | byte 0 of a page (4.4) | the page BYTE LAYOUT changes | the page decoder |
| Payload field kinds | the registry (3.3) | a kind is added | the field decoders, additively |
| Content version | the page stamp (5.3) | the owner publishes | the remap decision (5.5) |
| Quarantine wrapper version | `KECQ` (12.4) | the wrapper layout changes | the wrapper decoder |

**A fifth number is NOT in that table and is worth naming for that reason: `ContentPackFormat.Generation`,
the engine's own pack format generation** (contracts 7.4). Scope A owns it and bumps it "ONLY when an
engine-owned row codec or the pack format gains a field an older reader cannot skip", and old readers that
meet a higher generation FAIL CLOSED rather than skipping (contracts 8.5). None of the four versions above
is it: a container page, a payload and a `KECQ` wrapper are durable formats that live in the JOURNAL
rather than in the pack, and their forward compatibility is carried by the tagged encoding and the byte-0
dispatch instead. What DOES reach the pack from this document is a new REMAP RULE KIND, which is
pack-carried, must fail closed on an old reader, and is exactly what contracts 8.5 says the generation is
for. So: **if Scope B ever introduces a remap rule kind, or any other pack-carried format change, Scope A
bumps `ContentPackFormat.Generation` for it.** Scope B does not have its own generation number and does
not want one. v1 introduces no rule kind, so nothing bumps.

**A content rollback does nothing to stored instances.** Pages are not rewritten, stamps are not lowered,
and a page stamped past the active version is row 2 of section 13: entries that still resolve are used and
entries that do not are quarantined until the version returns. That is the property gate 0 decision 5 buys
by making the stamp a comparable NUMBER rather than a hash, and it is why a rollback is an operational
action rather than a data migration.

**A keep-legacy republish is append-only in every direction.** A new mod id appears, the original id keeps
its id and gains new ranges, one `MovedToLegacy` rule is appended to the global sequence (contracts 8.1),
and every stored page moves lazily on its next load. Nothing is rewritten eagerly, nothing is deleted, and
rolling the publish BACK leaves pages that already moved pointing at a legacy id the rolled-back version
still contains, because a retired row stays in the pack forever (contracts 5.1). So the rollback of a
keep-legacy publish is safe in one direction and lossy in the other: the items that moved stay moved. That
asymmetry is contracts 8.6's irreversibility rule, and it is the reason keep-legacy is the non-default.

## 15. Security and exploit analysis

The threat is a player with a modified client and unlimited patience, plus an operator who makes a
mistake. Each subsection names the attack, the mitigation and the test that proves the mitigation.

### 15.1 Duplication

Four doors, and the journal closes three of them before this document starts.

- **Replay of a craft against a refilled slot.** The normalized intent carries the target's INSTANCE ID
  (10.6), so the same operation id against a different item hashes differently and resolves
  `OperationConflict` rather than applying twice. Without that field the intent is (currency, slot) and a
  resubmit after the slot refilled would apply to the new item. Test: 17.10, a replay with the slot
  refilled, asserting `OperationConflict` and an untouched page.
- **Crash between admission and commit.** Nothing is written, so nothing is duplicated (13, row 3). The
  ids the batch allocated are burned. Test: the existing `--journal-crash-probe` harness
  (`KhaozEngine.Benchmarks/README.md:238-243`) extended with an item batch, asserting the page bytes and
  the allocator high-water mark after the kill.
- **A drop and a reclaim.** A claim MOVES the instance id (7.3) and never mints one, and the dropper is
  not special cased, so a drop-and-claim cycle produces the same item rather than a second one. Test:
  17.11, drop then claim by the dropper, asserting the instance id is unchanged.
- **Player trading, later.** Not built (1.3). The shape is one commit across two player streams with
  `PresentAtCommit` (`JournalCommit.cs:29`, `:56-62`), which is atomic by the store's transaction. Section
  18 carries it as the adoption item that makes the flag required.

### 15.2 Rollback and restore

A restore from backup rewinds pages and can therefore restore an item that was consumed, which is
duplication by administration rather than by exploit. The engine cannot prevent it and does not pretend
to. What it does is make the aftermath survivable, and the guard has to be one the restore cannot also
rewind: the allocator's persisted state carries the `store_epoch` it was written under and refuses to
issue when the live epoch differs (3.6). A restored allocator therefore STOPS rather than reissuing, and
starting it again is an explicit operator act.

**The procedure, in order, and every step is load bearing:**

1. **Quiesce.** Stop every writer against the database being restored. An allocator still running on the
   old process is the one thing an epoch cannot tell apart, because it holds its state in memory.
2. **Restore** the point-in-time backup.
3. **Rotate the store epoch** through `IMutationJournalMaintenance.RotateStoreEpochAsync`, which the
   journal's own runbook already requires of any point-in-time restore
   (`DURABLE-PLAYER-JOURNAL-DESIGN-2026-09-06.md` section 10). This is the step that makes the restored
   allocator state refuse, and skipping it is what the whole guard exists to catch.
4. **Rotate the node id** with `Rotate(ushort)`, which appends the old node id to the retired list and
   writes the new node id and the LIVE epoch together.
5. **Reopen** to writers.

**What that buys, stated exactly rather than optimistically.** Every id issued after the rotate comes from
a node id no pre-restore item can carry, so a post-rotate item and a pre-restore item can never share an
id. What it does not undo is the window the restore itself opened: ids issued between the backup point and
the restore were handed to real items, those items are gone from the store, and copies of them may survive
in a client cache, on another shard, on another host's region stream or in an operator's export. Those ids
are never reissued, because the counter never goes backwards past the rotate, so a surviving copy resolves
to nothing rather than to a DIFFERENT item, which is the property that matters. That is weaker than the
earlier draft's claim that "the restored world and the lost one can never name the same item", and it is
the claim the mechanism actually supports. Test: 17.12, `Rotate` then an allocation, asserting no overlap
with the pre-rotation range, that a boot on a retired node id throws, and that an allocator whose
persisted epoch differs from the live one refuses to issue.

### 15.3 Payload tampering

**The invariant: the server never accepts instance BYTES from a client, only OPERATIONS.** Every
client-to-server message in this design names items by slot and by id (7.6), and there is no message
anywhere carrying a payload in that direction. A payload is produced by the generator, by a craft
primitive, or by a decode of the server's own durable bytes, and by nothing else.

That makes the whole class of "craft a payload, send it, get the item" unreachable rather than mitigated,
which is worth more than any validation. Test: 17.13, an architecture test asserting that no
client-to-server message route in the tile netcode carries a payload-shaped field, in the spirit of
`ArchitectureTests.NoTwoShaderToolchains` (AGENTS.md).

### 15.4 Stacking through unknown fields

Two items stack when their payload bytes are equal (4.6). An attacker who could make the server FORGET a
field could make two different items compare equal and merge, destroying one and keeping its count. That
is why `InstancePropertyRegistry` permits no unregistration and no codec replacement (3.3): forgetting a
kind is the attack, and the registry makes it impossible rather than unlikely.

The remaining surface is a DEPLOY mismatch: a server built without a kind another server wrote. Its
decoder keeps the unknown field verbatim (contracts 9.4) and byte equality still sees it, so the two items
still do not merge. Preserving unknown fields is therefore a security property as well as a compatibility
one. Test: 17.2's round trip through a registry missing a kind, extended to assert the two items do not
merge.

### 15.5 Roll prediction

Both generators the engine ships today are deliberately predictable: `DeterministicRng` publishes its full
`State` for save and resume (`DeterministicRng.cs:29-33`) and `TileActorRandom.For(seed, netId, tick)`
derives every draw from three values a client can observe. Grimhollow's four production seeds are integer
literals with a comment beside them saying they must not survive to a shared server
(`b-grimhollow.md:786-836`, https://github.com/APKiwiOrg/Grimhollow/issues/214).

The mitigation is contracts 14: `IRandomSource` with no readable seed and no readable state, the
cryptographic implementation on hosted servers with rejection sampling rather than modulo, and gate 0
decision 11's Warning line on every boot a hosted server runs the seeded source. What this document adds
is that the generator and every craft operation take the source in their CONSTRUCTOR (9.1, 10.5), so
"does this object roll, and on what" is answerable by reading the composition root rather than by tracing
every call site. A seeded generator is a different object from the production one, which is what makes
gate 0 decision 11's boot Warning able to see it at all.

**Modulo bias is the subtle half and it is farmable.** A weighted pick with modulo over a weight total
that does not divide the generator's range biases the low candidates by a fraction a patient player can
measure over a million drops. Rejection sampling removes it. Test: 17.6's distribution test run against
both sources, asserting the cryptographic one is within bound and asserting the seeded one is deterministic
rather than unbiased, which is the honest assertion for it.

### 15.6 Socket nesting and oversize denial of service

A payload that could nest without limit is a decompression bomb: a few hundred bytes expanding into
unbounded decoder recursion. The decoder enforces ONE LEVEL structurally (3.5), returning `socket-nesting`
rather than recursing, so the depth is a constant rather than a budget. `MaxInstancePayloadBytes = 512`
bounds the breadth, `SetSlotAt` refuses an over-cap payload (4.7), and the ground spawn overload throws on
one (7.3).

Neither limit is reachable from a client anyway, by 15.3, so this is defence against a bad content publish
and a game bug rather than against a player. Test: 17.3's decoder fuzzing, which asserts no input ever
throws, never recurses past one level and never allocates past the cap.

### 15.7 A race across two containers

Moving an item from a bag to a bank touches two pages, and two operations racing could in principle read
the same source slot twice and write it into two destinations. The journal closes it and this document
only has to not reopen it: the admitted layer serializes per stream, an operation is sent to the store only
when it is at the head of EVERY one of its queues (`JournalAdmittedState.cs:82-88`), and a cross-page move
is ONE commit with two projection writes on one stream (5.6). Grimhollow adds a validation ticket order so
two clicks whose loads finish out of order cannot be planned against stale state
(`b-grimhollow.md:618-639`).

The one thing this document adds is a refusal: a batch CLOSES rather than widening when an operation would
touch a second stream (6.4, rule 2), so a cross-account move can never be folded into an unrelated batch.
Test: 17.14, two concurrent moves of one stack, asserting exactly one succeeds and the other is refused.

### 15.8 Legacy mods, and the visibility leak surface

**A legacy mod that can never be generated is a feature, and it is also an audit tool.** An item carrying
one provably predates the publish that created it, which is how a support question about a suspiciously
good item is answered. Two standing rules keep that true and a currency can opt out of neither (10.3):
nothing ADDS a legacy mod, and nothing REWRITES an affix entry that already names one. The second is what
closes the reroll farm, and without it the audit tool would have been the exploit: a currency whose only
step is `RerollValues(ByModId <legacy id>)` draws a fresh position against the preserved range every time
it is applied, so patience alone walks the roll to the top of a range no live mod has.

**The visibility leak surface is three holes, two closed and one accepted.** A tooltip computing its own
answer is closed by there being one function both paths call (12.5) and one test asserting they agree
(17.9). A component serialized once and sent to everyone is closed by the public view being computed
before the writer (7.4), so the owner-only bytes are never in the shared capture at all. The accepted one
is 12.7's: stacking by byte equality tells a player whether two unidentified items are identical. Closing
it means excluding gated kinds from the stacking compare, which would make two genuinely different items
merge and destroy one, so the leak is strictly cheaper than its fix.

## 16. Performance budgets

The owner's six from #884 item 13, plus four this document's design introduces. Every target is derived
from arithmetic already in this document or from the recorded journal baseline (698 commits per second at
1,000 players with no backpressure recorded, 42,365 bytes allocated per operation, `a-engine.md:607-641`).
**That 698 is an OFFERED load that the store kept up with, not a ceiling it hit**, so it is a starting
point for budget 13 rather than a limit anything here is measured against. Nothing in this table is
measured yet, which is what the last column says.

| # | Budget | Target | How measured | Measured |
|---|---|---|---|---|
| 1 | Bytes per rare item, payload | at most 80 | encode the 3.8 PoE row, count bytes | TBD (stage 5) |
| 2 | Bytes per rare item, slot entry | at most 96 | the same, through 4.4 | TBD (stage 5) |
| 3 | Page commit size, 100 rares | at most 8 KB | encode a full page, count bytes | TBD (stage 5) |
| 4 | Write volume, 20 crafts in one held action | at most 20 KB and 1 commit | `--items` bench, sum `JournalCommit.OwnedByteCount` | TBD (stage 5) |
| 5 | Rare generation time | under 20 microseconds per item | `--items` bench, 1M generations, report p50 and p99 | TBD (stage 5) |
| 6 | Stat evaluation per attack | under 2 microseconds, 0 bytes allocated | evaluate one stat over 11 worn items with 6 affixes each | TBD (stage 5) |
| 7 | Container page sync size, cold open | at most 8 KB and 8 frames per page | encode and fragment a full page (7.5) | TBD (stage 5) |
| 8 | Steady-state sync after one craft | 1 frame, at most 96 bytes | the delta of 7.5 | TBD (stage 5) |
| 9 | Generator table build at 2,000 mods | under 500 ms, under 40 MB resident | build the 9.2 tables from a synthetic pack | TBD (stage 5) |
| 10 | Container load, 10 pages with a full remap pass | under 5 ms, under 200 KB allocated | `Load` over 10 pages and 200 rules | TBD (stage 5) |
| 11 | Ground instance bytes per viewer per second | at most 8 KB per second per viewer at 28 ground instances in interest | public view bytes times instances in interest divided by `TickSeconds` (7.4) | TBD (stage 5) |
| 12 | Resident page bytes at 1,000 logged-in players | under 250 MB | sum the decoded page bytes plus the admitted layer's two dictionaries, at a 1,000 stack bank each | TBD (stage 5) |
| 13 | Commits per second offered, 1,000 players at 4 Hz | 4,000 per second pre-coalescing, and what the store sustains is the number to find | `--items` bench against SQLite and SQL Server, offered load against accepted | TBD (stage 5) |

Where each number comes from, because a target with no derivation is a guess in a table:

- **1 and 2** are 3.8's 58 and 69 with headroom for one more field. The deepest expressible item is 410
  bytes (3.8), so these are TYPICAL budgets and the 512 cap is the ceiling.
- **3 and 7** are 5.4's 6.9 KB page plus header and fragmentation overhead. Eight frames because
  `ceil(6900 / 1015)` is seven and one is spare (7.5).
- **4** is 6.7's arithmetic with the right event size: one 6.9 KB page plus twenty `item-crafted` events
  at about 127 bytes (10.6) is 9.4 KB. The 20 KB target is what covers the two-page case as well, because
  a craft that consumes a currency from a second page writes both, at `13.8 + 2.5 = 16.3` KB. An earlier
  draft targeted 16 KB off an 8.6 KB derivation that used the `item-generated` event size, which would
  have made the two-page case a failing budget for the wrong reason.
- **5** is twenty draws and six passes over a few-hundred-entry array (9.4), which is hundreds of
  nanoseconds of real work, budgeted at 20 microseconds so the answer is about the ALLOCATION and the
  memo, not about the arithmetic.
- **6** is the one that must be checked hardest. Eleven worn items times up to thirteen lines each is about
  140 lines, folded through 11.6's eight steps. Two microseconds is generous, and zero allocation is the
  binding half: an evaluation that allocates per attack is an evaluation that runs per attack.
- **9** is 9.2's 1.7 million appends: 20 MB of tag-band tables plus a 9.8 MB memo is about 30 MB, and the
  budget adds the build's transient arrays on top.
- **10** is 5.5's one pass over 10 pages with 200 rules, each rule a no-op on a page holding no reference.
- **11** is the one the design introduces rather than inherits, and it is the cost of the tile serve being
  full state (7.4). A rare's PUBLIC view is its 58 byte payload less the four bytes of `OwnerOnly`
  durability, so 54, and the component adds its instance id varint and a payload length, so about 60 bytes
  on the wire per ground instance. A 28 slot bag dropped into one cell is `28 * 60 = 1,680` bytes in every
  snapshot that cell is in, and the tile tick is 250 ms, so one viewer standing there receives about
  6.7 KB per second of REPEATED bytes, for the whole 300 second despawn window. Ten viewers in that cell
  is 67 KB per second of the server's egress for one death pile. The budget is per viewer because that is
  the number an operator can act on.
- **13** is the steady state rather than the burst, which is the case 6.7 never priced. The batch window
  is one server tick (6.4), so an active player offers at most one commit per tick, and the tile tick is
  250 ms, so 1,000 players acting every tick offer 4,000 commits per second BEFORE coalescing, which is
  the number a batch exists to reduce. The recorded SQLite baseline is 698.46 completed operations per
  second at 1,000 players with ZERO backpressure recorded (`a-engine.md:607-641`), which makes it an
  observed offered load rather than a measured ceiling, and this document cites it that way. SQL Server is
  unmeasured. Finding the real ceiling on both is what this budget is for.
- **12** is test 5's "several million instances in memory" given a number. A 1,000 stack bank is about
  70 KB of page bytes (5.4), so 1,000 logged-in players is about 70 MB before anything copies it.
  `JournalLimits` clones every byte array on the way in and again on EVERY property read
  (`JournalLimits.cs:107`, `:137`), and the admitted layer holds a committed and an uncommitted section
  dictionary per stream, so the resident figure is two to three copies of that 70 MB. 250 MB is the
  ceiling that covers three copies with headroom, and it is the budget that says whether "millions of
  owned items" is a memory problem or a disk one.

**What a consumer does on `Backpressure`, because this document has not said it anywhere else.** The
admitted queue is per stream at a default depth of 8 (`JournalExecutorOptions.cs:14`), and past it
`JournalAdmittedState.Refusal` answers `Backpressure` (`JournalAdmittedState.cs:41-50`), which is a
REFUSED action rather than a queued one. The consumer's answer is the refusal path it already has for a
guard failure (10.6): nothing durable is written, the working copy is discarded, and the client is told
the action did not happen, with a resync rather than a retry. It must NOT auto-retry, because a retry
under backpressure is what turns a queue at depth into a queue that never drains, and it must not present
optimistically, because the operation never reached the store. A game that wants a held craft to survive
a burst throttles at the INPUT, one craft per tick per player, which is the window the batch already
uses.

**Every one of these lands in `KhaozEngine.Benchmarks` as an `--items` mode with a checked-in baseline**
(2.3), following the journal set's shape exactly, so the numbers are reproducible and a regression is a
diff rather than an opinion. The 42,365 bytes per journal operation is the number to watch on budgets 4
and 10: every journal byte array is cloned on write and again on read (`JournalLimits.cs:107, 137`), so a
page that doubles in size doubles two copies and not one.

## 17. Test plan

Numbered so the sections above can cite a test rather than describe one, and a reference of the form
`17.N` anywhere in this document means ROW N of the table below. Homes are 2.3's four projects.

| # | Test | Home | What it pins |
|---|---|---|---|
| 1 | Golden payload and page files | `ItemInstances.Tests` | contracts 9.8's 45 bytes SORTED (3.7), the four 3.8 rows, one full 100 rare page, one quarantined entry |
| 2 | Unknown-kind round trip | `ItemInstances.Tests` | a decoder whose registry omits a kind the encoder wrote reproduces the input byte for byte, and the two items do not merge (15.4) |
| 3 | Decoder fuzzing | `ItemInstances.Tests` | mutation over the goldens (bit flips, truncations, length lies, kind swaps): NEVER throws, reasons are stable per mutation class, no recursion past one level |
| 4 | Cross-version round trips | `Foundation.Tests` | a version 1 container blob read by the version 2 reader, seated at instance id 0 with an empty payload, then written back as version 2 (4.5) |
| 5 | Scale | `Benchmarks` plus a structural test in `Server.Tests` | a bank of 1,000 affixed stacks, several million instances in memory against budget 12, 20 crafts in one batch, mirroring `MutationJournalBenchmarkTests` |
| 6 | Generator distribution | `ItemInstances.Tests` | weights proportional within an integer bound, positions uniform and both ends reachable, and draw count a function of the affix count including an item whose pool empties mid roll (9.3, 9.4 step 8) |
| 7 | Evaluator determinism | `ItemInstances.Tests` | the `More` fold order changes the answer and the stated order is stable, add-then-remove restores exactly, and floor division rounds `-14000` to `-1` where C# `/` gives 0 (11.6) |
| 8 | Remap idempotence | `ItemInstances.Tests` | applying the full ordered rule set twice produces the same bytes as once (contracts 8.3) |
| 9 | Visibility agreement | `ItemInstances.Tests` | the replication filter and the tooltip builder call `CanSee` and agree on every kind, at every level, identified and not (12.5) |
| 10 | Craft replay | `Server.Tests` | same id and same intent replays to the original receipt, same id with a refilled slot is `OperationConflict` (15.1) |
| 11 | Drop and claim | `TileWorld.Netcode.Tests` plus `Server.Tests` | the instance id survives a drop, a claim by a stranger and a claim by the dropper |
| 12 | Allocator rotation and the epoch refusal | `ItemInstances.Tests` | `Rotate` issues no id in the old node's range, a boot on a retired node id throws, an allocator whose persisted `store_epoch` differs from the live one refuses to issue, and a crash skips the unissued block (3.6) |
| 13 | No client payload route | `Server.Tests` | an architecture test over the message routes asserting none carries a payload-shaped field (15.3) |
| 14 | Concurrent cross-container move | `Server.Tests` | two moves of one stack, exactly one succeeds |
| 15 | Page sync consistency | `TileWorld.Netcode.Tests` | a reassembler fed a chunk whose `Sequence` differs mid assembly (it discards and restarts, 7.5 rule 1), a fifth concurrent assembly (it evicts the oldest and counts it, rule 2), a truncated final chunk (it quarantines rather than throwing, rule 3), and a connection drop mid assembly (rule 4). NOT out-of-order chunks: the channel is `ReliableOrdered`, so that cannot happen and the reassembler deliberately does not handle it |
| 16 | Quarantine byte preservation | `ItemInstances.Tests` | wrap, store, load, unwrap, assert byte equality including an over-cap original |
| 17 | Page delta frame bound | `TileWorld.Netcode.Tests` | fourteen changed rare slots produce one delta frame, fifteen produce a fragmented page send, a 100 slot reorder produces a fragmented page send, and no path encodes a game message above `MaxGameMessageBytes` (7.5) |

Four notes on how to run these rather than what they assert:

- **Fuzzing is mutation over goldens, not random bytes.** Random bytes reject at the first varint and prove
  nothing. A mutation of a valid golden exercises the paths a real corruption reaches, and the corpus is
  the checked-in goldens so a new golden widens the fuzzer for free. The assertion is a triple: no throw, a
  reason from the closed set, and the SAME reason for the same mutation across runs.
- **The scale tests go in `Benchmarks` and their structural behaviour goes in `Server.Tests`**, which is the
  existing split (`MutationJournalBenchmarkTests`, 16 facts, beside the `--journal` mode). A benchmark that
  only runs by hand is a benchmark nobody runs.
- **Test 7 must run once with `-c Release` before merging.** The evaluator's overflow behaviour differs
  under a `Debug.Assert` rescue, and CI tests Release while a local `dotnet test` runs Debug (AGENTS.md,
  the `TeleportEpochBasisTests` lesson, #637).
- **Nothing here needs a GPU and nothing here writes process-global state**, so no test in this plan needs a
  `DisableParallelization` collection. If one later does, it enlists in a named collection with the shared
  state in its doc comment, per the #349 rule.

## 18. Grimhollow adoption plan

WRITTEN, NOT EXECUTED. Nothing here is done by this document, and each row is work for
[Grimhollow #208](https://github.com/APKiwiOrg/Grimhollow/issues/208) to sequence. Gate 0 decision 12 puts
`feature/item-drop` first, so every row assumes that branch has landed.

| # | Change | From | To | Tracked |
|---|---|---|---|---|
| 1 | Bag, worn and bank become paged containers | three sections at 30, 11 and 56 slots (`b-grimhollow.md:503-520`) | `bag/p00`, `worn/p00`, `bank/p00` to `bank/p09` at 100 slots each | #208 |
| 2 | Bank grows to the owner's 1,000 stacks | `BankSlots = 56` | 10 pages, 17 of the 64 sections used, 5,700 slots available (5.4) | #208 |
| 3 | `PlayerJournalSections` widens | a `byte` flags enum with all eight bits used | `uint`, threaded through `ReplaceSections` and `SyncSections`. A `ushort` holds 16 bits and row 2's ten page bank needs 17 sections (5.4), so it runs out on the page this same table ships | https://github.com/APKiwiOrg/Grimhollow/issues/223 |
| 4 | `ValidateContainer` is replaced | a hard THROW on an unknown item id (`GrimhollowJournalContracts.cs:483-524`) | the engine validator plus quarantine (12.2), so one bad id is a placeholder rather than a locked-out player | https://github.com/APKiwiOrg/Grimhollow/issues/224 |
| 5 | The bank message is replaced | one whole-container blob that hits the 1,024 byte cap at 102 occupied slots | page sync plus deltas (7.5) | https://github.com/APKiwiOrg/Grimhollow/issues/221 |
| 6 | Dropped instances ride the region ground streams | `loot-created` with item id and count | the same event plus instance id, payload and stamp (7.3), and the sibling component on the spawn (7.2) | #208 |
| 7 | Rolls move to `IRandomSource` | three `new Random(seed)` sites and four integer literals (`b-grimhollow.md:786-836`) | `CryptographicRandomSource` in production, `SeededRandomSource` in tests, one constructor parameter at a time | https://github.com/APKiwiOrg/Grimhollow/issues/214 |
| 8 | `EquipStats` becomes content stats | three ints on a hardcoded switch over nine ids | four `stat` rows and `Flat` lines per base (11.7) | #208 |
| 9 | `PresentAtCommit` is set | never set, anywhere (`b-grimhollow.md:618-639`) | set on a contested claim now, and on a trade when trading arrives | #208 |
| 10 | `ItemStack` deconstruction sites are updated, OUTSIDE Scope A's step 7 to 11 window | `var (id, count) = stack` throughout | the three component form (4.2), scheduled before Scope A's reader switching opens or after it closes, never inside it (section 20, phase 1) | #208 |

**Row 4 is the one with a behaviour change a player can see**, and it is the reason to do it early: today a
retired or reslotted item makes a container undecodable, so item retirement is a breaking persistence
change (`b-grimhollow.md`, candidate I). After it, retirement is a content publish plus a remap rule.

**Row 1 has a migration and it is the only one here that does.** An existing `bank` section decodes as a
version 1 blob into page 0 (4.5), pages 1 to 9 start absent, and the first ordinary commit writes page 0
back as version 2. `WidenBag` and `NarrowBag` (`b-grimhollow.md:88-102`) exist because the version 1 codec
refuses a blob whose declared slot count is not the caller's, and they are DELETED rather than ported,
because a page declares its own slot count and its own first slot (4.4).

**Row 7 is a precondition for row 6 in practice.** A shared server rolling affixes on a literal seed
publishes its own drop table, so the seeds move before the first affixed item drops, not after.

## 19. Ruinborne adoption plan

WRITTEN, NOT EXECUTED, for [Ruinborne #465](https://github.com/APKiwiOrg/Ruinborne/issues/465). Ruinborne
is the harder adopter of the two because its durable store is relational rows rather than a codec, and
because it already has two identities where the engine wants one.

**The precondition, and it is absolute.** The PostDeploy duplicate-row collapse partitions non-stackable
rows by `(character_id, item_id)` with no `instance_json` term and runs on EVERY redeploy
(`c-ruinborne.md:939-948`), so the moment two differently rolled copies of one item exist they collapse
into one. Instances cannot land in Ruinborne before
[Ruinborne #299](https://github.com/APKiwiOrg/Ruinborne/issues/299) is fixed. Nothing else on this list
matters until it is.

| # | Change | From | To |
|---|---|---|---|
| 1 | `OwnedItemId` is replaced | a derived version-5 GUID on the wire and in the journal (`c-ruinborne.md:949-962`) | the engine `long` instance id, with a DUAL-READ window: the old GUID stays on the row for one release so an in-flight use or destroy request still resolves |
| 2 | The bag cell model moves onto pages | `bag_order INT` with legitimate permanent holes and shared cells | a page's sparse ascending entries, which express a hole at zero cost and never compact (5.7). The equipped-row-keeps-its-cell rule is one game-range property kind on the worn entry |
| 3 | Capacity stays exactly as it is | `player_character.bag_capacity`, a slot gate, never trimming | `PagedItemContainer`'s capacity, which is 5.7's four rules and is Ruinborne's semantics restated |
| 4 | The SQL rows become a read model | the rows ARE the durable store, with FKs and filtered unique indexes doing the integrity work | the journal pages are durable and the rows are projected from them, so the indexes become a report rather than a constraint. This is the row with the real cost, and it is open question 9 |
| 5 | `item_stat` moves onto the evaluator | `(item_id, stat_id, flat, percent)` per DEFINITION | one `stat` row per `stat_id` at `Scale = 100`, one `Flat` and one `Increased` line per row, source kind 1 (11.7) |
| 6 | Loot rolls through the generator | `LootRoll.Roll` with an unseeded `System.Random` created once at server start | the loot table picks the base, `ItemGenerator` rolls the instance, both on `CryptographicRandomSource` |
| 7 | The over-cap repair lands | a lowered `max_stack` leaves over-cap rows intact, invisibly and permanently | contracts 8.2 kind 4: legal, may only shrink, self-healing, with the state explicit. [Ruinborne #507](https://github.com/APKiwiOrg/Ruinborne/issues/507) |
| 8 | The string item id becomes a KEY | `NVARCHAR(64)` as the primary key across seven tables | the key survives, an int32 definition id is added beside it, and the derived one-byte wire index is DELETED (contracts 5.5) |

**Row 1's dual-read window is the migration and it is worth spelling out.** One engine instance id is
allocated per existing owned row whose payload is non-empty, and rows with no properties take id 0 and
need no allocation at all (contracts 6.5). Ruinborne's `instance_json` column has never held a value
(`c-ruinborne.md:565-589`), so there is no legacy payload to decode and the instance payload arrives on a
clean slate, which is the single biggest gift in this adoption.

**Row 4 is the one to think hardest about, and this document does not decide it.** Moving the durable store
from rows to pages loses the FK to `item_def`, the filtered unique index on `(character_id, slot) WHERE
equipped = 1`, and the two SQL repairs that are only expressible because the state is rows
(`c-ruinborne.md:906-925`). It also gains paging, batching, quarantine and instances. A middle path exists
and may be the right one: keep the rows durable, adopt the PAYLOAD as a column, and take the container
page machinery only for the bank. Open question 9 puts it to the owner.

## 20. Phased delivery

Five phases. Each names what it ships and the acceptance test that says it shipped.

**Phase 1, the instance record and the container.** The whole of sections 3 and 4:
`KhaozEngine.ItemInstances` with the payload codec, the property registry, the `KECQ` wrapper, the
instance validator and the allocator, plus `ItemStack`'s third component, `ItemSlot`, and container codec
version 2 with its version 1 reader. Behind the narrow `IContentSnapshot` of 2.5, so it does not wait on
Scope A. **Not** paging, **not** the journal, **not** generation, **not** crafting, **not** the evaluator.
Acceptance: tests 1, 2, 3, 4, 12 and 16 green, and the contracts' 45 byte example reproduced byte for
byte. Test 12 is here rather than later because the allocator ships in this phase and its epoch refusal
(3.6) is the one behaviour in it that cannot be added afterwards without a durable migration.

**Phase 1's `ItemStack` change is a fleet-wide break and it is SEQUENCED against Scope A's Grimhollow
adoption.** A three component record struct generates a three out-parameter `Deconstruct`, so every
`var (id, count) = stack` in the fleet stops compiling, and `ItemStack` equality gains the instance id
(4.2). Scope A's Grimhollow plan runs eleven steps of reader switching, and its steps 7 to 11 are the
window in which Grimhollow is HALF migrated, reading some values from content rows and some from code.
**Phase 1 lands before that window opens or after it closes, never inside it**, because a deconstruction
break in the middle of a half-switched reader set is two migrations interleaved, and because Scope A's
acceptance test `EveryStoredContainerStillDecodes` would otherwise be comparing results across a container
codec version bump nobody told it about. Scope B phase 1 does not otherwise wait on Scope A (2.5), so this
is the ONE ordering edge between the two programs at phase 1, and section 18 row 10 is the Grimhollow-side
half of it.

**Phase 1 is a strong base rather than a partial catalog, and that is the distinction #884 draws.** What is
settled in it is every byte format, every id space, every ordering rule and the stacking test, which are
the expensive things (section 21). What is absent from it is breadth, which is content.

**Phase 2, pages and commits.** Sections 5 and 6: `PagedItemContainer`, `ItemContainerPage`, the section
naming, the load path with remap, and `ContainerCommitBuilder` in `KhaozEngine.ItemInstances.Journal`.
Gated on nothing new. Acceptance: tests 5, 8 and 14 green, and budget 4 measured at one commit.

**Phase 3, the wire.** Section 7: the sibling ground component, the spawn overload,
`TileFragmentedMessage`, the page delta, the owner remainder message and `PublicView`. Acceptance: tests 9,
11, 15 and 17 green, and budgets 7 and 8 measured.

**Phase 4, content and generation.** Sections 8 and 9: the eighteen content types, their validators, the
candidate tables and `ItemGenerator`. **Gated on Scope A's registry and publish path being real** (2.5).
Acceptance: test 6 green, budgets 5 and 9 measured, and one authored pack of the owner's own mods rolling
items end to end.

**Phase 5, crafting and stats.** Sections 10 and 11: the fourteen primitives, the guard vocabulary, the
currency row, the operation registry, the craft journal operation, and `ContentStatEvaluator`. Acceptance:
tests 7, 10 and 13 green, budget 6 measured, and one authored currency composing at least four primitives
with guards.

Adoption (sections 18 and 19) runs per consumer AFTER phase 3 for the container half and after phase 5 for
the stat half. Neither consumer waits on the other.

## 21. Decisions that are expensive to change once data exists

Contracts 16 holds the shared list and every entry there still binds. This table adds ONLY what this
document introduces, and "expensive" has the same meaning: rewriting durable bytes that already exist in a
consumer's production database.

| Decision | Section | Cost if changed later |
|---|---|---|
| The affix list is sorted ascending by mod id | 3.4 | The sort is baked into every stored payload, and it IS the stacking test |
| `Position` is a fixed two byte little endian `ushort` | 3.4 | Changes the byte length of every stored affix entry |
| The tier is the mod row's AUTHORED ORDINAL, not a content id | 3.4, 8.3 | Every stored affix names it, and it is why a tier reorder is refused at publish |
| A rarity id is ONE BYTE, so 255 rarity rows EVER ALLOCATED | 8.5 | Kind 130's width, pinned by contracts 9.8. Contracts 5.1 never reuses and never deletes an id, so every retired rarity keeps its number forever and the ceiling counts the dead as well as the live |
| Container page codec version dispatch on byte 0 | 4.4 | Versions congruent to 1 modulo 256 can never be assigned |
| `ContainerPageSlots = 100` | 5.2 | Every section name and every page's `FirstSlot` check |
| Section naming `<container>/p<NN>` | 5.2 | The durable section key a stored projection is filed under |
| `EntryFlags` is one varint per entry, bit 0 quarantined | 4.4 | Removing it makes a quarantined slot indistinguishable without sniffing |
| A merge keeps the LOWER of two instance ids | 4.6 | Makes the merge commutative, so a replay in either order agrees |
| A unique's lines are ordinary zero-weight mod rows | 8.6 | The alternative is a second affix shape in the payload |
| The `(SourceKind, Ordinal, InstanceId, ModifierIndex)` fold order | 11.4 | Integer rounding is not associative, so the order IS the displayed number |
| Source kinds 5 and 6 reserved for passives and buffs | 11.4 | Reassigning one moves every value a later passive tree produces |
| The craft intent carries the target instance id | 10.6 | Without it a replay applies to whatever refilled the slot |
| `item-crafted` carries BEFORE and AFTER payloads | 10.6 | Nothing else in the durable record can answer what a craft changed |
| The `KECQ` wrapper layout | 12.4 | It is durable and holds the only copy of a failed item's bytes |
| `RevealedMask` bit assignments, 129 to 0, 131 to 1, 133 to 2, 134 to 3 | 3.3, 12.7 | The mask is durable inside kind 128, so a bit that moves re-points every partially identified item in the world |

**The last row is the sharpest and is easy to miss, and the vector is not the one an earlier draft
named.** That draft wrote the hazard as "a game that later registers a gated kind at 130", which 3.3
forbids outright: a game may not register into the Scope B range at all. The REACHABLE vector is the
ENGINE's own reserved range, kinds 9 to 127. An engine release adding a gated kind there lands BELOW 129,
so under a derived ascending index it would take bit 0 and shift all four v1 assignments by one, on every
stored payload, with no byte changing and nothing to detect it. That is why the bit is a REGISTERED
constant (3.3) rather than an index, why registration throws on a duplicate bit, and why the four v1
assignments are in the table above rather than only in a sentence. The mitigation for the kind ID is
still append-only, and the mitigation for the MASK is that the bit never moves whatever the id does.

## 22. Contract change requests

**All four of the requests this section opened with were ACCEPTED by the owner and are already folded
into the contracts.** They are kept below rather than deleted, because the reasoning is what a later
reader needs when they meet the amended text and wonder why it says what it says. Two further requests
follow them, raised by the stage 4 review round and not yet answered.

**The third-round contract amendments this document is written against**, which land with the contracts
rather than here and which several sections above depend on:

| Amendment | Contracts | What depends on it here |
|---|---|---|
| `IRandomSource`, `CryptographicRandomSource` and `SeededRandomSource` live in `KhaozEngine.Primitives` | 3.2, 14.1 | 2.2 declares none of the three, 9.1 and 10.5 take the seam in a constructor |
| The one validator takes an optional PREVIOUS snapshot, null at boot and in tests | 10.4 | 8.9's three publish-only checks, which had no input before it |
| `ContainedInstanceId` is `varint uint64` | 9.5 | 3.5's socket entry and 3.8's five byte instance id sizing |
| Every division in the stat fold is FLOOR division, so round half up holds for every sign | 13.2 | 11.6's algorithm and its worked negative case |
| The localized text key value kind is a MARKER carrying no stored value | 4.7 | every `localized text key` field in section 8 and 10.4 |

**1. ACCEPTED. Section 9.8, the worked byte example: restate the affix list in canonical order.** The
example wrote mod ids 4210, 91, 260. Section 3.4 of this document makes the affix list canonically
ASCENDING BY MOD ID, so an implementer copying 9.8's byte block into a golden file produced a
non-canonical payload, which then failed to stack with a correctly encoded twin. The byte COUNT and every
entry's bytes were unchanged either way. Contracts 9.8 now writes the entries 91, 260, 4210, and contracts
9.9 carries the list rule itself, so the ordering is the CONTRACT's rather than this document's refinement.
3.7 reproduces the amended block as it stands.

**2. ACCEPTED. Section 3.2, the package set: record `KhaozEngine.ItemInstances.Journal`.** Contracts 3.2
placed `KhaozEngine.ItemInstances` in `Foundation`. Section 2.1 adds a second, small, `Server`-umbrella
package for the commit builder, because composing a `JournalCommit` needs `KhaozEngine.WorldStore` and
`Foundation` cannot reference a `Server` package. It was filed because contracts 18 does not say whether
adding a package is a refinement or a contradiction, and the safer reading is that a package set is a
vocabulary. Contracts 3.2 now carries the row and the layering reason.

**3. ACCEPTED. Section 9.6, the payload cap's characterisation.** The contract called 512 "about 11 times
the realistic size" and therefore "a guard rail rather than a budget". The request was filed against a
figure of 410 bytes for the deepest item, which the stage 4 review found to be wrong, so the request is
recorded here with the ARITHMETIC IT SHOULD HAVE CARRIED: 3.8's table counts a maximal item at 160 fixed
bytes plus 352 of nested payload, which is the cap exactly, so 512 is 11 times the 45 byte worked example
and 1.0 times a maximal item. Both readings are true of different items. The rule is unaffected, since
raising the cap stays backward compatible and lowering it stays forbidden, and the amended contracts 9.6
no longer promises a margin a six socket item does not have.

**4. ACCEPTED. Section 10.5, fail closed at boot: say what it binds.** The contract said a missing or
invalid active content version fails the boot, with no runtime fallback. Section 10.5 of this document has
a server meet a currency naming a craft operation id the process never registered, and refuses at the
moment of USE rather than at boot, because that is a code deploy mismatch rather than a content version
problem and failing the boot for it takes every player down for one unusable currency. Contracts 10.5 now
binds the rule to the CONTENT VERSION and the pack's validity, and names a missing code registration a
refusal at use with a counter. It is open question 8 either way.

**5. NEW. Section 6.2, the instance allocator: say that its persisted state carries the store epoch.**
Contracts 6.2 fixes the allocation scheme and says the packed high-water mark is persisted per node. What
it does not say is what else that record carries, and section 3.6 of this document now requires one more
field: the `store_epoch` the mark was written under, with a refusal to issue when the live epoch differs.
The reason is that a point-in-time restore rolls back every guard that lives in the store being restored,
including a retired node list, so the only guard that can fire is one comparing a restored value against a
value an operator rotated. The journal already requires that rotate of any restore
(`DURABLE-PLAYER-JOURNAL-DESIGN-2026-09-06.md` section 10). **Requested change:** one sentence in
contracts 6.2 saying the persisted allocator record carries the store epoch and refuses to issue on a
mismatch. It is a refinement of what a durable record contains rather than a change to the id scheme, and
it is filed because the id scheme is a contract and this adds to its durable state. 15.2 has the operator
procedure.

**6. NEW. Section 5.4, keep-legacy: say whether "never crafted again" covers REWRITING an existing
entry.** Contracts 5.4 says a legacy copy is "a legacy id that can never be generated or crafted again".
Read narrowly that covers adding the mod to an item. Read conservatively it also covers rewriting an
affix entry that already names it, which is the reading 10.3 takes: no primitive and no game operation
rewrites a legacy entry's roll position, tier or flags. The narrow reading leaves `RerollValues` free to
draw a fresh position against a legacy tier's preserved range as many times as a player can pay for it,
walking the roll to the top of a range no live mod can produce, which turns the mechanism the owner chose
in order to FREEZE old rolls into a farm for them. **Requested change:** one sentence in contracts 5.4
stating which reading binds. This document builds the conservative one and section 10.3 says what it
costs, so the owner can relax it at gate 1 with the consequence in front of them.

## 23. Open questions for the owner

Nine, each with a recommended default this document already builds to, so silence is an answer.

**1. Are `Corrupted`, `Mirrored` and `Fractured` the right three bits for the ENGINE's `Flags` kind?**
(3.3.) They are engine-range, so every game gets them, and all three are recognisably from one game's
vocabulary. **Recommended default: keep them, and treat the NAMES as the engine's shorthand for "cannot be
modified further", "is a copy" and "one property is locked".** They are the three facts a crafting system of
any shape needs, the bits are free, and 29 more are reserved.

**2. Is a 100 slot page right, against 128?** (5.2.) One hundred is legible in a log line and a section
name, 128 is a shift a compiler produces either way. **Recommended default: 100.**

**3. A stack merge destroys one instance id. Is an event enough?** (4.6.) A `stack-merged` event names both
ids for the journal's retention window. A FIELD on the payload carrying dead ids would keep it forever and
would grow without bound. **Recommended default: the event.**

**4. Is `MaxInstancePayloadBytes = 512` right, given that a six socket item can fill it exactly?** (3.5,
3.8's table, and change request 3.) The v1 field set reaches the cap by construction: 160 bytes are fixed
on a maximal item and the remaining 352 are the six nested payloads, so the cap is what decides how deep a
gem may be rather than a rail nothing approaches. Raising it later is backward compatible and lowering it
is not, so the cost of being wrong is asymmetric in the safe direction. **Recommended default: keep 512
for v1, author `socket_type.max_nested_bytes` on every socket type that admits a deep item (8.7) so the
ceiling is a publish-time refusal rather than a runtime one, and revisit the cap when a real six socket
item is authored.**

**5. Are mod tiers a list on the mod row, or their own content type?** (8.3.) A list makes the tier ordinal
stable by construction and makes a reorder a publish refusal. A content type would give every tier its own
id, its own remap rule and its own retire path, at the cost of putting a second content id in every stored
affix entry. **Recommended default: the list.**

**6. Should the ENGINE cap ground item payload bytes per cell?** (7.4, 7.6, and 13 row 11.) Today the
spawn is capped per ITEM, the cell is capped by COUNT, and a snapshot frame is not capped at all, so the
exposure is bandwidth rather than a throw: a full-state serve re-sends every ground payload in interest to
every viewer on every tick. **Recommended default: yes, add a per-cell payload BYTE budget beside the
existing occupancy limit on `SpawnGroundItem`, and measure budget 11 before deciding whether the sibling
component also needs the AoI delta path.**

**7. Should the engine cap the affix COUNT, or leave it to the rarity rules?** (3.3, 8.5.) Kind 131's count
is a byte, so 255 is the format ceiling, and the owner's stated shape is six. **Recommended default: no
engine cap. The rarity rule's `max_affixes` is the cap, it is content, and 255 stays the format's ceiling.**

**8. An unregistered craft operation: refuse at use, or fail the boot?** (10.5, and change request 4.)
**Recommended default: refuse at use, with a counter, because the alternative takes a server down for one
unusable currency row.**

**9. For Ruinborne, do the journal pages become durable, or do the SQL rows stay durable with a payload
column?** (19, row 4.) Pages buy paging, batching, quarantine and one shared code path. Rows keep the two
filtered unique indexes, the foreign keys and the two SQL repairs that are only expressible over rows.
**Recommended default: the middle path. Keep the rows durable, add the payload as a column, and adopt the
page machinery for the BANK only, where the write amplification actually hurts.** That is the smallest
change that gets Ruinborne instances, and it leaves the larger question to its own design.

## 24. Appendix A: review log

Stage 4 ran one adversarial review of this document (F1 to F28) and one cross-spec consistency review
against Scope A and the contracts (C1 to C30, of which fifteen touch this document). Every finding is
below with the reviewer's severity, the orchestrator's verified verdict, and what this document did about
it. A finding is recorded whether it was fixed, rejected or taken as a direction, because a review whose
rejections are invisible reads as a review that found nothing to argue with.

**Verdict column.** CONFIRMED means the orchestrator read the cited lines and the claim held. PARTIAL
means part of it held. REFUTED means it did not. LEAD means it was surfaced rather than checked. The
severity beside it is the verified one, which is not always the reviewer's.

| Id | Claim | Reviewer | Verified | Disposition | Commit |
|---|---|---|---|---|---|
| F1 | A quarantine wrapper cannot fit the page entry bound, and invariant 4 rejects every wrapper | high | CONFIRMED high | FIXED 4.4, 4.7, 5.4. `PayloadLength` is bounded by the section cap, `MaxInstancePayloadBytes` binds a non-quarantined entry only, and `SetSlotAt` skips invariants 2 and 4 on a quarantined slot | 84188832 |
| F2 | An unresolved content reference is counted and tolerated, which is a fourth outcome | high | CONFIRMED high | FIXED 12.2, 12.3, 5.5, 13 rows 5 and 6. Checks 7 and 8 quarantine per contracts 10.1 and 10.2. The tolerance argument is recorded as REJECTED below | 84188832 |
| F3 | The restore-from-backup guard cannot fire, because the retired node list is restored too | high | CONFIRMED medium | FIXED 3.6, 13 row 7, 15.2, test 12. The allocator binds its persisted mark to the store epoch and refuses on a mismatch, the retired list is demoted to a re-boot guard, and 15.2 carries the procedure and a weaker honest claim. Contract request 5 | b8a37d9d |
| F4 | Remap rules and the validator never descend into a socket's nested payload | high | PARTIAL medium | FIXED 5.5 step 2, 12.2 check 6. The pass and the checks traverse nested payloads and `ContainedDefinitionId` at every depth, and the re-encode recomputes `NestedLength`, the kind 132 field length and the entry payload length | 74b16f21 |
| F5 | A client-headed batch's intent is the whole ordered list, so a resubmit conflicts instead of replaying | high | CONFIRMED high | FIXED 6.4, 6.5, 6.6. A client-headed batch's normalized intent is the client operation's own alone, server-caused work rides as events and projection writes | 84188832 |
| F6 | `RerollValues` farms a legacy mod, which contracts 5.4 says can never be crafted again | high | REFUTED as a contradiction | DIRECTION TAKEN, see below. The text contradiction was refuted and the conservative reading was applied anyway: a legacy affix entry is FROZEN (10.3, 10.7, 15.8). Contract request 6 puts it to the owner | fcc1c6fe |
| F7 | A multi-slot page delta exceeds 1,024 bytes and the encoder throws inside the serve loop | high | CONFIRMED high | FIXED 7.5, 13 row 12, test 17, phase 3. The builder measures as it writes, fourteen rare slots fit, and an oversize delta is abandoned for a fragmented page | 84188832 |
| F8 | Ground item payloads are re-sent full-state per viewer per tick and never priced | high | CONFIRMED medium | FIXED 7.4, budget 11. The tile serve is stated as full state, `AoiDeltaReplicator` is named as unused, and the cost is a budget in bytes per viewer per second at the 250 ms tick | b8a37d9d |
| F9 | `RevealedMask` bits are derived from registration order, so a future engine gated kind re-points every stored mask | medium | CONFIRMED medium | FIXED 3.3, 12.7, 21. A gated kind carries a fixed `identificationMaskBit`, the four v1 bits are pinned, and 21 names the engine 9 to 127 range as the reachable vector | fcc1c6fe |
| F10 | The craft event is quoted at 40 and 83 bytes and is about 127 | medium | CONFIRMED medium | FIXED 6.4, 6.7, 9.5, 10.6, budget 4. `item-generated` is 72 bytes, `item-crafted` is 127, before-and-after costs 59 not 42, and the factor is 146 not 180 | b8a37d9d |
| F11 | No steady-state commits-per-second budget, and no stated action on `Backpressure` | medium | PARTIAL low | FIXED 16 budget 13 and its derivation. 1,000 players at 4 Hz offer 4,000 per second pre-coalescing, the consumer refuses rather than retries, and the 698 figure is recast as offered load in 5.5 and 16 | beb4750a |
| F12 | The deepest expressible item is understated, so request 3 and question 4 rest on a wrong figure | medium | CONFIRMED medium | FIXED 3.5, 3.8, 22 request 3, question 4. The item is counted field by field: 160 fixed plus 352 nested is the cap, so the field set fills 512 by construction | b8a37d9d |
| F13 | A snapshot frame is not subject to the 1,024 byte cap | medium | CONFIRMED medium | FIXED 7.6, 13 row 11, question 6. The cap is only in `EncodeGameMessage`, so the row is bandwidth rather than a throw | b8a37d9d |
| F14 | "The same number of draws" is false, because step 8 skips draws on an empty pool | medium | CONFIRMED medium | FIXED 9.4 step 8, test 6. An empty pool draws and discards one `NextInt(0, 1)` and one `NextRollPosition()`, and test 6 cites 9.3 | fcc1c6fe |
| F15 | The negative rounding rule departs from contracts 13.2 and its example is wrong | medium | CONFIRMED medium | FIXED 11.6, test 7. Floor division per the amended contracts 13.2, the sign-magnitude rule dropped, the example corrected to `-14000` giving -1 | fcc1c6fe |
| F16 | No `StringId` keys are named for the placeholder presentations | medium | CONFIRMED medium | FIXED 12.3, 12.7. Three engine keys, `khaoz.item.quarantined`, `khaoz.item.retired` and `khaoz.item.unidentified`, resolved through `ContentStringCatalog` on the `SafeFormat` path | fcc1c6fe |
| F17 | The generator memo is understated by about 2.5 times and is small for its key space | medium | CONFIRMED medium | FIXED 9.2, budget 9. The memo is 9.8 MB, the total is 30 MB, and the eviction and the 19,200 key space are stated | b8a37d9d |
| F18 | `IRandomSource` per call contradicts contracts 14.4's constructor rule | medium | CONFIRMED medium | FIXED 2.2, 9.1, 9.4, 10.5, 15.5. Constructor injection, and a replay builds a second generator sharing the tables | fcc1c6fe |
| F19 | No resident memory budget for millions of instances against the journal's double clone | medium | CONFIRMED medium | FIXED budget 12 and its derivation, test 5. About 70 MB of page bytes at 1,000 players, two to three copies, 250 MB ceiling | b8a37d9d |
| F20 | A ground item has no owner viewer, so its owner-only behaviour is undefined | lead | LEAD | FIXED 7.4. A ground item has no owner viewer in v1, its public view is its whole replicated payload, and `BoundTo` is stripped before the component is written | beb4750a |
| F21 | The header says section 22 is empty and it holds four requests | low | CONFIRMED low | FIXED the header. It names the four requests and the amendment round | beb4750a |
| F22 | Test 12 appears in no phase's acceptance list | low | CONFIRMED low | FIXED phase 1 acceptance | beb4750a |
| F23 | A live table swap at publish contradicts the no-live-apply decision | low | CONFIRMED low | FIXED 9.2. A boot builds the tables, the beside-then-swap shape stays as the later hook. Same fix as C15 | 6acc3048 |
| F24 | The rarity ceiling is 255 rows ever allocated, not 255 live rarities | low | CONFIRMED low | FIXED 21. The row now says the ceiling counts every retired rarity, because contracts 5.1 never reuses an id | beb4750a |
| F25 | `IsCorruptible` is called standing in 10.3 and authored in 10.4 | low | CONFIRMED low | FIXED 10.3, 10.4. It is STANDING, the whetstone no longer authors it, and the guard row stays for an authored restatement | beb4750a |
| F26 | Test 15 asks for out-of-order chunks, which 7.5 rule 1 says cannot happen | low | CONFIRMED low | FIXED test 15. The four cases are a sequence change, a fifth assembly, a truncated final chunk and a dropped connection | beb4750a |
| F27 | The OSRS row reaches 7 bytes with a one-byte definition id against a two-byte preamble | low | CONFIRMED low | FIXED 3.8's preamble. One byte under 128, two under 16,384, three above, and the OSRS row is stated as the floor | beb4750a |
| F28 | 8.5 misquotes contracts 9.8's rarity bytes and 3.7's sentence contradicts itself | low | CONFIRMED low | FIXED 8.5 and 3.7. The rarity bytes are `82 01 01 03`, and 3.7 reproduces the amended contract block in canonical order | 177b0b39, beb4750a |
| C1 | The `IRandomSource` seam is declared in Scope B's package and Scope A needs it below | high | CONFIRMED high | FIXED 2.2, 9.3. The three rows are gone and the seam is `KhaozEngine.Primitives` per contracts 14.1 | beb4750a |
| C2 | Seven Scope B types carry opaque bytes and list-valued fields that no value kind names | high | CONFIRMED high | FIXED 8.1 to 8.10, 9.2, 10.4. Eleven child types at 263 to 273, weights as whole `ServerOnly` types, no opaque bytes and no ad hoc list left | 177b0b39 |
| C6 | A remap rule cannot be applied to an instance payload: no kind-to-content-type mapping exists | high | CONFIRMED high | FIXED 3.3, 2.2, 5.5, 12.2. `Register` takes a field shape and reference targets, and the pass and the checks are derived from them | 74b16f21 |
| C8 | Scope B references a content type key `item_base` that Scope A registers as `item` | medium | CONFIRMED medium | FIXED 8.6 and 10.4. Both targets name `item` | 177b0b39 |
| C9 | Scope B's field names are PascalCase against the contracts' lower-case rule | medium | CONFIRMED medium | FIXED every schema in 8 and 10.4, and the prose that named them in 9.4, 10.2, 10.3 and 23. The mod line field is `line` and the rare name word field is `text` | 177b0b39 |
| C10 | Scope B narrows the socket count from a varint to a byte without filing it | medium | CONFIRMED medium | FIXED 3.3. The count is a varint per contracts 9.5, with the reason the byte would have been invisible in the golden file | 6acc3048 |
| C15 | Scope B says a publish swaps the candidate tables live, and v1 has no live apply | medium | CONFIRMED medium | FIXED 9.2, same fix as F23 | 6acc3048 |
| C17 | The retired-definition placeholder is assumed by Scope A and built by neither | medium | CONFIRMED medium | FIXED 12.2 check 13, 12.3. A `Retired` outcome carries the placeholder presentation without quarantining the bytes | fcc1c6fe |
| C18 | `ItemStack` gaining a third component is a fleet-wide break neither plan sequences | medium | CONFIRMED medium | FIXED phase 1, 18 row 10. Phase 1 lands before Scope A's Grimhollow reader-switch window opens or after it closes | 6acc3048 |
| C22 | `PlayerJournalSections` as a `ushort` is one bit short of this document's own 17 | low | CONFIRMED low | FIXED 18 row 3. `uint`, citing the 17 | beb4750a |
| C24 | Contracts 12.3's composed rare-name template has no row in either spec | low | CONFIRMED low | FIXED 8.5, 8.8. `rarity_rule.display_format` IS the template, its arguments are the word texts in position order then the base name | 177b0b39, 74b16f21 |
| C25 | `stat.tags` is authored by Scope A and read by nobody | lead | LEAD | FIXED 11.3. A line's tag scope is matched against the union of the context tags and the target stat row's own tags | beb4750a |
| C26 | Nothing enforces the three-way type id split at registration | low | CONFIRMED low | FIXED 3.3, 2.2, 10.5. `Register` takes an `InstanceKindBand` and throws on a mismatch | beb4750a |
| C27 | Placeholder lines and tooltips are rendered without naming the layered catalog | low | CONFIRMED low | FIXED 7.4, 12.3. Both name `ContentStringCatalog` and the `SafeFormat` path | beb4750a |
| C30 | Neither spec says whether a Scope B format change bumps `FormatGeneration` | lead | LEAD | FIXED 14. A fifth number is named: Scope A bumps `ContentPackFormat.Generation` for any pack-carried change including a new remap rule kind, and Scope B has no generation of its own | beb4750a |

### F2, the rejected argument, recorded in full

The draft this review read made validator checks 7 and 8 "counted and tolerated": an unresolvable content
reference inside a payload kept its bytes verbatim, contributed nothing to the evaluator, rendered as a
placeholder line and was re-checked on every load, while the item stayed equipped, tradable and craftable.
**The argument for it was a real one and it is why the draft said what it said.** A payload-level unknown
reference is strictly more likely than an unknown definition, because there are more mods than bases, so
quarantining on it multiplies the blast radius of one forgotten remap rule from one item to every item
carrying that mod. Tolerance keeps a player playing through the window in which an author fixes a publish.

**It was rejected because it is a contradiction rather than a refinement.** Contracts 10.1 says every
durable record resolves to exactly one of Valid, Remapped or Quarantined and that "there is no fourth",
and defines Quarantined as "Something did not resolve or did not parse". Contracts 10.2 names
`unknown-content-reference` as a QUARANTINE reason code and says a quarantined item "is unusable,
untradeable and undroppable". Tolerating it is a fourth outcome under a reason code the contract assigned
to the third, and section 22 did not file it, so it would have shipped as a silent divergence. The
concrete failure the reviewer built: an affix whose tier no longer resolves stays on a TRADABLE item, the
player sells it as top tier, the buyer applies a `RerollValues` that writes a fresh position against a
range that does not exist, and the missing `ReplacedBy` rule then lands and reads that position against a
nerfed mod's range. The trade already happened. Under the contract the item quarantines at the first load,
is untradeable by definition, and returns intact when the rule publishes, because the bytes were never
touched.

The tolerance argument would still be available as a CHANGE REQUEST against contracts 10.1 and 10.2, and
this document does not make one, because the second half of the rejected reading is the expensive half:
tolerance without untradeability is the exploit, and tolerance WITH untradeability is most of what
quarantine already is.

### F6, the direction taken over a refuted finding

The reviewer's finding was that `RerollValues` farms a legacy mod because 10.3's standing refusal covers
only primitives that ADD a mod. **The text claim was REFUTED**: the draft defined "crafted" as added by a
craft and already offered `NotLegacy` as an authored guard, so the document did not contradict itself.

**The conservative reading was applied anyway, on the orchestrator's direction**, because the owner's
words in contracts 5.4 are "a legacy id that can never be generated or crafted again" and the exploit the
reviewer constructed is real whatever the draft meant: a currency whose only step is
`RerollValues(ByModId <legacy id>)` draws a fresh position against the preserved range every application,
so patience alone walks a roll to the top of a range no live mod can produce, and the mechanism the owner
chose in order to FREEZE old rolls becomes a farm for them. So 10.3 now carries a second standing rule: a
legacy affix entry is FROZEN and no primitive and no game operation rewrites its roll position, tier or
flags. 10.7 and 15.8 are aligned with it.

**It is a TIGHTENING and the owner can relax it at gate 1**, which is why contract request 6 exists. The
cost is stated in 10.3: an item with one legacy affix cannot have that entry rerolled by any currency,
and a currency authored to reroll one entry by index simply refuses when that entry is the legacy one.

### Findings this document did not act on

None. Every F and every C in the table above is either fixed here or is a contracts amendment carried by
that document and depended on from section 22's table. The fifteen consistency findings that name Scope A
or the contracts rather than this document (C3, C4, C5, C7, C11, C12, C13, C14, C16, C19, C20, C21, C23,
C28 and C29) belong to those documents and are not restated here. Four of them reach this one through the
contracts and are in section 22's amendment table.
