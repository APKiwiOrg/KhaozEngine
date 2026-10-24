using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class PublicSupportQueryMeasurementTests
{
    [Fact]
    public void NonFriendConsumerCapturesMovementAndCombinesBodyProfiles()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var colliders = TileWorldColliders.Build(TileWorldPhysicsTestData.FlatWorld(), TileWorldPhysicsTestData.Catalogs(),
            "public-capture");
        using var installed = colliders.AddTo(world);
        var registration = installed.CreateMovementRegistration("public-capture");
        var tuning = MoveTuning.Default with
        {
            Controller = MovementControllerKind.Contact,
            Water = new(WaterTraversalMode.DryOnly),
            TractionHysteresisRadians = 0,
        };
        using var outer = SupportQueryCapture.Begin("outer");
        var player = CaptureBody("player", tuning, nested: true);
        var cow = CaptureBody("cow", tuning with { CapsuleRadius = 0.3f }, nested: false);
        var combined = SupportQueryMeasurement.Combine([player, cow]);
        Assert.Equal(new[] { "cow", "player", "player/preflight" }, combined.ByLabel.Keys);
        foreach (var row in combined.ByLabel.Values)
        {
            Assert.True(row[SupportQueryCounter.Find] > 0);
            Assert.True(row[SupportQueryCounter.MovementLeases] > 0);
            Assert.True(row[SupportQueryCounter.PhysicsLeases] > 0);
            Assert.True(row.ProofReuse.HasCompleteLifetimes);
        }
        foreach (var counter in Enum.GetValues<SupportQueryCounter>())
        {
            Assert.Equal(combined.ByLabel.Values.Sum(row => row[counter]), combined.Total[counter]);
            Assert.Equal(player.Total[counter] + cow.Total[counter], combined.Total[counter]);
            Assert.Equal(0, outer.Snapshot().ByLabel["outer"][counter]);
        }
        var reversed = SupportQueryMeasurement.Combine([cow, player]);
        Assert.Equal(combined.ByLabel.Keys, reversed.ByLabel.Keys);
        foreach (var counter in Enum.GetValues<SupportQueryCounter>())
            Assert.Equal(combined.Total[counter], reversed.Total[counter]);
        var earlier = outer.Snapshot();
        outer.Dispose();
        Assert.Equal(earlier.Total[SupportQueryCounter.Find], outer.Snapshot().Total[SupportQueryCounter.Find]);

        SupportQueryMeasurement CaptureBody(string label, MoveTuning body, bool nested)
        {
            using var capture = SupportQueryCapture.Begin(label);
            if (nested)
                using (SupportQueryCapture.Begin(label + "/preflight")) Move(body, Vector2.Zero);
            Move(body, Vector2.Zero); // Hold uses the same real controller and recorder as a moving step.
            var earlier = capture.Snapshot();
            long finds = earlier.Total[SupportQueryCounter.Find];
            Move(body, Vector2.UnitX);
            capture.Dispose();
            var result = capture.Snapshot();
            Assert.True(result.Total[SupportQueryCounter.Find] > finds);
            Assert.Equal(finds, earlier.Total[SupportQueryCounter.Find]);
            return result;
        }

        void Move(MoveTuning body, Vector2 direction)
        {
            using var binding = EffectiveMovementBinding.Ordinary(body, world, registration,
                colliders.MovementGroundHeight, colliders.MovementGroundNormal,
                MovementBoundary.Rectangle(0, -64, 64, 0), colliders.Medium);
            var frame = new MovementFrameDescriptor(default, Vector3.Zero, 1);
            Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var acquired));
            using var queries = Assert.IsType<MovementQueryLease>(acquired);
            var state = new FramedMovementState(new MoveState
            {
                Position = new(2.5f, body.CapsuleHalfHeight, -2.5f),
                Grounded = true,
            }, frame, null);
            var result = binding.StepTowards(state, direction, false, 1f / 30, queries);
            Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            Assert.True(result.State.State.Grounded);
            if (direction != Vector2.Zero) Assert.True(result.State.State.Position.X > state.State.Position.X);
        }
    }
}
