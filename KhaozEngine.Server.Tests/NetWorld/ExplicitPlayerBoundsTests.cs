using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ExplicitPlayerBoundsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitMovementHonoursTheConfiguredPlayArea(bool circle)
    {
        using var environment = new ExplicitPlayerEnvironment();
        WorldBounds bounds = circle ? new CircleBounds(Vector2.Zero, 0.5f) : new RectBounds(-0.5f, -1, 0.5f, 1);
        var simulator = Simulator(environment, bounds);
        var initial = ExplicitPlayerEnvironment.Initial;
        using var read = simulator.BeginExplicitRead(initial);
        Assert.True(read!.BasisValid);
        var moved = simulator.Step(initial, new(Vector2.UnitX, false, 0), 0.25f);
        Assert.True(bounds.Contains(moved.Position.X, moved.Position.Z));
        Assert.InRange(moved.Position.X, 0.49f, 0.5f);
        Assert.Equal(MovementStepOutcome.Blocked, simulator.LastExplicitOutcome);
        Assert.True(moved.Grounded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOutsideCorrectionBasisIsNotPublishedAsAnInsidePlacement(bool circle)
    {
        using var environment = new ExplicitPlayerEnvironment();
        WorldBounds bounds = circle ? new CircleBounds(Vector2.Zero, 0.5f) : new RectBounds(-0.5f, -1, 0.5f, 1);
        var simulator = Simulator(environment, bounds);
        var initial = ExplicitPlayerEnvironment.Initial;
        initial.Position = new(2, 0.751f, 0);
        using var read = simulator.BeginExplicitRead(initial);
        Assert.False(read!.BasisValid);
        Assert.Equal(MovementStepOutcome.PlacementRefused, read.Outcome);
        Assert.Equal(initial.Position, simulator.Step(initial, MoveCommand.Idle, 0.1f).Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TangentialInputAtTheBoundaryStaysInsideAndMakesProgress(bool circle)
    {
        using var environment = new ExplicitPlayerEnvironment();
        WorldBounds bounds = circle ? new CircleBounds(Vector2.Zero, 0.5f) : new RectBounds(-0.5f, -1, 0.5f, 1);
        var simulator = Simulator(environment, bounds);
        var initial = ExplicitPlayerEnvironment.Initial;
        initial.Position = new(0.5f, 0.751f, 0);
        using var read = simulator.BeginExplicitRead(initial);
        Assert.True(read!.BasisValid);
        var moved = simulator.Step(initial, new(Vector2.UnitY, false, 0), 0.1f);
        Assert.True(bounds.Contains(moved.Position.X, moved.Position.Z));
        Assert.True(moved.Position.Z < -0.2f);
        Assert.True(moved.Grounded);
    }

    [Fact]
    public void ACustomPointOnlyClampCannotMasqueradeAsACompletePathProof()
    {
        using var environment = new ExplicitPlayerEnvironment();
        var simulator = Simulator(environment, new PointOnlyBounds());
        using var read = simulator.BeginExplicitRead(ExplicitPlayerEnvironment.Initial);
        Assert.False(read!.BasisValid);
        Assert.Equal(MovementStepOutcome.EnvironmentUnresolved, read.Outcome);
    }


    [Fact]
    public void ARebasedCircleKeepsTheSamePolicyIdentityAndLocalTrajectory()
    {
        Vector3 origin = new(65536, 0, -65536);
        using var near = new ExplicitPlayerEnvironment();
        using var far = new ExplicitPlayerEnvironment(origin);
        var nearBounds = new CircleBounds(Vector2.Zero, 0.5f);
        var farBounds = new CircleBounds(new(origin.X, origin.Z), 0.5f);
        Assert.Equal(MovementAvailability.Known, farBounds.TryCaptureExplicit(Vector3.Zero, out var absolute));
        Assert.Equal(MovementAvailability.Known, farBounds.TryCaptureExplicit(origin, out var rebased));
        Assert.Equal(absolute!.SemanticIdentity, rebased!.SemanticIdentity);
        var a = Simulator(near, nearBounds);
        var b = Simulator(far, farBounds);
        b.Frame = WorldFrame.Nearest(origin);
        var first = ExplicitPlayerEnvironment.Initial;
        first.Move.Position.X = 0.5f;
        var second = first;
        second.FrameAnchor = new(origin.X, origin.Z);
        using var ar = a.BeginExplicitRead(first);
        using var br = b.BeginExplicitRead(second);
        Assert.True(ar!.BasisValid);
        Assert.True(br!.BasisValid);
        var command = new MoveCommand(Vector2.UnitY, false, 0);
        var moved = a.Step(first, command, 0.1f);
        var translated = b.Step(second, command, 0.1f);
        Assert.Equal(moved.Move, translated.Move);
        Assert.Equal(a.LastExplicitOutcome, b.LastExplicitOutcome);
        Assert.Equal(second.FrameAnchor, translated.FrameAnchor);
    }


    [Fact]
    public void TheCapturedBoundaryFrameCannotChangeInsideAnExplicitRead()
    {
        using var environment = new ExplicitPlayerEnvironment();
        var simulator = Simulator(environment, new RectBounds(-1, -1, 1, 1));
        using (var read = simulator.BeginExplicitRead(ExplicitPlayerEnvironment.Initial))
        {
            Assert.True(read!.BasisValid);
            Assert.Throws<InvalidOperationException>(() => simulator.Frame = WorldFrame.Nearest(new(128, 0, 0)));
            Assert.Equal(WorldFrame.Origin, simulator.Frame);
        }
        simulator.Frame = WorldFrame.Nearest(new(128, 0, 0));
        Assert.NotEqual(WorldFrame.Origin, simulator.Frame);
    }

    [Fact]
    public void ScopePreparationCannotReplaceTheFrameWhoseBoundaryWasCaptured()
    {
        using var environment = new ExplicitPlayerEnvironment();
        PlayerMoveSimulator? simulator = null;
        bool attemptChange = true;
        var settings = new ExplicitPlayerMovement(environment.Context, (state, frame) =>
        {
            if (attemptChange) simulator!.Frame = WorldFrame.Nearest(new(128, 0, 0));
            return environment.Scope(state, frame);
        }, new(WaterTraversalMode.SurfaceSwimmer, 7));
        simulator = new((_, _) => 0, ExplicitPlayerEnvironment.Tuning, null, new RectBounds(-1, -1, 1, 1),
            environment.Physics, null, SamplerSpace.World, settings);
        Assert.Throws<InvalidOperationException>(() => { using var read = simulator.BeginExplicitRead(ExplicitPlayerEnvironment.Initial); });
        Assert.Equal(WorldFrame.Origin, simulator.Frame);
        attemptChange = false;
        using var valid = simulator.BeginExplicitRead(ExplicitPlayerEnvironment.Initial);
        Assert.True(valid!.BasisValid);
    }

    static PlayerMoveSimulator Simulator(ExplicitPlayerEnvironment environment, WorldBounds bounds) => new(
        (_, _) => throw new InvalidOperationException("Legacy sampler called."), ExplicitPlayerEnvironment.Tuning,
        null, bounds, environment.Physics, null, SamplerSpace.World,
        new(environment.Context, environment.Scope, new(WaterTraversalMode.SurfaceSwimmer, 7)));

    sealed class PointOnlyBounds : WorldBounds
    {
        public override bool Contains(float x, float z) => Math.Abs(x) <= 0.5f;
        public override Vector2 Clamp(float x, float z) => new(Math.Clamp(x, -0.5f, 0.5f), z);
    }
}
