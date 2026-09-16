# Item Instances Phases 4 and 5 Implementation Plan (Scope B)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**This plan starts only after the phase 2 and 3 release has landed AND Scope A's publish path is on
`main`.** Phases 1 to 3 shipped in 18.51.0: the payload codec, the property registry, the `KECQ` wrapper,
the instance validator, the allocator, container codec version 2, the paged container, the remap pass, the
load path, `ContainerCommitBuilder`, the visibility function, the fragmenter, the sibling ground component
and the page delta. Nothing in this plan re-implements any of them. The Scope A half is NOT on `main` at the
time of writing and is the real gate, which is `OWNER DECISION 1` below.

**Goal:** Ship spec 20's phases 4 and 5 as one release: the eighteen affix content types with their
validators in the `KEC0100` band, the precomputed candidate tables and `ItemGenerator`, then the crafting
framework with its guards, its registry and its journal operation, and the integer `ContentStatEvaluator`.

**Architecture:** `KhaozEngine.ItemInstances` (Foundation) gains three new folders and no new package
reference: `Content/` holds the eighteen content types, `Generation/` holds the tables and the generator,
`Crafting/` holds the primitives, the guards, the plan and the registry, and `Stats/` holds the evaluator
and the line builder. `KhaozEngine.ItemInstances.Journal` (Server) gains the two event bodies its
`ItemInstanceEvents` constants already name and a reader for both. `KhaozEngine.Catalog` gains ONE thing,
the pass 6 wiring that lets an Instances-band registration emit its own `KEC01xx` findings, and that edit
is `OWNER DECISION 6`. Nothing in the engine learns what an item means, and no mod, currency, rarity or
tier is named in engine code anywhere.

**Tech Stack:** .NET 10, `KhaozEngine.ItemInstances`, `KhaozEngine.ItemInstances.Journal`,
`KhaozEngine.Items`, `KhaozEngine.Primitives`, `KhaozEngine.Catalog` (Scope A), `KhaozEngine.Stats` (read
only, unchanged), `KhaozEngine.WorldStore`, xUnit.

**Spec:** `docs/design/ITEM-INSTANCES-DESIGN-2026-09-15.md` (cited below as spec N.N)

**Contracts:** `docs/design/CONTENT-CONTRACTS-DESIGN-2026-09-14.md` (cited as contracts N.N). Where the two
disagree the contracts win.

**Scope A spec:** `docs/design/CONTENT-CATALOG-DESIGN-2026-09-15.md` (cited as catalog N.N), and its plan is
`docs/superpowers/plans/2026-09-15-content-catalog-phase1.md` (cited as the catalog plan).

## The two acceptances, which are spec 20's own

This plan carries two phases and therefore two acceptances, and they are the group gates below rather than a
single line at the end.

- **Spec 20 phase 4, content and generation.** Sections 8 and 9: the eighteen content types, their
  validators, the candidate tables and `ItemGenerator`. Gated on Scope A's registry AND publish path being
  real. **Acceptance: spec 17 row 6 green, budgets 5 and 9 measured against the SHIPPED types, and one
  authored pack of real mods rolling items end to end.**
- **Spec 20 phase 5, crafting and stats.** Sections 10 and 11: the fourteen primitives, the guard
  vocabulary, the currency row, the operation registry, the craft journal operation, and
  `ContentStatEvaluator`. **Acceptance: spec 17 rows 7, 10 and 13 green, budget 6 measured, and one authored
  currency composing at least four primitives with guards.**

Both must hold before the release at the end of this plan. Neither is a stopping point on its own, because
the two phases share one package set and one version bump.

## OWNER DECISIONS, surfaced before dispatch

Seven. Each is a design call rather than an implementation one, each is marked again at the task that acts
on it, and each has the answer this plan builds to if the owner says nothing. An implementer who reaches one
of these and finds no answer takes the default and says so in the task report.

| # | The decision | This plan's default if unanswered | Task |
|---|---|---|---|
| 1 | **The Scope A gate.** Phase 4 needs the registry AND the publish path. The registry is on `main`. The publish path, the runtime, the four derived indexes, `IContentLoadIndex`'s wiring and `ContentStringCatalog` are all in flight on `feature/content-catalog-phase1-b`, `-c` and `-d` and are NOT merged. Does this plan wait for the catalog phase 1 release, or branch from the catalog work and land after it? | WAIT. Start this plan only when the catalog milestones 1.2 and 1.3 are on `main`, because task 4 registers an `IContentLoadIndex` and task 6 publishes a real pack. | all |
| 2 | **#942, the craft intent shape.** Spec 10.6 writes the normalized intent with `[TargetContainerId: varint uint16]`, and the shipped `ContainerOperation.WriteCanonical` writes container NAMES because nothing in the tree defines a container id registry. Two intent shapes for one action kind is a conflicting-replay bug waiting to happen. | ADOPT THE SHIPPED SHAPE and correct spec 10.6, because the name is what `ContainerSectionNames.Format` files pages under and a numbering invented here is durable data every consumer then owns. | 10 |
| 3 | **The frozen legacy affix entry** (spec 10.3, contracts 5.4). The conservative reading freezes every byte of an affix entry naming a legacy mod, which means a `RerollValues(ByIndex)` on that entry refuses outright. Spec 10.3 flags the relaxation for gate 1 and prices it. | FROZEN, exactly as spec 10.3 and contracts 5.4 write it. Relaxing it later changes no stored byte, and tightening it later would strand items. | 8 |
| 4 | **The tier ordinal's packed width.** Spec 9.2 writes the candidate table's packed key as `(mod id << 4) \| tier ordinal`, which holds 15 tiers. Spec 8.3 says the ordinal is 1 to 255 and kind 131's tier slot is a byte. One of the two numbers is wrong, and the choice is an authoring ceiling. | KEEP `<< 4` AND MAKE THE CEILING A PUBLISH REFUSAL: a mod carrying a tier ordinal above 15 is `KEC0102`, so the ceiling is loud at publish rather than silent in a table. Widening to `<< 8` costs a mod id ceiling of 2^23 and is the alternative. | 4 |
| 5 | **The `--items` baseline moves.** Budgets 5, 6 and 9 were measured against the spike under `KhaozEngine.Benchmarks/Items/`. Phase 4's acceptance says the budgets are measured, and a number measured against a spike does not describe the shipped generator. Re-pointing the mode re-baselines `Baselines/items-sqlite-v1-seed915.json`. | RE-POINT the generation, table and stat phases at the shipped types, keep the seed and the synthetic row set, and re-baseline in ONE commit whose message names the old and new numbers. | 6, 11 |
| 6 | **Where the `KEC0100` band's checks run.** `ContentValidator.RunInstanceBand` is a shipped hook whose comment says "Scope B ships its own checks here", which is impossible as written: `KhaozEngine.Catalog` cannot reference `KhaozEngine.ItemInstances` without closing a cycle. | RUN THE REGISTRATION'S OWN `IContentValidator` IN PASS 6 when its band is `Instances`, TRUSTED (a throw propagates rather than becoming `KEC0040`), with its own findings' codes passed through unchanged. That is an additive edit to one Scope A file plus its doc comment. | 3 |
| 7 | **Open question 8, an unregistered craft operation.** Refuse at use with a counter, or fail the boot. Spec 23 recommends refuse at use and spec 10.5 builds to it. | REFUSE AT USE, with the counter, because the alternative takes a server down for one unusable currency row. Recorded here only so the implementer does not re-open it. | 9 |

## What this plan covers, and what it defers

- **The eighteen content types are SCAFFOLDING and this plan authors no content.** Every row in spec 8 is a
  SHAPE. There is no mod named in engine code, no currency named in engine code, no rarity named in engine
  code and no tier count fixed in engine code. Spec 8.1's own words are the test this plan is held to, and
  task 6's end-to-end pack is the OWNER's rows through the shipped publish path, not a fixture the engine
  ships.
- **Nothing here re-implements a phase 1 to 3 surface.** The payload builder, the page codec, the remap
  pass, the validator, the visibility function and the commit builder are all shipped and are CALLED. In
  particular the generator writes through `ItemInstancePayloadBuilder` and never a second encoder, and the
  craft working copy is a builder over a decoded payload rather than a byte patcher.
- **`KhaozEngine.Stats` is not modified.** `StatModifier(int Channel, float Flat, float Percent)` is a
  shipped struct and adding a `More` kind to it is a breaking change to a released type (spec 11.1,
  contracts 13.3). `ContentStatEvaluator` is a SIBLING and a game uses one or the other per stat.

**Deferred, and out of scope here:**

- **Consumer adoption (spec 18 and 19).** Runs per consumer AFTER this plan lands for the stat half, and it
  already could have run for the container half since 18.51.0. Neither consumer waits on the other, and
  nothing in this plan edits a game repo.
- **A reader for the five container-operation event bodies** ([#941](https://github.com/APKiwiOrg/KhaozEngine/issues/941)).
  Task 10 ships the reader for the TWO bodies this plan writes, `item-generated` and `item-crafted`, because
  a before-and-after audit that cannot be read back is the failure spec 10.6 cites. The other five stay
  open on #941.
- **Per-kind value width enforcement** ([#917](https://github.com/APKiwiOrg/KhaozEngine/issues/917)), which
  is gated on [#903](https://github.com/APKiwiOrg/KhaozEngine/issues/903)'s `Varint64` question. Task 5
  refuses an out-of-range input at the GENERATOR's door, which is a caller bug and a throw, and leaves the
  codec-level narrowing where it is.
- **The stack cap on the merge path** ([#924](https://github.com/APKiwiOrg/KhaozEngine/issues/924)). No
  craft merges two stacks. A craft rewrites one payload in place and DECREMENTS a currency stack, and a
  decrement can only shrink, which is the one direction contracts 8.2 kind 4 always permits. Task 7 states
  that in a doc comment and #924 stays open for the `Add` path.
- **A sorted-entry declaration on a registration**
  ([#930](https://github.com/APKiwiOrg/KhaozEngine/issues/930)). Every affix list this plan writes goes
  through the SHIPPED `InstancePropertyCodec.AffixList`, which is exactly the codec the remap pass keys its
  re-sort on, so nothing here needs the declaration and nothing here makes #930 worse.
- **A live content apply.** A boot builds the tables and they are immutable for the life of the process
  (spec 9.2). A new version becomes active at server RESTART, contracts 1.3 item 8. Do not build a swap.

**Spec test plan rows this plan lands:** 6, 7, 10 and 13. Rows 1, 2, 3, 4, 12 and 16 landed with phase 1,
and rows 5, 8, 9, 11, 14, 15 and 17 landed with phases 2 and 3.

## Scope A dependency, per task

Scope A's `KhaozEngine.Catalog` shipped its milestone 1.1 to `main` (the registry, the schema, the four pack
formats, `ContentVarint`, the hashes, `RemapRuleSet`, `FileSystemPackStore`, `ContentPackReader`,
`IContentSnapshot`, `ItemRow` and `ContentValidator`). **Everything this plan needs BEYOND 1.1 is in flight
and unmerged**, which is `OWNER DECISION 1`.

| Name | Where it lives today | Where this plan reads it |
|---|---|---|
| `ContentTypeRegistry.RegisterContentType` with its `band`, `schema`, `chunkSlots` and `loadIndex` arguments | `main` | tasks 1, 2, 3, 4 |
| `ContentRegistrationBand.Instances` (256 to 1023) | `main` | every one of the eighteen registrations |
| `ContentFieldSchema`, `ContentFieldEntry`, `ContentFieldKind` (the seven value kinds) | `main` | tasks 1, 2, 3 |
| `ContentRowCodecBase`, the positional walk driven by the schema | `main` | tasks 1, 2, 3 |
| `ContentTextKey`, the one derivation of a localization key | `main` | every `LocalizedTextKey` marker field |
| `EngineContentTypes.SocketTypeTypeKey`, the late binding `base_socket.socket_type` resolves through | `main` | task 2, which is what finally registers a type under that key |
| `EngineContentTypes.TagTypeKey`, `StatTypeKey`, `ItemTypeKey` | `main` | every `tag_id`, `stat_id` and `base_id` reference target |
| `IContentSnapshot`, `ContentRow`, `ContentFieldValue`, `ItemRow` | `main` | tasks 4, 5, 7, 9, 11, 12 |
| `ContentValidator` and its five passes, plus `RunInstanceBand` | `main`, as an EMPTY hook | task 3, which fills it (`OWNER DECISION 6`) |
| `IRandomSource` (`KhaozEngine.Primitives`, contracts 14.1) | `main` | tasks 5 and 9, by CONSTRUCTOR |
| **The publish path**: `KhaozEngine.Catalog.Authoring`, `ContentPublisher`, the publish diff, the id allocator, `ContentClientEncodeCheck` | `feature/content-catalog-phase1-b` and `-c`, UNMERGED | tasks 2, 3 and 6. This is the gate spec 20 phase 4 names |
| **`ContentRuntime`, `ContentTypeTable`, `ContentDerivedIndexes`, `ContentTagIndex`, `ContentLootIndex`** | `feature/content-catalog-phase1-c`, UNMERGED | task 4, whose bucket shape is `ContentLootIndex`'s prefix sums one level up |
| **`IContentLoadIndex`'s wiring**, the `loadIndex:` argument and the type-id-ordered build at boot step 7b | interface on `main`, WIRING on `-c` | task 4, which is how `ModCandidateTables` gets built at boot |
| `ContentStringCatalog` (catalog plan task 32) | `feature/content-catalog-phase1-d`, UNMERGED | NOT read by any task here. Every name this plan composes is stored as IDs and composed by the DISPLAY layer through `rarity_rule.display_format` (spec 8.5). Named so a later reader does not add the dependency |
| `ContentFamilyIndex` and family declarations ([#934](https://github.com/APKiwiOrg/KhaozEngine/issues/934)) | index on `-c`, DECLARATIONS nowhere | **NOT read by any task here, and it blocks nothing.** Spec 8 uses `mod_group` for exclusivity and TAGS for weighting, and no schema in section 8 names a family. Verified by reading all eighteen tables. #934 stays open and does not gate this plan |

**`ContentLootIndex` is the shape task 4 copies, not a type it consumes.** Its per-table entry list with the
weights PREFIX SUMMED, held as flat arrays sliced per table so no table owns a collection of its own, is
exactly what spec 9.2 asks for one level up with the key widened from a table id to `(tag, kind, band)`.
Read `KhaozEngine.Catalog/Runtime/ContentLootIndex.cs` on `feature/content-catalog-phase1-c` before writing
task 4, and copy the shape and the reasoning about a zero-width entry never being the answer of a binary
search. Do NOT reference the type.

## Global Constraints

Binding on every task. Each restates a rule from the spec, the contracts or AGENTS.md, and a task that
breaks one of these is wrong even when its own tests pass. The first eleven are the phase 2 and 3 plan's,
restated because they still bind. The rest are what these two phases add.

- **Work in a fresh worktree** at `~/KhaozEngine/.claude/worktrees/item-instances-phase4-5` branched from
  the latest `origin/main`, which must already carry both the phase 2 and 3 release and the Scope A
  milestones `OWNER DECISION 1` names. Engine program
  [#884](https://github.com/APKiwiOrg/KhaozEngine/issues/884) owns this work.
- **The payload is a canonical TLV and the encoder is the thing that makes it canonical.** Fields strictly
  ascending by kind, no kind twice, every varint minimal (contracts 9.3). Phase 1 shipped that encoder and
  that decoder: do not write a second one.
- **Varints are unsigned minimal LEB128 and nothing in this plan is zig-zagged.** Use `ContentVarint` from
  `KhaozEngine.Catalog`. Do NOT write a second varint implementation anywhere.
- **Little endian through `BinaryPrimitives` with the endianness in the method name, on BOTH sides.**
  `BitConverter` is forbidden (contracts 15).
- **No floats anywhere on these paths.** Integer maths only (contracts 13.4 and 15). This is the constraint
  these two phases break most easily: a weighted draw is `NextInt(0, weightTotal)` over an integer
  cumulative array, never a floating point draw and never a rejection loop, and a stat fold is `long`
  arithmetic with floor division.
- **Byte equality IS the stacking rule** (spec 4.6). Nothing anywhere decodes two payloads to compare them.
- **The decoder NEVER throws.** It answers false plus a reason token from the CLOSED set of contracts 9.7.
  Adding a token is a deliberate, additive act and never something a task does in passing.
- **No ambient statics and no service locator.** Every dependency arrives through a constructor or a method
  argument, including `IContentSnapshot`, `IRandomSource` and `IStatConditionRegistry` (contracts 14.4).
- **One responsibility per file, every file under 800 lines.** When the KESIZE ratchet fires, put the new
  code in a new type. Never split a file at an arbitrary line, and never hand-edit `.filesize-baseline`.
  Three tasks here are large enough that the split is planned up front rather than discovered.
- **Warnings are errors** in every configuration. Fix at the source, never with `NoWarn` or a pragma.
- **CI tests Release and a local `dotnet test` runs Debug.** Spec 17's own note says test 7 must run once
  with `-c Release` before merging, because the evaluator's overflow behaviour differs under a
  `Debug.Assert` rescue.
- **A test that writes process-global state enlists in a `DisableParallelization` collection.**
  `KhaozEngine.ItemInstances.Tests` has NO collection today and nothing in this plan should add process
  state. `CraftingRegistry` is PER INSTANCE and frozen per instance, following `ContentTypeRegistry` and
  `InstancePropertyRegistry`, never a static, which is what keeps that true.
- **Every roll goes through `IRandomSource`, taken in the CONSTRUCTOR** (contracts 14.4, spec 9.1). A type
  with no `IRandomSource` provably cannot roll, and that property is the whole point. A replay tool builds a
  SECOND generator sharing the same immutable tables.
- **The draw COUNT is a function of the affix count and nothing else** (spec 9.3). A candidate that is
  filtered out leaves the pool BEFORE the draw. A pick whose live pool is empty still draws and discards.
  Without that, a seeded session diverges at the first item whose pool empties.
- **No PoE mod, currency, rarity or tier is copied into engine code, ever** (gate 0, spec 8.1 and 8.10). The
  engine ships eighteen SHAPES, their codecs and their validators. A test fixture may author rows, and a
  fixture row is never shipped as content.
- **The eighteen types register in the `Instances` band, 256 to 1023, in ONE block, and an id is never
  reused** (contracts 4.3 and 5.1). The eighteen ids of spec 8.1 are constants in one file.
- **A child type carries `id`, `key`, a key reference to its parent, and a `sort` where its ORDER matters**
  (spec 8.1). A child whose rows have no order has no `sort`. That shape is uniform across all eleven so an
  editor, an audit row and a publish diff read the same way for every one.
- **Field names are snake_case on every Scope B type** (spec 8.2, contracts 12.1 and 5.3), because the
  localization key is derived mechanically and the validator refuses anything else.
- **The generator writes through `ItemInstancePayloadBuilder` and the craft working copy is a BUILDER.**
  Neither one patches bytes. That is what makes canonical form free and what stops a second encoder existing.
- **Every division in the stat fold is FLOOR division in basis points** (contracts 13.2, spec 11.6).
  `Math.DivRem` with a negative-remainder adjustment, never `Math.Round` and never bare `/`.
- **One implementation of each rule.** One weighted draw, one roll formula, one fold, one guard evaluator,
  one refusal vocabulary. Spec 6.4's roll formula already exists as arithmetic in the spec and must exist
  exactly once in code.
- **No em dashes, no en dashes, no semicolons in prose** in any file this plan writes, code comments and XML
  doc included. Run `scripts/check-dashes.sh --tree` and `scripts/check-prose.sh --tree` before every push.
- **Commit early, with explicit paths.** Never `git add -A`, never `git commit -a`, never `git stash`. Every
  task ends with its own commit. Push after the first commit and after each later one.
- **Never route around a guard.** No `--no-verify`, no `FILESIZE_OK`, no `PACK_RELEASED_OK`, no
  `BACKLOG_FILE_OK`. A hook that blocks you is the answer: stop and report it.

## Task index

| # | Task | Size | Gate |
|---|---|---|---|
| 1 | The `mod` family: `mod`, `mod_group`, `mod_tier`, `mod_tier_weight`, `stat_line` | large | Scope A registry |
| 2 | The rarity, unique, socket and rare-name families, ten types | large | task 1 |
| 3 | The three currency types, `InstanceContentTypes.Register`, and the `KEC0100` band | large | tasks 1 and 2, Scope A publish |
| 4 | `ModCandidateTables`: bands, the kind-split buckets, the overlap, the load index | large | task 3, Scope A runtime |
| 5 | `ItemGenerator`: the thirteen steps, the identification state and the `item-generated` body | large | task 4 |
| 6 | Distribution facts, the end-to-end authored pack, and budgets 5 and 9 re-pointed | medium | task 5 |
| 7 | `CraftWorkingCopy` and the fourteen primitives | large | task 5 |
| 8 | The guard vocabulary, the selector and the three standing rules | medium | task 7 |
| 9 | `CraftPlan`, `CraftingRegistry`, `ICraftOperation` and currency resolution | large | tasks 3 and 8 |
| 10 | The craft journal operation, the intent, the `item-crafted` body and its reader | medium | task 9 |
| 11 | `ContentStatEvaluator`, the fold, the conditions and budget 6 | large | Scope A registry |
| 12 | `InstanceStatLines`: a payload plus content into `StatModifierLine`s | medium | tasks 1 and 11 |
| 13 | Documentation sweep: package READMEs, README catalog, USING, DEPENDENCY-SEAMS | medium | tasks 1 to 12 |
| 14 | The finishing ritual: one version bump, changelog, pack, no tag | medium | task 13 |

Groups and their acceptances:

- **Group A, tasks 1 to 3.** The eighteen content types, their codecs, their schemas and their band of
  findings. Acceptance: every one of the eighteen registers, round trips and validates, the `KEC0100` band
  is reachable from a publish, and `dotnet test -c Release` green across the solution.
- **Group B, tasks 4 to 6.** The tables and the generator, spec 9. Acceptance: spec 17 row 6 green, budgets
  5 and 9 measured against the SHIPPED types, and one authored pack rolling items end to end. **With group
  A, that is spec 20 phase 4's acceptance.**
- **Group C, tasks 7 to 10.** The crafting framework, spec 10. Acceptance: spec 17 rows 10 and 13 green, and
  one authored currency composing at least four primitives with guards.
- **Group D, tasks 11 and 12.** The stat evaluation base, spec 11. Acceptance: spec 17 row 7 green and
  budget 6 measured. **With group C, that is spec 20 phase 5's acceptance.**
- **Group E, tasks 13 and 14.** Documentation and release. Acceptance: `scripts/check-doc-versions.sh`,
  `scripts/check-dashes.sh --tree`, `scripts/check-prose.sh --tree` and `scripts/check-file-size.sh --tree`
  all exit 0, the full solution green in Release, and `local-feed` packed with NO tag.

**The budgets each gate measures, and which `--items` phase measures them.** The mode is
`KhaozEngine.Benchmarks/Items/`, driven by `ItemsBenchmarkConfig` (`--quick`, `--players`, `--generations`,
`--crafts`, `--seed`, `--database`, `--output`) and `ItemsBenchmarkRunner.RunAsync`, with the checked-in
baseline at `KhaozEngine.Benchmarks/Baselines/items-sqlite-v1-seed915.json`.

| Budget | Target (spec 16) | The `--items` phase that measures it | Task |
|---|---|---|---|
| 5, rare generation time | under 20 us, measured p50 1.9 and p99 5.8 us | `ItemsWorkMeasurements.MeasureGeneration` and `MeasureColdGeneration`, reported as `Budget5P50Microseconds`, `Budget5P99Microseconds`, `Budget5AllocatedBytesPerGeneration`, `Budget5DeadEntriesPerGeneration` and `Budget5InvariantViolations` | 6 |
| 9, table build at 2,000 mods | under 500 ms and 40 MB, measured 266 ms and 24.1 MB | the `ModCandidateTables.Build` call timed inside `ItemsBenchmarkRunner.RunAsync`, reported as `Budget9TableBuildMilliseconds`, `Budget9TableResidentBytes`, `Budget9TableSelfReportedBytes`, `Budget9SuppressedEntries` and `Budget9ConsistencyFailures` | 6 |
| 6, stat evaluation per attack | under 2 us and 0 bytes, measured 444 ns and 0 bytes | `ItemsWorkMeasurements.MeasureStatEvaluation`, reported as `Budget6Nanoseconds`, `Budget6CachedNanoseconds`, `Budget6AllocatedBytes` and `Budget6LineCount` | 11 |

Budgets 1 to 4, 7, 8 and 10 to 13 are already measured and are NOT this plan's to move. A task that changes
one of those numbers has changed something it should not have, and the re-baseline of `OWNER DECISION 5`
must leave them byte identical.

---

## Group A: the eighteen affix content types (spec 8)

Every task in this group is gated on Scope A's registry, which is on `main`, and tasks 2 and 3 additionally
want the publish path for their publish-only checks. **The eighteen ids are assigned in ONE block and never
reused** (spec 8.1, contracts 5.1), so task 1 writes the whole id table even though it implements five of
the types.

### Task 1: the `mod` family, five types (large, gate: Scope A registry)

Spec 8.1, 8.2, 8.3 and 8.4. These five are the generator's ENTIRE input (spec 9.2, "the input is three row
sets and nothing else" plus the two that describe a line), so they come first and task 4 reads nothing else.

**Files:**

- Create: `KhaozEngine.ItemInstances/Content/InstanceContentTypeIds.cs`
- Create: `KhaozEngine.ItemInstances/Content/ModContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/ModGroupContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/ModTierContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/ModTierWeightContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/StatLineContentType.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Content/ModFamilyTests.cs`

**Interfaces:**

- Consumes: `ContentTypeRegistry.RegisterContentType`, `ContentRegistrationBand.Instances`,
  `ContentFieldSchema`, `ContentFieldEntry`, `ContentFieldKind`, `ContentRowCodecBase`,
  `ContentVisibility`, `ContentTextKey`, `EngineContentTypes.TagTypeKey` and `StatTypeKey`
- Produces: `InstanceContentTypeIds` (all eighteen ids and keys as `public const`), five static type classes
  each exposing `CreateSchema()`, a codec and its field-name constants

- [ ] **Step 1: Write the registration and round-trip facts first.**

~~~csharp
[Fact] public void The_five_register_in_the_Instances_band_at_256_257_263_264_and_265()
[Fact] public void A_registration_naming_the_Engine_or_Game_band_for_one_of_these_ids_throws()
[Fact] public void Every_field_name_is_snake_case_under_the_content_key_rules()
[Fact] public void A_row_round_trips_through_its_codec_byte_for_byte()
[Fact] public void An_absent_optional_field_writes_the_zero_form_and_reads_back_absent()
[Fact] public void A_localized_text_key_field_writes_NO_bytes_and_derives_mod_fine_crafted_line()
[Fact] public void mod_tier_weight_is_ServerOnly_as_a_WHOLE_TYPE_rather_than_per_field()
[Fact] public void The_five_chunk_slot_counts_are_the_8_1_table_exactly()
[Fact] public void A_codec_that_writes_a_field_the_schema_omits_throws_at_registration()
~~~

  The sixth is the one worth a sentence: `line` stores NOTHING (spec 8.2, contracts 4.7). The
  `LocalizedTextKey` kind is a MARKER whose presence in the schema declares that the derived key exists in
  the text chunks, and the row carries no string. A test that asserts the encoded row is one byte shorter
  than a naive reader expects is what stops someone adding a value to it later.

- [ ] **Step 2: Run them and confirm the missing types fail the build.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~ModFamilyTests
~~~

- [ ] **Step 3: Write `InstanceContentTypeIds` with all EIGHTEEN ids and keys**, spec 8.1's table verbatim,
  even though this task implements five. One file, `public const ushort` per id and `public const string` per
  key, with the table's parent column in the doc comment. The reason it is all eighteen now is contracts
  5.1: the ids are assigned in one block and never reused, so a later task cannot renumber and an implementer
  reading task 2 does not have to re-derive the assignment.
- [ ] **Step 4: Implement the five schemas exactly as spec 8.2, 8.3 and 8.4 write them.** Every field kind,
  every reference target, every visibility and every required flag from the tables. `mod.group_id` is a key
  reference to `mod_group` and is NOT required. `stat_line.tag_scope` is the contracts' own `TagList` value
  kind (contracts 4.7) and is the ONE multi-valued field in any Scope B schema, which is why it is a kind
  rather than a child type. `stat_line.condition_id` is a plain int and 0 means unconditional.
- [ ] **Step 5: Subclass `ContentRowCodecBase` ONLY where the generic positional walk cannot express a
  constraint**, and check the constraint on BOTH sides so an encoder cannot write a row its own decoder
  refuses. The constraints that need a subclass: `mod_tier.ordinal` is 1 to 255,
  `mod_tier.item_level_min` and `item_level_max` are 1 to 65535, and `stat_line.combine` is 1, 2 or 3. Every
  cross-ROW rule (a parent that resolves, an ordinal unique within its mod, `min <= max` against the
  previous snapshot) is a VALIDATOR check and belongs to task 3, not here. Keeping the split clean is what
  lets a codec stay a positional walk.
- [ ] **Step 6: Register `mod_tier_weight` as `ContentVisibility.ServerOnly` at the TYPE level**, not per
  field, and put spec 8.1's reason in the doc comment: the client chunk builder omits whole FIELDS, so a
  weight hidden inside a mixed-visibility row had no way out. Three whole `ServerOnly` types make it a
  publish-time property again, and a client downloads no weight row at all.
- [ ] **Step 7: Take the chunk slot counts from spec 8.1's table and no other source**, 4,096 for `mod`, 256
  for `mod_group`, 16,384 for `mod_tier`, 65,536 for `mod_tier_weight` and 32,768 for `stat_line`. They are
  powers of two between 256 and 65,536 as contracts 4.5 requires, and they are folded into the manifest hash
  through the chunk list, so changing one later renumbers every chunk of that type.
- [ ] **Step 8: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release
git add KhaozEngine.ItemInstances/Content KhaozEngine.ItemInstances.Tests/Content
git commit -m "iteminstances(content): the mod family, its tiers, its weights and its stat lines"
~~~

---

### Task 2: the rarity, unique, socket and rare-name families, ten types (large, gate: task 1)

Spec 8.5, 8.6, 8.7 and 8.8. Ten types, four parents and six children, in four families that do not depend on
each other. Split the file set by FAMILY rather than by an arbitrary line count, which is what keeps every
file under the ratchet by construction.

**Files:**

- Create: `KhaozEngine.ItemInstances/Content/RarityRuleContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/RarityWeightContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/RarityKindLimitContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/UniqueTemplateContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/UniqueLineContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/UniqueSocketContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/SocketTypeContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/SocketTagRuleContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/RareNameWordContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/RareNameWordWeightContentType.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Content/RarityFamilyTests.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Content/UniqueFamilyTests.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Content/SocketAndNameFamilyTests.cs`

**Interfaces:**

- Consumes: task 1's `InstanceContentTypeIds`, the same Scope A surfaces, plus
  `EngineContentTypes.SocketTypeTypeKey` and `ItemTypeKey`
- Produces: ten more static type classes in the same shape

- [ ] **Step 1: Write the facts that are about the FORMAT rather than about a schema.**

~~~csharp
[Fact] public void A_rarity_rule_id_above_255_is_refused_because_kind_130_is_ONE_BYTE()
[Fact] public void socket_type_registering_at_260_resolves_the_shipped_base_socket_late_binding()
[Fact] public void A_base_socket_row_naming_a_socket_type_id_resolves_once_both_are_registered()
[Fact] public void unique_template_weight_is_a_ServerOnly_FIELD_on_a_Client_type()
[Fact] public void The_client_encode_check_omits_that_one_field_and_keeps_the_rest_of_the_row()
[Fact] public void unique_socket_sort_IS_the_socket_index_of_kind_132_and_carries_authored_order()
[Fact] public void rare_name_word_position_is_1_to_255()
[Fact] public void The_three_weight_types_are_ServerOnly_as_WHOLE_types()
[Fact] public void Every_one_of_the_ten_round_trips_byte_for_byte()
~~~

  The first is a format constraint rather than a preference, and it is in spec 21's expensive table: kind
  130 is `[RarityId: byte]`, pinned by contracts 9.8's `82 01 01 03`, and contracts 5.1 never reuses an id,
  so every retired rarity keeps its number forever and the ceiling counts the dead as well as the live. The
  refusal belongs in the codec, not in prose.

- [ ] **Step 2: Implement the rarity family.** `rarity_rule` carries `display_format` as a marker, the four
  counts, `name_word_positions` and `upgrade_from` as a SINGLE parent so the rarities form a forest. Spec 8.5
  is explicit that `display_format` IS contracts 12.3's composed name template and that its arguments are the
  item's `rare_name_word` texts in POSITION ORDER followed by the base item's own name. Put that sentence in
  the field's doc comment, because it is the only place the argument order is written down and the display
  layer is in another repo.
- [ ] **Step 3: Implement the unique family, and write the ONE asymmetry into the doc comment.** A unique's
  lines are ORDINARY MOD ROWS with a single tier, no `mod_tier_weight` row anywhere and a group that keeps
  them off a rare, and `unique_line` points at that mod and tier (spec 8.6). That costs one mod row per
  unique line and buys a payload with no second affix shape, no second decode path and no second remap
  story. Spec 8.10 calls it the single most important consequence of 3.4 for an author to understand, so it
  is a doc comment on `UniqueLineContentType` and a row in the package README, not a sentence in a design doc
  nobody reads at authoring time.
- [ ] **Step 4: Implement the socket family.** `socket_tag_rule.rule` is 1 accept and 2 reject, and REJECT is
  checked FIRST and wins (spec 8.7). A type with no accept row accepts nothing, which is how a decorative
  socket is authored and is a legal state rather than an error. `socket_type.max_nested_bytes` of 0 means
  `MaxInstancePayloadBytes`, which is `ItemSlot.MaxPayloadBytes` and never a second copy of the number.
- [ ] **Step 5: Implement the rare-name family.** `rare_name_word.text` is a marker whose derived key is
  `rare_name_word.<key>.text`, and `position` is which slot in the name the word may fill. Kind 134 stores
  the word IDS in position order, so the name is reproducible from the payload with no re-roll, which is
  contracts 14.3 applied to a name.
- [ ] **Step 6: Register `socket_type` at 260 and confirm the late binding closes.**
  `EngineContentTypes.SocketTypeTypeKey` is the key `base_socket.socket_type` points at, and the shipped doc
  comment says "Scope B or a game registers a type under it". This task is what finally makes that true, so
  the test in step 1 is the one that proves the shipped engine type stops carrying a dangling reference
  target when instances are in use.
- [ ] **Step 7: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release
git add KhaozEngine.ItemInstances/Content KhaozEngine.ItemInstances.Tests/Content
git commit -m "iteminstances(content): rarity rules, unique templates, socket types and rare name words"
~~~

---

### Task 3: the currency types, the registration entry point and the `KEC0100` band (large, gate: tasks 1 and 2, Scope A publish)

Spec 8.8, 8.9 and 10.4. The three currency types register HERE so all eighteen ids sit in one table, and
their SEMANTICS are group C's. This task also fills the `KEC0100` band, which is the reason it is large.

**OWNER DECISION 6 is in step 5.** `ContentValidator.RunInstanceBand` is a shipped hook in
`KhaozEngine.Catalog` whose comment says "Scope B ships its own checks here", which cannot be written as
stated because `Catalog` cannot reference `ItemInstances`. The plan's default is to run the REGISTRATION's
own `IContentValidator` in pass 6 when its band is `Instances`, trusted, with its codes passed through.

**Files:**

- Create: `KhaozEngine.ItemInstances/Content/CraftingCurrencyContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/CurrencyStepContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/CurrencyGuardContentType.cs`
- Create: `KhaozEngine.ItemInstances/Content/InstanceContentTypes.cs`
- Create: `KhaozEngine.ItemInstances/Content/Validation/InstanceContentFindings.cs`
- Create: `KhaozEngine.ItemInstances/Content/Validation/ModFamilyChecks.cs`
- Create: `KhaozEngine.ItemInstances/Content/Validation/RarityAndUniqueChecks.cs`
- Create: `KhaozEngine.ItemInstances/Content/Validation/CurrencyAndSocketChecks.cs`
- Create: `KhaozEngine.ItemInstances/Content/Validation/RareNameCoverageCheck.cs`
- Modify: `KhaozEngine.Catalog/Validation/ContentValidator.cs` (pass 6, `OWNER DECISION 6`)
- Create: `KhaozEngine.ItemInstances.Tests/Content/InstanceContentValidationTests.cs`

**Interfaces:**

- Consumes: `IContentValidator`, `ContentFinding`, `ContentValidationReport`, the `previous` snapshot argument
- Produces: `InstanceContentTypes.Register(registry)`, the `KEC0100` to `KEC0199` band

- [ ] **Step 1: Write the twelve checks of spec 8.9 as twelve facts, plus the three PUBLISH-ONLY skips.**

~~~csharp
[Fact] public void A_child_row_whose_parent_reference_does_not_resolve_is_KEC0100()
[Fact] public void A_currency_guard_naming_a_step_of_a_DIFFERENT_currency_is_KEC0100()
[Fact] public void A_mod_tier_ordinal_outside_1_to_255_or_duplicated_within_its_mod_is_KEC0102()
[Fact] public void A_tier_reorder_since_the_previous_published_version_is_KEC0103()
[Fact] public void A_stat_line_whose_combine_is_not_1_2_or_3_or_whose_min_exceeds_max_is_KEC0104()
[Fact] public void A_mod_group_max_per_item_below_1_is_KEC0105()
[Fact] public void A_rarity_rule_whose_upgrade_from_chain_cycles_is_KEC0106()
[Fact] public void A_unique_line_whose_mod_carries_a_mod_tier_weight_row_is_KEC0107()
[Fact] public void A_socket_type_whose_accept_and_reject_tag_sets_overlap_is_KEC0108()
[Fact] public void A_rarity_position_with_no_word_of_non_zero_weight_reachable_is_KEC0109()
[Fact] public void A_currency_above_max_steps_or_with_a_duplicate_sort_is_KEC0110()
[Fact] public void A_rarity_rule_id_that_moved_since_the_previous_version_is_KEC0111()
[Fact] public void A_negative_or_overflowing_weight_on_any_of_the_three_weight_types_is_KEC0112()
[Fact] public void The_three_publish_only_checks_are_SKIPPED_when_previous_is_null_and_named_by_KEC0000()
[Fact] public void A_valid_authored_set_produces_no_finding_at_all()
[Fact] public void An_EMPTY_row_set_for_all_eighteen_types_is_VALID_because_every_cross_check_is_vacuous()
~~~

  The last one is the OSRS row of spec 8.10 and it is the fact that proves the claim these are shapes rather
  than PoE: zero mod rows, zero rarity rules, zero uniques, all eighteen types registered, and the validator
  permits it. Do not let it be an afterthought, because a check written as a `foreach` over rarities times
  positions times tags is the one most likely to divide by a count that is zero.

- [ ] **Step 2: Implement the three currency schemas from spec 10.4's tables.** `crafting_currency` carries
  `name` and `description` as markers, `consumes_definition_id` as a key reference to `item`,
  `consumes_count` and `max_steps`. `currency_step` carries `sort`, `operation` and `parameter_a` to
  `parameter_d`. `currency_guard` carries `currency_step_id` which is EMPTY for a target guard and set for a
  step guard, plus `sort`, `guard_kind`, `parameter_a` and `parameter_b`. **Target guards and step guards are
  ONE type told apart by one empty reference**, because the guard SCHEMA is identical and a second type
  would duplicate it.
- [ ] **Step 3: Implement `InstanceContentTypes.Register(registry)`**, mirroring
  `EngineContentTypes.Register` exactly: all eighteen, once, at process start and before any pack loads, with
  a second call on the same registry throwing because the ids are taken. That single entry point is what a
  host calls, and it is what makes "does this process use instances" one line in a boot sequence.
- [ ] **Step 4: Implement the checks in four files split by FAMILY**, not by line count, with
  `InstanceContentFindings` holding the code constants and the message templates. The code is a STABLE token
  a counter, a test and an operator runbook key on, so a code is never renumbered and a withdrawn one is
  never reissued, exactly as `KEC0001` to `KEC0042` are treated. **`KEC0109` is the expensive one** and it
  gets its own file: it is a cross product over rarities, positions and tags, run once per publish, and spec
  8.9 says so. Write it as a single pass building a reachable-tag set per rarity and then one sweep, never as
  a nested loop over every base.
- [ ] **Step 5: Wire pass 6 in `KhaozEngine.Catalog`. OWNER DECISION 6.** The edit is additive and small:
  `RunInstanceBand` iterates the registrations whose `Type.IsInstances` is true, calls each one's own
  `IContentValidator` if it has one, and adds its findings UNCHANGED rather than folding them into
  `KEC0040`. A throw from one PROPAGATES, because an Instances-band type is engine code in the engine's own
  band and a throw there is a bug rather than untrusted input. Update the method's doc comment in the same
  commit, because the shipped comment describes a mechanism that cannot exist. Do not touch passes 1 to 5,
  do not touch `RunTypeValidators`, and do not change what `KEC0000` means.
- [ ] **Step 6: Bound the three weight types, which closes the Scope B half of
  [#944](https://github.com/APKiwiOrg/KhaozEngine/issues/944).** `mod_tier_weight.weight`,
  `rarity_weight.weight` and `rare_name_word_weight.weight` are bare ints in spec 8, exactly as
  `loot_entry.weight` was, and the same hazard applies one level up: a negative weight makes a prefix array
  non-monotonic and unsearchable, and a sum past `int.MaxValue` saturates and silently changes the odds.
  `KEC0112` refuses a weight below zero, and `KEC0113` refuses a bucket whose weights sum past
  `int.MaxValue`. The engine-range half of #944 stays that issue's. Say so in the commit body rather than
  closing it.
- [ ] **Step 7: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release
dotnet test KhaozEngine.Catalog.Tests/KhaozEngine.Catalog.Tests.csproj -c Release
git add KhaozEngine.ItemInstances/Content KhaozEngine.ItemInstances.Tests/Content KhaozEngine.Catalog/Validation/ContentValidator.cs
git commit -m "iteminstances(content): the currency types, one registration entry point and the KEC0100 band"
~~~

**Group A acceptance:** all eighteen types register in the `Instances` band, every row round trips byte for
byte, the `KEC0100` band is reachable from a publish through pass 6, an empty row set for all eighteen is
valid, and `dotnet test -c Release` is green across the solution.

---

## Group B: the candidate tables and the generator (spec 9)

### Task 4: `ModCandidateTables`, the bands, the buckets and the overlap (large, gate: task 3, Scope A runtime)

Spec 9.2. **LIFT FROM THE SPIKE:** `KhaozEngine.Benchmarks/Items/ModCandidateTables.cs` (477 lines) is the
whole shape, measured: the band boundaries, the flat pair of arrays per `(tag, kind, band)` bucket with the
packed key beside the cumulative weight, the group index, the per-signature suppression lists, the
self-check that compares the merged count and weight against what the suppression scalars imply, and
`ResidentBytes`. Four things change on the way in, and each is a correctness change rather than a port.

1. **`SyntheticContent` is replaced by `IContentSnapshot`.** The input is three row sets and nothing else
   (spec 9.2): every `mod` row for its `kind`, `group_id` and `legacy`, every `mod_tier` row for its ordinal
   and its two level bounds, and every `mod_tier_weight` row for its tag and weight. All three are ordinary
   rows, so the build is a scan of three indexed row sets.
2. **`KindCount = 2` is replaced by the AUTHORED kind set.** Spec 8.2 gives kinds 1 prefix and 2 suffix to
   the engine and leaves 3 to 255 to the game, and spec 9.4 step 5 draws over "the kinds still under their
   per kind cap", which includes every kind a `rarity_kind_limit` row names. The spike's constant 2 would
   silently drop an implicit, a corruption line or a material line. Build the kind list from the `mod` rows
   present, ascending, and index buckets by its POSITION rather than by the kind id.
3. **`TierBits = 4` is `OWNER DECISION 4`.** The default is to keep the packing at 4 bits and make an
   ordinal above 15 a `KEC0102` publish refusal in task 3, so the ceiling is loud rather than a table that
   quietly aliases tier 16 onto tier 0.
4. **The suppression index is a `ushort`, so a bucket is capped at 65,535 entries** (spec 9.2 item 3). That
   cap needs a refusal rather than a wrap: a bucket that would exceed it is a build-time throw, because
   `IContentLoadIndex.Build` fails the boot closed by contract.

**Files:**

- Create: `KhaozEngine.ItemInstances/Generation/ModCandidateTables.cs`
- Create: `KhaozEngine.ItemInstances/Generation/ModCandidateTables.Build.cs`
- Create: `KhaozEngine.ItemInstances/Generation/ModCandidateTables.Overlap.cs`
- Create: `KhaozEngine.ItemInstances/Generation/ModCandidateTablesIndex.cs`
- Create: `KhaozEngine.ItemInstances/Generation/GenerationTagSignature.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Generation/ModCandidateTablesTests.cs`

**Interfaces:**

- Consumes: `IContentSnapshot`, `IContentLoadIndex`, `ContentRow`, `ItemRow`, task 1's five types
- Produces: `ModCandidateTables` with a per-roll query surface, and `ModCandidateTablesIndex` as the
  `IContentLoadIndex` a host registers against the `mod` type

- [ ] **Step 1: Write the facts that pin the SHAPE, because a wrong overlap list is a wrong weight rather
  than a crash.**

~~~csharp
[Fact] public void The_bands_are_the_intervals_between_every_distinct_min_and_max_plus_one()
[Fact] public void Within_one_band_no_tiers_gate_changes_so_the_live_tier_set_is_constant()
[Fact] public void A_bucket_is_ascending_by_packed_key_BY_CONSTRUCTION_with_no_sort_pass()
[Fact] public void A_LEGACY_mod_never_enters_a_table_at_all()
[Fact] public void A_tier_with_no_weight_row_can_never_spawn_and_that_is_legal()
[Fact] public void A_base_weight_is_the_FIRST_matching_tag_in_authored_order_never_a_sum()
[Fact] public void The_suppression_scalars_reproduce_the_merged_count_and_weight_on_every_key()
[Fact] public void The_union_is_never_materialised_at_boot_or_at_a_roll()
[Fact] public void A_bucket_above_65535_entries_THROWS_at_build_rather_than_wrapping_a_ushort()
[Fact] public void A_snapshot_with_NO_mod_tier_weight_rows_builds_empty_tables_which_is_a_CLIENT()
[Fact] public void The_index_reads_another_types_ROWS_and_never_another_INDEX()
[Fact] public void A_build_failure_THROWS_and_fails_the_boot_closed()
~~~

  The seventh is the self-check spec 9.2 ends on and it is the only thing downstream that can catch a wrong
  list, because a wrong list produces a plausible number. Compute the merged count and weight along the same
  pass that records the discards, compare against what the scalars imply, and COUNT any disagreement into a
  public property the benchmark reports as `Budget9ConsistencyFailures`. The measured count is zero over all
  15,000 keys and a non-zero one is a red test rather than a note in a log.

  The tenth is not an edge case, it is the CLIENT. Weights are `ServerOnly` as whole types (spec 8.3), so a
  client's pack carries no weight row, and the tables it builds are empty by construction rather than by a
  flag. That is the property that keeps the generator off a client with no `if (isServer)` anywhere.

- [ ] **Step 2: Build the bands.** Collect every distinct `item_level_min` and `item_level_max + 1` across
  every `mod_tier` row, sort, and take the intervals as the bands. The COUNT is the authored level curve and
  nothing else decides it, so this task does not get to pick it. Resolve a band from an item level with a
  binary search over the boundaries and nothing else.
- [ ] **Step 3: Build the `(tag, kind, band)` buckets as a flat pair of arrays.** The packed key is
  `(mod id << TierBits) | tier ordinal` and the value beside it is the CUMULATIVE weight through that entry.
  Ascending packed order IS `(mod id, tier ordinal)` order, so the bucket is sorted by construction rather
  than by a sort pass, which is what makes a pick reproducible from a seed and independent of pack load
  order (contracts 4.3). Three things fold in at BUILD time rather than per candidate: the kind, which is
  now the bucket, the legacy flag, because a legacy tier never enters a table, and the running weight,
  because a cumulative array is a binary search and a weight array is a walk.
- [ ] **Step 4: Precompute the OVERLAP, and precompute what the union DISCARDS rather than the union.** For
  each `(tag signature, kind, band, tag position)`, record the sorted 16 bit indices an earlier tag of the
  same signature already carries, plus their summed weight and their count. A roll subtracts those two
  scalars from the bucket's own count and total, which gives the union's count and total without the union,
  and skips the listed entries inside the draw. **Nothing is allocated, memoized or evicted at a roll**, so
  there is no cache, no hit rate, no eviction policy and no pathological pack that degrades to a merge per
  roll. Spec 9.2's measured table is the reason and it is worth re-reading before writing this step: the
  union materialised is 148 MB, the memoized merge is 11 us per roll at 86 percent of rolls, and the overlap
  is 5.2 MB and zero.
- [ ] **Step 5: Declare a tag-position ceiling and refuse past it.** The suppression header count is
  `signatures * bands * kinds * positions`, so an unbounded position count makes budget 9 unbounded. Declare
  `MaxGenerationTagPositions = 8`, build the overlap for the positions a signature actually has, and refuse a
  base whose authored tag list exceeds the ceiling with a task 3 finding rather than at build. Spec 9.2's
  arithmetic assumes two to four, and eight is double the measured worst case with the memory stated in the
  doc comment.
- [ ] **Step 6: Build the group index at boot** (spec 9.4 step 6), so "every other mod of this group" is a
  lookup rather than a scan. It is the fourth flat array and it is built in the same pass.
- [ ] **Step 7: Register as an `IContentLoadIndex` against the `mod` type.** `Build(IContentSnapshot)` reads
  the other four types' rows through the snapshot, which the interface explicitly permits, and reads NO other
  index. It throws to fail the boot closed. **A BOOT builds the tables, not a publish**, and they are
  immutable for the life of the process, so there is exactly ONE table set in a process and no roll can see
  two. Do not build a swap: a new content version becomes active at server RESTART.
- [ ] **Step 8: Expose `ResidentBytes` as the self-reported size**, mirroring the spike, so budget 9 has a
  number that does not depend on the GC's mood alongside the two that do.
- [ ] **Step 9: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~ModCandidateTables
git add KhaozEngine.ItemInstances/Generation KhaozEngine.ItemInstances.Tests/Generation
git commit -m "iteminstances(generation): banded candidate tables and the precomputed overlap"
~~~

---

### Task 5: `ItemGenerator`, the thirteen steps and the `item-generated` body (large, gate: task 4)

Spec 9.1, 9.3, 9.4 and 9.5. **LIFT FROM THE SPIKE:**
`KhaozEngine.Benchmarks/Items/SpikeItemGenerator.cs` (521 lines) is spec 9.4's thirteen steps in order with
every working array as scratch the generator owns, so the only per-generation allocation is the payload
buffer. Port it against the shipped types and keep the scratch-array discipline, because budget 5's
allocation column is the half that binds.

**Files:**

- Create: `KhaozEngine.ItemInstances/Generation/ItemGenerator.cs`
- Create: `KhaozEngine.ItemInstances/Generation/ItemGenerator.Draw.cs`
- Create: `KhaozEngine.ItemInstances/Generation/ItemGenerator.Assemble.cs`
- Create: `KhaozEngine.ItemInstances/Generation/GenerationContext.cs`
- Create: `KhaozEngine.ItemInstances/Generation/GenerationResult.cs`
- Create: `KhaozEngine.ItemInstances.Journal/ItemGeneratedEvent.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Generation/ItemGeneratorTests.cs`
- Create: `KhaozEngine.Server.Tests/ItemInstances/ItemGeneratedEventTests.cs`

**Interfaces:**

- Consumes: `ModCandidateTables`, `IRandomSource`, `IContentSnapshot`, `ItemRow`,
  `ItemInstancePayloadBuilder`, `InstanceIdAllocator`, `InstancePropertyKind`
- Produces: `ItemGenerator.Generate(in GenerationContext)`, `GenerationContext`, `GenerationResult`,
  `ItemGeneratedEvent.Write` and `TryRead`

- [ ] **Step 1: Write the ORDER facts first, because the order of the draws is the reproducibility
  contract.**

~~~csharp
[Fact] public void Two_items_of_one_rarity_on_one_base_at_one_level_consume_the_SAME_draw_count()
[Fact] public void A_pick_whose_live_pool_is_EMPTY_still_draws_twice_and_places_nothing()
[Fact] public void The_kind_is_drawn_BEFORE_the_mod_so_a_deep_prefix_pool_does_not_eat_the_suffixes()
[Fact] public void Placing_a_mod_deducts_its_WHOLE_run_of_tiers_from_every_table_of_its_kind()
[Fact] public void A_group_at_max_per_item_deducts_every_other_mod_of_that_group()
[Fact] public void An_entry_the_overlap_already_suppressed_is_never_deducted_twice()
[Fact] public void The_landing_entry_of_a_shifted_draw_is_LIVE_by_construction()
[Fact] public void The_affix_list_is_sorted_ascending_by_mod_id_before_it_is_written()
[Fact] public void A_forced_unique_takes_its_lines_and_sockets_from_content_and_draws_NOTHING()
[Fact] public void A_generated_item_carries_kind_128_with_state_0_and_a_revealed_mask_of_0()
[Fact] public void A_payload_that_would_be_EMPTY_takes_instance_id_0_and_is_a_plain_stack()
[Fact] public void The_only_allocation_per_generation_is_the_payload_buffer()
[Fact] public void The_generator_holds_its_IRandomSource_and_has_no_per_call_source_parameter()
~~~

  The second is the one an implementer removes as dead code and must not: `NextInt(0, 1)` in place of step
  7's weighted pick and one `NextRollPosition()` in place of step 8, discarded. Without the two discards one
  item consumes fewer draws than another of the same rarity on the same base, and a seeded session diverges
  at the first item whose pool empties. `NextInt(0, 1)` rather than nothing, because step 7's real draw is
  `NextInt(0, liveWeight)` and a weight total of zero is not a legal argument.

  The thirteenth is contracts 14.4 read as a test rather than as a comment. A per-call source keeps "does
  this roll" answerable at the METHOD and loses it at the TYPE, and the type is the half the contract cares
  about.

- [ ] **Step 2: Implement the API spec 9.4 gives, with two additions the spec's own prose requires.**

~~~csharp
public readonly record struct GenerationContext(
    int BaseId, int ItemLevel, int ForcedRarityId, int ForcedUniqueTemplateId, int Quality);

public readonly record struct GenerationResult(
    int BaseId, long InstanceId, ReadOnlyMemory<byte> Payload, int RarityId, int ContentVersion,
    int AffixCount, int RequestedAffixCount);

public sealed class ItemGenerator
{
    public ItemGenerator(ModCandidateTables tables, IContentSnapshot snapshot, IRandomSource random);
    public GenerationResult Generate(in GenerationContext context);
}
~~~

  **The two added members are not an embellishment.** Spec 9.4 step 8 says an item whose pool empties "ends
  with fewer affixes than the count asked for, which is a legal outcome and is REPORTED IN THE RESULT rather
  than retried", and the record it declares has nowhere to report it. The spike already carries
  `AffixCount`. Both are needed, because a caller that cannot tell a three-affix roll from a six-affix roll
  that ran dry cannot log the difference. Flag it to the spec owner in the task report.

- [ ] **Step 3: Implement steps 1 to 8 in `ItemGenerator.Draw.cs`, in the numbered order, with no
  reordering for convenience.** Opening the pool is eight scalar reads, no merge, no copy and no allocation.
  A pick is a walk of at most four tag positions, a walk of the dead entries that sit behind the draw, and a
  binary search of about ten steps. **None of those is a function of the candidate pool's SIZE**, and that is
  the property the first draft of spec 9.2 did not have, measured at 4,454 candidate visits and 47.8 us at
  the median. If a reviewer sees a loop over the candidate array, the design has regressed to the first
  draft.
- [ ] **Step 4: Implement steps 9 to 13 in `ItemGenerator.Assemble.cs`.** Sort ascending by mod id, roll the
  rare name per position, seat the sockets with NO draw in v1, assemble through
  `ItemInstancePayloadBuilder`, and allocate the instance id only when the payload is non-empty.
- [ ] **Step 5: Write kind 128 explicitly, with state 0 and a revealed mask of 0.** Spec 9.4 step 12's field
  list does not name kind 128 and spec 12.7 requires it, because an item that carries no `Identification`
  field at all is INDISTINGUISHABLE from an identified one under `CanSee`, whose `identified` argument would
  then have to be guessed by the caller. The generator is what decides a new item is unidentified, so the
  generator writes the field. State that in the task report, because it is a spec gap rather than a choice.
- [ ] **Step 6: Refuse an out-of-range input at the generator's door, and THROW.** An item level outside 1 to
  65535, a quality outside 0 to 65535, a rarity id outside 1 to 255, an affix count above 255. Each is a
  caller bug in the same class as `ItemInstancePayloadBuilder`'s own throws, and the generator is handed
  values by code rather than bytes by a peer. This is NOT the codec-level width enforcement of
  [#917](https://github.com/APKiwiOrg/KhaozEngine/issues/917), which stays gated on #903, and the doc comment
  says so rather than implying the hole is closed.
- [ ] **Step 7: Implement the `item-generated` body in `KhaozEngine.ItemInstances.Journal`, not in
  `ItemInstances`.** Spec 9.5's layout byte for byte. The package is where `ItemInstanceEvents` already
  names the event and where every other event body lives, and the generator itself never writes a journal
  byte: it hands back a `GenerationResult` and the caller that composes the commit encodes it. That keeps
  `Foundation` free of event-shaped types and it is a plan choice the spec leaves open.
- [ ] **Step 8: Write the READER beside the writer**, `TryRead` answering false plus a reason rather than
  throwing, because the bytes come from a store. Spec 9.5's whole argument for carrying the full payload is
  that the page is rewritten whole on every later commit, so this event is the only durable record of what
  the item looked like when it dropped. A record nothing can read back is not a record.
- [ ] **Step 9: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~ItemGeneratedEvent
git add KhaozEngine.ItemInstances/Generation KhaozEngine.ItemInstances.Journal/ItemGeneratedEvent.cs KhaozEngine.ItemInstances.Tests/Generation KhaozEngine.Server.Tests/ItemInstances
git commit -m "iteminstances(generation): the thirteen ordered steps and the item-generated body"
~~~

---

### Task 6: distribution, the authored pack, and budgets 5 and 9 (medium, gate: task 5)

Spec 9.6, 17 row 6 and 20 phase 4's acceptance. Three separate things, and all three are what turns "the
generator exists" into "the generator shipped".

**OWNER DECISION 5 is in step 4.** Re-pointing the `--items` mode at the shipped types re-baselines
`items-sqlite-v1-seed915.json`.

**Files:**

- Create: `KhaozEngine.ItemInstances.Tests/Generation/GeneratorDistributionTests.cs`
- Create: `KhaozEngine.Server.Tests/ItemInstances/AuthoredPackGenerationTests.cs`
- Modify: `KhaozEngine.Benchmarks/Items/ItemsWorkMeasurements.cs`
- Modify: `KhaozEngine.Benchmarks/Items/ItemsBenchmarkRunner.cs`
- Modify: `KhaozEngine.Benchmarks/Items/SyntheticContent.cs`
- Modify: `KhaozEngine.Benchmarks/Baselines/items-sqlite-v1-seed915.json`
- Delete: `KhaozEngine.Benchmarks/Items/SpikeItemGenerator.cs`, `ModCandidateTables.cs`, `RarePool.cs`,
  `RareNameTables.cs`

- [ ] **Step 1: Write spec 17 row 6, which is three claims and not one.**

~~~csharp
[Fact] public void Each_candidates_share_of_a_large_seeded_run_is_its_weight_over_the_pool_total()
[Fact] public void The_tolerance_is_stated_as_an_INTEGER_bound_rather_than_a_float()
[Fact] public void Roll_positions_are_uniform_and_BOTH_ENDS_of_a_tier_range_are_reachable()
[Fact] public void The_draw_count_is_a_function_of_the_affix_count_including_a_pool_that_empties()
~~~

  The second is the assertion obeying the rule it tests. A chi-squared style bound stated as COUNTS keeps
  contracts 13.4 true inside the test itself, and a test that computes a float tolerance to assert an
  integer system is the kind of thing that passes review and then teaches the next reader the wrong lesson.

  The third is contracts 6.4's worked table: position 0 gives `min`, position 65,535 gives `max`, and
  position 32,768 lands exactly halfway. Assert the three rows of that table directly, not a statistical
  approximation of them.

- [ ] **Step 2: Note what row 6 does NOT test, and why.** `SeededRandomSource` wraps `DeterministicRng`,
  whose `Next(int)` uses modulo and says so, and `CryptographicRandomSource` uses rejection sampling
  (contracts 14.2). Modulo bias on an affix pool is farmable, which is exactly why the two implementations
  differ, and a distribution test run on the seeded source cannot see the production source's property. Put
  that in the test class doc comment so nobody later concludes the seeded run proved the cryptographic one.
- [ ] **Step 3: Write the END TO END fact, which is phase 4's acceptance clause and lives in
  `KhaozEngine.Server.Tests`** because it needs the authoring store and the publish path. Author a small set
  of rows through `IContentAuthoringStore`, publish them through `ContentPublisher` into a
  `FileSystemPackStore`, read them back with `ContentPackReader`, build a `ContentRuntime`, let
  `ModCandidateTablesIndex` build at boot step 7b, and roll items. The rows are a TEST FIXTURE and they are
  not PoE: a handful of shapes chosen to exercise two kinds, two tiers, a group, a unique and a two-word
  rare name. Assert that every rolled payload decodes, validates and stacks with its own twin.
- [ ] **Step 4: Re-point the `--items` mode at the shipped types. OWNER DECISION 5.** Delete the spike's
  generator, its tables and its two pool helpers, keep `SyntheticContent` as the row SOURCE by publishing it
  through `ContentSnapshotBuilder` into real rows of the eighteen types, and point
  `ItemsWorkMeasurements.MeasureGeneration` and the timed table build at `ItemGenerator` and
  `ModCandidateTables`. Keep the seed at 915 and keep every other phase untouched. **Budgets 1 to 4, 7, 8
  and 10 to 13 must come back byte identical**, and a phase whose number moves means the re-point changed
  something it should not have.
- [ ] **Step 5: Re-baseline in ONE commit whose message names the old and new numbers.** Budget 5 was p50
  1.9 us and p99 5.8 us, budget 9 was 266 ms and 24.1 MB. Both targets stay where spec 16 sets them, under
  20 us and under 500 ms and 40 MB, and a shipped implementation that misses a target is a STOP rather than
  a new baseline.
- [ ] **Step 6: Extend the structural test beside the mode rather than writing a second one.**
  `KhaozEngine.Server.Tests/Benchmarks/ItemsBenchmarkTests.cs` already fences the mode, so add the facts
  that budget 5's and budget 9's fields are PRESENT and non-zero in the result and that
  `Budget9ConsistencyFailures` is exactly 0. **Assert the structure, never the numbers**: a test that
  asserts a microsecond figure goes red on a busy runner and teaches everyone to rerun it.
- [ ] **Step 7: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~Items
dotnet run --project KhaozEngine.Benchmarks -c Release -- --items --quick
git add KhaozEngine.ItemInstances.Tests/Generation KhaozEngine.Server.Tests/ItemInstances KhaozEngine.Benchmarks KhaozEngine.Server.Tests/Benchmarks/ItemsBenchmarkTests.cs
git commit -m "bench(items): measure budgets 5 and 9 against the shipped generator and tables"
~~~

**Group B acceptance, which with group A is spec 20 phase 4's own:** spec 17 row 6 green, budgets 5 and 9
measured against the SHIPPED types and inside their spec 16 targets, and one authored pack of real rows
rolling items end to end through the publish path.

---
