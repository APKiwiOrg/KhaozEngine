using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class PlayerColdPlacementTests
{
    [Theory]
    [InlineData("flat")]
    [InlineData("shore")]
    [InlineData("stairs")]
    [InlineData("roof")]
    [InlineData("bank")]
    [InlineData("large-frame")]
    [InlineData("vertical-frame")]
    public void SeededColdStepsEqualTheUncertifiedAlgorithm(string scenario)
    {
        using var field = new OrdinaryShoreField();
        Vector3 origin = scenario == "large-frame" ? new(4194304, 0, -4194304)
            : scenario == "vertical-frame" ? new(0, 1, 0) : default;
        using var scene = new OrdinaryPlayerWorld(origin: origin, groundY: origin.Y, stairs: scenario == "stairs", roof: scenario == "roof",
            field: scenario is "shore" or "bank" ? field : null, dropHeight: scenario == "bank" ? 0.25f : null);
        using var simulator = scene.Simulator();
        var random = new Random(1730);
        using var work = new PlacementWork();
        for (int i = 0; i < 64; i++)
        {
            float x = i < 8 ? new[] { -2f, 0.75f, MathF.BitDecrement(0.75f), MathF.BitIncrement(0.75f),
                1.25f, 4f, 7.75f, 8.25f }[i] : random.Next(-64, 128) / 16f;
            // Initial takes frame-local axes, including when the nearest frame saturates at its index limit.
            var basis = scene.Initial(x + (origin.X - scene.Frame.Anchor.X),
                random.Next(-8, 9) / 16f + (origin.Z - scene.Frame.Anchor.Z));
            if ((i & 3) == 1) { basis.Move.Grounded = false; basis.Move.Position.Y += 0.125f; }
            if ((i & 3) == 2) basis.Move = PlayerMovementPublication.Canonicalize(basis.Move with
            { FacingYaw = 0.5f, HorizontalVelocity = new(0.5f, 0.25f), JumpBufferRemaining = 0.05f });
            var command = new MoveCommand(new(random.Next(-1, 2), random.Next(-1, 2)), (i & 7) == 3, 0);
            using var read = simulator.BeginExplicitRead(basis);
            var actual = simulator.Step(basis, command, OrdinaryPlayerWorld.Dt);
            var outcome = simulator.LastExplicitOutcome;
            var expected = ReferenceStep(simulator, read!, basis, command, OrdinaryPlayerWorld.Dt);
            Assert.Equal(expected.Outcome, outcome);
            AssertExact(expected.State, actual);
            AssertKernelExact(simulator, read!, basis, command, OrdinaryPlayerWorld.Dt);
        }
        Assert.True(work.Validations >= 64);
        if (scenario != "roof") Assert.True(work.Reuses > 0);
    }

    [Theory]
    [InlineData(MovementAvailability.Unresolved, MovementStepOutcome.EnvironmentUnresolved)]
    [InlineData(MovementAvailability.Invalid, MovementStepOutcome.EnvironmentInvalid)]
    [InlineData(MovementAvailability.PrecisionExceeded, MovementStepOutcome.PrecisionRefused)]
    [InlineData(MovementAvailability.CapacityExceeded, MovementStepOutcome.EnvironmentUnresolved)]
    public void ColdCoverageRefusalsNeverIssueACertificate(MovementAvailability status, MovementStepOutcome expectedOutcome)
    {
        using var field = new ColdWaterField { Status = status };
        using var scene = new OrdinaryPlayerWorld(field: field);
        using var simulator = scene.Simulator();
        var basis = scene.Initial(-2);
        using var work = new PlacementWork();
        using var read = simulator.BeginExplicitRead(basis);
        Assert.False(read!.BasisValid);
        Assert.Equal(expectedOutcome, read.Outcome);
        var actual = simulator.Step(basis, new MoveCommand(Vector2.UnitX, true, 0), OrdinaryPlayerWorld.Dt);
        Assert.Equal(expectedOutcome, simulator.LastExplicitOutcome);
        AssertExact(ReferenceStep(simulator, read, basis, new MoveCommand(Vector2.UnitX, true, 0), OrdinaryPlayerWorld.Dt).State, actual);
        Assert.Equal(0, work.Reuses);
    }

    [Fact]
    public void RefusedDryOnlyWetPlacementKeepsTheUncertifiedRefusal()
    {
        using var field = new OrdinaryShoreField();
        using var scene = new OrdinaryPlayerWorld(field: field);
        using var simulator = scene.Simulator(OrdinaryPlayerWorld.Tuning with { Water = new(WaterTraversalMode.DryOnly, 0) });
        var basis = scene.Initial(2);
        using var work = new PlacementWork();
        using var read = simulator.BeginExplicitRead(basis);
        Assert.False(read!.BasisValid);
        Assert.Equal(MovementStepOutcome.PlacementRefused, read.Outcome);
        AssertExact(ReferenceStep(simulator, read, basis, MoveCommand.Idle, OrdinaryPlayerWorld.Dt).State,
            simulator.Step(basis, MoveCommand.Idle, OrdinaryPlayerWorld.Dt));
        Assert.Equal(0, work.Reuses);
    }
}
