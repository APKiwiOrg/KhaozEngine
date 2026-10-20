using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public sealed partial class GeneralSweepExplicitCostTests
{
    [Theory]
    [InlineData("scope", MovementStepOutcome.EnvironmentUnresolved)]
    [InlineData("stale-pin", MovementStepOutcome.EnvironmentUnresolved)]
    [InlineData("frame", MovementStepOutcome.FrameMismatch)]
    [InlineData("missing-binding", MovementStepOutcome.EnvironmentUnresolved)]
    public void GeneralSweepExplicitScopeAndRawSupportGuardsRemainAtomic(string fault, MovementStepOutcome outcome)
    {
        var row = Row("stairs hull");
        using var run = new SupportCostExecution(row, "ExplicitTick");
        var producer = fault == "scope" ? DryEnvironment(run.View, undersized: true) : run.Producer;
        if (fault == "missing-binding") producer.OnContactSupport = null;
        var acquired = producer.Acquire();
        Assert.Equal(MovementAvailability.Known, acquired.Status);
        using var lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
        if (fault == "stale-pin") producer.Fault = fault;
        var frame = fault == "frame"
            ? new MovementFrameDescriptor(WorldFrame.Origin, Vector3.Zero, 2) : lease.Frame;
        MoveState inputState = row.Input;
        var input = new FramedMovementState(inputState, frame, new(Room, null, lease.Identity));
        run.View.Reset();
        MovementStepResult result = ExplicitCharacterMovement.StepTowards(input, row.Direction, false, row.Dt,
            SupportGateCostScenes.Tuning, new(row.WaterMode), lease);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(inputState, input.State);
        Assert.Equal(MovementStepResult.HoldState(inputState) with { ClimbRateEwma = 0 }, result.State.State);
        Assert.Null(result.State.Selection);
        if (fault is "scope" or "stale-pin" or "frame")
        {
            Assert.Equal(default, run.View.Counts);
            Assert.Equal(0, producer.AnalyticGroundCalls);
            Assert.Equal(0, producer.ContactSupportCalls);
        }
    }
}
