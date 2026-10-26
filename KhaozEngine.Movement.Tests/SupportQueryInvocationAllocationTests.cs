using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

[Collection("AllocSensitive")]
public class SupportQueryInvocationAllocationTests
{
    [Theory]
    [InlineData(MovementControllerKind.Legacy)]
    [InlineData(MovementControllerKind.Contact)]
    public void DisabledNpcInstrumentationAddsNoAllocationOrCounts(MovementControllerKind controller)
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
        var steering = new RangeSteering(Vector2.UnitX, RangeMoveStatus.Following);
        var counts = Enumerable.Repeat(17L, 48).ToArray();
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(false) with { Counts = counts });
        try
        {
            Core();
            EntryPoints();
            long coreBytes = AllocationMeasurement.Measure("Uninstrumented ground movement", Core);
            long entryBytes = AllocationMeasurement.Measure("Disabled NPC invocation counters", EntryPoints);
            Assert.Equal(coreBytes, entryBytes);
            if (controller == MovementControllerKind.Legacy) Assert.Equal(0, entryBytes);
            Assert.All(counts, count => Assert.Equal(17, count));
            Assert.False(SupportQueryDiagnostics.Enabled);
            Assert.Same(counts, SupportQueryCaptureTests.State().Counts);
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }

        void Core()
        {
            _ = context.Step(body, Vector2.Zero, false, 0.1f, tuning);
            _ = context.Step(body, Vector2.UnitX, false, 0.1f, tuning);
        }
        void EntryPoints()
        {
            _ = NpcGroundMovement.Hold(body, 0.1f, tuning, context);
            _ = NpcGroundMovement.Step(body, steering, false, 0.1f, tuning, context);
        }
    }

    [Fact]
    public void DisabledRecorderDoesNotAllocateStorageForAnyInvocationCounter()
    {
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(false));
        try
        {
            RecordAll();
            long bytes = AllocationMeasurement.Measure("Disabled invocation recorder without storage", RecordAll);
            Assert.Equal(0, bytes);
            Assert.Null(SupportQueryCaptureTests.State().Counts);
            using var capture = SupportQueryCapture.Begin("later");
            for (int i = 42; i < 48; i++) Assert.Equal(0, capture.Snapshot().Total[(SupportQueryCounter)i]);
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }

        static void RecordAll()
        {
            for (int i = 42; i < 48; i++) SupportQueryDiagnostics.Record((SupportQueryCounter)i);
        }
    }
}
