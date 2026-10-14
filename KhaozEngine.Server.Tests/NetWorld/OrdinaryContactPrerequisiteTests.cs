using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class OrdinaryContactPrerequisiteTests
{
    [Theory]
    [InlineData(false, 20f)]
    [InlineData(true, 20f)]
    [InlineData(false, 96f)]
    [InlineData(true, 96f)]
    public void ColdOrdinaryPlacementSeatsOnTheRegisteredFloorWithoutAnyNetworkAdapter(bool grounded, float radius)
    {
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        var floor = physics.AddStatic(new BoxShape(new(radius, 0.125f, radius)), Pose.At(new(0, -0.125f, 0)));
        var builder = OrdinaryMovementRegistration.CreateBuilder("world", physics,
            new(new(-radius, -8, -radius), new(radius, 8, radius), 0, 0));
        builder.Add(floor, 0, int.MaxValue, new("world", "floor"), new(-radius, -0.25f, -radius), new(radius, 0, radius));
        OrdinaryMovementRegistration registration;
        using (var read = physics.AcquireQueryReadLease()) registration = builder.Build(read);
        var tuning = MoveTuning.Default with
        {
            Controller = MovementControllerKind.Contact,
            Water = new(WaterTraversalMode.SurfaceSwimmer, 7),
            CapsuleRadius = 0.25f,
            CapsuleHalfHeight = 0.75f,
            WalkSpeed = 4,
            RunSpeed = 8,
            Gravity = 20,
            StepHeight = 0.4f
        };
        using var binding = EffectiveMovementBinding.Ordinary(tuning, physics, registration, (_, _) => 0, null,
            MovementBoundary.Rectangle(-100000, -100000, 100000, 100000));
        var frame = new MovementFrameDescriptor(default, physics.Origin, 1);
        var state = new MoveState { Position = new(4, 0.75f, 4), Grounded = grounded };
        var direct = CharacterMovement.Step(state, MoveCommand.Idle, 0.1f, (_, _) => 0, tuning, null, physics);
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var queries));
        using (queries)
        {
            var settled = binding.SettlePlacement(new(state, frame, null), queries!);
            var step = binding.Step(new(state, frame, null), MoveCommand.Idle, 0.1f, queries!);
            Assert.True(settled.State.State.Grounded,
                $"settle={settled.Outcome} grounded={settled.State.State.Grounded} y={settled.State.State.Position.Y} " +
                $"ordinary={step.Outcome} y={step.State.State.Position.Y} grounded={step.State.State.Grounded} " +
                $"directY={direct.Position.Y} directGrounded={direct.Grounded}");
            Assert.Equal(0.75f, settled.State.State.Position.Y);
            Assert.Equal(MovementStepOutcome.Advanced, settled.Outcome);
            Assert.Equal(MovementStepOutcome.Advanced, step.Outcome);
            Assert.True(direct.Grounded);
            Assert.Equal(0.75f, direct.Position.Y);
            Assert.Equal(direct, step.State.State);
        }
    }
}
