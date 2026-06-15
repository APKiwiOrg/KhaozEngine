using System;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Builds simple <see cref="GltfMesh"/> primitives in code (no asset files), so games don't hand-roll
    /// them. Each face is a quad with its own outward normal (flat shading); vertex color is white
    /// (<see cref="Vector4.One"/>) since color now comes from the per-instance tint passed to
    /// <see cref="Scene3D.Draw(MeshHandle, Matrix4x4, Vector4)"/>.
    /// </summary>
    public static class MeshPrimitives
    {
        /// <summary>Axis-aligned cube centered at the origin: 6 quads = 24 vertices / 36 indices.</summary>
        public static GltfMesh Box(float size = 1f)
        {
            float h = size * 0.5f;
            return BuildBox(new Vector3(-h, -h, -h), new Vector3(h, h, h));
        }

        /// <summary>
        /// Flat box (footprint <paramref name="size"/>×<paramref name="size"/>, height
        /// <paramref name="thickness"/>) resting with its base at y=0, so it sits on the ground plane.
        /// 24 vertices / 36 indices.
        /// </summary>
        public static GltfMesh Tile(float size = 1f, float thickness = 0.1f)
        {
            float h = size * 0.5f;
            return BuildBox(new Vector3(-h, 0f, -h), new Vector3(h, thickness, h));
        }

        /// <summary>
        /// Solid cylinder along +Y, base at y=0, top at <paramref name="height"/>. Side vertices carry radial
        /// (smooth) normals; the caps (when <paramref name="capped"/>) are flat ±Y triangle fans. Front faces
        /// are CCW with outward normals (matching <see cref="Box"/>). <paramref name="segments"/> is clamped to
        /// a minimum of 3.
        /// </summary>
        public static GltfMesh Cylinder(float radius = 0.5f, float height = 1f, int segments = 16, bool capped = true)
        {
            segments = System.Math.Max(3, segments);
            var white = Vector4.One;
            var verts = new System.Collections.Generic.List<ModelVertex>();
            var inds = new System.Collections.Generic.List<ushort>();

            // Side wall: per-segment quad with duplicated radial-normal vertices (so the seam is sharp-free).
            for (int s = 0; s < segments; s++)
            {
                float a0 = MathF.Tau * s / segments;
                float a1 = MathF.Tau * (s + 1) / segments;
                Vector3 n0 = new Vector3(MathF.Cos(a0), 0f, MathF.Sin(a0));
                Vector3 n1 = new Vector3(MathF.Cos(a1), 0f, MathF.Sin(a1));
                Vector3 b0 = n0 * radius;                          // base, angle 0
                Vector3 b1 = n1 * radius;                          // base, angle 1
                Vector3 t0 = b0 + new Vector3(0f, height, 0f);     // top, angle 0
                Vector3 t1 = b1 + new Vector3(0f, height, 0f);     // top, angle 1

                ushort baseIdx = (ushort)verts.Count;
                verts.Add(new ModelVertex(b0, n0, white));
                verts.Add(new ModelVertex(b1, n1, white));
                verts.Add(new ModelVertex(t1, n1, white));
                verts.Add(new ModelVertex(t0, n0, white));
                // outward CCW (viewed from outside)
                inds.Add(baseIdx); inds.Add((ushort)(baseIdx + 1)); inds.Add((ushort)(baseIdx + 2));
                inds.Add(baseIdx); inds.Add((ushort)(baseIdx + 2)); inds.Add((ushort)(baseIdx + 3));
            }

            if (capped)
            {
                AddCapFan(verts, inds, new Vector3(0f, height, 0f), radius, segments, Vector3.UnitY, true);
                AddCapFan(verts, inds, Vector3.Zero, radius, segments, -Vector3.UnitY, false);
            }

            return new GltfMesh(verts.ToArray(), inds.ToArray());
        }

        /// <summary>
        /// Solid cone: base circle of <paramref name="radius"/> at y=0, apex at (0, <paramref name="height"/>, 0).
        /// Side normals point outward/up; the base cap (when <paramref name="capped"/>) is a flat -Y fan. Front
        /// faces CCW. <paramref name="segments"/> clamped to a minimum of 3.
        /// </summary>
        public static GltfMesh Cone(float radius = 0.5f, float height = 1f, int segments = 16, bool capped = true)
        {
            segments = System.Math.Max(3, segments);
            var white = Vector4.One;
            var verts = new System.Collections.Generic.List<ModelVertex>();
            var inds = new System.Collections.Generic.List<ushort>();
            var apex = new Vector3(0f, height, 0f);
            float slope = MathF.Sqrt(radius * radius + height * height);

            for (int s = 0; s < segments; s++)
            {
                float a0 = MathF.Tau * s / segments;
                float a1 = MathF.Tau * (s + 1) / segments;
                Vector3 b0 = new Vector3(MathF.Cos(a0) * radius, 0f, MathF.Sin(a0) * radius);
                Vector3 b1 = new Vector3(MathF.Cos(a1) * radius, 0f, MathF.Sin(a1) * radius);
                // side normal: radial component scaled by height, plus +Y component scaled by radius (outward/up).
                Vector3 n0 = SideConeNormal(a0, radius, height, slope);
                Vector3 n1 = SideConeNormal(a1, radius, height, slope);
                Vector3 nApex = Vector3.Normalize(n0 + n1);

                ushort baseIdx = (ushort)verts.Count;
                verts.Add(new ModelVertex(b0, n0, white));
                verts.Add(new ModelVertex(b1, n1, white));
                verts.Add(new ModelVertex(apex, nApex, white));
                // CCW from outside: base0 -> base1 -> apex
                inds.Add(baseIdx); inds.Add((ushort)(baseIdx + 1)); inds.Add((ushort)(baseIdx + 2));
            }

            if (capped)
                AddCapFan(verts, inds, Vector3.Zero, radius, segments, -Vector3.UnitY, false);

            return new GltfMesh(verts.ToArray(), inds.ToArray());
        }

        /// <summary>
        /// Square-based pyramid: <paramref name="baseSize"/>×<paramref name="baseSize"/> base centered on X/Z at
        /// y=0, apex at (0, <paramref name="height"/>, 0). Flat normals on the four triangular sides and the -Y
        /// base quad. Front faces CCW.
        /// </summary>
        public static GltfMesh Pyramid(float baseSize = 1f, float height = 1f)
        {
            float h = baseSize * 0.5f;
            var white = Vector4.One;
            var apex = new Vector3(0f, height, 0f);
            // base corners, CCW seen from below
            var c0 = new Vector3(-h, 0f, -h);
            var c1 = new Vector3(h, 0f, -h);
            var c2 = new Vector3(h, 0f, h);
            var c3 = new Vector3(-h, 0f, h);

            var verts = new System.Collections.Generic.List<ModelVertex>();
            var inds = new System.Collections.Generic.List<ushort>();

            void Tri(Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                ushort baseIdx = (ushort)verts.Count;
                verts.Add(new ModelVertex(a, n, white));
                verts.Add(new ModelVertex(b, n, white));
                verts.Add(new ModelVertex(c, n, white));
                inds.Add(baseIdx); inds.Add((ushort)(baseIdx + 1)); inds.Add((ushort)(baseIdx + 2));
            }

            // four sides (CCW outward): each base edge then apex
            Tri(c0, c1, apex); // -Z
            Tri(c1, c2, apex); // +X
            Tri(c2, c3, apex); // +Z
            Tri(c3, c0, apex); // -X

            // base quad (-Y, CCW seen from below)
            var nDown = -Vector3.UnitY;
            ushort bi = (ushort)verts.Count;
            verts.Add(new ModelVertex(c0, nDown, white));
            verts.Add(new ModelVertex(c3, nDown, white));
            verts.Add(new ModelVertex(c2, nDown, white));
            verts.Add(new ModelVertex(c1, nDown, white));
            inds.Add(bi); inds.Add((ushort)(bi + 1)); inds.Add((ushort)(bi + 2));
            inds.Add(bi); inds.Add((ushort)(bi + 2)); inds.Add((ushort)(bi + 3));

            return new GltfMesh(verts.ToArray(), inds.ToArray());
        }

        /// <summary>
        /// Right-triangular prism (a ramp): <paramref name="size"/>×<paramref name="size"/> footprint at y=0,
        /// rising linearly from y=0 at -Z to y=<paramref name="height"/> at +Z. Closed solid (bottom, vertical
        /// back, sloped top, two triangular sides) with flat normals. Front faces CCW.
        /// </summary>
        public static GltfMesh Wedge(float size = 1f, float height = 1f)
        {
            float h = size * 0.5f;
            var white = Vector4.One;
            // six corners of the prism.
            // low edge at -Z (y=0), high edge at +Z (y=0 and y=height for the back wall).
            var a = new Vector3(-h, 0f, -h);      // bottom, low, -X
            var b = new Vector3(h, 0f, -h);       // bottom, low, +X
            var c = new Vector3(h, 0f, h);        // bottom, high, +X
            var d = new Vector3(-h, 0f, h);       // bottom, high, -X
            var e = new Vector3(-h, height, h);   // top, high, -X
            var f = new Vector3(h, height, h);    // top, high, +X

            var verts = new System.Collections.Generic.List<ModelVertex>();
            var inds = new System.Collections.Generic.List<ushort>();

            void Tri(Vector3 p, Vector3 q, Vector3 r)
            {
                Vector3 n = Vector3.Normalize(Vector3.Cross(q - p, r - p));
                ushort baseIdx = (ushort)verts.Count;
                verts.Add(new ModelVertex(p, n, white));
                verts.Add(new ModelVertex(q, n, white));
                verts.Add(new ModelVertex(r, n, white));
                inds.Add(baseIdx); inds.Add((ushort)(baseIdx + 1)); inds.Add((ushort)(baseIdx + 2));
            }
            void Quad(Vector3 p, Vector3 q, Vector3 r, Vector3 s)
            {
                Vector3 n = Vector3.Normalize(Vector3.Cross(q - p, s - p));
                ushort baseIdx = (ushort)verts.Count;
                verts.Add(new ModelVertex(p, n, white));
                verts.Add(new ModelVertex(q, n, white));
                verts.Add(new ModelVertex(r, n, white));
                verts.Add(new ModelVertex(s, n, white));
                inds.Add(baseIdx); inds.Add((ushort)(baseIdx + 1)); inds.Add((ushort)(baseIdx + 2));
                inds.Add(baseIdx); inds.Add((ushort)(baseIdx + 2)); inds.Add((ushort)(baseIdx + 3));
            }

            // bottom (-Y), CCW from below: a, d, c, b
            Quad(a, d, c, b);
            // back wall (+Z), vertical, CCW from outside (+Z): d, c -> f, e ; outward +Z
            Quad(c, d, e, f);
            // sloped top: from low edge (a,b at -Z) up to high edge (e,f at +Z), normal points up/-Z-ish
            Quad(b, a, e, f);
            // side -X triangle (a, d, e), outward -X
            Tri(d, a, e);
            // side +X triangle (b, c, f), outward +X
            Tri(b, f, c);

            return new GltfMesh(verts.ToArray(), inds.ToArray());
        }

        /// <summary>
        /// UV sphere centered at the origin, radius <paramref name="radius"/>, with smooth radial normals.
        /// <paramref name="rings"/> clamped to a minimum of 2, <paramref name="segments"/> to a minimum of 3.
        /// Front faces CCW with outward normals.
        /// </summary>
        public static GltfMesh Sphere(float radius = 0.5f, int rings = 8, int segments = 12)
        {
            rings = System.Math.Max(2, rings);
            segments = System.Math.Max(3, segments);
            var white = Vector4.One;
            int cols = segments + 1; // duplicate the seam column for clean wrap
            var verts = new ModelVertex[(rings + 1) * cols];
            var inds = new System.Collections.Generic.List<ushort>();

            for (int r = 0; r <= rings; r++)
            {
                float phi = MathF.PI * r / rings;        // 0..PI from +Y pole to -Y pole
                float y = MathF.Cos(phi);
                float ringRadius = MathF.Sin(phi);
                for (int s = 0; s <= segments; s++)
                {
                    float theta = MathF.Tau * s / segments;
                    var dir = new Vector3(ringRadius * MathF.Cos(theta), y, ringRadius * MathF.Sin(theta));
                    dir = Vector3.Normalize(dir);
                    verts[r * cols + s] = new ModelVertex(dir * radius, dir, white);
                }
            }

            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segments; s++)
                {
                    ushort i0 = (ushort)(r * cols + s);
                    ushort i1 = (ushort)(r * cols + s + 1);
                    ushort i2 = (ushort)((r + 1) * cols + s);
                    ushort i3 = (ushort)((r + 1) * cols + s + 1);
                    // CCW outward: top-left, bottom-left, bottom-right / top-left, bottom-right, top-right
                    inds.Add(i0); inds.Add(i2); inds.Add(i3);
                    inds.Add(i0); inds.Add(i3); inds.Add(i1);
                }
            }

            return new GltfMesh(verts, inds.ToArray());
        }

        /// <summary>Cone side normal at angle <paramref name="angle"/>: radial scaled by height + up scaled by radius.</summary>
        static Vector3 SideConeNormal(float angle, float radius, float height, float slope)
        {
            // gradient of the cone surface: outward radial * (height/slope) + up * (radius/slope).
            float rc = height / slope;
            float up = radius / slope;
            return Vector3.Normalize(new Vector3(MathF.Cos(angle) * rc, up, MathF.Sin(angle) * rc));
        }

        /// <summary>
        /// Adds a flat triangle-fan cap (center + ring) facing <paramref name="normal"/>. <paramref name="ccwFromOutside"/>
        /// controls winding so the visible front face is CCW.
        /// </summary>
        static void AddCapFan(System.Collections.Generic.List<ModelVertex> verts,
            System.Collections.Generic.List<ushort> inds, Vector3 center, float radius, int segments,
            Vector3 normal, bool ccwFromOutside)
        {
            var white = Vector4.One;
            ushort centerIdx = (ushort)verts.Count;
            verts.Add(new ModelVertex(center, normal, white));
            ushort ringStart = (ushort)verts.Count;
            for (int s = 0; s < segments; s++)
            {
                float a = MathF.Tau * s / segments;
                var p = center + new Vector3(MathF.Cos(a) * radius, 0f, MathF.Sin(a) * radius);
                verts.Add(new ModelVertex(p, normal, white));
            }
            for (int s = 0; s < segments; s++)
            {
                ushort cur = (ushort)(ringStart + s);
                ushort next = (ushort)(ringStart + (s + 1) % segments);
                if (ccwFromOutside)
                {
                    inds.Add(centerIdx); inds.Add(cur); inds.Add(next);
                }
                else
                {
                    inds.Add(centerIdx); inds.Add(next); inds.Add(cur);
                }
            }
        }

        /// <summary>Builds an axis-aligned box spanning [min, max] as 6 outward-facing quads.</summary>
        static GltfMesh BuildBox(Vector3 min, Vector3 max)
        {
            var white = Vector4.One;
            var vertices = new ModelVertex[24];
            var indices = new ushort[36];
            int vi = 0, ii = 0;

            void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
            {
                ushort baseIdx = (ushort)vi;
                vertices[vi++] = new ModelVertex(a, n, white);
                vertices[vi++] = new ModelVertex(b, n, white);
                vertices[vi++] = new ModelVertex(c, n, white);
                vertices[vi++] = new ModelVertex(d, n, white);
                indices[ii++] = baseIdx;
                indices[ii++] = (ushort)(baseIdx + 1);
                indices[ii++] = (ushort)(baseIdx + 2);
                indices[ii++] = baseIdx;
                indices[ii++] = (ushort)(baseIdx + 2);
                indices[ii++] = (ushort)(baseIdx + 3);
            }

            // +X
            Face(new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, min.Y, min.Z),
                 new Vector3(max.X, max.Y, min.Z), new Vector3(max.X, max.Y, max.Z), Vector3.UnitX);
            // -X
            Face(new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, min.Y, max.Z),
                 new Vector3(min.X, max.Y, max.Z), new Vector3(min.X, max.Y, min.Z), -Vector3.UnitX);
            // +Y
            Face(new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, max.Y, max.Z),
                 new Vector3(max.X, max.Y, min.Z), new Vector3(min.X, max.Y, min.Z), Vector3.UnitY);
            // -Y
            Face(new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
                 new Vector3(max.X, min.Y, max.Z), new Vector3(min.X, min.Y, max.Z), -Vector3.UnitY);
            // +Z
            Face(new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, max.Z),
                 new Vector3(max.X, max.Y, max.Z), new Vector3(min.X, max.Y, max.Z), Vector3.UnitZ);
            // -Z
            Face(new Vector3(max.X, min.Y, min.Z), new Vector3(min.X, min.Y, min.Z),
                 new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z), -Vector3.UnitZ);

            return new GltfMesh(vertices, indices);
        }
    }
}
