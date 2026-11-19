#if CONTACT_SHORE_ATTRIBUTION
using KhaozEngine.Physics;
#endif
using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

public sealed partial class TileMediumSampler
{
    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
#if CONTACT_SHORE_ATTRIBUTION
        using var shoreStep = ShoreStepProbe.Begin(ShorePhase.WaterSampler);
#endif
        if (!query.IsValid || !identity.IsValid || !float.IsFinite(physicsOrigin.X) || !float.IsFinite(physicsOrigin.Y) ||
            !float.IsFinite(physicsOrigin.Z) || query.Body.CurrentSpace.WorldId != _field.WorldId)
            return new(MovementAvailability.Invalid, 0, 0, 0, 0, 0, identity);
        if (_field.CanTraceFlat(query, physicsOrigin))
            return TraceFlat(query, physicsOrigin, identity, spans, contacts);
        return TracePrisms(query, physicsOrigin, identity, spans, contacts);
    }
}
