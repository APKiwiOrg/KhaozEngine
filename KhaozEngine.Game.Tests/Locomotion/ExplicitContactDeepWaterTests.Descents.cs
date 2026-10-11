using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

public partial class ExplicitCharacterMovementTests
{
    [Theory]
    [InlineData(WaterTraversalMode.DryOnly, false)]
    [InlineData(WaterTraversalMode.DryOnly, true)]
    [InlineData(WaterTraversalMode.WadeOnly, true)]
    public void ForbiddenDescendingBanksAndFlatStepsKeepTheAlongShoreComponent(WaterTraversalMode mode, bool stepped)
    {
        MoveTuning tuning = DeepWaterTuning(0.75f);
        float line = mode == WaterTraversalMode.DryOnly ? 1 : 1 - 1.5f * tuning.SwimEnterDepthFraction;
        float top = line - PlacementSkin + 0.1f;
        using var environment = new AnalyticMovementEnvironment(
            [new Room("room", new(new(-16), new(16)))],
            [new Water("lake", "room", new(new(-8, -4, -8), new(8, 1, 8)), 1)]);
        using var lease = environment.Acquire("room", 4, 4);
        environment.Acquisition.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
        {
            Vector2 axis = new(request.Body.Centre.X, request.Body.Centre.Z);
            float height = stepped ? axis.X < 1 ? top : top - 0.2f : top - 0.1f * axis.X;
            Vector3 normal = stepped ? Vector3.UnitY : Vector3.Normalize(new Vector3(0.1f, 1, 0));
            ground = new(axis, height, normal, new("world", "bank"), Space("room"), null, Identity);
            return MovementAvailability.Known;
        };
        environment.Acquisition.OnContactSupport = (in MovementContactSupportRequest request,
            out MovementContactSupportBinding binding) =>
        {
            binding = new(new("world", "bank"), Space("room"), null, Identity);
            return MovementAvailability.Known;
        };
        float startX = stepped ? 0.95f : 0.9f;
        float startY = stepped ? top : top - 0.1f * startX;
        var state = new FramedMovementState(new MoveState
        {
            Position = new(startX, startY + 0.75f + PlacementSkin, 0),
            Grounded = true,
        }, lease.Frame, new(Space("room"), new MovementSupportKey("world", "bank"), Identity));

        MovementStepResult result = ExplicitCharacterMovement.StepTowards(state, Vector2.One, false,
            0.25f, tuning, new(mode), lease);

        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.True(result.State.State.Grounded);
        Assert.False(result.State.State.Swimming);
        Assert.True(result.State.State.Position.X < 1);
        Assert.True(result.State.State.Position.Y - 0.75f > line);
        Assert.True(result.State.State.Position.Z > 0.2f, $"pose {result.State.State.Position}");
    }

    [Theory]
    [InlineData(1f, 1f, 0.03577709f)]
    [InlineData(2f, 1f, 0.10733126f)]
    [InlineData(1f, -1f, 0.03577709f)]
    [InlineData(2f, -1f, 0.10733126f)]
    public void ObliqueFlatDescentKeepsItsFeasibleShoreTangent(float zWeight, float sign, float tangentTravel)
    {
        MoveTuning tuning = DeepWaterTuning(0.75f);
        using var environment = new AnalyticMovementEnvironment(
            [new Room("room", new(new(-16), new(16)))],
            [new Water("lake", "room", new(new(-8, -4, -8), new(8, 1, 8)), 1)]);
        using var lease = environment.Acquire("room", 4, 4);
        float edge = 0.46f * (1 + zWeight) + 0.08f;
        environment.Acquisition.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
        {
            Vector2 axis = new(request.Body.Centre.X, request.Body.Centre.Z);
            float height = sign * (axis.X + zWeight * axis.Y) < edge ? 1.099f : 0.899f;
            ground = new(axis, height, Vector3.UnitY, new("world", "bank"), Space("room"), null, Identity);
            return MovementAvailability.Known;
        };
        environment.Acquisition.OnContactSupport = (in MovementContactSupportRequest request,
            out MovementContactSupportBinding binding) =>
        {
            binding = new(new("world", "bank"), Space("room"), null, Identity);
            return MovementAvailability.Known;
        };
        var state = new FramedMovementState(new MoveState
        {
            Position = new(sign * 0.46f, 1.85f, sign * 0.46f),
            Grounded = true,
        }, lease.Frame, new(Space("room"), new MovementSupportKey("world", "bank"), Identity));

        MovementStepResult result = ExplicitCharacterMovement.StepTowards(state,
            sign * Vector2.Normalize(new Vector2(2, 1)), false, 0.02f, tuning,
            new(WaterTraversalMode.DryOnly), lease);

        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.True(result.State.State.Grounded);
        Assert.False(result.State.State.Swimming);
        Vector3 end = result.State.State.Position;
        Assert.True(sign * (end.X + zWeight * end.Z) < edge);
        // A 4 m/s, 0.02 s move in direction (2, 1) travels 0.08 / sqrt(5) on each unit of intent.
        // Removing only the shore-normal component preserves zWeight * dx - dz.
        float along = sign * (zWeight * (end.X - state.State.Position.X) - (end.Z - state.State.Position.Z));
        Assert.InRange(along, tangentTravel - 0.0005f, tangentTravel + 0.0005f);
        Assert.InRange(end.Y, 1.8495f, 1.8505f);
        Assert.Equal(new MovementSupportKey("world", "bank"), result.State.Selection!.Value.Support);
    }
}
