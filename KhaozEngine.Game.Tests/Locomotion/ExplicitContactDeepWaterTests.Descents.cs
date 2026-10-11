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
}
