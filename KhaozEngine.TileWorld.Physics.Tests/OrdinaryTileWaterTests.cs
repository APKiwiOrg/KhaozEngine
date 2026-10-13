using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class OrdinaryTileWaterTests
{
    internal static MoveTuning Tuning(WaterTraversalMode mode) => new(4, 6, 0.75f, 20f * MathF.PI / 180,
        CapsuleRadius: 0.25f, TractionHysteresisRadians: 0)
    { Controller = MovementControllerKind.Contact, Water = new(mode, mode == WaterTraversalMode.SurfaceSwimmer ? 7 : 0) };

    [Theory]
    [InlineData("SurfaceEntry")]
    [InlineData("SurfaceExit")]
    [InlineData("BuoyancyAndHop")]
    public void SurfaceEntryExitBuoyancyAndHopAtZeroOffset(string scenario)
    {
        using var scene = new TileScene(WaterTraversalMode.SurfaceSwimmer);
        using var lease = scene.Acquire();
        var state = scene.State(new(scenario == "SurfaceExit" ? 8.3f : 10, -1, -10), false);
        var result = scene.Binding.StepTowards(state, Vector2.Zero, false, 1f / 30, lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Swimming);
        Assert.True(result.State.State.Position.Y > -1);
        if (scenario == "SurfaceExit")
        {
            var swimming = result.State;
            var body = new MovementBodyQuery(swimming.State.Position, scene.Tuning.CapsuleRadius,
                scene.Tuning.CapsuleHalfHeight, swimming.Selection!.Value.Space, null);
            var point = lease.SampleCentreWater(body);
            Assert.Equal(MovementAvailability.Known, point.Availability);
            Assert.True(point.InWater);
            Assert.Equal(0, point.Interval!.Value.UpperY);
            var spans = new MovementCoverageSpan[64];
            var contacts = new MovementDomainContact[256];
            var wetCoverage = lease.TraceWater(new(body, Vector3.Zero), spans, contacts);
            Assert.Equal(MovementAvailability.Known, wetCoverage.Availability);
            Assert.All(spans[..wetCoverage.SpansWritten], span => Assert.False(span.HasDryCoverage));

            var exit = scene.Binding.StepTowards(swimming, -Vector2.UnitX, false, 0.3f, lease);
            Assert.Equal(MovementStepOutcome.Advanced, exit.Outcome);
            Assert.False(exit.State.State.Swimming);
            Assert.False(exit.State.State.Grounded);
            Assert.Equal(WaterExcursionState.AirborneFromWater, exit.State.State.WaterExcursion);
            Assert.True(exit.State.State.Position.X + scene.Tuning.CapsuleRadius < 8);
            Assert.Equal(-10, exit.State.State.Position.Z);
            Assert.True(exit.State.State.Position.Y - scene.Tuning.CapsuleHalfHeight < 0);
            Assert.Equal(Vector3.Zero, exit.State.Frame.PhysicsOrigin);
            Assert.Equal(scene.Frame, exit.State.Frame);
            Assert.Equal(0, exit.State.State.StepDeltaY);
            var dryBody = new MovementBodyQuery(exit.State.State.Position, body.Radius, body.HalfHeight, body.CurrentSpace, null);
            var dryCoverage = lease.TraceWater(new(dryBody, Vector3.Zero), spans, contacts);
            Assert.Equal(MovementAvailability.Known, dryCoverage.Availability);
            Assert.True(dryCoverage.IsValid);
            Assert.Equal(lease.Identity, dryCoverage.Identity);
            Assert.InRange(dryCoverage.CertifiedErrorMetres, 0f, 0.001f);
            Assert.Equal(0, dryCoverage.ContactsWritten);
            var drySpan = Assert.Single(spans[..dryCoverage.SpansWritten]);
            Assert.Equal(0, drySpan.EnterFraction);
            Assert.Equal(1, drySpan.ExitFraction);
            Assert.True(drySpan.HasDryCoverage);
            return;
        }
        if (scenario != "BuoyancyAndHop") return;
        var surface = new MoveState { Position = new(10, -0.15f, -10), Swimming = true, WaterExcursion = WaterExcursionState.Surface };
        var hop = scene.Binding.Step(new(surface, scene.Frame, null), new(Vector2.Zero, false, 0, jump: true), 1f / 30, lease);
        Assert.Equal(WaterExcursionState.AirborneFromWater, hop.State.State.WaterExcursion);
        Assert.Equal((float)(7d - (double)scene.Tuning.Gravity / 30), hop.State.State.VerticalVelocity);
    }

    [Theory]
    [InlineData("WadeWallAndSlide", WaterTraversalMode.WadeOnly)]
    [InlineData("DryOnly", WaterTraversalMode.DryOnly)]
    public void WadeOnlyShoreSlideAndDryOnlyRefusal(string scenario, WaterTraversalMode mode)
    {
        using var scene = new TileScene(mode);
        using var lease = scene.Acquire();
        var wet = scene.State(new(10, -1, -10), false);
        var refused = scene.Binding.StepTowards(wet, Vector2.Zero, false, 1f / 30, lease);
        Assert.Equal(MovementStepOutcome.PlacementRefused, refused.Outcome);
        Assert.Equal(wet.State.Position, refused.State.State.Position);
        if (scenario == "DryOnly") return;
        var dry = scene.State(new(7.74f, -3.25f, -10));
        var slide = scene.Binding.StepTowards(dry, Vector2.Normalize(new(1, 1)), false, 0.1f, lease);
        Assert.Equal(MovementStepOutcome.Blocked, slide.Outcome);
        Assert.True(slide.State.State.Position.X <= 7.751f);
        Assert.True(slide.State.State.Position.Z > -10);
    }

    [Theory]
    [InlineData("BedAndDownSeat")]
    public void BedAndShallowToDeepDownSeatRespectFullBody(string scenario)
    {
        Assert.Equal("BedAndDownSeat", scenario);
        using var scene = new TileScene(WaterTraversalMode.SurfaceSwimmer, scenario);
        using var lease = scene.Acquire();
        var fallingState = new MoveState
        {
            Position = new(16, -3, -10),
            VerticalVelocity = -20,
            WaterExcursion = WaterExcursionState.AirborneFromWater
        };
        var falling = new FramedMovementState(fallingState, scene.Frame, null);
        var result = scene.Binding.Step(falling, new(Vector2.Zero, false, 0), 0.2f, lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Position.Y - 0.75f >= -4);
        Assert.True(result.State.State.Swimming);
        var shallow = scene.State(new(11.5f, -80 * 0.01f + 0.75f, -10));
        var downSeat = scene.Binding.StepTowards(shallow, Vector2.UnitX, false, 0.3f, lease);
        Assert.Equal(MovementStepOutcome.Advanced, downSeat.Outcome);
        Assert.True(downSeat.State.State.Position.X > 12);
        Assert.True(downSeat.State.State.Swimming);
    }

    [Theory]
    [InlineData("ThinDryBand")]
    [InlineData("TiedShoreSolidCorner")]
    public void ThinDryBandAndTiedShoreSolidCornerNeverTunnel(string scenario)
    {
        using var scene = new TileScene(WaterTraversalMode.DryOnly, scenario);
        using var lease = scene.Acquire();
        bool thin = scenario == "ThinDryBand";
        var before = scene.State(new(thin ? 0 : 7.5f, -3.25f, thin ? -1 : -10.5f));
        var result = scene.Binding.StepTowards(before, Vector2.UnitX, true, 0.2f, lease);
        Assert.Equal(MovementStepOutcome.Blocked, result.Outcome);
        Assert.True(result.State.State.Position.X <= (thin ? 0.151f : 7.751f));
        Assert.False(result.State.State.Swimming);
    }

    [Theory]
    [InlineData("ForbiddenStartAndLagRecovery")]
    public void ForbiddenStartAndRecoveryPayLagOnlyAfterAdmission(string scenario)
    {
        Assert.Equal("ForbiddenStartAndLagRecovery", scenario);
        using var scene = new TileScene(WaterTraversalMode.DryOnly);
        using var lease = scene.Acquire();
        var wetState = new MoveState { Position = new(10, -3.35f, -10), Grounded = true, StepDeltaY = 0.1f };
        var wet = new FramedMovementState(wetState, scene.Frame, null);
        var refused = scene.Binding.StepTowards(wet, Vector2.UnitX, false, 0.1f, lease);
        Assert.Equal(MovementStepOutcome.PlacementRefused, refused.Outcome);
        Assert.Equal(wet.State.Position, refused.State.State.Position);
        var recoveredState = new MoveState { Position = new(7, -3.35f, -10), Grounded = true, StepDeltaY = 0.1f };
        var recovered = new FramedMovementState(recoveredState, scene.Frame, null);
        var result = scene.Binding.StepTowards(recovered, -Vector2.UnitX, false, 0.1f, lease);
        Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
        Assert.True(result.State.State.Position.X < 7, $"position {result.State.State.Position}, rise {result.State.State.StepDeltaY}, grounded {result.State.State.Grounded}, commanded {result.State.State.CommandedVelocity}");
        Assert.Equal(-3.25f, result.State.State.Position.Y);
        Assert.True(result.State.State.StepDeltaY > 0);
    }

    sealed class TileScene : IDisposable
    {
        readonly BepuPhysicsWorld _physics = new(Vector3.Zero);
        readonly TileColliderRegistration _installed;
        internal MoveTuning Tuning { get; }
        internal EffectiveMovementBinding Binding { get; }
        internal MovementFrameDescriptor Frame { get; } = new(default, Vector3.Zero, 1);

        internal TileScene(WaterTraversalMode mode, string scenario = "")
        {
            Tuning = OrdinaryTileWaterTests.Tuning(mode);
            var doc = TileWorldPhysicsTestData.FlatWorld();
            if (scenario == "ThinDryBand") doc.TileSize = 0.05f;
            for (int z = 0; z < 64; z++)
                for (int x = 0; x < 64; x++)
                {
                    doc.SetCornerHeightCm(x, z, 0, (short)(scenario == "BedAndDownSeat" && x <= 12 ? -80 : -400));
                    if (x >= 8 && x < (scenario == "ThinDryBand" ? 9 : 24)) doc.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
                }
            doc.SetCornerHeightCm(8, 0, 0, 2);
            if (scenario == "TiedShoreSolidCorner") doc.AddObject("bench", 8, 10, 0, 0);
            var colliders = TileWorldColliders.Build(doc, TileWorldPhysicsTestData.Catalogs(), "water-world");
            _installed = colliders.AddTo(_physics);
            Binding = EffectiveMovementBinding.Ordinary(Tuning, _physics, _installed.CreateMovementRegistration("water-world"),
                colliders.MovementGroundHeight, colliders.MovementGroundNormal,
                MovementBoundary.Rectangle(0, -64, 64, 0), colliders.Medium);
        }

        internal MovementQueryLease Acquire()
        {
            Assert.Equal(MovementAvailability.Known, Binding.TryAcquire(Binding.CreateOrdinaryScope(Frame), out var lease));
            return Assert.IsType<MovementQueryLease>(lease);
        }

        internal FramedMovementState State(Vector3 centre, bool grounded = true) =>
            new(new MoveState { Position = centre, Grounded = grounded }, Frame, null);

        public void Dispose() { Binding.Dispose(); _installed.Dispose(); _physics.Dispose(); }
    }
}
