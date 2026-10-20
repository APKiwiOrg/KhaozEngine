using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Threading;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

public sealed class ContactNavigationProofReuseScheduleTests
{
    [Theory]
    [InlineData(ContactNavigationReuseMode.Off)]
    [InlineData(ContactNavigationReuseMode.Footprints)]
    [InlineData(ContactNavigationReuseMode.Combined)]
    public void ReuseFailureSelectionMatchesSerialAfterRealHitsUnderForcedBands(ContactNavigationReuseMode mode)
    {
        using var fixture = new ContactNavigationReuseFixture();
        using var laterThrown = new ManualResetEventSlim();
        var timeout = TimeSpan.FromSeconds(30);
        bool coordinate = false;
        Thread? laterThread = null;
        int waited = 0;
        fixture.OnHeight = (x, z) =>
        {
            if (x >= fixture.From.X - 0.01f) return;
            int row = z == fixture.From.Z ? 0 : z == fixture.From.Z + 0.25f ? 1 : -1;
            if (row < 0) return;
            if (coordinate)
            {
                if (row == 1)
                {
                    laterThread = Thread.CurrentThread;
                    laterThrown.Set();
                }
                else
                {
                    Assert.True(laterThrown.Wait(timeout));
                    Assert.NotSame(Thread.CurrentThread, laterThread);
                    Assert.True(laterThread!.Join(timeout));
                    Interlocked.Exchange(ref waited, 1);
                }
            }
            throw new OrderedProviderFailure(row);
        };
        using var source = fixture.Context.CreateNavigationBinding(Tuning);
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            using var reference = fixture.Proof(false);
            var expected = Assert.Throws<OrderedProviderFailure>(() => reference.TryEdge(fixture.From,
                fixture.From - Vector3.UnitX * 0.25f));
            Assert.Equal(0, expected.Row);
            Vector3 later = fixture.From + Vector3.UnitZ * 0.25f;
            Assert.Equal(1, Assert.Throws<OrderedProviderFailure>(() => reference.TryEdge(later,
                later - Vector3.UnitX * 0.25f)).Row);
            SupportQueryDiagnostics.Reset();
            var workers = ContactNavigationWorkers.Create(fixture.Bake, fixture.Context, source,
                new(fixture.Bake.Columns, fixture.Bake.Options, Tuning, default), 2, 2,
                new(mode != ContactNavigationReuseMode.Off, mode == ContactNavigationReuseMode.Combined, false));
            coordinate = true;
            Exception? primary = null;
            try
            {
                var cutoff = workers.Run(ContactNavigationPhase.Edges, 2, row => row, (proof, band) =>
                {
                    int row = band.Start;
                    Assert.True(band.Admits(row));
                    Vector3 from = fixture.From + Vector3.UnitZ * (row * 0.25f);
                    using (proof.BeginSource(new(new(6, 4 + row, 0, 0), 0)))
                    {
                        Assert.True(proof.TryEdge(from, from + Vector3.UnitX * 0.25f));
                        Assert.True(proof.TryEdge(from, from + Vector3.UnitX * 0.25f));
                        var counts = SupportQueryDiagnostics.Snapshot();
                        if (mode != ContactNavigationReuseMode.Off) Assert.True(counts.FootprintReuseHits > 0);
                        if (mode == ContactNavigationReuseMode.Combined) Assert.True(counts.InitialHoldReuseHits > 0);
                        proof.TryEdge(from, from - Vector3.UnitX * 0.25f);
                    }
                });
                var selected = Assert.Throws<OrderedProviderFailure>(cutoff.ThrowSelected);
                primary = selected;
                Assert.Equal(expected.Message, selected.Message);
                Assert.Equal(1, Volatile.Read(ref waited));
                ContactNavigationReuseFixture.Released();
            }
            finally { workers.Release(primary); }
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    sealed class OrderedProviderFailure(int row) : Exception($"source {row}")
    {
        internal int Row { get; } = row;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerPhaseThreadChangeCannotInheritAReuseSession(bool changeThread)
    {
        using var fixture = new ContactNavigationReuseFixture();
        using var source = fixture.Context.CreateNavigationBinding(Tuning);
        using var firstThread = new ProofThread();
        using var secondThread = changeThread ? new ProofThread() : null;
        ProofThread linksThread = secondThread ?? firstThread;
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            var workers = ContactNavigationWorkers.Create(fixture.Bake, fixture.Context, source,
                new(fixture.Bake.Columns, fixture.Bake.Options, Tuning, default), 1, 1, new(true, true, false));
            var sessions = new ConcurrentDictionary<object, Thread>();
            var threads = new ConcurrentQueue<Thread>();
            fixture.OnHeight = (_, _) =>
            {
                foreach (var active in fixture.ActiveSessions())
                    sessions.TryAdd(active.Session, Thread.CurrentThread);
            };
            Assert.Empty(fixture.ActiveSessions());
            ContactNavigationReuseFixture.Released();
            try
            {
                RunPhase(ContactNavigationPhase.Admission, firstThread, (proof, band) =>
                {
                    Assert.True(band.Admits(0));
                    Assert.Empty(fixture.ActiveSessions());
                    Assert.True(proof.TryEdge(fixture.From, fixture.To));
                    Assert.Equal(0, SupportQueryDiagnostics.Snapshot().FootprintReuseHits);
                    ContactNavigationReuseFixture.Released();
                });
                Assert.Empty(sessions);
                RunGroupedPhase(ContactNavigationPhase.Edges, firstThread);
                Assert.Equal(2, sessions.Count);
                Assert.All(sessions.Values, thread => Assert.Same(firstThread.Thread, thread));
                object[] edgeSessions = sessions.Keys.ToArray();
                RunGroupedPhase(ContactNavigationPhase.Links, linksThread);
                Assert.Equal(4, sessions.Count);
                Assert.All(sessions.Keys.Except(edgeSessions), session => Assert.Same(linksThread.Thread, sessions[session]));
                Assert.Equal(new[] { firstThread.Thread, firstThread.Thread, linksThread.Thread }, threads.ToArray());
                Assert.True(SupportQueryDiagnostics.Snapshot().FootprintReuseHits > 0);
                Assert.True(SupportQueryDiagnostics.Snapshot().InitialHoldReuseHits > 0);
                ContactNavigationReuseFixture.Released();
            }
            finally { workers.Release(null); }

            // Keep the production phase runner, but choose the proof's executing thread explicitly. Both test
            // threads stay alive until every phase finishes, so terminated threads' managed IDs are irrelevant.
            void RunPhase(ContactNavigationPhase phase, ProofThread thread, Action<ContactNavigationProof, ContactNavigationBand> work) =>
                workers.Run(phase, 1, row => row, (proof, band) =>
                {
                    WorkerDiagnosticsPlan plan = WorkerDiagnostics.Plan();
                    WorkerDiagnosticsRecord record = default;
                    thread.Run(() =>
                    {
                        var scope = WorkerDiagnosticsScope.Enter(plan, null);
                        try
                        {
                            threads.Enqueue(Thread.CurrentThread);
                            work(proof, band);
                        }
                        finally { record = scope.Exit(); }
                    });
                    WorkerDiagnostics.Merge([record]);
                }).ThrowSelected();

            void RunGroupedPhase(ContactNavigationPhase phase, ProofThread thread) => RunPhase(phase, thread, (proof, band) =>
            {
                Assert.True(band.Admits(0));
                Assert.Empty(fixture.ActiveSessions());
                ContactNavigationReuseFixture.Released();
                using (proof.BeginSource(ContactNavigationReuseFixture.Source()))
                {
                    Assert.Empty(fixture.ActiveSessions());
                    var cold = SupportQueryDiagnostics.Snapshot();
                    Assert.True(proof.TryEdge(fixture.From, fixture.To));
                    Assert.Single(fixture.ActiveSessions());
                    var first = SupportQueryDiagnostics.Snapshot();
                    Assert.True(first.FootprintReuseMisses > cold.FootprintReuseMisses);
                    Assert.Equal(cold.InitialHoldReuseHits, first.InitialHoldReuseHits);
                    Assert.True(proof.TryEdge(fixture.From, fixture.To));
                    Assert.True(SupportQueryDiagnostics.Snapshot().FootprintReuseHits > first.FootprintReuseHits);
                    Assert.True(SupportQueryDiagnostics.Snapshot().InitialHoldReuseHits > first.InitialHoldReuseHits);
                }
                Assert.Empty(fixture.ActiveSessions());
                ContactNavigationReuseFixture.Released();
                // A further group on the same thread starts cold too.
                var before = SupportQueryDiagnostics.Snapshot();
                using (proof.BeginSource(ContactNavigationReuseFixture.Source(1)))
                    Assert.True(proof.TryEdge(fixture.From, fixture.From));
                Assert.True(SupportQueryDiagnostics.Snapshot().FootprintReuseMisses > before.FootprintReuseMisses);
                Assert.Equal(before.InitialHoldReuseHits, SupportQueryDiagnostics.Snapshot().InitialHoldReuseHits);
                ContactNavigationReuseFixture.Released();
            });
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    sealed class ProofThread : IDisposable
    {
        readonly BlockingCollection<Action> _work = new();
        readonly AutoResetEvent _completed = new(false);
        static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        internal Thread Thread { get; }

        internal ProofThread()
        {
            Thread = new(() =>
            {
                foreach (Action work in _work.GetConsumingEnumerable()) work();
            })
            {
                IsBackground = true,
            };
            Thread.Start();
        }

        internal void Run(Action work)
        {
            ExceptionDispatchInfo? failure = null;
            _work.Add(() =>
            {
                try { work(); }
                catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
                finally { _completed.Set(); }
            });
            Assert.True(_completed.WaitOne(Timeout));
            failure?.Throw();
        }

        public void Dispose()
        {
            _work.CompleteAdding();
            Assert.True(Thread.Join(Timeout));
            _work.Dispose();
            _completed.Dispose();
        }
    }

    [Fact]
    public void ReferencePolicyPropagatesToEveryPhaseWithoutSeeding()
    {
        using var fixture = new ContactNavigationReuseFixture();
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            fixture.OnHeight = (_, _) => Assert.True(SupportReferenceEvaluation.Active);
            using var reference = SupportReferenceEvaluation.Begin();
            fixture.Build(new(3) { UseStableContactHoldReuse = true });
            var counts = SupportQueryDiagnostics.Snapshot();
            Assert.True(counts.Volumes > 0);
            Assert.Equal(0, counts.FootprintReuseHits);
            Assert.Equal(0, counts.FootprintReuseStores);
            Assert.Equal(0, counts.InitialHoldReuseHits);
            ContactNavigationReuseFixture.Released();
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
        Assert.False(SupportReferenceEvaluation.Active);
    }
}
