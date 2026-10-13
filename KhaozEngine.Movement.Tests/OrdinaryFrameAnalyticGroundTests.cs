using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class OrdinaryFrameAnalyticGroundTests
{
    [Theory]
    [InlineData("height", 0f)]
    [InlineData("normal", 0f)]
    [InlineData("local-y", 4f)]
    public void FrameGroundFactsAndCompleteStatesEqualTheDirectFramedDryPath(string row, float originY)
    {
        using var physics = new BepuPhysicsWorld(Vector3.Zero);
        Vector3 origin = new(256, originY, 128);
        physics.Rebase(origin);
        OrdinaryMovementRegistration registration;
        using (var lease = physics.AcquireQueryReadLease())
            registration = OrdinaryMovementRegistration.CreateBuilder("world", physics,
                new(new(236, -8, 108), new(276, 8, 148), originY - 1, originY + 1)).Build(lease);
        Vector2 heightAxis = default, normalAxis = default;
        float Height(float x, float z)
        {
            heightAxis = new(x, z);
            return row == "height" ? x >= 0.4f ? 0.2f : 0 : row == "local-y" ? 0.2000001f : 0;
        }
        Vector3 Normal(float x, float z)
        {
            normalAxis = new(x, z);
            return row == "normal" && x >= 0.4f ? Vector3.Normalize(new(-2f, 1, -1f)) : Vector3.UnitY;
        }
        var tuning = Fixtures.OrdinaryContactScene.DefaultTuning;
        var state = new MoveState { Position = new(0, Height(0, 0) + 0.75f, 0), Grounded = true };
        var direct = CharacterMovement.StepTowards(state, Vector2.UnitX, false, 0.1f, Height, tuning, Normal, physics);
        using var binding = EffectiveMovementBinding.Ordinary(tuning, physics, registration, Height, Normal,
            MovementBoundary.Rectangle(236, 108, 276, 148), groundSpace: OrdinaryGroundSpace.Frame);
        var frame = new MovementFrameDescriptor(WorldFrame.Nearest(origin), origin, 1);
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var read));
        using (read)
        {
            Assert.Equal(MovementAvailability.Known, read!.RebuildSelection(new(state, frame, null), out var selection));
            var body = new MovementBodyQuery(new(0.4f, 1, 0.4f), tuning.CapsuleRadius, tuning.CapsuleHalfHeight, selection.Space, null);
            Assert.Equal(MovementAvailability.Known, read.SampleAnalyticGround(new(body, 0, 0, 0,
                new(selection.Space, body.Feet)), out var fact));
            Assert.Equal(new Vector2(0.4f), heightAxis);
            Assert.Equal(new Vector2(0.4f), normalAxis);
            Assert.Equal(Height(0.4f, 0.4f), fact!.Value.Height);
            Assert.Equal(Normal(0.4f, 0.4f), fact.Value.Normal);
            var ordinary = binding.StepTowards(new(state, frame, null), Vector2.UnitX, false, 0.1f, read);
            Assert.Equal(MovementStepOutcome.Advanced, ordinary.Outcome);
            Assert.Equal(direct, ordinary.State.State);
        }
    }
}
