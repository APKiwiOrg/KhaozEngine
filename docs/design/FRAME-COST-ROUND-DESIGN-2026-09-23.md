# Frame cost round

Status: spec approved by the owner on 2026-09-23. Implementation plan written and approved for subagent-driven execution. Both owner decisions in Plan amendments were accepted.
Date: 2026-09-23.
Issues: [#1110](https://github.com/APKiwiOrg/KhaozEngine/issues/1110),
[#1111](https://github.com/APKiwiOrg/KhaozEngine/issues/1111),
[#1112](https://github.com/APKiwiOrg/KhaozEngine/issues/1112),
[#1113](https://github.com/APKiwiOrg/KhaozEngine/issues/1113),
[#1114](https://github.com/APKiwiOrg/KhaozEngine/issues/1114),
[#1115](https://github.com/APKiwiOrg/KhaozEngine/issues/1115),
[#1116](https://github.com/APKiwiOrg/KhaozEngine/issues/1116).
Consumer: [Grimhollow #325](https://github.com/APKiwiOrg/Grimhollow/issues/325), pinned at 20.0.0 and waiting.

## Outcome

A Grimhollow client frame spends most of its CPU inside `Scene3D.RenderInternal`, and most of that is
bookkeeping that repeats identical work every frame. This round removes the largest repeated costs without
changing what a frame draws, apart from two bounded and documented pixel differences.

The target is Grimhollow's town path recording in about 3 ms instead of 8.5 ms on native Metal, with the steady
per-frame allocation that forces a gen0 gone. Six of the seven items are backend independent or Windows specific,
so D3D11 players get most of the win. The round adds no public API and no setting.

## Evidence

Measured on engine 20.0.0 with Grimhollow's throwaway offscreen probe (`tools/SnapshotTool perf` on the
Grimhollow branch `feature/perf-probe`), which drives the real `HollowmereViewer` and a real session over an
in-process loopback server on native Metal. The full findings, ablation summaries and trace breakdown are in
[`FINDINGS.md`](https://github.com/APKiwiOrg/Grimhollow/blob/e0154c2e/tools/SnapshotTool/perf-results/FINDINGS.md).
The machine was under heavy unrelated load, so every comparison below is an interleaved A/B on medians.

Baseline, town path, 1920x1080, session attached.

| Measure | Median |
| --- | --- |
| CPU per frame | 9.2 to 9.8 ms |
| `Scene3D.RenderInternal` recording | 8.5 ms |
| Game session and viewer code | under 0.6 ms |
| GPU per frame | 4.7 to 5.0 ms |
| Allocated per frame | 13.6 KiB, one gen0 per 600 frames |

Share of `Scene3D.RenderInternal` in a CPU trace of the same path.

| Share | Cost |
| --- | --- |
| 46.3% | `AcquirePointShadowSlots`, of which `MatrixBits` 22.3% and `MixPointSignature` 21.8% |
| 13.2% | `ObjCAutoreleasePool.Enter` around Metal encoder calls |
| 11.6% | Key-light shadow depth pass |
| 10.9% | `BuildAndUploadPointLightClusters`, mostly the 918 KiB copy rather than assignment |
| 10.1% | `WaterRenderer.Draw`, mostly per-plane grid building and staged uploads |
| 6.5% | Rendering the two point shadow rows rebuilt every frame |

Ablations, town path unless noted.

| Switch | CPU median on then off | Note |
| --- | --- | --- |
| Point shadows disabled | 9.8 and 10.9 ms, then 5.3 and 4.7 ms | GPU 5.0 to 3.7 ms |
| Point shadows disabled, forest | 7.9 and 8.8 ms, then 4.0 ms | |
| Water not drawn | 9.7 and 9.5 ms, then 8.6 and 8.5 ms | allocation 13.6 to 4.0 KiB, GPU only 0.15 ms lower |
| 38 static lights cut to 10 | 9.4 ms, then 5.7 ms | about 0.13 ms per static light |

Static point rows rebuilt at the budget of two on every frame of every path with a session, and on the forest
and meadow paths with no bodies at all (1,184 and 863 rebuilds over 600 frames), while paths whose draw focus
stays put rebuilt none. The key-light atlas re-rendered on 600 of 600 frames even with the player standing still
on the spawn tile, because wandering bodies and the idle breathing of rigid body parts keep its caster data
changing.

## Scope

In scope, in the order below.

1. Point caster index and word-wise signatures ([#1110](https://github.com/APKiwiOrg/KhaozEngine/issues/1110)).
2. Dissolve quantized in the point signature ([#1111](https://github.com/APKiwiOrg/KhaozEngine/issues/1111)).
3. Compact point light clusters ([#1112](https://github.com/APKiwiOrg/KhaozEngine/issues/1112)).
4. Water flat quads, pre-pass uploads and culling ([#1113](https://github.com/APKiwiOrg/KhaozEngine/issues/1113)).
5. Metal pool hoisting, cached selectors and allocation-free staging ([#1114](https://github.com/APKiwiOrg/KhaozEngine/issues/1114)).
6. D3D11 high-performance adapter by default ([#1115](https://github.com/APKiwiOrg/KhaozEngine/issues/1115)).
7. ChatBox visible-row drawing ([#1116](https://github.com/APKiwiOrg/KhaozEngine/issues/1116)).

Out of scope.

- Per-cascade key-light dirt, option C of
  [`SHADOW-RERECORD-STALL-DESIGN-2026-08-12.md`](SHADOW-RERECORD-STALL-DESIGN-2026-08-12.md) under
  [#410](https://github.com/APKiwiOrg/KhaozEngine/issues/410). Cascades re-fit on every camera move, which
  `ShadowCascadeStabilityTests` pins, and in Grimhollow the standing case is still dirtied by wandering bodies,
  so option C would rarely hold. The shape that would hold is a split between a cached static caster atlas and
  per-frame dynamic casters, which needs a caller hint and its own spec. It is filed as a roadmap item.
- Everything filed from the same pass outside this list, [#1117](https://github.com/APKiwiOrg/KhaozEngine/issues/1117)
  to [#1129](https://github.com/APKiwiOrg/KhaozEngine/issues/1129),
  [#380](https://github.com/APKiwiOrg/KhaozEngine/issues/380) and
  [#464](https://github.com/APKiwiOrg/KhaozEngine/issues/464).
- Static prop re-emission. It measured under 5% of recording, and
  [#399](https://github.com/APKiwiOrg/KhaozEngine/issues/399) already records the decision.

## 1. Point caster index

### Today

`AcquirePointShadowSlots` (`Scene3D.PointShadows.cs`) calls `PointCasterSignature` once per static request that
holds a row, every frame. Each call walks every slot of `_runs`, skips stale handles, meshes that
`MeshCastsShadows` rejects and kind `None`, recomputes the world sphere inside `InstanceTouchesLight`, and for
each caster that touches the light mixes mesh index, generation, `ShadowCastKind`, the 16 matrix floats and the
raw bits of `Dissolve.X` into FNV-1a one byte at a time. `BuildPointCasterSpans` (`Scene3D.PointShadowPass.cs`)
then walks every instance again for each rendered row. Instances are immediate mode with no identity across
frames, so the signature is the only change detection and has to stay.

### Design

A new internal type, `PointCasterIndex`, in its own file, owned by `Scene3D` and built once per frame inside
`PreparePointShadows` when at least one point request exists, static or dynamic.

- It records one world sphere per eligible rigid caster slot, with exactly the eligibility rules the signature
  uses today. The sphere comes from `mesh.Bounds.WorldSphere(model)`, the same call on the same inputs as
  `InstanceTouchesLight`.
- `InstanceTouchesLight` gains an overload that takes a precomputed sphere, and the existing overload calls it.
  The index, the signature and the pass therefore share one definition of touching a light, which keeps design
  decision 8 of the point shadow design intact.
- Casters are binned by sphere centre into a uniform 3D grid with a fixed internal cell size of 8 m. A caster whose
  radius exceeds the cell size, such as a region ground chunk, goes to an oversize list instead.
- A light query visits the cells its sphere overlaps, grown by one cell size, plus the oversize list. The
  candidates are sorted by slot, then filtered with the exact touch test. The result is the same set of casters,
  in the same ascending slot order, that the full walk produces today.
- All storage is grow-only and reused across frames, so a steady frame allocates nothing.
  `PointShadowAllocationTests.PreparePointShadowsOnASteadyFrameAllocatesNothing` keeps holding.
- The index is independent of the key-light tier. Reusing `_shadowCasterSpheres` was rejected because it is
  stale under `ShadowMode.Off`, Blob and a degenerate camera, and the dirty-state commit swaps its span lists.

The signature keeps its inputs and changes its mixing. Light fields and per-caster fields are mixed as whole
64-bit words with a fixed-constant multiply and rotate mixer instead of byte-wise FNV. The matrix is mixed as
eight 64-bit words. The mixer is not `System.HashCode`, which is seeded per process. A signature is only compared
within one process, so the value may differ from today's without effect.

`BuildPointCasterSpans` takes the light's candidate list from the index instead of walking every instance.
Dynamic lights use the same query.

### Tests

- The existing signature tests keep passing unchanged: `AStaticMapIsNotRebuiltWhileNothingUnderItMoves`,
  `AStaticMapIsRebuiltWhenItsCastersMove`, the face-resolution, near-radius and exclusion-box redraw tests, and
  the allocation test.
- A headless equivalence test compares the index query with the full walk, kept in the test project as an
  oracle, over seeded random scenes that include oversize casters, lights on cell boundaries and empty frames.
  The test asserts identical slot lists.
- A headless count test asserts that the number of exact touch tests for a large sparse scene is bounded by the
  local candidate count rather than lights times instances. It counts, it does not time.

### Risk

[#1054](https://github.com/APKiwiOrg/KhaozEngine/issues/1054) skinned point shadows has an approved plan that
reorders request gathering in the same files. Its plan keeps rigid signatures unchanged in behaviour, so the two
are separable. This round is smaller and lands first. The #1054 implementation rebases onto it.

## 2. Dissolve quantized in the point signature

### Today

A dissolving prop casts a dithered point shadow through the dissolve pipelines of `PointShadowRenderer`, with the
same noise as the colour pass. The signature mixes the raw bits of `Dissolve.X`. A prop inside a distance fade
band or an LOD crossfade band changes `Dissolve.X` on every frame the draw focus moves, so any static light with
such a prop inside its radius is dirty on every frame of the fade. The rebuild budget of two per frame is spent
continuously. The signature also omits `DissolveComplement`, so a complement flip at a constant threshold changes
the map without changing the signature.

### Design

Confirm before changing. A headless test reproduces the churn with one static light and one prop whose dissolve
advances a little each frame, and asserts one rebuild per frame on the current code. The Grimhollow probe then
confirms on the real world by comparing rebuild counts on the forest and meadow paths before and after.

The signature mixes the dissolve threshold quantized to sixteen steps, `round(clamp(Dissolve.X, 0, 1) * 16)`,
plus the complement flag bit, instead of the raw threshold bits. The rendered row still uses each instance's
actual dissolve at the moment it is rebuilt. Between steps the cached map keeps the previous step's dither.

This is the one intended pixel difference in the point pass. A fading prop's point shadow advances in sixteen
steps instead of every frame. Fades happen at the draw-distance edge, where a lamp's shadow covers very few
pixels. A prop at run pace crosses a 16 m fade band in about sixteen rebuilds instead of one per frame.

Rigid bodies inside a lamp radius still dirty it on every frame they move, by design. Grimhollow's avatar leaves
the rigid signature when it goes skinned under #1054.

The key-light caster compare has the same complement gap. That is recorded as a follow-up issue and not changed
here, because the key-light compare is exact rather than hashed and belongs to the #410 work.

### Tests

- The churn reproduction above, inverted after the change: a prop advancing by less than one step causes no
  rebuild, and crossing a step causes exactly one.
- A complement flip at a constant threshold now causes a rebuild.

## 3. Compact point light clusters

### Today

`PointLightClusterBuilder` fills a fixed image of 3,456 clusters (16 by 9 by 24) with 17 `uvec4` each, a header of
count and overflow flag plus 64 packed light indices, 940,032 bytes in total. It clears the whole image, builds six
planes for every cluster, tests every light against every cluster in ascending light order and uploads the whole
image through `ModelRenderer.BuildAndUploadPointLightClusters` on every frame that queues a light. No light is
culled first. The shared lighting block in `ShaderSources.Lighting.cs` reads it as `uvec4 PointLightClusters[]` at
`set=0, binding=2`, and it is spliced into six fragment programs.

### Design

The resource, binding, stride and total size stay the same. The contents change.

- A header region of 864 `uvec4`, four clusters per element, one `uint` per cluster. The low 8 bits hold the
  light count, with 255 meaning overflow. The high 24 bits hold the offset of the cluster's first index in the
  index region.
- An index region directly after it, four light indices per `uvec4`, each cluster's indices contiguous and in
  ascending light order.
- Capacity is unchanged. The index region holds 231,552 indices, at least the 221,184 a full image of 64 per
  cluster needs. A cluster over 64 lights is marked overflow and stores no indices, exactly as the overflow flag
  works today, and the shader falls back to every light.
- Only the used prefix is uploaded, the 13.8 KB header plus the used index bytes. Grimhollow's town frame needs a
  few tens of kilobytes instead of 918 KiB.

Assignment keeps its exact per-cluster plane test and changes which clusters it runs on.

- Lights whose sphere lies wholly outside the view frustum are dropped first. They could not pass any cluster
  test, so this removes work without changing a result.
- Each remaining light gets a conservative cluster range, its projected screen rectangle in tiles and its view
  depth range in slices. A light whose sphere crosses the near plane takes the full tile range.
- Clusters are visited in index order. Each cluster tests, in ascending order, only the lights whose range
  contains it, with the existing exact plane test.
- The assignment is therefore identical to today's, cluster by cluster, and in the same order. Output stays
  byte-identical, which matters because lighting sums are order sensitive.
- Cluster planes are still built from the GPU-corrected view-projection so the CPU and the shader keep sharing it.
  They are built only for clusters that some light's range reaches. Caching planes per projection was rejected
  because it would move assignment to view space and break that shared definition.

The shader change is confined to the shared lighting block: read the header `uint`, decode count and offset, and
loop over the index region. The loop keeps its current varying-count form, so the D3D11 unroll note in
[`STABLE-POINT-LIGHTING-2026-09-19.md`](STABLE-POINT-LIGHTING-2026-09-19.md) applies unchanged. The Vulkan SPIR-V,
Metal MSL and D3D11 HLSL hash tables are rebaked and the constants pinned by `PointLightClusterShaderTests` are
updated.

### Tests

- `PointLightClusterBuilderTests` move to the new layout and keep their semantics, including overflow,
  orthographic projection and `WarmBuildAllocatesNothing`.
- A headless equivalence test compares the per-cluster light lists with the current brute-force builder, kept in
  the test project as an oracle, over seeded random lights and cameras including near-plane crossings and
  overflow.
- `ManyPointLightsGpuTests` and `PointLightGpuTests` pass on Metal locally and on the D3D11 and Vulkan legs in CI.

## 4. Water

### Today

`TileWorldView.DrawWaterPlanes` queues every loaded plane with no cull. In the default camera-focused grid mode,
`WaterRenderer.Draw` builds a 97 by 97 grid per plane after `SetFramebuffer` and uploads 112,908 bytes per plane
into offset 0 of one shared vertex buffer. On native Metal each upload is staged, ends the render encoder for a
blit and reopens the pass. `UsesFlatQuad` is consulted only in clipmap mode, and the flat quad path also uploads
inside the pass. Grimhollow's river has zero swell, so every plane takes the full grid.

### Design

- `UsesFlatQuad` is consulted in every grid mode. Its test widens to every procedural configuration whose
  displacement is zero, which today's check misses for a zero or negative wavelength and a negative amplitude.
- All flat quads are written into one buffer of 48 bytes per plane and uploaded once before `SetFramebuffer`. Each
  quad is drawn at its own vertex offset. The buffer grows only and retires the old one, like
  `EnsureClipBuffers`.
- Planes that still need the camera-focused grid get one slice each of a shared buffer, uploaded before
  `SetFramebuffer` and drawn through the existing static index buffer with a vertex offset. That is the clipmap
  path's shape.
- Planes whose bounds, grown by the swell amplitude, lie outside the view frustum are skipped before any geometry
  is built. The cull sits in `WaterRenderer` so every consumer gets it.

This is the second intended pixel difference. A flat quad matches the zero-swell grid mathematically, because
every varying is affine or constant and fog, depth fade, shore and foam are per fragment, but it is not
byte-identical. `WaterFlatPlaneTests` already bounds the difference at mean under 0.002 and worst under 0.02.
`Golden3D_TileWorld_River` is rebaked within that bound.

### Tests

- `WaterFlatPlaneTests` extended to the camera-focused mode and to each widened flat case.
- A headless allocation test for a steady frame with 35 planes in the water path. The Metal staging allocation
  itself is item 5.
- `Golden3D_TileWorld_River` rebaked and reviewed.

### Coordination

`feature/water-glint-foam` edits the water shaders, `WaterMath` helpers and the shader hash tables, and none of
the renderer files above. Flat quad equivalence still holds on that branch because the fold is zero at zero
swell. The two may both move the River golden and the hash tables, which is resolved by rebaking after the second
merge.

## 5. Metal encoder overhead and staging allocations

### Today

Every setter and draw in `MetalEncoderSink` and `MetalRenderApi` opens its own `ObjCAutoreleasePool`. Rule M-N5,
enforced by `MetalAutoreleaseArchitectureTests`, requires every path from an entry point of the package to an
`objc_msgSend` to pass through a method that opens a pool, and a pool covers everything below it.
[#600](https://github.com/APKiwiOrg/KhaozEngine/issues/600) measured the pool pair at about 21 ns and recorded the
structural answer, one pool per flush. `ObjCRuntime` resolves hot selectors through a string-keyed dictionary.
Every staged upload builds a diagnostic string in `MetalBufferUpload` even on success, and `MetalStagingArena`
allocates an `OpenBlock` object per block.

### Design

- The pool moves up to the `MetalCommandList` members that reach the sink: draws, dispatches, encoder
  transitions, uploads and framebuffer changes. One pool then covers a whole bind flush plus its draw. Sink
  members stop opening pools and document that the caller holds one. They are called only from inside the
  package, so the architecture test keeps its rule and gains no exclusion list.
- Hot selectors are cached in static fields, the way `MetalCompletionHandler` already caches its own.
- `MetalBufferUpload` builds its message only on the failure path. `MetalStagingArena` reuses its block records so
  a steady frame's staging allocates nothing.

### Tests

- `MetalAutoreleaseArchitectureTests` passes unchanged, including its positive controls.
- A Metal GPU allocation test for steady-state staged uploads.
- The existing native Metal GPU suites.

## 6. D3D11 high-performance adapter

### Today

With `KE_D3D11_ADAPTER` unset, `D3D11AdapterSelection.Choose` returns `DefaultEnumeration` and the device is
created with a null adapter and `DriverType.Hardware`. DXGI takes the first adapter it enumerates, which on a
hybrid laptop is usually the integrated GPU.

### Design

- The pure policy gains a resolved shape for the unset request, high-performance preference, taken when the
  factory reports `IDXGIFactory6` support. That support is passed in as a fact so the policy stays testable
  without Windows.
- The Windows glue asks `IDXGIFactory6.EnumAdapterByGpuPreference(0, HighPerformance)` for the adapter and
  creates the device on it with `DriverType.Unknown`. Any failure falls back to today's null-adapter path with a
  logged warning.
- `D3D11FeatureProbe` resolves the adapter the same way, so it probes the adapter the device will use.
- Every explicit `KE_D3D11_ADAPTER` value keeps winning, and CI keeps pinning WARP.

### Tests

- `D3D11AdapterSelectionTests` cover the new default, the missing-interface fallback and every explicit override.
- The D3D11 CI leg. Nothing here can run on the Mac beyond compilation and the pure policy tests.

## 7. ChatBox visible rows

### Today

`ChatBox.Draw` calls `DrawRow` for every cached row inside the scroll clip, and the clip only discards pixels.
`DrawRow` slices a timestamped row into two new strings and measures the timestamp every frame.

### Design

- `Draw` computes the first and last row whose bounds meet the content area, from the scroll offset, the stride
  and the same sparse-space offset `RowBounds` applies, and draws only those.
- `CachedRow` carries the timestamp text, the message text and the message x offset, computed when the layout is
  refreshed. The existing refresh triggers already cover a font, wrap width or history change.

### Tests

- A headless test with a full 100-entry history asserts that only the visible rows are drawn.
- A headless allocation test asserts that drawing a steady history allocates nothing.

## Version, release and adoption

- The round rides the staged, untagged 20.2.0. If 20.2.0 is tagged before this round integrates, the round takes
  the next free version under the contributor rules.
- Items land as separate commits on `feature/frame-cost-round`, followed by one release commit that extends the
  newest `CHANGELOG.md` entry, runs the full documentation sweep and updates guarded declarations.
- The integration branch passes the full Release suite, the native Metal GPU suite locally and the Metal, D3D11
  and Vulkan CI legs. `scripts/pack-local-feed.sh` packs it.
- Grimhollow is pinned and waiting on this change, so the release tag is created with `scripts/tag-release.sh`
  under the waiting-consumer exception in the contributor rules.
- Grimhollow adopts it under [Grimhollow #325](https://github.com/APKiwiOrg/Grimhollow/issues/325), sweeping every
  version from 20.0.0 in its integration ledger.

## Acceptance

Measured by rerunning the Grimhollow probe as interleaved runs against 20.0.0 and the new engine on one machine.

- Town path with a session: `RenderInternal` recording median at or under 4 ms, from 8.5 ms.
- Forest and meadow paths with no session: static point rebuilds on at most 5% of frames, from nearly every frame.
- Steady allocation at or under 5 KiB per frame and no gen0 over 600 frames on the town path.
- GPU median no worse than baseline.
- Every golden within tolerance, with the River rebake reviewed.

## Plan amendments

Reading the code for the implementation plan (`docs/superpowers/plans/2026-09-23-frame-cost-round.md`) corrected
the details below. Where an amendment and an earlier section disagree, the amendment wins. Two were owner decisions
and are marked with their outcome.

Item 1.

- The index is built lazily, once per point-shadow frame, and `PreparePointShadows` triggers it after its atlas
  check. `DebugRenderPointShadowSlot` and tests that call `RenderPointShadowSlots` directly render rows on frames
  that queued no request, and an index built only inside `PreparePointShadows` would be stale there.
- A query grows the light's sphere by one cell plus a 0.25 m slack, because the float touch test can keep a centre a
  rounding error past the grown sphere. Non-finite and out-of-range centres go to the oversize list, and a query
  wider than the binned caster count walks every binned caster. Each of these only adds candidates.

Item 3.

- The cull is not a six-plane frustum test. Behind the eye a column's side planes cross, and the exact test accepts
  some spheres that lie wholly outside the frustum. A light is culled only when its conservative range is empty, and
  side planes never narrow the range of a light that reaches the near plane.
- The tile range comes from signed distances to the 17 column and 10 row boundary planes, the same planes the
  clusters test, widened by 1e-3 of the frame's geometry scale.
- **Owner decision, accepted on 2026-09-23.** Building planes only for reached clusters means a degenerate cluster no light reaches no
  longer forces the whole frame to the full-list fallback. That happens only for a near plane below about 1 mm.
  Pixels are identical either way, because the fallback walks every light and a light outside its radius adds
  zero, but the diagnostics and the buffer contents differ for those cameras.
- An overflowed cluster still adds 64 to `LightReferenceCount`, so the diagnostic keeps its value and meaning. The
  upload is rounded up to a whole `uvec4`.

Item 4.

- A plane's cull bound grows horizontally by the Gerstner pinch, `abs(steepness) * wavelength / (2 pi)`, and
  vertically by the amplitude. Growing by the amplitude alone would cull crests the pinch carries into view. FFT
  planes are never culled, because the CPU holds no bound on their displacement.
- A plane does not displace when its amplitude or its wavelength is zero or less, which is the shader's own gate.
- `scene3d_sky_two_discs` also draws a zero-swell plane and moves to the flat quad with the River golden.
- The flat and grid buffers grow geometrically, the way `EnsureUboCapacity` grows, rather than to the exact size.

Item 5.

- **Owner decision, accepted on 2026-09-23.** `MetalAutoreleaseArchitectureTests` cannot pass unchanged. Its IL walk resolves a call
  through `IMetalEncoderSink` or `IMetalRenderApi` to the bodiless interface member, so every seam member is a
  computed entry point that must open its own pool. The walk gains two edges: an interface call reaches every
  package implementation, and a constructed generic call reaches its definition. The rule text, its failure message
  and its positive controls are unchanged, and there is still no exclusion list.
- `ObjCAutoreleasePool.Enter` becomes a no-op off macOS, because device-free tests drive `MetalCommandList` on the
  Linux and Windows legs.
- The upload's pool sits in `MetalBufferUpload.StageAndCopy`, so the ring half of `UpdateBuffer`, which every
  uniform write takes, stays pool-free.
- Hot selectors live in nested `Selectors` classes, the `MetalCompletionHandler` pattern, which amends the ObjC
  folder's rule against static selector fields. `MTLRenderPassDescriptor.Create` built its class name into a fresh
  array on every pass, and the class is now cached once it resolves.

Item 6.

- A runtime without `IDXGIFactory6` logs INFO, not a warning, because nothing was asked for that could not be given.
  A preferred adapter that cannot be fetched or refuses the device warns and lets DXGI pick.
- Only the unset default retries on DXGI's pick when its adapter refuses a device. An explicit pin fails as it does
  today.
- The feature probe now honours explicit `KE_D3D11_ADAPTER` values as well as the default.
- No CI leg runs the new default, because the Windows leg pins WARP. A run on a hybrid laptop is the only runtime
  proof, and it is a manual validation step after release.

Item 7.

- The headless proof goes through an internal row sink that the real draw also uses, because a `SpriteBatch` needs
  a GPU device.
- One extra row is drawn past each edge of the viewport, so glyph overhang and whole-pixel scissor rounding keep
  the frame identical to drawing every row.

## Rejected alternatives

- A game-side cap on shadowed lamps. The owner chose the engine fix. The spike measured it, and a changing count
  reallocates the atlas ([#1126](https://github.com/APKiwiOrg/KhaozEngine/issues/1126)).
- Per-instance identity or changed flags for point casters. Immediate-mode submission has no identity, and adding
  one is an API change. The index keeps the signature model.
- Casting point shadows as a binary presence test on dissolve. It changes the point pass shaders and departs from
  the colour and key-light dissolve rules. Quantizing the signature leaves rendering untouched.
- A second binding for the cluster index list. It touches four resource layouts and three backend binding tests
  for no gain over sharing the existing buffer.
- Per-cascade key-light dirt in this round, for the reasons under Scope.
