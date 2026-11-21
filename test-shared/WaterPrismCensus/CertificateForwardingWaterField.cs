using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.WaterPrismCensus;

/// <summary>A recording field's traces with its tile producer's boundary reach certificate exposed. The recording field
/// alone hides the certificate, so the same scenario runs every boundary proof through the full solve with it and
/// through the certificate with this, while every trace that still runs is recorded the same way.</summary>
internal sealed class CertificateForwardingWaterField(RecordingWaterField traces, TileMediumSampler producer)
    : IMovementWaterBoundaryReachProof
{
    public string SemanticIdentity => traces.SemanticIdentity;
    public long Generation => traces.Generation;
    public MovementWaterExtent Extent => traces.Extent;
    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin) =>
        traces.SampleCentreWater(body, physicsOrigin);

    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
        traces.TraceWater(query, physicsOrigin, identity, spans, contacts);

    /// <summary>Sees every certificate answer the producer gives, declines included.</summary>
    internal Action<MovementWaterReach>? Answered = null;

    public MovementWaterReach CertifyBoundaryReach(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity)
    {
        MovementWaterReach reach = producer.CertifyBoundaryReach(query, physicsOrigin, identity);
        Answered?.Invoke(reach);
        return reach;
    }
}
