using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class DebugShapesTests
    {
        const float Eps = 1e-4f;

        [Fact]
        public void Box_Adds24Endpoints_Forming12AxisAlignedEdges()
        {
            var segs = new List<Vector3>();
            var center = new Vector3(1, 2, 3);
            var size = new Vector3(2, 4, 6);
            DebugShapes.Box(segs, center, size);

            Assert.Equal(24, segs.Count);
            Assert.Equal(0, segs.Count % 2); // even

            // The 8 axis-aligned corners must all appear among the endpoints.
            Vector3 h = size * 0.5f;
            foreach (int sx in new[] { -1, 1 })
            foreach (int sy in new[] { -1, 1 })
            foreach (int sz in new[] { -1, 1 })
            {
                var corner = center + new Vector3(sx * h.X, sy * h.Y, sz * h.Z);
                Assert.Contains(segs, p => Vector3.Distance(p, corner) < Eps);
            }

            // Every edge is axis-aligned: exactly one component differs between its two endpoints.
            for (int i = 0; i < segs.Count; i += 2)
            {
                Vector3 d = Vector3.Abs(segs[i + 1] - segs[i]);
                int differing = (d.X > Eps ? 1 : 0) + (d.Y > Eps ? 1 : 0) + (d.Z > Eps ? 1 : 0);
                Assert.Equal(1, differing);
            }
        }

        [Fact]
        public void Grid_AddsExpectedCount_OnXZPlaneAtCenterY()
        {
            var segs = new List<Vector3>();
            var center = new Vector3(0, 5, 0);
            int cells = 4;
            DebugShapes.Grid(segs, center, cellSize: 2f, cells: cells);

            // (cells+1) lines each direction, 2 endpoints per line, 2 directions.
            int expected = (cells + 1) * 2 /*two directions*/ * 2 /*endpoints*/;
            Assert.Equal(expected, segs.Count);
            Assert.Equal(0, segs.Count % 2);

            // All endpoints lie on the XZ plane at center.Y.
            Assert.All(segs, p => Assert.True(Math.Abs(p.Y - center.Y) < Eps));
        }

        [Fact]
        public void Circle_AddsSegmentsTimesTwo_AllAtRadius_InPlanePerpendicularToNormal()
        {
            var segs = new List<Vector3>();
            var center = new Vector3(2, 1, -3);
            var normal = Vector3.UnitY;
            float radius = 4f;
            int segments = 16;
            DebugShapes.Circle(segs, center, normal, radius, segments);

            Assert.Equal(segments * 2, segs.Count);
            Assert.Equal(0, segs.Count % 2);

            var n = Vector3.Normalize(normal);
            foreach (var p in segs)
            {
                Vector3 rel = p - center;
                Assert.True(Math.Abs(rel.Length() - radius) < Eps, $"radius off: {rel.Length()}");
                // In the plane perpendicular to normal => component along normal is ~0.
                Assert.True(Math.Abs(Vector3.Dot(rel, n)) < Eps, "endpoint not in plane");
            }
        }

        [Fact]
        public void Axes_Adds6Endpoints_ThreeUnitScaledAxesFromOrigin()
        {
            var segs = new List<Vector3>();
            var origin = new Vector3(1, 1, 1);
            float scale = 3f;
            DebugShapes.Axes(segs, origin, scale);

            Assert.Equal(6, segs.Count);
            Assert.Equal(0, segs.Count % 2);

            // Each axis starts at origin and ends scale units along its axis.
            Assert.Equal(origin, segs[0]);
            Assert.Equal(origin + new Vector3(scale, 0, 0), segs[1]);
            Assert.Equal(origin, segs[2]);
            Assert.Equal(origin + new Vector3(0, scale, 0), segs[3]);
            Assert.Equal(origin, segs[4]);
            Assert.Equal(origin + new Vector3(0, 0, scale), segs[5]);
        }

        [Fact]
        public void AllBuilders_ProduceEvenEndpointCounts()
        {
            var segs = new List<Vector3>();
            DebugShapes.Box(segs, Vector3.Zero, Vector3.One);
            DebugShapes.Grid(segs, Vector3.Zero, 1f, 3);
            DebugShapes.Circle(segs, Vector3.Zero, Vector3.UnitZ, 1f, 8);
            DebugShapes.Axes(segs, Vector3.Zero, 1f);
            Assert.Equal(0, segs.Count % 2);
        }
    }
}
