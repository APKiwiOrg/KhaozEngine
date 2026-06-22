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
