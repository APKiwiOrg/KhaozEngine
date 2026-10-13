using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class OrdinaryContactHeadTests
{
    [Fact]
    public void OrdinaryReadUsesActualPhysicsOriginAndAdvancesEpochAfterRebase()
    {
        using var world = new OrdinaryPlayerWorld();
        using var simulator = world.Simulator();
        var basis = world.Initial(4, 4);
        MovementFrameDescriptor first;
        MovementQueryScope firstScope;
        using (var read = simulator.BeginExplicitRead(basis))
        {
            Assert.True(read!.BasisValid);
            first = read.Queries!.Frame;
            firstScope = read.Queries.Witness.Scope;
        }
        var origin = new Vector3(256, 4, 128);
        world.Physics.Rebase(origin);
        simulator.Frame = WorldFrame.Nearest(origin.X, origin.Z);
        basis = basis.ToAnchor(new(simulator.Frame.Anchor.X, simulator.Frame.Anchor.Z));
        using var next = simulator.BeginExplicitRead(basis);
        Assert.True(next!.BasisValid);
        Assert.Equal(origin, next.Queries!.Frame.PhysicsOrigin);
        Assert.Equal(simulator.Frame, next.Queries.Frame.Frame);
        Assert.Equal(first.Epoch + 1, next.Queries.Frame.Epoch);
        Assert.NotEqual(firstScope, next.Queries.Witness.Scope);
        var actual = simulator.Step(basis, MoveCommand.Idle, 0.1f);
        Assert.Equal(basis.Position, actual.Position);
        Assert.Equal(0.75f, actual.Position.Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfiguredBoundaryWinsForHeadPredictionMotionAndPlacement(bool sharded)
    {
        var boundary = MovementBoundary.Rectangle(-2, -2, 2, 2);
        using var heads = new OrdinaryNetworkHeads(sharded, spawn: new(0, 0.75f, 1),
            bounds: new RectBounds(-1, -1, 1, 1), boundary: boundary);
        var basis = heads.State;
        basis.Position = new(1.8f, 0.75f, 1);
        Assert.True(heads.Place(basis));
        heads.Pump(1);
        Assert.True(heads.Client.SendInput(new(Vector2.UnitX, false, 0)) >= 0);
        Assert.Equal(2, heads.Client.LocalPredictedState.Position.X);
        heads.Pump(1);
        Assert.Equal(2, heads.State.Position.X);
        Assert.Equal(WirePublication(heads.State.Move), heads.Client.LocalPredictedState.Move);
        var before = heads.State;
        var outside = before;
        outside.Position = new(2.1f, 0.75f, 1);
        Assert.False(heads.Place(outside, teleport: true));
        Assert.Equal(before, heads.State);
    }
}
