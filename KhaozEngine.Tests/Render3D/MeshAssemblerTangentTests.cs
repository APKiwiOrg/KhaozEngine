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
