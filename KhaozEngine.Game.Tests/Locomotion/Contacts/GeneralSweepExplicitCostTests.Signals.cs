using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public sealed partial class GeneralSweepExplicitCostTests
{
    static readonly MovementSpaceKey Room = new("world", "room");

    [Theory]
    [InlineData("stairs box")]
    [InlineData("stairs hull")]
    [InlineData("stairs mesh")]
    public void AcquiredStairsWithoutCarriedAscentConsumeLookAheadAndMatchDrySignals(string name)
    {
        var row = Row(name);
        MoveState input = row.Input with { ClimbRate = 0, ClimbRateEwma = 0 };
        using var run = new SupportCostExecution(row, "ExplicitTick");
        var capture = new TickCapture(run);
        SupportCostOutput result = capture.Invoke(() =>
        {
            var acquired = run.Producer.Acquire();
            Assert.Equal(MovementAvailability.Known, acquired.Status);
            using var lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
            var framed = new FramedMovementState(input, lease.Frame, new(Room, null, lease.Identity));
            return Output(ExplicitCharacterMovement.StepTowards(framed, row.Direction, false, row.Dt,
                SupportGateCostScenes.Tuning, new(row.WaterMode), lease), input);
        });
        AssertAccepted(run, row, capture, result);
        SupportFindObservation[] signals = capture.Reads.Where(r => r.ReachUp > 1).ToArray();
        Assert.Equal(GroundSignals.Samples, signals.Length);
        for (int i = 0; i < signals.Length; i++)
        {
            float forward = (i + 1) * SupportGateCostScenes.Tuning.CapsuleRadius;
            Assert.Equal(new Vector2(result.EndFeet.X + forward, result.EndFeet.Z), signals[i].Axis);
            Assert.Equal(SupportGateCostScenes.Tuning.StepHeight + 2 * forward, signals[i].ReachUp);
            Assert.NotEqual((int)SupportStatus.Refused, signals[i].Status);
        }

        // The dry head carries the same geometric feet without the explicit body's placement offset.
        MoveState dryInput = input with { Position = row.Feet + new Vector3(0, 0.9f, 0) };
        MoveState dry = ContactController.Step(dryInput, row.Direction, 1, false, false, row.Dt,
            static (_, _) => float.NaN, SupportGateCostScenes.Tuning, null, run.View, null, null);
        Assert.Equal(dry.ClimbRate, result.State!.Value.ClimbRate);
        Assert.Equal(dry.ClimbRateEwma, result.State.Value.ClimbRateEwma);
        Assert.Equal(dry.StepDeltaY, result.State.Value.StepDeltaY);
        output.WriteLine($"{name}. No carried ascent, {signals.Length} completed look-ahead reads, " +
            $"signals {dry.ClimbRate:R}/{dry.ClimbRateEwma:R}/{dry.StepDeltaY:R} match dry.");
    }

    static SupportCostOutput Output(MovementStepResult result, MoveState input)
    {
        MoveState next = result.State.State;
        var feet = new Vector3(next.Position.X, next.Position.Y - 0.9f - 0.001f, next.Position.Z);
        bool accepted = result.Outcome == MovementStepOutcome.Advanced;
        return new(accepted, result.Outcome.ToString(), feet.Y, 0, next.Position - input.Position, !accepted)
        { State = next, EndFeet = feet, StepOutcome = result.Outcome };
    }
}
