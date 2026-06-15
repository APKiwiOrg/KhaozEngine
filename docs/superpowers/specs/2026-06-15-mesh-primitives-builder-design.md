# Procedural mesh primitives + MeshBuilder (Render3D) design

**Goal:** give KhaozEngine.Render3D a generic procedural-mesh toolkit — more `MeshPrimitives` shapes plus a
`MeshBuilder` that composes transformed, individually-coloured primitives into a single `GltfMesh`. This lets a
game build recognizable multi-part silhouettes (a turret, a drone, a tank) in code, with no asset files,
drawn as one instance. Track-B engine-first: the primitives + builder are generic (every game reuses them);
the game-specific assembly (what a "turret" looks like) stays in the game.

**Context:** today `MeshPrimitives` has only `Box` and `Tile`, and Hardpoint draws every entity as one tinted
box. The per-instance tint (`Scene3D.Draw(mesh, world, tint)`) multiplies a mesh's vertex colour, so a mesh
with baked per-part vertex colours renders multi-coloured under a single draw. The builder bakes those
colours.

## Existing types (unchanged)
- `struct ModelVertex { Vector3 Position; Vector3 Normal; Vector4 Color; ModelVertex(p,n,c); }` (40 bytes).
- `sealed class GltfMesh { ModelVertex[] Vertices; ushort[] Indices; GltfMesh(v,i); int TriangleCount; }`.
- `Scene3D.LoadMesh(GltfMesh) -> MeshHandle`. Indices are `ushort` -> a built mesh must stay < 65536 vertices.
- Winding: existing `Box` faces are outward CCW; new shapes must match (front faces CCW, outward normals).

## New primitives (`MeshPrimitives`, all return `GltfMesh`, white vertex colour)
All centered/seated consistently with `Box` (centered at origin) and `Tile` (base at y=0). Pick the seating
noted per shape. Smooth (radial) normals for curved surfaces; flat normals for caps/flats.

- `Cylinder(float radius = 0.5f, float height = 1f, int segments = 16, bool capped = true)` — axis along +Y,
  base at y=0, top at y=height. Side vertices have radial normals; caps (if `capped`) are flat (+Y / -Y),
  built as a triangle fan around a center vertex.
- `Cone(float radius = 0.5f, float height = 1f, int segments = 16, bool capped = true)` — base circle at y=0
  (radius `radius`), apex at (0, height, 0). Base cap flat -Y if `capped`. Side normals point outward/up.
- `Pyramid(float baseSize = 1f, float height = 1f)` — square base `baseSize`×`baseSize` at y=0 (centered on
  X/Z), apex at (0, height, 0). 4 triangular sides (flat normals) + base quad (-Y).
- `Wedge(float size = 1f, float height = 1f)` — right-triangular prism (a ramp): `size`×`size` footprint at
  y=0, rising linearly from y=0 at -Z to y=`height` at +Z. Flat normals; closed solid (5 faces: bottom, back,
  two triangular sides, sloped top).
- `Sphere(float radius = 0.5f, int rings = 8, int segments = 12)` — UV sphere centered at origin, smooth
  radial normals.

Guard against degenerate args (segments >= 3, rings >= 2, positive sizes) — clamp or throw `ArgumentException`;
pick clamp for segments/rings (min sensible) and document it.

## New `MeshBuilder` (new file `KhaozEngine.Render3D/MeshBuilder.cs`)
Composes primitives into one `GltfMesh`.

```csharp
public sealed class MeshBuilder
{
    public int VertexCount { get; }
    public int IndexCount { get; }

    // Append part transformed by `transform`, KEEPING the part's own vertex colours.
    public MeshBuilder Add(GltfMesh part, Matrix4x4 transform);

    // Append part transformed by `transform`, BAKING `color` onto every appended vertex (overrides part colour).
    public MeshBuilder Add(GltfMesh part, Matrix4x4 transform, Vector4 color);

    public GltfMesh Build();   // returns the accumulated mesh; safe to call once (or document re-use)
}
```
Rules:
- Positions transformed by `transform` (Vector3.Transform(pos, transform)).
- Normals transformed by the normal matrix = transpose(inverse(upper-3x3 of transform)), then normalized. For
  the common case (rotation + uniform scale + translation) this is correct; do it generally via
  `Matrix4x4.Invert` + transpose so non-uniform scale still yields correct normals. If `Invert` fails
  (degenerate), fall back to the rotation part.
- Indices offset by the current vertex count before appending (so sub-meshes don't collide).
- `Add(..., color)` sets each appended `ModelVertex.Color = color`.
- `Build` throws `InvalidOperationException` if total vertices would exceed `ushort.MaxValue`.
- Fluent (returns `this`).

## Files
- Modify `KhaozEngine.Render3D/MeshPrimitives.cs` — add Cylinder/Cone/Pyramid/Wedge/Sphere (keep Box/Tile).
  Refactor the shared face/quad helpers if it reduces duplication, but don't change Box/Tile output.
- Create `KhaozEngine.Render3D/MeshBuilder.cs`.
- Create `KhaozEngine.Tests/Render3D/MeshPrimitivesTests.cs` and `MeshBuilderTests.cs` (or extend an existing
  Render3D test file if one exists).
- Release: bump `<KhaozEngine5xVersion>` 5.17.0 -> 5.18.0-experimental, CHANGELOG, pack the 5 5.x packages.

## Testing (headless, no GPU)
- Each primitive: index count divisible by 3; every index < vertex count; all normals unit length (±1e-3);
  bounds correct (cone apex at y=height, base ring at y=0 within radius; cylinder spans y∈[0,height]; sphere
  within radius of origin; pyramid apex/base; wedge spans the slope). `capped:false` lowers the count vs
  `capped:true`.
- MeshBuilder: `Add` offsets indices (combined indices reference combined vertices, max index < VertexCount);
  a translated box has its positions shifted by the translation; the colour overload sets every appended
  vertex colour; two parts produce summed vertex/index counts; `Build` over-65535 guard throws.
- Visual: a composed multi-part, multi-colour mesh (cylinder base + box barrel + cone tip) is snapshot-verified
  on screen by the controller via `Render3DSnapshot` (asymmetric scene, per the upside-down lesson) — not part
  of the subagent's automated tests.
