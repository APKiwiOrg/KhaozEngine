using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class SupportQueryInvocationTests
{
    [Theory]
    [InlineData(MovementControllerKind.Legacy)]
    [InlineData(MovementControllerKind.Contact)]
    public void NpcEntryPointsCountInvocationsRatherThanTravelState(MovementControllerKind controller)
    {
        var first = Run(controller);
        var second = Run(controller);
        var holds = controller == MovementControllerKind.Legacy ? SupportQueryCounter.NpcLegacyHolds : SupportQueryCounter.NpcContactHolds;
        var steps = controller == MovementControllerKind.Legacy ? SupportQueryCounter.NpcLegacySteps : SupportQueryCounter.NpcContactSteps;
        Assert.Equal(2, first.Total[holds]);
        Assert.Equal(3, first.Total[steps]);
        Assert.Equal(2, first.ByLabel["waiting"][holds]);
        Assert.Equal(0, first.ByLabel["waiting"][steps]);
        Assert.Equal(0, first.ByLabel["npc"][holds]);
        Assert.Equal(3, first.ByLabel["npc"][steps]);
        for (int i = 42; i < 48; i++)
            if (i != (int)holds && i != (int)steps) Assert.Equal(0, first.Total[(SupportQueryCounter)i]);
        foreach (var counter in Enum.GetValues<SupportQueryCounter>())
            Assert.Equal(first.Total[counter], second.Total[counter]);
        if (controller == MovementControllerKind.Contact) Assert.True(first.Total[SupportQueryCounter.Find] > 0);
    }

    [Fact]
    public void WorkerCapturesCombineDisjointNpcInvocationsWithoutEnablingOtherThreads()
    {
        using var caller = SupportQueryCapture.Begin("caller");
        var measurements = new SupportQueryMeasurement[2];
        var failures = new Exception?[2];
        var workers = Enumerable.Range(0, 2).Select(index => new Thread(() =>
        {
            try
            {
                Assert.False(SupportQueryDiagnostics.Enabled);
                measurements[index] = Run((MovementControllerKind)index);
                Assert.False(SupportQueryDiagnostics.Enabled);
            }
            catch (Exception error) { failures[index] = error; }
        })).ToArray();
        foreach (var worker in workers) worker.Start();
        foreach (var worker in workers) worker.Join();
        Assert.All(failures, failure => Assert.Null(failure));
        var combined = SupportQueryMeasurement.Combine(measurements);
        Assert.Equal(2, combined.Total[SupportQueryCounter.NpcLegacyHolds]);
        Assert.Equal(2, combined.Total[SupportQueryCounter.NpcContactHolds]);
        Assert.Equal(3, combined.Total[SupportQueryCounter.NpcLegacySteps]);
        Assert.Equal(3, combined.Total[SupportQueryCounter.NpcContactSteps]);
        Assert.Equal(0, combined.Total[SupportQueryCounter.PlayerLegacySteps]);
        Assert.Equal(0, combined.Total[SupportQueryCounter.PlayerContactSteps]);
        var reversed = SupportQueryMeasurement.Combine(measurements.Reverse());
        foreach (var counter in Enum.GetValues<SupportQueryCounter>())
        {
            Assert.Equal(0, caller.Snapshot().Total[counter]);
            Assert.Equal(combined.Total[counter], reversed.Total[counter]);
        }
    }

    [Fact]
    public void NullNpcContextDoesNotRecordAnInvocation()
    {
        using var capture = SupportQueryCapture.Begin("null");
        Assert.Throws<ArgumentNullException>(() => NpcGroundMovement.Hold(default, 0.1f, MoveTuning.Default, null!));
        Assert.Throws<ArgumentNullException>(() => NpcGroundMovement.Step(default, default, false, 0.1f, MoveTuning.Default, null!));
        for (int i = 42; i < 48; i++) Assert.Equal(0, capture.Snapshot().Total[(SupportQueryCounter)i]);
    }

    static SupportQueryMeasurement Run(MovementControllerKind controller)
    {
        using var scene = new OrdinaryContactScene();
        using var context = new GroundMoveContext(scene.Height, scene.Normal, scene.Physics,
            null, null, null, null, scene.Registration, MovementBoundary.Rectangle(-20, -20, 20, 20));
        var tuning = scene.Tuning with
        {
            Controller = controller,
            Water = controller == MovementControllerKind.Legacy ? default : scene.Tuning.Water,
        };
        var body = new MoveState { Position = new(0, tuning.CapsuleHalfHeight, 0), Grounded = true };
        context.ValidateTuning(tuning with
        {
            Controller = controller == MovementControllerKind.Legacy ? MovementControllerKind.Contact : MovementControllerKind.Legacy,
            Water = controller == MovementControllerKind.Contact ? default : scene.Tuning.Water,
        });
        Vector3? destination = new(6, tuning.CapsuleHalfHeight, 0);
        var waiting = new RangeSteering(Vector2.UnitX, RangeMoveStatus.WaitingForPath);
        using var capture = SupportQueryCapture.Begin("npc");
        // A creature host retains its destination while it waits for a path and calls Hold.
        for (int tick = 0; tick < 2; tick++)
        {
            Assert.NotNull(destination);
            Assert.NotEqual(body.Position, destination.Value);
            using (SupportQueryCapture.Begin("waiting")) body = NpcGroundMovement.Hold(body, 0.1f, tuning, context);
            Assert.Equal(0, body.Position.X);
        }
        // Passing the same waiting state to Step still counts a Step, even with no travel.
        body = NpcGroundMovement.Step(body, waiting, false, 0.1f, tuning, context);
        Assert.Equal(0, body.Position.X);
        var following = new RangeSteering(Vector2.UnitX, RangeMoveStatus.Following);
        body = NpcGroundMovement.Step(body, following, false, 0.1f, tuning, context);
        body = NpcGroundMovement.Step(body, following, false, 0.1f, tuning, context);
        Assert.True(body.Position.X > 0);
        _ = context.Step(body, Vector2.Zero, false, 0.1f, tuning);
        capture.Dispose();
        return capture.Snapshot();
    }
}
