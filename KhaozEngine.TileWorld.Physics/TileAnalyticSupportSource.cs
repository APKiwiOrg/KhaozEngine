using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>Identity from the same private captured lattice used by the installed ground's analytic field.</summary>
internal sealed class TileAnalyticSupportSource(TileMovementSnapshot snapshot, string worldId) : IMovementAnalyticSupportSource
{
    readonly string _source = worldId + "/tile-ground-v1/" + snapshot.Ground.Fingerprint;
    readonly MovementSupportKey _owner = new(worldId, "tile-ground");
    readonly Func<float, float, float> _height = snapshot.Height;
    readonly Func<float, float, Vector3> _normal = snapshot.Normal;

    public bool Matches(Func<float, float, float> height, Func<float, float, Vector3>? normal, OrdinaryGroundSpace space) =>
        space == OrdinaryGroundSpace.World && height.Equals(_height) && normal is not null && normal.Equals(_normal);

    public MovementAvailability Describe(Vector2 absoluteAxis, out MovementSurfaceFeature feature)
    {
        feature = default;
        CapturedTileGround ground = snapshot.Ground;
        if (!ground.TryCoordinates(absoluteAxis.X, absoluteAxis.Y, out float tx, out float tz) ||
            tx != TileWorldSpace.TileX(absoluteAxis.X, ground.TileSize) ||
            tz != TileWorldSpace.TileZ(absoluteAxis.Y, ground.TileSize) ||
            tx < int.MinValue || tx >= int.MaxValue || tz < int.MinValue || tz >= int.MaxValue)
            return MovementAvailability.Unresolved;
        int x = (int)MathF.Floor(tx), z = (int)MathF.Floor(tz);
        int selected = DrawnGroundTriangle.Select(ground.Triangles(x, z), new(tx - x, tz - z), out _);
        return selected < 0 ? MovementAvailability.Unresolved : GetFeature(new(_source, Address(x, z), selected), out feature);
    }

    public MovementAvailability GetFeature(MovementSurfaceFeatureKey key, out MovementSurfaceFeature feature)
    {
        feature = default;
        if (!TryTriangle(key, out int x, out int z, out var triangle)) return MovementAvailability.Unresolved;
        Vector3 origin = TileWorldSpace.ToWorld(x, 0, z, snapshot.Ground.TileSize);
        feature = new(key, _owner, origin + triangle.A, origin + triangle.B, origin + triangle.C, triangle.Normal);
        return MovementAvailability.Known;
    }

    public bool AreAdjacent(MovementSurfaceFeatureKey first, MovementSurfaceFeatureKey second)
    {
        if (!TryTriangle(first, out int ax, out int az, out var a) ||
            !TryTriangle(second, out int bx, out int bz, out var b)) return false;
        if (first == second) return true;
        if (Math.Abs((long)ax - bx) > 1 || Math.Abs((long)az - bz) > 1) return false;
        Span<Lattice> left = stackalloc Lattice[3] { At(ax, az, a.PlanA), At(ax, az, a.PlanB), At(ax, az, a.PlanC) };
        Span<Lattice> right = stackalloc Lattice[3] { At(bx, bz, b.PlanA), At(bx, bz, b.PlanB), At(bx, bz, b.PlanC) };
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                if (SharedEdge(left[i], left[(i + 1) % 3], right[j], right[(j + 1) % 3])) return true;
        return false;
    }

    bool TryTriangle(MovementSurfaceFeatureKey key, out int x, out int z, out DrawnGroundTriangle triangle)
    {
        x = (int)(key.Address >> 32);
        z = unchecked((int)key.Address);
        triangle = default;
        if (!key.IsValid || !string.Equals(key.Source, _source, StringComparison.Ordinal)) return false;
        ReadOnlySpan<DrawnGroundTriangle> triangles = snapshot.Ground.Triangles(x, z);
        if ((uint)key.Part >= (uint)triangles.Length) return false;
        triangle = triangles[key.Part];
        return true;
    }

    static long Address(int x, int z) => ((long)x << 32) | (uint)z;
    readonly record struct Lattice(long X, long Z);
    static Lattice At(int x, int z, TileLatticePoint point)
    {
        Vector2 local = TileTriangulation.Local(point);
        return new(2L * x + (int)(2 * local.X), 2L * z + (int)(2 * local.Y));
    }

    // Positive edge overlap in the original integer half-tile lattice. This also handles a mid-edge cut
    // meeting a full edge on the next tile. A common point alone never establishes adjacency.
    static bool SharedEdge(Lattice a, Lattice b, Lattice c, Lattice d)
    {
        long x = b.X - a.X, z = b.Z - a.Z;
        if (x * (c.Z - a.Z) != z * (c.X - a.X) || x * (d.Z - a.Z) != z * (d.X - a.X)) return false;
        return x != 0
            ? Math.Max(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) < Math.Min(Math.Max(a.X, b.X), Math.Max(c.X, d.X))
            : Math.Max(Math.Min(a.Z, b.Z), Math.Min(c.Z, d.Z)) < Math.Min(Math.Max(a.Z, b.Z), Math.Max(c.Z, d.Z));
    }
}
