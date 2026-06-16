# Mesh primitives round 2 + UV channel + smooth-normals (5.22.0-experimental)

**Goal:** broaden the procedural-mesh toolkit (Torus, Capsule, RoundedBox, Plane) and add a **UV texture-
coordinate channel** to the mesh vertex format (generated for every primitive) so meshes are ready for
textured rendering later, plus a smooth-normals utility. The textured-rendering *pipeline* is a deliberate
later step — this release just makes the geometry data carry UVs.

## Part 1 — UV channel on the vertex format (the risk; do carefully)

`ModelVertex` (Models/GltfMesh.cs) gains a `Vector2 Uv`:
```csharp
public struct ModelVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector4 Color;
    public Vector2 Uv;
    public ModelVertex(Vector3 p, Vector3 n, Vector4 c, Vector2 uv) { Position=p; Normal=n; Color=c; Uv=uv; }
    public ModelVertex(Vector3 p, Vector3 n, Vector4 c) : this(p, n, c, Vector2.Zero) { } // back-compat
    public const uint SizeInBytes = 48; // 3*4 + 3*4 + 4*4 + 2*4
}
```
Keep BOTH ctors so non-primitive call sites compile unchanged.

**Vertex layout** (Rendering/ModelRenderer.cs `vertexLayout`): append after Color, matching struct order:
`new VertexElementDescription("TexCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2)`.

**Shaders** (Internal/ShaderSources.cs `ModelVert`): add input `layout(location=3) in vec2 TexCoord;` and a
pass-through output `layout(location=4) out vec2 vUv;` set `vUv = TexCoord;`. `ModelFrag`: add matching
`layout(location=4) in vec2 vUv;` (declared, currently unused — texturing is a later step; declaring it keeps
the stage interface matched). Do NOT change the lighting math. (Stage in/out locations: vNormalW=0, vColor=1,
vDepth=2, vWorldPos=3, vUv=4.)

**`MeshBuilder.Add`** (MeshBuilder.cs): preserve UVs — `new ModelVertex(pos, nrm, color ?? v.Color, v.Uv)`.

**`GltfLoader`** (Models/GltfLoader.cs): read `TEXCOORD_0` if the glTF primitive has it (SharpGLTF
`GetVertexColumns().TexCoords0` or equivalent — check the API; if absent default to `Vector2.Zero`), and pass
it to the 4-arg `ModelVertex` ctor.

**Snapshot/line/billboard renderers are UNAFFECTED** — `LineVertex`/`BillboardVertex` are separate types.

## Part 2 — UV generation for the existing primitives

Update every existing `MeshPrimitives` builder (Box, Tile, Cylinder, Cone, Pyramid, Wedge, Sphere) to emit
sensible UVs (use the 4-arg ctor):
- Box/Tile/Wedge/Pyramid: per-face 0..1 (each quad/tri face maps its corners to (0,0)-(1,1)).
- Cylinder/Cone side: U = angle/2π, V = height fraction (0 at base, 1 at top); caps: planar disc mapping
  (centre at 0.5,0.5, ring on the unit circle).
- Sphere: U = longitude/2π, V = latitude/π.
Counts/indices/normals are unchanged — only the per-vertex Uv is added.

## Part 3 — New primitives (all return `GltfMesh`, base-at-0 or centered per the existing convention, white
vertex colour, CCW outward, smooth normals on curved surfaces, with UVs)

- `Plane(float width = 1f, float depth = 1f, int subdivisionsX = 1, int subdivisionsZ = 1)` — a flat XZ quad
  at y=0 centered on origin, subdivided into a grid (for terrain/UV-mapped floors); normal +Y; UV spans 0..1.
- `RoundedBox(float size = 1f, float radius = 0.1f, int segments = 4)` — a box with rounded edges/corners
  (radius clamped to < size/2). Smooth normals around the rounds. (A reasonable construction: per-corner
  sphere octants + edge cylinders + flat faces; or scale a subdivided cube toward rounded — pick a clean
  approach and keep the triangle count modest.)
- `Capsule(float radius = 0.5f, float height = 1f, int segments = 16, int rings = 6)` — a cylinder of body
  height `height` capped by two hemispheres of `radius`, axis +Y, base hemisphere bottom at y=0. Smooth radial
  normals; UV cylindrical on the body, polar on the caps.
- `Torus(float majorRadius = 0.5f, float minorRadius = 0.2f, int majorSegments = 24, int minorSegments = 12)`
  — a ring in the XZ plane centered at origin. Smooth normals; UV = (majorAngle/2π, minorAngle/2π).

Clamp degenerate args (segments/rings >= 3, positive radii; radius clamps as noted).

## Part 4 — Smooth-normals utility

New `MeshOps` (KhaozEngine.Render3D/MeshOps.cs), public static:
- `GltfMesh WithSmoothNormals(GltfMesh mesh, float positionEpsilon = 1e-5f)` — returns a copy whose normals are
  the area-or-count-averaged normals of all vertices sharing a position (welded by rounding to epsilon). Leaves
  positions/UVs/colours/indices intact. Useful to smooth a flat-shaded mesh (e.g. a faceted `Box`-built shape).
- (Optional) `GltfMesh RecomputeFlatNormals(GltfMesh mesh)` — per-triangle face normals (handy companion). Only
  add if cheap; otherwise skip.

## Files
- Modify `Models/GltfMesh.cs` (ModelVertex), `Rendering/ModelRenderer.cs` (vertex layout), `Internal/
  ShaderSources.cs` (ModelVert/Frag UV), `MeshBuilder.cs` (copy UV), `Models/GltfLoader.cs` (read TEXCOORD_0),
  `MeshPrimitives.cs` (UVs on existing + the 4 new shapes).
- Create `MeshOps.cs`.
- Tests: extend `KhaozEngine.Tests/Render3D/MeshPrimitivesTests.cs` (new shapes: index%3==0, indices in range,
  unit normals, bounds, UVs within [0,1] where applicable) + `MeshBuilderTests.cs` (UV preserved through Add) +
  a `MeshOpsTests.cs` (WithSmoothNormals welds + averages; a flat box becomes smooth at shared corners). The
  outward-normal-DIRECTION test helper from last round should also cover the new flat-faced parts.
- Release: bump `<KhaozEngine5xVersion>` 5.21.0 -> 5.22.0-experimental, CHANGELOG, pack the 6 5.x packages.

## Testing + verification
- Headless: as above. CRITICAL — the existing `Box`/`Tile`/etc. must still render correctly after the vertex-
  format change (the controller snapshot-verifies a scene of old + new primitives; a vertex-layout/shader
  stride mismatch would garble everything, so this is the key check).
- Determinism of counts unchanged for existing primitives (only Uv added).
