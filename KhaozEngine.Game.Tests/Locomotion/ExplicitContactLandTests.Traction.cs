using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactLeasedQueryTests
{
    [Theory]
    [InlineData(30, false)]
    [InlineData(60, false)]
    [InlineData(30, true)]
    [InlineData(60, true)]
    public void DrySteepRunAndCoyoteJumpUseTheSharedSlideWithoutAWetOwner(int hz, bool jump)
    {
        float angle = 50 * MathF.PI / 180, grade = MathF.Tan(angle);
        Vector3 normal = Vector3.Normalize(new(-grade, 1, 0));
        using var scene = new QueryScene(x => 4 + grade * x, normal);
        ConfigureLegacyPlaneSupport(scene, grade, normal);
        var tuning = Tuning with { JumpSpeed = 7, Gravity = 25 };
        var dry = new MoveState
        { Position = new(0, 4.75f, 0), TimeSinceGrounded = 0 };
        var selection = new MovementSelection(Room, null, scene.Lease.Identity);
        var mapping = new LeasedContactQueryContext(scene.Lease, tuning, selection, new(Room, new(0, 4, 0)));
        var input = new FramedMovementState(dry with { Position = mapping.BodyCentre(new(0, 4, 0)) },
            scene.Lease.Frame, selection);
        float dt = 1f / hz;
        var command = new MoveCommand(Vector2.UnitX, true, 0, jump: jump);
        var resolved = CharacterMovement.ResolveCameraCommand(command, tuning);
        MoveState expected = ContactController.Step(dry, resolved.dir, resolved.fraction, resolved.run, jump,
            dt, (x, _) => 4 + grade * x, tuning, (_, _) => normal, null, null, null);
        var result = ExplicitCharacterMovement.Step(input, command, dt, tuning,
            new(WaterTraversalMode.SurfaceSwimmer, 7), scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        AssertBits(expected with { Position = Vector3.Zero }, result.State.State with { Position = Vector3.Zero });
        Assert.InRange(Vector3.Distance(expected.Position - new Vector3(0, 0.75f, 0),
            mapping.KernelFeet(result.State.State.Position, tuning)), 0, 0.0005f);
        Assert.False(result.State.State.Grounded);
        Assert.False(result.State.State.SupportGranted);
        Assert.False(result.State.State.Swimming);
        Assert.Equal(WaterExcursionState.None, result.State.State.WaterExcursion);
        Assert.Null(result.State.Selection!.Value.Support);
        Assert.True(result.State.State.Position.X < 0);
        Assert.Equal(0, result.State.State.StepDeltaY);
        Assert.Equal(0, result.State.State.ClimbRate);
        if (jump) Assert.Equal(7, result.State.State.VerticalVelocity);
        float launchY = result.State.State.Position.Y;
        for (int tick = 1; tick <= 5; tick++)
        {
            MoveState before = result.State.State;
            var followCommand = jump ? MoveCommand.Idle : new MoveCommand(Vector2.UnitX, true, 0);
            var follow = CharacterMovement.ResolveCameraCommand(followCommand, tuning);
            expected = ContactController.Step(expected, follow.dir, follow.fraction, follow.run, false,
                dt, (x, _) => 4 + grade * x, tuning, (_, _) => normal, null, null, null);
            result = ExplicitCharacterMovement.Step(result.State, followCommand, dt, tuning,
                new(WaterTraversalMode.SurfaceSwimmer, 7), scene.Lease);
            Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            AssertBits(expected with { Position = Vector3.Zero }, result.State.State with { Position = Vector3.Zero });
            Assert.False(result.State.State.Grounded);
            Assert.False(result.State.State.SupportGranted);
            Assert.Null(result.State.Selection!.Value.Support);
            Assert.Equal(0, result.State.State.StepDeltaY);
            Assert.Equal(0, result.State.State.ClimbRate);
            if (jump)
            {
                double rise = 7 * (double)tick * dt - 25 * (double)dt * dt * tick * (tick + 1) / 2;
                Assert.InRange(result.State.State.Position.Y - launchY, rise - 0.0005, rise + 0.0005);
            }
            else
            {
                Assert.True(result.State.State.Position.X < before.Position.X);
                Assert.True(result.State.State.Position.Y < before.Position.Y);
            }
        }
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(60, true)]
    [InlineData(30, false)]
    [InlineData(60, false)]
    public void DryTractionBandKeepsGroundHistoryAndDoesNotGrantAnAirborneBody(int hz, bool grounded)
    {
        float angle = 46 * MathF.PI / 180, grade = MathF.Tan(angle);
        Vector3 normal = Vector3.Normalize(new(-grade, 1, 0));
        using var scene = new QueryScene(x => 4 + grade * x, normal);
        ConfigureLegacyPlaneSupport(scene, grade, normal);
        var dry = new MoveState { Position = new(0, 4.75f, 0), Grounded = grounded, TimeSinceGrounded = 1 };
        var selection = new MovementSelection(Room, grounded ? new MovementSupportKey("world", "floor") : null,
            scene.Lease.Identity);
        var mapping = new LeasedContactQueryContext(scene.Lease, Tuning, selection, new(Room, new(0, 4, 0)));
        var input = new FramedMovementState(dry with { Position = mapping.BodyCentre(new(0, 4, 0)) },
            scene.Lease.Frame, selection);
        MoveState expected = ContactController.Step(dry, Vector2.UnitX, 1, true, false, 1f / hz,
            (x, _) => 4 + grade * x, Tuning, (_, _) => normal, null, null, null);
        var result = ExplicitCharacterMovement.StepTowards(input, Vector2.UnitX, true, 1f / hz,
            Tuning, new(WaterTraversalMode.SurfaceSwimmer, 7), scene.Lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.Equal(grounded, result.State.State.Grounded);
        Assert.Equal(grounded, result.State.State.SupportGranted);
        AssertBits(expected with { Position = Vector3.Zero }, result.State.State with { Position = Vector3.Zero });
        Assert.Equal(WaterExcursionState.None, result.State.State.WaterExcursion);
        Assert.Equal(grounded, result.State.Selection!.Value.Support.HasValue);
    }

    static void ConfigureLegacyPlaneSupport(QueryScene scene, float grade, Vector3 normal) =>
        scene.Environment.OnSupport = (in MovementSupportRequest request, Span<MovementSupportCandidate> candidates) =>
        {
            candidates[0] = new(new("world", "floor"), Room,
                new(request.Body.Centre.X, 4 + grade * request.Body.Centre.X, request.Body.Centre.Z), normal, null);
            return new(MovementAvailability.Known, 1, 1, scene.Lease.Identity);
        };
}
