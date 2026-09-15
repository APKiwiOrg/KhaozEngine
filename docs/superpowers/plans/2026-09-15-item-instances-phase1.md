# Item Instances Phase 1 Implementation Plan (Scope B)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Ship the durable, byte-level foundation for owned item instances: the canonical tagged payload, the property registry, the instance id allocator, quarantine, the paged container and container codec version 2, the tick-bounded commit batch, and the ground-item and page-sync wire, as two new packages plus changes to three existing ones.

**Architecture:** `KhaozEngine.ItemInstances` (Foundation) owns the payload codec, the property registry, validation, quarantine, visibility, the page codec, the paged container and the remap pass. `KhaozEngine.ItemInstances.Journal` (Server) owns everything that composes a `JournalCommit`, because `Foundation` cannot reference a `Server` package. `KhaozEngine.Items` gains a third `ItemStack` component, `ItemSlot`, and codec version 2 with its version 1 reader. `KhaozEngine.TileWorld.Netcode` gains an item-agnostic fragmenter and a sibling ground component holding opaque bytes, and gains NO items dependency. Nothing in the engine learns what an item means.

**Tech Stack:** .NET 10, `KhaozEngine.Items`, `KhaozEngine.Primitives`, `KhaozEngine.Catalog` (Scope A), `KhaozEngine.WorldStore`, `KhaozEngine.TileWorld.Netcode`, `KhaozEngine.Replication`, xUnit.

**Spec:** `docs/design/ITEM-INSTANCES-DESIGN-2026-09-15.md` (cited below as spec N.N)

**Contracts:** `docs/design/CONTENT-CONTRACTS-DESIGN-2026-09-14.md` (cited as contracts N.N). Where the two disagree the contracts win.

**Scope A spec:** `docs/design/CONTENT-CATALOG-DESIGN-2026-09-15.md` (cited as catalog N.N)

## What this plan calls phase 1, and what it defers

Spec 20 divides Scope B into five phases. **This plan implements the ENGINE HALF, which is spec 20's
phases 1, 2 and 3 taken together**, because those three are one package set, one durable format family
and one wire, and splitting them across three releases would ship a container that cannot commit and a
commit that cannot sync. The plan keeps spec 20's own grouping visible: the group acceptances below are
spec 20's phase acceptances, unchanged.

**Deferred exactly as spec 20 defers them, and out of scope here:**

- **The eighteen affix content types and the item generator** (spec 8 and 9, `ItemGenerator`,
  `GenerationContext`, `GenerationResult`, `ModCandidateTables`). Spec 20 phase 4, gated on Scope A's
  registry and publish path being real.
- **The crafting framework** (spec 10, `CraftPrimitive`, `CraftGuard`, `CraftPlan`, `CraftOutcome`,
  `CraftRefusal`, `CraftingRegistry`, `ICraftOperation`). Spec 20 phase 5.
- **The stat evaluator** (spec 11, `ContentStatEvaluator`, `StatModifierLine`, `StatCombineKind`,
  `StatSourceKey`, `StatContext`, `IStatConditionRegistry`). Spec 20 phase 5.
- **Consumer adoption** (spec 18 and 19). Runs per consumer after this plan lands, not inside it.

Phase 1 is a STRONG BASE rather than a partial catalog (spec 1.2, spec 20). What it settles is every
byte format, every id space, every ordering rule and the stacking test, which are the expensive things
(spec 21). What it does not ship is breadth, which is content the owner authors afterwards.

**Spec test plan rows this plan lands:** 1, 2, 3, 4, 5 (structural half), 8, 9, 11 (engine half), 12,
14, 15, 16 and 17. Rows 6, 7, 10 and 13 belong to the deferred phases and are named where a later phase
picks them up.

## Scope A dependency, per task

Scope A ships `KhaozEngine.Catalog` in its own phase 1, built in five milestones (catalog 18.1).
**Only milestone 1.1 gates anything here.** It ships the registry, the field schema, the codecs,
`ContentVarint`, the hashes, the four pack formats, `RemapRule` and `RemapRuleSet`, `FileSystemPackStore`,
`ContentPackReader`, plus `IContentSnapshot` and `ItemRow` (catalog 2.2). Scope A ships it ahead of
everything that consumes it for exactly this reason.

What this plan consumes from it, by name, all from catalog 2.2 and 2.5:

| Name | Where this plan reads it |
|---|---|
| `IContentSnapshot` | `InstanceValidator.Validate`, the container load path |
| `ContentVersionIdentity` and its `int Number` | the page stamp, spec 5.3 |
| `ContentTypeId`, `ContentKey`, `ContentRow` | reference-target resolution in the validator |
| `ItemRow` | the four hot `item` fields the stacking and capacity paths read |
| `RemapRule`, `RemapRuleKind`, `RemapRuleSet` | the instance remap pass, spec 5.5 step 2 |
| `IContentSnapshot.IsRetired(type, id)` | validator check 13, spec 12.2 |
| `ContentVarint` | every varint this plan writes, contracts 15 |
| `ContentStringCatalog` | the three placeholder `StringId`s of spec 12.3 |
| `IRandomSource` (`KhaozEngine.Primitives`, contracts 14.1) | not used by phase 1, named so a later phase does not re-derive the seam |

**Tasks 1, 2 and 3 have NO Scope A gate at all.** They live in `KhaozEngine.Items` and
`KhaozEngine.TileWorld.Netcode`, neither of which references `KhaozEngine.Catalog`, so implementation
starts on them the day this plan is approved. Every task from 4 onward creates or edits
`KhaozEngine.ItemInstances`, whose csproj references `KhaozEngine.Catalog` (contracts 3.2), so all of
them are gated on milestone 1.1 having landed. Each task states its gate in its own header.

**The one ordering edge between the two programs** is spec 20's: task 1 takes `ItemStack` to three
components and task 6 takes `ItemContainerCodec` to version 2, which is a fleet-wide compile break plus
a durable codec bump, and it must land BEFORE step 7 or AFTER step 11 of Scope A's Grimhollow adoption
(catalog 16.8, spec 20). Never inside that window. Confirm with the Scope A implementer which side of
the window you are on before starting task 1, and record the answer in the task 1 commit message.

## Global Constraints

Binding on every task. Each restates a rule from the spec, the contracts or AGENTS.md, and a task that
breaks one of these is wrong even when its own tests pass.

- **Work in a fresh worktree** at `/Users/antonio/KhaozEngine/.claude/worktrees/item-instances-phase1`
  branched from the latest `origin/main`. Engine program
  [#884](https://github.com/APKiwiOrg/KhaozEngine/issues/884) owns this work.
- **The payload is a canonical TLV and the encoder is the thing that makes it canonical.** Fields
  strictly ascending by kind, no kind twice, every varint minimal (contracts 9.3). The decoder CHECKS
  all three and refuses.
- **Varints are unsigned minimal LEB128 and nothing in this plan is zig-zagged.** Content ids, instance
  ids, kind ids, lengths, counts and roll positions are all declared unsigned (contracts 15). Use
  `ContentVarint` from `KhaozEngine.Catalog`. Do NOT write a second varint implementation anywhere.
- **Little endian through `BinaryPrimitives` with the endianness in the method name, on BOTH sides.**
  `BitConverter` is forbidden (contracts 15). Version 1's write side leaned on `BinaryWriter` and that
  asymmetry is not carried into version 2.
- **No floats anywhere on these paths.** Integer maths only (contracts 13.4 and 15).
- **Byte equality IS the stacking rule.** Two occupied slots merge only when the definition matches, the
  predicate says yes, neither is quarantined, and `a.Payload.Span.SequenceEqual(b.Payload.Span)` holds
  (spec 4.6). Nothing anywhere decodes two payloads to compare them.
- **The decoder NEVER throws.** It answers false plus a reason token from the CLOSED set of contracts
  9.7 (spec 3.2). A counter is keyed on those tokens, so adding one is a deliberate, additive act and
  never something a task does in passing.
- **No ambient statics and no service locator.** Every dependency arrives through a constructor or a
  method argument, including `IContentSnapshot` and, in later phases, `IRandomSource` (contracts 14.4).
  No test in this plan writes process-global state, so no test needs a `DisableParallelization`
  collection. If one later does, it enlists in a named collection with the shared state in its doc
  comment, per the #349 rule in AGENTS.md.
- **One operation identity per commit**, unchanged from what the journal does today
  (`JournalCommit.cs:14-22`, spec 6.1). Nothing in this plan changes `KhaozEngine.WorldStore`, either
  provider schema, or the store conformance suite (spec 2.4).
- **One responsibility per file, every file under 800 lines.** When the KESIZE ratchet fires, put the new
  code in a new type. Never split a file at an arbitrary line, and never hand-edit `.filesize-baseline`.
- **Warnings are errors** in every configuration. Fix at the source, never with `NoWarn` or a pragma.
- **CI tests Release and a local `dotnet test` runs Debug.** Any test asserting on `Debug.Assert`,
  `[Conditional("DEBUG")]` or `#if DEBUG` behaviour must be run once with `-c Release` before merging.
- **A new test project references ONLY what its tests use**, carries `<IsPackable>false</IsPackable>`
  and pins `<RootNamespace>KhaozEngine.Tests</RootNamespace>` (spec 2.3, AGENTS.md). Push CI selects
  test projects by the reference graph, so an over-broad reference silently degrades selection.
- **Every player-facing string is a `StringId`.** The three placeholder keys are engine owned and fixed:
  `khaoz.item.quarantined`, `khaoz.item.retired`, `khaoz.item.unidentified` (spec 12.3). The engine ships
  the keys and no translation.
- **No em dashes, no en dashes, no semicolons in prose** in any file this plan writes, code comments and
  XML doc included. Run `scripts/check-dashes.sh --tree` and `scripts/check-prose.sh --tree` before every
  push, not only at the end.
- **Commit early, with explicit paths.** Never `git add -A`, never `git commit -a`, never `git stash`.
  Every task ends with its own commit. Push after the first commit and after each later one.
- **Never route around a guard.** No `--no-verify`, no `FILESIZE_OK`, no `PACK_RELEASED_OK`, no
  `BACKLOG_FILE_OK`. A hook that blocks you is the answer: stop and report it.

## Task index

| # | Task | Size | Scope A gate |
|---|---|---|---|
| 1 | `ItemStack` third component, `ItemSlot`, the payload-carrying container doors | medium | none |
| 2 | The fleet-wide `ItemStack` deconstruction break and its consumer sweep | medium | none |
| 3 | `TileFragmentedMessage`, the item-agnostic fragmenter and reassembler | medium | none |
| 4 | The `KhaozEngine.ItemInstances` package skeleton and the property registry | medium | milestone 1.1 |
| 5 | `ItemInstancePayload`: the canonical TLV codec, sockets, goldens | large | milestone 1.1 |
| 6 | Decoder fuzzing and the unknown-kind round trip | medium | milestone 1.1 |
| 7 | `QuarantineWrapper`, the `KECQ` format | small | milestone 1.1 |
| 8 | `ItemInstanceVisibility`: `CanSee`, `PublicView` and the owner remainder | medium | milestone 1.1 |
| 9 | `InstanceIdAllocator`, the store epoch and the rotation guard | medium | milestone 1.1 |
| 10 | Container codec version 2 and the version 1 reader | large | milestone 1.1 |
| 11 | `ItemContainerPage`, `PagedItemContainer`, the capacity gate and the merge rule | large | milestone 1.1 |
| 12 | `InstanceValidator`, its thirteen checks, the counter and the log line | large | milestone 1.1 |
| 13 | The registry-derived remap pass and its idempotence | large | milestone 1.1 |
| 14 | `KhaozEngine.ItemInstances.Journal`, section names and the container load path | medium | milestone 1.1 |
| 15 | `ContainerCommitBuilder`, the tick-bounded batch and its crash facts | large | milestone 1.1 |
| 16 | `TileGroundItemInstance` and the `SpawnGroundItem` overload | medium | milestone 1.1 |
| 17 | The page delta, its one-frame bound and the resync request | large | milestone 1.1 |
| 18 | The `--items` benchmark structural test in `KhaozEngine.Server.Tests` | medium | milestone 1.1 |
| 19 | Documentation sweep: package READMEs, README catalog, USING, DEPENDENCY-SEAMS | medium | milestone 1.1 |
| 20 | The finishing ritual: one version bump, changelog, pack, no tag | medium | milestone 1.1 |

Groups and their acceptances:

- **Group A, tasks 1 to 3.** The changes that need nothing from Scope A. Acceptance: `dotnet test -c Release`
  green across the solution with `ItemStack` at three components, and the reassembler facts of spec 17 row
  15 green.
- **Group B, tasks 4 to 9.** The instance record, spec 3. Acceptance: spec 17 rows 1, 2, 3, 12 and 16
  green, and contracts 9.8's 45 bytes reproduced byte for byte. This is spec 20 phase 1's acceptance.
- **Group C, tasks 10 to 15.** The container, the pages and the commit, spec 4, 5 and 6. Acceptance: spec
  17 rows 4, 8 and 14 green, and budget 4 measured at ONE commit. This is spec 20 phase 2's acceptance
  plus phase 1's row 4.
- **Group D, tasks 16 to 18.** The wire, spec 7. Acceptance: spec 17 rows 9, 11 and 17 green, and budgets
  7 and 8 measured. This is spec 20 phase 3's acceptance.
- **Group E, tasks 19 and 20.** Documentation and release. Acceptance: `scripts/check-doc-versions.sh`,
  `scripts/check-dashes.sh --tree`, `scripts/check-prose.sh --tree` and `scripts/check-file-size.sh --tree`
  all exit 0, the full solution green in Release, and `local-feed` packed with NO tag.

---

## Group A: what needs nothing from Scope A

### Task 1: `ItemStack` gains an instance id, and the container gains payload doors (medium, no Scope A gate)

Spec 4.1, 4.2, 4.3 and 4.7. Option A of spec 4.2's weighed table: the instance id goes ON `ItemStack`
as a third component defaulting to 0, and the payload lives in a parallel array on the container reached
through a new `ItemSlot`.

**Files:**

- Modify: `KhaozEngine.Items/ItemContainer.cs`
- Create: `KhaozEngine.Items/ItemSlot.cs`
- Create: `KhaozEngine.Items/ItemContainer.Slots.cs`
- Modify: `KhaozEngine.Foundation.Tests/Items/ItemContainerTests.cs`
- Create: `KhaozEngine.Foundation.Tests/Items/ItemSlotTests.cs`

**Interfaces:**

- Consumes: nothing new. `KhaozEngine.Items` stays pure .NET with no package reference.
- Produces: `ItemStack(int ItemId, int Count, long InstanceId = 0)`, `ItemSlot`, `SetSlotAt`,
  `TakeSlotAt`, a payload-aware `Swap` and a payload-clearing `SetAt`.

- [ ] **Step 1: Write the failing tests first.** In `ItemSlotTests`, cover exactly these, all of which are
  behaviour spec 4.3 and 4.7 name:

~~~csharp
[Fact] public void A_plain_stack_still_has_instance_id_zero_and_an_empty_payload()
[Fact] public void SetAt_clears_a_slots_payload_and_quarantine_flag()
[Fact] public void SetSlotAt_refuses_a_payload_above_the_cap()
[Fact] public void SetSlotAt_refuses_a_non_empty_payload_with_a_zero_instance_id()
[Fact] public void SetSlotAt_allows_an_empty_payload_with_a_non_zero_instance_id()
[Fact] public void SetSlotAt_refuses_a_non_canonical_payload()
[Fact] public void SetSlotAt_skips_the_cap_and_canonical_checks_when_quarantined()
[Fact] public void TakeSlotAt_returns_the_whole_slot_and_seats_ItemSlot_Empty()
[Fact] public void Swap_moves_the_payload_with_the_stack()
[Fact] public void Two_slots_with_equal_payload_bytes_compare_equal_through_ItemSlot()
~~~

- [ ] **Step 2: Run them and confirm the build fails on the missing types.**

~~~bash
dotnet test KhaozEngine.Foundation.Tests/KhaozEngine.Foundation.Tests.csproj -c Release --filter FullyQualifiedName~ItemSlotTests
~~~

Expected: a build failure naming `ItemSlot`, `SetSlotAt` and `TakeSlotAt`.

- [ ] **Step 3: Implement the two value types exactly as spec 4.3 writes them.**

~~~csharp
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
~~~

`IsEmpty` and `Empty` keep their current meaning: `default` gives instance id 0 and an empty payload, so
a cleared slot and a never-filled one stay the same value. `Payload` is a `ReadOnlyMemory<byte>` over
bytes the container owns and never mutates in place.

- [ ] **Step 4: Add the parallel payload array and the four doors**, in `ItemContainer.Slots.cs` so
  `ItemContainer.cs` does not grow. The container holds `byte[]?[] _payloads` and `bool[] _quarantined`
  beside `_slots`. A plain stack allocates nothing: a null payload entry IS the empty payload.
  - `SetAt(int slot, ItemStack stack)` keeps its exact current sanitising behaviour and GAINS one: it
    clears the slot's payload and quarantined flag (spec 4.7). A version 1 blob decode goes through this
    door, which is why the clear is the only correct reading of what it already meant.
  - `SetSlotAt(int slot, ItemSlot value)` throws `ArgumentException` for each of spec 4.7's four
    invariants. Invariants 2 (at most `MaxInstancePayloadBytes`) and 4 (canonical) are SKIPPED when
    `value.Quarantined` is set, and `QuarantineWrapper.Verify` stands in for them from task 7. Until task
    7 lands, the quarantined path accepts the bytes unchecked and carries a `TODO` naming task 7. The
    canonical check runs on EVERY call and never under a `Debug.Assert`, because `[Conditional("DEBUG")]`
    members do not exist in the Release configuration CI builds.
  - `TakeSlotAt(int slot)` returns the whole `ItemSlot` and seats `ItemSlot.Empty`.
  - `Swap(a, b)` swaps the payload and flag entries alongside the stacks, so the one-line tuple swap
    becomes three.
- [ ] **Step 5: Declare the cap where `KhaozEngine.Items` can see it.** `MaxInstancePayloadBytes = 512`
  is contracts 9.6's number and `KhaozEngine.Items` cannot reference `KhaozEngine.ItemInstances` (the
  dependency runs the other way). Declare it as `public const int ItemSlot.MaxPayloadBytes = 512` in
  `KhaozEngine.Items` and have `KhaozEngine.ItemInstances` expose
  `ItemInstancePayload.MaxInstancePayloadBytes => ItemSlot.MaxPayloadBytes` in task 5, so there is ONE
  number. Add a one-line comment on the const naming contracts 9.6 and the one-way raise rule.

- [ ] **Step 6: Take the canonical check as a CONSTRUCTOR PREDICATE, because the spec leaves this open
  and the only other answers are worse.** Spec 4.7 invariant 4 says `SetSlotAt` refuses a non-canonical
  payload and that "the check is the decoder's own". The decoder is `ItemInstancePayload` in
  `KhaozEngine.ItemInstances`, which DEPENDS on `KhaozEngine.Items` (contracts 3.2), so the container
  cannot call it. A second varint reader inside `KhaozEngine.Items` is forbidden by contracts 15, and a
  new `Items` to `Catalog` edge appears in no spec. So the container takes the check the same way it
  already takes the stacking rule:

~~~csharp
public ItemContainer(
    int slotCount,
    Func<int, bool> stackable,
    Func<ReadOnlyMemory<byte>, bool>? payloadCanonical = null);
~~~

  `ItemInstances` passes `ItemInstancePayload.IsCanonical`. When the predicate is null the container
  refuses ANY non-empty, non-quarantined payload outright, so a container built without the check cannot
  carry payloads at all and the door is never half-open. Both existing constructors keep compiling. Record
  this choice in the XML doc on the parameter, citing spec 4.7 and contracts 3.2.
- [ ] **Step 7: Extend the existing `ItemContainerTests` rather than replacing them.** The five existing
  facts stay exactly as they are and keep passing, which is the regression fence. Add the merge-side fact
  spec 4.6 names: when two stackable entries merge, the SURVIVING instance id is the numerically LOWER of
  the two, so the merge is commutative and a replay in either order agrees.
- [ ] **Step 8: Run the focused and the full Foundation suite green.**

~~~bash
dotnet test KhaozEngine.Foundation.Tests/KhaozEngine.Foundation.Tests.csproj -c Release --filter FullyQualifiedName~Item
dotnet test KhaozEngine.Foundation.Tests/KhaozEngine.Foundation.Tests.csproj -c Release
~~~

- [ ] **Step 9: Commit.**

~~~bash
git add KhaozEngine.Items/ItemContainer.cs KhaozEngine.Items/ItemSlot.cs KhaozEngine.Items/ItemContainer.Slots.cs KhaozEngine.Foundation.Tests/Items
git commit -m "items(instances): ItemStack carries an instance id and the container carries payloads"
~~~

---

### Task 2: The fleet-wide `ItemStack` deconstruction break and its consumer sweep (medium, no Scope A gate)

Spec 4.2's honestly priced cost, spec 20's sequencing paragraph. A three component record struct
generates a three out-parameter `Deconstruct`, so every `var (id, count) = stack` in the fleet stops
compiling, and `ItemStack` equality now includes the instance id. Neither is silent and both surface at
the first build. This is its OWN task because the sweep crosses repositories and must not be buried
inside task 1's diff.

**Files:**

- Modify: `KhaozEngine.Tests/ArchitectureTests.cs` (one fact, see step 4)
- No engine source. Everything else in this task is a survey plus cross-repo issues.

**Interfaces:**

- Consumes: task 1's `ItemStack`
- Produces: a recorded, complete list of break sites and one filed issue per affected consumer

- [ ] **Step 1: Sweep THIS repository first and fix what it finds.** Two patterns break, and grep for both
  rather than reasoning about which call sites exist:

~~~bash
grep -rn 'var (' --include='*.cs' . | grep -i 'stack\|slot\|item'
grep -rn '(int [A-Za-z]*, int [A-Za-z]*) *= ' --include='*.cs' .
grep -rn 'ItemStack' --include='*.cs' . | wc -l
~~~

  At the time of writing the engine holds no positional deconstruction of `ItemStack`, so this step is
  expected to be a confirmation rather than a fix. Confirm it rather than assuming it.

- [ ] **Step 2: Sweep every consumer repository, read only, and record the numbers.** The four pinned
  consumers are in AGENTS.md's table plus Grimhollow. Run the same two greps in each of `~/Hardpoint`,
  `~/Nullwake`, `~/SpaceGame`, `~/Ruinborne` and `~/Grimhollow`, excluding `obj`, `bin`, `vendor` and
  nested worktrees. Do NOT edit a consumer repository from this worktree.

  Known at the time of writing: Grimhollow is the only consumer on `KhaozEngine.Items`, with about 166
  files referencing `ItemStack` and at least one positional deconstruction
  (`Grimhollow.Tests/Server/ProcessingCamp.Masonry.cs:59`, `(int itemId, int count) = items[index];`).
  Nullwake, SpaceGame, Ruinborne and Hardpoint had zero `KhaozEngine.Items` references. Verify all five
  again rather than trusting this paragraph, because the fleet moves.

- [ ] **Step 3: File ONE issue per affected consumer, as a full URL cross-reference.** AGENTS.md's rule:
  a cross-repo handoff is an issue reference and never a pair of hand-written entries. Each issue names
  the engine version the break lands in, the two grep patterns, the file and line list from step 2, and
  the fix, which is `var (id, count, _) = stack` or a named read of `stack.ItemId` and `stack.Count`.
  Label `needs/upstream` on the consumer side and `confidence/verified` here. Link both ways by full URL.
  Add each to the org board (https://github.com/orgs/APKiwiOrg/projects/1), which has no auto-add.

- [ ] **Step 4: Add the one fact that keeps the break honest.** In `KhaozEngine.Tests/ArchitectureTests.cs`,
  assert that `ItemStack` has exactly three components and that `ItemStack.Empty` equals
  `new ItemStack(0, 0, 0)`. The point is not the shape, it is that a fourth component is a second
  fleet-wide break and has to be a deliberate act rather than a side effect of someone adding a field.

~~~csharp
[Fact]
public void ItemStack_has_three_components_so_a_fourth_is_a_deliberate_fleet_break()
{
    MethodInfo deconstruct = typeof(ItemStack).GetMethod("Deconstruct")!;
    Assert.Equal(3, deconstruct.GetParameters().Length);
    Assert.Equal(new ItemStack(0, 0, 0), ItemStack.Empty);
}
~~~

- [ ] **Step 5: Confirm the Scope A window, in writing.** Spec 20 and catalog 18.1 both say this break
  must land BEFORE step 7 or AFTER step 11 of Scope A's Grimhollow adoption (catalog 16.8). Ask the Scope
  A implementer which side you are on, and put the answer in this task's commit message. If the answer is
  "inside the window", STOP and report rather than proceeding.

- [ ] **Step 6: Run the architecture suite and commit.**

~~~bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter FullyQualifiedName~ArchitectureTests
git add KhaozEngine.Tests/ArchitectureTests.cs
git commit -m "items(instances): pin ItemStack at three components"
~~~

---

### Task 3: `TileFragmentedMessage`, the item-agnostic fragmenter and reassembler (medium, no Scope A gate)

Spec 7.5. The engine has no fragmentation layer at all today, verified by grep, and the single precedent
for a larger logical payload is hand-rolled application-level chunking on the reliable ordered channel,
done once for combat (`TileWorldServer.Tick.cs:241-259`). This task builds the general one. **It knows
nothing about items**, which is both correct layering and the reason it can land before Scope A.

**Files:**

- Create: `KhaozEngine.TileWorld.Netcode/TileFragmentedMessage.cs`
- Create: `KhaozEngine.TileWorld.Netcode/TileFragmentReassembler.cs`
- Create: `KhaozEngine.TileWorld.Netcode.Tests/TileFragmentedMessageTests.cs`

**Interfaces:**

- Consumes: `TileProtocol.MaxGameMessageBytes` (1024, `TileProtocol.Frames.cs:65`) and the four byte
  envelope `[tag:1][kind:ushort 2][flags:1]` (`TileProtocol.Frames.cs:77`)
- Produces: a fragmenter over any `ReadOnlySpan<byte>`, and a bounded reassembler

- [ ] **Step 1: Write the failing tests, which are spec 17 row 15 verbatim plus the round trip.**

~~~csharp
[Fact] public void A_payload_under_one_chunk_is_one_chunk_and_round_trips()
[Fact] public void A_page_sized_payload_round_trips_through_every_chunk_in_order()
[Fact] public void A_chunk_whose_sequence_differs_mid_assembly_discards_and_restarts()
[Fact] public void A_fifth_concurrent_assembly_evicts_the_oldest_and_counts_it()
[Fact] public void A_truncated_final_chunk_answers_a_reason_rather_than_throwing()
[Fact] public void A_dropped_connection_discards_every_partial_assembly()
[Fact] public void No_chunk_exceeds_the_game_message_cap()
~~~

  **NOT out-of-order chunks.** The channel is `ReliableOrdered`, so that cannot happen and the
  reassembler deliberately does not handle it. Spec 17 row 15 says so in as many words, and a test
  asserting reordering would pin behaviour the design refuses to have.

- [ ] **Step 2: Run them and confirm the missing type fails the build.**

~~~bash
dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release --filter FullyQualifiedName~TileFragmentedMessageTests
~~~

- [ ] **Step 3: Implement the header exactly as spec 7.5 writes it.** Five bytes, all fixed width, no
  varint anywhere, which is what lets this task precede Scope A:

~~~
[StreamId: byte]        // which logical stream, the GAME assigns these
[Sequence: uint16 LE]   // increments per transmission of that stream, wraps
[ChunkIndex: byte]
[ChunkCount: byte]      // 1 to 255
[Bytes: the rest]
~~~

  A chunk carries `MaxGameMessageBytes - 4 - 5 = 1015` payload bytes and 255 chunks carry 258 KB, which
  is forty times the largest page and five times the worst case page of spec 5.4. `Sequence` is little
  endian through `BinaryPrimitives.WriteUInt16LittleEndian`, per contracts 15.

- [ ] **Step 4: Implement the reassembler's four rules, which are spec 7.5's, in order.**
  1. A chunk whose `Sequence` differs from the assembly in progress DISCARDS that assembly and starts a
     new one. That is what a server restarting a page mid-transmission looks like. It is not an error and
     it does not throw.
  2. At most FOUR partial assemblies are held at once. A fifth evicts the oldest and increments a public
     counter. A bounded-memory rule rather than a timer, because a timer on a reliable ordered channel
     measures nothing.
  3. On the last chunk the assembled bytes are handed BACK to the caller for decoding, and a decode
     failure is the caller's quarantine rather than a throw here. The reassembler itself answers
     `TryComplete(out ReadOnlyMemory<byte> assembled, out string? reason)`.
  4. A partial assembly still open when the connection drops is discarded with the connection, through an
     explicit `DropConnection(int slot)` the server calls. Nothing here holds a timer or a background task.

- [ ] **Step 5: Keep the encoder total in the direction that matters.** The FRAGMENTER throws on a payload
  above `255 * 1015` bytes, because that is a local caller bug in the same class as the existing cap throw.
  The REASSEMBLER never throws, because its bytes come from a remote peer, which is the rule every frame
  decoder in `TileProtocol` already follows (`TileProtocol.Frames.cs:27-34`).
- [ ] **Step 6: Run green and commit.**

~~~bash
dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release
git add KhaozEngine.TileWorld.Netcode/TileFragmentedMessage.cs KhaozEngine.TileWorld.Netcode/TileFragmentReassembler.cs KhaozEngine.TileWorld.Netcode.Tests/TileFragmentedMessageTests.cs
git commit -m "tileworld(netcode): fragment a logical payload across reliable ordered frames"
~~~

---

## Group B: the instance record (spec 3)

Every task from here on is gated on **Scope A milestone 1.1**, because each one creates or edits
`KhaozEngine.ItemInstances`, whose csproj references `KhaozEngine.Catalog` (contracts 3.2).

### Task 4: The `KhaozEngine.ItemInstances` package and the property registry (medium, gate: milestone 1.1)

Spec 2.1, 2.2, 2.3 and 3.3. The registry is first because the payload codec, the remap pass and the
validator all DERIVE their behaviour from it rather than from a list written in a document, which is
what stops a kind being remapped-but-not-validated.

**Files:**

- Create: `KhaozEngine.ItemInstances/KhaozEngine.ItemInstances.csproj`
- Create: `KhaozEngine.ItemInstances/README.md`
- Create: `KhaozEngine.ItemInstances/InstancePropertyKind.cs`
- Create: `KhaozEngine.ItemInstances/InstanceFieldShape.cs`
- Create: `KhaozEngine.ItemInstances/InstancePropertyRegistry.cs`
- Create: `KhaozEngine.ItemInstances/IInstancePropertyCodec.cs`
- Create: `KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj`
- Create: `KhaozEngine.ItemInstances.Tests/InstancePropertyRegistryTests.cs`
- Modify: `KhaozEngine.slnx`
- Modify: `KhaozEngine.Foundation/KhaozEngine.Foundation.csproj`
- Modify: `KhaozEngine.Tests/ArchitectureTests.cs`

**Interfaces:**

- Consumes: `KhaozEngine.Items`, `KhaozEngine.Catalog`, `KhaozEngine.Primitives`
- Produces: `InstancePropertyKind`, `InstanceKindBand`, `PropertyVisibility`, `InstanceSlotKind`,
  `InstanceCountWidth`, `InstanceReferenceSite`, `InstanceFieldShape`, `InstanceReferenceTarget`,
  `IInstancePropertyCodec`, `InstancePropertyRegistry`

- [ ] **Step 1: Write the failing registry tests.** All of these are spec 3.3's rules stated as facts, and
  the band and bit ones are the two that stop a durable-format bug rather than a compile error:

~~~csharp
[Fact] public void A_game_band_registration_below_1024_throws()
[Fact] public void A_ScopeB_band_registration_outside_128_to_1023_throws()
[Fact] public void An_Engine_band_registration_above_127_throws()
[Fact] public void Kind_zero_is_never_registrable()
[Fact] public void A_duplicate_kind_throws()
[Fact] public void A_duplicate_identification_mask_bit_throws()
[Fact] public void A_registration_after_the_first_pack_load_throws()
[Fact] public void Unregistering_and_replacing_a_codec_are_both_refused()
[Fact] public void The_v1_engine_and_ScopeB_kinds_register_with_the_shapes_and_targets_of_3_3()
[Fact] public void The_four_v1_identification_bits_are_129_to_0_131_to_1_133_to_2_134_to_3()
~~~

- [ ] **Step 2: Create the package and wire it in.** The csproj follows `KhaozEngine.Items` exactly:
  `PackageId`, `<Version>$(KhaozEngineVersion)</Version>`, `PackageReadmeFile`, a `Description`, the
  `None Include="README.md"` pack item, and `ProjectReference`s to `KhaozEngine.Items`,
  `KhaozEngine.Catalog` and `KhaozEngine.Primitives`. Add the project to `KhaozEngine.slnx`, add it to the
  `Foundation` umbrella's `ProjectReference` set, and update the locked umbrella membership list in
  `ArchitectureTests.UmbrellaMembership()`, which fails CI otherwise by design.
- [ ] **Step 3: Create the test project.** References ONLY `KhaozEngine.ItemInstances`, `KhaozEngine.Items`,
  `KhaozEngine.Catalog` and `KhaozEngine.Primitives` (spec 2.3). It carries `<IsPackable>false</IsPackable>`
  and `<RootNamespace>KhaozEngine.Tests</RootNamespace>`. Declared namespaces are `KhaozEngine.Tests.*`.
  Add it to `KhaozEngine.slnx`. Do NOT add a `WorldStore` or a `TileWorld` reference: those tests live in
  `KhaozEngine.Server.Tests` and `KhaozEngine.TileWorld.Netcode.Tests` precisely so this project's graph
  stays narrow and push CI's selection stays sharp.
- [ ] **Step 4: Write `InstancePropertyKind` as `public const ushort` per kind**, all fifteen of spec 3.3's:
  1 `Flags`, 2 `ItemLevel`, 3 `Quality`, 4 `Charges`, 5 `Durability`, 6 `BoundTo`, 7 `Materials`,
  8 `Tier`, 128 `Identification`, 129 `UniqueTemplate`, 130 `Rarity`, 131 `Affixes`, 132 `Sockets`,
  133 `Enchantments`, 134 `RareName`. Kinds 2, 5, 130, 131 and 132 are PINNED by contracts 9.8's worked
  example and task 5's golden reproduces it, so none of them moves.

- [ ] **Step 5: Implement the shape descriptors exactly as spec 3.3 writes them.**

~~~csharp
public enum InstanceKindBand : byte { Engine = 1, ScopeB = 2, Game = 3 }
public enum PropertyVisibility : byte { ServerOnly = 0, OwnerOnly = 1, Everyone = 2 }
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
~~~

  `PropertyVisibility` is ordered least to most visible so the replication comparison of contracts 11.2 is
  a `<=` on the enum. That ordering is load bearing and belongs in the doc comment.

- [ ] **Step 6: Implement `Register` with the exact signature spec 3.3 gives**, and make every refusal a
  THROW at startup rather than a silent acceptance:

~~~csharp
public static void Register(
    InstanceKindBand band,
    ushort kind,
    IInstancePropertyCodec codec,
    PropertyVisibility visibility,
    int identificationMaskBit,
    in InstanceFieldShape shape,
    ReadOnlySpan<InstanceReferenceTarget> references);
~~~

  `band` is the caller's own declaration of which range it may register into and a mismatch throws.
  `identificationMaskBit` is a FIXED bit assigned at registration and -1 for a kind that is not gated.
  It is never the kind's position in the ascending list of gated kinds: spec 3.3 and spec 21's last row
  explain that a derived index re-points every partially identified item in the world the moment an engine
  release adds a gated kind below 129, silently, with no byte changing. Registration throws on a duplicate
  bit exactly as it throws on a duplicate kind. The registry FREEZES when the first pack loads, exposed as
  `Freeze()` called by the pack load path, and a later registration throws.
  `ReplicationRegistry.Register` (`TileProtocol.Components.cs:122`) is the engine's precedent one level up.

- [ ] **Step 7: Register the v1 kinds with spec 3.3's shape and target table, verbatim.** These eight rows
  are the whole of what the remap pass and the validator later walk, so a typo here is a data bug rather
  than a test failure:

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

  Kind 132's count is a VARINT and kind 131's is a BYTE, and the difference is deliberate (spec 3.3).
  Contracts 9.5 writes the socket count as a varint and narrowing it would be a width change, invisible in
  the golden file because the worked example writes `01` and that is both. Put that sentence in the code
  comment beside the two registrations, because it is exactly the kind of thing a later reader tidies.
  Kind 134's header slot is a `rarity_rule` id rather than a `unique_template` one: it records WHICH
  rarity rule's display format composed the name.

- [ ] **Step 8: Write the package README.** It ships INSIDE the nupkg and is read standalone on NuGet, so
  it is self-contained: what the package is, the payload format in one block, the kind ranges, the
  registration rule and the three placeholder `StringId`s. Do not point it at a design doc.
- [ ] **Step 9: Run the focused tests green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter FullyQualifiedName~ArchitectureTests
git add KhaozEngine.ItemInstances KhaozEngine.ItemInstances.Tests KhaozEngine.slnx KhaozEngine.Foundation/KhaozEngine.Foundation.csproj KhaozEngine.Tests/ArchitectureTests.cs
git commit -m "iteminstances(registry): property kinds, bands and field shapes"
~~~

---

### Task 5: `ItemInstancePayload`, the canonical TLV codec and its goldens (large, gate: milestone 1.1)

Spec 3.2, 3.4, 3.5, 3.7 and 3.8, over contracts 9.1 through 9.9. This is the format everything else in
the plan rests on, and spec 21 records most of it as expensive to change once data exists.

**LIFT FROM THE SPIKE, do not rewrite.** `KhaozEngine.Benchmarks/Items/InstancePayload.cs` (327 lines) is
a clean, measured implementation of this exact format: the same field walk, the same closed reason set,
the same per-kind shape checks, the same one-level socket refusal, the same retained-run public view. Lift
its structure and its logic. Three things change on the way in, and nothing else should:

1. Its `Varint` (`KhaozEngine.Benchmarks/Items/Varint.cs`) is REPLACED by `ContentVarint` from
   `KhaozEngine.Catalog`. Contracts 15 wants one varint implementation in the tree and the spike's copy
   exists only because Scope A had not shipped.
2. Its `CheckShape` switch is REPLACED by a walk of the registry's `InstanceFieldShape` from task 4. The
   spike hard-codes the kinds because it was a spike. Deriving the walk is what gives a GAME kind at or
   above 1,024 remap, drift detection and quarantine for free (spec 3.3).
3. Its `internal` surface becomes `public` with XML doc, and its types move to the names spec 2.2 gives.

**The spike stays and keeps building.** Do not delete it, do not make it reference the new package, and do
not edit it in this task. Its numbers are what section 16's measured column reports.

**Files:**

- Create: `KhaozEngine.ItemInstances/ItemInstancePayload.cs`
- Create: `KhaozEngine.ItemInstances/ItemInstancePayload.Shapes.cs`
- Create: `KhaozEngine.ItemInstances/ItemInstancePayloadBuilder.cs`
- Create: `KhaozEngine.ItemInstances/InstancePayloadReason.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Payload/ItemInstancePayloadTests.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Payload/PayloadGoldenTests.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Payload/Goldens/*.bin` (checked in)

**Interfaces:**

- Consumes: `ContentVarint`, task 4's registry, `ItemSlot.MaxPayloadBytes`
- Produces: `ItemInstancePayload` (`TryDecode`, `Encode`, `Validate`, `PublicView`, `SequenceEqual`,
  `IsCanonical`), `ItemInstancePayloadBuilder`, the closed reason token set

- [ ] **Step 1: Write the GOLDEN test first, and make it contracts 9.8's forty-five bytes.** This is spec
  17 row 1 and spec 20 phase 1's acceptance in one fact. Spec 3.7 reproduces the block in canonical order
  so it can be copied into the golden file exactly as it stands:

~~~
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
~~~

  The fact asserts BOTH directions: building that item through `ItemInstancePayloadBuilder` produces those
  45 bytes exactly, and decoding those 45 bytes produces those fields. Add the four spec 3.8 rows as
  goldens in the same file, with their payload and slot-entry byte counts asserted: OSRS 0 and 7, Tibia
  7 and 15, Mortal Online 20 and 30, PoE 58 and 69. Those numbers are budgets 1 and 2.

- [ ] **Step 2: Write the canonical-form facts.** Contracts 9.3's three rules, each as a refusal:

~~~csharp
[Fact] public void Fields_out_of_ascending_order_answer_kind_out_of_order()
[Fact] public void A_duplicate_kind_answers_kind_duplicate()
[Fact] public void A_non_minimal_varint_answers_varint_not_minimal()   // 0x81 0x00 is not 1
[Fact] public void A_varint_that_does_not_terminate_answers_varint_overflow()
[Fact] public void A_declared_length_past_the_end_answers_field_truncated()
[Fact] public void A_payload_above_the_cap_answers_payload_too_long()
[Fact] public void A_nested_payload_carrying_kind_132_answers_socket_nesting()
[Fact] public void A_known_kinds_wrong_shape_answers_field_malformed()
[Fact] public void A_non_zero_affix_flags_varint_answers_field_malformed()   // contracts 9.9
[Fact] public void Affix_entries_not_ascending_by_mod_id_answer_field_malformed()
[Fact] public void The_encoder_never_produces_a_payload_the_decoder_refuses()
~~~

- [ ] **Step 3: Run them and confirm the missing type fails the build.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~Payload
~~~

- [ ] **Step 4: Implement the format.** No header, no magic and no version byte, because a payload never
  travels alone (contracts 9.1). `[Kind: varint uint16][Length: varint int32][Bytes: Length bytes]`,
  repeated zero or more times. An EMPTY payload is zero bytes, which is what a plain stack has.
- [ ] **Step 5: Implement the affix entry exactly as spec 3.4 pins it.**

~~~
[ModId: varint int32]     // a `mod` content row, never 0
[Tier: byte]              // the mod's AUTHORED tier ordinal, 1 to 255, never 0
[Position: uint16 LE]     // the roll position, contracts 6.4
[Flags: varint uint32]    // reserved, 0 in v1, contracts 9.9
~~~

  `Position` is a FIXED two byte little endian `ushort` and not a varint: positions are uniform over the
  whole range, so a varint would cost more on average. The list is sorted ASCENDING BY MOD ID, and a mod
  id appears at most once, which is what makes the field canonical and therefore what makes spec 4.6's
  byte comparison the stacking rule. Prefix versus suffix is NOT in the entry, it is read off the mod row.

- [ ] **Step 6: Implement the socket entry and the ONE LEVEL rule.** Spec 3.5 over contracts 9.5:

~~~
[SocketTypeId: varint int32]           // 0 means no restriction
[ContainedDefinitionId: varint int32]  // 0 means empty
[ContainedInstanceId: varint uint64]   // 0 when empty, or when the contained item has no instance
[NestedLength: varint int32]
[Nested: NestedLength bytes]           // a payload in this same format
~~~

  The DECODER enforces the nesting limit: a nested payload containing kind 132 answers `socket-nesting`
  rather than recursing. That is a structural limit and not a convention, and it is what stops a 45 byte
  payload becoming a denial of service (spec 15.6). Socket ORDER is AUTHORED and never sorted, unlike the
  affix list. The consequence is worth a doc comment: two otherwise identical items whose gems sit in
  different sockets do not stack, which is correct, because they are different items.

- [ ] **Step 7: Pin the closed reason set in ONE place.** `InstancePayloadReason` holds contracts 9.7's
  eight tokens as `public const string`: `payload-too-long`, `field-truncated`, `kind-out-of-order`,
  `kind-duplicate`, `varint-not-minimal`, `varint-overflow`, `socket-nesting`, `field-malformed`. A
  counter is keyed on them (spec 12.6), so the set is closed and a ninth is a deliberate additive act.
  Add a test asserting the public constant list has exactly those eight members, so a drive-by addition
  goes red.
- [ ] **Step 8: Expose the four members spec 2.2 names, plus the one task 1 needs.**

~~~csharp
public static bool TryDecode(ReadOnlySpan<byte> payload, Span<PayloadField> fields, out int fieldCount, out string? reason);
public static int  Encode(ItemInstancePayloadBuilder builder, Span<byte> destination);
public static string? Validate(ReadOnlySpan<byte> payload);
public static int  PublicView(ReadOnlySpan<byte> payload, PropertyVisibility level, bool identified, uint revealedMask, Span<byte> destination);
public static bool SequenceEqual(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right);
public static bool IsCanonical(ReadOnlyMemory<byte> payload);
public const int MaxInstancePayloadBytes = ItemSlot.MaxPayloadBytes;   // 512, contracts 9.6
~~~

  `PublicView` is implemented here as a stub returning the whole payload and is FINISHED in task 8, which
  is where the visibility rule belongs. `TryDecode` NEVER throws.

- [ ] **Step 9: Keep the unknown kind verbatim.** A decoder that meets a kind it does not know keeps the
  field's exact bytes and its position, and re-emits them unchanged (contracts 9.4). The builder records
  it as an opaque `(kind, bytes)` pair in its ordered field list. The preserved bytes participate in byte
  equality, so two items differing only in an unknown field do not stack, which is the conservative answer.
- [ ] **Step 10: Run the payload suite green, then run it once in Release.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release
~~~

- [ ] **Step 11: Commit.**

~~~bash
git add KhaozEngine.ItemInstances/ItemInstancePayload.cs KhaozEngine.ItemInstances/ItemInstancePayload.Shapes.cs KhaozEngine.ItemInstances/ItemInstancePayloadBuilder.cs KhaozEngine.ItemInstances/InstancePayloadReason.cs KhaozEngine.ItemInstances.Tests/Payload
git commit -m "iteminstances(payload): the canonical tagged field codec and its goldens"
~~~

---

### Task 6: Decoder fuzzing and the unknown-kind round trip (medium, gate: milestone 1.1)

Spec 17 rows 2 and 3. Both are fences around task 5 rather than new behaviour, which is why they are their
own task: a fuzzer folded into the codec commit is a fuzzer nobody tunes.

**Files:**

- Create: `KhaozEngine.ItemInstances.Tests/Payload/PayloadFuzzTests.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Payload/PayloadMutator.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Payload/UnknownKindTests.cs`

**Interfaces:**

- Consumes: task 5's codec and its goldens, `SeededRandomSource` from `KhaozEngine.Primitives`
- Produces: a mutation corpus that widens for free whenever a golden is added

- [ ] **Step 1: Write the unknown-kind round trip first.** A decoder whose registry deliberately OMITS a
  kind the encoder wrote reproduces the input byte for byte, and the two items do NOT merge (spec 15.4).
  Two facts, and the second is the one that matters: this is why unregistering a kind is forbidden, because
  it would make two previously distinct items stack and destroy one identity.
- [ ] **Step 2: Write the fuzzer as MUTATION OVER GOLDENS, never random bytes.** Spec 17's note says why:
  random bytes reject at the first varint and prove nothing, while a mutation of a valid golden exercises
  the paths a real corruption reaches. The corpus IS the checked-in goldens, so a new golden widens the
  fuzzer for nothing.
- [ ] **Step 3: Assert the triple, on every mutation.** No throw. A reason from the CLOSED set of eight.
  The SAME reason for the same mutation across runs. The mutation classes are bit flips, truncations,
  length lies, kind swaps, count lies and nested-depth injection, and each class is its own `[Theory]` case
  so a red run names which class moved.

~~~csharp
[Theory]
[InlineData(MutationClass.BitFlip)]
[InlineData(MutationClass.Truncate)]
[InlineData(MutationClass.LengthLie)]
[InlineData(MutationClass.KindSwap)]
[InlineData(MutationClass.CountLie)]
[InlineData(MutationClass.NestDeeper)]
public void Mutating_a_golden_never_throws_and_answers_a_stable_closed_reason(MutationClass kind)
~~~

- [ ] **Step 4: Assert no recursion past one level**, by feeding a mutation that nests kind 132 three deep
  and asserting `socket-nesting` and a bounded stack. This is the denial-of-service fence of spec 15.6.
- [ ] **Step 5: Take the seed as a constant in the test and never from the clock.** Contracts 14.2's
  `SeededRandomSource` wraps `DeterministicRng`, so a failing mutation is reproducible from the seed the
  test printed. A fuzzer whose corpus changes per run is a flake generator.
- [ ] **Step 6: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~Payload
git add KhaozEngine.ItemInstances.Tests/Payload
git commit -m "iteminstances(payload): mutation fuzzing over the goldens"
~~~

---

