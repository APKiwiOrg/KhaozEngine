using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ArrivalProofInvalidationTests
{
    [Fact]
    public void RevokedReadFallsBackAtTheSameFloatIntervalDecision()
    {
        // #1729. Losing the batch proof must neither throw nor restart the already-decided interval.
        var expectedField = new RevokedField();
        var actualField = new RevokedField();
        using var expectedScene = new FlatArrivalScene(water: expectedField);
        using var actualScene = new FlatArrivalScene(water: actualField);
        MoveTuning tuning = expectedScene.Tuning;
        var body = new MoveState { Position = new(0, tuning.CapsuleHalfHeight, 0), Grounded = true };
        var target = ReachTarget.Point(body.Position + new Vector3(0.4f, 0, 0));
        MoveState predicted = expectedScene.Context.Step(body, Vector2.UnitX, false, 0.1f, tuning);
        int expectedAdmissions = 0, actualAdmissions = 0;
        bool ExpectedAdmits(in MoveState from, in MoveState to, in MoveTuning shape)
        {
            if (++expectedAdmissions == 1) expectedField.Revoke();
            return ArrivalRangeCrossingTests.Admit(from, to, shape);
        }
        bool ActualAdmits(in MoveState from, in MoveState to, in MoveTuning shape)
        {
            if (++actualAdmissions == 1) actualField.Revoke();
            return ArrivalRangeCrossingTests.Admit(from, to, shape);
        }
        Vector2 expected = ArrivalReference.Stop(body, tuning, target, 0.1f, false, 0.1f,
            expectedScene.Context, Vector2.UnitX, predicted, ExpectedAdmits, out int count);
        using var work = new ArrivalPreviewDiagnostics();
        Vector2 actual = RangeApproachCore.StopAtRange(body, tuning, target, 0.1f, false, 0.1f,
            actualScene.Context, Vector2.UnitX, predicted, ActualAdmits);
        ArrivalRangeEquivalenceTests.Same(expected, actual);
        ArrivalRangeEquivalenceTests.Same(expectedScene.Context.Step(body, expected, false, 0.1f, tuning),
            actualScene.Context.Step(body, actual, false, 0.1f, tuning));
        Assert.Equal(1, work.CertifiedTrials);
        Assert.Equal(count - 1, work.FullSteps);
        Assert.True(work.FullSteps > 0);
    }

    sealed class RevokedField : IMovementDryTracePointProof
    {
        readonly IMovementWaterField _inner = MovementWaterField.Level("world", -3, -4);
        public string SemanticIdentity => "arrival-revoked-interval";
        public long Generation { get; private set; }
        public MovementWaterExtent Extent => _inner.Extent;
        public bool CanClassifyDryTracePoints(in MovementMediumSweepQuery query, Vector3 physicsOrigin) =>
            ((IMovementDryTracePointProof)_inner).CanClassifyDryTracePoints(query, physicsOrigin);
        internal void Revoke() => Generation++;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin) =>
            _inner.SampleCentreWater(body, physicsOrigin);
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
            Generation == 0 ? _inner.TraceWater(query, physicsOrigin, identity, spans, contacts)
                : new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, identity);
    }
}
