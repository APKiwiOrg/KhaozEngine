using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Pure geometry builders for the debug-line overlay. Each method appends line-segment ENDPOINT pairs
    /// (always an even count of <see cref="Vector3"/>) to the supplied list; the caller (typically
    /// <see cref="Scene3D"/>) attaches the colour. No GPU, so the geometry is unit-testable headlessly.
    /// </summary>
    public static class DebugShapes
    {
        /// <summary>Append the 12 edges (24 endpoints) of an axis-aligned box centred at
        /// <paramref name="center"/> with full extents <paramref name="size"/>.</summary>
        public static void Box(List<Vector3> segments, Vector3 center, Vector3 size)
        {
            Vector3 h = size * 0.5f;
            // 8 corners.
            Vector3 c000 = center + new Vector3(-h.X, -h.Y, -h.Z);
            Vector3 c100 = center + new Vector3(+h.X, -h.Y, -h.Z);
            Vector3 c110 = center + new Vector3(+h.X, +h.Y, -h.Z);
            Vector3 c010 = center + new Vector3(-h.X, +h.Y, -h.Z);
            Vector3 c001 = center + new Vector3(-h.X, -h.Y, +h.Z);
            Vector3 c101 = center + new Vector3(+h.X, -h.Y, +h.Z);
            Vector3 c111 = center + new Vector3(+h.X, +h.Y, +h.Z);
            Vector3 c011 = center + new Vector3(-h.X, +h.Y, +h.Z);

            // Bottom face (y-).
            AddSeg(segments, c000, c100);
            AddSeg(segments, c100, c101);
            AddSeg(segments, c101, c001);
            AddSeg(segments, c001, c000);
            // Top face (y+).
            AddSeg(segments, c010, c110);
            AddSeg(segments, c110, c111);
            AddSeg(segments, c111, c011);
            AddSeg(segments, c011, c010);
            // Vertical edges.
            AddSeg(segments, c000, c010);
            AddSeg(segments, c100, c110);
            AddSeg(segments, c101, c111);
            AddSeg(segments, c001, c011);
        }

        /// <summary>Append a grid on the XZ plane through <paramref name="center"/>.Y: <c>cells+1</c> lines in
        /// each of X and Z, each spanning <c>cells*cellSize</c> and centred on <paramref name="center"/>.</summary>
        public static void Grid(List<Vector3> segments, Vector3 center, float cellSize, int cells)
        {
            float half = cells * cellSize * 0.5f;
            float y = center.Y;
            for (int i = 0; i <= cells; i++)
            {
                float offset = -half + i * cellSize;
                // Lines parallel to the Z axis (varying X).
                AddSeg(segments,
                    new Vector3(center.X + offset, y, center.Z - half),
                    new Vector3(center.X + offset, y, center.Z + half));
                // Lines parallel to the X axis (varying Z).
                AddSeg(segments,
                    new Vector3(center.X - half, y, center.Z + offset),
                    new Vector3(center.X + half, y, center.Z + offset));
            }
        }

        /// <summary>Append a circle of <paramref name="segmentCount"/> segments (<c>segmentCount*2</c> endpoints),
        /// all at <paramref name="radius"/> from <paramref name="center"/>, in the plane perpendicular to
        /// <paramref name="normal"/>.</summary>
        public static void Circle(List<Vector3> segments, Vector3 center, Vector3 normal, float radius, int segmentCount)
        {
            // Build an orthonormal basis (u, v) spanning the plane perpendicular to normal.
            Vector3 n = Vector3.Normalize(normal);
            Vector3 reference = Math.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            Vector3 u = Vector3.Normalize(Vector3.Cross(reference, n));
            Vector3 v = Vector3.Cross(n, u);

            Vector3 Point(int i)
            {
                float t = (float)(i % segmentCount) / segmentCount * MathF.Tau;
                return center + (u * MathF.Cos(t) + v * MathF.Sin(t)) * radius;
            }

            for (int i = 0; i < segmentCount; i++)
                AddSeg(segments, Point(i), Point(i + 1));
        }

        /// <summary>Append 3 axis lines (6 endpoints) from <paramref name="origin"/>, each
        /// <paramref name="scale"/> long along +X, +Y, +Z. The caller colours them per-axis.</summary>
        public static void Axes(List<Vector3> segments, Vector3 origin, float scale)
        {
            AddSeg(segments, origin, origin + new Vector3(scale, 0, 0));
            AddSeg(segments, origin, origin + new Vector3(0, scale, 0));
            AddSeg(segments, origin, origin + new Vector3(0, 0, scale));
        }

        static void AddSeg(List<Vector3> segments, Vector3 a, Vector3 b)
        {
            segments.Add(a);
            segments.Add(b);
        }
    }
}
