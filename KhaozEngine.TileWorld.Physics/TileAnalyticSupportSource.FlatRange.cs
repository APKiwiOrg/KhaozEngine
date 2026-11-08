using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

internal sealed partial class TileAnalyticSupportSource
{
    public MovementAvailability CertifyFlatRegion(Vector2 minimum, Vector2 maximum, out float height)
    {
        height = default;
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
        bool found = false;
        int visits = 0;
        for (long z = minZ; z <= maxZ; z++) for (long x = minX; x <= maxX; x++)
        {
            var triangles = ground.Triangles((int)x, (int)z);
            if (triangles.IsEmpty) return MovementAvailability.Unresolved;
            foreach (var triangle in triangles)
            {
                if (++visits > MovementScopeWitness.MaxDependencyVisits) return MovementAvailability.CapacityExceeded;
                if (!found) { height = triangle.A.Y; found = true; }
                int bits = BitConverter.SingleToInt32Bits(height);
                // Height's final clamp is [h,h], so all barycentric evaluations return exactly h. Negative zero
                // is declined because its arithmetic/clamp sign need not equal the authored vertex's sign.
                if (!float.IsFinite(height) || bits == int.MinValue || triangle.Normal != Vector3.UnitY ||
                    BitConverter.SingleToInt32Bits(triangle.A.Y) != bits ||
                    BitConverter.SingleToInt32Bits(triangle.B.Y) != bits ||
                    BitConverter.SingleToInt32Bits(triangle.C.Y) != bits) return MovementAvailability.Unresolved;
            }
        }
        return found ? MovementAvailability.Known : MovementAvailability.Unresolved;

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
