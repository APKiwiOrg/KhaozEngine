using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.ContactNavigationBaselineTests;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

public class ParallelContactBakeTests
{
    public static TheoryData<string> Fixtures => new(Hashes.Keys);

    static Dictionary<string, string> Hashes => ContactNavigationBaselineTests.Baselines
        .ToDictionary(row => (string)row[0], row => (string)row[1]);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryDegreeMatchesTheSerialBaseline(string fixture)
    {
        BaselineBake serial = BuildFixture(fixture);
        foreach (int degree in new[] { 1, 2, 3, 8 })
            AssertMatchesBaseline(fixture, serial, BuildFixture(fixture, new NavBakeExecution(degree)));
    }

    [Fact]
    public void DegreeAboveTheRowCountMatchesTheBaseline()
    {
        const string fixture = "multi-layer-row-bands";
        BaselineBake serial = BuildFixture(fixture);
        Assert.True(serial.Bake.GetProfile("player").Space.Layers[0].Height < 16);
        AssertMatchesBaseline(fixture, serial, BuildFixture(fixture, new NavBakeExecution(16)));
    }

    static void AssertMatchesBaseline(string fixture, BaselineBake serial, BaselineBake parallel)
    {
        Assert.Equal(serial.Bake.ProfileNames, parallel.Bake.ProfileNames);
        foreach (string name in serial.Bake.ProfileNames)
            SameGraphBits(serial.Bake.GetProfile(name), parallel.Bake.GetProfile(name));
        AssertFixture(fixture, parallel);
        Assert.Equal(Hashes[fixture], Convert.ToHexStringLower(SHA256.HashData(NormalizeFile(parallel))));
    }

    [Fact]
    public void DiagnosticBuildAtDegreeEightMergesWorkerRecordsAndRestoresCallerState()
    {
        using var scene = new SmallStairScene();
        var callerPhases = new PhaseRecorder();
        var workerRecorders = new PhaseRecorder[8];
        Action begin = () => { }, end = () => { };
        Action<SupportFindObservation> find = _ => { };
        SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(
            SupportQueryDiagnostics.ThreadState.Fresh(true) with { Begin = begin, End = end, Find = find });
        Action<ContactAllocationPhase, bool>? priorObserver = ContactAllocationDiagnostics.Exchange(callerPhases.Observe);
        WorkerDiagnostics.SetObserverFactory(worker => (workerRecorders[worker] = new PhaseRecorder()).Observe);
        try
        {
            GroundNavigation serial = scene.Build(new(1) { UseStableContactHoldReuse = true });
            SupportQueryCounts serialCounts = SupportQueryDiagnostics.Snapshot();
            SupportRouteCounts serialRoutes = SupportQueryDiagnostics.Routes;
            Assert.All(workerRecorders, recorder => Assert.Null(recorder));
            SupportQueryDiagnostics.Reset();
            int callerEvents = callerPhases.Events;

            GroundNavigation parallel = scene.Build(new NavBakeExecution(8));
            SupportQueryCounts parallelCounts = SupportQueryDiagnostics.Snapshot();
            SameGraphBits(serial, parallel);

            int workers = serial.Space.Layers[0].Height;
            Assert.Equal(workers, workerRecorders.Count(recorder => recorder is not null));
            // Every proof counts the same. Each extra worker adds one first pin. The workers bind the world forms
            // the serial build published, since no static changed between the builds.
            Assert.True(serialCounts.WorldFormBuilds > 0);
            Assert.Equal(0, parallelCounts.WorldFormBuilds);
            Assert.Equal(serialCounts with { PhysicsLeases = 0, MovementLeases = 0, ProducerPins = 0, WorldFormBuilds = 0 },
                parallelCounts with { PhysicsLeases = 0, MovementLeases = 0, ProducerPins = 0 });
            Assert.Equal(serialCounts.MovementLeases + workers - 1, parallelCounts.MovementLeases);
            Assert.Equal(serialCounts.ProducerPins + workers - 1, parallelCounts.ProducerPins);
            Assert.True(parallelCounts.PhysicsLeases >= serialCounts.PhysicsLeases + workers - 1);
            Assert.Equal(serialRoutes, SupportQueryDiagnostics.Routes);
            Assert.True(serialCounts.Find > 0);

            int caller = Environment.CurrentManagedThreadId;
            Assert.True(callerPhases.Events > callerEvents);
            Assert.Equal([caller], callerPhases.Threads);
            callerPhases.AssertBalanced();
            // Workers claim bands as they free up, so one worker may prove nothing in a phase.
            Assert.True(workerRecorders.Take(workers).Sum(recorder => recorder.Events) > 0);
            foreach (PhaseRecorder recorder in workerRecorders.Take(workers))
            {
                Assert.DoesNotContain(caller, recorder.Threads);
                recorder.AssertBalanced();
            }

            SupportQueryDiagnostics.ThreadState after = SupportQueryDiagnostics.Exchange(
                SupportQueryDiagnostics.ThreadState.Fresh(true));
            SupportQueryDiagnostics.Exchange(after);
            Assert.True(after.Enabled);
            Assert.Same(begin, after.Begin);
            Assert.Same(end, after.End);
            Assert.Same(find, after.Find);
            Action<ContactAllocationPhase, bool>? observer = ContactAllocationDiagnostics.Exchange(null);
            Assert.Equal(callerPhases.Observe, observer);
        }
        finally
        {
            WorkerDiagnostics.SetObserverFactory(null);
            ContactAllocationDiagnostics.Exchange(priorObserver);
            SupportQueryDiagnostics.Exchange(prior);
        }
    }

    [Fact]
    public void WorkerDiagnosticsScopeRestoresEveryPriorThreadValue()
    {
        long[] counts = new long[24];
        Action begin = () => { }, end = () => { };
        Action<SupportFindObservation> find = _ => { };
        Action<ContactAllocationPhase, bool> observer = (_, _) => { };
        var installed = new SupportQueryDiagnostics.ThreadState(false, counts, begin, end,
            new(new(1, 2, 3), 4, 5, true), find, new(1, 2, 3, 4, 5, 6, 7, 8, 9, 10));
        SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(installed);
        Action<ContactAllocationPhase, bool>? priorObserver = ContactAllocationDiagnostics.Exchange(observer);
        try
        {
            var scope = WorkerDiagnosticsScope.Enter(new WorkerDiagnosticsPlan(true, null), null);
            Assert.True(SupportQueryDiagnostics.Enabled);
            Assert.Null(ContactAllocationDiagnostics.Exchange(null));
            SupportQueryDiagnostics.Record(SupportQueryCounter.Find, 3);
            WorkerDiagnosticsRecord record = scope.Exit();
            Assert.Equal(3, record.Counts.Find);
            Assert.Equal(default, record.Routes);

            SupportQueryDiagnostics.ThreadState restored = SupportQueryDiagnostics.Exchange(installed);
            Assert.Equal(installed, restored);
            Assert.All(counts, count => Assert.Equal(0, count));
            Assert.Same(observer, ContactAllocationDiagnostics.Exchange(observer));
        }
        finally
        {
            ContactAllocationDiagnostics.Exchange(priorObserver);
            SupportQueryDiagnostics.Exchange(prior);
        }
    }

    [Fact]
    public void WaterGenerationChangeBetweenWorkerFirstPinsRefusesTheBuild()
    {
        var water = new MutableWaterField(MovementWaterField.Level("world", -3.5f, -4));
        using var scene = new SmallStairScene(water: water);
        GroundNavigation serial = scene.Build(null);
        var pinned = new List<int>();
        using (ContactNavigationBuildHooks.Install(new()
        {
            WorkerPinned = worker =>
            {
                pinned.Add(worker);
                if (worker == 0) water.Advance();
            },
        }))
        {
            var refused = Assert.Throws<InvalidOperationException>(() => scene.Build(new NavBakeExecution(2)));
            Assert.Contains("snapshot changed", refused.Message);
        }
        // The setup failure came after worker 0 was fully constructed.
        Assert.Equal([0], pinned);
        scene.AssertUsable();
        SameGraphBits(serial, scene.Build(new NavBakeExecution(2)));
    }

    [Theory]
    [InlineData((int)ContactNavigationPhase.Admission)]
    [InlineData((int)ContactNavigationPhase.Edges)]
    [InlineData((int)ContactNavigationPhase.Links)]
    public void WaterGenerationChangeAfterABandCompletesRefusesTheBuild(int changedAfter)
    {
        var phase = (ContactNavigationPhase)changedAfter;
        var water = new MutableWaterField(MovementWaterField.Level("world", -3.5f, -4));
        using var scene = new SmallStairScene(water: water);
        int completed = 0;
        using (ContactNavigationBuildHooks.Install(new()
        {
            BandCompleted = (completedPhase, _) =>
            {
                if (completedPhase == phase && Interlocked.Increment(ref completed) == 1) water.Advance();
            },
        }))
        {
            var refused = Assert.Throws<InvalidOperationException>(() => scene.Build(new NavBakeExecution(2)));
            Assert.True(RefusalTexts.Any(text => refused.Message.Contains(text, StringComparison.Ordinal)), refused.ToString());
        }
        Assert.True(Volatile.Read(ref completed) >= 1);
        scene.AssertUsable();
    }

    // The snapshot refusal, a worker's lease refusal when its next acquisition sees the change, or the environment pin
    // of a lease already held across the change, which checks WaterField.Generation through the environment tuple on
    // every step. A query that meets the stale pin reports it as Stale availability, so the pin refusal also surfaces
    // as a query that returned Stale. Which form wins depends on which worker meets the change first.
    static readonly string[] RefusalTexts =
    [
        "The contact navigation build snapshot changed.",
        "Movement lease acquisition returned",
        "The movement lease differs from the capture or proof basis.",
        "The ordinary environment pin is stale.",
        "query returned Stale.",
    ];

    [Fact]
    public void WaterGenerationChangeAfterTheLastBandIsCaughtBeforePublishing()
    {
        var water = new MutableWaterField(MovementWaterField.Level("world", -3.5f, -4));
        using var scene = new SmallStairScene(water: water);
        int links = scene.Build(null).Space.Links.Count;
        Assert.True(links >= 2);
        int bands = ContactNavigationWorkers.BandCount(2, links), completed = 0;
        using (ContactNavigationBuildHooks.Install(new()
        {
            BandCompleted = (phase, _) =>
            {
                if (phase == ContactNavigationPhase.Links && Interlocked.Increment(ref completed) == bands) water.Advance();
            },
        }))
        {
            var refused = Assert.Throws<InvalidOperationException>(() => scene.Build(new NavBakeExecution(2)));
            Assert.Contains("snapshot changed", refused.Message);
        }
        scene.AssertUsable();
    }

    [Fact]
    public void WorkerMergeAddsEveryCounter()
    {
        ConstructorInfo constructor = typeof(SupportQueryCounts).GetConstructors().Single();
        Assert.Equal(Enum.GetNames<SupportQueryCounter>(), constructor.GetParameters().Select(parameter => parameter.Name));
        var counts = (SupportQueryCounts)constructor.Invoke(
            [.. Enumerable.Range(1, constructor.GetParameters().Length).Select(value => (object)(long)value)]);
        var routes = new SupportRouteCounts(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(
            SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            WorkerDiagnostics.Merge([new WorkerDiagnosticsRecord(counts, routes)]);
            Assert.Equal(counts, SupportQueryDiagnostics.Snapshot());
            Assert.Equal(routes, SupportQueryDiagnostics.Routes);
        }
        finally
        {
            SupportQueryDiagnostics.Exchange(prior);
        }
    }

    sealed class PhaseRecorder
    {
        readonly Dictionary<int, int> _depth = [];
        readonly HashSet<int> _threads = [];
        readonly object _gate = new();
        int _events;

        internal int Events { get { lock (_gate) return _events; } }
        internal int[] Threads { get { lock (_gate) return [.. _threads]; } }

        internal void Observe(ContactAllocationPhase phase, bool entering)
        {
            lock (_gate)
            {
                int thread = Environment.CurrentManagedThreadId;
                _threads.Add(thread);
                _events++;
                int depth = _depth.GetValueOrDefault(thread) + (entering ? 1 : -1);
                Assert.True(depth >= 0, $"{phase} closed without opening.");
                _depth[thread] = depth;
            }
        }

        internal void AssertBalanced()
        {
            lock (_gate) Assert.All(_depth.Values, depth => Assert.Equal(0, depth));
        }
    }
}
