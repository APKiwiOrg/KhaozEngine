using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

// #1454 through the borrowed interval. The split-statics A/B scene is JoinedEdgeContinuityTests' plane, read with
// this suite's 0.125 m foot, so the axis sits 0.1 m past the x 0 edge.
public partial class ContactLeasedQueryTests
{
    static readonly float JoinedGate = MathF.Cos(Tuning.MaxSlopeRadians);
    const float JoinedFoot = 0.125f;

    static JoinedEdgeContinuityTests.Expected JoinedOracle(FootSupportScene geometry, Vector2 axis) =>
        JoinedEdgeContinuityTests.Oracle(geometry, "split-statics", axis, JoinedFoot, JoinedGate);

    static SupportMemberKey JoinedKey(in SupportSample sample) => new(sample.Static!.Value, sample.FeatureId);

    [Fact]
    public void JoinedEdgeLeasedSeatAndLookAheadKeepThePair()
    {
        using FootSupportScene geometry = JoinedEdgeContinuityTests.Partition("split-statics");
        using var scene = new QueryScene(geometry);
        var queries = scene.Queries();
        Vector2 from = new(0.1f, -0.125f), to = new(0.1f, -0.025f);
        JoinedEdgeContinuityTests.Expected origin = JoinedOracle(geometry, from), expected = JoinedOracle(geometry, to);
        float feetY = (float)origin.Height;
        SupportSample start = queries.FindSupport(Query(new(from.X, feetY, from.Y)), out var binding);
        Assert.Equal(new MovementSupportKey("world", "bank"), binding!.Value.Owner);
        Assert.Equal(new SupportMemberKey(origin.Static, origin.ElementId), JoinedKey(start));

        GroundSeatResult seat = GroundSeat.Resolve(queries, start, from, feetY, to, Vector2.UnitY, JoinedFoot, Tuning,
            JoinedGate);
        Assert.Equal(SeatOutcome.Seated, seat.Outcome);
        Assert.Equal(SupportStatus.Walkable, seat.Support.Status);
        Assert.Equal(new SupportMemberKey(expected.Static, expected.ElementId), JoinedKey(seat.Support));
        Assert.NotEqual(JoinedKey(start), JoinedKey(seat.Support));
        Assert.True(Math.Abs((double)seat.FeetY - expected.Height) <= seat.Support.HeightError);
        Assert.True(start.Joined.Contains(JoinedKey(seat.Support)) || seat.Support.Joined.Contains(JoinedKey(start)));
        Assert.Same(scene.View.FootprintLease, seat.Support.Source.Result.Lease);
        Assert.Equal(0f, seat.StepPart);
        Assert.Equal(0f, seat.DiscreteStep);
        MovementContactSupportBinding owner = queries.BindSupport(new(to.X, seat.FeetY, to.Y), seat.Support,
            JoinedFoot, Tuning.MaxSlopeRadians);
        Assert.Equal(new MovementSupportKey("world", "bank"), owner.Owner);

        // Look-ahead from A at z -0.3 reaches B at its first sample, 0.25 m on, and rises 0.175 there, within the
        // walkable grade. The pair is directly joined, so the window holds no rise and no run starts.
        Vector2 axis = new(0.1f, -0.3f);
        float y = (float)JoinedOracle(geometry, axis).Height;
        SupportSample axisSupport = queries.FindSupport(Query(new(axis.X, y, axis.Y)));
        Assert.Equal(geometry["a"], axisSupport.Static);
        int before = scene.View.FootprintCalls;
        (float ClimbRate, float Ewma, float StepDeltaY) signals;
        using (GroundSignals.AlwaysSample())
            signals = GroundSignals.Resolve(new MoveState(), 0.02f, 0.07f, true, false, 0, new(0, 0.1f),
                new(axis.X, y, axis.Y), axisSupport, true, 1f / 30, Tuning, JoinedFoot, JoinedGate, queries);
        Assert.Equal(before + GroundSignals.Samples, scene.View.FootprintCalls);
        Assert.Equal((0f, 0f, 0.02f), signals);
        // Each look-ahead read captures its own winning row at its own exact query.
        Vector2 first = axis + Tuning.CapsuleRadius * Vector2.UnitY;
        float reach = Tuning.StepHeight + 2 * Tuning.CapsuleRadius;
        SupportSample sample = queries.FindSupport(new FootSupportQuery(first, y, JoinedFoot, reach, reach, JoinedGate));
        Assert.Equal(geometry["b"], sample.Static);
        Assert.Equal(first, sample.Source.Query.Axis);
        Assert.Equal(JoinedKey(sample), sample.Joined.Selected);
        Assert.True(SupportContinuity.IsContinuous(axisSupport, axis, sample, first, JoinedGate));
        Assert.Equal(0f, GroundSeat.DiscreteStep(axisSupport, axis, sample, first, JoinedGate));
        Assert.Equal(1, scene.View.Acquisitions);
    }

    // A clearance push earns a fresh Find at the pushed axis, which carries its own winner's row and query.
    [Fact]
    public void JoinedEdgePushedAxisCapturesItsOwnRow()
    {
        using var geometry = PushedAxisGeometry();
        using var scene = new QueryScene(geometry);
        var queries = scene.Queries();
        var feet = new Vector3(0.7f, 0, 0);
        SupportSample start = queries.FindSupport(Query(feet));
        var seat = new GroundSeatResult(SeatOutcome.Seated, 0, start, 0, Vector3.Zero);
        GroundPlacement placed = GroundPlacement.Try(queries, start, 0, new(feet.X, feet.Z), Vector2.Zero,
            seat, JoinedFoot, Tuning, JoinedGate, 1);
        Assert.True(placed.Valid && placed.Pushed);
        Assert.Equal(geometry["pushed"], placed.Support.Static);
        Assert.Equal(new Vector2(feet.X, feet.Z), start.Source.Query.Axis);
        Assert.Equal(new Vector2(placed.Feet.X, placed.Feet.Z), placed.Support.Source.Query.Axis);
        Assert.Equal(JoinedKey(placed.Support), placed.Support.Joined.Selected);
        Assert.Equal(JoinedKey(start), start.Joined.Selected);
        Assert.Same(start.Source.Result.Lease, placed.Support.Source.Result.Lease);
        Assert.Equal(1, scene.View.Acquisitions);
    }

    // A selected view that excludes B never publishes it, so no endpoint can list it and the target falls to A.
    [Fact]
    public void JoinedEdgeExcludedMemberIsNeverJoined()
    {
        using FootSupportScene geometry = JoinedEdgeContinuityTests.Partition("split-statics");
        using IPhysicsWorldQueryView view = geometry.World.CreateQueryViewExcludingStatics([geometry["b"]]);
        Vector2 from = new(0.1f, -0.125f), to = new(0.1f, -0.025f);
        float feetY = (float)JoinedOracle(geometry, from).Height;
        var b = new SupportMemberKey(geometry["b"], 0);
        using IPhysicsQueryLease lease = geometry.World.AcquireQueryReadLease();
        FootSupportQuery query = Query(new(from.X, feetY, from.Y));
        SupportSample start = FootSupport.Find(null, view, lease, query);
        SupportSample target = FootSupport.Find(null, view, lease, query with { Axis = to });
        Assert.Equal(geometry["a"], start.Static);
        Assert.Equal(geometry["a"], target.Static);
        Assert.Same(view, target.Source.Result.QueryWorld);
        Assert.False(start.Joined.Contains(b));
        Assert.False(target.Joined.Contains(b));
        SupportSample whole = FootSupport.Find(null, geometry.World, lease, query with { Axis = to });
        Assert.Equal(b, JoinedKey(whole));
    }

    // An undersized scope stays Unresolved before the producer or backend sees the target, whatever evidence the start
    // carries.
    [Fact]
    public void JoinedEdgeUndersizedScopeRefusesBeforeTheBackend()
    {
        using FootSupportScene geometry = JoinedEdgeContinuityTests.Partition("split-statics");
        // The scope holds the transition's start feet at the origin and the start body, but not the target body.
        using var scene = new QueryScene(geometry, scopeMin: new(-2, -2, -0.3f), scopeMax: new(2));
        var queries = scene.Queries();
        Vector2 from = new(0.1f, -0.025f), to = new(0.1f, -0.125f);
        float feetY = (float)JoinedOracle(geometry, from).Height;
        SupportSample start = queries.FindSupport(Query(new(from.X, feetY, from.Y)));
        Assert.Equal(SupportStatus.Walkable, start.Status);
        Assert.Equal(1, scene.View.FootprintCalls);
        ContactQueryFailure failure = Assert.Throws<ContactQueryFailure>(() => GroundSeat.Resolve(queries, start, from,
            feetY, to, Vector2.UnitY, JoinedFoot, Tuning, JoinedGate));
        Assert.Equal(MovementAvailability.Unresolved, failure.Availability);
        failure = Assert.Throws<ContactQueryFailure>(() => queries.FindSupport(Query(new(to.X, feetY, to.Y))));
        Assert.Equal(MovementAvailability.Unresolved, failure.Availability);
        Assert.Equal(1, scene.View.FootprintCalls);
        Assert.Equal(1, scene.Environment.AnalyticGroundCalls);
        Assert.Equal(1, scene.Environment.ContactSupportCalls);
    }

    /// <summary>The copied evidence is a value. A warm borrowed support read and its copies allocate nothing.</summary>
    [Collection("AllocSensitive")]
    public sealed class JoinedEdgeAllocation(ITestOutputHelper output)
    {
        [Fact]
        public void JoinedEdgeEvidenceCopiesAllocateNothing()
        {
            output.WriteLine($"SupportSample {Unsafe.SizeOf<SupportSample>()} bytes, joined members " +
                $"{Unsafe.SizeOf<SupportJoinedMembers>()}, key storage {Unsafe.SizeOf<SupportJoinedMemberKeys>()}, " +
                $"source {Unsafe.SizeOf<SupportContinuitySource>()}, seat {Unsafe.SizeOf<GroundSeatResult>()}, " +
                $"placement {Unsafe.SizeOf<GroundPlacement>()}, ground step {Unsafe.SizeOf<GroundStepResult>()}");
            Assert.Equal(2048, Unsafe.SizeOf<SupportJoinedMemberKeys>());
            // The memo's fixed entries are provisioned once per memo, never per Find.
            long memoBytes = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(new SupportRequestMemo());
            memoBytes = GC.GetAllocatedBytesForCurrentThread() - memoBytes;
            output.WriteLine($"One SupportRequestMemo provisions {memoBytes} bytes for {SupportRequestMemo.Capacity} entries");
            using FootSupportScene geometry = JoinedEdgeContinuityTests.Partition("split-statics");
            using var scene = new QueryScene(geometry);
            var queries = scene.Queries();
            Vector2 from = new(0.1f, -0.125f), to = new(0.1f, -0.025f);
            float feetY = (float)JoinedOracle(geometry, from).Height;
            FootSupportQuery start = Query(new(from.X, feetY, from.Y)), target = start with { Axis = to };
            // Warm every path once, including GroundSeat's static initialization, which boxes its static sample.
            SupportSample a = queries.FindSupport(start), b = queries.FindSupport(target);
            Assert.True(SupportContinuity.IsContinuous(a, from, b, to, JoinedGate));
            Assert.Equal(0f, GroundSeat.DiscreteStep(a, from, b, to, JoinedGate));
            long bytes;
            bool continuous;
            using (SupportAllocationRegion.Start())
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                a = queries.FindSupport(start);
                b = queries.FindSupport(target);
                SupportSample copy = b;
                continuous = SupportContinuity.IsContinuous(a, from, copy, to, JoinedGate) &&
                    GroundSeat.DiscreteStep(a, from, copy, to, JoinedGate) == 0;
                bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            }
            Assert.True(continuous);
            Assert.Equal(0, bytes);
        }
    }
}
