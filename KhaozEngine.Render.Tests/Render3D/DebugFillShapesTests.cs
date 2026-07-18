using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class DebugFillShapesTests
    {
        const float Eps = 1e-4f;

        // Geometric normal of triangle (a, b, c).
        static Vector3 TriNormal(Vector3 a, Vector3 b, Vector3 c) => Vector3.Cross(b - a, c - a);

        [Fact]
        public void FilledQuad_XZ_Emits2Triangles_WithCorrectCornersAndExtents()
        {
            var tris = new List<Vector3>();
            var center = new Vector3(2, 0.05f, -3);
            var half = new Vector2(1.5f, 0.5f);
            // XZ ground quad: normal +Y, u along +X. v = cross(+Y,+X) = -Z, so .Y extends along Z.
            DebugFillShapes.FilledQuad(tris, center, Vector3.UnitY, Vector3.UnitX, half);

            Assert.Equal(6, tris.Count);             // 2 triangles
            Assert.Equal(0, tris.Count % 3);

            // All four expected corners must appear among the 6 vertices, at the right extents, coplanar in Y.
            foreach (int sx in new[] { -1, 1 })
            foreach (int sz in new[] { -1, 1 })
            {
                var corner = center + new Vector3(sx * half.X, 0, sz * half.Y);
                Assert.Contains(tris, p => Vector3.Distance(p, corner) < Eps);
            }
            Assert.All(tris, p => Assert.True(Math.Abs(p.Y - center.Y) < Eps, "vertex left the XZ plane"));
        }

        [Fact]
        public void FilledQuad_XZ_BothTrianglesWoundCCWAboutNormal()
        {
            var tris = new List<Vector3>();
            DebugFillShapes.FilledQuad(tris, Vector3.Zero, Vector3.UnitY, Vector3.UnitX, new Vector2(1f, 1f));

            // Each triangle's geometric normal points along +Y (consistent winding, not back-to-back).
            for (int i = 0; i < tris.Count; i += 3)
            {
                Vector3 n = Vector3.Normalize(TriNormal(tris[i], tris[i + 1], tris[i + 2]));
                Assert.True(Vector3.Dot(n, Vector3.UnitY) > 0.99f, $"triangle {i / 3} wound the wrong way: {n}");
            }
        }

        [Fact]
        public void FilledQuad_GeneralPlane_StaysInPlaneAndWindsToNormal()
        {
            var tris = new List<Vector3>();
            var center = new Vector3(1, 2, 3);
            var normal = Vector3.Normalize(new Vector3(0.3f, 1f, -0.4f));
            DebugFillShapes.FilledQuad(tris, center, normal, Vector3.UnitX, new Vector2(2f, 0.75f));

            Assert.Equal(6, tris.Count);
            // Every vertex lies in the plane: (p - center) is perpendicular to the normal.
            Assert.All(tris, p => Assert.True(Math.Abs(Vector3.Dot(p - center, normal)) < Eps, "vertex off plane"));
            for (int i = 0; i < tris.Count; i += 3)
            {
                Vector3 n = Vector3.Normalize(TriNormal(tris[i], tris[i + 1], tris[i + 2]));
                Assert.True(Vector3.Dot(n, normal) > 0.99f, "winding not aligned to plane normal");
            }
        }

        [Theory]
        [InlineData(3)]
        [InlineData(16)]
        [InlineData(32)]
        public void FilledCircle_EmitsSegmentsTimesThreeVertices_AllOnRadiusOrCenter(int segments)
        {
            var tris = new List<Vector3>();
            var center = new Vector3(-1, 0.02f, 4);
            float radius = 2.5f;
            DebugFillShapes.FilledCircle(tris, center, Vector3.UnitY, radius, segments);

            Assert.Equal(segments * 3, tris.Count);  // a fan of `segments` triangles
            Assert.Equal(0, tris.Count % 3);

            // Every triangle is (center, rim, rim): vertex 0 of each is the centre, the other two are on the rim.
            for (int i = 0; i < tris.Count; i += 3)
            {
                Assert.True(Vector3.Distance(tris[i], center) < Eps, "fan apex is not the centre");
                Assert.True(Math.Abs((tris[i + 1] - center).Length() - radius) < Eps, "rim vertex off radius");
                Assert.True(Math.Abs((tris[i + 2] - center).Length() - radius) < Eps, "rim vertex off radius");
                Assert.True(Math.Abs(tris[i + 1].Y - center.Y) < Eps && Math.Abs(tris[i + 2].Y - center.Y) < Eps,
                    "rim left the XZ plane");
            }
        }

        // A square rim on the XZ plane wound so each (center, rim[i], rim[i+1]) triangle (and the wrap) faces +Y.
        static List<Vector3> SquareRim() => new()
        {
            new Vector3(1, 0, 0), new Vector3(0, 0, -1), new Vector3(-1, 0, 0), new Vector3(0, 0, 1),
        };

        [Fact]
        public void FilledFan_Open_EmitsRimMinusOneTriangles_AsCenterRimRim()
        {
            var tris = new List<Vector3>();
            var center = new Vector3(2, 0.05f, -1);
            var rim = SquareRim();
            DebugFillShapes.FilledFan(tris, center, rim, closed: false);

            Assert.Equal((rim.Count - 1) * 3, tris.Count);   // 3 triangles, no wrap edge
            for (int i = 0; i < rim.Count - 1; i++)
            {
                Assert.Equal(center, tris[i * 3]);           // apex
                Assert.Equal(rim[i], tris[i * 3 + 1]);
                Assert.Equal(rim[i + 1], tris[i * 3 + 2]);
            }
        }

        [Fact]
        public void FilledFan_Closed_AddsWrapTriangle()
        {
            var tris = new List<Vector3>();
            var center = Vector3.Zero;
            var rim = SquareRim();
            DebugFillShapes.FilledFan(tris, center, rim, closed: true);

            Assert.Equal(rim.Count * 3, tris.Count);         // 4 triangles incl. the wrap
            int last = tris.Count - 3;                        // the wrap triangle (center, rim[^1], rim[0])
            Assert.Equal(center, tris[last]);
            Assert.Equal(rim[^1], tris[last + 1]);
            Assert.Equal(rim[0], tris[last + 2]);
        }

        [Fact]
        public void FilledFan_WindsCCWAboutNormal()
        {
            var tris = new List<Vector3>();
            DebugFillShapes.FilledFan(tris, Vector3.Zero, SquareRim(), closed: true);

            for (int i = 0; i < tris.Count; i += 3)
            {
                Vector3 n = Vector3.Normalize(TriNormal(tris[i], tris[i + 1], tris[i + 2]));
                Assert.True(Vector3.Dot(n, Vector3.UnitY) > 0.99f, $"fan triangle {i / 3} wound the wrong way: {n}");
            }
        }

        [Fact]
        public void Degenerate_Inputs_AppendNothing()
        {
            var tris = new List<Vector3>();
            DebugFillShapes.FilledQuad(tris, Vector3.Zero, Vector3.Zero, Vector3.UnitX, Vector2.One);       // zero normal
            DebugFillShapes.FilledQuad(tris, Vector3.Zero, Vector3.UnitY, Vector3.UnitY, Vector2.One);      // uAxis ∥ normal
            DebugFillShapes.FilledCircle(tris, Vector3.Zero, Vector3.UnitY, 1f, 2);                          // too few segments
            DebugFillShapes.FilledCircle(tris, Vector3.Zero, Vector3.Zero, 1f, 16);                          // zero normal
            DebugFillShapes.FilledFan(tris, Vector3.Zero, new List<Vector3>(), closed: true);               // empty rim
            DebugFillShapes.FilledFan(tris, Vector3.Zero, new List<Vector3> { Vector3.UnitX }, closed: true);// single rim point
            Assert.Empty(tris);
        }
    }
}
