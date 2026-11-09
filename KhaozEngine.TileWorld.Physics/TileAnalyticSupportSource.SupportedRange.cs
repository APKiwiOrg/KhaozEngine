using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

internal sealed partial class TileAnalyticSupportSource : IMovementSupportedAnalyticSupportSource
{
    public MovementAvailability CaptureSupportedRegion(Vector2 minimum, Vector2 maximum,
        out IMovementSupportedGroundRegion? region)
    {
        region = null;
        if (!float.IsFinite(minimum.X) || !float.IsFinite(minimum.Y) || !float.IsFinite(maximum.X) ||
            !float.IsFinite(maximum.Y) || minimum.X > maximum.X || minimum.Y > maximum.Y)
            return MovementAvailability.Invalid;
        CapturedTileGround ground = snapshot.Ground;
        if (!Map(minimum, out int ax, out int az) || !Map(maximum, out int bx, out int bz))
            return MovementAvailability.Unresolved;
        int minX = Math.Min(ax, bx), maxX = Math.Max(ax, bx), minZ = Math.Min(az, bz), maxZ = Math.Max(az, bz);
        long width = (long)maxX - minX + 1, depth = (long)maxZ - minZ + 1;
        if (width > MovementScopeWitness.MaxDependencyVisits || depth > MovementScopeWitness.MaxDependencyVisits ||
            width * depth > MovementScopeWitness.MaxDependencyVisits) return MovementAvailability.CapacityExceeded;
        var cells = new Dictionary<(int, int), DrawnGroundTriangle[]>();
        float low = float.PositiveInfinity, high = float.NegativeInfinity, upward = 1;
        int visits = 0;
        for (long z = minZ; z <= maxZ; z++) for (long x = minX; x <= maxX; x++)
        {
            var triangles = ground.Triangles((int)x, (int)z);
            if (triangles.IsEmpty) return MovementAvailability.Unresolved;
            foreach (var triangle in triangles)
            {
                if (++visits > MovementScopeWitness.MaxDependencyVisits) return MovementAvailability.CapacityExceeded;
                float a = triangle.A.Y, b = triangle.B.Y, c = triangle.C.Y;
                Vector3 normal = triangle.Normal;
                if (!float.IsFinite(a) || !float.IsFinite(b) || !float.IsFinite(c) || !float.IsFinite(normal.Y))
                    return MovementAvailability.Unresolved;
                low = MathF.Min(low, MathF.Min(a, MathF.Min(b, c)));
                high = MathF.Max(high, MathF.Max(a, MathF.Max(b, c)));
                upward = MathF.Min(upward, normal.Y);
            }
            cells.Add(((int)x, (int)z), triangles.ToArray());
        }
        region = new TileSupportedGroundRegion(ground.TileSize, minimum, maximum, cells, low, high, upward);
        return MovementAvailability.Known;

        bool Map(Vector2 axis, out int x, out int z)
        {
            x = z = 0;
            if (!ground.TryCoordinates(axis.X, axis.Y, out float tx, out float tz) ||
                tx != TileWorldSpace.TileX(axis.X, ground.TileSize) || tz != TileWorldSpace.TileZ(axis.Y, ground.TileSize) ||
                tx < int.MinValue || tx >= int.MaxValue || tz < int.MinValue || tz >= int.MaxValue) return false;
            x = (int)MathF.Floor(tx);
            z = (int)MathF.Floor(tz);
            return true;
        }
    }
}

/// <summary>Only captured cells participate. The drawn selector and expression also serve ordinary sampling.</summary>
internal sealed class TileSupportedGroundRegion(float tileSize, Vector2 minimum, Vector2 maximum,
    Dictionary<(int, int), DrawnGroundTriangle[]> cells, float low, float high, float upward) : IMovementSupportedGroundRegion
{
    public float MinimumHeight => low;
    public float MaximumHeight => high;
    public float MinimumNormalY => upward;
    public MovementAvailability Sample(Vector2 absoluteAxis, out float height, out Vector3 normal)
    {
        height = default;
        normal = default;
        if (!float.IsFinite(absoluteAxis.X) || !float.IsFinite(absoluteAxis.Y) ||
            absoluteAxis.X < minimum.X || absoluteAxis.X > maximum.X ||
            absoluteAxis.Y < minimum.Y || absoluteAxis.Y > maximum.Y) return MovementAvailability.Unresolved;
        float tx = TileWorldSpace.TileX(absoluteAxis.X, tileSize), tz = TileWorldSpace.TileZ(absoluteAxis.Y, tileSize);
        int x = (int)MathF.Floor(tx), z = (int)MathF.Floor(tz);
        if (!cells.TryGetValue((x, z), out var triangles)) return MovementAvailability.Unresolved;
        int selected = DrawnGroundTriangle.Select(triangles, new(tx - x, tz - z), out var weights);
        if (selected < 0) return MovementAvailability.Unresolved;
        height = triangles[selected].Height(weights);
        normal = triangles[selected].Normal;
        return MovementAvailability.Known;
    }
}
