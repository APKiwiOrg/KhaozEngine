using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public partial class OrdinaryContactHeadTests
{
    [Theory]
    [InlineData(false, "dry")]
    [InlineData(true, "dry")]
    [InlineData(false, "stairs")]
    [InlineData(true, "stairs")]
    [InlineData(false, "entry")]
    [InlineData(true, "entry")]
    [InlineData(false, "exit")]
    [InlineData(true, "exit")]
    public void AuthorityAndClientPredictTheSameOrdinaryComposition(bool sharded, string row)
    {
        float h = row == "entry" ? -4 : row == "exit" ? 0.1f : 0;
        using var heads = new OrdinaryNetworkHeads(sharded, water: row is "entry" or "exit", h: h, stairs: row == "stairs", spawn: new(0, h + 0.75f, 0));
        var basis = heads.State;
        basis.Position = new(0, row == "entry" ? -1 : h + 0.75f, 2);
        basis.Move.Grounded = row != "entry";
        basis.Move.Swimming = row == "exit";
        basis.Move.WaterExcursion = row == "exit" ? WaterExcursionState.Surface : WaterExcursionState.None;
        Assert.True(heads.Place(basis));
        heads.Pump(1);
        basis = heads.State;
        using var simulator = heads.ClientWorld.Simulator();
        var command = new MoveCommand(Vector2.UnitX, false, row == "dry" ? 0.75f : 0, faceCamera: row == "dry");
        MoveState expected;
        using (simulator.BeginExplicitRead(basis)) expected = simulator.Step(basis, command, 0.1f).Move;
        Assert.True(heads.Client.SendInput(command) >= 0);
        Assert.Equal(expected, heads.Client.LocalPredictedState.Move);
        heads.Pump(1);
        var actual = heads.State.Move;
        AssertSame(sharded ? CellPublication(expected) : expected, actual);
        Assert.Equal(WirePublication(actual), heads.Client.LocalPredictedState.Move);
        if (row == "stairs") Assert.True(actual.ClimbRateEwma > 0);
    }

    [Theory]
    [InlineData("idle", 0f)]
    [InlineData("dry", 0f)]
    [InlineData("stairs", 0f)]
    [InlineData("entry", 0f)]
    [InlineData("exit", 0f)]
    [InlineData("dry", 4f)]
    public void EveryNetworkHeadMatchesOrdinaryCompositionDryWetAndStairSignals(string row, float physicsY)
    {
        float h = row == "entry" ? -4 : row == "exit" ? 0.1f : 0;
        using var world = new OrdinaryPlayerWorld(new(256, physicsY, 128), stairs: row == "stairs",
            water: row is "entry" or "exit", groundY: h);
        using var reference = world.Reference();
        using var simulator = world.Simulator();
        var initial = world.Initial(y: row == "entry" ? -1 : null);
        if (row == "entry") initial.Move.Grounded = false;
        if (row == "exit") { initial.Move.Swimming = true; initial.Move.WaterExcursion = WaterExcursionState.Surface; }
        var command = row == "idle" ? MoveCommand.Idle : new MoveCommand(Vector2.UnitX, false,
            row == "dry" ? 0.75f : 0, faceCamera: row == "dry");
        var descriptor = new MovementFrameDescriptor(world.Frame, world.Physics.Origin, 1);
        Assert.Equal(MovementAvailability.Known, reference.TryAcquire(reference.CreateOrdinaryScope(descriptor), out var queries));
        MoveState expected;
        using (queries)
        {
            var local = initial.Move;
            local.Position -= new Vector3(world.Physics.Origin.X - world.Frame.Anchor.X, physicsY,
                world.Physics.Origin.Z - world.Frame.Anchor.Z);
            var result = reference.Step(new(local, descriptor, null), command, OrdinaryPlayerWorld.Dt, queries!);
            Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            expected = result.State.State;
            expected.Position += new Vector3(world.Physics.Origin.X - world.Frame.Anchor.X, physicsY,
                world.Physics.Origin.Z - world.Frame.Anchor.Z);
        }
        using (var read = simulator.BeginExplicitRead(initial))
        {
            Assert.True(read!.BasisValid);
            var actual = simulator.Step(initial, command, OrdinaryPlayerWorld.Dt);
            Assert.Equal(expected, actual.Move);
            Assert.Equal(expected.FacingYaw, actual.Move.FacingYaw);
            if (row == "stairs") Assert.True(actual.Move.ClimbRateEwma > 0);
            if (row == "entry") Assert.True(actual.Swimming);
            if (row == "exit") Assert.False(actual.Swimming);
        }
        if (row is "idle" or "dry" or "stairs")
        {
            var local = initial.Move;
            local.Position.Y -= physicsY;
            var dryReference = CharacterMovement.Step(local, command, OrdinaryPlayerWorld.Dt,
                (_, _) => h - physicsY, OrdinaryPlayerWorld.Tuning, null, world.Physics);
            dryReference.Position.Y += physicsY;
            Assert.Equal(dryReference, expected);
        }
        using var system = new PlayerMovementSystem(world.Ground, OrdinaryPlayerWorld.Tuning, null, null,
            world.Physics, null, world.Frame, SamplerSpace.World, null, world.Field, world.Registration, world.Boundary);
        var ecs = new World();
        var entity = ecs.Spawn();
        ecs.Set(entity, new NetId(1));
        ecs.Set(entity, ReplicatedPosition.InFrame(world.Frame, initial.Position));
        MovementComponents.Set(ecs, entity, initial);
        ecs.Set(entity, new PendingMove { Command = command });
        system.Update(ecs, OrdinaryPlayerWorld.Dt);
        var cell = PlayerMoveState.From(ecs.Get<ReplicatedPosition>(entity), ecs.Get<MovementState>(entity),
            ecs.Get<MovementOwnerState>(entity));
        AssertSame(CellPublication(expected), cell.Move);
    }

    [Fact]
    public void FrameSamplersBecomeAbsoluteProvidersIncludingPhysicsYOrigin()
    {
        using var world = new OrdinaryPlayerWorld(new(256, 4, 128));
        float Sample(float x, float z)
        {
            Assert.InRange(x, -20, 20);
            Assert.InRange(z, -20, 20);
            return -4;
        }
        using var sim = new PlayerMoveSimulator(Sample, OrdinaryPlayerWorld.Tuning, null, null, world.Physics,
            null, SamplerSpace.Frame, null, null, world.Registration, world.Boundary) { Frame = world.Frame };
        var basis = world.Initial();
        using var read = sim.BeginExplicitRead(basis);
        Assert.True(read!.BasisValid);
        var actual = sim.Step(basis, new(Vector2.UnitX, false, 0), 0.1f);
        Assert.Equal(new Vector3(0.4f, 0.75f, 0), actual.Position);
        Assert.True(actual.Grounded);
    }

    internal static void AssertSame(MoveState expected, MoveState actual)
    {
        var differences = new System.Collections.Generic.List<string>();
        foreach (var field in typeof(MoveState).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            object? wanted = field.GetValue(expected), got = field.GetValue(actual);
            if (!Equals(wanted, got)) differences.Add($"{field.Name} expected={wanted} actual={got}");
        }
        Assert.True(expected.Equals(actual), string.Join("\n", differences));
    }

    internal static MoveState WirePublication(MoveState state)
    {
        var player = new PlayerMoveState { Move = state };
        var projected = PlayerMoveState.From(ReplicatedPosition.FromWorld(state.Position, WorldFrame.Origin), MovementState.From(player),
            MovementOwnerState.From(player));
        return projected.Move;
    }

    internal static MoveState CellPublication(MoveState state)
    {
        // These outputs remain in the cell's live component. The released public read seeds
        // the EWMA from the quantized climb rate, and the codec omits these per-tick outputs.
        var projected = WirePublication(state);
        projected.CommandedVelocity = state.CommandedVelocity;
        projected.LandingImpactSpeed = state.LandingImpactSpeed;
        projected.SupportGranted = state.SupportGranted;
        return projected;
    }

    [Fact]
    public void WireGenerationRemainsFourteen() => Assert.Equal(14, MoveProtocol.WireProtocolVersion);

    [Fact]
    public void ConfiguredOrdinaryBoundaryWinsOverWorldBounds()
    {
        using var world = new OrdinaryPlayerWorld();
        using var sim = world.Simulator(bounds: new RectBounds(-1, -1, 1, 1),
            boundary: MovementBoundary.Rectangle(-2, -2, 2, 2));
        var basis = world.Initial(1.8f);
        using var read = sim.BeginExplicitRead(basis);
        Assert.True(read!.BasisValid);
        Assert.Equal(2, sim.Step(basis, new(Vector2.UnitX, false, 0), 0.1f).Position.X);
    }
}
