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

### Task 7: `QuarantineWrapper`, the `KECQ` format (small, gate: milestone 1.1)

Spec 12.4 over contracts 10.2 and 15. The bytes of a failed item are kept VERBATIM, and this is the
durable envelope that keeps them.

**Files:**

- Create: `KhaozEngine.ItemInstances/QuarantineWrapper.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Quarantine/QuarantineWrapperTests.cs`
- Modify: `KhaozEngine.Items/ItemContainer.Slots.cs` (resolve task 1 step 4's `TODO`)

**Interfaces:**

- Consumes: `ContentVarint`, task 5's reason set
- Produces: `QuarantineWrapper` with `Wrap`, `TryUnwrap` and `Verify`

- [ ] **Step 1: Write the failing tests, which are spec 17 row 16 plus the version refusal.**

~~~csharp
[Fact] public void Wrap_then_unwrap_returns_the_original_bytes_exactly()
[Fact] public void An_original_above_MaxInstancePayloadBytes_wraps_and_unwraps_unchanged()
[Fact] public void Verify_accepts_a_well_formed_wrapper_and_refuses_a_bare_payload()
[Fact] public void A_wrapper_version_other_than_1_is_refused_rather_than_guessed()
[Fact] public void The_reason_code_ordinal_round_trips_to_the_same_closed_token()
[Fact] public void A_wrapper_whose_declared_OriginalLength_lies_is_refused()
~~~

- [ ] **Step 2: Implement the format exactly as spec 12.4 writes it.**

~~~
[Magic: 4 bytes 'K','E','C','Q']   // 0x4B 0x45 0x43 0x51
[Version: uint16 LE]               // 1
[ReasonCode: byte]                 // an ordinal from the closed set of QUARANTINE reasons in 12.2
[StampedVersion: varint int32]     // the page stamp the record failed under
[OriginalLength: varint int32]
[Original: OriginalLength bytes]   // verbatim, never re-encoded
~~~

  **A magic here and none on a payload, and the two are consistent.** Contracts 15 forbids a magic on a
  format always embedded in a larger versioned record, and a payload is such a format. A wrapper is not:
  it must be distinguishable from a payload at a glance in a hex dump of a page, and by a tool that never
  saw the entry flag. Four bytes for that, once per quarantined entry, on a path that is rare by definition.

- [ ] **Step 3: Let `OriginalLength` exceed `MaxInstancePayloadBytes`.** `payload-oversize` is a reason,
  and refusing to wrap the thing that failed for being too big destroys exactly the item the wrapper exists
  to keep. The page entry's own length check is what bounds it, at the 2 MiB section cap (spec 4.4 and 5.4).
- [ ] **Step 4: Finish task 1's quarantined door.** `SetSlotAt` skips invariants 2 and 4 when
  `value.Quarantined` is set, and `QuarantineWrapper.Verify` stands in for them: four magic bytes, the
  version, and a declared `OriginalLength` that matches the bytes present. Invariants 1 and 3 still bind.
  Without this exception the door refuses the wrapper and the quarantine path is unreachable at exactly the
  moment it is needed. Pass `Verify` in through the same predicate seam task 1 step 6 introduced.
- [ ] **Step 5: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~Quarantine
dotnet test KhaozEngine.Foundation.Tests/KhaozEngine.Foundation.Tests.csproj -c Release --filter FullyQualifiedName~ItemSlot
git add KhaozEngine.ItemInstances/QuarantineWrapper.cs KhaozEngine.ItemInstances.Tests/Quarantine KhaozEngine.Items/ItemContainer.Slots.cs
git commit -m "iteminstances(quarantine): the KECQ wrapper keeps failed bytes verbatim"
~~~

---

### Task 8: `ItemInstanceVisibility`, the ONE `CanSee` and `PublicView` (medium, gate: milestone 1.1)

Spec 7.4 and 12.5 over contracts 11.1 and 11.2. There is exactly ONE function answering "may this viewer
see this field", and both the replication filter and the tooltip builder call it. A tooltip that computed
its own answer is how a client eventually renders something the server never sent.

**Files:**

- Create: `KhaozEngine.ItemInstances/ItemInstanceVisibility.cs`
- Modify: `KhaozEngine.ItemInstances/ItemInstancePayload.cs` (finish task 5's `PublicView` stub)
- Create: `KhaozEngine.ItemInstances.Tests/Visibility/ItemInstanceVisibilityTests.cs`

**Interfaces:**

- Consumes: task 4's registry (visibility and `identificationMaskBit` per kind), task 5's field walk
- Produces: `ItemInstanceVisibility.CanSee` and `ItemInstanceVisibility.PublicView`

- [ ] **Step 1: Write the failing tests, which are spec 17 row 9 plus the identification gate.**

~~~csharp
[Fact] public void ServerOnly_is_never_visible_to_anyone_including_the_owner()
[Fact] public void OwnerOnly_is_visible_only_when_the_viewer_level_is_OwnerOnly()
[Fact] public void Everyone_is_always_visible()
[Fact] public void A_gated_kind_is_hidden_from_the_OWNER_too_while_unidentified()
[Fact] public void A_set_RevealedMask_bit_reveals_exactly_its_own_registered_kind()
[Fact] public void The_replication_filter_and_the_tooltip_builder_agree_on_every_kind_at_every_level()
[Fact] public void PublicView_of_a_rare_drops_kinds_4_5_and_6_and_keeps_the_rest()
[Fact] public void PublicView_output_is_still_canonical_and_still_decodes()
[Fact] public void PublicView_allocates_nothing_beyond_its_destination_span()
~~~

  The sixth is spec 17 row 9 itself and it is a table-driven `[Theory]` over every registered kind times
  the three levels times identified and not, asserting the two call sites produce the same answer. The
  point is that there is one function, so the test drives the SAME function from both call shapes.

- [ ] **Step 2: Implement `CanSee` with spec 12.5's rule, in order.**

~~~csharp
public static bool CanSee(ushort kind, PropertyVisibility viewerLevel, bool identified, uint revealedMask);
public static int  PublicView(ReadOnlySpan<byte> payload, PropertyVisibility level, bool identified,
                              uint revealedMask, Span<byte> destination);
~~~

  `ServerOnly` is never visible to anyone. `OwnerOnly` is visible when the viewer level is `OwnerOnly`.
  `Everyone` is visible always. THEN, and only then, the identification gate: a kind carrying an
  `identificationMaskBit` is hidden when `identified` is false and its bit in `revealedMask` is clear,
  EVEN FROM THE OWNER. That last clause is gate 0 decision 8 and is why unidentified is a mechanic rather
  than a fourth visibility level. Both members are pure and static, so a test calls them with no server.

- [ ] **Step 3: Implement `PublicView` as the forward pass over RETAINED RUNS**, which is the spike's
  `InstancePayload.PublicView` and its `Flush` helper. Because fields are already ascending and each is
  length prefixed, a filtered payload is a sequence of memcpy calls over contiguous ranges with no decode,
  no re-sort and no allocation beyond the output. That is why spec 3.3 declines contracts 11.2's optional
  coupling of kind ids to visibility: the coupling would buy a single memcpy instead of two or three,
  forever, in exchange for constraining every future kind assignment. Note the visibility levels are NOT
  monotonic in the kind id (4, 5 and 6 are `OwnerOnly` while 7 and 8 are `Everyone`), so the run walk is
  the only correct shape.
- [ ] **Step 4: Add the OWNER REMAINDER as a second projection, and record that the spec left its home
  open.** Spec 7.6 lists an "owner remainder" server-to-client message and spec 7.4 says the owner-only
  remainder rides a targeted game message, but neither says which package builds the bytes. The plan's
  choice: `ItemInstanceVisibility.OwnerRemainder(payload, revealedMask, destination)` writes the
  COMPLEMENT of `PublicView(payload, Everyone, ...)`, in the same retained-run shape, and lives here
  beside `PublicView` so the two cannot disagree. The MESSAGE KIND stays the game's, because
  `TileProtocol` reserves the `ushort` kind space to the game and the engine only caps the frame
  (`TileProtocol.Frames.cs:65`). Add a fact that `PublicView` bytes plus `OwnerRemainder` bytes reconstruct
  the full payload for an identified item at `OwnerOnly`.
- [ ] **Step 5: State the ground-item rule in code, because it is a rule and not an omission.** A drop's
  entity net id is nobody's, so there is no viewer this design calls the owner of a ground stack (spec
  7.4). A ground item's public view is `PublicView(payload, PropertyVisibility.Everyone, ...)` and there
  is NO owner remainder for a drop. Kind 6 `BoundTo` is therefore stripped before the component is
  written, so a passer-by cannot read who a dropped item is bound to, which is a fact about a PLAYER
  rather than about an item. Put that paragraph in the XML doc, and add the fact that a rare's 58 byte
  payload replicates as 54 on the ground, which is budget 11's input.
- [ ] **Step 6: Add the allocation fact under the existing `AllocSensitive` collection** if and only if
  `KhaozEngine.ItemInstances.Tests` gains one. It does not have that collection today and this task does
  not add a process-global one: assert instead that `PublicView` writes into a caller `Span<byte>` and
  returns a length, so the absence of allocation is a property of the signature.
- [ ] **Step 7: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release
git add KhaozEngine.ItemInstances/ItemInstanceVisibility.cs KhaozEngine.ItemInstances/ItemInstancePayload.cs KhaozEngine.ItemInstances.Tests/Visibility
git commit -m "iteminstances(visibility): one CanSee behind replication and tooltips"
~~~

---

### Task 9: `InstanceIdAllocator`, the store epoch and the rotation guard (medium, gate: milestone 1.1)

Spec 3.6 over contracts 6.2. Spec 20 puts spec 17 row 12 in phase 1 for a stated reason: the allocator
ships in this phase and its epoch refusal is the one behaviour in it that cannot be added afterwards
without a durable migration.

**Files:**

- Create: `KhaozEngine.ItemInstances/InstanceIdAllocator.cs`
- Create: `KhaozEngine.ItemInstances/IInstanceIdStore.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Allocator/InstanceIdAllocatorTests.cs`

**Interfaces:**

- Consumes: `NetIdAllocator` from `KhaozEngine.Replication` for the packing scheme ONLY, see step 3
- Produces: `InstanceIdAllocator` with `Next`, `Rotate(ushort newNodeId)` and an epoch-bound durable state

- [ ] **Step 1: Write the failing tests, which are spec 17 row 12's four clauses plus the exhaustion throw.**

~~~csharp
[Fact] public void Rotate_issues_no_id_in_the_old_nodes_range()
[Fact] public void A_boot_on_a_node_id_already_on_the_retired_list_throws()
[Fact] public void An_allocator_whose_persisted_store_epoch_differs_from_the_live_one_refuses_to_issue()
[Fact] public void A_crash_skips_the_unissued_remainder_of_a_reserved_block_and_never_reissues()
[Fact] public void The_range_is_persisted_BEFORE_the_first_id_in_it_is_handed_out()
[Fact] public void Exhausting_the_48_bit_counter_throws_rather_than_wrapping()
[Fact] public void Node_zero_ids_are_numerically_identical_to_a_plain_counter()
~~~

  The fifth is the one that catches the failure this whole section exists to prevent, and it is an ORDER
  assertion rather than a value one: drive the allocator through a fake `IInstanceIdStore` that records
  the interleaving of persist calls and issue calls, and assert the persist for a block precedes every
  issue from it. Contracts 6.2: the ORDER is the contract, not the batch size, and a batching optimisation
  is where it gets quietly inverted.

- [ ] **Step 2: Take the durable store as a constructor seam**, never an ambient static:

~~~csharp
public interface IInstanceIdStore
{
    InstanceIdState Read();                                   // high-water mark, node id, store epoch, retired nodes
    void Persist(in InstanceIdState state);                   // called BEFORE any id in the new block is issued
}

public readonly record struct InstanceIdState(long PackedHighWater, ushort NodeId, long StoreEpoch, ReadOnlyMemory<ushort> RetiredNodes);
~~~

  The host owns the implementation, because the journal store is where a real one persists. The engine
  ships the seam and the arithmetic and no provider, which keeps `KhaozEngine.ItemInstances` in `Foundation`.

- [ ] **Step 3: Reuse `NetIdAllocator`'s SCHEME, with its own counter, and say which.** Contracts 6.2 and
  spec 3.6 both say Scope B reuses the type behind `InstanceIdAllocator` with its OWN persisted high-water
  mark, because a net id and an instance id are different spaces that must not share a counter.
  `NetIdAllocator` lives in `KhaozEngine.Replication`, which is a `Server` package, and
  `KhaozEngine.ItemInstances` is `Foundation` and cannot reference it. **The plan's choice, recorded because
  the spec does not name the layering problem:** `InstanceIdAllocator` holds its own copy of the four
  constants (`CounterBits = 48`, `NodeBits = 16`, `CounterMask`, `MaxNodeId`) and its own `Pack`, `NodeOf`
  and `CounterOf`, with a doc comment naming `NetIdAllocator.cs:14-70` as the scheme it mirrors and a test
  asserting the two produce identical packed values for the same inputs. That test lives in
  `KhaozEngine.Server.Tests`, which already references both, so `KhaozEngine.ItemInstances.Tests` keeps its
  narrow reference set.
- [ ] **Step 4: Reserve durably BEFORE issuing.** The allocator reserves a block of 4,096 ids by persisting
  `high-water + 4096` and only then hands out ids from below it. A crash SKIPS the unissued remainder,
  which is free at 2^48 per node, and can never reissue one.
- [ ] **Step 5: Bind the state to the STORE EPOCH and refuse when it differs.** The allocator records the
  `store_epoch` its high-water mark was persisted under and REFUSES TO ISSUE when the live epoch differs.
  A point-in-time restore rolls the high-water mark, the node id AND the retired list back together, so a
  retired-node check cannot be the restore guard: in the restored bytes the node in use was never retired
  and the check passes. The epoch is a comparison between a restored value and a value an OPERATOR rotated,
  which is the property the retired list could never have. The journal owns both mechanism and runbook:
  `IMutationJournalMaintenance.RotateStoreEpochAsync`, and
  `DURABLE-PLAYER-JOURNAL-DESIGN-2026-09-06.md` section 10. Put both citations in the refusal's message.
- [ ] **Step 6: Keep the retired node list, demoted to a RE-BOOT guard.** `Rotate(ushort newNodeId)`
  appends the old node id, and a boot refuses a node id already on the list. That catches an operator who
  rotates onto a node id this store has already used, which is an ordinary configuration mistake worth one
  refusal at boot. It is no longer the restore guard, because it cannot be one.
- [ ] **Step 7: Write the id as an UNSIGNED varint over the int64 bit pattern**, never zig-zagged
  (contracts 15). `Pack(65535, counter)` sets the high bit and is a NEGATIVE `long`, so a zig-zag would
  encode it as a ten byte value with the sign flipped into the low bit. Add a fact pinning a node 65,535
  id's varint length. Node 0, the only shape a single-process server has, keeps ids numerically identical
  to a plain counter and costs four varint bytes up to 268,435,455.
- [ ] **Step 8: Implement the which-items-get-an-id rule as a pure predicate**, contracts 6.2 verbatim,
  because it is easy to get backwards: an item gets an instance id when its encoded payload is NON-EMPTY,
  or its definition declares durability, sockets or any per-instance field. The rule is a property of the
  ITEM rather than of the definition, so a definition gaining a property later does not retroactively give
  every stored copy an id it does not have.
- [ ] **Step 9: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~Allocator
git add KhaozEngine.ItemInstances/InstanceIdAllocator.cs KhaozEngine.ItemInstances/IInstanceIdStore.cs KhaozEngine.ItemInstances.Tests/Allocator
git commit -m "iteminstances(ids): node-prefixed instance ids bound to the store epoch"
~~~

**Group B acceptance:** spec 17 rows 1, 2, 3, 12 and 16 green, and contracts 9.8's 45 bytes reproduced
byte for byte. That is spec 20 phase 1's acceptance in full.

---

## Group C: the container, the pages and the commit (spec 4, 5 and 6)

### Task 10: Container codec version 2 and the version 1 reader (large, gate: milestone 1.1)

Spec 4.4 and 4.5. **LIFT FROM THE SPIKE:** `KhaozEngine.Benchmarks/Items/ContainerPageCodec.cs` (210
lines) already writes this format byte for byte with the redundant `FirstSlot` check and the trailing-byte
refusal. What it does NOT have is the version 1 dispatch, because the spike never met a stored v1 blob.
Lift the encoder and the decoder, add the dispatch, swap its `Varint` for `ContentVarint`, and make the
types public with the spec 2.2 names.

**Files:**

- Create: `KhaozEngine.ItemInstances/ItemContainerPageCodec.cs`
- Create: `KhaozEngine.ItemInstances/ItemContainerPageCodec.Decode.cs`
- Create: `KhaozEngine.ItemInstances/PageEntry.cs`
- Modify: `KhaozEngine.Items/ItemContainerCodec.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Pages/ItemContainerPageCodecTests.cs`
- Create: `KhaozEngine.Foundation.Tests/Items/ItemContainerCodecVersionTests.cs`
- Create: `KhaozEngine.Foundation.Tests/Items/Fixtures/container-v1-*.blob` (checked in)

**Interfaces:**

- Consumes: `ContentVarint`, task 5's payload codec, task 7's wrapper, `ItemSlot`
- Produces: `ItemContainerPageCodec` (`Encode`, `TryDecode`, `Validate`, `Version`), `PageHeader`,
  `PageEntry`, `PageSlotInput`, and `ItemContainerCodec.Version` as a `public const ushort` at 2

- [ ] **Step 1: Write the cross-version facts FIRST, which are spec 17 row 4.** They need a checked-in
  version 1 blob, so produce one with the CURRENT encoder before changing anything and commit it as a
  fixture. `KhaozEngine.Server.Tests/NetWorld/Fixtures/cell-v1-32bit.blob` is the precedent for a
  checked-in pre-change blob and its `None Include ... CopyToOutputDirectory` csproj shape.

~~~csharp
[Fact] public void A_version_1_blob_decodes_through_the_version_2_reader_unchanged()
[Fact] public void Every_slot_from_a_version_1_blob_seats_instance_id_0_empty_payload_and_flag_clear()
[Fact] public void A_version_1_blob_takes_content_version_stamp_0_so_every_rule_applies()
[Fact] public void A_version_1_blobs_eleven_Validate_rules_are_all_still_enforced()
[Fact] public void A_version_1_blob_whose_declared_slot_count_differs_is_still_refused_whole()
[Fact] public void A_version_2_writer_never_produces_a_version_1_blob()
[Fact] public void Byte_0_value_1_dispatches_to_version_1_and_anything_else_to_the_ushort_reader()
~~~

  The fifth is load bearing for Grimhollow, whose `WidenBag` and `NarrowBag` helpers exist precisely
  because of that refusal. Do not "improve" it.

- [ ] **Step 2: Write the version 2 page facts.**

~~~csharp
[Fact] public void A_page_round_trips_every_field_of_4_4()
[Fact] public void A_FirstSlot_that_is_not_PageIndex_times_the_page_size_is_refused()
[Fact] public void Entries_out_of_ascending_slot_order_are_refused()
[Fact] public void A_blob_that_runs_out_of_bytes_before_EntryCount_is_refused()
[Fact] public void Trailing_bytes_after_the_last_entry_are_refused()
[Fact] public void A_non_quarantined_entry_above_MaxInstancePayloadBytes_answers_payload_oversize()
[Fact] public void A_QUARANTINED_entry_above_MaxInstancePayloadBytes_encodes_and_decodes()
[Fact] public void An_OSRS_slot_entry_is_seven_bytes_against_version_1s_fixed_ten()
[Fact] public void A_full_page_of_100_rares_is_under_eight_kilobytes()
~~~

  The seventh is the cap-raise case spec 4.4 exists for and it is not hypothetical: engine 20.x raises
  the cap, a six socket item reaches 700 bytes, a shard still on 19.x loads the page, the entry
  quarantines, the wrapper is about 711 bytes, and it HAS to be writable or the page cannot be re-encoded
  and the whole container becomes uncommittable. One oversize item must not cost a player their bank.
  The eighth and ninth are budgets 2 and 3.

- [ ] **Step 3: Implement the format exactly as spec 4.4 writes it.**

~~~
[FormatVersion: uint16 LE]             // 2. Byte 0 is also the legacy dispatch byte
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
  [PayloadLength: varint int32]        // bounded by the SECTION cap, see step 5
  [Payload: PayloadLength bytes]
~~~

- [ ] **Step 4: Implement the byte 0 dispatch and write the rule down where it will be read.** Version 1
  put a single `byte` at offset 0, so a reader has to tell a v1 blob from a v2 one before it knows how
  wide the version field is. **Byte 0 is the dispatch: the value 1 means the version 1 format, and
  anything else means a `ushort` version whose low byte is that value.** The only cost is that container
  codec versions congruent to 1 modulo 256 are never assigned, so version 257 is skipped and versions 2
  through 256 are free. Put that sentence in the XML doc on `ItemContainerCodec.Version`, because a later
  implementer would otherwise assign 257 and break every stored v1 bank in the fleet. Spec 21 records it.

- [ ] **Step 5: Apply the two payload bounds, which contradict on purpose and are settled here.** Spec 4.4:
  a NON-quarantined entry's payload is at most `MaxInstancePayloadBytes` and a larger one is refused with
  `payload-oversize`. A QUARANTINED entry's payload is the wrapper, bounded by the page's own bound, which
  is the journal's 2 MiB projection section cap less the rest of the page (`JournalLimits.cs:16`). Both
  bounds live in the decoder and the encoder, and the quarantined exception is guarded by the entry's own
  `EntryFlags` bit 0 rather than by sniffing the payload for `KECQ`: `K` is 0x4B, which is a perfectly
  legal property kind varint, so the sniff is ambiguous and the flag byte is what exists to avoid it.
- [ ] **Step 6: Keep `PageIndex` and `FirstSlot` both present, redundantly, on purpose.** The decoder
  checks `FirstSlot == PageIndex * expectedPageSlots` and refuses a mismatch. Two bytes per page, twenty
  on a ten page bank, and it catches a page written into the wrong section, which is otherwise silent.
- [ ] **Step 7: Bump `ItemContainerCodec.Version` to a `public const ushort` at 2 and keep the version 1
  path verbatim.** All eleven of `Validate`'s current rules stay exactly as they are
  (`ItemContainerCodec.cs:74-104`). The version 1 decode path seats every slot with instance id 0, an
  empty payload and the quarantined flag clear, and takes stamp 0. Do not refactor the v1 reader while
  you are here: it is the thing the fixtures pin.
- [ ] **Step 8: Name the page-level reason tokens and record that the spec did not.** Contracts 9.7's
  eight tokens are PAYLOAD reasons. A page can also fail at the page level (bad version, bad header,
  truncated, entries out of order, wrong slot origin, trailing bytes), and neither spec names those
  tokens. **The plan's choice:** a second closed set in `ItemContainerPageReason`, using the spike's own
  names so the benchmark and the package agree: `page-version`, `page-truncated`, `page-slot-origin`,
  `page-slot-order`, `page-entry-count`, `page-entry-malformed`, `page-trailing-bytes`. They are page
  reasons rather than quarantine reason CODES, because spec 5.5 step 1 quarantines a failed page as a UNIT
  and the wrapper's `ReasonCode` byte is an ordinal from the payload set. Flag this to the spec owner in
  the task's report so the spec can adopt or rename them.
- [ ] **Step 9: Run both suites green, then the whole solution once.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release
dotnet test KhaozEngine.Foundation.Tests/KhaozEngine.Foundation.Tests.csproj -c Release
dotnet test KhaozEngine.slnx -c Release
~~~

- [ ] **Step 10: Commit.**

~~~bash
git add KhaozEngine.ItemInstances/ItemContainerPageCodec.cs KhaozEngine.ItemInstances/ItemContainerPageCodec.Decode.cs KhaozEngine.ItemInstances/PageEntry.cs KhaozEngine.Items/ItemContainerCodec.cs KhaozEngine.ItemInstances.Tests/Pages KhaozEngine.Foundation.Tests/Items
git commit -m "items(codec): container codec version 2 reads version 1"
~~~

---

### Task 11: `ItemContainerPage`, `PagedItemContainer` and the merge rule (large, gate: milestone 1.1)

Spec 4.6, 5.2, 5.3 and 5.7. Write fresh rather than lifting: the spike has no paged container at all, only
a page codec.

**Files:**

- Create: `KhaozEngine.ItemInstances/ItemContainerPage.cs`
- Create: `KhaozEngine.ItemInstances/PagedItemContainer.cs`
- Create: `KhaozEngine.ItemInstances/PagedItemContainer.Capacity.cs`
- Create: `KhaozEngine.ItemInstances/InstanceStacking.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Pages/PagedItemContainerTests.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Pages/InstanceStackingTests.cs`

**Interfaces:**

- Consumes: `ItemSlot`, task 10's page codec, `ItemRow` from `KhaozEngine.Catalog` for the definition's
  durability, socket and stack-cap facts
- Produces: `ContainerPageSlots`, `ItemContainerPage`, `PagedItemContainer`, `InstanceStacking.CanMerge`

- [ ] **Step 1: Write the stacking facts, because rule 4 is the whole of "their properties are identical".**

~~~csharp
[Fact] public void Two_entries_merge_only_when_all_four_of_4_6_hold()
[Fact] public void Equal_payload_bytes_merge_and_one_differing_byte_does_not()
[Fact] public void An_entry_carrying_kind_5_or_132_never_merges_whatever_the_predicate_says()
[Fact] public void A_quarantined_entry_never_merges()
[Fact] public void The_predicate_is_consulted_per_operation_and_never_cached()
[Fact] public void A_merge_keeps_the_numerically_LOWER_instance_id_so_a_replay_in_either_order_agrees()
[Fact] public void A_merge_saturates_at_int_MaxValue_exactly_as_Add_does_today()
[Fact] public void Two_items_differing_only_in_an_UNKNOWN_field_do_not_merge()
~~~

  Rule 4 is a `memcmp` rather than a structural comparison ONLY because the payload is canonical. A
  reviewer who sees a decode inside `CanMerge` should reject it.

- [ ] **Step 2: Write the page and capacity facts, which are Ruinborne's model restated (spec 5.7).**

~~~csharp
[Fact] public void A_grant_that_opens_a_NEW_slot_is_refused_at_or_above_capacity()
[Fact] public void A_grant_that_merges_entirely_into_existing_stacks_is_allowed_at_any_occupancy()
[Fact] public void Lowering_capacity_below_occupancy_is_legal_and_trims_nothing()
[Fact] public void Capacity_is_never_read_from_content()
[Fact] public void A_hole_survives_a_load_a_save_and_a_remap_and_costs_zero_bytes()
[Fact] public void Reading_a_page_never_dirties_it()
[Fact] public void Slot_743_is_page_7_slot_43()
~~~

- [ ] **Step 3: Implement the geometry.** `public const int ContainerPageSlots = 100`. One hundred rather
  than 128, deliberately: the power of two buys a shift a compiler produces anyway and costs legibility
  everywhere a human reads a page number. Contracts 4.5's power-of-two rule binds CONTENT chunk sizes and
  says nothing about container pages. Spec 21 records 100 as expensive to change, so it is a const with a
  doc comment and not a constructor parameter.
- [ ] **Step 4: Implement `ItemContainerPage` with exactly two things that dirty it**, spec 5.3: an
  operation that changed a slot, and a remap that changed an id. Nothing else, and in particular READING
  one never does. It holds the decoded slots, the stamp, the dirty flag and the page index.
- [ ] **Step 5: Implement `PagedItemContainer` splitting the two concepts `ItemContainer` conflates.**
  SLOT SPACE is the page geometry, fixed at construction, `PageCount * ContainerPageSlots`, an address
  space that never shrinks. CAPACITY is a separate mutable integer, the number of OCCUPIED slots a grant
  may leave behind, consulted by `Add` and by nothing else. Ruinborne's four rules of spec 5.7 are the
  behaviour, and rule 3 is the surprising one: lowering capacity below occupancy is LEGAL, the container
  loads intact, is never trimmed, and is refused new slots until occupancy falls. That is the
  generalisation of contracts 8.2 kind 4's over-cap stack policy.
- [ ] **Step 6: Keep entries SPARSE and never compact.** A hole is the absence of an entry and costs zero
  bytes, exactly as version 1's sparse form already did. The dense renumber Ruinborne's repair explicitly
  refuses to do is not something this container can do by accident, and a test pins that.
- [ ] **Step 7: Expose the dirty set**, because task 15's commit builder asks the container for it and
  folds those pages into whatever commit comes next (spec 5.6). That is what makes the lazy rewrite cost
  nothing: it never causes a commit, it only joins one.
- [ ] **Step 8: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release
git add KhaozEngine.ItemInstances/ItemContainerPage.cs KhaozEngine.ItemInstances/PagedItemContainer.cs KhaozEngine.ItemInstances/PagedItemContainer.Capacity.cs KhaozEngine.ItemInstances/InstanceStacking.cs KhaozEngine.ItemInstances.Tests/Pages
git commit -m "iteminstances(pages): paged containers, the capacity gate and byte-equal stacking"
~~~

---

### Task 12: `InstanceValidator`, its thirteen checks, the counter and the log line (large, gate: milestone 1.1)

Spec 12.2, 12.3, 12.6 and 12.7 over contracts 10.1, 10.2 and 10.4. Write fresh: the spike has no
validator. `KhaozEngine.Content/JsonSchemaValidator.cs:11-101` is the run-to-the-end sweep shape to copy.

**Files:**

- Create: `KhaozEngine.ItemInstances/InstanceValidator.cs`
- Create: `KhaozEngine.ItemInstances/InstanceValidator.References.cs`
- Create: `KhaozEngine.ItemInstances/InstanceValidationReport.cs`
- Create: `KhaozEngine.ItemInstances/InstanceValidationStrings.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Validation/InstanceValidatorTests.cs`

**Interfaces:**

- Consumes: `IContentSnapshot` (`TryGetRow`, `IsRetired`), task 4's registry, task 5's field walk
- Produces: `InstanceValidator.Validate(page, snapshot)`, `InstanceValidationReport`,
  `InstanceValidationFinding`, `InstanceValidationOutcome`

- [ ] **Step 1: Write one fact per check, thirteen of them**, naming the reason token spec 12.2 assigns.
  Checks 1 to 5, 9, 10 and 11 are STRUCTURAL and quarantine. Checks 6, 7 and 8 are DRIFT and also
  quarantine. Check 12 is POLICY and is TOLERATED. Check 13 is POLICY and produces `Retired`.
  The two that a reader will get wrong without a test are 12 and 13, so write those first:

~~~csharp
[Fact] public void Check_12_an_over_cap_count_is_counted_and_changes_nothing()
[Fact] public void Check_13_a_RETIRED_definition_is_not_a_quarantine_and_the_page_still_loads()
[Fact] public void A_structural_failure_quarantines_that_ENTRY_and_leaves_the_rest_of_the_page_alone()
[Fact] public void An_unresolved_content_reference_quarantines_because_no_rule_covered_it()
[Fact] public void The_validator_accumulates_and_never_stops_at_the_first_finding()
[Fact] public void The_validator_never_throws_never_logs_never_counts_and_never_mutates_the_page()
~~~

- [ ] **Step 2: DERIVE checks 6 and 7 from the registry's `InstanceReferenceTarget` descriptors**, in the
  same recursive order the remap pass of task 13 walks, over the same nested payloads. Spec 12.2 says why:
  an earlier draft wrote check 7 as a closed enumeration and it already omitted kind 7's material ids and
  a socket's `ContainedDefinitionId`, and it would have omitted every game kind at or above 1,024 forever.
  A kind cannot be remapped-but-not-validated or validated-but-not-remapped. Check 8 stays hand written,
  because a tier ordinal is not a content id: it is a key INTO the row check 7 already resolved.
- [ ] **Step 3: Keep it PURE.** No store reads, no ambient state, no logging, no counter, no throw for a
  content reason. A throw from it is a bug in the validator. The caller logs, counts and quarantines.

- [ ] **Step 4: Ship the three placeholder `StringId`s and no translation.** `khaoz.item.quarantined`,
  `khaoz.item.retired`, `khaoz.item.unidentified` (spec 12.3). All three are player facing, so none is a
  literal, which is AGENTS.md's founding rule. They are prefixed `khaoz.` deliberately: they are ENGINE
  strings rather than content rows, so contracts 12.1's derived `<type key>.<content key>.<field>` grammar
  does not name them and the prefix keeps them out of its space. They resolve through
  `ContentStringCatalog` on the `SafeFormat` path, so a translator's malformed template falls back to the
  unformatted template rather than throwing inside the frame loop. A reason code and a stamped version are
  NOT player text and are never formatted into these strings.
- [ ] **Step 5: Name the counter and the log line, and invent no others.** Counter
  `khaoz.content.quarantined_records`, dimensioned by content type id and reason code. ONE log line per
  PAGE under category `ContentValidation` at Warning, naming the reason code, the stamped version, the
  active version and the owning stream key, and NEVER the payload bytes or a raw account id. One per page
  rather than per entry, because a page that fails wholesale would otherwise emit a hundred identical
  lines, which is how an operator learns to filter the category out. The COUNTER is still incremented per
  record, because a counter is what a dashboard reads and a log line is what a human reads. Add a fact
  asserting a wholesale page failure emits exactly one line and a hundred counter increments.
- [ ] **Step 6: Implement kind 128's identification mechanic on the registered bit.** `RevealedMask` bit N
  is the bit a kind was REGISTERED with, never its position in the ascending list of gated kinds (spec
  12.7 and spec 21's last row). Add a fact that registering a NEW gated engine kind at, say, 9 does NOT
  move bits 0 to 3, which is the exact hazard the registered constant exists to prevent.
- [ ] **Step 7: Add the unidentified stacking fact and its accepted leak.** An unidentified item still
  STACKS by byte equality, and two unidentified items with different hidden affixes have different bytes,
  so they do not merge. That leaks one bit: a player who tries to stack two unidentified items learns
  whether they are identical. The leak is inherent to stacking by bytes and spec 15.8 records it as
  accepted. Pin the behaviour so nobody "fixes" it later without reading that section.
- [ ] **Step 8: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release
git add KhaozEngine.ItemInstances/InstanceValidator.cs KhaozEngine.ItemInstances/InstanceValidator.References.cs KhaozEngine.ItemInstances/InstanceValidationReport.cs KhaozEngine.ItemInstances/InstanceValidationStrings.cs KhaozEngine.ItemInstances.Tests/Validation
git commit -m "iteminstances(validation): thirteen checks, three outcomes, one counter"
~~~

---

### Task 13: The registry-derived remap pass and its idempotence (large, gate: milestone 1.1)

Spec 5.5 step 2 over contracts 8.1 through 8.6. **LIFT FROM THE SPIKE:**
`KhaozEngine.Benchmarks/Items/RemapRuleSet.cs` (269 lines) has the whole shape: the applicable-rule
prefilter keyed on `(typeId, fromId)`, the scan that never writes when nothing matched, the innermost-first
re-encode, and the affix re-sort. Two things change on the way in. Its `RemapRule` is REPLACED by Scope A's
`RemapRule` and `RemapRuleSet` from `KhaozEngine.Catalog`, and its hard-coded `ScanPayload` switch is
REPLACED by a walk of the registry's reference targets, for the same reason task 5 step 2 gives.

**Files:**

- Create: `KhaozEngine.ItemInstances/InstanceRemapPass.cs`
- Create: `KhaozEngine.ItemInstances/InstanceRemapPass.Rewrite.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Remap/InstanceRemapPassTests.cs`

**Interfaces:**

- Consumes: `RemapRule`, `RemapRuleKind`, `RemapRuleSet` from `KhaozEngine.Catalog`, task 4's registry,
  task 10's page codec
- Produces: `InstanceRemapPass.Apply(page, rules, pageStamp, destination)` returning what changed

- [ ] **Step 1: Write the idempotence fact first, which is spec 17 row 8.** Applying the full ordered rule
  set TWICE produces the same bytes as applying it once (contracts 8.3). That is what makes a crash between
  apply and commit safe, and it is why the lazy rewrite is safe at all.
- [ ] **Step 2: Write the depth and width facts, which are the two an implementer gets wrong.**

~~~csharp
[Fact] public void A_gem_socketed_into_a_sword_is_rewritten_by_the_same_rule_that_rewrites_it_in_a_bag()
[Fact] public void A_ReplacedBy_that_WIDENS_a_varint_recomputes_the_two_lengths_above_it()
[Fact] public void A_rewrite_restores_canonical_affix_order_when_a_replacement_moves_a_mod_id()
[Fact] public void A_rule_matching_nothing_is_a_SCAN_and_writes_zero_bytes()
[Fact] public void A_rule_whose_IntroducedIn_is_at_or_below_the_page_stamp_does_not_apply()
[Fact] public void Rules_apply_in_Sequence_order_in_ONE_pass()
[Fact] public void A_page_stamped_NEWER_than_the_active_version_is_not_an_error_and_is_not_lowered()
~~~

  The second is the whole reason the pass RE-ENCODES rather than patching bytes in place: a nested payload
  carrying mod 91 is one byte shorter than the same payload carrying mod 4210, so a hit recomputes,
  innermost first, the nested payload's bytes, then the socket entry's `NestedLength`, then kind 132's
  `Length`, then the entry's `PayloadLength` in the page.

- [ ] **Step 3: Walk every id the REGISTRY's reference targets name, never a list in a document.** The
  entry's own definition id, every id inside every registered field, and, through kind 132's
  `NestedPayload` slot, every id inside every socket's nested payload along with that socket's own
  `ContainedDefinitionId`. Nothing is skipped for being nested. That is the property that stops an item
  surviving three publishes invisibly and then quarantining on the day a player unsockets it.
- [ ] **Step 4: Mark the page DIRTY and set its IN-MEMORY stamp to the active version when anything
  changed, and do NOT write it.** The rewrite is lazy and rides the next ordinary commit (spec 5.5 step 3,
  contracts 10.3). Eagerly rewriting at boot is a write storm proportional to the whole player base
  arriving exactly when the server is coldest. The cost of lazy is that a remapped page can be lost on a
  crash, which means it is remapped again on the next load, and that is safe because the set is idempotent.
- [ ] **Step 5: Rely on contracts 8.3's publish-side guarantee rather than re-deriving it.** No rule's
  `ToId` is any earlier rule's `FromId` for the same type, so ONE pass is enough. Do not add a fixed-point
  loop "just in case": it would hide a publish validator bug rather than surface it. Add a defensive fact
  that a rule set violating that shape is REFUSED by `RemapRuleSet` before the pass runs.
- [ ] **Step 6: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~Remap
git add KhaozEngine.ItemInstances/InstanceRemapPass.cs KhaozEngine.ItemInstances/InstanceRemapPass.Rewrite.cs KhaozEngine.ItemInstances.Tests/Remap
git commit -m "iteminstances(remap): the registry-derived pass and its idempotence"
~~~

---

### Task 14: `KhaozEngine.ItemInstances.Journal` and the container load path (medium, gate: milestone 1.1)

Spec 2.1, 5.2 and 5.5. The package exists for a LAYERING reason rather than a size one: composing a
`JournalCommit` needs `KhaozEngine.WorldStore`, and putting that inside `ItemInstances` would drag a
`Server` package into `Foundation` and therefore into every client build.

**Files:**

- Create: `KhaozEngine.ItemInstances.Journal/KhaozEngine.ItemInstances.Journal.csproj`
- Create: `KhaozEngine.ItemInstances.Journal/README.md`
- Create: `KhaozEngine.ItemInstances.Journal/ContainerSectionNames.cs`
- Create: `KhaozEngine.ItemInstances.Journal/ContainerLoad.cs`
- Create: `KhaozEngine.ItemInstances.Journal/ContainerLoadResult.cs`
- Create: `KhaozEngine.ItemInstances.Journal/ItemInstanceEvents.cs`
- Create: `KhaozEngine.Server.Tests/ItemInstances/ContainerLoadTests.cs`
- Modify: `KhaozEngine.slnx`, `KhaozEngine.Server/KhaozEngine.Server.csproj`,
  `KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj`, `KhaozEngine.Tests/ArchitectureTests.cs`

**Interfaces:**

- Consumes: `KhaozEngine.ItemInstances`, `KhaozEngine.WorldStore`
- Produces: `ContainerSectionNames` (`Format`, `Parse`), `ContainerLoad.Load`, `ContainerLoadResult`,
  `ItemInstanceEvents`

- [ ] **Step 1: Write the section naming facts.** `<container>/p<NN>`, zero padded to two digits, unpadded
  above page 99 (spec 5.2). `bank/p00` through `bank/p09` for a 1,000 slot bank, `bag/p00`, `worn/p00`.
  `JournalProjectionWrite`'s section name is an identifier capped at 128 characters over
  `[A-Za-z0-9._:/-]` (`JournalProjectionWrite.cs:13`, `JournalLimits.cs:86-98`), and the slash is in that
  set, so nothing needs escaping. `Parse` is the ONE place the name is taken apart, and a round-trip
  `[Theory]` over pages 0, 9, 10, 99, 100 and 563 pins it.
- [ ] **Step 2: Write the load facts, which are spec 5.5's five steps as five assertions.**

~~~csharp
[Fact] public void A_page_that_fails_at_the_PAGE_level_quarantines_as_a_UNIT()
[Fact] public void Rules_apply_BEFORE_the_validator_runs_so_a_drift_finding_means_no_rule_covered_it()
[Fact] public void A_rule_that_changed_something_marks_the_page_dirty_and_does_not_write_it()
[Fact] public void A_failed_entry_check_quarantines_that_entry_and_leaves_the_page_loading()
[Fact] public void Load_reads_no_store_touches_no_ambient_state_and_makes_one_pass()
~~~

  The ORDER in the second fact is the part that is easy to get backwards and it changes the meaning of
  every drift finding, so it is asserted rather than assumed.

- [ ] **Step 3: Implement the load signature spec 5.5 gives**, taking its whole world as arguments:

~~~csharp
public static ContainerLoadResult Load(
    IReadOnlyList<JournalProjectionSection> sections,
    IContentSnapshot snapshot);
~~~

  It returns the decoded pages, the accumulated findings and the dirty set. No store reads, no ambient
  state, following the one-validator shape contracts 10.4 sets for the content side.

- [ ] **Step 4: Check the page against the SECTION it arrived in**, using `ContainerSectionNames.Parse`
  plus the page header's own `PageIndex`. That is spec 13 row 10 and it is otherwise silent.
- [ ] **Step 5: Wire the package in.** Add to `KhaozEngine.slnx`, to the `Server` umbrella's
  `ProjectReference` set, and update the locked umbrella membership in `ArchitectureTests`. Add a
  `ProjectReference` from `KhaozEngine.Server.Tests`, which already references `WorldStore` and both
  providers, which is exactly why spec 2.3 puts these tests there rather than in a new project.
- [ ] **Step 6: Write the package README**, self-contained, naming the layering reason the package exists.
- [ ] **Step 7: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~ItemInstances
git add KhaozEngine.ItemInstances.Journal KhaozEngine.Server.Tests/ItemInstances KhaozEngine.slnx KhaozEngine.Server/KhaozEngine.Server.csproj KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj KhaozEngine.Tests/ArchitectureTests.cs
git commit -m "iteminstances(journal): page section names and the container load path"
~~~

---

### Task 15: `ContainerCommitBuilder`, the tick-bounded batch and its crash facts (large, gate: milestone 1.1)

Spec 6.4, 6.5 and 6.6. Option A of spec 6.2's weighed table, with the batch window fixed at ONE SERVER
TICK. **LIFT THE COMMIT SHAPES FROM THE SPIKE:** `KhaozEngine.Benchmarks/Items/ItemsCommitFactory.cs`
already builds the exact `JournalCommit` this task emits, with one identity, one event per operation and
one projection write per page, and `ItemsJournalMeasurements.cs` drives it against a real SQLite store.

**This task changes NOTHING in `KhaozEngine.WorldStore`.** No change to either provider schema and no
change to the store conformance suite (spec 2.4). That is a RESULT rather than an accident: option B of
spec 6.2 would have needed all three and spec 6.3 prices it exactly. If an implementer finds themselves
editing `JournalCommit`, `JournalLimits` or a provider, STOP: the design has drifted to option B and the
owner has to choose it knowingly.

**Files:**

- Create: `KhaozEngine.ItemInstances.Journal/ContainerCommitBuilder.cs`
- Create: `KhaozEngine.ItemInstances.Journal/ContainerBatchWindow.cs`
- Create: `KhaozEngine.ItemInstances.Journal/ContainerOperation.cs`
- Create: `KhaozEngine.Server.Tests/ItemInstances/ContainerCommitBuilderTests.cs`

**Interfaces:**

- Consumes: `JournalCommit`, `JournalStreamMutation`, `JournalEvent`, `JournalProjectionWrite`,
  `JournalOperationIdentity`, `JournalLimits`, task 11's dirty set
- Produces: `ContainerCommitBuilder.Open`, `Apply`, `Close`

- [ ] **Step 1: Write the window facts, which are spec 6.4's five closers.** The batch closes on the FIRST
  of: the tick boundary, an operation that would touch a SECOND stream, an operation that sets
  `PresentAtCommit`, a CLIENT originated operation, or 128 events, 64 projection writes or the 8 MiB
  aggregate cap (`JournalLimits.cs:10, 11, 19`). One fact each, and one asserting the FIRST of them wins.
- [ ] **Step 2: Write the identity facts, which are spec 6.5 and the half that is easy to get wrong.**

~~~csharp
[Fact] public void A_batch_never_merges_two_CLIENT_originated_operations()
[Fact] public void A_SERVER_minted_batchs_intent_is_the_canonical_ORDERED_operation_list()
[Fact] public void A_CLIENT_headed_batchs_intent_is_the_clients_own_operation_ALONE()
[Fact] public void A_client_headed_resubmit_omitting_the_server_work_still_resolves_Replayed()
[Fact] public void A_client_resubmit_with_different_parameters_is_OperationConflict()
~~~

  The fourth is the load bearing one. If a client-headed batch's intent were the whole ordered list, the
  resubmit would hash differently, `ResolveOperationAsync` would answer `OperationConflict`, and the
  consumer would treat a COMMITTED withdraw as a failed one: `Withdraw` rolls the admitted view back and
  supersedes everything queued behind it transitively (`JournalAdmittedState.cs:117-147`), the player is
  told the action failed, and a re-click applies it twice.

- [ ] **Step 3: Write the crash and replay facts, which are spec 6.6 case by case**, against a real SQLite
  store the way `MutationJournalStoreConformance` already does:

~~~csharp
[Fact] public void Crash_BEFORE_admission_leaves_nothing_and_a_client_resubmit_resolves_NotFound()
[Fact] public void Crash_AFTER_admission_before_commit_reverts_the_pages_and_SKIPS_the_allocated_ids()
[Fact] public void Crash_AFTER_commit_replays_to_the_ORIGINAL_receipt_and_result()
[Fact] public void A_terminal_store_failure_attaches_a_correction_whose_section_keys_ARE_the_pages_to_resync()
[Fact] public void A_transient_retry_corrects_nothing_and_the_batch_stays_admitted()
[Fact] public void Two_moves_of_one_stack_across_containers_leave_exactly_one_winner()
~~~

  The last is spec 17 row 14. The fourth needs no new journal shape: because a batch writes whole pages,
  the section keys in `JournalCorrection` are exactly the pages the consumer must resync.

- [ ] **Step 4: Implement the API spec 6.4 gives.**

~~~csharp
var batch = ContainerCommitBuilder.Open(streamKey, actionKind, scope, containers);
batch.Apply(operation);          // repeated, against an in-memory working copy
JournalCommit commit = batch.Close(identityFactory);
~~~

  `Close` emits ONE `JournalOperationIdentity`, ONE `JournalEvent` per logical operation in order, ONE
  `JournalProjectionWrite` per touched page carrying the page's FINAL bytes plus the pages the container's
  dirty set names, and ONE result. The audit trail is not collapsed, only the projection is.

- [ ] **Step 5: Encode the server-minted intent canonically.** `[Count: varint][ per operation: [Kind:
  varint][Parameters] ]`, little endian, minimal varints, contracts 15. Canonical because the journal
  hashes the intent to detect a conflicting replay (`JournalValidation.Hash` is `SHA256.HashData`,
  `JournalLimits.cs:135`), so two encodings of one batch must produce one byte sequence.
- [ ] **Step 6: Apply the page rule of spec 5.6 and nothing wider:** the pages holding the slots the
  operation changed, and no others. A move across two pages of one container is ONE commit with TWO
  projection writes on the SAME stream, which `JournalCommit` already allows (`JournalCommit.cs:37`,
  `:125-138`): one stream, two sections, one event. Atomicity is the database transaction's.
- [ ] **Step 7: Measure budget 4 here rather than in task 18.** Twenty crafts in one held action is at most
  20 KB and ONE commit, summed over `JournalCommit.OwnedByteCount`. Assert the commit COUNT in the test
  and leave the byte number to the benchmark, so a structural regression is a red test rather than a
  slower number nobody reads.
- [ ] **Step 8: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~ContainerCommitBuilder
git add KhaozEngine.ItemInstances.Journal KhaozEngine.Server.Tests/ItemInstances
git commit -m "iteminstances(journal): one commit per tick from a batch of page operations"
~~~

**Group C acceptance:** spec 17 rows 4, 8 and 14 green, and budget 4 measured at ONE commit.

---

## Group D: the wire (spec 7)

### Task 16: `TileGroundItemInstance` and the `SpawnGroundItem` overload (medium, gate: milestone 1.1)

Spec 7.2 and 7.3. A SIBLING component rather than a widened `TileGroundItem`, and the deciding row of spec
7.2's table is not a preference: `WriteGroundItem` writes exactly twenty bytes with no declared length
(`TileProtocol.Components.cs:321-344`), so a reader built against today's protocol consumes twenty bytes
and then reads the next component's type id. Adding a field makes every already-shipped client misparse the
rest of the entity. A sibling is a NEW extension type id, and `SnapshotWriter` length prefixes extension
components precisely so an older client skips an id it never registered (`SnapshotWriter.cs:11-13`).

**Files:**

- Create: `KhaozEngine.TileWorld.Netcode/TileGroundItemInstance.cs`
- Modify: `KhaozEngine.TileWorld.Netcode/TileProtocol.Components.cs`
- Modify: `KhaozEngine.TileWorld.Netcode/TileWorldServer.GroundItems.cs`
- Create: `KhaozEngine.TileWorld.Netcode.Tests/TileGroundItemInstanceTests.cs`

**Interfaces:**

- Consumes: `ReplicationRegistry.FirstExtensionTypeId`, `SnapshotWriter`
- Produces: `TileGroundItemInstance`, `TileGroundItemInstanceTypeId`, one `SpawnGroundItem` overload

- [ ] **Step 1: Write the failing tests, including the engine half of spec 17 row 11.**

~~~csharp
[Fact] public void A_drop_with_no_instance_seats_NO_sibling_component_and_costs_no_wire_bytes()
[Fact] public void A_drop_with_an_instance_round_trips_the_id_and_the_payload()
[Fact] public void An_OLD_client_registry_skips_the_sibling_and_still_parses_the_rest_of_the_entity()
[Fact] public void A_declared_length_longer_than_the_frame_answers_a_ZERO_length_payload_not_a_throw()
[Fact] public void A_declared_length_above_MaxInstancePayloadBytes_answers_a_zero_length_payload()
[Fact] public void SpawnGroundItem_throws_on_a_payload_above_the_cap_and_on_bytes_with_instance_id_0()
[Fact] public void The_existing_SpawnGroundItem_overload_still_compiles_and_seats_instance_id_0()
[Fact] public void The_instance_id_survives_a_drop_and_a_claim_by_a_stranger_and_by_the_dropper()
~~~

  The third is the compatibility claim the whole design choice rests on, so it is a test rather than a
  paragraph: build a registry WITHOUT the sibling registration, write a snapshot with it, and read back.

- [ ] **Step 2: Implement the component exactly as spec 7.2 writes it**, carrying NO dependency on
  `KhaozEngine.ItemInstances`. It holds an opaque `long` and opaque bytes, exactly as `TileGroundItem`
  holds an opaque `int`. The tile netcode still does not know what an item is.

~~~csharp
public struct TileGroundItemInstance : IComponent
{
    public long InstanceId;     // 0 is never seated: a drop with no instance carries no component
    public byte[] Payload;      // the PUBLIC view, see spec 7.4. Never mutated in place
}
~~~

- [ ] **Step 3: Register it at `FirstExtensionTypeId + 8` on the default channels.** The tile netcode owns
  ids up to `FirstExtensionTypeId + 15` (`TileProtocol.Components.cs`), so 8 is inside the block and does
  not eat the game's range. The write delegate writes the id as an unsigned varint and the payload length
  prefixed. The read delegate is TOTAL: a declared length longer than the frame, or longer than
  `MaxInstancePayloadBytes`, answers a zero length payload rather than throwing, which is the file's own
  rule because the bytes come from a remote peer (`TileProtocol.Frames.cs:27-34`).
- [ ] **Step 4: Do NOT register it `OwnerOnly`.** `ReplicationChannels.OwnerOnly` scopes a component to
  the client whose own net id equals the ENTITY's net id (`ReplicationChannels.cs:49-53`), and a drop's
  entity net id is never a viewer's, so registering it that way hides it from everyone including the
  person who dropped it. Spec 7.4 says so. Put that sentence in the registration comment, because it is
  the obvious-looking wrong answer.
- [ ] **Step 5: Add the ONE overload and delegate the existing one to it**, so no existing call site
  changes (spec 7.3):

~~~csharp
public long SpawnGroundItem(TileCoord at, int itemId, int count, long ttlTicks,
                            long instanceId, ReadOnlySpan<byte> payload);
~~~

  It throws on a payload above `MaxInstancePayloadBytes` and on a non-empty payload with instance id 0,
  because both are caller bugs in the same class as the existing non-positive count throw
  (`TileWorldServer.GroundItems.cs:64-69`). The engine does not DECODE the payload: the bytes came from
  the server's own container and the server is the only thing that ever writes them (spec 15.3).
- [ ] **Step 6: Take the cap as a constant local to `TileWorld.Netcode`**, mirroring
  `ItemSlot.MaxPayloadBytes` with a doc comment naming contracts 9.6, because this package has no items
  dependency and must not gain one. Add a fact in `KhaozEngine.Server.Tests`, which sees both, asserting
  the two constants are equal.
- [ ] **Step 7: Run green and commit.**

~~~bash
dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release
git add KhaozEngine.TileWorld.Netcode/TileGroundItemInstance.cs KhaozEngine.TileWorld.Netcode/TileProtocol.Components.cs KhaozEngine.TileWorld.Netcode/TileWorldServer.GroundItems.cs KhaozEngine.TileWorld.Netcode.Tests/TileGroundItemInstanceTests.cs
git commit -m "tileworld(netcode): a sibling ground component carrying opaque instance bytes"
~~~

---

### Task 17: The page delta, its one-frame bound and the resync request (large, gate: milestone 1.1)

Spec 7.5 and 7.6. **LIFT FROM THE SPIKE:** `KhaozEngine.Benchmarks/Items/PageWire.cs` has the delta
builder that MEASURES as it writes and answers -1 when the next change would not fit, which is the whole
mechanism.

**Where the delta builder lives, because the spec does not say and the layering forces it.** Spec 2.2 says
`KhaozEngine.TileWorld.Netcode` gains NO items dependency, and the delta body is "the entry body of 4.4
without its Slot field", which only the page codec can write. **The plan's choice:** the delta ENCODER and
its budget arithmetic live in `KhaozEngine.ItemInstances` as `ContainerPageDelta`, and
`KhaozEngine.TileWorld.Netcode` carries only the item-agnostic fragmenter from task 3. The SERVER that
owns both composes them. Flag this to the spec owner in the task report.

**Files:**

- Create: `KhaozEngine.ItemInstances/ContainerPageDelta.cs`
- Create: `KhaozEngine.ItemInstances/ContainerPageSyncRequest.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Pages/ContainerPageDeltaTests.cs`
- Create: `KhaozEngine.TileWorld.Netcode.Tests/PageSyncFrameBoundTests.cs`

**Interfaces:**

- Consumes: task 10's entry body writer, task 3's fragmenter, `TileProtocol.MaxGameMessageBytes`
- Produces: `ContainerPageDelta.TryBuild`, `ContainerPageSyncRequest`

- [ ] **Step 1: Write the frame-bound facts, which are spec 17 row 17 exactly.**

~~~csharp
[Fact] public void Fourteen_changed_rare_slots_produce_ONE_delta_frame()
[Fact] public void Fifteen_changed_rare_slots_produce_a_FRAGMENTED_page_send()
[Fact] public void A_100_slot_reorder_produces_a_fragmented_page_send()
[Fact] public void No_path_encodes_a_game_message_above_MaxGameMessageBytes()
[Fact] public void A_cold_open_of_a_full_rare_page_is_at_most_8_KB_in_at_most_8_frames()
[Fact] public void A_single_craft_costs_73_bytes_in_one_frame()
~~~

  The last two are budgets 7 and 8. The fourth is the one that protects the tick: `EncodeGameMessage`
  THROWS above the cap (`TileProtocol.Frames.cs:173-174`), the delta is sent from inside the per-viewer
  serve loop, and nothing in `TileWorld.Netcode` catches around it. The combat path already paid for this
  exact shape and its comment says the throw "took the tick down for every player on the server"
  (`TileWorldServer.Tick.cs:236-247`).

- [ ] **Step 2: Implement the delta message exactly as spec 7.5 writes it.**

~~~
[ContainerId: byte][PageIndex: byte][ChangedCount: byte]
[ per change: [Slot: varint uint16] then either
              [0x00] for "now empty"
              or [0x01] then the entry body of 4.4 without its Slot field ]
~~~

- [ ] **Step 3: MEASURE as you write and ABANDON rather than truncate.** The budget is
  `MaxGameMessageBytes` less the four byte envelope (`TileProtocol.Frames.cs:77`) less the delta's own
  three byte header, so 1,017 bytes of changes. When the next change would not fit, the builder abandons
  the delta and the caller sends the WHOLE PAGE through the fragmenter. **Not a second delta frame:** two
  deltas for one page would have to be applied in order by a client that may have missed the first, which
  is the reassembly problem the fragmenter already solves once.
- [ ] **Step 4: Implement the resync request, which is the ONE new client-to-server message and carries
  two bytes.** `[ContainerId: byte][PageIndex: byte]`. The server rate limits it at one page per client
  per tick, which bounds the worst case a malicious client can ask for at one page of fragments per tick,
  the same shape the snapshot already costs. That rate limit is a documented server rule rather than
  engine code here, so write it into the type's XML doc and into the package README.
- [ ] **Step 5: State the client rules the delta leans on, in the XML doc.** A client REFUSES a delta for a
  page it has not fully received and asks for a full page sync instead, so a delta can never be applied to
  bytes the client guessed at. On the last chunk of a fragmented page the assembled bytes go through the
  SAME decoder the server encoded with, and a failure quarantines rather than throwing.
- [ ] **Step 6: Assert the invariant of spec 7.6 as an architecture-shaped test.** No client-to-server
  message in this design carries an instance payload, and every one of them names an item by id. The full
  version of that test is spec 17 row 13 and belongs to a later phase with the craft messages, so here it
  covers only the resync request and the take request.
- [ ] **Step 7: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release
dotnet test KhaozEngine.TileWorld.Netcode.Tests/KhaozEngine.TileWorld.Netcode.Tests.csproj -c Release
git add KhaozEngine.ItemInstances/ContainerPageDelta.cs KhaozEngine.ItemInstances/ContainerPageSyncRequest.cs KhaozEngine.ItemInstances.Tests/Pages KhaozEngine.TileWorld.Netcode.Tests/PageSyncFrameBoundTests.cs
git commit -m "iteminstances(wire): a one-frame page delta that abandons to the fragmenter"
~~~

**Group D acceptance:** spec 17 rows 9, 11 and 17 green, and budgets 7 and 8 measured.

---

### Task 18: The `--items` benchmark structural test (medium, gate: milestone 1.1)

Spec 2.3 and 17 row 5. The `--items` mode ALREADY EXISTS in `KhaozEngine.Benchmarks/Items/` with a
checked-in baseline at `KhaozEngine.Benchmarks/Baselines/items-sqlite-v1-seed915.json`, and section 16's
measured column came from it. What is MISSING is the structural test beside it, which is the half that
actually runs in CI. Spec 17's note is blunt about why: a benchmark that only runs by hand is a benchmark
nobody runs.

**Files:**

- Create: `KhaozEngine.Server.Tests/Benchmarks/ItemsBenchmarkTests.cs`

**Interfaces:**

- Consumes: `ItemsBenchmarkConfig`, `ItemsBenchmarkRunner`, `ItemsBenchmarkResult`, the checked-in baseline
- Produces: the structural fence around the `--items` mode

- [ ] **Step 1: Mirror `MutationJournalBenchmarkTests` exactly** (`KhaozEngine.Server.Tests/WorldStore/
  Journal/MutationJournalBenchmarkTests.cs`, 605 lines, 16 facts). Same shape, same split: `Parse` accepts
  explicit options and REJECTS hard-limit violations, `--quick` runs end to end and produces a result, the
  result serialises to JSON and back, and `--output` writes a readable file.
- [ ] **Step 2: Pin the config's hard limits as refusals**, because that is what the precedent test spends
  most of its facts on: `MaximumPlayers`, `MaximumGenerations` and `MaximumCrafts` from
  `ItemsBenchmarkConfig`, plus a relative `--database` path.
- [ ] **Step 3: Run `--quick` inside the test and assert the STRUCTURE, never the numbers.** A budget's
  measured value belongs in the baseline JSON, which a human diffs. What the test asserts is that every
  budget the runner claims to measure is PRESENT in the result and that none is NaN, zero or absent. A
  test that asserts a microsecond figure is a test that goes red on a busy runner and teaches everyone to
  rerun it.
- [ ] **Step 4: Assert the one structural number that IS a design property.** Twenty crafts in one held
  action produce exactly ONE commit (budget 4). That is a count, not a timing, and it is the property the
  whole of spec 6 exists for, so a regression there must be red rather than slow.
- [ ] **Step 5: Do NOT touch the spike.** This task adds a test project file and nothing else. The spike
  keeps building and keeps its numbers.
- [ ] **Step 6: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~ItemsBenchmarkTests
git add KhaozEngine.Server.Tests/Benchmarks/ItemsBenchmarkTests.cs
git commit -m "bench(items): structural facts beside the --items mode"
~~~

---

