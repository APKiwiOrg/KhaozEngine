# PBR-lite materials (normal + roughness maps) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add tangent-space normal maps + a roughness map to the rigid lit model pass (PBR-lite), fully additive, with the no-map path rendering bit-identical to today.

**Architecture:** Add a tangent to `ModelVertex` (computed in `MeshAssembler`, read from glTF `TANGENT`). Extend the model resource set with optional Normal + Roughness textures (1x1 flat-normal / zero-roughness defaults) using the proven `EdgeFrag` grouped-textures-then-sampler layout. Rewrite `ModelFrag` to build a TBN and perturb the lighting normal, and to modulate Blinn-Phong specular by roughness; both reduce exactly to today's expressions at the defaults. A pure CPU mirror (`SurfaceShading`) gives headless coverage; a new gated GPU golden + the unchanged existing goldens give cross-backend coverage.

**Tech Stack:** C# / net10.0, KhaozEngine.Render3D, GLSL #version 450 (cross-compiled GLSL->SPIR-V->MSL/HLSL/GLSL via the GPU seam), xUnit headless + gated GPU golden snapshots, Veldrid behind KhaozEngine.Gpu.

**Conventions for this plan:**
- Work in the existing worktree (branch `worktree-feature+pbr-lite-materials`). Run all commands from
  `/Users/antonio/KhaozEngine/.claude/worktrees/feature+pbr-lite-materials`.
- Headless tests run with `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`. GPU goldens are gated
  (`[GpuFact]`, need `KE_GPU_TESTS=1` + a device).
- **One version bump per batch.** Tasks 1-9 commit individually with a non-version scope
  (`render3d(pbr-lite): ...`). The single version bump + CHANGELOG/CHANGENOTES + pack happens once in
  Task 10 (scope `render3d(7.23.0):` / `docs(7.23.0):`).
- No em-dashes anywhere.

---

### Task 1: Tangent field on `ModelVertex`

**Files:**
- Modify: `KhaozEngine.Render3D/Models/GltfMesh.cs` (the `ModelVertex` struct, lines ~7-17)
- Test: `KhaozEngine.Tests/Render3D/ModelVertexTangentTests.cs` (create)

- [ ] **Step 1: Write the failing test**

```csharp
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class ModelVertexTangentTests
    {
        [Fact]
        public void Vertex_is_64_bytes_with_tangent()
        {
            Assert.Equal(64u, ModelVertex.SizeInBytes);
            Assert.Equal(64, Marshal.SizeOf<ModelVertex>());
        }

        [Fact]
        public void Legacy_ctors_leave_tangent_zero()
        {
            var v4 = new ModelVertex(Vector3.UnitX, Vector3.UnitY, Vector4.One, new Vector2(0.25f, 0.5f));
            var v3 = new ModelVertex(Vector3.UnitX, Vector3.UnitY, Vector4.One);
            Assert.Equal(Vector4.Zero, v4.Tangent);
            Assert.Equal(Vector4.Zero, v3.Tangent);
            // Existing fields are unchanged.
            Assert.Equal(new Vector2(0.25f, 0.5f), v4.Uv);
        }

        [Fact]
        public void Five_arg_ctor_sets_tangent()
        {
            var t = new Vector4(1f, 0f, 0f, -1f);
            var v = new ModelVertex(Vector3.Zero, Vector3.UnitY, Vector4.One, Vector2.Zero, t);
            Assert.Equal(t, v.Tangent);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~ModelVertexTangentTests`
Expected: FAIL to compile / assert (no `Tangent` member, `SizeInBytes` is 48).

- [ ] **Step 3: Implement**

Replace the `ModelVertex` struct in `KhaozEngine.Render3D/Models/GltfMesh.cs` with:

```csharp
    /// <summary>Interleaved vertex: position, normal, base color (RGBA), texture UV, and a tangent
    /// (xyz = model-space tangent direction, w = +/-1 bitangent handedness). 64 bytes. A zero tangent
    /// (the default from the back-compat ctors) signals "no TBN" to the shader, which then lights with the
    /// geometric normal - so untangented meshes (primitives, skinned) render exactly as before.</summary>
    public struct ModelVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Color;
        public Vector2 Uv;
        public Vector4 Tangent;
        public ModelVertex(Vector3 p, Vector3 n, Vector4 c, Vector2 uv, Vector4 tangent)
        { Position = p; Normal = n; Color = c; Uv = uv; Tangent = tangent; }
        public ModelVertex(Vector3 p, Vector3 n, Vector4 c, Vector2 uv) : this(p, n, c, uv, Vector4.Zero) { }
        public ModelVertex(Vector3 p, Vector3 n, Vector4 c) : this(p, n, c, Vector2.Zero, Vector4.Zero) { } // back-compat
        public const uint SizeInBytes = 64; // 3*4 + 3*4 + 4*4 + 2*4 + 4*4
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~ModelVertexTangentTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Models/GltfMesh.cs KhaozEngine.Tests/Render3D/ModelVertexTangentTests.cs
git commit -m "render3d(pbr-lite): add tangent to ModelVertex (64 bytes, back-compat ctors)"
```

---

### Task 2: `MeshAssembler` computes tangents; `MeshCorner` carries an optional source tangent

**Files:**
- Modify: `KhaozEngine.Render3D/Models/MeshAssembler.cs` (the `MeshCorner` struct + `MeshAssembler.Build`)
- Test: `KhaozEngine.Tests/Render3D/MeshAssemblerTangentTests.cs` (create)

- [ ] **Step 1: Write the failing test**

```csharp
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class MeshAssemblerTangentTests
    {
        // A unit quad in the XY plane (normal +Z supplied), UVs mapping +X->+U, +Y->+V.
        static List<MeshCorner> Quad(Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD)
        {
            Vector3 A = new(0, 0, 0), B = new(1, 0, 0), C = new(1, 1, 0), D = new(0, 1, 0);
            Vector3 n = Vector3.UnitZ;
            return new List<MeshCorner>
            {
                new(A, n, Vector4.One, uvA), new(B, n, Vector4.One, uvB), new(C, n, Vector4.One, uvC),
                new(A, n, Vector4.One, uvA), new(C, n, Vector4.One, uvC), new(D, n, Vector4.One, uvD),
            };
        }

        [Fact]
        public void Computes_orthonormal_tangent_pointing_along_U()
        {
            var mesh = MeshAssembler.Build(Quad(new(0, 0), new(1, 0), new(1, 1), new(0, 1)));
            foreach (var v in mesh.Vertices)
            {
                var t = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
                Assert.True(t.Length() > 0.99f && t.Length() < 1.01f, $"tangent not unit: {t.Length()}");
                Assert.True(System.MathF.Abs(Vector3.Dot(t, v.Normal)) < 1e-3f, "tangent not orthogonal to normal");
                Assert.True(t.X > 0.98f, $"tangent should point along +X, got {t}");
                Assert.True(v.Tangent.W == 1f || v.Tangent.W == -1f, $"handedness must be +/-1, got {v.Tangent.W}");
            }
        }

        [Fact]
        public void Degenerate_uv_yields_zero_tangent()
        {
            var mesh = MeshAssembler.Build(Quad(Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero));
            foreach (var v in mesh.Vertices)
                Assert.Equal(Vector4.Zero, v.Tangent);
        }

        [Fact]
        public void Source_tangent_is_honoured_when_supplied()
        {
            Vector3 A = new(0, 0, 0), B = new(1, 0, 0), C = new(1, 1, 0);
            Vector3 n = Vector3.UnitZ;
            var src = new Vector4(0f, 1f, 0f, -1f); // along +Y, handedness -1
            var corners = new List<MeshCorner>
            {
                new(A, n, Vector4.One, new Vector2(0, 0), src),
                new(B, n, Vector4.One, new Vector2(1, 0), src),
                new(C, n, Vector4.One, new Vector2(1, 1), src),
            };
            var mesh = MeshAssembler.Build(corners);
            foreach (var v in mesh.Vertices)
            {
                Assert.True(System.MathF.Abs(v.Tangent.Y - 1f) < 1e-4f, $"expected +Y tangent, got {v.Tangent}");
                Assert.Equal(-1f, v.Tangent.W);
            }
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~MeshAssemblerTangentTests`
Expected: FAIL to compile (`MeshCorner` has no 5-arg ctor; tangents are zero).

- [ ] **Step 3: Implement**

In `KhaozEngine.Render3D/Models/MeshAssembler.cs`, extend `MeshCorner` to carry an optional source tangent:

```csharp
    internal readonly struct MeshCorner
    {
        public readonly Vector3 Position;
        public readonly Vector3 Normal;   // meaningful only when HasNormal
        public readonly bool HasNormal;
        public readonly Vector4 Color;
        public readonly Vector2 Uv;
        public readonly Vector4? Tangent;  // source tangent (xyz dir, w handedness); null => compute from UV+pos

        public MeshCorner(Vector3 position, Vector3? normal, Vector4 color, Vector2 uv, Vector4? tangent = null)
        {
            Position = position;
            HasNormal = normal.HasValue;
            Normal = normal ?? default;
            Color = color;
            Uv = uv;
            Tangent = tangent;
        }
    }
```

Then update `MeshAssembler.Build` to accumulate and finalize tangents. Replace the body from the local
arrays through the final return with:

```csharp
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();   // source normal, or an accumulator for computed ones
            var colors = new List<Vector4>();
            var uvs = new List<Vector2>();
            var computed = new List<bool>();      // true => normals[i] is an accumulator to normalize at the end
            var tan1 = new List<Vector3>();       // accumulated UV-space s-direction per welded vertex
            var tan2 = new List<Vector3>();       // accumulated UV-space t-direction per welded vertex
            var srcTangent = new List<Vector4?>();// source tangent if the corner supplied one
            var weld = new Dictionary<(long, long, long, bool, long, long, long, long, long), int>();
            var indices = new List<int>(corners.Count);

            int Resolve(in MeshCorner c, Vector3 faceN, Vector3 sdir, Vector3 tdir)
            {
                var key = (Q(c.Position.X, 1e4f), Q(c.Position.Y, 1e4f), Q(c.Position.Z, 1e4f),
                           c.HasNormal,
                           c.HasNormal ? Q(c.Normal.X, 1e3f) : 0L,
                           c.HasNormal ? Q(c.Normal.Y, 1e3f) : 0L,
                           c.HasNormal ? Q(c.Normal.Z, 1e3f) : 0L,
                           Q(c.Uv.X, 1e4f), Q(c.Uv.Y, 1e4f));

                if (weld.TryGetValue(key, out int existing))
                {
                    if (!c.HasNormal) normals[existing] += faceN; // keep smoothing across shared faces
                    tan1[existing] += sdir;                       // accumulate tangent dirs across shared faces
                    tan2[existing] += tdir;
                    return existing;
                }

                int idx = positions.Count;
                positions.Add(c.Position);
                colors.Add(c.Color);
                uvs.Add(c.Uv);
                normals.Add(c.HasNormal ? c.Normal : faceN);
                computed.Add(!c.HasNormal);
                tan1.Add(sdir);
                tan2.Add(tdir);
                srcTangent.Add(c.Tangent);
                weld[key] = idx;
                return idx;
            }

            for (int t = 0; t < corners.Count; t += 3)
            {
                MeshCorner c0 = corners[t], c1 = corners[t + 1], c2 = corners[t + 2];
                Vector3 faceN = Vector3.Cross(c1.Position - c0.Position, c2.Position - c0.Position);
                // Lengyel per-face tangent (s) / bitangent (t) directions from the UV gradient.
                Vector3 e1 = c1.Position - c0.Position, e2 = c2.Position - c0.Position;
                float du1 = c1.Uv.X - c0.Uv.X, dv1 = c1.Uv.Y - c0.Uv.Y;
                float du2 = c2.Uv.X - c0.Uv.X, dv2 = c2.Uv.Y - c0.Uv.Y;
                float r = du1 * dv2 - du2 * dv1;
                Vector3 sdir = Vector3.Zero, tdir = Vector3.Zero;
                if (MathF.Abs(r) > 1e-12f)
                {
                    float f = 1f / r;
                    sdir = (e1 * dv2 - e2 * dv1) * f;
                    tdir = (e2 * du1 - e1 * du2) * f;
                }
                indices.Add(Resolve(c0, faceN, sdir, tdir));
                indices.Add(Resolve(c1, faceN, sdir, tdir));
                indices.Add(Resolve(c2, faceN, sdir, tdir));
            }

            // 32-bit indices: no ushort ceiling. GltfMesh picks UInt16 for meshes that still fit (<= 65536 verts)
            // and UInt32 beyond, so large welded meshes load instead of throwing/truncating.
            var verts = new ModelVertex[positions.Count];
            for (int i = 0; i < positions.Count; i++)
            {
                Vector3 n = normals[i].LengthSquared() > 1e-12f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
                verts[i] = new ModelVertex(positions[i], n, colors[i], uvs[i], ResolveTangent(n, tan1[i], tan2[i], srcTangent[i]));
            }

            var outIndices = new uint[indices.Count];
            for (int i = 0; i < indices.Count; i++) outIndices[i] = (uint)indices[i];
            return new GltfMesh(verts, outIndices);
```

And add this helper method to the `MeshAssembler` class (next to `Q`):

```csharp
        // Finalize one vertex's tangent: a supplied source tangent (normalized, handedness preserved) wins;
        // otherwise Gram-Schmidt orthogonalize the accumulated s-direction against the normal and take the
        // handedness sign from the bitangent. Degenerate input (no UV gradient) => zero tangent (shader falls
        // back to the geometric normal).
        static Vector4 ResolveTangent(Vector3 n, Vector3 sdir, Vector3 tdir, Vector4? source)
        {
            if (source.HasValue)
            {
                var s = source.Value;
                var t = new Vector3(s.X, s.Y, s.Z);
                if (t.LengthSquared() <= 1e-12f) return Vector4.Zero;
                return new Vector4(Vector3.Normalize(t), s.W == 0f ? 1f : s.W);
            }
            Vector3 ortho = sdir - n * Vector3.Dot(n, sdir);
            if (ortho.LengthSquared() <= 1e-12f) return Vector4.Zero;
            ortho = Vector3.Normalize(ortho);
            float w = Vector3.Dot(Vector3.Cross(n, sdir), tdir) < 0f ? -1f : 1f;
            return new Vector4(ortho, w);
        }
```

Update the `MeshAssembler` summary comment to mention it now also produces per-vertex tangents.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~MeshAssemblerTangentTests`
Expected: PASS (3 tests). Also run the existing assembler tests to confirm topology is unchanged:
`dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~MeshAssembler`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Models/MeshAssembler.cs KhaozEngine.Tests/Render3D/MeshAssemblerTangentTests.cs
git commit -m "render3d(pbr-lite): compute per-vertex tangents in MeshAssembler"
```

---

### Task 3: `GltfLoader.Load` reads the glTF `TANGENT` accessor

**Files:**
- Modify: `KhaozEngine.Render3D/Models/GltfLoader.cs` (`Load`, lines ~14-44)
- Test: `KhaozEngine.Tests/Render3D/GltfLoaderTangentTests.cs` (create)

The test asset `KhaozEngine.Tests/assets/testmodel.glb` is copied next to the test assembly. This test is
robust regardless of whether that asset carries tangents/UVs: every tangent must be finite and either
~zero (no UV gradient / no source tangent) or ~unit length (computed or read). It guards against garbage
from the new accessor read while Task 2 already proves tangent correctness.

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class GltfLoaderTangentTests
    {
        static string Asset => Path.Combine(AppContext.BaseDirectory, "assets", "testmodel.glb");

        [Fact]
        public void Loaded_tangents_are_finite_and_zero_or_unit()
        {
            Assert.True(File.Exists(Asset), $"test asset missing at {Asset}");
            var mesh = GltfLoader.Load(Asset);
            Assert.NotEmpty(mesh.Vertices);
            foreach (var v in mesh.Vertices)
            {
                var t = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
                Assert.True(float.IsFinite(t.X) && float.IsFinite(t.Y) && float.IsFinite(t.Z)
                            && float.IsFinite(v.Tangent.W), "tangent has a non-finite component");
                float len = t.Length();
                Assert.True(len < 1e-4f || (len > 0.99f && len < 1.01f), $"tangent neither zero nor unit: {len}");
            }
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails (or passes trivially)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~GltfLoaderTangentTests`
Note: after Task 2, `Load` already routes through `MeshAssembler`, so this may already PASS (computed
tangents). That is acceptable - Step 3 still wires the `TANGENT` read so artist-supplied tangents win.
If it FAILS (asset has tangents that are mishandled), Step 3 fixes it.

- [ ] **Step 3: Implement the TANGENT read**

In `KhaozEngine.Render3D/Models/GltfLoader.cs`, inside `Load`'s primitive loop, read the accessor and pass
it through. After the `texcoords` line add:

```csharp
                var srcTangents = prim.GetVertexAccessor("TANGENT")?.AsVector4Array();
```

Add a local accessor next to `Norm`/`Uv`:

```csharp
                Vector4? Tan(int i) => srcTangents != null && i < srcTangents.Count ? srcTangents[i] : (Vector4?)null;
```

And pass the tangent into each corner:

```csharp
                foreach (var (a, b, c) in prim.GetTriangleIndices())
                {
                    corners.Add(new MeshCorner(pos[a], Norm(a), baseColor, Uv(a), Tan(a)));
                    corners.Add(new MeshCorner(pos[b], Norm(b), baseColor, Uv(b), Tan(b)));
                    corners.Add(new MeshCorner(pos[c], Norm(c), baseColor, Uv(c), Tan(c)));
                }
```

Update the `Load` summary to note: "reads POSITION/NORMAL/TEXCOORD_0/TANGENT; a missing TANGENT is computed
from UV+position by MeshAssembler. Material textures (normal/roughness) are bound separately via
Scene3D.SurfaceMaps, not auto-read."

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~GltfLoaderTangentTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Models/GltfLoader.cs KhaozEngine.Tests/Render3D/GltfLoaderTangentTests.cs
git commit -m "render3d(pbr-lite): read glTF TANGENT accessor (compute fallback)"
```

---

### Task 4: `SurfaceShading` CPU mirror + default-map texels

This is the headless coverage of the new shader math (the `SkinningMath` pattern: a pure CPU mirror,
commented as authoritative-by-convention and backed by the GPU golden). The shader in Task 6 must match it
exactly.

**Files:**
- Create: `KhaozEngine.Render3D/SurfaceShading.cs`
- Create: `KhaozEngine.Render3D/Internal/DefaultMaps.cs`
- Test: `KhaozEngine.Tests/Render3D/SurfaceShadingTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SurfaceShadingTests
    {
        [Fact]
        public void Decodes_flat_normal_texel_to_z_axis()
        {
            // 1x1 flat-normal default decodes to ~(0,0,1).
            byte[] texel = DefaultMaps.FlatNormalTexel();
            var rgb = new Vector3(texel[0] / 255f, texel[1] / 255f, texel[2] / 255f);
            var n = SurfaceShading.DecodeNormalSample(rgb);
            Assert.True(MathF.Abs(n.X) < 0.01f && MathF.Abs(n.Y) < 0.01f && MathF.Abs(n.Z - 1f) < 0.01f, $"{n}");
        }

        [Fact]
        public void Default_roughness_texel_is_zero_green()
        {
            byte[] texel = DefaultMaps.ZeroRoughnessTexel();
            Assert.Equal(0, texel[1]); // .g sampled by the shader
            Assert.Equal(255, texel[3]); // opaque
        }

        [Fact]
        public void Flat_normal_reproduces_geometric_normal()
        {
            var N = Vector3.Normalize(new Vector3(0.2f, 0.9f, 0.1f));
            var tangent = new Vector4(1f, 0f, 0f, 1f);
            var nTS = new Vector3(0f, 0f, 1f); // flat
            var got = SurfaceShading.PerturbNormal(N, tangent, nTS);
            Assert.True((got - N).Length() < 1e-4f, $"expected {N}, got {got}");
        }

        [Fact]
        public void Zero_tangent_falls_back_to_geometric_normal()
        {
            var N = Vector3.Normalize(new Vector3(0.2f, 0.9f, 0.1f));
            var got = SurfaceShading.PerturbNormal(N, Vector4.Zero, new Vector3(0.7f, 0f, 0.7f));
            Assert.True((got - N).Length() < 1e-4f);
        }

        [Fact]
        public void Tangent_space_tilt_pushes_normal_toward_tangent()
        {
            var N = Vector3.UnitZ;                 // geometric normal +Z
            var tangent = new Vector4(1f, 0f, 0f, 1f); // tangent +X
            var nTS = Vector3.Normalize(new Vector3(0.6f, 0f, 0.8f)); // tilt toward +x in tangent space
            var got = SurfaceShading.PerturbNormal(N, tangent, nTS);
            Assert.True(got.X > 0.1f, $"normal should tilt toward +X, got {got}");
            Assert.True(MathF.Abs(got.Length() - 1f) < 1e-4f);
        }

        [Fact]
        public void Roughness_zero_is_identity_for_spec_params()
        {
            var (s, e) = SurfaceShading.ApplyRoughness(0.8f, 48f, 0f);
            Assert.Equal(0.8f, s, 5);
            Assert.Equal(48f, e, 5);
        }

        [Fact]
        public void Higher_roughness_lowers_strength_and_exponent()
        {
            var (s0, e0) = SurfaceShading.ApplyRoughness(0.8f, 48f, 0.25f);
            var (s1, e1) = SurfaceShading.ApplyRoughness(0.8f, 48f, 0.75f);
            Assert.True(s1 < s0 && s0 < 0.8f);
            Assert.True(e1 < e0 && e0 < 48f);
            // Fully rough clamps the exponent to MinSpecExponent (>= 1) and kills strength.
            var (sFull, eFull) = SurfaceShading.ApplyRoughness(0.8f, 48f, 1f);
            Assert.Equal(0f, sFull, 5);
            Assert.Equal(SurfaceShading.MinSpecExponent, eFull, 5);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~SurfaceShadingTests`
Expected: FAIL to compile (`SurfaceShading` / `DefaultMaps` do not exist).

- [ ] **Step 3: Implement**

Create `KhaozEngine.Render3D/Internal/DefaultMaps.cs`:

```csharp
namespace KhaozEngine.Render3D.Internal
{
    /// <summary>1x1 default texels for the optional model maps, kept pure so the byte values are
    /// headless-testable. Flat normal (128,128,255) decodes to tangent-space (0,0,1); zero roughness
    /// (.g = 0) means "fully smooth" so the per-instance specular is used unchanged. These are the
    /// no-map defaults that keep untextured meshes rendering bit-identical to the pre-PBR pass.</summary>
    internal static class DefaultMaps
    {
        public static byte[] FlatNormalTexel() => new byte[] { 128, 128, 255, 255 };
        public static byte[] ZeroRoughnessTexel() => new byte[] { 0, 0, 0, 255 };
    }
}
```

Create `KhaozEngine.Render3D/SurfaceShading.cs`:

```csharp
using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Pure, GPU-free mirror of the model fragment shader's PBR-lite surface math: decoding a
    /// tangent-space normal sample, perturbing the geometric normal through a TBN built from an interpolated
    /// tangent, and modulating Blinn-Phong specular by roughness. This documents the intended math and makes
    /// it headless-unit-testable; ModelFrag (Internal/ShaderSources.cs) MUST mirror it. Presentation only.</summary>
    public static class SurfaceShading
    {
        /// <summary>Specular exponent at full roughness (the broad-highlight floor). The exponent eases from
        /// the per-instance shininess (roughness 0) down to this (roughness 1).</summary>
        public const float MinSpecExponent = 8f;

        /// <summary>Decode an RGB normal-map sample (each channel 0..1) to a tangent-space normal (-1..1).</summary>
        public static Vector3 DecodeNormalSample(Vector3 rgb) => rgb * 2f - Vector3.One;

        /// <summary>Perturb <paramref name="geoNormal"/> by a tangent-space normal using
        /// <paramref name="tangent"/> (xyz = model-space tangent, w = +/-1 handedness). A zero/degenerate
        /// tangent returns the (normalized) geometric normal unchanged - the no-TBN fallback. With a flat
        /// sample (0,0,1) the result is the geometric normal.</summary>
        public static Vector3 PerturbNormal(Vector3 geoNormal, Vector4 tangent, Vector3 tangentSpaceNormal)
        {
            Vector3 N = SafeNormalize(geoNormal);
            var t = new Vector3(tangent.X, tangent.Y, tangent.Z);
            if (t.LengthSquared() <= 1e-10f) return N;
            Vector3 T = SafeNormalize(t);
            T = SafeNormalize(T - N * Vector3.Dot(N, T));     // Gram-Schmidt
            Vector3 B = Vector3.Cross(N, T) * tangent.W;       // handedness
            // mat3(T,B,N) * nTS  (columns are T, B, N).
            Vector3 perturbed = T * tangentSpaceNormal.X + B * tangentSpaceNormal.Y + N * tangentSpaceNormal.Z;
            return SafeNormalize(perturbed);
        }

        /// <summary>Modulate the per-instance Blinn-Phong spec by roughness (0..1): strength scales by
        /// (1 - rough); the exponent eases from <paramref name="baseExponent"/> to
        /// <see cref="MinSpecExponent"/>, clamped to at least 1. Roughness 0 returns the inputs unchanged.</summary>
        public static (float strength, float exponent) ApplyRoughness(float baseStrength, float baseExponent, float rough)
        {
            float strength = baseStrength * (1f - rough);
            float exponent = MathF.Max(baseExponent + (MinSpecExponent - baseExponent) * rough, 1f);
            return (strength, exponent);
        }

        static Vector3 SafeNormalize(Vector3 v)
        {
            float len = v.Length();
            return len > 1e-8f ? v / len : v;
        }
    }
}
```

Note: `ApplyRoughness` uses `a + (b-a)*t` rather than a `Lerp` call so it matches the GLSL `mix` exactly.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~SurfaceShadingTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/SurfaceShading.cs KhaozEngine.Render3D/Internal/DefaultMaps.cs KhaozEngine.Tests/Render3D/SurfaceShadingTests.cs
git commit -m "render3d(pbr-lite): SurfaceShading CPU mirror + 1x1 default-map texels"
```

---

### Task 5: Model resource layout + default textures + `CreateMaterialSet` + vertex layout

GPU-side plumbing. There is no headless test for the layout (it needs a device); correctness is validated
by Task 9's gated goldens. The step here is "build stays green". This task and Task 6 (shaders) form a
coherent pair - the layout/shader are only exercised together when a `Scene3D` pipeline is built (gated).

**Files:**
- Modify: `KhaozEngine.Render3D/Rendering/ModelRenderer.cs`

- [ ] **Step 1: Add the two default textures + the 5-binding layout**

In `ModelRenderer`, add fields next to `_white`:

```csharp
        readonly IGpuTexture _flatNormal;       // 1x1 (128,128,255): tangent-space (0,0,1); no-map normal default
        readonly IGpuTexture _defaultRough;     // 1x1 (0,0,0): roughness 0 (fully smooth); no-map spec default
```

Replace the `_layout` creation with the grouped textures-then-sampler layout (mirrors EdgeFrag, the
known-good Metal multi-texture set):

```csharp
            _layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Albedo", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("NormalMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("RoughnessMap", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Sampler", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
```

After the `_white` creation/update block add:

```csharp
            // No-map defaults. Flat normal (0,0,1 in tangent space) and zero roughness reproduce today's
            // geometric-normal lighting and per-instance specular exactly, so untextured meshes are unchanged.
            _flatNormal = factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            gd.UpdateTexture(_flatNormal, DefaultMaps.FlatNormalTexel(), 0, 0, 1, 1);
            _defaultRough = factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            gd.UpdateTexture(_defaultRough, DefaultMaps.ZeroRoughnessTexel(), 0, 0, 1, 1);
```

Add `using KhaozEngine.Render3D.Internal;` at the top if not already present (it is, for `ShaderSources`).

Update `_defaultSet` creation to bind all five resources in layout order:

```csharp
            _defaultSet = factory.CreateResourceSet(new GpuResourceSetDescription(_layout, _ubo, _white, _flatNormal, _defaultRough, _sampler));
```

- [ ] **Step 2: Extend `CreateMaterialSet` and add the tangent vertex element**

Replace `CreateMaterialSet` with an overload that takes optional normal + roughness, defaulting albedo to
white and the maps to the flat/zero defaults:

```csharp
        /// <summary>Build a per-mesh material resource set binding <paramref name="albedo"/> (white default
        /// when null), <paramref name="normal"/> (flat-normal default when null), and
        /// <paramref name="roughness"/> (zero-roughness default when null), plus the shared frame UBO and
        /// sampler. Owned by the caller (Scene3D) and disposed when the mesh unloads. Passing only an albedo
        /// reproduces the pre-PBR single-texture material exactly.</summary>
        public IGpuResourceSet CreateMaterialSet(IGpuTexture? albedo = null, IGpuTexture? normal = null, IGpuTexture? roughness = null) =>
            _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(
                _layout, _ubo, albedo ?? _white, normal ?? _flatNormal, roughness ?? _defaultRough, _sampler));
```

In the slot-0 vertex layout, add the `Tangent` element after `TexCoord`:

```csharp
            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4));
```

Update the slot-1 comment to read "per-instance data (locations 5..11)" since the tangent shifts them.

In `Dispose`, add the two new textures (before or with `_white`):

```csharp
            _white.Dispose(); _flatNormal.Dispose(); _defaultRough.Dispose();
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj -c Debug`
Expected: Build succeeded (the existing `CreateMaterialSet(_textures[...])` calls in Scene3D still bind by
the optional `albedo` parameter).

- [ ] **Step 4: Run the full headless suite (no regression)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (GPU goldens skipped; everything else green).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Rendering/ModelRenderer.cs
git commit -m "render3d(pbr-lite): model set gains Normal+Roughness textures (flat/zero defaults)"
```

---

### Task 6: Shader rewrite (`ModelVert`, `ModelFrag`, `SkinnedModelVert`)

Pure GLSL string edits in `Internal/ShaderSources.cs`. GLSL only cross-compiles at device load, so this is
validated by the gated goldens in Task 9 (build alone does not compile GLSL). Match `SurfaceShading`
exactly.

**Files:**
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.cs`

- [ ] **Step 1: `ModelVert` - add the tangent attribute + output, shift instance locations**

In `ModelVert`: add `layout(location=4) in vec4 Tangent;` after `TexCoord`; renumber the per-instance
inputs to locations 5..11; add `layout(location=8) out vec4 vTangent;` and `vTangent = Tangent;` in main.
The full updated `ModelVert`:

```glsl
#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj;
    vec4 LightDir; vec4 LightColor; vec4 Ambient; vec4 Params;
    vec4 FillDir; vec4 FillColor; vec4 CameraPos;
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
};
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 Tangent;      // model-space tangent (xyz) + handedness (w); zero => no TBN
layout(location=5) in vec4 IModel0;      // per-instance model matrix rows
layout(location=6) in vec4 IModel1;
layout(location=7) in vec4 IModel2;
layout(location=8) in vec4 IModel3;
layout(location=9) in vec4 ITint;
layout(location=10) in vec4 IEmissive;
layout(location=11) in vec4 ISpecParams;
layout(location=0) out vec3 vNormalW;
layout(location=1) out vec4 vColor;
layout(location=2) out float vDepth;
layout(location=3) out vec3 vWorldPos;
layout(location=4) out vec2 vUv;
layout(location=5) out vec4 vTint;
layout(location=6) out vec4 vEmissive;
layout(location=7) out vec4 vSpecParams;
layout(location=8) out vec4 vTangent;
void main() {
    mat4 Model = mat4(IModel0, IModel1, IModel2, IModel3);
    vec4 world = Model * vec4(Position, 1.0);
    gl_Position = ViewProj * world;
    vNormalW = normalize(mat3(Model) * Normal);
    vColor = Color;
    vDepth = gl_Position.z / gl_Position.w;
    vWorldPos = world.xyz;
    vUv = TexCoord;
    vTint = ITint;
    vEmissive = IEmissive;
    vSpecParams = ISpecParams;
    vTangent = vec4(mat3(Model) * Tangent.xyz, Tangent.w); // rotate tangent to world; preserve handedness
}
```

Update the leading comment block above `ModelVert` to say per-instance data is locations 5..11 (tangent at
4 shifted them).

- [ ] **Step 2: `ModelFrag` - new bindings, TBN perturb, roughness-modulated spec, geometric oNormal**

Replace `ModelFrag` with:

```glsl
#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj;
    vec4 LightDir;   // xyz = key light travel direction
    vec4 LightColor;
    vec4 Ambient;
    vec4 Params;     // x = CelBands, y = active point-light count
    vec4 FillDir;    // xyz = fill light travel direction
    vec4 FillColor;
    vec4 CameraPos;  // xyz = eye position
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
};
layout(set=0, binding=1) uniform texture2D Albedo;       // 1x1 white default keeps untextured meshes unchanged
layout(set=0, binding=2) uniform texture2D NormalMap;    // 1x1 flat default (0,0,1); only sampled when a tangent exists
layout(set=0, binding=3) uniform texture2D RoughnessMap; // 1x1 zero default => spec uses per-instance params
layout(set=0, binding=4) uniform sampler Samp;           // shared sampler for all three textures (EdgeFrag-style)
layout(location=0) in vec3 vNormalW;
layout(location=1) in vec4 vColor;
layout(location=2) in float vDepth;
layout(location=3) in vec3 vWorldPos;
layout(location=4) in vec2 vUv;
layout(location=5) in vec4 vTint;
layout(location=6) in vec4 vEmissive;
layout(location=7) in vec4 vSpecParams; // x = specular strength, y = shininess exponent
layout(location=8) in vec4 vTangent;    // world-space tangent (xyz) + handedness (w); zero => geometric normal
layout(location=0) out vec4 oColor;
layout(location=1) out vec4 oNormal;
layout(location=2) out vec4 oDepth;
void main() {
    vec3 Ngeo = normalize(vNormalW);
    // Perturb the lighting normal via a TBN only when a tangent exists. Zero tangent (primitives, skinned,
    // untangented meshes) => geometric normal, bit-identical to the pre-PBR pass. A flat normal sample
    // (0,0,1) also yields Ngeo, so a tangent-bearing mesh with no normal map is unchanged too.
    vec3 N = Ngeo;
    if (dot(vTangent.xyz, vTangent.xyz) > 1e-10) {
        vec3 T = normalize(vTangent.xyz);
        T = normalize(T - Ngeo * dot(Ngeo, T));
        vec3 B = cross(Ngeo, T) * vTangent.w;
        vec3 nTS = texture(sampler2D(NormalMap, Samp), vUv).xyz * 2.0 - 1.0;
        N = normalize(mat3(T, B, Ngeo) * nTS);
    }
    vec3 texRgb = texture(sampler2D(Albedo, Samp), vUv).rgb; // white (1,1,1) for untextured meshes
    vec3 albedo = vColor.rgb * vTint.rgb * texRgb;
    // Roughness modulation (glTF metallic-roughness .g convention; metallic ignored). rough 0 (default)
    // collapses to today's per-instance spec exactly: strength*(1-0)=strength, mix(exp,8,0)=exp.
    float rough = texture(sampler2D(RoughnessMap, Samp), vUv).g;
    float specStrength = vSpecParams.x * (1.0 - rough);
    float specExp = max(mix(vSpecParams.y, 8.0, rough), 1.0);
    float ndlKey  = max(dot(N, -normalize(LightDir.xyz)), 0.0);
    float ndlFill = max(dot(N, -normalize(FillDir.xyz)), 0.0);
    float bands = Params.x;
    if (bands >= 1.0) { ndlKey = floor(ndlKey*bands+0.5)/bands; ndlFill = floor(ndlFill*bands+0.5)/bands; }
    vec3 diffuse = LightColor.rgb*ndlKey + FillColor.rgb*ndlFill;
    vec3 V = normalize(CameraPos.xyz - vWorldPos);
    vec3 H = normalize(-normalize(LightDir.xyz) + V);
    float spec = pow(max(dot(N,H),0.0), specExp) * specStrength * step(0.0001, ndlKey);
    vec3 specColor = LightColor.rgb*spec;
    int npl = int(Params.y);
    for (int i = 0; i < npl; i++) {
        vec3 toL = PointPosRadius[i].xyz - vWorldPos;
        float radius = PointPosRadius[i].w;
        float dist = length(toL);
        vec3 L = (dist > 1e-4) ? toL / dist : vec3(0.0);
        float ndl = max(dot(N, L), 0.0);
        if (bands >= 1.0) ndl = floor(ndl*bands+0.5)/bands;
        float f = clamp(1.0 - (dist*dist)/max(radius*radius, 1e-6), 0.0, 1.0);
        float att = f * f * PointColorIntensity[i].w;
        vec3 lc = PointColorIntensity[i].rgb;
        diffuse += lc * (ndl * att);
        vec3 Hp = normalize(L + V);
        float sp = pow(max(dot(N,Hp),0.0), specExp) * specStrength * step(0.0001, ndl);
        specColor += lc * (sp * att);
    }
    vec3 lit = albedo * (Ambient.rgb + diffuse) + specColor + vEmissive.rgb;
    oColor = vec4(lit, 1.0);
    oNormal = vec4(Ngeo * 0.5 + 0.5, 1.0); // GEOMETRIC normal for the edge pass (not the perturbed one)
    oDepth = vec4(vDepth, vDepth, vDepth, 1.0);
}
```

- [ ] **Step 3: `SkinnedModelVert` - emit a zero tangent for link-compatibility**

In `SkinnedModelVert`, add `layout(location=8) out vec4 vTangent;` to the outputs and `vTangent = vec4(0.0);`
in `main` (skinned meshes carry no tangents this release; the zero tangent makes ModelFrag fall back to the
geometric normal). Update the comment block to note this keeps the shared `ModelFrag` link-compatible.

- [ ] **Step 4: Build to verify it compiles**

Run: `dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj -c Debug`
Expected: Build succeeded (GLSL is compiled at device load, not here; the gated goldens in Task 9 validate
the cross-compile).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Internal/ShaderSources.cs
git commit -m "render3d(pbr-lite): TBN normal perturb + roughness-modulated spec in ModelFrag"
```

---

### Task 7: `Scene3D.SurfaceMaps` + `LoadMesh(GltfMesh, SurfaceMaps)`

**Files:**
- Modify: `KhaozEngine.Render3D/Scene3D.cs` (near the `TextureHandle` struct + the `LoadMesh` overloads)
- Test: `KhaozEngine.Tests/Render3D/SurfaceMapsTests.cs` (create)

The full `LoadMesh(GltfMesh, SurfaceMaps)` path needs a GPU device (validated by Task 9's golden). The
headless test covers the `SurfaceMaps` value type: handle defaulting and validity.

- [ ] **Step 1: Write the failing test**

```csharp
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SurfaceMapsTests
    {
        [Fact]
        public void Albedo_only_leaves_normal_and_roughness_invalid()
        {
            var maps = new Scene3D.SurfaceMaps(new Scene3D.TextureHandle());
            Assert.False(maps.Albedo.IsValid);   // default handle is invalid
            Assert.False(maps.Normal.IsValid);
            Assert.False(maps.Roughness.IsValid);
        }

        [Fact]
        public void Default_struct_has_all_invalid_handles()
        {
            var maps = default(Scene3D.SurfaceMaps);
            Assert.False(maps.Albedo.IsValid);
            Assert.False(maps.Normal.IsValid);
            Assert.False(maps.Roughness.IsValid);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~SurfaceMapsTests`
Expected: FAIL to compile (`Scene3D.SurfaceMaps` does not exist).

- [ ] **Step 3: Implement**

In `Scene3D.cs`, add the `SurfaceMaps` struct just after the `TextureHandle` struct:

```csharp
        /// <summary>A bundle of optional surface maps for <see cref="LoadMesh(GltfMesh,SurfaceMaps)"/>:
        /// albedo, tangent-space normal, and roughness (glTF metallic-roughness .g convention). Any invalid
        /// (<c>default</c>) handle falls back to the renderer's default for that slot (white albedo, flat
        /// normal, zero roughness), so binding only some maps is fine. Load each map with
        /// <see cref="LoadTexture(string)"/> / <see cref="LoadTexture(byte[],int,int)"/>.</summary>
        public readonly struct SurfaceMaps
        {
            public readonly TextureHandle Albedo;
            public readonly TextureHandle Normal;
            public readonly TextureHandle Roughness;
            public SurfaceMaps(TextureHandle albedo, TextureHandle normal = default, TextureHandle roughness = default)
            {
                Albedo = albedo; Normal = normal; Roughness = roughness;
            }
        }
```

Add the `LoadMesh` overload next to the existing `LoadMesh(GltfMesh, TextureHandle)`:

```csharp
        /// <summary>Upload a mesh and bind a full PBR-lite material (<paramref name="maps"/>): albedo + optional
        /// normal + optional roughness. Invalid handles fall back to the renderer defaults. Normal perturbation
        /// requires the mesh to carry tangents (glTF meshes via <see cref="GltfLoader"/>, or
        /// <see cref="MeshAssembler"/> output); primitives have none and are lit by their geometric normal.</summary>
        public MeshHandle LoadMesh(GltfMesh mesh, SurfaceMaps maps)
        {
            IGpuTexture? a = maps.Albedo.IsValid ? _textures[maps.Albedo.ListIndex] : null;
            IGpuTexture? n = maps.Normal.IsValid ? _textures[maps.Normal.ListIndex] : null;
            IGpuTexture? r = maps.Roughness.IsValid ? _textures[maps.Roughness.ListIndex] : null;
            IGpuResourceSet? material = (a != null || n != null || r != null)
                ? _model.CreateMaterialSet(a, n, r)
                : null;
            return LoadMeshInternal(mesh, material);
        }
```

- [ ] **Step 4: Run tests + full suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~SurfaceMapsTests`
Expected: PASS (2 tests).
Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (whole suite, GPU goldens skipped).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Scene3D.cs KhaozEngine.Tests/Render3D/SurfaceMapsTests.cs
git commit -m "render3d(pbr-lite): Scene3D.SurfaceMaps + LoadMesh(mesh, maps) overload"
```

---

### Task 8: `PixelPostProcessSettings.UseSmoothPreset()`

**Files:**
- Modify: `KhaozEngine.Render3D/PixelPostProcessSettings.cs`
- Test: `KhaozEngine.Tests/Render3D/SmoothPresetTests.cs` (create)

- [ ] **Step 1: Write the failing test**

```csharp
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SmoothPresetTests
    {
        [Fact]
        public void Smooth_preset_turns_off_the_stylized_passes()
        {
            var s = new PixelPostProcessSettings
            {
                CelBands = 4, Quantize = true, Dither = true, Outline = true, Starfield = true,
            };
            s.UseSmoothPreset();
            Assert.Equal(0, s.CelBands);
            Assert.False(s.Quantize);
            Assert.False(s.Dither);
            Assert.False(s.Outline);
            Assert.False(s.Starfield);
        }

        [Fact]
        public void Smooth_preset_leaves_lighting_untouched()
        {
            var s = new PixelPostProcessSettings();
            var key = s.LightColor;
            var ambient = s.AmbientColor;
            s.UseSmoothPreset();
            Assert.Equal(key, s.LightColor);
            Assert.Equal(ambient, s.AmbientColor);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~SmoothPresetTests`
Expected: FAIL to compile (`UseSmoothPreset` does not exist).

- [ ] **Step 3: Implement**

Add to `PixelPostProcessSettings` (after the fields):

```csharp
        /// <summary>Dial the stylized post chain down for a smooth/realistic look: cel bands off, palette
        /// quantize + dither off, edge outline off, starfield off. Lighting, colours, and render scaling are
        /// left untouched. Pair with normal/roughness maps (PBR-lite) for a semi-realistic material - the post
        /// chain otherwise still quantizes/outlines a realistic surface.</summary>
        public void UseSmoothPreset()
        {
            CelBands = 0;
            Quantize = false;
            Dither = false;
            Outline = false;
            Starfield = false;
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~SmoothPresetTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/PixelPostProcessSettings.cs KhaozEngine.Tests/Render3D/SmoothPresetTests.cs
git commit -m "render3d(pbr-lite): PixelPostProcessSettings.UseSmoothPreset()"
```

---

### Task 9: New GPU golden scene + bake (Metal local, D3D11 + Vulkan via CI)

**Files:**
- Modify: `KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs` (add a new `[GpuFact]`)
- Create (baked): `KhaozEngine.Tests/Gpu/goldens/scene3d_normalmap.metal.txt`,
  `scene3d_normalmap.direct3d11.txt`, `scene3d_normalmap.vulkan.txt`

- [ ] **Step 1: Add the new golden test**

Add this method to `GoldenSnapshotTests` (it builds a tangent-bearing quad via `MeshAssembler`, binds a
normal map that tilts along +U and a roughness gradient 0->1 along +U, and uses the new Smooth preset so
the perturbation + spec falloff read cleanly):

```csharp
        [GpuFact]
        public void Golden3D_NormalRoughness()
        {
            const int TexN = 64;
            // Normal map: tangent-space normal tilts toward +x as u increases (a smooth diffuse gradient under
            // the fixed key light). Roughness map: 0 at u=0 (smooth, full spec) -> 1 at u=1 (matte).
            var normalPx = new byte[TexN * TexN * 4];
            var roughPx = new byte[TexN * TexN * 4];
            for (int y = 0; y < TexN; y++)
                for (int x = 0; x < TexN; x++)
                {
                    float u = (x + 0.5f) / TexN;
                    float tiltX = (u - 0.5f) * 1.4f;                 // -0.7 .. +0.7
                    float nz = MathF.Sqrt(MathF.Max(0f, 1f - tiltX * tiltX));
                    int i = (y * TexN + x) * 4;
                    normalPx[i + 0] = (byte)(System.Math.Clamp((tiltX * 0.5f + 0.5f) * 255f, 0f, 255f));
                    normalPx[i + 1] = 128;                            // no tilt along bitangent
                    normalPx[i + 2] = (byte)(System.Math.Clamp((nz * 0.5f + 0.5f) * 255f, 0f, 255f));
                    normalPx[i + 3] = 255;
                    byte rough = (byte)System.Math.Clamp(u * 255f, 0f, 255f);
                    roughPx[i + 0] = rough; roughPx[i + 1] = rough; roughPx[i + 2] = rough; roughPx[i + 3] = 255;
                }

            MeshHandle quad = default;

            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    // A flat XZ quad (normal +Y supplied), UVs mapping +X->+U, +Z->+V, built via MeshAssembler
                    // so it carries a real tangent (along +X). Spans [-1.5,1.5] in X and Z.
                    Vector3 A = new(-1.5f, 0, -1.5f), B = new(1.5f, 0, -1.5f), C = new(1.5f, 0, 1.5f), D = new(-1.5f, 0, 1.5f);
                    Vector3 up = Vector3.UnitY;
                    var corners = new System.Collections.Generic.List<MeshCorner>
                    {
                        new(A, up, Vector4.One, new Vector2(0, 0)), new(B, up, Vector4.One, new Vector2(1, 0)), new(C, up, Vector4.One, new Vector2(1, 1)),
                        new(A, up, Vector4.One, new Vector2(0, 0)), new(C, up, Vector4.One, new Vector2(1, 1)), new(D, up, Vector4.One, new Vector2(0, 1)),
                    };
                    GltfMesh mesh = MeshAssembler.Build(corners);

                    Scene3D.TextureHandle nrm = scene.LoadTexture(normalPx, TexN, TexN);
                    Scene3D.TextureHandle rgh = scene.LoadTexture(roughPx, TexN, TexN);
                    quad = scene.LoadMesh(mesh, new Scene3D.SurfaceMaps(default, nrm, rgh));

                    scene.Post.UseSmoothPreset();   // smooth look so the normal/roughness gradient reads cleanly
                    scene.Camera.Frame(Vector3.Zero, new Vector3(2.4f, 3.2f, 2.4f));
                },
                drawFrame: scene =>
                {
                    // Light grey, shiny: the spec highlight is visible on the smooth (low-u) side and fades to
                    // matte on the rough (high-u) side.
                    scene.Draw(quad, Matrix4x4.Identity, new Color(0.75f, 0.76f, 0.8f, 1f), Material.Shiny(0.9f, 48f));
                },
                frames: 2);

            GoldenCompare.AssertOrUpdate("scene3d_normalmap", rgba, W, H);
        }
```

Ensure the test file's `using`s include `System` (for `MathF`), `KhaozEngine.Render3D` (already present;
`MeshCorner`/`MeshAssembler` are internal and visible via `InternalsVisibleTo("KhaozEngine.Tests")`).

- [ ] **Step 2: Build the test project**

Run: `dotnet build KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release`
Expected: Build succeeded.

- [ ] **Step 3: Verify the EXISTING goldens still pass on Metal (no re-bake) - the regression proof**

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter "FullyQualifiedName~Golden"`
Expected: the existing `scene3d`, `scene3d_textured`, `scene3d_fill`, `scene3d_texbillboard`, and all 2D
goldens PASS unchanged (the new `scene3d_normalmap` FAILS with "golden missing" - that is expected, it has
not been baked yet). If any EXISTING golden regresses, STOP: the no-map path is not bit-identical - debug
before baking anything.

- [ ] **Step 4: Bake the Metal golden for the new scene only**

Run: `KE_GPU_TESTS=1 KE_UPDATE_GOLDENS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj -c Release --filter "FullyQualifiedName~Golden3D_NormalRoughness"`
Expected: PASS (writes `KhaozEngine.Tests/Gpu/goldens/scene3d_normalmap.metal.txt` only). Confirm exactly
one new file:

Run: `git status --porcelain KhaozEngine.Tests/Gpu/goldens/`
Expected: only `scene3d_normalmap.metal.txt` is new/modified (no existing goldens touched).

- [ ] **Step 5: Bake D3D11 + Vulkan goldens via the CI workflow, then commit all three**

The D3D11 (WARP) and Vulkan (lavapipe) goldens MUST be baked on their runner environments so the verify
legs match. Push the branch, dispatch the bake workflow, download the artifacts, drop in the two files:

```bash
git add KhaozEngine.Tests/Gpu/GoldenSnapshotTests.cs KhaozEngine.Tests/Gpu/goldens/scene3d_normalmap.metal.txt
git commit -m "render3d(pbr-lite): normal/roughness GPU golden scene + Metal bake"
git push -u origin worktree-feature+pbr-lite-materials
gh workflow run cross-platform-gpu.yml --ref worktree-feature+pbr-lite-materials -f bake=true
# wait for the run, then:
gh run list --workflow=cross-platform-gpu.yml --branch worktree-feature+pbr-lite-materials --limit 1
gh run download <run-id> -n goldens-direct3d11 -D /tmp/ke-goldens-d3d11
gh run download <run-id> -n goldens-vulkan -D /tmp/ke-goldens-vulkan
cp /tmp/ke-goldens-d3d11/scene3d_normalmap.direct3d11.txt KhaozEngine.Tests/Gpu/goldens/
cp /tmp/ke-goldens-vulkan/scene3d_normalmap.vulkan.txt KhaozEngine.Tests/Gpu/goldens/
git add KhaozEngine.Tests/Gpu/goldens/scene3d_normalmap.direct3d11.txt KhaozEngine.Tests/Gpu/goldens/scene3d_normalmap.vulkan.txt
git commit -m "test(gpu): bake scene3d_normalmap D3D11 + Vulkan goldens"
```

Note: triggering `cross-platform-gpu` spins up 3 runners (CI cost). This is the user's own repo so no
external approval is needed, but PAUSE here and confirm with the user whether to drive this via `gh` or
let them run the workflow + drop the files. Also confirm the cross-backend tolerance test passes after
committing all three:

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter FullyQualifiedName~CrossBackendGoldenTests`
Expected: PASS (the three `scene3d_normalmap` goldens agree within the generous cross-backend tolerance).

---

### Task 10: Docs + release ritual (single version bump for the batch)

**Files:**
- Modify: `Directory.Build.props` (version line)
- Modify: `CHANGELOG.md`, `CHANGENOTES.md`
- Modify: `docs/CONSUMERS.md` (engine current version), `docs/ROADMAP.md` (current released version),
  `README.md` (`<PackageReference>` example)
- Modify: `docs/USING-KHAOZENGINE.md` (normal/roughness maps + Smooth preset note)

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<KhaozEngineVersion>7.22.0</KhaozEngineVersion>` to
`<KhaozEngineVersion>7.23.0</KhaozEngineVersion>`.

- [ ] **Step 2: CHANGELOG.md entry (newest-first, detailed)**

Add at the top of the entries:

```markdown
## 7.23.0

- Render3D PBR-lite materials: the rigid lit model pass now supports an optional tangent-space NORMAL map
  and a ROUGHNESS map alongside the albedo. `ModelVertex` carries a tangent (computed in `MeshAssembler`
  from UV+position, or read from the glTF `TANGENT` accessor); `GltfMesh`/primitives without one fall back
  to the geometric normal, so untextured meshes render bit-identical to 7.22.0. Bind maps explicitly with
  the new `Scene3D.SurfaceMaps` (albedo/normal/roughness `TextureHandle`s) via
  `LoadMesh(GltfMesh, SurfaceMaps)`. Roughness (glTF metallic-roughness `.g`; metallic ignored) lowers
  specular strength and broadens the highlight; roughness 0 reproduces the previous Blinn-Phong specular
  exactly. Normal maps perturb only the LIT normal - the MRT normal target (edge-outline post) keeps the
  geometric normal. Skinned meshes stay albedo-only this release.
- New `PixelPostProcessSettings.UseSmoothPreset()`: cel bands / palette / dither / outline / starfield off,
  for a smooth/realistic look to pair with PBR-lite materials. A realistic surface is otherwise still
  quantized/outlined by the post chain.
- New pure helper `SurfaceShading` (CPU mirror of the model fragment shader's TBN perturb + roughness
  math, headless-tested). New gated GPU golden `scene3d_normalmap` (Metal + D3D11 + Vulkan).
- glTF material normal/metallic-roughness texture auto-read is NOT included (bind explicitly); noted as a
  future follow-up.
```

- [ ] **Step 3: CHANGENOTES.md entry (newest-first, one or two sentences)**

Add at the top:

```markdown
- 7.23.0: PBR-lite materials on the lit model pass - optional tangent-space normal + roughness maps
  (bound explicitly via Scene3D.SurfaceMaps), plus a UseSmoothPreset() for a realistic look. Untextured
  meshes render bit-identical to 7.22.0.
```

- [ ] **Step 4: Update the three guarded version declarations + the USING doc**

Update each to `7.23.0`:
- `docs/CONSUMERS.md`: the "Engine current version" line.
- `docs/ROADMAP.md`: the "Current released version" line.
- `README.md`: the `<PackageReference ... Version="..." />` example.

Add a short section to `docs/USING-KHAOZENGINE.md` documenting: how to bind normal/roughness maps via
`Scene3D.SurfaceMaps` + `LoadMesh(mesh, maps)`; that normal mapping needs tangents (glTF/`MeshAssembler`
meshes, not primitives); that roughness uses the glTF `.g` convention; and that
`PixelPostProcessSettings.UseSmoothPreset()` dials the post chain down for a realistic look (palette/cel/
outline are FULLSCREEN passes that otherwise stylize any material).

- [ ] **Step 5: Verify the doc-version guard + full headless suite**

Run: `bash scripts/check-doc-versions.sh`
Expected: passes (the three declarations match `<KhaozEngineVersion>` = 7.23.0).
Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (whole headless suite).

- [ ] **Step 6: Pack to local-feed**

Run: `mkdir -p local-feed && dotnet pack -c Release -o ./local-feed`
Expected: all packable projects pack at 7.23.0 into `./local-feed`.

- [ ] **Step 7: Commit the release**

```bash
git add Directory.Build.props CHANGELOG.md CHANGENOTES.md docs/CONSUMERS.md docs/ROADMAP.md README.md docs/USING-KHAOZENGINE.md
git commit -m "render3d(7.23.0): PBR-lite normal + roughness maps, Smooth post preset"
```

- [ ] **Step 8: Merge, tag, push (per the engine release ritual)**

This is the engine's full-publish finish. Confirm with the user before pushing (CI publishes on `v*`).
Then merge to `main`, tag, and push `main` + tag:

```bash
# from the worktree, fast-forward main onto this branch's tip (or merge), then:
git tag v7.23.0
# push main + tag (engine ritual: CI publishes to GitHub Packages on v*)
```

The exact merge/cleanup follows the global finishing-a-development-branch defaults (merge to `main`,
remove the worktree, delete the merged local + remote branch, `git fetch --prune`).

- [ ] **Step 9: Manual Metal validation handoff**

Hand the user a one-click windowed boot command (from THIS worktree path) to eyeball a normal/roughness
mesh on a real Metal device - the cross-backend confidence check the unit tests cannot give. Provide a
`Render3DSnapshot`-style scene or the relevant consumer demo. (The goldens + CPU mirror are proxies; the
device run is the real check for the new multi-texture binding.)

---

## Self-review notes

- **Spec coverage:** tangent on ModelVertex (T1) + compute/read (T2,T3); optional Normal+Roughness bindings
  with flat/zero defaults (T5); ModelFrag TBN + roughness, geometric oNormal, cel-band gate preserved (T6);
  glTF explicit binding (T7) + TANGENT read (T3); Smooth preset (T8); headless direction + byte-identical
  tests (T1,T2,T4) + GPU golden + unchanged existing goldens (T9); Metal-risk mitigation = EdgeFrag layout
  (T5) + golden tripwire + manual run (T9, T10.9); release ritual (T10). All spec sections map to a task.
- **No-regression invariants:** zero-tangent meshes skip the TBN (T6 branch) and `rough=0` collapses spec
  to today's exact expressions; proven by existing goldens passing without re-bake (T9.3).
- **Type consistency:** `ModelVertex(...,Vector4 tangent)`, `MeshCorner(...,Vector4? tangent=null)`,
  `MeshAssembler.ResolveTangent`, `SurfaceShading.{DecodeNormalSample,PerturbNormal,ApplyRoughness,
  MinSpecExponent}`, `DefaultMaps.{FlatNormalTexel,ZeroRoughnessTexel}`, `ModelRenderer.CreateMaterialSet(
  IGpuTexture? albedo=null, normal=null, roughness=null)`, `Scene3D.SurfaceMaps`,
  `Scene3D.LoadMesh(GltfMesh, SurfaceMaps)`, `PixelPostProcessSettings.UseSmoothPreset()` are used
  consistently across tasks. Shader binding indices 1=Albedo, 2=NormalMap, 3=RoughnessMap, 4=Sampler match
  the C# layout element order in T5.
```
