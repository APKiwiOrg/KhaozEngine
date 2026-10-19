using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public sealed partial class GeneralSweepExplicitCostTests
{
    [Theory]
    [MemberData(nameof(ContactFollowupTraversalTests.PlankRows), MemberType = typeof(ContactFollowupTraversalTests))]
    public void AcquiredPlankTraversalArrivesWithinOneMillimetre(float pitch, bool compound, int sign, bool stopOnProp)
    {
        var (scene, obstacle, direction) = ContactFollowupScenes.Plate(pitch, compound);
        using (scene)
        using (var inner = scene.World.CreateQueryViewExcludingStatics([]))
        {
            var view = new CountingQueryView(inner);
            var producer = DryEnvironment(view);
            bool sawObstacle = false;
            int obstacleFeature = compound ? ContactFollowupScenes.CompoundLeaf : 0;
            producer.OnContactSupport = (in MovementContactSupportRequest request, out MovementContactSupportBinding binding) =>
            {
                bool onObstacle = request.Static == obstacle && request.FeatureId >= obstacleFeature;
                sawObstacle |= onObstacle;
                binding = new(new("world", onObstacle ? "plate" : "floor"), Room, null, producer.Identity);
                return MovementAvailability.Known;
            };
            Vector2 along = sign * direction;
            Vector2 start = ContactFollowupScenes.PlateCentre - ContactFollowupTraversalTests.Approach * along;
            Vector2 target = stopOnProp ? ContactFollowupScenes.PlateCentre
                : ContactFollowupScenes.PlateCentre + ContactFollowupTraversalTests.Approach * along;
            var state = new MoveState
            {
                Position = new(start.X, 0.901f, start.Y),
                Grounded = true,
                HorizontalVelocity = along * ContactFollowupTraversalTests.Speed,
            };
            MovementSelection? selection = new(Room, null, producer.Identity);
            Vector3 feet = new(start.X, 0, start.Y);
            int ticks = 0;
            for (; ticks < ContactFollowupTraversalTests.MaxTicks; ticks++)
            {
                Vector2 move = ContactFollowupTraversalTests.Command(feet, target);
                var acquired = producer.Acquire();
                Assert.Equal(MovementAvailability.Known, acquired.Status);
                using var lease = Assert.IsType<MovementQueryLease>(acquired.Lease);
                var input = new FramedMovementState(state, lease.Frame, selection);
                MovementStepResult result = ExplicitCharacterMovement.StepTowards(input,
                    move / (SupportGateCostScenes.Tuning.WalkSpeed * ContactFollowupTraversalTests.Dt), false,
                    ContactFollowupTraversalTests.Dt, SupportGateCostScenes.Tuning, new(WaterTraversalMode.DryOnly), lease);
                Assert.True(result.Outcome == MovementStepOutcome.Advanced,
                    $"Tick {ticks + 1}, feet {feet}, move {move}, outcome {result.Outcome}.");
                Assert.True(result.State.State.Grounded);
                state = result.State.State;
                selection = result.State.Selection;
                feet = new(state.Position.X, state.Position.Y - 0.9f - 0.001f, state.Position.Z);
                if (Vector2.Distance(new(feet.X, feet.Z), target) <= 0.001f &&
                    state.ClimbRate == 0 && state.StepDeltaY == 0) break;
            }

            var query = new FootSupportQuery(target, feet.Y, ContactFollowupTraversalTests.FootRadius,
                SupportGateCostScenes.Tuning.StepHeight, SupportGateCostScenes.Tuning.StepHeight,
                ContactFollowupTraversalTests.CosMaxSlope);
            var expected = ContactFollowupScenes.PlateSupport(pitch, compound, query);
            Assert.Equal(SupportStatus.Walkable, expected.Status);
            Assert.True(sawObstacle);
            Assert.True(ticks < ContactFollowupTraversalTests.MaxTicks);
            Assert.True(Vector3.Distance(feet, new(target.X, (float)expected.Height, target.Y)) <= 0.001f);
            Assert.Equal(new MovementSupportKey("world", stopOnProp ? "plate" : "floor"), selection!.Value.Support);
            query = query with { Axis = new(feet.X, feet.Z) };
            expected = ContactFollowupScenes.PlateSupport(pitch, compound, query);
            var finalAcquisition = producer.Acquire();
            Assert.Equal(MovementAvailability.Known, finalAcquisition.Status);
            using var finalLease = Assert.IsType<MovementQueryLease>(finalAcquisition.Lease);
            var request = new MovementSupportRequest(new(state.Position, 0.4f, 0.9f, Room, selection.Value.Support),
                query.ReachUp, query.ReachDown, SupportGateCostScenes.Tuning.MaxSlopeRadians, new(Room, feet));
            Assert.Equal(MovementAvailability.Known,
                finalLease.FindContactSupport(request, query, out var support, out var binding));
            Assert.Equal(expected.Static, support.Static);
            Assert.Equal(expected.ElementId, support.FeatureId);
            Assert.True(Math.Abs(support.Height - expected.Height) <= support.HeightError);
            Assert.Equal(selection.Value.Support, binding!.Value.Owner);
            output.WriteLine($"Plank {pitch:R}, compound {compound}, sign {sign}, stop {stopOnProp}. " +
                $"Arrived {feet} after {ticks + 1} ticks, oracle {expected.Height:R}, " +
                $"raw support {support.Static}/{support.FeatureId}.");
        }
    }

    static EnvironmentAcquisitionFixture DryEnvironment(CountingQueryView view, bool undersized = false)
    {
        var identity = new MovementQueryIdentity("closure", 1, "scope");
        var frame = new MovementFrameDescriptor(WorldFrame.Origin, view.Origin, 1);
        var scope = new MovementQueryScope(new(-10), new(10), undersized ? 0.1f : 20,
            undersized ? 0.1f : 20, Room, identity, frame);
        var producer = new EnvironmentAcquisitionFixture(view, scope) { ContactSupportEnabled = true };
        producer.OnBodySample = (in MovementBodyQuery _) => new(MovementAvailability.Known, Room, null, false, 0, null);
        producer.OnCoverage = (in MovementMediumSweepQuery _, Span<MovementCoverageSpan> spans,
            Span<MovementDomainContact> contacts) =>
        {
            spans[0] = new(0, 1, 0, 0, true);
            return new(MovementAvailability.Known, 1, 1, 0, 0, 0, identity);
        };
        producer.OnContactSupport = (in MovementContactSupportRequest _, out MovementContactSupportBinding binding) =>
        {
            binding = new(new("world", "floor"), Room, null, identity);
            return MovementAvailability.Known;
        };
        return producer;
    }
}
