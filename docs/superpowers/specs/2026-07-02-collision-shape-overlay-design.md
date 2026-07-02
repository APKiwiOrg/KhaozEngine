# Debug collision-shape overlay (engine side of Ruinborne's F2 proxy viewer)

Status: approved design, pre-implementation.
Target engine version: 9.1.0 (additive minor).
Branch: `feature/collision-overlay`.

## Goal

A toggleable in-game overlay that renders physics collision shapes as translucent,
color-coded geometry over the live scene (a red/blue Blender-style proxy view), plus a
small on-screen legend mapping colors to shape kinds. Games bind their own toggle key
(Ruinborne uses F2). It is the first layer of an extensible overlay framework: the legend
and toggle plumbing must accommodate future layers (nav debug, AoI radii, terrain chunk
bounds) without rework.

Render-only. Zero effect on the movement sim, the `.coll` bakes, or determinism.

## Non-goals

- No new physics behaviour, no change to `PhysicsShape`, `Pose`, or any bake output.
- No editor/authoring tooling. This renders what the game already feeds its world builder.
- No per-shape picking/selection or interactive editing.

## Placement

No new package. Everything lands in existing packages, respecting the current layering
(`Render3D` already depends on `Physics`; that is where `PropCollisionBake` lives):

- `KhaozEngine.Render3D`: shape to mesh conversion, the convex-hull triangulator, the
  translucent overlay render primitive, and `CollisionShapeOverlay`.
- `KhaozEngine.Gui`: the domain-agnostic legend widget.
- `KhaozEngine.Tests`: headless conversion tests (required) and one GPU golden.
- `TerrainWalkSample`: the acceptance fixture and F2 toggle.

## Components

### 1. `ConvexHull3D` (Render3D, headless)

A small dependency-free 3D convex-hull triangulator.

```
public static class ConvexHull3D
{
    // Returns an outward-wound triangle mesh (vertices + index triples) enclosing the
    // input point cloud. Returns empty arrays for degenerate input (< 4 points, or all
    // coplanar / collinear).
    public static (Vector3[] Vertices, int[] Indices) Triangulate(IReadOnlyList<Vector3> points);
}
```

Rationale: the bake pipeline's `HullFromPoints` only dedupes points. The actual hull
triangulation is Bepu's `ConvexHullHelper` at runtime, which we cannot call from the render
layer without pulling the opt-in `Physics.Bepu` package into `Render3D` (bad layering) and a
`BufferPool` into tests. A compact incremental hull keeps the converter headless,
dependency-free, and unit-testable.

Algorithm: incremental (add points one at a time, remove faces the point can see, cap the
horizon) or gift-wrapping. Outward winding verified by face-normal-vs-centroid sign. This is
new engine geometry code, not a reuse of the bake pipeline.

### 2. `CollisionShapeMesh` (Render3D, headless)

Converts one `PhysicsShape` into one colored `GltfMesh` in the shape's local space.

```
public static class CollisionShapeMesh
{
    public static GltfMesh Build(PhysicsShape shape, CollisionOverlayPalette palette);
}
```

- Kind color (with translucent alpha) is baked into `ModelVertex.Color`. A `CompoundShape`
  can mix kinds, so per-vertex color is how one mesh carries multiple colors and still draws
  in one call.
- Per-kind geometry, reproducing the *authored* collision geometry and the runtime placement
  conventions (unit tests pin these):
  - `BoxShape`: axis-aligned box centered on the pose, full extents `2 * HalfExtents`.
  - `SphereShape`: UV/ico sphere centered on the pose, radius `Radius`.
  - `CapsuleShape`: cylinder + two hemispherical caps along local Y, symmetric about the
    pose, total height `2 * Radius + Length`.
  - `CylinderShape`: base-aligned along local Y, spanning `pose.Y .. pose.Y + Length`, radius
    `Radius`. This matches Bepu's runtime lift of `+Length/2`.
  - `ConvexHullShape`: `ConvexHull3D.Triangulate(Points)`.
  - `TriangleMeshShape`: vertices + indices as-is.
  - `CompoundShape`: recurse each child, compose the child's local `Pose` into the vertices,
    color each child by its own kind.

### 3. `CollisionOverlayPalette` (Render3D)

Maps each shape kind to a translucent `Color` and a display name. Sensible defaults
(box/sphere/capsule/cylinder/hull/mesh each a distinct hue at a low alpha), overridable by
the game. Also the source of legend labels.

### 4. Translucent overlay render primitive (Render3D / `Scene3D`)

A new general-purpose pass, reusable by every future overlay layer:

```
// On Scene3D:
public void DrawOverlayMesh(MeshHandle mesh, Matrix4x4 world);
```

- Unlit: fragment color is the vertex color, no lighting/texture sampling.
- Depth state `GpuDepthStencilState.DepthTestLessEqualNoWrite` (test on, write off) and blend
  `GpuBlendAttachment.AlphaBlend`, so proxies read against the world without occluding each
  other or corrupting the depth buffer.
- Per-draw world matrix via a dynamic-offset UBO (not a per-instance vertex attribute), to
  avoid the known Veldrid/Metal bug where instances past the first are dropped when a vertex
  shader indexes a buffer by a per-instance attribute.
- Rendered into the model framebuffer after beams/decals and before the pixel-post chain,
  the same slot textured billboards and beams use, so it has the world depth buffer to test
  against.
- New GLSL vert/frag added to `ShaderSources`, compiled via
  `factory.CreateShadersFromSpirv`, following the existing overlay-renderer pattern.

### 5. `CollisionShapeOverlay` (Render3D)

The first overlay layer, built on the primitive above.

```
public sealed class CollisionShapeOverlay : IDisposable
{
    public bool Enabled { get; set; }
    public CollisionOverlayPalette Palette { get; set; }

    // Build meshes once from a static set (the same (shape, pose) list the game feeds its
    // Populate-style world builder). Replaces any previously built set. Releases old handles.
    public void Build(Scene3D scene, IReadOnlyList<CollisionStatic> statics);

    // If Enabled, submit the prebuilt meshes to the scene overlay queue. No allocation.
    public void Draw(Scene3D scene);

    // Shape kinds present in the current build, for the legend.
    public IReadOnlyList<CollisionShapeKind> PresentKinds { get; }

    public void Dispose(); // release GPU mesh handles
}

public readonly record struct CollisionStatic(PhysicsShape Shape, Pose Pose);
public enum CollisionShapeKind { Box, Sphere, Capsule, Cylinder, ConvexHull, TriangleMesh }
```

- `Build`: for each static, `CollisionShapeMesh.Build(shape, palette)`, upload via
  `scene.LoadMesh`, store `(MeshHandle, worldMatrix)` where the world matrix comes from the
  static's `Pose`. Record the set of kinds present.
- `Draw`: `if (!Enabled) return;` then submit each stored `(handle, world)` to
  `DrawOverlayMesh`. Pre-sized list, precomputed matrices, zero per-frame allocation.

### 6. `OverlayLegend` (Gui)

A domain-agnostic swatch + label panel, modeled on `DiagnosticsOverlay` (measure content,
draw panel + border, per-row `Swatch` + `DrawString`, optional fade). Reusable by any
overlay layer.

```
public sealed class OverlayLegend
{
    public bool Visible { get; set; }
    public void SetEntries(IReadOnlyList<LegendEntry> entries);
    public bool Update(float dt); // fade
    public void Draw(SpriteBatch batch, SpriteFont font, Texture2D white, Rect viewport);
}
public readonly record struct LegendEntry(Color Swatch, string Label);
```

Gui stays free of `Render3D`/`Physics` deps: the game builds `LegendEntry` items from the
overlay's `PresentKinds` + `Palette` and hands them to the legend.

## Data flow

1. Game assembles `IReadOnlyList<CollisionStatic>` (the list it already feeds
   `Populate`-style builders).
2. `overlay.Build(scene, statics)` once. Meshes + world matrices cached.
3. Each frame: game reads its `InputState` snapshot, flips `overlay.Enabled` on the toggle
   key (engine input rule: game reads the snapshot, overlay API is just a bool). In 3D draw,
   `overlay.Draw(scene)`. In 2D draw, if enabled, `legend.Draw(...)`.

## Testing

Required (headless, no GPU):

- `ConvexHull3D`: cube point cloud to 12 outward triangles; tetrahedron to 4; coplanar and
  < 4 points to empty; a random cloud, assert every face normal points away from the
  centroid and all input points are inside or on the hull.
- `CollisionShapeMesh` per kind: vertex bounds and count sanity, winding, and placement
  conventions (box centered, cylinder base-aligned, capsule symmetric height `2r+length`).
- `CompoundShape`: child local poses composed correctly and each child colored by its kind.
- `CollisionShapeOverlay`: rebuild replaces the set and `Draw` allocates nothing on repeat
  calls (build once, draw many).

Optional but included (GPU golden):

- A `GpuFact` rendering the overlay over a fixture scene containing one of each shape kind at
  a fixed camera, compared to per-backend goldens. Baked on all three backends via
  `cross-platform-gpu.yml` `workflow_dispatch` with `bake=true`. Main stays red until the
  D3D11, Vulkan, and Metal golden files are all committed (a Metal-only bake turns main red).

## Sample / acceptance

`TerrainWalkSample`: add a small hand-placed building-proxy fixture (a baked proxy such as
the `blacksmith_proxy.coll` fixture, or a small `BakeProxy` compound) at a known pose. Feed
its `(shape, pose)` to a `CollisionShapeOverlay`. `Input.WasPressed(Key.F2)` flips
`overlay.Enabled`. Draw the legend while on.

Acceptance: toggling the overlay shows translucent building proxies + a legend over the
render meshes, headless tests green, packed to `local-feed`.

## Release (9.1.0)

Per `CLAUDE.md`, in order, after re-checking for a concurrent version bump and taking the
next free version if 9.1.0 is claimed:

1. Bump `<KhaozEngineVersion>` to 9.1.0 in `Directory.Build.props`.
2. `CHANGELOG.md` entry (newest-first, one-line summary first).
3. Update the three guard-checked version strings (`docs/CONSUMERS.md` engine current
   version, `docs/ROADMAP.md` current released version, the `README.md` PackageReference
   example).
4. Full doc sweep: `docs/USING-KHAOZENGINE.md` new section for the overlay API,
   `KhaozEngine.Render3D/README.md` (CollisionShapeOverlay, CollisionShapeMesh, ConvexHull3D,
   DrawOverlayMesh) and `KhaozEngine.Gui/README.md` (OverlayLegend). No `Physics` API change.
   `docs/CONSUMERS.md` note: Ruinborne (now 9.0.1) bumps 9.0.1 to 9.1.0 to adopt the overlay,
   a trivial same-major pin bump.
5. `dotnet pack -c Release -o ./local-feed`.
6. Commit, `git tag v9.1.0`.
7. Hold the push + tag, confirm with the user before publishing (heavy-CI batch policy).
   Exception to the hold: the golden bake needs the workflow on the remote, so the bake run
   is coordinated with the user as part of the release.
