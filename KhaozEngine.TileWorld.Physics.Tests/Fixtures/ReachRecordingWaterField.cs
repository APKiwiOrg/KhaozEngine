using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

/// <summary>A tile sampler behind a wrapper that forwards the boundary reach certificate. It records whether the
/// certificate was consulted, can throw from it, and can advance its generation to make a held pin stale.</summary>
internal sealed class ReachRecordingWaterField(TileMediumSampler inner) : IMovementWaterBoundaryReachProof
{
    internal int Consulted { get; private set; }
    internal Func<Exception>? Fault { get; set; }
    internal long Revision { get; set; }
    public string SemanticIdentity => inner.SemanticIdentity;
    public long Generation => Revision;
    public MovementWaterExtent Extent => inner.Extent;

    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin) =>
        inner.SampleCentreWater(body, physicsOrigin);

    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
        inner.TraceWater(query, physicsOrigin, identity, spans, contacts);

    public MovementWaterReach CertifyBoundaryReach(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity)
    {
        Consulted++;
        if (Fault is { } fault) throw fault();
        return inner.CertifyBoundaryReach(query, physicsOrigin, identity);
    }
}
