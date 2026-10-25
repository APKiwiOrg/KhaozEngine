using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

/// <summary>SC-15. An exact repeat support request inside one lease interval is served from that interval's memo,
/// bit-identical to the uncached evaluator, and nothing crosses an interval, a tick, a request bit or a view.</summary>
public sealed class ExactRepeatMemoTests(ITestOutputHelper output)
{
    static readonly MovementSpaceKey Room = new("world", "room");
    // A grounded tick reads support at its traction gate, the slope limit widened by the hysteresis band.
    static readonly float CosMaxSlope = MathF.Cos(CharacterMovement.TractionGate(true, SupportGateCostScenes.Tuning));

    readonly record struct TickResult(object Result, SupportQueryCounts Counts, SupportFindObservation[] Reads);

    // The pushed seat's first dry substep reads its support at the move's end, then reads it again after the push
    // places the feet on the seat edge. The two reads are bit-identical, so the second is served from the memo.
    [Fact]
    public void ExactRepeatInsideOneIntervalHitsTheMemo()
    {
        SupportCostRow seat = Row("pushed seat");
        using var execution = new SupportCostExecution(seat, "DryTick");
        TickResult memo = Tick(execution, seat, bypass: false);
        TickResult bypassed = Tick(execution, seat, bypass: true);
        foreach (SupportFindObservation read in memo.Reads) output.WriteLine(read.ToString());

        // The substep's axis is the start axis plus one tick of walking, rounded once.
        var repeat = new FootSupportQuery(new(0.10000001f, 0), 0, 0.2f, 0.4f, 0.4f, CosMaxSlope);
        SupportFindObservation[] repeated = memo.Reads.Where(read => Same(read, repeat)).ToArray();
        Assert.Equal(2, repeated.Length);
        Assert.Equal(repeated[0], repeated[1]);
        Assert.Equal(1, memo.Reads.Length - memo.Reads.Select(Key).Distinct().Count());
        Assert.Equal(1, memo.Counts.FindMemoHits);
        Assert.Equal(0, bypassed.Counts.FindMemoHits);

        // Every read is still a Find and observed, but the repeat reaches the backend once.
        Assert.Equal(bypassed.Counts.Find, memo.Counts.Find);
        Assert.Equal(bypassed.Reads, memo.Reads);
        Assert.Equal(bypassed.Counts.Volumes - 1, memo.Counts.Volumes);
        Assert.Equal(bypassed.Counts.Proposals - 2, memo.Counts.Proposals);
        AssertSameBits(bypassed.Result, memo.Result, seat.Name);
    }

    [Fact]
    public void ExactRepeatMemoNeverCrossesAnInterval()
    {
        SupportCostRow seat = Row("pushed seat");
        using SupportFootprintScene scene = SupportGateCostScenes.Build(seat);
        using IPhysicsWorldQueryView other = scene.World.CreateQueryViewExcludingStatics([]);
        var memo = new SupportRequestMemo();
        var query = new FootSupportQuery(new(0.10000001f, 0), 0, 0.2f, 0.4f, 0.4f, CosMaxSlope);
        // The look-ahead read the guard refuses, so a refusal is held for its interval only.
        var refused = new FootSupportQuery(new(1, 0), 0.018999912f, 0.2f, 2, 2, CosMaxSlope);
        var analytic = new FootAnalyticGround(-10, Vector3.UnitY);
        var scope = new SupportFootprintBounds(new(-30, -30, -30), new(30, 30, 30));
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            SupportSample first, refusal;
            using (IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease())
            {
                memo.Open(lease);
                first = Read(analytic, scene.World, lease, query, scope, memo, hit: false);
                Assert.Equal(first, Read(analytic, scene.World, lease, query, scope, memo, hit: true));
                refusal = Read(analytic, scene.World, lease, refused, scope, memo, hit: false);
                Assert.Equal(SupportStatus.Refused, refusal.Status);
                Assert.Equal(refusal, Read(analytic, scene.World, lease, refused, scope, memo, hit: true));

                // Every request bit is in the key, a signed zero and one ulp included.
                foreach (FootSupportQuery changed in Changed(query))
                    Read(analytic, scene.World, lease, changed, scope, memo, hit: false);
                Read(analytic with { Height = MathF.BitIncrement(-10) }, scene.World, lease, query, scope, memo, hit: false);
                Read(analytic with { Normal = Vector3.Normalize(new(0.001f, 1, 0)) }, scene.World, lease, query, scope, memo,
                    hit: false);
                Read(null, scene.World, lease, query, scope, memo, hit: false);
                Read(analytic, scene.World, lease, query, null, memo, hit: false);
                Read(analytic, scene.World, lease, query, scope with { Maximum = new(30, 30, MathF.BitIncrement(30)) }, memo,
                    hit: false);
                // The view is part of the identity, even over the same owner and lease.
                Read(analytic, other, lease, query, scope, memo, hit: false);
                Assert.Equal(15, memo.Count);
                Read(analytic, scene.World, lease, query with { FeetY = 0.05f }, scope, memo, hit: false);
                Assert.Equal(SupportRequestMemo.Capacity, memo.Count);
                // A full memo stops remembering and still answers every read uncached.
                Read(analytic, scene.World, lease, query with { FeetY = 0.25f }, scope, memo, hit: false);
                Read(analytic, scene.World, lease, query with { FeetY = 0.25f }, scope, memo, hit: false);

                // Closing ends the interval even while the same lease stays live, as a borrowed lease does across
                // ticks. A memo open for another lease never serves this one.
                memo.Close();
                Assert.Equal(0, memo.Count);
                Read(analytic, scene.World, lease, query, scope, memo, hit: false);
                Assert.Equal(0, memo.Count);
            }

            using (IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease())
            {
                memo.Open(lease);
                Assert.Equal(first, Read(analytic, scene.World, lease, query, scope, memo, hit: false));
                Assert.Equal(refusal, Read(analytic, scene.World, lease, refused, scope, memo, hit: false));
                Assert.Equal(first, Read(analytic, scene.World, lease, query, scope, memo, hit: true));
                memo.Close();
            }
            using (IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease())
            {
                memo.Open(lease);
                Assert.Throws<InvalidOperationException>(() => memo.Open(lease));
                memo.Close();
            }
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    // The reference evaluator bypasses every support cost optimization, the memo included. Under it a repeat is read
    // again and nothing is remembered, through a direct read and through a dry tick's own memo.
    [Fact]
    public void ReferenceEvaluationIsNeverServedFromTheMemo()
    {
        SupportCostRow seat = Row("pushed seat");
        using (var execution = new SupportCostExecution(seat, "DryTick"))
        {
            TickResult bypassed = Tick(execution, seat, bypass: true);
            TickResult reference;
            using (SupportReferenceEvaluation.Begin()) reference = Tick(execution, seat, bypass: false);
            Assert.Equal(0, reference.Counts.FindMemoHits);
            Assert.Equal(bypassed.Reads, reference.Reads);
            AssertSameBits(bypassed.Result, reference.Result, seat.Name);
            Assert.Equal(1, Tick(execution, seat, bypass: false).Counts.FindMemoHits);
        }

        using SupportFootprintScene scene = SupportGateCostScenes.Build(seat);
        var memo = new SupportRequestMemo();
        var query = new FootSupportQuery(new(0.10000001f, 0), 0, 0.2f, 0.4f, 0.4f, CosMaxSlope);
        var analytic = new FootAnalyticGround(-10, Vector3.UnitY);
        var scope = new SupportFootprintBounds(new(-30, -30, -30), new(30, 30, 30));
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
            memo.Open(lease);
            SupportSample first;
            using (SupportReferenceEvaluation.Begin())
            {
                first = Read(analytic, scene.World, lease, query, scope, memo, hit: false);
                Assert.Equal(first, Read(analytic, scene.World, lease, query, scope, memo, hit: false));
                Assert.Equal(0, memo.Count);
            }
            Assert.Equal(first, Read(analytic, scene.World, lease, query, scope, memo, hit: false));
            Assert.Equal(first, Read(analytic, scene.World, lease, query, scope, memo, hit: true));
            memo.Close();
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    // Consecutive dry ticks each own a lease, and consecutive explicit ticks on one borrowed interval each end their
    // memo with the tick. Each tick serves only its own repeats.
    [Fact]
    public void ExactRepeatMemoNeverCrossesATick()
    {
        SupportCostRow seat = Row("pushed seat");
        using (var dry = new SupportCostExecution(seat, "DryTick"))
        {
            TickResult first = Tick(dry, seat, bypass: false);
            TickResult second = Tick(dry, seat, bypass: false);
            Assert.Equal(1, first.Counts.FindMemoHits);
            Assert.Equal(1, second.Counts.FindMemoHits);
            Assert.Equal(first.Counts.Find, second.Counts.Find);
            AssertSameBits(first.Result, second.Result, "dry");
            using IPhysicsQueryLease lease = dry.View.AcquireQueryReadLease();
            SupportRequestMemo? memo = SupportRequestMemo.OpenDry(lease);
            Assert.NotNull(memo);
            Assert.Equal(0, memo.Count);
            // A nested tick has its own memo and evidence arena.
            SupportRequestMemo? nested = SupportRequestMemo.OpenDry(lease);
            Assert.NotNull(nested);
            Assert.NotSame(memo, nested);
            nested.Close();
            memo.Close();
        }

        SupportCostRow entry = Row("explicit entry");
        using var borrowed = new SupportCostExecution(entry, "ExplicitTickBorrowed");
        SupportQueryCounts[] counts = new SupportQueryCounts[2];
        for (int i = 0; i < counts.Length; i++)
        {
            var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
            try
            {
                Assert.Equal("Advanced", borrowed.Invoke().Outcome);
                counts[i] = SupportQueryDiagnostics.Snapshot();
            }
            finally { SupportQueryDiagnostics.Exchange(prior); }
            Assert.Null(borrowed.LastMovementLease!.Workspace.SupportMemo.Lease);
            Assert.Equal(0, borrowed.LastMovementLease!.Workspace.SupportMemo.Count);
        }
        Assert.Equal(1, counts[0].FindMemoHits);
        Assert.Equal(1, counts[1].FindMemoHits);
        Assert.Equal(counts[0].Find, counts[1].Find);
    }

    // Each worker thread's dry memo is its own, so a parallel bake never shares remembered reads.
    [Fact]
    public void DryMemoIsPerThread()
    {
        SupportCostRow seat = Row("pushed seat");
        using SupportFootprintScene scene = SupportGateCostScenes.Build(seat);
        using var opened = new System.Threading.ManualResetEventSlim();
        using var release = new System.Threading.ManualResetEventSlim();
        SupportRequestMemo? mine = null, theirs = null;
        var worker = BoundedThread.Start(() =>
        {
            using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
            theirs = SupportRequestMemo.OpenDry(lease);
            opened.Set();
            BoundedThread.Await(release);
            theirs!.Close();
        });
        try
        {
            BoundedThread.Await(opened);
            using IPhysicsQueryLease lease = scene.World.AcquireQueryReadLease();
            mine = SupportRequestMemo.OpenDry(lease);
            Assert.NotNull(mine);
            Assert.NotNull(theirs);
            Assert.NotSame(theirs, mine);
            Assert.NotSame(theirs!.Lease, mine.Lease);
            mine.Close();
        }
        finally
        {
            release.Set();
            worker.Join();
        }
    }

    // Every cost row through its own door, and every sparse row through both, with the memo and with the uncached
    // evaluator. The ticks are bit-identical, every read stays a Find, the producer's samples and bindings are
    // unchanged and each exact repeat is the memo's only hit.
    [Fact]
    public void MemoTicksMatchTheUncachedEvaluatorOnEveryCostRow()
    {
        int compared = 0;
        long hits = 0;
        foreach (SupportCostRow scene in SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]))
            foreach (bool explicitDoor in scene.Dense ? new[] { scene.Explicit } : new[] { false, true })
            {
                SupportCostRow row = scene with { Explicit = explicitDoor };
                string name = $"{row.Name} {(explicitDoor ? "explicit" : "dry")}";
                using var execution = new SupportCostExecution(row, explicitDoor ? "ExplicitTick" : "DryTick");
                TickResult cold = Tick(execution, row, bypass: false);
                TickResult bypassed = Tick(execution, row, bypass: true);
                TickResult warm = Tick(execution, row, bypass: false);
                foreach (TickResult memo in new[] { cold, warm })
                {
                    AssertSameBits(bypassed.Result, memo.Result, name);
                    Assert.Equal(bypassed.Reads, memo.Reads);
                    Assert.Equal(bypassed.Counts.Find, memo.Counts.Find);
                    Assert.Equal(bypassed.Counts.AnalyticSamples, memo.Counts.AnalyticSamples);
                    Assert.Equal(bypassed.Counts.Bindings, memo.Counts.Bindings);
                    int repeats = memo.Reads.Length - memo.Reads.Select(Key).Distinct().Count();
                    Assert.True(repeats == memo.Counts.FindMemoHits, $"{name}: {repeats} repeats, {memo.Counts.FindMemoHits} hits");
                    Assert.True(memo.Counts.Volumes <= bypassed.Counts.Volumes, name);
                }
                Assert.Equal(cold.Counts.FindMemoHits, warm.Counts.FindMemoHits);
                if (cold.Counts.FindMemoHits > 0) output.WriteLine($"{name}: {cold.Counts.FindMemoHits} of {cold.Counts.Find} reads served.");
                hits += cold.Counts.FindMemoHits;
                compared++;
            }
        output.WriteLine($"{compared} ticks compared, {hits} reads served from the memo.");
        Assert.True(hits > 0);
    }

    static SupportSample Read(FootAnalyticGround? analytic, IPhysicsWorld world, IPhysicsQueryLease lease,
        in FootSupportQuery query, SupportFootprintBounds? scope, SupportRequestMemo memo, bool hit)
    {
        long before = SupportQueryDiagnostics.Snapshot().FindMemoHits;
        SupportSample sample = FootSupport.Find(analytic, world, lease, query, scope, memo);
        Assert.Equal(hit ? 1 : 0, SupportQueryDiagnostics.Snapshot().FindMemoHits - before);
        SupportSample oracle = FootSupport.Find(analytic, world, lease, query, scope, null);
        AssertSameBits(oracle, sample, query.ToString());
        return sample;
    }

    static IEnumerable<FootSupportQuery> Changed(FootSupportQuery q)
    {
        yield return q with { Axis = new(MathF.BitIncrement(q.Axis.X), q.Axis.Y) };
        yield return q with { Axis = new(q.Axis.X, -0f) };
        yield return q with { FeetY = MathF.BitIncrement(q.FeetY) };
        yield return q with { FootRadius = MathF.BitIncrement(q.FootRadius) };
        yield return q with { ReachUp = MathF.BitIncrement(q.ReachUp) };
        yield return q with { ReachDown = MathF.BitIncrement(q.ReachDown) };
        yield return q with { CosMaxSlope = MathF.BitIncrement(q.CosMaxSlope) };
    }

    static TickResult Tick(SupportCostExecution execution, SupportCostRow row, bool bypass)
    {
        var reads = new List<SupportFindObservation>();
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        IDisposable? uncached = bypass ? SupportRequestMemo.Bypass() : null;
        try
        {
            SupportQueryDiagnostics.SetFindObserver(reads.Add);
            object result;
            if (row.Explicit)
            {
                (MovementAvailability status, IDisposable? acquired) = execution.Producer.Acquire();
                Assert.Equal(MovementAvailability.Known, status);
                using var lease = (MovementQueryLease)acquired!;
                var framed = new FramedMovementState(row.Input, lease.Frame, new(Room, null, lease.Identity));
                result = ExplicitCharacterMovement.StepTowards(framed, row.Direction, false, row.Dt,
                    SupportGateCostScenes.Tuning, new(row.WaterMode), lease);
            }
            else result = execution.Invoke().State!.Value;
            return new(result, SupportQueryDiagnostics.Snapshot(), [.. reads]);
        }
        finally
        {
            uncached?.Dispose();
            SupportQueryDiagnostics.Exchange(prior);
        }
    }

    static SupportCostRow Row(string name) =>
        SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0]).Single(r => r.Name == name);

    static SupportFindObservation Key(SupportFindObservation read) => read with { Status = 0, Height = 0, HeightError = 0 };

    static bool Same(SupportFindObservation read, in FootSupportQuery q) =>
        read.Axis == q.Axis && read.FeetY == q.FeetY && read.Radius == q.FootRadius && read.ReachUp == q.ReachUp &&
        read.ReachDown == q.ReachDown && read.CosMaxSlope == q.CosMaxSlope;

    static void AssertSameBits(object expected, object actual, string path)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        if (expected is float f)
        {
            Assert.True(BitConverter.SingleToInt32Bits(f) == BitConverter.SingleToInt32Bits((float)actual),
                $"{path}: expected {f:R}, actual {actual}");
            return;
        }
        if (expected is double d)
        {
            Assert.True(BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits((double)actual),
                $"{path}: expected {d:R}, actual {actual}");
            return;
        }
        Type type = expected.GetType();
        if (type.IsPrimitive || type.IsEnum || type == typeof(string))
        {
            Assert.True(expected.Equals(actual), $"{path}: {expected} against {actual}");
            return;
        }
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            // CF-4 interval evidence is not geometric state. JoinedEdgeContinuityTests compares it explicitly.
            if (field.FieldType == typeof(SupportContinuityHandle)) continue;
            object? left = field.GetValue(expected), right = field.GetValue(actual);
            if (left is null) Assert.Null(right);
            else AssertSameBits(left, right!, path + "." + field.Name);
        }
    }
}
