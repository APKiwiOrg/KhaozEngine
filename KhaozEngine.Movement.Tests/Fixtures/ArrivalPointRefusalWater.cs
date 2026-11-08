using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Tests.Movement.Fixtures;

/// <summary>The review's immutable producer. Trace geometry is dry, but point classification can refuse.</summary>
internal sealed class ArrivalPointRefusalWater : IMovementWaterField
{
    readonly IMovementWaterField _inner = MovementWaterField.Level("world", -3, -4);
    public string SemanticIdentity => "arrival-point-refusal";
    public long Generation => 0;
    public MovementWaterExtent Extent => _inner.Extent;

    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 origin)
    {
        double x = (double)body.Centre.X + origin.X;
        return x > 0.04 && x < 0.08
            ? new(MovementAvailability.PrecisionExceeded, default, null, false, 0, null)
            : _inner.SampleCentreWater(body, origin);
    }

    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 origin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
        _inner.TraceWater(query, origin, identity, spans, contacts);
}
