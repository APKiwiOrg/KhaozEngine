using System;
using System.Numerics;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>
/// The floor of a tile world on plane 0, read analytically from the same drawn ground triangles
/// <see cref="TileWorldColliders"/> registers, so a height or normal from here agrees with a ray cast against the
/// ground mesh. Only the drawn ground is the floor. Walk surfaces and other object tops hold bodies up as physics
/// statics and never appear here.
/// <para>Three cases, with no search and no allocation per call:</para>
/// <list type="bullet">
/// <item>A point on a drawable tile takes the height and unit up normal of the ground triangle under it.</item>
/// <item>A point outside the rectangle bounding the regions loaded at build is first moved just inside that
/// rectangle, so the floor carries on level from the world's edge.</item>
/// <item>A point on a tile with no ground (no underlay, <see cref="TileSettings.NoDraw"/>, or a region missing inside
/// the rectangle) takes the document's lattice height, <see cref="TileWorldDocument.HeightAt"/>, with a straight up
/// normal.</item>
/// </list>
/// It never answers NaN. A point infinitely far out clamps like any other, and a NaN point, or any point in a world
/// with no loaded regions, answers height 0 with a straight up normal. The sampler reads the document it was built
/// from on every call, but its rectangle was captured at build and the colliders it has to agree with were built
/// then too, so a document edited after <see cref="TileWorldColliders.Build"/> needs a rebuild.
/// <para>It is read-only after construction, with no cache and no shared scratch buffer, so several threads may call
/// it at once as long as nothing edits the document meanwhile.</para>
/// </summary>
public sealed class TileGroundSampler
{
    // The one plane this round describes.
    const int Plane = 0;

    readonly TileWorldDocument _document;
    readonly float _tileSize;
    readonly bool _bounded;
    // The loaded rectangle in tile coordinates, the far edges pulled to the last float inside it.
    readonly float _minX, _maxX, _minZ, _maxZ;

    internal TileGroundSampler(TileWorldDocument document, RegionCoord[] regions)
    {
        _document = document;
        _tileSize = document.TileSize;
        if (regions.Length > 0)
        {
            int minRx = int.MaxValue, maxRx = int.MinValue, minRz = int.MaxValue, maxRz = int.MinValue;
            foreach (RegionCoord region in regions)
            {
                minRx = Math.Min(minRx, region.Rx);
                maxRx = Math.Max(maxRx, region.Rx);
                minRz = Math.Min(minRz, region.Rz);
                maxRz = Math.Max(maxRz, region.Rz);
            }
            _bounded = true;
            _minX = (float)minRx * TileRegion.Size;
            _minZ = (float)minRz * TileRegion.Size;
            _maxX = MathF.BitDecrement((float)(maxRx + 1L) * TileRegion.Size);
            _maxZ = MathF.BitDecrement((float)(maxRz + 1L) * TileRegion.Size);
        }
        HeightDelegate = HeightAt;
        NormalDelegate = NormalAt;
    }

    /// <summary><see cref="HeightAt"/> as the ground-height delegate the movement step takes, created once.</summary>
    public Func<float, float, float> HeightDelegate { get; }

    /// <summary><see cref="NormalAt"/> as the ground-normal delegate the movement step takes, created once.</summary>
    public Func<float, float, Vector3> NormalDelegate { get; }

    /// <summary>The floor's height in metres at a world point.</summary>
    /// <param name="worldX">World x in metres.</param>
    /// <param name="worldZ">World z in metres, which runs against tile z.</param>
    public float HeightAt(float worldX, float worldZ)
    {
        if (!TryLoadedTile(worldX, worldZ, out float tileX, out float tileZ)) return 0f;
        return TryTriangle(tileX, tileZ, out GroundTriangle triangle)
            ? triangle.Height
            : _document.HeightAt(TileWorldSpace.WorldX(tileX, _tileSize), TileWorldSpace.WorldZ(tileZ, _tileSize), Plane);
    }

    /// <summary>The floor's unit normal at a world point, always pointing up: the normal of the ground triangle under
    /// it, or <see cref="Vector3.UnitY"/> where no ground is drawn.</summary>
    /// <param name="worldX">World x in metres.</param>
    /// <param name="worldZ">World z in metres, which runs against tile z.</param>
    public Vector3 NormalAt(float worldX, float worldZ)
    {
        if (!TryLoadedTile(worldX, worldZ, out float tileX, out float tileZ)) return Vector3.UnitY;
        return TryTriangle(tileX, tileZ, out GroundTriangle triangle) ? triangle.Normal : Vector3.UnitY;
    }

    // The point in tile coordinates, clamped into the loaded rectangle. False, before any document read, when there
    // is no rectangle or the point is NaN, which the clamp passes through.
    bool TryLoadedTile(float worldX, float worldZ, out float tileX, out float tileZ)
    {
        tileX = Math.Clamp(TileWorldSpace.TileX(worldX, _tileSize), _minX, _maxX);
        tileZ = Math.Clamp(TileWorldSpace.TileZ(worldZ, _tileSize), _minZ, _maxZ);
        return _bounded && float.IsFinite(tileX) && float.IsFinite(tileZ);
    }

    // The ground triangle under a tile-space point, chosen among its tile's triangles as the one whose smallest
    // barycentric weight is largest, so a point on a shared edge takes either neighbour and float error never leaves
    // it with none. False when the tile draws no ground.
    bool TryTriangle(float tileX, float tileZ, out GroundTriangle triangle)
    {
        int x = (int)MathF.Floor(tileX), z = (int)MathF.Floor(tileZ);
        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        if (!TileGroundTriangles.TryDescribe(_document, x, z, Plane, out TileGroundCell cell, triangles))
        {
            triangle = default;
            return false;
        }

        var local = new Vector2(tileX - x, tileZ - z);
        int best = 0;
        Vector3 weights = default;
        float bestMin = float.NegativeInfinity;
        for (int i = 0; i < cell.TriangleCount; i++)
        {
            Vector3 w = Weights(local, TileTriangulation.Local(triangles[i].A), TileTriangulation.Local(triangles[i].B),
                                TileTriangulation.Local(triangles[i].C));
            float min = MathF.Min(w.X, MathF.Min(w.Y, w.Z));
            if (min > bestMin)
            {
                bestMin = min;
                best = i;
                weights = w;
            }
        }

        // Placed relative to the tile's own corner, so the normal's edge vectors stay exact however far out the tile
        // is. A lattice height does not depend on the origin, so the heights are the ones the ground mesh holds.
        TileLatticeTriangle t = triangles[best];
        triangle = new GroundTriangle(
            TileGroundTriangles.LatticePosition(_document, x, z, Plane, t.A, x, z),
            TileGroundTriangles.LatticePosition(_document, x, z, Plane, t.B, x, z),
            TileGroundTriangles.LatticePosition(_document, x, z, Plane, t.C, x, z),
            weights);
        return true;
    }

    // Barycentric weights of p on the tile-local triangle (a, b, c). Lattice triangles always have area in plan, so
    // the determinant is never zero. Tile-local coordinates and metre positions relative to the tile's corner differ
    // only by scale and a flip of z, so the weights hold for both.
    static Vector3 Weights(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 ab = b - a, ac = c - a, ap = p - a;
        float determinant = ab.X * ac.Y - ac.X * ab.Y;
        float wb = (ap.X * ac.Y - ac.X * ap.Y) / determinant;
        float wc = (ab.X * ap.Y - ap.X * ab.Y) / determinant;
        return new Vector3(1f - wb - wc, wb, wc);
    }

    // One ground triangle in metres relative to its tile's corner, and a point's weights on it.
    readonly record struct GroundTriangle(Vector3 A, Vector3 B, Vector3 C, Vector3 Weights)
    {
        public float Height => Weights.X * A.Y + Weights.Y * B.Y + Weights.Z * C.Y;

        // Lattice triangles are never vertical, so the cross product always has a Y to orient by.
        public Vector3 Normal
        {
            get
            {
                Vector3 n = Vector3.Cross(B - A, C - A);
                return Vector3.Normalize(n.Y < 0f ? -n : n);
            }
        }
    }
}
