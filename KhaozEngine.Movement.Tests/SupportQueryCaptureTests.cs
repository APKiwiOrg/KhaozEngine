using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class SupportQueryCaptureTests
{
    [Fact]
    public void EveryPublicCounterReadsTheExistingRecorderAsLong()
    {
        Type[] results = [typeof(SupportQueryMeasurement), typeof(SupportQueryMetrics),
            typeof(SupportQueryRouteMetrics), typeof(SupportProofReuseMetrics)];
        Assert.All(results, type => Assert.All(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            constructor => Assert.True(constructor.IsAssembly)));
        string[] names = ("Find Proposals Volumes SelectedStatics MeshLeaves Polygons Members Extrema " +
            "IntervalFallbacks ExactFallbacks JoinPairs JoinsProved JoinsPruned Unsupported Ambiguous CapacityExceeded " +
            "AnalyticSamples Bindings PhysicsLeases MovementLeases ProducerPins GroundSubsteps MediumRetries ModeSegments " +
            "WorldFormBuilds PolygonOperands PrefilterRejects TopologyJoins TangentSamples JoinsSkipped MeshTriangleCaptures " +
            "FindMemoHits AdmissionCertificates StairStratum RectangleContact SharedMemberFacts FootprintReuseHits " +
            "FootprintReuseMisses FootprintReuseStores FootprintReuseBudgetSkips EdgeSteps InitialHoldReuseHits " +
            "NpcLegacyHolds NpcContactHolds NpcLegacySteps NpcContactSteps PlayerLegacySteps PlayerContactSteps").Split(' ');
        Assert.Equal(names, Enum.GetNames<SupportQueryCounter>());
        using var capture = SupportQueryCapture.Begin("all");
        long[] counts = Enumerable.Range(0, 48).Select(i => (long)int.MaxValue + i + 1).ToArray();
        SupportQueryDiagnostics.Exchange(State() with { Counts = counts, Routes = new(11, 12, 13, 14, 15, 16, 17, 18, 19, 20) });
        var measured = capture.Snapshot();
        var routes = measured.Total.Routes;
        Assert.Equal(new long[] { 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 },
            new long[] { routes.WallRetries, routes.PushedPlacements, routes.MediumResolves, routes.MaximumMediumRetries,
                routes.GroundSegments, routes.SlideSegments, routes.AirSegments, routes.WaterSegments,
                routes.RoundedReads, routes.MediumHandoffs });
        for (int i = 0; i < names.Length; i++)
        {
            Assert.Equal(i, (int)Enum.Parse<SupportQueryCounter>(names[i]));
            Assert.Equal(counts[i], measured.Total[(SupportQueryCounter)i]);
        }
        counts[0] = int.MaxValue - 1L;
        SupportQueryDiagnostics.Record(SupportQueryCounter.Find, 3);
        Assert.Equal((long)int.MaxValue + 2, capture.Snapshot().Total[SupportQueryCounter.Find]);
        Assert.Equal((long)int.MaxValue + 1, measured.Total[SupportQueryCounter.Find]);
    }

    [Fact]
    public void NestedLabelsCountEachOperationOnce()
    {
        using var parent = SupportQueryCapture.Begin("player");
        SupportQueryDiagnostics.Record(SupportQueryCounter.Find, 2);
        var earlier = parent.Snapshot();
        using (SupportQueryCapture.Begin("player/preflight")) SupportQueryDiagnostics.Record(SupportQueryCounter.Find, 3);
        using (SupportQueryCapture.Begin("cow")) SupportQueryDiagnostics.Record(SupportQueryCounter.Find, 5);
        using (SupportQueryCapture.Begin("cow")) SupportQueryDiagnostics.Record(SupportQueryCounter.Find, 7);
        var measured = parent.Snapshot();
        Assert.Equal(17, measured.Total[SupportQueryCounter.Find]);
        Assert.Equal(2, measured.ByLabel["player"][SupportQueryCounter.Find]);
        Assert.Equal(3, measured.ByLabel["player/preflight"][SupportQueryCounter.Find]);
        Assert.Equal(12, measured.ByLabel["cow"][SupportQueryCounter.Find]);
        Assert.Equal(measured.Total[SupportQueryCounter.Find], measured.ByLabel.Values.Sum(row => row[SupportQueryCounter.Find]));
        Assert.Equal(2, earlier.Total[SupportQueryCounter.Find]);
        parent.Dispose();
        Assert.Equal(17, parent.Snapshot().Total[SupportQueryCounter.Find]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureRestoresEveryPriorValueWhenMeasuredWorkThrows(bool enabled)
    {
        long[] counts = Enumerable.Range(1, 48).Select(i => (long)i).ToArray();
        long[] original = (long[])counts.Clone();
        Action begin = () => { }, end = () => { };
        Action<SupportFindObservation> find = _ => { };
        Action<SupportCurvedWorkObservation> curved = _ => { };
        var seeded = new SupportQueryDiagnostics.ThreadState(enabled, counts, begin, end,
            new(new Vector3(1, 2, 3), 4, 5, true), find, new(1, 2, 3, 4, 5, 6, 7, 8, 9, 10), curved,
            new(0, 0, 9, 99), "prior");
        var prior = SupportQueryDiagnostics.Exchange(seeded);
        SupportQueryCapture? capture = null;
        try
        {
            Assert.Throws<MeasuredFailure>((Action)(() =>
            {
                using (capture = SupportQueryCapture.Begin("throwing"))
                {
                    var active = State();
                    Assert.Null(active.Begin);
                    Assert.Null(active.End);
                    Assert.Null(active.Find);
                    Assert.Null(active.CurvedWork);
                    SupportQueryDiagnostics.Record(SupportQueryCounter.Find, 11);
                    SupportQueryDiagnostics.Route(SupportQueryRoute.Ground);
                    SupportQueryDiagnostics.GroundState(Vector3.Zero, 0, 0, false);
                    throw new MeasuredFailure();
                }
            }));
            var restored = State();
            Assert.Equal(seeded, restored);
            Assert.Same(counts, restored.Counts);
            Assert.Equal(original, counts);
            Assert.Same(begin, restored.Begin);
            Assert.Same(end, restored.End);
            Assert.Same(find, restored.Find);
            Assert.Same(curved, restored.CurvedWork);
            Assert.Equal("prior", restored.MeasurementLabel);
            Assert.Equal(11, capture!.Snapshot().Total[SupportQueryCounter.Find]);
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Fact]
    public void ScopeMisuseDoesNotCorruptTheActiveRecorder()
    {
        Assert.Throws<ArgumentNullException>(() => SupportQueryCapture.Begin(null!));
        Assert.Throws<ArgumentException>(() => SupportQueryCapture.Begin(""));
        Assert.Throws<ArgumentException>(() => SupportQueryCapture.Begin(" \t"));
        using var parent = SupportQueryCapture.Begin(" parent ");
        using var child = SupportQueryCapture.Begin("child");
        SupportQueryDiagnostics.Record(SupportQueryCounter.Find, 3);
        var before = State();
        Assert.Throws<InvalidOperationException>(() => parent.Snapshot());
        Assert.Throws<InvalidOperationException>(() => parent.Dispose());
        Exception? snapshotError = null, disposeError = null;
        var thread = new Thread(() =>
        {
            snapshotError = Record.Exception(() => child.Snapshot());
            disposeError = Record.Exception(() => child.Dispose());
        });
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(snapshotError);
        Assert.IsType<InvalidOperationException>(disposeError);
        Assert.Equal(before, State());
        child.Dispose();
        child.Dispose();
        Assert.Equal(3, parent.Snapshot().Total[SupportQueryCounter.Find]);
        Assert.Contains(" parent ", parent.Snapshot().ByLabel.Keys);
    }

    [Fact]
    public void DisabledThreadsAndPriorObserversStayIsolated()
    {
        int observed = 0;
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            SupportQueryDiagnostics.SetGroundSubstepObserver(() => observed++, () => observed++);
            SupportQueryDiagnostics.SetFindObserver(_ => observed++);
            SupportQueryDiagnostics.SetCurvedWorkObserver(_ => observed++);
            using (SupportQueryCapture.Begin("active"))
            {
                SupportQueryDiagnostics.BeginGroundSubstep();
                SupportQueryDiagnostics.EndGroundSubstep();
                SupportQueryDiagnostics.ObserveFind(default);
                SupportQueryDiagnostics.ObserveCurvedWork(default);
                Exception? failure = null;
                var thread = new Thread(() => failure = Record.Exception(() =>
                {
                    Assert.False(SupportQueryDiagnostics.Enabled);
                    Assert.Null(State().Counts);
                    SupportQueryDiagnostics.Record(SupportQueryCounter.Find);
                    Assert.Equal(default, SupportQueryDiagnostics.Snapshot());
                    Assert.Null(State().Counts);
                }));
                thread.Start();
                thread.Join();
                Assert.Null(failure);
                Assert.Equal(0, observed);
            }
            SupportQueryDiagnostics.BeginGroundSubstep();
            SupportQueryDiagnostics.EndGroundSubstep();
            SupportQueryDiagnostics.ObserveFind(default);
            SupportQueryDiagnostics.ObserveCurvedWork(default);
            Assert.Equal(4, observed);
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    internal static SupportQueryDiagnostics.ThreadState State()
    {
        var state = SupportQueryDiagnostics.Exchange(default);
        SupportQueryDiagnostics.Exchange(state);
        return state;
    }

    sealed class MeasuredFailure : Exception;
}
