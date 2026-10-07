# World Authoring R2: Sculpted Caves, Sparse Surfaces and Exact Terrain Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give native MapDoc bounded, integer-addressed sculpted surfaces with paired floors and ceilings on any rational lattices, explicit walls, portals, openings and occupied spaces, height-aware support, exact imported terrain, a one-time format advance and atomic terrain transactions, without claiming downstream runtime capabilities.

**Architecture:** Canonical terrain lives in 64-cell patch payloads on rational lattice frames. Every authored vertex has an exact rational lattice address, so any accepted edge subdivision (2 to 64) and any finer lattice maps to the same exact world point. Topology records are anchored in one patch payload and every cross-record reference names its anchor patch, so lookups are bounded reads, never scans. One GPU-free compiler emits collision-free face identities with whole-metre submission anchors. A space's lower and upper bounds may use unrelated rational lattices (a 1/2 m floor under a 1/3 m ceiling is legal): coverage and separation are validated on the exact common refinement of both triangulations clipped to each footprint cell, under declared work and output budgets, with distinct capacity, representability and invalid-input outcomes. Queries run over one immutable acquired scope (`MapScopedSurfaces`), so membership, support and relation results share one snapshot and one factory-only read witness. Storage is writer-owned under `tiles/surfaces/`, content addressed and manifest last, and its completeness joins the existing tile partial flag, so the #1310 stale-window refusal covers surfaces-only windows. Native edits extend the R1 transaction seam.

**Tech Stack:** C# on `net10.0`, System.Numerics, System.Text.Json, `System.Security.Cryptography`, closed JSON Schema, xUnit 2.9.2. No new third-party dependency.

**Spec:** `docs/design/WORLD-AUTHORING-R2-CAVES-SCALE-2026-10-06.md` (CD1 planning basis through 0bd1d69d8), with base contracts C1 to C5, C8 and DG9.1 to DG9.6 in `docs/design/WORLD-AUTHORING-MIGRATION-DESIGN-2026-10-05.md`, and the accepted swimming F3 producer boundary at engine commit 404fa519f, `docs/design/SWIM-ENVIRONMENT-FACADE-F3-2026-10-06.md` (SHA-256 d508ac3b5bed503a30e12e044c91a9a33924f13184efe750961a8c496559f572). F3 is accepted semantic design, not a released runtime API. Executors read all three. The public source register is published beside this plan as `docs/superpowers/plans/2026-10-05-world-authoring-r2-source-evidence.md` and the requirement map as `docs/superpowers/plans/2026-10-05-world-authoring-r2-self-review.md` (root confirms both names at publication). Private oracle provenance never enters this repository (Decision D1).

## Approval and execution gate

- **What is approved.** Coordinator 938c801f-2df0-4298-8bd1-dc39e9015e60, under the owner's delegation OA17, approved the written R2 design through 0bd1d69d8 as the planning basis (CD1). The coordinator also confirmed D1 recommendation B (public synthetic fixtures plus a private exhaustive digest-verified oracle, CD2), ruled D7 on 2026-10-07 (general rational mixed-resolution bounds with bounded exact common refinement and clipping, not the nested integer-ratio restriction v2 proposed), and set the latest provisional planning allowance at 38 to 64 engineer-days. On 2026-10-07 the coordinator also approved, under OA17, atomic footprint-straddle refusal with actionable alignment guidance (D4) and explicitly tagged compatibility-only legacy lower coverage (D9), and accepted root's settlement of D7's work budget: every attempted lower and upper pair is charged against `MaxPairChecks` before bounding-box rejection. These are delegated coordinator decisions, not fresh owner messages, and none is execution approval.
- **What this plan needs.** Full-plan approval is pending root's independent self-review and the coordinator's final plan review. Nothing here authorizes implementation, a build, a release, a tag or game adoption.
- **Execution method.** Subagent-driven-development, serial, one building worker at a time on the shared Mac, a fresh implementer and a fresh task reviewer per task, one whole-branch review at the end. Workers stop at their verified commit. The controller integrates and records.
- **Released prerequisite.** Before Task 1, the controller records the actual owner-released engine release that contains the R1 native APIs: its version, its owner-created tag and the successful tag-run publication of its packages. Ancestry of the `v20.27.0` tag (whose publication failed) or a remembered 20.27.1 status is not evidence. If no owner-released R1 publication exists, execution stops and the coordinator is told.
- **Execution base check.** The controller also records current engine `main`, `origin/main`, every `v*` tag, `<KhaozEngineVersion>`, any staged or in-flight version (the Catalog lane's 20.28.0 candidate was unintegrated and unreleased when this plan was written), and `git diff ca13d62d7 <base> -- KhaozEngine.MapDoc KhaozEngine.MapEditor KhaozEngine.MapEdit.Tool KhaozEngine.Primitives/WorldFrame.cs KhaozEngine.Tests scripts/ci-selective-test.sh KhaozEngine.slnx`. Any drift from the cited symbols is re-planned before dispatch.
- **Execution worktree.** Created only at that gate, from current reconciled engine `main`, as `feature/wa-r2-caves-scale` at `/Users/antonio/KhaozEngine/.worktrees/wa-r2-caves-scale`. Copy forward only the accepted design and this approved plan. Never merge the historical `feature/world-authoring` planning branch. Every dispatch includes `test "$(git branch --show-current)" = "feature/wa-r2-caves-scale"`.
- **Versions.** Format numbers, payload versions and engine versions here are sequence positions, not reservations. The release version is chosen at the release-preparation gate. Only the owner tags.

## Global Constraints

Every task's requirements include these lines.

- One tool, MapEditor GUI plus ke-mapedit MCP, authors terrain, props and buildings. No two-format hybrid, no temporary TileWorld/native hybrid (OA1, OA3).
- Props and buildings keep free position, yaw and positive uniform scale (OA2). Float `MapTransform` stays for placements and rigid local floors.
- Core MapDoc and its runtime packages acquire no TileWorld or Grimhollow dependency. Only `KhaozEngine.MapDoc.Compatibility.Tests` and the out-of-solution `KhaozEngine.MapDoc.OracleHarness.Tests` reference TileWorld.
- Proposed verification envelope: **64 km by 64 km, approximately +/-32,000 m horizontally**, existing datum world Y=0, terrain **+/-500 m**, fixture objects to **+/-564 m**, probes to **+/-640 m**, **128 m** horizontal padding. Acceptance fixture bounds, not caps.
- Runtime local terrain query target **at most 0.0001 m** against the canonical descriptor in the same frame. Imported differential: exact integers, bytes and IDs, **0.00001 m** vertex, **0.000001** normal component. Imported physics raycast tolerance **0.00001 m** stays binding. No target relaxes automatically.
- Named vertical proof risk: float spacing through +/-640 m is **0.00006103515625 m**, final rounding alone can consume **0.000030517578125 m**. A failure is reported with its inputs. The remedy is an owner-reviewed vertical anchor in R3/R8 adapters, never a relaxed target.
- Integer anchor subtraction happens before conversion to float. No absolute large-float round trip. Submission anchors are whole metres.
- **Exact arithmetic.** `MapExactValue` (a reduced `long` over `long`, computed through `Int128` intermediates) is the only exact scalar. A reduced value that does not fit is not representable: queries and acquisition return `NotRepresentable`, document operations refuse with a message containing `not representable`. It is never `CapacityExceeded`, never rounded, snapped or toleranced. No tolerance in this plan changes.
- **Authored versus generated vertices.** Authored vertices are `MapLatticeAddress` values whose reduced denominator is at most 64. Refinement vertices generated by clipping are exact world `MapExactXz` values, never lattice addresses, so the authored denominator bound does not constrain them. Declared seam and strip vertex sequences remain strict: common refinement never waives a matching subdivision.
- Patch maximum **64 by 64 cells**, int32 heights, **1 MiB** canonical patch payload, **65,536** faces per compiled patch. Edge subdivision **2 to 64** segments. Index pages at most **256 entries** and **1 MiB**. Monolithic embedding at most **256 patches** and **8 MiB**. Query budgets **256 candidate patches, 4,096 inspected faces, 64 support intersections**, plus reviewable provider budgets of **32 page reads, 64 record reads and record depth 8**. Refinement budgets per footprint cell **16,384 source faces per bound, 65,536 pair checks, 32,768 refinement faces, 131,072 refinement vertices**. A pair check is every attempted lower and upper polygon pair, charged with checked arithmetic before any bounding-box rejection. Compiled bound-face context limits per validation call, **256 unique bound patches and 262,144 compiled faces**, are proposed operational defaults awaiting coordinator approval, not approved world limits. They are enforced cardinalities counted from cell bytes before any compile, never a byte cap (D7). Query contexts use the query budgets. Overflow returns `CapacityExceeded` with no partial result, never a truncated one.
- **Legacy lower compatibility (D9).** Only a lower bound of the explicit, versioned kind `MapBoundKind.LegacyExteriorV1`, in an `Exterior` space with an `OpenTop` upper bound, on the exact imported recipe, may cover a known presence-1 `LegacyTileWorld` void or NoDraw cell. It is non-capture compatibility coverage with the released bilinear arithmetic and a cell tag, never a physical face, never a filled hole, never a finite cave floor or ceiling, and never a substitute for missing, unloaded or corrupt data.
- Writer-owned surface storage lives under `tiles/surfaces/` inside the existing `tiles/` reservation. Author-owned `surfaces/` resources are never reserved, swept or rewritten. `MapDocumentStorage.IsReserved` is unchanged.
- Game nav budget stays **8 MiB aggregate after deterministic gzip -9, in plain git**. R2 bakes no nav.
- #1310 is a code-verified open defect. The save lock alone does not prove windowed no-loss. Windowed no-loss is claimed for tile entries only after Task 1 and for surfaces only after Task 7.
- Format 4 inputs migrate once to the next free format (nominally 5). The authored identity token changes exactly once, with no continuity. Resolver-v1 execution, its options and caller `OptionsHash` stay unchanged.
- Producer facts only. Swimming owns the generic lease, mutation gate, capsule solver and movement consumers (#1299). Pivot owns hearing, presentation, dialogue, shop, follow and target eligibility (Grimhollow #458). No game floor ID, graph, global Y threshold or support-key equality gate. F3 types are an accepted contract, not released APIs. R3-only shapes named here are plan inputs for R3's own review, not R2 code.
- **Factory-only results.** `MapScopedIdentity`, `MapCoverageWitness`, `MapReadWitness`, `MapMembershipResult`, `MapSupportResult`, `MapSupportCandidateSet` and `MapRelationResult` are `public sealed class` types with an `internal` constructor, get-only properties and read-only copied collections. A consumer cannot construct or mutate a falsely complete scoped result.
- Private oracle boundary (D1): no shipped-source bytes, per-path provenance, coordinate-bearing paths, derived geometry, assertion snapshots, counts, digests of private content or detailed reports enter this public repository, its CI or its logs of record. Missing private inputs fail private acceptance. Nothing is skipped or waived.
- Repository rules: stage and commit explicit paths, never the shared stash. New behavior gets a headless test, namespaces `KhaozEngine.Tests.*`. KESIZE ratchet: new files under 800 lines, baseline files never grow (this plan never edits `KhaozEngine.MapEdit.Tool/MutationService.cs`, `KhaozEngine.MapEditor/EditorCommands.cs` or `KhaozEngine.MapEditor/MapEditorScene.cs`). Warnings are errors. No em-dash or en-dash glyphs and no prose semicolons in Markdown or comments.
- Every build, test, format, pack and guard runs through `/tmp/grimhollow-orch/slot-run.sh`, one at a time, with a unique log per run. Record actual exit codes. Exit 75 means nothing ran: report it and let the coordinator schedule reacquisition. No automatic retry helper, loops, stress, local GPU work or human window launch. Hosted stress needs explicit owner permission.

## Review Focus

These five inputs are implied by the spec, not exercised by its acceptance rows, and most likely to bite a user. Each has a pinned test in its owning task.

1. **A second writer replaced content this window never loaded, then the window saves, including a world with no tile entries at all.** Refusal before any file changes. Task 1 `StaleWindow_RefusesAfterAnotherWriterReplacedAnUnloadedTile`, Task 7 `SurfacesOnlyWindows_SecondSaveRefusesAsStale`.
2. **An author changes an overlay cut on a cell next to a real hole.** Paint changes, the physical surface and the hole do not. Task 9 `OverlayCut_NeverOpensOrClosesAPhysicalAperture`.
3. **Negative lattice addresses at a slot edge.** Floor division, one owner per shared corner, identical exact world vertices on both sides. Task 4 `PatchKey_FloorsNegativeCellsIntoSlots`, Task 10 `Seam_AcrossNegativeSlotEdgeSharesExactVertices`.
4. **Two independent floors at the same height, one the actor's current support.** `Ambiguous`, never silent continuity. Task 13 `EqualHeightIndependentOwnersRefuseEvenWhenOneIsCurrent`.
5. **A point exactly on a portal plane or a horizontal opening plane.** ToSpace owns wall-portal points, the space above owns opening-plane points. Task 12B `ExactPortalAndOpeningPlanes_FollowTheOwnershipTieRules`.

---

## Decisions this plan makes

### D1. Private shipped-source oracle (confirmed by the coordinator, recommendation B)

KhaozEngine is public and Grimhollow is private. Public CI runs only synthetic fixtures. The exhaustive comparison runs privately in one exact project, `KhaozEngine.MapDoc.OracleHarness.Tests`, which is deliberately not in `KhaozEngine.slnx`.

| Item | Public engine repository | Private side |
| --- | --- | --- |
| Harness code | `KhaozEngine.MapDoc.OracleHarness.Tests` source, input guard, entry boundary, report writer, comparison logic and synthetic guard fixtures. No shipped data, digests, paths or counts | None |
| Provenance manifest | JSON schema only (`ShippedSourceProvenance`, Task 3) | `docs/superpowers/programs/world-authoring/proofs/r2-oracle/shipped-source-provenance.json` in the private Grimhollow repository, committed by the controller. Holds source tag and commit, per-path SHA-256, aggregate digest and rule, extraction pathspec, engine pin, units, row orientation, comparison policy and oracle equivalence |
| Source bytes | Never | A fresh `git archive` extraction under a `0700` temporary directory, deleted after the run |
| Reports and logs | Never committed. Engine Outcome records only pass or fail and the private record reference | Report files under `/tmp/grimhollow-orch/private/wa-r2/`, their SHA-256 and run notes recorded in `proofs/r2-oracle/RUNS.md` in the private Grimhollow repository |
| Public summary | Only coordinator-approved sanitized wording, stating pass or fail and the engine commit. No counts, coordinates, paths or digests of private content | Detailed results |

Rules the harness enforces (Task 3):

- **One entry boundary.** Every private test body runs inside `PrivateOracleEntry.Run`. It reads exactly the four named variables (`KHAOZ_R2_SHIPPED_SOURCE`, `KHAOZ_R2_ORACLE_PROVENANCE`, `KHAOZ_R2_ORACLE_PROVENANCE_SHA256`, `KHAOZ_R2_ORACLE_REPORT`) through `Environment.GetEnvironmentVariable(name)` into a `Dictionary<string, string?>`, so no `IDictionary` conversion of the whole environment exists. It then validates inputs, creates the private report securely, runs the body and maps every exception.
- **Missing or unsafe inputs fail.** A missing variable, a provenance digest mismatch, an unreadable or unparsable provenance file, a report path inside a git work tree or already present, and a failed source verification each throw `PrivateOracleInputException` whose message names only the variable or rule. Inputs never skip.
- **Secure report or no run.** The report's parent directory must exist with mode exactly `0700`. The file is created with `FileMode.CreateNew` and `UnixCreateMode` `0600`. If it cannot be created that way, the entry throws `PrivateOracleFailure("private oracle: report could not be created securely")` and the body never runs.
- **Every failure route is sanitized.** Filesystem, JSON, loader, arithmetic and assertion exceptions thrown by the body are caught at the entry. Their type, message and stack go only into the private report (category `exception`), and the entry throws `PrivateOracleFailure("private oracle: exception failure, report sha256 <hex>")` with no inner exception. Comparison failures throw `private oracle: <n> <category> mismatches, report sha256 <hex>`. No harness test writes to `ITestOutputHelper` or the console, uses `[InlineData]` with source data, or calls `Assert` on source-derived values.
- **No PR exposure.** Public PR CI never sees credentials, the private checkout or the harness. Task 2 pins exclusion from both the full and the selective CI path. The harness is Unix-only, because it checks POSIX modes.

### D2. #1310 refusal and one completeness contract

`MapTiledFile.Save` carries unloaded entries by their old hash and then deletes the manifest temp and reads the previous manifest. A window opened before another writer replaced and swept an unloaded file therefore commits a manifest naming a deleted file and sweeps the newer one.

The repair records a **generation witness**, the lower-hex SHA-256 of the exact manifest bytes an index was read from or committed with, in `MapTileIndex.ManifestSha256`. `ReadManifest` is the only manifest reader, so `LoadTiled` (whole and windowed), `MapDocumentSource.OpenTiled`, `Refresh` and `VerifyTiled` all receive it. A **partial** save reads the current manifest bytes once under the lock and throws `stale window` when they are absent, unreadable or differently digested, before the manifest-temp delete, any payload write and any sweep. The same bytes feed `ReadPrevious`. After the commit the index carries the new digest. Whole documents keep last-writer-wins, and `SaveAs` and form conversion keep released behavior.

**Completeness is one flag.** Task 7 adds `MapTileIndex.Surfaces` (`MapSurfaceStorageIndex`, the surface directory and index pages this view read from the same manifest) and redefines `MapTileIndex.IsPartial` as "any tile entry unloaded, or any surface directory page, index page or listed patch unread". The existing doc comment already states that every save entry point checks `IsPartial`. The nine production checks at `ca13d62d7` (`MapTiledFile.Save.cs:48,68`, `MapBoundDocumentValidation.cs:26`, `MapDocumentFile.cs:293`, `GenerateDungeonCommand.cs:86`, `MapEditSession.cs:342,405,575,580`, `NativeDocumentService.cs:48`) therefore refuse or report surfaces-only windows without edits. A new `MapTileIndex.HasUnloadedTiles` keeps the old tile-only meaning for code that needs it. A surfaces-only window can never bypass #1310 or become a complete resolved document.

### D3. Data model summary

| Concept | Decision |
| --- | --- |
| Lattice frame | Per logical surface: rational cell unit, rational height unit, row direction, datum `WorldY0`. Imported terrain uses cell 1/1 m, height 1/100 m, `NegativeZ`. Any positive reduced `int` ratio is a legal unit |
| Authored vertex address | `MapLatticeAddress(X, Z, Denominator)`, the exact rational lattice position `(X/D, Z/D)` in that surface's cells, reduced by `gcd(|X|, |Z|, D)`, `1 <= D <= 64`. Corners have `D = 1`, legacy mid-edges `D = 2`, a `k`-segment subdivision vertex divides `k`, a fan centre divides 6. `MapLatticeFrame.WorldXz` maps any address to exact world metres, so one world vertex compares exactly across lattices of different units |
| Generated refinement vertex | `MapExactXz(X, Z)` in exact world metres, produced only by `MapCommonRefinement`. Never stored, never an authored address, never subject to the 64 denominator bound |
| Patch slot | 64-cell slots per surface, negative flooring. One rectangle and one payload per `(SurfaceId, SlotX, SlotZ)` |
| Cell record | 8 bytes: underlay, overlay, cut, rotation, flags (bit-identical to `TileSettings`), physical topology |
| Presence | One bit per cell. 0 removes physical triangles and is written only by native authoring, including fine-patch conversion. Imported void and NoDraw keep 1 |
| Edge subdivision | Legal only on a present cell's edge whose other side is outside the patch rectangle or a presence-0 cell inside it (the conversion rim). Interior edges between two present cells refuse |
| Shared vertices | `MapCornerDependency(CornerX, CornerZ, MapVertexOwner(Patch, Address))`. The owner and its address in the owner's lattice are fixed at creation, lowest-ordinal incident patch key. Only explicit reassignment moves them |
| Records and references | Each record is anchored in one patch payload. Every reference is `MapRecordRef(Id, Anchor)`, so resolution is one bounded patch read. Each sparse index entry also lists `IncidentRecords`, the anchored references of records anchored elsewhere whose XZ footprint touches that patch, independent of height, so a scope finds every relevant record without a scan. Re-anchoring is an explicit edit that updates every referrer |
| Spaces | `MapSpaceDoc` lists walls, portals and links separately. Footprints reference their space and a lattice patch. Their lower and upper bounds name surfaces on any rational lattice (D7). An imported exterior lower bound may instead carry the compatibility-only kind `LegacyExteriorV1` under its exact recipe (D9) |
| Support recipe | Root `SupportRecipe`: `LegacyXzCallbackV1` (resolver 1, migrated default) or `AuthoredBindingsV2` (resolver 2), written on every save |
| Identity | Scheme 1 unchanged for surface-free resolver-1 documents. Scheme 2 hashes the semantic projection. `MapScopedIdentity` is a factory-only class built only by `MapScopedSurfaces.Acquire` and `CompleteView` |
| Storage | Monolithic: inline `surfacePatches`. Tiled: manifest `surfaceStorage` names directory pages, which name index pages (16 by 16 slot blocks), which name payloads, all content addressed |
| Exact scalar | `MapExactValue`, normalized in its constructor, `default` equals zero, overflow throws `MapExactOverflowException` (an `ArithmeticException`) that queries map to `NotRepresentable` |

### D4. Fine-patch conversion: rim model, triangulation and classification

**Rim model.** Converting a region of surface `S` to a new surface `S-fine` with subdivision `k` (2 to 64, fine units `u/k` and `h/k`) sets the region's coarse cells to presence 0, which forms an inner rim inside the coarse patch. Every rim edge of a present coarse neighbour gains `MapEdgeSubdivision(..., k)`. The fine patches cover the hole exactly, record `MapCornerDependency` on the coarse owners of every boundary vertex, and one `MapSurfaceSeam` joins each rim edge to the fine boundary. Where a coarse rim cell has a legacy mid-edge vertex on the rim and `k` is odd, the fine side subdivides that fine boundary edge into 2 so both vertex sequences match. Edge legality (D3) admits exactly these rim edges and patch-rectangle boundary edges, and the validators and tests in Tasks 4, 9, 10 and 11 use that one rule.

**Subdivided triangles.** A parent triangle with no inserted vertex on any of its edges emits itself as child 0. A parent triangle with at least one inserted vertex is fanned from its exact centroid over its boundary sequence: start at its first vertex after winding normalization, walk its three edges in order, include every corner, legacy mid-edge and inserted vertex, and emit child `c` as `(centroid, b_c, b_c+1)`. The polygon is convex, so the children never overlap, every boundary segment is used exactly once, their exact areas sum to the parent's and every child inherits the parent's plane and paint. A parent has at most `3 + 3 * 63 = 192` children. The centroid lies on the parent plane, so geometry is unchanged.

**Face identity.** `MapFaceKey(OwnerId, Patch, Primitive, ParentTriangle, Child, Side)`. Floors and ceilings use the slot-relative cell index, parent triangle 0 to 3 and child 0 to 191. Walls use the segment, triangle 0 or 1, child 0. Keys are tuples, so subdivision can never collide. `MapFaceKey` orders by `OwnerId` (ordinal), `Patch` (null first, then `MapPatchKey` order), `Primitive`, `ParentTriangle`, `Child`, `Side`.

**Classification.** Every coarse cell in the region is classified before anything mutates.

| Class | Meaning | Default |
| --- | --- | --- |
| `ExactCoplanar` | All physical triangles share one plane and the paint boundary lies on fine edges or diagonals at this `k` | Converted exactly |
| `ExactDiagonal` | `Full` or `DiagonalHalf` cell with a real crease on its diagonal. The crease runs along fine cell diagonals for every `k`, crossed fine cells take the forced topology, half paint becomes fine `DiagonalHalf` paint | Converted exactly |
| `UnsupportedEncoding` | Valid piecewise-planar geometry or paint that the regular fine lattice cannot encode at this `k`: a non-coplanar corner cut (its fan creases have slope 2 and never lie on fine edges or diagonals), or a corner-cut paint boundary with odd `k` | Refuses, or with `AcceptAuthoredDifferences` lists every changed face |
| `NotRepresentable` | `LegacyTileWorld` void or NoDraw cell. Its bilinear non-capture fallback has no triangulated native equivalent and `Native` policy forbids NoDraw | Refuses, or with acceptance becomes physical faces from its legacy authored triangulation, listing `FallbackRemoved`, `FlagRemoved` and every new face |

Converting any `LegacyTileWorld` cell also changes compiled arithmetic from `heightCm * 0.01f` to exact rational offsets, so it always produces `ArithmeticPolicy` difference rows (with the maximum offset delta) and requires acceptance. Every intentional change appears in `MapConversionResult.Differences`. Nothing is silently resampled.

**Footprints over a converted bound.** For every footprint whose lower or upper bound names the converted surface, each footprint cell (on the footprint's own lattice) is classified against the region in exact world coordinates. A cell wholly inside the region moves to the fine surface. A cell wholly outside stays. If every cell moves, the footprint's bound is retargeted in place. Otherwise the inside cells move to a new footprint `<footprintId>-<fineSurfaceId>`, anchored with the original. A moved lower bound of kind `LegacyExteriorV1` becomes `SupportFloor` on the fine surface, because the fine surface is `Native` and accepted conversion has turned the fallback into physical faces. Cells left on the coarse surface keep their tag. A cell whose interior the region boundary crosses refuses the whole conversion atomically, before any mutation, with the message `footprint straddle: footprint '<id>' cell <slotCell> crosses the conversion region boundary. Align the region to whole footprint cells or split the footprint first.` The first offending cell in (footprint id, slot cell) order is named. R2 never subdivides a footprint automatically and never changes a bound the request did not name. Choosing an aligned region or splitting the footprint is an authoring gesture that R9/R10 surface as guidance. This only concerns conversion of a bound. Mixed-lattice bounds themselves are valid (D7), and an author can always pick a region aligned to both lattices.

### D5. Producer facts for F3 and #458, snapshot lifetime and bounded lookup

**One snapshot, one witness.** `MapScopedSurfaces.Acquire(source, scope, assetSha256)` performs one bounded acquisition over an `IMapSurfaceSource` in this canonical order: (1) the scope's patches through the index, (2) breadth-first record resolution through anchors (the incident records of every acquired scope patch and the forward references of acquired records, never expanding a space's `Walls`, `Portals` or `Links` lists), (3) bound-surface patch enumeration for every acquired footprint cell that overlaps the scope rectangle with positive area. The result owns one `MapScopedIdentity` and one `MapReadWitness`. `MapSpaceMembership`, `MapSupportQuery` and `MapSpaceRelations` take a `MapScopedSurfaces` and return that same witness instance. `MapCommonRefinement.RefineFootprintCell` reads the same acquisition and returns geometry, not a witness. A query needing anything not acquired returns `MissingGeometry`, never a fresh read.

**Snapshot lifetime inside R2.** Three different guarantees, stated separately:

- `MapDocumentSurfaceSource.Capture(document)` deep-copies the resident refs, patches and storage index at capture, so later document edits never change its answers. Its cost is proportional to the resident set, which the monolithic embedding limit or the window bounds. It is an editing and test convenience, not the R8 runtime path.
- `MapStoredSurfaceSource.Open(directory)` **pins the manifest generation**, not the world. It reads those manifest bytes once and only reads pages and payloads that generation names. A later save may sweep those files, and a pinned read then returns `Missing`, never newer data. There is no refresh.
- Only a **successful acquisition** is immutable. `MapScopedSurfaces` owns deep copies of every acquired patch and record, bounded by `MaxCandidatePatches` patches of at most 1 MiB and `MaxRecordReads` record reads. `Patch(key)` returns a fresh clone on every call, every exposed list is a read-only copy, and nothing after `Acquire` returns re-reads the source. Mutating a returned clone or array cannot change a later answer, the identity digest or the witness.

R8 owns live residency, publication and eviction. Swimming owns the F3 read lease and mutation gate. R2 claims neither.

| Consumer need | R2 producer (this plan) | Later owner |
| --- | --- | --- |
| F3 `MovementSpaceKey`, `MovementSupportKey` | `MapSpaceDoc.Id`, `MapFaceKey` owner and patch | R3 native adapter (F3 G1b) |
| F3 `EnumerateSupport` candidates | `MapSupportQuery.EnumerateCandidates` | Swimming consumes, R3 adapts |
| F3 coverage witness and query identity | `MapCoverageWitness`, `MapScopedIdentity`, `MapReadWitness` | R3/R8 scope acquisition, swimming lease |
| F3 water intervals | not in R2 | R4 |
| #458 membership with provenance | `MapSpaceMembership.Query` | Pivot policy at G3.4 |
| #458 relation, portal aperture and authored state | `MapSpaceRelations.Relate` with input points, exact aperture geometry, anchors and `AuthoredOpen` state | R3 decides whether live state exists |
| #458 physical occlusion and clearance certainty | `NotEvaluated` | R3 `MapPhysicalRelations` |

**Planned R3 boundary (R3 plan input, not R2 code, not released).** Listed so consumers see one coherent shape. R3's own plan review fixes the final form.

```csharp
// R3 plan input, package KhaozEngine.MapDoc.Physics. Not implemented, released or approved by R2.
public enum MapPhysicalTest { SegmentOcclusion, BodyClearance }
public enum MapPhysicalStatus { Evaluated, Stale, MissingGeometry, CapacityExceeded, NotRepresentable, Invalid }
public sealed record MapBodyProfile(float Radius, float HalfHeight);
public sealed record MapMotionPath(IReadOnlyList<MapFramePoint> Points);          // caller supplied, 2 to 64 points
public sealed record MapPhysicalRelationRequest(MapRelationResult Facts, MapPhysicalTest Test, MapMotionPath? Path, MapBodyProfile? Body);
public sealed class MapPhysicalRelationResult                                    // factory-only like the R2 results
{
    public MapPhysicalStatus Status { get; }
    public MapPhysicalCertainty Certainty { get; }                               // NotEvaluated unless Status is Evaluated
    public MapPhysicalRelationRequest Request { get; }
    public MapReadWitness Witness { get; }                                       // Facts.Witness
    public string? Detail { get; }
}
public static class MapPhysicalRelations
{
    public static MapPhysicalRelationResult Evaluate(MapPhysicalRelationRequest request, MovementQueryLease lease);
}
```

Rules R3 adopts with this shape: `Invalid` when `Facts.Status` is not `Resolved`, when `SegmentOcclusion` carries a path or body, when `BodyClearance` lacks a body or path, when the path has fewer than 2 or more than 64 points, when any point's frame differs from `Facts.A.Frame`, when the first point is not exactly `Facts.A` or the last not exactly `Facts.B`, or when radius or half-height is not finite and positive. `Stale` when the lease's frame, generation or certified coverage does not cover `Facts.Witness`. `SegmentOcclusion` evaluates exactly the straight segment from `Facts.A` to `Facts.B`. `BodyClearance` sweeps the body along the caller's polyline exactly as given. R3 constructs no route, inserts no pass point and runs no search. A caller that wants clearance through an aperture supplies the path, for example from #1301 navigation, using R2's exact aperture geometry. This boundary carries no live portal state. R2 supplies only `MapPortalStateSource.AuthoredOpen`, and whether R3 adds live state is R3's decision. `MovementQueryLease` is the accepted F3 contract type at 404fa519f, not a released API. The test kind is the only purpose input. Hearing, sight and interaction policy stay with pivot.

### D6. Labor estimate

**Current provisional estimate: 38 to 64 engineer-days** (34 to 57.5 implementation and verification plus 4 to 6 review and reconciliation, upper bound rounded from 63.5). It is the coordinator's latest provisional planning allowance, subject to final decomposition and self-review. It is not an execution, calendar or budget commitment.

Historical bases and why each moved. Only the last row is current.

| Basis | Total (engineer-days) | What changed from the previous row |
| --- | --- | --- |
| CD1 approved design | 29 to 49 | Design table: 26 to 44 implementation plus 3 to 5 review |
| v1 plan, confirmed provisional under CD2 | 34 to 58 | CD1 added three deliverables the design table did not cost: #1310 (Task 1), exact fine-patch conversion (Task 11) and the #458/F3 facts (Task 15). Review 4 to 6 |
| v2 plan (superseded) | 37 to 62 | +3 low, +4 high from controller review 1: unified surface completeness (Task 7), bounded record acquisition and scoped identity (Task 8), centroid fan and tuple face keys (Task 9), conversion classes and rim rules (Task 11), commensurate mixed-resolution validation (Task 12), exact aperture geometry (Task 15), harness exclusion guards (Task 2), release-preparation gate |
| **v3, unchanged in v4 (current)** | **38 to 64** | +1 low, +2 high from the D7 ruling and controller review 2: general rational refinement with budgets and outcome classes split into Task 12A (+0.5 to +1 over v2's Task 12), bound-slot enumeration and `NotRepresentable` acquisition (Task 8, +0.25 to +0.5), mixed-bound retargeting and straddle refusal (Task 11, +0.25), harness entry sanitization (Task 3, +0 to +0.25). Factory-only result shapes, the corrected R3 boundary and restored assertions fit inside existing rows |

The final v4 corrections (D9 tagged legacy coverage, the attempted-pair budget and straddle guidance) are absorbed in the existing Task 5, 11, 12A, 12B and 13 rows. The per-task column is unchanged and 38 to 64 remains the one current estimate. Execution should watch Task 12A, which now also carries the shared classifier and the bilinear helper. The coordinator final review corrections of 08d4be1a (the compiled bound-face context, partial raw-save refusals and bounded bound-slot counting) are absorbed in the existing Task 7, 8, 9, 12A, 12B and 13 rows. They consume slack, mostly in Task 12A, and do not change the column or the current 38 to 64 allowance.

### D7. Mixed-resolution bounds: general rational common refinement (coordinator ruling 2026-10-07)

**Ruling.** Lower and upper bounds of a footprint may name surfaces on any rational lattices, independently of each other and of the footprint lattice. A 1/2 m floor under a 1/3 m ceiling is coherent and never forces an unrelated conversion. v2's nested integer-ratio (commensurate) restriction, `MapLatticeFrame.IsCommensurateWith` and the `incommensurate` finding are removed.

**Bound-face enumeration.** For a footprint cell with exact world rectangle `R = [minX, maxX] x [minZ, maxZ]` and a bound surface with cell unit `u`, `MapLatticeRanges.CellRange` (Task 8) maps `R` into that lattice (negating Z for `NegativeZ`) and returns the half-open index range of cells whose closed squares overlap `R` with positive area: `floor(minX / u)` to `ceil(maxX / u) - 1`, likewise for Z. A cell or patch that only touches `R` along an edge or at a point is not enumerated. All arithmetic is `MapExactValue`, so an unrepresentable range is `NotRepresentable`. Acquisition (Task 8) uses the same range to enumerate bound slots, counts them against the remaining `MaxCandidatePatches` before reading any, and refuses with `CapacityExceeded` (detail `bound slots`) before any partial result exists. Counting never materializes a slot list: each range's gross slot count is a checked `long` product computed before iteration, its overlap with the bounded set of already acquired or already pending keys is counted by scanning that set, never the range, and the range refuses at once when its remaining fresh count exceeds the remaining budget. A range that fits has a gross count of at most the remaining budget plus the known-set size, so iterating it is bounded (Task 8). The old "at most four bound patches per footprint cell" assumption is gone.

**Compiled bound-face reuse (coordinator final review I1).** Bound faces are never compiled per footprint cell or per call site. `MapBoundFaceContext` (Task 12A, internal) is the one immutable view and validation context that owns compiled reuse. There is no global or cross-call cache.

- **Lifetime.** One context per `MapSpaceCoverageValidator.Validate` call, shared by every footprint and cell of that call, so Task 16 and 17 transactions share it through the validator. One per public `BoundFaces` or `RefineFootprintCell` call. One per membership `Query` and per support `Select` or `EnumerateCandidates` call (Tasks 12B and 13), where support passes its context to its own membership step. A context is never static, never stored in `MapScopedSurfaces`, a result or a witness, and is unreachable once its call returns.
- **Binding and cache keys.** A context is bound by reference to one `MapScopedSurfaces` and refuses any other (`ArgumentException` containing `context view`). Entries are keyed by `MapPatchKey`, which in that immutable view names exactly one surface ref and one acquired patch. Each entry holds the compile of exactly the demanded slot cells (`MapSlotCellMask`, Task 9). A lookup outside the demanded mask throws `InvalidOperationException` containing `context demand` and never compiles.
- **Internal immutable access.** The context, `MapLowerCellClassifier`, the validator, membership and support read acquired patches only through `MapScopedSurfaces.TryAcquiredPatch` (Task 8), which returns the view's own acquired instance without cloning. Internal readers never write. Public `Patch` keeps its defensive clone and counts it in the internal `PatchClones`.
- **Preflight counted from bytes, then compile once.** Preparation has three phases and publishes nothing before the last ends. (1) Per footprint cell and bound, lower first: the checked `CellRange` size gate, classification, then `MapSurfaceCompiler.CountFaces` (Task 9) over the physical (lower) or present (upper) cells in range of each covering patch, summed with checked arithmetic and compared with `MaxSourceFacesPerBound`, the per-bound in-range budget (`CapacityExceeded`, detail `source faces`). A cell that fails keeps its own per-cell outcome and contributes no demand. (2) Each surviving cell's demand is merged into per-patch slot-cell masks (a 64-word working mask per patch, frozen into the immutable `MapSlotCellMask` before phase 3) as the traversal reaches it, deduplicated on insertion and never collected into a list before checking. A cell already set in its patch's mask adds nothing. A patch not yet in the context is checked against `MaxContextPatches` before its mask is allocated (`context patches`), and each newly set cell adds its `CountFaces` count to a checked running total compared with `MaxContextFaces` (`context faces`), so the total always equals the sum of `CountFaces` over the union masks, the total unique compiled work. The first overflow refuses every surviving cell of the call with zero compiles. The traversal still finishes phase 1 for the remaining cells so their own per-cell outcomes are reported, but it merges no further demand. Traversal order is fixed (footprint id, then slot cell ascending, lower before upper), so the counted totals are deterministic. (3) Each patch compiles exactly once over its union mask. A dense patch therefore costs only the cells some demand names, and a small requested range can never compile a whole dense patch outside the context budget.
- **Limits, cardinalities and lifetime.** Validation contexts use `MaxContextPatches` 256 and `MaxContextFaces` 262,144 from `MapRefinementLimits`. These defaults are proposed operational limits for coordinator approval with this correction, not previously approved world limits. Query contexts use `MaxCandidatePatches` and `MaxInspectedFaces` of their scope's `MapQueryLimits`. The enforced bounds are allocation cardinalities, never bytes. Enforced by check: at most `MaxContextPatches` patch entries, each with one slot-cell mask of 4,096 bits (a patch has at most 64 by 64 cells), and at most `MaxContextFaces` compiled faces summed over every compiled patch. Following from the compile by construction: one paint entry and at most three vertex entries per compiled face. Borrowed immutable inputs: the `MapScopedSurfaces` view, its acquired patch payloads, cell bytes and records are read through `TryAcquiredPatch` and never copied into the context. The caller owns the view, which outlives the context. Transient preflight state is one cell's demand at a time, at most `MaxSourceFacesPerBound` cells per bound by the `CellRange` size gate, plus the masks above, plus one face-free `CellOutcomes` entry per refused footprint cell (status and detail only), which is proportional to the borrowed footprint records and never to faces. Nothing grows past those bounds before the check that would refuse it, so no separate preflight cap is needed. The context and its compiles become unreachable when the call returns. There is no global or mutable shared cache. Memory in bytes is only an estimate: at an assumed 512 bytes per compiled face, which is neither measured nor enforced, the defaults suggest about 128 MiB for a validation context and about 2 MiB for a default query context. Preflight work is bounded by two size gates and `2 * MaxSourceFacesPerBound` cell reads per footprint cell, plus one `ValidateLocal` and one masked compile per unique patch.

**Algorithm (Task 12A).** `Refine(lower, upper, min, max, limits)` over exact `MapBoundFace(Key, Triangle)` inputs:

1. **Validate.** `min.X < max.X` and `min.Z < max.Z`, else `Invalid` (`cell`). Every input triangle has nonzero exact XZ signed area, else `Invalid` (`degenerate`). More than `MaxSourceFacesPerBound` faces on either bound is `CapacityExceeded`.
2. **Clip each bound face to `R`.** Sutherland-Hodgman against `x >= minX`, `x <= maxX`, `z >= minZ`, `z <= maxZ` in that order, counter-clockwise output, consecutive duplicates removed. A clipped polygon with zero exact area is discarded. A face that only touches `R` therefore contributes nothing.
3. **Areas.** `LowerArea`, `UpperArea` (absent for an open top) and `CellArea` are exact sums. Coverage judgments belong to the validator.
4. **Pair checks.** Every attempted pair of a clipped lower polygon and a clipped upper polygon is one pair check, charged before its bounding-box test. The charge is made in bulk before the pair loop: `checked((long)lowerPolygons * upperPolygons)` is compared with `MaxPairChecks`, and a larger demand returns `CapacityExceeded` (detail `pair checks`) before any pair bounding-box comparison, pair intersection clipping or published output. Within budget the loop visits lower polygons in `MapFaceKey` order, then upper polygons in `MapFaceKey` order, and only a pair whose exact bounding boxes overlap with positive area goes on to step 5. `PairChecks` therefore counts attempted pairs, never only positive-bounding-box candidates, so no comparison work is uncharged. With an open top, every lower polygon is a face on its own and `PairChecks` is 0.
5. **Intersect.** Clip the lower polygon against each directed edge of the counter-clockwise upper polygon, keeping the closed left side. Intersection points are exact (`t = c_p / (c_p - c_q)` from the two signed cross products). A result with positive exact area is a refinement face `(Lower, Upper)`, normalized to counter-clockwise order starting at its smallest vertex (Z, then X). A convex pair intersects in at most one convex polygon, so `(Lower, Upper)` is a unique canonical generated key. Faces are counted against `MaxRefinementFaces`, distinct vertices against `MaxRefinementVertices`.
6. **Separation.** At every vertex of every face, the exact upper plane height minus the exact lower plane height, both from the face's source triangles. `MinSeparation` is the minimum. Both bounds are linear on each face, so positive separation at every refinement vertex proves the whole cell.
7. **Outcome.** `Complete` with faces in `(Lower, Upper)` order and distinct vertices ordered by Z then X. `CapacityExceeded`, `NotRepresentable` (any `MapExactOverflowException`, detail contains `overflow`) and `Invalid` return no faces and no vertices.

**Validation (Task 12B).** Every per-cell finding has the form `<kind>: footprint '<id>' cell <slotCell>`. For each footprint cell, the outcome of `RefineFootprintCell` over the call's one context is used. A context refusal is one finding per footprint with surviving cells, `refinement capacity: footprint '<id>' (context faces)` or `(context patches)`, and those cells get no other refinement finding. A per-cell outcome `CapacityExceeded` is kind `refinement capacity`, `NotRepresentable` is `not representable`, `Invalid` is `invalid`, `MissingGeometry` is `missing geometry`. `LowerArea + CompatibilityArea != CellArea`, or an upper bound with `UpperArea != CellArea`, is `missing bound`. `CompatibilityArea` is nonzero only for cells the shared classifier (D9) returns as `LegacyNonCapture` under a `LegacyExteriorV1` lower bound. It is counted from classified cell rectangles and never creates faces, vertices or separation, and the refinement itself stays geometric. Under any other lower bound a legacy fallback cell has no faces and no compatibility coverage, so it is uncovered unless an opening is declared. `MinSeparation <= 0` is `separation`, suffixed ` at (<x>, <z>)` with the first offending vertex (Z, then X order) in exact `n/d` text. Strips and seams keep their strict vertex-sequence rules.

**Fixtures.** Near 1/2 m against 1/3 m (one cell with eight exact vertices and five faces), the same world at +32,000 m, a ridge on the 1/2 m lattice under a valley on the 1/3 m lattice whose only zero separation is the edge crossing (1/2, 1/3) that neither lattice owns, boundary-touch at a slot edge, coincident diagonals and a T-junction, a degenerate triangle and an empty cell, a hostile exact-overflow pair, a 1/64 m floor under a 1 m roof against every budget, two identical 16 by 16 triangulated grids whose 262,144 attempted pairs exceed the pair-check budget although only 1,024 pairs pass the bounding-box test, a unit pair whose bound range is not representable, and a 1/1,000,000 m bound that exceeds the slot budget before any read.

### D8. Sanitized proof and release ordering

Package-bearing version, changelog and declarations land on the task branch before final verification. The clean candidate gets the full local verification, the private oracle, a private pack and the normal hosted CI run before integration. The shared pack runs only from pushed `main`. Only the owner tags. Details are in "Review, release preparation, candidate gates and integration".

### D9. Compatibility-only legacy lower coverage (coordinator approval 2026-10-07)

**Problem v3 left.** A `LegacyTileWorld` void or NoDraw cell keeps presence 1 and compiles to no faces. v3's validator therefore reported `missing bound` for an imported exterior over such a cell, while membership and support used the released bilinear fallback for the same cell. Three consumers disagreed about one cell.

**Explicit tag.** `MapBoundKind.LegacyExteriorV1` is appended after `OpenTop` (byte value 4), so existing kinds keep their values. It is legal only as a footprint's lower bound. Ordinary `SupportFloor` bounds never accept fallback, and their behavior, the format-4 migration (which creates no footprints) and resolver v1 are unchanged. A recipe change would be a new kind, never a reinterpretation of this one.

**Exact recipe, one home.** `MapLegacyExteriorRecipe.Check` (Task 5) returns null for a footprint that uses no tag or meets the recipe, otherwise the first failing rule in this order: `legacy recipe: space` (the space is not `Exterior`), `legacy recipe: upper` (the upper bound is not `OpenTop`, including an upper bound of kind `LegacyExteriorV1`), `legacy recipe: surface` (the lower surface is absent, or its role is not `SupportFloor`, its presence policy not `LegacyTileWorld` or its frame not exactly `MapLatticeFrame.ImportedMetreCentimetre`), `legacy recipe: lattice` (the footprint's `Lattice.SurfaceId` is not the lower surface). The lattice rule makes each declared footprint cell exactly one imported cell, so compatibility coverage is bounded by the footprint's own slot-cell list and never clips or mixes lattices. `MapLegacyExteriorRecipe.PolicyId` is `kemap/legacy-exterior/1`. The tag is part of query policy version 1 and is carried by the footprint record, so it enters the patch and scheme-2 semantic digests.

**One classifier.** `MapLowerCellClassifier.Classify(view, footprint, cellX, cellZ)` (Task 12A) is the only code that decides how a lower-bound cell (absolute cell on the lower surface's lattice) is treated. `MapCommonRefinement.BoundFaces` and `RefineFootprintCell` (validator), `MapSpaceMembership` and `MapSupportQuery` all call it. It applies to lower kinds `SupportFloor` and `LegacyExteriorV1`. Checks run in this order and the first match wins.

| Order | Condition | Class | Validator (12B) | Membership (12B) | Support (13) |
| --- | --- | --- | --- | --- | --- |
| 1 | The footprint's space record is not acquired, missing or corrupt | `Unavailable` | `missing geometry` | `MissingGeometry` | `MissingGeometry` |
| 2 | A bound uses the tag and `MapLegacyExteriorRecipe.Check` fails | `InvalidRecipe` | `invalid` with the `legacy recipe` detail, plus the Task 5 finding | `Invalid` | `Invalid` |
| 3 | The lower patch read is anything other than `Present` or `KnownEmpty` (not acquired, unloaded, missing, corrupt) | `Unavailable` | `missing geometry` | `MissingGeometry` | `MissingGeometry` |
| 4 | `KnownEmpty`, a cell outside the patch rectangle, or presence 0 | `KnownHole` | `missing bound` | `MissingGeometry` (`missing bound`) | `MissingGeometry` |
| 5 | Presence 1, `LegacyTileWorld`, underlay 0 or `NoDraw`, lower kind `LegacyExteriorV1`, a corner outside the `short` range | `InvalidRecipe` (`legacy recipe: height`) | as order 2 | `Invalid` | `Invalid` |
| 6 | As order 5 with every corner inside the `short` range | `LegacyNonCapture` | Covered through `CompatibilityArea`, no faces | `Resolved` when the bilinear height is at most y, with `LowerCompatibility` set | `LegacyFallback` when no physical face is eligible and the bilinear height lies in the interval, otherwise `NoSupport` |
| 7 | Presence 1, `LegacyTileWorld`, underlay 0 or `NoDraw`, lower kind `SupportFloor`, regardless of corner range | `UntaggedLegacy` | `missing bound` | `MissingGeometry` (`missing bound`) | `MissingGeometry` |
| 8 | Otherwise | `Physical` | Canonical faces | Exact face height | Canonical faces |

Orders 5 to 7 use exactly Task 9's `LegacyFallbackCells` rule, and Task 12A asserts the two agree. A presence-0 hole is never filled, a missing, unloaded or corrupt patch is never fallback, and a cave or a finite upper bound can never carry the tag.

**Released arithmetic, one helper.** `MapLegacyBilinear` (Task 12A) is the only bilinear code. `Evaluate(h00Cm, h10Cm, h01Cm, h11Cm, fx, fz)` reproduces `TileWorldDocument.HeightAt` (`KhaozEngine.TileWorld/TileWorldDocument.Heights.cs:65` at `ca13d62d7`) operation for operation in `float`: each corner `cm * 0.01f`, `south = h00 + (h10 - h00) * fx`, `north = h01 + (h11 - h01) * fx`, `south + (north - south) * fz`. `HeightMetres(surface, patch, world)` applies the released frame convention for the imported recipe (tile size 1, `TileWorldSpace.TileX = worldX / tileSize`, `TileZ = -worldZ / tileSize`): `tx = world.X` and `tz = -world.Z` exactly, `x0 = floor(tx)` and `z0 = floor(tz)` exactly, corners `(x0, z0)`, `(x0 + 1, z0)`, `(x0, z0 + 1)`, `(x0 + 1, z0 + 1)` read from the patch. For exact fractional remainders `rx = tx - x0` and `rz = tz - z0`, use the already declared correctly rounded `MapExactValue.ToSingle()` to obtain `fx` and `fz`. Do not require these remainders to be exactly representable as floats. For example, coordinate `-1/1073741824` has remainder `1 - 2^-30`, which rounds to `1f` in the released subtraction. Correctly rounding that exact remainder gives the same result. This rounding is the explicit compatibility arithmetic, never geometry snapping or a change of cell ownership. For the same origin-frame binary32 input it matches the released float subtraction, without forming an absolute world float. Exact coordinate overflow still refuses through the existing representability outcome. No tolerance is introduced or relaxed. Any other frame or policy throws `ArgumentException` containing `legacy recipe`.

**Provenance, not identity.** `MapLegacyCellTag(MapPatchKey Patch, int SlotCell, string Policy)` names a compatibility cell. It is never a `MapFaceKey`. `MapMembershipResult.LowerCompatibility` and `MapSupportResult.Compatibility` carry it. A `LegacyFallback` support result has `Face` null, `Normal` null and `IsCaptureSupport` false. No geometric normal, physical clearance or capture claim is fabricated, and `EnumerateCandidates` never lists a fallback cell. Relations take membership as is.

**Downstream.** R11/G2's importer declares `LegacyExteriorV1` on imported exterior footprints whose plane contains void or NoDraw cells. R3 and R4 must keep fallback non-capture and emit no physics, nav or water faces for it.

## File Structure

| Path | Responsibility | Task |
| --- | --- | --- |
| `KhaozEngine.MapDoc/MapTileIndex.cs`, `MapTiledFile.cs`, `MapTiledFile.Save.cs` | Generation witness, stale-window refusal, unified completeness | 1, 7 |
| `KhaozEngine.MapDoc.OracleHarness.Tests/**` (not in slnx) | Format-4 recorder, private input guard, entry boundary, private inventory and comparison | 2, 3, 18 |
| `KhaozEngine.Tests/ArchitectureTests.MapDoc.cs` | Harness exclusion from full and selective CI, TileWorld containment | 2 |
| `KhaozEngine.MapDoc.Tests/Fixtures/FormatFour/**`, `FormatFourFixtures.cs`, `AssertFixtures.cs` | Frozen synthetic format-4 inputs, released resolver-v1 expectations, shared file digest helper | 2 |
| `KhaozEngine.MapDoc.Compatibility.Tests/**` | Public synthetic legacy oracle and differential, including the released bilinear agreement | 3, 9, 12A, 18 |
| `KhaozEngine.MapDoc/Surfaces/MapRational.cs`, `MapExactValue.cs`, `MapLatticeAddress.cs`, `MapLatticeFrame.cs`, `MapPatchKey.cs`, `MapSurfaceCell.cs`, `MapSurfacePatch.cs`, `MapSurfacePatchCodec.cs`, `MapSurfaceRef.cs` | Exact scalars, lattice, addresses, patch, cell and bounded codec | 4 |
| `KhaozEngine.MapDoc/Surfaces/MapTopologyRecords.cs`, `MapSurfacePatch.Records.cs`, `MapTopologyReferenceValidator.cs`, `MapSurfaceSemantics.cs`, `KhaozEngine.MapDoc/Spaces/MapSpaceRecords.cs`, `MapLegacyExteriorRecipe.cs` | Records, anchored references, digests, the D9 recipe rule | 5 |
| `KhaozEngine.MapDoc/MapDocument.Surfaces.cs`, `MapSupportRecipe.cs`, `MapSurfaceMigration.cs`, `Surfaces/MapSurfaceSet.cs` | Format advance, recipe, bindings, container | 6 |
| `KhaozEngine.MapDoc/Storage/*.cs` | Pages, tiled store, embedding, completeness index, sources | 7 |
| `KhaozEngine.MapDoc/Identity/MapAuthoredIdentityV2.cs`, `MapScopedIdentity.cs`, `MapWitnesses.cs`, `KhaozEngine.MapDoc/Storage/MapScopedSurfaces.cs`, `KhaozEngine.MapDoc/Surfaces/MapLatticeRanges.cs` | Scheme 2, bounded acquisition including bound-slot enumeration, factory-only identity and witnesses | 8 |
| `KhaozEngine.MapDoc/Surfaces/MapSurfaceTopology.cs`, `MapSurfaceCompiler.cs`, `MapCompiledPatch.cs`, `MapSubdividedTriangle.cs` | Canonical faces, centroid fan, paint, fallback cells, anchors | 9 |
| `KhaozEngine.MapDoc/Surfaces/MapBoundaryGeometry.cs`, `MapWallStripCompiler.cs`, `MapSeamValidator.cs`, `MapOpeningBoundary.cs` | Chains, strips, seams across lattices, opening planes | 10 |
| `KhaozEngine.MapDoc/Editing/MapNativeWriteSet.cs`, `MapFinePatchConversion.cs`, `MapConversionClassifier.cs` | Write sets, conversion, classification, footprint retargeting | 11 |
| `KhaozEngine.MapDoc/Spaces/MapRefinementTypes.cs`, `MapCommonRefinement.cs`, `MapBoundFaceContext.cs`, `MapLowerCellClassifier.cs`, `KhaozEngine.MapDoc/Surfaces/MapLegacyBilinear.cs` | Exact common refinement, bound-face enumeration, the compiled bound-face context (D7), pair-check budget and outcomes, the one lower-cell classifier and the one released bilinear helper (D9) | 12A |
| `KhaozEngine.MapDoc/MapFramePoint.cs`, `Spaces/MapSpaceMembership.cs`, `Spaces/MapSpaceCoverageValidator.cs` | Membership, coverage and separation validation | 12B |
| `KhaozEngine.MapDoc/Support/*.cs` | Support query, resolver v2, adoption | 13 |
| `KhaozEngine.MapDoc/Surfaces/MapFrameLocal.cs` | Frame-local meshes and rigid local composition | 14 |
| `KhaozEngine.MapDoc/Spaces/MapSpaceRelations.cs`, `MapRelationFacts.cs` | Relation facts and exact apertures | 15 |
| `KhaozEngine.MapEditor/NativeDocumentTransaction.cs`, `KhaozEngine.MapDoc/Editing/MapNativeEditEffects.cs` | General native transaction seam and effects | 16 |
| `KhaozEngine.MapDoc/Editing/MapTerrainEdits.cs`, `KhaozEngine.MapEditor/NativeTerrainCommands.cs`, `KhaozEngine.MapEdit.Tool/MutationService.Terrain.cs` | Terrain edits, commands, service | 17 |
| `KhaozEngine.MapDoc/README.md`, `KhaozEngine.MapEditor/README.md`, `KhaozEngine.MapEdit.Tool/README.md`, `docs/USING-KHAOZENGINE.md` | Living documentation | 18 |

Tests needing `internal` MapDoc members live in `KhaozEngine.MapEditor.Tests/MapDoc/`, which has `InternalsVisibleTo` from MapDoc, MapEditor and MapEdit.Tool. Public-API tests live in `KhaozEngine.MapDoc.Tests/`. From Task 7 on, MapEditor.Tests compiles every `KhaozEngine.MapDoc.Tests/**/*Fixtures.cs` by link and copies `Fixtures/**`. **Link invariant (coordinator final review M1).** No type name declared in a linked file may equal a type name declared in MapEditor.Tests' own sources, whatever the namespace. That covers every type in those files, including non-suffixed helpers such as `NativeMemoryAssetSource` and `FilteredSurfaceSource`. MapEditor.Tests helpers are not suffix-free: it already declares `GltfTriangleFixtures` (`KhaozEngine.MapEditor.Tests/MapEditor/GltfTriangleFixtures.cs`), so the suffix alone proves nothing. The build enforces the shared namespace (CS0101) and ambiguous imports (CS0104). Task 7's name check and every task that adds a `*Fixtures.cs` file or a MapEditor.Tests type keep the intersection empty.

## Task dependency and labor table

| Task | Depends on | Shared files touched later | Design labor row | Labor (days) |
| --- | --- | --- | --- | ---: |
| 1 #1310 stale-window refusal | none | `MapTiledFile*.cs`, `MapTileIndex.cs` (7) | new prerequisite | 1 to 2 |
| 2 Format-4 expectations, harness project, CI exclusion guards | 1 | harness csproj (3, 18) | 6a | 0.5 to 1.5 |
| 3 Public legacy oracle, private input guard, entry boundary and inventory | 2 | Compatibility project (9, 18) | 6a | 0.5 to 1.25 |
| 4 Exact scalars, lattice, addresses, patch, codec | 3 | `MapSurfacePatchCodec.cs` (5) | 1 | 1.5 to 2.5 |
| 5 Anchored records, references, digests | 4 | none | 1 | 1 to 2 |
| 6 Format 5, recipe, migration | 2, 5 | `MapCanonical.cs`, `MapTiledFile.cs`, schema (7) | 1 | 1 to 1.5 |
| 7 Surface storage and unified completeness | 1, 6 | none | 1 | 2 to 3 |
| 8 Scheme 2, acquisition, factory-only identity | 7 | none | 1 | 1.75 to 2.5 |
| 9 Surface compiler | 5, 3 | none | 2 | 2.5 to 4.5 |
| 10 Boundary geometry | 9, 8 | none | 2 | 2.5 to 4.5 |
| 11 Fine-patch conversion | 10 | none | new CD1 | 2.25 to 3.75 |
| 12A Exact common refinement | 10, 8 | none | 4 (D7) | 1.5 to 2.5 |
| 12B Membership, coverage and separation | 12A, 11 | none | 4 | 3.5 to 5 |
| 13 Support and resolver v2 | 12B | none | 3 | 2 to 3.5 |
| 14 Precision envelope | 13 | none | 3 | 1 to 1.5 |
| 15 Relation facts | 13 | none | new #458/F3 | 2 to 3 |
| 16 Transaction seam | 11, 8 | `NativeDocumentSnapshot.cs` (17) | 5 | 2 to 3 |
| 17 Terrain commands and service | 16, 12B | none | 5 | 3 to 5 |
| 18 Compatibility comparison and docs | all | none | 6b | 2 to 4 |
| Release preparation and candidate gates | 18 | `Directory.Build.props`, `CHANGELOG.md` | release | 0.5 to 1 |
| Implementation and verification subtotal | | | | **34 to 57.5** |
| Independent review and reconciliation | | | review | 4 to 6 |
| **Total (D6, current)** | | | | **38 to 64** |

Order realizes the design's **6a, 1, 2, 4, 3, 5, 6b** with #1310 first. Version-2 identity (Task 8) is tested before resolver-v2 results publish (Task 13). Execution is strictly serial in the listed order (1, 2, ..., 11, 12A, 12B, 13, ..., 18).

## Downstream allocation (required, not optional)

R2 does not estimate other rounds. Each row is mandatory work the owning round's plan must cost before its approval. No whole-program calendar is forecast.

| Owner | Required work consuming R2 contracts |
| --- | --- |
| R3 | Physics sidedness, floor support, ceiling and wall headroom, pick/LOS, placement supports and movement integration on `MapCompiledPatch` and strip faces. Whole-metre pose rule `new Pose(anchor - world.Origin, rot)` with an asserted whole-metre origin. #1301 tiled and incremental nav capture, profile and link identity (`TiledNav_SeamsLinksAndVerticalLayersAreDeterministic`). `StackedCaveFloorsAndCeilings` physical half. F3 G1b native adapter over R2 producers using swimming's generic lease and solver without duplicating #1299. `MapPhysicalRelations` (D5 plan input, reviewed in R3's plan) for #458 occlusion and clearance over caller-supplied segments and paths, plus R3's own decision on live portal state. Multi-cell ghost/handoff with R8 and G3. **Contact-backend dependency (R3/G1b, recorded only):** swimming Task 2's immutable focused evidence is engine commit `e1b6e04c838f9dd8972ba2ece3ba0b3cb816ca14`, `docs/design/SWIM-CONTACT-BACKEND-EVIDENCE-2026-10-06.md`. Root independently checked its focused logs: corrected contacts 23/23 and regressions 46/46 passed, and the first 21-pass, 1-failure run is preserved. It is focused generic proof, not a released or native capability and not whole-branch, release or G1b proof. Its explicit refusal bounds are 4,096 selected broad-phase candidates or leaves, 4,096 children per compound, 16,384 contacts, 1,024 triangles in a mesh's expanded local capsule region, unit mesh scale only, certified error at most 0.001 m, and whole-query refusal when smoothing changes a retained normal without certifiable recomputed separation. Nonzero layer masks are unsupported (#1315). It gives no native partition guarantee. Swimming Task 3's witness and pin details are accepted at engine commit `e74e3274c805bc57ae76f339ea3e87e1a967c942`, `docs/design/SWIM-ENVIRONMENT-TASK3-API-DETAILS-2026-10-07.md`: at most 256 witness IDs, 1,024 UTF-8 bytes per ID, 65,536 aggregate UTF-8 bytes and 4,096 combined producer dependency entries per prepare or pin attempt. These are query-policy limits, not world caps. Native G1b must satisfy or explicitly refuse them with identity consistency, `ReferenceEquals` source and view binding, bounded pre-copy and sort, and exception-safe cleanup that releases the physics gate. Its value tests are authored and queued, not yet verified or released. R3/G1b must prove collision chunking or resolve backend capability before treating R2's 65,536-face compiled patches as contact-compatible, including canonical triangle and owner mapping, complete swept and contact coverage at chunk seams, the normal and separation proof and policy identity. No assumption that chunking makes dense or wedged contacts impossible. No silent dry, traversable or proven-unreachable fallback, truncation or weakened F3 contract. R2's compile budget and scope are unchanged |
| R4 | Flooded/dry containment and bed/surface semantics on R2 spaces, `DryCaveUnderOcean_RemainsDry`, F3 water intervals, #1300 data inputs, #1297 area-scaled geometry, #1299 walker profile identity with swimming |
| R5 | Prefab local floors through `MapFrameLocal.CompileInFrame`, parent-space and interior domains as nested `MapSpaceDoc`, roof links, no duplicate floor owner |
| R8 | Directory-page residency, bounded closure acquisition, scope validation and publication, generation fencing and eviction over `IMapSurfaceSource` and `MapScopedSurfaces`. Lighting by occupied space, camera occlusion with physical ceilings retained, HLOD, RenderOrigin submissions using whole-metre anchors, feather tessellation and goldens, #1300 captures, `FarOriginHlodRenderAndPick_MeetPrecisionContract` |
| R6/R7 | Underground markers and layer-aware foliage consume selected support and space keys under their own policies |
| R9/R10 | Authoring layers that group and select records, one editing experience over Task 16/17 transactions, floor/ceiling/space selection, portal and hole diagnostics, mixed-lattice refinement findings, footprint straddle guidance (offer an aligned region or a footprint split from the named footprint and cell, never automatic subdivision), scoped transactions and halo acquisition (#1302), re-anchoring gestures, history, MCP verbs and wire schemas, affected nav tile reporting |
| R11 and G2 | Refreeze the actual shipped world at import time, exhaustive ledger, named semantic acceptance. Declare `LegacyExteriorV1` (D9) on imported exterior footprints whose plane holds void or NoDraw cells, and prove they validate. Negative regions, the overhanging bridge and parapets, gate rotation and tree LOD facts are stale until that refreeze. R2's private counts are not promised to survive it |
| G1 to G5 | Released-pin adoption, importer run, server and client switch (G3.4 consumes #458 producer facts with pivot), TileWorld deletion, owner playtest and release batch. 8 MiB nav budget enforced at G3 |

---

## Verification conventions

Run once per execution session from the worktree root, before Task 1:

```bash
test "$(git branch --show-current)" = "feature/wa-r2-caves-scale"
test -f /tmp/grimhollow-orch/slot-run.sh
wa_r2_log_dir="/tmp/grimhollow-orch/logs/wa-r2-$(date +%Y%m%dT%H%M%S)-$$"
mkdir -p "$wa_r2_log_dir"
MAPDOC=KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj
EDITOR=KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj
COMPAT=KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj
HARNESS=KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj
RUMP=KhaozEngine.Tests/KhaozEngine.Tests.csproj
wa_run()  { n=$1; shift; bash /tmp/grimhollow-orch/slot-run.sh "wa-r2-$n" "$wa_r2_log_dir/$n.log" -- "$@"; echo "exit $? for $n"; }
wa_test() { wa_run "$1" dotnet test "$2" -c Release --filter "$3"; }
```

Each run is one invocation with its own log name. `wa_run` reports the exit and never retries. Exit 75 means the slot was busy and nothing ran: record "not run" and wait for the coordinator. A red step is valid only when the named tests fail for the stated reason, or the build fails only because a type this task introduces does not exist. A green step needs exit 0, zero failed tests and a nonzero matching test count read from the log. A filter matching nothing is a failure.

Every commit step is `git add -- <listed paths>`, then `git diff --cached --check`, then `git commit -m "<message>"`. Each task lists only its paths and message.

**Test code shape.** Test blocks are compact executable C#: each method's body with its exact assertions. Omitted only: `using` lines, the wrapper `public sealed class <file name>` in namespace `KhaozEngine.Tests.MapDoc` (MapDoc and MapEditor tests), `KhaozEngine.Tests.MapDocCompatibility` or `KhaozEngine.Tests.MapDocOracle`, and `[Fact]` on parameterless methods (theories show their attributes). Test methods are `public void`. Fixture helpers are defined in the task that first lists them.

---

### Task 1: #1310 generation witness and stale-window refusal

**Files:**
- Modify: `KhaozEngine.MapDoc/MapTileIndex.cs` (constructor line 27, new property), `KhaozEngine.MapDoc/MapTiledFile.cs` (`ReadManifest` lines 22 to 87, `Load` line 124), `KhaozEngine.MapDoc/MapTiledFile.Save.cs` (`Save` lines 22 to 113, `ReadPrevious` lines 246 to 269)
- Test: `KhaozEngine.MapEditor.Tests/MapDoc/MapTiledStaleWindowTests.cs`

**Interfaces:**
- Consumes existing: `MapDocumentFile.SaveTiled`, `LoadTiled(string, MapTileRect, ...)`, `VerifyTiled`, `SaveAs`, `MapDocumentSource.OpenTiled/FromDocument/Refresh/Tiles/Manifest`, `NativeDocumentSnapshot.Clone(MapDocument, MapDocRegistry)`, `TiledDocFixture.SampleDoc()`, `InDirectory(Action<string>)`, `TileFiles(string)` (`TiledDocFixture.cs:18,79,101`). `SampleDoc` uses 512 m tiles: `p-a` (10, 20) in tile (0, 0), `p-c` (-600, 20) in tile (-2, 0), `p-c.Kind == "hut"`, `p-a.Yaw == 0.5f`.
- Produces: `public string? MapTileIndex.ManifestSha256 { get; }`, null for an in-memory index. Internal constructor gains a trailing `string? manifestSha256 = null`.
- Produces behavior: a save of a document whose `Tiles.IsPartial` is true throws `MapDocumentException` containing `stale window` when the current manifest is absent, unreadable or digests differently from `ManifestSha256`. The check runs under the save lock, after the existing source-directory and tile-size guards, before `DeleteQuietly(manifest temp)`, `ReadPrevious`, any payload write and any sweep.

- [ ] **Step 1: Write the reproduction test against released behavior**

```csharp
static readonly MapTileRect OriginWindow = new(new MapTileCoord(0, 0), new MapTileCoord(0, 0));
static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
static MapPlacement P(MapDocument doc, string id) => doc.Placements.Single(p => p.Id == id);
static MapDocument SaveAndOpenWindow(string dir) { MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), dir); return MapDocumentFile.LoadTiled(dir, OriginWindow); }
static void ReplaceUnloadedTile(string dir) { MapDocument whole = MapDocumentFile.LoadTiled(dir); P(whole, "p-c").Kind = "barn"; MapDocumentFile.SaveTiled(whole, dir); }
static string SaveRefusal(MapDocument doc, string dir) => Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(doc, dir)).Message;

public void StaleWindow_RefusesAfterAnotherWriterReplacedAnUnloadedTile() => TiledDocFixture.InDirectory(dir =>
{
    string manifest = Path.Combine(dir, "map.json");
    MapDocument window = SaveAndOpenWindow(dir);
    ReplaceUnloadedTile(dir);
    byte[] newer = File.ReadAllBytes(manifest);
    var newerFiles = TiledDocFixture.TileFiles(dir);
    P(window, "p-a").Yaw = 2.75f;
    Assert.Contains("stale window", SaveRefusal(window, dir));
    Assert.Equal(newer, File.ReadAllBytes(manifest));
    Assert.Equal(newerFiles, TiledDocFixture.TileFiles(dir));
    Assert.Empty(MapDocumentFile.VerifyTiled(dir));
    MapDocument reloaded = MapDocumentFile.LoadTiled(dir);
    Assert.Equal(("barn", 0.5f), (P(reloaded, "p-c").Kind, P(reloaded, "p-a").Yaw));
});
```

- [ ] **Step 2: Run it to record the defect**

Run: `wa_test t1-repro "$EDITOR" "FullyQualifiedName~MapTiledStaleWindowTests"`
Expected: FAIL with `Assert.Throws() Failure: No exception was thrown`. Copy the failure lines into Outcome as the deterministic #1310 reproduction. If it passes, stop and report that the defect is not reproduced as analysed.

- [ ] **Step 3: Add the protocol tests**

```csharp
public void StaleWindow_RefusesBeforeTouchingTheManifestTemp() => TiledDocFixture.InDirectory(dir =>
{
    MapDocument window = SaveAndOpenWindow(dir);
    ReplaceUnloadedTile(dir);
    string temp = Path.Combine(dir, "map.json.tmp");
    File.WriteAllText(temp, "left by a crashed writer");
    Assert.Contains("stale window", SaveRefusal(window, dir));
    Assert.Equal("left by a crashed writer", File.ReadAllText(temp));
});

[Theory, InlineData("garbage"), InlineData("missing")]
public void StaleWindow_RefusesWhenTheManifestIsUnreadableOrMissing(string fault) => TiledDocFixture.InDirectory(dir =>
{
    MapDocument window = SaveAndOpenWindow(dir);
    string manifest = Path.Combine(dir, "map.json");
    if (fault == "garbage") File.WriteAllText(manifest, "{"); else File.Delete(manifest);
    var files = TiledDocFixture.TileFiles(dir);
    P(window, "p-a").Yaw = 2.75f;
    Assert.Contains("stale window", SaveRefusal(window, dir));
    Assert.Equal(files, TiledDocFixture.TileFiles(dir));
    Assert.Equal(fault == "garbage", File.Exists(manifest));
    if (fault == "garbage") Assert.Equal("{", File.ReadAllText(manifest));
});

public void CurrentWindow_SavesAndRefreshesItsGenerationWitness() => TiledDocFixture.InDirectory(dir =>
{
    string manifest = Path.Combine(dir, "map.json");
    MapDocument window = SaveAndOpenWindow(dir);
    string loaded = Sha256(manifest);
    Assert.Equal(loaded, window.Tiles!.ManifestSha256);
    P(window, "p-a").Yaw = 2.75f;
    MapDocumentFile.SaveTiled(window, dir);
    Assert.True(window.Tiles!.IsPartial);
    Assert.Equal(Sha256(manifest), window.Tiles.ManifestSha256);
    Assert.NotEqual(loaded, window.Tiles.ManifestSha256);
    P(window, "p-a").Yaw = 3.5f;
    MapDocumentFile.SaveTiled(window, dir);
    MapDocument whole = MapDocumentFile.LoadTiled(dir);
    Assert.Equal((3.5f, "hut"), (P(whole, "p-a").Yaw, P(whole, "p-c").Kind));
});

public void GenerationWitness_FlowsThroughEveryManifestReaderAndTransactionClone() => TiledDocFixture.InDirectory(dir =>
{
    string manifest = Path.Combine(dir, "map.json");
    MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), dir);
    string first = Sha256(manifest);
    MapDocumentSource source = MapDocumentSource.OpenTiled(dir);
    Assert.Equal((first, first), (source.Tiles.ManifestSha256, source.Manifest.Tiles!.ManifestSha256));
    MapDocument whole = MapDocumentFile.LoadTiled(dir);
    Assert.Equal(first, whole.Tiles!.ManifestSha256);
    Assert.Same(whole.Tiles, NativeDocumentSnapshot.Clone(whole, MapDocRegistry.CreateDefault()).Tiles);
    P(whole, "p-b").Yaw = 1.25f;
    MapDocumentFile.SaveTiled(whole, dir);
    source.Refresh();
    Assert.Equal(Sha256(manifest), source.Tiles.ManifestSha256);
    Assert.NotEqual(first, source.Tiles.ManifestSha256);
    Assert.Null(MapDocumentSource.FromDocument(TiledDocFixture.SampleDoc()).Tiles.ManifestSha256);
});

public void WholeDocumentAndSaveAsKeepReleasedBehaviour() => TiledDocFixture.InDirectory(dir =>
{
    MapDocumentFile.SaveTiled(TiledDocFixture.SampleDoc(), dir);
    MapDocument older = MapDocumentFile.LoadTiled(dir), newer = MapDocumentFile.LoadTiled(dir);
    P(newer, "p-c").Kind = "barn";
    MapDocumentFile.SaveTiled(newer, dir);
    P(older, "p-a").Yaw = 2.75f;
    MapDocumentFile.SaveTiled(older, dir);                        // whole documents: last writer wins, no refusal
    Assert.Equal("hut", P(MapDocumentFile.LoadTiled(dir), "p-c").Kind);
    TiledDocFixture.InDirectory(other =>
    {
        string copy = Path.Combine(other, "copy");
        MapDocumentFile.SaveAs(older, copy, MapDocumentForm.Tiled);
        Assert.Empty(MapDocumentFile.VerifyTiled(copy));
    });
});
```

The existing `MapTiledConcurrentSaveTests` stay green unchanged.

- [ ] **Step 4: Implement the witness and the refusal**

`ReadManifest` reads the bytes once with `File.ReadAllBytes`, digests them, decodes with a BOM-detecting `StreamReader` over those bytes (the decoding `File.ReadAllText` applies) and passes the digest into the index. `Load` passes it into the rebuilt window index. `Save` reads the current manifest bytes once under the lock into `byte[]? current`, runs the check beside the existing partial guards, and makes `ReadPrevious` parse those same bytes. After `WriteManifest` it digests the temp file's bytes into the index built at line 112.

- [ ] **Step 5: Run green with the existing tiled suites**

Run: `wa_test t1-green "$EDITOR" "FullyQualifiedName~MapTiledStaleWindowTests|FullyQualifiedName~MapTiledConcurrentSaveTests|FullyQualifiedName~MapTiledFileTests|FullyQualifiedName~MapTiledDurabilityTests|FullyQualifiedName~MapTiledFormatTests"`
Expected: PASS, 7 new cases plus every existing case in those classes.

- [ ] **Step 6: Commit** `KhaozEngine.MapDoc/MapTileIndex.cs KhaozEngine.MapDoc/MapTiledFile.cs KhaozEngine.MapDoc/MapTiledFile.Save.cs KhaozEngine.MapEditor.Tests/MapDoc/MapTiledStaleWindowTests.cs`, message `fix(mapdoc): refuse a stale tiled window before any save mutation`. The body carries `Closes #1310` only after the controller confirms that disposition. Tile-entry windowed no-loss is claimed from this commit, surfaces from Task 7.

---

### Task 2: Frozen format-4 expectations, the oracle harness project and CI exclusion guards (6a, part 1)

**Files:**
- Create: `KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj` (references `KhaozEngine.MapDoc` and `KhaozEngine.TileWorld`, xUnit packages as in `KhaozEngine.MapDoc.Tests`, `<IsPackable>false</IsPackable>`, RootNamespace `KhaozEngine.Tests.MapDocOracle`, links `../KhaozEngine.MapDoc.Tests/NativeFixtures.cs`, `NativeAssetFixtures.cs` and `FormatFourFixtures.cs` with `<Compile Include=... Link=...>`). Not added to `KhaozEngine.slnx`.
- Create: `KhaozEngine.MapDoc.OracleHarness.Tests/README.md` (purpose, the D1 boundary, exact commands, never run by CI, never commits shipped content), `FormatFourRecorder.cs`
- Create (generated once, then committed): `KhaozEngine.MapDoc.Tests/Fixtures/FormatFour/native-monolithic.mapdoc.json`, `native-tiled/**`, `resolver-v1-expectations.json`
- Create: `KhaozEngine.MapDoc.Tests/FormatFourFixtures.cs`, `AssertFixtures.cs`, `FormatFourResolverExpectationTests.cs`
- Create: `KhaozEngine.Tests/ArchitectureTests.MapDoc.cs` (partial `ArchitectureTests`)
- Modify: `KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj` (`<None Include="Fixtures/**" CopyToOutputDirectory="PreserveNewest" />`)

**Interfaces:**
- Consumes existing: `MapDocumentFile.LoadText/SaveText/Save/SaveTiled/Load/LoadTiled/VerifyTiled`, `MapResolver.Resolve(MapDocument, MapAssetClosure, Func<float,float,float>, MapResolveOptions)`, `MapAuthoredIdentity.Compute(MapDocument, MapAssetClosure, MapResolveOptions)`, `NativeFixtures.AnalyticV3Json()`, `NativeAssetFixtures.Valid()`, and in `ArchitectureTests.cs` the private statics `RepoRoot()` (line 732), `LoadGraph()` (line 736), record `Project(Name, IsPackableLibrary, ProjectRefs, PackageRefs, TargetFrameworks, FrameworkRefs)` (line 720) and `TransitiveClosure(string, IReadOnlyDictionary<string, Project>)` (line 777). Project names are full csproj stems such as `KhaozEngine.MapDoc`.
- Produces test-only (`internal static class FormatFourFixtures`): `Build() -> (MapDocument Document, MapAssetClosure Assets, MapResolveOptions Options)`, `MonolithicPath`, `TiledDirectory`, `LoadExpectations() -> ResolverExpectations`, `Assets()`, `Options = new MapResolveOptions("headless", 1, "options")`, `SupportHeight(float x, float z) => x * 0.25f + z * 0.5f`, `ResolveRecording(MapDocument, out IReadOnlyList<(float X, float Z)> calls) -> MapResolvedDocument`, `AssertMatchesExpectations(MapResolvedDocument, IReadOnlyList<(float X, float Z)>)` (ids, kinds, assets, numeric ids, five float bit patterns, tags and call bits against `LoadExpectations()`, without the hash), `PinnedFixtureDigest`, `ComputeFixtureDigest()` (SHA-256 over UTF-8 lines `relativePath TAB sha256 LF`, ordinal, every file under `Fixtures/FormatFour`).
- Produces test-only (`internal static class AssertFixtures`): `Sha256(string path) -> string` (lower hex of the file bytes).
- Produces test-only records: `ResolverExpectations(string AuthoredHashFormatFour, IReadOnlyList<ExpectedPlacement> Placements, IReadOnlyList<ExpectedCall> Calls)`, `ExpectedPlacement(string Id, string Kind, string AssetId, long? NumericId, uint XBits, uint YBits, uint ZBits, uint YawBits, uint ScaleBits, IReadOnlyList<string> Tags)`, `ExpectedCall(uint XBits, uint ZBits)`.
- Fixture document: `LoadText(AnalyticV3Json())`, then `Id = "r2-format-four"`, `ResolverIdentity = new(1, 1)`, `NativeAssets = NativeAssetFixtures.Valid().Roots`, `NumericIdHighWaterMark = 13`. Placement `a` is the migrated `old-inn` renamed `a`, `AssetId "tree"`, `NumericId 11`, `Y null`. Add `b` (`Kind "scenery"`, `AssetId "tree"`, `NumericId 12`, X 70, Z -70, Y 2.5, tags `first`, `second`) and `c` (`Kind "scenery"`, `AssetId "tree"`, `NumericId 13`, X 10, Z 10, Y null). Tile size 64 puts `a` in (-1, 0), `c` in (0, 0), `b` in (1, -2).

- [x] **Step 1: Write the expectation and guard tests**

```csharp
// FormatFourResolverExpectationTests (MapDoc.Tests)
public void FormatFourFixtures_ReproduceTheRecordedReleasedResolver()
{
    ResolverExpectations e = FormatFourFixtures.LoadExpectations();
    MapResolvedDocument r = FormatFourFixtures.ResolveRecording(MapDocumentFile.Load(FormatFourFixtures.MonolithicPath), out var calls);
    Assert.Equal(new[] { "a", "b", "c" }, r.Placements.Select(p => p.PlacementId));
    Assert.Equal(new[] { 2.5f, 2.5f, 7.5f }, r.Placements.Select(p => p.Transform.Position.Y));
    Assert.Equal(new[] { (-30f, 20f), (10f, 10f) }, calls);
    FormatFourFixtures.AssertMatchesExpectations(r, calls);
    Assert.Equal(e.AuthoredHashFormatFour, r.AuthoredHash);      // Task 6 flips only this line to NotEqual
}
public void FormatFourFixtures_AreImmutable() => Assert.Equal(FormatFourFixtures.PinnedFixtureDigest, FormatFourFixtures.ComputeFixtureDigest());
public void FormatFourTiledFixture_MatchesTheMonolithicWorld()
{
    Assert.Empty(MapDocumentFile.VerifyTiled(FormatFourFixtures.TiledDirectory));
    Assert.Equal(MapDocumentFile.SaveText(MapDocumentFile.Load(FormatFourFixtures.MonolithicPath)),
                 MapDocumentFile.SaveText(MapDocumentFile.LoadTiled(FormatFourFixtures.TiledDirectory)));
}

// ArchitectureTests.MapDoc.cs (KhaozEngine.Tests, the rump)
const string Harness = "KhaozEngine.MapDoc.OracleHarness.Tests";
public void OracleHarness_IsOutsideTheSolutionAndBothCiTestSelections()
{
    string root = RepoRoot();
    var slnxPaths = Regex.Matches(File.ReadAllText(Path.Combine(root, "KhaozEngine.slnx")), "Path=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
    Assert.DoesNotContain(slnxPaths, p => p.Contains(Harness, StringComparison.Ordinal));                     // full path builds and tests the slnx
    Assert.Equal(new[] { "KhaozEngine.slnx" }, Directory.EnumerateFiles(root, "*.sln*").Select(Path.GetFileName)); // bare dotnet resolves to it
    Assert.Empty(Directory.EnumerateFiles(root, "*.csproj"));
    string selective = File.ReadAllText(Path.Combine(root, "scripts", "ci-selective-test.sh"));
    Assert.Contains("SLNX=\"KhaozEngine.slnx\"", selective);                                                   // selective intersects with slnx test paths
    Assert.Contains("grep -oE 'Path=\"[^\"]+\"' \"$SLNX\"", selective);
    Assert.DoesNotContain(Harness, selective);
    Assert.DoesNotContain(Harness, File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml")));
    Assert.True(File.Exists(Path.Combine(root, Harness, Harness + ".csproj")));
}
public void OracleHarness_IsUnreferencedAndNotPackable()
{
    IReadOnlyDictionary<string, Project> graph = LoadGraph();
    Assert.DoesNotContain(graph.Values, p => p.ProjectRefs.Contains(Harness));
    Assert.False(graph[Harness].IsPackableLibrary);
    Assert.Contains(XDocument.Load(Path.Combine(RepoRoot(), Harness, Harness + ".csproj")).Root!.Descendants("IsPackable"),
        e => string.Equals((string?)e, "false", StringComparison.OrdinalIgnoreCase));
}
public void TileWorld_ReachesMapDocProjectsOnlyThroughTheTwoOracleTestProjects()
{
    IReadOnlyDictionary<string, Project> graph = LoadGraph();
    string[] allowed = { "KhaozEngine.MapDoc.Compatibility.Tests", Harness };
    foreach (Project p in graph.Values.Where(p => p.Name.StartsWith("KhaozEngine.MapDoc", StringComparison.Ordinal) || p.Name.StartsWith("KhaozEngine.MapEdit", StringComparison.Ordinal)))
        if (TransitiveClosure(p.Name, graph).Contains("KhaozEngine.TileWorld")) Assert.Contains(p.Name, allowed);
}
```

The rump parses every top-level `*.csproj` (`LoadGraph`), so the harness is also checked by every existing architecture rule. The selective path adds the rump whenever a `.csproj` changes (`ci-selective-test.sh:133`), so these guards run on any harness project edit. At `ca13d62d7` no MapDoc or MapEdit project reaches TileWorld transitively, so the containment guard starts green.

- [x] **Step 2: Run red**

Run: `wa_test t2-red-mapdoc "$MAPDOC" "FullyQualifiedName~FormatFourResolverExpectationTests"`
Expected: build FAIL naming `FormatFourFixtures` and `ResolverExpectations`.
Run: `wa_test t2-red-rump "$RUMP" "FullyQualifiedName~ArchitectureTests"`
Expected: FAIL in `OracleHarness_IsOutsideTheSolutionAndBothCiTestSelections` and `OracleHarness_IsUnreferencedAndNotPackable` because the harness csproj does not exist yet. Every other architecture test passes.

- [x] **Step 3: Implement fixtures, the harness project and the recorder, then record once**

`FormatFourRecorder.Record_FormatFourFixtures` requires `KHAOZ_R2_RECORD_DIR` and throws `set KHAOZ_R2_RECORD_DIR` when absent (never skips). It asserts `MapDocumentFile.CurrentFormatVersion == 4`, writes the monolithic file, the tiled directory and the expectations JSON (float bit patterns as unsigned integers) and prints `fixture digest <64 hex>`. These fixtures are synthetic and public, so printing is allowed here and nowhere in the private tests. Before recording, confirm `git diff a87038f5a HEAD -- KhaozEngine.MapDoc/MapResolver.cs KhaozEngine.MapDoc/MapAuthoredIdentity.cs` is empty, so the recording is the released resolver.

Run: `wa_run t2-harness-build dotnet build "$HARNESS" -c Release`
Run: `wa_run t2-record env KHAOZ_R2_RECORD_DIR="$PWD/KhaozEngine.MapDoc.Tests/Fixtures/FormatFour" dotnet test "$HARNESS" -c Release --no-build --filter "FullyQualifiedName~FormatFourRecorder"`
Expected: both exit 0, the second prints the digest. Pin it as `FormatFourFixtures.PinnedFixtureDigest`.

- [x] **Step 4: Run green, including harness format**

Run: `wa_test t2-green-mapdoc "$MAPDOC" "FullyQualifiedName~FormatFourResolverExpectationTests"` (PASS, 3 tests)
Run: `wa_test t2-green-rump "$RUMP" "FullyQualifiedName~ArchitectureTests"` (PASS, 3 new tests plus every existing architecture case)
Run: `wa_run t2-harness-format dotnet format "$HARNESS" --verify-no-changes --no-restore` (exit 0)

- [x] **Step 5: Commit** `KhaozEngine.MapDoc.OracleHarness.Tests KhaozEngine.MapDoc.Tests/Fixtures/FormatFour KhaozEngine.MapDoc.Tests/FormatFourFixtures.cs KhaozEngine.MapDoc.Tests/AssertFixtures.cs KhaozEngine.MapDoc.Tests/FormatFourResolverExpectationTests.cs KhaozEngine.MapDoc.Tests/KhaozEngine.MapDoc.Tests.csproj KhaozEngine.Tests/ArchitectureTests.MapDoc.cs`, message `test(mapdoc): freeze format-4 resolver expectations and fence the oracle harness`.

---

### Task 3: Public legacy oracle, private input guard, entry boundary and private inventory (6a, part 2)

**Files:**
- Create: `KhaozEngine.MapDoc.Compatibility.Tests/KhaozEngine.MapDoc.Compatibility.Tests.csproj` (references `KhaozEngine.MapDoc` and `KhaozEngine.TileWorld` only, RootNamespace `KhaozEngine.Tests.MapDocCompatibility`), `LegacyOracleWorld.cs`, `LegacyOracleWorldTests.cs`
- Create: `KhaozEngine.MapDoc.OracleHarness.Tests/ShippedSourceProvenance.cs`, `PrivateOracleInputs.cs`, `PrivateOracleReport.cs`, `PrivateOracleEntry.cs`, `PrivateOracleFixtures.cs`, `PrivateOracleGuardTests.cs`, `ShippedSourceInventory.cs`, `ShippedSourceInventoryTests.cs`
- Modify: `KhaozEngine.slnx` (add the Compatibility project beside `KhaozEngine.MapDoc.Tests`)

**Interfaces:**
- Consumes existing TileWorld authoring: `TileWorldDocument.GetOrCreateRegion`, `SetCornerHeightCm`, `SetUnderlay`, `SetOverlay`, `SetOverlayShape`, `SetOverlayRotation`, `SetSettings`, `TileGroundTriangles.TryDescribe(TileWorldDocument, int worldX, int worldZ, int plane, out TileGroundCell, Span<TileLatticeTriangle>)` (`TileGroundCell(Cut, Rotation, SplitSwNe, TriangleCount)`), `TileTriangulation.MaxTriangles`, `TileGroundTriangles.Build(TileWorldDocument, RegionCoord, int)`, `TileWorldFile.Load(string, TileWorldLoadOptions?)`.
- Produces (public): `LegacyOracleWorld.Create() -> LegacyOracleWorld` with `TileWorldDocument Document` and `IReadOnlyList<LegacyOracleCase> Cases`, `LegacyOracleCase(string Name, int WorldX, int WorldZ, int Plane)`. Regions (-1, 0) and (0, 0). Cases: `cut-<shape>-<rotation>` (16, overlay 5), `diagonal-no-overlay-<rotation>` (4, authored `DiagonalHalf` with overlay 0), `flag-blocked`, `flag-indoors`, `flag-bridge`, `flag-nodraw`, `flag-featheroverlay`, `void`, `derived-plane-1`, `override-plane-2`, `seam-west`, `seam-east`, and `extreme` (corners 32767, -32768, 0, 1).
- Produces (harness, public code, no data): `ShippedSourceProvenance` loaded from a JSON file with members `sourceRepository`, `tag`, `commit`, `sourceEnginePin`, `extractionPathspec`, `paths` (`{ path, sha256 }`), `aggregateSha256`, `aggregateRule` (`sha256 of UTF-8 lines path TAB sha256 LF, ordinal by path`), `units`, `rowOrientation`, `comparison`, `oracleEquivalence`. Methods `static Load(string path)`, `ComputeAggregate() -> string`, `Verify(string extractedRoot) -> int` (count of missing, extra or mismatched files, never their names in an exception).
- Produces (harness): `sealed class PrivateOracleInputs` with `SourceRoot`, `Provenance`, `ReportPath`, `static Require(IReadOnlyDictionary<string, string?> environment)`, `static FromEnvironment(Func<string, string?> read)` (reads exactly the four names in the D1 order into a `Dictionary<string, string?>`, then `Require`) and `static FromProcess() => FromEnvironment(Environment.GetEnvironmentVariable)`. `Require` throws `PrivateOracleInputException` with exactly one of: `missing <NAME>` (first missing in D1 order, null counts as missing), `provenance unreadable` (any IO or JSON failure while reading it), `provenance digest mismatch`, `report path inside a git work tree` (a `.git` entry in the report directory or an ancestor), `report path already exists`, `source verification failed: <n> files`. Never a value, path or inner exception.
- Produces (harness): `sealed class PrivateOracleReport : IDisposable` with `static Open(string path)` (parent mode must be exactly `0700`, `FileMode.CreateNew`, `UnixCreateMode = UserRead | UserWrite`, any failure throws `PrivateOracleFailure("private oracle: report could not be created securely")`), `Record(string category, string detailJson)`, `Check(string category, bool ok, string detailJson)`, `ThrowIfAnyFailed()` (flushes, closes, then throws `PrivateOracleFailure("private oracle: <n> <category> mismatches, report sha256 <hex>")` for the first failing category in first-seen order), `Sha256`. `PrivateOracleFailure` and `PrivateOracleInputException` are sealed `Exception` types whose constructors take only a message.
- Produces (harness): `static class PrivateOracleEntry` with `Run(IReadOnlyDictionary<string, string?> environment, Action<PrivateOracleInputs, PrivateOracleReport> body)` and `Run(Action<PrivateOracleInputs, PrivateOracleReport> body)` (uses `FromProcess`). Order: `Require`, `PrivateOracleReport.Open(inputs.ReportPath)`, then `body` inside `try`. Any exception from `body` is recorded as category `exception` (type, message, stack) and rethrown as `PrivateOracleFailure("private oracle: exception failure, report sha256 <hex>")` with no inner exception. After a normal return it calls `ThrowIfAnyFailed()`.
- Produces (harness test-only): `PrivateOracleFixtures.InSecureTemp(Action<string>)` (a fresh `0700` directory under `Path.GetTempPath()`, deleted afterwards), `SyntheticEnvironment(string root, string secret) -> Dictionary<string, string?>` (writes `root/<secret>/src/region.txt` with text `synthetic`, a matching provenance file at `root/<secret>/provenance.json` with one path, creates `root/out` with mode `0700`, returns the four variables with report `root/out/report.json`), `Throw(string fault, string sourceRoot)` (throws `FileNotFoundException`, `JsonException`, `InvalidDataException` or `OverflowException` for `filesystem`, `json`, `loader` or `arithmetic`, each with message `"at " + sourceRoot`).
- Produces (harness): `ShippedSourceInventory.Build(string root) -> ShippedSourceInventory` (per-plane counts of drawable cells, overlays, each cut, each flag bit, void and NoDraw cells, authored upper-plane corners, signed region keys and distinct corners), written only to the private report.

- [x] **Step 1: Write the public oracle test and the harness guard tests**

```csharp
// LegacyOracleWorldTests (Compatibility.Tests, public)
public void OracleWorld_ExercisesEveryOperativeTopologyClass()
{
    LegacyOracleWorld w = LegacyOracleWorld.Create();
    var triangles = new TileLatticeTriangle[TileTriangulation.MaxTriangles];
    bool Describe(string name, out TileGroundCell cell)
    {
        LegacyOracleCase c = w.Cases.Single(x => x.Name == name);
        return TileGroundTriangles.TryDescribe(w.Document, c.WorldX, c.WorldZ, c.Plane, out cell, triangles);
    }
    var cuts = w.Cases.Where(c => c.Name.StartsWith("cut-", StringComparison.Ordinal)).ToList();
    Assert.Equal(16, cuts.Count);
    Assert.Equal(8, cuts.Count(c => Describe(c.Name, out TileGroundCell cell) && cell.TriangleCount == 4));
    for (int r = 0; r < 4; r++)
    {
        Assert.True(Describe($"diagonal-no-overlay-{r}", out TileGroundCell d));
        Assert.Equal((TileOverlayShape.Full, r % 2 == 0), (d.Cut, d.SplitSwNe));
    }
    Assert.False(Describe("void", out _));
    Assert.False(Describe("flag-nodraw", out _));
    Assert.Contains(w.Cases, c => c.WorldX == -1);
    Assert.Contains(w.Cases, c => c.Plane == 1);
    Assert.Contains(w.Cases, c => c.Plane == 2);
}

// PrivateOracleGuardTests (harness, synthetic only, no private data)
static readonly string[] Names = { "KHAOZ_R2_SHIPPED_SOURCE", "KHAOZ_R2_ORACLE_PROVENANCE", "KHAOZ_R2_ORACLE_PROVENANCE_SHA256", "KHAOZ_R2_ORACLE_REPORT" };
const string Secret = "SECRET-7f3a";
static T Refuse<T>(Action act) where T : Exception { T e = Assert.Throws<T>(act); Assert.Null(e.InnerException); Assert.DoesNotContain(Secret, e.ToString()); return e; }

public void Require_FailsOnEveryMissingVariableWithoutEchoingValues() => PrivateOracleFixtures.InSecureTemp(root =>
{
    Dictionary<string, string?> full = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
    foreach (string name in Names)
    {
        var env = new Dictionary<string, string?>(full) { [name] = null };
        Assert.Equal($"missing {name}", Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env)).Message);
    }
    full["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = new string('0', 64);
    PrivateOracleInputException mismatch = Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(full));
    Assert.Equal("provenance digest mismatch", mismatch.Message);
    Assert.DoesNotContain(root, mismatch.ToString());
});

[Theory, InlineData("missing"), InlineData("garbage")]
public void Require_ProvenanceReadFailuresAreSanitized(string fault) => PrivateOracleFixtures.InSecureTemp(root =>
{
    Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
    string provenance = env["KHAOZ_R2_ORACLE_PROVENANCE"]!;
    if (fault == "missing") File.Delete(provenance);
    else { File.WriteAllText(provenance, "{" + Secret); env["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = AssertFixtures.Sha256(provenance); }
    Assert.Equal("provenance unreadable", Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env)).Message);
});

public void Require_RefusesAReportPathInsideAGitWorkTreeOrAlreadyPresent() => PrivateOracleFixtures.InSecureTemp(root =>
{
    Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
    Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));
    Directory.CreateDirectory(Path.Combine(root, "repo", "out"));
    env["KHAOZ_R2_ORACLE_REPORT"] = Path.Combine(root, "repo", "out", "r.json");
    Assert.Equal("report path inside a git work tree", Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env)).Message);
    string existing = Path.Combine(root, "out", "existing.json");
    File.WriteAllText(existing, "{}");
    env["KHAOZ_R2_ORACLE_REPORT"] = existing;
    Assert.Equal("report path already exists", Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.Require(env)).Message);
});

public void FromEnvironment_ReadsOnlyTheFourNamedVariables()
{
    var asked = new List<string>();
    Assert.Equal("missing KHAOZ_R2_SHIPPED_SOURCE",
        Refuse<PrivateOracleInputException>(() => PrivateOracleInputs.FromEnvironment(n => { asked.Add(n); return null; })).Message);
    Assert.Equal(Names, asked);
}

public void Report_FailureMessageCarriesOnlyCategoryCountAndDigest() => PrivateOracleFixtures.InSecureTemp(root =>
{
    string path = Path.Combine(root, "out", "r.json");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.SetUnixFileMode(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    using PrivateOracleReport report = PrivateOracleReport.Open(path);
    report.Check("vertex", false, "{\"x\":12.5}");
    report.Check("vertex", false, "{\"x\":12.5}");
    PrivateOracleFailure e = Assert.Throws<PrivateOracleFailure>(report.ThrowIfAnyFailed);
    Assert.Matches("^private oracle: 2 vertex mismatches, report sha256 [0-9a-f]{64}$", e.Message);
    Assert.DoesNotContain("12.5", e.ToString());
    Assert.Contains("12.5", File.ReadAllText(path));
    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
});

[Theory, InlineData("filesystem"), InlineData("json"), InlineData("loader"), InlineData("arithmetic")]
public void Entry_SanitizesEveryFailureRoute(string fault) => PrivateOracleFixtures.InSecureTemp(root =>
{
    Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
    PrivateOracleFailure e = Refuse<PrivateOracleFailure>(() => PrivateOracleEntry.Run(env, (inputs, _) => PrivateOracleFixtures.Throw(fault, inputs.SourceRoot)));
    Assert.Matches("^private oracle: exception failure, report sha256 [0-9a-f]{64}$", e.Message);
    Assert.Contains(Secret, File.ReadAllText(env["KHAOZ_R2_ORACLE_REPORT"]!));
});

public void Entry_FailsClosedWhenTheReportCannotBeCreatedSecurely() => PrivateOracleFixtures.InSecureTemp(root =>
{
    Dictionary<string, string?> env = PrivateOracleFixtures.SyntheticEnvironment(root, Secret);
    File.SetUnixFileMode(Path.Combine(root, "out"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
    bool ran = false;
    Assert.Equal("private oracle: report could not be created securely",
        Refuse<PrivateOracleFailure>(() => PrivateOracleEntry.Run(env, (_, _) => ran = true)).Message);
    Assert.False(ran);
    Assert.False(File.Exists(env["KHAOZ_R2_ORACLE_REPORT"]!));
});
```

- [x] **Step 2: Run red**

Run: `wa_test t3-red-compat "$COMPAT" "FullyQualifiedName~LegacyOracleWorldTests"` (build FAIL naming `LegacyOracleWorld`)
Run: `wa_test t3-red-harness "$HARNESS" "FullyQualifiedName~PrivateOracleGuardTests"` (build FAIL naming `PrivateOracleInputs` and `PrivateOracleEntry`)

- [ ] **Step 3: Implement the public oracle, the guard, the entry, the report and the inventory**

`Require` checks in this order: all four variables present, provenance bytes read (an IO failure is `provenance unreadable`), their digest compared, the JSON parsed (a failure is `provenance unreadable`), the report path checked, the extraction verified. Every exception inside `Require` is caught and mapped to one of its fixed messages.

`ShippedSourceInventoryTests.ShippedSourceInventory_VerifiesAndInventoriesTheFrozenSource` is `PrivateOracleEntry.Run((inputs, report) => { ... })`: `Require` has already verified the extraction against the provenance, the body loads with `TileWorldFile.Load`, records the inventory through `report.Record`, and calls `report.Check("inventory", regions.Count > 0, "{}")`. Counts are recorded privately, never compared with historical fixed counts. The harness `AssertFixtures` reference is the linked `KhaozEngine.MapDoc.Tests/AssertFixtures.cs` (add the `<Compile Include ... Link>` line).

- [ ] **Step 4: Run green, then the private inventory**

Run: `wa_test t3-green-compat "$COMPAT" "FullyQualifiedName~LegacyOracleWorldTests"` (PASS, 1 test)
Run: `wa_test t3-green-harness "$HARNESS" "FullyQualifiedName~PrivateOracleGuardTests"` (PASS, 11 cases)
Run: `wa_run t3-harness-format dotnet format "$HARNESS" --verify-no-changes --no-restore` (exit 0)

Private run, controller only, with inputs from the private Grimhollow proofs record:

```bash
mkdir -p /tmp/grimhollow-orch/private && chmod 700 /tmp/grimhollow-orch/private
priv="$(mktemp -d /tmp/grimhollow-orch/private/wa-r2-XXXXXX)"; chmod 700 "$priv"
git -C /Users/antonio/Grimhollow archive "<commit from the private manifest>" <pathspec from the private manifest> | tar -x -C "$priv" && mkdir -m 700 "$priv/out"
wa_run t3-private env KHAOZ_R2_SHIPPED_SOURCE="$priv" KHAOZ_R2_ORACLE_PROVENANCE="<private manifest path>" \
  KHAOZ_R2_ORACLE_PROVENANCE_SHA256="<digest from the private record>" KHAOZ_R2_ORACLE_REPORT="$priv/out/inventory.json" \
  dotnet test "$HARNESS" -c Release --filter "FullyQualifiedName~ShippedSourceInventory"
```

Expected: exit 0. Record the report digest in the private `RUNS.md` and only "inventory passed, private record <Grimhollow commit>" in engine Outcome. Delete the extraction after recording. A missing or mismatched input is a failure to report, never a skip. The slot log of this run contains only sanitized messages by construction, and stays under `/tmp/grimhollow-orch`, never in a public artifact.

- [ ] **Step 5: Commit** `KhaozEngine.MapDoc.Compatibility.Tests KhaozEngine.MapDoc.OracleHarness.Tests KhaozEngine.slnx`, message `test(mapdoc): add the public legacy oracle and a sanitized private oracle entry`.

---

### Task 4: Exact scalars, lattice frame, vertex addresses, patch, cell and bounded codec

**Files:**
- Create: `KhaozEngine.MapDoc/Surfaces/MapRational.cs`, `MapExactValue.cs`, `MapLatticeAddress.cs`, `MapLatticeFrame.cs`, `MapPatchKey.cs`, `MapSurfaceCell.cs`, `MapSurfacePatch.cs`, `MapSurfacePatchCodec.cs`, `MapSurfaceRef.cs`
- Test: `KhaozEngine.MapDoc.Tests/Surfaces/SurfacePatchCodecTests.cs`, `LatticeAddressTests.cs`, fixture `SurfacePatchFixtures.cs`

**Interfaces (all new, namespace `KhaozEngine.MapDoc.Surfaces`):**
- `public readonly struct MapRational : IEquatable<MapRational>` with `public MapRational(int numerator, int denominator)` that throws `MapDocumentException` containing `positive` for a non-positive part and `reduced` for an unreduced pair, and get-only `Numerator`, `Denominator`. Any positive reduced pair is a legal unit.
- `public sealed class MapExactOverflowException : ArithmeticException`, message contains `overflow`.
- `public readonly struct MapExactValue : IEquatable<MapExactValue>, IComparable<MapExactValue>` with `public MapExactValue(long numerator, long denominator)` that normalizes (positive denominator, reduced, zero denominator throws `ArgumentException`), get-only `Numerator`, `Denominator` (stored as denominator minus one, so `default` is exactly zero), `Add`, `Subtract`, `Multiply`, `Divide`, `Negate`, `Floor() -> long`, `Ceiling() -> long`, `Sign`, `ToSingle()` (correctly rounded), `ToDouble()`, `ToString()` (`"n"` when the denominator is 1, else `"n/d"`), `static FromSingle(float)` (the exact binary value, `ArgumentException` containing `finite` for NaN or infinity, `MapExactOverflowException` when the reduced value does not fit). Intermediates are `Int128`. A reduced result that does not fit `long` throws `MapExactOverflowException`.
- `public readonly record struct MapExactPoint(MapExactValue X, MapExactValue Y, MapExactValue Z)`, `public readonly record struct MapExactXz(MapExactValue X, MapExactValue Z) : IComparable<MapExactXz>` (Z, then X).
- `public readonly struct MapLatticeAddress : IEquatable<MapLatticeAddress>, IComparable<MapLatticeAddress>` with a private constructor, get-only `long X`, `long Z`, `int Denominator`, `public const int MaxDenominator = 64`, `static Create(long x, long z, int denominator)` (reduces by `gcd(|x|, |z|, d)`, refuses `d < 1` or a reduced `d > 64` with `denominator`), `static Corner(long x, long z)`, ordering by exact Z then exact X position. `default` is invalid and every consumer refuses it.
- `public enum MapRowDirection : byte { PositiveZ = 0, NegativeZ = 1 }`, `public enum MapHeightDatum : byte { WorldY0 = 0 }`.
- `public sealed record MapLatticeFrame(MapRational CellUnitMetres, MapRational HeightUnitMetres, MapRowDirection RowDirection, MapHeightDatum Datum)` with `static ImportedMetreCentimetre` = (1/1, 1/100, NegativeZ, WorldY0), `MapExactXz WorldXz(MapLatticeAddress a)` (`X = a.X/a.D * unit`, `Z = +/- a.Z/a.D * unit`), `MapExactValue Metres(MapExactValue heightUnits)`, `MapExactPoint Corner(long x, long z, int heightUnits)` and `MapLatticeAddress AddressOf(MapExactValue worldX, MapExactValue worldZ)` (refuses `denominator` beyond 64). No commensurability method exists (D7).
- `public readonly record struct MapPatchKey(string SurfaceId, long SlotX, long SlotZ) : IComparable<MapPatchKey>`, order `SurfaceId` ordinal, then `SlotZ`, then `SlotX`, `const int SlotCells = 64`, `static ForCell(string, long cellX, long cellZ)` with floor division.
- `public enum MapOverlayCut : byte { Full, DiagonalHalf, CornerQuarter, CornerThreeQuarter }`, `[Flags] public enum MapCellFlags : byte { None = 0, Blocked = 1, Indoor = 2, LegacyBridge = 4, NoDraw = 8, FeatherOverlay = 16 }` (bits 5 to 7 invalid, equal to `TileSettings`), `public enum MapCellTopology : byte { Auto, ForceSwNe, ForceNwSe }`, `public readonly record struct MapSurfaceCell(ushort Underlay, ushort Overlay, MapOverlayCut Cut, byte Rotation, MapCellFlags Flags, MapCellTopology Topology)`.
- `public enum MapCellEdge : byte { South, East, North, West }`, `public sealed record MapVertexOwner(MapPatchKey Patch, MapLatticeAddress Address)` (address in the owner surface's lattice), `public sealed record MapCornerDependency(int CornerX, int CornerZ, MapVertexOwner Owner)`, `public sealed record MapEdgeSubdivision(int CellX, int CellZ, MapCellEdge Edge, int Segments)` (inserted vertices at `i/Segments` of the full cell edge, `0 < i < Segments`).
- `public sealed partial class MapSurfacePatch` with `MapPatchKey Key`, `int CellMinX, CellMinZ, Width, Depth` (`0 <= CellMin`, `1 <= size <= 64`, `CellMin + size <= 64`), `int[] Heights` (`(Width+1)*(Depth+1)`, index `z*(Width+1)+x`), `MapSurfaceCell[] Cells`, `ulong[] Presence` (bit `z*Width+x`, unused bits zero), `List<MapCornerDependency> CornerDependencies`, `List<MapEdgeSubdivision> EdgeSubdivisions`, `IsPresent`, `SetPresent`, `Height(int cornerX, int cornerZ)`, `MapLatticeAddress CornerAddress(int cornerX, int cornerZ)` (absolute lattice corner), `Clone()` (deep) and `IReadOnlyList<string> ValidateLocal()`: subdivision segments outside 2 to 64 (`segments`), a duplicate `(cell, edge)` (`duplicate`), a subdivided edge whose other side is a present cell inside the rectangle (`interior`), a subdivision on an absent cell (`absent`), a dependency corner outside the patch (`dependency`).
- `public static class MapSurfacePatchCodec` with `MaxEncodedBytes = 1_048_576`, `byte[] Encode(MapSurfacePatch)` (canonical compact UTF-8 JSON, fixed member order `key, cellMinX, cellMinZ, width, depth, heights, cells, presence, cornerDependencies, edgeSubdivisions, records`, key as `{ "surfaceId", "slotX", "slotZ" }`, int64 as decimal strings, addresses as `{ "x": "<long>", "z": "<long>", "d": <int> }`, heights, cells and presence as base64 little-endian), `MapSurfacePatch Decode(ReadOnlySpan<byte>, MapPatchKey expectedKey)` refusing before allocation on oversize input, dimensions out of range, wrong base64 lengths (`heights` checked before `cells`), unknown members (`unknown member '<name>'`), nonzero unused presence bits, invalid flags or rotation. `records` is an empty array until Task 5.
- `public enum MapSurfaceRole : byte { SupportFloor, Ceiling, PaintOverride }`, `public enum MapPresencePolicy : byte { Native, LegacyTileWorld }`, `public sealed record MapRecordRef(string Id, MapPatchKey Anchor)`, `public sealed record MapIndoorSpan(string Id, MapRecordRef ParentSpace, int LowerOffsetUnits, int UpperOffsetUnits, IReadOnlyList<string> DomainTags)`, `public sealed record MapSurfaceRef(string Id, MapLatticeFrame Frame, MapSurfaceRole Role, MapPresencePolicy PresencePolicy, string? PaintTargetSurfaceId, MapIndoorSpan? IndoorSpan, string SemanticSha256)`. An indoor span makes the imported Indoor mask of that surface a nested domain of `ParentSpace`, spanning `[surface height + Lower, surface height + Upper)` in its height unit. Its values are an R11 import decision.

**Fixture.** `SurfacePatchFixtures.Sample()`: key `("ground", -1, 0)`, `CellMinX 60`, `CellMinZ 0`, width 4, depth 2, heights `{ -50000, 0, 1433, 1331, 1363, 518, int.MaxValue, int.MinValue, 7, 8, 9, 10, 11, 12, 13 }`, cells in order: `(14,0,Full,0,None,Auto)`, `(3,5,DiagonalHalf,1,FeatherOverlay,Auto)`, `(3,5,CornerQuarter,2,Blocked|Indoor,Auto)`, `(0,0,Full,0,NoDraw|LegacyBridge,Auto)`, `(65535,65535,CornerThreeQuarter,3,None,ForceNwSe)`, `(1,0,Full,0,None,ForceSwNe)`, `(1,0,Full,0,None,Auto)` twice, presence `{ 0b1111_1101UL }`. `Row(int cells)` is surface `r`, slot (0, 0), one row of `cells` present `Full` cells, heights 0.

- [ ] **Step 1: Write the failing tests**

```csharp
// SurfacePatchCodecTests
public void Patch_RoundTripsExactIntegersAndSignedSlots()
{
    MapSurfacePatch p = SurfacePatchFixtures.Sample();
    byte[] bytes = MapSurfacePatchCodec.Encode(p);
    MapSurfacePatch back = MapSurfacePatchCodec.Decode(bytes, p.Key);
    Assert.Equal(p.Heights, back.Heights);
    Assert.Equal(p.Cells, back.Cells);
    Assert.Equal((false, true), (back.IsPresent(1, 0), back.IsPresent(0, 1)));
    Assert.Equal(MapLatticeAddress.Corner(-4, 0), back.CornerAddress(0, 0));
    Assert.Equal(bytes, MapSurfacePatchCodec.Encode(back));
    Assert.Contains("\"slotX\":\"-1\"", Encoding.UTF8.GetString(bytes));
}
public void PatchKey_FloorsNegativeCellsIntoSlots()
{
    Assert.Equal(new MapPatchKey("g", -1, -1), MapPatchKey.ForCell("g", -1, -64));
    Assert.Equal(new MapPatchKey("g", -2, 0), MapPatchKey.ForCell("g", -65, 63));
    Assert.Equal(new MapPatchKey("g", 1, 0), MapPatchKey.ForCell("g", 64, 0));
    Assert.True(new MapPatchKey("a", 5, 5).CompareTo(new MapPatchKey("b", -9, -9)) < 0);
    Assert.True(new MapPatchKey("a", 9, -1).CompareTo(new MapPatchKey("a", 0, 0)) < 0);
}
[Theory, InlineData("width", "\"width\":4", "\"width\":65"), InlineData("heights", "\"depth\":2", "\"depth\":3"),
 InlineData("presence", "/QAAAAAAAAA=", "/QAAAAABAAA="), InlineData("unknown", "\"width\":4", "\"width\":4,\"extra\":1")]
public void Decode_RefusesMalformedPayloadBeforeAllocation(string member, string from, string to)
{
    string text = Encoding.UTF8.GetString(MapSurfacePatchCodec.Encode(SurfacePatchFixtures.Sample()));
    Assert.Contains(from, text);
    byte[] bad = Encoding.UTF8.GetBytes(text.Replace(from, to));
    Assert.Contains(member, Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Decode(bad, SurfacePatchFixtures.Sample().Key)).Message);
}
public void Decode_RefusesOversizedInputUnreducedUnitsAndOverflow()
{
    Assert.Contains("1048576", Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Decode(new byte[1_048_577], SurfacePatchFixtures.Sample().Key)).Message);
    Assert.Contains("reduced", Assert.Throws<MapDocumentException>(() => new MapRational(2, 200)).Message);
    Assert.Contains("positive", Assert.Throws<MapDocumentException>(() => new MapRational(0, 1)).Message);
    Assert.Equal(new MapExactPoint(new(4, 1), new(1433, 100), new(-64, 1)), MapLatticeFrame.ImportedMetreCentimetre.Corner(4, 64, 1433));
    Assert.Contains("overflow", Assert.Throws<MapExactOverflowException>(() => new MapExactValue(long.MaxValue, 1).Multiply(new(2, 1))).Message);
}

// LatticeAddressTests
public void ExactValue_NormalizesAndConvertsFloatsExactlyOrRefuses()
{
    Assert.Equal(new MapExactValue(1, 2), new MapExactValue(-2, -4));
    Assert.Equal(new MapExactValue(0, 1), default(MapExactValue));
    Assert.Equal(("1/2", "-3"), (new MapExactValue(2, 4).ToString(), new MapExactValue(-6, 2).ToString()));
    Assert.Equal(new MapExactValue(13421773, 134217728), MapExactValue.FromSingle(0.1f));
    Assert.Throws<MapExactOverflowException>(() => MapExactValue.FromSingle(1e-30f));
    Assert.Contains("finite", Assert.Throws<ArgumentException>(() => MapExactValue.FromSingle(float.NaN)).Message);
    Assert.Equal((2L, 3L), (new MapExactValue(5, 2).Floor(), new MapExactValue(5, 2).Ceiling()));
    Assert.Equal((-3L, -2L), (new MapExactValue(-5, 2).Floor(), new MapExactValue(-5, 2).Ceiling()));
}
public void Address_ReducesAndOneWorldVertexMatchesAcrossUnits()
{
    Assert.Equal(MapLatticeAddress.Create(1, 2, 2), MapLatticeAddress.Create(2, 4, 4));
    Assert.Equal(MapLatticeAddress.Corner(1, 0), MapLatticeAddress.Create(3, 0, 3));
    var coarse = new MapLatticeFrame(new(1, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
    var half = new MapLatticeFrame(new(1, 2), new(1, 200), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
    var third = new MapLatticeFrame(new(1, 3), new(1, 300), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
    var sixtyFourth = new MapLatticeFrame(new(1, 64), new(1, 6400), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
    Assert.Equal(coarse.WorldXz(MapLatticeAddress.Create(4, 3, 3)), third.WorldXz(MapLatticeAddress.Corner(4, 3)));
    Assert.Equal(coarse.WorldXz(MapLatticeAddress.Create(127, 64, 64)), sixtyFourth.WorldXz(MapLatticeAddress.Corner(127, 64)));
    Assert.Equal(half.WorldXz(MapLatticeAddress.Corner(2, 0)), third.WorldXz(MapLatticeAddress.Corner(3, 0)));       // 1 m on both
    Assert.Equal(new MapExactValue(1, 2), third.WorldXz(MapLatticeAddress.Create(3, 0, 2)).X);                       // 1.5 third-cells
    Assert.Equal(MapLatticeAddress.Create(4, 3, 3), coarse.AddressOf(new(4, 3), new(1, 1)));
    Assert.Equal(new MapExactXz(new(4, 3), new(-1, 1)), MapLatticeFrame.ImportedMetreCentimetre.WorldXz(MapLatticeAddress.Create(4, 3, 3)));
    Assert.Contains("denominator", Assert.Throws<MapDocumentException>(() => coarse.AddressOf(new(1, 128), new(0, 1))).Message);
    Assert.Throws<MapDocumentException>(() => MapLatticeAddress.Create(1, 0, 65));
}
public void Subdivision_LegalOnlyOnThePatchBoundaryOrAPresenceRim()
{
    MapSurfacePatch p = SurfacePatchFixtures.Row(3);
    p.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.West, 3));
    p.EdgeSubdivisions.Add(new(2, 0, MapCellEdge.East, 64));
    Assert.Empty(p.ValidateLocal());
    p.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.East, 2));
    Assert.Contains(p.ValidateLocal(), f => f.Contains("interior"));
    p.SetPresent(1, 0, false);
    Assert.Empty(p.ValidateLocal());                                 // cell 1 is now a rim, cell 0 East is legal
    p.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.East, 2));
    Assert.Contains(p.ValidateLocal(), f => f.Contains("duplicate"));
    p.EdgeSubdivisions[^1] = new(0, 0, MapCellEdge.South, 65);
    Assert.Contains(p.ValidateLocal(), f => f.Contains("segments"));
}
```

- [ ] **Step 2: Run red.** `wa_test t4-red "$MAPDOC" "FullyQualifiedName~SurfacePatchCodecTests|FullyQualifiedName~LatticeAddressTests"`. Expected: build FAIL naming `MapSurfacePatch`, `MapExactValue` and `MapLatticeAddress`.
- [ ] **Step 3: Implement the types and the codec.**
- [ ] **Step 4: Run green.** `wa_test t4-green "$MAPDOC" "FullyQualifiedName~SurfacePatchCodecTests|FullyQualifiedName~LatticeAddressTests"`. Expected: PASS, 10 cases.
- [ ] **Step 5: Commit** `KhaozEngine.MapDoc/Surfaces KhaozEngine.MapDoc.Tests/Surfaces`, message `feat(mapdoc): add exact scalars, rational lattice addresses and bounded surface patches`.

---

### Task 5: Anchored topology and space records, references and semantic digests

**Files:**
- Create: `KhaozEngine.MapDoc/Surfaces/MapTopologyRecords.cs`, `MapSurfacePatch.Records.cs`, `MapTopologyReferenceValidator.cs`, `MapSurfaceSemantics.cs`, `KhaozEngine.MapDoc/Spaces/MapSpaceRecords.cs`, `KhaozEngine.MapDoc/Spaces/MapLegacyExteriorRecipe.cs`
- Modify: `KhaozEngine.MapDoc/Surfaces/MapSurfacePatchCodec.cs` (`records` member, `type` discriminator `seam, chain, strip, portal, opening, link, space, footprint`)
- Test: `KhaozEngine.MapDoc.Tests/Surfaces/TopologyRecordTests.cs`, fixture `TopologyRecordFixtures.cs`

**Interfaces (new):**
- `public abstract record MapTopologyRecord(string Id)`. Ids are document-unique (ordinal). A record lives in exactly one patch's `List<MapTopologyRecord> Records` list (`MapSurfacePatch.Records.cs`), its anchor. Creation anchors it in the lowest-ordinal incident patch. The anchor need not stay incident. Moving it is an explicit re-anchor edit (Task 17) that updates every referrer.
- `public readonly record struct MapLatticeVertex(string SurfaceId, MapLatticeAddress Address)`, `public sealed record MapSurfaceEdgeRef(MapPatchKey Patch, MapLatticeVertex From, MapLatticeVertex To)`, `public sealed record MapSurfaceSeam(string Id, MapSurfaceEdgeRef First, MapSurfaceEdgeRef Second, IReadOnlyList<(MapLatticeVertex First, MapLatticeVertex Second)> Pairs) : MapTopologyRecord(Id)`.
- `public enum MapChainKind : byte { SurfaceEdge, Authored }`, `public sealed record MapChainVertex(MapLatticeVertex Vertex, int? HeightUnits)` (null for `SurfaceEdge`, required for `Authored`), `public sealed record MapBoundaryChain(string Id, MapChainKind Kind, MapPatchKey? SourcePatch, IReadOnlyList<MapChainVertex> Vertices) : MapTopologyRecord(Id)` (2 to 4,097 vertices).
- `public enum MapStripFacing : byte { Front, Back, TwoSided }`, `public sealed record MapWallStrip(string Id, MapRecordRef LowerChain, MapRecordRef UpperChain, MapStripFacing Facing, ushort MaterialId) : MapTopologyRecord(Id)`.
- `public sealed record MapCavePortal(string Id, MapRecordRef FromSpace, MapRecordRef ToSpace, IReadOnlyList<MapLatticeVertex> Interval, MapRecordRef BandBottom, MapRecordRef? BandTop) : MapTopologyRecord(Id)` (null top only when both spaces are `Exterior`).
- `public sealed record MapHorizontalOpening(string Id, MapPatchKey Patch, IReadOnlyList<int> SlotCells) : MapTopologyRecord(Id)` (slot-relative `z*64 + x`, ascending, unique, inside the rectangle, presence 0).
- `public sealed record MapVerticalLink(string Id, MapRecordRef UpperSpace, MapRecordRef LowerSpace, IReadOnlyList<MapRecordRef> Openings, IReadOnlyList<MapRecordRef> Portals, IReadOnlyList<MapRecordRef> GeometryOwners) : MapTopologyRecord(Id)`.
- In `KhaozEngine.MapDoc.Spaces`: `public enum MapSpaceKind : byte { Cave, Exterior }`, `public enum MapSide : byte { Front, Back }`, `public sealed record MapBoundaryRef(MapRecordRef Record, MapSide Side)`, `public sealed record MapSpaceDoc(string Id, MapSpaceKind Kind, MapRecordRef? Parent, MapRecordRef? AliasOf, IReadOnlyList<string> DomainTags, IReadOnlyList<MapBoundaryRef> Walls, IReadOnlyList<MapBoundaryRef> Portals, IReadOnlyList<MapRecordRef> Links) : MapTopologyRecord(Id)`, `public enum MapBoundKind : byte { SupportFloor, Ceiling, HorizontalOpening, OpenTop, LegacyExteriorV1 }` (D9, lower bound only), `public sealed record MapBoundRef(MapBoundKind Kind, string? SurfaceId, MapRecordRef? Opening)`, `public sealed record MapSpaceFootprint(string Id, MapRecordRef Space, MapPatchKey Lattice, IReadOnlyList<int> SlotCells, MapBoundRef Lower, MapBoundRef Upper) : MapTopologyRecord(Id)`. `Lattice` names the frame and slot whose cells the footprint uses. A portal appears as `(portal, Front)` in its FromSpace's `Portals` and `(portal, Back)` in its ToSpace's.
- `public static class MapTopologyReferenceValidator { public static IReadOnlyList<string> Validate(IReadOnlyList<MapSurfaceRef> surfaces, IReadOnlyCollection<MapSurfacePatch> patches); }`, complete-view only. Findings: duplicate ids (`duplicate id`), a reference whose anchor patch does not hold that id (`anchor`, naming the id), a dangling reference (naming the missing id), a reference to the wrong record type (`type`), a `(record, side)` used twice (`duplicate`), portal and link lists inconsistent with their records (`portal list`, `link list`), `OpenTop` on a cave (`open top`), opening over a present cell (`present`), a bound surface that does not exist or has the wrong role (`role`), alias targets that are not alias-free peers (`alias`), parent cycles (`cycle`), and a footprint failing `MapLegacyExteriorRecipe.Check` (`<rule>: footprint '<id>'`, where the rule text begins `legacy recipe`). Any rational lattice is a legal bound reference (D7). Geometry belongs to Tasks 10, 12A and 12B.
- `public static class MapLegacyExteriorRecipe` in `KhaozEngine.MapDoc/Spaces/MapLegacyExteriorRecipe.cs`, the one home of the D9 recipe: `public const string PolicyId = "kemap/legacy-exterior/1"` and `public static string? Check(MapSpaceFootprint footprint, MapSpaceDoc space, MapSurfaceRef? lowerSurface)`, returning null when neither bound is `LegacyExteriorV1` or the recipe holds, otherwise the first D9 rule text (`legacy recipe: space`, `upper`, `surface` or `lattice`). It reads only its arguments, so the reference validator and the classifier (Task 12A) share one rule.
- `public static class MapSurfaceSemantics` with `string PatchDigest(MapSurfacePatch)` (domain `kemap/surface-patch/1`, records ordered by id, lists canonical, independent of JSON whitespace) and `string SurfaceDigest(MapSurfaceRef, IEnumerable<KeyValuePair<MapPatchKey, string>>)` (domain `kemap/surface/1`, excludes `SemanticSha256`).

**Fixture.** `TopologyRecordFixtures.TwoSpacesSharingOneStrip() -> TopologyWorld` where `sealed record TopologyWorld(List<MapSurfaceRef> Surfaces, List<MapSurfacePatch> Patches)` with `Validate() => MapTopologyReferenceValidator.Validate(Surfaces, Patches)`. Surfaces `floor` (SupportFloor, heights 0) and `ceiling` (Ceiling, heights 300), both `(1/1, 1/100, PositiveZ)`, slot (0, 0), cells x 0 to 1, z 0. Every record is anchored in `floor` (0, 0): spaces `a` and `b` (Cave), footprints `fragment-a` (slot cell 0) and `fragment-b` (slot cell 1) with lower `(SupportFloor, "floor", null)` and upper `(Ceiling, "ceiling", null)`, chains `lower` (`floor` edge at corner x 1) and `upper` (`ceiling` edge at x 1), strip `wall` (`TwoSided`) in `a.Walls` as `(wall, Front)` and `b.Walls` as `(wall, Back)`. `ReplaceWall(world, spaceId, MapBoundaryRef)`, `SetUpper(world, footprintId, MapBoundRef)`, `SetLowerKind(world, footprintId, MapBoundKind)` and `SetLowerSurface(world, footprintId, surfaceId, MapLatticeFrame)` (adds that `SupportFloor` surface ref if absent) rewrite in place. `LegacyExteriorWorld()` is surface `plane-0` (`ImportedMetreCentimetre`, `LegacyTileWorld`, `SupportFloor`), slot (0, 0), cells x 0 to 1, z 0, flat 0, with Exterior space `world` and footprint `world-cells` (lattice `plane-0` (0, 0), slot cells `[0, 1]`, lower `(LegacyExteriorV1, "plane-0", null)`, upper `OpenTop`), both anchored in `plane-0` (0, 0). `OpeningOverPresentCell(world)` adds an opening over present cell 0. `SampleWithRecords()` is `SurfacePatchFixtures.Sample()` plus strip `strip-b` (`TwoSided`, chain refs anchored at the sample key), `SurfaceEdge` chain `lower` and `Authored` chain `upper` (heights 300) at addresses `Corner(-4, 0)` and `Corner(-3, 0)`.

- [ ] **Step 1: Write the failing tests**

```csharp
static readonly MapPatchKey Floor00 = new("floor", 0, 0), Ceiling00 = new("ceiling", 0, 0);

public void Records_RoundTripThroughTheCodecAndDigestDeterministically()
{
    MapSurfacePatch p = TopologyRecordFixtures.SampleWithRecords();
    string digest = MapSurfaceSemantics.PatchDigest(p);
    Assert.Equal(digest, MapSurfaceSemantics.PatchDigest(MapSurfacePatchCodec.Decode(MapSurfacePatchCodec.Encode(p), p.Key)));
    p.Records.Reverse();
    Assert.Equal(digest, MapSurfaceSemantics.PatchDigest(p));
    p.Heights[2] = 1434;
    Assert.NotEqual(digest, MapSurfaceSemantics.PatchDigest(p));
}
public void ReferenceValidator_RefusesDuplicateSideDanglingAndWrongAnchor()
{
    TopologyWorld w = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
    Assert.Empty(w.Validate());
    TopologyRecordFixtures.ReplaceWall(w, "b", new(new("wall", Floor00), MapSide.Front));
    Assert.Contains(w.Validate(), f => f.Contains("duplicate"));
    TopologyRecordFixtures.ReplaceWall(w, "b", new(new("missing-wall", Floor00), MapSide.Back));
    Assert.Contains(w.Validate(), f => f.Contains("missing-wall"));
    TopologyRecordFixtures.ReplaceWall(w, "b", new(new("wall", Ceiling00), MapSide.Back));
    Assert.Contains(w.Validate(), f => f.Contains("anchor"));
}
public void ReferenceValidator_RefusesOpenTopOnACaveAndAPresentOpeningButAcceptsAnyRationalBound()
{
    TopologyWorld w = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
    TopologyRecordFixtures.SetUpper(w, "fragment-a", new(MapBoundKind.OpenTop, null, null));
    Assert.Contains(w.Validate(), f => f.Contains("open top"));
    w = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
    TopologyRecordFixtures.OpeningOverPresentCell(w);
    Assert.Contains(w.Validate(), f => f.Contains("present"));
    foreach (MapRational unit in new MapRational[] { new(1, 2), new(1, 3), new(2, 3), new(1, 64), new(5, 7) })
    {
        w = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
        TopologyRecordFixtures.SetLowerSurface(w, "fragment-a", "other", new MapLatticeFrame(unit, new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0));
        Assert.Empty(w.Validate());                                  // D7: any rational lattice is a legal bound reference
    }
}
public void ReferenceValidator_AcceptsTheLegacyExteriorTagOnlyInItsExactRecipe()
{
    Assert.Empty(TopologyRecordFixtures.LegacyExteriorWorld().Validate());
    TopologyWorld cave = TopologyRecordFixtures.TwoSpacesSharingOneStrip();
    TopologyRecordFixtures.SetLowerKind(cave, "fragment-a", MapBoundKind.LegacyExteriorV1);
    Assert.Contains(cave.Validate(), f => f.Contains("legacy recipe: space") && f.Contains("fragment-a"));        // never a finite cave floor
    TopologyWorld upper = TopologyRecordFixtures.LegacyExteriorWorld();
    TopologyRecordFixtures.SetUpper(upper, "world-cells", new(MapBoundKind.LegacyExteriorV1, "plane-0", null));
    Assert.Contains(upper.Validate(), f => f.Contains("legacy recipe: upper"));
    TopologyWorld native = TopologyRecordFixtures.LegacyExteriorWorld();
    TopologyRecordFixtures.SetLowerSurface(native, "world-cells", "native", new MapLatticeFrame(new(1, 1), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0));
    Assert.Contains(native.Validate(), f => f.Contains("legacy recipe: surface"));
    Assert.Equal(("kemap/legacy-exterior/1", (byte)4), (MapLegacyExteriorRecipe.PolicyId, (byte)MapBoundKind.LegacyExteriorV1));
}
public void Codec_RefusesARecordSetOverTheBound()
{
    MapSurfacePatch p = SurfacePatchFixtures.Row(1);
    for (int i = 0; i < 40; i++)
        p.Records.Add(new MapBoundaryChain($"c{i}", MapChainKind.Authored, null,
            Enumerable.Range(0, 4097).Select(x => new MapChainVertex(new MapLatticeVertex("r", MapLatticeAddress.Corner(x, 0)), 0)).ToList()));
    Assert.Contains("1048576", Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Encode(p)).Message);
}
```

- [ ] **Step 2: Run red.** `wa_test t5-red "$MAPDOC" "FullyQualifiedName~TopologyRecordTests"`. Expected: build FAIL naming `MapWallStrip`.
- [ ] **Step 3: Implement records, the codec member, the validator and digests.**
- [ ] **Step 4: Run green.** `wa_test t5-green "$MAPDOC" "FullyQualifiedName~TopologyRecordTests|FullyQualifiedName~SurfacePatchCodecTests|FullyQualifiedName~LatticeAddressTests"`. Expected: PASS, 15 cases.
- [ ] **Step 5: Commit** `KhaozEngine.MapDoc/Surfaces KhaozEngine.MapDoc/Spaces KhaozEngine.MapDoc.Tests/Surfaces`, message `feat(mapdoc): add anchored cave topology and space records with semantic digests`.

---

### Task 6: Format advance, support recipe and resolver-v1 preservation

**Files:**
- Create: `KhaozEngine.MapDoc/MapDocument.Surfaces.cs` (partials of `MapDocument` and `MapPlacement`), `MapSupportRecipe.cs`, `MapSurfaceMigration.cs`, `Surfaces/MapSurfaceSet.cs`
- Modify: `KhaozEngine.MapDoc/MapDocumentFile.cs:84` (`CurrentFormatVersion = 5`) and `:30-35` (register `4 -> MapSurfaceMigration.Upgrade`), `mapdoc.schema.json` (`formatVersion` const 5, `supportRecipe`, `surfaces`, placement `supportBinding`), `MapDocumentMembers.cs:113`, `MapCanonical.cs:72`, `MapTiledFile.cs:182` (`GlobalsOnly`), `MapNativeValidation.cs:8`, `MapBoundDocumentValidation.cs:27`, `MapAuthoredIdentity.cs:46-52`, `KhaozEngine.MapEditor/NativeDocumentSnapshot.cs:16` (`Publish` copies `SupportRecipe` and `Surfaces`), `KhaozEngine.MapDoc.Tests/FormatFourResolverExpectationTests.cs` (hash line becomes `Assert.NotEqual`), `KhaozEngine.MapDoc.Tests/FormatFourFixtures.cs` (two helpers below)
- Test: `KhaozEngine.MapDoc.Tests/FormatAdvanceTests.cs`

**Interfaces:**
- `public enum MapSupportRecipe { LegacyXzCallbackV1 = 1, AuthoredBindingsV2 = 2 }`, `public MapSupportRecipe MapDocument.SupportRecipe { get; set; } = LegacyXzCallbackV1`, written on every save.
- `[JsonIgnore] public MapSurfaceSet MapDocument.Surfaces { get; set; } = new();`. `MapSurfaceSet` has `List<MapSurfaceRef> Refs`, `SortedDictionary<MapPatchKey, MapSurfacePatch> Patches` (resident), `bool IsEmpty`, `Clone()` (deep), `IEnumerable<MapTopologyRecord> AllRecords()` (patch key order, then id) and `bool TryGetRecord(MapRecordRef, out MapTopologyRecord?)` (reads only the anchor's resident patch). Until Task 7 any writer refuses non-empty `Patches` with `surface storage requires Task 7`.
- `public enum MapSupportBindingKind : byte { Surface, Space }`, `public sealed record MapSupportBinding(MapSupportBindingKind Kind, string? SurfaceId, string? SpaceId, float? ReferenceY, float? SearchBelow, float? SearchAbove)`, `public MapSupportBinding? MapPlacement.SupportBinding` (omitted when null, so existing tile hashes do not change).
- `public static JsonObject MapSurfaceMigration.Upgrade(JsonObject)`: pure, 4 to 5 (5 is a no-op), writes `formatVersion` 5 and `supportRecipe` `LegacyXzCallbackV1`, adds nothing else.
- Validation: resolver `(1, 1)` (`MapResolverIdentityDoc(PayloadVersion, ResolverVersion)`) requires `LegacyXzCallbackV1`, no surfaces, no bindings. `(1, 2)` requires `AuthoredBindingsV2`. Explicit `Y` with a binding refuses. `MapBoundDocumentValidation.ValidateLocal` accepts exactly those pairs. `MapAuthoredIdentity.Compute` refuses resolver version 2 with a message containing `MapAuthoredIdentityV2`, so `MapResolver.Resolve` refuses before any callback. `MapDocumentHash.SchemeVersion` and its format-3 golden stay unchanged.
- Test-only additions to `FormatFourFixtures`: `CopyTiledToTemp() -> string` (fresh temp copy of the tiled fixture) and `TileFileDigests(string root) -> IReadOnlyList<string>` (ordinal `relativePath TAB sha256` lines over `tiles/`).

- [ ] **Step 1: Write the failing tests**

```csharp
static MapDocument Migrated() => MapDocumentFile.Load(FormatFourFixtures.MonolithicPath);

public void FormatAdvance_PreservesResolverV1Execution_Monolithic()
{
    MapDocument doc = Migrated();
    Assert.Equal((5, MapSupportRecipe.LegacyXzCallbackV1), (doc.FormatVersion, doc.SupportRecipe));
    MapResolvedDocument r = FormatFourFixtures.ResolveRecording(doc, out var calls);
    FormatFourFixtures.AssertMatchesExpectations(r, calls);
}
public void FormatAdvance_TiledWindowCarriesTheDocumentRecipeToUnloadedPlacements()
{
    string dir = FormatFourFixtures.CopyTiledToTemp();
    var tiles = FormatFourFixtures.TileFileDigests(dir);
    MapDocument window = MapDocumentFile.LoadTiled(dir, new MapTileRect(new(0, 0), new(0, 0)));
    Assert.DoesNotContain(window.Placements, p => p.Id == "a");
    MapDocumentFile.SaveTiled(window, dir);
    string manifest = File.ReadAllText(Path.Combine(dir, "map.json"));
    Assert.Contains("\"formatVersion\": 5", manifest);
    Assert.Contains("\"supportRecipe\": \"LegacyXzCallbackV1\"", manifest);
    Assert.Equal(tiles, FormatFourFixtures.TileFileDigests(dir));
    MapResolvedDocument r = FormatFourFixtures.ResolveRecording(MapDocumentFile.LoadTiled(dir), out var calls);
    FormatFourFixtures.AssertMatchesExpectations(r, calls);
}
public void FormatAdvance_IdentityTokenChangesExactlyOnce()
{
    MapDocument migrated = Migrated();
    string once = MapAuthoredIdentity.Compute(migrated, FormatFourFixtures.Assets(), FormatFourFixtures.Options);
    Assert.NotEqual(FormatFourFixtures.LoadExpectations().AuthoredHashFormatFour, once);
    Assert.Equal(once, MapAuthoredIdentity.Compute(MapDocumentFile.LoadText(MapDocumentFile.SaveText(migrated)), FormatFourFixtures.Assets(), FormatFourFixtures.Options));
}
public void FormatFourObjectInMemory_FailsCurrentValidation()
{
    MapDocument doc = Migrated();
    doc.FormatVersion = 4;
    Assert.Contains(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()), e => e.Contains("formatVersion is 4, expected 5"));
}
public void ResolverV1Overload_RefusesAVersionTwoDocumentBeforeAnyCallback()
{
    MapDocument doc = Migrated();
    doc.ResolverIdentity = new(1, 2);
    doc.SupportRecipe = MapSupportRecipe.AuthoredBindingsV2;
    int calls = 0;
    Assert.Throws<MapDocumentException>(() => MapResolver.Resolve(doc, FormatFourFixtures.Assets(), (x, z) => { calls++; return 0f; }, FormatFourFixtures.Options));
    Assert.Equal(0, calls);
}
[Theory, InlineData(1, MapSupportRecipe.AuthoredBindingsV2, false), InlineData(2, MapSupportRecipe.LegacyXzCallbackV1, false),
 InlineData(1, MapSupportRecipe.LegacyXzCallbackV1, true)]
public void SupportRecipe_PairsWithResolverVersionAndRefusesExplicitYWithBinding(int resolver, MapSupportRecipe recipe, bool bindExplicitY)
{
    MapDocument doc = Migrated();
    doc.ResolverIdentity = new(1, resolver);
    doc.SupportRecipe = recipe;
    if (bindExplicitY) doc.Placements.Single(p => p.Id == "b").SupportBinding = new(MapSupportBindingKind.Surface, "ground", null, null, null, null);
    Assert.NotEmpty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()));
}
public void AnalyticFormatFour_MigratesWithoutNativeAdditions()
{
    string text = MapDocumentFile.SaveText(MapDocumentFile.LoadText(NativeFixtures.AnalyticV3Json()));
    Assert.Contains("\"formatVersion\": 5", text);
    Assert.Contains("\"supportRecipe\": \"LegacyXzCallbackV1\"", text);
    Assert.Contains("\"surfaces\": []", text);
    Assert.DoesNotContain("supportBinding", text);
}
```

- [ ] **Step 2: Run red.** `wa_test t6-red "$MAPDOC" "FullyQualifiedName~FormatAdvanceTests"`. Expected: build FAIL naming `MapSupportRecipe`.
- [ ] **Step 3: Implement the advance.** The migration and the writers are the only places that learn format 5. `GlobalsOnly`, `WriteNativeGlobals` and `NativeDocumentSnapshot.Publish` all carry both new root members.
- [ ] **Step 4: Run green.** `wa_run t6-green-mapdoc dotnet test "$MAPDOC" -c Release` (whole project) and `wa_test t6-green-editor "$EDITOR" "FullyQualifiedName~KhaozEngine.Tests.MapDoc|FullyQualifiedName~MapEditTool"`. Expected: PASS (9 new cases). An existing test that hard-codes format-4 output text is a consumer-visible change: update it here and list each file in the commit body.
- [ ] **Step 5: Commit** `KhaozEngine.MapDoc KhaozEngine.MapEditor/NativeDocumentSnapshot.cs KhaozEngine.MapDoc.Tests KhaozEngine.MapEditor.Tests`, message `feat(mapdoc): advance to format 5 with a document-level legacy support recipe`. The release states that every format-4 token changes once on load and resolver-v1 execution is unchanged.

---

### Task 7: Writer-owned surface storage, unified completeness and generation-pinned sources

**Files:**
- Create: `KhaozEngine.MapDoc/Storage/MapSurfaceStorageLayout.cs`, `MapSurfaceIndexEntry.cs`, `MapSurfacePages.cs`, `MapSurfaceStorageIndex.cs`, `MapSurfaceTiledStore.cs`, `MapSurfaceEmbedding.cs`, `IMapSurfaceSource.cs`, `MapSurfaceScope.cs`, `MapDocumentSurfaceSource.cs`, `MapStoredSurfaceSource.cs`
- Modify: `KhaozEngine.MapDoc/MapTileIndex.cs` (`Surfaces`, unified `IsPartial`, `HasUnloadedTiles`), `MapTiledFile.Save.cs` (surface writes after tiles, combined keep set, carry-forward, dependency and unread-page guards, delegating to `MapSurfaceTiledStore` to stay under 800 lines), `MapTiledFile.cs` (read `surfaceStorage`, window pages and payloads, verify surface files), `MapDocumentFile.cs` (embed or refuse in `PrepareWholeWrite`, read `surfacePatches`), `MapDocumentSchema.cs`, `mapdoc.schema.json`, `Surfaces/MapSurfaceSemantics.cs` (`RootDigest`)
- Modify: `KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj` (`<Compile Include="../KhaozEngine.MapDoc.Tests/**/*Fixtures.cs" Link="MapDocFixtures/%(RecursiveDir)%(Filename)%(Extension)" />`, copy `../KhaozEngine.MapDoc.Tests/Fixtures/**` to `Fixtures/`)
- Test: `KhaozEngine.MapEditor.Tests/MapDoc/SurfaceStorageTests.cs`, `KhaozEngine.MapDoc.Tests/Storage/SparseSurfaceIndexTests.cs`, `SurfaceSourceSnapshotTests.cs`, `SurfaceStorageNamespaceTests.cs`, fixture `SurfaceStorageFixtures.cs`

**Interfaces (namespace `KhaozEngine.MapDoc.Storage` unless noted):**
- Layout: payloads `tiles/surfaces/p/<first two hex>/<sha256>.json`, index pages `tiles/surfaces/i/<sha256>.json`, directory pages `tiles/surfaces/d/<sha256>.json`, names are SHA-256 of canonical bytes. Surface payload and page writes invoke the existing `MapTiledSaveStep.BeforeTileWrite` and `AfterTileWrite` steps like tile files.
- `MapSlotRect(long MinX, long MinZ, long MaxXExclusive, long MaxZExclusive)`, `MapCellRect` (same shape), `MapSurfaceIndexEntry(MapPatchKey Key, MapCellRect Cells, int MinHeightUnits, int MaxHeightUnits, string PayloadSha256, string SemanticSha256, IReadOnlyList<MapPatchKey> Dependencies, IReadOnlyList<string> RecordIds, IReadOnlyList<MapRecordRef> IncidentRecords, IReadOnlyList<string> SpaceIds, bool Loaded)` (`IncidentRecords` is writer-maintained bookkeeping, excluded from semantic identity, and `VerifyTiled` reports a stale list as `incident`), `MapIndexPageRef(MapSlotRect Covers, string Sha256, int EntryCount)`, `MapDirectoryPageRef(string SurfaceId, MapSlotRect Covers, string Sha256)`. Default packing: index pages cover aligned 16 by 16 slot blocks, directory pages aligned 16 by 16 blocks of index pages. Internal `MapSurfacePacking(int IndexBlockSlots = 16, int DirectoryBlockPages = 16)` for repack tests, passed through a new internal overload `MapTiledFile.Save(MapDocument, string, MapDocRegistry, MapDocumentSaveOptions?, MapSurfacePacking? packing = null)`. Pages refuse over 1 MiB. Manifest member `surfaceStorage` is the `MapDirectoryPageRef` list. The document root never stores byte digests.
- `public sealed class MapSurfaceStorageIndex` with `Directory`, `ReadDirectoryPages`, `ReadIndexPages`, `int UnreadIndexPages`, `Entries`, `bool IsPartial` (unread directory page, unread index page or unloaded entry) and `MapPatchStatus StatusOf(MapPatchKey)` (`Present`, `Unloaded` when listed and not loaded or when a covering page is unread, `KnownEmpty` when every covering page was read and the key is unlisted or no directory page of that surface covers it).
- `MapTileIndex`: `public MapSurfaceStorageIndex? Surfaces { get; }` (null for monolithic and in-memory), `public bool HasUnloadedTiles` (old tile-only meaning) and `IsPartial => HasUnloadedTiles || Surfaces?.IsPartial == true` (D2). Its doc comment states the unified rule.
- Monolithic: root `surfacePatches` sorted by key. `internal static class MapSurfaceEmbedding` with `MaxPatches = 256`, `MaxEncodedBytes = 8_388_608L`, `internal static void Check(MapSurfaceSet, int maxPatches, long maxEncodedBytes)` refusing with `tiled`.
- `public enum MapPatchStatus { Present, KnownEmpty, Unloaded, Missing, Corrupt }`, `public sealed record MapPatchRead(MapPatchKey Key, MapPatchStatus Status, MapSurfacePatch? Patch, string? SemanticSha256, string? Detail, int PagesRead)`. Returned patches are fresh clones.
- `public sealed record MapQueryLimits(int MaxCandidatePatches = 256, int MaxInspectedFaces = 4096, int MaxSupportIntersections = 64, int MaxPageReads = 32, int MaxRecordReads = 64, int MaxRecordDepth = 8)`.
- `public sealed record MapSurfaceScope(WorldFrame Frame, Vector2 LocalMin, Vector2 LocalMax, float? MinY, float? MaxY, IReadOnlyList<MapSurfaceRole> Roles, IReadOnlyList<string>? SpaceIds, MapQueryLimits Limits)` with `string Digest` (domain `kemap/scope-request/1` over frame, float bit patterns, roles, spaces and limits). Non-finite or inverted bounds throw `ArgumentException`. Per surface, the local rectangle converts exactly to that surface's cells and expands by one of its cells.
- `public sealed record MapCoveredRange(string SurfaceId, MapSlotRect Slots)`, `public enum MapFindStatus { Complete, Incomplete, CapacityExceeded }`, `public sealed record MapPatchFindResult(MapFindStatus Status, MapSurfaceScope Scope, string SnapshotId, IReadOnlyList<MapPatchRead> Patches, IReadOnlyList<MapCoveredRange> KnownEmpty, IReadOnlyList<MapPatchRead> Unavailable, int PagesRead)`. `CapacityExceeded` returns no patches. `MapPatchRead` and `MapPatchFindResult` stay public records because source implementations construct them. A source is a trusted producer. The factory-only rule protects consumers, not sources.
- `public interface IMapSurfaceSource { string SnapshotId { get; } string RootSha256 { get; } IReadOnlyList<MapSurfaceRef> Surfaces { get; } MapPatchRead ReadPatch(MapPatchKey key); MapPatchFindResult FindPatches(MapSurfaceScope scope); }`.
- `MapSurfaceSemantics.RootDigest(MapDocument)` (domain `kemap/native-root/2`, the scheme-1 normalized root without `surfacePatches`, plus `supportRecipe` and surface refs with recomputed surface digests).
- `public sealed class MapDocumentSurfaceSource : IMapSurfaceSource` with `public static MapDocumentSurfaceSource Capture(MapDocument document)` only. It deep-copies refs, resident patches and the storage index at capture (D5). `SnapshotId` is `doc:` plus the SHA-256 of the root digest and the ordered patch digests at capture. `PagesRead` is always 0.
- `public sealed class MapStoredSurfaceSource : IMapSurfaceSource` with `public static MapStoredSurfaceSource Open(string tiledDirectory)`. It pins the manifest generation (D5): it reads the manifest once, `SnapshotId` is `manifest:` plus that byte digest, it reads only pages and payloads named by that generation, verifies bytes before decoding, recomputes semantic digests (`Corrupt` on mismatch), caches verified pages for its lifetime, counts decoded pages per call in `PagesRead`, and never writes, sweeps, repairs, lists directories or reads through `IMapAssetSource`. A file swept after `Open` reads `Missing`. It has no refresh.
- Windowed `LoadTiled` reads, per surface, the directory and index pages covering the window's world rectangle expanded by one cell, and loads every listed patch there.
- Save rules: validate, lock, Task 1's generation check (now driven by the unified `IsPartial`), the partial raw-save surface rules below, write payloads, index pages, directory pages, then rename the manifest. Sweep only after a readable previous manifest, keeping every tile file and every surface file reachable from the new manifest. Unread pages are carried forward by digest and unloaded payloads are never rewritten.
- **Partial raw saves (coordinator final review M3).** A raw save certifies reference integrity only, never geometry. Reference integrity means every corner-dependency owner, record reference and anchor in the new generation resolves to an existing patch and to a record of the right type, and that every corner dependency of a patch the save writes satisfies exact XZ owner-address agreement: the owner address is a vertex of the owner patch (a corner of a declared cell, including internal cell corners, or an explicitly subdivided cell-edge vertex within the patch rectangle) and has exactly the same world XZ as the dependent corner, computed from the two surface frames, the keys and the owner's rectangle and subdivisions, never from heights (`dependency position`). That agreement is the existing shared-vertex contract (D3, Task 10 `ValidateCornerDependencies` finding `position`), which native transactions already enforce. Every raw save now also enforces it as a reference-validity invariant, monolithic or tiled, on every new or changed patch and on every patch of a whole-document save. Height agreement, seams, strips, coverage and separation stay uncertified geometry, a native-transaction property (Tasks 16 and 17), and native transactions already refuse partial documents. For a partial document, each changed loaded patch P (new, deleted, or with a semantic digest different from its loaded entry) is checked in this order, every refusal before any file is written. (1) Outgoing: a dependency or record reference of P naming an unloaded patch refuses (`unloaded`), a new P under an unread index page refuses (`unloaded`), and a dependency of P that fails XZ agreement refuses (`dependency position`). (2) Reverse knowledge: on every surface, every index page covering P's exact world rectangle expanded by one cell of that surface must have been read, a surface with no covering directory page being known empty, otherwise `unloaded: reverse dependants unknown`. (3) Reverse dependants: a read index entry that is not loaded and lists P in `Dependencies`, or a record in P's entry `IncidentRecords` anchored in an unloaded patch, refuses `unloaded: reverse dependant '<patch key or record id>'`. The save does not guess which corners or edges that dependant uses, so any change to P refuses, never a partial rewrite. (4) Structure: P new or deleted, or P's `CellMinX`, `CellMinZ`, `Width`, `Depth`, `Presence`, `EdgeSubdivisions` or `CornerDependencies` different from its loaded payload, refuses `unsupported partial edit: structure`, and a surface ref removed or given a different `Frame` refuses `unsupported partial edit: surface`. Such an edit can remove a vertex, edge, record or key that an unloaded referrer names, reassign a vertex whose other incident patches are unloaded, or need an `IncidentRecords` list for a new key that unloaded records would contribute to, and window metadata cannot prove otherwise. Presence 0 is written only by native authoring anyway (D3). (5) Records: any change to P's `Records` (add, remove, modify or re-anchor) refuses `unsupported partial edit: records`. The comparison is against P's loaded payload and entry, so a deleted P counts as removing every record it held and never passes for lack of new records to compare. Referrers anchored in unread patches and document-wide id uniqueness cannot be checked from a window. Structure and topology edits go through native transactions on a complete document.
- **Why the reverse scan is complete.** Corner dependants: under XZ agreement, a valid dependant D of P on surface `s` has a dependent corner at the exact world XZ of a vertex of P, which lies in P's closed rectangle. That corner is a corner of at least one cell of D's rectangle, a square of side `u_s` (one cell of `s`) with a vertex in P's closed rectangle, so the cell overlaps P's rectangle expanded by `u_s` with positive area. The index page of D's slot covers that cell, so rule (2) has read it, and D's entry with its `Dependencies` is visible to rule (3). XZ agreement holds for every stored generation because every engine writer checks it on each patch it writes and carried patches were checked when written. It is never inferred from height agreement, and an unread page is never assumed empty. Record dependants whose geometry touches P are listed in P's own `IncidentRecords` (writer-maintained, `VerifyTiled` reports a stale list as `incident`), carried by P's loaded entry. Record references that are not spatial, a `MapRecordRef` to a record anchored in P held by a space, footprint, portal or link anchored anywhere, are not indexed in reverse and are never assumed spatial. They stay valid only because rules (4) and (5) keep P and every record id and anchor in it. What survives all five rules is a payload-only change: heights or cell bytes of an existing patch whose rectangle, presence, subdivisions, dependencies and records are unchanged. It removes no vertex, edge, record or key that any referrer names, so every reverse reference stays resolvable, and P's incident list carries over unchanged because no record and no XZ extent changed. The geometry a payload-only change implies for loaded or unloaded dependants (for example a dependent corner height that no longer matches) stays uncertified, as before. Retained carry-forward: a heights-only change whose covering pages are all read and whose reverse dependants are all loaded saves (the slot-0 window in `SurfaceStorage_WindowCarriesUnloadedPatchesAndRefusesUnloadedDependencies`), and unchanged payloads are never rewritten. Whole-document raw saves gain only the `dependency position` refusal. Every rule only adds a refusal, so no atomic seam, dependency or no-loss guarantee weakens.

**Fixture.** `SurfaceStorageFixtures.ThreeSurfaces()`: resolver-2 document (`(1, 2)`, `AuthoredBindingsV2`, the Task 2 asset roots, `TileSize = 64`, so one document tile is one slot), no placements. Surfaces `ground`, `ridge`, `far`, all `(1/1, 1/100, PositiveZ)`, `Native`, `SupportFloor`. `ground` slot (0, 0) is `CellMinX 60`, 4 by 4 cells at 1000. `ground` slot (1, 0) is a full-width strip, `CellMinX 0`, width 64, depth 4, at 1000, recording `MapCornerDependency(0, z, new(ground(0,0), Corner(64, z)))` for z 0 to 4. A slot-2 window (cells 127 to 192 after expansion) therefore loads `ground` (1, 0) without its owner. `ridge` slot (0, 0) over cells 60 to 63, 4 deep, at 1500, anchors Authored chain `ridge-rim` at `Corner(60, 0)` and `Corner(64, 0)`, heights 1500. `far` slots (300, 0) and (-300, 0), 4 by 4 at 2000, no dependencies. Helpers: `Assets()`, `SaveToTemp(doc) -> string`, `LoadSlotWindow(dir, slotX)` (`LoadTiled(dir, new MapTileRect(new(slotX, 0), new(slotX, 0)))`), `SemanticSnapshot(doc)` (root digest plus ordered patch digests in this task, switched to the scheme-2 token in Task 8 with no assertion change), `SurfaceFiles(dir)` (sorted relative paths under `tiles/surfaces`), `PayloadBytes(dir, key)`, `DeletePayload(dir, key)`, `FlipPayloadByte(dir, key)`, `RewriteIndexSemanticDigest(dir, key, digest)` (rewrites the entry and re-hashes the page, directory and manifest so only the semantic digest is wrong), `ExplicitYPlacement(id, x, z)`, `FlatPatch(MapPatchKey key, int height)` (4 by 4 present `Full` cells at `CellMinX 0`), `FlatPatches(n)` (a resolver-2 document with `n` flat `ground` patches at slots 0 to n-1). `ReverseDependants(bool withFinePage)` is the M3 counterexample, a resolver-2 document with `TileSize = 64`, `Native` `SupportFloor` surfaces, `PositiveZ`: `wide` `(2/1, 1/100)` slot (0, 0), `CellMinX 0`, width 64, depth 2 (world x 0 to 128, z 0 to 4) at 1000, loaded by the slot-0 window, and `edge` `(2/1, 1/100)` slot (1, 0), `CellMinX 0`, width 2, depth 2 (world x 128 to 132) at 1000, recording `MapCornerDependency(0, z, new(wide(0,0), Corner(64, z)))` for z 0 to 2. Its entry sits in index block 0, which the slot-0 window reads, so it is listed and not loaded. With `withFinePage`, `fine` `(1/16, 1/1600)` slot (40, 0), 4 by 4 cells at 0 (world x 160 to 160.25), whose index block 2 covers world x 128 to 192 and is not read by the slot-0 window. Keys `Wide0 = ("wide", 0, 0)` and `Edge1 = ("edge", 1, 0)`. `RecordReferrer()` is the M3 deletion counterexample, a resolver-2 document with `TileSize = 64` and one `Native` `SupportFloor` surface `ground` `(1/1, 1/100, PositiveZ)`. Slot (0, 0), `CellMinX 0`, 4 by 4 cells at 1000, anchors Exterior space `yard` (no parent, alias, tags, walls, portals or links). Slot (300, 0), 4 by 4 cells at 1000, anchors footprint `yard-far` with `Space = MapRecordRef("yard", ground(0,0))`, `Lattice` `ground` (300, 0), slot cell 0, lower `(SupportFloor, "ground", null)` and upper `(OpenTop, null, null)`. The footprint's geometry touches only slot (300, 0), so the only referrer of `yard` is not spatial and is absent from the `IncidentRecords` of slot (0, 0). The slot-0 window loads slot (0, 0) with its index block, and slot (300, 0) stays unloaded. Keys `Yard0 = ("ground", 0, 0)` and `YardFar = ("ground", 300, 0)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// SurfaceStorageTests (MapEditor.Tests, internal seams)
static readonly MapPatchKey Ridge = new("ridge", 0, 0), Ground0 = new("ground", 0, 0), Ground1 = new("ground", 1, 0), FarEast = new("far", 300, 0), FarWest = new("far", -300, 0);
static string Refusal(MapDocument doc, string dir) => Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveTiled(doc, dir)).Message;

[Theory, InlineData(MapTiledSaveStep.BeforeTileWrite), InlineData(MapTiledSaveStep.BeforeManifestRename), InlineData(MapTiledSaveStep.AfterManifestRename)]
public void SurfaceStorage_ManifestLastCommitAndForms(MapTiledSaveStep failAt) => TiledDocFixture.InDirectory(dir =>
{
    MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
    MapDocumentFile.SaveTiled(doc, dir);
    byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
    doc.Surfaces.Patches[Ridge].Heights[0] = 1250;
    var save = new MapDocumentSaveOptions { OnStep = s => { if (s == failAt) throw new IOException("injected"); } };
    Assert.Throws<IOException>(() => MapDocumentFile.SaveTiled(doc, dir, save: save));
    bool committed = failAt == MapTiledSaveStep.AfterManifestRename;
    Assert.Equal(committed, !manifest.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "map.json"))));
    Assert.Equal(committed ? 1250 : 1500, MapDocumentFile.LoadTiled(dir).Surfaces.Patches[Ridge].Heights[0]);
    Assert.All(MapDocumentFile.VerifyTiled(dir), f => Assert.True(f.StartsWith("orphan", StringComparison.Ordinal) || f.StartsWith("stray temp", StringComparison.Ordinal), f));
});
public void SurfaceStorage_SweepKeepsTilesAndReachablePagesAndRemovesStrays() => TiledDocFixture.InDirectory(dir =>
{
    MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
    doc.Placements.Add(SurfaceStorageFixtures.ExplicitYPlacement("tree-1", 10f, 10f));
    MapDocumentFile.SaveTiled(doc, dir);
    string stray = Path.Combine(dir, "tiles", "surfaces", "p", "aa", new string('a', 64) + ".json");
    Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
    File.WriteAllText(stray, "{}");
    doc.Surfaces.Patches[Ridge].Heights[0] = 1250;
    MapDocumentFile.SaveTiled(doc, dir);
    Assert.False(File.Exists(stray));
    Assert.Empty(MapDocumentFile.VerifyTiled(dir));
    Assert.Contains(TiledDocFixture.TileFiles(dir), f => !f.StartsWith(Path.Combine("tiles", "surfaces"), StringComparison.Ordinal));
    Assert.Equal(1250, MapDocumentFile.LoadTiled(dir).Surfaces.Patches[Ridge].Heights[0]);
});
public void SurfaceStorage_SweepIsSkippedAfterAnUnreadablePreviousManifest() => TiledDocFixture.InDirectory(dir =>
{
    MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
    MapDocumentFile.SaveTiled(doc, dir);
    var before = SurfaceStorageFixtures.SurfaceFiles(dir);
    File.WriteAllText(Path.Combine(dir, "map.json"), "{");
    doc.Surfaces.Patches[Ridge].Heights[0] = 1250;
    MapDocumentFile.SaveTiled(doc, dir);                                   // whole document: no stale check, no sweep
    Assert.Superset(before.ToHashSet(), SurfaceStorageFixtures.SurfaceFiles(dir).ToHashSet());
    Assert.Equal(1250, MapDocumentFile.LoadTiled(dir).Surfaces.Patches[Ridge].Heights[0]);
});
public void SurfaceStorage_WindowCarriesUnloadedPatchesAndRefusesUnloadedDependencies() => TiledDocFixture.InDirectory(dir =>
{
    MapDocumentFile.SaveTiled(SurfaceStorageFixtures.ThreeSurfaces(), dir);
    byte[] ground1 = SurfaceStorageFixtures.PayloadBytes(dir, Ground1);
    MapDocument w0 = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
    w0.Surfaces.Patches[Ground0].Heights[6] = 1100;                         // interior corner (1, 1), no dependent
    MapDocumentFile.SaveTiled(w0, dir);
    Assert.Equal(ground1, SurfaceStorageFixtures.PayloadBytes(dir, Ground1)); // loaded but unchanged, never rewritten
    MapDocument w2 = SurfaceStorageFixtures.LoadSlotWindow(dir, 2);
    Assert.Equal((true, false), (w2.Surfaces.Patches.ContainsKey(Ground1), w2.Surfaces.Patches.ContainsKey(Ground0)));
    var files = SurfaceStorageFixtures.SurfaceFiles(dir);
    w2.Surfaces.Patches[Ground1].Heights[0] = 1200;                         // the dependent corner at cell X 64
    Assert.Contains("unloaded", Refusal(w2, dir));
    Assert.Equal(files, SurfaceStorageFixtures.SurfaceFiles(dir));
});
public void SurfaceStorage_NewPatchUnderAnUnreadIndexPageRefuses() => TiledDocFixture.InDirectory(dir =>
{
    MapDocumentFile.SaveTiled(SurfaceStorageFixtures.ThreeSurfaces(), dir);
    MapDocument w0 = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
    var key = new MapPatchKey("ground", 40, 0);                              // index block 2 was never read
    w0.Surfaces.Patches.Add(key, SurfaceStorageFixtures.FlatPatch(key, 1000));
    Assert.Contains("unloaded", Refusal(w0, dir));
});
[Theory, InlineData("dependant"), InlineData("page"), InlineData("records"), InlineData("delete"), InlineData("presence")]
public void PartialSave_RefusesEditsWithUnknownOrUnloadedReverseDependants(string variant) => TiledDocFixture.InDirectory(dir =>
{
    MapDocumentFile.SaveTiled(variant switch
    {
        "records" => SurfaceStorageFixtures.ThreeSurfaces(),
        "delete" or "presence" => SurfaceStorageFixtures.RecordReferrer(),
        _ => SurfaceStorageFixtures.ReverseDependants(withFinePage: variant == "page"),
    }, dir);
    var files = SurfaceStorageFixtures.SurfaceFiles(dir);
    byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
    MapDocument w0 = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
    string expected = "unsupported partial edit: structure";
    switch (variant)
    {
        case "records":
            Assert.Equal(1, w0.Surfaces.Patches[Ridge].Records.RemoveAll(r => r.Id == "ridge-rim"));   // nothing references it, still refused
            expected = "unsupported partial edit: records";
            break;
        case "delete":
            Assert.False(w0.Surfaces.Patches.ContainsKey(SurfaceStorageFixtures.YardFar));          // yard's only referrer: unloaded, not spatial, not incident
            Assert.True(w0.Surfaces.Patches.Remove(SurfaceStorageFixtures.Yard0));                  // no new Records left to compare, still refused
            break;
        case "presence":
            w0.Surfaces.Patches[SurfaceStorageFixtures.Yard0].SetPresent(1, 1, false);
            break;
        default:
            Assert.False(w0.Surfaces.Patches.ContainsKey(SurfaceStorageFixtures.Edge1));
            w0.Surfaces.Patches[SurfaceStorageFixtures.Wide0].Heights[1] = 1100;                     // corner (1, 0), never a dependency target
            expected = variant == "page" ? "unloaded: reverse dependants unknown" : "unloaded: reverse dependant 'edge";
            break;
    }
    Assert.Contains(expected, Refusal(w0, dir));
    Assert.Equal(files, SurfaceStorageFixtures.SurfaceFiles(dir));
    Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(dir, "map.json")));
});
public void RawSave_RefusesACornerDependencyWhoseOwnerVertexIsElsewhere() => TiledDocFixture.InDirectory(dir =>
{
    MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
    var deps = doc.Surfaces.Patches[Ground1].CornerDependencies;
    deps[0] = deps[0] with { Owner = new MapVertexOwner(Ground0, MapLatticeAddress.Corner(63, 0)) };   // a real vertex of ground(0,0), 1 m west of cell X 64
    Assert.Contains("dependency position", Refusal(doc, dir));                                           // whole-document save, the owner is loaded
    Assert.Empty(SurfaceStorageFixtures.SurfaceFiles(dir));
});
public void StaleWindow_RefusesAfterAnotherWriterReplacedAnUnloadedSurfacePatch() => TiledDocFixture.InDirectory(dir =>
{
    MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
    doc.Placements.Add(SurfaceStorageFixtures.ExplicitYPlacement("tree-1", 10f, 10f));
    MapDocumentFile.SaveTiled(doc, dir);
    MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 0);
    MapDocument whole = MapDocumentFile.LoadTiled(dir);
    whole.Surfaces.Patches[FarEast].Heights[0] = 2100;
    MapDocumentFile.SaveTiled(whole, dir);
    byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
    window.Surfaces.Patches[Ground0].Heights[6] = 1100;
    Assert.Contains("stale window", Refusal(window, dir));
    Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(dir, "map.json")));
    Assert.Equal(2100, MapDocumentFile.LoadTiled(dir).Surfaces.Patches[FarEast].Heights[0]);
});
public void SurfacesOnlyWindows_SecondSaveRefusesAsStale() => TiledDocFixture.InDirectory(dir =>
{
    MapDocumentFile.SaveTiled(SurfaceStorageFixtures.ThreeSurfaces(), dir);
    MapDocument a = SurfaceStorageFixtures.LoadSlotWindow(dir, 300), b = SurfaceStorageFixtures.LoadSlotWindow(dir, -300);
    Assert.Equal((0, false, true), (a.Tiles!.Entries.Count, a.Tiles.HasUnloadedTiles, a.Tiles.IsPartial));
    b.Surfaces.Patches[FarWest].Heights[0] = 2100;
    MapDocumentFile.SaveTiled(b, dir);
    byte[] manifest = File.ReadAllBytes(Path.Combine(dir, "map.json"));
    a.Surfaces.Patches[FarEast].Heights[0] = 2200;
    Assert.Contains("stale window", Refusal(a, dir));
    Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(dir, "map.json")));
    MapDocument whole = MapDocumentFile.LoadTiled(dir);
    Assert.Equal((2100, 2000), (whole.Surfaces.Patches[FarWest].Heights[0], whole.Surfaces.Patches[FarEast].Heights[0]));
});
public void SurfacesOnlyWindow_IsPartialEverywhereACompleteDocumentIsRequired() => TiledDocFixture.InDirectory(dir =>
{
    MapDocumentFile.SaveTiled(SurfaceStorageFixtures.ThreeSurfaces(), dir);
    MapDocument w = SurfaceStorageFixtures.LoadSlotWindow(dir, 300);
    Assert.Contains("windowed", Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveText(w)).Message);
    TiledDocFixture.InDirectory(other => Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveAs(w, Path.Combine(other, "m.json"), MapDocumentForm.Monolithic)));
    Assert.Throws<MapDocumentException>(() => MapBoundDocumentValidation.Validate(w, SurfaceStorageFixtures.Assets()));
    Assert.Throws<MapDocumentException>(() => NativeDocumentSnapshot.Clone(w, MapDocRegistry.CreateDefault()));
    MapDocumentFile.SaveTiled(w, dir);                                      // own-directory save of the unchanged window
    Assert.Equal(AssertFixtures.Sha256(Path.Combine(dir, "map.json")), w.Tiles!.ManifestSha256);
    Assert.True(w.Tiles.Surfaces!.IsPartial);
    MapDocumentSource source = MapDocumentSource.OpenTiled(dir);
    Assert.Empty(source.Tiles.Surfaces!.ReadDirectoryPages);
    Assert.True(source.Manifest.Tiles!.IsPartial);
    source.Refresh();
    Assert.True(source.Manifest.Tiles!.IsPartial);
});
public void SurfaceStorage_MonolithicEmbeddingIsBounded() => TiledDocFixture.InDirectory(dir =>
{
    Assert.Equal((256, 8_388_608L), (MapSurfaceEmbedding.MaxPatches, MapSurfaceEmbedding.MaxEncodedBytes));
    MapDocument doc = SurfaceStorageFixtures.FlatPatches(3);
    Assert.Contains("tiled", Assert.Throws<MapDocumentException>(() => MapSurfaceEmbedding.Check(doc.Surfaces, 2, long.MaxValue)).Message);
    Assert.Contains("tiled", Assert.Throws<MapDocumentException>(() => MapSurfaceEmbedding.Check(doc.Surfaces, 3, 10)).Message);
    MapSurfaceEmbedding.Check(doc.Surfaces, 3, long.MaxValue);
    string path = Path.Combine(dir, "m.json");
    MapDocumentFile.Save(doc, path);
    Assert.Equal(3, MapDocumentFile.Load(path).Surfaces.Patches.Count);
});

// SparseSurfaceIndexTests (MapDoc.Tests)
public void SparseIndex_UnloadedIsNotEmpty()
{
    string dir = SurfaceStorageFixtures.SaveToTemp(SurfaceStorageFixtures.ThreeSurfaces());
    MapStoredSurfaceSource stored = MapStoredSurfaceSource.Open(dir);
    Assert.Equal(MapPatchStatus.Present, stored.ReadPatch(new("ground", 0, 0)).Status);
    Assert.Equal(MapPatchStatus.KnownEmpty, stored.ReadPatch(new("ground", 2, 0)).Status);
    Assert.Equal(MapPatchStatus.KnownEmpty, stored.ReadPatch(new("ground", 40, 40)).Status);
    Assert.Equal(MapPatchStatus.Unloaded, MapDocumentSurfaceSource.Capture(SurfaceStorageFixtures.LoadSlotWindow(dir, 0)).ReadPatch(new("far", 300, 0)).Status);
    var scope = new MapSurfaceScope(WorldFrame.Origin, new Vector2(61, 1), new Vector2(62, 2), null, null, new[] { MapSurfaceRole.SupportFloor }, null, new MapQueryLimits());
    MapPatchFindResult found = MapStoredSurfaceSource.Open(dir).FindPatches(scope);
    Assert.Equal((MapFindStatus.Complete, 4), (found.Status, found.PagesRead));   // one directory and one index page for each of ground and ridge
    Assert.Equal(new[] { new MapPatchKey("ground", 0, 0), new("ridge", 0, 0) }, found.Patches.Select(p => p.Key));
    MapPatchFindResult over = MapStoredSurfaceSource.Open(dir).FindPatches(scope with { Limits = new MapQueryLimits(MaxCandidatePatches: 1) });
    Assert.Equal((MapFindStatus.CapacityExceeded, 0), (over.Status, over.Patches.Count));
    SurfaceStorageFixtures.DeletePayload(dir, new("far", -300, 0));
    SurfaceStorageFixtures.FlipPayloadByte(dir, new("far", 300, 0));
    MapStoredSurfaceSource fresh = MapStoredSurfaceSource.Open(dir);
    Assert.Equal((MapPatchStatus.Missing, MapPatchStatus.Corrupt), (fresh.ReadPatch(new("far", -300, 0)).Status, fresh.ReadPatch(new("far", 300, 0)).Status));
}

// SurfaceSourceSnapshotTests (MapDoc.Tests)
public void DocumentSurfaceSource_CaptureIsADeepCopy()
{
    MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
    var key = new MapPatchKey("ground", 0, 0);
    MapDocumentSurfaceSource src = MapDocumentSurfaceSource.Capture(doc);
    string id = src.SnapshotId;
    doc.Surfaces.Patches[key].Heights[0] = 7;
    src.ReadPatch(key).Patch!.Heights[0] = 8;
    Assert.Equal(1000, src.ReadPatch(key).Patch!.Heights[0]);
    Assert.Equal(id, src.SnapshotId);
    Assert.NotEqual(id, MapDocumentSurfaceSource.Capture(doc).SnapshotId);
}
public void StoredSurfaceSource_PinsItsGenerationAndReportsSweptFilesMissing()
{
    string dir = SurfaceStorageFixtures.SaveToTemp(SurfaceStorageFixtures.ThreeSurfaces());
    var key = new MapPatchKey("far", 300, 0);
    MapStoredSurfaceSource pinned = MapStoredSurfaceSource.Open(dir);
    MapDocument whole = MapDocumentFile.LoadTiled(dir);
    whole.Surfaces.Patches[key].Heights[0] = 2100;
    MapDocumentFile.SaveTiled(whole, dir);
    Assert.Equal(MapPatchStatus.Missing, pinned.ReadPatch(key).Status);
    MapPatchRead fresh = MapStoredSurfaceSource.Open(dir).ReadPatch(key);
    Assert.Equal((MapPatchStatus.Present, 2100), (fresh.Status, fresh.Patch!.Heights[0]));
}

// SurfaceStorageNamespaceTests (MapDoc.Tests, public API)
public void SurfaceStorage_AuthoredSurfacesResourcesSurviveAndReservedPathsRefuse()
{
    string dir = SurfaceStorageFixtures.SaveToTemp(SurfaceStorageFixtures.ThreeSurfaces());
    string mesh = Path.Combine(dir, "surfaces", "rock.mesh");
    Directory.CreateDirectory(Path.GetDirectoryName(mesh)!);
    File.WriteAllBytes(mesh, new byte[] { 1, 2, 3, 4 });
    for (int i = 0; i < 2; i++)
    {
        MapDocument d = MapDocumentFile.LoadTiled(dir);
        d.Surfaces.Patches[new("ridge", 0, 0)].Heights[0] += 1;
        MapDocumentFile.SaveTiled(d, dir);
    }
    Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(mesh));
    var guarded = new MapStorageGuardedAssetSource(dir, MapDocumentForm.Tiled);
    Assert.Throws<MapDocumentException>(() => guarded.Read(new MapAssetRef("x", "tiles/surfaces/p/aa/x.json", "", 1)));
    Assert.True(MapDocumentStorage.IsReserved(dir, MapDocumentForm.Tiled, Path.Combine(dir, "tiles", "surfaces", "d", "x.json")));
    Assert.False(MapDocumentStorage.IsReserved(dir, MapDocumentForm.Tiled, mesh));
}
```

- [ ] **Step 2: Run red.** `wa_test t7-red-editor "$EDITOR" "FullyQualifiedName~SurfaceStorageTests"` and `wa_test t7-red-mapdoc "$MAPDOC" "FullyQualifiedName~KhaozEngine.Tests.MapDoc.Storage"`. Expected: build FAIL naming `MapStoredSurfaceSource` and `MapSurfaceStorageIndex`.
- [ ] **Step 3: Implement storage, completeness, sources and save integration.** `SemanticSnapshot` compares `MapSurfaceSemantics.RootDigest` plus ordered patch digests in this task. Task 8 switches it to the scheme-2 token without changing any assertion. Before adding the link, check the M1 invariant from the worktree root. Expected: no output.

```bash
decl='^[[:space:]]*((public|internal|private|protected|file|static|sealed|abstract|readonly|partial|ref)[[:space:]]+)*(record[[:space:]]+(class|struct)|record|class|struct|interface|enum)[[:space:]]+[A-Za-z_][A-Za-z0-9_]*'
comm -12 \
  <(find KhaozEngine.MapDoc.Tests -name '*Fixtures.cs' -exec grep -hoE "$decl" {} + | awk '{print $NF}' | sort -u) \
  <(find KhaozEngine.MapEditor.Tests -name '*.cs' -not -path '*/bin/*' -not -path '*/obj/*' -exec grep -hoE "$decl" {} + | awk '{print $NF}' | sort -u)
```

- [ ] **Step 4: Run green.** `wa_test t7-green-editor "$EDITOR" "FullyQualifiedName~SurfaceStorageTests|FullyQualifiedName~MapTiled|FullyQualifiedName~NativeStorage|FullyQualifiedName~MapEditTool"` (PASS, 17 new cases plus every existing tiled, storage and service case, which proves the nine `IsPartial` sites still behave) and `wa_run t7-green-mapdoc dotnet test "$MAPDOC" -c Release` (whole project, 4 new cases).
- [ ] **Step 5: Commit** `KhaozEngine.MapDoc KhaozEngine.MapDoc.Tests/Storage KhaozEngine.MapEditor.Tests/MapDoc/SurfaceStorageTests.cs KhaozEngine.MapEditor.Tests/KhaozEngine.MapEditor.Tests.csproj`, message `feat(mapdoc): store sparse surfaces with one completeness contract`. Surface windowed no-loss is claimed from this commit.

---

### Task 8: Scheme-2 identity, bounded scope acquisition and factory-only scoped identity

**Files:**
- Create: `KhaozEngine.MapDoc/Identity/MapAuthoredIdentityV2.cs`, `KhaozEngine.MapDoc/Identity/MapScopedIdentity.cs`, `KhaozEngine.MapDoc/Identity/MapWitnesses.cs`, `KhaozEngine.MapDoc/Storage/MapScopedSurfaces.cs`, `KhaozEngine.MapDoc/Surfaces/MapLatticeRanges.cs`
- Test: `KhaozEngine.MapDoc.Tests/Identity/SchemeTwoIdentityTests.cs`, `KhaozEngine.MapDoc.Tests/Storage/ScopedAcquisitionTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/SchemeTwoRepackTests.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/ScopedAcquisitionWorkTests.cs` (internal work counters), fixtures `KhaozEngine.MapDoc.Tests/Storage/AnchorFixtures.cs`, `ScopeFixtures.cs`, `AcquisitionBoundFixtures.cs`

**Interfaces (new):**
- `public static class MapAuthoredIdentityV2` with `string Compute(MapDocument document, MapAssetClosure assets, MapResolveOptions options)` (complete in-memory view) and `string Compute(MapStoredSurfaceSource source, MapAssetClosure assets, MapResolveOptions options)` (streams one patch at a time from the pinned generation, root projection from the manifest globals). Both require `options.ResolverVersion == 2`, document `(1, 2)` and `AuthoredBindingsV2`, refuse a partial view (`window`) and refuse a recomputed semantic digest that differs from the persisted one (`Corrupt`). Projection: `RootDigest`, ordered `(patchKey, semanticDigest)`, closure hash, builder id and version, options hash, resolver version, domain `kemap/native-authored/2`. Page grouping, embedding, file names and byte digests are excluded.
- `public static class MapLatticeRanges` in `KhaozEngine.MapDoc/Surfaces/MapLatticeRanges.cs`, the one home for footprint and bound cell arithmetic (D7): `MapExactXz[] CellRect(MapLatticeFrame frame, MapPatchKey lattice, int slotCell)` (the exact world `[min, max]` corners of that slot cell, Z negated for `NegativeZ`) and `MapCellRect CellRange(MapLatticeFrame frame, MapExactXz min, MapExactXz max)` (the half-open index range `floor(min / u)` to `ceil(max / u)` of cells whose closed squares overlap the rectangle with positive area). Both use only `MapExactValue`, so they throw `MapExactOverflowException` when a value is not representable. Task 12A reuses them.
- `public sealed record MapUnavailable(string What, MapPatchKey Key, MapPatchStatus Status)` (`What` is a record id or `patch`).
- **Factory-only shape** (Global Constraints). Example, the other types follow it exactly:

```csharp
public sealed class MapReadWitness
{
    internal MapReadWitness(string snapshotId, string scopedDigest, WorldFrame frame, int queryPolicyVersion, bool complete) { ... }
    public string SnapshotId { get; }
    public string ScopedDigest { get; }
    public WorldFrame Frame { get; }
    public int QueryPolicyVersion { get; }
    public bool Complete { get; }
}
```

- `public sealed class MapCoverageWitness` (internal constructor, get-only): `string SnapshotId`, `string ScopeDigest`, `WorldFrame Frame`, `IReadOnlyList<string> SurfaceIds`, `IReadOnlyList<KeyValuePair<MapPatchKey, string>> Present` (each value the acquisition-time `MapSurfaceSemantics.PatchDigest`), `IReadOnlyList<MapCoveredRange> KnownEmpty`, `IReadOnlyList<MapRecordRef> Records`, `IReadOnlyList<MapUnavailable> Unavailable`, `bool Complete`. Lists are `ReadOnlyCollection<T>` over private copies.
- `public sealed class MapScopedIdentity` (internal constructor, get-only): `string RootSha256`, `string ScopeDigest`, `WorldFrame Frame`, `IReadOnlyList<KeyValuePair<MapPatchKey, string>> Patches`, `IReadOnlyList<MapRecordRef> Records`, `IReadOnlyList<MapCoveredRange> KnownEmpty`, `IReadOnlyList<string> AssetSha256`, `int QueryPolicyVersion`, `int BuildPolicyVersion`, `bool Complete`, `string Digest` (domain `kemap/scoped/1`), `void RequireComplete()` (throws `MapDocumentException` containing `incomplete`). Only `MapScopedSurfaces` constructs it. `MapResolvedDocument` gains no conversion from it.
- `public enum MapAcquireStatus { Complete, Incomplete, CapacityExceeded, NotRepresentable }`.
- `public sealed class MapScopedSurfaces` (private constructor) with `public static MapScopedSurfaces Acquire(IMapSurfaceSource source, MapSurfaceScope scope, IReadOnlyList<string> assetSha256, int queryPolicyVersion = 1, int buildPolicyVersion = 1)`, `public static MapScopedSurfaces CompleteView(MapSurfaceSet set)`, and get-only `Status`, `Detail`, `Scope`, `Surfaces`, `Witness`, `Identity`, `ReadWitness`, `PagesRead`, `RecordReads`, plus `MapPatchRead Patch(MapPatchKey key)` (acquired `Present` or `KnownEmpty` as a fresh clone on every call, otherwise `Unloaded` with detail `not acquired`), `bool TryRecord(MapRecordRef r, out MapTopologyRecord? record, out MapPatchStatus status)` and `IEnumerable<MapTopologyRecord> RecordsIn(MapPatchKey key)`. Internal immutable access for the D7 bound-face context: `internal bool TryAcquiredPatch(MapPatchKey key, out MapSurfacePatch? patch, out MapPatchStatus status)` returns the view's own acquired instance, the private copy made at acquisition, without cloning. Only MapDoc's read paths (the context, the classifier, the validator, membership and support) call it, and they never write. `internal int PatchClones` counts public `Patch` clones for work tests. An internal overload `Acquire(IMapSurfaceSource source, MapSurfaceScope scope, IReadOnlyList<string> assetSha256, MapAcquisitionWork work)` fills `internal sealed class MapAcquisitionWork` (test observation only) with `long LargestBoundSlotRange` (the largest checked range count computed) and `long BoundSlotVisits` (slots actually iterated).
- Acquisition, in this canonical order (D5). (1) `FindPatches(scope)`. (2) Breadth-first record resolution through `ReadPatch(anchor)`: the `IncidentRecords` of every scope patch and every `MapRecordRef` held by acquired records, except the `Walls`, `Portals` and `Links` lists of a `MapSpaceDoc`, to `MaxRecordDepth` and `MaxRecordReads`. A reference needed beyond the depth budget is `CapacityExceeded`. A complete in-memory capture computes incident lists from its resident records. (3) Bound enumeration: for every acquired `MapSpaceFootprint` (id order) and each of its slot cells (ascending) whose exact world rectangle overlaps the scope rectangle with positive area, the lower then upper bound surface's cell range from `MapLatticeRanges.CellRange` (D7) for every bound kind that names a surface (`SupportFloor`, `Ceiling` and `LegacyExteriorV1`), converted to slots ascending by (SlotZ, SlotX) and de-duplicated. The total of not-yet-acquired bound slots is counted against the remaining `MaxCandidatePatches` before any read, refusing with `CapacityExceeded` and detail `bound slots`. Counting is bounded before iteration (coordinator final review M4). `known` is the set of acquired keys (every key read in steps 1 and 2, whatever its status, so bounded by `MaxCandidatePatches` and `MaxRecordReads`) plus the pending set of not-yet-acquired bound slots collected so far (bounded by the remaining budget). `remaining` is the remaining `MaxCandidatePatches` budget minus the pending set. For each range on surface `s`: (a) `gross = checked((long)slotWidth * slotDepth)`. An `OverflowException` from that product is the same `CapacityExceeded` (`bound slots`), because it exceeds any remaining budget, while a `MapExactOverflowException` from the range arithmetic stays `NotRepresentable`. (b) `overlap` is the number of `known` keys on `s` whose slot lies in the range, found by scanning `known`, never by enumerating the range. (c) `fresh = gross - overlap`, and `fresh > remaining` refuses at once without visiting a slot. (d) Otherwise `gross <= remaining + overlap`, which is at most `remaining` plus the size of `known`, so the range is iterated and each slot not in `known` joins the pending set. A range with `fresh == 0` is not iterated. Known-empty covered ranges from `FindPatches` are not part of `known`, so a bound slot that lies only in such a range still counts as fresh, the existing conservative count. No slot collection larger than the bounded `known` set is ever built and no range is visited before its fresh count fits. `MicroBound` (15,625 by 15,625, 244,140,625 slots, none known) refuses after zero slot visits. `AcquiredBounds` (both bound slots already acquired, remaining 0, gross 1 per range) completes with zero visits, where a gross count alone would refuse. Bound patches add no further record expansion. Page reads across all three steps count against `MaxPageReads`. Any budget overflow gives `CapacityExceeded` with no content. Any `MapExactOverflowException` gives `NotRepresentable` with no content and detail containing `overflow`. A missing or corrupt anchor or bound patch gives `Incomplete` with a `MapUnavailable` row. Nothing is published until the end, and the result never changes after `Acquire` returns (D5).
- `CompleteView(set)` is the complete-view form for bounded fixtures, validators and R2 complete-document transactions: every resident patch and record acquired (no bound enumeration needed), origin frame, a scope rectangle covering every resident slot rounded outward to whole metres, witness `Complete` when nothing is missing, root digest over the surface refs only.
- Test-only `ScopeFixtures`: `AllRoles`, `Around(WorldFrame frame, float x, float z, float half = 2f, MapQueryLimits? limits = null)` (local rectangle `[x - half, x + half] x [z - half, z + half]`, no Y bounds, all roles, no space filter), `Acquire(IMapSurfaceSource source, MapSurfaceScope scope)` (no asset digests), `Whole(MapDocument doc)` (origin frame, the local rectangle covering every resident patch, for complete near-origin fixtures).

**Fixture.** `AnchorFixtures.FarAnchors()` returns a stored world directory (`SaveToTemp`). Frames `(1/1, 1/100, PositiveZ)`, `Native`. Surfaces `ground` (SupportFloor) and `roof` (Ceiling). Patches: `ground` (40, 0) cells 0 to 3 by 0 to 3 at 1000, `roof` (40, 0) cells x 2 to 3, z 0 to 3 at 1400, `ground` (0, 0) one cell at 0, `roof` (41, 0) one cell at 1400. Records, with absolute corners `X = 2560 + local`:

| Record | Anchor | Definition |
| --- | --- | --- |
| `outside` (Exterior) | `ground` (0, 0) | `Portals` `(door, Back)` |
| `outside-origin` footprint | `ground` (0, 0) | cell 0, lower `ground`, upper `OpenTop` |
| `annex` (Cave) | `ground` (40, 0) | `Portals` `(door, Front)`, `Walls` `(annex-south, Front)`, `(annex-north, Back)`, `(annex-east, Back)` |
| `outside-near`, `annex-cells` footprints | `ground` (40, 0) | Lattice `ground` (40, 0), cells x 0 to 1 (outside, upper `OpenTop`) and x 2 to 3 (annex, upper `roof`), z 0 to 3, lower `ground` |
| `door` portal | `ground` (40, 0) | From `outside`, To `annex`, interval `Corner(2562, 0..4)`, bottom `door-bottom`, top `door-top` |
| `door-bottom` chain | `ground` (40, 0) | SurfaceEdge of `ground` at X 2562, z 0 to 4 |
| `door-top` chain | `roof` (41, 0) | SurfaceEdge of `roof` at X 2562, z 0 to 4 (anchored outside the point window on purpose) |
| `annex-south`, `annex-north` strips and every strip chain | `ground` (40, 0) or `roof` (40, 0) | Edges z 0 (x 2562 to 2564) and z 4, lower `ground` edge, upper `roof` edge |
| `annex-east` strip | `roof` (41, 0) | Edge x 2564, z 0 to 4. Referenced only from `annex.Walls`, so only the incident index of `ground` (40, 0) and `roof` (40, 0) finds it |

`FarFrame = new WorldFrame(20, 0)` (anchor X 2560).

`AcquisitionBoundFixtures` (resolver-2 documents, `Native`, `PositiveZ`, every record anchored in the scope's ceiling patch):

| Fixture | Surfaces | Space and footprint | Scope |
| --- | --- | --- | --- |
| `BoundaryTouch()` | `half` SupportFloor `(1/2, 1/200)`: slot (0, 0) `CellMinX 60`, 4 by 4 (world x 30 to 32), and slot (1, 0) `CellMinX 0`, 4 by 4 (world x 32 to 34), heights 0. `third` Ceiling `(1/3, 1/300)`: slot (1, 0) `CellMinX 30`, width 2, depth 1 (absolute cells 94 and 95, world x 31 1/3 to 32, z 0 to 1/3), heights 900 | Exterior `ledge`, footprint `ledge-cells` on lattice `third` (1, 0), slot cell 31 (world x 95/3 to 32), lower `half`, upper `third` | `TouchScope = Around(Origin, 31.8f, 0.2f, 0.1f) with { Roles = [Ceiling] }` |
| `MicroBound()` | `unit` Ceiling `(1/1, 1/100)` slot (0, 0) one cell at 300. `micro` SupportFloor `(1/1000000, 1/100)` slot (0, 0) 4 by 4 at 0 | Exterior `slab`, footprint `slab-cells` on lattice `unit` (0, 0), cell 0, lower `micro`, upper `unit` | `MicroScope = Around(Origin, 0.5f, 0.5f, 0.25f) with { Roles = [Ceiling] }` |
| `AcquiredBounds()` | `low` SupportFloor `(1/1, 1/100)` slot (0, 0) one cell at 0. `top` Ceiling `(1/1, 1/100)` slot (0, 0) one cell at 300 | Exterior `porch`, footprint `porch-cells` on lattice `top` (0, 0), cell 0, lower `low`, upper `top` | `AcquiredScope = Around(Origin, 0.5f, 0.5f, 0.25f, new MapQueryLimits(MaxCandidatePatches: 2))`, all roles |
| `AdversarialUnits()` | `odd` Ceiling `(2147483647/2147483646, 1/100)` slot (0, 0) cells 0 to 7, depth 1, at 300. `fine` SupportFloor `(1/2147483647, 1/100)` slot (0, 0) one cell at 0 | Exterior `strange`, footprint `strange-cells` on lattice `odd` (0, 0), slot cell 5, lower `fine`, upper `odd` | `AdversarialScope = Around(Origin, 5.5f, 0.5f, 0.25f) with { Roles = [Ceiling] }` |

Expected arithmetic: `ledge-cells` maps to `half` cells `floor(190/3) = 63` to `ceil(64) - 1 = 63`, so only slot (0, 0), and the slot-1 patch only touches it at x = 32. `slab-cells` maps to `micro` cells 0 to 999,999 per axis, 15,625 slots per axis, far above 255 remaining. `porch-cells` maps to `low` cell 0 and `top` cell 0, slot (0, 0) on each surface, and `FindPatches` already acquired both, so remaining is 0 and each range has gross 1, overlap 1 and fresh 0. For `strange-cells` with `p = 2147483647`, the cell's minimum X is `5p/(p-1)` m and its `fine` index is `5p^2/(p-1)`, already reduced with a numerator near `2.3e19`, so it does not fit `long`.

- [ ] **Step 1: Write the failing tests**

```csharp
// SchemeTwoIdentityTests
static readonly MapResolveOptions V2 = new("headless", 1, "options", ResolverVersion: 2);
static string Token(MapDocument doc) => MapAuthoredIdentityV2.Compute(doc, SurfaceStorageFixtures.Assets(), V2);

public void SchemeTwo_MonolithicAndTiledCopiesShareOneToken()
{
    MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
    Assert.Equal(Token(doc), MapAuthoredIdentityV2.Compute(MapStoredSurfaceSource.Open(SurfaceStorageFixtures.SaveToTemp(doc)), SurfaceStorageFixtures.Assets(), V2));
}
[Theory, InlineData("height", true), InlineData("unit", true), InlineData("record", true), InlineData("role", true), InlineData("displayName", false)]
public void SchemeTwo_ChangesWithGeometryAndPolicyOnly(string change, bool changes)
{
    string before = Token(SurfaceStorageFixtures.ThreeSurfaces());
    MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
    SurfaceStorageFixtures.Apply(doc, change);
    Assert.Equal(changes, before != Token(doc));
}
public void SchemeTwo_StaleSemanticDigestIsCorrupt()
{
    string dir = SurfaceStorageFixtures.SaveToTemp(SurfaceStorageFixtures.ThreeSurfaces());
    SurfaceStorageFixtures.RewriteIndexSemanticDigest(dir, new("ridge", 0, 0), new string('0', 64));
    Assert.Contains("Corrupt", Assert.Throws<MapDocumentException>(() => MapAuthoredIdentityV2.Compute(MapStoredSurfaceSource.Open(dir), SurfaceStorageFixtures.Assets(), V2)).Message);
}
public void SchemeTwo_RefusesAPartialView()
{
    string dir = SurfaceStorageFixtures.SaveToTemp(SurfaceStorageFixtures.ThreeSurfaces());
    Assert.Contains("window", Assert.Throws<MapDocumentException>(() => Token(SurfaceStorageFixtures.LoadSlotWindow(dir, 300))).Message);
}
public void ScopedIdentity_IsFactoryOnlyAndTakesItsFrameAndScopeFromTheAcquisition()
{
    var src = MapDocumentSurfaceSource.Capture(SurfaceStorageFixtures.ThreeSurfaces());
    MapSurfaceScope scope = ScopeFixtures.Around(WorldFrame.Origin, 62, 2);
    MapScopedSurfaces a = ScopeFixtures.Acquire(src, scope), b = ScopeFixtures.Acquire(src, scope);
    MapScopedSurfaces floorsOnly = ScopeFixtures.Acquire(src, scope with { Roles = new[] { MapSurfaceRole.SupportFloor } });
    Assert.Equal((a.Identity.Digest, WorldFrame.Origin), (b.Identity.Digest, a.Identity.Frame));
    Assert.NotEqual(a.Identity.ScopeDigest, floorsOnly.Identity.ScopeDigest);
    Assert.Equal((a.Identity.Digest, src.SnapshotId, true), (a.ReadWitness.ScopedDigest, a.ReadWitness.SnapshotId, a.ReadWitness.Complete));
    Assert.NotEqual(Token(SurfaceStorageFixtures.ThreeSurfaces()), a.Identity.Digest);
    Assert.Empty(typeof(MapResolvedDocument).GetConstructors());
    foreach (Type t in new[] { typeof(MapScopedIdentity), typeof(MapReadWitness), typeof(MapCoverageWitness), typeof(MapScopedSurfaces) })
    {
        Assert.Empty(t.GetConstructors());
        Assert.DoesNotContain(t.GetProperties(), p => p.SetMethod is { IsPublic: true });
    }
}

// ScopedAcquisitionTests
static MapSurfaceScope FarScope(MapQueryLimits? limits = null) => ScopeFixtures.Around(AnchorFixtures.FarFrame, 2, 2, half: 1.5f, limits);

public void Acquire_ResolvesFarAnchoredRecordsWithBoundedReads()
{
    MapScopedSurfaces s = ScopeFixtures.Acquire(MapStoredSurfaceSource.Open(AnchorFixtures.FarAnchors()), FarScope());
    Assert.Equal(MapAcquireStatus.Complete, s.Status);
    Assert.Equal(5, s.PagesRead);        // ground dir + index (block 2), roof dir + index (block 2), ground index (block 0)
    Assert.Equal(2, s.RecordReads);      // anchor patches ground(0,0) ("outside") and roof(41,0) ("door-top", "annex-east"), each read once
    Assert.Equal(new[] { new MapPatchKey("ground", 0, 0), new("ground", 40, 0), new("roof", 40, 0), new("roof", 41, 0) },
        s.Witness.Present.Select(p => p.Key).OrderBy(k => k));
    Assert.Contains(new MapRecordRef("door-top", new("roof", 41, 0)), s.Witness.Records);   // through door's reference
    Assert.Contains(new MapRecordRef("annex-east", new("roof", 41, 0)), s.Witness.Records); // through the incident index only
    Assert.True(s.TryRecord(new("outside", new("ground", 0, 0)), out MapTopologyRecord? outside, out _) && outside is MapSpaceDoc);
}
public void Acquire_MissingAnchorIsUnavailableNotEmpty()
{
    string dir = AnchorFixtures.FarAnchors();
    SurfaceStorageFixtures.DeletePayload(dir, new("roof", 41, 0));
    MapScopedSurfaces s = ScopeFixtures.Acquire(MapStoredSurfaceSource.Open(dir), FarScope());
    Assert.Equal(MapAcquireStatus.Incomplete, s.Status);
    Assert.Contains(new MapUnavailable("door-top", new("roof", 41, 0), MapPatchStatus.Missing), s.Witness.Unavailable);
    Assert.Equal((false, false), (s.Identity.Complete, s.ReadWitness.Complete));
    Assert.Contains("incomplete", Assert.Throws<MapDocumentException>(s.Identity.RequireComplete).Message);
}
[Theory, InlineData(4, 64, 8), InlineData(32, 1, 8), InlineData(32, 64, 0)]
public void Acquire_PageRecordAndDepthBudgetsRefuse(int pages, int records, int depth)
{
    MapScopedSurfaces s = ScopeFixtures.Acquire(MapStoredSurfaceSource.Open(AnchorFixtures.FarAnchors()),
        FarScope(new MapQueryLimits(MaxPageReads: pages, MaxRecordReads: records, MaxRecordDepth: depth)));
    Assert.Equal((MapAcquireStatus.CapacityExceeded, 0), (s.Status, s.Witness.Present.Count));
}
public void LatticeRanges_ArePositiveAreaHalfOpenAndExact()
{
    var half = new MapLatticeFrame(new(1, 2), new(1, 200), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
    var third = half with { CellUnitMetres = new(1, 3), HeightUnitMetres = new(1, 300) };
    MapExactXz[] r = MapLatticeRanges.CellRect(third, new MapPatchKey("third", 1, 0), 31);
    Assert.Equal(new[] { new MapExactXz(new(95, 3), new(0, 1)), new MapExactXz(new(32, 1), new(1, 3)) }, r);
    Assert.Equal(new MapCellRect(63, 0, 64, 1), MapLatticeRanges.CellRange(half, r[0], r[1]));                     // x = 32 only touches cell 64
    Assert.Equal(new MapCellRect(-1, -1, 0, 0), MapLatticeRanges.CellRange(MapLatticeFrame.ImportedMetreCentimetre,
        new MapExactXz(new(-1, 2), new(0, 1)), new MapExactXz(new(0, 1), new(1, 2))));                               // NegativeZ negates Z
}
public void Acquire_EnumeratesExactlyThePositiveAreaBoundSlotsOfAMixedFootprint()
{
    MapScopedSurfaces s = ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(AcquisitionBoundFixtures.BoundaryTouch()), AcquisitionBoundFixtures.TouchScope);
    Assert.Equal(MapAcquireStatus.Complete, s.Status);
    Assert.Equal(new[] { new MapPatchKey("half", 0, 0), new("third", 1, 0) }, s.Witness.Present.Select(p => p.Key).OrderBy(k => k));
}
public void Acquire_BoundSlotBudgetRefusesBeforeReading()
{
    MapScopedSurfaces s = ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(AcquisitionBoundFixtures.MicroBound()), AcquisitionBoundFixtures.MicroScope);
    Assert.Equal((MapAcquireStatus.CapacityExceeded, 0), (s.Status, s.Witness.Present.Count));
    Assert.Contains("bound slots", s.Detail);
}
public void Acquire_UnrepresentableBoundRangeIsNotRepresentableNotCapacity()
{
    MapScopedSurfaces s = ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(AcquisitionBoundFixtures.AdversarialUnits()), AcquisitionBoundFixtures.AdversarialScope);
    Assert.Equal((MapAcquireStatus.NotRepresentable, 0, false), (s.Status, s.Witness.Present.Count, s.ReadWitness.Complete));
    Assert.Contains("overflow", s.Detail);
}
public void Acquisition_IsImmutableAgainstReturnedCopiesAndLaterSweeps()
{
    string dir = SurfaceStorageFixtures.SaveToTemp(SurfaceStorageFixtures.ThreeSurfaces());
    var ground = new MapPatchKey("ground", 0, 0);
    MapScopedSurfaces s = ScopeFixtures.Acquire(MapStoredSurfaceSource.Open(dir), ScopeFixtures.Around(WorldFrame.Origin, 62, 2));
    string digest = s.Identity.Digest;
    s.Patch(ground).Patch!.Heights[0] = 7;
    Assert.Throws<NotSupportedException>(() => ((IList<KeyValuePair<MapPatchKey, string>>)s.Witness.Present).Add(default));
    MapDocument whole = MapDocumentFile.LoadTiled(dir);
    whole.Surfaces.Patches[ground].Heights[0] = 1234;
    MapDocumentFile.SaveTiled(whole, dir);                                     // sweeps the payload the acquisition read
    Assert.Equal(1000, s.Patch(ground).Patch!.Heights[0]);
    Assert.Equal((digest, digest), (s.Identity.Digest, s.ReadWitness.ScopedDigest));
}
```

`SurfaceStorageFixtures.Apply(doc, change)` changes ground (0, 0) height index 5 by +1, the `ridge` height unit to 1/1000 with every height times 10, the first `ridge-rim` height by +1, `ridge`'s role to `Ceiling`, or `DisplayName`. `SchemeTwoRepackTests.SchemeTwo_RepackedPagesKeepTheTokenWhileFilesDiffer` (MapEditor.Tests) saves the same document with `MapSurfacePacking(IndexBlockSlots: 1, DirectoryBlockPages: 2)` into a second directory and asserts equal stored tokens and unequal index page file sets:

```csharp
public void SchemeTwo_RepackedPagesKeepTheTokenWhileFilesDiffer() => TiledDocFixture.InDirectory(a => TiledDocFixture.InDirectory(b =>
{
    MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
    MapDocumentFile.SaveTiled(doc, a);
    MapTiledFile.Save(doc, b, MapDocRegistry.CreateDefault(), null, new MapSurfacePacking(IndexBlockSlots: 1, DirectoryBlockPages: 2));
    var options = new MapResolveOptions("headless", 1, "options", ResolverVersion: 2);
    Assert.Equal(MapAuthoredIdentityV2.Compute(MapStoredSurfaceSource.Open(a), SurfaceStorageFixtures.Assets(), options),
                 MapAuthoredIdentityV2.Compute(MapStoredSurfaceSource.Open(b), SurfaceStorageFixtures.Assets(), options));
    Assert.NotEqual(SurfaceStorageFixtures.SurfaceFiles(a).Where(f => f.Contains("/i/")), SurfaceStorageFixtures.SurfaceFiles(b).Where(f => f.Contains("/i/")));
}));
```

It uses the internal `MapTiledFile.Save` packing overload from Task 7. `ScopedAcquisitionWorkTests` (MapEditor.Tests) proves M4 with the internal counters, the zero-visit refusal and the finite already-acquired range:

```csharp
public void BoundSlotEnumeration_RefusesFromTheCheckedCountWithoutVisitingSlots()
{
    var work = new MapAcquisitionWork();
    MapScopedSurfaces s = MapScopedSurfaces.Acquire(MapDocumentSurfaceSource.Capture(AcquisitionBoundFixtures.MicroBound()), AcquisitionBoundFixtures.MicroScope, Array.Empty<string>(), work);
    Assert.Equal((MapAcquireStatus.CapacityExceeded, 0, 244_140_625L, 0L), (s.Status, s.Witness.Present.Count, work.LargestBoundSlotRange, work.BoundSlotVisits));
    Assert.Contains("bound slots", s.Detail);
}
public void BoundSlotEnumeration_SubtractsAlreadyAcquiredSlotsBeforeRefusing()
{
    var work = new MapAcquisitionWork();
    MapScopedSurfaces s = MapScopedSurfaces.Acquire(MapDocumentSurfaceSource.Capture(AcquisitionBoundFixtures.AcquiredBounds()), AcquisitionBoundFixtures.AcquiredScope, Array.Empty<string>(), work);
    Assert.Equal((MapAcquireStatus.Complete, 2, 1L, 0L), (s.Status, s.Witness.Present.Count, work.LargestBoundSlotRange, work.BoundSlotVisits));   // gross 1 > remaining 0, fresh 0
}
```

- [ ] **Step 2: Run red.** `wa_test t8-red "$MAPDOC" "FullyQualifiedName~SchemeTwoIdentityTests|FullyQualifiedName~ScopedAcquisitionTests"`. Expected: build FAIL naming `MapAuthoredIdentityV2` and `MapScopedSurfaces`.
- [ ] **Step 3: Implement identity, witnesses, acquisition and the scoped identity.** Switch `SurfaceStorageFixtures.SemanticSnapshot` to the scheme-2 token.
- [ ] **Step 4: Run green.** `wa_test t8-green-mapdoc "$MAPDOC" "FullyQualifiedName~SchemeTwoIdentityTests|FullyQualifiedName~ScopedAcquisitionTests|FullyQualifiedName~KhaozEngine.Tests.MapDoc.Storage"` (PASS, 19 new cases plus the Task 7 MapDoc storage cases) and `wa_test t8-green-editor "$EDITOR" "FullyQualifiedName~SchemeTwoRepackTests|FullyQualifiedName~SurfaceStorageTests|FullyQualifiedName~ScopedAcquisitionWorkTests"` (PASS, 3 new cases plus the Task 7 storage cases).
- [ ] **Step 5: Commit** `KhaozEngine.MapDoc/Identity KhaozEngine.MapDoc/Storage/MapScopedSurfaces.cs KhaozEngine.MapDoc/Surfaces/MapLatticeRanges.cs KhaozEngine.MapDoc.Tests/Identity KhaozEngine.MapDoc.Tests/Storage KhaozEngine.MapEditor.Tests/MapDoc/SchemeTwoRepackTests.cs KhaozEngine.MapEditor.Tests/MapDoc/ScopedAcquisitionWorkTests.cs`, message `feat(mapdoc): add scheme-2 identity and bounded scoped acquisition with factory-only witnesses`.

---

### Task 9: Canonical surface compiler

**Files:**
- Create: `KhaozEngine.MapDoc/Surfaces/MapSurfaceTopology.cs`, `MapSurfaceCompiler.cs`, `MapCompiledPatch.cs`, `MapSubdividedTriangle.cs`
- Create: `KhaozEngine.MapDoc.Compatibility.Tests/LegacyOracleConverter.cs`, `LegacyOracleCompilerTests.cs`
- Test: `KhaozEngine.MapDoc.Tests/Surfaces/SurfaceCompilerTests.cs`, fixture `CompilerFixtures.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/CompilerBudgetTests.cs`

**Interfaces (new, `KhaozEngine.MapDoc.Surfaces`):**
- `public enum MapLatticePoint : byte { Sw, Se, Nw, Ne, MidS, MidE, MidN, MidW }`, `public readonly record struct MapLatticeTriangle(MapLatticePoint A, MapLatticePoint B, MapLatticePoint C, bool Overlay)`.
- `public static class MapSurfaceTopology` with `bool SplitSwNe(int sw, int se, int nw, int ne, MapOverlayCut cut, byte rotation, MapCellTopology topology)` (forced topology wins, `DiagonalHalf` forces even rotation SW-NE and odd NW-SE, otherwise `Math.Abs((long)sw - ne) <= Math.Abs((long)se - nw)`, a forced topology contradicting a `DiagonalHalf` rotation is a validation error) and `int Triangulate(MapOverlayCut, byte rotation, bool splitSwNe, Span<MapLatticeTriangle>)`, reproducing the released table and winding of `TileTriangulation.Triangulate` (`TileTriangulation.cs:60`). Overlay 0 triangulates as `Full` with the split of its authored cut.
- `public readonly record struct MapFaceKey(string OwnerId, MapPatchKey? Patch, int Primitive, byte ParentTriangle, ushort Child, MapSide Side) : IComparable<MapFaceKey>` (D4 order).
- `public enum MapFaceRole : byte { SupportFloor, Ceiling, Wall }`, `public readonly record struct MapVertexId(string SurfaceId, MapLatticeAddress Address)`, `public readonly record struct MapSubmissionAnchor(long X, long Y, long Z)`, `public readonly record struct MapExactTriangle(MapExactPoint A, MapExactPoint B, MapExactPoint C)`.
- `public readonly record struct MapCompiledFace(MapFaceKey Key, MapFaceRole Role, int A, int B, int C, Vector3 Normal)` (geometric normals, floors up, ceilings down, a ceiling face is the floor face with `B` and `C` swapped), `public readonly record struct MapPaintCoverage(MapFaceKey Face, ushort Underlay, ushort Overlay, bool OverlayCovers, bool Feather)`, `public sealed record MapFallbackCell(int SlotCell, int Sw, int Se, int Nw, int Ne)`.
- `public sealed class MapCompiledPatch` with `Key`, `Role`, `Anchor`, `IReadOnlyList<MapVertexId> VertexIds`, `IReadOnlyList<MapExactPoint> ExactVertices` (exact world), `IReadOnlyList<Vector3> Offsets`, `Faces`, `Paint`, `LegacyFallbackCells`, and `MapExactTriangle ExactTriangle(MapCompiledFace face)`.
- `public static class MapSurfaceCompiler` with `MaxFacesPerPatch = 65_536`, `MaxChildrenPerParent = 192`, `MapCompiledPatch Compile(MapSurfaceRef, MapSurfacePatch)`, `MapCompiledPatch ApplyPaintOverride(MapCompiledPatch target, MapSurfaceRef overrideSurface, MapSurfacePatch overridePatch)` (physical faces unchanged, never support) and `MapExactValue? ExactHeight(MapSurfaceRef, MapSurfacePatch, MapExactValue worldX, MapExactValue worldZ)` (null without physical faces, points on a shared edge take the lowest face key, which has the same exact height). Internal `Compile(MapSurfaceRef, MapSurfacePatch, int maxFaces)` for the budget test. `Compile` runs `patch.ValidateLocal()` first, throwing its first finding, and counts faces before allocating them, refusing with `65536` (or the given budget).
- Internal, for the D7 bound-face context: `internal sealed class MapSlotCellMask` (immutable, 64 `ulong` words over slot cells `z*64 + x`) with `static MapSlotCellMask All`, `static MapSlotCellMask Of(IEnumerable<int> slotCells)`, `MapSlotCellMask Union(MapSlotCellMask other)`, `bool Contains(int slotCell)` and `int Count`. `internal static long CountFaces(MapSurfaceRef surface, MapSurfacePatch patch, MapSlotCellMask cells)` returns the faces the compiler would emit for those cells, read from cell bytes, presence and edge subdivisions with checked `long` arithmetic, allocating no face or vertex. Fan parents count 3 plus their inserted boundary vertices, and fallback and absent cells count 0. `Compile`'s own pre-count calls it, so there is one counting rule. `internal static MapCompiledPatch Compile(MapSurfaceRef surface, MapSurfacePatch patch, MapSlotCellMask cells, int maxFaces)` runs `ValidateLocal`, emits exactly the faces, keys, paint and exact triangles the full compile emits for those cells, keeps only the vertices those faces use, and refuses like the full compile above `maxFaces`.
- Arithmetic. `Native`: anchor X and Z are the floor of the patch's minimum world corner coordinate, anchor Y the floor of the midpoint of its height range, offsets exact rationals converted once. `LegacyTileWorld` requires `ImportedMetreCentimetre`, anchor `(SlotX * 64, 0, -SlotZ * 64)`, corner offsets `((float)(cx - originX), h * 0.01f, -(float)(cz - originZ))` and mid-edge `(a + b) * 0.5f`, the released `TileGroundTriangles.CornerPosition` and `MidEdgePosition` order (`TileGroundTriangles.cs:164,168`). Inserted subdivision vertices and fan centres have no legacy counterpart and use exact rationals converted once under either policy. Legacy cells with underlay 0 or `NoDraw` emit no faces and enter `LegacyFallbackCells`. That list is compile output only. Whether such a cell is covered, supported or a member's lower bound is decided solely by the D9 classifier (Task 12A), which uses this same rule. `Native` refuses `NoDraw`. Presence 0 emits nothing and is never a fallback. Feather is paint only.
- Subdivision follows D4: a parent triangle with inserted vertices on any of its edges is fanned from its exact centroid over its full boundary sequence. Inserted vertex heights are exact linear interpolations along their cell edge.

**Fixture.** `CompilerFixtures.OneCell(int sw, int se, int nw, int ne, MapSurfaceCell cell, MapSurfaceRole role = SupportFloor) -> (MapSurfaceRef Surface, MapSurfacePatch Patch)` is surface `one`, `(1/1, 1/100, PositiveZ)`, `Native`, slot (0, 0), cell 0. `Row(MapPresencePolicy, params (MapSurfaceCell Cell, bool Present)[])` is surface `row`, one row, corners 0, `ImportedMetreCentimetre` for the legacy policy. `ThirdsSurface()` is `thirds`, `(1/3, 1/100, PositiveZ)`, `Native`, and `ThirdsPatch()` is slot (-1, 0), `CellMinX 61`, width 3, depth 1, heights `[100, 150, 200, 250, 300, 350, 400, 450]`, all `Full` underlay 1. `Full = new MapSurfaceCell(1, 0, MapOverlayCut.Full, 0, MapCellFlags.None, MapCellTopology.Auto)`. Helpers: `Has(mesh, face, MapVertexId)`, `ExactAt(mesh, surfaceId, address) -> MapExactPoint`, `ExactArea(mesh, face)` (exact unsigned XZ area), `Area(mesh, slotCell)` (sum over a cell's faces), `ChildEdges(mesh, slotCell, parent)` (each child's boundary segment, the edge opposite the centre, as an exact pair), `ParentPlaneHeight(mesh, face)` (exact height of the parent triangle's plane at the child's centroid, from the parent's three corner or mid-edge vertices) and `CentreHeight(mesh, face)` (exact Y of the child's centroid from its own vertices).

- [ ] **Step 1: Write the failing tests**

```csharp
static MapCompiledPatch Compile((MapSurfaceRef Surface, MapSurfacePatch Patch) x) => MapSurfaceCompiler.Compile(x.Surface, x.Patch);

[Theory, InlineData((byte)0), InlineData((byte)1), InlineData((byte)2), InlineData((byte)3)]
public void NativeDiagonalHalfWithoutOverlayStillForcesSplit(byte r)
{
    Assert.Equal(r % 2 == 0, MapSurfaceTopology.SplitSwNe(0, 100, 100, 100, MapOverlayCut.DiagonalHalf, r, MapCellTopology.Auto));
    MapCompiledPatch mesh = Compile(CompilerFixtures.OneCell(0, 100, 100, 100, CompilerFixtures.Full with { Cut = MapOverlayCut.DiagonalHalf, Rotation = r }));
    var sw = new MapVertexId("one", MapLatticeAddress.Corner(0, 0));
    var ne = new MapVertexId("one", MapLatticeAddress.Corner(1, 1));
    Assert.Equal(2, mesh.Faces.Count);
    Assert.All(mesh.Faces, f => Assert.Equal(r % 2 == 0, CompilerFixtures.Has(mesh, f, sw) && CompilerFixtures.Has(mesh, f, ne)));
    Assert.All(mesh.Faces, f => Assert.True(f.Normal.Y > 0));
}
public void CeilingWindingReversesFloorWinding()
{
    MapCompiledPatch floor = Compile(CompilerFixtures.OneCell(0, 100, 50, 160, CompilerFixtures.Full));
    MapCompiledPatch ceiling = Compile(CompilerFixtures.OneCell(0, 100, 50, 160, CompilerFixtures.Full, MapSurfaceRole.Ceiling));
    Assert.Equal(floor.Faces.Select(f => (f.A, f.C, f.B)), ceiling.Faces.Select(f => (f.A, f.B, f.C)));
    Assert.All(floor.Faces, f => Assert.True(f.Normal.Y > 0));
    Assert.All(ceiling.Faces, f => Assert.True(f.Normal.Y < 0));
}
public void PhysicalApertureHasNoFacesAndNoFallback()
{
    MapCompiledPatch mesh = Compile(CompilerFixtures.Row(MapPresencePolicy.Native, (CompilerFixtures.Full, true), (CompilerFixtures.Full, false)));
    Assert.Equal(2, mesh.Faces.Count);
    Assert.All(mesh.Faces, f => Assert.Equal(0, f.Key.Primitive));
    Assert.Empty(mesh.LegacyFallbackCells);
}
public void ImportedNoDrawAndVoidKeepPresenceFlagsAndTaggedFallback()
{
    var noDraw = CompilerFixtures.Full with { Flags = MapCellFlags.NoDraw };
    var voidCell = CompilerFixtures.Full with { Underlay = 0 };
    var (s, p) = CompilerFixtures.Row(MapPresencePolicy.LegacyTileWorld, (CompilerFixtures.Full, true), (noDraw, true), (voidCell, true));
    MapCompiledPatch mesh = MapSurfaceCompiler.Compile(s, p);
    Assert.Equal(new[] { 1, 2 }, mesh.LegacyFallbackCells.Select(c => c.SlotCell));
    Assert.Equal(new MapSubmissionAnchor(0, 0, 0), mesh.Anchor);
    Assert.All(mesh.Faces, f => Assert.Equal(0, f.Key.Primitive));
    Assert.True(p.IsPresent(1, 0) && p.IsPresent(2, 0));
    Assert.Equal(MapCellFlags.NoDraw, p.Cells[1].Flags);
}
public void OverlayCut_NeverOpensOrClosesAPhysicalAperture()
{
    var (s, p) = CompilerFixtures.Row(MapPresencePolicy.Native, (CompilerFixtures.Full, true), (CompilerFixtures.Full, false));
    MapCompiledPatch before = MapSurfaceCompiler.Compile(s, p);
    p.Cells[0] = p.Cells[0] with { Overlay = 5, Cut = MapOverlayCut.CornerQuarter, Rotation = 1 };
    p.Cells[1] = p.Cells[1] with { Overlay = 5, Cut = MapOverlayCut.CornerThreeQuarter };
    MapCompiledPatch after = MapSurfaceCompiler.Compile(s, p);
    Assert.Equal((new MapExactValue(1, 1), new MapExactValue(0, 1)), (CompilerFixtures.Area(after, 0), CompilerFixtures.Area(after, 1)));
    Assert.Equal(CompilerFixtures.Area(before, 0), CompilerFixtures.Area(after, 0));
    Assert.False(p.IsPresent(1, 0));
    Assert.Contains(after.Paint, c => c.OverlayCovers && c.Overlay == 5);
    Assert.DoesNotContain(before.Paint, c => c.OverlayCovers);
}
public void SubmissionAnchorsAreWholeMetresAndOffsetsExact()
{
    MapCompiledPatch mesh = MapSurfaceCompiler.Compile(CompilerFixtures.ThirdsSurface(), CompilerFixtures.ThirdsPatch());
    Assert.Equal(new MapSubmissionAnchor(-1, 2, 0), mesh.Anchor);
    int i = mesh.VertexIds.ToList().IndexOf(new MapVertexId("thirds", MapLatticeAddress.Corner(-2, 1)));
    Assert.Equal(new Vector3(0.33333334f, 1.5f, 0.33333334f), mesh.Offsets[i]);
    Assert.Equal(new MapExactValue(7, 2), MapSurfaceCompiler.ExactHeight(CompilerFixtures.ThirdsSurface(), CompilerFixtures.ThirdsPatch(), new(-2, 3), new(1, 3)));
}
[Theory, InlineData(2, 5, 1, 2), InlineData(3, 6, 1, 3)]
public void RimSubdivision_FansFromTheCentroidWithExactAreaAndUniqueKeys(int k, int faces, int firstNumerator, int denominator)
{
    var (s, p) = CompilerFixtures.OneCell(100, 100, 100, 100, CompilerFixtures.Full);
    p.EdgeSubdivisions.Add(new MapEdgeSubdivision(0, 0, MapCellEdge.South, k));       // patch boundary, legal
    MapCompiledPatch mesh = MapSurfaceCompiler.Compile(s, p);
    Assert.Equal(faces, mesh.Faces.Count);                                            // k+2 children on the south parent, 1 on the other
    Assert.Contains(new MapVertexId("one", MapLatticeAddress.Create(firstNumerator, 0, denominator)), mesh.VertexIds);
    Assert.Contains(new MapVertexId("one", MapLatticeAddress.Create(2, 1, 3)), mesh.VertexIds);   // centroid of (Sw, Se, Ne)
    Assert.Equal(faces, mesh.Faces.Select(f => f.Key).Distinct().Count());
    Assert.Equal(new MapExactValue(1, 1), CompilerFixtures.Area(mesh, 0));
}
public void RimSubdivision_TwoEdgesOfOneTriangleAt64StayDisjointAndCollisionFree()
{
    var (s, p) = CompilerFixtures.OneCell(0, 100, 50, 160, CompilerFixtures.Full);   // splits NW-SE, the (Se, Ne, Nw) triangle owns East and North
    p.EdgeSubdivisions.Add(new MapEdgeSubdivision(0, 0, MapCellEdge.East, 64));
    p.EdgeSubdivisions.Add(new MapEdgeSubdivision(0, 0, MapCellEdge.North, 64));
    MapCompiledPatch mesh = MapSurfaceCompiler.Compile(s, p);
    byte parent = mesh.Faces.GroupBy(f => f.Key.ParentTriangle).Single(g => g.Count() > 1).Key;
    var children = mesh.Faces.Where(f => f.Key.ParentTriangle == parent).ToList();
    Assert.Equal(129, children.Count);                                                 // 3 + 63 + 63
    Assert.Equal(Enumerable.Range(0, 129).Select(i => (ushort)i), children.Select(f => f.Key.Child));
    Assert.Equal(130, mesh.Faces.Select(f => f.Key).Distinct().Count());
    Assert.All(children, f => Assert.True(CompilerFixtures.ExactArea(mesh, f).Sign > 0));
    Assert.Equal(new MapExactValue(1, 2), children.Aggregate(new MapExactValue(0, 1), (a, f) => a.Add(CompilerFixtures.ExactArea(mesh, f))));
    Assert.Equal(129, CompilerFixtures.ChildEdges(mesh, 0, parent).Distinct().Count());   // 64 east, 64 north, 1 diagonal, each once
    Assert.All(mesh.Paint.Where(c => c.Face.ParentTriangle == parent), c => Assert.Equal(1, c.Underlay));
    Assert.All(children, f => Assert.Equal(CompilerFixtures.ParentPlaneHeight(mesh, f), CompilerFixtures.CentreHeight(mesh, f)));
}
public void EdgeSubdivision_RefusesInteriorAndOutOfRangeSplits()
{
    var (s, p) = CompilerFixtures.Row(MapPresencePolicy.Native, (CompilerFixtures.Full, true), (CompilerFixtures.Full, true));
    p.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.East, 2));
    Assert.Contains("interior", Assert.Throws<MapDocumentException>(() => MapSurfaceCompiler.Compile(s, p)).Message);
    var (s1, p1) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
    p1.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.South, 65));
    Assert.Contains("segments", Assert.Throws<MapDocumentException>(() => MapSurfaceCompiler.Compile(s1, p1)).Message);
}

// CompilerBudgetTests (MapEditor.Tests, internal overload)
public void Compile_RefusesBeforeExceedingTheFaceBudget()
{
    var (s, p) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
    p.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.South, 64));                          // 66 children plus 1
    Assert.Contains("66", Assert.Throws<MapDocumentException>(() => MapSurfaceCompiler.Compile(s, p, maxFaces: 66)).Message);
    Assert.Equal(67, MapSurfaceCompiler.Compile(s, p, maxFaces: 67).Faces.Count);
}
public void CountFaces_MatchesTheCompilerAndAMaskedCompileKeepsExactlyItsCells()
{
    var (s, p) = CompilerFixtures.OneCell(0, 100, 50, 160, CompilerFixtures.Full);
    p.EdgeSubdivisions.Add(new MapEdgeSubdivision(0, 0, MapCellEdge.East, 64));
    p.EdgeSubdivisions.Add(new MapEdgeSubdivision(0, 0, MapCellEdge.North, 64));
    Assert.Equal(130L, MapSurfaceCompiler.CountFaces(s, p, MapSlotCellMask.All));               // 129 fan children plus 1, as compiled
    MapSurfaceRef thirds = CompilerFixtures.ThirdsSurface();
    MapSurfacePatch patch = CompilerFixtures.ThirdsPatch();
    MapSlotCellMask one = MapSlotCellMask.Of(new[] { 62 });
    Assert.Equal((6L, 2L), (MapSurfaceCompiler.CountFaces(thirds, patch, MapSlotCellMask.All), MapSurfaceCompiler.CountFaces(thirds, patch, one)));
    MapCompiledPatch full = MapSurfaceCompiler.Compile(thirds, patch), masked = MapSurfaceCompiler.Compile(thirds, patch, one, MapSurfaceCompiler.MaxFacesPerPatch);
    Assert.Equal(full.Faces.Where(f => f.Key.Primitive == 62).Select(f => (f.Key, full.ExactTriangle(f))), masked.Faces.Select(f => (f.Key, masked.ExactTriangle(f))));
}

// LegacyOracleCompilerTests (Compatibility.Tests)
public void LegacyOracle_SyntheticWorldMatchesNativeCompiler()
{
    LegacyOracleWorld w = LegacyOracleWorld.Create();
    foreach (var (region, plane) in LegacyOracleConverter.RegionPlanes(w.Document))
    {
        var (surface, patch) = LegacyOracleConverter.ToNative(w.Document, region, plane);
        MapCompiledPatch native = MapSurfaceCompiler.Compile(surface, patch);
        TileGroundMesh legacy = TileGroundTriangles.Build(w.Document, region, plane);
        Assert.Equal(legacy.Indices.Length / 3, native.Faces.Count);
        LegacyOracleConverter.AssertSameTriangles(legacy, native, vertexTolerance: 0.00001f, normalTolerance: 0.000001f);
        Assert.Equal(LegacyOracleConverter.FallbackCells(w.Document, region, plane), native.LegacyFallbackCells.Select(c => c.SlotCell));
        Assert.Equal(LegacyOracleConverter.CellBytes(w.Document, region, plane), LegacyOracleConverter.CellBytes(patch));
    }
}
```

`LegacyOracleConverter` is test-only. `RegionPlanes(doc)` lists every `(RegionCoord, plane)` with authored content in ascending order. `ToNative` produces surface `plane-<n>` (`ImportedMetreCentimetre`, `LegacyTileWorld`, `SupportFloor`), key `(plane-<n>, rx, rz)`, a full 64 by 64 rectangle, heights from `CornerHeightCm` (derived planes materialized), cells copied byte for byte, presence 1. `AssertSameTriangles` pairs triangles in emission order and compares world positions (legacy region origin plus vertex, native anchor plus offset) and `LegacyNormal`, which is `Vector3.Normalize(Vector3.Cross(b - a, c - a))`, the formula the native compiler uses for this policy. `FallbackCells` and `CellBytes` read the legacy layers.

- [ ] **Step 2: Run red.** `wa_test t9-red "$MAPDOC" "FullyQualifiedName~SurfaceCompilerTests"`. Expected: build FAIL naming `MapSurfaceCompiler`.
- [ ] **Step 3: Implement topology, the fan, the compiler and the test-only converter.**
- [ ] **Step 4: Run green.** `wa_test t9-green-mapdoc "$MAPDOC" "FullyQualifiedName~SurfaceCompilerTests"` (PASS, 13 cases), `wa_test t9-green-editor "$EDITOR" "FullyQualifiedName~CompilerBudgetTests"` (PASS, 2), `wa_run t9-green-compat dotnet test "$COMPAT" -c Release` (PASS, 2). A tolerance failure is a reported blocker with the offending case, never a widened tolerance.
- [ ] **Step 5: Commit** `KhaozEngine.MapDoc/Surfaces KhaozEngine.MapDoc.Tests/Surfaces KhaozEngine.MapEditor.Tests/MapDoc/CompilerBudgetTests.cs KhaozEngine.MapDoc.Compatibility.Tests`, message `feat(mapdoc): compile canonical faces with exact subdivision fans and legacy arithmetic`.

---

### Task 10: Chains, wall strips, cross-lattice seams and opening planes

**Files:**
- Create: `KhaozEngine.MapDoc/Surfaces/MapBoundaryGeometry.cs`, `MapWallStripCompiler.cs`, `MapSeamValidator.cs`, `MapOpeningBoundary.cs`
- Test: `KhaozEngine.MapDoc.Tests/Surfaces/BoundaryGeometryTests.cs`, fixture `BoundaryFixtures.cs`

**Interfaces (new):**
- `public enum MapResolveStatus { Resolved, MissingGeometry, Invalid }`, `public sealed record MapChainResolution(MapResolveStatus Status, IReadOnlyList<MapExactPoint> Points, string? Detail)`.
- `public static class MapBoundaryGeometry { public static MapChainResolution ResolveChain(MapBoundaryChain chain, MapScopedSurfaces view); }`. `SurfaceEdge` vertices take the exact height of the source surface at that address (a corner, a legacy mid-edge or a subdivision vertex). `Authored` vertices use their surface's height unit. A patch read other than `Present` is `MissingGeometry`.
- `public sealed class MapCompiledStrip` with `string StripId`, `MapSubmissionAnchor Anchor`, `IReadOnlyList<MapExactPoint> ExactVertices`, `IReadOnlyList<Vector3> Offsets`, `IReadOnlyList<MapCompiledFace> Faces`.
- `public static class MapWallStripCompiler { public static MapCompiledStrip Compile(MapWallStrip strip, MapChainResolution lower, MapChainResolution upper); }`. The two chains must have identical ordered exact world XZ sequences, even when they lie on different lattices (`vertex sequence`). Lower above upper anywhere refuses (`lower`). Segment `i` is the ruled quad `L_i, L_i+1, U_i+1, U_i` split lower-start to upper-end into triangle 0 `{L_i, L_i+1, U_i+1}` and triangle 1 `{L_i, U_i+1, U_i}`, with key `(strip, null, i, triangle, 0, side)`. Zero-area triangles drop. For chain direction `(dx, dz)` the Front normal is `normalize(dz, 0, -dx)`. `TwoSided` emits both sides under one owner with distinct `Side`.
- `public static class MapSeamValidator` with `IReadOnlyList<string> Validate(MapSurfaceSeam seam, MapScopedSurfaces view)` (each pair maps to one exact world XZ (`position`) and height (`height`), and the pairs cover every vertex of both edges, corners, legacy mid-edges and subdivision vertices (`subdivide`)), and `IReadOnlyList<string> ValidateCornerDependencies(MapSurfacePatch patch, MapScopedSurfaces view)` (dependent corner and owner address agree on exact world XZ (`position`) and exact height in metres (`owner`), owner patch not present (`unresolved`)). The given patch is validated as passed, owners are read from the view.
- `public readonly record struct MapPortalPlaneTriangle(MapVertexId A, MapVertexId B, MapVertexId C, Vector3 Normal)`, `public sealed record MapOpeningPlane(string OpeningId, MapSubmissionAnchor Anchor, IReadOnlyList<Vector3> Offsets, IReadOnlyList<MapPortalPlaneTriangle> Triangles, IReadOnlyList<MapExactTriangle> ExactTriangles, IReadOnlyList<MapFaceKey> Keys)`, `public static class MapOpeningBoundary { public static MapOpeningPlane Compile(MapSurfaceRef surface, MapSurfacePatch patch, MapHorizontalOpening opening); }`. The plane uses the canonical triangulation those absent cells would have, normals up, keys `(openingId, patch, slotCell, parent, 0, Front)`, and is never support, draw, capture or collision input.

**Fixture.** All views are `MapScopedSurfaces.CompleteView(set)` (Task 8). `CompileStrip(int[] lower, int[] upper, MapStripFacing facing) -> MapCompiledStrip` builds surface `s` `(1/1, 1/100, PositiveZ)`, Authored chains `lower` and `upper` at `Corner(0,0)`, `Corner(1,0)`, ... with the given heights, strip `w`, resolves both chains and compiles. `CoarseFineSeam(int k, bool subdivided, int middleOffset) -> (MapScopedSurfaces View, MapSurfaceSeam Seam)` builds `coarse` (`(1/1, 1/100)`, one cell, SW 100, SE 200, NW 100, NE 200, north edge subdivided `k` when `subdivided`) and `fine` (`(1/k, 1/(100k))`, cells x 0 to k-1 in the fine row above the coarse cell, its south corner row at fine lattice z = k with heights `100k + 100i` for `i = 0..k`, plus `middleOffset` added at `i = 1`), and the seam pairing coarse `Create(i, k, k)` with fine `Corner(i, k)`. `NegativeSlotPair() -> (MapScopedSurfaces View, (MapSurfaceRef Surface, MapSurfacePatch Patch) Neg, (MapSurfaceRef Surface, MapSurfacePatch Patch) Pos)` builds surface `neg` slot (-1, 0) (`CellMinX 63`, heights 10, 20, 30, 40) and slot (0, 0) (`CellMinX 0`, heights 20, 50, 40, 60) where slot (0, 0) records owners `(neg(-1,0), Corner(0,0))` and `(neg(-1,0), Corner(0,1))`. `FineCornerOnCoarseRim() -> (MapScopedSurfaces View, MapSurfacePatch Fine)` is `CoarseFineSeam(3, true, 0)` plus the fine patch dependency `(1, 0, owner (coarse(0,0), Create(1, 3, 3)))`, and `ViewWithoutCoarse()` is the same set without `coarse` (0, 0). `TwoCellOpening() -> (MapSurfaceRef, MapSurfacePatch, MapHorizontalOpening)` is `o`, cells x 0 to 2 at 400, presence 0 on slot cells 0 and 1, opening `hole` over `[0, 1]`.

- [ ] **Step 1: Write the failing tests**

```csharp
public void WallStrip_RuledQuadsSplitLowerStartToUpperEnd()
{
    MapCompiledStrip s = BoundaryFixtures.CompileStrip(new[] { 0, 0 }, new[] { 300, 300 }, MapStripFacing.Front);
    Assert.Equal(new[] { (0, (byte)0), (0, (byte)1) }, s.Faces.Select(f => (f.Key.Primitive, f.Key.ParentTriangle)));
    Assert.All(s.Faces, f => Assert.Equal(new Vector3(0, 0, -1), f.Normal));
}
public void WallStrip_ZeroHeightEndDegeneratesToOneTriangle()
    => Assert.Single(BoundaryFixtures.CompileStrip(new[] { 0, 0 }, new[] { 0, 300 }, MapStripFacing.Front).Faces);
public void WallStrip_LowerAboveUpperRefuses()
    => Assert.Contains("lower", Assert.Throws<MapDocumentException>(() => BoundaryFixtures.CompileStrip(new[] { 0, 400 }, new[] { 300, 300 }, MapStripFacing.Front)).Message);
public void TwoSidedLintelEmitsOppositeFacesUnderOneOwner()
{
    MapCompiledStrip s = BoundaryFixtures.CompileStrip(new[] { 0, 0 }, new[] { 300, 300 }, MapStripFacing.TwoSided);
    Assert.Equal(4, s.Faces.Select(f => f.Key).Distinct().Count());
    Assert.All(s.Faces, f => Assert.Equal("w", f.Key.OwnerId));
    Assert.Equal(2, s.Faces.Count(f => f.Key.Side == MapSide.Back && f.Normal == new Vector3(0, 0, 1)));
}
[Theory, InlineData(2, true, 0, null), InlineData(2, false, 0, "subdivide"), InlineData(2, true, 1, "height"),
 InlineData(3, true, 0, null), InlineData(64, true, 0, null)]
public void Seam_ExactSharedVerticesAgreeAcrossLattices(int k, bool subdivided, int middleOffset, string? finding)
{
    var (view, seam) = BoundaryFixtures.CoarseFineSeam(k, subdivided, middleOffset);
    IReadOnlyList<string> findings = MapSeamValidator.Validate(seam, view);
    if (finding is null) Assert.Empty(findings); else Assert.Contains(findings, f => f.Contains(finding));
}
public void Seam_AcrossNegativeSlotEdgeSharesExactVertices()
{
    var (view, neg, pos) = BoundaryFixtures.NegativeSlotPair();
    Assert.Empty(MapSeamValidator.ValidateCornerDependencies(pos.Patch, view));
    MapCompiledPatch a = MapSurfaceCompiler.Compile(neg.Surface, neg.Patch), b = MapSurfaceCompiler.Compile(pos.Surface, pos.Patch);
    var shared = MapLatticeAddress.Corner(0, 0);
    Assert.Equal(CompilerFixtures.ExactAt(a, "neg", shared), CompilerFixtures.ExactAt(b, "neg", shared));
    Assert.Equal((new MapSubmissionAnchor(-1, 0, 0), new MapSubmissionAnchor(0, 0, 0)), (a.Anchor, b.Anchor));
    pos.Patch.Heights[0] = 21;
    Assert.Contains(MapSeamValidator.ValidateCornerDependencies(pos.Patch, view), f => f.Contains("owner"));
}
public void CornerDependency_OnAFineBoundaryResolvesTheCoarseSubdivisionVertex()
{
    var (view, fine) = BoundaryFixtures.FineCornerOnCoarseRim();
    Assert.Empty(MapSeamValidator.ValidateCornerDependencies(fine, view));
    fine.Heights[1] += 1;                                          // fine corner (1, 0) of the patch's south row
    Assert.Contains(MapSeamValidator.ValidateCornerDependencies(fine, view), f => f.Contains("owner"));
    fine.Heights[1] -= 1;
    Assert.Contains(MapSeamValidator.ValidateCornerDependencies(fine, BoundaryFixtures.ViewWithoutCoarse()), f => f.Contains("unresolved"));
}
public void HorizontalOpeningIsAMembershipPlaneNotGeometry()
{
    var (surface, patch, opening) = BoundaryFixtures.TwoCellOpening();
    MapOpeningPlane plane = MapOpeningBoundary.Compile(surface, patch, opening);
    Assert.Equal((4, 4, 4), (plane.Triangles.Count, plane.ExactTriangles.Count, plane.Keys.Distinct().Count()));
    Assert.All(plane.Triangles, t => Assert.True(t.Normal.Y > 0));
    Assert.DoesNotContain(MapSurfaceCompiler.Compile(surface, patch).Faces, f => f.Key.Primitive is 0 or 1);
}
```

- [ ] **Step 2: Run red.** `wa_test t10-red "$MAPDOC" "FullyQualifiedName~BoundaryGeometryTests"`. Expected: build FAIL naming `MapWallStripCompiler`.
- [ ] **Step 3: Implement chain resolution, strips, seams and opening planes.**
- [ ] **Step 4: Run green.** `wa_test t10-green "$MAPDOC" "FullyQualifiedName~BoundaryGeometryTests|FullyQualifiedName~SurfaceCompilerTests"`. Expected: PASS, 25 cases.
- [ ] **Step 5: Commit** `KhaozEngine.MapDoc/Surfaces KhaozEngine.MapDoc.Tests/Surfaces`, message `feat(mapdoc): compile wall strips, exact cross-lattice seams and opening planes`.

---

### Task 11: Exact fine-patch conversion on the rim model

**Files:**
- Create: `KhaozEngine.MapDoc/Editing/MapNativeWriteSet.cs`, `MapFinePatchConversion.cs`, `MapConversionClassifier.cs`
- Test: `KhaozEngine.MapDoc.Tests/Editing/FinePatchConversionTests.cs`, fixture `ConversionFixtures.cs`

**Interfaces (new, `KhaozEngine.MapDoc.Editing`):**
- `public sealed record MapCornerOwnerChange(MapPatchKey Dependent, int CornerX, int CornerZ, MapVertexOwner? OldOwner, MapVertexOwner NewOwner)`, `public sealed record MapNativeWriteSet(IReadOnlyList<MapPatchKey> Patches, IReadOnlyList<string> SurfaceIds, IReadOnlyList<string> RecordIds, IReadOnlyList<string> SpaceIds, IReadOnlyList<MapCornerOwnerChange> OwnerChanges, IReadOnlyList<ushort> Materials, bool Placements)` with `static PlacementsOnly`.
- `public sealed record MapFinePatchRequest(string SourceSurfaceId, IReadOnlyList<MapCellRect> Regions, int Subdivision, string FineSurfaceId, bool AcceptAuthoredDifferences)` (regions disjoint, absolute source cells, maximum exclusive).
- `public enum MapCellConversionClass : byte { ExactCoplanar, ExactDiagonal, UnsupportedEncoding, NotRepresentable }`, `public sealed record MapCellConversion(long CellX, long CellZ, MapCellConversionClass Class, string? Reason)` (`Reason` contains `crease`, `paint` or `legacy fallback`).
- `public enum MapDifferenceKind : byte { Geometry, Paint, FallbackRemoved, FlagRemoved, ArithmeticPolicy, Retessellated }`, `public sealed record MapAuthoredDifference(MapDifferenceKind Kind, long CellX, long CellZ, MapFaceKey? OldFace, MapFaceKey? NewFace, string Detail)`. `Retessellated` rows record a rim neighbour face replaced by fan children with an unchanged plane and need no acceptance.
- `public sealed record MapConversionResult(MapSurfaceSet Candidate, IReadOnlyList<MapCellConversion> Cells, IReadOnlyList<MapAuthoredDifference> Differences, MapNativeWriteSet WriteSet)`.
- `public static class MapConversionClassifier { public static MapCellConversion Classify(MapSurfaceRef surface, MapSurfacePatch patch, long cellX, long cellZ, int subdivision); }` (absolute source cell).
- `public static class MapFinePatchConversion { public static MapConversionResult Convert(MapSurfaceSet surfaces, MapFinePatchRequest request); }`. Rules (D4): subdivision 2 to 64, fine units `u/k` and `h/k` reduced, fine corner heights are the exact canonical plane values times `k` (integers by construction, int32 overflow refuses with `overflow`), crossed fine cells take the forced topology, paint and flags are reproduced per fine cell so every overlay's exact XZ area is unchanged, region cells become presence 0, rim edges gain `k` subdivisions, odd `k` next to a legacy mid-edge on the rim adds a 2-segment fine boundary subdivision, fine boundary corners record owners on the coarse patch at their exact coarse addresses, one seam per rim edge. Footprints bounded by the source surface follow the D4 footprint rule (retarget in place, split into `<id>-<fineSurfaceId>`, or refuse atomically with the D4 `footprint straddle` message naming the first offending footprint and slot cell and the alignment guidance), computed in exact world coordinates on each footprint's own lattice, which may differ from the source surface's lattice. A moved `LegacyExteriorV1` lower bound becomes `SupportFloor` on the fine surface (D4). Other bounds of those footprints are never touched, and no footprint is subdivided automatically. A region whose edges carry a `SurfaceEdge` chain, a portal interval or an opening of the source surface refuses naming that record. Classification and footprint checks run for every region cell before any mutation, on `surfaces.Clone()`, so a refusal leaves the input untouched. Without acceptance any `UnsupportedEncoding` or `NotRepresentable` cell, and any `LegacyTileWorld` source cell (its `ArithmeticPolicy` rows), refuses with `authored difference` and every `cell (x, z)`. With acceptance, `UnsupportedEncoding` cells take exact canonical corner heights with `Auto` topology and fine-cell-centroid paint, `NotRepresentable` cells become physical faces from their legacy authored triangulation, and every removed and added face is listed.

**Fixture.** `ConversionFixtures.Slopes(MapOverlayCut centreCut = Full, byte centreRotation = 0, bool coplanarCentre = false, MapOverlayCut southCut = Full, byte southRotation = 0) -> MapSurfaceSet`: surface `coarse`, `(1/1, 1/100, PositiveZ)`, `Native`, `SupportFloor`, slot (0, 0), 3 by 3 cells, corner rows z 0 to 3: `[0, 100, 200, 300]`, `[50, 160, 250, 360]`, `[100, 210, 330, 400]`, `[150, 260, 380, 500]`. The centre cell (1, 1) has SW 160, SE 250, NW 210, NE 330 (`coplanarCentre` sets NE to 300), splits NW-SE, and is underlay 1, overlay 7, the given cut and rotation, flags `FeatherOverlay | Blocked`. Cell (1, 0) uses `southCut`/`southRotation` with overlay 3. Other cells are underlay 1, overlay 0. `Request(int k, bool accept)` converts region `(1, 1, 2, 2)` into `coarse-fine-1`. `Coarse(set) -> (MapSurfaceRef Surface, MapSurfacePatch Patch)`, `Fine(result)` is the fine patch at the fine slot holding fine cells `k` to `2k-1`, `FineSurface(result)` its ref. `FineCorners(patch)` returns all corner heights row by row, `FineCell(patch, x, z)` a local cell, `ExactHeight(set, x, z)` the highest `MapSurfaceCompiler.ExactHeight` among `SupportFloor` patches, `OverlayArea(set)` the exact overlay-covered area per overlay ID. `RimSeam(result, MapCellEdge edge)` is the seam on that side of the hole, `FractionAlongCoarseEdge(result, MapLatticeVertex v)` its exact position along the coarse rim edge. `WithCaveCeiling()` adds surface `roof` (Ceiling, same frame) with one patch cell at (1, 1), height 900, its four boundary edges subdivided 3, and space `room` (anchored `coarse` (0, 0)) with footprint `room-cells` (lattice `coarse` (0, 0)) over slot cell `1*64+1`, lower `coarse`, upper `roof`. `WithWallOnTheCentreEdge()` adds a `SurfaceEdge` chain `west-foot` on the centre cell's west edge. `LegacyRowWithNoDraw()` is `plane-0` (`ImportedMetreCentimetre`, `LegacyTileWorld`), cells x 0 to 1, z 0, flat 0, cell 1 `NoDraw`, with Exterior `world` and footprint `world-cells` (lattice `plane-0` (0, 0), slot cells `[0, 1]`, lower `(LegacyExteriorV1, "plane-0", null)`, upper `OpenTop`) anchored in `plane-0` (0, 0), so the input meets the D9 recipe. `Legacy(set)` is its surface and patch, and `LegacyRequest(k, accept)` converts both cells into `plane-0-fine-1`. `MixedPorch()`: `half-floor` (SupportFloor `(1/2, 1/200)`) slot (0, 0) 2 by 2 cells at 0, `third-roof` (Ceiling `(1/3, 1/300)`) slot (0, 0) 3 by 3 cells at 900, Exterior `porch` with footprint `porch-cells` on lattice `half-floor` (0, 0), slot cells `[0, 1, 64, 65]`, lower `half-floor`, upper `third-roof`, both anchored in `half-floor` (0, 0). `PorchRoofRequest(int width)` converts `third-roof` cells `(0, 0, width, 3)` with `k = 2` into `third-roof-fine-1`.

- [ ] **Step 1: Write the failing tests**

```csharp
static MapConversionResult Convert(MapSurfaceSet set, MapFinePatchRequest r) => MapFinePatchConversion.Convert(set, r);
static Dictionary<MapPatchKey, string> Digests(MapSurfaceSet set) => set.Patches.ToDictionary(p => p.Key, p => MapSurfaceSemantics.PatchDigest(p.Value));

public void Conversion_HalvesPreserveDiagonalTrianglesPaintAndFlagsExactly()
{
    MapConversionResult r = Convert(ConversionFixtures.Slopes(), ConversionFixtures.Request(2, false));
    Assert.Equal(MapCellConversionClass.ExactDiagonal, r.Cells.Single().Class);
    MapSurfacePatch fine = ConversionFixtures.Fine(r);
    Assert.Equal(new[] { 320, 410, 500, 370, 460, 580, 420, 540, 660 }, ConversionFixtures.FineCorners(fine));
    Assert.Equal((MapCellTopology.ForceNwSe, MapCellTopology.ForceNwSe), (ConversionFixtures.FineCell(fine, 1, 0).Topology, ConversionFixtures.FineCell(fine, 0, 1).Topology));
    for (int z = 0; z < 2; z++)
        for (int x = 0; x < 2; x++)
        {
            MapSurfaceCell c = ConversionFixtures.FineCell(fine, x, z);
            Assert.Equal(((ushort)7, MapOverlayCut.Full, MapCellFlags.FeatherOverlay | MapCellFlags.Blocked), (c.Overlay, c.Cut, c.Flags));
        }
    Assert.False(r.Candidate.Patches[new("coarse", 0, 0)].IsPresent(1, 1));
    Assert.Equal((new MapRational(1, 2), new MapRational(1, 200)), (ConversionFixtures.FineSurface(r).Frame.CellUnitMetres, ConversionFixtures.FineSurface(r).Frame.HeightUnitMetres));
}
[Theory, InlineData(MapOverlayCut.Full, (byte)0), InlineData(MapOverlayCut.DiagonalHalf, (byte)1)]
public void Conversion_ThirdsAreExactWithForcedDiagonalsAndHalfPaint(MapOverlayCut cut, byte rotation)
{
    MapConversionResult r = Convert(ConversionFixtures.Slopes(cut, rotation), ConversionFixtures.Request(3, false));
    Assert.Equal(MapCellConversionClass.ExactDiagonal, r.Cells.Single().Class);
    MapSurfacePatch fine = ConversionFixtures.Fine(r);
    Assert.Equal(new[] { 480, 570, 660, 750, 530, 620, 710, 830, 580, 670, 790, 910, 630, 750, 870, 990 }, ConversionFixtures.FineCorners(fine));
    foreach (var (x, z) in new[] { (0, 2), (1, 1), (2, 0) }) Assert.Equal(MapCellTopology.ForceNwSe, ConversionFixtures.FineCell(fine, x, z).Topology);
    if (cut == MapOverlayCut.DiagonalHalf)
    {
        MapSurfaceCell centre = ConversionFixtures.FineCell(fine, 1, 1);
        Assert.Equal((MapOverlayCut.DiagonalHalf, (byte)1, (ushort)7), (centre.Cut, centre.Rotation, centre.Overlay));
        Assert.Equal(((ushort)0, (ushort)7), (ConversionFixtures.FineCell(fine, 0, 0).Overlay, ConversionFixtures.FineCell(fine, 2, 2).Overlay));
    }
    Assert.Equal(ConversionFixtures.OverlayArea(ConversionFixtures.Slopes(cut, rotation)), ConversionFixtures.OverlayArea(r.Candidate));
}
[Theory, InlineData(2, 8), InlineData(3, 12)]
public void Conversion_SubdividesRimEdgesWithStableOwners(int k, int dependencies)
{
    MapConversionResult r = Convert(ConversionFixtures.Slopes(), ConversionFixtures.Request(k, false));
    MapSurfacePatch coarse = r.Candidate.Patches[new("coarse", 0, 0)];
    Assert.Equal(new[] { new MapEdgeSubdivision(1, 0, MapCellEdge.North, k), new(0, 1, MapCellEdge.East, k), new(2, 1, MapCellEdge.West, k), new(1, 2, MapCellEdge.South, k) }.ToHashSet(),
        coarse.EdgeSubdivisions.ToHashSet());
    MapSurfacePatch fine = ConversionFixtures.Fine(r);
    Assert.Equal(dependencies, fine.CornerDependencies.Count);                    // every boundary vertex, 4k
    Assert.All(fine.CornerDependencies, d => Assert.Equal(new MapPatchKey("coarse", 0, 0), d.Owner.Patch));
    MapScopedSurfaces view = MapScopedSurfaces.CompleteView(r.Candidate);
    var seams = r.Candidate.AllRecords().OfType<MapSurfaceSeam>().ToList();
    Assert.Equal(4, seams.Count);
    Assert.All(seams, s => Assert.Empty(MapSeamValidator.Validate(s, view)));
    Assert.All(r.Candidate.Patches.Values, p => Assert.Empty(p.ValidateLocal()));
}
public void Conversion_OddSubdivisionNextToACornerCutRimMatchesBothSequences()
{
    MapConversionResult r = Convert(ConversionFixtures.Slopes(southCut: MapOverlayCut.CornerQuarter, southRotation: 2), ConversionFixtures.Request(3, false));
    Assert.Contains(new MapEdgeSubdivision(1, 0, MapCellEdge.South, 2), ConversionFixtures.Fine(r).EdgeSubdivisions);
    MapSurfaceSeam south = ConversionFixtures.RimSeam(r, MapCellEdge.South);
    Assert.Equal(new[] { new MapExactValue(0, 1), new(1, 3), new(1, 2), new(2, 3), new(1, 1) }, south.Pairs.Select(p => ConversionFixtures.FractionAlongCoarseEdge(r, p.First)));
    Assert.Empty(MapSeamValidator.Validate(south, MapScopedSurfaces.CompleteView(r.Candidate)));
    Assert.Equal(new[] { (1L, 0L), (0L, 1L), (2L, 1L), (1L, 2L) }.ToHashSet(),
        r.Differences.Where(d => d.Kind == MapDifferenceKind.Retessellated).Select(d => (d.CellX, d.CellZ)).ToHashSet());
    Assert.DoesNotContain(r.Differences, d => d.Kind is MapDifferenceKind.Geometry or MapDifferenceKind.Paint);
}
public void Conversion_SixtyFourthsStayExact()
{
    MapSurfaceSet before = ConversionFixtures.Slopes();
    MapConversionResult r = Convert(before, ConversionFixtures.Request(64, false));
    MapSurfacePatch fine = ConversionFixtures.Fine(r);
    IReadOnlyList<int> corners = ConversionFixtures.FineCorners(fine);
    Assert.Equal((65 * 65, 160 * 64, 330 * 64), (corners.Count, corners[0], corners[^1]));
    for (int z = 0; z <= 8; z++)
        for (int x = 0; x <= 8; x++)
            Assert.Equal(ConversionFixtures.ExactHeight(before, new(8 + x, 8), new(8 + z, 8)), ConversionFixtures.ExactHeight(r.Candidate, new(8 + x, 8), new(8 + z, 8)));
    Assert.Equal(ConversionFixtures.OverlayArea(before), ConversionFixtures.OverlayArea(r.Candidate));
    Assert.True(MapSurfacePatchCodec.Encode(fine).Length < 1_048_576);
}
[Theory, InlineData(2), InlineData(3)]
public void Conversion_NoDriftOnAnyExactSamplePoint(int k)
{
    MapSurfaceSet before = ConversionFixtures.Slopes();
    MapConversionResult r = Convert(before, ConversionFixtures.Request(k, false));
    for (int z = 0; z <= 24; z++)
        for (int x = 0; x <= 24; x++)
            Assert.Equal(ConversionFixtures.ExactHeight(before, new(x, 8), new(z, 8)), ConversionFixtures.ExactHeight(r.Candidate, new(x, 8), new(z, 8)));
    Assert.Equal(ConversionFixtures.OverlayArea(before), ConversionFixtures.OverlayArea(r.Candidate));
    MapSurfacePatch fine = ConversionFixtures.Fine(r);
    Assert.Equal(MapSurfaceSemantics.PatchDigest(fine), MapSurfaceSemantics.PatchDigest(MapSurfacePatchCodec.Decode(MapSurfacePatchCodec.Encode(fine), fine.Key)));
}
public void Conversion_NonCoplanarCornerCutIsUnsupportedEncodingUnlessAccepted()
{
    MapSurfaceSet input = ConversionFixtures.Slopes(MapOverlayCut.CornerQuarter);
    string message = Assert.Throws<MapDocumentException>(() => Convert(input, ConversionFixtures.Request(2, false))).Message;
    Assert.Contains("authored difference", message);
    Assert.Contains("cell (1, 1)", message);
    MapCellConversion c = MapConversionClassifier.Classify(ConversionFixtures.Coarse(input).Surface, ConversionFixtures.Coarse(input).Patch, 1, 1, 2);
    Assert.Equal(MapCellConversionClass.UnsupportedEncoding, c.Class);
    Assert.Contains("crease", c.Reason);
    MapConversionResult accepted = Convert(input, ConversionFixtures.Request(2, true));
    Assert.Equal((4, 8), (accepted.Differences.Count(d => d.Kind == MapDifferenceKind.Geometry && d.OldFace is not null),
                          accepted.Differences.Count(d => d.Kind == MapDifferenceKind.Geometry && d.NewFace is not null)));
}
[Theory, InlineData(2, MapCellConversionClass.ExactCoplanar), InlineData(3, MapCellConversionClass.UnsupportedEncoding)]
public void Conversion_CoplanarCornerCutPaintNeedsAnEvenSubdivision(int k, MapCellConversionClass expected)
{
    var coarse = ConversionFixtures.Coarse(ConversionFixtures.Slopes(MapOverlayCut.CornerQuarter, coplanarCentre: true));
    MapCellConversion c = MapConversionClassifier.Classify(coarse.Surface, coarse.Patch, 1, 1, k);
    Assert.Equal(expected, c.Class);
    if (k % 2 == 1) Assert.Contains("paint", c.Reason);
}
public void Conversion_LegacyFallbackIsNotRepresentableAndArithmeticIsAnExplicitDifference()
{
    MapSurfaceSet input = ConversionFixtures.LegacyRowWithNoDraw();
    Assert.Contains("authored difference", Assert.Throws<MapDocumentException>(() => Convert(input, ConversionFixtures.LegacyRequest(2, false))).Message);
    var legacy = ConversionFixtures.Legacy(input);
    MapCellConversion c = MapConversionClassifier.Classify(legacy.Surface, legacy.Patch, 1, 0, 2);
    Assert.Equal(MapCellConversionClass.NotRepresentable, c.Class);
    Assert.Contains("legacy fallback", c.Reason);
    MapConversionResult r = Convert(input, ConversionFixtures.LegacyRequest(2, true));
    Assert.Contains(r.Differences, d => d.Kind == MapDifferenceKind.FallbackRemoved && d.CellX == 1);
    Assert.Contains(r.Differences, d => d.Kind == MapDifferenceKind.FlagRemoved && d.CellX == 1);
    Assert.Contains(r.Differences, d => d.Kind == MapDifferenceKind.ArithmeticPolicy && d.CellX == 0 && d.Detail.Contains("max delta"));
    MapSpaceFootprint world = r.Candidate.AllRecords().OfType<MapSpaceFootprint>().Single(f => f.Id == "world-cells");
    Assert.Equal((MapBoundKind.SupportFloor, "plane-0-fine-1"), (world.Lower.Kind, world.Lower.SurfaceId));   // fallback became physical faces, so the tag is gone
    Assert.Empty(MapTopologyReferenceValidator.Validate(r.Candidate.Refs, r.Candidate.Patches.Values));
}
public void Conversion_RetargetsOnlyTheConvertedFloorAndKeepsTheCoarseCeiling()
{
    MapSurfaceSet input = ConversionFixtures.WithCaveCeiling();
    string roof = MapSurfaceSemantics.PatchDigest(input.Patches[new("roof", 0, 0)]);
    MapConversionResult r = Convert(input, ConversionFixtures.Request(3, false));
    MapSpaceFootprint room = r.Candidate.AllRecords().OfType<MapSpaceFootprint>().Single(f => f.Id == "room-cells");
    Assert.Equal(("coarse-fine-1", "roof"), (room.Lower.SurfaceId, room.Upper.SurfaceId));
    Assert.Equal(roof, MapSurfaceSemantics.PatchDigest(r.Candidate.Patches[new("roof", 0, 0)]));
    Assert.Empty(MapTopologyReferenceValidator.Validate(r.Candidate.Refs, r.Candidate.Patches.Values));
}
public void Conversion_OnAMixedFootprintRetargetsOnlyTheConvertedBound()
{
    MapSurfaceSet input = ConversionFixtures.MixedPorch();
    MapConversionResult r = Convert(input, ConversionFixtures.PorchRoofRequest(width: 3));
    MapSpaceFootprint porch = r.Candidate.AllRecords().OfType<MapSpaceFootprint>().Single(f => f.Id == "porch-cells");
    Assert.Equal(("half-floor", "third-roof-fine-1"), (porch.Lower.SurfaceId, porch.Upper.SurfaceId));
    Assert.Equal(Digests(input)[new("half-floor", 0, 0)], MapSurfaceSemantics.PatchDigest(r.Candidate.Patches[new("half-floor", 0, 0)]));
    Assert.Empty(MapTopologyReferenceValidator.Validate(r.Candidate.Refs, r.Candidate.Patches.Values));
}
public void Conversion_RefusesARegionThatSplitsAFootprintCellBeforeMutating()
{
    MapSurfaceSet input = ConversionFixtures.MixedPorch();
    var digests = Digests(input);
    string message = Assert.Throws<MapDocumentException>(() => Convert(input, ConversionFixtures.PorchRoofRequest(width: 2))).Message;  // x = 2/3 m crosses [1/2, 1)
    Assert.Contains("footprint straddle: footprint 'porch-cells' cell 1 crosses the conversion region boundary.", message);   // cells 1 and 65 straddle, 1 is first
    Assert.Contains("Align the region to whole footprint cells or split the footprint first.", message);
    Assert.Equal(digests, Digests(input));
}
public void Conversion_RefusesARecordOnAConvertedEdgeBeforeMutating()
{
    MapSurfaceSet input = ConversionFixtures.WithWallOnTheCentreEdge();
    var digests = Digests(input);
    Assert.Contains("west-foot", Assert.Throws<MapDocumentException>(() => Convert(input, ConversionFixtures.Request(2, false))).Message);
    Assert.Equal(digests, Digests(input));
}
```

The k = 2 and k = 3 corner values follow from the centre planes `160 + 90x + 50z` (lower NW-SE triangle) and `130 + 120x + 80z` (upper), in coarse cell units, times `k`.

- [ ] **Step 2: Run red.** `wa_test t11-red "$MAPDOC" "FullyQualifiedName~FinePatchConversionTests"`. Expected: build FAIL naming `MapFinePatchConversion`.
- [ ] **Step 3: Implement the write set, the classifier and the conversion.**
- [ ] **Step 4: Run green.** `wa_test t11-green "$MAPDOC" "FullyQualifiedName~FinePatchConversionTests|FullyQualifiedName~BoundaryGeometryTests"`. Expected: PASS, 17 conversion cases plus the 12 boundary cases.
- [ ] **Step 5: Commit** `KhaozEngine.MapDoc/Editing KhaozEngine.MapDoc.Tests/Editing`, message `feat(mapdoc): convert terrain to exact finer patches or report authored differences`.

---

### Task 12A: Exact common refinement of mixed-resolution bounds (D7)

**Files:**
- Create: `KhaozEngine.MapDoc/Spaces/MapRefinementTypes.cs`, `KhaozEngine.MapDoc/Spaces/MapCommonRefinement.cs`, `KhaozEngine.MapDoc/Spaces/MapBoundFaceContext.cs`, `KhaozEngine.MapDoc/Spaces/MapLowerCellClassifier.cs`, `KhaozEngine.MapDoc/Surfaces/MapLegacyBilinear.cs`
- Test: `KhaozEngine.MapDoc.Tests/Spaces/CommonRefinementTests.cs`, `LowerCellClassifierTests.cs`, fixtures `RefinementFixtures.cs`, `MixedResolutionFixtures.cs`, `LegacyExteriorFixtures.cs`, `BoundFaceContextFixtures.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/RefinementWorkTests.cs` (internal work counters), `KhaozEngine.MapDoc.Compatibility.Tests/LegacyBilinearOracleTests.cs` (released arithmetic)

**Interfaces (new, `KhaozEngine.MapDoc.Spaces`):**
- Consumes: `MapLatticeRanges.CellRect/CellRange` (Task 8), `MapScopedSurfaces.TryAcquiredPatch/TryRecord/Surfaces/PatchClones` (Task 8), `MapSurfaceCompiler.CountFaces`, the masked `Compile`, `MapSlotCellMask` and `MapCompiledPatch.ExactTriangle` (Task 9), `MapOpeningBoundary.Compile` (Task 10), `MapSpaceFootprint`, `MapBoundRef` and `MapLegacyExteriorRecipe` (Task 5), `MapFaceKey` ordering and `LegacyFallbackCells` (Task 9), `LegacyOracleWorld` (Task 3) and `LegacyOracleConverter.ToNative` (Task 9) for the compatibility test.
- `public enum MapRefinementStatus { Complete, CapacityExceeded, NotRepresentable, Invalid, MissingGeometry }`.
- `public sealed record MapRefinementLimits(int MaxSourceFacesPerBound = 16_384, int MaxPairChecks = 65_536, int MaxRefinementFaces = 32_768, int MaxRefinementVertices = 131_072, int MaxContextPatches = 256, int MaxContextFaces = 262_144)`. The last two are the D7 validation-context budgets.
- `public readonly record struct MapBoundFace(MapFaceKey Key, MapExactTriangle Triangle)`.
- `public sealed record MapBoundFaceSet(MapRefinementStatus Status, IReadOnlyList<MapBoundFace> Faces, IReadOnlyList<MapLegacyCellTag> Compatibility, string? Detail)`.
- `public sealed record MapRefinementFace(MapFaceKey Lower, MapFaceKey? Upper, IReadOnlyList<MapExactXz> Polygon, MapExactValue? MinSeparation)` (`Upper` and `MinSeparation` are null under an open top).
- `public sealed record MapCellRefinement(MapRefinementStatus Status, IReadOnlyList<MapExactXz> Vertices, IReadOnlyList<MapRefinementFace> Faces, MapExactValue CellArea, MapExactValue LowerArea, MapExactValue CompatibilityArea, IReadOnlyList<MapLegacyCellTag> CompatibilityCells, MapExactValue? UpperArea, MapExactValue? MinSeparation, int PairChecks, string? Detail)`. `PairChecks` is the number of attempted lower and upper pairs (D7 step 4). Every status other than `Complete` has empty `Vertices`, `Faces` and `CompatibilityCells`, zero `CompatibilityArea` and zero `PairChecks`.
- `internal sealed class MapRefinementWork` (test observation only, never a result) with `long ChargedPairs`, `long BoundingBoxComparisons`, `long PositiveBoundingBoxPairs`, `long PositiveIntersections`, filled by an internal `Refine` overload. `ChargedPairs` is the bulk demand of D7 step 4 even when it is refused, and the other three count work actually performed.
- `public static class MapCommonRefinement` with:
  - `MapCellRefinement Refine(IReadOnlyList<MapBoundFace> lower, IReadOnlyList<MapBoundFace>? upper, MapExactXz min, MapExactXz max, MapRefinementLimits limits)`, exactly the D7 algorithm (null `upper` is an open top), and `internal MapCellRefinement Refine(..., MapRefinementLimits limits, MapRefinementWork work)`. `Refine` never produces compatibility coverage (`CompatibilityArea` zero, `CompatibilityCells` empty).
  - `MapBoundFaceSet BoundFaces(MapScopedSurfaces view, MapSpaceFootprint footprint, MapBoundRef bound, MapExactXz min, MapExactXz max, MapRefinementLimits limits)`, where `bound` is `footprint.Lower` or `footprint.Upper` (otherwise `ArgumentException`): for `SupportFloor`, `Ceiling` and `LegacyExteriorV1`, `CellRange` on the bound surface's frame, refusing with `CapacityExceeded` when the checked cell count exceeds `MaxSourceFacesPerBound` before enumerating. Counting precedes compiling (D7 compiled reuse): the faces of the contributing cells are counted from bytes with `MapSurfaceCompiler.CountFaces`, and a checked sum above `MaxSourceFacesPerBound` is `CapacityExceeded` (detail `source faces`) before any compile. Only then are faces read from the context. Each lower cell goes through `MapLowerCellClassifier.Classify` (D9): `Physical` contributes every compiled face (fan children included), `LegacyNonCapture` contributes its tag to `Compatibility` and no face, `KnownHole` and `UntaggedLegacy` contribute nothing, `Unavailable` is `MissingGeometry` naming it and `InvalidRecipe` is `Invalid` with the `legacy recipe` detail. Upper `SupportFloor` or `Ceiling` cells contribute every compiled face of present cells, a covering patch read other than `Present` or `KnownEmpty` is `MissingGeometry` naming it, and an upper `LegacyExteriorV1` is `Invalid` (`legacy recipe: upper`). The public overload prepares one context for its own call, and each covering patch compiles once in it. For `HorizontalOpening`, the opening plane's exact triangles of the cells in range, keyed by `MapOpeningPlane.Keys`, and an opening record that is not acquired, missing or of another type is `MissingGeometry` naming it. `OpenTop` is not a valid argument (`ArgumentException`).
  - `MapCellRefinement RefineFootprintCell(MapScopedSurfaces view, MapSpaceFootprint footprint, int slotCell, MapRefinementLimits limits)`: `CellRect` on the footprint's lattice frame, `BoundFaces` for the lower bound and, unless `OpenTop`, the upper bound, the first non-`Complete` bound status returned as is, otherwise `Refine` over the faces, then `CompatibilityArea` as the exact area of each compatibility cell's rectangle intersected with the footprint cell (under the D9 lattice rule, the whole cell) and `CompatibilityCells` as the tags in slot-cell order. Any `MapExactOverflowException` is `NotRepresentable` with detail containing `overflow`. The public overload prepares one context for its own call.
  - `MapExactValue Area(MapRefinementFace face)` (exact polygon area).
  - Internal context overloads (D7 compiled reuse): `internal static MapFootprintPreparation PrepareFootprints(MapScopedSurfaces view, IReadOnlyList<MapSpaceFootprint> footprints, MapRefinementLimits limits, MapBoundFaceWork? work)` runs the three D7 phases over every slot cell of those footprints and returns `internal sealed record MapFootprintPreparation(MapBoundFaceContext? Context, string? Refusal, IReadOnlyDictionary<(string FootprintId, int SlotCell), MapCellRefinement> CellOutcomes)`, where `CellOutcomes` holds the phase-1 per-cell refusals and `Refusal` is `context patches` or `context faces` with a null `Context`. `internal static MapBoundFaceSet BoundFaces(MapBoundFaceContext context, MapSpaceFootprint footprint, MapBoundRef bound, MapExactXz min, MapExactXz max, MapRefinementLimits limits)` and `internal static MapCellRefinement RefineFootprintCell(MapBoundFaceContext context, MapSpaceFootprint footprint, int slotCell, MapRefinementLimits limits)` read faces only from the context and never compile. `internal static MapCellRefinement RefineFootprintCell(MapScopedSurfaces view, MapSpaceFootprint footprint, int slotCell, MapRefinementLimits limits, MapBoundFaceWork work)` is the public path with counters.
- `internal readonly record struct MapCellDemand(MapPatchKey Patch, int SlotCell)`. `internal sealed class MapBoundFaceContext` in `MapBoundFaceContext.cs` with `internal static MapBoundFaceContext? Prepare(MapScopedSurfaces view, IEnumerable<MapCellDemand> demands, int maxPatches, long maxFaces, MapBoundFaceWork? work, out string? refusal)` (phases 2 and 3 of D7, null with `context patches` or `context faces` and zero compiles on overflow). It merges each demand into its patch mask as the lazily produced sequence yields it, holds no demand list, and after the first overflow drains the sequence without storing more, so `PrepareFootprints` still finishes phase 1 for every cell, `MapScopedSurfaces View`, `IReadOnlyList<MapBoundFace> Faces(MapCellDemand cell)` (that demanded cell's compiled faces with exact triangles, in `MapFaceKey` order, `InvalidOperationException` containing `context demand` for an undemanded cell) and `MapCompiledPatch Compiled(MapPatchKey key)`. Its dictionary is built once in `Prepare` and never changes. Membership (12B) and support (13) use `Prepare` directly with their query budgets.
- `internal sealed class MapBoundFaceWork` (test observation only, never a result) with `int ContextsPrepared`, `long BoundFacesCounted` (phase-1 per-bound counts from bytes), `long ContextFacesCounted` (phase-2 union count), `int Compiles` and `long CompiledFaces`.
- `public enum MapLowerCellClass : byte { Physical, LegacyNonCapture, KnownHole, UntaggedLegacy, Unavailable, InvalidRecipe }`, `public readonly record struct MapLegacyCellTag(MapPatchKey Patch, int SlotCell, string Policy)` (never a `MapFaceKey`), `public sealed record MapLowerCellClassification(MapLowerCellClass Class, MapPatchKey Patch, int SlotCell, MapLegacyCellTag? Legacy, string? Detail)` (`Legacy` set only for `LegacyNonCapture`, `Detail` names the patch and status or the `legacy recipe` rule).
- `public static class MapLowerCellClassifier { public static MapLowerCellClassification Classify(MapScopedSurfaces view, MapSpaceFootprint footprint, long cellX, long cellZ); }`, the D9 table in its order, for the absolute cell on the lower surface's lattice. A footprint whose lower kind is not `SupportFloor` or `LegacyExteriorV1` throws `ArgumentException`. It reads cell bytes and presence through `TryAcquiredPatch`, never clones or compiles, and uses `MapLegacyExteriorRecipe.Check` for orders 2 and 5.
- `public static class MapLegacyBilinear` in `KhaozEngine.MapDoc.Surfaces` with `float Evaluate(int h00Cm, int h10Cm, int h01Cm, int h11Cm, float fx, float fz)` and `float HeightMetres(MapSurfaceRef surface, MapSurfacePatch patch, MapExactXz world)`, exactly the D9 arithmetic. The body of `Evaluate` is the released expression:

```csharp
float h00 = h00Cm * 0.01f, h10 = h10Cm * 0.01f, h01 = h01Cm * 0.01f, h11 = h11Cm * 0.01f;
float south = h00 + (h10 - h00) * fx;
float north = h01 + (h11 - h01) * fx;
return south + (north - south) * fz;
```

**Fixtures.** `RefinementFixtures` (synthetic `MapBoundFace` lists, flat triangles at a given height in centimetres, keys `(owner, null, 0, index, 0, Front)` in listed order): `X(long xn, long xd, long zn, long zd) -> MapExactXz`, `Origin00 = X(0, 1, 0, 1)`, `One11 = X(1, 1, 1, 1)`, `UnitSquareSwNe(owner, cm)` (triangles `(0,0) (1,0) (1,1)` and `(0,0) (1,1) (0,1)`), `UnitSquareNwSe(owner, cm)` (`(0,0) (1,0) (0,1)` and `(1,0) (1,1) (0,1)`), `FanFromSouthMidpoint(owner, cm)` (`(0,0) (1/2,0) (0,1)`, `(1/2,0) (1,1) (0,1)`, `(1/2,0) (1,0) (1,1)`), `CollinearTriangle()` (`(0,0) (1,0) (2,0)`), `LowerUnitTriangle()` (`(0,0) (1,0) (0,1)` at 0), `PrimeSliverUpper(long p)` (`(1/p,0) (1,1) (1/p,1)` at 100), `UnitSquareGrid2(owner, cm)` (the same construction below with 2 by 2 cells, 8 triangles), `UnitSquareGrid16(owner, cm)` (the unit square split into 16 by 16 cells of side 1/16, each split SW-NE into `(i/16, j/16) ((i+1)/16, j/16) ((i+1)/16, (j+1)/16)` and `(i/16, j/16) ((i+1)/16, (j+1)/16) (i/16, (j+1)/16)`, cells in row-major order with j outer, 512 triangles), `LedgeBoundFaces(MapScopedSurfaces s) -> (IReadOnlyList<MapBoundFace> Lower, IReadOnlyList<MapBoundFace> Upper, MapExactXz Min, MapExactXz Max)` (via `BoundFaces` with the `ledge-cells` footprint and its two bounds for cell 31) and `TouchingFace()` (a `half` slot (1, 0) face `(32,0) (65/2,0) (32,1/2)` that meets `ledge-cells` only along x = 32).

`MixedResolutionFixtures` (all `Native`, `PositiveZ`, views are `MapScopedSurfaces.CompleteView`, every record anchored in the floor patch). `Document(MapSurfaceSet set)` wraps a set in a resolver-2 document (`(1, 2)`, `AuthoredBindingsV2`, the Task 2 asset roots) with no added records, and Task 17 reuses it:

| Fixture | Floor | Ceiling | Space |
| --- | --- | --- | --- |
| `HalfUnderThird(bool far)` returning `(View, Footprint)` | `half` `(1/2, 1/200)` slot `(s, s)`, `s = far ? 1000 : 0`, 2 by 2 cells at 0 | `third` `(1/3, 1/300)` slot `(t, t)`, `t = far ? 1500 : 0`, 3 by 3 cells at 300 (1 m) | Exterior `box`, footprint `box-cells` on lattice `third` `(t, t)`, slot cells `[0, 1, 2, 64, 65, 66, 128, 129, 130]`. Both slots start exactly at 32,000 m when `far` |
| `SixtyFourthUnderMetre()` returning `(View, Footprint)` | `fine64` `(1/64, 1/6400)` slot (0, 0), 64 by 64 cells at 0 | `metre` `(1/1, 1/100)` slot (0, 0), one cell at 300 | Exterior `slab64`, footprint `slab64-cells` on lattice `metre` (0, 0), cell 0 |
| `RidgeUnderValley(int ridge, int valley)` returning `View` | `half` as above near the origin, corners with x index 1 (world x 1/2) at `ridge` half-units, others 0 | `third` as above, corner row z index 1 (world z 1/3) at `valley` third-units, others 2700 (9 m) | Exterior `box` as above |
| `ClosedRidgeCave(bool subdivided)` returning `View` | `RidgeUnderValley(1000, 1800)` floor, plus edge subdivisions 3 on every `half` boundary edge when `subdivided` | Its ceiling, plus edge subdivisions 2 on every `third` boundary edge when `subdivided` | `box` is a Cave closed by four strips on the 1 m square, lower chains `SurfaceEdge` on `half`, upper chains `SurfaceEdge` on `third`, each chain listing exactly the vertices its surface has on that edge (1/6 m spacing on both when subdivided, otherwise 1/2 m against 1/3 m) |
| `FinePeakUnderCoarseRoof(int peakFineUnits)` returning `View` | `peak` `(1/3, 1/300)`, 3 by 3 fine cells, every corner 0 except interior corner (1, 1) at `peakFineUnits` | `lid` `(1/1, 1/100)`, one cell at 300, its four boundary edges subdivided 3 | Cave `box` on lattice `lid` (0, 0) cell 0, closed by four strips whose lower (`peak`) and upper (`lid`) `SurfaceEdge` chains list the same 1/3 m vertices |
| `Converted()` returning `MapDocument` | `ConversionFixtures.WithCaveCeiling()` converted with `Request(3, false)` | Its `roof` (one cell, edges subdivided 3) | `room` closed by four strips whose lower chains run on the `coarse-fine-1` boundary and upper chains on the `roof` edges, both at 1/3 m spacing, all wrapped by `Document(set)` |

Exact expectations for `HalfUnderThird` cell 1 (world `[1/3, 2/3] x [0, 1/3]`): `half` cells 0 and 1 overlap it. Each flat `half` cell splits SW-NE (diagonals `z = x` and `z = x - 1/2`), the `third` cell splits SW-NE (`z = x - 1/3`). The `half` cell-0 triangle above its diagonal only touches the rectangle at (1/3, 1/3) and is discarded. Refinement vertices are (1/3, 0), (1/2, 0), (2/3, 0), (1/2, 1/6), (2/3, 1/6), (1/3, 1/3), (1/2, 1/3), (2/3, 1/3). Faces have areas 1/72, 1/24, 1/72, 1/36 and 1/72, summing to 1/9. Pair checks are recomputed for the attempted-pair metric, not relabelled from v3's candidate count: clipping leaves three lower polygons (the discarded touching triangle is never paired) and two upper polygons, so 6 pairs are attempted. All 6 pass the bounding-box test, and one (the `half` cell-1 triangle below its diagonal against the `third` triangle above its diagonal) has a zero-area intersection, leaving 5 faces.

| Fixture | Clipped lower by upper polygons | Attempted pairs (`PairChecks`) | Positive bounding-box pairs | Positive intersections (faces) |
| --- | --- | ---: | ---: | ---: |
| `HalfUnderThird` cell 1 | 3 by 2 | 6 | 6 | 5 |
| `UnitSquareNwSe` under `UnitSquareSwNe` | 2 by 2 | 4 | 4 | 4 |
| `UnitSquareSwNe` under itself | 2 by 2 | 4 | 4 | 2 |
| `UnitSquareSwNe` under `FanFromSouthMidpoint` | 2 by 3 | 6 | 6 | 5 |
| `SixtyFourthUnderMetre` cell 0 | 8,192 by 2 | 16,384 | 16,384 | 8,192 |
| `UnitSquareGrid2` under itself | 8 by 8 | 64 | 16 | 8 |
| `UnitSquareGrid16` under itself, mathematical demand only, never executed to completion | 512 by 512 | 262,144 | 1,024 | 512 |
| Any open top | n by none | 0 | 0 | n |

In the 16 by 16 grids a triangle's bounding box is its own 1/16 m cell, so it overlaps with positive area only the two upper triangles of the same cell. The identical one intersects with positive area and the other meets it only along the shared diagonal. The tee areas in (Lower, Upper) order are 1/12, 1/6, 1/4, 1/6 and 1/3, summing to 1. For `RidgeUnderValley` the floor is at most `ridge/200` m with equality only on x = 1/2, the ceiling at least `valley/300` m with equality only on z = 1/3, so the minimum separation is attained only at (1/2, 1/3), a vertex of neither lattice.

**Legacy exterior fixture (D9).** `LegacyExteriorFixtures.Row(string variant, long slotX = 0) -> MapDocument`, wrapped by `MixedResolutionFixtures.Document`: surface `plane-0` (`ImportedMetreCentimetre`, `LegacyTileWorld`, `SupportFloor`) at slot `(slotX, 0)`, cells x `64 * slotX` to `64 * slotX + 3`, z 0, corner rows z 0 `[0, 100, 300, 300, 300]` and z 1 `[0, 200, 600, 600, 600]`. Cell 0 is `Full`, cell 1 is `Full` with `NoDraw`, cell 2 is underlay 0 (void), cells 0 to 2 have presence 1 and cell 3 has presence 0. Exterior space `world` has footprint `world-cells` on lattice `plane-0` `(slotX, 0)`, slot cells `[0, 1, 2]`, lower `(LegacyExteriorV1, "plane-0", null)`, upper `OpenTop`, all anchored in that patch. Helpers: `View(variant)` is `MapScopedSurfaces.CompleteView(Row(variant).Surfaces)`, `Footprint(view)` its single footprint, `Compiled(doc)` the compiled `plane-0` patch. On the `NegativeZ` frame, world (x + 0.5, -0.5) is the centre of cell x, and the bilinear heights at the centres of cells 1 and 2 are 3.0 m and 4.5 m.

| Variant | Change from `tagged` | Expected |
| --- | --- | --- |
| `tagged` | none | valid, cells 1 and 2 are `LegacyNonCapture` |
| `untagged` | lower `(SupportFloor, "plane-0", null)` | `missing bound` at cells 1 and 2 |
| `hole` | slot cells `[0, 1, 2, 3]` | `missing bound` at cell 3, never fallback |
| `cave` | `world` is `Cave` with upper `(Ceiling, "roof", null)`, `roof` a `Native` `Ceiling` on `(1/1, 1/100, NegativeZ)` at the same slot, cells x 0 to 2, z 0, at 900 | `legacy recipe: space` |
| `upper-tag` | upper `(LegacyExteriorV1, "plane-0", null)` | `legacy recipe: upper` |
| `foreign-lattice` | footprint lattice `shadow` at the same slot, `shadow` a `Native` `SupportFloor` on `(1/1, 1/100, NegativeZ)`, cells x 0 to 2, z 0, at -1000, bounding no space | `legacy recipe: lattice` |

**Bound-face context fixtures (D7 compiled reuse).** `BoundFaceContextFixtures` returns `(MapScopedSurfaces View, MapSpaceFootprint Footprint)` from `MapScopedSurfaces.CompleteView` of a `MixedResolutionFixtures.Document`, every surface `Native` and `PositiveZ`, every record anchored in the floor patch. `EightByEight()`: `floor8` `SupportFloor` `(1/1, 1/100)` slot (0, 0), cells x and z 0 to 7, every corner 0, and `ceil8` `Ceiling` `(1/2, 1/200)` slot (0, 0), cells x and z 0 to 15, every corner 600 (3 m), with Exterior `room8` and footprint `room8-cells` on lattice `floor8` (0, 0), the 64 slot cells `z*64 + x` for x and z 0 to 7, lower `floor8`, upper `ceil8`. It demands 64 by 2 = 128 floor faces and 256 by 2 = 512 ceiling faces from two patches. The context-budget refusal reuses `EightByEight()` under `MaxContextFaces: 639`, one below its demand: every cell passes its per-bound budget (2 lower and 8 upper faces), and the running union total reaches 640 at the last demanded cell, so preparation refuses with zero compiles. No fixture is sized to the default budgets, whose values 256 and 262,144 are asserted separately as values.

- [ ] **Step 1: Write the failing tests**

```csharp
static MapExactXz X(long xn, long xd, long zn, long zd) => RefinementFixtures.X(xn, xd, zn, zd);
static MapCellRefinement Cell((MapScopedSurfaces View, MapSpaceFootprint Footprint) f, int slotCell, MapRefinementLimits? limits = null)
    => MapCommonRefinement.RefineFootprintCell(f.View, f.Footprint, slotCell, limits ?? new MapRefinementLimits());

public void HalfFloorUnderThirdCeiling_RefinesOneFootprintCellExactly()
{
    MapCellRefinement r = Cell(MixedResolutionFixtures.HalfUnderThird(far: false), 1);
    Assert.Equal(MapRefinementStatus.Complete, r.Status);
    Assert.Equal(new[] { X(1,3,0,1), X(1,2,0,1), X(2,3,0,1), X(1,2,1,6), X(2,3,1,6), X(1,3,1,3), X(1,2,1,3), X(2,3,1,3) }, r.Vertices);
    Assert.Equal((5, 6), (r.Faces.Count, r.PairChecks));
    Assert.Equal(new[] { 0, 0, 1, 1, 1 }, r.Faces.Select(f => f.Lower.Primitive));
    Assert.All(r.Faces, f => Assert.Equal(1, f.Upper!.Value.Primitive));
    Assert.Equal(new MapExactValue[] { new(1, 72), new(1, 72), new(1, 72), new(1, 36), new(1, 24) }, r.Faces.Select(MapCommonRefinement.Area).Order());
    Assert.Equal((new MapExactValue(1, 9), new MapExactValue(1, 9), (MapExactValue?)new MapExactValue(1, 9)), (r.CellArea, r.LowerArea, r.UpperArea));
    Assert.Equal(new MapExactValue(1, 1), r.MinSeparation);
}
public void HalfUnderThird_FarCopyRefinesToTheSameExactOffsets()
{
    MapCellRefinement near = Cell(MixedResolutionFixtures.HalfUnderThird(far: false), 1), far = Cell(MixedResolutionFixtures.HalfUnderThird(far: true), 1);
    var shift = new MapExactValue(32000, 1);
    Assert.Equal(near.Vertices, far.Vertices.Select(v => new MapExactXz(v.X.Subtract(shift), v.Z.Subtract(shift))));
    Assert.Equal((near.Faces.Count, near.PairChecks, near.MinSeparation), (far.Faces.Count, far.PairChecks, far.MinSeparation));
}
public void CommonRefinement_IncludesEdgeCrossings()
{
    MapCellRefinement r = MapCommonRefinement.Refine(RefinementFixtures.UnitSquareNwSe("lo", 0), RefinementFixtures.UnitSquareSwNe("up", 300),
        RefinementFixtures.Origin00, RefinementFixtures.One11, new());
    Assert.Equal(new[] { X(0,1,0,1), X(1,1,0,1), X(1,2,1,2), X(0,1,1,1), X(1,1,1,1) }, r.Vertices);
    Assert.Equal((4, 4), (r.Faces.Count, r.PairChecks));
}
public void Refine_CoincidentDiagonalsAndTJunctionsAddNoSliverFaces()
{
    MapCellRefinement same = MapCommonRefinement.Refine(RefinementFixtures.UnitSquareSwNe("lo", 0), RefinementFixtures.UnitSquareSwNe("up", 300),
        RefinementFixtures.Origin00, RefinementFixtures.One11, new());
    Assert.Equal((2, 4, 4), (same.Faces.Count, same.Vertices.Count, same.PairChecks));
    MapCellRefinement tee = MapCommonRefinement.Refine(RefinementFixtures.UnitSquareSwNe("lo", 0), RefinementFixtures.FanFromSouthMidpoint("up", 300),
        RefinementFixtures.Origin00, RefinementFixtures.One11, new());
    Assert.Equal(new[] { X(0,1,0,1), X(1,2,0,1), X(1,1,0,1), X(1,3,1,3), X(0,1,1,1), X(1,1,1,1) }, tee.Vertices);
    Assert.Equal((5, 6), (tee.Faces.Count, tee.PairChecks));
    Assert.Equal(new MapExactValue[] { new(1, 12), new(1, 6), new(1, 4), new(1, 6), new(1, 3) }, tee.Faces.Select(MapCommonRefinement.Area));
    Assert.Equal(new MapExactValue(1, 1), tee.Faces.Aggregate(new MapExactValue(0, 1), (a, f) => a.Add(MapCommonRefinement.Area(f))));
}
public void Refine_OpenTopKeepsLowerPolygonsWithoutSeparation()
{
    MapCellRefinement r = MapCommonRefinement.Refine(RefinementFixtures.UnitSquareSwNe("lo", 0), null,
        X(1, 4, 0, 1), X(3, 4, 1, 1), new());
    Assert.Equal((MapRefinementStatus.Complete, 2, 0), (r.Status, r.Faces.Count, r.PairChecks));
    Assert.All(r.Faces, f => Assert.Equal(((MapFaceKey?)null, (MapExactValue?)null), (f.Upper, f.MinSeparation)));
    Assert.Equal((new MapExactValue(1, 2), (MapExactValue?)null, (MapExactValue?)null), (r.LowerArea, r.UpperArea, r.MinSeparation));
}
public void Refine_ClassifiesInvalidCapacityAndRepresentabilityOutcomesWithoutPartialResults()
{
    static void Empty(MapCellRefinement r, MapRefinementStatus status, string? detail)
    {
        Assert.Equal((status, 0, 0), (r.Status, r.Faces.Count, r.Vertices.Count));
        if (detail is not null) Assert.Contains(detail, r.Detail);
    }
    Empty(MapCommonRefinement.Refine(RefinementFixtures.CollinearTriangle(), RefinementFixtures.UnitSquareSwNe("up", 300), RefinementFixtures.Origin00, RefinementFixtures.One11, new()),
        MapRefinementStatus.Invalid, "degenerate");
    Empty(MapCommonRefinement.Refine(RefinementFixtures.UnitSquareSwNe("lo", 0), null, RefinementFixtures.Origin00, X(0, 1, 1, 1), new()),
        MapRefinementStatus.Invalid, "cell");
    Empty(MapCommonRefinement.Refine(RefinementFixtures.LowerUnitTriangle(), RefinementFixtures.PrimeSliverUpper(long.MaxValue), RefinementFixtures.Origin00, RefinementFixtures.One11, new()),
        MapRefinementStatus.NotRepresentable, "overflow");                  // the crossing x = p/(2p-1) needs a denominator above long.MaxValue
    var near = MixedResolutionFixtures.HalfUnderThird(far: false);
    foreach (MapRefinementLimits tight in new MapRefinementLimits[] { new(MaxSourceFacesPerBound: 3), new(MaxPairChecks: 5), new(MaxRefinementFaces: 4), new(MaxRefinementVertices: 7) })
        Empty(Cell(near, 1, tight), MapRefinementStatus.CapacityExceeded, null);
}
public void SixtyFourthFloorUnderOneMetreRoof_MeetsAndRefusesItsDeclaredBudgets()
{
    var f = MixedResolutionFixtures.SixtyFourthUnderMetre();
    MapCellRefinement ok = Cell(f, 0);
    Assert.Equal((MapRefinementStatus.Complete, 8192, 16384, 4225), (ok.Status, ok.Faces.Count, ok.PairChecks, ok.Vertices.Count));
    Assert.Equal(new MapExactValue(3, 1), ok.MinSeparation);
    foreach (MapRefinementLimits tight in new MapRefinementLimits[] { new(MaxSourceFacesPerBound: 8191), new(MaxPairChecks: 16383), new(MaxRefinementFaces: 8191), new(MaxRefinementVertices: 4224) })
        Assert.Equal(MapRefinementStatus.CapacityExceeded, Cell(f, 0, tight).Status);
}
public void BoundaryTouch_DropsTouchingFacesAndUsesOnlyThePositiveAreaSlot()
{
    MapScopedSurfaces s = ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(AcquisitionBoundFixtures.BoundaryTouch()), AcquisitionBoundFixtures.TouchScope);
    MapSpaceFootprint ledge = s.RecordsIn(new("third", 1, 0)).OfType<MapSpaceFootprint>().Single();
    MapCellRefinement r = MapCommonRefinement.RefineFootprintCell(s, ledge, 31, new());
    Assert.Equal(MapRefinementStatus.Complete, r.Status);
    Assert.All(r.Faces, f => Assert.Equal(new MapPatchKey("half", 0, 0), f.Lower.Patch));
    var (lower, upper, min, max) = RefinementFixtures.LedgeBoundFaces(s);
    MapCellRefinement withTouch = MapCommonRefinement.Refine(lower.Append(RefinementFixtures.TouchingFace()).ToList(), upper, min, max, new());
    Assert.Equal(r.Vertices, withTouch.Vertices);
    Assert.Equal(r.Faces.Select(f => (f.Lower, f.Upper)), withTouch.Faces.Select(f => (f.Lower, f.Upper)));
}
public void AdversarialUnits_RefineFootprintCellIsNotRepresentable()
{
    MapDocument doc = AcquisitionBoundFixtures.AdversarialUnits();
    MapScopedSurfaces view = MapScopedSurfaces.CompleteView(doc.Surfaces);
    MapSpaceFootprint strange = view.RecordsIn(new("odd", 0, 0)).OfType<MapSpaceFootprint>().Single();
    MapCellRefinement r = MapCommonRefinement.RefineFootprintCell(view, strange, 5, new());
    Assert.Equal((MapRefinementStatus.NotRepresentable, 0), (r.Status, r.Faces.Count));
    Assert.Contains("overflow", r.Detail);
}
public void Refine_ChargesEveryAttemptedPairBeforeBoundingBoxRejection()
{
    var lower = RefinementFixtures.UnitSquareGrid16("lo", 0);
    var upper = RefinementFixtures.UnitSquareGrid16("up", 100);
    foreach (MapRefinementLimits refused in new MapRefinementLimits[] { new(MaxPairChecks: 8192), new(), new(MaxPairChecks: 262_143) })   // the default 65,536 refuses too
    {
        MapCellRefinement r = MapCommonRefinement.Refine(lower, upper, RefinementFixtures.Origin00, RefinementFixtures.One11, refused);
        Assert.Equal((MapRefinementStatus.CapacityExceeded, 0, 0, 0), (r.Status, r.Faces.Count, r.Vertices.Count, r.PairChecks));
        Assert.Contains("pair checks", r.Detail);
    }
    MapCellRefinement ok = MapCommonRefinement.Refine(RefinementFixtures.UnitSquareGrid2("lo", 0), RefinementFixtures.UnitSquareGrid2("up", 100), RefinementFixtures.Origin00, RefinementFixtures.One11, new(MaxPairChecks: 64));
    Assert.Equal((MapRefinementStatus.Complete, 8, 9, 64), (ok.Status, ok.Faces.Count, ok.Vertices.Count, ok.PairChecks));
    Assert.Equal(new MapExactValue(1, 1), ok.MinSeparation);
}

// LowerCellClassifierTests (MapDoc.Tests)
static MapLowerCellClass Class(string variant, long cellX)
{
    MapScopedSurfaces v = LegacyExteriorFixtures.View(variant);
    return MapLowerCellClassifier.Classify(v, LegacyExteriorFixtures.Footprint(v), cellX, 0).Class;
}
public void Classifier_SeparatesPhysicalLegacyHoleUntaggedAndInvalidCells()
{
    Assert.Equal(new[] { MapLowerCellClass.Physical, MapLowerCellClass.LegacyNonCapture, MapLowerCellClass.LegacyNonCapture, MapLowerCellClass.KnownHole },
        new long[] { 0, 1, 2, 3 }.Select(x => Class("tagged", x)));
    Assert.Equal((MapLowerCellClass.Physical, MapLowerCellClass.UntaggedLegacy, MapLowerCellClass.UntaggedLegacy), (Class("untagged", 0), Class("untagged", 1), Class("untagged", 2)));
    foreach (string variant in new[] { "cave", "upper-tag", "foreign-lattice" })
        Assert.Equal(MapLowerCellClass.InvalidRecipe, Class(variant, 1));
    MapScopedSurfaces tagged = LegacyExteriorFixtures.View("tagged");
    Assert.Equal(new MapLegacyCellTag(new("plane-0", 0, 0), 2, "kemap/legacy-exterior/1"), MapLowerCellClassifier.Classify(tagged, LegacyExteriorFixtures.Footprint(tagged), 2, 0).Legacy);
    Assert.Equal(new[] { 1, 2 }, LegacyExteriorFixtures.Compiled(LegacyExteriorFixtures.Row("tagged")).LegacyFallbackCells.Select(c => c.SlotCell));   // compiler and classifier share one rule
}
public void LegacyExterior_RefinementCountsClassifiedCoverageWithoutFaces()
{
    MapScopedSurfaces v = LegacyExteriorFixtures.View("tagged");
    MapSpaceFootprint f = LegacyExteriorFixtures.Footprint(v);
    MapCellRefinement physical = MapCommonRefinement.RefineFootprintCell(v, f, 0, new()), legacy = MapCommonRefinement.RefineFootprintCell(v, f, 1, new());
    Assert.Equal((MapRefinementStatus.Complete, 2, new MapExactValue(1, 1), new MapExactValue(0, 1)), (physical.Status, physical.Faces.Count, physical.LowerArea, physical.CompatibilityArea));
    Assert.Equal((MapRefinementStatus.Complete, 0, 0, new MapExactValue(0, 1), new MapExactValue(1, 1)), (legacy.Status, legacy.Faces.Count, legacy.Vertices.Count, legacy.LowerArea, legacy.CompatibilityArea));
    Assert.Equal(new[] { new MapLegacyCellTag(new("plane-0", 0, 0), 1, MapLegacyExteriorRecipe.PolicyId) }, legacy.CompatibilityCells);
    MapScopedSurfaces untagged = LegacyExteriorFixtures.View("untagged");
    MapCellRefinement bare = MapCommonRefinement.RefineFootprintCell(untagged, LegacyExteriorFixtures.Footprint(untagged), 1, new());
    Assert.Equal((MapRefinementStatus.Complete, new MapExactValue(0, 1), new MapExactValue(0, 1)), (bare.Status, bare.LowerArea, bare.CompatibilityArea));   // uncovered, so 12B reports missing bound
    foreach (string variant in new[] { "cave", "upper-tag", "foreign-lattice" })
    {
        MapScopedSurfaces bad = LegacyExteriorFixtures.View(variant);
        MapCellRefinement r = MapCommonRefinement.RefineFootprintCell(bad, LegacyExteriorFixtures.Footprint(bad), 1, new());
        Assert.Equal((MapRefinementStatus.Invalid, 0), (r.Status, r.CompatibilityCells.Count));
        Assert.Contains("legacy recipe", r.Detail);
    }
}

// RefinementWorkTests (MapEditor.Tests, internal work counters)
public void PairBudget_RefusesBeforeAnyComparisonAndCountsBoundingBoxPairsSeparately()
{
    var lower = RefinementFixtures.UnitSquareGrid16("lo", 0);
    var upper = RefinementFixtures.UnitSquareGrid16("up", 100);
    var refused = new MapRefinementWork();
    MapCommonRefinement.Refine(lower, upper, RefinementFixtures.Origin00, RefinementFixtures.One11, new(MaxPairChecks: 8192), refused);
    Assert.Equal((262_144L, 0L, 0L, 0L), (refused.ChargedPairs, refused.BoundingBoxComparisons, refused.PositiveBoundingBoxPairs, refused.PositiveIntersections));
    var done = new MapRefinementWork();
    MapCommonRefinement.Refine(RefinementFixtures.UnitSquareGrid2("lo", 0), RefinementFixtures.UnitSquareGrid2("up", 100), RefinementFixtures.Origin00, RefinementFixtures.One11, new(MaxPairChecks: 64), done);
    Assert.Equal((64L, 64L, 16L, 8L), (done.ChargedPairs, done.BoundingBoxComparisons, done.PositiveBoundingBoxPairs, done.PositiveIntersections));
    var tee = new MapRefinementWork();
    MapCommonRefinement.Refine(RefinementFixtures.UnitSquareSwNe("lo", 0), RefinementFixtures.FanFromSouthMidpoint("up", 300), RefinementFixtures.Origin00, RefinementFixtures.One11, new(), tee);
    Assert.Equal((6L, 6L, 6L, 5L), (tee.ChargedPairs, tee.BoundingBoxComparisons, tee.PositiveBoundingBoxPairs, tee.PositiveIntersections));
}
public void BoundFaces_CountInRangeFacesFromBytesBeforeAnyCompile()
{
    var f = MixedResolutionFixtures.SixtyFourthUnderMetre();
    var refused = new MapBoundFaceWork();
    MapCellRefinement r = MapCommonRefinement.RefineFootprintCell(f.View, f.Footprint, 0, new MapRefinementLimits(MaxSourceFacesPerBound: 8191), refused);
    Assert.Equal((MapRefinementStatus.CapacityExceeded, 8192L, 0, 0L), (r.Status, refused.BoundFacesCounted, refused.Compiles, refused.CompiledFaces));
    Assert.Contains("source faces", r.Detail);
    var done = new MapBoundFaceWork();
    Assert.Equal(MapRefinementStatus.Complete, MapCommonRefinement.RefineFootprintCell(f.View, f.Footprint, 0, new MapRefinementLimits(), done).Status);
    Assert.Equal((1, 8194L, 8194L, 2, 8194L), (done.ContextsPrepared, done.BoundFacesCounted, done.ContextFacesCounted, done.Compiles, done.CompiledFaces));
}
public void BoundFaceContext_CompilesEachBoundPatchOnceAndOverBudgetDemandCompilesNothing()
{
    var room = BoundFaceContextFixtures.EightByEight();
    var work = new MapBoundFaceWork();
    MapFootprintPreparation prepared = MapCommonRefinement.PrepareFootprints(room.View, new[] { room.Footprint }, new MapRefinementLimits(), work);
    Assert.Empty(prepared.CellOutcomes);
    foreach (int cell in room.Footprint.SlotCells)
        Assert.Equal(MapRefinementStatus.Complete, MapCommonRefinement.RefineFootprintCell(prepared.Context!, room.Footprint, cell, new MapRefinementLimits()).Status);
    Assert.Equal((1, 640L, 2, 640L, 0), (work.ContextsPrepared, work.ContextFacesCounted, work.Compiles, work.CompiledFaces, room.View.PatchClones));   // 64 cells, two patches, one compile each
    var refused = new MapBoundFaceWork();
    MapFootprintPreparation none = MapCommonRefinement.PrepareFootprints(room.View, new[] { room.Footprint }, new MapRefinementLimits(MaxContextFaces: 639), refused);
    Assert.Equal(((MapBoundFaceContext?)null, 640L, 640L, 0, 0L), (none.Context, refused.BoundFacesCounted, refused.ContextFacesCounted, refused.Compiles, refused.CompiledFaces));   // 640 demanded, one over
    Assert.Contains("context faces", none.Refusal);
    Assert.Equal((256, 262_144), (new MapRefinementLimits().MaxContextPatches, new MapRefinementLimits().MaxContextFaces));   // proposed defaults, values only
}

// LegacyBilinearOracleTests (Compatibility.Tests, public)
public void LegacyBilinear_MatchesReleasedHeightAtBitForBit()
{
    float negative = -1f / 1073741824f;
    Assert.Equal(BitConverter.SingleToInt32Bits(negative - MathF.Floor(negative)), BitConverter.SingleToInt32Bits(new MapExactValue(1073741823, 1073741824).ToSingle()));
    Assert.Equal(1f, new MapExactValue(1073741823, 1073741824).ToSingle());
    LegacyOracleWorld w = LegacyOracleWorld.Create();
    float[] fractions = { 0f, 0.25f, 0.5f, 0.75f, 0.9990234375f };
    foreach (string name in new[] { "void", "flag-nodraw", "extreme", "seam-west", "derived-plane-1" })
    {
        LegacyOracleCase c = w.Cases.Single(x => x.Name == name);
        var (surface, patch) = LegacyOracleConverter.ToNative(w.Document, RegionCoord.Of(c.WorldX, c.WorldZ), c.Plane);
        foreach (float fx in fractions)
            foreach (float fz in fractions)
            {
                float worldX = c.WorldX + fx, worldZ = -(c.WorldZ + fz);
                float released = w.Document.HeightAt(worldX, worldZ, c.Plane);
                float native = MapLegacyBilinear.HeightMetres(surface, patch, new MapExactXz(MapExactValue.FromSingle(worldX), MapExactValue.FromSingle(worldZ)));
                Assert.Equal(BitConverter.SingleToInt32Bits(released), BitConverter.SingleToInt32Bits(native));
            }
    }
}
```

`AcquisitionBoundFixtures.AdversarialUnits` anchors its records in the `odd` patch for this test (the Task 8 table names the scope's ceiling patch, which is `odd`). `LegacyOracleCase.WorldX` and `WorldZ` are TileWorld tile coordinates, the convention of `TileGroundTriangles.TryDescribe` and `RegionCoord.Of`, so tile row z spans world Z from `-(z + 1)` to `-z` and `TileWorldDocument.TileSize` is the default 1 (`TileWorldDocument.cs:15` at `ca13d62d7`). The 16 by 16 fixture only proves refusal with zero pair comparisons. Its full mathematical counts are not executed. The 2 by 2 fixture proves separate attempted and positive-box counters with 64 comparisons, 16 positive boxes and 8 positive intersections. The two context tests prove I1 with counters, never timing: in-range faces are counted from bytes before any compile, the 8 by 8 footprint compiles each of its two bound patches once for all 64 cells with no public clone, and the same footprint under `MaxContextFaces` 639 refuses after counting 640 faces with zero compiles. The default budget values are asserted on their own, with no giant fixture.

- [ ] **Step 2: Run red.** `wa_test t12a-red "$MAPDOC" "FullyQualifiedName~CommonRefinementTests|FullyQualifiedName~LowerCellClassifierTests"`. Expected: build FAIL naming `MapCommonRefinement`, `MapCellRefinement` and `MapLowerCellClassifier`.
- [ ] **Step 3: Implement the refinement and the compiled bound-face context exactly as D7 pins them, and the classifier and bilinear helper exactly as D9 pins them.** All refinement geometry is `MapExactValue`. No float, epsilon, snapping or rounding appears in `MapCommonRefinement.cs` or `MapLowerCellClassifier.cs`. The only float arithmetic is `MapLegacyBilinear`'s released expression after one exact-to-float conversion of each fraction.
- [ ] **Step 4: Run green.** `wa_test t12a-green "$MAPDOC" "FullyQualifiedName~CommonRefinementTests|FullyQualifiedName~LowerCellClassifierTests|FullyQualifiedName~ScopedAcquisitionTests"` (PASS, 12 new cases, 10 refinement and 2 classifier, plus the Task 8 acquisition cases), `wa_test t12a-green-editor "$EDITOR" "FullyQualifiedName~RefinementWorkTests|FullyQualifiedName~CompilerBudgetTests"` (PASS, 3 refinement work cases plus the 2 Task 9 budget cases) and `wa_run t12a-green-compat dotnet test "$COMPAT" -c Release` (PASS, 3: Tasks 3 and 9 plus this one). A bit mismatch against `HeightAt` is a reported blocker with the case and fractions, never a tolerance.
- [ ] **Step 5: Commit** `KhaozEngine.MapDoc/Spaces/MapRefinementTypes.cs KhaozEngine.MapDoc/Spaces/MapCommonRefinement.cs KhaozEngine.MapDoc/Spaces/MapBoundFaceContext.cs KhaozEngine.MapDoc/Spaces/MapLowerCellClassifier.cs KhaozEngine.MapDoc/Surfaces/MapLegacyBilinear.cs KhaozEngine.MapDoc.Tests/Spaces KhaozEngine.MapEditor.Tests/MapDoc/RefinementWorkTests.cs KhaozEngine.MapDoc.Compatibility.Tests/LegacyBilinearOracleTests.cs`, message `feat(mapdoc): refine mixed-resolution bounds exactly and classify legacy lower cells`.

---

### Task 12B: Occupied-space membership, coverage and separation validation

**Files:**
- Create: `KhaozEngine.MapDoc/MapFramePoint.cs`, `KhaozEngine.MapDoc/Spaces/MapSpaceMembership.cs`, `MapSpaceCoverageValidator.cs`
- Test: `KhaozEngine.MapDoc.Tests/Spaces/SpaceMembershipTests.cs`, `PortalBandCoverageTests.cs`, fixtures `CaveFixtures.cs`, `PortalBandFixtures.cs`, `FilteredSurfaceSourceFixtures.cs` (holds the test-only `sealed class FilteredSurfaceSource : IMapSurfaceSource`, so the Task 7 link includes it), `KhaozEngine.MapEditor.Tests/MapDoc/CoverageWorkTests.cs` (internal work counters)

**Interfaces (new):**
- `public readonly record struct MapFramePoint(WorldFrame Frame, Vector3 Local)` in `KhaozEngine.MapDoc`, local XZ relative to the frame anchor, Y world-datum absolute, with `MapExactXz ExactWorldXz()` (the integer anchor plus `MapExactValue.FromSingle` of each local float, no float addition) and `MapExactValue ExactY()`. Both throw `MapExactOverflowException` for an unrepresentable float and `ArgumentException` for a non-finite one.
- `public enum MapMembershipStatus { Resolved, Outside, MissingGeometry, Ambiguous, CapacityExceeded, NotRepresentable, Invalid }`, `public sealed class MapMembershipResult` (factory-only, Global Constraints) with `Status`, `MapFramePoint Point`, `string? SpaceId`, `IReadOnlyList<string> DomainKeys`, `MapLegacyCellTag? LowerCompatibility` (set only when the resolved space's lower bound under the point is a D9 `LegacyNonCapture` cell), `string? Detail`, `MapReadWitness Witness`. `DomainKeys` is the space id, its parent chain, each space's `DomainTags`, then any matching indoor span id and tags.
- `public sealed class MapSpaceMembership { public MapSpaceMembership(MapScopedSurfaces scoped); public MapMembershipResult Query(MapFramePoint point); }`. A point in another frame throws `ArgumentException` containing `frame`. An unrepresentable point is `NotRepresentable`, never snapped. A point outside the acquired rectangle or Y range is `MissingGeometry` (`outside acquired scope`). An acquisition that is `CapacityExceeded` or `NotRepresentable` gives that status. Otherwise: footprints from acquired records whose cells contain the point (half-open on the footprint lattice), each bound evaluated exactly on the cell of its own surface under the point, on whatever rational lattice that surface uses (corner and fan arithmetic as compiled), or on an opening plane, `OpenTop` unbounded, inclusion when `lower <= y < upper`. A lower `SupportFloor` or `LegacyExteriorV1` cell is first classified by `MapLowerCellClassifier.Classify` and treated exactly as the D9 table's membership column says. A `LegacyNonCapture` lower height is `MapExactValue.FromSingle(MapLegacyBilinear.HeightMetres(...))`, compared exactly with the point's `ExactY`, and the result carries the tag. `InvalidRecipe` is `Invalid` with the `legacy recipe` detail. On a wall portal plane inside its band the point belongs to `ToSpace`. At a shared portal vertex the lowest-ordinal incident boundary record id decides. On a horizontal opening plane the space above owns it. Alias groups collapse to the target. Indoor spans come from `MapSurfaceRef.IndoorSpan` of surfaces whose `ParentSpace.Id` is the resolved space or an ancestor, when that surface's cell under the point has `Indoor` and `y` lies in the span. A needed patch or record that is not acquired, missing or corrupt, or an absent bound without a declared opening, is `MissingGeometry` naming it. Two non-aliased candidates are `Ambiguous`. No surface-world fallback. Each `Query` prepares one D7 context over the cells under the point on every bound surface it evaluates, with `MaxCandidatePatches` and `MaxInspectedFaces` of the scope's limits (overflow is `CapacityExceeded`), reads exact heights only from that context and never calls `Patch` or a whole-patch compile. An internal overload `Query(MapFramePoint point, MapBoundFaceWork work)` exposes the counters, and an internal evaluation that takes an already prepared context serves support (Task 13).
- `public static class MapSpaceCoverageValidator { public static IReadOnlyList<string> Validate(MapScopedSurfaces completeView); public static IReadOnlyList<string> Validate(MapScopedSurfaces completeView, MapRefinementLimits limits); }` (refuses a view whose witness is not complete with `ArgumentException`). Findings, ordered by record id then slot cell: Task 5 reference rules, Task 10 seams and corner dependencies, every patch's `ValidateLocal`, per footprint cell the D7 refinement findings in D7's `<kind>: footprint '<id>' cell <n>` form (`refinement capacity`, `not representable`, `invalid` with the refinement detail such as `legacy recipe: space` appended in parentheses, `missing geometry`, `missing bound` judged as `LowerArea + CompatibilityArea != CellArea`, `separation ... at (<x>, <z>)`), per-side coverage of every `Cave` footprint boundary edge and every portal interval (`vertex sequence`, `gap`, `overlap`, `air interval`), adjacent columns of one space with differing bounds lacking strips (`gap`), coincident support faces with two owners and no `PaintOverride` (`coincident`), peer spaces sharing air without alias or partition (`ambiguous`). Strip compilation keeps its strict `vertex sequence` rule across lattices. An `Exterior` footprint edge whose neighbouring column belongs to no space is an open world edge and needs no coverage. Validation is a complete-view operation for bounded fixtures and transactions, not large-world validation. The refinement findings come from one `MapCommonRefinement.PrepareFootprints` call over every footprint of the view (one context per `Validate` call, D7), then the internal context `RefineFootprintCell` per surviving cell. A context refusal is one `refinement capacity: footprint '<id>' (context faces)` or `(context patches)` finding per affected footprint with zero compiles. Every acquired patch read in validation goes through `TryAcquiredPatch`. An internal overload `Validate(MapScopedSurfaces completeView, MapRefinementLimits limits, MapBoundFaceWork work)` exposes the counters.

**Cave fixture.** `CaveFixtures.RampChamberShaft()` is a resolver-2 document. Every surface is `(1/1, 1/100, PositiveZ)`, `Native`, slot (0, 0). Coordinates are cells, so world X = x and Z = z, and every vertex is `Corner(x, z)` on its surface.

| Surface | Role | Cells (x, z) | Heights (cm) | Absent cells |
| --- | --- | --- | --- | --- |
| `outer` | SupportFloor | 0 to 7, 0 to 7 | all 1000 | x 2 to 5 at z 2 |
| `cave-floor` | SupportFloor | 2 to 5, 2 to 7 | corner row z 2: 1000, z 3: 800, z 4: 600, z 5 to 8: 400 | x 3 to 4, z 5 to 6 |
| `cave-ceiling` | Ceiling | 2 to 5, 3 to 7 | all 900 | none |
| `deep-floor` | SupportFloor | 2 to 5, 5 to 7 | all -2000 | none |
| `deep-ceiling` | Ceiling | 2 to 5, 5 to 7 | all -1500 | x 3 to 4, z 5 to 6 |

| Record | Definition |
| --- | --- |
| `rim-seam` | `outer` edge z 2, x 2 to 6 paired with `cave-floor` edge z 2, x 2 to 6 |
| `mouth` | Portal From `chamber` To `outside`, interval z 3, x 2 to 6, bottom `cave-floor` edge z 3 (800), top `cave-ceiling` edge z 3 (900) |
| `mouth-return` | Strip Front, lower `cave-ceiling` edge z 3, upper `outer` edge z 3, x 2 to 6 |
| `ramp-west`, `ramp-east` | Strips at x 2 (Front) and x 6 (Back), z 2 to 3, lower `cave-floor`, upper `outer`, zero height at z 2 |
| `west-wall`, `east-wall`, `back-wall` | Strips at x 2 (Front), x 6 (Back), z 8 (Front), lower `cave-floor`, upper `cave-ceiling` |
| `shaft-top`, `shaft-bottom` | Openings over cells x 3 to 4, z 5 to 6 in `cave-floor` and `deep-ceiling` |
| `shaft-west/east/south/north` | Strips at x 3 (Front), x 5 (Back), z 5 (Back), z 7 (Front), lower `deep-ceiling`, upper `cave-floor` |
| `deep-west/east/south/north` | Strips at x 2 (Front), x 6 (Back), z 5 (Back), z 8 (Front), lower `deep-floor`, upper `deep-ceiling` |
| `shaft-link` | Link, upper `chamber`, lower `deep`, openings `shaft-top`, `shaft-bottom`, owners the four shaft strips |
| `outside` (Exterior) | Footprints: `outer` cells except x 2 to 5 at z 2 (lower `outer`, upper `OpenTop`), and x 2 to 5 at z 2 (lower `cave-floor`, upper `OpenTop`). Walls `(mouth-return, Front)`, `(ramp-west, Front)`, `(ramp-east, Back)`, Portals `(mouth, Back)` |
| `chamber` (Cave) | Footprints: x 2 to 5, z 3 to 7 except shaft cells (lower `cave-floor`, upper `cave-ceiling`), shaft cells (lower opening `shaft-top`, upper `cave-ceiling`). Walls `(west-wall, Front)`, `(east-wall, Back)`, `(back-wall, Front)`, Portals `(mouth, Front)`, Links `shaft-link` |
| `shaft` (Cave) | Footprint shaft cells, lower opening `shaft-bottom`, upper opening `shaft-top`. Walls the four shaft strips |
| `deep` (Cave) | Footprints x 2 to 5, z 5 to 7 except shaft cells (lower `deep-floor`, upper `deep-ceiling`), shaft cells (lower `deep-floor`, upper opening `shaft-bottom`). Walls the four deep strips, Links `shaft-link` |

Anchors: `outside` and its `outer` footprint in `outer`, `shaft-bottom` in `deep-ceiling`, `deep`, its footprints and the deep walls in `deep-floor`, every other record in `cave-floor`. Every `MapRecordRef` names that anchor. Front normals follow Task 10. `ShaftJoinedToLowerChamber()` lowers the `shaft` footprint to `deep-floor`, drops the shaft cells from `deep`, removes `shaft-bottom` (its cells stay absent) and closes the shaft perimeter below the deep ceiling with portals `shaft-portal-w/e/s/n` (From `shaft`, To `deep`, band `deep-floor` edge to `deep-ceiling` edge). `WithoutOpening("shaft-top")` deletes that opening and points the chamber shaft-cell footprint's lower bound at `cave-floor`. Nothing else changes, so the other references to `shaft-top` deliberately dangle (coordinator final review M2): the `shaft` footprint's upper bound and `shaft-link.Openings` still name it. The expected failures are explicit: the Task 5 dangling-reference findings naming `shaft-top`, `missing bound` on the four chamber shaft cells (`cave-floor` is absent there, D9 order 4) and `missing geometry` on the four `shaft` footprint cells (their upper opening record is missing). No record is removed or retargeted to hide them. `WithPeerChamber(string? aliasOf)` adds space `chamber-copy` over the first chamber footprint's cells. `IndoorRow()` is `plane-0` (`ImportedMetreCentimetre`, `LegacyTileWorld`), cells x 0 to 1 at z 0, flat 0, cell 0 `Indoor`, Exterior `world` (OpenTop) over both, and `IndoorSpan("indoor-0", world@plane-0(0,0), 0, 450, ["indoor"])` on the `plane-0` ref. Helpers: `View(doc) = MapScopedSurfaces.CompleteView(doc.Surfaces)`, `Compile(doc, surfaceId)` compiles that surface's slot (0, 0), `Record<T>(doc, id)`, `Query(doc, x, y, z, limits?)` and `Query(IMapSurfaceSource source, x, y, z, limits?)` acquire `ScopeFixtures.Around(WorldFrame.Origin, x, z, half: 2, limits)` and query `new MapFramePoint(WorldFrame.Origin, new(x, y, z))`. `FilteredSurfaceSource(IMapSurfaceSource inner, params MapPatchKey[] unloaded)` reports those keys `Unloaded` from `ReadPatch` and `FindPatches`.

**Band fixture.** `PortalBandFixtures.Build(variant)`: spaces `hall` (cells x 0 to 1, z 0 to 1, floor `hall-floor`, ceiling `hall-ceiling`) and `passage` (x 2 to 3, floor `passage-floor`, ceiling `passage-ceiling`), frame `(1/1, 1/100, PositiveZ)`, portal `door` (From `hall`, To `passage`) on x 2, z 0 to 2 (`Corner(2, 0..2)`). Hall-side strips run +Z at x 2 and face the hall on `Back`. Passage edges at x 4 and the outer hall edges carry closing strips except in `cave-open-edge`. `CompileAllStrips(doc)` resolves and compiles every strip.

| Variant | Floors | Ceilings | Band | Strips | Expected |
| --- | --- | --- | --- | --- | --- |
| `header` | 0, 0 | hall 3000, passage 300 | 0 to passage ceiling | `header` Back, passage ceiling edge to hall ceiling edge | valid |
| `header-missing` | as header | as header | as header | none | `gap` |
| `header-overlap` | as header | as header | as header | `header` lower authored at 250 | `overlap` |
| `riser` | hall 0, passage 100 | hall 3000, passage 400 | 100 to 400 | `riser` Back hall floor to passage floor, `header` Back 400 to 3000 | valid |
| `lintel` | 0, 0 | hall 500, passage 600 | 0 to authored `lintel-bottom` 300 | `lintel` TwoSided 300 to 500 as `(lintel, Back)` in hall and `(lintel, Front)` in passage, `lintel-upper` Front 500 to 600 | valid |
| `arch` | 0, 0 | hall 3000, passage 500 | 0 to authored `arch` 300, 400, 300 | `header` Back, lower `arch`, upper hall ceiling | valid |
| `arch-mismatch` | as arch | as arch | as arch | `header` lower authored with only `Corner(2,0)` and `Corner(2,2)` at 300 | `vertex sequence` |
| `outside-air` | 0, 0 | hall 3000, passage 300 | 0 to authored 3100 | as header | `air interval` |
| `duplicate-side` | as header | as header | as header | `(header, Back)` in both spaces | `duplicate` |
| `cave-open-edge` | as header | as header | as header | as header, passage x 4 closing strip removed | `gap` |

- [ ] **Step 1: Write the failing tests**

```csharp
// SpaceMembershipTests
static (string?, MapMembershipStatus) Space(MapDocument doc, float x, float y, float z) { var m = CaveFixtures.Query(doc, x, y, z); return (m.SpaceId, m.Status); }
static IReadOnlyList<string> Validate(MapDocument doc) => MapSpaceCoverageValidator.Validate(CaveFixtures.View(doc));

public void NativeCaveRepresentationContract()
{
    Assert.Empty(Validate(CaveFixtures.RampChamberShaft()));
    Assert.Empty(Validate(CaveFixtures.ShaftJoinedToLowerChamber()));
    MapDocument cave = CaveFixtures.RampChamberShaft();
    Assert.Equal(("chamber", MapMembershipStatus.Resolved), Space(cave, 2.5f, 5.5f, 4.5f));
    Assert.Equal(("outside", MapMembershipStatus.Resolved), Space(cave, 2.5f, 10.5f, 4.5f));
    Assert.Equal(((string?)null, MapMembershipStatus.Outside), Space(cave, 2.5f, 9.5f, 4.5f));   // inside the rock roof
    Assert.Equal(("deep", MapMembershipStatus.Resolved), Space(cave, 2.5f, -19f, 6.5f));
    Assert.Equal(("outside", MapMembershipStatus.Resolved), Space(cave, 3.5f, 9.5f, 2.5f));
}
public void ExactPortalAndOpeningPlanes_FollowTheOwnershipTieRules()
{
    MapDocument cave = CaveFixtures.RampChamberShaft();
    Assert.Equal("outside", Space(cave, 4.0f, 8.5f, 3.0f).Item1);      // on the mouth plane, To owns it
    Assert.Equal("chamber", Space(cave, 3.5f, 4.0f, 5.5f).Item1);      // on the shaft-top plane, the space above owns it
    Assert.Equal("shaft", Space(cave, 3.5f, 3.99f, 5.5f).Item1);
    Assert.Equal("shaft", Space(cave, 3.5f, -15.0f, 5.5f).Item1);
    Assert.Equal("deep", Space(cave, 3.5f, -15.01f, 5.5f).Item1);
}
public void ShaftOpenings_HaveKnownBoundsWithoutFabricatedFloors()
{
    MapDocument cave = CaveFixtures.RampChamberShaft();
    int[] shaftCells = { 5 * 64 + 3, 5 * 64 + 4, 6 * 64 + 3, 6 * 64 + 4 };
    foreach (string surface in new[] { "cave-floor", "deep-ceiling" })
        Assert.DoesNotContain(CaveFixtures.Compile(cave, surface).Faces, f => shaftCells.Contains(f.Key.Primitive));
    MapDocument open = CaveFixtures.WithoutOpening("shaft-top");                         // the shaft footprint and shaft-link still name shaft-top on purpose
    IReadOnlyList<string> found = Validate(open);
    IReadOnlyList<string> dangling = MapTopologyReferenceValidator.Validate(open.Surfaces.Refs, open.Surfaces.Patches.Values);
    Assert.Contains(dangling, f => f.Contains("shaft-top"));
    Assert.Subset(found.ToHashSet(), dangling.ToHashSet());
    foreach (int cell in shaftCells)
    {
        Assert.Contains(found, f => f.Contains("missing bound") && f.EndsWith($"cell {cell}", StringComparison.Ordinal));
        Assert.Contains(found, f => f.Contains("missing geometry") && f.Contains($"cell {cell}"));
    }
    Assert.Equal(MapMembershipStatus.MissingGeometry, CaveFixtures.Query(open, 3.5f, 5.0f, 5.5f).Status);
}
public void UnloadedAmbiguousAndOverBudgetQueriesRefuse()
{
    MapDocument cave = CaveFixtures.RampChamberShaft();
    var filtered = new FilteredSurfaceSource(MapDocumentSurfaceSource.Capture(cave), new MapPatchKey("cave-ceiling", 0, 0));
    Assert.Equal(MapMembershipStatus.MissingGeometry, CaveFixtures.Query(filtered, 2.5f, 5.5f, 4.5f).Status);
    Assert.Equal(MapMembershipStatus.CapacityExceeded, CaveFixtures.Query(cave, 2.5f, 5.5f, 4.5f, new MapQueryLimits(MaxCandidatePatches: 1)).Status);
    MapDocument peer = CaveFixtures.WithPeerChamber(null);
    Assert.Contains(Validate(peer), f => f.Contains("ambiguous"));
    Assert.Equal(MapMembershipStatus.Ambiguous, CaveFixtures.Query(peer, 2.5f, 5.5f, 4.5f).Status);
    Assert.Equal("chamber", CaveFixtures.Query(CaveFixtures.WithPeerChamber("chamber"), 2.5f, 5.5f, 4.5f).SpaceId);
}
public void TinyLocalCoordinate_IsNotRepresentableNeverSnapped()
    => Assert.Equal(MapMembershipStatus.NotRepresentable, CaveFixtures.Query(CaveFixtures.RampChamberShaft(), 1e-30f, 10.5f, 4.5f).Status);
public void ImportedIndoorMask_IsANestedDomainWithAnExplicitSpan()
{
    MapDocument row = CaveFixtures.IndoorRow();
    Assert.Equal(new[] { "world", "indoor-0", "indoor" }, CaveFixtures.Query(row, 0.5f, 1.0f, -0.5f).DomainKeys);
    Assert.Equal(new[] { "world" }, CaveFixtures.Query(row, 0.5f, 4.6f, -0.5f).DomainKeys);
    Assert.Equal(new[] { "world" }, CaveFixtures.Query(row, 1.5f, 1.0f, -0.5f).DomainKeys);
}
public void MixedResolutionBounds_ValidateOnTheCommonRefinement()
{
    MapDocument converted = MixedResolutionFixtures.Converted();
    Assert.Empty(Validate(converted));
    Assert.Equal("room", CaveFixtures.Query(converted, 1.5f, 5.0f, 1.5f).SpaceId);           // over the fine floor, under the 9 m coarse roof
    Assert.Empty(MapSpaceCoverageValidator.Validate(MixedResolutionFixtures.FinePeakUnderCoarseRoof(899)));
    Assert.Contains(MapSpaceCoverageValidator.Validate(MixedResolutionFixtures.FinePeakUnderCoarseRoof(900)), f => f.Contains("separation"));
    Assert.Empty(MapSpaceCoverageValidator.Validate(MixedResolutionFixtures.HalfUnderThird(far: false).View));
    Assert.Empty(MapSpaceCoverageValidator.Validate(MixedResolutionFixtures.HalfUnderThird(far: true).View));
}
[Theory, InlineData(1199, null), InlineData(1200, "(1/2, 1/3)")]
public void RidgeUnderValley_SeparationIsCheckedAtCrossingsNeitherLatticeOwns(int ridge, string? vertex)
{
    IReadOnlyList<string> findings = MapSpaceCoverageValidator.Validate(MixedResolutionFixtures.RidgeUnderValley(ridge, 1800));
    if (vertex is null) Assert.Empty(findings);
    else Assert.Contains(findings, f => f.Contains("separation") && f.Contains(vertex));
}
public void MixedResolutionWalls_StillRequireMatchingSubdivisions()
{
    Assert.Empty(MapSpaceCoverageValidator.Validate(MixedResolutionFixtures.ClosedRidgeCave(subdivided: true)));
    Assert.Contains(MapSpaceCoverageValidator.Validate(MixedResolutionFixtures.ClosedRidgeCave(subdivided: false)), f => f.Contains("vertex sequence"));
}
public void MixedRefinementOutcomes_BecomeExplicitFindings()
{
    var f = MixedResolutionFixtures.SixtyFourthUnderMetre();
    Assert.Empty(MapSpaceCoverageValidator.Validate(f.View));
    Assert.Contains(MapSpaceCoverageValidator.Validate(f.View, new MapRefinementLimits(MaxPairChecks: 16383)), x => x.Contains("refinement capacity"));
    Assert.Contains(MapSpaceCoverageValidator.Validate(MapScopedSurfaces.CompleteView(AcquisitionBoundFixtures.AdversarialUnits().Surfaces)),
        x => x.Contains("not representable") && x.Contains("strange-cells"));
}
public void BoundaryTouchLedge_ResolvesMembershipOverTheMixedBounds()
{
    MapScopedSurfaces s = ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(AcquisitionBoundFixtures.BoundaryTouch()), AcquisitionBoundFixtures.TouchScope);
    MapMembershipResult m = new MapSpaceMembership(s).Query(new MapFramePoint(WorldFrame.Origin, new(31.8f, 1f, 0.2f)));
    Assert.Equal(("ledge", MapMembershipStatus.Resolved), (m.SpaceId, m.Status));
    Assert.Same(s.ReadWitness, m.Witness);
}
public void ConvertedMixedPorch_ValidatesWithItsCoarseHalfFloor()
{
    MapConversionResult r = MapFinePatchConversion.Convert(ConversionFixtures.MixedPorch(), ConversionFixtures.PorchRoofRequest(width: 3));
    Assert.Empty(MapSpaceCoverageValidator.Validate(MapScopedSurfaces.CompleteView(r.Candidate)));
}
public void FarAnchoredSpaces_ResolveThroughTheAcquiredAnchor()
{
    string dir = AnchorFixtures.FarAnchors();
    MapSurfaceScope scope = ScopeFixtures.Around(AnchorFixtures.FarFrame, 2, 2, half: 1.5f);
    MapScopedSurfaces s = ScopeFixtures.Acquire(MapStoredSurfaceSource.Open(dir), scope);
    var membership = new MapSpaceMembership(s);
    MapMembershipResult outside = membership.Query(new(AnchorFixtures.FarFrame, new(0.5f, 10.5f, 1.5f)));
    MapMembershipResult annex = membership.Query(new(AnchorFixtures.FarFrame, new(2.5f, 12f, 1.5f)));
    Assert.Equal(("outside", "annex"), (outside.SpaceId, annex.SpaceId));
    Assert.Equal(new[] { "outside" }, outside.DomainKeys);
    Assert.Same(s.ReadWitness, outside.Witness);
    Assert.Same(s.ReadWitness, annex.Witness);
    SurfaceStorageFixtures.DeletePayload(dir, new("ground", 0, 0));
    MapMembershipResult missing = new MapSpaceMembership(ScopeFixtures.Acquire(MapStoredSurfaceSource.Open(dir), scope)).Query(new(AnchorFixtures.FarFrame, new(0.5f, 10.5f, 1.5f)));
    Assert.Equal(MapMembershipStatus.MissingGeometry, missing.Status);
    Assert.Contains("outside", missing.Detail);
}
public void LegacyExterior_ValidatorAndMembershipShareTheClassification()
{
    static MapMembershipResult At(MapDocument doc, float x, float y = 5f) => CaveFixtures.Query(doc, x, y, -0.5f);
    MapDocument tagged = LegacyExteriorFixtures.Row("tagged");
    Assert.Empty(Validate(tagged));
    Assert.Equal(("world", (MapLegacyCellTag?)null), (At(tagged, 0.5f).SpaceId, At(tagged, 0.5f).LowerCompatibility));
    Assert.Equal(new MapLegacyCellTag(new("plane-0", 0, 0), 1, MapLegacyExteriorRecipe.PolicyId), At(tagged, 1.5f).LowerCompatibility);
    Assert.Equal((MapMembershipStatus.Resolved, MapMembershipStatus.Outside), (At(tagged, 1.5f, 3.0f).Status, At(tagged, 1.5f, 2.99f).Status));   // bilinear lower 3 m, inclusive
    MapDocument hole = LegacyExteriorFixtures.Row("hole"), untagged = LegacyExteriorFixtures.Row("untagged");
    Assert.Contains(Validate(hole), f => f.Contains("missing bound") && f.Contains("cell 3"));
    Assert.Equal(MapMembershipStatus.MissingGeometry, At(hole, 3.5f).Status);                                            // a physical hole is never filled
    Assert.Contains(Validate(untagged), f => f.Contains("missing bound") && f.Contains("cell 1"));
    Assert.Equal(MapMembershipStatus.MissingGeometry, At(untagged, 1.5f).Status);                                        // no tag, no fallback
    foreach (string variant in new[] { "cave", "upper-tag", "foreign-lattice" })
    {
        MapDocument bad = LegacyExteriorFixtures.Row(variant);
        Assert.Contains(Validate(bad), f => f.Contains("legacy recipe"));
        Assert.Equal(MapMembershipStatus.Invalid, At(bad, 1.5f).Status);
    }
}

// PortalBandCoverageTests
[Theory, InlineData("header", null), InlineData("header-missing", "gap"), InlineData("header-overlap", "overlap"), InlineData("riser", null),
 InlineData("lintel", null), InlineData("arch", null), InlineData("arch-mismatch", "vertex sequence"), InlineData("outside-air", "air interval"),
 InlineData("duplicate-side", "duplicate"), InlineData("cave-open-edge", "gap")]
public void PortalBandCoverage_HeadersRisersAndLintels(string variant, string? finding)
{
    IReadOnlyList<string> findings = MapSpaceCoverageValidator.Validate(CaveFixtures.View(PortalBandFixtures.Build(variant)));
    if (finding is null) Assert.Empty(findings); else Assert.Contains(findings, f => f.Contains(finding));
}
public void PortalBandCoverage_EachFaceIsEmittedOnce()
{
    var faces = PortalBandFixtures.CompileAllStrips(PortalBandFixtures.Build("lintel")).SelectMany(s => s.Faces).ToList();
    Assert.Equal(faces.Count, faces.Select(f => f.Key).Distinct().Count());
    Assert.Equal(4, faces.Count(f => f.Key.OwnerId == "lintel"));
}

// CoverageWorkTests (MapEditor.Tests, internal work counters)
public void CoverageValidator_SharesOneContextWithoutClonesAndRefusesOverBudgetDemandWithZeroCompiles()
{
    var room = BoundFaceContextFixtures.EightByEight();
    var work = new MapBoundFaceWork();
    Assert.Empty(MapSpaceCoverageValidator.Validate(room.View, new MapRefinementLimits(), work));
    Assert.Equal((1, 2, 640L, 0), (work.ContextsPrepared, work.Compiles, work.CompiledFaces, room.View.PatchClones));
    Assert.All(room.View.Witness.Present, p => Assert.Equal(p.Value, MapSurfaceSemantics.PatchDigest(room.View.Patch(p.Key).Patch!)));   // internal readers wrote nothing
    var refused = new MapBoundFaceWork();
    IReadOnlyList<string> findings = MapSpaceCoverageValidator.Validate(room.View, new MapRefinementLimits(MaxContextFaces: 639), refused);
    Assert.Equal("refinement capacity: footprint 'room8-cells' (context faces)", Assert.Single(findings, f => f.Contains("room8-cells")));
    Assert.Equal((0, 0L, 640L), (refused.Compiles, refused.CompiledFaces, refused.ContextFacesCounted));
}
public void Membership_ReadsOneQueryContextWithoutClones()
{
    var room = BoundFaceContextFixtures.EightByEight();
    var work = new MapBoundFaceWork();
    MapMembershipResult m = new MapSpaceMembership(room.View).Query(new MapFramePoint(WorldFrame.Origin, new(2.5f, 1f, 3.5f)), work);
    Assert.Equal(("room8", MapMembershipStatus.Resolved), (m.SpaceId, m.Status));
    Assert.Equal((1, 2, 4L, 0), (work.ContextsPrepared, work.Compiles, work.CompiledFaces, room.View.PatchClones));   // one floor cell and one ceiling cell
}
```

`(1.5, 5.0, 1.5)` in `Converted()` lies over the fine floor at 1.6 to 3.3 m and under the 9 m coarse roof.

- [ ] **Step 2: Run red.** `wa_test t12b-red "$MAPDOC" "FullyQualifiedName~SpaceMembershipTests|FullyQualifiedName~PortalBandCoverageTests"`. Expected: build FAIL naming `MapSpaceMembership` and `MapSpaceCoverageValidator`.
- [ ] **Step 3: Implement membership and coverage.** Membership reads only the acquired set through one query context per call. Validation streams the complete view one patch at a time, prepares one context for the call and calls the internal context `RefineFootprintCell` per surviving footprint cell. Neither path classifies a lower cell itself: both go through `MapLowerCellClassifier`.
- [ ] **Step 4: Run green.** `wa_test t12b-green "$MAPDOC" "FullyQualifiedName~SpaceMembershipTests|FullyQualifiedName~PortalBandCoverageTests|FullyQualifiedName~CommonRefinementTests"` (PASS, 15 membership cases, 11 band cases and the 10 refinement cases) and `wa_test t12b-green-editor "$EDITOR" "FullyQualifiedName~CoverageWorkTests|FullyQualifiedName~RefinementWorkTests"` (PASS, 2 new cases plus the 3 Task 12A work cases).
- [ ] **Step 5: Commit** `KhaozEngine.MapDoc/MapFramePoint.cs KhaozEngine.MapDoc/Spaces KhaozEngine.MapDoc.Tests/Spaces KhaozEngine.MapEditor.Tests/MapDoc/CoverageWorkTests.cs`, message `feat(mapdoc): resolve occupied-space membership and validate mixed-resolution cave coverage`.

---

### Task 13: Height-aware support query and resolver version 2

**Files:**
- Create: `KhaozEngine.MapDoc/Support/MapSupportQuery.cs`, `MapSupportTypes.cs`, `MapResolverV2.cs`, `MapResolverAdoption.cs`
- Test: `KhaozEngine.MapDoc.Tests/Support/SupportQueryTests.cs`, `ResolverV2Tests.cs`, fixture `SupportFixtures.cs`, `KhaozEngine.MapEditor.Tests/MapDoc/SupportWorkTests.cs` (internal work counters)

**Interfaces (new, `KhaozEngine.MapDoc.Support`):**
- `public sealed record MapSupportRequest(MapFramePoint Point, string? SurfaceId, string? SpaceId, MapFaceKey? CurrentSupport, float MaxStepUp, float MaxDropDown, float? MaxSlopeRadians)`.
- `public enum MapSupportStatus { Supported, LegacyFallback, NoSupport, MissingGeometry, Ambiguous, CapacityExceeded, NotRepresentable, Invalid }`, `public sealed class MapSupportResult` (factory-only) with `Status`, `MapFaceKey? Face`, `float WorldY`, `Vector3? Normal` (the face's geometric normal, null unless `Supported`), `bool IsCaptureSupport`, `MapLegacyCellTag? Compatibility` (set only for `LegacyFallback`), `string? SpaceId`, `MapRecordRef? Via`, `string? Detail`, `MapReadWitness Witness`. `WorldY` is world-datum absolute, computed exactly and rounded once, except that a `LegacyFallback` `WorldY` is the `MapLegacyBilinear.HeightMetres` float itself.
- `public readonly record struct MapSupportCandidate(MapFaceKey Face, string SpaceId, float WorldY, Vector3 Normal, MapRecordRef? Via)` (a caller-buffer element, not a witness), `public sealed class MapSupportCandidateSet` (factory-only) with `Status`, `int Count`, `int RequiredCapacity`, `MapReadWitness Witness`.
- `public sealed class MapSupportQuery { public MapSupportQuery(MapScopedSurfaces scoped); public MapSupportResult Select(MapSupportRequest request); public MapSupportCandidateSet EnumerateCandidates(MapSupportRequest request, Span<MapSupportCandidate> buffer); }`.
- Order, the design's: (1) the acquired scope, any unavailable dependency is `MissingGeometry`, an unrepresentable point is `NotRepresentable`, (2) occupied space from `SpaceId` or membership (`Outside` is `NoSupport` with `outside every space`), keep `SupportFloor` faces bounding that space plus spaces reached through an acquired opening or portal whose aperture contains the point's XZ, recorded in `Via`, (3) interval `[y - MaxDropDown, y + MaxStepUp]` and slope, (4) highest eligible face. Intersections along the vertical line are counted before space filtering, beyond `MaxSupportIntersections` is `CapacityExceeded` with no face. `CurrentSupport` breaks only exact-height ties between faces of one surface or of surfaces joined by a declared seam. Equal-height independent owners are `Ambiguous` even when one is current. `PaintOverride` never supports. Lower cells follow the D9 table's support column through the membership result of step (2), which already holds the one classification. When no physical face is eligible and that membership carries `LowerCompatibility`, the result is `LegacyFallback` with `WorldY` from `MapLegacyBilinear.HeightMetres` (the released `TileWorldDocument.HeightAt` arithmetic, D9), `Face` null, `Normal` null, `IsCaptureSupport = false` and `Compatibility` set, provided that height lies in the step (3) interval, otherwise `NoSupport`. An untagged legacy cell, a presence-0 hole and unavailable data never fall back. Membership `Invalid` is `Invalid`. A known opening with no eligible lower face is `NoSupport`. An undeclared absent bound is `MissingGeometry`. Support addresses the cell under the point directly on each surface's own lattice and inspects only its faces (with fan children), counted against `MaxInspectedFaces`. Each `Select` or `EnumerateCandidates` call prepares one D7 context over those cells and the membership bound cells, passes it to its membership step, never calls `Patch` or a whole-patch compile, and an internal overload `Select(MapSupportRequest request, MapBoundFaceWork work)` exposes the counters. `EnumerateCandidates` writes at most `buffer.Length` and returns `CapacityExceeded` with the true `RequiredCapacity` when short. A partial set is never usable. It lists physical faces only, so a point whose only support is a fallback cell gives status `LegacyFallback` with `Count` 0 and `RequiredCapacity` 0.
- `public sealed record MapPlacementSupport(string PlacementId, MapSupportResult Support)`, `public sealed class MapSupportedResolution { public MapResolvedDocument Document { get; } public IReadOnlyList<MapPlacementSupport> Supports { get; } }`.
- `public static class MapResolverV2 { public static MapSupportedResolution Resolve(MapDocument document, MapAssetClosure assets, IMapSurfaceSource surfaces, MapResolveOptions options); }`. Requires `(1, 2)`, `AuthoredBindingsV2`, `options.ResolverVersion == 2`, computes the token with `MapAuthoredIdentityV2`, keeps explicit Y exactly. Each bound placement acquires `Around(WorldFrame.Nearest(x, z), localX, localZ, half: 2)` and selects there: `Surface` by exact sampling of that surface, `Space` within `[ReferenceY - SearchBelow, ReferenceY + SearchAbove]`. A missing-Y placement without a binding, or a binding resolving to any status other than `Supported` (including `LegacyFallback` and `Invalid`, so compatibility coverage never becomes authored support), throws naming the placement and status before any result.
- `public sealed record MapPlacementSupportChoice(float? ExplicitY, MapSupportBinding? Binding)`, `public static class MapResolverAdoption { public static MapDocument ConvertToAuthoredSupport(MapDocument document, IReadOnlyDictionary<string, MapPlacementSupportChoice> choices); }`. Refuses a partial document (`window`), refuses unless every missing-Y placement has exactly one choice (message names the first missing id in single quotes), returns a detached `(1, 2)`, `AuthoredBindingsV2` copy.

**Fixture.** `SupportFixtures.EightStacked()`: `(1/1, 1/100, PositiveZ)`, slot (0, 0), cells x 0 to 1, z 0 to 1, surfaces `floor-k` at `k*1000` and `ceiling-k` at `k*1000 + 500`, Cave spaces `level-k`, k 0 to 7. `EqualSlabs(bool secondIsPaint)`: `slab-a` and `slab-b` at 0 over cell (0, 0), Exterior `room` (OpenTop) with one footprint per slab, `slab-b` a `PaintOverride` of `slab-a` when requested. `Step()`: `low` at 0 over (0, 0), `step` at 30 over (1, 0), Exterior `yard`. `LegacyFallback()` is `LegacyExteriorFixtures.Row("tagged")` (Task 12A), whose cells 0 and 1 carry v3's corner rows and `NoDraw` cell, now declared with the D9 tag so the same validator accepts it. `Request(x, y, z, up, down)` is an origin-frame request with no surface, space, current support or slope. `Acquire(doc, MapFramePoint point, MapQueryLimits? limits = null)` acquires `Around(point.Frame, point.Local.X, point.Local.Z, half: 2, limits)` over `Capture(doc)`. `Select(doc, request, limits?)` acquires that way and selects. `FirstFace(doc, surfaceId)` is the first compiled face key at slot (0, 0). `V2 = new MapResolveOptions("headless", 1, "options", ResolverVersion: 2)`, `Assets()` the Task 2 closure. `CaveWithPlacements()` is `RampChamberShaft()` plus `p-explicit` at (1, 12.25, 1), `p-surface` at X 3.5, Z 4.5 bound to surface `cave-floor` (5.0 m), `p-space` at X 2.5, Z 6.5 bound to space `chamber`, `ReferenceY 4.2`, search 1 and 1 (floor 4.0 m), all `Kind "scenery"`, `AssetId "tree"`. `EqualSlabsWithBinding()` adds `p-amb` at (0.5, 0.5) bound to `room`, `ReferenceY 0.2`.

- [ ] **Step 1: Write the failing tests**

```csharp
// SupportQueryTests
static MapSupportResult Select(MapDocument doc, MapSupportRequest r, MapQueryLimits? limits = null) => SupportFixtures.Select(doc, r, limits);
static MapSupportRequest Req(float x, float y, float z, float up, float down) => SupportFixtures.Request(x, y, z, up, down);

public void StackedCaveFloorsAndCeilings_PreserveGeometryAndSupportSelection()
{
    MapDocument doc = SupportFixtures.EightStacked();
    MapSupportResult a = Select(doc, Req(0.5f, 10.2f, 0.5f, 0.5f, 1f));
    Assert.Equal((MapSupportStatus.Supported, "floor-1", 10.0f, "level-1"), (a.Status, a.Face!.Value.OwnerId, a.WorldY, a.SpaceId));
    Assert.Equal("floor-0", Select(doc, Req(0.5f, 4.9f, 0.5f, 1f, 5f)).Face!.Value.OwnerId);
    Assert.Equal("floor-0", Select(doc, Req(0.5f, 1.0f, 0.5f, 20f, 1f)).Face!.Value.OwnerId);        // floor-1 is in range but bounds another space
    Assert.Equal(MapSupportStatus.NoSupport, Select(doc, Req(0.5f, 7.0f, 0.5f, 1f, 1f)).Status);       // inside the rock between levels
    Assert.Equal("floor-3", Select(doc, Req(0.5f, 30.2f, 0.5f, 0.5f, 1f) with { SpaceId = "level-3" }).Face!.Value.OwnerId);
    MapSupportResult over = Select(doc, Req(0.5f, 70.2f, 0.5f, 0.5f, 100f), new MapQueryLimits(MaxSupportIntersections: 2));
    Assert.Equal((MapSupportStatus.CapacityExceeded, (MapFaceKey?)null), (over.Status, over.Face));
}
public void EqualHeightIndependentOwnersRefuseEvenWhenOneIsCurrent()
{
    MapDocument slabs = SupportFixtures.EqualSlabs(secondIsPaint: false);
    MapSupportRequest req = Req(0.5f, 0.2f, 0.5f, 0.5f, 1f) with { CurrentSupport = SupportFixtures.FirstFace(slabs, "slab-a") };
    Assert.Equal(MapSupportStatus.Ambiguous, Select(slabs, req).Status);
    MapSupportCandidateSet set = new MapSupportQuery(SupportFixtures.Acquire(slabs, req.Point)).EnumerateCandidates(req, new MapSupportCandidate[1]);
    Assert.Equal((MapSupportStatus.CapacityExceeded, 1, 2), (set.Status, set.Count, set.RequiredCapacity));
    Assert.Equal("slab-a", Select(SupportFixtures.EqualSlabs(secondIsPaint: true), req).Face!.Value.OwnerId);
}
public void CurrentLowerOwnerDoesNotBlockALegalHigherStep()
{
    MapDocument step = SupportFixtures.Step();
    MapSupportResult r = Select(step, Req(1.5f, 0.35f, 0.5f, 0.5f, 1f) with { CurrentSupport = SupportFixtures.FirstFace(step, "low") });
    Assert.Equal(("step", 0.3f), (r.Face!.Value.OwnerId, r.WorldY));
}
public void KnownHolesAreNoSupport_UndeclaredHolesAreMissingGeometry_LinksReachLowerFloors()
{
    MapDocument cave = CaveFixtures.RampChamberShaft();
    Assert.Equal(MapSupportStatus.NoSupport, Select(cave, Req(3.5f, 3.0f, 5.5f, 0.5f, 5f)).Status);
    MapSupportResult deep = Select(cave, Req(3.5f, 3.0f, 5.5f, 0.5f, 30f));
    Assert.Equal((MapSupportStatus.Supported, "deep-floor", -20.0f, "deep"), (deep.Status, deep.Face!.Value.OwnerId, deep.WorldY, deep.SpaceId));
    Assert.Equal(new MapRecordRef("shaft-link", new("cave-floor", 0, 0)), deep.Via);
    Assert.Equal(MapSupportStatus.MissingGeometry, Select(CaveFixtures.WithoutOpening("shaft-top"), Req(3.5f, 5.0f, 5.5f, 0.5f, 1f)).Status);
}
public void LegacyFallbackIsTaggedNonCapture()
{
    MapDocument doc = SupportFixtures.LegacyFallback();
    Assert.Empty(MapSpaceCoverageValidator.Validate(CaveFixtures.View(doc)));                                         // the fixture passes the same validator
    MapSupportResult r = Select(doc, Req(1.5f, 5.0f, -0.5f, 0.5f, 10f));
    Assert.Equal((MapSupportStatus.LegacyFallback, 3.0f, false, (MapFaceKey?)null, (Vector3?)null), (r.Status, r.WorldY, r.IsCaptureSupport, r.Face, r.Normal));
    Assert.Equal(new MapLegacyCellTag(new("plane-0", 0, 0), 1, MapLegacyExteriorRecipe.PolicyId), r.Compatibility);
    Assert.Equal(MapSupportStatus.NoSupport, Select(doc, Req(1.5f, 5.0f, -0.5f, 0.5f, 1f)).Status);                   // 3 m lies below the 4 m to 5.5 m interval
    MapSupportRequest req = Req(1.5f, 5.0f, -0.5f, 0.5f, 10f);
    MapSupportCandidateSet set = new MapSupportQuery(SupportFixtures.Acquire(doc, req.Point)).EnumerateCandidates(req, new MapSupportCandidate[4]);
    Assert.Equal((MapSupportStatus.LegacyFallback, 0, 0), (set.Status, set.Count, set.RequiredCapacity));            // never a capture candidate
}
public void LegacyExterior_ValidatorMembershipAndSupportAgreeAndRefuseOutsideTheRecipe()
{
    static (MapMembershipStatus, MapSupportStatus) At(MapDocument doc, float x) =>
        (CaveFixtures.Query(doc, x, 5f, -0.5f).Status, Select(doc, Req(x, 5f, -0.5f, 0.5f, 10f)).Status);
    static (MapMembershipStatus, MapSupportStatus) From(IMapSurfaceSource source)
    {
        MapScopedSurfaces s = ScopeFixtures.Acquire(source, ScopeFixtures.Around(WorldFrame.Origin, 1.5f, -0.5f, half: 2));
        return (new MapSpaceMembership(s).Query(new(WorldFrame.Origin, new(1.5f, 5f, -0.5f))).Status, new MapSupportQuery(s).Select(Req(1.5f, 5f, -0.5f, 0.5f, 10f)).Status);
    }
    MapDocument tagged = LegacyExteriorFixtures.Row("tagged");
    Assert.Empty(MapSpaceCoverageValidator.Validate(CaveFixtures.View(tagged)));
    Assert.Equal(new[] { (MapMembershipStatus.Resolved, MapSupportStatus.Supported), (MapMembershipStatus.Resolved, MapSupportStatus.LegacyFallback), (MapMembershipStatus.Resolved, MapSupportStatus.LegacyFallback) },
        new[] { 0.5f, 1.5f, 2.5f }.Select(x => At(tagged, x)));
    Assert.Equal(4.5f, Select(tagged, Req(2.5f, 5f, -0.5f, 0.5f, 10f)).WorldY);
    Assert.Equal(CaveFixtures.Query(tagged, 2.5f, 5f, -0.5f).LowerCompatibility, Select(tagged, Req(2.5f, 5f, -0.5f, 0.5f, 10f)).Compatibility);
    Assert.Equal((MapMembershipStatus.MissingGeometry, MapSupportStatus.MissingGeometry), At(LegacyExteriorFixtures.Row("hole"), 3.5f));
    Assert.Equal((MapMembershipStatus.MissingGeometry, MapSupportStatus.MissingGeometry), At(LegacyExteriorFixtures.Row("untagged"), 1.5f));
    foreach (string variant in new[] { "cave", "upper-tag", "foreign-lattice" })
        Assert.Equal((MapMembershipStatus.Invalid, MapSupportStatus.Invalid), At(LegacyExteriorFixtures.Row(variant), 1.5f));
    string dir = SurfaceStorageFixtures.SaveToTemp(tagged);
    var key = new MapPatchKey("plane-0", 0, 0);
    Assert.Equal((MapMembershipStatus.Resolved, MapSupportStatus.LegacyFallback), From(MapStoredSurfaceSource.Open(dir)));
    Assert.Equal((MapMembershipStatus.MissingGeometry, MapSupportStatus.MissingGeometry), From(new FilteredSurfaceSource(MapStoredSurfaceSource.Open(dir), key)));   // unloaded
    SurfaceStorageFixtures.FlipPayloadByte(dir, key);
    Assert.Equal((MapMembershipStatus.MissingGeometry, MapSupportStatus.MissingGeometry), From(MapStoredSurfaceSource.Open(dir)));                                // corrupt
    SurfaceStorageFixtures.DeletePayload(dir, key);
    Assert.Equal((MapMembershipStatus.MissingGeometry, MapSupportStatus.MissingGeometry), From(MapStoredSurfaceSource.Open(dir)));                                // missing
    var untaggedDigest = MapSurfaceSemantics.PatchDigest(LegacyExteriorFixtures.Row("untagged").Surfaces.Patches[key]);
    Assert.NotEqual(untaggedDigest, MapSurfaceSemantics.PatchDigest(tagged.Surfaces.Patches[key]));                   // the policy tag is semantic identity
}
public void LegacyFallback_UsesCellRelativeExactFractionsWithoutAWorldFloatRoundTrip()
{
    MapSupportRequest near = Req(1.1f, 5f, -0.5f, 0.5f, 10f);
    MapSupportRequest far = near with { Point = new MapFramePoint(new WorldFrame(250, 0), new(1.1f, 5f, -0.5f)) };     // anchor x 32,000 m
    MapSupportResult a = Select(LegacyExteriorFixtures.Row("tagged"), near), b = Select(LegacyExteriorFixtures.Row("tagged", slotX: 500), far);
    Assert.Equal((MapSupportStatus.LegacyFallback, MapSupportStatus.LegacyFallback), (a.Status, b.Status));
    Assert.Equal(BitConverter.SingleToInt32Bits(a.WorldY), BitConverter.SingleToInt32Bits(b.WorldY));
    Assert.Equal(BitConverter.SingleToInt32Bits(MapLegacyBilinear.Evaluate(100, 300, 200, 600, 1.1f - 1f, 0.5f)), BitConverter.SingleToInt32Bits(a.WorldY));
    Assert.NotEqual(BitConverter.SingleToInt32Bits(MapLegacyBilinear.Evaluate(100, 300, 200, 600, 32001.1f - 32001f, 0.5f)), BitConverter.SingleToInt32Bits(b.WorldY));   // a world-float round trip samples fx 0.099609375
}
public void CanonicalEntranceSeamAndAperture_Agree()
{
    MapDocument cave = CaveFixtures.RampChamberShaft();
    Assert.Empty(MapSeamValidator.Validate(CaveFixtures.Record<MapSurfaceSeam>(cave, "rim-seam"), CaveFixtures.View(cave)));
    MapCompiledPatch outer = CaveFixtures.Compile(cave, "outer"), floor = CaveFixtures.Compile(cave, "cave-floor");
    for (int x = 2; x <= 6; x++)
        Assert.Equal(CompilerFixtures.ExactAt(outer, "outer", MapLatticeAddress.Corner(x, 2)), CompilerFixtures.ExactAt(floor, "cave-floor", MapLatticeAddress.Corner(x, 2)));
    Assert.DoesNotContain(outer.Faces, f => f.Key.Primitive >= 2 * 64 + 2 && f.Key.Primitive <= 2 * 64 + 5);
    Assert.Empty(outer.LegacyFallbackCells);
    Assert.Equal("cave-floor", Select(cave, Req(3.5f, 9.5f, 2.5f, 0.5f, 1f)).Face!.Value.OwnerId);
}
public void TinyLocalCoordinate_SupportIsNotRepresentable()
    => Assert.Equal(MapSupportStatus.NotRepresentable, Select(CaveFixtures.RampChamberShaft(), Req(1e-30f, 10.5f, 4.5f, 0.5f, 1f)).Status);

// SupportWorkTests (MapEditor.Tests, internal work counters)
public void Support_ReadsOneQueryContextWithoutClones()
{
    var room = BoundFaceContextFixtures.EightByEight();
    var work = new MapBoundFaceWork();
    var request = new MapSupportRequest(new MapFramePoint(WorldFrame.Origin, new(2.5f, 0.5f, 3.5f)), null, null, null, 1f, 1f, null);
    MapSupportResult r = new MapSupportQuery(room.View).Select(request, work);
    Assert.Equal((MapSupportStatus.Supported, 0f), (r.Status, r.WorldY));
    Assert.Equal((1, 2, 4L, 0), (work.ContextsPrepared, work.Compiles, work.CompiledFaces, room.View.PatchClones));   // membership shares the support context
}

// ResolverV2Tests
public void ResolverV2_ExplicitYIsUnchangedAndBindingsResolve()
{
    MapDocument doc = SupportFixtures.CaveWithPlacements();
    MapSupportedResolution r = MapResolverV2.Resolve(doc, SupportFixtures.Assets(), MapDocumentSurfaceSource.Capture(doc), SupportFixtures.V2);
    Assert.Equal(new[] { ("p-explicit", 12.25f), ("p-space", 4.0f), ("p-surface", 5.0f) },
        r.Document.Placements.Select(p => (p.PlacementId, p.Transform.Position.Y)).OrderBy(t => t.PlacementId, StringComparer.Ordinal));
}
public void ResolverV2_AmbiguousBindingRefusesNamingThePlacement()
{
    MapDocument doc = SupportFixtures.EqualSlabsWithBinding();
    string m = Assert.Throws<MapDocumentException>(() => MapResolverV2.Resolve(doc, SupportFixtures.Assets(), MapDocumentSurfaceSource.Capture(doc), SupportFixtures.V2)).Message;
    Assert.Contains("p-amb", m);
    Assert.Contains("Ambiguous", m);
}
public void ResolverAdoption_RefusesUntilEveryMissingYPlacementIsConverted()
{
    MapDocument v1 = MapDocumentFile.Load(FormatFourFixtures.MonolithicPath);
    var onlyA = new Dictionary<string, MapPlacementSupportChoice> { ["a"] = new(2.5f, null) };
    Assert.Contains("'c'", Assert.Throws<MapDocumentException>(() => MapResolverAdoption.ConvertToAuthoredSupport(v1, onlyA)).Message);
    MapDocument window = MapDocumentFile.LoadTiled(FormatFourFixtures.CopyTiledToTemp(), new MapTileRect(new(0, 0), new(0, 0)));
    Assert.Contains("window", Assert.Throws<MapDocumentException>(() => MapResolverAdoption.ConvertToAuthoredSupport(window, onlyA)).Message);
    MapDocument adopted = MapResolverAdoption.ConvertToAuthoredSupport(v1, new Dictionary<string, MapPlacementSupportChoice> { ["a"] = new(2.5f, null), ["c"] = new(7.5f, null) });
    Assert.Equal((new MapResolverIdentityDoc(1, 2), MapSupportRecipe.AuthoredBindingsV2), (adopted.ResolverIdentity, adopted.SupportRecipe));
    Assert.Equal(new float?[] { 2.5f, 2.5f, 7.5f }, adopted.Placements.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => p.Y));
    Assert.Equal(new MapResolverIdentityDoc(1, 1), v1.ResolverIdentity);
}
```

The legacy point (1.5, -0.5) is the centre of cell 1 on a `NegativeZ` frame, so the bilinear height is `(100 + 300 + 200 + 600) / 4 = 300` cm, and every corner product `cm * 0.01f` rounds to the whole metre, so the float result is exactly 3.0. Cell 2 gives 4.5 m. In the far case `WorldFrame(250, 0)` anchors at x 32,000 m, so local x 1.1 lands in cell 1 of slot 500 with the same exact fraction `1.1f - 1` as the near case. The float `32001.1f` is 32001.099609375, so a path that formed the world float would sample a different fraction.

- [ ] **Step 2: Run red.** `wa_test t13-red "$MAPDOC" "FullyQualifiedName~SupportQueryTests|FullyQualifiedName~ResolverV2Tests"`. Expected: build FAIL naming `MapSupportQuery`.
- [ ] **Step 3: Implement the query, resolver and adoption.**
- [ ] **Step 4: Run green.** `wa_test t13-green "$MAPDOC" "FullyQualifiedName~SupportQueryTests|FullyQualifiedName~ResolverV2Tests|FullyQualifiedName~NativeResolverTests|FullyQualifiedName~FormatAdvanceTests|FullyQualifiedName~SpaceMembershipTests"`. Expected: PASS, 12 new tests plus the existing resolver, format-advance and membership cases. Then `wa_test t13-green-editor "$EDITOR" "FullyQualifiedName~SupportWorkTests|FullyQualifiedName~CoverageWorkTests"`. Expected: PASS, 1 new case plus the 2 Task 12B work cases.
- [ ] **Step 5: Commit** `KhaozEngine.MapDoc/Support KhaozEngine.MapDoc.Tests/Support KhaozEngine.MapEditor.Tests/MapDoc/SupportWorkTests.cs`, message `feat(mapdoc): select height-aware support and resolve version-2 bindings`.

---

### Task 14: Precision envelope proofs and frame-local meshes

**Files:**
- Create: `KhaozEngine.MapDoc/Surfaces/MapFrameLocal.cs`
- Test: `KhaozEngine.MapDoc.Tests/Precision/SparseFarAndDeepGeometryTests.cs`, fixture `PrecisionFixtures.cs`

**Interfaces (new):**
- `public sealed class MapFrameMesh` with `WorldFrame Frame`, `IReadOnlyList<Vector3> LocalPositions`, `IReadOnlyList<MapCompiledFace> Faces`.
- `public static class MapFrameLocal` with `ToFrame(MapCompiledPatch, WorldFrame)`, `ToFrame(MapCompiledStrip, WorldFrame)` and `CompileInFrame(MapCompiledPatch local, MapTransform placement, WorldFrame frame)`.
- `ToFrame` route: `dx = anchor.X - frame.X * 128` and `dz = anchor.Z - frame.Z * 128` in `long`, converted once (exact, the frame radius bounds them), then `local = (float)dx + offset.X`, one rounding. Y is `(float)anchor.Y + offset.Y`, world-datum, one rounding. Any local XZ magnitude beyond `WorldFrame.MaxLocalRadius` refuses with `frame radius`.
- `CompileInFrame` route, the approved local-precision path: each vertex's local position is the exact `ExactVertices` value of the prefab patch (near its own origin), yaw and positive scale are applied in `double`, the reduced translation `t = (double)placement.Position - frame.Anchor` is exact (float to double is exact, the anchor is a whole multiple of 128 within the frame radius), and the sum is rounded to `float` once. Y keeps the world datum: `(double)placement.Position.Y + scaled local Y`, rounded once, which is where the named vertical risk applies. It never forms `float(frameAnchor + local)`. R1 `MapTransform.TransformPoint` and `Compose` are unchanged.

**Fixture.** `PrecisionFixtures.Probe(long slotX, long slotZ, int baseCm)` is a document with surface `probe`, `(1/3, 1/100, PositiveZ)`, `Native`, 4 by 4 cells, corners `baseCm + 10x + 3z`, Exterior `air` (OpenTop). Slot 1500 is lattice 96,000, which is 32,000 m at 1/3 m, and slot 1506 is 32,128 m, the envelope plus 128 m padding, with frames 250 and 251 anchored there. `Deck()` is surface `deck`, `(1/1, 1/100, PositiveZ)`, 2 by 2 cells at 0. `Compile(doc)` compiles the single patch. `ExactQueryHeight(doc, frame, local) -> MapExactValue` converts the float local point's exact value to rationals and returns the exact canonical height. `ReferenceInFrame(deck, placement, frame) -> IReadOnlyList<(double X, double Y, double Z)>` computes every vertex in `double` from exact inputs, unrounded, in `LocalPositions` order. `Select(doc, frame, local, up, down)` acquires `Around(frame, local.X, local.Z, half: 2)` and selects.

- [ ] **Step 1: Write the failing tests**

```csharp
static bool Within(double a, double b) => Math.Abs(a - b) <= 0.0001;

[Theory, InlineData(50000, 1500, 250), InlineData(-50000, -1500, -250), InlineData(50000, 1506, 251), InlineData(-50000, -1506, -251)]
public void SparseFarAndDeepGeometry_PreservesLocalCoordinates(int baseCm, long slot, short frameIndex)
{
    MapDocument near = PrecisionFixtures.Probe(0, 0, baseCm), far = PrecisionFixtures.Probe(slot, slot, baseCm);
    var frame = new WorldFrame(frameIndex, frameIndex);
    MapFrameMesh a = MapFrameLocal.ToFrame(PrecisionFixtures.Compile(near), WorldFrame.Origin), b = MapFrameLocal.ToFrame(PrecisionFixtures.Compile(far), frame);
    Assert.Equal(a.LocalPositions, b.LocalPositions);
    Assert.Equal(a.Faces.Select(f => f.Normal), b.Faces.Select(f => f.Normal));
    var local = new Vector3(0.4f, baseCm / 100f + 0.5f, 0.6f);
    foreach (var (doc, f) in new[] { (near, WorldFrame.Origin), (far, frame) })
    {
        MapSupportResult r = PrecisionFixtures.Select(doc, f, local, up: 0.5f, down: 1f);
        Assert.Equal(MapSupportStatus.Supported, r.Status);
        Assert.True(Within(r.WorldY, PrecisionFixtures.ExactQueryHeight(doc, f, local).ToDouble()));
    }
    Assert.Contains("frame radius", Assert.Throws<MapDocumentException>(() => MapFrameLocal.ToFrame(PrecisionFixtures.Compile(far), WorldFrame.Origin)).Message);
}
[Theory, InlineData(0.8f, 563.5f), InlineData(1.2f, 563.5f), InlineData(0.8f, -563.5f), InlineData(1.2f, -563.5f)]
public void RigidLocalFloor_ComposesTheSameInNearAndFarFrames(float scale, float y)
{
    MapCompiledPatch deck = PrecisionFixtures.Compile(PrecisionFixtures.Deck());
    var frame = new WorldFrame(250, 250);
    var farPose = new MapTransform(new Vector3(32010.25f, y, 31996.5f), 0.371f, scale);
    MapFrameMesh a = MapFrameLocal.CompileInFrame(deck, new MapTransform(new Vector3(10.25f, y, -3.5f), 0.371f, scale), WorldFrame.Origin);
    MapFrameMesh b = MapFrameLocal.CompileInFrame(deck, farPose, frame);
    Assert.Equal(a.LocalPositions, b.LocalPositions);
    Assert.Contains(new Vector3(10.25f, y, -3.5f), a.LocalPositions);                 // the deck's local origin vertex
    var reference = PrecisionFixtures.ReferenceInFrame(deck, farPose, frame);
    for (int i = 0; i < reference.Count; i++)
        Assert.True(Within(b.LocalPositions[i].X, reference[i].X) && Within(b.LocalPositions[i].Y, reference[i].Y) && Within(b.LocalPositions[i].Z, reference[i].Z));
}
public void ProbeAt640Metres_MeetsTheTargetAndTheNamedRiskIsRecorded()
{
    MapDocument probe = PrecisionFixtures.Probe(1500, 1500, 50000);
    var frame = new WorldFrame(250, 250);
    var local = new Vector3(0.4f, 639.9f, 0.6f);
    MapSupportResult r = PrecisionFixtures.Select(probe, frame, local, up: 0f, down: 141f);
    Assert.True(Within(r.WorldY, PrecisionFixtures.ExactQueryHeight(probe, frame, local).ToDouble()));
    Assert.Equal(0.00006103515625f, MathF.BitIncrement(640f) - 640f);
    Assert.Equal(0.000030517578125f, (MathF.BitIncrement(640f) - 640f) / 2);
    Assert.True(Math.Abs((32000.01f - 32000f) - 0.01) <= 0.001);
}
```

A failing precision assertion is reported with its inputs and the remedy in Global Constraints. The test is never weakened.

- [ ] **Step 2: Run red.** `wa_test t14-red "$MAPDOC" "FullyQualifiedName~SparseFarAndDeepGeometryTests"`. Expected: build FAIL naming `MapFrameLocal`.
- [ ] **Step 3: Implement frame-local conversion.**
- [ ] **Step 4: Run green.** `wa_test t14-green "$MAPDOC" "FullyQualifiedName~SparseFarAndDeepGeometryTests"`. Expected: PASS, 9 cases.
- [ ] **Step 5: Commit** `KhaozEngine.MapDoc/Surfaces/MapFrameLocal.cs KhaozEngine.MapDoc.Tests/Precision`, message `test(mapdoc): prove frame-local surface precision across the sparse envelope`.

---

### Task 15: Canonical relation and portal facts for F3 and Grimhollow #458

**Files:**
- Create: `KhaozEngine.MapDoc/Spaces/MapSpaceRelations.cs`, `MapRelationFacts.cs`
- Test: `KhaozEngine.MapDoc.Tests/Spaces/SpaceRelationFactsTests.cs`, fixture `RelationFixtures.cs`

**Interfaces (new, `KhaozEngine.MapDoc.Spaces`):**
- `public enum MapRelationStatus { Resolved, MissingGeometry, Ambiguous, CapacityExceeded, NotRepresentable, Invalid }`, `public enum MapGeometricRelation { SameSpace, ConnectedThroughPortals, ConnectedThroughVerticalLink, NotConnectedWithinBound, Undetermined }`, `public enum MapPortalKind : byte { WallPortal, HorizontalOpening }`, `public enum MapPortalStateSource : byte { AuthoredOpen }` (the only state R2 supplies), `public enum MapPhysicalCertainty : byte { NotEvaluated, Clear, Blocked, Unknown }` (R2 always `NotEvaluated`).
- `public sealed record MapApertureColumn(MapLatticeVertex Vertex, MapExactPoint Bottom, MapExactPoint? Top)` (exact world points, null top only for an open-top exterior pair), `public sealed record MapApertureGeometry(MapPortalKind Kind, IReadOnlyList<MapApertureColumn> Columns, IReadOnlyList<MapExactTriangle> PlaneTriangles, IReadOnlyList<MapRecordRef> Provenance)`. Wall portals fill `Columns` in interval order from the band chains. Openings fill `PlaneTriangles` from `MapOpeningBoundary`. `Provenance` lists the portal or opening, its chains and its link, each with its anchor, and every one is also in the witness.
- `public sealed record MapApertureSummary(float BottomY, float? TopY, float? MinClearHeight, float Width)`, derived from the exact geometry for convenience only.
- `public sealed record MapPortalFact(MapRecordRef Record, MapPortalKind Kind, MapRecordRef FromSpace, MapRecordRef ToSpace, MapRecordRef? ViaLink, MapApertureGeometry Geometry, MapApertureSummary Summary, MapPortalStateSource State)`.
- `public sealed record MapRelationQuery(int MaxPortalHops = 8, bool IncludeVerticalLinks = true)`.
- `public sealed class MapRelationResult` (factory-only) with `Status`, `MapFramePoint A`, `MapFramePoint B`, `MapRelationQuery Query`, `MapMembershipResult MembershipA`, `MapMembershipResult MembershipB`, `MapGeometricRelation Relation`, `IReadOnlyList<MapPortalFact> Path`, `MapPhysicalCertainty Occlusion`, `MapPhysicalCertainty Clearance`, `string? Detail`, `MapReadWitness Witness`. It carries the exact framed endpoints and query that R3's planned request (D5) consumes.
- `public sealed class MapSpaceRelations { public MapSpaceRelations(MapScopedSurfaces scoped); public MapRelationResult Relate(MapFramePoint a, MapFramePoint b, MapRelationQuery query); }`. Both points resolve membership in the same acquired scope. The same space id is `SameSpace` whatever their supports. Otherwise a deterministic breadth-first search over acquired wall portals and, when allowed, acquired openings (an opening joins the space whose lower bound it is with the space whose upper bound it is), ordered by record id, bounded by `MaxPortalHops`. Space boundary lists are never expanded. A path using an opening is `ConnectedThroughVerticalLink` with its link. Exhausting the bound or the acquired set is `NotConnectedWithinBound`, never a claim of disconnection. A needed record that is unavailable gives `MissingGeometry` with `Undetermined` and a detail naming it. An unrepresentable endpoint gives `NotRepresentable` with `Undetermined`. An endpoint whose membership is `Invalid` (a D9 recipe violation) gives `Invalid` with `Undetermined`. A membership carrying `LowerCompatibility` is used as is, and no relation fact claims physical support or clearance from it. Results carry the acquisition's witness. No eligibility, hearing, visibility or interaction member, floor id, global Y threshold or support-key comparison exists.

| #458 consumer case (pivot policy at G3.4) | Producer facts read from R2 | Still required from R3 before native adoption |
| --- | --- | --- |
| Remote chop, bench and blow sounds | Membership and `DomainKeys` of listener and source, relation, portal path with exact apertures | Occlusion over the caller's segment under the same witness, and R3's decision on live portal state |
| Tree-fall sound | As above for the tree's base point | Same |
| Dialogue and shop range | Membership of both actors, relation | Clearance certainty over a caller-supplied path |
| Follow | Relation and portal path for route intent | Navigation route (R3 #1301) supplies any path |
| Target selection | Membership, relation | Pick and LOS (R3) |

Existing game distance and intent rules are untouched. No acoustic propagation is added. Connectivity alone grants no hearing, visibility or interaction.

**Fixture.** `RelationFixtures.Relate(MapDocument doc, Vector3 a, Vector3 b, MapRelationQuery? q = null, IMapSurfaceSource? source = null)` acquires one origin-frame scope `Around(WorldFrame.Origin, 4, 4, half: 8)` over `Capture(doc)` or `source` and relates the two origin-frame points. An overload adds `out MapScopedSurfaces acquired`. `RelateIn(IMapSurfaceSource source, MapSurfaceScope scope, Vector3 a, Vector3 b)` relates points in `scope.Frame`.

- [ ] **Step 1: Write the failing tests**

```csharp
public void ConnectedDistinctSupports_AreTheSameSpace()
{
    MapDocument cave = CaveFixtures.RampChamberShaft();
    MapRelationResult r = RelationFixtures.Relate(cave, new(1.5f, 10.5f, 1.5f), new(3.5f, 9.5f, 2.5f));
    Assert.Equal((MapRelationStatus.Resolved, MapGeometricRelation.SameSpace), (r.Status, r.Relation));
    Assert.Equal(("outer", "cave-floor"), (SupportFixtures.Select(cave, SupportFixtures.Request(1.5f, 10.5f, 1.5f, 0.5f, 1f)).Face!.Value.OwnerId,
                                           SupportFixtures.Select(cave, SupportFixtures.Request(3.5f, 9.5f, 2.5f, 0.5f, 1f)).Face!.Value.OwnerId));
}
public void StackedSpacesSharingXz_ConnectOnlyThroughTheirPortal()
{
    MapRelationResult r = RelationFixtures.Relate(CaveFixtures.RampChamberShaft(), new(2.5f, 10.5f, 4.5f), new(2.5f, 5.5f, 4.5f), out MapScopedSurfaces acquired);
    Assert.Equal(("outside", "chamber", MapGeometricRelation.ConnectedThroughPortals), (r.MembershipA.SpaceId, r.MembershipB.SpaceId, r.Relation));
    Assert.Equal((new Vector3(2.5f, 10.5f, 4.5f), new Vector3(2.5f, 5.5f, 4.5f)), (r.A.Local, r.B.Local));
    MapPortalFact mouth = Assert.Single(r.Path);
    Assert.Equal((new MapRecordRef("mouth", new("cave-floor", 0, 0)), MapPortalStateSource.AuthoredOpen), (mouth.Record, mouth.State));
    Assert.Equal(Enumerable.Range(2, 5).Select(x => (new MapExactPoint(new(x, 1), new(8, 1), new(3, 1)), (MapExactPoint?)new MapExactPoint(new(x, 1), new(9, 1), new(3, 1)))),
        mouth.Geometry.Columns.Select(c => (c.Bottom, c.Top)));
    Assert.Equal((8.0f, (float?)9.0f, (float?)1.0f, 4.0f), (mouth.Summary.BottomY, mouth.Summary.TopY, mouth.Summary.MinClearHeight, mouth.Summary.Width));
    Assert.All(mouth.Geometry.Provenance, p => Assert.Contains(p, acquired.Witness.Records));
    Assert.Equal((MapPhysicalCertainty.NotEvaluated, MapPhysicalCertainty.NotEvaluated), (r.Occlusion, r.Clearance));
    Assert.Equal(MapGeometricRelation.NotConnectedWithinBound,
        RelationFixtures.Relate(CaveFixtures.RampChamberShaft(), new(2.5f, 10.5f, 4.5f), new(2.5f, 5.5f, 4.5f), new MapRelationQuery(MaxPortalHops: 0)).Relation);
}
public void ShaftConnectsThroughTheVerticalLink()
{
    MapDocument cave = CaveFixtures.RampChamberShaft();
    MapRelationResult r = RelationFixtures.Relate(cave, new(2.5f, 5.5f, 6.5f), new(2.5f, -19f, 6.5f));
    Assert.Equal(MapGeometricRelation.ConnectedThroughVerticalLink, r.Relation);
    Assert.Equal(new[] { "shaft-top", "shaft-bottom" }, r.Path.Select(p => p.Record.Id));
    Assert.All(r.Path, p => Assert.Equal((new MapRecordRef("shaft-link", new("cave-floor", 0, 0)), 8), (p.ViaLink!, p.Geometry.PlaneTriangles.Count)));
    Assert.Equal(MapGeometricRelation.NotConnectedWithinBound,
        RelationFixtures.Relate(cave, new(2.5f, 5.5f, 6.5f), new(2.5f, -19f, 6.5f), new MapRelationQuery(IncludeVerticalLinks: false)).Relation);
}
public void FarAnchoredPortal_SuppliesExactApertureFromAnOutOfWindowChain()
{
    string dir = AnchorFixtures.FarAnchors();
    MapSurfaceScope scope = ScopeFixtures.Around(AnchorFixtures.FarFrame, 2, 2, half: 1.5f);
    MapRelationResult r = RelationFixtures.RelateIn(MapStoredSurfaceSource.Open(dir), scope, new(0.5f, 10.5f, 1.5f), new(2.5f, 12f, 1.5f));
    Assert.Equal(MapGeometricRelation.ConnectedThroughPortals, r.Relation);
    MapPortalFact door = Assert.Single(r.Path);
    Assert.Equal("door", door.Record.Id);
    Assert.Equal(Enumerable.Range(0, 5).Select(z => (new MapExactPoint(new(2562, 1), new(10, 1), new(z, 1)), (MapExactPoint?)new MapExactPoint(new(2562, 1), new(14, 1), new(z, 1)))),
        door.Geometry.Columns.Select(c => (c.Bottom, c.Top)));
    Assert.Contains(new MapRecordRef("door-top", new("roof", 41, 0)), door.Geometry.Provenance);
    SurfaceStorageFixtures.DeletePayload(dir, new("roof", 41, 0));
    MapRelationResult missing = RelationFixtures.RelateIn(MapStoredSurfaceSource.Open(dir), scope, new(0.5f, 10.5f, 1.5f), new(2.5f, 12f, 1.5f));
    Assert.Equal((MapRelationStatus.MissingGeometry, MapGeometricRelation.Undetermined), (missing.Status, missing.Relation));
    Assert.Contains("door-top", missing.Detail);
}
public void MissingAndAmbiguousScopesStayUnresolved()
{
    MapDocument cave = CaveFixtures.RampChamberShaft();
    var filtered = new FilteredSurfaceSource(MapDocumentSurfaceSource.Capture(cave), new MapPatchKey("cave-ceiling", 0, 0));
    MapRelationResult m = RelationFixtures.Relate(cave, new(2.5f, 10.5f, 4.5f), new(2.5f, 5.5f, 4.5f), source: filtered);
    Assert.Equal((MapRelationStatus.MissingGeometry, MapGeometricRelation.Undetermined), (m.Status, m.Relation));
    Assert.Equal(MapRelationStatus.Ambiguous, RelationFixtures.Relate(CaveFixtures.WithPeerChamber(null), new(2.5f, 10.5f, 4.5f), new(2.5f, 5.5f, 4.5f)).Status);
}
public void OneAcquisition_SharesOneWitnessAcrossMembershipSupportAndRelations()
{
    MapScopedSurfaces s = ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(CaveFixtures.RampChamberShaft()), ScopeFixtures.Around(WorldFrame.Origin, 3, 4, half: 6));
    var p = new MapFramePoint(WorldFrame.Origin, new(2.5f, 5.5f, 4.5f));
    Assert.Same(s.ReadWitness, new MapSpaceMembership(s).Query(p).Witness);
    Assert.Same(s.ReadWitness, new MapSupportQuery(s).Select(new MapSupportRequest(p, null, null, null, 0.5f, 1f, null)).Witness);
    Assert.Same(s.ReadWitness, new MapSpaceRelations(s).Relate(p, new MapFramePoint(WorldFrame.Origin, new(2.5f, 10.5f, 4.5f)), new()).Witness);
    Assert.Throws<ArgumentException>(() => new MapSpaceMembership(s).Query(new MapFramePoint(new WorldFrame(1, 0), p.Local)));
}
public void Witness_IsStableForOneSnapshotAndChangesWithGeometry()
{
    MapDocument cave = CaveFixtures.RampChamberShaft();
    MapDocumentSurfaceSource src = MapDocumentSurfaceSource.Capture(cave);
    MapSurfaceScope scope = ScopeFixtures.Around(WorldFrame.Origin, 3, 4, half: 6);
    Assert.Equal(ScopeFixtures.Acquire(src, scope).ReadWitness.ScopedDigest, ScopeFixtures.Acquire(src, scope).ReadWitness.ScopedDigest);
    cave.Surfaces.Patches[new("cave-ceiling", 0, 0)].Heights[0] = 905;
    Assert.NotEqual(ScopeFixtures.Acquire(src, scope).ReadWitness.ScopedDigest,
                    ScopeFixtures.Acquire(MapDocumentSurfaceSource.Capture(cave), scope).ReadWitness.ScopedDigest);
}
public void RelationFacts_ExposeNoEligibilityPolicy()
{
    string[] banned = { "CanHear", "CanSee", "CanInteract", "IsAudible", "IsVisible", "FloorId" };
    var members = typeof(MapSpaceRelations).Assembly.GetExportedTypes()
        .Where(t => t.Namespace is "KhaozEngine.MapDoc.Spaces" or "KhaozEngine.MapDoc.Support")
        .SelectMany(t => t.GetMembers()).Select(m => m.Name);
    Assert.DoesNotContain(members, banned.Contains);
}
public void QueryResults_AreFactoryOnly()
{
    foreach (Type t in new[] { typeof(MapMembershipResult), typeof(MapSupportResult), typeof(MapSupportCandidateSet), typeof(MapRelationResult) })
    {
        Assert.Empty(t.GetConstructors());
        Assert.DoesNotContain(t.GetProperties(), p => p.SetMethod is { IsPublic: true });
    }
}
```

- [ ] **Step 2: Run red.** `wa_test t15-red "$MAPDOC" "FullyQualifiedName~SpaceRelationFactsTests"`. Expected: build FAIL naming `MapSpaceRelations`.
- [ ] **Step 3: Implement relation facts.**
- [ ] **Step 4: Run green.** `wa_test t15-green "$MAPDOC" "FullyQualifiedName~SpaceRelationFactsTests"`. Expected: PASS, 9 tests.
- [ ] **Step 5: Commit** `KhaozEngine.MapDoc/Spaces KhaozEngine.MapDoc.Tests/Spaces/SpaceRelationFactsTests.cs KhaozEngine.MapDoc.Tests/Spaces/RelationFixtures.cs`, message `feat(mapdoc): expose bounded relation facts with exact portal apertures`.

After the controller accepts this task, it sends the exact producer signatures above and D5's R3 plan input to pivot (#458) and swimming (F3) as R2 contract facts, marked as unreleased until the owner tag. Their consumers remain blocked on R3 facts, released pins and adoption.

---

### Task 16: General native document transaction seam

**Files:**
- Create: `KhaozEngine.MapEditor/NativeDocumentTransaction.cs`, `KhaozEngine.MapDoc/Editing/MapNativeEditEffects.cs`
- Modify: `KhaozEngine.MapEditor/NativePlacementTransaction.cs:16-32` (delegate to the general seam, same signature), `NativeDocumentSnapshot.cs:8-14` (deep-copy `Surfaces` with `MapSurfaceSet.Clone()` and serialize only the surface-free projection, so a tiled whole document over 256 patches can still be cloned), `NativeDocumentSnapshot.cs:16-39` (`PublishWriteSet`), `EditorHistory.cs:28-34`, `EditorDocument.cs` (`LastNativeEffects`), `KhaozEngine.MapEdit.Tool/MapEditSession.cs:48-49` (`ApplyNative` returns effects)
- Test: `KhaozEngine.MapEditor.Tests/MapDoc/NativeDocumentTransactionTests.cs`, fixture `TransactionFixtures.cs`

**Interfaces:**
- Produces (MapDoc, public): `[Flags] public enum MapNativeInvalidation { None = 0, Terrain = 1, Physics = 2, Nav = 4, Material = 8, Residency = 16, Placements = 32 }`, `public readonly record struct MapBox3(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)`, `public sealed record MapDigestChange(string Key, string? Before, string? After)`, `public sealed record MapNativeEditEffects(MapBox3? OldBounds, MapBox3? NewBounds, IReadOnlyList<MapPatchKey> Patches, IReadOnlyList<string> SpaceIds, IReadOnlyList<string> DependencyIds, IReadOnlyList<MapDigestChange> DigestChanges, MapNativeInvalidation Invalidates)` with `string Describe()` (canonical text).
- Produces (MapEditor, internal): `internal interface INativeDocumentCommand { NativeDocumentPreparation Prepare(MapDocument candidate, bool undo); }`, `internal sealed record NativeDocumentPreparation(MapNativeWriteSet WriteSet, MapNativeEditEffects Effects, Action Accept)`, `internal static class NativeDocumentTransaction { internal static MapNativeEditEffects Run(MapDocument document, IEditorCommand command, bool undo, MapAssetClosure? assets, MapDocRegistry? registry = null, bool localOnly = false); }`. Existing `INativePlacementCommand` implementations run through an adapter producing `MapNativeWriteSet.PlacementsOnly` and `Invalidates = Placements`.
- Behavior: refuse a partial document first, by the unified `IsPartial`, before the closure check and preparation (`window`). Keep `candidate.Tiles` as the same `MapTileIndex` instance, so the witness and surface completeness survive clone and publish. Prepare on a detached candidate, validate locally or against the bound closure, validate every write-set patch's `ValidateLocal`, seams and corner dependencies over `MapScopedSurfaces.CompleteView`, run `MapSpaceCoverageValidator` when any record or space changed, then publish exactly the write-set members and call `Accept`. A rejection leaves document, history, dirty state, identity, `LastNativeEffects` and retry state unchanged.
- Produces: `public MapNativeEditEffects? EditorDocument.LastNativeEffects { get; }`.

**Fixture.** `TransactionFixtures.NativeWithSurfaces()` opens `SurfaceStorageFixtures.ThreeSurfaces()` in an `EditorDocument` bound to its closure. `State(e)` concatenates `SaveText`, undo and redo labels, `IsDirty`, the scheme-2 token and `LastNativeEffects?.Describe()`. `TestPatchHeightCommand(MapPatchKey key, int index, int value)` is a test-only `EditorCommand, INativeDocumentCommand`. `NativePlacementHistoryFixture.NewProp(string id)` is the existing helper declared at `KhaozEngine.MapEditor.Tests/MapDoc/NativePlacementHistoryFixture.cs:80` at `ca13d62d7` (root verified it exists, its initializer spans lines 80 to 87), and that fixture starts at `NumericIdHighWaterMark = 10`.

- [ ] **Step 1: Write the failing tests**

```csharp
public void PlacementCommands_KeepReleasedBehaviourThroughTheGeneralSeam()
{
    using var f = new NativePlacementHistoryFixture();
    f.Editor.Execute(new AddPlacementCommand(f.NewProp("seam-check")));
    Assert.Equal((11L, MapNativeInvalidation.Placements), (f.Document.Placements.Single(p => p.Id == "seam-check").NumericId, f.Editor.LastNativeEffects!.Invalidates));
    Assert.True(f.Editor.Undo());
    Assert.DoesNotContain(f.Document.Placements, p => p.Id == "seam-check");
    Assert.True(f.Editor.Redo());
    Assert.Equal((11L, 11L), (f.Document.Placements.Single(p => p.Id == "seam-check").NumericId, f.Document.NumericIdHighWaterMark));
}
public void WriteSet_PublishesOnlyItsMembers()
{
    EditorDocument e = TransactionFixtures.NativeWithSurfaces();
    var placements = e.Doc.Placements;
    MapSurfacePatch ridge = e.Doc.Surfaces.Patches[new("ridge", 0, 0)];
    e.Execute(new TestPatchHeightCommand(new("ground", 0, 0), 0, 1100));
    Assert.Same(placements, e.Doc.Placements);
    Assert.Same(ridge, e.Doc.Surfaces.Patches[new("ridge", 0, 0)]);
    Assert.Equal(1100, e.Doc.Surfaces.Patches[new("ground", 0, 0)].Heights[0]);
    Assert.Equal(new[] { new MapPatchKey("ground", 0, 0) }, e.LastNativeEffects!.Patches);
}
public void RejectedCommand_LeavesDocumentHistoryDirtyIdentityAndEffectsUnchanged()
{
    EditorDocument e = TransactionFixtures.NativeWithSurfaces();
    e.Execute(new TestPatchHeightCommand(new("ground", 0, 0), 0, 1100));
    string before = TransactionFixtures.State(e);
    for (int i = 0; i < 2; i++)
    {
        // corner index 4 is cell X 64, the owner corner ground(1,0) depends on
        Assert.Contains("owner", Assert.Throws<MapDocumentException>(() => e.Execute(new TestPatchHeightCommand(new("ground", 0, 0), 4, 1234))).Message);
        Assert.Equal(before, TransactionFixtures.State(e));
    }
}
public void PartialDocument_NativeTerrainCommandRefuses() => TiledDocFixture.InDirectory(dir =>
{
    MapDocumentFile.SaveTiled(SurfaceStorageFixtures.ThreeSurfaces(), dir);
    MapDocument window = SurfaceStorageFixtures.LoadSlotWindow(dir, 300);
    Assert.False(window.Tiles!.HasUnloadedTiles);                                  // surfaces-only window
    var e = new EditorDocument(window);
    Assert.Contains("window", Assert.Throws<MapDocumentException>(() => e.Execute(new TestPatchHeightCommand(new("far", 300, 0), 0, 2100))).Message);
});
```

- [ ] **Step 2: Run red.** `wa_test t16-red "$EDITOR" "FullyQualifiedName~NativeDocumentTransactionTests"`. Expected: build FAIL naming `INativeDocumentCommand` and `LastNativeEffects`.
- [ ] **Step 3: Implement the seam.**
- [ ] **Step 4: Run green.** `wa_test t16-green "$EDITOR" "FullyQualifiedName~NativeDocumentTransactionTests|FullyQualifiedName~NativePlacementHistoryTests|FullyQualifiedName~NativeLifecycleTests|FullyQualifiedName~NativeWindowBindingTests|FullyQualifiedName~MutationServicePlacementTests|FullyQualifiedName~NativeRejections"`. Expected: PASS, 4 new tests plus every existing case.
- [ ] **Step 5: Commit** `KhaozEngine.MapEditor KhaozEngine.MapEdit.Tool/MapEditSession.cs KhaozEngine.MapDoc/Editing/MapNativeEditEffects.cs KhaozEngine.MapEditor.Tests/MapDoc`, message `feat(mapeditor): extend native transactions to typed document write sets`.

---

### Task 17: Terrain edits, commands and the shared service path

**Files:**
- Create: `KhaozEngine.MapDoc/Editing/MapTerrainEdits.cs`, `KhaozEngine.MapEditor/NativeTerrainCommands.cs`, `KhaozEngine.MapEdit.Tool/MutationService.Terrain.cs`
- Test: `KhaozEngine.MapDoc.Tests/Editing/TerrainEditsTests.cs`, `KhaozEngine.MapEditor.Tests/MapEditTool/TerrainTransactionTests.cs`, fixtures `TerrainEditFixtures.cs`, `TerrainTransactionFixture.cs`

**Interfaces (new):**
- `public abstract record MapTerrainEdit(string Label)` with derived records, each passing a fixed label: `MapSetCornerHeights(MapPatchKey Patch, int CornerX, int CornerZ, int Width, int Depth, IReadOnlyList<int> Values)`, `MapSmoothCorners(MapPatchKey Patch, int CornerX, int CornerZ, int Width, int Depth, int Iterations)`, `MapSetCells(MapPatchKey Patch, int CellX, int CellZ, int Width, int Depth, IReadOnlyList<MapSurfaceCell> Cells)`, `MapSetPresence(MapPatchKey Patch, IReadOnlyList<int> SlotCells, bool Present)`, `MapReassignCornerOwner(IReadOnlyList<MapCornerOwnerChange> Changes)`, `MapReanchorRecords(IReadOnlyList<MapRecordAnchorMove> Moves)` with `public sealed record MapRecordAnchorMove(string RecordId, MapPatchKey From, MapPatchKey To)`, `MapReplaceTopology(IReadOnlyList<MapSurfacePatch> UpsertPatches, IReadOnlyList<MapPatchKey> RemovePatches, IReadOnlyList<MapSurfaceRef> UpsertSurfaces, IReadOnlyList<string> RemoveSurfaces)`, `MapConvertFinePatch(MapFinePatchRequest Request)`, `MapCompositeEdit(IReadOnlyList<MapTerrainEdit> Edits)`. Coordinates are patch-local.
- `public sealed record MapTerrainEditResult(MapSurfaceSet Candidate, MapNativeWriteSet WriteSet, MapNativeEditEffects Effects)`, `public static class MapTerrainEdits { public static MapTerrainEditResult Prepare(MapSurfaceSet surfaces, MapTerrainEdit edit); }`. The input set is never mutated.
- Smoothing keeps J2.1: 1 to 64 iterations (`ArgumentOutOfRangeException` before work), double-buffered 3 by 3 average including the centre, halo corners read unchanged from this patch or a `Present` same-surface neighbour, `Math.Round(sum / 9.0, MidpointRounding.AwayFromZero)` per pass. A halo corner outside the surface, unloaded or ambiguous refuses (`halo`).
- Ownership and anchors: an upserted patch touching existing corners records dependencies on their current owners and never takes ownership. Removing or splitting an owner patch without `MapReassignCornerOwner` for every dependent corner refuses (`owner`). Removing a patch that anchors records without `MapReanchorRecords` for each refuses (`anchor`, naming the record). A re-anchor rewrites every `MapRecordRef` to the moved record in the same edit. Reassignment, re-anchoring, dependent references and the identity change publish together.
- Invalidation: heights, smoothing, presence and owner reassignment give `Terrain | Physics | Nav | Residency`, topology replacement and conversion add `Material`, underlay or overlay ID changes alone give `Terrain | Material`, any cut, rotation, topology or flag change gives all five. Re-anchoring alone gives `Residency`.
- `public sealed class TerrainEditCommand : EditorCommand, INativeDocumentCommand` with `TerrainEditCommand(MapTerrainEdit edit)`, capturing exact before and after patch and ref snapshots of its write set on first acceptance.
- `public sealed record MapTerrainMutationResult(MutationResult Mutation, MapNativeEditEffects Effects)`, `public MapTerrainMutationResult MutationService.TerrainApply(MapTerrainEdit edit)` in the new partial file, through the same session lock and seam. MCP verbs stay in R10.

**Fixture.** `TerrainEditFixtures.Bump()` is a set with surface `bump`, `(1/1, 1/100, PositiveZ)`, `BumpKey = ("bump", 0, 0)`, 4 by 4 cells, corners 0 except (2, 2) at 9. `Ring(x, z)` yields the 8 neighbouring corners. `Digest(set)` is the ordered patch digests. `GroundBelowSlot()` is `ground` slot (0, -1), `CellMinX 60`, `CellMinZ 60`, 4 by 4 at 1000, so local corner (4, 4) is lattice `Corner(64, 0)`, owned by slot (0, 0). `ReassignGroundColumn(set)` moves every corner owned by slot (0, 0) to its lowest-ordinal remaining incident patch: `Corner(60..64, 0)` to slot (0, -1) and `Corner(64, 1..4)` to slot (1, 0), dropping a dependency whose new owner is the dependent itself. `WithFarAnchors()` adds to `ThreeSurfaces().Surfaces` an Authored chain `far-chain` on `far` at `Corner(19200, 0)` and `Corner(19201, 0)` (2000) anchored in `far` (-300, 0), a SurfaceEdge chain `far-edge` on `far` (300, 0) at the same addresses, and strip `far-wall` anchored in `far` (300, 0) with lower `far-edge` and upper `far-chain`. `Edit("heights" | "ids" | "cut")` sets corner (1, 1) of `BumpKey` to 5, cell (0, 0) underlay to 2, or cell (0, 0) cut to `CornerQuarter` with overlay 3. `TerrainTransactionFixture.Create(MapDocument doc) : IDisposable` saves `doc` monolithically and opens it in an `EditorDocument` (`Editor`) and a `MapEditSession` with `MutationService` (`Service`), exposing `Text()`, `Token()` and `State()`. `CaveFixtures.ClosedMouth()` is `RampChamberShaft()` without the ramp footprint, `ramp-west`, `ramp-east`, `rim-seam`, `mouth` and `mouth-return`, every `outer` cell present, `cave-floor` from `CellMinZ 3`, and the chamber closed at z 3 by strip `mouth-seal` (`cave-floor` edge to `cave-ceiling` edge, +X chain, `(mouth-seal, Back)` in the chamber). `OpenMouthEdit()` upserts the `outer` and `cave-floor` patches of `RampChamberShaft()`, which carry their anchored records. `CeilingCrossingEdit()` upserts `cave-ceiling` with its z 3 corner row at 700.

- [ ] **Step 1: Write the failing pure-edit tests**

```csharp
static MapTerrainEditResult Prepare(MapSurfaceSet set, MapTerrainEdit edit) => MapTerrainEdits.Prepare(set, edit);
static MapReplaceTopology Remove(MapPatchKey key) => new(Array.Empty<MapSurfacePatch>(), new[] { key }, Array.Empty<MapSurfaceRef>(), Array.Empty<string>());

[Theory, InlineData(1, 1), InlineData(2, 0)]
public void Smooth_KeepsJ21Semantics(int iterations, int expected)
{
    MapSurfaceSet bump = TerrainEditFixtures.Bump();
    var digest = TerrainEditFixtures.Digest(bump);
    MapSurfacePatch p = Prepare(bump, new MapSmoothCorners(TerrainEditFixtures.BumpKey, 2, 2, 1, 1, iterations)).Candidate.Patches[TerrainEditFixtures.BumpKey];
    Assert.Equal(expected, p.Height(2, 2));                           // 9/9 = 1, then 1/9 rounds to 0
    Assert.All(TerrainEditFixtures.Ring(2, 2), c => Assert.Equal(0, p.Height(c.X, c.Z)));
    Assert.Equal(digest, TerrainEditFixtures.Digest(bump));
}
[Theory, InlineData(0, false), InlineData(65, false), InlineData(16, true), InlineData(17, true), InlineData(64, true)]
public void Smooth_AcceptsOneToSixtyFourPasses(int iterations, bool ok)
{
    var edit = new MapSmoothCorners(TerrainEditFixtures.BumpKey, 2, 2, 1, 1, iterations);
    if (ok) Assert.NotNull(Prepare(TerrainEditFixtures.Bump(), edit));
    else Assert.Throws<ArgumentOutOfRangeException>(() => Prepare(TerrainEditFixtures.Bump(), edit));
}
public void Smooth_RefusesAMissingHalo()
    => Assert.Contains("halo", Assert.Throws<MapDocumentException>(() => Prepare(TerrainEditFixtures.Bump(), new MapSmoothCorners(TerrainEditFixtures.BumpKey, 0, 0, 1, 1, 1))).Message);
[Theory, InlineData("heights", MapNativeInvalidation.Terrain | MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Residency),
 InlineData("ids", MapNativeInvalidation.Terrain | MapNativeInvalidation.Material),
 InlineData("cut", MapNativeInvalidation.Terrain | MapNativeInvalidation.Physics | MapNativeInvalidation.Nav | MapNativeInvalidation.Material | MapNativeInvalidation.Residency)]
public void Effects_InvalidationFlagsMatchTheEditKind(string kind, MapNativeInvalidation expected)
    => Assert.Equal(expected, Prepare(TerrainEditFixtures.Bump(), TerrainEditFixtures.Edit(kind)).Effects.Invalidates);
public void CornerOwner_AddingALowerKeyPatchKeepsTheOwner_DeletingRequiresReassignment()
{
    MapSurfaceSet set = SurfaceStorageFixtures.ThreeSurfaces().Surfaces;
    MapTerrainEditResult added = Prepare(set, new MapReplaceTopology(new[] { TerrainEditFixtures.GroundBelowSlot() }, Array.Empty<MapPatchKey>(), Array.Empty<MapSurfaceRef>(), Array.Empty<string>()));
    Assert.Contains(new MapCornerDependency(4, 4, new MapVertexOwner(new("ground", 0, 0), MapLatticeAddress.Corner(64, 0))),
        added.Candidate.Patches[new("ground", 0, -1)].CornerDependencies);
    Assert.Contains("owner", Assert.Throws<MapDocumentException>(() => Prepare(added.Candidate, Remove(new("ground", 0, 0)))).Message);
    MapTerrainEditResult done = Prepare(added.Candidate, new MapCompositeEdit(new MapTerrainEdit[] { TerrainEditFixtures.ReassignGroundColumn(added.Candidate), Remove(new("ground", 0, 0)) }));
    MapScopedSurfaces view = MapScopedSurfaces.CompleteView(done.Candidate);
    Assert.All(done.Candidate.Patches.Values, p => Assert.Empty(MapSeamValidator.ValidateCornerDependencies(p, view)));
    Assert.Contains(done.Effects.DigestChanges, c => c.Key.Contains("ground"));
}
public void RemovingAnAnchorPatchRequiresReanchoringEveryReferrer()
{
    MapSurfaceSet set = TerrainEditFixtures.WithFarAnchors();
    var farWest = new MapPatchKey("far", -300, 0);
    var farEast = new MapPatchKey("far", 300, 0);
    string m = Assert.Throws<MapDocumentException>(() => Prepare(set, Remove(farWest))).Message;
    Assert.Contains("anchor", m);
    Assert.Contains("far-chain", m);
    MapTerrainEditResult r = Prepare(set, new MapCompositeEdit(new MapTerrainEdit[] { new MapReanchorRecords(new[] { new MapRecordAnchorMove("far-chain", farWest, farEast) }), Remove(farWest) }));
    Assert.Equal(new MapRecordRef("far-chain", farEast), r.Candidate.AllRecords().OfType<MapWallStrip>().Single(w => w.Id == "far-wall").UpperChain);
    Assert.Empty(MapTopologyReferenceValidator.Validate(r.Candidate.Refs, r.Candidate.Patches.Values));
    Assert.True(r.Effects.Invalidates.HasFlag(MapNativeInvalidation.Residency));
}
```

- [ ] **Step 2: Write the failing transaction tests**

```csharp
public void TerrainTransaction_RestoresSeamSpaceAndIdentity()
{
    using TerrainTransactionFixture gui = TerrainTransactionFixture.Create(CaveFixtures.ClosedMouth());
    using TerrainTransactionFixture service = TerrainTransactionFixture.Create(CaveFixtures.ClosedMouth());
    (string text0, string token0) = (gui.Text(), gui.Token());
    gui.Editor.Execute(new TerrainEditCommand(CaveFixtures.OpenMouthEdit()));
    MapTerrainMutationResult viaService = service.Service.TerrainApply(CaveFixtures.OpenMouthEdit());
    Assert.Equal(gui.Text(), service.Text());
    Assert.Equal(gui.Editor.LastNativeEffects!.Describe(), viaService.Effects.Describe());
    Assert.Equal(MapDocumentFile.SaveText(CaveFixtures.RampChamberShaft()), gui.Text());
    Assert.NotEqual(token0, gui.Token());
    Assert.True(gui.Editor.Undo());
    Assert.Equal((text0, token0), (gui.Text(), gui.Token()));
    Assert.True(gui.Editor.Redo());
    Assert.Equal(service.Text(), gui.Text());
    string state = gui.State();
    for (int i = 0; i < 2; i++)
    {
        Assert.Contains("separation", Assert.Throws<MapDocumentException>(() => gui.Editor.Execute(new TerrainEditCommand(CaveFixtures.CeilingCrossingEdit()))).Message);
        Assert.Equal(state, gui.State());
    }
    Assert.Contains("halo", Assert.Throws<MapDocumentException>(() => gui.Editor.Execute(new TerrainEditCommand(new MapSmoothCorners(new("outer", 0, 0), 0, 0, 1, 1, 1)))).Message);
    Assert.Equal(state, gui.State());
}
public void FinePatchConversion_UndoRedoRestoresExactPatchesAndOwners()
{
    using TerrainTransactionFixture f = TerrainTransactionFixture.Create(MixedResolutionFixtures.Document(ConversionFixtures.Slopes()));
    string text0 = f.Text();
    f.Editor.Execute(new TerrainEditCommand(new MapConvertFinePatch(ConversionFixtures.Request(3, false))));
    string converted = f.Text();
    Assert.Contains("coarse-fine-1", converted);
    Assert.True(f.Editor.Undo());
    Assert.Equal(text0, f.Text());
    Assert.True(f.Editor.Redo());
    Assert.Equal(converted, f.Text());
}
```

- [ ] **Step 3: Run red.** `wa_test t17-red "$MAPDOC" "FullyQualifiedName~TerrainEditsTests"`. Expected: build FAIL naming `MapTerrainEdits`.
- [ ] **Step 4: Implement edits, the command and the service method.**
- [ ] **Step 5: Run green.** `wa_test t17-green-mapdoc "$MAPDOC" "FullyQualifiedName~TerrainEditsTests"` (PASS, 13 cases) and `wa_test t17-green-editor "$EDITOR" "FullyQualifiedName~TerrainTransactionTests|FullyQualifiedName~NativeDocumentTransactionTests|FullyQualifiedName~MutationService"` (PASS, 2 new tests plus every existing case).
- [ ] **Step 6: Commit** `KhaozEngine.MapDoc/Editing KhaozEngine.MapEditor/NativeTerrainCommands.cs KhaozEngine.MapEdit.Tool/MutationService.Terrain.cs KhaozEngine.MapDoc.Tests/Editing KhaozEngine.MapEditor.Tests/MapEditTool`, message `feat(mapeditor): add atomic terrain transactions shared by GUI and service`.

---

### Task 18: Compatibility comparison, private exhaustive oracle and living documentation (6b)

**Files:**
- Create: `KhaozEngine.MapDoc.Compatibility.Tests/LegacyTerrainImportTests.cs`, `KhaozEngine.MapDoc.OracleHarness.Tests/ShippedTerrainComparison.cs`, `ShippedTerrainComparisonTests.cs` (links `LegacyOracleConverter.cs`)
- Modify: `KhaozEngine.MapDoc.Compatibility.Tests/LegacyOracleConverter.cs` (`LegacyCorners`, `SharedCornerMismatches`), `KhaozEngine.MapDoc/README.md`, `KhaozEngine.MapEditor/README.md`, `KhaozEngine.MapEdit.Tool/README.md`, `docs/USING-KHAOZENGINE.md`, `KhaozEngine.MapDoc.OracleHarness.Tests/README.md`, and `docs/DEPENDENCY-SEAMS.md` only if it enumerates test-project edges

**Interfaces:** consumes everything above. Produces `LegacyOracleConverter.LegacyCorners(TileWorldDocument, RegionCoord, int plane) -> int[]` (65 by 65, the row order of `MapSurfacePatch.Heights`) and `SharedCornerMismatches(TileWorldDocument) -> IReadOnlyList<string>` (corners that differ across a signed region seam after conversion). Harness additions: `sealed record ShippedRegion(RegionCoord Coord, TileWorldDocument Document)`, `ShippedSourceInventory.Regions(string root) -> IReadOnlyList<ShippedRegion>` (ordered by signed region key) and `static class ShippedTerrainComparison` with `CompareRegion(ShippedRegion, PrivateOracleReport)`. No production importer and no game dependency.

- [ ] **Step 1: Write the public comparison test**

```csharp
public void LegacyTerrainImport_RemainsExactWithCaveModel()
{
    LegacyOracleWorld w = LegacyOracleWorld.Create();
    int nativeFaces = 0, legacyTriangles = 0;
    foreach (var (region, plane) in LegacyOracleConverter.RegionPlanes(w.Document))
    {
        var (surface, patch) = LegacyOracleConverter.ToNative(w.Document, region, plane);
        Assert.Equal(LegacyOracleConverter.LegacyCorners(w.Document, region, plane), patch.Heights);
        Assert.Equal(LegacyOracleConverter.CellBytes(w.Document, region, plane), LegacyOracleConverter.CellBytes(patch));
        Assert.Empty(patch.Records);
        nativeFaces += MapSurfaceCompiler.Compile(surface, patch).Faces.Count(f => f.Role == MapFaceRole.SupportFloor);
        legacyTriangles += TileGroundTriangles.Build(w.Document, region, plane).Indices.Length / 3;
    }
    Assert.Equal(legacyTriangles, nativeFaces);
    Assert.Empty(LegacyOracleConverter.SharedCornerMismatches(w.Document));
}
```

Equal totals with no records prove import added no cave, space or floor and empty upper planes produced no support.

- [ ] **Step 2: Write the private exhaustive comparison**

```csharp
public void ShippedTerrain_RemainsExactWithCaveModel() => PrivateOracleEntry.Run((inputs, report) =>
{
    // inputs.SourceRoot was verified against the private provenance inside Require.
    foreach (ShippedRegion region in ShippedSourceInventory.Regions(inputs.SourceRoot))
        ShippedTerrainComparison.CompareRegion(region, report);   // only report.Check and report.Record, never Assert on source values
});
```

`ShippedTerrainComparison.CompareRegion` (harness, public code, no data) checks, for every region and all four planes, through `report.Check` only: exact corners (derived planes recorded as derived), exact cell bytes including reserved Bridge bits, every drawable triangle within 0.00001 m, every normal component within 0.000001, the fallback set, shared corners across every signed region seam, no records, and, when the frozen source still has a drawable cell under the historical spike point named in the private manifest, native support height equal to the legacy movement triangle within 0.00001 m. It records per-plane counts and maximum errors only in the private report. `PrivateOracleEntry.Run` ends with `ThrowIfAnyFailed()` and sanitizes any exception (D1).

- [ ] **Step 3: Run red, then green, public and private**

Run: `wa_test t18-red "$COMPAT" "FullyQualifiedName~LegacyTerrainImportTests"`. Expected: build FAIL naming `LegacyCorners`.
Run: `wa_run t18-green dotnet test "$COMPAT" -c Release`. Expected: PASS, 4 tests (Tasks 3, 9 and 12A plus this one).
Run: `wa_run t18-harness-build dotnet build "$HARNESS" -c Release`, then `wa_test t18-harness-guards "$HARNESS" "FullyQualifiedName~PrivateOracleGuardTests"`, then `wa_run t18-harness-format dotnet format "$HARNESS" --verify-no-changes --no-restore`. Expected: exit 0 for all three, 11 guard cases.
Private run (controller): the Task 3 private block with `--filter "FullyQualifiedName~ShippedSourceInventory|FullyQualifiedName~ShippedTerrainComparison"` and report `comparison.json`. Expected: exit 0. Engine Outcome records only pass or fail and the private record reference. An exceedance is a reported blocker carried privately, never a tolerance change.

- [ ] **Step 4: Update living documentation**

`KhaozEngine.MapDoc/README.md`: format 5, the support recipe and the one-time token change, rational lattice addresses, mixed-resolution bounds and their exact common refinement with its budgets (including attempted-pair checks) and outcome classes, the compatibility-only `LegacyExteriorV1` lower bound with its exact recipe, non-capture tag and released bilinear arithmetic, sparse patches and their limits, storage under `tiles/surfaces/` and the unreserved `surfaces/`, the unified partial flag and the #1310 refusal including surfaces-only windows, what a raw save certifies (reference integrity including exact XZ owner-address agreement, never geometry) and the partial raw-save reverse-dependant, structure and records refusals, `IMapSurfaceSource` outcomes, generation pinning versus immutable acquisitions, `MapScopedSurfaces` acquisition and factory-only witnesses, scheme 2 and scoped identity, membership, support and relation facts with exact apertures, `NotRepresentable` outcomes, conversion classes and the footprint straddle rule, and what R3, R4, R5 and R8 still own. MapEditor and MapEdit.Tool READMEs: `TerrainEditCommand`, `LastNativeEffects`, `TerrainApply`. `docs/USING-KHAOZENGINE.md`: format-4 documents migrate on load and their token changes once, resolver-v1 options and execution are unchanged, windowed saves refuse after another writer changed the manifest. The harness README restates D1, including the entry boundary and the secure report rule. Sweep Markdown with `git grep -n -e "formatVersion 4" -e "CurrentFormatVersion = 4" -e "MapTiledFile.Save" -e "IsPartial" -- '*.md'` and fix live references.

- [ ] **Step 5: Run the documentation guards**, each once with its own log: `wa_run t18-dashes sh scripts/check-dashes.sh --tree`, `wa_run t18-prose sh scripts/check-prose.sh --tree`, `wa_run t18-file-size sh scripts/check-file-size.sh --tree`, `wa_run t18-agent-instructions sh scripts/check-agent-instructions.sh --tree`, `wa_run t18-doc-versions bash scripts/check-doc-versions.sh`. Expected: every exit 0. A KESIZE finding is fixed by moving behavior into a new type, never a baseline increase.
- [ ] **Step 6: Commit** `KhaozEngine.MapDoc.Compatibility.Tests KhaozEngine.MapDoc.OracleHarness.Tests KhaozEngine.MapDoc/README.md KhaozEngine.MapEditor/README.md KhaozEngine.MapEdit.Tool/README.md docs/USING-KHAOZENGINE.md`, message `test(mapdoc): prove exact legacy terrain under the cave model and document R2`.

---

## Review, release preparation, candidate gates and integration

**Per task.** The controller checks the worker's report against the actual commit, `git log --oneline <task-base>..HEAD`, the diff, and the red and green logs and exits, then dispatches a fresh task reviewer on that exact range. Findings return to the same implementer with a scoped re-review. A load-bearing unresolved finding blocks the next task.

**1. Whole-branch review.** After Task 18, one fresh whole-branch review covers the full range, the Global Constraints, every cross-task interface, D1 to D9 (D9 explicitly included, coordinator final review M6), the Review Focus tests and the downstream allocation, using the per-task logs as evidence. Material findings get fixes, focused re-runs and a targeted re-review.

**2. Release preparation on the task branch.** The controller fetches and re-reads `main`, `Directory.Build.props`, every tag and any staged version, and coordinates with the coordinator (the Catalog lane's 20.28.0 candidate and any other in-flight lane). Following `docs/CONTRIBUTOR-RULES.md`, it rides an unreleased staged version or takes the next free minor, never a number reserved here. One commit, `release(<version>): sculpted caves and sparse surfaces`, changes `<KhaozEngineVersion>`, adds the newest `CHANGELOG.md` entry and every declaration `scripts/check-doc-versions.sh` guards. Consumer notes state the one-time format-4 token change, unchanged resolver-v1 execution, the stale-window refusal for every partial save including surfaces-only windows, the raw-save `dependency position` refusal and the partial raw-save reverse-dependant, structure and records refusals, mixed-resolution bounds and their explicit outcomes, and that R2 claims no physics, nav, water or rendering capability.

**3. Merge current `main` into the task branch** and resolve there.

**4. Candidate verification**, once, in order, each with its own log, on the merged release-bearing head:

```bash
wa_run final-build dotnet build KhaozEngine.slnx -c Release
wa_run final-suite dotnet test KhaozEngine.slnx -c Release --no-build --filter "Category!=LiveSocket"
wa_run final-format dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
wa_run final-harness-build dotnet build "$HARNESS" -c Release
wa_run final-harness-guards dotnet test "$HARNESS" -c Release --no-build --filter "FullyQualifiedName~PrivateOracleGuardTests"
wa_run final-harness-format dotnet format "$HARNESS" --verify-no-changes --no-restore
wa_run final-dashes sh scripts/check-dashes.sh --tree
wa_run final-prose sh scripts/check-prose.sh --tree
wa_run final-file-size sh scripts/check-file-size.sh --tree
wa_run final-agent-instructions sh scripts/check-agent-instructions.sh --tree
wa_run final-doc-versions bash scripts/check-doc-versions.sh
```

The solution suite already includes the rump's harness exclusion guards, so no suite runs twice. The harness is outside the solution, so its build, guard tests and format run separately. Then the controller runs the Task 18 private block once on this head with a fresh extraction and a new report name. Every command needs exit 0, zero warnings, a nonzero test count where tests run, and no format diff. A failure is repaired through the review path and only the affected commands re-run once. No GPU tests, clients, windows, stress or repeated runs.

**5. Private pack of the candidate.** `priv_feed="$(mktemp -d /tmp/wa-r2-feed-XXXXXX)"`, then `wa_run final-private-pack env KHAOZENGINE_FEED="$priv_feed" scripts/pack-local-feed.sh`. Expected: exit 0 and the package set at the chosen version, recorded in Outcome. The shared feed is untouched.

**6. Normal hosted CI on the candidate.** The controller pushes `feature/wa-r2-caves-scale` and runs `gh workflow run ci.yml --repo APKiwiOrg/KhaozEngine --ref feature/wa-r2-caves-scale`, the full restore, Release build, test, determinism and pack path on a GitHub-hosted runner, with no publish. It records the run URL and waits for success. `cross-platform-gpu.yml` is path-filtered to render and GPU projects that R2 does not touch. If the final diff does touch those paths, the coordinator schedules that matrix too. A failure is reproduced and repaired through the review path, never retried until green.

**7. Integration.** In the coordinator's mutation window, the controller first fresh-checks `main` (coordinator final review M5): `git fetch origin`, then `git merge-base --is-ancestor main HEAD` and `git merge-base --is-ancestor origin/main HEAD` on the verified candidate. If either fails, `main` advanced since step 3 and nothing is fast-forwarded. The controller returns to step 3 and merges current `main` again. A version, changelog or staged-version change on `main` also sends it back to step 2. It then repeats candidate verification on the new merged head: the step 4 build, solution suite, format and repository guards always, the harness commands and the private block when the merge diff touches `KhaozEngine.MapDoc*`, the harness project, `scripts/` or `Directory.Build.props`, and steps 5 and 6 because the candidate SHA changed. Only when both checks pass on a verified candidate does the controller fast-forward `main` to it and push. The `main` push CI must pass.

**8. Shared pack.** `scripts/pack-local-feed.sh` runs from `main` only after `origin/main` contains the release commit.

**9. Owner release.** Only the owner tags, with `scripts/tag-release.sh`. R2 is not released until that tag's run publishes. R3, R4, R5, R8 and the swimming and pivot consumers treat R2 contracts as dependencies only from the released pin.

Slot exit 75 at any step is recorded as "not run" and reported. The coordinator schedules reacquisition. No retry helper runs.

## Outcome and proof ledger

Planning checkpoint 2026-10-07: CD1/CD2/CD4/CD7 are design and planning boundaries only. Root reviewed v4 and corrected the unapproved 262,144-comparison completion fixture to a 64-comparison control, clarified untagged legacy classification and pinned released negative-boundary fractional rounding. The coordinator final review of 08d4be1a (I1, M1 to M6) is applied as a scoped correction: one compiled bound-face context per validation or query call counted from bytes, the fixture link name invariant, the explicit dangling `WithoutOpening` failures, partial raw-save reverse-dependant refusals, bounded bound-slot counting, the fresh `main` check at integration and D9 in the whole-branch range. Root targeted reconciliation 2 then separated enforced cardinalities from the memory estimate and marked the context defaults as proposed, subtracted already-known slots before the bound-slot refusal, proved the reverse scan under exact XZ owner-address agreement with structural refusals and a deletion counterexample, and replaced the overdense fixture with the 8 by 8 footprint at 639. Full executable plan approval remains pending. No R2 runtime commands were run.

The controller fills this as execution proceeds. A row is complete only when its commit exists on the branch, its review passed and its logs and exits are recorded. Private oracle rows hold only pass or fail and the private record reference.

| Row | Base SHA | Commit SHA | Red log and exit | Green logs and exits | Review verdict | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| Released prerequisite | | | | | | owner-released R1 version, tag, publication run |
| Execution base | | | | | | main, tags, staged versions, API drift diff |
| 1 #1310 refusal | | | t1-repro | t1-green | | reproduction lines, `Closes #1310` decision |
| 2 Format-4, harness, CI guards | | | t2-red-mapdoc, t2-red-rump | t2-green-mapdoc, t2-green-rump, t2-harness-format | | pinned fixture digest |
| 3 Legacy oracle, private guard and entry | | | t3-red-compat, t3-red-harness | t3-green-compat, t3-green-harness, t3-harness-format | | private inventory pass or fail, private record ref |
| 4 Exact scalars, lattice, addresses | | | t4-red | t4-green | | |
| 5 Anchored records | | | t5-red | t5-green | | |
| 6 Format 5 | | | t6-red | t6-green-mapdoc, t6-green-editor | | updated format-4 text tests |
| 7 Storage and completeness | | | t7-red-editor, t7-red-mapdoc | t7-green-editor, t7-green-mapdoc | | surface no-loss claim |
| 8 Scheme 2, acquisition, witnesses | | | t8-red | t8-green-mapdoc, t8-green-editor | | |
| 9 Compiler | | | t9-red | t9-green-mapdoc, t9-green-editor, t9-green-compat | | |
| 10 Boundary geometry | | | t10-red | t10-green | | |
| 11 Conversion | | | t11-red | t11-green | | |
| 12A Common refinement | | | t12a-red | t12a-green, t12a-green-editor, t12a-green-compat | | D7 ruling and attempted-pair budget applied, D9 classifier and bilinear agreement |
| 12B Membership and coverage | | | t12b-red | t12b-green, t12b-green-editor | | one validation context, zero-compile over-budget refusal (8 by 8 at 639) |
| 13 Support and resolver v2 | | | t13-red | t13-green, t13-green-editor | | |
| 14 Precision | | | t14-red | t14-green | | named vertical risk observation |
| 15 Relation facts | | | t15-red | t15-green | | contract facts sent to #458 and F3 |
| 16 Transaction seam | | | t16-red | t16-green | | |
| 17 Terrain transactions | | | t17-red | t17-green-mapdoc, t17-green-editor | | |
| 18 Comparison and docs | | | t18-red | t18-green, t18 harness and guards | | private comparison pass or fail, private record ref |
| Whole-branch review | | | | | | |
| Release preparation | | | | | | version, ride or bump reason |
| Candidate verification | merged main SHA | candidate SHA | | final-* logs | | private comparison on candidate |
| Private pack | | | | final-private-pack | | package set |
| Hosted CI | | | | | | run URL |
| Integration and main CI | | | | | | fresh `main` ancestry check result, main SHA, run URL |
| Shared pack | | | | | | |
| Owner tag | | | | | | owner-created, publication run |

Approval record: full-plan approval (who, when, decision ID), the D7 ruling and its attempted-pair settlement (2026-10-07), the D4 straddle and D9 legacy-coverage approvals (2026-10-07) and the current D6 allowance.

## Plan self-review summary

The requirement map, interface consistency, dependency and residual-risk checks are in the published self-review. Source facts are in the public source register. Existing APIs are cited with their current-main file and line. Every other type, method and fixture helper is introduced in the task that first lists it.

## Execution handoff

Plan complete. Please review the plan. Does it capture what you want? The execution method already supplied by the program is subagent-driven-development, serial, with the shared build slot, a fresh reviewer per task and one whole-branch review.


### Task 1 execution checkpoint, intended RED

Owner-released R1 prerequisite is v20.27.1 at54a1f358, releaseCI37513485071 and101published package
provenances verified. R2sourcebase54a1f358, documentationbootstrap a9f0c13ea, approvedplan05e0d6948
with originalplanhash2ab5ca603b382c237caf70938218115dce51fc1ae3e91e4fa193c7fe3a76280a.
Onlythis Outcome append changes the execution copy's approvedplan text at this checkpoint.

Coordinator authorized parentinline single-reproduction preparation after Gauge had no worker.
The46-line test uses only the approved public synthetic TiledDocFixture. Coordinator inspected it
and granted one primaryRED. Freshdependencies and testassembly compiled, then exactlyonecase failed
with Assert.Throws() Failure: No exception was thrown, expectedMapDocumentException. Exit1,
zero passes/skips, intendedreleased-behaviorfailure. Primarycompute was explicitlyreleased.
Proof is proofs/2026-10-07-r2-task1-red.json. No productbehavior was changed before this result.

Coordinator accepted the RED and authorized only remainingTask1protocoltests and the three listed
productfiles, with fresh scopedsource review and a separately granted GREEN. No downstream surface
work, formatversion, privateworld, main/feed/tag or gamepin work is authorized. Snapshot vendoring
is an independent #459 handoff and not an R2 execution dependency.


### Task 1 reviewed source and focused GREEN

Implementation starts from RED checkpoint ef06f06543b1b380ad8a953fd218fa0a221dfc96.
The approved seven protocol cases were written before the witness/refusal implementation in the
three named product files. Fresh source review verified the product against the specification and
found one missing MapEditor namespace import in the clone assertion. Root added only that import
and verified every other reviewed byte was unchanged. Coordinator accepted that correction.

One authorized focused GREEN compiled and passed all 63 cases with zero failures and skips,
including all seven new cases. TRX SHA-256 is
4f1d11a034a1339f50e4624b17d9e0e3081a6e0510178b801fb0d08f7b9eb576.
The log SHA-256 is60db8b44107004dd3135944266618a3b32636ac0583cf02876fed079289f82bb.
One separately granted four-file format verification exited0 with a workspace-loading warning.
The default output does not identify its cause. No warning-free workspace load is claimed and no
autofix, diagnostic rerun or extra tests were performed. Four source hashes remained unchanged.
The format log SHA-256 is239c224f01960886368326d9ef5ebdcce071dcd0996d157bef6a963fc1eaa49c.
Both secondary compute grants were explicitly released. Focused proof is
proofs/2026-10-07-r2-task1-green.json.

Optional review notes remain recorded: the existing four-guards comment is stale, and a throw after
manifest rename leaves the caller's old witness, so a retry refuses until reload. The latter fails
closed after an already committed save and the released save ordering is preserved. Neither note
changes this task's implementation. No downstream Task2, main/feed integration, format version,
private oracle, pack or release follows from this focused proof. Full-round verification remains
at the approved finishing gate, and #1310 stays open until actual integration.


Task 1 implementation and focused proof are pushed at
8e393cf756f9ea3e1cbf5c590275c7274ebc6f2e on feature/wa-r2-caves-scale. Root verified clean HEAD
against origin and all four source hashes against GREEN. Five light guards, the hooked commit and
push exited0. This is the Task 1 branch checkpoint only. R2's later tasks, full-round finishing,
main integration and release are not claimed. Task2 remains closed pending coordination.


### Task 2 tests-first RED checkpoint, implementation unopened

At base2890c0602951d85016fc0f8c0d67074ba66f4ccb, the authorized worker prepared exactly
FormatFourResolverExpectationTests.cs and ArchitectureTests.MapDoc.cs, three Facts each.
Parent inspected both files against the approved Task2 section. Pivot independently inspected
HEAD, scope and frozen hashes, then granted one serial paired RED window.

MapDoc compilation exited1 with one CS0246 for absent ResolverExpectations and nine CS0103
references to absent FormatFourFixtures. Zero tests executed. No out-variable cascade or unrelated
diagnostic occurred. This is compile-time missing-capability evidence, not a runtime failure.
Only after that result was inspected did the second authorized command run. The architecture
project compiled and executed34cases,32passed and only the two expected missing-harness guards
failed, with zero skips. All31existing controls and the new containment guard passed.

Both frozen source hashes remained unchanged. Primarycompute was explicitlyreleased. Evidence is
proofs/2026-10-07-r2-task2-red.json, including log and TRX hashes. No retries, helper, harness,
project change, recorder, generated fixture or private input was introduced. This is an intentional
RED checkpoint only. Remaining Task2 implementation, one-time recorder, GREEN, harness format and
review remain required and unopened. No full Task2 completion or downstream work is claimed.


### Task 2 public recording completed once

The owner directed progress after the idle checkpoint. With Gauge unable to assign an implementer,
parent prepared the approved helpers, harness and recorder inline. Pivot independently reviewed the
complete implementation diff against Task2 and granted the bounded secondary build/record window.
The harness built with zero warnings/errors. One recorder invocation passed one case and produced
seven public synthetic files. No private data, workflow, solution entry or production API changed.

The recorder and an independent byte inspection agree on fixture digest
`d71a03bee5142f1f4e8d4b9bca34a147d145d4fd9e74ea0c46de0b6d9567c93a`.
The manifest covers the monolithic document, tiled manifest and three tiles, persistent empty
save-lock file, and resolver expectations. Released resolver/identity source differences against
a87038f5a were empty. Placement heights and callback order matched the approved literals.
Proof is proofs/2026-10-07-r2-task2-recording.json.

Secondary compute and fixture-output ownership were explicitly released before pinning the digest.
The exact digest is now pinned, and all recorded bytes are unchanged. Three resolver expectations,
34 architecture cases and explicit harness/scoped format checks are the next scheduled proof.
The recorder is not rerun. Task2 is not complete until those checks and the preserved review pass.


### Task 2 first GREEN failure and reviewed ordering correction

The first resolver GREEN executed three cases, two passed and the tiled/monolithic raw SaveText
comparison failed. No architecture or format command followed. Secondary compute was released.
The immutable-fixture and recorded-resolver bit-pattern cases passed. All seven recorded bytes
and the pinned digest stayed unchanged.

Source inspection shows the released tile index orders tiles by Z then X and Load appends their
placements. The fixture therefore loads b,a,c from tiles but a,b,c from the monolithic document.
SaveText preserves that list order, while authored identity treats placement order as nonsemantic.
The test now sorts only the two loaded placement lists by ordinal ID, then retains VerifyTiled
and full SaveText equality. No field is omitted or tolerance relaxed. This is a correction to the
plan's raw serialization-order assumption, not a change to production or the frozen recording.
Pivot independently checked the source path and exact test delta and accepted the correction.
Evidence is proofs/2026-10-07-r2-task2-first-green-ordering.json.

A new bounded window is queued for the changed three-case resolver run, then the previously unrun
architecture cases and two diagnostic-verbosity format checks. The recorder remains single-use.
No further owner approval is required for this corrected test subject within approved Task2.


### Task 2 focused verification complete

After the reviewed ordering correction, all three resolver expectations and all34architecture cases
passed with zero failures/skips. The first harness-format run found only initializer whitespace.
Those line breaks were corrected, preserving every non-whitespace character and every recorded byte.
No test or recorder was repeated for that whitespace-only correction.

The subsequently granted harness and scoped-source format checks both exited0 with diagnostic
verbosity and no workspace warnings/errors. Harness coverage includes its code-style/reference
analyzers and eight documents. Solution coverage includes the requested MapDoc.Tests and
KhaozEngine.Tests documents and analyzer passes, with zero changes. Secondarycompute was released.
Complete focused proof is proofs/2026-10-07-r2-task2-verification.json.

The fixture directory has the scoped text/eol=lf Git rule, preserving raw hashes on Windows
checkouts. Initial failure evidence remains separate. Task2's public fixtures and isolated harness
are ready for the scoped commit. No private shipped data was read, no production resolver or
format changed, and no main/feed/tag or native adoption is included. Task3 owns the later private
entry boundary and exhaustive source handling.


Task2 is committed at0374d2810e880c0f7e3bc623abf08e9acc2c7f37, with root verification of clean
origin equality, frozen fixture blobs and passing focused checks. Task3 begins from that source.

### Task 3 public/synthetic RED checkpoint

One worker prepared the compatibility project and public topology test plus eleven synthetic
privacy-guard cases. Parent and pivot inspected the exact three-file source before the paired RED.
Compatibility compilation emitted three diagnostics for absent LegacyOracleWorld/LegacyOracleCase.
Harness compilation emitted eight diagnostics: six for PrivateOracleFixtures and one each for
PrivateOracleInputException and PrivateOracleInputs. Both exited1 with zero tests executed.
No unrelated compiler/setup/platform diagnostic occurred. The missing fixture receiver prevented
binding its lambda bodies, so Entry/Report/Failure were not separately diagnosed, and no runtime
or platform-analyzer success is inferred. All three source hashes stayed unchanged.

Proof is proofs/2026-10-07-r2-task3-red.json. Primarycompute was explicitlyreleased. No private
world input, extraction, recorder rerun or implementation occurred during the window. Approved
public/synthetic implementation follows, with actual private inventory kept as its separate
controlled acceptance step after guard verification and source-provenance preparation.
