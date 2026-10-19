using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Numerics;
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

    [Fact]
    public void WorkerPhaseThreadChangeCannotInheritAReuseSession()
    {
        using var fixture = new ContactNavigationReuseFixture();
        using var source = fixture.Context.CreateNavigationBinding(Tuning);
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            var workers = ContactNavigationWorkers.Create(fixture.Bake, fixture.Context, source,
                new(fixture.Bake.Columns, fixture.Bake.Options, Tuning, default), 1, 1, new(true, true, false));
            var sessions = new ConcurrentDictionary<object, int>();
            var threads = new ConcurrentQueue<int>();
            fixture.OnHeight = (_, _) =>
            {
                foreach (var active in fixture.ActiveSessions())
                    sessions.TryAdd(active.Session, Environment.CurrentManagedThreadId);
            };
            Assert.Empty(fixture.ActiveSessions());
            ContactNavigationReuseFixture.Released();
            try
            {
                workers.Run(ContactNavigationPhase.Admission, 1, row => row, (proof, band) =>
                {
                    threads.Enqueue(Environment.CurrentManagedThreadId);
                    Assert.True(band.Admits(0));
                    Assert.Empty(fixture.ActiveSessions());
                    Assert.True(proof.TryEdge(fixture.From, fixture.To));
                    Assert.Equal(0, SupportQueryDiagnostics.Snapshot().FootprintReuseHits);
                    ContactNavigationReuseFixture.Released();
                }).ThrowSelected();
                Assert.Empty(sessions);
                RunGroupedPhase(ContactNavigationPhase.Edges);
                Assert.Equal(2, sessions.Count);
                RunGroupedPhase(ContactNavigationPhase.Links);
                Assert.Equal(4, sessions.Count);
                Assert.Equal(3, threads.Distinct().Count());
                Assert.Equal(2, sessions.Values.Distinct().Count());
                Assert.True(SupportQueryDiagnostics.Snapshot().FootprintReuseHits > 0);
                Assert.True(SupportQueryDiagnostics.Snapshot().InitialHoldReuseHits > 0);
                ContactNavigationReuseFixture.Released();
            }
            finally { workers.Release(null); }

            void RunGroupedPhase(ContactNavigationPhase phase) => workers.Run(phase, 1, row => row, (proof, band) =>
            {
                threads.Enqueue(Environment.CurrentManagedThreadId);
                Assert.True(band.Admits(0));
                Assert.Empty(fixture.ActiveSessions());
                ContactNavigationReuseFixture.Released();
                using (proof.BeginSource(ContactNavigationReuseFixture.Source()))
                {
                    Assert.Empty(fixture.ActiveSessions());
                    Assert.True(proof.TryEdge(fixture.From, fixture.To));
                    Assert.Single(fixture.ActiveSessions());
                    Assert.True(proof.TryEdge(fixture.From, fixture.To));
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
            }).ThrowSelected();
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
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
