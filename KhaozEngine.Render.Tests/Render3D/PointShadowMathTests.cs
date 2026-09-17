using System;
using System.Numerics;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure (device-free) tests for the omnidirectional point-shadow face math
    /// (<see cref="PointShadowMath"/>): the cube-map face convention from the design doc's decision 3, the
    /// per-face view + 90 degree projection, and the atlas cell bake that places one face of one light row into
    /// the shared atlas.
    /// <para>
    /// The property every case here circles is ONE equation, because it is the only thing the pass and the
    /// receiver have to agree about: a point in direction <c>d</c> from the light must rasterize into the atlas
    /// texel that <c>AtlasUv(FaceAndUv(d))</c> names. The pass side reaches it through the baked matrix and the
    /// receiver side reaches it through the table, and <c>PointShadowPassGpuTests</c> then pins the pair on a
    /// real device by reading the atlas back.
    /// </para>
    /// <para>
    /// Clip to atlas UV is the Metal/Direct3D authored convention the whole engine writes against (see
    /// <c>GpuClip</c>): clip x +1 is the right edge and clip y +1 is the TOP edge of the render target, and a
    /// readback is row-major from the top. So <c>u = (x + 1) / 2</c> and <c>v = (1 - y) / 2</c>, which is what
    /// <see cref="AtlasUvOf"/> spells once for every case below. The backend adaptation
    /// (<c>GpuClip.Correct</c>) is applied to the finished matrix by the caller, never here.
    /// </para>
    /// </summary>
    public sealed class PointShadowMathTests
    {
        static readonly Vector3[] Axes =
        {
            Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ,
        };

        /// <summary>The atlas UV a clip-space position lands on, under the engine's authored clip convention.
        /// Returns false when the point is behind the face (w at or below zero), which is what the
        /// one-face-only case asserts about the other five.</summary>
        static bool AtlasUvOf(in Matrix4x4 m, Vector3 world, out Vector2 uv, out float depth)
        {
            Vector4 clip = Vector4.Transform(new Vector4(world, 1f), m);
            uv = default;
            depth = 0f;
            if (clip.W <= 1e-6f) return false;
            uv = new Vector2((clip.X / clip.W + 1f) * 0.5f, (1f - clip.Y / clip.W) * 0.5f);
            depth = clip.Z / clip.W;
            return true;
        }

        [Fact]
        public void EachAxisDirectionIsItsOwnFaceCentre()
        {
            for (int face = 0; face < PointShadowMath.FaceCount; face++)
            {
                PointShadowMath.FaceAndUv(Axes[face] * 7f, out int got, out Vector2 uv);
                Assert.Equal(face, got);
                Assert.Equal(0.5f, uv.X, 5);
                Assert.Equal(0.5f, uv.Y, 5);
            }
        }

        [Fact]
        public void TheTableIsTheDesignDocsTable()
        {
            // +X face: sc = -d.z = 0.5, tc = -d.y = -0.5, so u = 0.75 and v = 0.25.
            PointShadowMath.FaceAndUv(new Vector3(1f, 0.5f, -0.5f), out int face, out Vector2 uv);
            Assert.Equal(0, face);
            Assert.Equal(0.75f, uv.X, 5);
            Assert.Equal(0.25f, uv.Y, 5);
        }

        [Fact]
        public void ADegenerateDirectionAnswersTheFirstFaceCentreRatherThanNaN()
        {
            PointShadowMath.FaceAndUv(Vector3.Zero, out int face, out Vector2 uv);
            Assert.Equal(0, face);
            Assert.Equal(new Vector2(0.5f, 0.5f), uv);
        }

        [Fact]
        public void AtlasUvPlacesTheCellByColumnAndRow()
        {
            Vector2 uv = PointShadowMath.AtlasUv(2, 3, 8, new Vector2(0.5f, 0.5f));
            Assert.Equal((2 + 0.5f) / 6f, uv.X, 5);
            Assert.Equal((3 + 0.5f) / 8f, uv.Y, 5);
        }

        [Fact]
        public void TheFaceMatrixCentresItsAxisAndReachesTheEdgeAtFortyFiveDegrees()
        {
            var light = new Vector3(2f, -1f, 4f);
            Matrix4x4 m = PointShadowMath.FaceView(0, light) * PointShadowMath.FaceProjection(10f);

            Assert.True(AtlasUvOf(m, light + new Vector3(3f, 0f, 0f), out Vector2 centre, out float depth));
            Assert.Equal(0.5f, centre.X, 4);
            Assert.Equal(0.5f, centre.Y, 4);
            Assert.InRange(depth, 0f, 1f);

            // The four 45 degree corners of the +X face land exactly on the face's UV corners.
            foreach ((Vector3 offset, float u, float v) in new[]
            {
                (new Vector3(3f, 3f, 0f), 0.5f, 0f),    // tc = -d.y = -3 -> v 0
                (new Vector3(3f, -3f, 0f), 0.5f, 1f),
                (new Vector3(3f, 0f, -3f), 1f, 0.5f),   // sc = -d.z = 3 -> u 1
                (new Vector3(3f, 0f, 3f), 0f, 0.5f),
            })
            {
                Assert.True(AtlasUvOf(m, light + offset, out Vector2 uv, out _));
                Assert.Equal(u, uv.X, 4);
                Assert.Equal(v, uv.Y, 4);
            }
        }

        [Fact]
        public void TheNearAndFarPlanesSpanTheUnitDepthRange()
        {
            Matrix4x4 m = PointShadowMath.FaceView(4, Vector3.Zero) * PointShadowMath.FaceProjection(10f);
            Assert.True(AtlasUvOf(m, new Vector3(0f, 0f, PointShadowMath.NearMetres), out _, out float near));
            Assert.True(AtlasUvOf(m, new Vector3(0f, 0f, 10f), out _, out float far));
            Assert.Equal(0f, near, 4);
            Assert.Equal(1f, far, 4);
        }

        [Theory]
        [InlineData(0, 0, 1)]
        [InlineData(1, 0, 4)]
        [InlineData(2, 3, 4)]
        [InlineData(3, 7, 8)]
        [InlineData(4, 0, 2)]
        [InlineData(5, 1, 2)]
        public void TheBakedMatrixLandsOnTheUvTheTableNames(int face, int slot, int rows)
        {
            var light = new Vector3(-6f, 3f, 11f);
            Matrix4x4 m = PointShadowMath.FaceViewProjection(face, slot, rows, light, 12f);

            // A direction well inside the face, not on any axis, so the case is about the whole mapping rather
            // than about the centre.
            Vector3 dir = Vector3.Normalize(Axes[face] * 2f + Perpendicular(face) * 0.7f);
            Vector3 world = light + dir * 4f;

            PointShadowMath.FaceAndUv(dir, out int table, out Vector2 faceUv);
            Assert.Equal(face, table);
            Vector2 want = PointShadowMath.AtlasUv(face, slot, rows, faceUv);

            Assert.True(AtlasUvOf(m, world, out Vector2 got, out _));
            Assert.Equal(want.X, got.X, 4);
            Assert.Equal(want.Y, got.Y, 4);
        }

        [Fact]
        public void EveryFaceCentreLandsInItsOwnCellAndInNobodyElsesFrustum()
        {
            var light = new Vector3(1f, 2f, -3f);
            const int rows = 4;
            const int slot = 2;
            for (int face = 0; face < PointShadowMath.FaceCount; face++)
            {
                Vector3 world = light + Axes[face] * 3f;
                Matrix4x4 own = PointShadowMath.FaceViewProjection(face, slot, rows, light, 10f);
                Assert.True(AtlasUvOf(own, world, out Vector2 uv, out _));
                Vector2 want = PointShadowMath.AtlasUv(face, slot, rows, new Vector2(0.5f, 0.5f));
                Assert.Equal(want.X, uv.X, 4);
                Assert.Equal(want.Y, uv.Y, 4);

                for (int other = 0; other < PointShadowMath.FaceCount; other++)
                {
                    if (other == face) continue;
                    Matrix4x4 m = PointShadowMath.FaceView(other, light) * PointShadowMath.FaceProjection(10f);
                    bool inFront = AtlasUvOf(m, world, out Vector2 outside, out _);
                    Assert.True(!inFront || outside.X < 0f || outside.X > 1f || outside.Y < 0f || outside.Y > 1f,
                        $"face {face}'s centre direction also lands inside face {other}'s frustum at {outside}");
                }
            }
        }

        [Fact]
        public void ACellNeverLeavesItsOwnColumnAndRow()
        {
            var light = Vector3.Zero;
            const int rows = 3;
            for (int face = 0; face < PointShadowMath.FaceCount; face++)
                for (int slot = 0; slot < rows; slot++)
                {
                    Matrix4x4 m = PointShadowMath.FaceViewProjection(face, slot, rows, light, 8f);
                    // The four 45 degree corners are the widest the face reaches, so containment there is
                    // containment everywhere the scissor would otherwise have to save.
                    foreach (Vector3 corner in FaceCorners(face))
                    {
                        Assert.True(AtlasUvOf(m, light + corner, out Vector2 uv, out _));
                        Assert.InRange(uv.X, face / 6f - 1e-4f, (face + 1) / 6f + 1e-4f);
                        Assert.InRange(uv.Y, slot / (float)rows - 1e-4f, (slot + 1) / (float)rows + 1e-4f);
                    }
                }
        }

        // A unit vector perpendicular to the face axis, so a test direction leans off the centre without
        // leaving the face.
        static Vector3 Perpendicular(int face) => face is 2 or 3 ? Vector3.UnitX : Vector3.UnitY;

        // The four 45 degree corner directions of one face, at distance 3 along the face axis.
        static Vector3[] FaceCorners(int face)
        {
            Vector3 axis = Axes[face] * 3f;
            (Vector3 a, Vector3 b) = face switch
            {
                0 or 1 => (new Vector3(0f, 3f, 0f), new Vector3(0f, 0f, 3f)),
                2 or 3 => (new Vector3(3f, 0f, 0f), new Vector3(0f, 0f, 3f)),
                _ => (new Vector3(3f, 0f, 0f), new Vector3(0f, 3f, 0f)),
            };
            return new[] { axis + a + b, axis + a - b, axis - a + b, axis - a - b };
        }
    }
}
