# Container Event Replay and Bulk Slide Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an event sourced host reconstruct complete item containers from journal events, including instance payload grants, crafts, and a 1,000-shelf bank compaction.

**Architecture:** Decode the current canonical operation bodies without changing their bytes. Emit version 2 grant and craft event envelopes with the payload and location data that version 1 omitted. Share prevalidated operation semantics between live `ContainerCommitBuilder` application and replay. Add one bulk slide operation whose single event and multi-page budget cover bank compaction.

**Tech Stack:** .NET 10, C#, KhaozEngine.ItemInstances, KhaozEngine.ItemInstances.Journal, KhaozEngine.WorldStore.Journal, xUnit

**Spec:** `docs/design/ITEM-RARITY-PRESENTATION-AND-EVENT-REPLAY-DESIGN-2026-09-23.md`, Replayable container operations and Compatibility and verification

## Global Constraints

- Work in the existing `feature/item-rarity-color` worktree after the colour plan's focused commits. Preserve concurrent work.
- `ContainerCommitBuilder.TryBuildParts` and `IPagedContainerWorkingCopy` already exist. Reuse both. Do not rebuild their APIs.
- Existing event type strings, operation kind numbers 1 through 6, version 1 bodies, and `ItemCraftedEvent` audit body bytes remain readable and unchanged.
- `JournalEvent.EventSchemaVersion` is the envelope version. New `item-granted` and `item-crafted` events use version 2. The inner `ItemCraftedEvent` version 1 audit body remains nested inside the new crafted event.
- Version 1 grant with a nonzero instance ID and version 1 crafted events cannot be independently replayed because their bytes omit the payload or slot. Reject them explicitly in the replay decoder. The existing audit reader continues to read the old crafted body.
- A replay uses only the recorded event and the prior container state. It does not reroll, mint an ID, read today's catalog, or mutate a page after detecting invalid input.
- Bank slide moves complete `ItemSlot` values. The page content version remains a page stamp, not a field in `ItemSlot`.
- The builder counts every dirtied page and its byte growth before admitting a slide. One 1,000-shelf shift stays below the 128-event and 64-projection-write engine maxima.
- One code task ends with its focused Release tests green and an explicit commit. Keep KESIZE under the existing baseline and preserve zero warnings.
- The staged engine version is `20.2.0` against tag `v20.1.0` at plan time. Re-read live main and tags before release work. Do not create a release tag without the repository's release gate.

## Review Focus

- A crafted event with a valid after payload but a mismatched before payload must refuse without changing the target. Task 3 tests it.
- An item granted with a nonzero instance ID and empty payload must still be distinguishable from an old grant that omitted the payload. Task 2 tests the version 2 envelope.
- A slide that crosses a page boundary must budget both pages before mutation. Task 4 tests it.
- An overlapping left or right slide must preserve source order and reject an occupied leading destination. Task 4 tests it.
- A malformed UTF-8 container name, nonminimal varint, trailing byte, or event type and kind mismatch must refuse as data, not throw. Task 1 tests each.

## File Structure

- `KhaozEngine.ItemInstances.Journal/ContainerOperation.cs` owns kind 7 and the canonical parameter grammar.
- `KhaozEngine.ItemInstances.Journal/ContainerOperationEventCodec.cs` owns event envelopes, v1 readers, v2 grant and craft writers and readers, and exact-body validation.
- `KhaozEngine.ItemInstances.Journal/ContainerOperationApplier.cs` owns preflight and mutation shared by live and replay paths.
- `KhaozEngine.ItemInstances.Journal/ContainerCommitBuilder.Operations.cs` delegates semantics to the applier and calculates the full slide budget.
- `KhaozEngine.ItemInstances.Journal/ContainerCommitBuilder.cs` records each event's schema version from the codec.
- `KhaozEngine.ItemInstances.Journal/ItemInstanceEvents.cs` appends the durable `item-slid` name and maps kind 7.
- `KhaozEngine.Server.Tests/ItemInstances/ContainerOperationEventCodecTests.cs` pins event bytes, versioning, and hostile inputs.
- `KhaozEngine.Server.Tests/ItemInstances/ContainerOperationReplayTests.cs` compares live and replayed pages and legacy refusals.
- `KhaozEngine.Server.Tests/ItemInstances/ContainerSlideTests.cs` pins full bank moves, overlap, page budgets, and limits.
- `KhaozEngine.ItemInstances.Journal/README.md`, `docs/USING-KHAOZENGINE.md`, `docs/INDEX.md`, and `CHANGELOG.md` document the shipped contracts and staged version.

---

### Task 1: Decode existing container event bodies without changing their bytes

**Files:**

- Modify: `KhaozEngine.ItemInstances.Journal/ContainerOperation.cs`
- Create: `KhaozEngine.ItemInstances.Journal/ContainerOperationEventCodec.cs`
- Create: `KhaozEngine.Server.Tests/ItemInstances/ContainerOperationEventCodecTests.cs`

**Interfaces:**

- Consumes: `ContentVarint.TryRead`, `InstanceIdAllocator`, `ContainerOperation.ToCanonicalArray`, `ItemInstanceEvents.EventTypeOf`
- Produces: `ContainerOperation.TryReadCanonical(ReadOnlySpan<byte>, out ContainerOperation, out string?)` and `ContainerOperationEventCodec.TryRead(string eventType, int schemaVersion, ReadOnlyMemory<byte> body, out ContainerOperation, out string?)`

- [ ] **Step 1: Add failing exact-byte and refusal tests.** Encode one each of Move, Split, Merge, plain Grant, and Take using `ToCanonicalArray`. Decode through the proposed event codec and assert every field and exact re-encode. Mutate the body to contain a truncated or nonminimal varint, malformed UTF-8 name, trailing byte, wrong kind for event type, and unknown schema version. Assert `false` with a stable reason and no throw. `item-crafted` version 1 remains readable through `ItemCraftedEvent.TryRead` but the replay codec answers its explicit legacy-location refusal.

```csharp
ContainerOperation move = ContainerOperation.Move(Bank, 2, Bag, 4, 1, Instance);
byte[] bytes = move.ToCanonicalArray();
Assert.True(ContainerOperationEventCodec.TryRead(ItemInstanceEvents.Moved, 1,
    bytes, out ContainerOperation read, out string? reason), reason);
Assert.Equal(bytes, read.ToCanonicalArray());
```

- [ ] **Step 2: Run the new test class and confirm a missing API failure.**

```bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~ContainerOperationEventCodecTests
```

- [ ] **Step 3: Implement one canonical parser and the v1 event dispatch.** Parse fields in `FieldsOf(kind)` order, decode names with strict UTF-8, require exact end of body, and map event type to the one permitted kind. Catch input validation exceptions and return a closed reason token. Keep `WriteCanonical` and all existing version 1 bytes unchanged. The replay codec rejects v1 instance grants and v1 crafted events with distinct reason tokens.

```csharp
public static bool TryRead(string eventType, int schemaVersion,
    ReadOnlyMemory<byte> body, out ContainerOperation operation, out string? reason)
{
    operation = default;
    if (schemaVersion != 1) { reason = "event-version"; return false; }
    if (StringComparer.Ordinal.Equals(eventType, ItemInstanceEvents.Crafted))
    { reason = "legacy-location-omitted"; return false; }
    if (!ContainerOperation.TryReadCanonical(body.Span, out operation, out reason)) return false;
    if (!StringComparer.Ordinal.Equals(operation.EventType, eventType))
    { reason = "event-kind"; operation = default; return false; }
    if (operation.Kind == ContainerOperationKind.Grant && operation.InstanceId != 0)
    { reason = "legacy-payload-omitted"; operation = default; return false; }
    return true;
}
```

- [ ] **Step 4: Run the focused tests green and commit.**

```bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~ContainerOperationEventCodecTests
git add KhaozEngine.ItemInstances.Journal/ContainerOperation.cs KhaozEngine.ItemInstances.Journal/ContainerOperationEventCodec.cs KhaozEngine.Server.Tests/ItemInstances/ContainerOperationEventCodecTests.cs
git commit -m "journal(items): read canonical container events"
```

### Task 2: Make grants and crafts self-contained in new event versions

**Files:**

- Modify: `KhaozEngine.ItemInstances.Journal/ContainerOperationEventCodec.cs`
- Modify: `KhaozEngine.ItemInstances.Journal/ContainerCommitBuilder.Operations.cs`
- Modify: `KhaozEngine.ItemInstances.Journal/ContainerCommitBuilder.cs`
- Modify: `KhaozEngine.Server.Tests/ItemInstances/ContainerOperationEventCodecTests.cs`
- Modify: `KhaozEngine.Server.Tests/ItemInstances/ContainerCommitFixtures.cs`
- Modify: `KhaozEngine.Server.Tests/ItemInstances/ContainerCommitBuilderTests.cs`

**Interfaces:**

- Consumes: Task 1 canonical parser, `ItemCraftedEvent.TryRead`, `ContainerOperation.Payload`, `ContainerOperation.EventPayload`
- Produces: `ContainerOperationEventCodec.Write(in ContainerOperation operation)` returning `(int SchemaVersion, byte[] Body)` and v2 reads from the same `TryRead` entry point

- [ ] **Step 1: Add failing v2 event tests.** A rare grant includes the canonical operation and its payload. A grant with a nonzero instance ID and an empty payload remains explicit in v2. A craft includes the canonical operation and the existing `ItemCraftedEvent` body, so its target and currency slots replay from the wrapper. Replace the builder fixture's arbitrary 127-byte craft body with a valid `ItemCraftedEvent.ToArray()` and adjust its byte-limit assertion to the new measured body size. Test a malformed inner audit body, a body with trailing bytes, and an event type that disagrees with its canonical kind. Assert that the builder still emits version 1 for Move, Split, Merge, and Take.

```csharp
ContainerOperation grant = ContainerOperation.Grant(Bag, 4, Sword, 1,
    Instance, Payload());
(int schema, byte[] body) = ContainerOperationEventCodec.Write(grant);
Assert.Equal(2, schema);
Assert.True(ContainerOperationEventCodec.TryRead(ItemInstanceEvents.Granted,
    schema, body, out ContainerOperation read, out _));
Assert.Equal(grant.Payload.ToArray(), read.Payload.ToArray());
```

- [ ] **Step 2: Run the focused tests red.**

```bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~ContainerOperationEventCodecTests|FullyQualifiedName~ContainerCommitBuilderTests"
```

- [ ] **Step 3: Implement the two version 2 envelopes.** For Grant, write a minimal varint length, canonical operation bytes, a minimal payload length, and exact payload bytes. For Craft, write the canonical operation length and bytes followed by the length and unchanged `ItemCraftedEvent` version 1 body. Decode both with exact-end checks. The builder calls `Write` and uses its returned schema version instead of hardcoding `1`. Validate that the craft inner event's instance ID matches the wrapper and that its after payload is the operation's payload. The inner `CurrencyId` is a content row, not the consumed item definition, so those are not compared.

```csharp
(int eventVersion, byte[] eventBody) = ContainerOperationEventCodec.Write(operation);
_events.Add(new JournalEvent(operation.EventType, eventVersion, eventBody));
```

- [ ] **Step 4: Run the focused tests green and commit.**

```bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~ContainerOperationEventCodecTests|FullyQualifiedName~ContainerCommitBuilderTests"
git add KhaozEngine.ItemInstances.Journal/ContainerOperationEventCodec.cs KhaozEngine.ItemInstances.Journal/ContainerCommitBuilder.Operations.cs KhaozEngine.ItemInstances.Journal/ContainerCommitBuilder.cs KhaozEngine.Server.Tests/ItemInstances/ContainerOperationEventCodecTests.cs KhaozEngine.Server.Tests/ItemInstances/ContainerCommitFixtures.cs KhaozEngine.Server.Tests/ItemInstances/ContainerCommitBuilderTests.cs
git commit -m "journal(items): carry grant and craft replay data"
```

### Task 3: Share prevalidated operation semantics with replay

**Files:**

- Create: `KhaozEngine.ItemInstances.Journal/ContainerOperationApplier.cs`
- Modify: `KhaozEngine.ItemInstances.Journal/ContainerCommitBuilder.Operations.cs`
- Create: `KhaozEngine.Server.Tests/ItemInstances/ContainerOperationReplayTests.cs`

**Interfaces:**

- Consumes: Tasks 1 and 2 event reads, `IPagedContainerWorkingCopy`, `ItemSlot`, `InstanceStacking`
- Produces: `ContainerOperationApplier.TryApply(IReadOnlyDictionary<string, IPagedContainerWorkingCopy>, in ContainerOperation, out string?)` used by builder and by an event sourced reducer

- [ ] **Step 1: Add replay equivalence tests.** For each kind, seed two identical paged containers, apply one operation through the live builder, take its event, decode it, apply through `ContainerOperationApplier`, and compare all encoded page bytes. Include full payload Move and Grant, a partial plain Take, a merge, and a Craft with currency. A Craft whose before payload mismatches the held slot must refuse without modifying target or currency. A missing destination container and a full capacity gate must also refuse before mutation.

```csharp
Assert.True(ContainerOperationEventCodec.TryRead(entry.EventType,
    entry.EventSchemaVersion, entry.Payload, out ContainerOperation replay, out _));
Assert.True(ContainerOperationApplier.TryApply(replayCopies, replay, out string? reason), reason);
for (int page = 0; page < liveBag.PageCount; page++)
    Assert.Equal(EncodePage(liveBag, page), EncodePage(replayedBag, page));

static byte[] EncodePage(PagedItemContainer container, int index)
{
    ItemContainerPage page = container.Pages[index];
    var entries = new PageSlotInput[ItemContainerPageCodec.ContainerPageSlots];
    int count = page.CopyEntriesTo(entries);
    return ItemContainerPageCodec.Encode(page.PageIndex, page.FirstSlot,
        page.SlotCount, page.ContentVersion, entries.AsSpan(0, count));
}
```

- [ ] **Step 2: Run the new replay tests red.**

```bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~ContainerOperationReplayTests
```

- [ ] **Step 3: Move semantics to one preflight then mutation path.** Preflight checks names, slot bounds, counts, instance IDs, destination emptiness or merge permission, capacity, quarantine, and Craft before payload and currency before writing any slot. Apply only after all checks pass. The live builder delegates to the same applier and preserves its existing throwing caller-bug contract by turning a `false` result into `ArgumentException`. Replay returns `false` and a stable reason for bad stored data. Neither path silently replaces a payload or rerolls content.

```csharp
if (!ContainerOperationApplier.TryApply(_containers, operation, out string? reason))
    throw new ArgumentException(reason, nameof(operation));
```

- [ ] **Step 4: Run replay and existing builder tests green and commit.**

```bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~ContainerOperationReplayTests|FullyQualifiedName~ContainerCommitBuilderTests"
git add KhaozEngine.ItemInstances.Journal/ContainerOperationApplier.cs KhaozEngine.ItemInstances.Journal/ContainerCommitBuilder.Operations.cs KhaozEngine.Server.Tests/ItemInstances/ContainerOperationReplayTests.cs
git commit -m "journal(items): replay container operations through one applier"
```

### Task 4: Add one bulk bank slide with full page accounting

**Files:**

- Modify: `KhaozEngine.ItemInstances.Journal/ContainerOperation.cs`
- Modify: `KhaozEngine.ItemInstances.Journal/ItemInstanceEvents.cs`
- Modify: `KhaozEngine.ItemInstances.Journal/ContainerOperationEventCodec.cs`
- Modify: `KhaozEngine.ItemInstances.Journal/ContainerOperationApplier.cs`
- Modify: `KhaozEngine.ItemInstances.Journal/ContainerCommitBuilder.Operations.cs`
- Create: `KhaozEngine.Server.Tests/ItemInstances/ContainerSlideTests.cs`

**Interfaces:**

- Consumes: Task 3 applier and the existing page-index geometry in `IPagedContainerWorkingCopy`
- Produces: `ContainerOperationKind.Slide = 7`, `ContainerOperation.Slide(string container, int firstSourceSlot, int firstDestinationSlot, int count)`, `ItemInstanceEvents.Slid = "item-slid"`

- [ ] **Step 1: Add failing slide tests.** Cover a 999-slot left shift after taking bank slot 0, an overlapping right shift with empty trailing space, distinct instance payloads, a destination outside address space, an occupied leading destination, a request that would touch 65 pages under a 64-write limit, and an event budget that accepts the 10-page bank as one event. Assert source order, final empty tail, exact replay page bytes, and no mutation on preflight refusal.

```csharp
ContainerOperation slide = ContainerOperation.Slide(Bank, 1, 0, 999);
Assert.True(batch.Apply(slide));
Assert.Equal(1, batch.EventCount);
Assert.True(batch.ProjectionWriteCount <= 10);
```

- [ ] **Step 2: Run the slide tests red.**

```bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~ContainerSlideTests
```

- [ ] **Step 3: Append kind 7 and its durable event name.** Canonical slide parameters are container, first source slot, first destination slot, and count. A left overlap moves low to high. A right overlap moves high to low. Preflight the entire source run and the destination fringe before changing a slot. Walk every page intersecting source or destination when projecting write count and aggregate bytes, de-duplicating shared pages. The recorded slide event is version 1 canonical bytes and is decoded by Task 1's parser.

```csharp
public static ContainerOperation Slide(string container, int firstSourceSlot,
    int firstDestinationSlot, int count) => new()
{
    Kind = ContainerOperationKind.Slide,
    Container = container,
    Slot = firstSourceSlot,
    DestinationSlot = firstDestinationSlot,
    Count = count,
};
```

- [ ] **Step 4: Run slide, replay and builder tests green and commit.**

```bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter "FullyQualifiedName~ContainerSlideTests|FullyQualifiedName~ContainerOperationReplayTests|FullyQualifiedName~ContainerCommitBuilderTests"
git add KhaozEngine.ItemInstances.Journal/ContainerOperation.cs KhaozEngine.ItemInstances.Journal/ItemInstanceEvents.cs KhaozEngine.ItemInstances.Journal/ContainerOperationEventCodec.cs KhaozEngine.ItemInstances.Journal/ContainerOperationApplier.cs KhaozEngine.ItemInstances.Journal/ContainerCommitBuilder.Operations.cs KhaozEngine.Server.Tests/ItemInstances/ContainerSlideTests.cs
git commit -m "journal(items): compact a container run in one event"
```

### Task 5: Prove stored replay, document the release, and pack the engine

**Files:**

- Modify: `KhaozEngine.Server.Tests/ItemInstances/ContainerCommitJournalTests.cs`
- Modify: `KhaozEngine.ItemInstances.Journal/README.md`
- Modify: `docs/USING-KHAOZENGINE.md`
- Modify: `docs/INDEX.md`
- Modify: `CHANGELOG.md`
- Modify when no version is staged: `Directory.Build.props`, root `README.md`, and guarded version examples in `docs/USING-KHAOZENGINE.md`

**Interfaces:**

- Consumes: Tasks 1 through 4 and the companion colour plan
- Produces: tested, documented engine package bytes for Grimhollow to adopt after an engine release

- [ ] **Step 1: Add a real journal store replay test.** Commit a rare Grant, cross-container Move, Craft, Take, and 1,000-shelf Slide through the builder, then read events from the store, start from the prior snapshot, replay through the codec and applier, and compare each final page against the stored projection. Include a multi-stream commit composed from `TryBuildParts` and assert it validates against full journal limits.

```csharp
Assert.True(ContainerOperationEventCodec.TryRead(stored.EventType,
    stored.EventSchemaVersion, stored.Payload, out ContainerOperation operation, out _));
Assert.True(ContainerOperationApplier.TryApply(rebuilt, operation, out _));
Assert.Equal(await SectionBytesAsync(store, 0), EncodePage(replayedBank, 0));
```

- [ ] **Step 2: Run the journal test against the complete engine path.** A failure names a contract gap in Tasks 1 through 4 and is fixed there before release docs are written. This test must pass before Step 3.

```bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~ContainerCommitJournalTests
```

- [ ] **Step 3: Update and commit living docs and the staged changelog.** Document v2 grant and craft bodies, legacy replay refusals, the replay API, slide semantics and limit accounting in the journal package README and consumer guide. Mark the design implemented in `docs/INDEX.md`. Append to the existing staged `20.2.0` `CHANGELOG.md` entry if it remains staged. If the current version is tagged by execution time, take the next free additive minor and update its guarded declarations and changelog in the same commit. Run the full Markdown name and behavior sweep required by `docs/CONTRIBUTOR-RULES.md`.

```bash
git add KhaozEngine.Server.Tests/ItemInstances/ContainerCommitJournalTests.cs KhaozEngine.ItemInstances.Journal/README.md docs/USING-KHAOZENGINE.md docs/INDEX.md CHANGELOG.md
git commit -m "docs(items): publish rarity and replay contracts"
```

- [ ] **Step 4: Run all required checks and pack through the guard script.** Fetch, merge local `main` and `origin/main` into this branch, and resolve conflicts here. Re-read version and tags before choosing a staged version. Build and test Release after those merges.

```bash
git fetch origin
git merge main
git merge origin/main
dotnet build KhaozEngine.slnx -c Release
dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
scripts/pack-local-feed.sh
scripts/check-local-feed.sh
```

- [ ] **Step 5: Integrate and meet the release gate.** The Step 3 command is for riding staged `20.2.0`. If a new version was required, include `Directory.Build.props` and every guarded version declaration in that commit and use a subject such as `release(20.3.0): add item rarity display and replay` when `20.3.0` is the next free version. From the existing main checkout, merge the verified branch and push `main`. Release tagging remains a separate user-started action unless the repository's explicit pinned-and-waiting consumer exception applies after the Grimhollow dependency is prepared. Do not hand-create a tag.

```bash
git merge --ff-only feature/item-rarity-color
git push origin main
```
