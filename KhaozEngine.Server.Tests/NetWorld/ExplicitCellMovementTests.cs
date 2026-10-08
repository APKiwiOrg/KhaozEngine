using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ExplicitCellMovementTests
{
    static ExplicitPlayerMovement Settings(ExplicitPlayerEnvironment environment) =>
        new(environment.Context, environment.Scope, new(WaterTraversalMode.SurfaceSwimmer, 7));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CellPublishesPoseAndOwnerTimersBeforeReleasingItsRead(bool ownerAlreadyPresent)
    {
        using var environment = new ExplicitPlayerEnvironment();
        var world = new World();
        var initial = ExplicitPlayerEnvironment.Initial;
        Entity entity = Spawn(world, initial, ownerAlreadyPresent);
        world.Set(entity, new PendingMove { Command = new(Vector2.UnitX, false, 0) });
        int released = 0;
        environment.OnPinDispose = () =>
        {
            Assert.InRange(world.Get<ReplicatedPosition>(entity).Local.X, 0.3999f, 0.4001f);
            Assert.True(world.Has<MovementOwnerState>(entity));
            Assert.Equal(0, world.Get<MovementOwnerState>(entity).JumpBufferRemaining);
            Assert.Throws<InvalidOperationException>(() => environment.Physics.Step(0.1f));
            released++;
        };
        SystemFor(environment).Update(world, 0.1f);
        Assert.True(released > 0);
        environment.OnPinDispose = null;
        environment.Physics.Step(0.1f);
    }

    [Fact]
    public void CellCarriesWaterOriginFlightIntoAndOutOfTheSharedSimulator()
    {
        using var environment = new ExplicitPlayerEnvironment();
        var world = new World();
        var initial = ExplicitPlayerEnvironment.Initial;
        initial.Move.Position.Y = 2;
        initial.Move.Grounded = false;
        initial.Move.VerticalVelocity = 3;
        initial.Move.WaterExcursion = WaterExcursionState.AirborneFromWater;
        Entity entity = Spawn(world, initial, true);
        world.Set(entity, new PendingMove { Command = new(Vector2.UnitX, true, 0) });
        var system = SystemFor(environment);
        system.Update(world, 0.1f);
        Assert.Equal(WaterExcursionState.AirborneFromWater, world.Get<MovementState>(entity).WaterExcursion);
        Assert.InRange(world.Get<ReplicatedPosition>(entity).Local.X, 0.2999f, 0.3001f);
        Assert.InRange(world.Get<ReplicatedPosition>(entity).Local.Y, 2.0999f, 2.1001f);
        system.Update(world, 0.1f);
        Assert.Equal(WaterExcursionState.AirborneFromWater, world.Get<MovementState>(entity).WaterExcursion);
        Assert.InRange(world.Get<ReplicatedPosition>(entity).Local.X, 0.5999f, 0.6001f);
    }

    [Fact]
    public void CellRefusalConsumesBufferedJumpAndClearsTickEvents()
    {
        using var environment = new ExplicitPlayerEnvironment();
        var world = new World();
        var initial = ExplicitPlayerEnvironment.Initial;
        initial.Move.JumpBufferRemaining = 1;
        Entity entity = Spawn(world, initial, true);
        world.Set(entity, new PendingMove { Command = new(Vector2.UnitX, false, 0, jump: true) });
        environment.Availability = MovementAvailability.Unresolved;
        var system = SystemFor(environment);
        system.Update(world, 0.1f);
        Assert.Equal(initial.Move.Position, world.Get<ReplicatedPosition>(entity).Local);
        Assert.Equal(0, world.Get<MovementOwnerState>(entity).JumpBufferRemaining);
        Assert.Equal(Vector2.Zero, world.Get<MovementState>(entity).CommandedVelocity);
        environment.Availability = MovementAvailability.Known;
        world.Set(entity, new PendingMove { Command = MoveCommand.Idle });
        system.Update(world, 0.1f);
        Assert.True(world.Get<MovementState>(entity).Grounded);
        Assert.Equal(0, world.Get<MovementState>(entity).VerticalVelocity);
    }


    [Fact]
    public void FarCellReconstructsItsLocalBasisAndPublishesTheMatchingFrame()
    {
        Vector3 origin = new(65536, 0, -65536);
        using var environment = new ExplicitPlayerEnvironment(origin);
        var world = new World();
        var initial = ExplicitPlayerEnvironment.Initial;
        initial.Position += origin;
        Entity entity = Spawn(world, initial, true);
        world.Set(entity, new PendingMove { Command = new(Vector2.UnitX, false, 0) });
        WorldFrame frame = WorldFrame.Nearest(origin);
        SystemFor(environment, frame).Update(world, 0.1f);
        var position = world.Get<ReplicatedPosition>(entity);
        Assert.Equal(frame, position.Frame);
        Assert.InRange(position.Local.X, 0.3999f, 0.4001f);
        Assert.All(environment.RebuiltPositions, value => Assert.InRange(value.X, -1f, 1f));
        Assert.Equal(origin, environment.Physics.Origin);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkippedCellEntitiesDoNotAcquireQueriesOrLoseTheirCarriedExcursion(bool migrating)
    {
        using var environment = new ExplicitPlayerEnvironment();
        var world = new World();
        var initial = ExplicitPlayerEnvironment.Initial;
        initial.Move.WaterExcursion = WaterExcursionState.AirborneFromWater;
        Entity entity = Spawn(world, initial, true);
        world.Set(entity, new PendingMove { Command = new(Vector2.UnitX, false, 0) });
        if (migrating) world.Set(entity, new Migrating()); else world.Set(entity, new Ghost());
        SystemFor(environment).Update(world, 0.1f);
        Assert.Equal(0, environment.Prepared);
        Assert.Equal(initial.Move.Position, world.Get<ReplicatedPosition>(entity).Local);
        Assert.Equal(WaterExcursionState.AirborneFromWater, world.Get<MovementState>(entity).WaterExcursion);
    }

    static PlayerMovementSystem SystemFor(ExplicitPlayerEnvironment environment, WorldFrame frame = default) => new(
        (_, _) => throw new InvalidOperationException("Legacy sampler called."), ExplicitPlayerEnvironment.Tuning,
        null, null, environment.Physics, null, frame, SamplerSpace.World, Settings(environment));

    static Entity Spawn(World world, PlayerMoveState state, bool owner)
    {
        Entity entity = world.Spawn();
        world.Set(entity, new NetId(1));
        world.Set(entity, ReplicatedPosition.FromWorld(state.Position, WorldFrame.Origin));
        world.Set(entity, MovementState.From(state));
        if (owner) world.Set(entity, MovementOwnerState.From(state));
        return entity;
    }
}
