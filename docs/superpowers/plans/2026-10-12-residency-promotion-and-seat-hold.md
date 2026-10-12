# Residency promotion and seat hold Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove synchronous Decor promotion work and add command-consuming movement holds on both server heads, released on the owner-approved 20.27.2 patch line.

**Architecture:** Feed detached ground and cover inputs into one bounded CPU build queue, retain accepted rendering until replacement, and restrict teleport priming to new arrivals and their necessary borders. Keep movement holds in host-owned seat bookkeeping and replace input after dequeue, leaving the existing movement simulation and reconciliation running. Merge the finished patch into current engine main in a separate worktree before the orchestrator tags and packs the patch commit.

**Tech Stack:** C#, .NET, xUnit, `ITileWorldScene`, `IChunkBuildDispatcher`, existing NetWorld command and snapshot plumbing.

**Spec:** [Approved death-screen design, Engine section only](/Users/antonio/.t3/worktrees/Grimhollow/feature-death-screen/docs/superpowers/specs/2026-10-12-death-screen-design.md#engine-khaozengine-v20272). Also read this worktree's `AGENTS.md` and `docs/CONTRIBUTOR-RULES.md`, especially Version, package and release rules.

## Global Constraints

- Owner ruling D1. Fix the freeze in KhaozEngine, not by widening Grimhollow's gameplay radius.
- Owner ruling D5. The two engine fixes ship as a patch, v20.27.2, branched from v20.27.1, and are ported to engine main. Grimhollow does not adopt 20.33.0 for this.
- Patch worktree `/Users/antonio/KhaozEngine/.worktrees/residency-promotion-and-seat-hold`, branch `fix/residency-promotion-and-seat-hold`, baseline `54a1f3584` (`v20.27.1`). Never merge main into this patch branch.
- A Decor to Gameplay promotion moves entirely off the frame thread. Its ground cover and its full ground are built by background work and applied under the per-frame caps, nearest first.
- The coarse ground and far prop clusters keep drawing until the full ground replaces them, so a promotion never shows a hole.
- `PrimeAround` settles synchronously only what would otherwise draw a hole: regions arriving from Unloaded and the borders they dirty. Promotions and their neighbour rebuilds take the background path above. `PrimeGameplay` is limited to the regions the prime itself loaded.
- `Update` follows the same rule, so a border walk that promotes regions does no promotion work inline. The legacy synchronous ring, with no profile, is unchanged.
- Both `WorldServer` and `ShardedWorldServer` expose exactly `public void HoldMovement(PlayerRef target)`, `public void ReleaseMovement(PlayerRef target)` and `public bool IsMovementHeld(int slot)`.
- Hold and release are queued as admin commands and applied at the start of the next tick. While held, replace the dequeued command with `MoveCommand.Idle` before stepping. Consume it and advance the acknowledgement. Gravity and support still step. A teleport does not release. A leave releases.
- Neither item changes the wire. No new replicated state, command encoding, component codec, schema or game behavior belongs in this patch.
- Version `20.27.2`. Its CHANGELOG explicitly records the additive E2 API as an owner-approved SemVer patch exception because `20.28.0` is taken.
- Every local build, test or pack uses `build-slot --label <what> -- <command>`. Use `--heavy` for full suites and packing. Never loop test commands, run stress tests or launch consumer clients.
- Preserve unrelated edits. Stage explicit paths and use explicit commit pathspecs. No hook bypasses, baseline growth or warning suppression. New code stays within the file-size ratchet.
- Patch implementers stop at verified commits. Tasks 7 to 9 are orchestrator-only. Tagging runs through a KhaozEngine-rooted thread with `scripts/tag-release.sh` under OA25.

## Review Focus

- A prime with new arrivals beside promotions settles the actual arrival borders without pulling any promotion ground or cover build onto the caller. Task 3.
- Cover influenced by a distant tagged door or large upper-plane roof remains identical to the synchronous result after live objects, catalog values or foliage layers change during a queued build. Task 2.
- Demotion, unload and reload before worker completion cannot accept a previous resident's ground or cover, even when the same region and plane keys recur. Task 3.
- Reverse completion order, empty planes and worker failures retain drawable coarse ground and far clusters while each apply kind respects its own cap and nearest-first ordering. Tasks 1 and 3.
- Holds survive teleport and sharded ownership changes, release on an actual leave including a throwing leave handler, and never leak into a recycled or unknown slot. Task 4.

---

## Verified seams and design decisions

At `54a1f3584`, `TileRegionResidency.PrimeAround` calls `MoveProfile(int.MaxValue)` and then blanket `Flush(int.MaxValue)`. `TransitionProfileResidents` promotes through `LoadRegion(region, state)` and calls `MarkRegionStreamed`. `LoadProfileArrivals` is the distinct arrival path. `PromoteToGameplay` builds cover synchronously, and Gameplay dirty flushes run `BuildMesh`, props and `RebuildCover` synchronously. `PrimeGroundIfRequested` primes every full-ground request, including promotions. These are the E1 call sites to change.

The reported Draw seam is imprecise. `TileWorldView.Draw` draws any existing ground mesh before its Gameplay check. That check skips ordinary props and roofs for Decor. `_propClusters.Draw` already runs independently. Preserve those fallback owners instead of inventing a second coarse mesh owner.

`SnapshotGroundDocument` copies only plane arrays in a 3 by 3 ring. `TileFoliageSurface` also reads layer rules, material kinds, solid footprints, upper roofs and door tags, and indexes `AllObjects()` once per surface. One additional caveat is important: `TileWorldCatalogs.Merge` copies dictionaries but shares mutable material and archetype instances. It does not provide immutable worker inputs.

Choose a shared ground-and-cover queue and a view-owned spatial exclusion index. The index stores frozen object semantics by footprint, updates through the existing edit and streaming notifications, and returns only the exclusions relevant to a region's layers. Workers receive copied plane arrays, copied material values, the immutable `TileFoliageLayer` instances in a copied list, and frozen exclusions. They never touch the live document, live catalog values or scene.

| Cover scheduling option | Advantage | Cost | Worker bound /10 | Snapshot safety /10 | Cohesion /10 | Total |
| --- | --- | --- | --- | --- | --- | --- |
| Shared queue, detached cover input | One ground-and-cover worker limit and uniform cancellation | Requires typed input/output variants | 10 | 10 | 9 | 29 |
| Independent cover queue | Smaller ground queue change | Separate limits can double residency CPU work and duplicate lifecycle handling | 5 | 10 | 6 | 21 |

Use the first option. Preserve the separate existing HLOD queue and its existing limits. The new shared queue retains `MaxConcurrentBuilds = 2` and adds `MaxCoverAppliesPerPump = 2` alongside the existing two applies per ground kind. A cover result is one region's complete batch, keyed with plane `-1` and kind `Cover`.

Hold seams are as reported. Both heads drain admin commands after `OnBeforeTick` and before movement. WorldServer steps immediately after dequeue. ShardedWorldServer writes `PendingMove` after dequeue, then `PlayerMovementSystem` steps it. Apply the gate at those two dequeue sites, without changing `PlayerMovementSystem`. `SetPlayerState(..., teleport: true)` advances the existing teleport epoch. `RemoteCommandQueue.Dequeue` and `ClientPrediction.Reconcile` already own acknowledgement and pending-command removal.

E2 is a command hold, not a new physics mode. Do not silently clear momentum, jump timers or an active server-authored movement commitment. The game owns its death interruption. Preserve the existing idle-step physics and document that distinction.

## File ownership map

- `TileWorldBuildQueue.cs` owns filtered priming and the fourth apply kind.
- New `TileWorldBuildSnapshot.cs` owns detached terrain and material copies. New `TileCoverBuildInput.cs` owns cover capture and CPU generation. New `TileCoverObjectIndex.cs` owns spatial exclusion snapshots. `TileFoliageSurface.cs` consumes those frozen exclusions while retaining its public constructor.
- New `TileWorldView.ProfileBuilds.cs` owns the typed shared queue, generations and apply dispatch. `TileWorldView.GroundResidency.cs` retains ground capture and requests. New `TileWorldView.CoverResidency.cs` owns cover requests and replacement.
- New `TileWorldView.Flush.cs` takes the existing cohesive flush implementation out of `TileWorldView.cs` and adds profile routing. `TileRegionResidency.cs` records arrival and promotion provenance for a prime. `TileWorldView.Capture.cs` retains explicit offline settlement.
- New `WorldServer.MovementHold.cs` and `ShardedWorldServer.MovementHold.cs` own each head's host-local held-slot set and public API. Existing Tick, admin-switch and session-cleanup sites call them.
- New focused tests use the existing Render and Server test projects. Existing contract tests stay in their current files.

### Task 1: Filter synchronous priming and budget cover results

**Files:**
- Modify: `KhaozEngine.TileWorld.Render3D/TileWorldBuildQueue.cs`.
- Modify/Test: `KhaozEngine.Render.Tests/TileWorld/TileWorldBuildQueueTests.cs`.
- Create/Test: `KhaozEngine.Render.Tests/TileWorld/TileWorldBuildQueueScopedPrimeTests.cs`.

**Interfaces:**
- Consumes: existing `TileWorldBuildKey`, generation rejection, `IChunkBuildDispatcher.Schedule(Action)` and `Pump(RegionCoord focus)`.
- Produces: `TileWorldBuildKind.Cover`, `int TileWorldBuildQueueOptions.MaxCoverAppliesPerPump` (default `2`), `void PrimeGameplay(RegionCoord focus, IReadOnlySet<RegionCoord> regions)` and `void PrimeForCapture(RegionCoord focus)` on `TileWorldBuildQueue<TInput, TOutput>`.
- `PrimeForCapture` explicitly settles all FullGround and Cover requests. `PrimeHlod` retains its existing contract.
- Retain the existing unfiltered `PrimeGameplay(RegionCoord focus)` temporarily so this task builds before Task 3 changes its view caller. Task 3 removes that overload. All queue tests move to explicit selection here.

- [ ] **Step 1: Write the failing queue tests.** Keep the existing `PrimeGameplay_blocks_for_full_ground_but_keeps_decor_apply_budgeted` test, passing its two full-ground regions explicitly. Add `Scoped_prime_builds_waits_and_applies_only_selected_full_ground`, covering Pending, Running and Ready requests outside the selected set. For each state, select region `(0, 0)` and exclude `(1, 0)`.

```csharp
queue.PrimeGameplay(default, new HashSet<RegionCoord> { new(0, 0) });
Assert.All(applied, result => Assert.Equal(new RegionCoord(0, 0), result.Key.Region));
Assert.DoesNotContain(builtInline, key => key.Region == new RegionCoord(1, 0));
Assert.Equal(1, queue.TrackedCount); // excluded full-ground work remains tracked
```

Use a manual dispatcher for Pending and Ready cases. For Running, start only the excluded worker on a gated background task, prime the selected pending request, assert that prime returns before releasing the gate, and release in `finally`. Use bounded waits, never sleeps or a repeated test run. Add `Pump_applies_cover_nearest_first_with_independent_caps` with all four kinds, reverse completion, full/cover caps `1`, coarse cap `2`, HLOD cap `1` and test-only MaxConcurrentBuilds `20` so every request can complete before Pump. Assert the selected cover distance, one cover apply, the other three caps and remaining ready count. Also test a zero cover cap and rejection of a negative cap.

- [ ] **Step 2: Run the new tests and confirm the missing scoped API and Cover kind fail.**

Run `build-slot --label residency-queue-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~TileWorldBuildQueue`.

- [ ] **Step 3: Implement the interfaces in `TileWorldBuildQueue.cs`.** Thread the region selection through pending execution, scheduled waits and ready application. An empty set builds, waits for and applies nothing. Collecting other completions must not grant them a synchronous apply. Add the cover budget to Pump without changing the other caps or concurrency accounting. Update the existing prime siblings to pass their intended region sets. Keep deterministic nearest-first applies and existing failure propagation. Only capture uses an unfiltered full-ground/cover prime.

- [ ] **Step 4: Run the same filter with label `residency-queue-green`.** Expected all queue tests pass, including blocked Decor, generation, cancellation and disposal tests.

- [ ] **Step 5: Commit the three explicit task paths.** Subject `fix(tileworld): scope gameplay priming and budget cover applies`.

### Task 2: Capture immutable, spatially bounded cover inputs

**Files:**
- Create: `KhaozEngine.TileWorld.Render3D/TileCoverBuildInput.cs`.
- Create: `KhaozEngine.TileWorld.Render3D/TileCoverObjectIndex.cs`.
- Create: `KhaozEngine.TileWorld.Render3D/TileWorldBuildSnapshot.cs`.
- Modify: `KhaozEngine.TileWorld.Render3D/TileFoliageSurface.cs:17-106` and `TileWorldView.Foliage.cs:21-54`.
- Create/Test: `KhaozEngine.Render.Tests/TileWorld/TileCoverBuildInputTests.cs`.
- Create/Test: `KhaozEngine.Render.Tests/TileWorld/TileCoverObjectIndexTests.cs`.

**Interfaces:**
- Produces: `internal readonly record struct TileCoverObject(TileRect Footprint, int Plane, bool Solid, bool Roof, bool Door)`.
- Produces: `TileCoverObjectIndex(TileWorldDocument document, TileWorldCatalogs catalogs)`, `void Refresh(RegionCoord anchorRegion)` and `IReadOnlyList<TileCoverObject> Query(RegionCoord region, IReadOnlyList<TileFoliageLayer> layers)`.
- Produces: `static TileCoverBuildInput TileCoverBuildInput.Capture(TileWorldDocument document, TileWorldCatalogs catalogs, RegionCoord region, bool smoothNormals, TileCoverObjectIndex objects)` and `IReadOnlyList<GroundCoverInstance> TileCoverBuildInput.Build()`.
- Define TileCoverBuildInput as an internal sealed record with exclusively owned snapshot state so Task 3 can give it the shared record base.
- Produces: `static TileWorldDocument TileWorldBuildSnapshot.CaptureGround(TileWorldDocument document, RegionCoord region)` and `static TileWorldCatalogs TileWorldBuildSnapshot.CaptureMaterials(TileWorldCatalogs catalogs)`, shared by ground and cover capture.
- Produces: an internal `TileFoliageSurface` constructor taking the existing four arguments plus `IReadOnlyList<TileCoverObject> exclusions, TileRect sampleBounds`. The public constructor remains unchanged.

- [ ] **Step 1: Write failing snapshot and exclusion tests.** Name them `Captured_cover_matches_live_cover_at_capture`, `Captured_cover_ignores_later_source_mutations`, `Distant_door_and_upper_roof_are_not_limited_to_ground_ring`, and `Index_refresh_removes_old_bounds_and_queries_only_relevant_objects`. Compare whole `GroundCoverInstance` sequences using the current synchronous distribution as the oracle. Include negative region coordinates, multiple foliage planes, overlay shapes, smooth normals and edge fade.

```csharp
Assert.Equal(expectedAtCapture, input.Build());
Assert.Equal(expectedAtCapture, input.Build()); // after height, settings, object/tag and catalog mutation
Assert.Equal(expectedAtCapture, input.Build()); // after replacing the document's foliage layer
Assert.DoesNotContain(unrelatedObject, objects.Query(region, layers));
Assert.Contains(distantRoof, objects.Query(region, layers));
Assert.Contains(distantDoor, objects.Query(region, layers));
```

Place a door outside the 3 by 3 terrain ring with clearance reaching the target, and an upper roof anchored outside that ring whose footprint reaches it. Include a solid exclusion in the one-tile edge-fade halo. Mutate material Kind and archetype footprint/tags after capture to catch the shallow-Merge trap. Refresh after object deletion or relocation and assert the old bounds no longer affect samples. Populate distant unrelated objects and assert the queried candidate set contains only intersecting footprints and relevant door centres, without a timing threshold.

- [ ] **Step 2: Run the new tests and confirm the missing input/index types fail.**

Run `build-slot --label residency-cover-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~TileCoverBuildInputTests|FullyQualifiedName~TileCoverObjectIndexTests'`.

- [ ] **Step 3: Implement capture, indexing and CPU generation.** Store exclusion values, not `TileObject` or `TileObjectArchetype` references. Bucket roof/solid footprints across every region they overlap, and door centres spatially, retaining anchor-region ownership so Refresh removes old entries before adding current ones. Query a one-tile halo for solid/roof edge sampling and each layer's exact metre clearance for doors, including doors outside the terrain ring. Filter by the existing same-plane solid/door and upper-plane roof rules. Clip tile exclusion indexing to the sampled halo so a large roof cannot allocate its entire footprint for each build.

Capture plane arrays over the existing 3 by 3 ring through the shared snapshot helper. Copy the foliage-layer list, safely sharing immutable `TileFoliageLayer` instances. Copy all GroundMaterial values into an exclusively owned catalog, never via shallow Merge. Snapshot SmoothNormals. Pass the target region's one-tile halo as sampleBounds so exclusion masks are clipped while door centres still derive from original footprints. Worker generation uses those inputs and returns `GroundCoverBatch` without scene access. Retain the public surface's existing whole-document behavior and reuse one distribution routine for legacy and detached builds.

- [ ] **Step 4: Run the new tests plus existing foliage tests.**

Run `build-slot --label residency-cover-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~TileCoverBuildInputTests|FullyQualifiedName~TileCoverObjectIndexTests|FullyQualifiedName~TileFoliageSurfaceTests|FullyQualifiedName~TileWorldViewFoliage'`. Expected exact distribution parity and all existing exclusions/lifetime tests pass.

- [ ] **Step 5: Commit only this task's explicit paths.** Subject `fix(tileworld): detach region cover inputs and index exclusions`.

### Task 3: Route promotions and their dependants through the shared queue

**Files:**
- Create: `KhaozEngine.TileWorld.Render3D/TileWorldView.ProfileBuilds.cs`, `TileWorldView.CoverResidency.cs`, `TileWorldView.Flush.cs`.
- Modify: `KhaozEngine.TileWorld.Render3D/TileWorldView.cs:163-185,348-432,473-550,749-767`, `TileWorldView.GroundResidency.cs`, `TileWorldView.Foliage.cs`, `TileWorldView.Capture.cs` and `TileRegionResidency.cs:170-277`.
- Create/Test: `KhaozEngine.Render.Tests/TileWorld/TileWorldProfilePromotionTests.cs`, `TileWorldProfilePromotionLifetimeTests.cs`, `TileWorldProfilePrimeTests.cs`, `ManualTileBuildDispatcher.cs`.
- Modify/Test: `KhaozEngine.Render.Tests/TileWorld/TileRegionResidencyTests.cs` only where profile settlement assumptions need explicit pumping.

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces in ProfileBuilds: internal abstract record bases `TileProfileBuildInput` and `TileProfileBuildOutput`. The input variants are `TileGroundBuildInput(TileWorldDocument Document, TileWorldCatalogs Catalogs, RegionCoord Region, int Plane, TileGroundLod Lod, TileGroundMesherOptions Options)` and Task 2's `TileCoverBuildInput`, both sealed records deriving from TileProfileBuildInput. The output variants are sealed records `TileGroundBuildOutput(GltfMesh? Mesh)` and `TileCoverBuildOutput(IReadOnlyList<GroundCoverInstance> Cover)` deriving from TileProfileBuildOutput. Keep cover's Capture/Build contract unchanged.
- Produces: one `TileWorldBuildQueue<TileProfileBuildInput, TileProfileBuildOutput>` for ground and cover, with `TileProfileBuildOutput BuildProfileCpu(TileWorldBuildRequest<TileProfileBuildInput> request)` and `void ApplyProfileBuild(TileWorldBuildResult<TileProfileBuildOutput> result)` dispatching by kind.
- Produces: `void RequestCover(RegionCoord region, RegionHandles handles)`, `void CancelCover(RegionCoord region)`, and `internal void FlushProfilePrime(RegionCoord focus, IReadOnlySet<RegionCoord> arrivals, IReadOnlySet<RegionCoord> promotions)`.
- Extend only the internal TileWorldView constructor with optional `Action<TileWorldBuildKey>? onCpuBuild = null`, called at every ground/cover CPU-build entry, including legacy synchronous entries. This is the headless test witness, not a public telemetry API.
- Test helper `ManualTileBuildDispatcher` implements `IChunkBuildDispatcher`, exposes `PendingCount`, `RunAt(int index)`, `RunAll()` and `Drain()`. Gate execution until explicitly driven and support reverse completion. It does not own test timing or spin.

- [ ] **Step 1: Write the failing acceptance and lifecycle tests.** Use the internal constructor's CPU witness and manual dispatcher. Test `Promotion_only_prime_has_no_caller_ground_or_cover_builds` and `Border_update_promoting_five_regions_has_no_caller_builds`. Author a 6 by 5 grass grid, profile radii `4, 10, 12`, prime at region `(5, 1)` and settle, then prime at `(2, 1)`. The five regions of column 0 are promotions, with no arrivals. Use a separate fixture primed at `(5, 1)` for Update to `(4, 1)`. Add nonempty foliage and selected HLOD props. Clear witness records after setup settlement.

```csharp
Assert.Equal(5, promotedRegions.Count);
Assert.Empty(cpuBuilds); // dispatcher is paused throughout PrimeAround or Update
Assert.Equal(meshLoadsBefore, scene.MeshLoads.Count);
Assert.Equal(coverCountBefore, view.GeneratedCoverCount);
Assert.All(promotedRegions, r => Assert.Equal(TileRegionResidencyState.Gameplay, view.ResidencyOf(r)));
```

Record witness calls with keys and managed-thread IDs in a ConcurrentQueue. For the next Draw while workers remain paused, assert no caller CPU builds, no new uploads, and the previously recorded coarse ground handles are still in `scene.Drawn`. Assert accepted cluster handles remain live and drawable at a focus inside the layer's HLOD range. Execute queued bodies on a background task and assert their witness thread IDs differ from the prime caller. After completing workers, assert ground and cover apply counts per Draw stay within their independent caps and all five regions eventually have the expected full mesh and cover.

Add `Mixed_prime_settles_only_arrivals_and_their_nonpromotion_borders`: combine a ridge-grid arrival with preexisting promotions. Assert caller CPU keys contain only arrivals and the arrival's necessary preexisting Gameplay ground-border neighbours, never promoted regions or cover builds. Assert the ridge's arrival border heights are correct before workers resume, and unrelated promotion neighbours settle later. Add a dirty edit on an unrelated profile region to prove it is not included in the synchronous settle.

Add `Stale_ground_and_cover_cannot_cross_demotion_or_reload`, with a paused old generation, demote/unload/reload the same key and complete old work last. Add `Pending_coarse_fallback_survives_promotion_and_cannot_replace_accepted_full`, `Empty_planes_do_not_leak_handles_or_starve_cover`, and `Failed_promotion_keeps_previous_ground_and_clusters`. Assert accepted handle identity, releases exactly once, correct GeneratedCoverCount and zero live handles after Dispose. Add `Capture_explicitly_settles_profile_ground_cover_and_hlod` and keep the existing legacy ring/border tests unchanged.

- [ ] **Step 2: Run the new tests and confirm the internal witness is missing or the synchronous-build assertions fail.**

Run `build-slot --label residency-promotion-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~TileWorldProfilePromotion|FullyQualifiedName~TileWorldProfilePrimeTests'`.

- [ ] **Step 3: Implement the shared profile build owner and safe replacement.** Mark handles created by LoadProfileRegion as profile-managed, keeping public `LoadRegion(region)` legacy-owned. Assign every ground and cover request a monotonically increasing view-wide generation, including after reload. Track expected FullGround and CoarseGround generations separately per plane and a cover generation per region. This avoids both an old resident's result matching a new resident and a late coarse result downgrading accepted full ground.

Promotion changes residency and queues FullGround and Cover, retaining accepted meshes, props and cluster handles. Preserve already pending coarse work until full ground applies, accepting coarse only while no current full replacement has applied. Demotion invalidates full and cover generations, releases cover once and requests coarse. Unload cancels all keys before dropping handles. Apply cover only to the current Gameplay generation, releasing the prior accepted batch and updating GeneratedCoverCount on the scene thread. CPU failures keep prior rendering and follow existing queue exception propagation. Dispose drains and rejects detached results without uploading them. Ground capture now uses Task 2's shared terrain/material snapshot helpers instead of shallow catalog Merge.

Wire the exclusion index to construction and existing MarkDirty/MarkRegionStreamed notifications. Defer index refresh until capture so source unload, which occurs after MarkRegionStreamed, is observed correctly. Refresh touched anchor regions before querying. Promotion alone must not re-scan world objects or invalidate unchanged HLOD snapshots.

- [ ] **Step 4: Implement profile flushing and scoped prime provenance.** Move the cohesive flush methods to `TileWorldView.Flush.cs`. For profile-managed dirty regions, enqueue ground and coalesced region cover replacements instead of BuildMesh/RebuildCover. Preserve props refresh when its document snapshot needs it. Ordinary Draw or Flush never invokes an unfiltered synchronous profile prime, even for `Flush(int.MaxValue)`. Keep the legacy null-mesh budget and exception-cleanup semantics. Bound dirty profile submissions by the requested flush budget and request cover once per touched region per flush.

In `TileRegionResidency`, reuse collections recording newly loaded arrivals and Decor-to-Gameplay promotions for this prime. Complete all source loads before final capture. `FlushProfilePrime` synchronously builds only loaded Gameplay ground-border neighbours of arrivals that were already Gameplay, excluding all promotions. The ground border reach is the existing DirtyRegionMargin, not foliage's potentially distant door reach. Queue every other profile dependency, including cover dependencies. Enqueue the final snapshots of the arrival regions and call scoped `PrimeGameplay` with only arrivals classified Gameplay. Never wait for or apply promotion ground/cover from this call. A promotions-only prime has an empty synchronous set.

Legacy PrimeAround still runs MoveLegacy and Flush(int.MaxValue). Remove the transitional unfiltered PrimeGameplay overload and PrimeGroundIfRequested now that profile primes pass a selection. `SettleForCapture` explicitly drains dirty submissions and uses `PrimeForCapture` plus the existing HLOD capture prime. It is an offline exception, never called by Update or PrimeAround. Add the CPU witness to synchronous and worker build entry points so an upload-only check cannot mask CPU generation on the caller.

- [ ] **Step 5: Run the affected renderer contracts once.**

Run `build-slot --label residency-promotion-green -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~TileWorldProfile|FullyQualifiedName~TileWorldBuildQueue|FullyQualifiedName~TileRegionResidency|FullyQualifiedName~TileWorldLargeWorld|FullyQualifiedName~TileFoliageSurface|FullyQualifiedName~TileWorldViewFoliage'`. Expected all pass, with exact headless CPU/thread, fallback, border and lifetime assertions. Pump a bounded number of simulated frames inside the relevant test to complete one scenario, never repeat test processes or use timing benchmarks as acceptance.

- [ ] **Step 6: Commit only the explicit task paths.** Subject `fix(tileworld): stream gameplay promotions without frame builds`.

### Task 4: Add queued movement holds on both server heads

**Files:**
- Create: `KhaozEngine.NetWorld/WorldServer.MovementHold.cs`, `KhaozEngine.NetWorld/ShardedWorldServer.MovementHold.cs`.
- Modify: `KhaozEngine.NetWorld/AdminCommand.cs:16-20`, `WorldServer.cs:556-563,710-716`, `ShardedWorldServer.cs:504-510,710-716`, `WorldServer.Sessions.cs:54-92`, `ShardedWorldServer.Sessions.cs:61-106`.
- Create/Test: `KhaozEngine.Server.Tests/NetWorld/MovementHoldTests.cs` and `MovementHoldLifecycleTests.cs`.

**Interfaces:**
- Consumes: `PlayerRef`, `ResolveSlot(PlayerRef)`, the existing admin buffer, `MoveCommand.Idle`, command dequeue, SetPlayerState and session cleanup.
- Produces on both heads: the three public signatures in Global Constraints. IsMovementHeld is a host-thread query of applied state, false for an unknown/departed slot. Repeated hold/release is idempotent, and queued commands preserve FIFO order.
- Produces: `AdminCommandKind.HoldMovement` and `AdminCommandKind.ReleaseMovement`, applied by each head's switch to its private held-slot set. Do not add methods to `IAdminControllable`, the admin HTTP surface or a wire message.

- [ ] **Step 1: Write failing parity tests for both heads.** Use theories selecting the head and a small test-local fixture wrapping the identical public operations, not a new production interface. Follow existing loopback join/send patterns and `MoveProtocol` snapshot header decoding. Use `TickSeconds = 1f / 30f`, flat ground, MaxInputBacklog `0` and a grounded start for command rejection. Name tests `Hold_and_release_apply_on_next_tick_in_queue_order`, `Held_walk_run_jump_consumes_every_sequence`, `Held_body_falls_and_release_walks`, `Held_input_reconciles_without_pending_backlog`, and `Unheld_movement_is_unchanged`.

```csharp
server.HoldMovement(PlayerRef.Slot(slot));
Assert.False(server.IsMovementHeld(slot));
server.Tick(dt);
Assert.True(server.IsMovementHeld(slot));
```

For 60 simulation ticks send alternating walk, run and jump commands with successive sequences, ingest, tick and read the emitted frame. Assert XZ is unchanged, the grounded body never jumps, and each header's ack equals that tick's sent sequence. Reconcile the corresponding predicted command and assert `ClientPrediction.PendingCommandCount` returns to zero. The finite 60-tick scenario is a unit test, not a stress or repeated-run proof. Drop a stationary held body from Y `10`, assert Y decreases and it lands at the tuning's ground-supported height within 180 fixed ticks. Release and assert the next new walk sequence moves it.

```csharp
Assert.Equal(start.Position.X, state.Position.X, 3);
Assert.Equal(start.Position.Z, state.Position.Z, 3);
Assert.True(state.Move.Grounded);
Assert.Equal(sentSequence, decodedAcknowledgement);
Assert.Equal(0, prediction.PendingCommandCount);
Assert.True(landed.Move.Grounded);
Assert.Equal(tuning.CapsuleHalfHeight, landed.Position.Y, 3); // flat ground is Y 0
Assert.NotEqual(landed.Position.Z, afterRelease.Position.Z);
```

Add lifecycle tests `Teleport_preserves_hold_and_advances_epoch`, `Actual_leave_and_throwing_leave_handler_clear_hold`, `Recycled_and_unknown_slots_are_not_held`, and sharded `Cross_cell_teleport_preserves_hold_and_one_owner`. Exercise account and slot PlayerRefs, both hold-then-release and release-then-hold in one drain, repeated commands and empty movement queues. A lingering body remains joined and held until actual OnLeave. Test expiry and reclaim leave paths using existing linger patterns.

- [ ] **Step 2: Run and confirm missing hold APIs fail.**

Run `build-slot --label movement-hold-red -- dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter FullyQualifiedName~MovementHold`.

- [ ] **Step 3: Implement each head's host-local hold owner.** Queue only a kind and target, resolving the target at apply time like existing movement commitments. Add the held slot only when it is joined. Preserve ordinary dequeue and lastAck assignment, then replace `cmd` with Idle when the set contains the slot. WorldServer passes it to simulator.Step and ShardedWorldServer passes it to PendingMove. Do not skip the step, gate PlayerMovementSystem, alter teleport state or clear the command queue on hold/release. Remove the slot in each OnLeave `finally` so a persistence callback throwing cannot leak the hold. Keep hold state outside ECS so it survives sharded handoff naturally.

- [ ] **Step 4: Run the parity tests and existing related contracts.**

Run `build-slot --label movement-hold-green -- dotnet test KhaozEngine.Server.Tests/KhaozEngine.Server.Tests.csproj -c Release --filter 'FullyQualifiedName~MovementHold|FullyQualifiedName~WorldServerAdminTests|FullyQualifiedName~ShardedWorldServerAdminTests|FullyQualifiedName~ShardedWorldServerLinger|FullyQualifiedName~WorldServerBacklogCatchUpTests|FullyQualifiedName~ClientPredictionTests|FullyQualifiedName~RemoteCommandQueue'`. Expected both heads satisfy the same hold/ack/fall/release assertions and existing behavior remains green.

- [ ] **Step 5: Commit only the nine explicit task paths.** Subject `feat(networld): add queued movement holds to player seats`.

### Task 5: Document and verify the 20.27.2 patch release commit

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `README.md`, `docs/USING-KHAOZENGINE.md`, `KhaozEngine.TileWorld.Render3D/README.md`, `KhaozEngine.NetWorld/README.md`.
- Inspect: all Markdown matches for changed names and residency/prime behavior. Modify additional living references only if the sweep identifies stale descriptions of this change, recording each explicit path in the commit.

**Interfaces:**
- Consumes: verified Tasks 1 to 4 on the patch branch.
- Produces: a clean, fully verified `patch_release_sha`, with KhaozEngineVersion `20.27.2`, matching guarded declarations and newest CHANGELOG heading. This is the only commit Tasks 6, 8 and 9 use as the patch release identity.

- [ ] **Step 1: Recheck the patch guard, tags and release identity.** Fetch origin and tags without merging anything into this branch. Inspect current origin/main and its staged version read-only. Verify `v20.27.2` is still free. A collision is an orchestrator decision under D5, not permission to pick a different version or rebase this patch on main.
- [ ] **Step 2: Write the living API and behavior docs.** Describe delayed full-ground/cover promotion, retained coarse/HLOD fallback, scoped arrival priming, nearest-first caps and unchanged legacy behavior. Document hold/release signatures, host-thread query, next-tick FIFO application, command consumption, falling/support, teleport/leave semantics and the distinction from cancelling physics or commitments. Sweep with `rg -n --glob '*.md' 'PrimeAround|PrimeGameplay|Decor|HoldMovement|ReleaseMovement|IsMovementHeld|movement commitment'` and correct stale statements, including the current TileWorld.Render3D README's claim that every Gameplay prime drains synchronously.
- [ ] **Step 3: Set `<KhaozEngineVersion>20.27.2</KhaozEngineVersion>` and add newest `## 20.27.2` in CHANGELOG in the same release commit.** Include E1 and E2, no-wire-change compatibility and the explicit additive-API SemVer exception approved by D5 because 20.28.0 is taken. Update PackageReference example declarations in root README and USING-KHAOZENGINE to 20.27.2. Keep the historic sections intact.
- [ ] **Step 4: Run full patch verification from this worktree, once.**

```bash
mkdir -p local-feed
build-slot --label residency-patch-release-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label residency-patch-release-tests -- dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
sh scripts/check-dashes.sh --tree
sh scripts/check-prose.sh --tree
sh scripts/check-file-size.sh --tree
sh scripts/check-agent-instructions.sh --tree
bash scripts/check-doc-versions.sh
git diff --check
```

Expected build/test exit `0`, zero warnings, all guards pass and the doc-version check reports 20.27.2. If a baseline-listed file shrank, ratchet down with `sh scripts/check-file-size.sh --update` and include that change. Do not raise a baseline. Do not pack before the merge and tag tasks below.

- [ ] **Step 5: Commit all release files with explicit pathspecs.** Subject `release(20.27.2): fix residency promotion and add movement holds`. Record `git rev-parse HEAD` as `patch_release_sha`, verify a clean tree, and hand the commit to the orchestrator. No push or tag by the patch implementer.

### Task 6: Port the patch into current engine main in an integration worktree

**Files:**
- Merge: patch changes into `/Users/antonio/KhaozEngine/.worktrees/port-residency-promotion-and-seat-hold`, branch `fix/port-residency-promotion-and-seat-hold`, created from current `origin/main`.
- Resolve: `Directory.Build.props`, `CHANGELOG.md`, guarded README/example declarations and any actual code conflicts against current main's capabilities.

**Interfaces:**
- Consumes: exact `patch_release_sha` from Task 5. Integration must merge this branch, not cherry-pick it, so the release commit becomes an ancestor of origin/main.
- Produces: verified `integration_sha` containing `patch_release_sha`, main's staged version and both CHANGELOG records. This task runs under a separate integration assignment, never in the patch implementer's worktree.

- [ ] **Step 1: Have the orchestrator fetch current origin/main and create the separate integration worktree and branch.** Run a starting guard there checking the exact directory/branch, a clean tree and that its starting HEAD equals the fetched origin/main. Record main's staged version before the merge, currently 20.33.0. Do not move or edit the existing main checkout.
- [ ] **Step 2: Merge the patch branch without committing and resolve in the integration worktree.** Merge exactly its verified release tip. Preserve main's staged KhaozEngineVersion and all corresponding guarded declarations. Retain the 20.27.2 CHANGELOG section between 20.28.0 and 20.27.1 and add a port note to the staged 20.33.0 entry. Preserve main-only APIs and changes while reconciling source conflicts. Do not resolve by copying the entire patch version of a file over main.
- [ ] **Step 3: Run the same full Release build, full test suite and whole-tree guards from Task 5 in the integration worktree.** Use labels `residency-main-port-build` and `residency-main-port-tests`, with the test command in the heavy lane. Expected zero warnings, all tests/guards pass and doc-version declarations match main's retained staged version.
- [ ] **Step 4: Commit the verified merge.** Subject `merge(tileworld): port residency promotion and movement hold patch`. Record `integration_sha`. Verify `git merge-base --is-ancestor "$patch_release_sha" HEAD` succeeds. Stop for orchestrator integration, leaving the patch release commit unchanged.

### Task 7: Push verified main (orchestrator only)

**Files:** No new source edits. Integrate the verified branch into the clean main checkout `/Users/antonio/KhaozEngine`.

**Interfaces:**
- Consumes: Task 6's verified `integration_sha` and exact `patch_release_sha`.
- Produces: pushed origin/main containing the patch release commit.

- [ ] **Step 1: Fetch and recheck concurrent main.** If local main or origin/main advanced since Task 6, reconcile in the integration worktree and repeat its required verification after changes. Never merge newer main into the patch branch. Preserve unrelated work and never reset a dirty main checkout.
- [ ] **Step 2: Fast-forward clean main to the verified integration tip and push main.** Use repository hooks normally. No force push. Verify the fetched origin/main contains both integration_sha and patch_release_sha with merge-base ancestor checks. Record the pushed tip. Tagging is still a separate step.

### Task 8: Tag the patch release commit through the engine thread (orchestrator only)

**Files:** No source edits. Work at the clean patch release tip in the original patch worktree.

**Interfaces:**
- Consumes: origin/main ancestor proof from Task 7 and `patch_release_sha`.
- Produces: canonical annotated `v20.27.2` pointing exactly at patch_release_sha, published under OA25 through a KhaozEngine-rooted thread.

- [ ] **Step 1: Dispatch the release action through the KhaozEngine-rooted orchestrator thread.** In the patch worktree verify branch identity, clean tree, HEAD equals patch_release_sha, version is 20.27.2 and origin/main contains it. Fetch tags and recheck the tag is free. A collision stops this release action for the orchestrator to resolve, without an implementer changing the fixed version.
- [ ] **Step 2: Create and publish the canonical tag with the repository script.**

```bash
scripts/tag-release.sh release "fix residency promotion and add movement holds"
git push origin v20.27.2
```

Verify `git rev-parse 'v20.27.2^{commit}'` equals patch_release_sha and the remote tag resolves to the same commit. Never tag the integration commit or staged main. The main push is CI only, this tag push is the patch release.

### Task 9: Pack the tagged patch after its port is on origin/main (orchestrator only)

**Files:** No tracked source edits. Generated packages go to the guarded shared feed through its wrapper.

**Interfaces:**
- Consumes: clean tagged patch commit, published v20.27.2 and origin/main ancestor proof.
- Produces: verified 20.27.2 packages in the shared engine local-feed for the subsequent Grimhollow adoption task.

- [ ] **Step 1: Verify the current patch HEAD equals both patch_release_sha and `v20.27.2^{commit}`, the tree is clean and origin/main contains it.** Run the wrapper's `--dry-run` to confirm it accepts the tagged patch rather than staged main. This owner-approved patch-line sequence overrides the usual instruction to pack from main, which carries 20.33.0.
- [ ] **Step 2: Pack from that exact tagged commit.**

```bash
build-slot --heavy --label residency-20.27.2-pack -- scripts/pack-local-feed.sh
scripts/check-local-feed.sh
```

Expected wrapper exit `0`, released-commit and shared-feed guards pass, packages are version 20.27.2 and the feed check passes. No bare dotnet pack, private-feed substitution or PACK_RELEASED_OK override. Report the patch tag, release SHA, port SHA and feed validation to the game orchestrator. Do not edit Grimhollow's engine pin or implement its adoption in this plan.

## Self-review

- Spec coverage: E1 promotion CPU work, cover snapshots, apply caps, fallbacks, arrivals/borders, Update and legacy behavior map to Tasks 1 to 3. E2 API, queue timing, consumption/acknowledgement, gravity/support, teleport, leave and both heads map to Task 4. The exact release sequence maps to Tasks 5 to 9. Grimhollow adoption, gameplay, UI and art are excluded.
- Step scan: code tasks have named failing tests, red and green commands, concrete signatures and commit boundaries. Release tasks use observable guards instead of artificial TDD tests for git operations. No implementation bodies are prescribed where the signatures and assertions determine them.
- Type consistency: one shared queue uses TileProfileBuildInput/Output variants. The detached cover input's later base-type change is explicit. Cover uses plane -1. Ground generations distinguish FullGround from CoarseGround, and all generations are view-wide rather than resetting on resident creation.
- Review Focus: each of the five conditions has named tests in its owning task. CPU-build witnesses cover both inline and queued builds, not only mesh uploads. Mixed primes and distant exclusions have explicit fixture conditions.
- Proportion: tasks carry only architecture choices, interfaces, test assertions and verification/release gates. Existing algorithms and physics remain their executable contracts.

## Owner questions

None block implementation. E2 follows the fixed command-suppression contract. Any later request for a physical horizontal pin or commitment cancellation is a separate owner decision, not an implicit extension of HoldMovement.
