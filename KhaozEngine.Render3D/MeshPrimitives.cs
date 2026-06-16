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

                float u0 = (float)s / segments;
                float u1 = (float)(s + 1) / segments;
                ushort baseIdx = (ushort)verts.Count;
                verts.Add(new ModelVertex(b0, n0, white, new Vector2(u0, 0f)));
                verts.Add(new ModelVertex(b1, n1, white, new Vector2(u1, 0f)));
                verts.Add(new ModelVertex(t1, n1, white, new Vector2(u1, 1f)));
                verts.Add(new ModelVertex(t0, n0, white, new Vector2(u0, 1f)));
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

                float u0 = (float)s / segments;
                float u1 = (float)(s + 1) / segments;
                ushort baseIdx = (ushort)verts.Count;
                verts.Add(new ModelVertex(b0, n0, white, new Vector2(u0, 0f)));
                verts.Add(new ModelVertex(b1, n1, white, new Vector2(u1, 0f)));
                verts.Add(new ModelVertex(apex, nApex, white, new Vector2((u0 + u1) * 0.5f, 1f)));
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

            // per-face UV for a triangular side: two base corners at the bottom, apex at the top-centre.
            void Tri(Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                ushort baseIdx = (ushort)verts.Count;
                verts.Add(new ModelVertex(a, n, white, new Vector2(0f, 0f)));
                verts.Add(new ModelVertex(b, n, white, new Vector2(1f, 0f)));
                verts.Add(new ModelVertex(c, n, white, new Vector2(0.5f, 1f)));
                inds.Add(baseIdx); inds.Add((ushort)(baseIdx + 1)); inds.Add((ushort)(baseIdx + 2));
            }

            // four sides: swap the first two corners so the computed normal (Cross(b-a, c-a))
            // points OUTWARD away from the axis (the un-swapped order stored inward normals).
            Tri(c1, c0, apex); // -Z
            Tri(c2, c1, apex); // +X
            Tri(c3, c2, apex); // +Z
            Tri(c0, c3, apex); // -X

            // base quad (-Y, CCW seen from below)
            var nDown = -Vector3.UnitY;
            ushort bi = (ushort)verts.Count;
            verts.Add(new ModelVertex(c0, nDown, white, new Vector2(0f, 0f)));
            verts.Add(new ModelVertex(c3, nDown, white, new Vector2(1f, 0f)));
            verts.Add(new ModelVertex(c2, nDown, white, new Vector2(1f, 1f)));
            verts.Add(new ModelVertex(c1, nDown, white, new Vector2(0f, 1f)));
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
                verts.Add(new ModelVertex(p, n, white, new Vector2(0f, 0f)));
                verts.Add(new ModelVertex(q, n, white, new Vector2(1f, 0f)));
                verts.Add(new ModelVertex(r, n, white, new Vector2(0.5f, 1f)));
                inds.Add(baseIdx); inds.Add((ushort)(baseIdx + 1)); inds.Add((ushort)(baseIdx + 2));
            }
            void Quad(Vector3 p, Vector3 q, Vector3 r, Vector3 s)
            {
                Vector3 n = Vector3.Normalize(Vector3.Cross(q - p, s - p));
                ushort baseIdx = (ushort)verts.Count;
                verts.Add(new ModelVertex(p, n, white, new Vector2(0f, 0f)));
                verts.Add(new ModelVertex(q, n, white, new Vector2(1f, 0f)));
                verts.Add(new ModelVertex(r, n, white, new Vector2(1f, 1f)));
                verts.Add(new ModelVertex(s, n, white, new Vector2(0f, 1f)));
                inds.Add(baseIdx); inds.Add((ushort)(baseIdx + 1)); inds.Add((ushort)(baseIdx + 2));
                inds.Add(baseIdx); inds.Add((ushort)(baseIdx + 2)); inds.Add((ushort)(baseIdx + 3));
            }

            // bottom (-Y outward): order gives Cross(q-p, s-p) pointing -Y.
            Quad(d, a, b, c);
            // back wall (vertical, +Z outward): order gives normal pointing +Z.
            Quad(d, c, f, e);
            // sloped top: from low edge (a,b at -Z) up to high edge (e,f at +Z), normal points up/-Z-ish (already outward)
            Quad(b, a, e, f);
            // side -X triangle (-X outward): order gives normal pointing -X.
            Tri(a, d, e);
            // side +X triangle (b, c, f), outward +X (already correct)
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
                float v = (float)r / rings; // latitude 0..1 (+Y pole to -Y pole)
                for (int s = 0; s <= segments; s++)
                {
                    float theta = MathF.Tau * s / segments;
                    var dir = new Vector3(ringRadius * MathF.Cos(theta), y, ringRadius * MathF.Sin(theta));
                    dir = Vector3.Normalize(dir);
                    var uv = new Vector2((float)s / segments, v);
                    verts[r * cols + s] = new ModelVertex(dir * radius, dir, white, uv);
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

        /// <summary>
        /// Flat XZ quad at y=0 centered on the origin, subdivided into a
        /// <paramref name="subdivisionsX"/>×<paramref name="subdivisionsZ"/> grid (for terrain / UV-mapped
        /// floors). Normal is +Y on every vertex; UV spans 0..1 across the whole plane. Front faces CCW seen
        /// from above. Subdivisions clamp to a minimum of 1.
        /// </summary>
        public static GltfMesh Plane(float width = 1f, float depth = 1f, int subdivisionsX = 1, int subdivisionsZ = 1)
        {
            subdivisionsX = System.Math.Max(1, subdivisionsX);
            subdivisionsZ = System.Math.Max(1, subdivisionsZ);
            var white = Vector4.One;
            var n = Vector3.UnitY;
            int colsX = subdivisionsX + 1;
            int colsZ = subdivisionsZ + 1;
            float hw = width * 0.5f, hd = depth * 0.5f;

            var verts = new ModelVertex[colsX * colsZ];
            var inds = new System.Collections.Generic.List<ushort>();

            for (int z = 0; z <= subdivisionsZ; z++)
            {
                float fz = (float)z / subdivisionsZ;
                for (int x = 0; x <= subdivisionsX; x++)
                {
                    float fx = (float)x / subdivisionsX;
                    var pos = new Vector3(-hw + fx * width, 0f, -hd + fz * depth);
                    verts[z * colsX + x] = new ModelVertex(pos, n, white, new Vector2(fx, fz));
                }
            }

            for (int z = 0; z < subdivisionsZ; z++)
            for (int x = 0; x < subdivisionsX; x++)
            {
                ushort i0 = (ushort)(z * colsX + x);
                ushort i1 = (ushort)(z * colsX + x + 1);
                ushort i2 = (ushort)((z + 1) * colsX + x);
                ushort i3 = (ushort)((z + 1) * colsX + x + 1);
                // CCW seen from +Y (outward up).
                inds.Add(i0); inds.Add(i2); inds.Add(i3);
                inds.Add(i0); inds.Add(i3); inds.Add(i1);
            }

            return new GltfMesh(verts, inds.ToArray());
        }

        /// <summary>
        /// Axis-aligned box of side <paramref name="size"/> centered at the origin with rounded edges and
        /// corners of <paramref name="radius"/> (clamped to &lt; size/2). Built by spherifying a subdivided cube:
        /// each cube-shell vertex is pushed onto the surface of a rounded box (clamp-to-inner-box + radius), and
        /// its normal points from the nearest inner-box point — so flats stay flat and edges/corners round
        /// smoothly. <paramref name="segments"/> (per cube face edge) clamps to a minimum of 1; higher = rounder
        /// edges. UV is the cube-shell grid position per face.
        /// </summary>
        public static GltfMesh RoundedBox(float size = 1f, float radius = 0.1f, int segments = 4)
        {
            segments = System.Math.Max(1, segments);
            float half = size * 0.5f;
            radius = System.Math.Clamp(radius, 0f, System.Math.Max(0f, half - 1e-4f));
            float inner = half - radius; // half-extent of the inner (sharp) box the rounds wrap around
            var white = Vector4.One;

            var verts = new System.Collections.Generic.List<ModelVertex>();
            var inds = new System.Collections.Generic.List<ushort>();
            var weld = new System.Collections.Generic.Dictionary<(long, long, long), ushort>();

            // build one cube face as a grid in [-1,1]^2, mapped by `place` to a 3D shell point.
            void Face(System.Func<float, float, Vector3> place)
            {
                int cols = segments + 1;
                var grid = new ushort[cols * cols];
                for (int b = 0; b <= segments; b++)
                for (int a = 0; a <= segments; a++)
                {
                    float fa = (float)a / segments * 2f - 1f;
                    float fb = (float)b / segments * 2f - 1f;
                    Vector3 cube = place(fa, fb); // point on the [-half,half] cube shell
                    // nearest point on the inner box; the offset from it (length radius) is the surface normal.
                    Vector3 innerPt = new Vector3(
                        System.Math.Clamp(cube.X, -inner, inner),
                        System.Math.Clamp(cube.Y, -inner, inner),
                        System.Math.Clamp(cube.Z, -inner, inner));
                    Vector3 offset = cube - innerPt;
                    Vector3 nrm;
                    Vector3 surf;
                    if (offset.LengthSquared() > 1e-12f)
                    {
                        nrm = Vector3.Normalize(offset);
                        surf = innerPt + nrm * radius;
                    }
                    else
                    {
                        nrm = Vector3.Normalize(cube); // degenerate (radius 0): fall back to radial
                        surf = cube;
                    }
                    var uv = new Vector2((fa + 1f) * 0.5f, (fb + 1f) * 0.5f);
                    var key = ((long)MathF.Round(surf.X * 1e4f), (long)MathF.Round(surf.Y * 1e4f), (long)MathF.Round(surf.Z * 1e4f));
                    if (!weld.TryGetValue(key, out ushort idx))
                    {
                        idx = (ushort)verts.Count;
                        verts.Add(new ModelVertex(surf, nrm, white, uv));
                        weld[key] = idx;
                    }
                    grid[b * cols + a] = idx;
                }

                for (int b = 0; b < segments; b++)
                for (int a = 0; a < segments; a++)
                {
                    ushort i0 = grid[b * cols + a];
                    ushort i1 = grid[b * cols + a + 1];
                    ushort i2 = grid[(b + 1) * cols + a];
                    ushort i3 = grid[(b + 1) * cols + a + 1];
                    inds.Add(i0); inds.Add(i2); inds.Add(i3);
                    inds.Add(i0); inds.Add(i3); inds.Add(i1);
                }
            }

            // six faces, wound CCW outward (a = first param, b = second).
            Face((a, b) => new Vector3(half, b * half, a * half));   // +X
            Face((a, b) => new Vector3(-half, b * half, -a * half)); // -X
            Face((a, b) => new Vector3(a * half, half, b * half));   // +Y
            Face((a, b) => new Vector3(-a * half, -half, b * half)); // -Y
            Face((a, b) => new Vector3(-a * half, b * half, half));  // +Z
            Face((a, b) => new Vector3(a * half, b * half, -half));  // -Z

            return new GltfMesh(verts.ToArray(), inds.ToArray());
        }

        /// <summary>
        /// Capsule along +Y: a cylindrical body of <paramref name="height"/> capped by two hemispheres of
        /// <paramref name="radius"/>. The bottom hemisphere's lowest point sits at y=0; total height is
        /// <paramref name="height"/> + 2·<paramref name="radius"/>. Smooth radial normals; UV is cylindrical
        /// (U = angle/2π) with V running 0..1 bottom-to-top over the whole silhouette. <paramref name="segments"/>
        /// (around) clamps to ≥3, <paramref name="rings"/> (per hemisphere) to ≥1.
        /// </summary>
        public static GltfMesh Capsule(float radius = 0.5f, float height = 1f, int segments = 16, int rings = 6)
        {
            segments = System.Math.Max(3, segments);
            rings = System.Math.Max(1, rings);
            radius = MathF.Max(1e-4f, radius);
            height = MathF.Max(0f, height);
            var white = Vector4.One;

            float yBottom = radius;           // centre of the bottom hemisphere
            float yTop = radius + height;     // centre of the top hemisphere

            // Build vertices row by row (bottom pole up to top pole). V is normalized over the full vertical
            // extent (bottom pole y=0 to top pole y=height+2r); rowStarts records each ring's first vertex.
            float totalHeight = height + 2f * radius;
            var rowStarts = new System.Collections.Generic.List<int>();
            var verts = new System.Collections.Generic.List<ModelVertex>();
            var inds = new System.Collections.Generic.List<ushort>();

            // Bottom hemisphere: phi from PI (south pole) to PI/2 (equator).
            for (int r = 0; r <= rings; r++)
            {
                float phi = MathF.PI - (MathF.PI * 0.5f) * r / rings; // PI..PI/2
                float yc = MathF.Cos(phi) * radius;       // negative..0
                float rr = MathF.Sin(phi) * radius;       // 0..radius
                float y = yBottom + yc;                   // 0..radius
                // normal = unit offset from bottom centre = (sin*radial, cos)
                EmitHemiRing(verts, segments, y, rr, MathF.Cos(phi), white, totalHeight, rowStarts);
            }
            // Top hemisphere: phi from PI/2 (equator) to 0 (north pole). Skip the equator (already emitted).
            for (int r = 1; r <= rings; r++)
            {
                float phi = (MathF.PI * 0.5f) - (MathF.PI * 0.5f) * r / rings; // PI/2..0
                float yc = MathF.Cos(phi) * radius;       // 0..radius
                float rr = MathF.Sin(phi) * radius;       // radius..0
                float y = yTop + yc;                      // height+radius .. height+2radius
                EmitHemiRing(verts, segments, y, rr, MathF.Cos(phi), white, totalHeight, rowStarts);
            }

            int totalRows = rowStarts.Count;
            for (int r = 0; r < totalRows - 1; r++)
            {
                int a0 = rowStarts[r];
                int b0 = rowStarts[r + 1];
                for (int s = 0; s < segments; s++)
                {
                    ushort i0 = (ushort)(a0 + s);
                    ushort i1 = (ushort)(a0 + s + 1);
                    ushort i2 = (ushort)(b0 + s);
                    ushort i3 = (ushort)(b0 + s + 1);
                    // CCW outward, lower ring -> upper ring.
                    inds.Add(i0); inds.Add(i2); inds.Add(i3);
                    inds.Add(i0); inds.Add(i3); inds.Add(i1);
                }
            }

            return new GltfMesh(verts.ToArray(), inds.ToArray());
        }

        /// <summary>
        /// Torus (ring) in the XZ plane centered at the origin: a tube of <paramref name="minorRadius"/> swept
        /// around a circle of <paramref name="majorRadius"/>. Smooth normals; UV = (majorAngle/2π,
        /// minorAngle/2π). <paramref name="majorSegments"/>/<paramref name="minorSegments"/> clamp to ≥3.
        /// </summary>
        public static GltfMesh Torus(float majorRadius = 0.5f, float minorRadius = 0.2f, int majorSegments = 24, int minorSegments = 12)
        {
            majorSegments = System.Math.Max(3, majorSegments);
            minorSegments = System.Math.Max(3, minorSegments);
            majorRadius = MathF.Max(1e-4f, majorRadius);
            minorRadius = MathF.Max(1e-4f, minorRadius);
            var white = Vector4.One;

            int colsU = majorSegments + 1; // duplicate seam columns for clean UV/normal wrap
            int colsV = minorSegments + 1;
            var verts = new ModelVertex[colsU * colsV];
            var inds = new System.Collections.Generic.List<ushort>();

            for (int u = 0; u <= majorSegments; u++)
            {
                float au = MathF.Tau * u / majorSegments;
                float cu = MathF.Cos(au), su = MathF.Sin(au);
                // centre of the tube cross-section, and the outward radial direction in the XZ plane.
                var ringCenter = new Vector3(cu * majorRadius, 0f, su * majorRadius);
                var radial = new Vector3(cu, 0f, su);
                for (int v = 0; v <= minorSegments; v++)
                {
                    float av = MathF.Tau * v / minorSegments;
                    float cv = MathF.Cos(av), sv = MathF.Sin(av);
                    // tube point: radial*cos(av) out + Y*sin(av) up, scaled by the minor radius.
                    var nrm = radial * cv + new Vector3(0f, sv, 0f);
                    var pos = ringCenter + nrm * minorRadius;
                    var uv = new Vector2((float)u / majorSegments, (float)v / minorSegments);
                    verts[u * colsV + v] = new ModelVertex(pos, Vector3.Normalize(nrm), white, uv);
                }
            }

            for (int u = 0; u < majorSegments; u++)
            for (int v = 0; v < minorSegments; v++)
            {
                ushort i0 = (ushort)(u * colsV + v);
                ushort i1 = (ushort)(u * colsV + v + 1);
                ushort i2 = (ushort)((u + 1) * colsV + v);
                ushort i3 = (ushort)((u + 1) * colsV + v + 1);
                // CCW outward (u increases around the major ring, v around the tube).
                inds.Add(i0); inds.Add(i3); inds.Add(i2);
                inds.Add(i0); inds.Add(i1); inds.Add(i3);
            }

            return new GltfMesh(verts, inds.ToArray());
        }

        /// <summary>
        /// Emits one latitude ring for the <see cref="Capsule"/>: a circle of <paramref name="ringRadius"/> at
        /// height <paramref name="y"/>, with a smooth normal whose +Y component is <paramref name="cosPhi"/> and
        /// whose radial magnitude makes it unit length. Records the row start for triangle stitching.
        /// </summary>
        static void EmitHemiRing(System.Collections.Generic.List<ModelVertex> verts, int segments, float y,
            float ringRadius, float cosPhi, Vector4 white, float totalHeight, System.Collections.Generic.List<int> rowStarts)
        {
            rowStarts.Add(verts.Count);
            float v = totalHeight > 1e-6f ? y / totalHeight : 0f;
            float radialMag = MathF.Sqrt(MathF.Max(0f, 1f - cosPhi * cosPhi));
            for (int s = 0; s <= segments; s++)
            {
                float theta = MathF.Tau * s / segments;
                var radial = new Vector3(MathF.Cos(theta), 0f, MathF.Sin(theta));
                var pos = radial * ringRadius + new Vector3(0f, y, 0f);
                var nrm = Vector3.Normalize(radial * radialMag + new Vector3(0f, cosPhi, 0f));
                verts.Add(new ModelVertex(pos, nrm, white, new Vector2((float)s / segments, v)));
            }
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
            // planar disc UV: centre at (0.5,0.5), ring traced on the unit circle.
            ushort centerIdx = (ushort)verts.Count;
            verts.Add(new ModelVertex(center, normal, white, new Vector2(0.5f, 0.5f)));
            ushort ringStart = (ushort)verts.Count;
            for (int s = 0; s < segments; s++)
            {
                float a = MathF.Tau * s / segments;
                var p = center + new Vector3(MathF.Cos(a) * radius, 0f, MathF.Sin(a) * radius);
                var uv = new Vector2(0.5f + 0.5f * MathF.Cos(a), 0.5f + 0.5f * MathF.Sin(a));
                verts.Add(new ModelVertex(p, normal, white, uv));
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

            // per-face UV: corners a,b,c,d map to (0,0),(1,0),(1,1),(0,1).
            void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
            {
                ushort baseIdx = (ushort)vi;
                vertices[vi++] = new ModelVertex(a, n, white, new Vector2(0f, 0f));
                vertices[vi++] = new ModelVertex(b, n, white, new Vector2(1f, 0f));
                vertices[vi++] = new ModelVertex(c, n, white, new Vector2(1f, 1f));
                vertices[vi++] = new ModelVertex(d, n, white, new Vector2(0f, 1f));
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
