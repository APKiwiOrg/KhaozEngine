using System;
using System.Numerics;

using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactLeasedQueryTests
{
    [Theory]
    [InlineData("flat", 30)]
    [InlineData("flat", 60)]
    [InlineData("step-up", 30)]
    [InlineData("step-up", 60)]
    [InlineData("step-down", 30)]
    [InlineData("step-down", 60)]
    [InlineData("stairs", 30)]
    [InlineData("stairs", 60)]
    [InlineData("ramp", 30)]
    [InlineData("ramp", 60)]
    [InlineData("ledge", 30)]
    [InlineData("ledge", 60)]
    [InlineData("landing", 30)]
    [InlineData("landing", 60)]
    [InlineData("coyote", 30)]
    [InlineData("coyote", 60)]
    [InlineData("buffered-jump", 30)]
    [InlineData("buffered-jump", 60)]
    [InlineData("air-momentum", 30)]
    [InlineData("air-momentum", 60)]
    [InlineData("commitment", 30)]
    [InlineData("commitment", 60)]
    public void ExplicitLandUsesTheDryTickWithTheDeclaredPlacementMapping(string scenario, int hz)
    {
        using FootSupportScene geometry = scenario switch
        {
            "step-up" or "step-down" => new FootSupportScene(SceneVariant.Box)
                .Flat("floor", -4, 6, -5, 5, 4).Flat("bank", 1, 5, -4, 4, 4.25f),
            "stairs" => new FootSupportScene(SceneVariant.Box).Flat("floor", -4, 0, -5, 5, 4)
                .Flat("step1", 0, 2.7f, -1, 1, 4.25f).Flat("step2", 0.35f, 2.7f, -1, 1, 4.5f)
                .Flat("step3", 0.7f, 2.7f, -1, 1, 4.75f),
            "ledge" or "coyote" => new FootSupportScene(SceneVariant.Box).Flat("ledge", -5, 0, -5, 5, 5),
            _ => new FootSupportScene(SceneVariant.Box).Flat("floor", -4, 6, -5, 5, 4)
        };
        MoveTuning tuning = scenario == "air-momentum" ? Tuning with { AirMomentum = true } : Tuning;
        Vector3 feet = scenario switch
        {
            "step-down" => new(1.5f, 4.25f, 0),
            "ledge" or "coyote" => new(-0.2f, 5, 0),
            "landing" or "buffered-jump" or "air-momentum" => new(0, 4.5f, 0),
            _ => new(-0.5f, 4, 0)
        };
        MoveState dry = new()
        {
            Position = feet + new Vector3(0, 0.75f, 0),
            Grounded = feet.Y != 4.5f,
            TimeSinceGrounded = feet.Y == 4.5f ? 1 : 0
        };
        if (scenario == "commitment") dry.Commitment = new MovementCommitment(3, Vector2.UnitX, 6, 5, 2f / hz, 4);
        bool coyotePressed = false, bufferPressed = false, launchedFromSupport = false, committedFlight = false;
        float Height(float x, float z) => scenario == "ramp" ? 4 + Math.Clamp(x * 0.25f, 0, 0.5f) : -1000;
        Vector3 Normal(float x, float z) => x > 0 && x < 2 ? Vector3.Normalize(new(-0.25f, 1, 0)) : Vector3.UnitY;
        float dt = 1f / hz;
        Vector2 direction = scenario == "step-down" ? -Vector2.UnitX : Vector2.UnitX;
        for (int tick = 0; tick < hz / 2; tick++)
        {
            bool coyoteJump = scenario == "coyote" && !coyotePressed && !dry.Grounded;
            bool bufferedJump = scenario == "buffered-jump" && !bufferPressed && !dry.Grounded &&
                dry.Position.Y - 4.75f < 0.2f;
            bool jump = coyoteJump || bufferedJump;
            coyotePressed |= coyoteJump;
            bufferPressed |= bufferedJump;
            MoveState before = dry;
            var camera = new MoveCommand(direction, scenario == "commitment", 0, jump: true);
            Vector2 resolved = jump ? CharacterMovement.ResolveCameraCommand(camera, tuning).dir : direction;
            dry = ContactController.Step(dry, resolved, 1, scenario == "commitment", jump, dt,
                Height, tuning, scenario == "ramp" ? Normal : null, geometry.World, null, null);
            using var scene = new QueryScene(geometry);
            scene.Environment.OnSupport = (in MovementSupportRequest _, Span<MovementSupportCandidate> _) =>
                new(MovementAvailability.Known, 0, 0, scene.Lease.Identity);
            if (scenario == "ramp")
                scene.Environment.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
                {
                    Vector2 axis = new(request.Body.Centre.X, request.Body.Centre.Z);
                    ground = new(axis, Height(axis.X, axis.Y), Normal(axis.X, axis.Y), new("world", "floor"),
                        Room, null, scene.Lease.Identity);
                    return MovementAvailability.Known;
                };
            var mapping = new LeasedContactQueryContext(scene.Lease, tuning,
                new(Room, null, scene.Lease.Identity), new(Room, queriesFeet(before)));
            var input = new FramedMovementState(before with { Position = mapping.BodyCentre(queriesFeet(before)) },
                scene.Lease.Frame, new(Room, null, scene.Lease.Identity));
            // At this authored datum both published-centre mappings recover exactly the same kernel pose.
            AssertBits(queriesFeet(before), mapping.KernelFeet(input.State.Position, tuning));
            // Camera commands normalize their magnitude, so both entries receive full-speed intent.
            MovementStepResult result = jump
                ? ExplicitCharacterMovement.Step(input, camera,
                    dt, tuning, new(WaterTraversalMode.SurfaceSwimmer, 7), scene.Lease)
                : ExplicitCharacterMovement.StepTowards(input, direction, scenario == "commitment", dt,
                    tuning, new(WaterTraversalMode.SurfaceSwimmer, 7), scene.Lease);
            Assert.True(result.Outcome is MovementStepOutcome.Advanced or MovementStepOutcome.Blocked,
                $"{scenario}, tick {tick}: {result.Outcome}");
            MoveState actual = result.State.State;
            AssertBits(dry with { Position = Vector3.Zero }, actual with { Position = Vector3.Zero });
            Assert.InRange(Vector3.Distance(queriesFeet(dry), mapping.KernelFeet(actual.Position, tuning)), 0, 0.0005f);
            launchedFromSupport |= dry.SupportGranted && !dry.Grounded && dry.VerticalVelocity > 0;
            committedFlight |= dry.Commitment.Phase == MovementCommitmentPhase.Airborne;
        }
        if (scenario == "coyote") Assert.True(coyotePressed && dry.Position.Y > 5.75f);
        if (scenario == "buffered-jump") Assert.True(bufferPressed && launchedFromSupport);
        if (scenario == "commitment") Assert.True(committedFlight);
        static Vector3 queriesFeet(MoveState state) => state.Position - new Vector3(0, 0.75f, 0);
    }

}
