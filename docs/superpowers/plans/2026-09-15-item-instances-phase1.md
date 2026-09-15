# Item Instances Phase 1 Implementation Plan (Scope B)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Ship the instance record and the container, which is spec 20's phase 1 exactly: the canonical tagged payload, the property registry, quarantine, the instance validator, the instance id allocator, `ItemStack`'s third component, `ItemSlot`, and container codec version 2 with its version 1 reader, as one new package plus changes to one existing one.

**Acceptance, which is spec 20 phase 1's own:** spec 17 rows 1, 2, 3, 4, 12 and 16 green, and contracts 9.8's forty-five byte example reproduced BYTE FOR BYTE. Nothing else counts as done, and no group here is finished until its own acceptance below also holds.

**Architecture:** `KhaozEngine.ItemInstances` (Foundation) owns the payload codec, the property registry, the `KECQ` wrapper, the instance validator, the instance id allocator and the page codec. `KhaozEngine.Items` gains a third `ItemStack` component, `ItemSlot`, the payload doors and codec version 2 with its version 1 reader. Nothing in the engine learns what an item means.

**Tech Stack:** .NET 10, `KhaozEngine.Items`, `KhaozEngine.Primitives`, `KhaozEngine.Catalog` (Scope A), xUnit. `KhaozEngine.Replication` is read for the id packing SCHEME only and is never referenced (task 7).

**Spec:** `docs/design/ITEM-INSTANCES-DESIGN-2026-09-15.md` (cited below as spec N.N)

**Contracts:** `docs/design/CONTENT-CONTRACTS-DESIGN-2026-09-14.md` (cited as contracts N.N). Where the two disagree the contracts win.

**Scope A spec:** `docs/design/CONTENT-CATALOG-DESIGN-2026-09-15.md` (cited as catalog N.N)

**The next plan:** `docs/superpowers/plans/2026-09-15-item-instances-phase2-3.md` carries spec 20's phases 2 and 3, and starts only after the release this plan ends with has landed.

## What this plan calls phase 1, and what it defers

Spec 20 divides Scope B into five phases. **This plan implements phase 1 AT THE SPEC'S OWN BOUNDARY**,
which is "the instance record and the container, the whole of sections 3 and 4". It is deliberately
narrower than the engine half: paging, the journal, the commit batch and the wire are one further
release, and they are the sibling plan's, so the durable byte formats settle and ship before anything
starts building on them.

**Deferred to `docs/superpowers/plans/2026-09-15-item-instances-phase2-3.md`, which starts after this
release lands:**

- **Paging and the journal** (spec 5 and 6, `PagedItemContainer`, `ItemContainerPage`, the section
  naming, the load path with remap, `ContainerCommitBuilder`). Spec 20 phase 2.
- **The wire** (spec 7, the fragmenter, the sibling ground component, the spawn overload, the page
  delta, the owner remainder and `PublicView`). Spec 20 phase 3.
- **The registry-derived remap pass** (spec 5.5 step 2, `InstanceRemapPass`), which the spec places with
  the pages rather than with the validator. See the second paragraph below: the validator ships HERE and
  the pass that produces its `Remapped` outcome does not.

**Deferred exactly as spec 20 defers them, and out of scope for both plans:**

- **The eighteen affix content types and the item generator** (spec 8 and 9, `ItemGenerator`,
  `GenerationContext`, `GenerationResult`, `ModCandidateTables`). Spec 20 phase 4, gated on Scope A's
  registry and publish path being real.
- **The crafting framework** (spec 10, `CraftPrimitive`, `CraftGuard`, `CraftPlan`, `CraftOutcome`,
  `CraftRefusal`, `CraftingRegistry`, `ICraftOperation`). Spec 20 phase 5.
- **The stat evaluator** (spec 11, `ContentStatEvaluator`, `StatModifierLine`, `StatCombineKind`,
  `StatSourceKey`, `StatContext`, `IStatConditionRegistry`). Spec 20 phase 5.
- **Consumer adoption** (spec 18 and 19). Runs per consumer after the wire lands, not inside either plan.

**The validator ships HERE, whole, because spec 20 phase 1's list names it.** All thirteen checks of spec
12.2, the per-container `InstanceValidationReport`, the three placeholder `StringId`s, the counter and the
log line are task 9. It needs an `IContentSnapshot` and a decoded container and it has both: milestone 1.1
ships the snapshot (2.5) and task 8 ships the version 2 codec, whose decode hands back exactly the header
and entry list the validator sweeps. Checks 1 to 5 also arrive EARLIER, as decoder refusals in task 4, and
that is not a duplicate: the codec refuses a malformed payload at the door and the validator runs to the
end accumulating findings, which is contracts 10.4's shape. Task 9 CALLS the codec for those five rather
than writing a second copy of them.

**What phase 1 does NOT ship is the REMAP PASS, which the spec places with the pages (5.5 step 2), and the
one visible consequence is an outcome.** `InstanceValidationOutcome` carries the contracts 10.1 vocabulary
complete from the start, because it is the contract's and not this plan's. `Remapped` is the DORMANT
member: nothing in phase 1 rewrites an id, so no phase 1 path produces it, and the phase 2-3 plan's remap
task is what lights it up. Task 9 says so in the XML doc on the member and pins it with a fact, so a reader
does not go hunting for the path that sets it. Nothing durable waits on that: the quarantine wrapper, its
reason ordinals and the entry flag bit all ship here too.

Phase 1 is a STRONG BASE rather than a partial catalog (spec 1.2, spec 20). What it settles is every
byte format, every id space, every ordering rule and the stacking test, which are the expensive things
(spec 21). What it does not ship is breadth, which is content the owner authors afterwards.

**Spec test plan rows this plan lands:** 1, 2, 3, 4, 12 and 16. Rows 5, 8, 9, 11, 14, 15 and 17 are the
phase 2-3 plan's, and rows 6, 7, 10 and 13 belong to spec 20 phases 4 and 5. **The validator has no row of
its own**, because spec 17 names none: what it cites are rows 1 and 16, the golden whose quarantined entry
exceeds `MaxInstancePayloadBytes` and the wrap-store-load-unwrap byte equality, both of which tasks 4 and 6
already land. Task 9's own fence is one fact per check, thirteen of them.

## Scope A dependency, per task

Scope A ships `KhaozEngine.Catalog` in its own phase 1, built in five milestones (catalog 18.1).
**Only milestone 1.1 gates anything here.** It ships the registry, the field schema, the codecs,
`ContentVarint`, the hashes, the four pack formats, `RemapRule` and `RemapRuleSet`, `FileSystemPackStore`,
`ContentPackReader`, plus `IContentSnapshot` and `ItemRow` (catalog 2.2). Scope A ships it ahead of
everything that consumes it for exactly this reason.

What this plan consumes from it, by name, all from catalog 2.2 and 2.5:

| Name | Where this plan reads it |
|---|---|
| `ContentVarint` | every varint this plan writes, contracts 15 |
| `ContentTypeId`, `ContentKey` | the type keys an `InstanceReferenceTarget` names, task 3 |
| `ContentVersionIdentity` and its `int Number` | the page stamp the container codec writes, task 8, spec 5.3 |
| `IContentSnapshot`, `ContentRow`, `ItemRow` | the validator's drift and policy checks, task 9. The container load path reads them too and it is the phase 2-3 plan's |
| `IContentSnapshot.IsRetired(type, id)` | validator check 13 and the `Retired` finding it raises, task 9, spec 12.2 |
| `RemapRule`, `RemapRuleKind`, `RemapRuleSet` | NOT read by phase 1. The remap pass is the phase 2-3 plan's, spec 5.5 step 2 |
| `ContentStringCatalog` | the three placeholder `StringId`s of spec 12.3, task 9 |
| `IRandomSource` (`KhaozEngine.Primitives`, contracts 14.1) | not used by either plan, named so a later phase does not re-derive the seam |

**Tasks 1 and 2 have NO Scope A gate at all.** They live in `KhaozEngine.Items`, which does not
reference `KhaozEngine.Catalog`, so implementation starts on them the day this plan is approved. Every
task from 3 onward creates or edits `KhaozEngine.ItemInstances`, whose csproj references
`KhaozEngine.Catalog` (contracts 3.2), so all of them are gated on milestone 1.1 having landed. Each
task states its gate in its own header.

**The one ordering edge between the two programs** is spec 20's: task 1 takes `ItemStack` to three
components and task 8 takes `ItemContainerCodec` to version 2, which is a fleet-wide compile break plus
a durable codec bump, and it must land BEFORE step 7 or AFTER step 11 of Scope A's Grimhollow adoption
(catalog 16.8, spec 20). Never inside that window. Confirm with the Scope A implementer which side of
the window you are on before starting task 1, and record the answer in the task 1 commit message.

## Global Constraints

Binding on every task. Each restates a rule from the spec, the contracts or AGENTS.md, and a task that
breaks one of these is wrong even when its own tests pass.

- **Work in a fresh worktree** at `~/KhaozEngine/.claude/worktrees/item-instances-phase1`
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
  method argument, including `IInstanceIdStore` and, in later phases, `IContentSnapshot` and
  `IRandomSource` (contracts 14.4). No test in this plan writes process-global state, so no test needs a
  `DisableParallelization` collection. If one later does, it enlists in a named collection with the
  shared state in its doc comment, per the #349 rule in AGENTS.md.
- **Nothing in this plan changes `KhaozEngine.WorldStore`**, either provider schema, or the store
  conformance suite (spec 2.4). Phase 1 writes no commit at all: the journal half is the phase 2-3 plan's.
- **One responsibility per file, every file under 800 lines.** When the KESIZE ratchet fires, put the new
  code in a new type. Never split a file at an arbitrary line, and never hand-edit `.filesize-baseline`.
- **Warnings are errors** in every configuration. Fix at the source, never with `NoWarn` or a pragma.
- **CI tests Release and a local `dotnet test` runs Debug.** Any test asserting on `Debug.Assert`,
  `[Conditional("DEBUG")]` or `#if DEBUG` behaviour must be run once with `-c Release` before merging.
- **A new test project references ONLY what its tests use**, carries `<IsPackable>false</IsPackable>`
  and pins `<RootNamespace>KhaozEngine.Tests</RootNamespace>` (spec 2.3, AGENTS.md). Push CI selects
  test projects by the reference graph, so an over-broad reference silently degrades selection.
- **Every player-facing string is a `StringId`.** The three placeholder keys are engine owned and fixed:
  `khaoz.item.quarantined`, `khaoz.item.retired`, `khaoz.item.unidentified` (spec 12.3). They ship with
  the validator in task 9, the engine ships the keys and no translation, and nothing anywhere invents a
  literal in their place.
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
| 3 | The `KhaozEngine.ItemInstances` package skeleton and the property registry | medium | milestone 1.1 |
| 4 | `ItemInstancePayload`: the canonical TLV codec, sockets, goldens | large | milestone 1.1 |
| 5 | Decoder fuzzing and the unknown-kind round trip | medium | milestone 1.1 |
| 6 | `QuarantineWrapper`, the `KECQ` format | small | milestone 1.1 |
| 7 | `InstanceIdAllocator`, the store epoch and the rotation guard | medium | milestone 1.1 |
| 8 | Container codec version 2 and the version 1 reader | large | milestone 1.1 |
| 9 | `InstanceValidator`: thirteen checks, the counter and the log line | large | milestone 1.1 |
| 10 | Documentation sweep: the package README, the README catalog, USING, DEPENDENCY-SEAMS | medium | milestone 1.1 |
| 11 | The finishing ritual: one version bump, changelog, pack, no tag | medium | milestone 1.1 |

Groups and their acceptances:

- **Group A, tasks 1 and 2.** The changes that need nothing from Scope A. Acceptance: `dotnet test -c Release`
  green across the solution with `ItemStack` at three components.
- **Group B, tasks 3 to 7.** The instance record, spec 3. Acceptance: spec 17 rows 1, 2, 3, 12 and 16
  green, and contracts 9.8's 45 bytes reproduced byte for byte.
- **Group C, tasks 8 and 9.** The container and the validator that sweeps it, spec 4 and 12. Acceptance:
  spec 17 row 4 green, a version 1 blob decoding through the version 2 reader unchanged, an OSRS slot entry
  at seven bytes against version 1's fixed ten, and the validator's thirteen checks green with the `Retired`
  finding and the two unresolved-reference quarantines among them. Groups B and C together are spec 20
  phase 1's acceptance in full.
- **Group D, tasks 10 and 11.** Documentation and release. Acceptance: `scripts/check-doc-versions.sh`,
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
    `value.Quarantined` is set, and `QuarantineWrapper.Verify` stands in for them from task 6. Until task
    6 lands, the quarantined path accepts the bytes unchecked and carries a `TODO` naming task 6. The
    canonical check runs on EVERY call and never under a `Debug.Assert`, because `[Conditional("DEBUG")]`
    members do not exist in the Release configuration CI builds.
  - `TakeSlotAt(int slot)` returns the whole `ItemSlot` and seats `ItemSlot.Empty`.
  - `Swap(a, b)` swaps the payload and flag entries alongside the stacks, so the one-line tuple swap
    becomes three.
- [ ] **Step 5: Declare the cap where `KhaozEngine.Items` can see it.** `MaxInstancePayloadBytes = 512`
  is contracts 9.6's number and `KhaozEngine.Items` cannot reference `KhaozEngine.ItemInstances` (the
  dependency runs the other way). Declare it as `public const int ItemSlot.MaxPayloadBytes = 512` in
  `KhaozEngine.Items` and have `KhaozEngine.ItemInstances` expose
  `ItemInstancePayload.MaxInstancePayloadBytes => ItemSlot.MaxPayloadBytes` in task 4, so there is ONE
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

**Group A acceptance:** `dotnet test -c Release` green across the solution with `ItemStack` at three
components, and every consumer break from task 2 filed rather than carried in someone's head.

---

## Group B: the instance record (spec 3)

Every task from here on is gated on **Scope A milestone 1.1**, because each one creates or edits
`KhaozEngine.ItemInstances`, whose csproj references `KhaozEngine.Catalog` (contracts 3.2).

### Task 3: The `KhaozEngine.ItemInstances` package and the property registry (medium, gate: milestone 1.1)

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
  example and task 4's golden reproduces it, so none of them moves.

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

- [ ] **Step 8: Write the package README and its README catalog row IN THIS TASK.** The README ships
  INSIDE the nupkg and is read standalone on NuGet, so it is self-contained: what the package is, the
  payload format in one block, the kind ranges, the registration rule and the three placeholder
  `StringId`s. Do not point it at a design doc. `scripts/check-doc-versions.sh` requires every packable
  package to have BOTH its own README and a row in the root `README.md` catalog, so a minimal row lands
  here and task 10 brings it up to the depth of the rows beside it. Without this step the guard is red
  from this task until task 10.
- [ ] **Step 9: Run the focused tests green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter FullyQualifiedName~ArchitectureTests
git add KhaozEngine.ItemInstances KhaozEngine.ItemInstances.Tests KhaozEngine.slnx KhaozEngine.Foundation/KhaozEngine.Foundation.csproj KhaozEngine.Tests/ArchitectureTests.cs
git commit -m "iteminstances(registry): property kinds, bands and field shapes"
~~~

---

### Task 4: `ItemInstancePayload`, the canonical TLV codec and its goldens (large, gate: milestone 1.1)

Spec 3.2, 3.4, 3.5, 3.7 and 3.8, over contracts 9.1 through 9.9. This is the format everything else in
the plan rests on, and spec 21 records most of it as expensive to change once data exists.

**The STRUCTURAL checks arrive here as decoder refusals, and task 9's sweep reuses them.** Spec 12.2's
checks 1 to 5 are what the CODEC itself enforces: canonical ascending order, no duplicate kind, minimal
varints, every declared length inside the payload, the one-level socket rule and the cap. `Validate` in
step 8 is that surface, it answers a reason from the closed set and it never throws. Task 9's
`InstanceValidator` CALLS it for those five rather than writing a second copy, and adds the eight that need
an `IContentSnapshot`: the drift checks, the policy checks, the per-container report, the counter and the
log line. Two entry points, one implementation of each check.

**LIFT FROM THE SPIKE, do not rewrite.** `KhaozEngine.Benchmarks/Items/InstancePayload.cs` (327 lines) is
a clean, measured implementation of this exact format: the same field walk, the same closed reason set,
the same per-kind shape checks, the same one-level socket refusal, the same retained-run public view. Lift
its structure and its logic. Three things change on the way in, and nothing else should:

1. Its `Varint` (`KhaozEngine.Benchmarks/Items/Varint.cs`) is REPLACED by `ContentVarint` from
   `KhaozEngine.Catalog`. Contracts 15 wants one varint implementation in the tree and the spike's copy
   exists only because Scope A had not shipped.
2. Its `CheckShape` switch is REPLACED by a walk of the registry's `InstanceFieldShape` from task 3. The
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

- Consumes: `ContentVarint`, task 3's registry, `ItemSlot.MaxPayloadBytes`
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

  `PublicView` is implemented here as a stub returning the whole payload and is FINISHED by the
  visibility task of `docs/superpowers/plans/2026-09-15-item-instances-phase2-3.md`, which is where the
  visibility rule belongs and which spec 20 puts in phase 3. `TryDecode` NEVER throws.

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

### Task 5: Decoder fuzzing and the unknown-kind round trip (medium, gate: milestone 1.1)

Spec 17 rows 2 and 3. Both are fences around task 4 rather than new behaviour, which is why they are their
own task: a fuzzer folded into the codec commit is a fuzzer nobody tunes.

**Files:**

- Create: `KhaozEngine.ItemInstances.Tests/Payload/PayloadFuzzTests.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Payload/PayloadMutator.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Payload/UnknownKindTests.cs`

**Interfaces:**

- Consumes: task 4's codec and its goldens, `SeededRandomSource` from `KhaozEngine.Primitives`
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

### Task 6: `QuarantineWrapper`, the `KECQ` format (small, gate: milestone 1.1)

Spec 12.4 over contracts 10.2 and 15. The bytes of a failed item are kept VERBATIM, and this is the
durable envelope that keeps them.

**Files:**

- Create: `KhaozEngine.ItemInstances/QuarantineWrapper.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Quarantine/QuarantineWrapperTests.cs`
- Modify: `KhaozEngine.Items/ItemContainer.Slots.cs` (resolve task 1 step 4's `TODO`)

**Interfaces:**

- Consumes: `ContentVarint`, task 4's reason set
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
[ReasonCode: byte]                 // the ordinal spec 12.4's reason table assigns that reason
[StampedVersion: varint int32]     // the page stamp the record failed under
[OriginalLength: varint int32]
[Original: OriginalLength bytes]   // verbatim, never re-encoded
~~~

  **A magic here and none on a payload, and the two are consistent.** Contracts 15 forbids a magic on a
  format always embedded in a larger versioned record, and a payload is such a format. A wrapper is not:
  it must be distinguishable from a payload at a glance in a hex dump of a page, and by a tool that never
  saw the entry flag. Four bytes for that, once per quarantined entry, on a path that is rare by definition.

  **The `ReasonCode` byte values come from spec 12.4's reason ordinal table, and this task does not
  choose them.** That table is the single source for which byte a reason is written as, and the byte is
  DURABLE: a wrapper stored under one ordinal is read back under the same one forever, so a task that
  numbered the reasons itself would pin a second meaning to the same byte the first time the two lists
  diverged. Read the ordinals off 12.4 at implementation time. The validator that raises most of those
  reasons is task 9, three tasks later, which is why the ordinals live in the spec rather than in the type
  that first writes them.

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

### Task 7: `InstanceIdAllocator`, the store epoch and the rotation guard (medium, gate: milestone 1.1)

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
byte for byte.

---

## Group C: the container and its validator (spec 4 and 12)

### Task 8: Container codec version 2 and the version 1 reader (large, gate: milestone 1.1)

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

- Consumes: `ContentVarint`, task 4's payload codec, task 6's wrapper, `ItemSlot`
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

### Task 9: `InstanceValidator`, its thirteen checks, the counter and the log line (large, gate: milestone 1.1)

Spec 12.2, 12.3, 12.6 and 12.7 over contracts 10.1, 10.2 and 10.4. Spec 20 phase 1's list names the
instance validator, so it ships here, whole. Write it fresh: the spike has no validator, and
`KhaozEngine.Content/JsonSchemaValidator.cs:11-101` is the run-to-the-end sweep shape to copy. It sweeps
what task 8 decodes, which is a version 2 container's header plus its entries, and it takes a standalone
payload for a caller holding one item without a container.

**Files:**

- Create: `KhaozEngine.ItemInstances/InstanceValidator.cs`
- Create: `KhaozEngine.ItemInstances/InstanceValidator.References.cs`
- Create: `KhaozEngine.ItemInstances/InstanceValidationReport.cs`
- Create: `KhaozEngine.ItemInstances/InstanceValidationStrings.cs`
- Create: `KhaozEngine.ItemInstances/InstanceValidationTelemetry.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Validation/InstanceValidatorTests.cs`
- Modify: `KhaozEngine.ItemInstances/KhaozEngine.ItemInstances.csproj` (step 7's one added reference)

**Interfaces:**

- Consumes: `IContentSnapshot` (`TryGetRow`, `IsRetired`), `ContentStringCatalog`, task 3's property
  registry and its reference targets, task 4's payload field walk and its `Validate`, task 8's decoded
  `PageHeader` and `PageEntry`, and `ILogger` from `KhaozEngine.Diagnostics` for the telemetry helper only
- Produces: `InstanceValidator.Validate` and `InstanceValidator.ValidateEntry`,
  `InstanceValidationReport`, `InstanceValidationFinding`, `InstanceValidationOutcome`,
  `InstanceValidationStrings`, `InstanceValidationTelemetry`

- [ ] **Step 1: Write one fact per check, thirteen of them**, naming the reason token spec 12.2 assigns.
  Checks 1 to 5, 9, 10 and 11 are STRUCTURAL and quarantine. Checks 6, 7 and 8 are DRIFT and also
  quarantine. Check 12 is POLICY and is TOLERATED. Check 13 is POLICY and answers `Retired`.
  The two that a reader will get wrong without a test are 12 and 13, so write those first:

~~~csharp
[Fact] public void Check_12_an_over_cap_count_is_counted_and_changes_nothing()
[Fact] public void Check_13_a_RETIRED_definition_is_not_a_quarantine_and_the_container_still_loads()
[Fact] public void A_structural_failure_quarantines_that_ENTRY_and_leaves_the_rest_alone()
[Fact] public void An_unresolved_content_reference_quarantines_because_no_rule_covered_it()
[Fact] public void The_validator_accumulates_and_never_stops_at_the_first_finding()
[Fact] public void The_validator_never_throws_never_logs_never_counts_and_never_mutates_its_input()
~~~

- [ ] **Step 2: Give it TWO entry points over ONE sweep.** Phase 1 has a decoded container and not yet an
  `ItemContainerPage`, which is spec 5 and the phase 2-3 plan's, so the surface is written over what task 8
  already hands back:

~~~csharp
public static InstanceValidationReport Validate(
    in PageHeader header, IReadOnlyList<PageEntry> entries, IContentSnapshot snapshot);

public static InstanceValidationOutcome ValidateEntry(
    in PageEntry entry, int stampedVersion, IContentSnapshot snapshot,
    out InstanceValidationFinding finding);
~~~

  The first is the whole stored container, so no new type sits between the codec and the validator. The
  second is the standalone payload door, and the first is a loop over it plus the two answers that are
  CONTAINER-wide: check 10's instance id uniqueness, and the report itself. When `ItemContainerPage`
  arrives in the phase 2-3 plan it wraps the same header plus entries and calls the same method, so nothing
  here changes shape when paging lands, and neither does the caller the load path becomes.
- [ ] **Step 3: Ship the outcome vocabulary COMPLETE and say which member is dormant.** Contracts 10.1
  fixes three outcomes and there is no fourth, so `InstanceValidationOutcome` carries `Valid`, `Remapped`
  and `Quarantined` from the start: it is the contract's vocabulary rather than this plan's, a report that
  cannot express one of them would have to be widened later, and a caller switching over the enum should
  compile once against the final set. **No phase 1 path produces `Remapped`**, because the remap pass is
  spec 5.5 step 2 and the phase 2-3 plan ships it. Put that sentence in the XML doc on the member and add a
  fact that no phase 1 input reaches it, so the dormancy is pinned rather than assumed.
- [ ] **Step 4: Model check 13's `Retired` as a FINDING and not as a fourth outcome**, which is the one
  place spec 12.2's table and spec 12.3's prose have to be read together. The table's outcome column says
  `retired`, and 12.3 says what that is NOT: not a quarantine, not a fourth contract outcome, not a counter
  and not a new log category. The bytes are not wrapped, the entry still decodes, the container still
  loads, and the record's contracts 10.1 outcome stays `Valid` or `Remapped` as its ids say. What `Retired`
  changes is the PRESENTATION (`khaoz.item.retired`) and the refusals that ride with it, carried in the
  finding the caller already reads. Contracts 8.2's kind 2 policy `0x01` is the rule it implements, and a
  retired row stays in the pack forever (contracts 5.1), so check 6 RESOLVES it and the item would
  otherwise be fully usable. That is the gap check 13 exists to close.

- [ ] **Step 5: DERIVE checks 6 and 7 from the registry's `InstanceReferenceTarget` descriptors**, in the
  same recursive order the phase 2-3 plan's remap pass will walk, over the same nested payloads. Spec 12.2
  says why: an earlier draft wrote check 7 as a closed enumeration and it already omitted kind 7's material
  ids and a socket's `ContainedDefinitionId`, and it would have omitted every game kind at or above 1,024
  forever. A kind cannot be remapped-but-not-validated or validated-but-not-remapped. Check 8 stays hand
  written, because a tier ordinal is not a content id: it is a key INTO the row check 7 already resolved.
- [ ] **Step 6: Keep it PURE.** No store reads, no ambient state, no logging, no counter, no throw for a
  content reason. A throw from it is a bug in the validator. The caller logs, counts and quarantines, and
  step 7 is what the caller calls.
- [ ] **Step 7: Name the counter and the log line, and invent no others.** Counter
  `khaoz.content.quarantined_records`, dimensioned by content type id and reason code. ONE log line per
  CONTAINER under category `ContentValidation` at Warning, naming the reason code, the stamped version, the
  active version and the owning stream key, and NEVER the payload bytes or a raw account id. One per
  container rather than per entry, because one that fails wholesale would otherwise emit a hundred
  identical lines, which is how an operator learns to filter the category out. The COUNTER is still
  incremented per record, because a counter is what a dashboard reads and a log line is what a human reads.
  **The validator stays pure, so the emission is a named member beside it rather than a rule written in a
  doc:** `InstanceValidationTelemetry.Report(report, logger, counter)` takes a finished report and does
  both side effects in one place. Phase 1 ships no load path to be that caller, and the phase 2-3 plan's
  one calls this instead of writing a second emitter, which is the whole reason it is a member and not a
  paragraph. `ILogger` arrives by argument from `KhaozEngine.Diagnostics` (`LogManager.GetLogger` is the
  injected path, never the ambient `Log` facade, #616), which is the one project reference this task adds
  to a Foundation package that already sits above it. **The engine has NO counter seam**, verified by grep:
  nothing in the tree defines one, so the counter arrives as a caller-supplied delegate and this task does
  not invent a package-wide metrics abstraction on the way past. Add a fact asserting a wholesale failure
  emits exactly one line and a hundred counter increments.

- [ ] **Step 8: Ship the three placeholder `StringId`s and no translation.** `khaoz.item.quarantined`,
  `khaoz.item.retired`, `khaoz.item.unidentified` (spec 12.3). All three are player facing, so none is a
  literal, which is AGENTS.md's founding rule. They are prefixed `khaoz.` deliberately: they are ENGINE
  strings rather than content rows, so contracts 12.1's derived `<type key>.<content key>.<field>` grammar
  does not name them and the prefix keeps them out of its space. They resolve through
  `ContentStringCatalog` on the `SafeFormat` path, so a translator's malformed template falls back to the
  unformatted template rather than throwing inside the frame loop. A reason code and a stamped version are
  NOT player text and are never formatted into these strings. Task 3 step 8's package README already names
  the three keys, so keep the two in step.
- [ ] **Step 9: Implement kind 128's identification mechanic on the registered bit.** `RevealedMask` bit N
  is the bit a kind was REGISTERED with, never its position in the ascending list of gated kinds (spec 12.7
  and spec 21's last row). Add a fact that registering a NEW gated engine kind at, say, 9 does NOT move
  bits 0 to 3, which is the exact hazard the registered constant exists to prevent. What ships here is the
  mask's semantics over task 3's registry. The function that READS it for a viewer,
  `ItemInstanceVisibility.CanSee`, is spec 12.5 and lands with the wire in the phase 2-3 plan.
- [ ] **Step 10: Add the unidentified stacking fact and its accepted leak.** An unidentified item still
  STACKS by byte equality, and two unidentified items with different hidden affixes have different bytes,
  so they do not merge. That leaks one bit: a player who tries to stack two unidentified items learns
  whether they are identical. The leak is inherent to stacking by bytes and spec 15.8 records it as
  accepted. Pin the behaviour so nobody "fixes" it later without reading that section.
- [ ] **Step 11: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release
git add KhaozEngine.ItemInstances/InstanceValidator.cs KhaozEngine.ItemInstances/InstanceValidator.References.cs KhaozEngine.ItemInstances/InstanceValidationReport.cs KhaozEngine.ItemInstances/InstanceValidationStrings.cs KhaozEngine.ItemInstances/InstanceValidationTelemetry.cs KhaozEngine.ItemInstances/KhaozEngine.ItemInstances.csproj KhaozEngine.ItemInstances.Tests/Validation
git commit -m "iteminstances(validation): thirteen checks, three outcomes, one counter"
~~~

**Group C acceptance:** spec 17 row 4 green, and the validator's thirteen checks green with the `Retired`
finding and the two unresolved-reference quarantines among them. Together with group B that is spec 20
phase 1's acceptance in full: rows 1, 2, 3, 4, 12 and 16 green, and the forty-five byte example reproduced
byte for byte.

---

## Group D: documentation and release

### Task 10: The documentation sweep for what phase 1 ships (medium, gate: milestone 1.1)

AGENTS.md's "Full doc sweep on EVERY feature" rule. `scripts/check-doc-versions.sh` verifies the
engine-version declarations, the newest changelog heading and the package INVENTORY. What it does NOT
check is whether any of that prose is CORRECT, so a stale catalog row or a package README describing
removed API sails through. This task is the content accuracy half, and it is its own task because a
package arrives in this plan and a missed row is a guard failure at release time.

**Files:**

- Modify: `README.md` (package table, umbrella table, repo-layout block)
- Modify: `KhaozEngine.Items/README.md`
- Modify: `KhaozEngine.Foundation/README.md`
- Modify: `KhaozEngine.ItemInstances/README.md`
- Modify: `docs/USING-KHAOZENGINE.md`
- Modify: `docs/DEPENDENCY-SEAMS.md`

- [ ] **Step 1: Bring the `KhaozEngine.ItemInstances` catalog row in `README.md` up to depth**, in the
  same voice as the `KhaozEngine.Items` row beside it, plus the `Depends on` column. Add it to the
  repo-layout block and to the `Foundation` umbrella table row. The minimal row landed with the package
  in task 3 step 8, so nothing here is what unblocks the inventory check. The README table is the SINGLE
  source for the catalog, so do not re-enumerate any of it in `AGENTS.md`.
- [ ] **Step 2: Update the MODIFIED package's own README**, which ships inside its nupkg and is read
  standalone on NuGet, so it rots independently of the master catalog. `KhaozEngine.Items` gains the
  third `ItemStack` component, `ItemSlot`, the payload doors and codec version 2 reading version 1. The
  `Foundation` umbrella README gains its new member. Finish `KhaozEngine.ItemInstances/README.md` to the
  same depth: the payload format in one block, the kind ranges, the registration rule, the `KECQ`
  wrapper, the validator's thirteen checks with its three placeholder `StringId`s and its counter, and
  the allocator's `IInstanceIdStore` seam.
- [ ] **Step 3: Add a `docs/USING-KHAOZENGINE.md` section for the new public API.** One section,
  following the file's existing shape: a worked example that builds a payload, seats it in a container
  slot through `SetSlotAt`, saves and reloads the container through codec version 2, sweeps what it
  decoded through `InstanceValidator.Validate` against a snapshot, and reads an instance id off an
  `ItemStack`. State explicitly what is NOT here yet and where it lands: paging, the remap pass, the
  journal, the commit batch and the wire are
  `docs/superpowers/plans/2026-09-15-item-instances-phase2-3.md`, and the generator, crafting and the
  stat evaluator are spec 20 phases 4 and 5.
- [ ] **Step 4: Add the seams to `docs/DEPENDENCY-SEAMS.md`.** Three edges changed: `ItemInstances` sits
  above `Items` and `Catalog` in `Foundation`, it gained a `Diagnostics` edge for the validator's log line
  (task 9 step 7, injected `ILogger`, never the ambient facade), and `Items` gained an optional
  canonical-payload predicate on its constructor rather than a dependency on the package that owns the
  decoder. That last one is the interesting entry, because it is a seam that was deliberately not crossed.
  `IInstanceIdStore` is a new seam with no engine-shipped provider and belongs in the table, and so does
  the validator's counter delegate, which exists because the engine has no counter seam to plug into.
- [ ] **Step 5: Do the mechanical check before committing.** Grep every new type, package and constant name
  across ALL `*.md` recursively (root, `docs/`, `docs/design/`, and every per-package `<Package>/README.md`)
  plus `AGENTS.md`, and confirm every place that should mention it does.
- [ ] **Step 6: Do NOT edit any file under `docs/design/`.** The two specs and the contracts are the record
  of the reasoning and this plan does not restate them. Shipped API and usage go to the changelog, USING
  and the package READMEs as they land, which is the rule that keeps `docs/design/` a why and not a
  reference surface.
- [ ] **Step 7: Run the guards and commit.**

~~~bash
scripts/check-doc-versions.sh
scripts/check-dashes.sh --tree
scripts/check-prose.sh --tree
scripts/check-file-size.sh --tree
git add README.md docs/USING-KHAOZENGINE.md docs/DEPENDENCY-SEAMS.md KhaozEngine.Items/README.md KhaozEngine.Foundation/README.md KhaozEngine.ItemInstances/README.md
git commit -m "docs(items): the catalog row, the package READMEs and the usage section"
~~~

  All four guards must exit 0 here. If `check-doc-versions.sh` is red, read the message: a complaint
  about the version line means task 11 has not run yet and is expected, and anything else is a real miss
  in this task.

---

### Task 11: The finishing ritual, one version bump, no tag (medium, gate: milestone 1.1)

AGENTS.md's finishing ritual, in order. **ONE version bump for the whole batch**, not one per task, which
is the rule that stops the engine version creeping through a run of one-line releases. The phase 2-3 plan
cuts its own single bump when it finishes, and starts only after this release has landed.

**Files:**

- Modify: `Directory.Build.props`
- Modify: `CHANGELOG.md`
- Modify: every `README.md` `<PackageReference>` example line the guard checks, one per umbrella
- Modify: `docs/USING-KHAOZENGINE.md` version-bearing example lines

- [ ] **Step 1: Fetch and merge current main into the feature branch BEFORE reading the version.**

~~~bash
git fetch --prune
git merge origin/main
~~~

  Resolve every conflict in the feature branch and rerun the affected suites. The shared
  `<KhaozEngineVersion>` line collides constantly here and local `main` is routinely ahead of `origin`.

- [ ] **Step 2: Read the version and the tags on the up-to-date main, then take the next FREE version.**

~~~bash
git tag --list 'v*' --sort=-v:refname | head
grep -n '<KhaozEngineVersion>' Directory.Build.props
~~~

  If `<KhaozEngineVersion>` is AHEAD of the newest tag, a version is in flight: RIDE it. Append to that
  staged version's changelog entry, roll its date, and do NOT bump. If nothing is in flight, cut exactly
  ONE fresh version and take the next free MINOR, because this work is additive. At the time of writing
  the version is 18.49.0 and the newest tag is v18.49.0, so nothing is in flight and the next free minor
  is 18.50.0. **Re-read both rather than trusting that sentence**: a concurrent chat may have bumped and
  tagged since. A collision here is auto-resolved and needs no asking.
- [ ] **Step 3: Write the changelog entry in the SAME commit as the version bump.** Newest first, detailed,
  with a tight one-line summary as the entry's FIRST sentence so the file doubles as the history view.
  The first sentence is: `The item instance record and the container arrive: a canonical tagged payload,
  node-prefixed instance ids, quarantine, the instance validator, and container codec version 2 reading
  version 1.` Then the detail, which is the public API and behaviour change: the new
  `KhaozEngine.ItemInstances` package, `ItemStack`'s third component and the deconstruction break it
  causes, `ItemSlot` and the payload doors, codec version 2 and its version 1 reader, the property
  registry and its band rule, the closed reason set, `KECQ`, `InstanceValidator` with its thirteen checks,
  its three placeholder `StringId`s and its `khaoz.content.quarantined_records` counter, and the allocator
  with its epoch refusal. Name the deferred half in one sentence so a reader does not go looking for a
  paged container that is not there: paging, the remap pass that makes `Remapped` reachable, the journal,
  the commit batch and the wire are the next release, and generation, crafting and the evaluator are after
  that.
- [ ] **Step 4: Update every engine-version declaration the guard checks.** EVERY `<PackageReference>`
  example line in `README.md` and in `docs/USING-KHAOZENGINE.md`, one per umbrella, not just one.

- [ ] **Step 5: Close what this lands and file what it leaves.** Close engine program
  [#884](https://github.com/APKiwiOrg/KhaozEngine/issues/884)'s phase 1 items that are actually resolved,
  or reference them with `Closes #NNN` in the commit. Anything this work knowingly leaves undone, defers
  or works around becomes a GitHub issue AT THIS POINT, with a `confidence/*` label, per AGENTS.md. The
  four open questions spec 20 leaves live in the spec and do not need issues. The page-level reason tokens
  of task 8 step 8 DO, because they are a choice this plan made that the spec should adopt or overrule.
  The phase 2-3 plan files its own two, so do not pre-file them here.
- [ ] **Step 6: Run every guard and the full Release verification.**

~~~bash
scripts/check-doc-versions.sh
scripts/check-dashes.sh --tree
scripts/check-prose.sh --tree
scripts/check-file-size.sh --tree
dotnet test KhaozEngine.slnx -c Release
~~~

  Every command exits zero. If `check-file-size.sh` fires, the fix is a new type and never a hand edit of
  `.filesize-baseline` and never a split at an arbitrary line. If the growth is genuinely legitimate,
  STOP AND ASK the user.

- [ ] **Step 7: Check the feed, then pack it through the guarded script.**

~~~bash
scripts/check-local-feed.sh
scripts/pack-local-feed.sh
~~~

  Never a bare `dotnet pack` into `local-feed`: the agent-side hook denies it and the guard exists because
  packing a RELEASED version quietly puts a bigger build behind a tag that does not describe it. The pack
  is silent through a normal release because the version is still staged at pack time.

- [ ] **Step 8: Commit the version batch with the new version as the scope.**

~~~bash
engine_version=$(sed -n 's:.*<KhaozEngineVersion>\([^<]*\)</KhaozEngineVersion>.*:\1:p' Directory.Build.props)
git add Directory.Build.props CHANGELOG.md README.md docs/USING-KHAOZENGINE.md
git commit -m "items(${engine_version}): the instance record, the payload codec and codec version 2"
~~~

- [ ] **Step 9: Reconcile once more, fast-forward main, verify and push main right away.**

~~~bash
git fetch --prune
git merge origin/main
dotnet test KhaozEngine.slnx -c Release
git -C ~/KhaozEngine merge --ff-only feature/item-instances-phase1
git -C ~/KhaozEngine push origin main
~~~

  If main advanced after the branch merge, merge it into the feature branch and repeat verification before
  the fast-forward. Do not hold the push and do not ask.

- [ ] **Step 10: STOP. Do NOT tag.** A `vX.Y.Z` tag is a separate, deliberate act that the user starts.
  The one sanctioned exception in AGENTS.md is a game pinned-and-waiting on this change, and no consumer is
  pinned on Scope B phase 1: Grimhollow's adoption is spec 18 and runs after the wire lands, and the other
  four consumers do not use `KhaozEngine.Items` at all. So the default ending applies: merge, push `main`,
  pack to `local-feed`, stop. If the situation has changed and a game really is blocked, say so in the
  report and let the user start the release.
- [ ] **Step 11: Hand the next plan over.** Say in the report that
  `docs/superpowers/plans/2026-09-15-item-instances-phase2-3.md` is now unblocked, and which engine
  version it builds on.
- [ ] **Step 12: Ask about releasing only if `git worktree list` shows you are the last chat standing**,
  once, as the last line of the report.

**Group D acceptance:** all four guards exit 0, the full solution is green in Release, `local-feed` holds
the new version, `main` is pushed, and no tag exists.

---

## Where this plan CHOSE, because the spec left it open

Each of these is a decision the spec does not make and an implementer would otherwise make silently and
differently. They are consolidated here so a reviewer can overrule one in a single read, and each is
restated at the task that acts on it.

| # | The gap | The plan's choice | Task |
|---|---|---|---|
| 1 | Spec 4.7 wants `SetSlotAt` to refuse a non-canonical payload, but the decoder lives in a package that DEPENDS on `KhaozEngine.Items`, and contracts 15 forbids a second varint reader | The container takes the check as an optional constructor predicate, the same shape the stacking rule already arrives in, and refuses every non-empty payload when it is absent | 1 |
| 2 | `MaxInstancePayloadBytes` is declared in `KhaozEngine.ItemInstances` (spec 2.2) but `KhaozEngine.Items` needs it and cannot reference that package | ONE number in `ItemSlot.MaxPayloadBytes`, which `ItemInstancePayload` re-exposes rather than redeclares. The phase 2-3 plan mirrors it once more in `TileWorld.Netcode`, with a cross-package equality fact | 1 |
| 3 | Contracts 9.7's eight reason tokens are PAYLOAD reasons, and nothing names the PAGE-level ones | A second closed set, `ItemContainerPageReason`, using the spike's names so the benchmark and the package agree | 8 |
| 4 | Spec 3.6 says reuse `NetIdAllocator`, which lives in a `Server` package the `Foundation` allocator cannot reference | `InstanceIdAllocator` mirrors the scheme with its own constants and a cross-package equality fact in `KhaozEngine.Server.Tests` | 7 |
| 5 | The allocator's durable state has no named home | An `IInstanceIdStore` constructor seam, engine ships no provider | 7 |
| 6 | Spec 12.2's validator is PURE and the caller logs and counts, but phase 1 ships no load path to be that caller, and the engine has no counter seam at all (verified by grep) | `InstanceValidationTelemetry.Report`, one named member beside the validator taking an injected `ILogger` and a counter delegate, which the phase 2-3 plan's load path calls rather than writing a second emitter | 9 |

Choice 3 becomes a GitHub issue in task 11 step 5, because it is something the spec should adopt or
overrule rather than inherit from a plan. Choices 1, 2, 4, 5 and 6 are recorded here and in their tasks.
