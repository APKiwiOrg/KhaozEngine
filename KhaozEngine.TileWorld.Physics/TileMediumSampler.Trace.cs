using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

public sealed partial class TileMediumSampler
{
    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, in MovementQueryIdentity identity,
        Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        if (!query.IsValid || !identity.IsValid || query.Body.CurrentSpace.WorldId != _field.WorldId)
            return new(MovementAvailability.Invalid, 0, 0, 0, 0, 0, identity);
        return TileWaterColumnCoverage.Trace(_field.TraceColumns, _field.Boundaries, _field.Exterior, query, identity, spans, contacts);
    }
}
