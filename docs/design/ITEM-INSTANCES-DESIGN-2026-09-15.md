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
22 is the only channel for changing that, and it is empty.

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
   sockets barred from stacking at publish. Section 4.5.
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
| `InstancePropertyRegistry` | `Register(kind, codec, visibility, identificationGated)`, frozen at first pack load | 3.3 |
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
| `ItemGenerator` | `Generate(in GenerationContext, IRandomSource)` | 9.4 |
| `GenerationContext`, `GenerationResult` | inputs and the resolved item | 9.4 |
| `ModCandidateTables` | built at pack load, queried per roll | 9.2 |
| `IRandomSource`, `CryptographicRandomSource`, `SeededRandomSource` | contracts 14.1 and 14.2 | 9.3 |
| `CraftPrimitive` | enum of the fourteen v1 primitives | 10.2 |
| `CraftGuard`, `CraftGuardKind` | the guard vocabulary | 10.3 |
| `CraftPlan`, `CraftOutcome`, `CraftRefusal` | a resolved sequence and its answer | 10.5 |
| `CraftingRegistry`, `ICraftOperation` | game-registered code operations | 10.6 |
| `ContentStatEvaluator` | the integer evaluator | 11.6 |
| `StatModifierLine`, `StatCombineKind`, `StatSourceKey`, `StatContext` | the evaluator's value types | 11.3 |
| `IStatConditionRegistry` | game conditions above the engine range | 11.5 |

`KhaozEngine.ItemInstances.Journal`:

| Type | Shape | Section |
|---|---|---|
| `ContainerCommitBuilder` | accumulates page operations, emits ONE `JournalCommit` | 6.4 |
| `ContainerSectionNames` | the `<container>/p<NN>` scheme and its parser | 5.2 |
| `ItemInstanceEvents` | the event type constants and their payload codecs | 6.6 |
| `ContainerLoadResult` | decoded pages plus findings plus the dirty set | 5.5 |

`KhaozEngine.Items`, modified:

| Change | Shape | Section |
|---|---|---|
| `ItemStack` | gains a third component, `long InstanceId`, defaulting to 0 | 4.2 |
| `ItemContainer.SetSlotAt` | the payload-carrying codec door | 4.6 |
| `ItemContainer.TakeSlotAt` | the payload-carrying take | 4.6 |
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
`KhaozEngine.Server.Tests` mirroring `MutationJournalBenchmarkTests`. Section 17.4 has the detail.

### 2.4 What the journal does NOT need

**The recommended coalescing design (section 6) requires no change to `KhaozEngine.WorldStore`, no
change to either provider schema and no change to the store conformance suite.** That is a result
rather than an accident: the option that would have needed all three is scored and rejected in 6.3, and
6.5 states exactly what it would have cost, so the owner can choose it knowingly.

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
properties into exactly one byte sequence, which is what turns the stacking rule of 4.5 into a
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
| 132 | `Sockets` | `[Count: byte][ socket entry ] * Count`, AUTHORED order | `Everyone` |
| 133 | `Enchantments` | the SAME entry layout as 131, ascending by mod id | `Everyone`, identification gated |
| 134 | `RareName` | `[TemplateId: varint int32][WordCount: byte][WordId: varint int32] * WordCount` | `Everyone`, identification gated |
| 135 to 1023 | reserved for Scope B | | |

**Note the visibility levels are NOT monotonic in the kind id**: kinds 4, 5 and 6 are `OwnerOnly` while
7 and 8 are `Everyone`. That is deliberate, and it is the reason section 7.4 declines the optional
optimisation contracts 11.2 offers (assigning kinds so an owner-only projection is a prefix truncation).
Coupling the kind ranges to the visibility vocabulary forever would have bought a memcpy over a
filtered copy, and section 7.4 shows the filtered copy is already a memcpy of the kept runs.

**Registration.** `InstancePropertyRegistry.Register(kind, codec, visibility, identificationGated)` runs
ONCE at process start, before any pack is loaded, and the registry freezes when the first pack loads. A
later registration throws. That is `ReplicationRegistry.Register`'s shape
(`TileProtocol.Components.cs:122`) and contracts 4.2's rule for the content type registry, applied one
level down. A game MAY register in the game range and MAY NOT register into the engine or Scope B
ranges, replace a registered codec, or unregister anything. Section 15.4 is why unregistering is
forbidden: it would make two previously distinct items stack and destroy one identity.

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
  were rolled or crafted in, which is what 4.5's byte comparison needs. Section 21 records it as
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
`TileObjectArchetype.Tags` (`TileWorldHash.cs:117-120`). The consequence for 4.5 is exact and worth
stating: two otherwise identical items whose gems sit in different sockets do NOT stack, which is
correct, because they are different items.

**The nested budget is finite and here is the arithmetic.** A six socket item spends
`6 * (1 + 2 + 5 + 1) = 54` bytes on socket overhead at typical id widths, plus about 100 bytes on its
own fields, leaving about 358 of the 512 byte cap across six nested payloads, so about 59 bytes each.
Fifty-nine bytes is one rare's worth of payload (3.8), so a six socket item can hold six ordinary gems
comfortably and cannot hold six six-affix rares. That ceiling is a v1 design consequence rather than a
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
- **A restore from backup ROTATES the node id.** Section 13, row 7, has the reasoning. The allocator
  exposes `Rotate(ushort newNodeId)` and the boot refuses a node id already on the persisted retired
  list, so a post-restore id can never collide with a pre-restore one. A restore burns one of 65,535
  node ids.
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
   F2 20 03 CC CC 00                        mod 4210, tier 3, position 52428, flags 0
   5B 01 33 33 00                           mod 91,   tier 1, position 13107, flags 0
   84 02 02 FF FF 00                        mod 260,  tier 2, position 65535, flags 0
84 01 0A                                 kind 132 len 10  sockets
   01                                       count 1
   07                                       socket type 7
   C1 06                                    contains definition 833
   E9 20                                    contains instance 4201
   03                                       nested length 3
      02 01 37                              nested kind 2 len 1, item level 55
```

Two things to check against 3.4 while reading it. The affixes are ascending by mod id (91, 260, 4210)
in the CONTRACT's example only by accident, because they are written 4210, 91, 260 there. **This
document's canonical order sorts them, so the canonical form of that item writes 91 first.** The
contract's example is an illustration of the ENTRY layout rather than of the list order, which it does
not state, so ordering the list is a refinement rather than a contradiction. An implementer copying the
contract's byte block into a golden file must sort it first, and the golden file in 17.1 is the sorted
form. That is the single most likely place a first implementation goes wrong, which is why it is called
out here rather than left to be discovered.

### 3.8 Coverage of the four reference games, with byte counts

Each row is a real item of that shape, encoded through 3.2 and seated in a container page entry through
4.4. "Payload" is the tagged field bytes. "Slot entry" is the whole entry including the payload, which
is what a page and therefore a commit actually costs. Definition ids are assumed dense from 1, so a
base in the first 16,383 costs two varint bytes and one above costs three.

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

**The deepest item v1 can express, for the guard rail.** Six affixes at three byte mod ids, six occupied
sockets each holding a forty byte nested payload with its own instance id, plus flags, item level,
quality, durability, a bound-to subject, identification, rarity, one enchantment and a rare name, is
**410 bytes**. That is 80 percent of `MaxInstancePayloadBytes = 512`.

This is worth stating plainly because it revises the characterisation in contracts 9.6, which called
512 "about 11 times the realistic size" and therefore "a guard rail rather than a budget". Both readings
are true of different items: 512 is 11 times the WORKED example of 9.8, and 1.25 times the deepest item
the v1 field set can produce. So the cap is a guard rail for an ordinary item and close to a budget for
a maximal one, and the practical consequence is that raising it is more likely than the contract
implies. Raising it is backward compatible and lowering it is not (contracts 9.6), so nothing is at
risk, but section 14.5 records the trigger to watch and section 21 lists the cap as expensive to lower.

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
  [PayloadLength: varint int32]        // 0 to MaxInstancePayloadBytes
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

`TakeSlotAt(int slot)` is `TakeAt`'s payload carrying sibling: it returns the whole `ItemSlot` and seats
`ItemSlot.Empty`. `Swap(a, b)` swaps the payload array entries alongside the stacks, so the existing one
line tuple swap becomes two.
