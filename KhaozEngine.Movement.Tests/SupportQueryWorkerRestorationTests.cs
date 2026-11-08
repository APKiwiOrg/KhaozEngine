using System;
using System.Threading;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.PublicSupportQueryBakeTests;

namespace KhaozEngine.Tests.Movement;

public class SupportQueryWorkerRestorationTests
{
    [Fact]
    public void FailedWorkerBuildMergesPartialWorkAndRestoresAllThreads()
    {
        using var scene = new ContactNavigationReuseFixture();
        Bake(scene, 1);
        var failure = new InvalidOperationException("selected worker band failure");
        var cleanup = new InvalidOperationException("worker cleanup failure");
        var oracle = new BakeOracle("player")
        {
            AfterBand = (phase, band) =>
            {
                if (phase == ContactNavigationPhase.Edges && band == 0) throw failure;
            },
        };
        var installed = SupportQueryCaptureTests.State() with
        {
            Enabled = false,
            Counts = null,
            MeasurementLabel = "prior",
            Begin = () => throw new InvalidOperationException("caller begin callback escaped"),
            End = () => throw new InvalidOperationException("caller end callback escaped"),
            Find = _ => throw new InvalidOperationException("caller Find callback escaped"),
        };
        var prior = SupportQueryDiagnostics.Exchange(installed);
        int caller = Environment.CurrentManagedThreadId, callerEvents = 0, workerEvents = 0;
        Action<ContactAllocationPhase, bool> callerObserver = (_, _) =>
        {
            Assert.Equal(caller, Environment.CurrentManagedThreadId);
            callerEvents++;
        };
        var priorObserver = ContactAllocationDiagnostics.Exchange(callerObserver);
        WorkerDiagnostics.SetObserverFactory(_ => (_, _) => Interlocked.Increment(ref workerEvents));
        var hooks = oracle.Hooks;
        try
        {
            using (ContactNavigationBuildHooks.Install(new()
            {
                WorkerPinned = hooks.WorkerPinned,
                BandCompleted = hooks.BandCompleted,
                Releasing = worker => { if (worker == 0) throw cleanup; },
            }))
            {
                using var capture = SupportQueryCapture.Begin("player");
                Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => Bake(scene, 3, oracle)));
                var aggregate = Assert.IsType<AggregateException>(failure.Data[ContactNavigationWorkers.CleanupFailuresKey]);
                Assert.Contains(cleanup, aggregate.InnerExceptions);
                oracle.AssertMatches(capture.Snapshot().Total, 3);
                Assert.True(capture.Snapshot().Total[SupportQueryCounter.Find] > 0);
                capture.Dispose();
                Assert.Equal(installed, SupportQueryCaptureTests.State());
            }
            Assert.True(callerEvents > 0);
            Assert.True(workerEvents > 0);
            Assert.Same(callerObserver, ContactAllocationDiagnostics.Exchange(callerObserver));
            using var clean = SupportQueryCapture.Begin("later");
            Assert.Equal(0, clean.Snapshot().Total[SupportQueryCounter.Find]);
            var later = new BakeOracle("later");
            using (ContactNavigationBuildHooks.Install(later.Hooks)) Bake(scene, 3, later);
            later.AssertMatches(clean.Snapshot().Total, 3);
            Assert.Equal(new[] { "later" }, clean.Snapshot().ByLabel.Keys);
        }
        finally
        {
            WorkerDiagnostics.SetObserverFactory(null);
            ContactAllocationDiagnostics.Exchange(priorObserver);
            SupportQueryDiagnostics.Exchange(prior);
        }
    }

    [Fact]
    public void DisabledBakeDoesNotInstallPublicCaptureOrChangeBytes()
    {
        using var scene = new ContactNavigationReuseFixture();
        byte[] captured;
        using (var capture = SupportQueryCapture.Begin("player")) captured = Bake(scene, 3);
        var installed = SupportQueryDiagnostics.ThreadState.Fresh(false) with
        {
            Retention = new(0, 0, long.MaxValue, long.MaxValue),
        };
        var prior = SupportQueryDiagnostics.Exchange(installed);
        int events = 0;
        var observer = ContactAllocationDiagnostics.Exchange((_, _) =>
        {
            Assert.False(SupportQueryDiagnostics.Enabled);
            Assert.Null(SupportQueryDiagnostics.MeasurementLabel);
            Assert.Null(SupportQueryCaptureTests.State().Counts);
            Interlocked.Increment(ref events);
        });
        WorkerDiagnostics.SetObserverFactory(_ => (_, _) =>
        {
            Assert.False(SupportQueryDiagnostics.Enabled);
            Assert.Null(SupportQueryDiagnostics.MeasurementLabel);
            Assert.Null(SupportQueryCaptureTests.State().Counts);
            Interlocked.Increment(ref events);
        });
        try
        {
            Assert.Equal(captured, Bake(scene, 1));
            Assert.Equal(captured, Bake(scene, 3));
            Assert.Equal(installed, SupportQueryCaptureTests.State());
            Assert.True(events > 0);
            var scope = WorkerDiagnosticsScope.Enter(WorkerDiagnostics.Plan(), null);
            scope.Restore();
            Assert.Equal(installed, SupportQueryCaptureTests.State());
        }
        finally
        {
            WorkerDiagnostics.SetObserverFactory(null);
            ContactAllocationDiagnostics.Exchange(observer);
            SupportQueryDiagnostics.Exchange(prior);
        }
    }

    [Fact]
    public void WorkerPlanIsAnImmutableLabelAndRestoresItsOwnThread()
    {
        using var capture = SupportQueryCapture.Begin("player");
        WorkerDiagnosticsPlan plan = WorkerDiagnostics.Plan();
        using var child = SupportQueryCapture.Begin("preflight");
        WorkerDiagnosticsRecord record = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            error = Record.Exception(() =>
            {
                var counts = new long[Enum.GetValues<SupportQueryCounter>().Length];
                counts[0] = 17;
                var saved = SupportQueryDiagnostics.ThreadState.Fresh(true) with
                {
                    Counts = counts,
                    MeasurementLabel = "worker prior",
                    Begin = () => { },
                    End = () => { },
                    Find = _ => { },
                    CurvedWork = _ => { },
                    Ground = new(new(1, 2, 3), 4, 5, true),
                    Routes = new(1, 2, 3, 4, 5, 6, 7, 8, 9, 10),
                };
                SupportQueryDiagnostics.Exchange(saved);
                Action<ContactAllocationPhase, bool> callback = (_, _) => { };
                ContactAllocationDiagnostics.Exchange(callback);
                var scope = WorkerDiagnosticsScope.Enter(plan, null);
                try
                {
                    Assert.Equal("player", SupportQueryDiagnostics.MeasurementLabel);
                    Assert.Null(SupportQueryCaptureTests.State().Find);
                    SupportQueryDiagnostics.Record(SupportQueryCounter.Find, 3);
                    for (int i = 42; i < 48; i++) SupportQueryDiagnostics.Record((SupportQueryCounter)i);
                    throw new InvalidOperationException("worker failure");
                }
                catch (InvalidOperationException) { }
                finally { record = scope.Exit(); }
                Assert.Equal(saved, SupportQueryCaptureTests.State());
                Assert.Same(counts, SupportQueryCaptureTests.State().Counts);
                Assert.Equal(17, counts[0]);
                Assert.Same(callback, ContactAllocationDiagnostics.Exchange(null));
            });
        });
        thread.Start();
        thread.Join();
        Assert.Null(error);
        Assert.Equal("player", record.MeasurementLabel);
        Assert.Equal(3, record.Counts.Find);
        child.Dispose();
        WorkerDiagnostics.Merge([record]);
        Assert.Equal(3, capture.Snapshot().ByLabel["player"][SupportQueryCounter.Find]);
        for (int i = 42; i < 48; i++) Assert.Equal(1, capture.Snapshot().ByLabel["player"][(SupportQueryCounter)i]);
        Assert.Equal(0, capture.Snapshot().ByLabel["preflight"][SupportQueryCounter.Find]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerScopesPreservePhysicalRetentionAndRestoreCallbacks(bool enabled)
    {
        using var fixture = new ContactNavigationReuseFixture();
        using var proof = fixture.Proof();
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(false));
        Action<ContactAllocationPhase, bool> observer = (_, _) => { };
        var oldObserver = ContactAllocationDiagnostics.Exchange(observer);
        try
        {
            var source = proof.BeginSource(ContactNavigationReuseFixture.Source());
            Assert.True(proof.TryEdge(fixture.From, fixture.To));
            var live = SupportQueryDiagnostics.Retention;
            Assert.True(live.Keys > 0);
            var saved = SupportQueryCaptureTests.State() with
            {
                MeasurementLabel = "prior",
                Begin = () => { },
                End = () => { },
                Find = _ => { },
                CurvedWork = _ => { },
                Routes = new(1, 2, 3, 4, 5, 6, 7, 8, 9, 10),
            };
            SupportQueryDiagnostics.Exchange(saved);
            var scope = WorkerDiagnosticsScope.Enter(new(enabled, null, MeasurementLabel: "player"), null);
            Assert.Equal("player", SupportQueryDiagnostics.MeasurementLabel);
            Assert.False(SupportQueryDiagnostics.HasCompleteLifetimes);
            Assert.Null(SupportQueryCaptureTests.State().Begin);
            Assert.Null(SupportQueryCaptureTests.State().Find);
            source.Dispose();
            if (enabled)
            {
                WorkerDiagnosticsRecord record = scope.Exit();
                Assert.Equal("player", record.MeasurementLabel);
                Assert.False(record.HasCompleteLifetimes);
                using var capture = SupportQueryCapture.Begin("player");
                WorkerDiagnostics.Merge([record]);
                Assert.False(capture.Snapshot().Total.ProofReuse.HasCompleteLifetimes);
            }
            else scope.Restore();
            Assert.Equal(saved with { Retention = live with { Keys = 0, Bytes = 0 } }, SupportQueryCaptureTests.State());
            Assert.Same(observer, ContactAllocationDiagnostics.Exchange(observer));

            scope = WorkerDiagnosticsScope.Enter(new(enabled, null, MeasurementLabel: "player"), null);
            source = proof.BeginSource(ContactNavigationReuseFixture.Source(1));
            Assert.True(proof.TryEdge(fixture.From, fixture.To));
            live = SupportQueryDiagnostics.Retention;
            Assert.True(live.Keys > 0);
            try { throw new InvalidOperationException("measured work failed"); }
            catch (InvalidOperationException) { }
            finally { scope.Restore(); }
            Assert.Equal(live.Keys, SupportQueryDiagnostics.Retention.Keys);
            Assert.Equal(live.Bytes, SupportQueryDiagnostics.Retention.Bytes);
            source.Dispose();
            ContactNavigationReuseFixture.Released();
        }
        finally
        {
            ContactAllocationDiagnostics.Exchange(oldObserver);
            SupportQueryDiagnostics.Exchange(prior);
        }
    }
}
