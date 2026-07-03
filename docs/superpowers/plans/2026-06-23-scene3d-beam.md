# Scene3D 3D Beam Primitive Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `Scene3D.DrawBeam(a, b, width, color, BeamStyle?)` — a camera-facing, additive, depth-interleaved glowing beam (lasers / thrusters / tethers) with a soft core+halo fragment shader and optional time-driven pulse/scroll + tapered ends.

**Architecture:** A pure `BeamGeometry` helper builds a view-aligned strip along a→b (mirroring `BillboardGeometry`). A dedicated `BeamRenderer` draws all beams in one additive pass INTO the model MRT framebuffer with depth-test-no-write (so geometry occludes the beam) and PreserveDestination on the normal/depth attachments (so the edge post-pass ignores it) — exactly the textured-billboard depth path. Per-beam style is baked into the vertex; one shared `{ViewProj, Time}` UBO drives animation off `Scene3D.EffectTimeSeconds`.

**Tech Stack:** C# / net10.0, KhaozEngine.Gpu (Veldrid behind the seam), GLSL 450 cross-compiled to SPIR-V, xUnit. Pure helpers are headless `[Fact]`; anything needing a `Scene3D` (its ctor needs a GPU device) is `[GpuFact]` gated behind `KE_GPU_TESTS=1`.

**Reference spec:** `docs/superpowers/specs/2026-06-23-scene3d-beam-design.md`

---

## File Structure

- **Create** `KhaozEngine.Render3D/BeamGeometry.cs` — pure view-aligned-strip builder (sibling to `BillboardGeometry.cs`).
- **Create** `KhaozEngine.Render3D/BeamStyle.cs` — public immutable `record struct` of beam tunables.
- **Create** `KhaozEngine.Render3D/Rendering/BeamRenderer.cs` — internal renderer (nested `BeamVertex`), mirrors `Rendering/TexturedBillboardRenderer.cs`.
- **Modify** `KhaozEngine.Render3D/Internal/ShaderSources.cs` — add `BeamVert` / `BeamFrag`.
- **Modify** `KhaozEngine.Render3D/Scene3D.cs` — `DrawBeam`, `EffectTimeSeconds`, `_beamItems` queue (cleared in `Begin`), `BeamItem`, `DrawBeams` flush, ctor/dispose wiring, internal `BeamCount`/`BeamItems`.
- **Create** `KhaozEngine.Tests/Render3D/BeamGeometryTests.cs` — headless geometry tests.
- **Create** `KhaozEngine.Tests/Render3D/BeamStyleTests.cs` — headless style tests.
- **Create** `KhaozEngine.Tests/Render3D/Scene3DBeamQueueTests.cs` — `[GpuFact]` queue + colour-resolution tests.
- **Modify** `KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs` — `scene3d_beam` golden.
- **Create** `KhaozEngine.Tests/Gpu/goldens/scene3d_beam.metal.txt` — baked Metal golden.
- **Modify** `docs/USING-KHAOZENGINE.md` — `DrawBeam` doc + recommended combo.
- **Modify** `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md` — release `7.26.0`.

---

## Task 1: `BeamGeometry` pure helper

**Files:**
- Create: `KhaozEngine.Render3D/BeamGeometry.cs`
- Test: `KhaozEngine.Tests/Render3D/BeamGeometryTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/Render3D/BeamGeometryTests.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class BeamGeometryTests
    {
        const float Eps = 1e-4f;
        static readonly Vector3 ViewDir = Vector3.Normalize(new Vector3(0.3f, -0.6f, -1f));

        [Fact]
        public void Corners_FacesCamera_SidePerpendicularToAxisAndViewDir()
        {
            var a = new Vector3(1, 2, 3);
            var b = new Vector3(4, 2, 7);
            Assert.True(BeamGeometry.Corners(a, b, ViewDir, 0.5f, out var aL, out var aR, out _, out _));

            Vector3 axis = Vector3.Normalize(b - a);
            Vector3 side = Vector3.Normalize(aR - aL);
            // The width axis perpendicular to both the beam axis and the view direction == the strip faces the camera.
            Assert.Equal(0f, Vector3.Dot(side, axis), 4);
            Assert.Equal(0f, Vector3.Dot(side, ViewDir), 4);
        }

        [Fact]
        public void Corners_SpansAToB()
        {
            var a = new Vector3(-2, 1, 0);
            var b = new Vector3(3, 1, 1);
            BeamGeometry.Corners(a, b, ViewDir, 0.4f, out var aL, out var aR, out var bL, out var bR);
            // Each end's corner midpoint is exactly that endpoint.
            Assert.True(Vector3.Distance((aL + aR) * 0.5f, a) < Eps);
            Assert.True(Vector3.Distance((bL + bR) * 0.5f, b) < Eps);
        }

        [Fact]
        public void Corners_RespectsWidth()
        {
            const float width = 0.8f;
            BeamGeometry.Corners(Vector3.Zero, new Vector3(0, 0, 5), ViewDir, width, out var aL, out var aR, out _, out _);
            // Full across span equals width (each corner is half a width off the axis).
            Assert.Equal(width, Vector3.Distance(aL, aR), 4);
        }

        [Fact]
        public void Triangles_WritesSixVerts_WithAcrossAndAlongUvs()
        {
            var a = Vector3.Zero;
            var b = new Vector3(0, 0, 4);
            Span<Vector3> pos = stackalloc Vector3[6];
            Span<Vector2> uv = stackalloc Vector2[6];
            int n = BeamGeometry.Triangles(a, b, ViewDir, 0.5f, pos, uv);
            Assert.Equal(6, n);

            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            foreach (var t in uv) { minU = MathF.Min(minU, t.X); maxU = MathF.Max(maxU, t.X); minV = MathF.Min(minV, t.Y); maxV = MathF.Max(maxV, t.Y); }
            Assert.Equal(0f, minU, 5); Assert.Equal(1f, maxU, 5);   // u across [0,1]
            Assert.Equal(0f, minV, 5); Assert.Equal(1f, maxV, 5);   // v along [0,1]

            // v=0 verts sit at the a-end (z=0), v=1 verts at the b-end (z=4).
            for (int i = 0; i < 6; i++)
                Assert.Equal(uv[i].Y < 0.5f ? 0f : 4f, pos[i].Z, 4);
        }

        [Fact]
        public void Corners_DegenerateAEqualsB_ReturnsFalse()
            => Assert.False(BeamGeometry.Corners(Vector3.One, Vector3.One, ViewDir, 0.5f, out _, out _, out _, out _));

        [Fact]
        public void Corners_NonPositiveWidth_ReturnsFalse()
            => Assert.False(BeamGeometry.Corners(Vector3.Zero, Vector3.UnitZ, ViewDir, 0f, out _, out _, out _, out _));

        [Fact]
        public void Triangles_Degenerate_ReturnsZero()
        {
            Span<Vector3> pos = stackalloc Vector3[6];
            Span<Vector2> uv = stackalloc Vector2[6];
            Assert.Equal(0, BeamGeometry.Triangles(Vector3.One, Vector3.One, ViewDir, 0.5f, pos, uv));
        }

        [Fact]
        public void Corners_AxisParallelToViewDir_IsFiniteWithProperWidth()
        {
            // axis = +Z, viewDir = +Z => cross degenerates; the fallback must stay finite and full-width.
            Assert.True(BeamGeometry.Corners(Vector3.Zero, new Vector3(0, 0, 5), Vector3.UnitZ, 0.6f, out var aL, out var aR, out _, out _));
            foreach (var c in new[] { aL.X, aL.Y, aL.Z, aR.X, aR.Y, aR.Z })
                Assert.False(float.IsNaN(c));
            Assert.Equal(0.6f, Vector3.Distance(aL, aR), 4);
        }

        [Fact]
        public void Triangles_ThrowsWhenSpanTooSmall()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                Span<Vector3> pos = new Vector3[5];
                Span<Vector2> uv = new Vector2[6];
                BeamGeometry.Triangles(Vector3.Zero, Vector3.UnitZ, ViewDir, 0.5f, pos, uv);
            });
            Assert.Throws<ArgumentException>(() =>
            {
                Span<Vector3> pos = new Vector3[6];
                Span<Vector2> uv = new Vector2[5];
                BeamGeometry.Triangles(Vector3.Zero, Vector3.UnitZ, ViewDir, 0.5f, pos, uv);
            });
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~BeamGeometryTests"`
Expected: FAIL to compile / `BeamGeometry` does not exist.

- [ ] **Step 3: Write the implementation**

Create `KhaozEngine.Render3D/BeamGeometry.cs`:

```csharp
using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Pure (GPU-free) helpers for building a camera-facing BEAM quad: a flat strip stretched along the axis
    /// a-&gt;b whose width direction faces the camera (perpendicular to both the beam axis and the view
    /// direction). Sibling to <see cref="BillboardGeometry"/>; the internal beam renderer consumes it.
    /// Unit-testable without a renderer.
    /// </summary>
    public static class BeamGeometry
    {
        /// <summary>
        /// The 4 corners of the camera-facing strip from <paramref name="a"/> to <paramref name="b"/>:
        /// <c>side = normalize(cross(viewDir, axis))</c>, each end offset <c>±side*(width/2)</c>. Outputs the
        /// a-end pair (<paramref name="aLeft"/>/<paramref name="aRight"/>) and the b-end pair. Returns false (no
        /// corners written) when the beam is degenerate (<paramref name="a"/>≈<paramref name="b"/> or
        /// <paramref name="width"/> &lt;= 0). When the axis is ~parallel to <paramref name="viewDir"/> (the beam
        /// points at/away from the camera) the cross degenerates; a stable perpendicular is chosen so the output
        /// stays finite (the strip is then edge-on, which is correct).
        /// </summary>
        public static bool Corners(Vector3 a, Vector3 b, Vector3 viewDir, float width,
            out Vector3 aLeft, out Vector3 aRight, out Vector3 bLeft, out Vector3 bRight)
        {
            aLeft = aRight = bLeft = bRight = default;
            Vector3 axisRaw = b - a;
            float len2 = axisRaw.LengthSquared();
            if (len2 < 1e-12f || width <= 0f) return false;

            Vector3 axis = axisRaw / MathF.Sqrt(len2);
            Vector3 vd = viewDir.LengthSquared() < 1e-12f ? -Vector3.UnitZ : Vector3.Normalize(viewDir);

            Vector3 s = Vector3.Cross(vd, axis);
            if (s.LengthSquared() < 1e-8f)          // axis ~parallel to viewDir: pick any perpendicular to axis
                s = PerpendicularTo(axis);
            Vector3 side = Vector3.Normalize(s) * (width * 0.5f);

            aLeft = a - side; aRight = a + side;
            bLeft = b - side; bRight = b + side;
            return true;
        }

        /// <summary>An arbitrary unit vector perpendicular to <paramref name="axis"/> (assumed unit length),
        /// chosen stably from whichever world axis is least parallel to it.</summary>
        static Vector3 PerpendicularTo(Vector3 axis)
        {
            Vector3 reference = MathF.Abs(axis.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            return Vector3.Normalize(Vector3.Cross(reference, axis));
        }

        /// <summary>
        /// Write the 6 triangle-list vertex positions (two triangles aLeft,aRight,bLeft and aRight,bRight,bLeft)
        /// for the beam strip into <paramref name="positions"/>, and matching UVs into <paramref name="uvs"/>:
        /// <c>u</c> is the across coordinate (0 on the left edge, 1 on the right), <c>v</c> the along coordinate
        /// (0 at <paramref name="a"/>, 1 at <paramref name="b"/>). Both spans must hold at least 6 elements.
        /// Returns 6, or 0 when the beam is degenerate (nothing written).
        /// </summary>
        public static int Triangles(Vector3 a, Vector3 b, Vector3 viewDir, float width,
            Span<Vector3> positions, Span<Vector2> uvs)
        {
            if (positions.Length < 6) throw new ArgumentException("positions span must hold at least 6 vertices", nameof(positions));
            if (uvs.Length < 6) throw new ArgumentException("uvs span must hold at least 6 vertices", nameof(uvs));

            if (!Corners(a, b, viewDir, width, out var aL, out var aR, out var bL, out var bR))
                return 0;

            positions[0] = aL; uvs[0] = new Vector2(0f, 0f);
            positions[1] = aR; uvs[1] = new Vector2(1f, 0f);
            positions[2] = bL; uvs[2] = new Vector2(0f, 1f);
            positions[3] = aR; uvs[3] = new Vector2(1f, 0f);
            positions[4] = bR; uvs[4] = new Vector2(1f, 1f);
            positions[5] = bL; uvs[5] = new Vector2(0f, 1f);
            return 6;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~BeamGeometryTests"`
Expected: PASS (10 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/BeamGeometry.cs KhaozEngine.Tests/Render3D/BeamGeometryTests.cs
git commit -m "render3d(beam): pure BeamGeometry view-aligned strip + headless tests"
```

---

## Task 2: `BeamStyle` public struct

**Files:**
- Create: `KhaozEngine.Render3D/BeamStyle.cs`
- Test: `KhaozEngine.Tests/Render3D/BeamStyleTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/Render3D/BeamStyleTests.cs`:

```csharp
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class BeamStyleTests
    {
        [Fact]
        public void Default_HasNullColours_AndSensibleShape()
        {
            var d = BeamStyle.Default;
            Assert.Null(d.CoreColor);    // null => the DrawBeam colour argument tints the beam
            Assert.Null(d.GlowColor);
            Assert.Equal(0.35f, d.CoreFraction, 4);
            Assert.Equal(2f, d.GlowSoftness, 4);
            Assert.Equal(0f, d.Taper, 4);
            Assert.Equal(0f, d.PulseSpeed, 4);
            Assert.Equal(0f, d.ScrollSpeed, 4);
        }

        [Fact]
        public void With_OverridesSingleField_LeavingOthers()
        {
            var s = BeamStyle.Default with { PulseSpeed = 6f, PulseAmount = 0.3f, Taper = 0.2f };
            Assert.Equal(6f, s.PulseSpeed, 4);
            Assert.Equal(0.3f, s.PulseAmount, 4);
            Assert.Equal(0.2f, s.Taper, 4);
            Assert.Equal(0.35f, s.CoreFraction, 4);   // unchanged from Default
            Assert.Null(s.CoreColor);                  // unchanged from Default
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~BeamStyleTests"`
Expected: FAIL to compile / `BeamStyle` does not exist.

- [ ] **Step 3: Write the implementation**

Create `KhaozEngine.Render3D/BeamStyle.cs`:

```csharp
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Tunables for a 3D energy beam (see <see cref="Scene3D.DrawBeam"/>). Immutable; derive variants with
    /// <c>with</c> (the idiom is <c>BeamStyle.Default with { ... }</c>, which keeps the sensible shape defaults).
    /// A bright inner core (<see cref="CoreColor"/>, the inner <see cref="CoreFraction"/> of the width) sits inside
    /// a softer halo (<see cref="GlowColor"/>, falling off across the full width by <see cref="GlowSoftness"/>).
    /// Both colours default (null) to the <see cref="Scene3D.DrawBeam"/> colour argument: the core uses it directly,
    /// the halo a 0.4x-alpha copy. Optional end <see cref="Taper"/>, brightness <see cref="PulseSpeed"/>/
    /// <see cref="PulseAmount"/>, and along-beam <see cref="ScrollSpeed"/> flow all read
    /// <see cref="Scene3D.EffectTimeSeconds"/>. Vocabulary mirrors the 2D
    /// <see cref="KhaozEngine.Render2D.Vfx.BeamParams"/>.
    /// </summary>
    public readonly record struct BeamStyle
    {
        /// <summary>Bright inner-core colour. Null =&gt; the <see cref="Scene3D.DrawBeam"/> colour argument.</summary>
        public Color? CoreColor { get; init; }

        /// <summary>Soft halo colour. Null =&gt; the resolved core colour at 0.4x alpha (a dimmer wash of the same hue).</summary>
        public Color? GlowColor { get; init; }

        /// <summary>Bright-core share of the half-width, in [0,1]. Default 0.35.</summary>
        public float CoreFraction { get; init; }

        /// <summary>Halo falloff exponent (higher = tighter halo hugging the core). Default 2.</summary>
        public float GlowSoftness { get; init; }

        /// <summary>End-fade fraction in [0,0.5]: the beam fades in over this fraction of its length at each end.
        /// 0 (default) = square ends.</summary>
        public float Taper { get; init; }

        /// <summary>Brightness pulse speed (radians/second). 0 (default) disables pulsing.</summary>
        public float PulseSpeed { get; init; }

        /// <summary>Pulse amplitude in [0,1]: fraction by which brightness oscillates. Default 0.</summary>
        public float PulseAmount { get; init; }

        /// <summary>Along-beam flow speed (cycles/second) of the core's brightness ripple. 0 (default) = no flow.</summary>
        public float ScrollSpeed { get; init; }

        /// <summary>A sensible starting point: hue-neutral (the <see cref="Scene3D.DrawBeam"/> colour tints both
        /// bands), a 35%-of-half-width bright core in a soft halo, square ends, static.</summary>
        public static BeamStyle Default => new()
        {
            CoreColor = null,
            GlowColor = null,
            CoreFraction = 0.35f,
            GlowSoftness = 2f,
            Taper = 0f,
            PulseSpeed = 0f,
            PulseAmount = 0f,
            ScrollSpeed = 0f,
        };
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~BeamStyleTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/BeamStyle.cs KhaozEngine.Tests/Render3D/BeamStyleTests.cs
git commit -m "render3d(beam): public BeamStyle tunables (split core/glow, taper, pulse, scroll)"
```

---

## Task 3: Beam shaders + `BeamRenderer`

No standalone unit test — this is GPU plumbing exercised by the golden in Task 5. The gate here is "the solution compiles and the SPIR-V cross-compiles at renderer construction" (the golden's `Render3DSnapshot.Capture` builds a `Scene3D` which builds the `BeamRenderer`, so a bad shader fails Task 5 loudly).

**Files:**
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.cs`
- Create: `KhaozEngine.Render3D/Rendering/BeamRenderer.cs`

- [ ] **Step 1: Add the shaders**

In `KhaozEngine.Render3D/Internal/ShaderSources.cs`, add these two constants immediately after the `TexturedBillboardFrag` constant (before the `FullscreenVert` section):

```csharp
        // ---- Additive glowing beam (lasers/thrusters/tethers). Drawn INTO the model MRT alongside the meshes
        //      with the depth test on (no write), so geometry occludes it (like the textured billboard). A
        //      camera-facing strip carries (across,along) UV; the core+halo profile is computed in the fragment
        //      shader from the across coordinate, with optional end taper + time-driven pulse/scroll. Per-beam
        //      style is baked per-vertex (split core/glow colour + two packed param vectors); the only uniform is
        //      ViewProj + Time, so the whole frame's beams render in one draw. Writes all 3 MRT targets to match
        //      the framebuffer; only colour matters (normal/depth use a PreserveDestination blend). ----
        public const string BeamVert = @"#version 450
layout(set=0, binding=0) uniform U { mat4 ViewProj; vec4 Time; };
layout(location=0) in vec3 Position;
layout(location=1) in vec2 Uv;
layout(location=2) in vec4 CoreColor;
layout(location=3) in vec4 GlowColor;
layout(location=4) in vec4 Shape;   // x=coreFrac, y=glowSoftness, z=taper
layout(location=5) in vec4 Anim;    // x=pulseSpeed, y=pulseAmount, z=scrollSpeed
layout(location=0) out vec2 vUv;
layout(location=1) out vec4 vCoreColor;
layout(location=2) out vec4 vGlowColor;
layout(location=3) out vec4 vShape;
layout(location=4) out vec4 vAnim;
void main() {
    gl_Position = ViewProj * vec4(Position, 1.0);
    vUv = Uv;
    vCoreColor = CoreColor;
    vGlowColor = GlowColor;
    vShape = Shape;
    vAnim = Anim;
}";

        public const string BeamFrag = @"#version 450
layout(set=0, binding=0) uniform U { mat4 ViewProj; vec4 Time; };
layout(location=0) in vec2 vUv;
layout(location=1) in vec4 vCoreColor;
layout(location=2) in vec4 vGlowColor;
layout(location=3) in vec4 vShape;   // x=coreFrac, y=glowSoftness, z=taper
layout(location=4) in vec4 vAnim;    // x=pulseSpeed, y=pulseAmount, z=scrollSpeed
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
void main() {
    float coreFrac = max(vShape.x, 0.02);
    float glowSoft = max(vShape.y, 0.5);
    float taper    = clamp(vShape.z, 0.0, 0.5);
    float d = abs(vUv.x * 2.0 - 1.0);                       // 0 at the axis, 1 at the edge
    float core = 1.0 - smoothstep(coreFrac * 0.6, coreFrac, d);
    float glow = pow(max(1.0 - d, 0.0), glowSoft);
    float taperFade = (taper > 0.0)
        ? smoothstep(0.0, taper, vUv.y) * smoothstep(0.0, taper, 1.0 - vUv.y)
        : 1.0;
    float pulse = 1.0 + vAnim.y * sin(Time.x * vAnim.x);
    float flow  = (vAnim.z != 0.0)
        ? 0.85 + 0.15 * sin((vUv.y - Time.x * vAnim.z) * 6.2831853)
        : 1.0;
    float master = max(taperFade * pulse, 0.0);
    vec3 rgb = vCoreColor.rgb * vCoreColor.a * core * flow
             + vGlowColor.rgb * vGlowColor.a * glow;
    oColor  = vec4(rgb, master);   // Additive (src.a / one): out.rgb = rgb*master + dst.rgb
    oNormal = vec4(0.0);           // discarded (PreserveDestination on attachment 1)
    oDepth  = vec4(0.0);           // discarded (PreserveDestination on attachment 2)
}";
```

- [ ] **Step 2: Create the renderer**

Create `KhaozEngine.Render3D/Rendering/BeamRenderer.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws additive glowing BEAMS (lasers/thrusters/tethers) INTO the model MRT framebuffer alongside the lit
    /// meshes, so they interleave in depth: the depth test (less-or-equal, no write) reads the meshes' depth, so a
    /// nearer mesh occludes the beam and a beam in front draws over a farther mesh. Attachment 0 is additive;
    /// attachments 1 &amp; 2 (normal, depth) preserve their destination so the edge post-pass ignores the beam. A
    /// camera-facing strip per beam (built by <see cref="BeamGeometry"/>); all beams in ONE draw - the per-beam
    /// style is baked into the vertex, so there is no per-draw uniform rebinding (the Metal/Veldrid mid-list
    /// uniform hazard the skinned-bone path documents).
    /// </summary>
    internal sealed class BeamRenderer : IDisposable
    {
        /// <summary>One beam vertex: world position, (across,along) UV, split core/glow colours, and two packed
        /// param vectors (shape: coreFrac/glowSoftness/taper; anim: pulseSpeed/pulseAmount/scrollSpeed). 84 bytes.</summary>
        internal struct BeamVertex
        {
            public Vector3 Position;
            public Vector2 Uv;
            public Vector4 CoreColor;
            public Vector4 GlowColor;
            public Vector4 Shape;
            public Vector4 Anim;
            public BeamVertex(Vector3 position, Vector2 uv, Vector4 coreColor, Vector4 glowColor, Vector4 shape, Vector4 anim)
            { Position = position; Uv = uv; CoreColor = coreColor; GlowColor = glowColor; Shape = shape; Anim = anim; }
            public const uint SizeInBytes = 84;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct FrameUniforms { public Matrix4x4 ViewProj; public Vector4 Time; }

        readonly IGpuDevice _gd;
        readonly IGpuBuffer _ubo;              // mat4 ViewProj + vec4 Time (80 bytes)
        readonly IGpuResourceLayout _layout;   // UBO (vertex + fragment)
        readonly IGpuResourceSet _set;
        readonly IGpuShaderSet _shaders;
        readonly IGpuPipeline _pipeline;
        IGpuBuffer? _vb;
        uint _vbCapacity;                      // capacity in vertices

        public BeamRenderer(IGpuDevice gd, GpuOutputDescription modelOutputs)
        {
            _gd = gd;
            var factory = gd.Factory;

            _ubo = factory.CreateBuffer(new GpuBufferDescription(80, GpuBufferUsage.UniformBuffer)); // mat4 + vec4
            _layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment)));
            _set = factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _ubo));
            _shaders = factory.CreateShadersFromSpirv(ShaderSources.BeamVert, ShaderSources.BeamFrag);

            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Uv", GpuVertexElementFormat.Float2),
                new GpuVertexElement("CoreColor", GpuVertexElementFormat.Float4),
                new GpuVertexElement("GlowColor", GpuVertexElementFormat.Float4),
                new GpuVertexElement("Shape", GpuVertexElementFormat.Float4),
                new GpuVertexElement("Anim", GpuVertexElementFormat.Float4));

            // Attachment 0 additive (glow accumulation); normal/depth preserved so the edge pass reads the
            // meshes' normal/depth, not the beam's (no outline traced around the strip).
            var blends = new[] { GpuBlendAttachment.Additive, GpuBlendAttachment.PreserveDestination, GpuBlendAttachment.PreserveDestination };

            _pipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = blends,
                // Read the meshes' depth (interleave/occlude) but don't write it: additive glow must not occlude.
                DepthStencil = GpuDepthStencilState.DepthTestLessEqualNoWrite,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { _layout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
                Outputs = modelOutputs,
            });
        }

        /// <summary>Upload this frame's view-projection (clip-Y corrected) + time once before the draw.</summary>
        public void SetFrameUniforms(IGpuCommandList cl, Matrix4x4 viewProj, float timeSeconds)
        {
            var u = new FrameUniforms
            {
                ViewProj = GpuClip.Correct(viewProj, _gd.Capabilities),
                Time = new Vector4(timeSeconds, 0f, 0f, 0f),
            };
            cl.UpdateBuffer(_ubo, 0, in u);
        }

        /// <summary>Draw <paramref name="verts"/> (all beams' strips) into <paramref name="target"/> (the model FB,
        /// no clear). <see cref="SetFrameUniforms"/> must have run this frame. No-op when empty.</summary>
        public void Draw(IGpuCommandList cl, ReadOnlySpan<BeamVertex> verts, IGpuFramebuffer target)
        {
            if (verts.Length == 0) return;

            EnsureCapacity((uint)verts.Length);
            cl.UpdateBuffer(_vb!, 0, verts);

            cl.SetFramebuffer(target);
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(0, _set);
            cl.SetVertexBuffer(0, _vb!);
            cl.Draw((uint)verts.Length, 1, 0, 0);
        }

        void EnsureCapacity(uint vertexCount)
        {
            if (_vb != null && _vbCapacity >= vertexCount) return;
            _vb?.Dispose();
            _vbCapacity = Math.Max(vertexCount, _vbCapacity == 0 ? 64u : _vbCapacity * 2);
            _vb = _gd.Factory.CreateBuffer(new GpuBufferDescription(_vbCapacity * BeamVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }

        public void Dispose()
        {
            _pipeline.Dispose();
            _shaders.Dispose();
            _set.Dispose();
            _layout.Dispose();
            _ubo.Dispose();
            _vb?.Dispose();
        }
    }
}
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj -c Debug`
Expected: Build succeeded (0 errors). If `GpuResourceSetDescription(_layout, _ubo)` or any seam signature differs, fix against the sibling `TexturedBillboardRenderer.cs` which uses the same factory calls.

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Render3D/Internal/ShaderSources.cs KhaozEngine.Render3D/Rendering/BeamRenderer.cs
git commit -m "render3d(beam): BeamVert/BeamFrag + additive depth-interleaved BeamRenderer"
```

---

## Task 4: `Scene3D` wiring + queue tests

**Files:**
- Modify: `KhaozEngine.Render3D/Scene3D.cs`
- Test: `KhaozEngine.Tests/Render3D/Scene3DBeamQueueTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/Render3D/Scene3DBeamQueueTests.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // DrawBeam queues onto a live Scene3D (its ctor needs a GPU device), so these run gated behind
    // KE_GPU_TESTS=1, mirroring Scene3DFillTests. They assert queue accounting + colour resolution only;
    // geometry is covered headlessly by BeamGeometryTests and the on-screen look by the golden snapshot.
    public sealed class Scene3DBeamQueueTests
    {
        static void WithScene(Action<Scene3D> body)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var f = gpu.GpuDevice.Factory;
            using IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gpu.GpuDevice, fb.Outputs);
            body(scene);
        }

        [GpuFact]
        public void DrawBeam_Queues_Then_Begin_Clears() => WithScene(scene =>
        {
            scene.Begin();
            Assert.Equal(0, scene.BeamCount);

            scene.DrawBeam(new Vector3(-1, 0, 0), new Vector3(1, 0, 0), 0.3f, Color.White);
            Assert.Equal(1, scene.BeamCount);

            scene.DrawBeam(Vector3.Zero, new Vector3(0, 2, 0), 0.2f, new Color(1f, 0f, 0f, 1f));
            Assert.Equal(2, scene.BeamCount);

            scene.Begin();
            Assert.Equal(0, scene.BeamCount);
        });

        [GpuFact]
        public void DrawBeam_Degenerate_IsNoOp() => WithScene(scene =>
        {
            scene.Begin();
            scene.DrawBeam(Vector3.One, Vector3.One, 0.5f, Color.White);          // a == b
            scene.DrawBeam(Vector3.Zero, new Vector3(0, 1, 0), 0f, Color.White);  // width 0
            Assert.Equal(0, scene.BeamCount);
        });

        [GpuFact]
        public void DrawBeam_NullColours_ResolveFromColourArg() => WithScene(scene =>
        {
            scene.Begin();
            scene.DrawBeam(Vector3.Zero, new Vector3(1, 0, 0), 0.3f, new Color(1f, 0f, 0f, 1f)); // style null => Default
            var item = scene.BeamItems[0];
            // core resolves to the colour arg (red)
            Assert.Equal(1f, item.CoreColor.X, 4);
            Assert.Equal(0f, item.CoreColor.Y, 4);
            Assert.Equal(1f, item.CoreColor.W, 4);
            // glow derives from the core at 0.4x alpha, same hue
            Assert.Equal(1f, item.GlowColor.X, 4);
            Assert.Equal(0.4f, item.GlowColor.W, 4);
        });

        [GpuFact]
        public void DrawBeam_StyleColours_OverrideTheArg() => WithScene(scene =>
        {
            scene.Begin();
            var style = BeamStyle.Default with
            {
                CoreColor = new Color(0f, 1f, 0f, 1f),
                GlowColor = new Color(0f, 0f, 1f, 0.5f),
            };
            scene.DrawBeam(Vector3.Zero, new Vector3(1, 0, 0), 0.3f, Color.White, style);
            var item = scene.BeamItems[0];
            Assert.Equal(1f, item.CoreColor.Y, 4);   // core = green
            Assert.Equal(1f, item.GlowColor.Z, 4);   // glow = blue
            Assert.Equal(0.5f, item.GlowColor.W, 4);
        });

        [GpuFact]
        public void EffectTimeSeconds_RoundTrips_AndBeginDoesNotClearIt() => WithScene(scene =>
        {
            scene.EffectTimeSeconds = 3.5f;
            scene.Begin();
            Assert.Equal(3.5f, scene.EffectTimeSeconds, 4);   // a clock the host owns: NOT cleared by Begin
        });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Scene3DBeamQueueTests"`
Expected: FAIL to compile / `DrawBeam`, `BeamCount`, `BeamItems`, `EffectTimeSeconds` do not exist.

- [ ] **Step 3: Wire `Scene3D`**

In `KhaozEngine.Render3D/Scene3D.cs`:

(a) Add the renderer + queue fields. After the `_texBillboards` field declaration (`readonly TexturedBillboardRenderer _texBillboards;`) add:

```csharp
        readonly BeamRenderer _beams;
```

After the `_texBillboardVerts` list declaration add:

```csharp
        // Glowing beams (lasers/thrusters/tethers): queued in submission order, flushed as one additive draw
        // into the model FB alongside the textured billboards (depth-interleaved, so geometry occludes them).
        readonly List<BeamItem> _beamItems = new();
        readonly List<BeamRenderer.BeamVertex> _beamVerts = new();
```

(b) Add the public clock property. After `public PixelPostProcessSettings Post { get; } = new();` add:

```csharp
        /// <summary>Host-set per-frame clock (seconds) driving beam pulse/scroll (see <see cref="DrawBeam"/> /
        /// <see cref="BeamStyle"/>). Set it once per frame in your draw callback (it runs after <see cref="Begin"/>),
        /// e.g. <c>scene.EffectTimeSeconds = totalSeconds</c>. NOT cleared by <see cref="Begin"/> - the host owns it.
        /// Presentation only; zero (never set) renders a static beam. A generic clock so future time-driven 3D
        /// effects can share it.</summary>
        public float EffectTimeSeconds { get; set; }
```

(c) Construct the renderer. In the constructor, after the `_texBillboards = new TexturedBillboardRenderer(...)` line add:

```csharp
            // Beams draw into the same model MRT as the textured billboards (depth-interleaved), so they target the
            // model framebuffer's output description.
            _beams = new BeamRenderer(gd, _res.ModelFB.Outputs);
```

(d) Clear the queue in `Begin()`. After `_texBillboardItems.Clear();` add:

```csharp
            _beamItems.Clear();
```

(Leave `EffectTimeSeconds` untouched in `Begin` - the host owns it.)

(e) Add `DrawBeam` + the internal accessors. Place this immediately after the textured-billboard `DrawBillboard(...)` overloads and the `internal int TexturedBillboardCount` member (before the `RenderTargetWidth` member):

```csharp
        // ---- Glowing beams (lasers/thrusters/tethers): a camera-facing strip a->b, additive, depth-interleaved
        //      into the model pass so geometry occludes it. Soft core+halo + optional taper/pulse/scroll in the
        //      fragment shader; animation reads EffectTimeSeconds. ----

        /// <summary>
        /// Queue an additive glowing beam from <paramref name="a"/> to <paramref name="b"/> (world points),
        /// <paramref name="width"/> world units across (the quad spans <paramref name="width"/>, i.e. ±width/2 from
        /// the axis), tinted by <paramref name="color"/> (the core colour unless <paramref name="style"/> overrides
        /// it). A camera-facing strip with a bright core + soft halo; optional end taper and time-driven pulse/scroll
        /// come from <paramref name="style"/> (null =&gt; <see cref="BeamStyle.Default"/>) and
        /// <see cref="EffectTimeSeconds"/>. Drawn INTO the model pass with the depth test on (no write), like the
        /// textured billboard, so a nearer mesh occludes the beam. Cleared in <see cref="Begin"/>. A degenerate beam
        /// (<paramref name="a"/>≈<paramref name="b"/> or <paramref name="width"/> &lt;= 0) is a silent no-op.
        /// Presentation only.
        /// </summary>
        public void DrawBeam(Vector3 a, Vector3 b, float width, Color color, BeamStyle? style = null)
        {
            if (width <= 0f || (b - a).LengthSquared() < 1e-12f) return;   // degenerate: nothing to draw
            BeamStyle s = style ?? BeamStyle.Default;
            Vector4 core = s.CoreColor ?? color;
            Vector4 glow = s.GlowColor is Color g ? g : new Vector4(core.X, core.Y, core.Z, core.W * 0.4f);
            _beamItems.Add(new BeamItem
            {
                A = a, B = b, Width = width,
                CoreColor = core,
                GlowColor = glow,
                Shape = new Vector4(s.CoreFraction, s.GlowSoftness, s.Taper, 0f),
                Anim = new Vector4(s.PulseSpeed, s.PulseAmount, s.ScrollSpeed, 0f),
            });
        }

        /// <summary>Count of beams queued this frame. Internal: lets tests assert <see cref="Begin"/> clears the
        /// queue and <see cref="DrawBeam"/> enqueues.</summary>
        internal int BeamCount => _beamItems.Count;

        /// <summary>The beams queued this frame (resolved colours/params). Internal: lets tests assert colour
        /// resolution.</summary>
        internal IReadOnlyList<BeamItem> BeamItems => _beamItems;
```

(f) Add the `BeamItem` struct. Place it next to the other internal queue structs, after the `TexturedBillboardRun` struct:

```csharp
        /// <summary>One queued beam: world endpoints + width, resolved core/glow colours (RGBA as Vector4), and two
        /// packed param vectors (Shape: coreFrac/glowSoftness/taper; Anim: pulseSpeed/pulseAmount/scrollSpeed).
        /// Built in <see cref="DrawBeam"/>; consumed in <see cref="DrawBeams"/>.</summary>
        internal struct BeamItem
        {
            public Vector3 A, B;
            public float Width;
            public Vector4 CoreColor;
            public Vector4 GlowColor;
            public Vector4 Shape;
            public Vector4 Anim;
        }
```

(g) Add the flush. In `RenderInternal`, immediately after the `DrawTexturedBillboards(cl);` call add:

```csharp
            // Beams: same model FB (still bound), after the textured billboards, before the post chain - so they
            // depth-interleave with the meshes and go through the pixel post like everything else in the model pass.
            DrawBeams(cl);
```

Then add the `DrawBeams` method next to `DrawTexturedBillboards`:

```csharp
        /// <summary>Build each queued beam's camera-facing strip (via <see cref="BeamGeometry"/>) into one vertex
        /// stream and draw them all in a single additive pass into the model FB. The model FB is still bound from
        /// the mesh pass; its depth buffer holds the meshes' depth so the beams interleave. No-op when nothing is
        /// queued.</summary>
        void DrawBeams(IGpuCommandList cl)
        {
            if (_beamItems.Count == 0) return;

            Vector3 viewDir = Camera.Forward;   // constant across the frame, matching the billboard basis
            _beamVerts.Clear();
            Span<Vector3> pos = stackalloc Vector3[6];
            Span<Vector2> uv = stackalloc Vector2[6];
            foreach (var it in _beamItems)
            {
                int n = BeamGeometry.Triangles(it.A, it.B, viewDir, it.Width, pos, uv);
                for (int v = 0; v < n; v++)
                    _beamVerts.Add(new BeamRenderer.BeamVertex(pos[v], uv[v], it.CoreColor, it.GlowColor, it.Shape, it.Anim));
            }
            if (_beamVerts.Count == 0) return;

            _beams.SetFrameUniforms(cl, Camera.ViewProjection, EffectTimeSeconds);
            _beams.Draw(cl, CollectionsMarshal.AsSpan(_beamVerts), _res.ModelFB);
        }
```

(h) Dispose the renderer. In `Dispose()`, after `_texBillboards.Dispose();` add:

```csharp
            _beams.Dispose();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Scene3DBeamQueueTests"`
Expected: PASS (6 tests). (If `KE_GPU_TESTS` is unset the tests SKIP — set it to actually exercise them.)

- [ ] **Step 5: Run the full headless suite to confirm no regressions**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (GPU tests skipped, everything else green).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render3D/Scene3D.cs KhaozEngine.Tests/Render3D/Scene3DBeamQueueTests.cs
git commit -m "render3d(beam): Scene3D.DrawBeam + EffectTimeSeconds clock, queue, flush, wiring"
```

---

## Task 5: GPU golden `scene3d_beam` + Metal bake

**Files:**
- Modify: `KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs`
- Create: `KhaozEngine.Tests/Gpu/goldens/scene3d_beam.metal.txt`

- [ ] **Step 1: Add the golden test**

In `KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs`, add this method immediately after `Golden3D_TexturedBillboard_DepthInterleaved` (before `Golden2D_FixedScene`):

```csharp
        [GpuFact]
        public void Golden3D_Beam_DepthInterleaved()
        {
            MeshHandle box = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    box = scene.LoadMesh(MeshPrimitives.Box(1.2f));
                    scene.Post.Starfield = false;   // flat background so the occlusion + glow read clearly
                    scene.Camera.Frame(Vector3.Zero, new Vector3(4.5f, 4.5f, 4.5f));
                    scene.EffectTimeSeconds = 0f;   // static frame => deterministic golden (no pulse/scroll)
                },
                drawFrame: scene =>
                {
                    // Opaque green box at the origin.
                    scene.Draw(box, Matrix4x4.Identity, new Color(0.15f, 0.7f, 0.2f, 1f));
                    // A bright magenta beam straight through the box (left -> right): the box occludes the centre,
                    // the glowing tapered ends poke out either side - locks the depth-interleave AND the additive glow.
                    scene.DrawBeam(new Vector3(-3f, 0f, 0f), new Vector3(3f, 0f, 0f), 0.5f,
                        new Color(1f, 0.2f, 0.9f, 1f),
                        BeamStyle.Default with { Taper = 0.15f });
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_beam", rgba, W, H);
        }
```

- [ ] **Step 2: Verify the golden is missing (the test fails informatively)**

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Golden3D_Beam_DepthInterleaved"`
Expected: FAIL with "golden 'scene3d_beam' missing ... Run with KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1 to generate it." (This also proves the beam shaders cross-compile and the pass renders without crashing.)

- [ ] **Step 3: Bake the Metal golden**

Run: `KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Golden3D_Beam_DepthInterleaved"`
Expected: PASS (the update path writes `KhaozEngine.Tests/Gpu/goldens/scene3d_beam.metal.txt` and skips the assert).

Then confirm the new file exists: `ls KhaozEngine.Tests/Gpu/goldens/scene3d_beam.metal.txt`

- [ ] **Step 4: Verify the golden now passes on re-run (compare path)**

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Golden3D_Beam_DepthInterleaved"`
Expected: PASS (compares against the just-baked Metal grid).

- [ ] **Step 5: Sanity-check the cross-backend guard still passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CrossBackendGoldenTests"`
Expected: PASS. (`scene3d_beam` has only a Metal golden so far, so it contributes no cross-backend pair and cannot fail this guard. The D3D11 + Vulkan goldens are baked on their own backends as a follow-up - see Task 8 note.)

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs KhaozEngine.Tests/Gpu/goldens/scene3d_beam.metal.txt
git commit -m "test(gpu): scene3d_beam depth-interleaved golden + Metal bake"
```

---

## Task 6: Document `DrawBeam` in USING-KHAOZENGINE.md

**Files:**
- Modify: `docs/USING-KHAOZENGINE.md`

- [ ] **Step 1: Read the billboard/light overlay section**

Run: `grep -n "DrawBillboard\|AddLight\|Dynamic point lights\|EnergyBeam" docs/USING-KHAOZENGINE.md`
Locate the 3D overlay section around the `scene.DrawBillboard(...)` / `scene.AddLight(...)` lines (~388-418).

- [ ] **Step 2: Insert the DrawBeam documentation**

Immediately after the existing `Dynamic point lights` bullet (the `scene.AddLight(...)` entry), add this block. Match the surrounding markdown style (terse, code-fenced examples):

````markdown
- 3D beams (since 7.26.0): `scene.DrawBeam(a, b, width, color, BeamStyle?)` queues a camera-facing, additive,
  **depth-interleaved** glowing beam between two world points (lasers, thrusters, tethers) - a bright core in a
  soft halo. It draws INTO the model pass with the depth test on (no write), like the textured billboard, so
  geometry occludes it. `color` tints the core; `BeamStyle` (default `BeamStyle.Default`) splits core/glow colour
  and adds `CoreFraction`, `GlowSoftness`, end `Taper`, and time-driven `PulseSpeed`/`PulseAmount` + `ScrollSpeed`.
  Animation reads `scene.EffectTimeSeconds`, a per-frame clock you set in your draw callback (it is NOT cleared by
  `Begin`); leave it at 0 for a static beam. A degenerate beam (`a≈b` or `width<=0`) is a no-op.

  ```csharp
  scene.EffectTimeSeconds = totalSeconds;                 // once per frame (host clock)
  var style = BeamStyle.Default with { Taper = 0.15f, PulseSpeed = 8f, PulseAmount = 0.2f, ScrollSpeed = 1.5f };
  scene.DrawBeam(muzzle, hit, 0.4f, new Color(1f, 0.3f, 0.9f, 1f), style);
  ```

  Recommended combo for an impactful beam: pair `DrawBeam` with an `AddLight` at each endpoint (so the beam lights
  nearby geometry) and a `ParticleSystem` spark burst at the impact point:

  ```csharp
  scene.DrawBeam(muzzle, hit, 0.4f, beamColor, style);
  scene.AddLight(muzzle, beamColor, radius: 4f, intensity: 2f);
  scene.AddLight(hit,    beamColor, radius: 5f, intensity: 3f);   // brighter flash at the impact
  // sparks at the impact: loop your particle system's Active span and DrawBillboard each (Additive)
  ```
````

- [ ] **Step 3: Commit**

```bash
git add docs/USING-KHAOZENGINE.md
git commit -m "docs(using): DrawBeam + AddLight + sparks recommended combo"
```

---

## Task 7: Release `7.26.0` (version bump + changelogs + doc-version declarations)

Minor bump (additive API). All on the feature branch; the merge + pack + tag + push is Task 8.

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`

- [ ] **Step 1: Bump the version line**

In `Directory.Build.props` change `<KhaozEngineVersion>7.25.0</KhaozEngineVersion>` to:

```xml
    <KhaozEngineVersion>7.26.0</KhaozEngineVersion>
```

- [ ] **Step 2: Add the CHANGELOG entry**

Add the newest-first entry at the top of `CHANGELOG.md` (match the existing entry format; no em-dashes):

```markdown
## 7.26.0

- render3d: `Scene3D.DrawBeam(a, b, width, color, BeamStyle?)` - a camera-facing, additive, depth-interleaved
  glowing beam between two world points (lasers, thrusters, tethers). A bright core in a soft halo, drawn into the
  model pass with the depth test on (no write) like the textured billboard, so geometry occludes it. New public
  `BeamStyle` (split core/glow colour, `CoreFraction`, `GlowSoftness`, end `Taper`, `PulseSpeed`/`PulseAmount`,
  `ScrollSpeed`) and `BeamGeometry` (pure view-aligned-strip builder). Time-driven pulse/scroll read a new
  per-frame `Scene3D.EffectTimeSeconds` clock the host sets (not cleared by `Begin`); 0 = static. New `BeamVert`/
  `BeamFrag` shaders and an internal `BeamRenderer` (one additive draw for all beams). Headless `BeamGeometryTests`/
  `BeamStyleTests`, gated `Scene3DBeamQueueTests`, and a `scene3d_beam` depth-interleave golden.
```

- [ ] **Step 3: Add the CHANGENOTES digest line**

Add the newest-first one-line digest at the top of `CHANGENOTES.md` (match the existing one-line style):

```markdown
- 7.26.0: Scene3D.DrawBeam - additive, depth-interleaved 3D glow beams (lasers/thrusters/tethers) with split
  core/glow, taper, and time-driven pulse/scroll via a new EffectTimeSeconds clock.
```

- [ ] **Step 4: Update the three guarded doc-version declarations**

The guard `scripts/check-doc-versions.sh` requires these three match `7.26.0`. Update each:

1. `docs/CONSUMERS.md` - the "Engine current version" line. Find it: `grep -n "current version\|Engine current" docs/CONSUMERS.md`, change the version to `7.26.0`.
2. `docs/ROADMAP.md` - the "Current released version" line. Find it: `grep -n "Current released version" docs/ROADMAP.md`, change to `7.26.0`.
3. `README.md` - the `<PackageReference>` example version. Find it: `grep -n "PackageReference.*KhaozEngine\|Version=\"7" README.md`, change the example version to `7.26.0`.

- [ ] **Step 5: Run the doc-version guard**

Run: `bash scripts/check-doc-versions.sh`
Expected: exit 0 (all three declarations match `7.26.0`). If it fails, it prints which file mismatches - fix that line.

- [ ] **Step 6: Run the full headless suite once more**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS.

- [ ] **Step 7: Commit the release bump**

```bash
git add Directory.Build.props CHANGELOG.md CHANGENOTES.md docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "render3d(7.26.0): Scene3D 3D beam primitive (DrawBeam + BeamStyle)"
```

---

## Task 8: Finish - merge to main, pack, tag, push

Per the engine release ritual (a finished release is a full publish) and the global finishing default. The `dotnet pack` runs from the **main** repo root, not the worktree, because the worktree's `local-feed` is deleted when the worktree is removed (engine-release-worktree-gotchas).

- [ ] **Step 1: Re-check no concurrent release took 7.26.0**

Run (from the worktree): `git fetch origin --tags && git tag | grep 7.26 ; git log origin/main --oneline -3`
Expected: no `v7.26.0` tag and `origin/main` still at the base this branch forked from. If someone else already shipped 7.26.0, bump this branch to the next free version (redo Task 7 with `7.27.0`) before merging.

- [ ] **Step 2: Merge the branch into main (from the main repo root)**

```bash
cd /Users/antonio/KhaozEngine
git checkout main
git merge --no-ff worktree-feature+scene3d-beam -m "render3d(7.26.0): Scene3D 3D beam primitive"
```

- [ ] **Step 3: Run the suite on the merged result (headless + GPU)**

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Beam|FullyQualifiedName~Golden3D_Beam|FullyQualifiedName~CrossBackendGoldenTests"
```
Expected: PASS. If anything fails, stop and fix on main before tagging.

- [ ] **Step 4: Pack to local-feed (cumulative within the release)**

```bash
mkdir -p local-feed
dotnet pack -c Release -o ./local-feed
```
Expected: every packable project packs at `7.26.0` into `./local-feed`.

- [ ] **Step 5: Tag and push**

```bash
git tag v7.26.0
git push origin main
git push origin v7.26.0
```
Expected: `main` + tag pushed; CI publishes the `7.26.0` packages to GitHub Packages on the `v7.26.0` tag.

- [ ] **Step 6: Clean up the worktree and merged branch**

```bash
git worktree remove .claude/worktrees/feature+scene3d-beam
git branch -d worktree-feature+scene3d-beam
```
(The branch was local-only; nothing to delete on the remote. If it had ever been pushed, also `git push origin --delete worktree-feature+scene3d-beam`.)

- [ ] **Step 7: Follow-up note - D3D11 + Vulkan goldens**

The `scene3d_beam` golden was baked on **Metal** only. Bake the `direct3d11` and `vulkan` references on those backends (as was done for `scene3d_normalmap`) with
`KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1 dotnet test --filter "FullyQualifiedName~Golden3D_Beam_DepthInterleaved"`,
then commit `scene3d_beam.direct3d11.txt` + `scene3d_beam.vulkan.txt`. Until then the cross-backend guard simply has no pair to check for this scene (it does not fail).

---

## Self-Review

**Spec coverage:**
- DrawBeam signature + queue/flush => Task 4. ✓
- Camera-facing strip a→b, width => Task 1 (`BeamGeometry`). ✓
- Additive, depth-interleaved into model MRT, PreserveDestination on normal/depth => Task 3 (`BeamRenderer`). ✓
- Soft core+halo + taper + pulse/scroll fragment shader => Task 3 (`BeamFrag`). ✓
- Split core/glow colour + scene clock (the two brainstorm decisions) => Tasks 2 (`BeamStyle`) + 4 (`EffectTimeSeconds`, colour resolution). ✓
- Headless geometry test (faces camera, spans a→b, respects width) => Task 1. ✓
- Per-backend golden bake => Task 5 (Metal) + Task 8 step 7 (D3D11/Vulkan follow-up). ✓
- Doc the recommended combo => Task 6. ✓
- SemVer minor + full release ritual => Tasks 7 + 8. ✓

**Type consistency:** `BeamItem` (Scene3D) fields `A,B,Width,CoreColor,GlowColor,Shape,Anim` map 1:1 onto `BeamRenderer.BeamVertex` ctor args `(pos, uv, coreColor, glowColor, shape, anim)`. `BeamStyle` fields (`CoreColor?`,`GlowColor?`,`CoreFraction`,`GlowSoftness`,`Taper`,`PulseSpeed`,`PulseAmount`,`ScrollSpeed`) are consumed exactly in `DrawBeam`. Shader vertex `in` locations 0-5 match the `GpuVertexLayoutDescription` element order in `BeamRenderer`. `SetFrameUniforms`/`Draw` signatures match the `DrawBeams` call site. ✓

**Placeholder scan:** No TBD/TODO; every code step shows full code; commands have expected output. ✓
