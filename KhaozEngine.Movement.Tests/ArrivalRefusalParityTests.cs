using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ArrivalRefusalParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APointProofCannotHideARefusedContainedTrace(bool sloped)
    {
        // #1754 R1. The endpoint and enclosure succeed, but a shorter placement trace refuses.
        using var scene = new FlatArrivalScene(water: new RefusedSubtraceWater(), slope: sloped ? 0.125f : null);
        MoveTuning tuning = scene.Tuning;
        var body = new MoveState { Position = new(0, tuning.CapsuleHalfHeight, 0), Grounded = true };
        MoveState predicted = scene.Context.Step(body, Vector2.UnitX, false, 0.1f, tuning);
        Assert.True(predicted.SupportGranted);
        Assert.True(predicted.Position.X > body.Position.X);
        MoveState refused = scene.Context.Step(body, Vector2.UnitX * 0.1875f, false, 0.1f, tuning);
        Assert.False(refused.SupportGranted);
        ArrivalRangeEquivalenceTests.Same(body.Position, refused.Position);
        var target = ReachTarget.Point(new(0.4f, tuning.CapsuleHalfHeight, 0));
        var trials = new List<MoveState>();
        int observed = 0;
        bool Observe(in MoveState from, in MoveState to, in MoveTuning shape)
        {
            Assert.True(observed < trials.Count);
            ArrivalRangeEquivalenceTests.Same(trials[observed++], to);
            return ArrivalRangeCrossingTests.Admit(from, to, shape);
        }
        Vector2 expected = ArrivalReference.Stop(body, tuning, target, 0.1f, false, 0.1f, scene.Context,
            Vector2.UnitX, predicted, ArrivalRangeCrossingTests.Admit, out int count, trials.Add);
        using var work = new ArrivalPreviewDiagnostics();
        Vector2 actual = RangeApproachCore.StopAtRange(body, tuning, target, 0.1f, false, 0.1f, scene.Context,
            Vector2.UnitX, predicted, Observe);
        Assert.Equal(count, observed);
        ArrivalRangeEquivalenceTests.Same(expected, actual);
        ArrivalRangeEquivalenceTests.Same(scene.Context.Step(body, expected, false, 0.1f, tuning),
            scene.Context.Step(body, actual, false, 0.1f, tuning));
        Assert.True(work.FullSteps > 0);
        Assert.Equal(count, work.FullSteps + work.CertifiedTrials);
    }

    [Theory]
    [InlineData(MovementAvailability.Unresolved, MovementStepOutcome.EnvironmentUnresolved)]
    [InlineData(MovementAvailability.Stale, MovementStepOutcome.EnvironmentUnresolved)]
    [InlineData(MovementAvailability.Invalid, MovementStepOutcome.EnvironmentInvalid)]
    [InlineData(MovementAvailability.CapacityExceeded, MovementStepOutcome.EnvironmentUnresolved)]
    [InlineData(MovementAvailability.PrecisionExceeded, MovementStepOutcome.PrecisionRefused)]
    public void EveryCoverageRefusalRetainsTheOriginalArrival(MovementAvailability refusal, MovementStepOutcome outcome)
    {
        // #1729. The optional certificate must not turn any refusal into an admissible trial.
        using var scene = new FlatArrivalScene(water: new RefusingWater(refusal));
        MoveTuning tuning = scene.Tuning;
        var body = new MoveState { Position = new(0, tuning.CapsuleHalfHeight, 0), Grounded = true };
        MoveState predicted = scene.Context.Step(body, Vector2.UnitX, false, 0.1f, tuning);
        var binding = scene.Context.ResolvedBinding;
        var frame = scene.Context.NavigationFrame;
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var lease));
        using (lease)
            Assert.Equal(outcome, binding.StepTowards(new(body, frame, null), Vector2.UnitX, false, 0.1f, lease!).Outcome);
        var target = ReachTarget.Point(predicted.Position);
        var random = new Random(172914);
        for (int sample = 0; sample < 16; sample++)
        {
            var command = new Vector2(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f);
            Vector2 expected = ArrivalReference.Stop(body, tuning, target, 0, false, 0.1f, scene.Context,
                command, predicted, ArrivalRangeCrossingTests.Admit, out int count);
            using var work = new ArrivalPreviewDiagnostics();
            Vector2 actual = RangeApproachCore.StopAtRange(body, tuning, target, 0, false, 0.1f, scene.Context,
                command, predicted, ArrivalRangeCrossingTests.Admit);
            ArrivalRangeEquivalenceTests.Same(expected, actual);
            ArrivalRangeEquivalenceTests.Same(scene.Context.Step(body, expected, false, 0.1f, tuning),
                scene.Context.Step(body, actual, false, 0.1f, tuning));
            Assert.Equal(32, count);
            Assert.Equal(count, work.FullSteps);
            Assert.Equal(0, work.CertifiedTrials);
        }
    }

    [Fact]
    public void TheOriginalStoredPlacementTraceIsRevalidatedInTheCapturedGeneration()
    {
        // #1754 R1. The stored centre and kernel feet/centre round trip have different Y bits here.
        // A new generation may refuse only the stored placement while answering every kernel trace.
        const float centre = 0.1f;
        float halfHeight = FlatArrivalScene.DefaultTuning.CapsuleHalfHeight;
        float feet = centre - halfHeight;
        Assert.NotEqual(BitConverter.SingleToInt32Bits(centre), BitConverter.SingleToInt32Bits(feet + halfHeight));
        var water = new StoredPlacementWater(centre, FlatArrivalScene.DefaultTuning.CapsuleRadius);
        using var scene = new FlatArrivalScene(height: (x, _) => feet + x * 0.125f,
            normal: (_, _) => Vector3.Normalize(new(-0.125f, 1, 0)), slope: 0.125f, water: water);
        MoveTuning tuning = scene.Tuning;
        var body = new MoveState { Position = new(0, centre, 0), Grounded = true };
        MoveState predicted = scene.Context.Step(body, Vector2.UnitX, false, 0.1f, tuning);
        Assert.True(predicted.SupportGranted);
        water.RefuseStoredPlacement();
        Assert.False(scene.Context.Step(body, Vector2.UnitX, false, 0.1f, tuning).SupportGranted);
        var target = ReachTarget.Point(predicted.Position);
        Vector2 expected = ArrivalReference.Stop(body, tuning, target, 0.1f, false, 0.1f, scene.Context,
            Vector2.UnitX, predicted, ArrivalRangeCrossingTests.Admit, out int count);
        using var work = new ArrivalPreviewDiagnostics();
        Vector2 actual = RangeApproachCore.StopAtRange(body, tuning, target, 0.1f, false, 0.1f, scene.Context,
            Vector2.UnitX, predicted, ArrivalRangeCrossingTests.Admit);
        ArrivalRangeEquivalenceTests.Same(expected, actual);
        Assert.Equal(count, work.FullSteps);
        Assert.Equal(0, work.CertifiedTrials);
    }

    sealed class RefusingWater(MovementAvailability refusal) : IMovementWaterField
    {
        public string SemanticIdentity => "arrival-refusal";
        public long Generation => 0;
        public MovementWaterExtent Extent => new(new(-20, -4, -20), new(20, 0, 20));
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin) =>
            new(MovementAvailability.Known, body.CurrentSpace, null, false, 1, null);
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
            new(refusal, 0, 0, 0, 0, 0, identity);
    }

    sealed class RefusedSubtraceWater : IMovementDryTracePointProof
    {
        readonly IMovementWaterField _inner = MovementWaterField.Level("world", -3, -4);
        public string SemanticIdentity => "arrival-point-proof-refused-subtrace";
        public long Generation => 0;
        public MovementWaterExtent Extent => _inner.Extent;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 origin) =>
            _inner.SampleCentreWater(body, origin);
        public bool CanClassifyDryTracePoints(in MovementMediumSweepQuery query, Vector3 origin) =>
            ((IMovementDryTracePointProof)_inner).CanClassifyDryTracePoints(query, origin);
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 origin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts)
        {
            double x = (double)query.Body.Centre.X + origin.X;
            return x > 0.04 && x < 0.08 ? new(MovementAvailability.PrecisionExceeded, 0, 0, 0, 0, 0, identity)
                : _inner.TraceWater(query, origin, identity, spans, contacts);
        }
    }

    sealed class StoredPlacementWater(float centre, float radius) : IMovementDryTracePointProof
    {
        readonly IMovementWaterField _inner = MovementWaterField.Level("world", -3, -4);
        public string SemanticIdentity => "arrival-stored-placement-trace";
        public long Generation { get; private set; }
        public MovementWaterExtent Extent => _inner.Extent;
        internal void RefuseStoredPlacement() => Generation++;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 origin) =>
            _inner.SampleCentreWater(body, origin);
        public bool CanClassifyDryTracePoints(in MovementMediumSweepQuery query, Vector3 origin) =>
            ((IMovementDryTracePointProof)_inner).CanClassifyDryTracePoints(query, origin);
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 origin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
            Generation > 0 && query.Body.Centre.Y == centre && query.Body.Radius == radius && query.Delta == Vector3.Zero
                ? new(MovementAvailability.PrecisionExceeded, 0, 0, 0, 0, 0, identity)
                : _inner.TraceWater(query, origin, identity, spans, contacts);
    }
}
