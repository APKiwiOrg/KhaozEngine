# P0 Stage 1 — correctness net + cheap wins (5.23.0-experimental)

First of three P0 hardening releases (see `docs/ENGINE-AUDIT-5x-2026-06-16.md`). Goal: put a GPU-correctness
safety net in place and knock out the low-risk perf/leak fixes BEFORE the bigger refactors (instancing in
Stage 2, the backend seam in Stage 3). All low-risk; no public API breakage.

## Part A — cheap perf/leak fixes (headless-verifiable)

1. **`ModelRenderer` ResourceLayout leak** (Rendering/ModelRenderer.cs): store the `CreateResourceLayout`
   result in a `readonly ResourceLayout _layout;` field (use it in `CreateResourceSet`/pipeline) and dispose it
   in `Dispose()`. (Mirrors LineRenderer/BillboardRenderer which already do this.)
2. **Hoist invariant binds out of the 3D instance loop** (ModelRenderer + Scene3D): `SetPipeline` and
   `SetGraphicsResourceSet` are invariant across instances within the model pass (the resource set references
   the shared `_ubo`; only its CONTENTS change per instance via `UpdateBuffer`). Add a
   `ModelRenderer.BindPass(CommandList cl)` that does `cl.SetPipeline(_pipeline)` + `cl.SetGraphicsResourceSet(0,
   _set)` once, and REMOVE those two calls from `DrawInstance` (which keeps `UpdateBuffer(_ubo,...)` +
   `SetVertexBuffer` + `SetIndexBuffer` + `DrawIndexed`). `Scene3D.RenderInternal` calls `BeginModelPass` then
   `BindPass` then the `foreach` of `DrawInstance`. (The per-instance UBO upload stays — that's Stage 2.)
3. **Cache the palette scratch array** (Rendering/PixelPostProcess.cs `PrepareUniforms`): replace the
   per-frame `var pal = new float[260];` with a reused `readonly float[] _palScratch = new float[260];` field,
   refilled in place each call (zero per-frame allocation).
4. **Drop `ScreenStack.Update`'s per-frame `ToArray()`** (Gui/ScreenStack.cs): replace
   `Screen[] snapshot = _screens.ToArray();` with a reused `List<Screen> _updateScratch` field
   (`_updateScratch.Clear(); _updateScratch.AddRange(_screens);` then iterate it) so add/remove during Update is
   still safe but no array is allocated per frame.

These change no public behaviour. Existing tests must stay green; the controller snapshot-verifies the 3D
render is unchanged after the bind-hoist.

## Part B — winding-vs-normal correctness net for primitives (headless)

The model pipeline uses `FaceCullMode.None`, so a reversed triangle winding renders identically and is INVISIBLE
to both tests and snapshots (it only shows as mis-lit faces — this already bit Pyramid/Wedge). The existing
`AssertAllFaceNormalsOutward` checks normal DIRECTION but does NOT catch a winding flip when the stored normal
is independently correct. Only Torus/Plane currently assert winding-vs-normal.

- Add a reusable test helper `AssertWindingMatchesNormals(GltfMesh)` that, for every triangle, asserts the
  geometric face normal `normalize(cross(p1-p0, p2-p0))` points the SAME way as the triangle's stored vertex
  normals (`dot(faceNormal, avgStoredNormal) > 0`, small epsilon). This catches a winding flip independently of
  whether the stored normal is outward.
- Apply it to EVERY `MeshPrimitives` shape: Box, Tile, Cylinder, Cone, Pyramid, Wedge, Sphere, Capsule,
  RoundedBox, Torus, Plane (in `MeshPrimitivesTests`). Keep the existing outward-direction tests too (they're
  complementary).

## Part C — golden-snapshot GPU regression test (the safety net)

A gated, tolerance-based image regression test so a shader/UBO/blend/winding regression is caught in
`dotnet test` on a machine with a GPU (instead of only by a human eyeing a PNG). MUST be skipped where there is
no Metal GPU (CI/Linux) so the normal suite stays green everywhere.

- New `KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs` + a `GpuFactAttribute : FactAttribute` that sets
  `Skip = "set KE_GPU_TESTS=1 to run GPU golden tests"` UNLESS the env var `KE_GPU_TESTS == "1"`. (So CI and
  default `dotnet test` skip them; the dev Mac runs them with `KE_GPU_TESTS=1 dotnet test`.)
- Two goldens:
  - **3D**: render a fixed asymmetric scene via `Render3DSnapshot.Capture` (e.g. a tile floor + a red Sphere
    with `Material.Shiny` + a green Box + a `DebugCircle` ring + the default lighting; fixed camera/size). The
    asymmetric content guards orientation (the upside-down class).
  - **2D**: render a fixed scene via `Render2DSnapshot.Capture` (a couple of coloured `SpriteBatch.Draw` rects +
    a `DrawString` in a known font; fixed size).
- Compare with TOLERANCE, not exact hash (robust to minor driver noise): downsample the RGBA byte buffer to a
  small grid (e.g. 32x18) of average RGB per cell, and compare to a committed reference grid with a per-channel
  tolerance (e.g. abs diff <= 0.06). Fail listing the worst cells.
- Store the reference grids as committed data files under `KhaozEngine.Tests/Gpu/goldens/` (a compact text
  format: one line per cell `r g b`, or JSON). Provide a regeneration path: if env `KE_UPDATE_GOLDENS == "1"`,
  the test WRITES the reference instead of asserting (so intended visual changes are re-baked deliberately).
- Generate the references ON THIS dev Mac (run with `KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1`), then run once more
  with just `KE_GPU_TESTS=1` to confirm they PASS, and COMMIT the goldens. The default `dotnet test` (no env)
  must still report all-green with the golden tests skipped.

## Files
- Modify `KhaozEngine.Render3D/Rendering/ModelRenderer.cs`, `Scene3D.cs`, `Rendering/PixelPostProcess.cs`,
  `KhaozEngine.Gui/ScreenStack.cs`.
- Tests: extend `KhaozEngine.Tests/Render3D/MeshPrimitivesTests.cs`; new `KhaozEngine.Tests/Gpu/
  GoldenSnapshotTests.cs` + `GpuFactAttribute.cs` + `goldens/*`.
- Release: bump `<KhaozEngine5xVersion>` 5.22.0 -> 5.23.0-experimental, CHANGELOG, pack the 6 5.x packages.

## Testing
- Default `dotnet test` (no env): ALL green, golden tests SKIPPED. The winding tests + the headless fixes are
  covered here.
- `KE_GPU_TESTS=1 dotnet test`: the 2 golden tests also run and PASS against the committed references.
- Controller additionally snapshot-verifies (eyeball) that the 3D render is visually unchanged after the
  bind-hoist + leak fix.
