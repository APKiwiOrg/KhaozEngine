# Runtime Skinned / Deformable Mesh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add GPU bone-palette skinning to KhaozEngine.Render3D so a smooth mesh can bend/deform at runtime under pure code control (tentacles, limbs, cables, soft-body), replacing many rigid-segment draws with one skinned draw.

**Architecture:** A parallel skinned path alongside the existing rigid path. New `SkinnedVertex` (adds bone indices + weights) feeds a new `SkinnedModelRenderer` with its own pipeline + vertex shader (the lit fragment shader is reused unchanged, so the color path is identical). Per-frame composed bone matrices for every skinned draw live in one growable read-only structured buffer (SSBO, `set 1`); each draw indexes its range via a per-instance bone-offset attribute, so multiple skinned draws of the same mesh collapse into one instanced draw. Authored glb rigs (JOINTS_0/WEIGHTS_0 + inverse-bind) and a procedural tube/chain builder both produce the same `SkinnedGltfMesh`.

**Tech Stack:** C# net10.0, KhaozEngine.Gpu (Veldrid seam), SharpGLTF (glTF load), GLSL 450 → SPIR-V, xUnit (headless tests).

**Spec:** `docs/superpowers/specs/2026-06-21-skinned-mesh-design.md`

**Key invariant (carried by tests):** passing a mesh's rest pose as the per-frame bone matrices yields the identity skinning transform, so the mesh does not move. Skinning rewrites position + normal only; `albedo = vColor * vTint * texRgb` is untouched.

---

## Conventions for every task

- Work in this worktree: `/Users/antonio/KhaozEngine/.claude/worktrees/feature+skinned-mesh`.
- Tests: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`. To run one class: append `--filter "FullyQualifiedName~<ClassName>"`.
- Commit subjects use conventional style `area(scope): summary`. Pre-release tasks use `render3d(skinned): ...`; the final release task uses the version as scope `render3d(7.10.0): ...`. No em-dashes anywhere.
- `local-feed/` must exist before any restore (`mkdir -p local-feed`); it already does in this repo.

---

## Task 1: Skinned vertex, mesh, and handle types

**Files:**
- Create: `KhaozEngine.Render3D/Models/SkinnedMesh.cs`
- Create: `KhaozEngine.Render3D/SkinnedMeshHandle.cs`
- Test: `KhaozEngine.Tests/Render3D/SkinnedMeshTypesTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SkinnedMeshTypesTests
    {
        [Fact]
        public void SkinnedVertex_LayoutIs80Bytes()
        {
            Assert.Equal(80u, SkinnedVertex.SizeInBytes);
            Assert.Equal(80, System.Runtime.InteropServices.Marshal.SizeOf<SkinnedVertex>());
        }

        [Fact]
        public void SkinnedGltfMesh_HoldsVertsIndicesBonesAndRestPose()
        {
            var v = new SkinnedVertex
            {
                Position = new Vector3(1, 2, 3), Normal = Vector3.UnitY,
                Color = new Vector4(1, 1, 1, 1), Uv = Vector2.Zero,
                BoneIndices = new Vector4(0, 1, 0, 0), BoneWeights = new Vector4(0.5f, 0.5f, 0, 0),
            };
            var mesh = new SkinnedGltfMesh(
                new[] { v }, new ushort[] { 0 },
                new[] { Matrix4x4.Identity, Matrix4x4.Identity },
                new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, 1, 0) });

            Assert.Single(mesh.Vertices);
            Assert.Equal(2, mesh.BoneCount);
            Assert.Equal(2, mesh.InverseBind.Length);
            Assert.Equal(2, mesh.RestPose.Length);
        }

        [Fact]
        public void SkinnedMeshHandle_DefaultIsGenerationZero()
        {
            Assert.Equal(0, default(SkinnedMeshHandle).Generation);
            Assert.Equal(1, new SkinnedMeshHandle(3).Generation);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SkinnedMeshTypesTests"`
Expected: FAIL with compile error "SkinnedVertex could not be found".

- [ ] **Step 3: Write the types**

`KhaozEngine.Render3D/Models/SkinnedMesh.cs`:

```csharp
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D
{
    /// <summary>Interleaved skinned vertex: position, normal, base color (RGBA), UV, then 4 bone indices
    /// (float-encoded, portable across GL/Metal/Vulkan) and 4 bone weights (normalized at load). 80 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SkinnedVertex
    {
        public Vector3 Position;     // 0
        public Vector3 Normal;       // 12
        public Vector4 Color;        // 24
        public Vector2 Uv;           // 40
        public Vector4 BoneIndices;  // 48 (up to 4 bone indices, float-encoded)
        public Vector4 BoneWeights;  // 64 (sum to 1; all-zero falls back to identity in the shader)
        public const uint SizeInBytes = 80; // 12 + 12 + 16 + 8 + 16 + 16
    }

    /// <summary>CPU-side skinned mesh: skinned vertices + indices + the skin's per-bone inverse-bind matrices and
    /// the rest-pose joint world transforms. Produced by <see cref="GltfLoader.LoadSkinned"/> or
    /// <see cref="SkinnedMeshBuilder"/>. GPU buffers are created internally by the renderer.</summary>
    public sealed class SkinnedGltfMesh
    {
        public SkinnedVertex[] Vertices { get; }
        public ushort[] Indices { get; }
        /// <summary>One inverse-bind matrix per bone: maps a model-space vertex into bone-local space at rest.</summary>
        public Matrix4x4[] InverseBind { get; }
        /// <summary>One rest (bind-pose) joint world transform per bone. Passing these to
        /// <see cref="Scene3D.DrawSkinned"/> yields the identity deform (the mesh does not move).</summary>
        public Matrix4x4[] RestPose { get; }
        public int BoneCount => InverseBind.Length;
        public int TriangleCount => Indices.Length / 3;

        public SkinnedGltfMesh(SkinnedVertex[] vertices, ushort[] indices, Matrix4x4[] inverseBind, Matrix4x4[] restPose)
        {
            Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
            Indices = indices ?? throw new ArgumentNullException(nameof(indices));
            InverseBind = inverseBind ?? throw new ArgumentNullException(nameof(inverseBind));
            RestPose = restPose ?? throw new ArgumentNullException(nameof(restPose));
            if (RestPose.Length != InverseBind.Length)
                throw new ArgumentException("RestPose and InverseBind must have one entry per bone.");
        }
    }
}
```

`KhaozEngine.Render3D/SkinnedMeshHandle.cs`:

```csharp
namespace KhaozEngine.Render3D
{
    /// <summary>A handle to a skinned mesh uploaded via <see cref="Scene3D.LoadSkinnedMesh"/>. Mirrors
    /// <see cref="MeshHandle"/>: a slot <see cref="Index"/> plus a <see cref="Generation"/> so a handle held
    /// after <see cref="Scene3D.UnloadSkinnedMesh"/> is detectably invalid. A <c>default</c> handle has
    /// <see cref="Generation"/> 0 and is never valid; live handles start at generation 1.</summary>
    public readonly struct SkinnedMeshHandle
    {
        public int Index { get; }
        public int Generation { get; }
        public SkinnedMeshHandle(int index, int generation) { Index = index; Generation = generation; }
        public SkinnedMeshHandle(int index) : this(index, 1) { }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SkinnedMeshTypesTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Models/SkinnedMesh.cs KhaozEngine.Render3D/SkinnedMeshHandle.cs KhaozEngine.Tests/Render3D/SkinnedMeshTypesTests.cs
git commit -m "render3d(skinned): SkinnedVertex, SkinnedGltfMesh, SkinnedMeshHandle types"
```

---

## Task 2: Pure skinning math (compose, blend, normalize)

This is the headless-testable core that carries the identity invariant.

**Files:**
- Create: `KhaozEngine.Render3D/Models/SkinningMath.cs`
- Test: `KhaozEngine.Tests/Render3D/SkinningMathTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SkinningMathTests
    {
        static Vector3 Apply(Matrix4x4 m, Vector3 p) => Vector3.Transform(p, m);

        [Fact]
        public void Compose_RestPoseTimesInverseBind_IsIdentity()
        {
            // A bone sitting at (0,1,0): inverseBind is the inverse of its rest world transform.
            var restWorld = Matrix4x4.CreateTranslation(0, 1, 0);
            Matrix4x4.Invert(restWorld, out var inverseBind);
            var skin = SkinningMath.Compose(restWorld, inverseBind);
            // Identity: a point is unmoved.
            var p = new Vector3(2, 3, 4);
            Assert.True(Vector3.Distance(Apply(skin, p), p) < 1e-4f);
        }

        [Fact]
        public void Blend_SingleBoneFullWeight_MatchesThatBonesSkinMatrix()
        {
            var skin0 = Matrix4x4.CreateTranslation(5, 0, 0);
            var skin1 = Matrix4x4.CreateTranslation(0, 9, 0);
            var bones = new[] { skin0, skin1 };
            var blended = SkinningMath.BlendSkinMatrix(bones,
                indices: new Vector4(1, 0, 0, 0), weights: new Vector4(1, 0, 0, 0));
            var p = Vector3.Zero;
            Assert.True(Vector3.Distance(Apply(blended, p), new Vector3(0, 9, 0)) < 1e-4f);
        }

        [Fact]
        public void Blend_TwoBoneHalfHalf_IsAveragedTransform()
        {
            var skin0 = Matrix4x4.CreateTranslation(4, 0, 0);
            var skin1 = Matrix4x4.CreateTranslation(0, 8, 0);
            var bones = new[] { skin0, skin1 };
            var blended = SkinningMath.BlendSkinMatrix(bones,
                indices: new Vector4(0, 1, 0, 0), weights: new Vector4(0.5f, 0.5f, 0, 0));
            Assert.True(Vector3.Distance(Apply(blended, Vector3.Zero), new Vector3(2, 4, 0)) < 1e-4f);
        }

        [Fact]
        public void Blend_ZeroTotalWeight_IsIdentity()
        {
            var bones = new[] { Matrix4x4.CreateTranslation(5, 5, 5) };
            var blended = SkinningMath.BlendSkinMatrix(bones,
                indices: new Vector4(0, 0, 0, 0), weights: Vector4.Zero);
            Assert.True(Vector3.Distance(Apply(blended, new Vector3(1, 1, 1)), new Vector3(1, 1, 1)) < 1e-4f);
        }

        [Fact]
        public void NormalizeWeights_SumsToOne_OrZeroStaysZero()
        {
            var n = SkinningMath.NormalizeWeights(new Vector4(1, 1, 2, 0));
            Assert.Equal(1f, n.X + n.Y + n.Z + n.W, 4);
            Assert.Equal(Vector4.Zero, SkinningMath.NormalizeWeights(Vector4.Zero));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SkinningMathTests"`
Expected: FAIL with "SkinningMath could not be found".

- [ ] **Step 3: Write the implementation**

`KhaozEngine.Render3D/Models/SkinningMath.cs`:

```csharp
using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Pure, GPU-free skinning math: composing a per-bone skin matrix from a joint world transform and
    /// the bone's inverse-bind, blending the 4-bone palette by weights, and normalizing weights. This is the
    /// CPU mirror of the skinned vertex shader's blend, so the deform is headless-unit-testable. Presentation
    /// only: never feed skinning results into simulation/RNG/netcode.</summary>
    public static class SkinningMath
    {
        /// <summary>The model-space skinning matrix for one bone: <c>jointWorld * inverseBind</c>. When
        /// <paramref name="jointWorld"/> equals the bone's rest world transform this is the identity (no deform).</summary>
        public static Matrix4x4 Compose(Matrix4x4 jointWorld, Matrix4x4 inverseBind) => inverseBind * jointWorld;

        /// <summary>Blend up to 4 composed bone matrices by <paramref name="weights"/>, indexing
        /// <paramref name="composedBones"/> with the (float-encoded) <paramref name="indices"/>. A zero total
        /// weight returns the identity so an unrigged vertex is left in place.</summary>
        public static Matrix4x4 BlendSkinMatrix(ReadOnlySpan<Matrix4x4> composedBones, Vector4 indices, Vector4 weights)
        {
            float total = weights.X + weights.Y + weights.Z + weights.W;
            if (total < 1e-8f) return Matrix4x4.Identity;
            Matrix4x4 m = default;
            m += Scale(composedBones[(int)indices.X], weights.X);
            m += Scale(composedBones[(int)indices.Y], weights.Y);
            m += Scale(composedBones[(int)indices.Z], weights.Z);
            m += Scale(composedBones[(int)indices.W], weights.W);
            return m;
        }

        /// <summary>Normalize a 4-weight vector to sum to 1; an all-zero input stays zero (identity fallback).</summary>
        public static Vector4 NormalizeWeights(Vector4 w)
        {
            float total = w.X + w.Y + w.Z + w.W;
            return total < 1e-8f ? Vector4.Zero : w / total;
        }

        static Matrix4x4 Scale(Matrix4x4 m, float s) => new(
            m.M11 * s, m.M12 * s, m.M13 * s, m.M14 * s,
            m.M21 * s, m.M22 * s, m.M23 * s, m.M24 * s,
            m.M31 * s, m.M32 * s, m.M33 * s, m.M34 * s,
            m.M41 * s, m.M42 * s, m.M43 * s, m.M44 * s);
    }
}
```

Note: System.Numerics row-vector convention is `Vector3.Transform(p, A*B) == Transform(Transform(p, A), B)`, so `inverseBind * jointWorld` applies inverse-bind first then the joint transform, which is correct for `Compose`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SkinningMathTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Models/SkinningMath.cs KhaozEngine.Tests/Render3D/SkinningMathTests.cs
git commit -m "render3d(skinned): pure SkinningMath compose/blend/normalize with identity invariant"
```

---

## Task 3: Procedural tube/chain builder

`SkinnedMeshBuilder.BuildTube` generates a segmented tube weighted to a bone chain. The identity-pose test reuses `SkinningMath` to prove the rest pose leaves the tube unmoved.

**Files:**
- Create: `KhaozEngine.Render3D/Models/SkinnedMeshBuilder.cs`
- Test: `KhaozEngine.Tests/Render3D/SkinnedMeshBuilderTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SkinnedMeshBuilderTests
    {
        // CPU mirror of the shader: skin each vertex by the rest/posed bones and return its deformed position.
        static Vector3 Skin(SkinnedVertex v, Matrix4x4[] joints, Matrix4x4[] inverseBind)
        {
            Span<Matrix4x4> composed = stackalloc Matrix4x4[joints.Length];
            for (int i = 0; i < joints.Length; i++) composed[i] = SkinningMath.Compose(joints[i], inverseBind[i]);
            var skin = SkinningMath.BlendSkinMatrix(composed, v.BoneIndices, v.BoneWeights);
            return Vector3.Transform(v.Position, skin);
        }

        [Fact]
        public void BuildTube_HasRequestedBonesAndNormalizedWeights()
        {
            var tube = SkinnedMeshBuilder.BuildTube(radius: 0.5f, length: 4f, ringSegments: 4, radialSegments: 6, boneCount: 5);
            Assert.Equal(5, tube.BoneCount);
            Assert.Equal(5, tube.RestPose.Length);
            Assert.True(tube.Vertices.Length > 0);
            foreach (var v in tube.Vertices)
            {
                float sum = v.BoneWeights.X + v.BoneWeights.Y + v.BoneWeights.Z + v.BoneWeights.W;
                Assert.True(MathF.Abs(sum - 1f) < 1e-3f, $"weights must sum to 1, got {sum}");
            }
        }

        [Fact]
        public void BuildTube_RestPose_LeavesGeometryUnmoved()
        {
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 4, 6, 5);
            foreach (var v in tube.Vertices)
            {
                var moved = Skin(v, tube.RestPose, tube.InverseBind);
                Assert.True(Vector3.Distance(moved, v.Position) < 1e-3f,
                    $"rest pose must not deform: {v.Position} -> {moved}");
            }
        }

        [Fact]
        public void BuildTube_BendingTipBone_CurvesTheFarEnd()
        {
            // Tube along +Z. Rotate only the last bone; the tip vertices should swing in X, the base must not move.
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 8, 6, 6, Axis.Z);
            var posed = (Matrix4x4[])tube.RestPose.Clone();
            int last = tube.BoneCount - 1;
            // Rotate the last bone 90 deg about Y around its own rest origin.
            var origin = tube.RestPose[last].Translation;
            posed[last] = Matrix4x4.CreateTranslation(-origin)
                          * Matrix4x4.CreateRotationY(MathF.PI / 2f)
                          * Matrix4x4.CreateTranslation(origin)
                          * tube.RestPose[last];

            float maxBaseShift = 0f, maxTipShift = 0f;
            foreach (var v in tube.Vertices)
            {
                float shift = Vector3.Distance(Skin(v, posed, tube.InverseBind), v.Position);
                if (v.Position.Z < 0.5f) maxBaseShift = MathF.Max(maxBaseShift, shift);
                if (v.Position.Z > 3.5f) maxTipShift = MathF.Max(maxTipShift, shift);
            }
            Assert.True(maxBaseShift < 1e-2f, $"base should stay put, shifted {maxBaseShift}");
            Assert.True(maxTipShift > 0.5f, $"tip should swing, shifted only {maxTipShift}");
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SkinnedMeshBuilderTests"`
Expected: FAIL with "SkinnedMeshBuilder could not be found" / "Axis could not be found".

- [ ] **Step 3: Write the implementation**

`KhaozEngine.Render3D/Models/SkinnedMeshBuilder.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>The axis a procedural skinned primitive runs along.</summary>
    public enum Axis { X, Y, Z }

    /// <summary>Builds procedural skinned primitives whose bones are defined in code (no authored glb rig). The
    /// dominant code-driven shape is an elongated tube that bends along its length: tentacle, cable, limb,
    /// antenna. Each ring of vertices is weighted to its 1-2 nearest bones with a smooth cross-boundary falloff,
    /// so bending reads as flesh rather than facets.</summary>
    public static class SkinnedMeshBuilder
    {
        /// <summary>Build a capped-open tube of <paramref name="length"/> along <paramref name="axis"/> with
        /// <paramref name="boneCount"/> bones evenly spaced from the base (axis 0) to the tip. The tube has
        /// <paramref name="ringSegments"/>+1 rings of <paramref name="radialSegments"/> vertices. Bone rest
        /// transforms are pure translations along the axis; the rest pose leaves the tube straight.</summary>
        public static SkinnedGltfMesh BuildTube(float radius, float length, int ringSegments, int radialSegments,
            int boneCount, Axis axis = Axis.Z)
        {
            if (ringSegments < 1) throw new ArgumentOutOfRangeException(nameof(ringSegments));
            if (radialSegments < 3) throw new ArgumentOutOfRangeException(nameof(radialSegments));
            if (boneCount < 1) throw new ArgumentOutOfRangeException(nameof(boneCount));

            // Bones: evenly spaced rest translations along the axis. InverseBind = inverse(restWorld).
            var restPose = new Matrix4x4[boneCount];
            var inverseBind = new Matrix4x4[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                float t = boneCount == 1 ? 0f : (float)b / (boneCount - 1);
                restPose[b] = Matrix4x4.CreateTranslation(AlongAxis(axis, t * length));
                Matrix4x4.Invert(restPose[b], out inverseBind[b]);
            }

            var verts = new List<SkinnedVertex>();
            // Two in-plane axes perpendicular to the run axis, for the ring cross-section.
            (Vector3 u, Vector3 w) = PerpAxes(axis);
            int rings = ringSegments + 1;
            for (int r = 0; r < rings; r++)
            {
                float along = (float)r / ringSegments;            // 0..1 down the tube
                float axial = along * length;
                // Bone weighting: position the ring on the [0, boneCount-1] bone axis, weight the two straddling
                // bones by the fractional distance (linear blend), clamped at the ends.
                float bonePos = along * (boneCount - 1);
                int b0 = Math.Clamp((int)MathF.Floor(bonePos), 0, boneCount - 1);
                int b1 = Math.Min(b0 + 1, boneCount - 1);
                float frac = bonePos - b0;
                var indices = new Vector4(b0, b1, 0, 0);
                var weights = SkinningMath.NormalizeWeights(new Vector4(1f - frac, frac, 0, 0));

                for (int s = 0; s < radialSegments; s++)
                {
                    float a = (float)s / radialSegments * MathF.Tau;
                    Vector3 dir = u * MathF.Cos(a) + w * MathF.Sin(a);
                    Vector3 pos = AlongAxis(axis, axial) + dir * radius;
                    verts.Add(new SkinnedVertex
                    {
                        Position = pos,
                        Normal = dir,                              // outward radial normal
                        Color = new Vector4(0.8f, 0.8f, 0.8f, 1f), // default gray, like GltfLoader's base color
                        Uv = new Vector2(along, (float)s / radialSegments),
                        BoneIndices = indices,
                        BoneWeights = weights,
                    });
                }
            }

            // Indices: quad strip between successive rings (two triangles per quad), radial wrap.
            var idx = new List<ushort>();
            for (int r = 0; r < ringSegments; r++)
            for (int s = 0; s < radialSegments; s++)
            {
                int s1 = (s + 1) % radialSegments;
                int a = r * radialSegments + s;
                int b = r * radialSegments + s1;
                int c = (r + 1) * radialSegments + s;
                int d = (r + 1) * radialSegments + s1;
                idx.Add((ushort)a); idx.Add((ushort)c); idx.Add((ushort)b);
                idx.Add((ushort)b); idx.Add((ushort)c); idx.Add((ushort)d);
            }

            return new SkinnedGltfMesh(verts.ToArray(), idx.ToArray(), inverseBind, restPose);
        }

        static Vector3 AlongAxis(Axis axis, float v) => axis switch
        {
            Axis.X => new Vector3(v, 0, 0),
            Axis.Y => new Vector3(0, v, 0),
            _ => new Vector3(0, 0, v),
        };

        static (Vector3, Vector3) PerpAxes(Axis axis) => axis switch
        {
            Axis.X => (Vector3.UnitY, Vector3.UnitZ),
            Axis.Y => (Vector3.UnitZ, Vector3.UnitX),
            _ => (Vector3.UnitX, Vector3.UnitY),
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~SkinnedMeshBuilderTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Models/SkinnedMeshBuilder.cs KhaozEngine.Tests/Render3D/SkinnedMeshBuilderTests.cs
git commit -m "render3d(skinned): procedural tube builder with ring-to-bone weighting"
```

---

## Task 4: Polyline-to-frames helper

A small parallel-transport utility so a consumer that has only a chain of points (not transforms) can produce per-joint world transforms for `DrawSkinned`.

**Files:**
- Create: `KhaozEngine.Render3D/Models/PolylineFrames.cs`
- Test: `KhaozEngine.Tests/Render3D/PolylineFramesTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class PolylineFramesTests
    {
        [Fact]
        public void StraightLineAlongZ_FramesAreTranslationsAtThePoints()
        {
            var pts = new[] { new Vector3(0, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 0, 2) };
            var frames = PolylineFrames.Build(pts, Axis.Z, Vector3.UnitY);
            Assert.Equal(3, frames.Length);
            for (int i = 0; i < pts.Length; i++)
                Assert.True(Vector3.Distance(frames[i].Translation, pts[i]) < 1e-4f);
        }

        [Fact]
        public void Frame_OrientsRunAxisAlongTheChainDirection()
        {
            // Chain turns from +Z to +X; the second frame's local +Z should point roughly +X in world.
            var pts = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 1), new Vector3(2, 0, 1) };
            var frames = PolylineFrames.Build(pts, Axis.Z, Vector3.UnitY);
            Vector3 localZ = Vector3.TransformNormal(Vector3.UnitZ, frames[1]);
            Assert.True(localZ.X > 0.5f, $"run axis should bend toward +X, got {localZ}");
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PolylineFramesTests"`
Expected: FAIL with "PolylineFrames could not be found".

- [ ] **Step 3: Write the implementation**

`KhaozEngine.Render3D/Models/PolylineFrames.cs`:

```csharp
using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Turns a chain of points into per-joint world transforms for <see cref="Scene3D.DrawSkinned"/>,
    /// orienting each frame's run axis along the local chain direction (a simple up-hint frame, sufficient for
    /// tentacles/cables; consumers that already track per-segment rotations can skip this). Presentation only.</summary>
    public static class PolylineFrames
    {
        /// <summary>Build one world transform per point. Each frame is positioned at its point with its
        /// <paramref name="runAxis"/> aligned to the direction toward the next point (the last reuses the
        /// previous direction), using <paramref name="up"/> as the roll reference.</summary>
        public static Matrix4x4[] Build(ReadOnlySpan<Vector3> points, Axis runAxis, Vector3 up)
        {
            if (points.Length == 0) return Array.Empty<Matrix4x4>();
            var frames = new Matrix4x4[points.Length];
            Vector3 prevDir = AxisVec(runAxis);
            for (int i = 0; i < points.Length; i++)
            {
                Vector3 dir = i + 1 < points.Length ? points[i + 1] - points[i] : prevDir;
                if (dir.LengthSquared() < 1e-10f) dir = prevDir;
                dir = Vector3.Normalize(dir);
                prevDir = dir;
                frames[i] = OrientAxisTo(runAxis, dir, up) * Matrix4x4.CreateTranslation(points[i]);
            }
            return frames;
        }

        static Vector3 AxisVec(Axis a) => a switch
        {
            Axis.X => Vector3.UnitX, Axis.Y => Vector3.UnitY, _ => Vector3.UnitZ
        };

        // Rotation mapping the run axis onto `dir`, with `up` as the secondary reference (Gram-Schmidt basis).
        static Matrix4x4 OrientAxisTo(Axis runAxis, Vector3 dir, Vector3 up)
        {
            Vector3 f = dir;
            Vector3 r = Vector3.Cross(up, f);
            if (r.LengthSquared() < 1e-8f) r = Vector3.Cross(Vector3.UnitX, f);
            r = Vector3.Normalize(r);
            Vector3 u = Vector3.Normalize(Vector3.Cross(f, r));
            // Columns map local (X=r, Y=u, Z=f) when runAxis is Z; remap for X/Y so the chosen axis lands on dir.
            (Vector3 lx, Vector3 ly, Vector3 lz) = runAxis switch
            {
                Axis.X => (f, u, r),
                Axis.Y => (r, f, u),
                _ => (r, u, f),
            };
            return new Matrix4x4(
                lx.X, lx.Y, lx.Z, 0,
                ly.X, ly.Y, ly.Z, 0,
                lz.X, lz.Y, lz.Z, 0,
                0, 0, 0, 1);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PolylineFramesTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render3D/Models/PolylineFrames.cs KhaozEngine.Tests/Render3D/PolylineFramesTests.cs
git commit -m "render3d(skinned): PolylineFrames helper (points -> joint transforms)"
```

---

## Task 5: GltfLoader.LoadSkinned (read authored glb rigs)

**Files:**
- Modify: `KhaozEngine.Render3D/Models/GltfLoader.cs`
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj` (add SharpGLTF.Toolkit for in-memory rig building in the test)
- Test: `KhaozEngine.Tests/Render3D/GltfLoaderSkinnedTests.cs`

- [ ] **Step 1: Add the test's glb-builder dependency**

The loader uses `SharpGLTF.Core` (`ModelRoot.Load`). Building a rigged glb in-memory for the test needs the `SharpGLTF.Toolkit` builder API. Add to `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, inside the existing `<ItemGroup>` that holds PackageReferences:

```xml
<PackageReference Include="SharpGLTF.Toolkit" Version="1.0.6" />
```

Run: `dotnet restore KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: restores with no error (version matches the existing SharpGLTF.Core 1.0.6 already referenced by Render3D).

- [ ] **Step 2: Write the failing test**

```csharp
using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Render3D;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class GltfLoaderSkinnedTests
    {
        // Build a minimal 2-bone skinned triangle glb in a temp file and return its path.
        static string WriteRiggedGlb()
        {
            var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexJoints4>("skin");
            var prim = mesh.UsePrimitive(MaterialBuilder.CreateDefault());

            // Three verts; vert at base bound to bone 0, the other two to bone 1.
            VertexBuilder<VertexPositionNormal, VertexEmpty, VertexJoints4> V(Vector3 p, int bone) =>
                new(new VertexPositionNormal(p, Vector3.UnitZ), default, new VertexJoints4((bone, 1f)));

            prim.AddTriangle(
                V(new Vector3(0, 0, 0), 0),
                V(new Vector3(0, 1, 0), 1),
                V(new Vector3(1, 1, 0), 1));

            // Armature: bone0 at origin, bone1 a child translated +1 in Y (rest world = (0,1,0)).
            var bone0 = new NodeBuilder("bone0");
            var bone1 = bone0.CreateNode("bone1");
            bone1.LocalTransform = Matrix4x4.CreateTranslation(0, 1, 0);

            var scene = new SceneBuilder();
            scene.AddSkinnedMesh(mesh, Matrix4x4.Identity, bone0, bone1);
            var model = scene.ToGltf2();

            string path = Path.Combine(Path.GetTempPath(), $"ke_skin_{Guid.NewGuid():N}.glb");
            model.SaveGLB(path);
            return path;
        }

        [Fact]
        public void LoadSkinned_ReadsBonesWeightsAndInverseBind()
        {
            string path = WriteRiggedGlb();
            try
            {
                SkinnedGltfMesh m = GltfLoader.LoadSkinned(path);

                Assert.Equal(2, m.BoneCount);
                Assert.True(m.Vertices.Length >= 3);
                // Every vertex's weights are normalized.
                foreach (var v in m.Vertices)
                {
                    float sum = v.BoneWeights.X + v.BoneWeights.Y + v.BoneWeights.Z + v.BoneWeights.W;
                    Assert.True(MathF.Abs(sum - 1f) < 1e-3f);
                }
                // bone1's inverse-bind should translate model->bone-local by -1 in Y (inverse of its (0,1,0) rest).
                Vector3 ibTranslation = m.InverseBind[1].Translation;
                Assert.True(MathF.Abs(ibTranslation.Y + 1f) < 1e-3f, $"expected ~ -1 Y, got {ibTranslation}");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadSkinned_RestPose_LeavesGeometryUnmoved()
        {
            string path = WriteRiggedGlb();
            try
            {
                SkinnedGltfMesh m = GltfLoader.LoadSkinned(path);
                Span<Matrix4x4> composed = stackalloc Matrix4x4[m.BoneCount];
                for (int i = 0; i < m.BoneCount; i++)
                    composed[i] = SkinningMath.Compose(m.RestPose[i], m.InverseBind[i]);
                foreach (var v in m.Vertices)
                {
                    var skin = SkinningMath.BlendSkinMatrix(composed, v.BoneIndices, v.BoneWeights);
                    Assert.True(Vector3.Distance(Vector3.Transform(v.Position, skin), v.Position) < 1e-3f);
                }
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadSkinned_OnUnriggedMesh_Throws()
        {
            // A non-skinned cube glb has no JOINTS_0; LoadSkinned must fail clearly.
            var mesh = new MeshBuilder<VertexPosition, VertexEmpty, VertexEmpty>("plain");
            var prim = mesh.UsePrimitive(MaterialBuilder.CreateDefault());
            prim.AddTriangle(
                new VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty>(new VertexPosition(0, 0, 0)),
                new VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty>(new VertexPosition(1, 0, 0)),
                new VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty>(new VertexPosition(0, 1, 0)));
            var scene = new SceneBuilder();
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
            string path = Path.Combine(Path.GetTempPath(), $"ke_plain_{Guid.NewGuid():N}.glb");
            scene.ToGltf2().SaveGLB(path);
            try { Assert.Throws<InvalidOperationException>(() => GltfLoader.LoadSkinned(path)); }
            finally { File.Delete(path); }
        }
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GltfLoaderSkinnedTests"`
Expected: FAIL with "GltfLoader does not contain a definition for LoadSkinned".

- [ ] **Step 4: Implement LoadSkinned**

Add to `KhaozEngine.Render3D/Models/GltfLoader.cs` (inside the `GltfLoader` class, after `Load`). Add `using System.Linq;` at the top if not present.

```csharp
        /// <summary>Load a rigged glb/glTF as a <see cref="SkinnedGltfMesh"/>: reads POSITION/NORMAL/TEXCOORD_0
        /// plus JOINTS_0/WEIGHTS_0 and the skin's inverse-bind matrices + rest-pose joint world transforms.
        /// Embedded images are ignored (bind a PNG albedo separately, as with <see cref="Load"/>). Throws if the
        /// mesh has no skin/joint data (use <see cref="Load"/> for rigid meshes). Indexed directly (no re-weld) so
        /// joints/weights stay aligned to their vertices; throws past the ushort index ceiling.</summary>
        public static SkinnedGltfMesh LoadSkinned(string path)
        {
            ModelRoot root = ModelRoot.Load(path);

            var verts = new List<SkinnedVertex>();
            var indices = new List<ushort>();
            Skin? skin = null;

            foreach (var mesh in root.LogicalMeshes)
            foreach (var prim in mesh.Primitives)
            {
                var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                var joints = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                var weights = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
                if (pos == null || joints == null || weights == null) continue;

                // Find the skin used by a node that references this mesh (first one wins; one skin per mesh here).
                skin ??= root.LogicalNodes.FirstOrDefault(n => n.Mesh == mesh && n.Skin != null)?.Skin
                         ?? root.LogicalSkins.FirstOrDefault();

                var normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var texcoords = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                Vector4 baseColor = ReadBaseColor(prim.Material);

                int baseIndex = verts.Count;
                for (int i = 0; i < pos.Count; i++)
                {
                    Vector4 w = SkinningMath.NormalizeWeights(weights[i]);
                    verts.Add(new SkinnedVertex
                    {
                        Position = pos[i],
                        Normal = normals != null && i < normals.Count ? normals[i] : Vector3.UnitY,
                        Color = baseColor,
                        Uv = texcoords != null && i < texcoords.Count ? texcoords[i] : Vector2.Zero,
                        BoneIndices = joints[i],
                        BoneWeights = w,
                    });
                }
                foreach (var (a, b, c) in prim.GetTriangleIndices())
                {
                    Checked(baseIndex + a); Checked(baseIndex + b); Checked(baseIndex + c);
                    indices.Add((ushort)(baseIndex + a));
                    indices.Add((ushort)(baseIndex + b));
                    indices.Add((ushort)(baseIndex + c));
                }
            }

            if (verts.Count == 0 || skin == null)
                throw new InvalidOperationException("glTF has no skinned mesh (JOINTS_0/WEIGHTS_0 + skin): " + path);

            int boneCount = skin.JointsCount;
            var inverseBind = new Matrix4x4[boneCount];
            var restPose = new Matrix4x4[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                var (node, ibm) = skin.GetJoint(b);
                inverseBind[b] = ibm;
                restPose[b] = node.WorldMatrix;     // bind-pose joint world transform
            }

            return new SkinnedGltfMesh(verts.ToArray(), indices.ToArray(), inverseBind, restPose);

            void Checked(int i)
            {
                if (i > ushort.MaxValue)
                    throw new InvalidOperationException("skinned glTF exceeds the 65535 ushort vertex ceiling: " + path);
            }
        }
```

Note on SharpGLTF API: `skin.GetJoint(b)` returns `(Node Joint, Matrix4x4 InverseBindMatrix)` in 1.0.6; `node.WorldMatrix` is the node's world transform. If the tuple field names differ in the installed version, destructure positionally as shown.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GltfLoaderSkinnedTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render3D/Models/GltfLoader.cs KhaozEngine.Tests/KhaozEngine.Tests.csproj KhaozEngine.Tests/Render3D/GltfLoaderSkinnedTests.cs
git commit -m "render3d(skinned): GltfLoader.LoadSkinned reads glb rigs (joints/weights/inverse-bind)"
```

---

## Task 6: Skinned vertex shader source

Add the skinned vertex shader (the lit fragment shader `ModelFrag` is reused unchanged).

**Files:**
- Modify: `KhaozEngine.Render3D/Internal/ShaderSources.cs`

No standalone test (GLSL compiles at pipeline build, exercised in Task 9's GPU smoke test).

- [ ] **Step 1: Add the shader constant**

Insert into `ShaderSources` (after `ModelFrag`):

```csharp
        // ---- Skinned model vertex shader. Same per-frame UBO (set 0, binding 0) and same per-instance stream as
        //      ModelVert, but with two extra per-vertex attributes (bone indices + weights) and one extra
        //      per-instance attribute (the bone offset into the shared bone buffer). The bone matrices for EVERY
        //      skinned draw this frame live in one read-only structured buffer (set 1, binding 0); each instance
        //      reads its own contiguous range starting at IBoneOffset. Outputs match ModelVert exactly so the
        //      shared ModelFrag links and the lit/colour path is identical. ----
        public const string SkinnedModelVert = @"#version 450
layout(set=0, binding=0) uniform U {
    mat4 ViewProj;
    vec4 LightDir; vec4 LightColor; vec4 Ambient; vec4 Params;
    vec4 FillDir; vec4 FillColor; vec4 CameraPos;
    vec4 PointPosRadius[16];
    vec4 PointColorIntensity[16];
};
layout(set=1, binding=0) readonly buffer Bones { mat4 bones[]; };
layout(location=0) in vec3 Position;
layout(location=1) in vec3 Normal;
layout(location=2) in vec4 Color;
layout(location=3) in vec2 TexCoord;
layout(location=4) in vec4 BoneIndices;  // up to 4 bone indices, float-encoded
layout(location=5) in vec4 BoneWeights;  // 4 weights, normalized at load
layout(location=6)  in vec4 IModel0;     // per-instance model matrix rows
layout(location=7)  in vec4 IModel1;
layout(location=8)  in vec4 IModel2;
layout(location=9)  in vec4 IModel3;
layout(location=10) in vec4 ITint;
layout(location=11) in vec4 IEmissive;
layout(location=12) in vec4 ISpecParams;
layout(location=13) in float IBoneOffset; // base index into bones[] for this instance's palette
layout(location=0) out vec3 vNormalW;
layout(location=1) out vec4 vColor;
layout(location=2) out float vDepth;
layout(location=3) out vec3 vWorldPos;
layout(location=4) out vec2 vUv;
layout(location=5) out vec4 vTint;
layout(location=6) out vec4 vEmissive;
layout(location=7) out vec4 vSpecParams;
void main() {
    int o = int(IBoneOffset);
    float total = BoneWeights.x + BoneWeights.y + BoneWeights.z + BoneWeights.w;
    mat4 skin;
    if (total < 1e-6) {
        skin = mat4(1.0);
    } else {
        skin = BoneWeights.x * bones[o + int(BoneIndices.x)]
             + BoneWeights.y * bones[o + int(BoneIndices.y)]
             + BoneWeights.z * bones[o + int(BoneIndices.z)]
             + BoneWeights.w * bones[o + int(BoneIndices.w)];
    }
    mat4 Model = mat4(IModel0, IModel1, IModel2, IModel3);
    vec4 local = skin * vec4(Position, 1.0);
    vec4 world = Model * local;
    gl_Position = ViewProj * world;
    vNormalW = normalize(mat3(Model) * mat3(skin) * Normal);
    vColor = Color;
    vDepth = gl_Position.z / gl_Position.w;
    vWorldPos = world.xyz;
    vUv = TexCoord;
    vTint = ITint;
    vEmissive = IEmissive;
    vSpecParams = ISpecParams;
}";
```

- [ ] **Step 2: Build to confirm it compiles (C#-side; GLSL is validated at pipeline creation in Task 9)**

Run: `dotnet build KhaozEngine.Render3D/KhaozEngine.Render3D.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Render3D/Internal/ShaderSources.cs
git commit -m "render3d(skinned): skinned vertex shader (reuses lit ModelFrag, bones in set 1 SSBO)"
```

---

## Task 7: SkinnedModelRenderer + bone-buffer grouping

The GPU renderer for the skinned pass, plus the pure grouping it relies on. The grouping is factored out and headless-tested; the GPU plumbing mirrors `ModelRenderer`.

**Files:**
- Create: `KhaozEngine.Render3D/Rendering/SkinnedModelRenderer.cs`
- Create: `KhaozEngine.Render3D/SkinnedSceneInstances.cs`
- Test: `KhaozEngine.Tests/Render3D/GroupSkinnedInstancesTests.cs`

- [ ] **Step 1: Write the failing test for the pure grouping**

```csharp
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Headless coverage of SkinnedModelRenderer.GroupSkinnedInstances: skinned draws are bucketed into
    /// contiguous per-mesh runs (first-seen order, prefix-sum offsets), and each instance keeps its own bone
    /// offset so instances of one mesh draw in a single instanced call yet read distinct bone ranges.</summary>
    public class GroupSkinnedInstancesTests
    {
        static SkinnedSceneInstances.Instance Inst(int mesh, uint boneOffset, Color tint) => new(
            new SkinnedMeshHandle(mesh), Matrix4x4.Identity, tint, Material.None, boneOffset);

        [Fact]
        public void InterleavedMeshes_BucketedContiguously_BoneOffsetsPreserved()
        {
            var items = new List<SkinnedSceneInstances.Instance>
            {
                Inst(0, 0,  Color.White),   // mesh 0, bones [0..)
                Inst(1, 8,  Color.White),   // mesh 1
                Inst(0, 16, Color.White),   // mesh 0 again, different bone range
            };
            var data = new List<SkinnedModelRenderer.SkinnedInstanceData>();
            var runs = new List<Scene3D.SkinnedMeshRun>();

            SkinnedModelRenderer.GroupSkinnedInstances(items, data, runs);

            Assert.Equal(2, runs.Count);
            Assert.Equal(0, runs[0].Mesh.Index);
            Assert.Equal(0u, runs[0].Start);
            Assert.Equal(2u, runs[0].Count);          // two instances of mesh 0, contiguous
            Assert.Equal(1, runs[1].Mesh.Index);
            Assert.Equal(2u, runs[1].Start);
            // mesh-0 instances keep their own bone offsets even after reordering.
            Assert.Equal(0f, data[0].BoneOffset);
            Assert.Equal(16f, data[1].BoneOffset);
            Assert.Equal(8f, data[2].BoneOffset);     // mesh 1
        }

        [Fact]
        public void Empty_ProducesNoRuns()
        {
            var data = new List<SkinnedModelRenderer.SkinnedInstanceData>();
            var runs = new List<Scene3D.SkinnedMeshRun>();
            SkinnedModelRenderer.GroupSkinnedInstances(new List<SkinnedSceneInstances.Instance>(), data, runs);
            Assert.Empty(runs);
            Assert.Empty(data);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GroupSkinnedInstancesTests"`
Expected: FAIL with "SkinnedSceneInstances could not be found".

- [ ] **Step 3: Write SkinnedSceneInstances**

`KhaozEngine.Render3D/SkinnedSceneInstances.cs`:

```csharp
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>The per-frame skinned-draw queue: one entry per <see cref="Scene3D.DrawSkinned"/> call, holding
    /// the mesh handle, model transform, tint, material, and the bone offset (the start index of this draw's
    /// composed bone matrices in Scene3D's shared per-frame bone buffer). Mirrors <see cref="SceneInstances"/>.</summary>
    public sealed class SkinnedSceneInstances
    {
        public readonly struct Instance
        {
            public readonly SkinnedMeshHandle Mesh;
            public readonly Matrix4x4 World;
            public readonly Vector4 Tint;             // stored as Vector4 (Color converts implicitly), like SceneInstances
            public readonly Material Material;
            public readonly uint BoneOffset;
            // Take Color (implicitly stored as Vector4) to mirror SceneInstances.Instance exactly.
            public Instance(SkinnedMeshHandle mesh, Matrix4x4 world, Color tint, Material material, uint boneOffset)
            {
                Mesh = mesh; World = world; Tint = tint; Material = material; BoneOffset = boneOffset;
            }
        }

        readonly List<Instance> _items = new();
        public IReadOnlyList<Instance> Items => _items;
        public void Begin() => _items.Clear();
        public void Add(SkinnedMeshHandle mesh, Matrix4x4 world, Color tint, Material material, uint boneOffset)
            => _items.Add(new Instance(mesh, world, tint, material, boneOffset));
    }
}
```

`Tint` stores a `Vector4` but the constructor takes a `Color` (the engine has an implicit `Color`→`Vector4`), so `GroupSkinnedInstances` can assign `inst.Tint` straight into `SkinnedInstanceData.Tint`, exactly as the rigid path does.

Note: the matte default material is `Material.None` (emissive 0, specular 0, shininess 32), confirmed in `KhaozEngine.Render3D/Material.cs`. `Material.Emissive` (Color), `Material.Specular` (float), `Material.Shininess` (float) are the fields `GroupSkinnedInstances` reads, matching the rigid `GroupInstances` at Scene3D.cs:713-714.

- [ ] **Step 4: Write SkinnedModelRenderer**

`KhaozEngine.Render3D/Rendering/SkinnedModelRenderer.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>Draws skinned meshes into the model MRT. Reuses ModelRenderer's frame UBO (set 0) and lit
    /// fragment shader; adds a skinned vertex shader, two extra per-vertex attributes (bone indices + weights),
    /// a per-instance bone offset, and one growable read-only structured buffer (set 1) holding every skinned
    /// draw's composed bone matrices for the frame. Instances of one mesh draw in a single instanced call, each
    /// reading its own bone range via the offset.</summary>
    internal sealed class SkinnedModelRenderer : IDisposable
    {
        /// <summary>Per-instance stream for the skinned pass: the rigid InstanceData fields plus the bone offset.
        /// 64 + 16*3 + 4 = 116 bytes.</summary>
        public struct SkinnedInstanceData
        {
            public Matrix4x4 Model;     // 64
            public Vector4 Tint;        // 16
            public Vector4 Emissive;    // 16
            public Vector4 SpecParams;  // 16
            public float BoneOffset;    // 4  (base index into the bone buffer; float so it rides as a Float1 attr)
            public const uint SizeInBytes = 116;
        }

        readonly IGpuDevice _gd;
        readonly ModelRenderer _model;          // shared frame UBO + material sets come from here
        readonly IGpuResourceLayout _boneLayout; // set 1: the bone structured buffer
        readonly IGpuPipeline _pipeline;
        readonly IGpuShaderSet _shaders;

        IGpuBuffer? _instanceBuffer; uint _instanceCapacity;
        IGpuBuffer? _boneBuffer; uint _boneCapacity;        // capacity in matrices
        IGpuResourceSet? _boneSet;

        public SkinnedModelRenderer(IGpuDevice gd, GpuOutputDescription modelOutputs, ModelRenderer model)
        {
            _gd = gd; _model = model;
            var factory = gd.Factory;

            _boneLayout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Bones", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Vertex)));

            _shaders = factory.CreateShadersFromSpirv(ShaderSources.SkinnedModelVert, ShaderSources.ModelFrag);

            var vertexLayout = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
                new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
                new GpuVertexElement("BoneIndices", GpuVertexElementFormat.Float4),
                new GpuVertexElement("BoneWeights", GpuVertexElementFormat.Float4));

            var instanceLayout = new GpuVertexLayoutDescription(
                stride: SkinnedInstanceData.SizeInBytes,
                instanceStepRate: 1,
                elements: new[]
                {
                    new GpuVertexElement("IModel0", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel1", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel2", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IModel3", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("ITint", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IEmissive", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("ISpecParams", GpuVertexElementFormat.Float4),
                    new GpuVertexElement("IBoneOffset", GpuVertexElementFormat.Float1),
                });

            _pipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[]
                {
                    GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend,
                },
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                // set 0 = material (UBO + albedo + sampler), reused from ModelRenderer's layout; set 1 = bones.
                ResourceLayouts = new[] { _model.MaterialLayout, _boneLayout },
                ShaderSet = _shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout, instanceLayout },
                Outputs = modelOutputs,
            });
        }

        /// <summary>Upload this frame's composed bone palette (every skinned draw's matrices, concatenated) into
        /// the shared structured buffer, growing it (and recreating its resource set) on demand.</summary>
        public void UploadBones(IGpuCommandList cl, ReadOnlySpan<Matrix4x4> bones)
        {
            if (bones.Length == 0) return;
            EnsureBoneCapacity((uint)bones.Length);
            cl.UpdateBuffer(_boneBuffer!, 0, bones);
        }

        void EnsureBoneCapacity(uint count)
        {
            if (_boneBuffer != null && _boneCapacity >= count) return;
            _boneBuffer?.Dispose(); _boneSet?.Dispose();
            _boneCapacity = Math.Max(count, _boneCapacity == 0 ? 64u : _boneCapacity * 2);
            _boneBuffer = _gd.Factory.CreateBuffer(new GpuBufferDescription(
                _boneCapacity * 64u, GpuBufferUsage.StructuredBufferReadOnly, structureByteStride: 64u));
            _boneSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_boneLayout, _boneBuffer));
        }

        public void UploadInstances(IGpuCommandList cl, ReadOnlySpan<SkinnedInstanceData> instances)
        {
            if (instances.Length == 0) return;
            EnsureInstanceCapacity((uint)instances.Length);
            cl.UpdateBuffer(_instanceBuffer!, 0, instances);
        }

        void EnsureInstanceCapacity(uint count)
        {
            if (_instanceBuffer != null && _instanceCapacity >= count) return;
            _instanceBuffer?.Dispose();
            _instanceCapacity = Math.Max(count, _instanceCapacity == 0 ? 64u : _instanceCapacity * 2);
            _instanceBuffer = _gd.Factory.CreateBuffer(
                new GpuBufferDescription(_instanceCapacity * SkinnedInstanceData.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }

        public void BindPass(IGpuCommandList cl)
        {
            cl.SetPipeline(_pipeline);
            cl.SetGraphicsResourceSet(1, _boneSet!);   // bones are constant across the whole skinned pass
        }

        /// <summary>Draw one skinned mesh's run. Binds the mesh's material set (or the renderer's white default)
        /// at set 0; set 1 (bones) is already bound by <see cref="BindPass"/>.</summary>
        public void DrawSkinnedMeshInstanced(IGpuCommandList cl, IGpuBuffer vb, IGpuBuffer ib, int indexCount,
            uint instanceStart, uint instanceCount, IGpuResourceSet? materialSet)
        {
            cl.SetGraphicsResourceSet(0, materialSet ?? _model.DefaultMaterialSet);
            cl.SetVertexBuffer(0, vb);
            cl.SetVertexBuffer(1, _instanceBuffer!);
            cl.SetIndexBuffer(ib, GpuIndexFormat.UInt16);
            cl.DrawIndexed((uint)indexCount, instanceCount, 0, 0, instanceStart);
        }

        /// <summary>Group queued skinned instances by mesh handle into <paramref name="instanceData"/> (flat,
        /// mesh-contiguous) and <paramref name="runs"/> (one per unique mesh, first-seen). Each instance keeps its
        /// own bone offset. Pure + headless-testable; both lists are cleared and refilled.</summary>
        internal static void GroupSkinnedInstances(IReadOnlyList<SkinnedSceneInstances.Instance> items,
            List<SkinnedInstanceData> instanceData, List<Scene3D.SkinnedMeshRun> runs)
        {
            instanceData.Clear(); runs.Clear();
            if (items.Count == 0) return;

            for (int i = 0; i < items.Count; i++)
            {
                var mesh = items[i].Mesh;
                int slot = FindRun(runs, mesh);
                if (slot < 0) runs.Add(new Scene3D.SkinnedMeshRun(mesh, 0, 1));
                else runs[slot] = new Scene3D.SkinnedMeshRun(mesh, 0, runs[slot].Count + 1);
            }

            uint cursor = 0;
            Span<uint> writeCursor = runs.Count <= 64 ? stackalloc uint[runs.Count] : new uint[runs.Count];
            for (int r = 0; r < runs.Count; r++)
            {
                writeCursor[r] = cursor;
                runs[r] = new Scene3D.SkinnedMeshRun(runs[r].Mesh, cursor, runs[r].Count);
                cursor += runs[r].Count;
            }

            for (int i = 0; i < (int)cursor; i++) instanceData.Add(default);
            for (int i = 0; i < items.Count; i++)
            {
                var inst = items[i];
                int slot = FindRun(runs, inst.Mesh);
                uint dst = writeCursor[slot]++;
                instanceData[(int)dst] = new SkinnedInstanceData
                {
                    Model = inst.World,
                    Tint = inst.Tint,
                    Emissive = inst.Material.Emissive,
                    SpecParams = new Vector4(inst.Material.Specular, inst.Material.Shininess, 0f, 0f),
                    BoneOffset = inst.BoneOffset,
                };
            }
        }

        static int FindRun(List<Scene3D.SkinnedMeshRun> runs, SkinnedMeshHandle mesh)
        {
            for (int r = 0; r < runs.Count; r++)
                if (runs[r].Mesh.Index == mesh.Index && runs[r].Mesh.Generation == mesh.Generation) return r;
            return -1;
        }

        public void Dispose()
        {
            _pipeline.Dispose(); _shaders.Dispose(); _boneLayout.Dispose();
            _boneSet?.Dispose(); _boneBuffer?.Dispose(); _instanceBuffer?.Dispose();
        }
    }
}
```

This references three new members on `ModelRenderer` (`MaterialLayout`, `DefaultMaterialSet`) and a new `Scene3D.SkinnedMeshRun`. Add them in the next steps.

- [ ] **Step 5: Expose the shared material layout + default set on ModelRenderer**

In `KhaozEngine.Render3D/Rendering/ModelRenderer.cs`, add two internal accessors (the fields `_layout` and `_defaultSet` already exist):

```csharp
        /// <summary>The material resource layout (set 0: UBO + albedo + sampler). Shared with the skinned
        /// pipeline so both passes bind the same material sets.</summary>
        internal IGpuResourceLayout MaterialLayout => _layout;

        /// <summary>The white-default material set, bound for skinned meshes with no texture.</summary>
        internal IGpuResourceSet DefaultMaterialSet => _defaultSet;
```

- [ ] **Step 6: Add SkinnedMeshRun to Scene3D**

In `KhaozEngine.Render3D/Scene3D.cs`, next to the existing `MeshRun` struct, add:

```csharp
        /// <summary>A contiguous run of skinned instances of one mesh handle inside the flat skinned-instance
        /// array.</summary>
        internal readonly struct SkinnedMeshRun
        {
            public readonly SkinnedMeshHandle Mesh;
            public readonly uint Start;
            public readonly uint Count;
            public SkinnedMeshRun(SkinnedMeshHandle mesh, uint start, uint count) { Mesh = mesh; Start = start; Count = count; }
        }
```

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~GroupSkinnedInstancesTests"`
Expected: PASS (2 tests). (This also forces the renderer + Scene3D additions to compile.)

- [ ] **Step 8: Commit**

```bash
git add KhaozEngine.Render3D/Rendering/SkinnedModelRenderer.cs KhaozEngine.Render3D/SkinnedSceneInstances.cs KhaozEngine.Render3D/Rendering/ModelRenderer.cs KhaozEngine.Render3D/Scene3D.cs KhaozEngine.Tests/Render3D/GroupSkinnedInstancesTests.cs
git commit -m "render3d(skinned): SkinnedModelRenderer + shared bone buffer + pure grouping"
```

---

## Task 8: Scene3D public API (LoadSkinnedMesh / DrawSkinned / UnloadSkinnedMesh)

Wire the skinned path into Scene3D: storage, the public API, the per-frame bone buffer + compose, Begin clears, and the render pass.

**Files:**
- Modify: `KhaozEngine.Render3D/Scene3D.cs`
- Test: `KhaozEngine.Tests/Render3D/Scene3DSkinnedQueueTests.cs`

- [ ] **Step 1: Write the failing test (headless queue behavior, no GPU)**

These assert the queue/bone-buffer bookkeeping via internal accessors, with no device.

```csharp
using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class Scene3DSkinnedQueueTests
    {
        [Fact]
        public void ComposeBones_RestPose_FillsIdentityBlock()
        {
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 2f, 2, 4, 3);
            var dst = new System.Collections.Generic.List<Matrix4x4>();
            // Scene3D.ComposeBonesInto is the pure compose helper the queue uses.
            uint offset = Scene3D.ComposeBonesInto(dst, tube.RestPose, tube.InverseBind);
            Assert.Equal(0u, offset);
            Assert.Equal(3, dst.Count);
            foreach (var m in dst)
                Assert.True(Vector3.Distance(Vector3.Transform(new Vector3(1, 2, 3), m), new Vector3(1, 2, 3)) < 1e-3f);
        }

        [Fact]
        public void ComposeBones_SecondCall_AppendsAtRunningOffset()
        {
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 2f, 2, 4, 3);
            var dst = new System.Collections.Generic.List<Matrix4x4>();
            Scene3D.ComposeBonesInto(dst, tube.RestPose, tube.InverseBind);
            uint offset2 = Scene3D.ComposeBonesInto(dst, tube.RestPose, tube.InverseBind);
            Assert.Equal(3u, offset2);
            Assert.Equal(6, dst.Count);
        }

        [Fact]
        public void ComposeBones_WrongBoneCount_Throws()
        {
            var dst = new System.Collections.Generic.List<Matrix4x4>();
            Assert.Throws<ArgumentException>(() =>
                Scene3D.ComposeBonesInto(dst, new[] { Matrix4x4.Identity }, new[] { Matrix4x4.Identity, Matrix4x4.Identity }));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Scene3DSkinnedQueueTests"`
Expected: FAIL with "Scene3D does not contain a definition for ComposeBonesInto".

- [ ] **Step 3: Add the pure compose helper to Scene3D**

In `KhaozEngine.Render3D/Scene3D.cs`, add (near `GroupInstances`):

```csharp
        /// <summary>Compose <paramref name="boneMatrices"/> (per-frame joint world transforms) with
        /// <paramref name="inverseBind"/> into <paramref name="dst"/>, appending one skin matrix per bone, and
        /// return the start offset (in matrices) of the appended block. Pure + headless-testable. Throws if the
        /// two inputs differ in length.</summary>
        internal static uint ComposeBonesInto(List<Matrix4x4> dst, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4[] inverseBind)
        {
            if (boneMatrices.Length != inverseBind.Length)
                throw new ArgumentException(
                    $"boneMatrices length {boneMatrices.Length} must equal the mesh bone count {inverseBind.Length}.");
            uint offset = (uint)dst.Count;
            for (int b = 0; b < boneMatrices.Length; b++)
                dst.Add(SkinningMath.Compose(boneMatrices[b], inverseBind[b]));
            return offset;
        }
```

Add `using System.Numerics;` is already present; ensure `System` and `System.Collections.Generic` are imported (they are).

- [ ] **Step 4: Run the compose test to green**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Scene3DSkinnedQueueTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Add skinned storage + API + render wiring to Scene3D**

In `KhaozEngine.Render3D/Scene3D.cs`:

(a) Fields, near the rigid mesh storage (`_meshes`, `_slots`):

```csharp
        // Skinned mesh storage, parallel to the rigid mesh storage above.
        readonly List<SkinnedMeshEntry?> _skinnedMeshes = new();
        readonly MeshSlotMap _skinnedSlots = new();
        readonly SkinnedSceneInstances _skinnedInstances = new();
        // Per-frame composed bone palette for every skinned draw (cleared each Begin), and reused grouping buffers.
        readonly List<Matrix4x4> _boneMatrices = new();
        readonly List<SkinnedModelRenderer.SkinnedInstanceData> _skinnedInstanceData = new();
        readonly List<SkinnedMeshRun> _skinnedRuns = new();
        SkinnedModelRenderer _skinnedModel = null!;   // set in the constructor after _model
```

(b) Construct the skinned renderer in the `Scene3D` constructor, right after `_model = new ModelRenderer(...)`:

```csharp
            _skinnedModel = new SkinnedModelRenderer(gd, _res.ModelFB.Outputs, _model);
```

(c) The skinned mesh slot record (near the rigid `Mesh` struct):

```csharp
        /// <summary>A GPU-resident skinned mesh: its vertex/index buffers, index count, optional material set, and
        /// the CPU-side inverse-bind matrices needed to compose per-frame bone palettes at DrawSkinned time.</summary>
        sealed class SkinnedMeshEntry
        {
            public readonly IGpuBuffer Vb, Ib;
            public readonly int IndexCount;
            public readonly IGpuResourceSet? MaterialSet;
            public readonly Matrix4x4[] InverseBind;
            public SkinnedMeshEntry(IGpuBuffer vb, IGpuBuffer ib, int indexCount, IGpuResourceSet? materialSet, Matrix4x4[] inverseBind)
            {
                Vb = vb; Ib = ib; IndexCount = indexCount; MaterialSet = materialSet; InverseBind = inverseBind;
            }
        }
```

(d) Public load/draw/unload API (place near `LoadMesh`/`Draw`/`UnloadMesh`):

```csharp
        /// <summary>Upload a skinned mesh to the GPU once; returns a handle to draw it with
        /// <see cref="DrawSkinned"/>. Untextured (samples the 1x1 white default, so colour is the baked vertex
        /// colour times any per-instance tint).</summary>
        public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh) => LoadSkinnedInternal(mesh, null);

        /// <summary>Upload a skinned mesh and bind <paramref name="texture"/> as its albedo
        /// (<c>texRgb * vColor * vTint</c>). An invalid handle falls back to untextured.</summary>
        public SkinnedMeshHandle LoadSkinnedMesh(SkinnedGltfMesh mesh, TextureHandle texture)
        {
            IGpuResourceSet? material = texture.IsValid ? _model.CreateMaterialSet(_textures[texture.ListIndex]) : null;
            return LoadSkinnedInternal(mesh, material);
        }

        SkinnedMeshHandle LoadSkinnedInternal(SkinnedGltfMesh mesh, IGpuResourceSet? material)
        {
            var f = _gd.Factory;
            var vb = f.CreateBuffer(new GpuBufferDescription((uint)(mesh.Vertices.Length * SkinnedVertex.SizeInBytes), GpuBufferUsage.VertexBuffer));
            _gd.UpdateBuffer(vb, 0, mesh.Vertices);
            var ib = f.CreateBuffer(new GpuBufferDescription((uint)(mesh.Indices.Length * sizeof(ushort)), GpuBufferUsage.IndexBuffer));
            _gd.UpdateBuffer(ib, 0, mesh.Indices);

            int index = _skinnedSlots.Alloc(out int generation);
            var entry = new SkinnedMeshEntry(vb, ib, mesh.Indices.Length, material, mesh.InverseBind);
            if (index < _skinnedMeshes.Count) _skinnedMeshes[index] = entry;
            else _skinnedMeshes.Add(entry);
            return new SkinnedMeshHandle(index, generation);
        }

        /// <summary>Queue one skinned draw. <paramref name="boneMatrices"/> are this frame's joint world
        /// transforms (model space), one per bone in the mesh's skin; the engine composes them with the mesh's
        /// inverse-bind. Passing the mesh's <see cref="SkinnedGltfMesh.RestPose"/> yields no deformation.
        /// Presentation only - never feed sim/RNG/netcode from bone state.</summary>
        public void DrawSkinned(SkinnedMeshHandle h, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 model, Color tint)
            => DrawSkinned(h, boneMatrices, model, tint, Material.None);

        /// <summary>As <see cref="DrawSkinned(SkinnedMeshHandle,ReadOnlySpan{Matrix4x4},Matrix4x4,Color)"/> with an
        /// explicit <paramref name="material"/> (emissive + specular).</summary>
        public void DrawSkinned(SkinnedMeshHandle h, ReadOnlySpan<Matrix4x4> boneMatrices, Matrix4x4 model, Color tint, Material material)
        {
            if (!_skinnedSlots.IsValid(h.Index, h.Generation)) return;          // stale/default handle: skip
            var entry = _skinnedMeshes[h.Index];
            if (entry is null) return;
            uint boneOffset = ComposeBonesInto(_boneMatrices, boneMatrices, entry.InverseBind);
            _skinnedInstances.Add(h, model, tint, material, boneOffset);
        }

        /// <summary>Free a skinned mesh's GPU buffers and release its slot. A <c>default</c> handle is a no-op; a
        /// stale handle throws.</summary>
        public void UnloadSkinnedMesh(SkinnedMeshHandle h)
        {
            if (h.Generation == 0) return;
            _skinnedSlots.Free(h.Index, h.Generation);
            var m = _skinnedMeshes[h.Index];
            if (m is { } e) { e.Vb.Dispose(); e.Ib.Dispose(); e.MaterialSet?.Dispose(); }
            _skinnedMeshes[h.Index] = null;
        }

        /// <summary>Skinned draws queued this frame. Internal: lets tests assert Begin clears the queue.</summary>
        internal int SkinnedInstanceCount => _skinnedInstances.Items.Count;
```

(e) In `Begin()`, add the skinned clears:

```csharp
            _skinnedInstances.Begin();
            _boneMatrices.Clear();
```

(f) In `RenderInternal`, render the skinned pass right after the rigid instanced draws (after the rigid `foreach (var run in _runs)` block, before `DrawTexturedBillboards`):

```csharp
            // Skinned pass: same model framebuffer + frame UBO, separate pipeline. Upload this frame's whole bone
            // palette once, then one instanced draw per unique skinned mesh (instances read their own bone range).
            var skinnedItems = _skinnedInstances.Items;
            if (skinnedItems.Count > 0)
            {
                SkinnedModelRenderer.GroupSkinnedInstances(skinnedItems, _skinnedInstanceData, _skinnedRuns);
                _skinnedModel.UploadBones(cl, CollectionsMarshal.AsSpan(_boneMatrices));
                _skinnedModel.UploadInstances(cl, CollectionsMarshal.AsSpan(_skinnedInstanceData));
                _skinnedModel.BindPass(cl);
                foreach (var run in _skinnedRuns)
                {
                    if (!_skinnedSlots.IsValid(run.Mesh.Index, run.Mesh.Generation)) continue;
                    var m = _skinnedMeshes[run.Mesh.Index];
                    if (m is not { } entry) continue;
                    _skinnedModel.DrawSkinnedMeshInstanced(cl, entry.Vb, entry.Ib, entry.IndexCount, run.Start, run.Count, entry.MaterialSet);
                }
            }
```

(g) In `Dispose()`, add:

```csharp
            _skinnedModel.Dispose();
            foreach (var m in _skinnedMeshes)
                if (m is { } e) { e.Vb.Dispose(); e.Ib.Dispose(); e.MaterialSet?.Dispose(); }
```

- [ ] **Step 6: Build and run the full test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS, including all skinned tests from Tasks 1-8. The material default is `Material.None` and the fields are `Material.Emissive/Specular/Shininess` (confirmed in `KhaozEngine.Render3D/Material.cs`); `GroupSkinnedInstances` mirrors the rigid `GroupInstances` at Scene3D.cs:709-715.

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Render3D/Scene3D.cs KhaozEngine.Tests/Render3D/Scene3DSkinnedQueueTests.cs
git commit -m "render3d(skinned): Scene3D LoadSkinnedMesh/DrawSkinned/UnloadSkinnedMesh + skinned render pass"
```

---

## Task 9: GPU smoke test (pipeline compiles + skinned draw runs + actually deforms)

A device-backed test that the skinned pipeline builds (GLSL cross-compiles), a skinned draw executes, and a bent pose visibly differs from the rest pose. Uses the same `[GpuFact]` + `GpuDeviceContext.CreateHeadless()` + `Render3DPreview` harness as `Render3DPreviewGpuTests`. `[GpuFact]` auto-skips unless `KE_GPU_TESTS=1` is set, so default `dotnet test` and CI skip it; the dev Mac runs it with `KE_GPU_TESTS=1`.

**Files:**
- Test: `KhaozEngine.Tests/Gpu/Render3DSkinnedGpuTests.cs`

- [ ] **Step 1: Write the smoke test**

This mirrors `Render3DPreviewGpuTests` exactly: `GpuDeviceContext.CreateHeadless()` → `Render3DPreview(gd, W, H)` → `preview.Capture(scene => ...)` → `GpuReadback.ToRgba`. `Render3DPreview.Scene` is the `Scene3D`; `Capture` runs `Begin` + the draw callback + the render and returns a sampleable `Texture2D`.

```csharp
using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Exercises the skinned pipeline on a live headless device: proves SkinnedModelVert cross-compiles, the bone
    // SSBO binds at set 1, the instanced skinned draw runs, and a bent pose deforms the mesh (a bent capture
    // differs from the rest-pose capture). Skipped unless KE_GPU_TESTS=1.
    public sealed class Render3DSkinnedGpuTests
    {
        const int W = 128, H = 128;

        [GpuFact]
        public void SkinnedTube_Renders_AndBendingDeformsIt()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;

            using var preview = new Render3DPreview(gd, W, H);
            SkinnedMeshHandle h = preview.Scene.LoadSkinnedMesh(SkinnedMeshBuilder.BuildTube(0.5f, 4f, 10, 10, 6, Axis.Z));
            // Frame the camera on the tube (it runs 0..4 along Z, centred ~ (0,0,2)).
            preview.Scene.Camera.Frame(new Vector3(0, 0, 2f), new Vector3(4f, 4f, 5f));

            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 10, 10, 6, Axis.Z); // same layout, for poses
            int last = tube.BoneCount - 1;
            var o = tube.RestPose[last].Translation;
            var bent = (Matrix4x4[])tube.RestPose.Clone();
            bent[last] = Matrix4x4.CreateTranslation(-o) * Matrix4x4.CreateRotationX(1.2f) * Matrix4x4.CreateTranslation(o) * tube.RestPose[last];

            Texture2D restTex = preview.Capture(scene =>
                scene.DrawSkinned(h, tube.RestPose, Matrix4x4.Identity, new Color(0.8f, 0.4f, 0.3f, 1f)));
            byte[] rest = GpuReadback.ToRgba(gd, restTex.Handle, W, H);

            Texture2D bentTex = preview.Capture(scene =>
                scene.DrawSkinned(h, bent, Matrix4x4.Identity, new Color(0.8f, 0.4f, 0.3f, 1f)));
            byte[] bentPixels = GpuReadback.ToRgba(gd, bentTex.Handle, W, H);

            // The tube renders (some opaque pixels), and bending changes the silhouette (the two frames differ).
            int opaque = 0, diff = 0;
            for (int i = 0; i < rest.Length; i += 4)
            {
                if (rest[i + 3] > 200) opaque++;
                if (Math.Abs(rest[i + 3] - bentPixels[i + 3]) > 32) diff++;
            }
            Assert.True(opaque > 100, $"skinned tube should render opaque pixels, got {opaque}");
            Assert.True(diff > 50, $"bending should change the silhouette vs rest pose, differing pixels {diff}");

            preview.Scene.UnloadSkinnedMesh(h);
        }
    }
}
```

If `Render3DPreview.Capture` calls `Begin` internally (as the preview path does), do not call `Begin` in the callback. If a compile error shows `Camera.Frame` has a different signature, match the call already used in `Render3DPreviewGpuTests` (`Camera.Frame(center, size)`).

- [ ] **Step 2: Run the smoke test (on a GPU-capable machine)**

Run: `KE_GPU_TESTS=1 dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render3DSkinnedGpuTests"`
Expected: PASS on the dev Mac. Without `KE_GPU_TESTS=1` it is SKIPPED (so CI stays green headless).

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Tests/Gpu/Render3DSkinnedGpuTests.cs
git commit -m "render3d(skinned): GPU smoke test (pipeline compiles, skinned instanced draw runs)"
```

---

## Task 10: Docs (usage + determinism + remove the "no skeletal animation" claims)

**Files:**
- Modify: `docs/USING-KHAOZENGINE.md`

- [ ] **Step 1: Find and qualify the stale claims**

Run: `grep -rn -i "skeletal\|vertex animation\|no.*animation\|rigid mesh" docs/USING-KHAOZENGINE.md`
For each statement asserting the engine has no skeletal/vertex animation, replace it with the new capability (runtime bone-palette skinning; no required glTF keyframe tracks). If `docs/ROADMAP.md` or `docs/CONSUMERS.md` carry the same claim, qualify those too (grep them the same way).

- [ ] **Step 2: Add the usage section**

Add a "Skinned / deformable meshes" section to `docs/USING-KHAOZENGINE.md`:

```markdown
## Skinned / deformable meshes (runtime bone control)

Render3D supports GPU bone-palette skinning for organic, code-driven deformation (tentacles,
limbs, cables, soft-body) without authored animation tracks. One skinned draw replaces many
rigid-segment draws.

```csharp
// Procedural: a tube weighted to a bone chain.
SkinnedGltfMesh tube = SkinnedMeshBuilder.BuildTube(radius: 0.4f, length: 5f,
    ringSegments: 12, radialSegments: 8, boneCount: 8, axis: Axis.Z);
SkinnedMeshHandle h = scene.LoadSkinnedMesh(tube, albedoTex);

// Or load an authored rig (reads JOINTS_0/WEIGHTS_0 + inverse-bind; embedded images ignored):
// SkinnedMeshHandle h = scene.LoadSkinnedMesh(GltfLoader.LoadSkinned("creature.glb"), albedoTex);

// Each frame: supply one joint world transform per bone (model space). Passing tube.RestPose
// gives no deformation. A chain of points can be turned into frames with PolylineFrames.Build.
scene.Begin();
scene.DrawSkinned(h, boneMatrices, model: Matrix4x4.Identity, tint: Color.White);
```

The bone matrices are joint world transforms; the engine composes them with the mesh's
inverse-bind. Skinning rewrites position and normal only, so the lit colour path
(`albedo = vColor * vTint * texRgb`), tint, and texture semantics are unchanged.

**Determinism: presentation only.** Bone matrices and `DrawSkinned` must never feed
simulation, RNG, or netcode. Skinning is a render-time visual; drive bones from already-computed
gameplay state, not the reverse.
```

- [ ] **Step 3: Verify docs build/lint if applicable, then commit**

Run: `grep -rn -i "no skeletal" docs/` (expected: no stale "no skeletal animation" claims remain)

```bash
git add docs/USING-KHAOZENGINE.md docs/ROADMAP.md docs/CONSUMERS.md
git commit -m "docs(skinned): document runtime skinned meshes; drop stale no-skeletal-animation claims"
```

(Only `git add` the doc files that actually changed.)

---

## Task 11: Release ritual (version bump, changelog, pack, tag)

Follow the engine release ritual in order. Additive feature → minor bump: 7.9.0 → **7.10.0**.

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `CHANGENOTES.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<KhaozEngineVersion>7.9.0</KhaozEngineVersion>` to `7.10.0`.

- [ ] **Step 2: Add the CHANGELOG.md entry (newest-first, detailed)**

Add at the top of the entries:

```markdown
## 7.10.0

Runtime skinned / deformable mesh support in Render3D. New `Scene3D.LoadSkinnedMesh` /
`DrawSkinned` / `UnloadSkinnedMesh` add GPU bone-palette skinning: a smooth mesh bends under
pure code control (tentacles, limbs, cables, soft-body), one skinned draw replacing many rigid
segments. `SkinnedMeshBuilder.BuildTube` generates a procedural tube weighted to a bone chain;
`GltfLoader.LoadSkinned` reads authored glb rigs (JOINTS_0/WEIGHTS_0 + inverse-bind, embedded
images still ignored); `PolylineFrames.Build` turns a chain of points into joint transforms.
`DrawSkinned` takes per-frame joint world transforms (the mesh's `RestPose` = no deform) and
composes them with the skin's inverse-bind. Every skinned draw's bones share one growable
structured buffer, indexed per-instance, so instances of one mesh draw in a single instanced
call. Skinning rewrites position + normal only; the lit colour path
(`albedo = vColor * vTint * texRgb`), tint, and texture semantics are unchanged. New types:
`SkinnedVertex`, `SkinnedGltfMesh`, `SkinnedMeshHandle`, `Axis`, `SkinningMath` (pure,
headless-testable). Presentation only: must not touch sim/RNG/netcode.
```

- [ ] **Step 3: Add the CHANGENOTES.md digest line (newest-first, one or two sentences)**

```markdown
- 7.10.0: Runtime skinned/deformable meshes in Render3D (GPU bone-palette skinning) - Scene3D.LoadSkinnedMesh/DrawSkinned, procedural tube builder + glb rig loader, code-driven bone control for tentacles/limbs/cables. Presentation only.
```

- [ ] **Step 4: Update the three guard-checked version declarations**

- `docs/CONSUMERS.md`: set the "Engine current version" line to 7.10.0.
- `docs/ROADMAP.md`: set "Current released version" to 7.10.0.
- `README.md`: bump the `<PackageReference ... Version="..."/>` example to 7.10.0.

Run the guard: `bash scripts/check-doc-versions.sh`
Expected: passes (all three match `<KhaozEngineVersion>`).

- [ ] **Step 5: Full test suite green, then pack**

```bash
dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj
dotnet pack -c Release -o ./local-feed
```

Expected: tests pass; pack writes `KhaozEngine.Render3D.7.10.0.nupkg` (and the rest of the shared-version packages) into `local-feed`.

- [ ] **Step 6: Commit the release in one commit**

```bash
git add Directory.Build.props CHANGELOG.md CHANGENOTES.md docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "render3d(7.10.0): runtime skinned/deformable mesh support"
```

- [ ] **Step 7: Hand off the tag/push to the finish step**

Do NOT tag/push here. Tagging `v7.10.0` and pushing `main` + the tag happen after the branch merges to `main` in the finish step (per the engine ritual), so the tag lands on the integrated commit. Report that the branch is ready to merge.

---

## Done criteria

- `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` passes (GPU smoke test skipped where no device).
- `bash scripts/check-doc-versions.sh` passes at 7.10.0.
- `local-feed/` has the 7.10.0 packages.
- The "no skeletal animation" claims are gone from the docs.
- Branch ready to merge to `main`, then tag `v7.10.0` + push (main + tag) in the finish step.

## Post-merge consumer follow-up (separate SpaceGame chat, not part of this plan)

SpaceGame pins 7.10.0, then replaces the 8x8 rigid cone-segment draws in `QueueEnemyMeshes`
(MultiplayerReplicatedRunView) with a single skinned tentacle mesh driven by the existing
`SlathTentacleLayout` chain (its per-segment transforms become the `boneMatrices` span).
