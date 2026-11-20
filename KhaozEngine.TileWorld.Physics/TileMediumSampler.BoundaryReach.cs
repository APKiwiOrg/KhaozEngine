using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

public sealed partial class TileMediumSampler : IMovementWaterBoundaryReachProof
{
    /// <summary>Certifies the water boundary question from the capture alone. Both trace kernels build every contact
    /// with a free surface and its own column's top as its level, and only from columns this reach counts, so the
    /// four producer guarantees hold. The capture verified one level per domain and its domain count when it was
    /// built. An empty reach answers <see cref="MovementWaterReach.Empty"/>. A reach whose columns share one surface
    /// bit for bit answers that surface. Two levels, more than 256 columns, unverified capture facts or a query the
    /// trace itself would refuse as invalid all decline.</summary>
    public MovementWaterReach CertifyBoundaryReach(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity)
    {
        if (!query.IsValid || !identity.IsValid || !float.IsFinite(physicsOrigin.X) || !float.IsFinite(physicsOrigin.Y) ||
            !float.IsFinite(physicsOrigin.Z) || query.Body.CurrentSpace.WorldId != _field.WorldId ||
            !_field.Facts.Hold || !TryCertifyBoundaryReach(query, physicsOrigin, out BoundaryReach reach))
            return MovementWaterReach.Declined;
        if (reach.Columns == 0) return MovementWaterReach.Empty;
        return reach.UniformLevel ? MovementWaterReach.FreeSurface(reach.HighestLevel) : MovementWaterReach.Declined;
    }

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
