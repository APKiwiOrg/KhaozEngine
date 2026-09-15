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

### 5.5 Loading a container

`ContainerLoadResult Load(IReadOnlyList<JournalProjectionSection> sections, IContentSnapshot snapshot)`,
one pass, no store reads, no ambient state, following the one-validator shape contracts 10.4 sets for
the content side.

1. For each section whose name parses as this container's, decode the page (4.4). A page that fails to
   decode at the PAGE level (bad version, bad header, truncated, entries out of order) is a whole page
   failure and is quarantined as a unit, because a page that cannot be parsed has no entries to keep.
2. For each decoded page, apply the remap rule set (contracts 8.3): every rule whose `IntroducedIn` is
   strictly greater than the page stamp, in `Sequence` order, in one pass. A rule that changes nothing
   is a scan rather than a rewrite, which is almost every rule on almost every page.
3. If any rule changed anything, mark the page DIRTY and set its in-memory stamp to the active version.
   Do NOT write it (5.6).
4. Validate each entry (12.2). Structural failures quarantine that ENTRY and leave the rest of the page
   alone. Drift failures are counted and tolerated (12.3).
5. Return the pages, the accumulated findings and the dirty set.

**Remapped pages are rewritten LAZILY, on the next ordinary commit, and contracts 10.3 says why.**
Eagerly rewriting every touched page at boot would be a write storm proportional to the whole player
base arriving exactly when the server is coldest, against a recorded baseline of 698 commits per second
(`a-engine.md:607-641`). The cost of lazy is that a page can sit remapped in memory across a session and
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

- **ONE `JournalOperationIdentity`.** Its `normalizedIntent` is the canonical encoding of the ORDERED
  operation list: `[Count: varint][ per operation: [Kind: varint][Parameters] ]`, little endian,
  minimal varints, exactly the rules of contracts 15. Canonical because the intent is what the journal
  hashes to detect a conflicting replay (`JournalValidation.Hash` is `SHA256.HashData`,
  `JournalLimits.cs:135`), so two encodings of the same batch must produce the same bytes.
- **ONE `JournalEvent` per logical operation**, in order. The audit trail is not collapsed, only the
  projection is. At about forty bytes an event and a cap of 128 events per operation
  (`JournalLimits.cs:10`), a batch is bounded at 128 operations and about 5 KB of events.
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
- **Replay with a DIFFERENT intent under the same id.** `OperationConflict`, unchanged. The canonical
  intent encoding in 6.4 is what makes that detection meaningful for a batch: a batch resubmitted with
  one extra operation appended hashes differently and is refused rather than silently applied.

### 6.7 What it is worth

The arithmetic that justifies the section, using 3.8's 69 byte entry and 5.4's 6.9 KB page.

| Workload | Today, one section per container | Paged, one commit each | Paged and batched |
|---|---|---|---|
| One craft on a 1,000 stack bank | 69 KB | 6.9 KB | 6.9 KB |
| Twenty crafts in a held action | 1,380 KB | 138 KB | **7.7 KB** |
| Twenty crafts, as journal events | 20 events | 20 events | 20 events |
| Commits issued | 20 | 20 | **1** |

Twenty crafts go from 1,380 KB and twenty commits to 7.7 KB and one, a factor of about 180 on bytes and
20 on commits. The 7.7 KB is one 6.9 KB page plus twenty events of about forty bytes. Section 16 turns
those into measured budgets.

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

The same function answers the tooltip. A tooltip builder that computed its own answer is how a client
eventually renders something the server never sent, so `CanSee` (12.5) is called by the replication
filter and by the tooltip builder and by nothing else, and 17.9 is the test that proves the two agree.

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

**A ground item's payload is capped independently of the frame.** The sibling component rides inside a
snapshot, and a snapshot frame is subject to the same 1,024 byte cap, so a cell holding several deep
items could in principle overflow one. The engine's answer is the one it already uses: the ground item
cap is a game budget rather than an engine one, and `TileWorldServer`'s cell occupancy limit
(`SpawnGroundItem` answers 0 on a full cell, `TileWorldServer.GroundItems.cs:62-98`) is where a game
tunes it. What this document adds is the arithmetic: at the 69 byte public view of a rare (3.8), a
snapshot carrying twenty ground rares in one interest set spends 1,380 bytes and overflows. Section 13
row 11 carries it as a failure mode with a detection, and open question 6 asks the owner whether the
engine should cap ground payload bytes per cell rather than leaving it to the game.

## 8. Affix content types

### 8.1 What registers, and where

Seven content types register in the Scope B range of contracts 4.3 (`256` to `1023`), through
`RegisterContentType` exactly as contracts 4.2 shapes it, at process start and before any pack loads.
Each carries a row codec, a validator, a field schema (contracts 4.7), a default content visibility and
a chunk slot count.

| Type id | Type key | What a row is | Default visibility | Chunk slots |
|---|---|---|---|---|
| 256 | `mod` | one affix, with its tiers, weights and stat lines | `Client` | 4,096 |
| 257 | `mod_group` | an exclusivity group and how many of it one item may carry | `Client` | 256 |
| 258 | `rarity_rule` | how many prefixes and suffixes a rarity permits, and its weight | `Client` | 256 |
| 259 | `unique_template` | a fixed item with fixed or narrow-range lines | `Client` | 4,096 |
| 260 | `socket_type` | what a socket accepts | `Client` | 256 |
| 261 | `crafting_currency` | a named sequence of primitives with guards | `Client` | 4,096 |
| 262 | `rare_name_word` | one word in a rare-name position | `Client` | 4,096 |

**All seven are `Client` by default and that is deliberate.** A client computes tooltips from this data
and must agree with the server to the unit (contracts 13.4), so hiding a mod's ranges would make the
tooltip a second implementation. What is `ServerOnly` is the LOOT TABLE that decides which base drops,
which is Scope A's engine-range type and not one of these. The one exception is per field rather than
per type, and it is named in 8.2: a mod's spawn weights are `ServerOnly`, because a weight is the only
field on these rows a client can farm rather than display.

**Nothing in the engine is PoE-specific, and this section is the place that claim has to be earned.**
Every row above is a SHAPE. There is no mod named in engine code, no currency named in engine code, no
rarity named in engine code and no tier count fixed in engine code. The engine ships the seven types,
their codecs and their validators, and the owner authors every row. A game that wants Tibia's model
authors three rarity rules with zero affixes each and never writes a mod row, and nothing about that is
a degraded mode.

### 8.2 `mod`, the central type

| Field | Value kind | Reference target | Visibility | Required |
|---|---|---|---|---|
| `Id` | int | | `Client` | yes |
| `Key` | string key | | `Client` | yes |
| `Kind` | int | | `Client` | yes |
| `GroupId` | key reference | `mod_group` | `Client` | no |
| `Legacy` | bool | | `Client` | yes |
| `DisplayFormatKey` | localized text key | | `Client` | yes |
| `Tiers` | opaque bytes | the tier list of 8.3 | mixed, see below | yes |

`Kind` is an integer the engine assigns exactly two meanings to, `1` prefix and `2` suffix, with `3` to
`255` free for the game. The engine needs the prefix and suffix split because contracts 9.9 put the
distinction on the ROW rather than in the affix entry (3.4), so the generator reads it to count against
the rarity rule's two limits. Everything above 2 is a kind the generator treats as its own counted pool,
which is how an implicit, a corruption line or a Mortal Online material line gets a slot without an
engine change.

`GroupId` is contracts' exclusivity, one level of indirection rather than a raw integer, so the group
can carry a count. `mod_group` is three fields: `Id`, `Key` and `MaxPerItem` (an int, default 1). Two
mods sharing a group with `MaxPerItem = 1` cannot both appear, which is the ordinary exclusivity case,
and a group with `MaxPerItem = 2` is a family an item may carry twice. A mod with no group is
unconstrained beyond the rarity rule's counts.

`Legacy` is contracts 5.4's flag, read by exactly two places: the generator skips a legacy row when
building its candidate tables (9.2), and the crafting guard refuses one (10.3). Everywhere else a legacy
mod resolves through the ordinary path, which is the whole point of not giving it its own id range.

### 8.3 Tiers, and why the ordinal is the payload's key

A mod's tiers are a LIST on the mod row rather than their own content type, and that is a deliberate
narrowing of the obvious shape. The payload stores `(mod id, tier ordinal)` (3.4), so the ordinal must
be stable for the life of the mod, and the cheapest way to guarantee that is to make the ordinal the
tier's position in a list the publish validator refuses to reorder. A separate `mod_tier` content type
would have given each tier its own id and its own remap rule, which sounds better until you notice the
payload would then carry a `mod_tier` id and a retire of one tier would need its own rule kind.

| Tier field | Value kind | Notes |
|---|---|---|
| `Ordinal` | int | 1 to 255, unique within the mod, IMMUTABLE once published |
| `ItemLevelMin` | int | inclusive, 1 to 65535 |
| `ItemLevelMax` | int | inclusive, `>= ItemLevelMin`, 65535 means no ceiling |
| `Weights` | list of `(tag id, int weight)` | `ServerOnly`, see below |
| `Lines` | list of stat lines | 8.4 |

**Weights are the one `ServerOnly` field in this section.** A weight tells a client the exact odds of
every outcome, which is farmable rather than displayable, so it is omitted from the client manifest
under contracts 11.3 and the publish validator refuses it appearing in a `Client` chunk. Everything else
about a tier is client visible, so a tooltip that says "this tier rolls 10 to 40 at item level 60 and
above" is computed from the same bytes on both sides.

**A weight is keyed by TAG, never by base id.** `Weights` is a list of `(tag id, weight)` pairs, and a
base's weight for this tier is the weight of the FIRST tag in the base's authored tag list that appears
in the pair list, or zero when none does. First rather than sum, because the base's tag order is
authored information (contracts 4.6) and a sum would make the order meaningless. A tier with an empty
pair list can never spawn, which is legal and is how a tier reachable only through crafting is authored.

### 8.4 Stat lines and ranges

A tier carries one or more stat lines, and a line is what turns a stored `ushort` position into a number
the evaluator folds (11.6).

| Line field | Value kind | Notes |
|---|---|---|
| `StatId` | key reference to `stat` | Scope A's engine-range type, contracts 13.1 |
| `Combine` | int | `1` Flat, `2` Increased, `3` More, contracts 13.2 |
| `Min`, `Max` | int | inclusive, in the stat's scaled units or in basis points |
| `TagScope` | ordered list of tag ids | empty means the stat itself, 11.4 |
| `ConditionId` | int | 0 means unconditional, 11.5 |

**One roll position drives every line on the tier.** A tier with two lines ("adds 10 to 40 physical
damage" as a pair) resolves both from the SAME stored position, so the two move together and the payload
stores one `ushort` for the affix rather than one per line. That is contracts 6.4's position applied
literally, and it is what makes a six-affix rare cost eighteen bytes of affix entries (3.7).

Whether a line's units are scaled units or basis points is decided by `Combine` and by nothing else:
`Flat` is in the stat's scaled units, `Increased` and `More` are in basis points where 10,000 is 100
percent (contracts 13.2). The validator checks `Min <= Max` and refuses `Min == Max` on a tier whose
only line is the one being checked ONLY when the mod's kind is a rolled kind, because a fixed line is
exactly how a unique's guaranteed value is authored (8.6).

### 8.5 `rarity_rule`

| Field | Value kind | Notes |
|---|---|---|
| `Id` | int | 1 to 255. The payload's kind 130 stores this as ONE byte (3.3) |
| `Key` | string key | |
| `DisplayFormatKey` | localized text key | |
| `MinAffixes`, `MaxAffixes` | int | total across every kind |
| `MaxPrefixes`, `MaxSuffixes` | int | per kind, `Kind` 1 and 2 of 8.2 |
| `MaxOfKind` | list of `(kind, int)` | for kinds above 2 |
| `NameWordPositions` | int | 0 means the item keeps its base name, above 0 rolls that many words |
| `Weights` | list of `(tag id, int weight)` | `ServerOnly`, the same shape as 8.3 |
| `UpgradeFrom` | key reference to `rarity_rule` | the rarity this one is reachable from, 0 for none |

**The id is a byte and that is a format constraint, not a preference.** Kind 130 is `[RarityId: byte]`,
pinned by contracts 9.8's `82 01 03`, so 255 rarities is the ceiling forever. That is generous against
PoE's four and Tibia's zero, and it is recorded in section 21 because raising it is a payload format
change.

`UpgradeFrom` is what a `set rarity` craft primitive walks (10.2) and what a rarity-upgrade currency
composes. It is a single parent rather than a list, so the rarities form a forest and a craft that
raises a rarity has exactly one answer for what comes next.

### 8.6 `unique_template`

| Field | Value kind | Notes |
|---|---|---|
| `Id` | int | stored in payload kind 129 |
| `Key` | string key | |
| `BaseId` | key reference to `item_base` | the base it is built on |
| `NameKey` | localized text key | the unique's own name, replacing the base name |
| `Lines` | list of stat lines | the SAME shape as 8.4, so `Min == Max` is a fixed value |
| `ForcedSockets` | list of `(socket type id, int count)` | seeds kind 132 at generation |
| `ItemLevelMin` | int | the lowest item level it can drop at |
| `Weight` | int | `ServerOnly` |

**A unique is a template plus rolls, not a separate item kind.** The generated item carries kind 129
naming the template, kind 131 carrying the rolled positions for the template's lines, and kind 130
carrying the unique rarity rule. Nothing in the payload format is special. That is what lets a craft
reroll a unique's values without any primitive knowing what a unique is: `reroll values` (10.2) rewrites
positions and never touches kind 129.

**A unique's lines are NOT mods and carry no mod id**, which is the one asymmetry in the format and it
is worth naming. Kind 131's entries are `(mod id, tier, position, flags)`, so a unique's line needs a
mod id to sit there. The answer is that a unique template's lines are authored as ORDINARY MOD ROWS with
a single tier, weight zero everywhere (so the generator can never roll them onto an ordinary item) and a
group that keeps them off a rare. That costs one mod row per unique line and buys a payload with no
second affix shape, no second decode path and no second remap story. It is the single most important
consequence of 3.4 for an author to understand, so 8.10 restates it in the authoring checklist.

### 8.7 `socket_type`

| Field | Value kind | Notes |
|---|---|---|
| `Id` | int | stored in a socket entry's `SocketTypeId` (3.5) |
| `Key` | string key | |
| `DisplayFormatKey` | localized text key | |
| `AcceptTags` | ordered list of tag ids | a contained item must carry at least one |
| `RejectTags` | ordered list of tag ids | a contained item must carry none |
| `MaxNestedBytes` | int | 0 means `MaxInstancePayloadBytes`, else a tighter per-socket cap |

Gate 0 decision 2 says socket types DO restrict, and that the restriction is content rather than code.
`AcceptTags` and `RejectTags` are that restriction, evaluated by the socket primitive (10.2) and by
nothing else. Reject wins over accept, checked in that order, so a type that accepts `gem` and rejects
`corrupted` is one row.

`MaxNestedBytes` exists because of 3.5's arithmetic: a six socket item has about 59 bytes per nested
payload before the 512 byte cap binds, and a socket type that admits a deep item makes that ceiling a
runtime surprise. Authoring the per socket budget turns it into a publish-time fact and a refusal at the
moment of socketing rather than a refusal at the moment of encoding.

### 8.8 `crafting_currency` and `rare_name_word`

`crafting_currency` is section 10's row and its fields are listed there (10.4), because a row with no
primitive vocabulary to read it against is a list of opaque columns. It registers here so the type id is
in one table.

`rare_name_word`:

| Field | Value kind | Notes |
|---|---|---|
| `Id` | int | stored in payload kind 134 |
| `Key` | string key | |
| `TextKey` | localized text key | the word itself, localized like everything player facing |
| `Position` | int | 1 to 255, which slot in the name it may fill |
| `Weights` | list of `(tag id, int weight)` | `ServerOnly` |

A rare name is `NameWordPositions` words (8.5) drawn one per position, each from the words whose
`Position` matches and whose weight against the base's tags is above zero. Kind 134 stores the word ids
in POSITION ORDER, so the name is reproducible from the payload with no re-roll, which is contracts
14.3's rule applied to a name.

**The composed name is localization's problem, not the payload's.** Contracts 12.3 owns composed names,
so kind 134 stores ids and the display layer composes them. A language whose adjective follows its noun
composes the same three ids differently, which is exactly why the words are ids rather than a string.

### 8.9 Registration, validation and what publish refuses

The seven validators run inside contracts 10.4's one validator and add these checks to its minimum list.
None of them relaxes anything the contract requires.

1. A `mod` tier's `ItemLevelMin <= ItemLevelMax`, and the tier ordinals within one mod are unique and
   unchanged since the previous published version. A REORDER is refused, because the ordinal is in every
   stored payload (3.4).
2. A stat line's `StatId` resolves, its `Combine` is 1, 2 or 3, and `Min <= Max`.
3. A `mod_group`'s `MaxPerItem` is at least 1.
4. A `rarity_rule`'s `MinAffixes <= MaxAffixes`, `MaxPrefixes + MaxSuffixes >= MaxAffixes`, and its
   `UpgradeFrom` chain has no cycle.
5. A `unique_template`'s `BaseId` resolves and every line's mod row has weight zero on every tier, which
   is the check that keeps a unique line off an ordinary rare.
6. A `socket_type`'s `AcceptTags` and `RejectTags` are disjoint.
7. A `rare_name_word`'s `Position` is at least 1, and for every rarity rule with `NameWordPositions = N`
   and every base tag reachable at that rarity, every position 1 to N has at least one word with a
   non-zero weight. That last one is the check that stops a publish producing an item whose name cannot
   be rolled, and it is the expensive one: it is a cross product over rarities, positions and tags, run
   once per publish, which is the right place for it.

### 8.10 The authoring checklist, and the four reference games on these seven types

An author adding one affix touches exactly one row. An author adding one unique touches one
`unique_template` row plus one `mod` row per line. An author adding a rarity touches one `rarity_rule`
row and, if it names items, N `rare_name_word` rows. Nothing above needs an engine release.

What each reference game authors, which is the concrete form of the claim that these are shapes rather
than PoE:

- **OSRS.** Nothing. Zero mod rows, zero rarity rules, zero uniques. Every item is a base with instance
  id 0, and all seven types register with an empty row set, which the validator permits because every
  cross-check above is vacuous over an empty set.
- **Tibia.** Zero mods. Item level and upgrade tier live in the engine range kinds 2 and 8 (3.3), and a
  plus-one upgrade is a craft primitive writing kind 8, not an affix. Socket types register if the game
  wants gems and stay empty if it does not.
- **Mortal Online.** Zero mods again, and this one is the interesting case. A crafted item's identity is
  its MATERIALS (kind 7), and its stats derive from them at evaluation time through game-registered
  modifiers (11.5), so the affix machinery sits unused while the instance machinery carries everything.
  That is contracts 6.3's point about storing inputs rather than derived stats, and it is why kind 7 is
  in the ENGINE range rather than in Scope B's.
- **PoE.** All seven, heavily. This is the only shape that uses the whole section, and the numbers
  section 9 is sized against (2,000 mods, 50,000 bases) are its numbers rather than the fleet's.

**Two authoring traps, written here because both cost a republish to undo.** First, a tier ordinal is
immutable, so inserting a new best tier at the top means appending it with the NEXT ordinal and letting
the ordinal carry no ordering meaning (3.4). Second, a unique's lines are mod rows with zero weight, so
an author who gives one a weight has quietly added it to the rare pool for every base carrying that tag.
Check 5 of 8.9 catches the second at publish. Nothing catches the first, because a reorder is refused
and an append is legal, which is the correct outcome and a surprising one to read for the first time.

## 9. Item generator

### 9.1 What it is and is not

`ItemGenerator` turns (base, item level, source of randomness) into a payload. It does NOT decide WHICH
base drops: that is a loot table, which is Scope A's engine-range content type and is `ServerOnly`. The
seam is deliberate, because a loot roll and an affix roll are different questions and Ruinborne's
`LootRoll` already owns the first one (`c-ruinborne.md:718-734`).

It holds an `IRandomSource` handed to it per CALL rather than per instance, which is a narrowing of
contracts 14.4 and worth the sentence. The contract says a type that rolls takes the source in its
constructor. The generator is a stateless function over precomputed tables, so a per-call source lets one
generator serve a live server on the cryptographic source and a replay tool on a seeded one without
building two, and it keeps "does this method roll" answerable from the signature, which is the property
14.4 actually wants.

### 9.2 The precomputed tables, and what they cost

The naive table is keyed by (base, item level) and it does not fit. At 50,000 bases and 100 item levels
that is 5,000,000 candidate arrays, and at even 500 candidates each it is tens of gigabytes. The shape
below is keyed so that the BASE COUNT contributes almost nothing, which is the whole trick.

**Two levels. Tag-band tables, built once, and a memoized merge per tag signature.**

1. **Bands.** Collect every distinct `ItemLevelMin` and `ItemLevelMax + 1` across every tier of every
   mod. Sort them. The intervals between consecutive values are the BANDS, and within one band no tier's
   gate changes, so the live tier set is constant. At 2,000 mods and 8 tiers each the boundary count is
   bounded by 32,000 and in practice is the authored level curve, tens rather than thousands.
2. **Tag-band tables.** For each `(tag id, band)` pair, an array of `(mod id, tier ordinal, weight)` for
   every tier that is live in that band and names that tag with a non-zero weight, sorted ascending by
   (mod id, tier ordinal). Sorted rather than insertion ordered, because the sort is what makes the
   weighted pick reproducible from a seed and independent of pack load order (contracts 4.3).
3. **The merge, at roll time.** A base's candidate set is the merge of the tables for its tags, walked in
   the base's AUTHORED tag order, taking the first weight found for each `(mod id, tier ordinal)` and
   ignoring later ones. That is 8.3's first-tag-wins rule executed rather than precomputed.
4. **The memo.** The merge is keyed by (tag signature, band), where the tag signature is the base's
   ordered tag list interned to an int. Bases sharing a tag list share a merge. A bounded dictionary
   holds the most recent 4,096, which at a live server's hot base set is effectively a full cache and at
   a pathological one degrades to a merge per roll, which is still microseconds.

**The arithmetic, at the owner's scale of 50,000 bases and 2,000 mods.** Take 8 tiers per mod (16,000
tiers), 5 tag weights per tier and 64 bands, with a tier spanning on average a third of them.

| Quantity | Formula | Result |
|---|---|---|
| Tag-band table entries | `16,000 tiers * 5 tags * 21 bands` | about 1.7 million |
| Bytes at 12 per entry | `1.7M * 12` | **about 20 MB** |
| Distinct tag signatures | authored, bounded by base count | a few hundred |
| Memo entries held | `4,096 * (merged array + header)` | **about 4 MB at 200 entries each** |
| Base-derived memory | `50,000 * 8` for the signature intern | **400 KB** |
| Build: appends | one per table entry | 1.7 million |
| Build: sort | 1.7M entries across about 19,000 buckets | dominated by the appends |

**About 25 MB resident and a build measured in low hundreds of milliseconds**, both of which belong in
16's budget table as targets rather than as claims, because neither is measured. The number that matters
for the SHAPE is the last row of the top half: 400 KB for fifty thousand bases. A base costs eight bytes
because it never enters a table, only its tag list does. That is what makes the design survive the
owner's "millions of owned items" over a large catalog.

**The tables are built when a pack LOADS and are immutable after.** A publish builds new tables beside
the old ones and swaps the reference, so a roll in flight finishes against the tables it started with,
which is the same live-swap shape `GrimhollowEconomy.Current` uses (`b-grimhollow.md:56-59`). Weights are
`ServerOnly` (8.3), so a CLIENT builds none of this and holds none of it.

### 9.3 The random source, restated at the call site

`IRandomSource` is contracts 14.1 verbatim. Three of its four members are used here:
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

GenerationResult Generate(in GenerationContext context, IRandomSource random);
```

Every step is numbered because the ORDER of the draws is the reproducibility contract.

1. **Resolve the band** from `context.ItemLevel` by binary search over the band boundaries. No draw.
2. **Resolve the unique**, if `ForcedUniqueTemplateId` is non-zero. Skip to step 9 with the template's
   lines as the affix list and its `ForcedSockets` as the socket list. No draw. A unique is FORCED by the
   caller rather than rolled here, because deciding that a unique drops is the loot table's job (9.1).
3. **Roll the rarity**, unless `ForcedRarityId` is non-zero. One `NextInt(0, total)` over the rarity
   rules' weights against the base's tags, using 8.3's first-tag-wins rule.
4. **Roll the affix count.** One `NextInt(rule.MinAffixes, rule.MaxAffixes + 1)`.
5. **Roll the prefix and suffix split.** For each of the `count` picks in turn, decide the kind FIRST:
   one `NextInt(0, openKindTotal)` over the kinds still under their per kind cap, weighted by the number
   of candidates of that kind. Kind before mod, so a rarity permitting three prefixes and three suffixes
   does not produce six prefixes because prefixes happen to outnumber suffixes in the pool.
6. **Filter the pool** for this pick: drop every candidate whose kind is not the chosen one, whose mod id
   is already on the item, whose `mod_group` is at `MaxPerItem`, or whose mod row carries `Legacy`. No
   draw. The filter is a forward pass over the memoized merged array, producing a cumulative weight
   array in the same order.
7. **Pick the mod and tier.** One `NextInt(0, weightTotal)` and a walk of the cumulative array. When
   several tiers of one mod are live in the band, they are separate candidates and the weights decide,
   which is how "a better tier is rarer" is authored rather than coded.
8. **Roll the position.** One `NextRollPosition()` per affix. Record `(mod id, tier ordinal, position,
   flags 0)`. Repeat steps 5 to 8 until `count` affixes are placed. A pick whose filtered pool is EMPTY
   places nothing, consumes no further draw for that pick, and the item ends with fewer affixes than the
   count asked for, which is a legal outcome and is reported in the result rather than retried.
9. **Sort the affix list ascending by mod id** (3.4). No draw.
10. **Roll the rare name.** For each position 1 to `rule.NameWordPositions`, one `NextInt(0, total)` over
    that position's words weighted against the base's tags. Record the word ids in position order.
11. **Seat the sockets.** A unique's `ForcedSockets` verbatim, otherwise the base's authored socket
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
from anything except this event. At 69 bytes for a rare (3.8) plus about 14 of header, an
`item-generated` event is about 83 bytes against the 128 events per operation cap
(`JournalLimits.cs:10`), so a drop burst of twenty rares is about 1.7 KB of events, which is inside the
one-tick batch budget of 6.4.

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
| 5 | `SetRarity` | rarity id or 0 for `UpgradeFrom` | writes kind 130, then trims or fills affixes to the new rule's counts |
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
| 2 | `RarityIsAtMost` | rarity id | kind 130's `UpgradeFrom` chain reaches it |
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
| 14 | `NotLegacy` | | no affix on the item names a `Legacy` mod row |
| 15 | `IsCorruptible` | | kind 1 bit 0 is clear, the standing refusal every currency inherits |

**Guards are ANDed and there is no OR, no NOT and no nesting.** An OR is two currency rows, which is one
more authored row and no evaluator. That refusal is the same one contracts 4.6 makes about tags and the
same one the guard vocabulary makes about itself: a small closed language whose every refusal is
attributable beats an expression tree whose failures need explaining.

**`NotLegacy` is the crafting guard contracts 5.4 names**, and it is a STANDING guard rather than an
authored one: every primitive that would add a mod refuses a legacy row regardless of the currency's guard
set, and `NotLegacy` as an authored guard is the stronger statement that the item must carry none at all.

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

### 10.4 A `crafting_currency` row

| Field | Value kind | Notes |
|---|---|---|
| `Id` | int | |
| `Key` | string key | |
| `NameKey`, `DescriptionKey` | localized text key | |
| `ConsumesDefinitionId` | key reference to `item_base` | what is spent, 0 for a free operation |
| `ConsumesCount` | int | |
| `TargetGuards` | guard set | evaluated once, before any step |
| `Steps` | ordered list | each step is `(guard set, primitive or operation id, parameters)` |
| `MaxSteps` | int | a publish-checked ceiling, at most 16 in v1 |

A step naming an id at or above 1,024 is a GAME operation rather than a primitive (10.5). The two share
the step list so a currency can mix them, which is the point of having a registry at all.

**Worked example, and it is deliberately not a PoE currency.** A "whetstone" that repairs and adds one
quality point, refusing a corrupted or unidentified item:

```
ConsumesDefinitionId = whetstone, ConsumesCount = 1
TargetGuards = [ IsCorruptible, IsIdentified(1) ]
Steps:
  1. guards [ QualityBetween(0, 19) ]  ->  SetQuality(delta +1)
  2. guards [ ]                        ->  Repair(0)
```

Two properties of that encoding are worth naming. A step whose guard set is false is SKIPPED rather than
refusing the whole craft, so the whetstone repairs an item already at quality 20. And `TargetGuards`
failing refuses the whole craft and consumes NOTHING, which is the difference between a precondition and a
step guard and is the reason the row carries both.

### 10.5 Game operations through a registry

```csharp
public interface ICraftOperation
{
    int Id { get; }                                  // >= 1024
    CraftRefusal? Apply(ref CraftWorkingCopy copy, ReadOnlySpan<int> parameters, IRandomSource random);
}

CraftingRegistry.Register(ICraftOperation operation);   // process start, frozen at first pack load
```

The registry follows `InstancePropertyRegistry` (3.3), which follows `ReplicationRegistry.Register`
(`TileProtocol.Components.cs:122`): registration once, at process start, frozen at the first pack load,
a later registration throws, and the id ranges below 1,024 are the engine's and cannot be registered into.

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

Before AND after, which is 42 bytes more than after alone on a rare and is worth every one of them. The
page is rewritten whole on the next commit, so without the before bytes nothing in the durable record can
answer what a craft changed, which is the exact failure Ruinborne's audit has (`c-ruinborne.md:328-346`,
audit rows recording that a row changed and carrying no values). Contracts 4.7 makes the same argument for
the content audit, and this is it applied to the instance audit.

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
   space with `Legacy` set (contracts 5.4), leaves the original id carrying the new ranges, and emits one
   `MovedToLegacy` remap rule (contracts 8.2 kind 3) from the original id to the legacy copy.
5. Every stored page whose stamp is older than that publish moves its entries onto the legacy id at load
   (5.5 step 2), lazily, and is rewritten on its next commit. Items generated after the publish carry the
   original id with its new ranges. The two coexist forever.

**The rule set's idempotence is what makes step 5 safe, and there is one trap.** Contracts 8.3 forbids a
rule whose `ToId` is an earlier rule's `FromId` for the same type. A second keep-legacy publish on the SAME
mod would emit a rule from the original id to a second legacy copy, which is legal, and a rule from the
FIRST legacy copy to anywhere would not be. So a legacy copy is terminal by construction: it can never be
generated, never be crafted, and never be the source of another rule. The publish validator's existing walk
catches an author who tries.

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

**A tag scope is an AND over the context's tags.** A line whose scope is `[fire, spell]` applies only when
the evaluation context carries both. An empty scope applies always, which is the common case and costs a
length check. Scope is the mechanism for "increased fire damage with spells" and for Grimhollow's
`CanBeAHatchet` predicate becoming a row (contracts 4.6), and it is the ONLY relation between a modifier
and a context: there is no scope expression, no negation and no OR, for the same reason 10.3 gives.

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
6. value     = (flat * increased + 5000) / 10000                 // round half up
7. for each More m, in the step 1 order:
       value = (value * (10000 + m.Value) + 5000) / 10000
8. value     = clamp(checked int, stat.Min, stat.Max)
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

**`+ 5000` before the divide is round half up, on a NON-NEGATIVE numerator only.** A negative `flat` with a
positive `increased` makes the numerator negative, and C# integer division truncates toward zero, so
`(-15000 + 5000) / 10000` is 0 rather than -1. The evaluator therefore computes the sign, rounds the
magnitude and restores the sign, and the test in 17.7 pins a negative case, because a stat that can go
negative (a resistance, a cold damage penalty) is ordinary and the trap is silent.

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
