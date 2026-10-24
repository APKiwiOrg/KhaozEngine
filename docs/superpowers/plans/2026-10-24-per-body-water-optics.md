# Per-body water optics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `subagent-driven-development` (recommended) or `executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render submerged objects as immersed without turning them into false shorelines or hardening authored riverbanks.

**Architecture:** Bind immutable bed-depth fields to individual water submissions. Keep opaque scene depth separate, preserve the no-binding legacy path, and prepare and retire GPU resources outside the draw path. TileWorld supplies authored terrain data through an explicit adapter mode.

**Tech Stack:** C# and .NET 10, the engine's GPU abstraction, GLSL compiled to Metal, Direct3D 11 and Vulkan, xUnit headless and GPU tests.

**Spec:** [`WATER-OBJECT-OPTICS-DESIGN-2026-10-24.md`](../../design/WATER-OBJECT-OPTICS-DESIGN-2026-10-24.md), approved on 2026-10-24.

**Status:** Approved by the owner on 2026-10-24 for subagent-driven execution. The engine
orchestrator owns Tasks 1 through 7. The Grimhollow coordinator retains Task 8. Release authorization
remains separate and requires approval of the exact tag.

Task 6 execution amendment: the required saddle proof failed with bilinear-only bed reconstruction.
The [triangle amendment spec](../../design/WATER-OPTICS-TRIANGLE-AMENDMENT-2026-10-24.md) and
[sequential subplan](2026-10-24-water-optics-triangle-amendment.md) govern its correction. They supersede
the bilinear-only TileWorld and universal four-fetch requirements below. Direct bilinear fields retain
their original contract. Task 6 and Task 7 admission remain gated on the unchanged saddle proof.

## Global constraints

- Work in the isolated engine worktree on `feature/water-object-optics`. Preserve unrelated changes and stage and commit explicit paths.
- Read its AGENTS.md, reconcile current main before the first task, and create the ignored `local-feed` directory before restore. Recheck changed interfaces if main advanced.
- Keep the data per body and bounded patch. Do not reuse the scene-wide FFT bathymetry field or its missing-data sentinel for optics.
- Samples are finite signed rest depths. Positive is water depth. Zero and negative are known bank or land samples.
- Use R32Float and four bounded texel fetches for explicit bilinear interpolation. Lattice dimensions are 2 through 1024 inclusive.
- Coverage includes the drawable domain, certified displacement envelope and interpolation support. Incomplete opted-in data is refused, never silently switched to legacy scene depth.
- A missing binding is the explicit legacy mode. Legacy images and existing FFT shoaling and surf behavior remain unchanged.
- Immutable source bytes, scene ownership, handle generations, complete replacement and in-flight lifetime are mandatory.
- Unchanged draws perform no optical field allocation, upload or managed allocation. Moving creatures do not rebuild bed data.
- Share TileWorld fields across compatible row-run rectangles. Include every sampled height dependency in invalidation.
- Keep the intended 0.20 m bank feather. The 0.02 m material-only adjustment was rejected by the matched consumer proof.
- Keep the approved duck geometry, motion, placement, camera and river material fixed in the final consumer comparison.
- The native R8a renderer is not a dependency of this branch. Preserve its original-domain, canonical-resource, anchor and preparation contracts in the R8b handoff.
- Private consumer assets and images stay out of the public engine repository.
- Every local build, test, render or bake uses `build-slot`, with `--heavy` for full suites, renders and packing. No local stress, test loops or repeated-until-green runs.
- GPU changes require the complete hosted Metal, Direct3D 11 and Vulkan gate. Changed goldens are baked by their corresponding hosted backends and inspected.
- Do not bump the version while writing this plan. Select or ride the correct engine version only during integration after re-reading main, tags and the changelog.
- Private pack proof uses an explicit private `KHAOZENGINE_FEED`. Shared feed packing follows a verified main push. Release authorization and Grimhollow adoption remain separate gates.

## Review focus

1. Two bodies at the same XZ, sharing a look but using different reference levels, must never read the last submitted body's field. Tasks 3 and 4 pin independent bindings and order reversal.
2. A valid bank sample must remain distinct from missing coverage. Tasks 2, 5 and 6 pin signed samples, interpolation edges and unavailable source data without deep-water or legacy fallback.
3. A stale or foreign-scene handle may have the same slot index as a live resource. Task 3 pins owner and generation checks and retirement across replacement.
4. Origin rebasing, TAA jitter and MSAA must not shift the bed field relative to water or the opaque scene. Tasks 4 and 7 pin mapping and real GPU output.
5. A height edit or neighbour unload may happen while an older water generation remains visible. Tasks 5 and 6 pin complete dependency invalidation and prohibit pairing new geometry with an old field.

## Task order and gates

| Task | Deliverable | Prerequisite |
| --- | --- | --- |
| 1 | Public optical proof fixture and measured baseline | Approved plan |
| 2 | Immutable field payload and reference sampling math | Task 1 |
| 3 | Scene resource preparation, binding and lifetime | Task 2 |
| 4 | Bed-depth shader routing and early optical acceptance | Task 3 |
| 5 | Available authored-height reads and CPU field production | Accepted Task 4 images |
| 6 | TileWorld preparation, sharing and invalidation | Task 5 |
| 7 | Complete backend gate, documentation and engine integration | Task 6 |
| 8 | Released engine adoption and consumer acceptance | Engine release authorized and published |

Task 4 is a real stop gate. If the public skinned-object and bank capture remains unconvincing, revise
the approved design before expanding adapters or treating the current alpha compositor as sufficient.
No task may obtain a passing image by moving the duck, hiding submerged geometry or tightening the bank.

## Exact contracts

### CPU fields

Create `KhaozEngine.Render3D/WaterOpticsField.cs` and `KhaozEngine.Render3D/Internal/WaterOpticsMath.cs`.
The field constructor is `WaterOpticsField(int width, int height, ReadOnlySpan<float> restDepths)`.
It defensively copies the validated payload with `restDepths.ToArray()` during construction only.
Expose `Width`, `Height`, `ReadOnlySpan<float> RestDepths`
and checked `long SampleBytes`. Set `MaxDimension = 1024`. The engine has no general maximum-texture-size
capability query. This explicit bound follows its existing water-field limit, and GPU factory refusal
remains a reported preparation failure with complete rollback. Do not invent a capability member.

`WaterOpticsField.SampleDepth(float u, float v)` accepts finite normalized coordinates in `[0, 1]`.
It returns the signed interpolated value. It does not clamp land to zero or invent a missing-depth value.
The corresponding shader helper uses the same lattice coordinates and final-row convention.

For `sx = u * (width - 1)`, choose `x0 = min(floor(sx), width - 2)` and `tx = sx - x0`, and do the
same for Z. Interpolate the four samples with those weights. The final row and column therefore use
their last two valid samples, with weight one at the outer edge.

`WaterOpticsMath.BedDepth(float restDepth, float referenceSurfaceY, float displacedSurfaceY)` returns
`max(0, restDepth + displacedSurfaceY - referenceSurfaceY)`. Inputs have already passed finite-value
validation. A changing water level leaves the source reference level fixed unless the producer rebuilds
or deliberately re-anchors the whole body.

The owning tests are `KhaozEngine.Render.Tests/Render3D/WaterOpticsFieldTests.cs`. They assert defensive
copying, exact corners, interior interpolation, negative bank values, the final row and column, invalid
dimensions, checked length, non-finite rejection and representative 2 m, 30 m and 100 m depths.

### Scene ownership and mappings

Create `WaterOpticsBinding.cs` in `KhaozEngine.Render3D` with these public value types:

```csharp
public readonly record struct WaterOpticsHandle(Guid Owner, int Index, int Generation);
public readonly struct WaterOpticsBinding
{
    public WaterOpticsBinding(WaterOpticsHandle field, Vector3 origin, Vector2 size);
    public WaterOpticsHandle Field { get; }
    public Vector3 Origin { get; }
    public Vector2 Size { get; }
    public bool IsBound { get; }
}
```

The handle's `IsValid` property requires a nonempty owner, nonnegative index and positive generation.
Default binding means legacy. A binding constructor rejects an invalid handle, non-finite origin or
nonpositive/non-finite size. Origin XZ is the rectangle minimum, Origin Y is the reference surface,
and Size gives its positive XZ extent. These coordinates use the same absolute frame as `WaterPlane`.
Local lattice coordinates remain `[0, Size]`, independent of the scene origin.

Append `WaterOpticsBinding optics = default` to the existing `WaterPlane` constructor and add `Optics`
and `WithOptics(WaterOpticsBinding optics) -> WaterPlane`, preserving every other plane value.
`RelativeWaterPlanes()` preserves the binding and subtracts the latched origin from its Origin exactly
once. The shader then works in bounded render-relative coordinates. It must not sample using the
absolute-coordinate noise input `wpAbsXz`.

The same contract file defines `WaterOpticsStats(int LiveFields, long LiveTextureBytes,
long RetiredTextureBytes, long UploadCount)` as a public readonly record struct. Expose its snapshot
through `Scene3D.WaterOpticsStats`. Texture payload bytes are checked sample count times four, with
retired bytes kept until disposal. These counters do not claim to measure opaque driver overhead.

`Scene3D.WaterOptics.cs` exposes `LoadWaterOpticsField(WaterOpticsField field) -> WaterOpticsHandle` and
`UnloadWaterOpticsField(WaterOpticsHandle handle) -> void`, plus
`GetWaterOpticsPadding(WaterLook? look = null) -> float` for producer preflight against the effective look.
Default unload is a no-op. Foreign, stale or
double-freed handles throw `ArgumentException`. Load after scene disposal throws `ObjectDisposedException`.

The internal `WaterOpticsResources(IGpuDevice device, GpuRetireQueue retired)` owns one Guid identity,
a `MeshSlotMap`, immutable field references and textures. It produces `Load(field)`, `Unload(handle)`
and `Resolve(handle) -> Entry`, where Entry exposes Field, Texture and Handle to the water renderer.
Allocate a slot only after successful upload. Unload invalidates the slot immediately and retires GPU
objects through the supplied queue. Do not use the mutable bathymetry uploader as the lifetime model.

Recreation means constructing a new scene/device and explicitly loading retained CPU fields again.
There is no existing automatic device-loss recovery loop to extend. Old handles are rejected by the
new owner. This satisfies the spec's reload path without introducing a separate recovery subsystem.

### Coverage and pre-recording preparation

Execution ruling: `Scene3D.PrepareFrame(int viewportWidth, int viewportHeight)` supplies the dimensions
needed to size targets before preparing optical resource sets. The original no-argument overload remains
for legacy callers. `GameApp3D` forwards frame dimensions during its pre-record phase, and public fixture
captures use the same preparation contract. `Render3DSurface` must not repair an unprepared bound frame
after recording starts. Bound recording refuses changed or missing prepared viewport/target inputs before
late target recreation. Tests cover first-frame sizing, resize, failed preparation retry, changed settings
and host forwarding. Repeated preparation remains idempotent. This closes the plan's original viewport
gap without weakening its preparation-before-recording requirement.

Add `WaterOpticsCoverage.Validate(in WaterPlane plane, WaterSettings effective) -> void` as internal
headless logic. An unbound plane returns unchanged. A bound plane requires its expanded rectangle to
fit the binding rectangle. Expand XZ by `WaterSwellReach.Of(amplitude, wavelength, steepness).X` using
the effective per-plane look. Invalid bounds or coverage throw `ArgumentException`.

Bound `FftOcean` requests throw `NotSupportedException` before recording. The engine has no certified
FFT displacement envelope, and significant wave height is not such a certificate. Unbound FFT water
keeps its existing path. Supporting bound FFT optics later requires a real envelope contract.

`WaterRenderer.PrepareOpticsFrame(RenderResources targets, in FramePrepare frame)` runs from
`Scene3D.PrepareFrame()` after the existing preparers and before command recording. It resolves fields,
validates coverage, ensures UBO capacity and prepares all required resource sets. It records the target,
UBO, ocean and bathymetry identities, bindings and effective displacement inputs used. Draw refuses a
changed or released input instead of allocating a replacement resource set while recording.

Move bathymetry Update into `PrepareOpticsFrame` before preparing sets, under `device.water.prepare`. The `PrepareFrame()`
path keeps it in Draw under its `device.water.bathymetry` owner. Existing ocean Record remains in Draw.
Latch `_framePrepared` only after every preparation succeeds. Reject new water submissions after
preparation. Preserve the legacy standalone renderer path where no optics is bound.

### TileWorld opt-in and source provenance

Add `TileWaterDepthMode { SceneDepth, AuthoredBed }` and an init-only
`TileWorldViewOptions.WaterDepthMode`, default SceneDepth. The view snapshots the choice at construction.
This is the deliberate compatibility policy. Grimhollow selects AuthoredBed when adopting the release.

Add `ITileWorldWaterOpticsScene` with the same Load/Unload/GetWaterOpticsPadding signatures as Scene3D. The existing
`Scene3DTileWorldScene` implements it. An authored-bed view with an unsupported scene throws
`NotSupportedException` before uploading a region. Do not change the default no-op water adapter into
a false claim that it supports optical fields.

In `TileWorldDocument.HeightReads.cs`, add:

```csharp
public readonly record struct TileCornerHeightRead(float Height, RegionCoord Owner, int SourcePlane);
public bool TryReadCornerHeight(int x, int z, int plane,
    Span<RegionCoord> inspectedRegions, out int inspectedCount,
    out TileCornerHeightRead value, out RegionCoord missingRegion);
```

The caller supplies capacity four. Report unique inspected regions in the existing owner, west, south,
southwest priority. A known but unloaded higher-priority region is unavailable, even if a lower-priority
fallback is loaded. No resolved owner is unavailable for optics, rather than the legacy lifted-zero
fallback. A loaded region with implicit flat heights is valid. SourcePlane records the requested authored
plane or plane 0 for a derived lift. Preserve centimetre saturation. Share the resolution kernel with
the existing methods, whose behavior and allocation contract remain unchanged.

`TileWaterOptics.TryBuild(document, region, plane, referenceSurfaceY, paddingMetres, out build,
out failure) -> bool` is a GPU-free Render3D adapter helper. Build contains Field, Origin, Size and the
read dependencies. Failure identifies the missing region or an unsupported field size. Align samples
to the authored corner lattice, with one lattice-cell halo beyond displacement padding. Use world-Z
ordering through `TileWorldSpace`, including its negative tile-Z convention.

The resulting bilinear field matches the authored `HeightAt` function. It is not claimed to equal the
piecewise-triangle interior of a non-planar rendered terrain quad. Task 6 must test and render a saddle
bank, and the visual gate rejects a bad shoreline rather than hiding that distinction.

Execution ruling: the float mapping must preserve its lattice within a positional budget of
`min(0.0001 metres, TileSize / 1024)`. Validate both axes, all encoded vertices, stored affine positions
and their mutual displacement. Refuse collapsed or non-finite mappings as `FieldTooLarge` before
publishing a field. Account for local UV arithmetic roundoff separately from absolute-position
quantization. The positional budget is below one thousandth of a tile and small relative to the
0.20 m bank feather. It is not a bound on vertical error for arbitrary terrain slopes. Tests retain
ordinary fractional tile sizes beyond the origin and reject whole-sample shifts at large coordinates.

`TileWaterOpticsBuild` contains Field, Origin, Size and
`IReadOnlyList<TileWaterOpticsDependency> Dependencies`. The internal dependency record carries Region
and nullable Plane, with null meaning an availability dependency. The public
`TileWaterOpticsFailure` record carries Region, Plane, ReferenceSurfaceY, Kind and optional MissingRegion.
Failure kinds are MissingHeightData and FieldTooLarge. GPU creation failures propagate through the
view's rollback path rather than becoming an apparently successful preparation.

## File responsibilities

| Files | Responsibility |
| --- | --- |
| `WaterOpticsField.cs`, `Internal/WaterOpticsMath.cs` | Owned CPU samples and reference interpolation/depth math |
| `WaterOpticsBinding.cs`, `Internal/WaterOpticsCoverage.cs` | Handle identity, per-submission mapping and complete coverage |
| `Rendering/WaterOpticsResources.cs` | Texture preparation, generation checks, diagnostics and retirement |
| `Scene3D.WaterOptics.cs`, `Rendering/WaterRenderer.Optics.cs` | Public scene forwarding and prepared per-field resource bindings |
| `Internal/ShaderSources.WaterOptics.cs` | Four-fetch sampling and independent optical bed depth |
| `TileWorldDocument.HeightReads.cs`, `TileCornerHeightRead.cs` | Available height reads with source provenance |
| `TileWaterOptics.cs`, `TileWaterDepthMode.cs` | CPU producer and explicit adapter selection |
| Existing `TileWorldView.Water.cs` and publication hooks | Complete region generations, sharing, invalidation and release |

Paths above are within their corresponding Render3D, TileWorld or TileWorld.Render3D package. Task
file lists below are authoritative. No new package or third-party dependency is needed.

### Task 1: Public optical proof fixture and baseline

**Files:** Create `KhaozEngine.Render.Tests/Gpu/WaterOpticsProofScene.cs` and
`KhaozEngine.Render.Tests/Gpu/WaterOpticsProofTests.cs`.

**Interfaces:** Produce a lazy `WaterOpticsProofScene : IDisposable`, `Prepare(Action<Scene3D>)`, and
`Capture(WaterOpticsScenario scenario, in WaterOpticsCase settings,
Func<WaterPlane, WaterPlane>? bind = null) -> WaterOpticsImage`. WaterOpticsImage carries RGBA, dimensions,
projected sample positions and `Sample(string name) -> Vector3`, averaging a 5 by 5 pixel patch.
WaterOpticsCase is a readonly record struct with `float BedDepth = 2f`, `float ObjectDepth = 0.103f`,
`Vector3 Offset = default`, `int Samples = 1`, `bool Temporal = false` and `bool ReverseOrder = false`.
WaterOpticsImage exposes `byte[] Rgba`, `int Width`, `int Height` and
`IReadOnlyDictionary<string, Vector2> Samples`. Scenario values are
FlatBed, SkinnedBody, SlopingBank, Ledge, DeepShelf, StackedBodies and AdjacentFields.

- [ ] Build the fixture from `OceanFocusScene`'s lazy shared-device pattern and the real skinned tube in
  `BlobDecalGhostHoleGoldenTests`. Use a 384 by 384 perspective view, a 0.103 m submerged-object sample,
  a dry sample and a sloping bank. Keep raw-depth and camera projection conventions intact.
- [ ] Add `FixtureShowsSkinnedObjectAndBank` and `AgedFixtureMatchesFreshScene`. Assert non-background
  object and bank samples and matching aged/fresh output under the existing backend comparison tolerance.
  This is diagnostic fixture work, not a test that blesses the known optical defect.
- [ ] Run `build-slot --heavy --label water-proof-baseline -- env KE_GPU_TESTS=1 KE_WATER_OPTICS_OUTPUT=/tmp/khaozengine-water-optics/baseline dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~WaterOpticsProofTests`.
  Require passed GPU facts and zero skipped selected facts. Inspect the images and preserve baseline metrics.
- [ ] Commit explicit fixture paths as `test(water): add public submerged-object optical proof`.

### Task 2: Immutable field payload and reference math

**Files:** Create `KhaozEngine.Render3D/WaterOpticsField.cs`,
`KhaozEngine.Render3D/Internal/WaterOpticsMath.cs` and
`KhaozEngine.Render.Tests/Render3D/WaterOpticsFieldTests.cs`.

**Interfaces:** Produce the CPU field and BedDepth signatures in Exact contracts. No GPU ownership yet.

- [ ] Write the field tests first, including these concrete assertions:

```csharp
float[] values = [-0.2f, 2f, 30f, 100f];
var field = new WaterOpticsField(2, 2, values);
values[0] = 99f;
Assert.Equal(-0.2f, field.SampleDepth(0, 0));
Assert.Equal(100f, field.SampleDepth(1, 1));
Assert.InRange(MathF.Abs(field.SampleDepth(.5f, .5f) - 32.95f), 0, .00001f);
Assert.Equal(16L, field.SampleBytes);
Assert.Equal(3f, WaterOpticsMath.BedDepth(2, 5, 6));
```

- [ ] Add `DimensionsAndPayloadAreValidated`, `NonFiniteDepthsAreRefused`, `NegativeBanksRemainSigned`,
  `LastRowAndColumnNeverReadPastThePayload` and `ReferenceLevelStaysFixedWhenWaterMoves`.
  Dimensions outside 2..1024 and non-finite coordinates are argument errors, never clamped inputs.
- [ ] Run `build-slot --label water-field-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~WaterOpticsFieldTests` and record the expected missing-contract failure.
- [ ] Implement the owned payload, checked size, explicit bilinear algorithm and depth math from Exact contracts.
- [ ] Run the same filter with label `water-field-green`. Require all selected tests passing, then commit the
  three explicit files as `feat(water): define immutable optical depth fields`.

### Task 3: Scene-owned resources and submission contracts

**Files:** Create `KhaozEngine.Render3D/WaterOpticsBinding.cs`,
`KhaozEngine.Render3D/Internal/WaterOpticsCoverage.cs`,
`KhaozEngine.Render3D/Rendering/WaterOpticsResources.cs`,
`KhaozEngine.Render3D/Scene3D.WaterOptics.cs`,
`KhaozEngine.Render.Tests/Render3D/WaterOpticsResourceTests.cs` and
`KhaozEngine.Render.Tests/Render3D/WaterOpticsCoverageTests.cs`.
Modify `WaterPlane.cs`, `Scene3D.cs`, `Scene3D.RenderOrigin.cs`, `Rendering/WaterRenderer.cs`,
`Scene3D.Unload.cs` where its lifecycle calls are needed, and the scoped `.filesize-baseline` row.

**Interfaces:** Consume Task 2 fields. Produce the handles, bindings, scene Load/Unload/GetWaterOpticsPadding API,
WaterOpticsResources and coverage validator defined above. Resource diagnostics expose live count,
live and retired texture payload bytes, and cumulative upload count. Payload accounting is not a claim
to measure opaque driver overhead. CPU payload ownership remains explicit through the retained Field.

- [ ] Write `LoadUploadsR32ExactlyOnce`, `SourceBytesCannotChangeAfterUpload`,
  `ForeignOwnerAndStaleGenerationAreRefused`, `ReleaseDefersDestructionButInvalidatesTheHandle`,
  `FailedCreationPublishesNoSlot`, and `NewSceneReloadsCpuFieldAndRejectsOldHandle`.
  Use FakeGpuDevice, factory failure injection and `GpuRetireQueue.CreateFrameCounted(device, 2)`.
  Check invalidation immediately and physical disposal only after the configured retirement delay.
  Test the maximum allowed field size with actual sampled R32Float textures on the backend gate.
- [ ] Add coverage cases for exact rectangle edges, effective per-plane procedural swell, incomplete padding,
  default legacy binding and FftOcean refusal. Pin `BindingSurvivesOriginReduction` at zero and shifted origins.
- [ ] Run `build-slot --label water-resource-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter 'FullyQualifiedName~WaterOpticsResourceTests|FullyQualifiedName~WaterOpticsCoverageTests'` and record red.
- [ ] Implement preparation and rollback with `GpuPixelFormat.R32Float`, MeshSlotMap and the existing retirement
  queue. Resolve and validate live bound handles at submission. Move the complete DrawWater member and its
  documentation into the new Scene3D partial, rather than growing the frozen Scene3D.cs file. Preserve bindings
  in RelativeWaterPlanes. Ratchet down only the affected baseline entry after the move.
- [ ] Run the same filter with label `water-resource-green`, plus existing Scene3D unload tests. Require passing
  tests and zero warnings. Commit only the task's explicit paths as `feat(water): own optical fields per scene`.

### Task 4: Bed-depth shading and early optical acceptance

**Files:** Create `KhaozEngine.Render3D/Rendering/WaterRenderer.Optics.cs`,
`KhaozEngine.Render3D/Internal/ShaderSources.WaterOptics.cs`,
`KhaozEngine.Render.Tests/Render3D/WaterOpticsBindingTests.cs` and
`KhaozEngine.Render.Tests/Gpu/WaterOpticsGoldenTests.cs`.
Modify `Rendering/WaterRenderer.cs`, `.Routing.cs`, `.SlotUpload.cs`, `Internal/ShaderSources.Water.cs`,
`Scene3D.FramePrepare.cs`, `Render3D/UboLayoutTests.cs`, `Render3D/WaterTestFrames.cs` and the Task 1 fixture.
The viewport preparation ruling also includes `Scene3D` target-sizing integration, `Render3DSurface`,
`KhaozEngine.Game.Render3D/GameApp3D.cs` and the focused host preparation tests.

**Interfaces:** Consume Task 3 Resolve and bindings. Produce
`WaterRenderer.PrepareOpticsFrame(RenderResources targets, in FramePrepare frame)` and shader helper
`float opticalBedDepth(vec3 waterRelative, float legacyDepth)`. Resource-set lookup is prepared before
recording and indexed by the current submission's field, never by look identity.

- [ ] Write `BoundBedDoesNotBecomeAShoreWhenASkinMoves`, `AboveWaterSkinStillOccludesWater`,
  `TwoLevelsAndReversedSubmissionKeepTheirOwnFields`, and `LegacyNoBindingMatchesItsControl` using Task 1.
  Use flat controlled lighting and equal-colour bed and submerged skin to isolate water coverage. The bound
  wet sample must match its bed-only control within RGB distance 0.04, while the above-water sample changes
  by less than 0.02. Save images and projected sample positions. Do not tune thresholds after seeing a failure.
  Prepare the fields through the fixture's Prepare callback. The draw-time bind callback only selects an
  already prepared binding and calls WaterPlane.WithOptics, with no upload or field creation.
- [ ] Run `build-slot --heavy --label water-optics-gpu-red -- env KE_GPU_TESTS=1 KE_WATER_OPTICS_OUTPUT=/tmp/khaozengine-water-optics/red dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~WaterOpticsGoldenTests`.
  Require the submerged-object cases to fail for the old depth source, not for a fixture or binding error.
- [ ] Append OpticsRect and OpticsParams as two Vector4 values to WaterUbo, making 1088 bytes while keeping
  its 1280-byte aligned slot. OpticsRect holds relative minimum XZ and inverse size. OpticsParams holds
  relative reference Y, enabled, width and height. Keep existing bindings 0..9 unchanged and append optics
  texture/sampler at 10/11 after atmosphere. Use the existing neutral bathymetry texture and point sampler
  for disabled optics, with no new field allocation.
- [ ] Implement four-fetch depth sampling in render-relative space. Route palette RGB/alpha, ordinary shore
  foam and shore fade to bed depth only when enabled. Keep opaque depth reconstruction, hardware occlusion,
  FFT inputs and the legacy arithmetic intact. Add no scene-colour sampling or refraction pass.
- [ ] Prepare UBO capacity, bathymetry updates and per-field resource sets before recording. Retire every
  invalidated set, including the legacy set on target changes. Cache against target identity/generation,
  UBO identity, ocean texture, bathymetry texture and field handle. Draw verifies the prepared bindings and
  effective displacement inputs. Failed preparation must not latch `_framePrepared` as successful.
- [ ] Add `InputsChangedAfterPreparationAreRefused`, `ReleasedQueuedFieldIsRefusedBeforeUse`,
  `TargetAndUboReplacementRetireAllAffectedSets`, and `UnchangedDrawAllocatesAndUploadsNothing`.
  Use the repository allocation harness and serialized allocation collection, with a warmed single frame.
  Update UBO and register-agreement tests for the new block and appended bindings.
  Rename WaterBlock_Preserves1056Bytes to WaterBlock_Preserves1088Bytes and pin both new member offsets.
- [ ] Run the GPU filter with label `water-optics-gpu-green`, then the focused headless filter
  `FullyQualifiedName~WaterOptics|FullyQualifiedName~UboLayoutTests|FullyQualifiedName~WaterBathymetryUploadGateTests`.
  Require zero skipped selected GPU facts, passing headless tests and inspectable images.
- [ ] Review the skinned object and soft bank images before Task 5. If they remain unconvincing, stop here
  and amend the design. Otherwise commit explicit paths as `fix(water): separate bed optics from submerged objects`.

### Task 5: Available height reads and TileWorld field production

**Files:** Create `KhaozEngine.TileWorld/TileCornerHeightRead.cs`,
`KhaozEngine.TileWorld/TileWorldDocument.HeightReads.cs`,
`KhaozEngine.TileWorld.Tests/TileCornerHeightReadTests.cs`,
`KhaozEngine.TileWorld.Render3D/TileWaterOptics.cs` and
`KhaozEngine.Render.Tests/TileWorld/TileWaterOpticsTests.cs`.
Modify the shared corner-resolution kernel in `TileWorldDocument.Heights.cs`.

**Interfaces:** Produce the TryReadCornerHeight contract above and
`TileWaterOptics.TryBuild(TileWorldDocument document, RegionCoord region, int plane,
float referenceSurfaceY, float paddingMetres, out TileWaterOpticsBuild build,
out TileWaterOpticsFailure failure) -> bool`.
Build carries Field, Origin, Size and unique height/availability dependencies. Failure carries a kind and
missing region. Neither type contains a GPU handle. Dependency provenance records inspected regions,
the requested plane and the resolved SourcePlane, including negative availability lookups.

- [ ] Write `KnownUnloadedOwnerDoesNotUseFallback`, `EdgeFallbackRecordsInspectedRegions`,
  `NoOwnerIsUnavailableRatherThanLiftedZero`, `ImplicitFlatRegionIsAvailable`, and
  `DerivedPlaneReportsBaseSourceAndPreservesSaturation`. Existing CornerHeight and HeightAt results must
  remain unchanged. Pin capacity-four span behavior and no allocation in the old corner-read path.
  Place the allocation assertion in the existing Render.Tests allocation harness.
- [ ] Run `build-slot --label water-height-red -- dotnet test KhaozEngine.TileWorld.Tests/KhaozEngine.TileWorld.Tests.csproj -c Release --filter FullyQualifiedName~TileCornerHeightReadTests` and record red.
- [ ] Implement the shared resolver and provenance-aware strict read without duplicating corner priority.
  The strict read performs no I/O. It reports missing data for its caller to prepare.
- [ ] Write `FieldMatchesAuthoredBilinearHeightAt`, `NegativeWorldZUsesTheCorrectRows`,
  `SameLevelRowRunsShareOneBuildDescription`, `DistinctLevelsHaveDistinctReferenceMappings`,
  `MissingHaloRefusesTheWholeBuild`, and `OversizedFieldIsRefusedWithoutDownsampling`.
  Test non-planar corners explicitly rather than claiming bilinear heights equal rendered triangles.
- [ ] Run the new TileWaterOpticsTests filter red, implement the aligned lattice with one-cell halo and
  signed reference depths, then rerun both focused projects green. Commit explicit paths as
  `feat(tile-world): prepare optical bed samples with provenance`.

### Task 6: TileWorld preparation, sharing and invalidation

**Files:** Create `KhaozEngine.TileWorld.Render3D/TileWaterDepthMode.cs`,
`KhaozEngine.TileWorld.Render3D/ITileWorldWaterOpticsScene.cs`,
`KhaozEngine.TileWorld.Render3D/TileWorldView.WaterOptics.cs`,
`KhaozEngine.Render.Tests/TileWorld/TileWorldViewWaterOpticsTests.cs` and
`KhaozEngine.Render.Tests/TileWorld/RecordingTileWorldScene.WaterOptics.cs`.
Modify `ITileWorldScene.cs`, `TileWorldView.cs`, `.Water.cs`, `.Flush.cs`, `.GroundResidency.cs`,
`.ProfileBuilds.cs` and the existing recording adapter declaration in `TileRenderTestData.cs`.

**Interfaces:** Produce the init-only depth mode and optional preparation port above.
Expose `TileWorldView.WaterOpticsFailures -> IReadOnlyList<TileWaterOpticsFailure>` as bounded preparation
diagnostics. Rebuild that snapshot only when preparation state changes. The view owns private epochs
for region availability and plane heights, driven by its existing dirty/load/unload contract.

- [ ] Write `LegacyModeKeepsDefaultSceneCompatibility`, `AuthoredModeRequiresPreparationCapability`,
  `PrepareUploadsBeforeAnyWaterSubmission`, `UnchangedDrawReusesFields`, and
  `ChangingOnlyColourDoesNotUploadBedData`. Recording scenes must record real preparation/release calls.
- [ ] Write `HeightEditPublishesGeometryAndFieldTogether`, `NewEarlierCornerOwnerInvalidatesFallback`,
  `BasePlaneEditInvalidatesDerivedFields`, `NeighbourUnloadCannotPublishAGuessedBed`,
  `StaleAsyncResultCannotReplaceCurrentField`, `FailedReplacementKeepsOnlyACompleteGeneration`, and
  `UnloadAndDisposeReleaseSharedFieldsExactlyOnce`. Include cleanup of failed, never-published requests.
- [ ] Run `build-slot --label tile-water-optics-red -- dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~TileWorldViewWaterOpticsTests` and record red.
- [ ] Implement field preparation in LoadGameplayRegion, Flush and accepted ApplyGround/ProfileBuild paths.
  Ask GetWaterOpticsPadding during preparation and retain that coverage witness with the generation.
  Resolve known height dependencies with Source.EnsureLoaded only in preparation, never in DrawWaterPlanes.
  If coverage remains unavailable, retain a diagnostic and leave the requested generation unpublished.
  Do not repeatedly perform I/O for an unchanged refused dependency state.
- [ ] Publish replacement terrain and optical fields as one generation. Keep an old generation only as its
  complete validated geometry/field snapshot. Track every inspected availability dependency and requested
  or base height plane through local epochs. MarkDirty and load/unload notifications invalidate dependants.
  Share one field per compatible region/plane/reference-level generation. Release it after its last owner.
- [ ] Make DrawWaterPlanes a submit-only walk over prepared generations. Add a saddle-bank GPU case and
  compare with the intended authored terrain and soft shoreline. Reject a visible dry hole or hard cut.
- [ ] Run the new filter and existing `TileWorldViewWaterTests`, `TileWaterPlanesTests` and
  `TileWorldViewSharedResourceTests` green. Commit explicit paths as
  `feat(tile-world): publish prepared water optics with region generations`.

### Task 7: Backend gate, documentation and engine integration

**Files:** Extend `KhaozEngine.Render.Tests/Gpu/WaterOpticsGoldenTests.cs` and its fixture. Update
`KhaozEngine.Render3D/README.md`, `KhaozEngine.TileWorld.Render3D/README.md`, `docs/USING-KHAOZENGINE.md`,
`docs/RENDER-PIPELINE.md`, the approved spec's status and `docs/INDEX.md`. Create
`docs/design/WATER-OPTICS-R8B-HANDOFF-2026-10-24.md`. Version/changelog declarations are selected only here.

**Interfaces:** Produce accepted public evidence and the native handoff. No unpublished MapDoc.Render3D
API is invented. The handoff defines how prepared bounded-water owners carry the new handle, local
mapping, coverage witness, byte charges and source digest after their native anchor reduction.

- [ ] Add `BedDepthAt2_30_100MetresMatchesItsReference`, `SubmergedLedgeCreatesNoFalseShoreBand`,
  `StackedCaveBodiesKeepIndependentFields`, `AdjacentFieldsMatchOneContinuousField`,
  `RenderOriginShiftKeepsOpticalSampling`, `TemporalJitterAndMsaaUseCurrentDepth`, and
  `MaximumFieldR32SamplesOnThisBackend`. Match the bound wet sample to its corresponding bed-only reference
  within RGB distance 0.04. Use a ledge 0.3 m below water and an XZ origin shift of (4096, -8192) with
  procedural colour variation disabled in that mapping test. Extend aged/fresh parity over fields and
  target sizes. Preserve existing legacy water and FFT checks.
- [ ] Run focused local GPU facts once through build-slot with KE_GPU_TESTS=1 and confirm zero skipped
  selected cases. Save measurements and contact sheets under `/tmp/khaozengine-water-optics/`.
- [ ] Use the relational WaterOpticsGoldenTests and saved evidence for the new paths. This plan adds no
  golden grid. Existing legacy grids must remain unchanged. Investigate any grid failure before considering
  a design amendment and an attributable hosted rebake. Never bake to hide a failed assertion.

```bash
gh workflow run cross-platform-gpu.yml --repo APKiwiOrg/KhaozEngine --ref feature/water-object-optics -f legs=all -f tier=push -f bake=false
```

- [ ] Record the run ID and inspect every leg. The final verify leaves renderTestFilter empty. A separately
  approved golden change uses bake=true and then bake=false sequentially on the branch, never concurrently.
  Download only needed `goldens-<backend>`
  or `golden-evidence-<backend>` artifacts, inspect them, and remove downloaded bulk after review.
- [ ] Complete the doc sweep for scene-wide depth claims, fallback, strict FFT refusal, the 1024 limit,
  explicit scene recreation and the TileWorld opt-in. Record the native R8b adapter as an open handoff.
  Keep #1300 open for its remaining acceptance work. Link the consumer gap and engine issue with full URLs.
  Follow the repository's parity handoff convention, including the engine parity label and a consumer
  needs/upstream issue. Reuse matching open issues rather than filing duplicates.
- [ ] Re-read main, remote main, tags and version, reconcile concurrent work, then select or ride one additive
  minor for the batch. Update CHANGELOG and all guarded declarations together. Run the following final gate.

```bash
build-slot --label water-optics-build -- dotnet build KhaozEngine.slnx -c Release
build-slot --heavy --label water-optics-suite -- dotnet test KhaozEngine.slnx -c Release --no-build --filter 'Category!=LiveSocket'
build-slot --label water-optics-format -- dotnet format KhaozEngine.slnx --verify-no-changes --no-restore
```

- [ ] Run whole-tree dash, prose, size and instruction guards and check-doc-versions. Resolve review findings.
  Re-dispatch the complete GPU gate if reconciliation changed shaders or goldens. A green push subset is
  not the complete gate. Apply only the repository's documented tracked-flake policy with exact evidence.
- [ ] Obtain an independent whole-branch review against the approved spec and this plan. Verify each finding
  against the actual code, correct it and rerun the affected checks before the integration step below.
- [ ] Check the release lock, merge and push the verified engine branch through its orchestrator, then run
  `build-slot --heavy --label water-optics-pack -- scripts/pack-local-feed.sh` from main after origin/main
  contains the commit. Use an explicit private KHAOZENGINE_FEED for any earlier pack proof.

### Task 8: Released consumer adoption and final acceptance

**Files:** In the existing Grimhollow task branch, modify `Directory.Build.props`, `.config/dotnet-tools.json`,
the refreshed vendor feed and generated navigation, `docs/ENGINE-INTEGRATION.md`, `docs/PLAY_CHANGELOG.md`,
`Grimhollow.Core/World/HollowmereWorld.cs`, `tools/SnapshotTool/ShowcaseStage.cs`,
`tools/SnapshotTool/ShadowGrainCapture.cs`, `tools/SnapshotTool/WorldSpotShots.cs` and the outdoor-world
setup in `tools/SnapshotTool/Program.cs`. Review the intentionally separate IndoorLightShots and TerrainShots
option construction without changing a non-water diagnostic merely to match a textual search.
Preserve the accepted duck files. Test through `Grimhollow.Tests/World/HollowmereViewerFrameTests.cs`
and the existing real-water capture metrics.

**Interfaces:** Consume a published engine version containing the accepted field API and TileWorld mode.
Produce a consumer that explicitly selects AuthoredBed and passes the existing matched duck/bank capture.

- [ ] Use the release-khaozengine skill at the release handoff. A game-rooted session does not create the
  engine tag. Obtain the required exact release authorization and use an engine-rooted release session.
  Confirm the published packages and feed before changing the shipping game pin.
- [ ] Apply the game repository's complete engine-pin procedure, including tools, vendor packages,
  capability sweep and navigation rebake. Route long bakes through its documented hosted workflow.
- [ ] Select AuthoredBed in the real viewer and its shared snapshot setup. Add an assertion that those
  paths request the same mode. Preserve the default river material, duck geometry and 8 cm lowering.
- [ ] Repeat `duck-swim` and its bank control with warmed Native TAA. Compare against the retained baseline.
  Require convincing immersion and an acceptable bank. Keep paddle, wing and ripple behavior unchanged.
  A failed consumer image returns to the optical acceptance gate, not to a new creature offset.
- [ ] Run the game repository's Release solution build, full suite and format check after reconciling main.
  The known #523 handshake issue is evaluated under the game's own rules, not an assumed engine waiver.
  Report every failure. Do not repeat runs until a flake happens to pass.

```bash
build-slot --label duck-water-build -- dotnet build Grimhollow.slnx -c Release
build-slot --heavy --label duck-water-tests -- dotnet test Grimhollow.Tests/Grimhollow.Tests.csproj -c Release
build-slot --label duck-water-format -- dotnet format Grimhollow.slnx --verify-no-changes --no-restore
```
- [ ] Obtain the final look verdict and hand over one copy-ready Debug --with-server command for feel/input
  validation when needed. Once all gates pass, merge and push the game work under its normal workflow.

## Self-review and execution handoff

The plan maps the spec's immutable data and precision rules to Task 2, scene ownership and complete
coverage to Tasks 3 and 4, TileWorld source and lifetime to Tasks 5 and 6, and backend/native/delivery
requirements to Tasks 7 and 8. Task 1 establishes the public control fixture. All five Review Focus rows
have named tests. API names and defaults above are shared across tasks rather than redefined in briefs.

The owner approved this plan and selected subagent-driven execution on 2026-10-24. Field preparation,
GPU lifetime and terrain publication each retain an independent rejection gate. Keep every task's
commit and review evidence with the plan's progress record.

The engine-rooted execution thread is bound to this existing engine worktree and owns Tasks 1 through 7
and the engine release handoff. The Grimhollow coordinator retains Task 8. Implementation proceeds
sequentially with one implementer at a time, fresh scoped task reviews and a final whole-branch review.
