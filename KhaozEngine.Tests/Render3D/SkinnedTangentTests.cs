using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Tangents on the skinned PBR-lite path: the procedural tube computes real (non-zero, unit) tangents
    /// from its UVs, and the no-tangent skinned stream deforms byte-identically to the pre-tangent geometry.</summary>
    public class SkinnedTangentTests
    {
        [Fact]
        public void BuildTube_ComputesFiniteUnitTangents()
        {
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 10, 10, 6, Axis.Z);
            int nonZero = 0;
            foreach (var v in tube.Vertices)
            {
                var t = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
                Assert.True(float.IsFinite(t.X) && float.IsFinite(t.Y) && float.IsFinite(t.Z) && float.IsFinite(v.Tangent.W),
                    "tangent has a non-finite component");
                float len = t.Length();
                Assert.True(len < 1e-4f || (len > 0.99f && len < 1.01f), $"tangent neither zero nor unit: {len}");
                if (len > 0.99f)
                {
                    nonZero++;
                    Assert.True(v.Tangent.W == 1f || v.Tangent.W == -1f, $"handedness must be +/-1, got {v.Tangent.W}");
                    // Tangent is orthogonal to the (radial) normal after Gram-Schmidt.
                    Assert.True(MathF.Abs(Vector3.Dot(Vector3.Normalize(t), Vector3.Normalize(v.Normal))) < 1e-3f,
                        "tangent should be orthogonal to the normal");
                }
            }
            Assert.True(nonZero > 0, "the tube has UVs, so most vertices should carry a real tangent");
        }

        // The no-normal-map skinned mesh (zero tangents) must CPU-skin to a ModelVertex stream byte-identical to
        // building the same stream WITHOUT any tangent field set. Guards "zero-tangent skinned path stays
        // byte-identical to 7.27.0 output": each deformed vertex carries a zero tangent and the pre-tangent
        // position/normal/color/uv.
        [Fact]
        public void ZeroTangentSkinnedStream_DeformsByteIdenticalToPreTangentGeometry()
        {
            // A tiny 2-vertex skinned mesh with NO tangents (Tangent left default = zero).
            var src = new[]
            {
                new SkinnedVertex
                {
                    Position = new Vector3(1, 0, 2), Normal = Vector3.UnitY, Color = new Vector4(0.6f, 0.2f, 0.1f, 1f),
                    Uv = new Vector2(0.1f, 0.2f), BoneIndices = new Vector4(0, 0, 0, 0), BoneWeights = new Vector4(1, 0, 0, 0),
                },
                new SkinnedVertex
                {
                    Position = new Vector3(-1, 1, 0), Normal = Vector3.UnitZ, Color = new Vector4(0.2f, 0.7f, 0.3f, 1f),
                    Uv = new Vector2(0.9f, 0.4f), BoneIndices = new Vector4(0, 0, 0, 0), BoneWeights = new Vector4(1, 0, 0, 0),
                },
            };
            var bones = new[] { Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateTranslation(2, -1, 3) };

            foreach (var v in src)
            {
                ModelVertex got = SkinningMath.SkinVertex(v, bones);
                // Expected: the exact pre-tangent deform (position point-transform, normal direction-transform
                // re-normalized), and a zero tangent.
                Vector3 p = Vector3.Transform(v.Position, bones[0]);
                Vector3 nn = Vector3.TransformNormal(v.Normal, bones[0]);
                nn = nn.LengthSquared() > 1e-12f ? Vector3.Normalize(nn) : v.Normal;
                var expect = new ModelVertex(p, nn, v.Color, v.Uv); // 4-arg ctor => zero tangent

                Assert.Equal(expect.Position, got.Position);
                Assert.Equal(expect.Normal, got.Normal);
                Assert.Equal(expect.Color, got.Color);
                Assert.Equal(expect.Uv, got.Uv);
                Assert.Equal(expect.Tangent, got.Tangent);   // both Vector4.Zero
                Assert.Equal(Vector4.Zero, got.Tangent);
            }
        }
    }
}
