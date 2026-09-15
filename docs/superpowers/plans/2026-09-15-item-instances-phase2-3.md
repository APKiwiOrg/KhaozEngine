# Item Instances Phases 2 and 3 Implementation Plan (Scope B)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**This plan starts only after the phase 1 release has landed.** `docs/superpowers/plans/2026-09-15-item-instances-phase1.md`, cited below as the phase 1 plan, ships the instance record and the container, which is every durable byte format this plan builds on: the payload codec, the property registry, the `KECQ` wrapper, the instance id allocator, `ItemStack`'s third component, `ItemSlot`, and container codec version 2. Confirm that release is on `main` and packed to `local-feed` before starting task 2. Task 1 alone is independent of it.

**Goal:** Ship spec 20's phases 2 and 3 as one release: the paged container, the full instance validator, the remap pass, the journal package and the tick-bounded commit batch, then the wire, which is the fragmenter, the visibility function and its public view, the sibling ground component and the page delta.

**Architecture:** `KhaozEngine.ItemInstances` (Foundation) gains the paged container, validation, visibility, the remap pass and the page delta encoder. `KhaozEngine.ItemInstances.Journal` (Server) is new and owns everything that composes a `JournalCommit`, because `Foundation` cannot reference a `Server` package. `KhaozEngine.TileWorld.Netcode` gains an item-agnostic fragmenter and a sibling ground component holding opaque bytes, and gains NO items dependency. Nothing in the engine learns what an item means.

**Tech Stack:** .NET 10, `KhaozEngine.Items`, `KhaozEngine.ItemInstances`, `KhaozEngine.Primitives`, `KhaozEngine.Catalog` (Scope A), `KhaozEngine.WorldStore`, `KhaozEngine.TileWorld.Netcode`, `KhaozEngine.Replication`, xUnit.

**Spec:** `docs/design/ITEM-INSTANCES-DESIGN-2026-09-15.md` (cited below as spec N.N)

**Contracts:** `docs/design/CONTENT-CONTRACTS-DESIGN-2026-09-14.md` (cited as contracts N.N). Where the two disagree the contracts win.

**Scope A spec:** `docs/design/CONTENT-CATALOG-DESIGN-2026-09-15.md` (cited as catalog N.N)

## The two acceptances, which are spec 20's own

This plan carries two phases and therefore two acceptances, and they are the group gates below rather
than a single line at the end.

- **Spec 20 phase 2, pages and commits.** Sections 5 and 6: `PagedItemContainer`, `ItemContainerPage`,
  the section naming, the load path with remap, and `ContainerCommitBuilder` in
  `KhaozEngine.ItemInstances.Journal`. Gated on nothing new. **Acceptance: spec 17 rows 5, 8 and 14
  green, and budget 4 measured at ONE commit.**
- **Spec 20 phase 3, the wire.** Section 7: the sibling ground component, the spawn overload,
  `TileFragmentedMessage`, the page delta, the owner remainder message and `PublicView`. **Acceptance:
  spec 17 rows 9, 11, 15 and 17 green, and budgets 7 and 8 measured.**

Both must hold before the release at the end of this plan. Neither is a stopping point on its own,
because the two phases share one package set and one version bump.

## What this plan covers, and what it defers

- **The full `InstanceValidator` lands HERE**, not in phase 1, and task 3 is where. Spec 20 puts "the
  instance validator" in phase 1's list, but defines phase 1 as the whole of sections 3 and 4, and
  section 12 is neither. What phase 1 shipped is what the CODEC enforces, spec 12.2's structural checks
  1 to 5 as decoder refusals. What is left is everything that needs an `IContentSnapshot` and a PAGE:
  the drift checks, the policy checks, the per-page report, the three placeholder `StringId`s, the
  counter and the log line. A page is spec 5, so the validator arrives with the load path that calls it.
  Nothing durable moved with it: the quarantine wrapper, its reason ordinals and the entry flag bit all
  shipped in phase 1.

**Deferred exactly as spec 20 defers them, and out of scope here:**

- **The eighteen affix content types and the item generator** (spec 8 and 9, `ItemGenerator`,
  `GenerationContext`, `GenerationResult`, `ModCandidateTables`). Spec 20 phase 4, gated on Scope A's
  registry and publish path being real.
- **The crafting framework** (spec 10, `CraftPrimitive`, `CraftGuard`, `CraftPlan`, `CraftOutcome`,
  `CraftRefusal`, `CraftingRegistry`, `ICraftOperation`). Spec 20 phase 5.
- **The stat evaluator** (spec 11, `ContentStatEvaluator`, `StatModifierLine`, `StatCombineKind`,
  `StatSourceKey`, `StatContext`, `IStatConditionRegistry`). Spec 20 phase 5.
- **Consumer adoption** (spec 18 and 19). Runs per consumer AFTER this plan lands for the container
  half, and after spec 20 phase 5 for the stat half. Neither consumer waits on the other.

**Spec test plan rows this plan lands:** 5 (structural half), 8, 9, 11 (engine half), 14, 15 and 17.
Rows 1, 2, 3, 4, 12 and 16 landed with phase 1, and rows 6, 7, 10 and 13 belong to spec 20 phases 4
and 5.

## Scope A dependency, per task

Scope A ships `KhaozEngine.Catalog` in its own phase 1, built in five milestones (catalog 18.1).
**Only milestone 1.1 gates anything here**, and the phase 1 release already required it, so in practice
it has landed long before this plan starts. It ships the registry, the field schema, the codecs,
`ContentVarint`, the hashes, the four pack formats, `RemapRule` and `RemapRuleSet`, `FileSystemPackStore`,
`ContentPackReader`, plus `IContentSnapshot` and `ItemRow` (catalog 2.2).

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
| `IRandomSource` (`KhaozEngine.Primitives`, contracts 14.1) | not used by this plan, named so a later phase does not re-derive the seam |

**Task 1 has NO Scope A gate and no phase 1 gate either.** It lives in `KhaozEngine.TileWorld.Netcode`,
which references neither `KhaozEngine.Catalog` nor `KhaozEngine.Items`, so it can be implemented first
and in parallel with anything. Every task from 2 onward edits `KhaozEngine.ItemInstances` or the new
`KhaozEngine.ItemInstances.Journal`, so all of them are gated on milestone 1.1 and on the phase 1
release. Each task states its gate in its own header.

**There is no Grimhollow sequencing edge left.** The fleet-wide `ItemStack` break and the container
codec bump both landed in phase 1, which is the release that had to sit outside Scope A's step 7 to 11
window (spec 20, catalog 16.8). Nothing in this plan changes `ItemStack`'s shape or the stored container
format, so this release can land at any point in Scope A's adoption.

## Global Constraints

Binding on every task. Each restates a rule from the spec, the contracts or AGENTS.md, and a task that
breaks one of these is wrong even when its own tests pass.

- **Work in a fresh worktree** at `~/KhaozEngine/.claude/worktrees/item-instances-phase2-3`
  branched from the latest `origin/main`, which must already carry the phase 1 release. Engine program
  [#884](https://github.com/APKiwiOrg/KhaozEngine/issues/884) owns this work.
- **The payload is a canonical TLV and the encoder is the thing that makes it canonical.** Fields
  strictly ascending by kind, no kind twice, every varint minimal (contracts 9.3). The decoder CHECKS
  all three and refuses. Phase 1 shipped that decoder: do not write a second one.
- **Varints are unsigned minimal LEB128 and nothing in this plan is zig-zagged.** Content ids, instance
  ids, kind ids, lengths, counts and roll positions are all declared unsigned (contracts 15). Use
  `ContentVarint` from `KhaozEngine.Catalog`. Do NOT write a second varint implementation anywhere.
- **Little endian through `BinaryPrimitives` with the endianness in the method name, on BOTH sides.**
  `BitConverter` is forbidden (contracts 15).
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

| # | Task | Size | Gate |
|---|---|---|---|
| 1 | `TileFragmentedMessage`, the item-agnostic fragmenter and reassembler | medium | none |
| 2 | `ItemContainerPage`, `PagedItemContainer`, the capacity gate and the merge rule | large | phase 1 release |
| 3 | `InstanceValidator`, its thirteen checks, the counter and the log line | large | phase 1 release |
| 4 | The registry-derived remap pass and its idempotence | large | phase 1 release |
| 5 | `KhaozEngine.ItemInstances.Journal`, section names and the container load path | medium | phase 1 release |
| 6 | `ContainerCommitBuilder`, the tick-bounded batch and its crash facts | large | phase 1 release |
| 7 | The `--items` benchmark structural test in `KhaozEngine.Server.Tests` | medium | phase 1 release |
| 8 | `ItemInstanceVisibility`: `CanSee`, `PublicView` and the owner remainder | medium | phase 1 release |
| 9 | `TileGroundItemInstance` and the `SpawnGroundItem` overload | medium | phase 1 release |
| 10 | The page delta, its one-frame bound and the resync request | large | phase 1 release, tasks 1 and 2 |
| 11 | Documentation sweep: package READMEs, README catalog, USING, DEPENDENCY-SEAMS | medium | phase 1 release |
| 12 | The finishing ritual: one version bump, changelog, pack, no tag | medium | phase 1 release |

Groups and their acceptances:

- **Group A, task 1.** The one change that needs nothing from Scope A and nothing from phase 1. It is
  first because it is unblocked, and because task 10 abandons to it. Acceptance: the reassembler facts of
  spec 17 row 15 green, and `dotnet test -c Release` green across the solution.
- **Group B, tasks 2 to 7.** The pages, the validation and the commit, spec 5, 6 and 12. Acceptance:
  spec 17 rows 5, 8 and 14 green, and budget 4 measured at ONE commit. **This is spec 20 phase 2's
  acceptance.**
- **Group C, tasks 8 to 10.** The wire, spec 7. Acceptance: spec 17 rows 9, 11 and 17 green, and budgets
  7 and 8 measured. With row 15 from task 1, **that is spec 20 phase 3's acceptance.**
- **Group D, tasks 11 and 12.** Documentation and release. Acceptance: `scripts/check-doc-versions.sh`,
  `scripts/check-dashes.sh --tree`, `scripts/check-prose.sh --tree` and `scripts/check-file-size.sh --tree`
  all exit 0, the full solution green in Release, and `local-feed` packed with NO tag.

---

## Group A: what needs nothing from Scope A

### Task 1: `TileFragmentedMessage`, the item-agnostic fragmenter and reassembler (medium, no Scope A gate)

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

**Group A acceptance:** the reassembler facts of spec 17 row 15 green, and `dotnet test -c Release` green
across the solution.

---

## Group B: the pages, the validation and the commit (spec 5, 6 and 12)

Every task from here on is gated on the **phase 1 release** being on `main`, because each one builds on
the payload codec, the property registry, the quarantine wrapper and container codec version 2 that
release shipped. They are also gated on **Scope A milestone 1.1**, which phase 1 already required.

The validator sits in this group rather than with the record it validates, for the reason the header
gives: spec 12.2's remaining checks need an `IContentSnapshot` and a PAGE, and the page is spec 5.

### Task 2: `ItemContainerPage`, `PagedItemContainer` and the merge rule (large, gate: phase 1 release)

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

- Consumes: `ItemSlot`, the phase 1 plan's `ItemContainerPageCodec`, `ItemRow` from `KhaozEngine.Catalog` for the definition's
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
- [ ] **Step 7: Expose the dirty set**, because task 6's commit builder asks the container for it and
  folds those pages into whatever commit comes next (spec 5.6). That is what makes the lazy rewrite cost
  nothing: it never causes a commit, it only joins one.
- [ ] **Step 8: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release
git add KhaozEngine.ItemInstances/ItemContainerPage.cs KhaozEngine.ItemInstances/PagedItemContainer.cs KhaozEngine.ItemInstances/PagedItemContainer.Capacity.cs KhaozEngine.ItemInstances/InstanceStacking.cs KhaozEngine.ItemInstances.Tests/Pages
git commit -m "iteminstances(pages): paged containers, the capacity gate and byte-equal stacking"
~~~

---

### Task 3: `InstanceValidator`, its thirteen checks, the counter and the log line (large, gate: phase 1 release)

Spec 12.2, 12.3, 12.6 and 12.7 over contracts 10.1, 10.2 and 10.4. Write fresh: the spike has no
validator. `KhaozEngine.Content/JsonSchemaValidator.cs:11-101` is the run-to-the-end sweep shape to copy.

**Files:**

- Create: `KhaozEngine.ItemInstances/InstanceValidator.cs`
- Create: `KhaozEngine.ItemInstances/InstanceValidator.References.cs`
- Create: `KhaozEngine.ItemInstances/InstanceValidationReport.cs`
- Create: `KhaozEngine.ItemInstances/InstanceValidationStrings.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Validation/InstanceValidatorTests.cs`

**Interfaces:**

- Consumes: `IContentSnapshot` (`TryGetRow`, `IsRetired`), the phase 1 plan's property registry and
  its payload field walk
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
  same recursive order the remap pass of task 4 walks, over the same nested payloads. Spec 12.2 says why:
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

### Task 4: The registry-derived remap pass and its idempotence (large, gate: phase 1 release)

Spec 5.5 step 2 over contracts 8.1 through 8.6. **LIFT FROM THE SPIKE:**
`KhaozEngine.Benchmarks/Items/RemapRuleSet.cs` (269 lines) has the whole shape: the applicable-rule
prefilter keyed on `(typeId, fromId)`, the scan that never writes when nothing matched, the innermost-first
re-encode, and the affix re-sort. Two things change on the way in. Its `RemapRule` is REPLACED by Scope A's
`RemapRule` and `RemapRuleSet` from `KhaozEngine.Catalog`, and its hard-coded `ScanPayload` switch is
REPLACED by a walk of the registry's reference targets, for the same reason the phase 1 plan's payload codec task gives.

**Files:**

- Create: `KhaozEngine.ItemInstances/InstanceRemapPass.cs`
- Create: `KhaozEngine.ItemInstances/InstanceRemapPass.Rewrite.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Remap/InstanceRemapPassTests.cs`

**Interfaces:**

- Consumes: `RemapRule`, `RemapRuleKind`, `RemapRuleSet` from `KhaozEngine.Catalog`, the phase 1 plan's
  property registry and its `ItemContainerPageCodec`
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

### Task 5: `KhaozEngine.ItemInstances.Journal` and the container load path (medium, gate: phase 1 release)

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
- [ ] **Step 6: Write the package README and its README catalog row IN THIS TASK**, self-contained,
  naming the layering reason the package exists. Same guard reason as the phase 1 plan's package
  skeleton task: a packable package with no catalog row reddens `scripts/check-doc-versions.sh` from
  here until task 11.
- [ ] **Step 7: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~ItemInstances
git add KhaozEngine.ItemInstances.Journal KhaozEngine.Server.Tests/ItemInstances KhaozEngine.slnx KhaozEngine.Server/KhaozEngine.Server.csproj KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj KhaozEngine.Tests/ArchitectureTests.cs
git commit -m "iteminstances(journal): page section names and the container load path"
~~~

---

### Task 6: `ContainerCommitBuilder`, the tick-bounded batch and its crash facts (large, gate: phase 1 release)

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
  `JournalOperationIdentity`, `JournalLimits`, task 2's dirty set
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
- [ ] **Step 7: Measure budget 4 here rather than in task 7.** Twenty crafts in one held action is at most
  20 KB and ONE commit, summed over `JournalCommit.OwnedByteCount`. Assert the commit COUNT in the test
  and leave the byte number to the benchmark, so a structural regression is a red test rather than a
  slower number nobody reads.
- [ ] **Step 8: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~ContainerCommitBuilder
git add KhaozEngine.ItemInstances.Journal KhaozEngine.Server.Tests/ItemInstances
git commit -m "iteminstances(journal): one commit per tick from a batch of page operations"
~~~

---

### Task 7: The `--items` benchmark structural test (medium, gate: phase 1 release)

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

**Group B acceptance, which is spec 20 phase 2's own:** spec 17 rows 5, 8 and 14 green, and budget 4
measured at ONE commit.

---

## Group C: the wire (spec 7)

### Task 8: `ItemInstanceVisibility`, the ONE `CanSee` and `PublicView` (medium, gate: phase 1 release)

Spec 7.4 and 12.5 over contracts 11.1 and 11.2. There is exactly ONE function answering "may this viewer
see this field", and both the replication filter and the tooltip builder call it. A tooltip that computed
its own answer is how a client eventually renders something the server never sent.

**Files:**

- Create: `KhaozEngine.ItemInstances/ItemInstanceVisibility.cs`
- Modify: `KhaozEngine.ItemInstances/ItemInstancePayload.cs` (finish the phase 1 plan's `PublicView` stub)
- Create: `KhaozEngine.ItemInstances.Tests/Visibility/ItemInstanceVisibilityTests.cs`

**Interfaces:**

- Consumes: the phase 1 plan's property registry (visibility and `identificationMaskBit` per kind) and
  its payload field walk
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

### Task 9: `TileGroundItemInstance` and the `SpawnGroundItem` overload (medium, gate: phase 1 release)

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

### Task 10: The page delta, its one-frame bound and the resync request (large, gate: phase 1 release, tasks 1 and 2)

Spec 7.5 and 7.6. **LIFT FROM THE SPIKE:** `KhaozEngine.Benchmarks/Items/PageWire.cs` has the delta
builder that MEASURES as it writes and answers -1 when the next change would not fit, which is the whole
mechanism.

**Where the delta builder lives, because the spec does not say and the layering forces it.** Spec 2.2 says
`KhaozEngine.TileWorld.Netcode` gains NO items dependency, and the delta body is "the entry body of 4.4
without its Slot field", which only the page codec can write. **The plan's choice:** the delta ENCODER and
its budget arithmetic live in `KhaozEngine.ItemInstances` as `ContainerPageDelta`, and
`KhaozEngine.TileWorld.Netcode` carries only the item-agnostic fragmenter from task 1. The SERVER that
owns both composes them. Flag this to the spec owner in the task report.

**Files:**

- Create: `KhaozEngine.ItemInstances/ContainerPageDelta.cs`
- Create: `KhaozEngine.ItemInstances/ContainerPageSyncRequest.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Pages/ContainerPageDeltaTests.cs`
- Create: `KhaozEngine.TileWorld.Netcode.Tests/PageSyncFrameBoundTests.cs`

**Interfaces:**

- Consumes: the phase 1 plan's page entry body writer, task 1's fragmenter,
  `TileProtocol.MaxGameMessageBytes`
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

**Group C acceptance, which is spec 20 phase 3's own:** spec 17 rows 9, 11 and 17 green here, row 15
green from task 1, and budgets 7 and 8 measured.

---

## Group D: documentation and release

### Task 11: The full documentation sweep (medium, gate: phase 1 release)

AGENTS.md's "Full doc sweep on EVERY feature" rule. `scripts/check-doc-versions.sh` verifies the
engine-version declarations, the newest changelog heading and the package INVENTORY. What it does NOT
check is whether any of that prose is CORRECT, so a stale catalog row or a package README describing
removed API sails through. This task is the content accuracy half, and it is its own task because a
package arrives in this plan and a missed row is a guard failure at release time.

**Files:**

- Modify: `README.md` (package table, umbrella table, repo-layout block)
- Modify: `KhaozEngine.TileWorld.Netcode/README.md`
- Modify: `KhaozEngine.Foundation/README.md`, `KhaozEngine.Server/README.md`
- Modify: `KhaozEngine.ItemInstances/README.md`, `KhaozEngine.ItemInstances.Journal/README.md`
- Modify: `docs/USING-KHAOZENGINE.md`
- Modify: `docs/DEPENDENCY-SEAMS.md`

- [ ] **Step 1: Add the `KhaozEngine.ItemInstances.Journal` catalog row to `README.md`**, in the same
  voice and depth as the `KhaozEngine.ItemInstances` row beside it, plus the `Depends on` column. Add it
  to the repo-layout block and to the `Server` umbrella table row. The minimal row landed with the
  package in task 5 step 6, so nothing here is what unblocks the inventory check. The README table is
  the SINGLE source for the catalog, so do not re-enumerate any of it in `AGENTS.md`.
- [ ] **Step 2: Update the MODIFIED packages' own READMEs**, which ship inside their nupkgs and are
  read standalone on NuGet, so each rots independently of the master catalog. `KhaozEngine.ItemInstances`
  gains the paged container, the capacity gate, validation, visibility, the remap pass and the page
  delta. `KhaozEngine.TileWorld.Netcode` gains the sibling ground component, the spawn overload and the
  fragmenter. Both umbrella READMEs gain their new member.
- [ ] **Step 3: Extend the `docs/USING-KHAOZENGINE.md` section phase 1 opened.** Follow the file's
  existing shape: take the worked example that encodes an item and seats it in a container on through a
  paged container, a commit built by `ContainerCommitBuilder`, and a page sync. State explicitly what is
  NOT here yet and where it lands: the generator, crafting and the stat evaluator are spec 20 phases 4
  and 5. Correct anything the phase 1 section says about a limit this plan lifted.
- [ ] **Step 4: Add the seams to `docs/DEPENDENCY-SEAMS.md`.** Two edges changed and one deliberately
  did not: `ItemInstances.Journal` sits above `ItemInstances` and `WorldStore` in `Server`, and
  `TileWorld.Netcode` gained a component and a fragmenter and NO items dependency. That last one is the
  interesting entry, because it is a seam that was deliberately not crossed. The phase 1 entries for
  `ItemInstances` and `IInstanceIdStore` stay as they are.
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
git add README.md docs/USING-KHAOZENGINE.md docs/DEPENDENCY-SEAMS.md KhaozEngine.TileWorld.Netcode/README.md KhaozEngine.Foundation/README.md KhaozEngine.Server/README.md KhaozEngine.ItemInstances/README.md KhaozEngine.ItemInstances.Journal/README.md
git commit -m "docs(items): the journal catalog row, the package READMEs and the usage section"
~~~

  All four guards must exit 0 here. If `check-doc-versions.sh` is red, read the message: a complaint
  about the version line means task 12 has not run yet and is expected, and anything else is a real miss
  in this task.

---

### Task 12: The finishing ritual, one version bump, no tag (medium, gate: phase 1 release)

AGENTS.md's finishing ritual, in order. **ONE version bump for the whole batch**, not one per task and
not one per phase, which is the rule that stops the engine version creeping through a run of one-line
releases. Phases 2 and 3 share a package set and a wire, so they share a bump.

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
  ONE fresh version and take the next free MINOR, because this work is additive. Do not assume the
  version the phase 1 release cut is still the newest: read both rather than reasoning from the previous
  plan. A collision here is auto-resolved and needs no asking.
- [ ] **Step 3: Write the changelog entry in the SAME commit as the version bump.** Newest first, detailed,
  with a tight one-line summary as the entry's FIRST sentence so the file doubles as the history view.
  The first sentence is: `Paged item containers, one commit per tick, and the item wire.` Then the
  detail, which is the public API and behaviour change: the new `KhaozEngine.ItemInstances.Journal`
  package, `PagedItemContainer` and the capacity gate, `ItemContainerPage` and the section naming, the
  full `InstanceValidator` with its three outcomes and one counter, the remap pass, the container load
  path, `ContainerCommitBuilder` and its batch window, the visibility function with `PublicView` and the
  owner remainder, the sibling ground component and the spawn overload, the fragmenter, the page delta
  and the resync request. Name the deferred half in one sentence so a reader does not go looking for a
  generator that is not there.
- [ ] **Step 4: Update every engine-version declaration the guard checks.** EVERY `<PackageReference>`
  example line in `README.md` and in `docs/USING-KHAOZENGINE.md`, one per umbrella, not just one.

- [ ] **Step 5: Close what this lands and file what it leaves.** Close engine program
  [#884](https://github.com/APKiwiOrg/KhaozEngine/issues/884)'s phase 2 and phase 3 items that are
  actually resolved, or reference them with `Closes #NNN` in the commit. Anything this work knowingly
  leaves undone, defers or works around becomes a GitHub issue AT THIS POINT, with a `confidence/*`
  label, per AGENTS.md. The four open questions spec 20 leaves live in the spec and do not need issues.
  The owner-remainder home of task 8 and the delta-builder home of task 10 DO, because both are choices
  this plan made that the spec should adopt or overrule.
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
git commit -m "items(${engine_version}): paged containers, one commit per tick and the item wire"
~~~

- [ ] **Step 9: Reconcile once more, fast-forward main, verify and push main right away.**

~~~bash
git fetch --prune
git merge origin/main
dotnet test KhaozEngine.slnx -c Release
git -C ~/KhaozEngine merge --ff-only feature/item-instances-phase2-3
git -C ~/KhaozEngine push origin main
~~~

  If main advanced after the branch merge, merge it into the feature branch and repeat verification before
  the fast-forward. Do not hold the push and do not ask.

- [ ] **Step 10: STOP. Do NOT tag.** A `vX.Y.Z` tag is a separate, deliberate act that the user starts.
  The one sanctioned exception in AGENTS.md is a game pinned-and-waiting on this change. Check whether one
  now is: consumer adoption (spec 18 and 19) runs after THIS release for the container half, so a game may
  have been waiting on the wire. If a game really is blocked, say so in the report and let the user start
  the release. Otherwise the default ending applies: merge, push `main`, pack to `local-feed`, stop.
- [ ] **Step 11: Ask about releasing only if `git worktree list` shows you are the last chat standing**,
  once, as the last line of the report.

**Group D acceptance:** all four guards exit 0, the full solution is green in Release, `local-feed` holds
the new version, `main` is pushed, and no tag exists.

---

## Where this plan CHOSE, because the spec left it open

Each of these is a decision the spec does not make and an implementer would otherwise make silently and
differently. They are consolidated here so a reviewer can overrule one in a single read, and each is
restated at the task that acts on it. The phase 1 plan carries its own table, and the two do not overlap.

| # | The gap | The plan's choice | Task |
|---|---|---|---|
| 1 | Spec 7.6 lists an owner remainder message and no package builds it | `ItemInstanceVisibility.OwnerRemainder`, beside `PublicView` so the two cannot disagree, with the message KIND left to the game | 8 |
| 2 | The page delta body is a page entry body, and `TileWorld.Netcode` must gain no items dependency | `ContainerPageDelta` lives in `KhaozEngine.ItemInstances` and the server composes it with the fragmenter | 10 |
| 3 | `MaxInstancePayloadBytes` is one number in `ItemSlot.MaxPayloadBytes` (phase 1), and `KhaozEngine.TileWorld.Netcode` needs it and may reference neither package | A local const mirroring it, with a cross-package equality fact in `KhaozEngine.Server.Tests`, which sees both | 9 |

Choices 1 and 2 become GitHub issues in task 12 step 5, because each is something the spec should adopt
or overrule rather than inherit from a plan. Choice 3 is recorded here and in its task.
