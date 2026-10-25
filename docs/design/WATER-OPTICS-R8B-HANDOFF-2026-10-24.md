# Water optics R8b handoff: native bounded-water optical beds

Status: Open handoff. The native adapter is not implemented. It remains open work under
[#1300](https://github.com/APKiwiOrg/KhaozEngine/issues/1300) until the native renderer actually prepares and
submits an optical binding. Compilation-only MapDoc water tests are not native optical acceptance.

Rationale: [per-body water optics](WATER-OBJECT-OPTICS-DESIGN-2026-10-24.md) and its
[triangle-backed amendment](WATER-OPTICS-TRIANGLE-AMENDMENT-2026-10-24.md). Consumer use:
[Render3D reference](../KhaozEngine.Render3D/README.md) and
[consumer guide](../USING-KHAOZENGINE.md).

## Boundary

The R8a native renderer is unfinished and is not a dependency of main's Render3D or TileWorld code. No
`KhaozEngine.MapDoc.Render3D` package or native renderer type is published on main, so this handoff names no
native API. It fixes what a prepared bounded-water owner must carry once R8b adopts the neutral resource. R8a's
canonical bounded-water resources, identity, anchors, original-domain clipping and dependency digests remain
authoritative. Where they meet this contract, R8b adapts to both rather than weakening either.

## Neutral surfaces to consume

Render3D, with no TileWorld or MapDoc dependency:

| Surface | Use |
| --- | --- |
| `WaterOpticsTriangleField(size, binColumns, binRows, triangles, certifiedRectangles)` | Immutable certified bed over actual triangles. Vertices are `WaterOpticsTriangle` in local metres, ordered X, signed rest depth, Z. Certificates are `WaterOpticsRectangle` values. |
| `MaxDimension`, `MaxTrianglesPerBin` | Bin axes 1 through 1024, atlas texture axes 2 through 1024, at most 32 candidates per bin. |
| `TextureBytes`, `RetainedBytes`, `ReconstructionErrorBoundMetres`, `CertifiedRectangles` | Byte charges, the consumed error bound and the coverage witness. |
| `WaterOpticsSurfaceException.Reason` | Typed refusal: `IncompleteCoverage`, `AmbiguousSurface`, `CapacityExceeded`, `UnrepresentableSurface`. |
| `Scene3D.LoadWaterOpticsTriangles`, `Scene3D.UnloadWaterOpticsField` | Scene-owned `WaterOpticsHandle` and deferred retirement. |
| `WaterOpticsBinding(handle, origin, size)`, `WaterPlane.Optics` | Per-submission mapping and opt-in. |
| `Scene3D.GetWaterOpticsPadding(look)` | Certified procedural displacement for preflight. FFT is refused. |
| `Scene3D.PrepareFrame(width, height)` | Required before recording any bound water. |
| `Scene3D.WaterOpticsStats` | Live and retired texture and retained CPU bytes through actual retirement. |

`WaterOpticsField` remains available for a producer whose bed truly is a lattice and whose steep drops tolerate
one cell of bilinear softening. Native bounded water renders arbitrary triangles, so the triangle field is the
expected kind.

Published MapDoc water data the producer reads: `MapWaterCompiler.Compile` emits `MapCompiledWaterBody` values with
`BodyId`, `SurfaceY`, `Regions`, `FragmentBounds`, `Status` and `Digest`. `MapWaterQuery.Column` returns the wet
intervals at one XZ. The aggregate `DeepestBedY` is not an optical bed.

## Bed selection

Select the bed from the water body's own covered space and wet interval, from the actual rendered surfaces that
bound it below. Do not query a scene-wide XZ height, the aggregate deepest bed or the first opaque surface. Two
bodies at the same XZ and different levels get independent fields, bindings and reference levels. Pass complete
source triangles in emitted order without clipping, welding or moving vertices. The field refuses holes inside a
certificate and disagreeing overlaps rather than resolving them by order.

## What a prepared owner carries after anchor reduction

Each prepared bounded-water owner carries five things together, and publishes or retires them as one unit.

1. **Handle.** The `WaterOpticsHandle` from `LoadWaterOpticsTriangles` on the scene that draws the body. It is
   generation checked and valid only in that scene. Release it with `UnloadWaterOpticsField` when the last
   prepared owner leaves. The texture outlives the call until in-flight frames retire. A new scene or device
   needs the retained CPU field loaded again, never the old handle.
2. **Local mapping.** Origin XZ is the field minimum and origin Y is the body's fixed reference surface, normally
   its compiled `SurfaceY`. Size equals the field `Size`. The binding lives in the same frame as the submitted
   `WaterPlane`, and Scene3D subtracts its render origin from both. After the native renderer reduces body
   coordinates to its anchor, the field origin must still reduce exactly or within budget under the scene's
   render origin. The default quantized render origin is a `WorldFrame` anchor, a multiple of `WorldFrame.Grid`
   with magnitude at most 2^22 m. An explicit off-grid `Scene3D.RenderOrigin` override is not a grid multiple. It
   rounds the reduced origin like all other submitted geometry and is outside the certificate, so the producer
   either charges that case or records it as unsupported. Changing the surface level does not move the reference
   level. Only a rebuilt generation or a deliberate re-anchor of the whole body does.
3. **Coverage witness.** The certificate list, the drawable domain it was built for after original-domain clipping,
   and the padding used. Each submitted rectangle, widened by `GetWaterOpticsPadding(look)` and outward mapping
   error, must fit one certificate. Larger swell, a changed domain or a changed look envelope invalidates the
   witness. Render3D admission accepts `WaterPlane` rectangles. A native surface submitted through another path
   needs an equivalent Render3D admission with the same one-certificate rule before it can bind a field. That
   admission is part of the open R8b work.
4. **Byte charges.** `TextureBytes` for the padded GPU atlas and `RetainedBytes` for the owned CPU atlas and
   certificates count against the owner's residency budget from load until actual retirement, matching
   `WaterOpticsStats`. Retained source payloads used to build
   or revalidate the field count separately, as TileWorld reports through `WaterOpticsSourceBytes`. No charge may
   claim opaque driver overhead or managed headers.
5. **Source digest.** The field identity is the body's compiled `Digest` plus the identities of every bed surface
   generation it consumed, the anchor and the padding. A change to any of them prepares a replacement against
   one candidate source table, uploads it, validates again and swaps geometry and field together. An upload
   failure rolls the group back. A colour-only look change reuses the field.

## Error budget and refusals

The producer owns one positional budget covering source conversion, anchor and render-origin conversion and the
field's upward-rounded `ReconstructionErrorBoundMetres`. TileWorld uses `min(0.0001 m, TileSize / 1024)` and
snaps binding origins to whole metres below 2^23 m. Reduction by the default quantized render origin or any 128 m
`WorldFrame` anchor is then exact and costs nothing. An explicit off-grid render origin override is outside that
certificate. R8b selects and records its own bound before acceptance. It does not receive a second independent allowance.

Map each typed `Reason` to a native diagnostic naming the body, domain and source. Keep the new generation
unpublished. A previous generation stays visible only while its geometry and sources remain valid. No opted-in
fragment falls back to scene depth, zero, deep water or a nearest triangle. Bound FFT water stays refused until it
has a certified displacement envelope.

## Acceptance still owed

- Native preparation and submission of a bound field through the real renderer.
- Stacked bodies at the same XZ, including an enclosed space, with independent fields.
- Adjacent fields agreeing on shared boundaries, render-origin shifts and the existing bounded-water clipping.
- Hosted Metal, Direct3D 11 and Vulkan captures under the repository's normal GPU gate.
