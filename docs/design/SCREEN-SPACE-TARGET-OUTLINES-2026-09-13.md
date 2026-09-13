# Screen-space target outlines

Status: implementation and validation for [#879](https://github.com/APKiwiOrg/KhaozEngine/issues/879).
Consumer: [Grimhollow #186](https://github.com/APKiwiOrg/Grimhollow/issues/186).

## Goal and contract

Selected targets have a thin border around their camera-projected silhouette. Overlapping limbs,
material parts and canopy masses form one union and never create internal strokes. Foreground
geometry hides the target and its border. An occluder's cut through a target creates no new border.
Grimhollow uses 1.25 physical output pixels for every target, independent of body size and distance.

The existing metre-width inverted-hull API remains compatible. Its welded normals solve cracks,
but hull expansion can still expose surfaces inside the projected silhouette. The new API describes
pixel width explicitly and allows independently transformed parts to share one frame-local group.

## Decision

Scores are 1 to 10, with equal weights. Correctness means satisfying the outer-only contract.

| Approach | Correctness | Width consistency | Runtime cost | Maintainability | Total |
|---|---:|---:|---:|---:|---:|
| Further inverted-hull tuning | 2 | 3 | 9 | 8 | 22 |
| Grouped target mask and pixel-space border | 10 | 10 | 7 | 8 | 35 |

Hull tuning is cheap and retains the current pass, but cannot remove the interior intersections
shown on the goblin. The grouped mask has additional target draws and temporary render targets,
but gives a direct occupancy rule for both interior suppression and constant output-pixel width.
Choose the grouped mask, retaining the old API for consumers that intentionally use world-space hulls.

## Interfaces

```csharp
MeshOutlineGroup Scene3D.BeginMeshOutline(Color color, float widthPixels);
void Scene3D.DrawMeshOutline(MeshOutlineGroup group, MeshHandle mesh, Matrix4x4 world);
void Scene3D.DrawMeshOutline(MeshHandle mesh, Matrix4x4 world, Color color, float widthPixels);
```

`MeshOutlineGroup` is an opaque frame-local handle. `Begin` clears the queue and invalidates prior
groups. Group ownership and stale handles are checked. Width must be finite and within 0.5 to 8
pixels. Separate groups retain their own colours and widths, with stable submission order.

`ITileWorldScene` exposes the grouped methods with compatible default implementations.
`Scene3DTileWorldScene` forwards them. `TileWorldView.SetOutlinedObject` and
`ClearOutlinedObject` provide object selection, with every part in one group.

## Rendering

The mask contains the full unexpanded target, including portions hidden behind world geometry.
The composite never colours an occupied center pixel. It searches only a bounded output-pixel
neighborhood and takes border colour from visible target coverage. Depth checks at both the source
and destination suppress hidden silhouettes and prevent painting over nearer geometry.

The pass runs after the scene post-processing stage and before final overlays. Pixel width is
measured against the final framebuffer dimensions. Mask targets are allocated lazily and resized
with the viewport. Ordinary model culling and alpha cutout must also apply to the mask.

MSAA and render scaling are correctness requirements. The ordinary scene depth MRT can contain
resolved edge values. A single-sample full-resolution mask must not be assumed to match it without
tests. If direct depth sampling fails those tests, use matching scene-sample coverage before the
final pixel-width composite. A large depth epsilon is not an acceptable substitute.

## Implementation plan

> Agent execution uses the subagent-driven-development workflow, with parent review and integration.

1. Add `TargetOutlineGoldenTests` in the render GPU test project. Render an opaque, unoutlined
   baseline and compare its independently measured coverage with the outlined image. Assert zero
   highlight pixels in the target interior, including limb and canopy overlap bands. Include a
   foreground wall, multiple groups, alpha-cutout holes and a backfacing one-sided plane.
2. Add `Rendering/TargetOutlineRenderer.cs`, `Internal/ShaderSources.TargetOutline.cs` and a
   cohesive `Scene3D.TargetOutlinePass.cs`. Keep mask creation, bounded edge search and frame
   submission responsibilities separate. Add the frame-local group type and argument checks.
3. Wire group queue clearing, final-output composition and disposal into `Scene3D`. Cover mask
   resize, partial allocation failure, stale groups, empty frames and material lifetime with
   headless tests. Preserve the legacy silhouette tests and their committed references.
4. Wire the tile scene and object-selection APIs. A multipart object starts one group and submits
   each transformed part to that group. Prove forwarding through the scene adapter.
5. Exercise 1.25-pixel outlines at multiple camera angles and distances, internal render scales,
   MSAA 1 and 4, and SSAA. Require a stable thin border and zero interior strokes using baseline
   coverage as the oracle. Keep the tests named `Golden` for all-backend pull-request verification.

   ```bash
   KE_GPU_TESTS=1 dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj -c Release --filter FullyQualifiedName~TargetOutline
   ```

6. Capture the actual Hollowmere oak and nine independently transformed goblin pieces in a
   temporary local GPU test. Inspect the images before claiming the visual requirement is met.
   Commit engine test coverage, then run the three-backend pull-request gate. Update the renderer
   reference docs, release the engine for the waiting consumer, and verify package publication.
7. Grimhollow replaces metre and rig-scaled widths with one 1.25-pixel constant. Each humanoid,
   rigid creature and selected world object uses a single outline group. Update fake-scene
   assertions, restore the new packages, run the full game build and tests, then merge and push.

## Acceptance

- No red lines through a goblin's head, torso, arm joints or overlapping legs.
- No yellow lines through overlapping oak canopy surfaces.
- A thin border follows only the visible outer edge and genuine transparent holes.
- Foreground geometry receives no leaked outline and introduces no cut-line.
- Border thickness remains constant for all target sizes, distances and render scales.
- Legacy hull callers retain their behavior. Frames without new outlines remain unchanged.
- Metal, Direct3D 11 and Vulkan pass the semantic render checks.
