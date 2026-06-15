using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class BillboardGeometryTests
    {
        const float Eps = 1e-5f;

        static readonly Vector3 Right = Vector3.UnitX;
        static readonly Vector3 Up = Vector3.UnitY;

        [Fact]
        public void Corners_AreCenteredAndSizeScaled()
        {
            var center = new Vector3(2f, 3f, 4f);
            const float size = 5f;
            BillboardGeometry.Corners(center, size, Right, Up, out var bl, out var br, out var tl, out var tr);

            Assert.Equal(center - Right * size - Up * size, bl);
            Assert.Equal(center + Right * size - Up * size, br);
            Assert.Equal(center - Right * size + Up * size, tl);
            Assert.Equal(center + Right * size + Up * size, tr);

            // Centroid of the 4 corners is exactly `center`.
            var centroid = (bl + br + tl + tr) * 0.25f;
            Assert.True(Vector3.Distance(centroid, center) < Eps);
        }

        [Fact]
        public void Corners_AreSizeScaled_HalfExtentEqualsSizeOnEachAxis()
        {
            var center = Vector3.Zero;
            const float size = 3f;
            BillboardGeometry.Corners(center, size, Right, Up, out var bl, out _, out _, out var tr);

            // Diagonal spans 2*size along right and 2*size along up.
            Assert.Equal(2f * size, tr.X - bl.X, 4);
            Assert.Equal(2f * size, tr.Y - bl.Y, 4);
        }

        [Fact]
        public void Triangles_QuadIsPlanar_OnTheRightUpPlane()
        {
            var center = new Vector3(1f, 1f, 7f);   // plane normal is +Z here (right=X, up=Y)
            const float size = 2f;
            Span<Vector3> pos = stackalloc Vector3[6];
            Span<Vector2> uv = stackalloc Vector2[6];
            int n = BillboardGeometry.Triangles(center, size, Right, Up, pos, uv);

            Assert.Equal(6, n);
            var normal = Vector3.Normalize(Vector3.Cross(Right, Up));
            foreach (var p in pos)
            {
                // every vertex lies on the plane through center with that normal
                float dist = Vector3.Dot(p - center, normal);
                Assert.True(MathF.Abs(dist) < Eps, $"vertex off-plane by {dist}");
            }
        }

        [Fact]
        public void Triangles_UvsSpanUnitSquare()
        {
            Span<Vector3> pos = stackalloc Vector3[6];
            Span<Vector2> uv = stackalloc Vector2[6];
            BillboardGeometry.Triangles(Vector3.Zero, 1f, Right, Up, pos, uv);

            float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
            foreach (var t in uv)
            {
                minU = MathF.Min(minU, t.X); maxU = MathF.Max(maxU, t.X);
                minV = MathF.Min(minV, t.Y); maxV = MathF.Max(maxV, t.Y);
            }
            Assert.Equal(0f, minU, 5);
            Assert.Equal(0f, minV, 5);
            Assert.Equal(1f, maxU, 5);
            Assert.Equal(1f, maxV, 5);
        }

        [Fact]
        public void Triangles_UvCornersMatchPositionCorners()
        {
            // The (0,0) UV must sit on bottom-left, (1,1) on top-right, etc.
            const float size = 4f;
            BillboardGeometry.Corners(Vector3.Zero, size, Right, Up, out var bl, out var br, out var tl, out var tr);
            Span<Vector3> pos = stackalloc Vector3[6];
            Span<Vector2> uv = stackalloc Vector2[6];
            BillboardGeometry.Triangles(Vector3.Zero, size, Right, Up, pos, uv);

            for (int i = 0; i < 6; i++)
            {
                Vector3 expected = uv[i] switch
                {
                    var u when u == BillboardGeometry.UvBL => bl,
                    var u when u == BillboardGeometry.UvBR => br,
                    var u when u == BillboardGeometry.UvTL => tl,
                    var u when u == BillboardGeometry.UvTR => tr,
                    _ => throw new Xunit.Sdk.XunitException($"unexpected uv {uv[i]}")
                };
                Assert.True(Vector3.Distance(pos[i], expected) < Eps);
            }
        }

        [Fact]
        public void Triangles_ThrowsWhenSpanTooSmall()
        {
            Assert.Throws<ArgumentException>(() =>
            {
                Span<Vector3> pos = new Vector3[5];
                Span<Vector2> uv = new Vector2[6];
                BillboardGeometry.Triangles(Vector3.Zero, 1f, Right, Up, pos, uv);
            });
            Assert.Throws<ArgumentException>(() =>
            {
                Span<Vector3> pos = new Vector3[6];
                Span<Vector2> uv = new Vector2[5];
                BillboardGeometry.Triangles(Vector3.Zero, 1f, Right, Up, pos, uv);
            });
        }

        [Theory]
        [InlineData(0.3f, -1f, 0.6f)]    // a generic look direction
        [InlineData(-0.7f, 0.2f, -0.5f)]
        [InlineData(0f, 0f, -1f)]        // straight ahead
        public void CameraBasis_ReturnsOrthonormalBasisPerpendicularToForward(float fx, float fy, float fz)
        {
            var forward = Vector3.Normalize(new Vector3(fx, fy, fz));
            BillboardGeometry.CameraBasis(forward, out var right, out var up);

            Assert.Equal(1f, right.Length(), 4);
            Assert.Equal(1f, up.Length(), 4);
            Assert.Equal(0f, Vector3.Dot(right, up), 4);
            Assert.Equal(0f, Vector3.Dot(right, forward), 4);
            Assert.Equal(0f, Vector3.Dot(up, forward), 4);
        }

        [Theory]
        [InlineData(0f, 1f, 0f)]     // forward straight up == UnitY
        [InlineData(0f, -1f, 0f)]    // straight down == -UnitY
        public void CameraBasis_HandlesForwardParallelToUnitY_WithoutNaN(float fx, float fy, float fz)
        {
            var forward = new Vector3(fx, fy, fz);
            BillboardGeometry.CameraBasis(forward, out var right, out var up);

            foreach (var c in new[] { right.X, right.Y, right.Z, up.X, up.Y, up.Z })
                Assert.False(float.IsNaN(c), "basis contained NaN");

            Assert.Equal(1f, right.Length(), 4);
            Assert.Equal(1f, up.Length(), 4);
            Assert.Equal(0f, Vector3.Dot(right, up), 4);
            // up should remain perpendicular to forward even in the fallback.
            Assert.Equal(0f, Vector3.Dot(up, Vector3.Normalize(forward)), 4);
        }
    }
}
