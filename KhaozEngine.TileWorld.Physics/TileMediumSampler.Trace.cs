using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

public sealed partial class TileMediumSampler
{
    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        if (!query.IsValid || !identity.IsValid || !float.IsFinite(physicsOrigin.X) || !float.IsFinite(physicsOrigin.Y) ||
            !float.IsFinite(physicsOrigin.Z) || query.Body.CurrentSpace.WorldId != _field.WorldId)
            return new(MovementAvailability.Invalid, 0, 0, 0, 0, 0, identity);
        return TileWaterColumnCoverage.Trace(_field.TraceColumns, _field.Boundaries, _field.Exterior, query, physicsOrigin,
            identity, spans, contacts);
    }
}
