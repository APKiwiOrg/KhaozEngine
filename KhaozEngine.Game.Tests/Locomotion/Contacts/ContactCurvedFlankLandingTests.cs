using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>A body walking off a tall lying log must fall to its floor, without seating on the steep flank or
/// penetrating the shell. These are real controller ticks, including their read lease and dry support memo.</summary>
public sealed class ContactCurvedFlankLandingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void RejectedSlideSeatHandsUnspentTimeToAir(bool capsule, bool aboutX, bool floorSeat)
    {
        MoveTuning tuning = ContactFollowupTraversalTests.Tuning;
        GroundCoreSettings settings = ContactFollowupTraversalTests.Settings;
        const float dt = ContactFollowupTraversalTests.Dt;
        var (scene, _, direction) = ContactFollowupScenes.Log(capsule, 0.3f, aboutX);
        using (scene)
        using (IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease())
        {
            SupportRequestMemo? memo = SupportRequestMemo.OpenDry(lease);
            Assert.NotNull(memo);
            SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(
                SupportQueryDiagnostics.ThreadState.Fresh(true));
            SupportQueryDiagnostics.SetFindObserver(find => output.WriteLine($"Find {find}"));
            try
            {
                ContactQueryContext queries = new DryContactQueryContext((_, _) => -1000, null, scene.World,
                    lease, tuning, null, memo);
                // Tick 29's last clear feet. The smaller move selects the steep flank, the larger one the floor.
                Vector2 axis = 0.46814263f * direction;
                var feet = new Vector3(axis.X, 0.0359118f, axis.Y);
                Vector2 move = (floorSeat ? 0.014f : 0.002f) * direction;
                Vector3 recovered = ShellMotion.RecoverWithQueries(queries, feet, tuning, out bool cleared);
                Assert.True(cleared);
                Assert.Equal(feet, recovered);
                float cosine = ContactFollowupTraversalTests.CosMaxSlope;
                float radius = ContactFollowupTraversalTests.FootRadius;
                SupportSample support = queries.FindSupport(new(axis, feet.Y, radius, tuning.StepHeight,
                    tuning.StepHeight, cosine));
                Assert.Equal(SupportStatus.Steep, support.Status);
                GroundSeatResult seat = GroundSeat.Resolve(queries, support, axis, feet.Y, axis + move, direction,
                    radius, tuning, cosine);
                Assert.Equal(floorSeat ? SeatOutcome.Seated : SeatOutcome.SteepSeated, seat.Outcome);
                var target = new Vector3(axis.X + move.X, seat.FeetY, axis.Y + move.Y);
                Assert.True(queries.PenetrationShell(ShellGeometry.Shape(tuning), ShellGeometry.Centre(target, tuning),
                    out Vector3 mtv));
                GroundPlacement placement = GroundPlacement.Try(queries, support, feet.Y, axis, move, seat, radius,
                    tuning, cosine, double.PositiveInfinity, clearSteep: true);
                output.WriteLine($"Rejected seat {seat}, target {target}, MTV {mtv}, placement {placement}");
                Assert.False(placement.Valid);

                AirStepResult flight = AirPass.Step(feet, 0.82707417f * direction, -1.322552f, dt, tuning.Gravity,
                    tuning, settings, tuning.MaxSlopeRadians, queries);
                output.WriteLine($"Air from the same clear feet {flight}");
                Assert.Equal(AirOutcome.Airborne, flight.Outcome);
                Assert.True(flight.Feet.Y < feet.Y);

                GroundStepResult slide = GroundCore.Slide(feet, move, dt, tuning, settings, queries,
                    tuning.MaxSlopeRadians);
                output.WriteLine($"Slide {slide}");
                Assert.Equal(GroundFooting.None, slide.Footing);
                Assert.Equal(feet, slide.Feet);
                Assert.Equal(dt, slide.RemainingTime);
            }
            finally
            {
                SupportQueryDiagnostics.Exchange(prior);
                memo!.Close();
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WalkOffTallLogLandsOnFloor(bool capsule, bool aboutX)
    {
        MoveTuning tuning = ContactFollowupTraversalTests.Tuning;
        GroundCoreSettings settings = ContactFollowupTraversalTests.Settings;
        const float dt = ContactFollowupTraversalTests.Dt;
        var (scene, obstacle, direction) = ContactFollowupScenes.Log(capsule, 0.3f, aboutX);
        using (scene)
        {
            var state = new MoveState { Position = new(0, 0.6f + tuning.CapsuleHalfHeight, 0), Grounded = true };
            Vector2 target = ContactFollowupTraversalTests.Approach * direction;
            bool fell = false, landed = false;
            ContactController.ContactTickContinuation last = default;
            for (int tick = 1; tick <= ContactFollowupTraversalTests.MaxTicks; tick++)
            {
                Vector3 feet = state.Position - tuning.CapsuleHalfHeight * Vector3.UnitY;
                Vector2 move = ContactFollowupTraversalTests.Command(feet, target);
                float distance = move.Length();
                using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
                SupportRequestMemo? memo = SupportRequestMemo.OpenDry(lease);
                Assert.NotNull(memo);
                try
                {
                    var queries = new DryContactQueryContext((_, _) => -1000, null, scene.World, lease, tuning, null, memo);
                    state = ContactController.StepWithQueries(state, distance > 0 ? move / distance : Vector2.Zero,
                        distance / (tuning.WalkSpeed * dt), false, false, dt, tuning, queries, out last,
                        settings: settings);
                    Assert.True(last.Completed, $"Tick {tick} refused the contact tick at {feet}.");
                    feet = state.Position - tuning.CapsuleHalfHeight * Vector3.UnitY;
                    output.WriteLine($"tick {tick}: {feet}, grounded {state.Grounded}, velocity " +
                        $"{state.HorizontalVelocity} {state.VerticalVelocity:R}, support {last.Support.Status} " +
                        $"{last.Support.Static?.Value}/{last.Support.FeatureId} {last.Support.Height:R}");
                    Assert.False(queries.PenetrationShell(ShellGeometry.Shape(tuning),
                        ShellGeometry.Centre(feet, tuning), out Vector3 mtv),
                        $"Tick {tick} penetrated the floor or log at {feet}, MTV {mtv}.");
                    Assert.True(feet.Y >= -0.001f, $"Tick {tick} passed below the floor at {feet}.");
                    fell |= !state.Grounded;
                    if (state.Grounded)
                    {
                        Assert.Equal(SupportStatus.Walkable, last.Support.Status);
                        Assert.Same(lease, last.Support.Source.Result.Lease);
                        if (fell)
                        {
                            landed = true;
                            Assert.Equal(scene["floor"], last.Support.Static);
                            Assert.NotEqual(obstacle, last.Support.Static);
                            Assert.InRange(MathF.Abs(feet.Y), 0, 0.001f);
                        }
                    }
                    if (landed && Vector3.Distance(feet, new(target.X, 0, target.Y)) <= 0.001f) break;
                }
                finally { memo!.Close(); }
            }

            Vector3 finalFeet = state.Position - tuning.CapsuleHalfHeight * Vector3.UnitY;
            Assert.True(fell, "The body never left the walkable flank.");
            Assert.True(landed, $"The body never landed on the floor, ending at {finalFeet}.");
            Assert.True(state.Grounded);
            Assert.Equal(scene["floor"], last.Support.Static);
            Assert.InRange(Vector3.Distance(finalFeet, new(target.X, 0, target.Y)), 0, 0.001f);
        }
    }
}
