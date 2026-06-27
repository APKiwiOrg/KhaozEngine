using System;
using System.Numerics;
using KhaozEngine.Collision;

namespace KhaozEngine.Render3D
{
    /// <summary>Tunables for <see cref="PropSurfaceBake"/>.</summary>
    public sealed class PropSurfaceBakeOptions
    {
        /// <summary>Grid cell edge (metres) of the baked height map. Default 0.25.</summary>
        public float CellSize = 0.25f;
        /// <summary>Max grid dimension (cells) per axis; a larger footprint widens the cell instead. Default 64.</summary>
        public int MaxGrid = 64;
        /// <summary>A mesh no taller than this is a small solid (rock/crate) and is always a walkable solid; taller
        /// meshes are classified by footprint spread (tree-like canopy -> not walkable). Default 2.5.</summary>
        public float SolidHeightMeters = 2.5f;
        public static readonly PropSurfaceBakeOptions Default = new();
    }

    /// <summary>
    /// Derives a render-free top-down max-height grid (<see cref="PropSurface"/>) from a
    /// <see cref="PropLoader"/>-normalized prop mesh (base y=0, XZ centred on origin), and classifies whether a
    /// prop is a walkable solid (rock/log/building -> a surface you stand on) or a thin blocker (tree -> no surface,
    /// keeps its trunk collider). The grid is the unit prop; placement scale/yaw are applied at query time by
    /// <see cref="WorldSurface"/>. Offline/tooling (uses the mesh); the runtime reads the baked binary.
    /// </summary>
    public static class PropSurfaceBake
    {
        /// <summary>True when the prop is a walkable solid (short, or solid without a much-wider upper spread);
        /// false for a tall thin trunk-with-canopy (tree).</summary>
        public static bool IsWalkableSolid(GltfMesh normalizedMesh, PropSurfaceBakeOptions? options = null)
        {
            PropSurfaceBakeOptions o = options ?? PropSurfaceBakeOptions.Default;
            Bounds(normalizedMesh, out float minY, out float maxY, out _, out _, out _, out _);
            float height = maxY - minY;
            if (height <= o.SolidHeightMeters) return true; // small solid

            // Tall: walkable only if it does not widen a lot above a low slice (a building's vertical walls keep a
            // near-constant footprint; a tree's canopy is much wider than its trunk).
            FootprintHalf(normalizedMesh, minY + 1.0f, out float lowHx, out float lowHz);   // trunk/base slice
            FootprintHalf(normalizedMesh, maxY, out float fullHx, out float fullHz);        // whole prop
            float lowR = MathF.Max(lowHx, lowHz), fullR = MathF.Max(fullHx, fullHz);
            return fullR <= lowR * 1.6f; // canopy spreads > 1.6x -> tree -> not walkable
        }

        /// <summary>Rasterize the top-down max-height grid over the mesh footprint: each triangle is projected to
        /// XZ and the cells it covers take the max of its barycentric-interpolated Y, so the grid is the highest
        /// surface above each cell (a proper top contour; uncovered cells stay NaN).</summary>
        public static PropSurface Bake(GltfMesh normalizedMesh, PropSurfaceBakeOptions? options = null)
        {
            if (normalizedMesh == null) throw new ArgumentNullException(nameof(normalizedMesh));
            PropSurfaceBakeOptions o = options ?? PropSurfaceBakeOptions.Default;
            Bounds(normalizedMesh, out _, out _, out float minX, out float maxX, out float minZ, out float maxZ);

            float spanX = MathF.Max(1e-3f, maxX - minX), spanZ = MathF.Max(1e-3f, maxZ - minZ);
            float cell = o.CellSize;
            int w = Math.Clamp((int)MathF.Ceiling(spanX / cell) + 1, 2, o.MaxGrid);
            int h = Math.Clamp((int)MathF.Ceiling(spanZ / cell) + 1, 2, o.MaxGrid);
            cell = MathF.Max(spanX / (w - 1), spanZ / (h - 1)); // widen the cell if clamped so the grid still covers

            var heights = new float[w * h];
            for (int k = 0; k < heights.Length; k++) heights[k] = float.NaN;

            ModelVertex[] verts = normalizedMesh.Vertices;
            uint[] idx = normalizedMesh.Indices32;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                Vector3 a = verts[idx[t]].Position, b = verts[idx[t + 1]].Position, c = verts[idx[t + 2]].Position;
                // XZ-projected edge vectors; skip a degenerate (zero-area) triangle.
                float v0x = b.X - a.X, v0z = b.Z - a.Z;
                float v1x = c.X - a.X, v1z = c.Z - a.Z;
                float denom = v0x * v1z - v1x * v0z;
                if (MathF.Abs(denom) < 1e-9f) continue;
                float inv = 1f / denom;

                int i0 = Math.Clamp((int)MathF.Floor((MathF.Min(a.X, MathF.Min(b.X, c.X)) - minX) / cell), 0, w - 1);
                int i1 = Math.Clamp((int)MathF.Ceiling((MathF.Max(a.X, MathF.Max(b.X, c.X)) - minX) / cell), 0, w - 1);
                int j0 = Math.Clamp((int)MathF.Floor((MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - minZ) / cell), 0, h - 1);
                int j1 = Math.Clamp((int)MathF.Ceiling((MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) - minZ) / cell), 0, h - 1);

                for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    float px = minX + i * cell, pz = minZ + j * cell;
                    // Barycentric of (px,pz) in the XZ triangle.
                    float dpx = px - a.X, dpz = pz - a.Z;
                    float vb = (dpx * v1z - v1x * dpz) * inv;   // weight of b
                    float vc = (v0x * dpz - dpx * v0z) * inv;   // weight of c
                    float va = 1f - vb - vc;                    // weight of a
                    const float eps = -1e-4f;
                    if (va < eps || vb < eps || vc < eps) continue; // outside this triangle
                    float y = va * a.Y + vb * b.Y + vc * c.Y;
                    int cellIdx = j * w + i;
                    if (float.IsNaN(heights[cellIdx]) || y > heights[cellIdx]) heights[cellIdx] = y;
                }
            }
            return new PropSurface(w, h, cell, minX, minZ, heights);
        }

        static void Bounds(GltfMesh m, out float minY, out float maxY, out float minX, out float maxX, out float minZ, out float maxZ)
        {
            minX = minY = minZ = float.MaxValue; maxX = maxY = maxZ = float.MinValue;
            foreach (ModelVertex v in m.Vertices)
            {
                Vector3 p = v.Position;
                minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
                minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
                minZ = MathF.Min(minZ, p.Z); maxZ = MathF.Max(maxZ, p.Z);
            }
        }

        static void FootprintHalf(GltfMesh m, float captureTopY, out float hx, out float hz)
        {
            hx = hz = 0f;
            foreach (ModelVertex v in m.Vertices)
                if (v.Position.Y <= captureTopY)
                { hx = MathF.Max(hx, MathF.Abs(v.Position.X)); hz = MathF.Max(hz, MathF.Abs(v.Position.Z)); }
        }
    }
}
