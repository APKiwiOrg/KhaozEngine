using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.Tests.NetWorld.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class SupportQueryPlayerInvocationTests
{
    [Theory]
    [InlineData(MovementControllerKind.Legacy)]
    [InlineData(MovementControllerKind.Contact)]
    public void SystemCountsExactlyTheSelectedEntityStepsIncludingIdleAndRejectedSteps(MovementControllerKind controller)
    {
        var first = Run(controller);
        var second = Run(controller);
        var selected = controller == MovementControllerKind.Legacy ? SupportQueryCounter.PlayerLegacySteps : SupportQueryCounter.PlayerContactSteps;
        Assert.Equal(12, first.Total[selected]);
        for (int i = 42; i < 48; i++)
            if (i != (int)selected) Assert.Equal(0, first.Total[(SupportQueryCounter)i]);
        foreach (var counter in Enum.GetValues<SupportQueryCounter>())
            Assert.Equal(first.Total[counter], second.Total[counter]);
        if (controller == MovementControllerKind.Contact) Assert.True(first.Total[SupportQueryCounter.Find] > 0);
    }

    [Fact]
    public void WorkerCapturesCombinePlayerCountsAndLeaveTheCallerUnchanged()
    {
        using var capture = SupportQueryCapture.Begin("caller");
        SupportQueryMeasurement[] results = new SupportQueryMeasurement[2];
        var failures = new Exception?[2];
        var workers = Enumerable.Range(0, 2).Select(index => new Thread(() =>
        {
            try
            {
                Assert.False(SupportQueryDiagnostics.Enabled);
                results[index] = Run((MovementControllerKind)index);
                Assert.False(SupportQueryDiagnostics.Enabled);
            }
            catch (Exception error) { failures[index] = error; }
        })).ToArray();
        foreach (var worker in workers) worker.Start();
        foreach (var worker in workers) worker.Join();
        Assert.All(failures, failure => Assert.Null(failure));
        var combined = SupportQueryMeasurement.Combine(results);
        Assert.Equal(12, combined.Total[SupportQueryCounter.PlayerLegacySteps]);
        Assert.Equal(12, combined.Total[SupportQueryCounter.PlayerContactSteps]);
        for (int i = 42; i < 46; i++) Assert.Equal(0, combined.Total[(SupportQueryCounter)i]);
        foreach (var counter in Enum.GetValues<SupportQueryCounter>()) Assert.Equal(0, capture.Snapshot().Total[counter]);
    }

    [Theory]
    [InlineData(MovementControllerKind.Legacy)]
    [InlineData(MovementControllerKind.Contact)]
    public void DirectSimulatorConsumersDoNotCountAsAuthoritativeSystemSteps(MovementControllerKind controller)
    {
        using var scene = new OrdinaryPlayerWorld();
        using var simulator = scene.Simulator(TuningFor(controller));
        using var capture = SupportQueryCapture.Begin("prediction");
        var basis = scene.Initial();
        using (simulator.BeginExplicitRead(basis))
        {
            var result = simulator.Step(basis, new MoveCommand(Vector2.UnitX, false, 0), 0.1f);
            Assert.True(result.Position.X > basis.Position.X);
        }
        // The explicit fallback is part of a Step, including without an acquired read.
        _ = simulator.Step(basis, MoveCommand.Idle, 0.1f);
        for (int i = 42; i < 48; i++) Assert.Equal(0, capture.Snapshot().Total[(SupportQueryCounter)i]);
    }

    [Theory]
    [InlineData(MovementControllerKind.Legacy)]
    [InlineData(MovementControllerKind.Contact)]
    public void SystemLeavesADisabledRecorderWithoutStorage(MovementControllerKind controller)
    {
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(false));
        try
        {
            Exercise(controller);
            Assert.False(SupportQueryDiagnostics.Enabled);
            var state = SupportQueryDiagnostics.Exchange(default);
            SupportQueryDiagnostics.Exchange(state);
            Assert.Null(state.Counts);
            Assert.Equal(default, SupportQueryDiagnostics.Snapshot());
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    static SupportQueryMeasurement Run(MovementControllerKind controller)
    {
        using var capture = SupportQueryCapture.Begin("players");
        Exercise(controller);
        capture.Dispose();
        return capture.Snapshot();
    }

    static MoveTuning TuningFor(MovementControllerKind controller) => OrdinaryPlayerWorld.Tuning with
    {
        Controller = controller,
        Water = controller == MovementControllerKind.Legacy ? default : OrdinaryPlayerWorld.Tuning.Water,
    };

    static void Exercise(MovementControllerKind controller)
    {
        using var scene = new OrdinaryPlayerWorld();
        var tuning = TuningFor(controller);
        using var system = new PlayerMovementSystem(scene.Ground, tuning, null, null, scene.Physics, null,
            scene.Frame, SamplerSpace.World, null, scene.Field, scene.Registration, scene.Boundary);
        var world = new World();
        var moving = Spawn(1, 0, new MoveCommand(Vector2.UnitX, false, 0));
        var idle = Spawn(2, 2, MoveCommand.Idle);
        var rejected = Spawn(3, 30, MoveCommand.Idle);
        var ghost = Spawn(4, 5, MoveCommand.Idle);
        world.Set(ghost, new Ghost { Source = new CellCoord(0, 0) });
        var migrating = Spawn(5, 7, MoveCommand.Idle);
        world.Set(migrating, new Migrating { Destination = new CellCoord(1, 0) });
        var missingCommand = world.Spawn();
        world.Set(missingCommand, new NetId(6));
        world.Set(missingCommand, ReplicatedPosition.InFrame(scene.Frame, new(9, 0.75f, 0)));
        world.Set(missingCommand, new MovementState());
        // Three selected entities for two ticks, then two for three ticks, gives twelve Steps.
        for (int tick = 0; tick < 5; tick++)
        {
            if (tick == 2) world.Set(moving, new Migrating { Destination = new CellCoord(1, 0) });
            system.Update(world, 0.1f);
        }
        Assert.True(world.Get<ReplicatedPosition>(moving).Local.X > 0);
        Assert.Equal(2, world.Get<ReplicatedPosition>(idle).Local.X);
        Assert.Equal(30, world.Get<ReplicatedPosition>(rejected).Local.X);
        Assert.Equal(5, world.Get<ReplicatedPosition>(ghost).Local.X);
        Assert.Equal(7, world.Get<ReplicatedPosition>(migrating).Local.X);
        if (controller == MovementControllerKind.Contact)
        {
            using var simulator = scene.Simulator(tuning);
            using var read = simulator.BeginExplicitRead(scene.Initial(x: 30));
            Assert.False(read!.BasisValid);
        }

        Entity Spawn(int id, float x, MoveCommand command)
        {
            var entity = world.Spawn();
            world.Set(entity, new NetId(id));
            world.Set(entity, ReplicatedPosition.InFrame(scene.Frame, new(x, 0.75f, 0)));
            MovementComponents.Set(world, entity, scene.Initial(x));
            world.Set(entity, new PendingMove { Command = command });
            return entity;
        }
    }
}
