using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
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
    [InlineData("slide-opposing", 30)]
    [InlineData("slide-opposing", 60)]
    [InlineData("wall-retry", 30)]
    [InlineData("wall-retry", 60)]
    public void ZeroOffsetLeasedQueriesMatchEveryDryStateBit(string scenario, int hz)
    {
        using FootSupportScene geometry = scenario switch
        {
            "step-up" or "step-down" => FootSupportScenes.Bank(SceneVariant.Box),
            "stairs" => FootSupportScenes.Stairs(SceneVariant.Box, 0.35f, 0.25f, 3),
            "ramp" => FootSupportScenes.Floor(SceneVariant.Box),
            "ledge" or "coyote" => new FootSupportScene(SceneVariant.Box).Flat("ledge", -5, 0, -5, 5, 2),
            "slide-opposing" or "wall-retry" => new FootSupportScene(SceneVariant.Box),
            _ => FootSupportScenes.Floor(SceneVariant.Box)
        };
        MoveTuning tuning = scenario == "air-momentum" ? Tuning with { AirMomentum = true } : Tuning;
        Vector3 feet = scenario switch
        {
            "step-down" => new(1.5f, 0.25f, 0),
            "ledge" or "coyote" => new(-0.2f, 2, 0),
            "landing" or "buffered-jump" or "air-momentum" => new(0, 0.5f, 0),
            "slide-opposing" => new(0.05f, 0.1f, 0),
            "wall-retry" => Vector3.Zero,
            _ => new(-0.5f, 0, 0)
        };
        MoveState dry = new()
        {
            Position = feet + new Vector3(0, 0.75f, 0),
            Grounded = feet.Y != 0.5f,
            TimeSinceGrounded = feet.Y == 0.5f ? 1 : 0
        };
        if (scenario == "commitment") dry.Commitment = new MovementCommitment(3, Vector2.UnitX, 6, 5, 2f / hz, 4);
        MoveState leased = dry;
        bool coyotePressed = false, bufferPressed = false, launchedFromSupport = false, committedFlight = false;
        // The opposing-face V and the terrain wall are the cost fixture's slide and retry routes.
        bool analytic = scenario is "ramp" or "slide-opposing" or "wall-retry";
        float Height(float x, float z) => scenario switch
        {
            "ramp" => Math.Clamp(x * 0.25f, 0, 0.5f),
            "slide-opposing" => 2 * MathF.Abs(x),
            "wall-retry" => MathF.Max(0, 2 * (x - 0.05f)),
            _ => -1000,
        };
        Vector3 Normal(float x, float z) => scenario switch
        {
            "slide-opposing" => Vector3.Normalize(new(x >= 0 ? -2 : 2, 1, 0)),
            "wall-retry" => x <= 0.05f ? Vector3.UnitY : Vector3.Normalize(new(-2, 1, 0)),
            _ => x > 0 && x < 2 ? Vector3.Normalize(new(-0.25f, 1, 0)) : Vector3.UnitY,
        };
        float dt = 1f / hz;
        Vector2 direction = scenario == "step-down" ? -Vector2.UnitX
            : scenario == "wall-retry" ? Vector2.Normalize(new(1, 1)) : Vector2.UnitX;
        long slides = 0, retries = 0;
        SupportQueryDiagnostics.Enable(true);
        try
        {
            for (int tick = 0; tick < hz / 2; tick++)
            {
                bool coyoteJump = scenario == "coyote" && !coyotePressed && !dry.Grounded;
                bool bufferedJump = scenario == "buffered-jump" && !bufferPressed && !dry.Grounded &&
                    dry.Position.Y - 0.75f < 0.2f;
                bool jump = coyoteJump || bufferedJump;
                coyotePressed |= coyoteJump;
                bufferPressed |= bufferedJump;
                dry = ContactController.Step(dry, direction, 0.5f, scenario == "commitment", jump, dt,
                    Height, tuning, analytic ? Normal : null, geometry.World, null, null);
                using var scene = new QueryScene(geometry);
                if (analytic)
                    scene.Environment.OnAnalyticGround = (in MovementSupportRequest request, out MovementAnalyticGround? ground) =>
                    {
                        Vector2 axis = new(request.Body.Centre.X, request.Body.Centre.Z);
                        ground = new(axis, Height(axis.X, axis.Y), Normal(axis.X, axis.Y), new("world", "floor"),
                            Room, null, scene.Lease.Identity);
                        return MovementAvailability.Known;
                    };
                var queries = new LeasedContactQueryContext(scene.Lease, tuning,
                    new(Room, null, scene.Lease.Identity), new(Room, queriesFeet(leased)), 0);
                SupportRouteCounts before = SupportQueryDiagnostics.Routes;
                leased = ContactController.StepWithQueries(leased, direction, 0.5f, scenario == "commitment",
                    jump, dt, tuning, queries);
                slides += SupportQueryDiagnostics.Routes.SlideSegments - before.SlideSegments;
                retries += SupportQueryDiagnostics.Routes.WallRetries - before.WallRetries;
                LocomotionStateBits.AssertEqual(dry, leased, LocomotionStateBits.Policy.ExcludeContinuityHandle);
                launchedFromSupport |= dry.SupportGranted && !dry.Grounded && dry.VerticalVelocity > 0;
                committedFlight |= dry.Commitment.Phase == MovementCommitmentPhase.Airborne;
            }
        }
        finally { SupportQueryDiagnostics.Enable(false); }
        if (scenario == "slide-opposing") Assert.True(slides > 0);
        if (scenario == "wall-retry") Assert.True(retries > 0);
        if (scenario == "coyote") Assert.True(coyotePressed && dry.Position.Y > 2.75f);
        if (scenario == "buffered-jump") Assert.True(bufferPressed && launchedFromSupport);
        if (scenario == "commitment") Assert.True(committedFlight);
        static Vector3 queriesFeet(MoveState state) => state.Position - new Vector3(0, 0.75f, 0);
    }
}
