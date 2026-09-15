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

