using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

/// <summary>A tile producer whose full solve refuses every moving path with <paramref name="refusal"/>, while stationary
/// placement traces stay Known. The lift, traverse and seat proofs of a step are its moving paths, so the first of them
/// refuses after placement is proved. It forwards the producer's boundary reach certificate. A refusing trace reports no
/// contact, so the four producer guarantees still hold. Wrap it in a field that forwards only the trace to hide the
/// certificate.</summary>
internal sealed class RouteRefusingWaterField(TileMediumSampler inner, MovementAvailability refusal)
    : IMovementWaterBoundaryReachProof
{
    internal int Refused { get; private set; }
    public string SemanticIdentity => inner.SemanticIdentity;
    public long Generation => inner.Generation;
    public MovementWaterExtent Extent => inner.Extent;

    public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin) =>
        inner.SampleCentreWater(body, physicsOrigin);

    public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
    {
        if (query.Delta == Vector3.Zero) return inner.TraceWater(query, physicsOrigin, identity, spans, contacts);
        Refused++;
        return new(refusal, 0, 0, 0, 0, 0, identity);
    }

    public MovementWaterReach CertifyBoundaryReach(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
        in MovementQueryIdentity identity) => inner.CertifyBoundaryReach(query, physicsOrigin, identity);
}
