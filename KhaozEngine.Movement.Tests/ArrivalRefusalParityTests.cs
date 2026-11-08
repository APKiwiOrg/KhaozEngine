using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ArrivalRefusalParityTests
{
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
}
