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
| 5, rare generation time | under 20 us, measured p50 1.9 and p99 5.8 us | `ItemsWorkMeasurements.MeasureGeneration` and `MeasureColdGeneration`, reported as `Budget5P50Microseconds`, `Budget5P99Microseconds`, `Budget5AllocatedBytesPerGeneration`, `Budget5PoolSuppressedEntries` and `Budget5InvariantViolations` | 6 |
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

## Group C: the crafting framework (spec 10)

**v1 ships the framework and ZERO currency rows**, which is #884's "Out of scope for v1" taken literally and
which the authored currency of task 9 does not contradict: that currency is a test fixture, and a fixture is
not shipped content.

### Task 7: `CraftWorkingCopy` and the fourteen primitives (large, gate: task 5)

Spec 10.1 and 10.2. Gated on task 5 because primitive 1 `AddRandomMod` is one pick through spec 9.4 steps 6
to 8 and primitive 4 `RerollMods` re-runs steps 4 to 9, so the crafting framework CONSUMES the generator
rather than duplicating a draw.

**Files:**

- Create: `KhaozEngine.ItemInstances/Crafting/CraftWorkingCopy.cs`
- Create: `KhaozEngine.ItemInstances/Crafting/CraftPrimitive.cs`
- Create: `KhaozEngine.ItemInstances/Crafting/CraftRefusal.cs`
- Create: `KhaozEngine.ItemInstances/Crafting/CraftPrimitives.Affixes.cs`
- Create: `KhaozEngine.ItemInstances/Crafting/CraftPrimitives.Sockets.cs`
- Create: `KhaozEngine.ItemInstances/Crafting/CraftPrimitives.Scalars.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Crafting/CraftWorkingCopyTests.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Crafting/CraftPrimitiveTests.cs`

**Interfaces:**

- Consumes: `ItemInstancePayload.TryDecode`, `ItemInstancePayloadBuilder`, `InstancePropertyRegistry`,
  `InstancePropertyKind`, task 4's `ModCandidateTables`, task 5's draw, `IContentSnapshot`
- Produces: `CraftWorkingCopy`, `CraftPrimitive` (the closed enum of fourteen), `CraftRefusal`, and one
  static apply method per primitive

- [ ] **Step 1: Write the all-or-nothing facts FIRST, because they are the whole shape.**

~~~csharp
[Fact] public void A_craft_decodes_into_a_BUILDER_and_never_patches_a_stored_byte()
[Fact] public void A_refusal_at_any_step_discards_the_builder_and_the_durable_bytes_are_untouched()
[Fact] public void There_is_no_partial_craft_and_therefore_no_rollback_path()
[Fact] public void The_re_encoded_payload_is_canonical_because_the_builder_made_it_so()
[Fact] public void A_working_copy_cannot_write_an_UNREGISTERED_property_kind()
[Fact] public void A_working_copy_cannot_exceed_MaxInstancePayloadBytes()
[Fact] public void A_working_copy_cannot_allocate_an_instance_id()
[Fact] public void An_unknown_kind_the_decode_preserved_survives_a_craft_verbatim()
~~~

  The eighth is the one that is easy to lose. Contracts 9.4 keeps an unknown kind verbatim INCLUDING its
  position in the ordering, and a craft that round trips through a builder must carry it through untouched,
  or a client built against build N+1 loses a field the moment build N crafts the item.

- [ ] **Step 2: Write one fact per primitive, fourteen of them, plus the four notes spec 10.2 says an
  implementer would otherwise guess.**

~~~csharp
[Fact] public void SetRarity_trims_from_the_END_of_the_sorted_list_so_a_replay_needs_no_draw()
[Fact] public void SetRarity_fills_by_repeating_steps_5_to_8_only_when_the_currency_asked_for_a_fill()
[Fact] public void SetRarity_with_parameter_0_walks_upgrade_from_and_has_exactly_one_answer()
[Fact] public void Socket_and_Unsocket_are_the_only_primitives_that_touch_TWO_slots()
[Fact] public void Socket_KEEPS_the_moved_items_instance_id_rather_than_minting_one()
[Fact] public void AddRandomMod_uses_the_items_OWN_item_level_from_kind_2()
[Fact] public void Identify_sets_state_1_and_the_mask_to_the_REGISTERED_gated_bits_only()
[Fact] public void SetFlag_refuses_a_bit_above_2_in_v1()
[Fact] public void Repair_with_amount_0_raises_kind_5_current_to_its_maximum_and_no_further()
[Fact] public void A_socket_primitive_refuses_a_contained_item_the_socket_types_tag_rules_reject()
[Fact] public void A_socket_primitive_refuses_a_nested_payload_above_socket_type_max_nested_bytes()
[Fact] public void A_nested_payload_is_ONE_LEVEL_and_a_socket_inside_a_socket_is_refused()
~~~

  **`Identify` is `OWNER DECISION`-adjacent and the spec contradicts itself.** Spec 12.7 says the primitive
  "sets state 1 and the mask to all ones", and spec 21 plus 12.7's own earlier sentence say bits 4 to 31 are
  unassigned and ZERO in v1. All ones writes 28 reserved bits. The plan's choice is state 1 plus the OR of
  every REGISTERED `identificationMaskBit`, which is `0b1111` in v1, satisfies 12.7's intent, never writes an
  unassigned bit, and is derived from the registration rather than hardcoded. A mask of 0 would be
  behaviourally identical, because `ItemInstanceVisibility.CanSee` consults the mask only when `identified`
  is false. Flag it to the spec owner.

- [ ] **Step 3: Implement `CraftWorkingCopy` as a `ref struct` over a decoded payload plus a builder.** It
  exposes the typed edits the primitives need and NOTHING that could produce a non-canonical payload. The
  four powers a game operation must not have (spec 10.5) are properties of this type rather than rules a
  reviewer enforces: an unregistered kind has no door, the cap is checked at every write, canonical order is
  the builder's, and there is no allocator reference anywhere in the type.
- [ ] **Step 4: Implement the fourteen primitives across three files split by SUBJECT.** Affixes are 1, 2,
  3, 4, 5, 9 and 10. Sockets are 6, 7 and 8. Scalars are 11, 12, 13 and 14. That split is chosen so a file
  grows only when its own subject gains a primitive, which is the KESIZE growth test rather than a line
  count.
- [ ] **Step 5: `AddRandomMod` and `RerollMods` call the GENERATOR's draw**, against the item's own base and
  its own item level from kind 2, and they consume draws from the same `IRandomSource` the executor holds.
  Do not write a second weighted pick. If the draw's shape does not fit through a public surface on
  `ItemGenerator`, extract it there rather than copying it here, and say so in the task report.
- [ ] **Step 6: State the stack cap position in a doc comment rather than leaving it implied.** No primitive
  merges two stacks. A craft rewrites one payload in place and the currency consumption DECREMENTS a stack,
  and a decrement can only shrink, which is the one direction contracts 8.2 kind 4 always permits. That is
  why [#924](https://github.com/APKiwiOrg/KhaozEngine/issues/924) does not block this task and why this task
  does not close it.
- [ ] **Step 7: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~Crafting
git add KhaozEngine.ItemInstances/Crafting KhaozEngine.ItemInstances.Tests/Crafting
git commit -m "iteminstances(crafting): an all-or-nothing working copy and the fourteen primitives"
~~~

---

### Task 8: the guard vocabulary, the selector and the three standing rules (medium, gate: task 7)

Spec 10.3. Three closed vocabularies, and the interesting half is the three STANDING rules, which are
refusals no currency can opt out of and which therefore cannot live in an authored guard set.

**OWNER DECISION 3 is in step 4.**

**Files:**

- Create: `KhaozEngine.ItemInstances/Crafting/CraftGuard.cs`
- Create: `KhaozEngine.ItemInstances/Crafting/CraftGuardKind.cs`
- Create: `KhaozEngine.ItemInstances/Crafting/CraftGuardEvaluator.cs`
- Create: `KhaozEngine.ItemInstances/Crafting/CraftSelector.cs`
- Create: `KhaozEngine.ItemInstances/Crafting/CraftStandingRules.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Crafting/CraftGuardTests.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Crafting/CraftStandingRuleTests.cs`

**Interfaces:**

- Consumes: `CraftWorkingCopy`, `IContentSnapshot`, task 1's `mod` and `mod_tier` rows
- Produces: `CraftGuardKind` (the fifteen), `CraftSelectorKind` (the six),
  `CraftGuardEvaluator.Evaluate(kind, a, b, in copy, snapshot)`, `CraftStandingRules`

- [ ] **Step 1: Write one fact per guard kind, fifteen of them, then the composition facts.**

~~~csharp
[Fact] public void Guards_are_ANDed_and_there_is_no_OR_no_NOT_and_no_nesting()
[Fact] public void A_TARGET_guard_that_fails_refuses_the_whole_craft_and_consumes_NOTHING()
[Fact] public void A_STEP_guard_that_fails_SKIPS_that_step_and_the_craft_continues()
[Fact] public void A_refusal_NAMES_the_guard_kind_rather_than_carrying_a_message()
[Fact] public void RarityIsAtMost_walks_the_upgrade_from_chain_rather_than_comparing_ids()
[Fact] public void HasMod_looks_in_kind_131_AND_kind_133()
[Fact] public void HasTag_asks_the_BASE_rather_than_the_instance()
~~~

  The third is the difference between a precondition and a step guard and it is the reason both exist. Spec
  10.4's worked whetstone repairs an item already at quality 20 precisely because its `QualityBetween(0, 19)`
  step guard skips one step rather than refusing the craft.

- [ ] **Step 2: Write one fact per selector kind, six of them.**

~~~csharp
[Fact] public void ByModId_refuses_when_the_mod_is_absent()
[Fact] public void ByIndex_indexes_the_SORTED_list()
[Fact] public void RandomOfKind_is_the_ONLY_selector_that_draws_and_it_draws_exactly_once()
[Fact] public void LowestTier_and_HighestTier_break_a_tie_by_LOWER_mod_id()
[Fact] public void A_currencys_draw_count_is_a_function_of_its_STEP_LIST_not_of_the_item_it_hits()
~~~

  The fifth is the selector vocabulary's whole reason for being closed, and it is the same reproducibility
  property spec 9.3 gives the generator.

- [ ] **Step 3: Implement `IsCorruptible` as STANDING.** Every primitive that writes any part of the payload
  refuses an item whose kind 1 bit 0 is set, whatever the currency's guard set says, because "corrupted"
  means "cannot be modified further" and a refusal a currency can FORGET is not that. It stays in the
  vocabulary because the working copy reports it by kind when it refuses, and because an authored
  `IsCorruptible` on a step is a legal, redundant way for an author to document intent. The worked whetstone
  of spec 10.4 deliberately does NOT author it.
- [ ] **Step 4: Implement the FROZEN LEGACY ENTRY rule, which is a SECOND standing rule and not a
  restatement of the first. OWNER DECISION 3.** No primitive and no game operation rewrites any part of an
  affix entry whose mod row carries `legacy`: not its roll position, not its tier, not its flags.
  `RerollValues` skips it, `SetRarity`'s trim and fill leave it where it is and COUNT it against the rule's
  limits, and an `ICraftOperation` that touches kind 131 or 133 is handed a working copy that refuses the
  write. The reason is in contracts 5.4 and spec 10.3 and it is worth restating in the code comment: an
  earlier draft scoped the refusal to primitives that ADD a mod, which left `RerollValues` free to draw a
  fresh position against a legacy tier's preserved range until it sat at 65,535, so the mechanism chosen to
  FREEZE old rolls would have become a farm for them. The cost is real and is the owner's to accept: an item
  with one legacy affix can never have its OTHER affixes rerolled by a currency authored as
  `RerollValues(ByIndex)` on that entry, which simply refuses.
- [ ] **Step 5: Implement `NotLegacy` in both its forms.** As a STANDING rule, every primitive that would
  ADD a mod refuses a legacy row regardless of the currency's guard set. As an AUTHORED guard, it is the
  stronger statement that the item must carry NONE at all. Two facts, because an implementer who writes one
  of them believes they have written both.
- [ ] **Step 6: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~Craft
git add KhaozEngine.ItemInstances/Crafting KhaozEngine.ItemInstances.Tests/Crafting
git commit -m "iteminstances(crafting): the guard vocabulary, the selector and the three standing rules"
~~~

---

### Task 9: `CraftPlan`, `CraftingRegistry` and currency resolution (large, gate: tasks 3 and 8)

Spec 10.4 and 10.5. The piece that turns three content types into something executable, plus the seam a game
reaches its own exotic operations through.

**OWNER DECISION 7 is settled in step 5** and is recorded only so it is not re-opened.

**Files:**

- Create: `KhaozEngine.ItemInstances/Crafting/CraftPlan.cs`
- Create: `KhaozEngine.ItemInstances/Crafting/CraftPlanIndex.cs`
- Create: `KhaozEngine.ItemInstances/Crafting/CraftOutcome.cs`
- Create: `KhaozEngine.ItemInstances/Crafting/CraftExecutor.cs`
- Create: `KhaozEngine.ItemInstances/Crafting/CraftingRegistry.cs`
- Create: `KhaozEngine.ItemInstances/Crafting/ICraftOperation.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Crafting/CraftPlanTests.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Crafting/CraftingRegistryTests.cs`

**Interfaces:**

- Consumes: task 3's three currency types, `IContentLoadIndex`, `IContentSnapshot`, `IRandomSource`,
  task 4's `ModCandidateTables`, tasks 7 and 8
- Produces: `CraftPlan`, `CraftPlanIndex` (the `IContentLoadIndex` on `crafting_currency`), `CraftOutcome`,
  `CraftExecutor`, `CraftingRegistry`, `ICraftOperation`

- [ ] **Step 1: Write the resolution facts.**

~~~csharp
[Fact] public void A_currency_resolves_into_a_plan_ONCE_at_boot_and_never_inside_a_tick()
[Fact] public void A_plans_steps_are_in_SORT_order_and_a_duplicate_sort_never_reached_the_pack()
[Fact] public void A_guard_with_no_currency_step_id_is_a_TARGET_guard()
[Fact] public void A_guard_naming_a_step_is_evaluated_immediately_before_THAT_step()
[Fact] public void An_operation_below_1024_resolves_to_a_PRIMITIVE_and_1024_or_above_to_the_registry()
[Fact] public void A_selector_parameter_occupies_TWO_slots_its_kind_then_its_parameter()
[Fact] public void A_currency_consuming_NO_definition_carries_no_currency_fields_at_all()
~~~

- [ ] **Step 2: Write the registry facts, which are `InstancePropertyRegistry`'s shape one level over.**

~~~csharp
[Fact] public void Registration_runs_at_process_start_and_a_later_Register_THROWS()
[Fact] public void The_registry_freezes_at_the_FIRST_pack_load()
[Fact] public void An_operation_whose_Id_is_below_1024_is_refused_by_the_REGISTRY_itself()
[Fact] public void An_operation_that_ROLLS_took_its_IRandomSource_in_ITS_OWN_constructor()
[Fact] public void Apply_has_NO_random_parameter_which_is_what_makes_the_previous_fact_checkable()
[Fact] public void The_registry_is_PER_INSTANCE_and_never_a_static_so_no_test_needs_a_collection()
[Fact] public void A_game_operation_that_writes_an_unregistered_kind_THROWS_at_encode()
[Fact] public void A_game_operation_cannot_allocate_an_instance_id()
~~~

  The sixth is the #349 rule applied before it bites. A static registry would be process-global state that
  every crafting test writes, which would force a `DisableParallelization` collection on the whole suite.
  Per instance costs one constructor argument and keeps the suite parallel, and it is what
  `ContentTypeRegistry` and `InstancePropertyRegistry` both already do.

- [ ] **Step 3: Implement `CraftPlanIndex` as an `IContentLoadIndex` on `crafting_currency`.** It reads its
  own rows plus `currency_step` and `currency_guard` through the snapshot, resolves each currency into an
  immutable `CraftPlan`, and throws to fail the boot closed. Same argument as task 4's tables: a lazy build
  inside a tick is a latency spike, and a plan derived from content is immutable for the version.
- [ ] **Step 4: Implement `CraftExecutor`, which the spec does not name and which has to exist.** Spec 10
  gives the primitives, the guards, the plan and the registry, and never says what runs them.

~~~csharp
public sealed class CraftExecutor
{
    public CraftExecutor(
        IContentSnapshot snapshot,
        ModCandidateTables tables,
        CraftingRegistry operations,
        IRandomSource random);

    public CraftOutcome Apply(in CraftPlan plan, ref CraftWorkingCopy copy);
}
~~~

  It holds the `IRandomSource` by constructor for the same reason `ItemGenerator` does, and it is the ONE
  place a target guard set, the ordered steps, each step's guard set and a game operation are composed.
  Record the choice, because it is a public type the spec leaves unnamed.

- [ ] **Step 5: Refuse an unregistered game operation AT USE, with a counter.** OWNER DECISION 7's default
  and spec 10.5's deliberate softening of contracts 10.5's fail-closed rule. The reason is that the rule is
  about a missing CONTENT VERSION rather than about a code registration: a server that shipped without an
  operation is a deploy mismatch, which is loud at the first use and would be a boot failure for every
  player if it were fail-closed at boot. The refusal token is `operation-unregistered`.
- [ ] **Step 6: Author ONE currency as a test fixture, composing at least four primitives with guards**,
  which is spec 20 phase 5's acceptance clause. Build it on spec 10.4's whetstone shape and extend it,
  deliberately NOT as a PoE currency: a target guard, four steps of which one is guarded and skipped, and a
  consumed definition. Assert the skip, the refusal and the resulting payload.
- [ ] **Step 7: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~Craft
git add KhaozEngine.ItemInstances/Crafting KhaozEngine.ItemInstances.Tests/Crafting
git commit -m "iteminstances(crafting): resolved plans, the operation registry and the executor"
~~~

---

### Task 10: the craft journal operation, the `item-crafted` body and its reader (medium, gate: task 9)

Spec 10.6, 15.1, and test plan rows 10 and 13.

**OWNER DECISION 2 is step 2.** Spec 10.6's normalized intent names container IDs and the shipped
`ContainerOperation.WriteCanonical` names container NAMES, and one of the two has to win before a craft
message crosses a wire ([#942](https://github.com/APKiwiOrg/KhaozEngine/issues/942)).

**Files:**

- Create: `KhaozEngine.ItemInstances.Journal/ItemCraftedEvent.cs`
- Modify: `KhaozEngine.ItemInstances.Journal/ContainerOperation.cs` (only if OWNER DECISION 2 goes the other
  way)
- Modify: `KhaozEngine.ItemInstances.Journal/README.md`
- Create: `KhaozEngine.Server.Tests/ItemInstances/CraftReplayTests.cs`
- Create: `KhaozEngine.Server.Tests/ItemInstances/NoClientPayloadRouteTests.cs`

**Interfaces:**

- Consumes: `ContainerCommitBuilder`, `ContainerOperation.Craft`, `ItemInstanceEvents.CraftActionKind`,
  `JournalCommit`, a real SQLite store
- Produces: `ItemCraftedEvent.Write` and `TryRead`

- [ ] **Step 1: Write spec 17 row 10 against a REAL SQLite store**, the way
  `MutationJournalStoreConformance` already does.

~~~csharp
[Fact] public void The_same_operation_id_and_the_same_intent_replays_to_the_ORIGINAL_receipt()
[Fact] public void The_same_operation_id_with_a_REFILLED_slot_is_OperationConflict()
[Fact] public void A_REFUSED_craft_writes_nothing_durable_and_never_reaches_the_journal()
[Fact] public void A_craft_that_consumes_a_currency_from_a_SECOND_page_is_ONE_commit_two_writes()
~~~

  The second is spec 15.1's exploit closed. The target's INSTANCE ID is the load bearing field of the
  intent: without it a replayed craft whose slot has since been refilled by a different item hashes
  identically and applies to the wrong item.

- [ ] **Step 2: Reconcile the intent. OWNER DECISION 2.** The plan's default is to ADOPT THE SHIPPED SHAPE
  and correct spec 10.6, because a container's identity on this path is its NAME, which is what
  `ContainerSectionNames.Format` files its pages under, and a numbering invented here becomes durable data
  every consumer then owns. Whichever way the owner answers, ONE encoding wins and the other is deleted,
  because two intent shapes for one action kind is a conflicting-replay bug in waiting. Close #942 with the
  decision recorded, whichever it is.
- [ ] **Step 3: Implement the `item-crafted` body exactly as spec 10.6 writes it**, carrying BOTH the before
  and the after payload. About 127 bytes on a rare, of which the before costs 59. It is worth every one of
  them: the page is rewritten whole on the next commit, so without the before bytes nothing in the durable
  record can answer what a craft CHANGED, which is exactly the failure a consumer audit already has where
  rows record that something changed and carry no values.
- [ ] **Step 4: Write the READER beside it**, total, answering false plus a reason. That closes the Scope B
  half of [#941](https://github.com/APKiwiOrg/KhaozEngine/issues/941) for the two bodies this plan writes,
  and the five container-operation bodies stay open on that issue. Update the journal package README's
  paragraph saying nothing reads one back, because after this task that sentence is wrong for two of the
  seven.
- [ ] **Step 5: Write spec 17 row 13, the architecture test, in its FULL form now that the craft messages
  exist.** Phase 3 landed the narrow version over the resync request and the take request. The full version
  asserts that no client-to-server message in this design carries a payload-shaped field and that every one
  of them names an item by ID. That is spec 15.3's invariant and it is what makes the server the only thing
  that ever writes instance bytes.
- [ ] **Step 6: Measure nothing new here.** Budget 4 is already measured at one commit and this task must
  not move it. Assert the COMMIT COUNT in the two-page craft fact and leave the byte number to the
  benchmark, so a structural regression is a red test rather than a slower number nobody reads.
- [ ] **Step 7: Run green and commit.**

~~~bash
dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~ItemInstances
git add KhaozEngine.ItemInstances.Journal KhaozEngine.Server.Tests/ItemInstances
git commit -m "iteminstances(journal): the item-crafted body, its reader and the craft replay facts"
~~~

**Group C acceptance:** spec 17 rows 10 and 13 green, one authored currency composing at least four
primitives with guards, and the three standing rules each pinned by their own fact.

---

## Group D: the stat evaluation base (spec 11)

### Task 11: `ContentStatEvaluator`, the fold and budget 6 (large, gate: Scope A registry)

Spec 11.1 to 11.6 over contracts 13.2. **LIFT FROM THE SPIKE:**
`KhaozEngine.Benchmarks/Items/ContentStatEvaluator.cs` (169 lines) is contracts 13.2's formula through spec
11.6's eight steps with the per-stat inverted index and no allocation on the read path, measured at 444 ns
with 0 bytes. Two things change on the way in: `int statCount` becomes `IContentSnapshot`, so `min`, `max`
and `scale` come from the `stat` rows rather than from constructor defaults, and the tag scope gains the
stat row's own tags.

**This task does NOT modify `KhaozEngine.Stats`.** `StatModifier(int Channel, float Flat, float Percent)` is
a shipped struct and adding a `More` kind to it is a breaking change to a released type. `StatSet` stays,
unchanged, beside this, and a game uses one or the other per stat and never both.

**Files:**

- Create: `KhaozEngine.ItemInstances/Stats/ContentStatEvaluator.cs`
- Create: `KhaozEngine.ItemInstances/Stats/ContentStatEvaluator.Fold.cs`
- Create: `KhaozEngine.ItemInstances/Stats/StatModifierLine.cs`
- Create: `KhaozEngine.ItemInstances/Stats/StatSourceKey.cs`
- Create: `KhaozEngine.ItemInstances/Stats/StatContext.cs`
- Create: `KhaozEngine.ItemInstances/Stats/IStatConditionRegistry.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Stats/ContentStatEvaluatorTests.cs`
- Modify: `KhaozEngine.Benchmarks/Items/ItemsWorkMeasurements.cs` (budget 6, `OWNER DECISION 5`)

**Interfaces:**

- Consumes: `IContentSnapshot`, `StatContentType`'s rows, `EngineContentTypes.StatTypeKey`
- Produces: `ContentStatEvaluator`, `StatModifierLine`, `StatCombineKind`, `StatSourceKey`, `StatContext`,
  `IStatConditionRegistry`

- [ ] **Step 1: Write spec 17 row 7 first, all three claims, and the rounding case by name.**

~~~csharp
[Fact] public void The_More_fold_ORDER_changes_the_answer_and_the_stated_order_is_stable()
[Fact] public void Add_then_remove_restores_the_prior_value_EXACTLY()
[Fact] public void AddSource_under_an_existing_key_REPLACES_in_place_and_keeps_its_position()
[Fact] public void Floor_division_rounds_minus_14000_to_minus_1_where_Csharp_slash_gives_0()
[Fact] public void Every_divide_in_the_fold_is_floor_division_for_EVERY_sign()
[Fact] public void Intermediate_arithmetic_is_long_and_the_result_is_CHECKED_into_int_before_the_clamp()
[Fact] public void A_pathological_modifier_set_saturates_at_the_clamp_rather_than_overflowing()
~~~

  **The fourth is the whole reason this test row exists and the trap is silent.** The numerator is
  `flat * increased = -14000`, a penalty of 1.4 scaled units, and `floordiv(-14000 + 5000, 10000)` is
  `floordiv(-9000, 10000)` which is `-1`, the nearest integer with the tie going up, where C# `(-9000) /
  10000` gives `0` and reports NO penalty at all. Negative values are ordinary here, because a `Flat`
  modifier may be negative and a stat's `min` may sit below zero. The implementation is `Math.DivRem` with a
  negative-remainder adjustment, NEVER `Math.Round`, which takes a `double` and would put floating point on
  the determinism path contracts 13.4 exists to keep clear of it.

  **Spec 17's own note says test 7 must run once with `-c Release` before merging**, because the overflow
  behaviour differs under a `Debug.Assert` rescue and CI tests Release while a local `dotnet test` runs
  Debug. Pin a configuration-independent observable, the clamped return value, and put any Debug-only
  escalation under `#if DEBUG` in the test itself.

- [ ] **Step 2: Write the scope and condition facts, and resolve the two places spec 11 contradicts
  itself.**

~~~csharp
[Fact] public void A_scope_matches_the_UNION_of_the_context_tags_and_the_STAT_ROWS_own_tags()
[Fact] public void Increased_fire_resistance_applies_with_NO_context_tag_because_the_stat_carries_fire()
[Fact] public void An_EMPTY_scope_applies_always_and_costs_one_length_check()
[Fact] public void A_condition_id_of_0_is_unconditional_and_the_engine_defines_NONE_in_v1()
[Fact] public void The_engine_never_calls_the_registry_for_an_id_in_its_own_0_to_1023_range()
[Fact] public void The_condition_registry_arrives_by_CONSTRUCTOR_and_not_on_StatContext()
~~~

  **Contradiction one:** spec 11.6's algorithm step 2 says "Drop every line whose tag scope is not a subset
  of context.Tags", and spec 11.3's prose says the match is against the UNION of the context's tags and the
  `stat` row's own `tags`. The algorithm block is stale relative to the prose, and the prose is the half
  that has a reason attached: without the stat row's half, `stat.tags` is dead weight in every stat row and
  every client pack, and the caller has to pass `fire` on every evaluation of fire resistance, which means
  every call site knows the taxonomy. Take 11.3 and flag 11.6's step 2 for correction.

  **Contradiction two:** spec 11.2 declares `StatContext(ReadOnlyMemory<int> Tags, int ConditionMask,
  IStatConditionRegistry? Conditions)` and spec 11.6 declares `ContentStatEvaluator(IContentSnapshot
  snapshot, IStatConditionRegistry? conditions = null)`. One dependency, two homes, and two homes is how two
  call sites end up with two registries. Take the CONSTRUCTOR, which is contracts 14.4's shape for every
  injected seam in this design, and drop `Conditions` from `StatContext`.

- [ ] **Step 3: Implement the fold in `ContentStatEvaluator.Fold.cs`, contracts 13.2's eight steps
  verbatim.** `flat = Base + sum(Flat)` in `long`, `increased = 10000 + sum(IncreasedBasisPoints)`,
  `value = floordiv(flat * increased + 5000, 10000)`, then each `More` in the step 1 order, then the clamp.
  **Section 6.4's roll formula is UNAFFECTED and keeps its `/`**: `position` is a `ushort` and `max - min`
  is non-negative by the tier's own bounds, so its numerator is never negative and floor and truncation
  agree on every input it can be given. Do not "fix" it.
- [ ] **Step 4: Fix the fold order as `(SourceKind, Ordinal, InstanceId, ModifierIndex)`, always.** Integer
  multiplication with rounding at each step is NOT associative, so `(a * x) * y` and `(a * y) * x` can
  differ by one unit, which means the order IS the displayed number and is in spec 21's expensive table. The
  instance id is in the key so two sources that somehow tie on kind and ordinal still order, which cannot
  happen through the engine's four source kinds and can happen through a game source.
- [ ] **Step 5: Implement the per-stat inverted index and the lazy per-stat recompute.** A read of a dirty
  stat refolds that stat alone and caches. What is NOT carried across from `StatSet` is the linear source
  scan, which is O(sources) per add or remove and is fine at a handful of sources and not fine at eleven
  worn items with six affixes and six sockets each. `Recompute(StatSourceKey changed)` marks dirty and
  nothing else. **A source changes on exactly five events**: equip, unequip, socket, unsocket, and a craft
  that rewrites a worn item's payload. A tick does not, a movement does not, and a condition changing state
  does not unless the game says so.
- [ ] **Step 6: Reserve source kinds 5 and 6 and assign neither.** Passives and buffs. A later passive tree
  adds a source kind and changes no fold, no format and no stored number, and reassigning one of those two
  later moves every value that tree produces. That is why this is a BASE rather than a system.
- [ ] **Step 7: Hold budget 6's zero-allocation half as a property of the SIGNATURE.** `Value(int statId, in
  StatContext context)` and `CopyValuesTo(ReadOnlySpan<int> statIds, Span<int> destination, in StatContext
  context)` write into a caller span and allocate nothing, and every working array is built when a source is
  ADDED. Assert it with `GC.GetAllocatedBytesForCurrentThread()` around a read, and note in the class doc
  that an evaluation which allocates per attack is an evaluation that runs per attack. This test writes no
  process-global state, so it needs no `DisableParallelization` collection.
- [ ] **Step 8: Re-point budget 6 at the shipped evaluator. OWNER DECISION 5.**
  `ItemsWorkMeasurements.MeasureStatEvaluation` measures eleven worn items with six affixes each, about 140
  lines, and the measured number is 444 ns and 0 bytes against a 2 us and 0 byte target. Zero allocation is
  the binding half and it must stay exactly 0.
- [ ] **Step 9: Run green in BOTH configurations and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj --filter FullyQualifiedName~ContentStatEvaluator
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~ContentStatEvaluator
git add KhaozEngine.ItemInstances/Stats KhaozEngine.ItemInstances.Tests/Stats KhaozEngine.Benchmarks/Items/ItemsWorkMeasurements.cs
git commit -m "iteminstances(stats): the integer evaluator, floor division and the fixed fold order"
~~~

---

### Task 12: `InstanceStatLines`, a payload plus content into lines (medium, gate: tasks 1 and 11)

Spec 11.4, 8.4 and contracts 6.4. **This task closes a gap spec 11 leaves open and never names.** Section 11
gives the evaluator and the value types and the source order, and nothing anywhere says WHO turns an item's
affixes into `StatModifierLine`s. Without it the evaluator is a fold with no input and the two consumer
mappings of spec 11.7 have nothing to map onto.

**Files:**

- Create: `KhaozEngine.ItemInstances/Stats/InstanceStatLines.cs`
- Create: `KhaozEngine.ItemInstances/Stats/InstanceStatSourceKind.cs`
- Create: `KhaozEngine.ItemInstances.Tests/Stats/InstanceStatLinesTests.cs`

**Interfaces:**

- Consumes: `ItemInstancePayload` field walk, `InstancePropertyKind.Affixes`, `Enchantments`, `Sockets`,
  task 1's `mod_tier` and `stat_line` rows, `IContentSnapshot`
- Produces: `InstanceStatLines.Build(payload, snapshot, wornSlot, destination)` filling a caller span

- [ ] **Step 1: Write the facts, which are spec 11.4's table read as assertions.**

~~~csharp
[Fact] public void ONE_roll_position_drives_EVERY_line_on_the_tier()
[Fact] public void A_two_line_tier_resolves_both_from_the_SAME_stored_position()
[Fact] public void The_value_is_contracts_6_4s_formula_and_there_is_exactly_ONE_copy_of_it()
[Fact] public void sort_is_what_makes_the_tiers_second_line_a_stable_phrase_across_a_republish()
[Fact] public void Source_kind_1_is_the_worn_slot_index_ascending()
[Fact] public void Source_kind_2_is_worn_slot_then_affix_index_in_the_SORTED_list()
[Fact] public void Source_kind_4_is_worn_slot_then_socket_index_in_AUTHORED_order()
[Fact] public void A_player_who_rearranges_two_gems_can_move_a_displayed_value_by_ONE_unit()
[Fact] public void Flat_is_in_the_stats_SCALED_UNITS_and_Increased_and_More_are_in_BASIS_POINTS()
[Fact] public void Build_writes_into_a_caller_span_and_allocates_nothing()
~~~

  The eighth is correct behaviour rather than a bug, and it is the price of integer rounding being honest.
  Socket order is AUTHORED and never sorted, so kind 4's ordinal is the authored index, and a fact that
  pins the one-unit move is what stops someone "fixing" it by sorting.

  The third is the constraint that matters most across the whole plan. Contracts 6.4's formula is
  `value = min + (int)(((long)position * (max - min) + 32767) / 65535)`, and it must exist exactly once in
  the tree. If the generator, a tooltip helper and this builder each carry a copy, they will disagree the
  first time one is touched.

- [ ] **Step 2: Implement the walk.** For each affix entry of kind 131 and each enchantment of kind 133,
  resolve `(mod id, tier ordinal)` to its `mod_tier` row, read that tier's `stat_line` rows in `sort` order,
  and emit one `StatModifierLine` per line with the value resolved through contracts 6.4 from the entry's
  stored position. For each socket of kind 132 carrying a nested payload, recurse ONE level and emit the
  contained item's lines at source kind 4.
- [ ] **Step 3: Share the tag scope array rather than allocating per line.** `StatModifierLine` carries
  `TagScopeStart` and `TagScopeLength` into ONE shared array the evaluator owns, so a source with eight
  lines allocates one array and eight structs. That is the allocation shape `StatSet.AddSource` already uses,
  one level tighter, and it is what keeps budget 6 at zero on the read path.
- [ ] **Step 4: Emit NOTHING for a gated kind the viewer may not see, and say why in the doc comment.** The
  evaluator runs SERVER side over the true payload, so this builder is not a visibility boundary and must not
  become one. A tooltip that wants the player's view asks `ItemInstanceVisibility.PublicView` first and
  builds lines from the projection, which is the same one function the replication filter uses. Two answers
  to "what does this item give me" is exactly what spec 12.5 exists to prevent.
- [ ] **Step 5: Run green and commit.**

~~~bash
dotnet test KhaozEngine.ItemInstances.Tests/KhaozEngine.ItemInstances.Tests.csproj -c Release --filter FullyQualifiedName~InstanceStatLines
git add KhaozEngine.ItemInstances/Stats KhaozEngine.ItemInstances.Tests/Stats
git commit -m "iteminstances(stats): one roll position into the lines a tier grants"
~~~

**Group D acceptance, which with group C is spec 20 phase 5's own:** spec 17 row 7 green in BOTH Debug and
Release, budget 6 measured against the shipped evaluator at 0 bytes allocated, and contracts 6.4's roll
formula present exactly once in the tree.

---

## Group E: documentation and release

### Task 13: the full documentation sweep (medium, gate: tasks 1 to 12)

AGENTS.md's "Full doc sweep on EVERY feature" rule. `scripts/check-doc-versions.sh` verifies the
engine-version declarations, the newest changelog heading and the package INVENTORY. What it does NOT check
is whether any of that prose is CORRECT, so a stale catalog row or a package README describing removed API
sails through. This task is the content accuracy half.

**No package is added by this plan**, so the inventory check cannot go red the way it could in phases 2 and
3. What CAN go red is every existing README that describes a gap this plan closed.

**Files:**

- Modify: `KhaozEngine.ItemInstances/README.md`
- Modify: `KhaozEngine.ItemInstances.Journal/README.md`
- Modify: `KhaozEngine.Catalog/README.md`
- Modify: `README.md` (the package table's two `ItemInstances` rows and the `KhaozEngine.Catalog` row)
- Modify: `docs/USING-KHAOZENGINE.md`
- Modify: `docs/DEPENDENCY-SEAMS.md`

- [ ] **Step 1: Rewrite `KhaozEngine.ItemInstances/README.md`'s "What this release does not ship" table**,
  which is the single most wrong paragraph in the tree after this plan lands. Two of its six rows go away
  entirely ("the affix content types and the item generator" and "the crafting framework and the content
  stat evaluator"), the `ContainerOperationKind.Craft` sentence about the event body being the crafting
  framework's is now DONE, and #917, #924, #932 and #933 stay exactly as they are. Do not delete a row for
  an issue that is still open.
- [ ] **Step 2: Add the four new sections to that README**, in the order the file already reads (record,
  container, wire, and now content, generation, crafting and stats). The eighteen content types with their
  ids and their parents, the candidate table shape and what it costs, the generator's thirteen ordered steps
  and the reproducibility contract, the fourteen primitives with the three standing rules, and the evaluator
  with its fold order and its floor division. Each is a section a reader lands on from NuGet with no design
  doc beside them.
- [ ] **Step 3: Correct `KhaozEngine.ItemInstances.Journal/README.md` in three places.** The paragraph
  saying "the payload codecs for `item-generated` and `item-crafted` arrive with the generator and the
  crafting framework that emit them" is now describing shipped code. The paragraph saying "these events are
  written and nothing here reads one back" is wrong for two of the seven. The paragraph about the container
  name versus the container id carries `OWNER DECISION 2`'s answer and cites the closed issue.
- [ ] **Step 4: Correct `KhaozEngine.Catalog/README.md`'s one sentence about the `KEC0100` band**, which
  says the band "runs INSIDE the sweep after pass 5" and must now say HOW an Instances-band registration
  reaches it, per `OWNER DECISION 6`. That is a Scope A README and this plan changed a Scope A behaviour, so
  the edit belongs here rather than in a catalog plan.
- [ ] **Step 5: Extend `docs/USING-KHAOZENGINE.md`'s existing item instances section.** Carry the worked
  example on through registering the eighteen types, building the tables at boot, rolling an item, crafting
  it and evaluating a stat off it. State explicitly what is NOT here: consumer adoption is spec 18 and 19 and
  runs per game.
- [ ] **Step 6: Add the seams to `docs/DEPENDENCY-SEAMS.md`.** Three edges to record and one that
  deliberately did not change: `ItemInstances` gained content types, a generator, a crafting framework and a
  stat evaluator and gained NO package reference, `ItemInstances.Journal` gained two event bodies and no new
  dependency, `KhaozEngine.Catalog` gained the pass 6 wiring and still references only `Primitives`, and
  **`KhaozEngine.Stats` is untouched and `ContentStatEvaluator` deliberately does not live in it**. That last
  one is the interesting entry, because it is a seam that was deliberately not crossed for a layering reason
  spec 11.1 gives.
- [ ] **Step 7: Do the mechanical check before committing.** Grep every new type, content type key, finding
  code and constant name across ALL `*.md` recursively (root, `docs/`, `docs/design/`, and every per-package
  `<Package>/README.md`) plus `AGENTS.md`, and confirm every place that should mention it does.
- [ ] **Step 8: Do NOT edit any file under `docs/design/`.** The specs are the record of the reasoning and
  this plan does not restate them. Every spec correction this plan found is a GitHub issue in task 14 step 5,
  not an edit here, because a design doc edited by an implementer stops being the record of what was decided
  at the time.
- [ ] **Step 9: Run the guards and commit.**

~~~bash
scripts/check-doc-versions.sh
scripts/check-dashes.sh --tree
scripts/check-prose.sh --tree
scripts/check-file-size.sh --tree
git add README.md docs/USING-KHAOZENGINE.md docs/DEPENDENCY-SEAMS.md KhaozEngine.ItemInstances/README.md KhaozEngine.ItemInstances.Journal/README.md KhaozEngine.Catalog/README.md
git commit -m "docs(items): the content types, the generator, crafting and the stat evaluator"
~~~

  All four guards must exit 0 here. A `check-doc-versions.sh` complaint about the version line means task 14
  has not run yet and is expected. Anything else is a real miss in this task.

---

### Task 14: the finishing ritual, one version bump, no tag (medium, gate: task 13)

AGENTS.md's finishing ritual, in order. **ONE version bump for the whole batch**, not one per task and not
one per phase. Phases 4 and 5 share a package set, so they share a bump.

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
  `<KhaozEngineVersion>` line collides constantly here, local `main` is routinely ahead of `origin`, and the
  Scope A catalog work is landing in parallel with this plan by construction.

- [ ] **Step 2: Read the version and the tags on the up-to-date main, then take the next FREE version.**

~~~bash
git tag --list 'v*' --sort=-v:refname | head
grep -n '<KhaozEngineVersion>' Directory.Build.props
~~~

  If `<KhaozEngineVersion>` is AHEAD of the newest tag, a version is in flight: RIDE it. Append to that
  staged version's changelog entry, roll its date, and do NOT bump. If nothing is in flight, cut exactly ONE
  fresh version and take the next free MINOR, because this work is additive. A collision here is
  auto-resolved and needs no asking.
- [ ] **Step 3: Write the changelog entry in the SAME commit as the version bump.** Newest first, detailed,
  with a tight one-line summary as the entry's FIRST sentence. The first sentence is: `The eighteen affix
  content types, the item generator, the crafting framework and the integer stat evaluator.` Then the
  detail: the eighteen types and their ids, the three whole-`ServerOnly` weight types and why, the
  `KEC0100` band and the pass 6 wiring, the banded candidate tables with the precomputed overlap,
  `ItemGenerator` and its thirteen ordered steps, the `item-generated` body and its reader, the fourteen
  primitives, the fifteen guards, the six selectors, the three standing rules, `CraftPlan`,
  `CraftingRegistry`, `CraftExecutor`, the `item-crafted` body carrying before and after,
  `ContentStatEvaluator` with its fixed fold order and floor division, and `InstanceStatLines`. Name the
  budget numbers that MOVED under `OWNER DECISION 5` and the ones that did not. Name what is still deferred
  in one sentence so a reader does not go looking for consumer adoption that is not there.
- [ ] **Step 4: Update every engine-version declaration the guard checks.** EVERY `<PackageReference>`
  example line in `README.md` and in `docs/USING-KHAOZENGINE.md`, one per umbrella, not just one.
- [ ] **Step 5: Close what this lands and FILE THE SPEC CORRECTIONS.** Close
  [#942](https://github.com/APKiwiOrg/KhaozEngine/issues/942) with `OWNER DECISION 2`'s answer recorded.
  Close the Scope B half of [#944](https://github.com/APKiwiOrg/KhaozEngine/issues/944) only if the
  engine-range half also landed, otherwise leave it open with a comment naming what task 3 did. Leave #917,
  #924, #930, #934 and #941 open, each with a comment saying what this release did and did not change. Then
  file one issue per spec correction this plan found, each `confidence/verified` with the section cited,
  because a spec contradiction discovered by an implementer and left in a plan file is a contradiction the
  next reader rediscovers. The list is the closing table of this plan.
- [ ] **Step 6: Run every guard and the full Release verification.**

~~~bash
scripts/check-doc-versions.sh
scripts/check-dashes.sh --tree
scripts/check-prose.sh --tree
scripts/check-file-size.sh --tree
dotnet test KhaozEngine.slnx -c Release
~~~

  Every command exits zero. If `check-file-size.sh` fires, the fix is a new type and never a hand edit of
  `.filesize-baseline` and never a split at an arbitrary line. If the growth is genuinely legitimate, STOP
  AND ASK the user.

- [ ] **Step 7: Check the feed, then pack it through the guarded script.**

~~~bash
scripts/check-local-feed.sh
scripts/pack-local-feed.sh
~~~

  Never a bare `dotnet pack` into `local-feed`.

- [ ] **Step 8: Commit the version batch with the new version as the scope.**

~~~bash
engine_version=$(sed -n 's:.*<KhaozEngineVersion>\([^<]*\)</KhaozEngineVersion>.*:\1:p' Directory.Build.props)
git add Directory.Build.props CHANGELOG.md README.md docs/USING-KHAOZENGINE.md
git commit -m "items(${engine_version}): affix content, generation, crafting and content stats"
~~~

- [ ] **Step 9: Reconcile once more, fast-forward main, verify and push main right away.**

~~~bash
git fetch --prune
git merge origin/main
dotnet test KhaozEngine.slnx -c Release
git -C ~/KhaozEngine merge --ff-only feature/item-instances-phase4-5
git -C ~/KhaozEngine push origin main
~~~

  If main advanced after the branch merge, merge it into the feature branch and repeat verification before
  the fast-forward. Do not hold the push and do not ask.

- [ ] **Step 10: STOP. Do NOT tag.** A `vX.Y.Z` tag is a separate, deliberate act that the user starts. The
  one sanctioned exception in AGENTS.md is a game pinned-and-waiting on this change. Consumer adoption for
  the stat half (spec 18 row 8 and spec 19) runs AFTER this release, so a game may now be waiting. If one
  really is blocked, say so in the report and let the user start the release.
- [ ] **Step 11: Ask about releasing only if `git worktree list` shows you are the last chat standing**,
  once, as the last line of the report.

**Group E acceptance:** all four guards exit 0, the full solution is green in Release, `local-feed` holds the
new version, `main` is pushed, and no tag exists.

---

## Where this plan CHOSE, because the spec left it open

Each of these is a decision the spec does not make and an implementer would otherwise make silently and
differently. They are consolidated here so a reviewer can overrule one in a single read, and each is
restated at the task that acts on it. The phase 1 and the phase 2-3 plans carry their own tables and none of
the three overlap.

| # | The gap | The plan's choice | Task |
|---|---|---|---|
| 1 | Spec 2.2 never says which PACKAGE owns the eighteen content types | `KhaozEngine.ItemInstances`, under `Content/`, because it is the only package that depends on `Catalog` and is depended on by the generator and the crafting framework that read those rows | 1, 2, 3 |
| 2 | `ContentValidator.RunInstanceBand` says "Scope B ships its own checks here" and `Catalog` cannot reference `ItemInstances` | Pass 6 runs the registration's OWN `IContentValidator` when its band is `Instances`, trusted, with its `KEC01xx` codes passed through unchanged. `OWNER DECISION 6` | 3 |
| 3 | Spec 9.2 keys the packed table entry as `(mod id << 4) \| tier ordinal` and spec 8.3 allows 255 ordinals | Keep the 4 bit packing and make an ordinal above 15 a `KEC0102` publish refusal. `OWNER DECISION 4` | 3, 4 |
| 4 | The spike's `KindCount = 2` cannot express spec 8.2's kinds 3 to 255 | Build the kind list from the `mod` rows present and index buckets by POSITION in that list | 4 |
| 5 | Spec 9.2 assumes a base carries two to four tags and names no ceiling | `MaxGenerationTagPositions = 8`, with a base beyond it refused by a task 3 finding, because the suppression header count is linear in the position count | 4 |
| 6 | Spec 9.2 says a boot builds the tables and names no mechanism | An `IContentLoadIndex` registered against the `mod` type, which is Scope A's own boot step 7b hook and which fails the boot closed by contract | 4 |
| 7 | Spec 9.4's `GenerationResult` has nowhere to report the affix count its own step 8 says is reported | Add `AffixCount` and `RequestedAffixCount`, which the spike already carries one of | 5 |
| 8 | Spec 9.4 step 12's field list omits kind 128, and spec 12.7 requires an unidentified item to carry it | The generator writes kind 128 with state 0 and mask 0, because an item with no `Identification` field is indistinguishable from an identified one under `CanSee` | 5 |
| 9 | Spec 9.5 and 10.6 give two event bodies and no package | Both in `KhaozEngine.ItemInstances.Journal` beside `ItemInstanceEvents`, with a READER each, because that is where every other event body lives and the generator writes no journal byte | 5, 10 |
| 10 | Spec 12.7's `Identify` sets the mask to "all ones" and spec 21 says bits 4 to 31 are zero in v1 | State 1 plus the OR of every REGISTERED `identificationMaskBit`, which is `0b1111` in v1 and never writes an unassigned bit | 7 |
| 11 | Spec 10 names the primitives, the guards, the plan and the registry, and never names what RUNS them | `CraftExecutor(snapshot, tables, operations, random)`, holding its `IRandomSource` by constructor for the same reason the generator does | 9 |
| 12 | Spec 10.4 does not say WHEN a currency's rows become an executable plan | A second `IContentLoadIndex`, on `crafting_currency`, resolving every currency once at boot, because a row walk inside a tick is the latency spike that interface exists to prevent | 9 |
| 13 | Spec 10.6's intent names container IDs and the shipped operation names container NAMES | Adopt the shipped shape and correct spec 10.6. `OWNER DECISION 2`, [#942](https://github.com/APKiwiOrg/KhaozEngine/issues/942) | 10 |
| 14 | Spec 11.2 puts `IStatConditionRegistry` on `StatContext` AND spec 11.6 puts it on the constructor | The CONSTRUCTOR, and `StatContext` drops the member, because contracts 14.4 is the shape for every injected seam here and two homes is how two call sites get two registries | 11 |
| 15 | Spec 11.6 step 2 matches a scope against `context.Tags` alone and spec 11.3 matches the union with the stat row's tags | The UNION, per 11.3, because the alternative makes `stat.tags` dead weight and forces every call site to know the taxonomy | 11 |
| 16 | Spec 11 never says who turns a payload into `StatModifierLine`s | `InstanceStatLines.Build`, which is task 12 and which is the only place contracts 6.4's roll formula is applied outside the generator | 12 |
| 17 | The three Scope B weight types are bare ints, exactly as `loot_entry.weight` was in [#944](https://github.com/APKiwiOrg/KhaozEngine/issues/944) | `KEC0112` for a weight below zero and `KEC0113` for a bucket summing past `int.MaxValue`, because a negative weight makes a prefix array unsearchable | 3 |
| 18 | Spec 20 phase 4's acceptance says the budgets are measured, and the mode measures a spike | Re-point the generation, table and stat phases at the shipped types and re-baseline in one commit. `OWNER DECISION 5` | 6, 11 |

Choices 2, 3, 10, 13, 14, 15 and 18 become GitHub issues in task 14 step 5, because each is a SPEC
correction the owner should adopt or overrule rather than inherit from a plan. Choices 7, 8, 11, 12 and 16
are gaps rather than contradictions and are filed as one issue together. The rest are recorded here and in
their tasks.

## Where the specs contradict each other or the shipped code

Read critically, these are the seven places the documents disagree with themselves or with what 18.51.0
actually shipped. Each is acted on at the task named above, and each is listed separately here because a
reader checking one spec against another deserves to find them in one place.

| # | Where | The disagreement |
|---|---|---|
| 1 | spec 9.2 item 2 against spec 8.3 and spec 3.4 | The candidate table's packed key reserves 4 bits for a tier ordinal the row schema allows 255 values of and the payload stores in a byte |
| 2 | spec 12.7 against spec 12.7 and spec 21 | `Identify` sets the revealed mask to "all ones" in one sentence and bits 4 to 31 are "unassigned and zero in v1" in the sentence before it and in the expensive-decisions table |
| 3 | spec 11.6 step 2 against spec 11.3 | The algorithm block matches a tag scope against the context's tags alone and the prose matches the union with the stat row's own tags |
| 4 | spec 11.2 against spec 11.6 | `IStatConditionRegistry` is declared both as a `StatContext` member and as a constructor parameter |
| 5 | spec 9.4's `GenerationResult` against spec 9.4 step 8 | Step 8 says a short roll is "reported in the result" and the result declared six lines above has no field for it |
| 6 | spec 10.6 against the shipped `ContainerOperation.WriteCanonical` | The craft intent names container IDs and the shipped canonical encoding names container NAMES, with no container id registry anywhere in the tree ([#942](https://github.com/APKiwiOrg/KhaozEngine/issues/942)) |
| 7 | spec 9.4 step 12 against spec 12.7 | The generator's field list omits kind 128 and the unidentified mechanic requires every generated item to carry it |

Two more that are NOT contradictions and read like them, recorded so nobody files them twice. Spec 8.1
assigns `socket_type` to the Instances band at 260 while `KhaozEngine.Catalog`'s shipped
`EngineContentTypes.SocketTypeTypeKey` describes it as a key "Scope B or a game registers a type under",
which agree: the late binding is resolved by task 2. And spec 9.2's claim that a client builds no tables is
not a special case in code, it is what falls out of the three weight types being `ServerOnly`, which is the
property task 4 step 1's tenth fact pins.
