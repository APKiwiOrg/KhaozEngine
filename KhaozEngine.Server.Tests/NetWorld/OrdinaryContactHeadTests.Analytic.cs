using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class OrdinaryContactHeadTests
{
    [Theory]
    [InlineData("height", 0f)]
    [InlineData("normal", 0f)]
    [InlineData("local-y", 4f)]
    public void FrameAnalyticSamplersPreserveCompleteDirectDryState(string row, float originY)
    {
        using var physics = new ObservedPhysicsWorld();
        physics.Rebase(new(256, originY, 128));
        OrdinaryMovementRegistration registration;
        using (var lease = physics.AcquireQueryReadLease())
            registration = OrdinaryMovementRegistration.CreateBuilder("world", physics,
                new(new(236, -8, 108), new(276, 8, 148), originY - 1, originY + 1)).Build(lease);
        float Height(float x, float z) => row == "height" ? x >= 0.4f ? 0.2f : 0 : row == "local-y" ? 0.2000001f : 0;
        Vector3 Normal(float x, float z) => row == "normal" && x >= 0.4f
            ? Vector3.Normalize(new(-2f, 1, -1f)) : Vector3.UnitY;
        var tuning = OrdinaryPlayerWorld.Tuning with { Water = new(WaterTraversalMode.DryOnly) };
        var frame = WorldFrame.Nearest(256, 128);
        using var simulator = new PlayerMoveSimulator(Height, tuning, Normal, null, physics,
            null, SamplerSpace.Frame, null, null, registration, MovementBoundary.Rectangle(236, 108, 276, 148))
        { Frame = frame };
        var state = row == "local-y"
            ? new MoveState { Position = new(0, 1.75f, 0), VerticalVelocity = -20 }
            : new MoveState { Position = new(0, Height(0, 0) + 0.75f, 0), Grounded = true };
        var command = new MoveCommand(Vector2.UnitX, false, 0);
        var direct = CharacterMovement.Step(state, command, 0.1f, Height, tuning, Normal, physics);
        var basis = new PlayerMoveState { Move = state, FrameAnchor = new(frame.Anchor.X, frame.Anchor.Z) };
        basis.Move.Position.Y += originY;
        using var read = simulator.BeginExplicitRead(basis);
        Assert.True(read!.BasisValid);
        var actual = simulator.Step(basis, command, 0.1f).Move;
        direct.Position.Y += originY;
        AssertSame(direct, actual);
    }
}
