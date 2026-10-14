using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Locomotion.Fixtures;

namespace KhaozEngine.Tests.NetWorld.Fixtures;

// Reuses Task 2's independent finite-volume oracle for the exact stationary shore tangent.
internal sealed class OrdinaryShoreField : IMovementWaterField, IDisposable
{
    readonly AnalyticMovementEnvironment oracle = new(
        [new("ordinary", new(new(-16), new(16)))],
        [new("lake", "ordinary", new(new(1, -4, -8), new(8, 1, 8)), 1)])
    { CertifyAxisSeparation = true };
    public string SemanticIdentity => "ordinary-shore";
    public long Generation => 0;
    public MovementWaterExtent Extent => new(new(1, -4, -8), new(8, 1, 8));
    internal bool Ready { get; set; } = true;

    internal OrdinaryShoreField()
    {
        using var read = oracle.Acquire(null);
    }

    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin)
    {
        if (!Ready || physicsOrigin != Vector3.Zero)
            return new(MovementAvailability.Unresolved, body.CurrentSpace, null, false, 1, null);
        return oracle.Acquisition.OnBodySample!(body);
    }

    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        if (!Ready || physicsOrigin != Vector3.Zero) return new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, identity);
        var result = oracle.Acquisition.OnCoverage!(query, spans, contacts);
        return new(result.Availability, result.SpansWritten, result.RequiredSpanCapacity, result.ContactsWritten,
            result.RequiredContactCapacity, result.CertifiedErrorMetres, identity);
    }

    public void Dispose() => oracle.Dispose();
}
