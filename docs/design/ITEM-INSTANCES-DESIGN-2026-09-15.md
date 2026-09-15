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
