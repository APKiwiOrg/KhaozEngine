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
            if (segmentCount < 3 || normal.LengthSquared() < 1e-12f) return;   // degenerate: nothing to draw
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

        /// <summary>Append a wireframe UV sphere centred at <paramref name="center"/> of the given
        /// <paramref name="radius"/>: <paramref name="parallels"/> horizontal latitude rings plus
        /// <paramref name="meridians"/> vertical pole-to-pole half-circle arcs. Each ring is
        /// <paramref name="ringSegments"/> segments and each meridian arc uses half that (a meridian spans half the
        /// circumference, so half the segment count keeps the segment length matched to the rings). An odd
        /// <paramref name="parallels"/> lands one ring exactly on the equator. Degenerate inputs (radius &lt;= 0,
        /// <paramref name="meridians"/> &lt; 2, <paramref name="parallels"/> &lt; 1, <paramref name="ringSegments"/>
        /// &lt; 3) append nothing.</summary>
        public static void Sphere(List<Vector3> segments, Vector3 center, float radius, int meridians, int parallels, int ringSegments)
        {
            if (radius <= 0f || meridians < 2 || parallels < 1 || ringSegments < 3) return;

            // Latitude rings, evenly spaced in polar angle strictly between the two poles.
            for (int j = 1; j <= parallels; j++)
            {
                float theta = MathF.PI * j / (parallels + 1);   // 0 = north pole, pi = south pole
                float y = center.Y + radius * MathF.Cos(theta);
                float rr = radius * MathF.Sin(theta);
                Circle(segments, new Vector3(center.X, y, center.Z), Vector3.UnitY, rr, ringSegments);
            }

            // Meridian arcs: half great circles from pole to pole at evenly spaced longitudes.
            int arcSteps = Math.Max(2, ringSegments / 2);
            for (int k = 0; k < meridians; k++)
            {
                float phi = MathF.Tau * k / meridians;
                float cosPhi = MathF.Cos(phi), sinPhi = MathF.Sin(phi);
                for (int i = 0; i < arcSteps; i++)
                    AddSeg(segments,
                        SpherePoint(center, radius, MathF.PI * i / arcSteps, cosPhi, sinPhi),
                        SpherePoint(center, radius, MathF.PI * (i + 1) / arcSteps, cosPhi, sinPhi));
            }
        }

        /// <summary>Append a wireframe hemisphere DOME (flat side down) sitting on the XZ plane through
        /// <paramref name="baseCenter"/> and bulging up to <paramref name="baseCenter"/>.Y + <paramref name="radius"/>
        /// at the apex: <paramref name="parallels"/> horizontal rings from just under the apex down to (and including)
        /// the base equator circle, plus <paramref name="meridians"/> vertical quarter-circle arcs from the apex to
        /// the equator. Each ring is <paramref name="ringSegments"/> segments and each arc uses a quarter of that (an
        /// arc spans a quarter of the circumference). The last ring (<c>j == parallels</c>) is exactly the base equator
        /// circle, so the flat rim is always drawn. Degenerate inputs append nothing.</summary>
        public static void Dome(List<Vector3> segments, Vector3 baseCenter, float radius, int meridians, int parallels, int ringSegments)
        {
            if (radius <= 0f || meridians < 2 || parallels < 1 || ringSegments < 3) return;

            // Latitude rings from apex down to the equator. j == parallels gives theta = pi/2 (the base equator).
            for (int j = 1; j <= parallels; j++)
            {
                float theta = (MathF.PI / 2f) * j / parallels;   // 0 = apex, pi/2 = equator (base rim)
                float y = baseCenter.Y + radius * MathF.Cos(theta);
                float rr = radius * MathF.Sin(theta);
                Circle(segments, new Vector3(baseCenter.X, y, baseCenter.Z), Vector3.UnitY, rr, ringSegments);
            }

            // Meridian arcs: quarter circles from the apex down to the equator.
            int arcSteps = Math.Max(2, ringSegments / 4);
            for (int k = 0; k < meridians; k++)
            {
                float phi = MathF.Tau * k / meridians;
                float cosPhi = MathF.Cos(phi), sinPhi = MathF.Sin(phi);
                for (int i = 0; i < arcSteps; i++)
                    AddSeg(segments,
                        SpherePoint(baseCenter, radius, (MathF.PI / 2f) * i / arcSteps, cosPhi, sinPhi),
                        SpherePoint(baseCenter, radius, (MathF.PI / 2f) * (i + 1) / arcSteps, cosPhi, sinPhi));
            }
        }

        /// <summary>Append a wireframe vertical cylinder (axis along +Y) centred at <paramref name="center"/>: a top
        /// and a bottom rim circle of <paramref name="radius"/> at <paramref name="center"/>.Y +/-
        /// <paramref name="halfHeight"/>, each of <paramref name="ringSegments"/> segments, joined by
        /// <paramref name="verticals"/> evenly spaced vertical side lines. Degenerate inputs (radius or halfHeight
        /// &lt;= 0, <paramref name="ringSegments"/> &lt; 3, <paramref name="verticals"/> &lt; 1) append nothing.</summary>
        public static void Cylinder(List<Vector3> segments, Vector3 center, float radius, float halfHeight, int ringSegments, int verticals)
        {
            if (radius <= 0f || halfHeight <= 0f || ringSegments < 3 || verticals < 1) return;

            Vector3 top = new(center.X, center.Y + halfHeight, center.Z);
            Vector3 bottom = new(center.X, center.Y - halfHeight, center.Z);
            Circle(segments, top, Vector3.UnitY, radius, ringSegments);
            Circle(segments, bottom, Vector3.UnitY, radius, ringSegments);

            for (int k = 0; k < verticals; k++)
            {
                float phi = MathF.Tau * k / verticals;
                Vector3 off = new(radius * MathF.Cos(phi), 0f, radius * MathF.Sin(phi));
                AddSeg(segments, bottom + off, top + off);
            }
        }

        /// <summary>A point on the sphere of <paramref name="radius"/> about <paramref name="center"/> at polar angle
        /// <paramref name="theta"/> (from +Y) and the longitude given by (<paramref name="cosPhi"/>,
        /// <paramref name="sinPhi"/>). Shared by <see cref="Sphere"/> and <see cref="Dome"/>.</summary>
        static Vector3 SpherePoint(Vector3 center, float radius, float theta, float cosPhi, float sinPhi)
        {
            float st = MathF.Sin(theta), ct = MathF.Cos(theta);
            return center + new Vector3(radius * st * cosPhi, radius * ct, radius * st * sinPhi);
        }

        static void AddSeg(List<Vector3> segments, Vector3 a, Vector3 b)
        {
            segments.Add(a);
            segments.Add(b);
        }
    }
}
