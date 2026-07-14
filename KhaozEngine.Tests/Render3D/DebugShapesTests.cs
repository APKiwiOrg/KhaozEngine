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
        public void Circle_ClosesTheLoop_LastEndpointEqualsFirst()
        {
            var segs = new List<Vector3>();
            DebugShapes.Circle(segs, new Vector3(1, 2, 3), Vector3.UnitY, 2f, 12);
            // AddSeg appends (P0,P1),(P1,P2),...,(P11,P0): the final endpoint wraps back to the first vertex.
            Assert.True(Vector3.Distance(segs[^1], segs[0]) < Eps, "circle did not close");
        }

        [Fact]
        public void Sphere_CountsAndAllEndpointsAtRadius_WithEquatorRing()
        {
            var segs = new List<Vector3>();
            var center = new Vector3(2, 3, -1);
            float radius = 5f;
            int meridians = 12, parallels = 5, ringSegments = 32;
            DebugShapes.Sphere(segs, center, radius, meridians, parallels, ringSegments);

            // parallels rings (each ringSegments*2 endpoints) + meridians arcs (each arcSteps*2 endpoints).
            int arcSteps = Math.Max(2, ringSegments / 2);
            int expected = parallels * ringSegments * 2 + meridians * arcSteps * 2;
            Assert.Equal(expected, segs.Count);
            Assert.Equal(0, segs.Count % 2);

            // Every endpoint lies exactly on the sphere surface.
            Assert.All(segs, p => Assert.True(Math.Abs(Vector3.Distance(p, center) - radius) < Eps,
                $"endpoint off surface: {Vector3.Distance(p, center)}"));

            // Odd parallels => a latitude ring sits exactly on the equator (y == center.Y, horizontal dist == radius).
            Assert.Contains(segs, p => Math.Abs(p.Y - center.Y) < Eps
                && Math.Abs(MathF.Sqrt((p.X - center.X) * (p.X - center.X) + (p.Z - center.Z) * (p.Z - center.Z)) - radius) < Eps);
        }

        [Fact]
        public void Dome_IsUpperHemisphere_WithApexAndBaseEquatorCircle()
        {
            var segs = new List<Vector3>();
            var baseCenter = new Vector3(-4, 1, 2);
            float radius = 3f;
            int meridians = 12, parallels = 4, ringSegments = 32;
            DebugShapes.Dome(segs, baseCenter, radius, meridians, parallels, ringSegments);

            int arcSteps = Math.Max(2, ringSegments / 4);
            int expected = parallels * ringSegments * 2 + meridians * arcSteps * 2;
            Assert.Equal(expected, segs.Count);

            // Every endpoint on the sphere surface AND in the upper half (y >= baseCenter.Y).
            Assert.All(segs, p =>
            {
                Assert.True(Math.Abs(Vector3.Distance(p, baseCenter) - radius) < Eps, "endpoint off surface");
                Assert.True(p.Y >= baseCenter.Y - Eps, $"endpoint below the flat base: y={p.Y}");
            });

            // The apex (top pole) is reached by the meridian arcs.
            Assert.Contains(segs, p => Vector3.Distance(p, baseCenter + new Vector3(0, radius, 0)) < Eps);

            // The base equator circle is present: endpoints at y == baseCenter.Y at full radius in the XZ plane.
            Assert.Contains(segs, p => Math.Abs(p.Y - baseCenter.Y) < Eps
                && Math.Abs(MathF.Sqrt((p.X - baseCenter.X) * (p.X - baseCenter.X) + (p.Z - baseCenter.Z) * (p.Z - baseCenter.Z)) - radius) < Eps);
        }

        [Fact]
        public void Cylinder_TwoRimCirclesAndVerticalSides_AtRadiusAndHalfHeight()
        {
            var segs = new List<Vector3>();
            var center = new Vector3(1, 5, -2);
            float radius = 2f, halfHeight = 4f;
            int ringSegments = 32, verticals = 8;
            DebugShapes.Cylinder(segs, center, radius, halfHeight, ringSegments, verticals);

            // Two rim circles (ringSegments*2 each) + verticals side lines (2 endpoints each).
            int expected = 2 * ringSegments * 2 + verticals * 2;
            Assert.Equal(expected, segs.Count);

            float top = center.Y + halfHeight, bottom = center.Y - halfHeight;
            Assert.All(segs, p =>
            {
                // Every endpoint is on the top or bottom cap plane...
                bool onCap = Math.Abs(p.Y - top) < Eps || Math.Abs(p.Y - bottom) < Eps;
                Assert.True(onCap, $"endpoint not on a cap: y={p.Y}");
                // ...at the cylinder radius from the axis.
                float horiz = MathF.Sqrt((p.X - center.X) * (p.X - center.X) + (p.Z - center.Z) * (p.Z - center.Z));
                Assert.True(Math.Abs(horiz - radius) < Eps, $"endpoint off the wall: r={horiz}");
            });

            // Both caps are actually drawn.
            Assert.Contains(segs, p => Math.Abs(p.Y - top) < Eps);
            Assert.Contains(segs, p => Math.Abs(p.Y - bottom) < Eps);
        }

        [Fact]
        public void WireVolumes_DegenerateInputs_AppendNothing()
        {
            var segs = new List<Vector3>();
            DebugShapes.Sphere(segs, Vector3.Zero, radius: 0f, meridians: 12, parallels: 5, ringSegments: 32);
            DebugShapes.Sphere(segs, Vector3.Zero, radius: 1f, meridians: 1, parallels: 5, ringSegments: 32);
            DebugShapes.Dome(segs, Vector3.Zero, radius: 1f, meridians: 12, parallels: 0, ringSegments: 32);
            DebugShapes.Cylinder(segs, Vector3.Zero, radius: 1f, halfHeight: 0f, ringSegments: 32, verticals: 8);
            DebugShapes.Cylinder(segs, Vector3.Zero, radius: 1f, halfHeight: 1f, ringSegments: 2, verticals: 8);
            Assert.Empty(segs);
        }

        [Fact]
        public void AllBuilders_ProduceEvenEndpointCounts()
        {
            var segs = new List<Vector3>();
            DebugShapes.Box(segs, Vector3.Zero, Vector3.One);
            DebugShapes.Grid(segs, Vector3.Zero, 1f, 3);
            DebugShapes.Circle(segs, Vector3.Zero, Vector3.UnitZ, 1f, 8);
            DebugShapes.Axes(segs, Vector3.Zero, 1f);
            DebugShapes.Sphere(segs, Vector3.Zero, 1f, 12, 5, 32);
            DebugShapes.Dome(segs, Vector3.Zero, 1f, 12, 4, 32);
            DebugShapes.Cylinder(segs, Vector3.Zero, 1f, 1f, 32, 8);
            Assert.Equal(0, segs.Count % 2);
        }
    }
}
