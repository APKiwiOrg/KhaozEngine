using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

public sealed partial class TileMediumSampler
{
    /// <summary>The most columns one reach certificate collects, the prism kernel's own candidate limit.</summary>
    internal const int MaxReachColumns = MovementQueryLease.MaxDomainContacts;

    /// <summary>What the captured columns within one path's reach say about water levels: how many there are,
    /// whether all of them carry the same surface bits, and the highest surface among them. With no column the
    /// highest surface is negative infinity.</summary>
    internal readonly record struct BoundaryReach(int Columns, bool UniformLevel, float HighestLevel);

    /// <summary>Counts the captured columns whose lateral projection meets the directed lateral bounds of the whole
    /// framed capsule path. That is exactly the candidate set the prism kernel selects its prisms and faces from,
    /// through the same index and bounds. It also covers the flat kernel's selection, because every merged flat
    /// column is an exact union of captured columns of one domain, one bed and one top, with the same lateral
    /// limits and an extra vertical test that only removes candidates. Every comparison is exact. More than
    /// <see cref="MaxReachColumns"/> columns returns false, as the prism kernel refuses beyond them.</summary>
    internal bool TryCertifyBoundaryReach(in MovementMediumSweepQuery query, Vector3 physicsOrigin, out BoundaryReach reach)
    {
        reach = default;
        Span<int> collected = stackalloc int[MaxReachColumns];
        int count = CollectReach(query, physicsOrigin, collected);
        if (count < 0) return false;
        bool uniform = true;
        float highest = float.NegativeInfinity;
        int first = 0;
        for (int i = 0; i < count; i++)
        {
            float top = _field.Columns[collected[i]].Maximum.Y;
            int bits = BitConverter.SingleToInt32Bits(top);
            if (i == 0) first = bits;
            else if (bits != first) uniform = false;
            if (top > highest) highest = top;
        }
        reach = new(count, uniform, highest);
        return true;
    }

    int CollectReach(in MovementMediumSweepQuery query, Vector3 physicsOrigin, Span<int> collected) =>
        _field.PointIndex.Collect(new TileWaterPathBounds(query, physicsOrigin), collected);

    internal TileWaterCaptureFacts CaptureFacts => _field.Facts;
}
