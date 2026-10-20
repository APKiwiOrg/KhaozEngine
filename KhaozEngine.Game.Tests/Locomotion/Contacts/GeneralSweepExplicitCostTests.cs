using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Locomotion.Contacts;

[Collection("AllocSensitive")]
public sealed partial class GeneralSweepExplicitCostTests(ITestOutputHelper output) : IDisposable
{
    readonly SupportQueryDiagnostics.ThreadState prior =
        SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));

    static SupportCostRow Row(string name) => SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0])
        .Single(r => r.Name == name) with
    { Explicit = true, MustPass = true };

    [Theory]
    [InlineData("stairs box")]
    [InlineData("stairs hull")]
    [InlineData("stairs mesh")]
    [InlineData("hidden crate")]
    public void AcquiredExplicitCostRowsAdvanceBeforeAllocationVerdict(string name)
    {
        var row = Row(name);
        using var run = new SupportCostExecution(row, "ExplicitTick");
        var capture = new TickCapture(run);
        SupportCostOutput result = capture.Invoke();
        Assert.NotNull(run.LastMovementLease);
        Assert.Equal(MovementAvailability.Known, run.LastAcquisitionStatus);
        AssertAccepted(run, row, capture, result);
        if (row.StairSignal)
        {
            SupportFindObservation[] signals = capture.Reads.Where(r => r.ReachUp > 1).ToArray();
            output.WriteLine($"{name}. Consumed look-ahead {signals.Length}.");
            Assert.Empty(signals);
        }
    }

    [Theory]
    [InlineData("stairs box")]
    [InlineData("stairs hull")]
    [InlineData("stairs mesh")]
    [InlineData("hidden crate")]
    public void FirstLiftKeepsBothExactDimensionPairs(string name)
    {
        using var run = new SupportCostExecution(Row(name), "ExplicitTick");
        using var restricted = run.World.CreateQueryViewExcludingStatics([]);
        Vector3 centre = name == "hidden crate" ? new(0, 1.1010001f, 0) : new(0.525f, 1.6010002f, 0);
        Vector3 delta = new(0, 0.4f, 0);
        using var lease = run.World.AcquireQueryReadLease();
        foreach (IPhysicsCapsuleGeometryQueries receiver in new IPhysicsCapsuleGeometryQueries[]
            { run.World, (IPhysicsCapsuleGeometryQueries)restricted })
        {
            foreach (CapsuleQueryDimensions dimensions in new CapsuleQueryDimensions[]
                { new(0.4f, 0.59999996f), new(0.40000007f, 0.6000001f) })
            {
                var shape = new CapsuleShape(dimensions.Radius, dimensions.Length);
                AssertClear(receiver.SweepCapsuleCertified(shape, Pose.At(centre), delta));
                AssertClear(receiver.SweepCapsuleCertified(dimensions, Pose.At(centre), delta));
                if (dimensions.Radius != 0.4f) continue;
                Span<CapsuleContact> contacts = new CapsuleContact[32];
                AssertEmpty(receiver.QueryCapsuleContacts(shape, Pose.At(centre), 0, contacts));
                AssertEmpty(receiver.QueryCapsuleContacts(dimensions, Pose.At(centre), 0, contacts));
            }
        }
        output.WriteLine($"{name}. Centre {centre}, delta {delta}, both exact pairs Clear, original contacts 0/0.");
    }

    static void AssertClear(CapsuleSweepResult result)
    {
        Assert.Equal(CapsuleSweepStatus.Clear, result.Status);
        Assert.Equal(0.4f, result.ClearThroughDistance);
        Assert.Null(result.ImpactDistance);
        Assert.InRange(result.CertifiedErrorMetres, 0, 0.001f);
    }

    static void AssertEmpty(CapsuleContactResult result)
    {
        Assert.True(result.Complete);
        Assert.Equal(0, result.Written);
        Assert.Equal(0, result.RequiredCapacity);
    }

    [Theory]
    [InlineData("stairs box")]
    [InlineData("stairs hull")]
    [InlineData("stairs mesh")]
    [InlineData("hidden crate")]
    public void BorrowedExplicitCostRowsMatchAcquiredWholeState(string name)
    {
        var row = Row(name);
        using var acquired = new SupportCostExecution(row, "ExplicitTick");
        var acquiredCapture = new TickCapture(acquired);
        SupportCostOutput expected = acquiredCapture.Invoke();
        AssertAccepted(acquired, row, acquiredCapture, expected);
        using var borrowed = new SupportCostExecution(row, "ExplicitTickBorrowed");
        var borrowedCapture = new TickCapture(borrowed);
        SupportCostOutput actual = borrowedCapture.Invoke();
        AssertAccepted(borrowed, row, borrowedCapture, actual);
        Assert.NotSame(acquired.LastMovementLease, borrowed.LastMovementLease);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.StepOutcome, actual.StepOutcome);
        Assert.Equal(expected.Motion, actual.Motion);
        Assert.Equal(expected.EndFeet, actual.EndFeet);
        Assert.Equal(acquiredCapture.Bindings, borrowedCapture.Bindings);
        Assert.Equal(acquiredCapture.Reads, borrowedCapture.Reads);
        Assert.Equal(acquiredCapture.Queries, borrowedCapture.Queries);
        // The borrowed route's separate acquisition precedes this captured tick.
        Assert.Equal(acquiredCapture.Counts with { PhysicsLeases = 0, MovementLeases = 0, ProducerPins = 0 },
            borrowedCapture.Counts);
    }

    void AssertAccepted(SupportCostExecution run, SupportCostRow row, TickCapture capture, SupportCostOutput result)
    {
        bool readsMatch = capture.Reads.Count > 0;
        foreach (var read in capture.Reads)
        {
            run.CheckFind(read, out bool matches);
            readsMatch &= matches;
        }
        result = run.CheckGeometry(result, readsMatch);
        var query = row.Query with { Axis = new(result.EndFeet.X, result.EndFeet.Z), FeetY = result.EndFeet.Y };
        var final = SupportGateCostScenes.Expected(run.Scene, row, query, guarded: false);
        double budget = (double)SupportGateCostScenes.Tuning.MaxStepClimbSpeed * row.Dt;
        bool paced = SupportCostExecution.Paced(row, final.Height);
        double? roof = SupportCostRoofOracle.Limit(run.Scene, query.Axis, query.FootRadius, final.Height,
            SupportGateCostScenes.Tuning);
        bool placed = final.Status == SupportStatus.Walkable && result.EndFeet.Y <= final.Height + 0.001 &&
            final.Height - result.EndFeet.Y <= (paced ? SupportGateCostScenes.Tuning.StepHeight : 0.001) &&
            (!paced || SupportCostExecution.PacedEndReaches(result.EndFeet.Y, final.Height, row.Feet.Y, budget, roof));
        output.WriteLine($"{row.Name}. {result.StepOutcome}, motion {result.Motion}, substeps {capture.Counts.GroundSubsteps}, " +
            $"Find {capture.Counts.Find}, reads {capture.Reads.Count}, reads match {readsMatch}, placed {placed}, " +
            $"support {final.Static}/{final.ElementId}, height {final.Height:R}, roof {roof}.");
        Assert.Equal(MovementStepOutcome.Advanced, result.StepOutcome);
        Assert.False(result.Refused);
        Assert.True(result.GeometryPassed);
        Assert.True(result.Motion.X > 0);
        Assert.True(capture.Counts.GroundSubsteps > 0);
        Assert.True(readsMatch);
        Assert.True(placed);
        Assert.True(result.Motion.Y <= SupportGateCostScenes.Tuning.MaxStepClimbSpeed * row.Dt);
        Assert.NotEmpty(capture.Bindings);
        Assert.All(capture.Bindings, binding =>
        {
            Assert.NotNull(binding.Static);
            Assert.True(binding.FeatureId >= 0);
        });
    }

    sealed class TickCapture
    {
        readonly SupportCostExecution run;
        internal readonly List<SupportFindObservation> Reads = [];
        internal readonly List<MovementContactSupportRequest> Bindings = [];
        internal SupportQueryCounts Counts;
        internal QueryCounts Queries;

        internal TickCapture(SupportCostExecution run)
        {
            this.run = run;
            var bind = run.Producer.OnContactSupport!;
            run.Producer.OnContactSupport = (in MovementContactSupportRequest request,
                out MovementContactSupportBinding binding) =>
            {
                Bindings.Add(request);
                return bind(request, out binding);
            };
        }

        internal SupportCostOutput Invoke(Func<SupportCostOutput>? operation = null)
        {
            Reads.Clear();
            Bindings.Clear();
            run.View.Reset();
            SupportQueryDiagnostics.Reset();
            SupportQueryDiagnostics.SetFindObserver(Reads.Add);
            try
            {
                var result = (operation ?? run.Invoke)();
                Counts = SupportQueryDiagnostics.Snapshot();
                Queries = run.View.Counts;
                return result;
            }
            finally { SupportQueryDiagnostics.SetFindObserver(null); }
        }
    }

    public void Dispose() => SupportQueryDiagnostics.Exchange(prior);
}
