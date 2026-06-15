# Render3D debug draw (5.20.0-experimental)

**Goal:** an immediate-mode 3D line/wireframe overlay in Render3D — `scene.DebugLine/Box/Grid/Axes/Circle/Ray`
— drawn on top of the post-processed image with the camera's view-projection. For dev viz and in-game cues
(range rings, flow-field arrows, board grid, bounds, gizmos). Cheap, no lighting, overlays everything.

## Pipeline hook
`Scene3D.RenderInternal(cl, w, h, target)` already ends with `_post.Run(cl, _res, target, Post)` which leaves
the final image on `target`. Debug lines render AFTER that, into the same `target`, using
`Camera.ViewProjection` — a separate **LineList** pipeline (depth disabled = overlay, alpha blend). The line
pipeline's `Outputs` is the scene's `_targetOutput` (the same description `target` was created with).

## API — `Scene3D` (additive, immediate-mode)
Debug segments accumulate per frame and clear in `Begin()` (same lifecycle as instances). All colours are RGBA
`Vector4`. None of these affect the model pass.

```csharp
public void DebugLine(Vector3 a, Vector3 b, Vector4 color);
public void DebugRay(Vector3 origin, Vector3 direction, float length, Vector4 color);
public void DebugBox(Vector3 center, Vector3 size, Vector4 color);                    // 12 edges, axis-aligned
public void DebugGrid(Vector3 center, float cellSize, int cells, Vector4 color);      // XZ-plane grid, cells x cells
public void DebugAxes(Vector3 origin, float scale);                                   // X red, Y green, Z blue
public void DebugCircle(Vector3 center, Vector3 normal, float radius, Vector4 color, int segments = 32);
```
`DebugCircle` with `normal = UnitY` draws a ground ring (the tower-range use case). `DebugGrid` draws
`cells+1` lines each way on the XZ plane through `center`, spanning `cells*cellSize`.

## Pure geometry helper — `DebugShapes` (new, public static, testable headlessly)
The segment GEOMETRY is built by pure functions so it can be unit-tested with no GPU. `Scene3D.Debug*` call
these then attach the colour.

```csharp
namespace KhaozEngine.Render3D;
public static class DebugShapes
{
    // Each appends line-segment ENDPOINTS (pairs) to `segments`. Count of added Vector3 is always even.
    public static void Box(List<Vector3> segments, Vector3 center, Vector3 size);        // adds 24 (12 edges)
    public static void Grid(List<Vector3> segments, Vector3 center, float cellSize, int cells); // adds (cells+1)*2 lines *2
    public static void Circle(List<Vector3> segments, Vector3 center, Vector3 normal, float radius, int segments_count); // adds segments_count*2
    public static void Axes(List<Vector3> segments, Vector3 origin, float scale);        // adds 6 (3 lines) — caller colours per-axis
}
```
(Axes colouring is per-axis, so `Scene3D.DebugAxes` builds the 3 coloured lines directly rather than via the
single-colour helper; `DebugShapes.Axes` is still provided + tested for the endpoints.)

## Internal `LineRenderer` (new, `Rendering/LineRenderer.cs`)
- Vertex: `struct LineVertex { Vector3 Position; Vector4 Color; }` (28 bytes) — internal.
- Pipeline: `PrimitiveTopology.LineList`, vertex layout (Position Float3, Color Float4), depth disabled
  (`DepthStencilStateDescription.Disabled`), alpha blend (`BlendAttachmentDescription.AlphaBlend`),
  `FaceCullMode.None`, `Outputs = targetOutput`. UBO = one `mat4 ViewProj` (64 bytes).
- A growable dynamic vertex buffer (recreate when the segment count exceeds capacity).
- `Draw(CommandList cl, Matrix4x4 viewProj, ReadOnlySpan<LineVertex> verts, Framebuffer target)`:
  `SetFramebuffer(target)` (no clear — overlay), upload verts + viewProj, set pipeline/resource set/vertex
  buffer, `cl.Draw((uint)verts.Length, ...)`. No-op when empty.
- Shaders (new entries in `Internal/ShaderSources.cs`): `LineVert` (gl_Position = ViewProj * vec4(Position,1);
  pass Color), `LineFrag` (oColor = vColor). Single color target (the final target has one attachment).

## `Scene3D` wiring
- Hold a `LineRenderer _lines` (created in the ctor with `_targetOutput`) + a `List<LineVertex> _lineVerts`.
- `Begin()` also clears `_lineVerts`.
- `Debug*` methods append to `_lineVerts` (via `DebugShapes` + colour).
- `RenderInternal`, after `_post.Run(...)`: if `_lineVerts.Count > 0`, `_lines.Draw(cl, Camera.ViewProjection,
  _lineVerts, target)`.
- Dispose `_lines`.

## Files
- Create `KhaozEngine.Render3D/DebugShapes.cs`, `KhaozEngine.Render3D/Rendering/LineRenderer.cs`.
- Modify `KhaozEngine.Render3D/Scene3D.cs` (debug API + wiring), `Internal/ShaderSources.cs` (Line shaders).
- Create `KhaozEngine.Tests/Render3D/DebugShapesTests.cs`.
- Release: bump `<KhaozEngine5xVersion>` 5.19.0 -> 5.20.0-experimental, CHANGELOG, pack 5 pkgs.

## Testing
- Headless (no GPU): `DebugShapes.Box` adds 24 endpoints forming the 12 axis-aligned edges (assert the 8
  corners appear, all segments axis-aligned); `Grid` adds the right count and lies on the XZ plane at
  `center.Y`; `Circle` adds `segments*2` endpoints all at `radius` from centre and in the plane perpendicular
  to `normal`; `Axes` adds 6 endpoints (3 unit-scaled axes from origin). All even counts.
- Visual (controller, Render3DSnapshot): a scene with a model + `DebugGrid` on the ground, `DebugBox` around a
  mesh, `DebugAxes` at the origin, and a `DebugCircle` ground ring — confirm the overlay renders on top, lines
  are correctly transformed by the camera, and colours/alpha blend. Asymmetric scene (upside-down lesson).
