using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Fixtures;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

public class ContactNavigationProofReuseTests(ITestOutputHelper output)
{
    [Fact]
    public void SameLayerSourceReusesFootprintsAcrossMovementLeases()
    {
        SupportQueryDiagnostics.ThreadState prior = SupportQueryDiagnostics.Exchange(
            SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            var bake = ContactNavigationBaselineTests.BuildFixture("low-lip-bank");
            string normalizedHash = Convert.ToHexStringLower(SHA256.HashData(
                ContactNavigationBaselineTests.NormalizeFile(bake)));
            SupportQueryCounts counts = SupportQueryDiagnostics.Snapshot();
            Assert.Equal("494ab0951ab46de04665351bf1e39ad9156f30aefa73cb43b34f4ecaba72e922", normalizedHash);
            Assert.True(counts.FootprintReuseHits > 0);
            Assert.Equal(0, counts.InitialHoldReuseHits);
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    public static IEnumerable<object[]> PinnedDegrees => ContactNavigationBaselineTests.Baselines
        .SelectMany(row => new[] { 1, 3 }.Select(degree => new object[] { row[0], row[1], degree }));

    [Theory]
    [MemberData(nameof(PinnedDegrees))]
    public void PinnedContactBaselinesAtSerialAndParallelDegrees(string fixture, string hash, int degree)
    {
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            ContactNavigationBaselineTests.BaselineBake reference;
            using (ContactNavigationBuildHooks.Install(new() { DisableFootprintReuse = true }))
                reference = ContactNavigationBaselineTests.BuildFixture(fixture, new NavBakeExecution(degree));
            SupportQueryCounts off = SupportQueryDiagnostics.Snapshot();
            SupportQueryDiagnostics.Reset();
            var actual = ContactNavigationBaselineTests.BuildFixture(fixture, new NavBakeExecution(degree));
            SupportQueryCounts counts = SupportQueryDiagnostics.Snapshot();
            Assert.Equal(GroundNavigationBakeRoundTripTests.Write(reference.Bake),
                GroundNavigationBakeRoundTripTests.Write(actual.Bake));
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(ContactNavigationBaselineTests.NormalizeFile(actual))));
            ContactNavigationBaselineTests.AssertFixture(fixture, actual);
            Assert.True(counts.FootprintReuseHits > 0);
            Assert.Equal(0, counts.InitialHoldReuseHits);
            Assert.Equal(off.Find, counts.Find);
            Assert.Equal(off.PhysicsLeases, counts.PhysicsLeases);
            Assert.Equal(off.MovementLeases, counts.MovementLeases);
            Assert.Equal(off.ProducerPins, counts.ProducerPins);
            Assert.Equal(off.Volumes - counts.FootprintReuseHits, counts.Volumes);
            ContactNavigationReuseFixture.Released();
            output.WriteLine($"{fixture} degree={degree} hash={hash} offVolumes={off.Volumes} volumes={counts.Volumes} " +
                $"hits={counts.FootprintReuseHits} misses={counts.FootprintReuseMisses} stores={counts.FootprintReuseStores} " +
                $"budgetSkips={counts.FootprintReuseBudgetSkips} initialHolds={counts.InitialHoldReuseHits} " +
                $"peakKeys={SupportQueryDiagnostics.Retention.PeakKeys} peakBytes={SupportQueryDiagnostics.Retention.PeakBytes}");
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualRestingAnchorsKeepOriginalRawSourceAndLayer(bool parallel)
    {
        var sources = new ContactNavigationSources();
        Vector3 a = new(0.5f, 0, 0.5f), b = new(0.5f, 2, 0.5f), c = new(1.5f, 0, 0.5f);
        var rawA = new ContactNavigationRawSource(0, 0, 1, 0);
        var rawB = new ContactNavigationRawSource(0, 0, 3, BitConverter.SingleToInt32Bits(2));
        var rawC = new ContactNavigationRawSource(1, 0, 2, 0);
        if (parallel)
        {
            var band = new ContactAdmissionBand(0, 2);
            band.Sources.Add((a, rawA, 1));
            band.Sources.Add((b, rawB, 3));
            band.Sources.Add((c, rawC, 6));
            // Compact accepted samples independently of their original ordinals.
            band.Keep(0, [new(true, 0, 4), new(true, 2, 4)]);
            band.Keep(1, [new(true, 0, 4)]);
            ContactAdmissionBand.MergeSources([band], new ContactNavigationCutoff(), sources);
        }
        else
        {
            sources.Record(a, rawA);
            sources.Record(b, rawB);
            sources.Record(c, rawC);
        }
        var lower = NavGrid.FromSurfaces(2, 1, 1, 0, 0, (_, _) => new(true, 0, 4), 0.2f, 1);
        var upper = NavGrid.FromSurfaces(2, 1, 1, 0, 0, (x, _) => new(x == 0, 2, 4), 0.2f, 1);
        var space = new NavSpace([lower, upper], []);
        sources.BindLayers(space);
        var rested = ContactNavigationProof.WithRestingHeights(space, new Dictionary<Vector3, float>
        { [a] = 2.5f, [b] = 2.5f, [c] = 2.5f });
        Assert.Equal(rested.Layers[0].RestingHeightAt(0, 0), rested.Layers[1].RestingHeightAt(0, 0));
        Assert.True(sources.TryGet(0, 0, 0, out var firstSource));
        Assert.True(sources.TryGet(1, 0, 0, out var secondSource));
        Assert.True(sources.TryGet(0, 1, 0, out var columnSource));
        Assert.Equal(new ContactNavigationSource(rawA, 0), firstSource);
        Assert.Equal(new ContactNavigationSource(rawB, 1), secondSource);
        Assert.Equal(new ContactNavigationSource(rawC, 0), columnSource);
        Assert.NotEqual(firstSource.Raw.SurfaceOrdinal, secondSource.Raw.SurfaceOrdinal);
        Assert.NotEqual(new SupportFootprintReuseDomain(this, firstSource), new(this, secondSource));
        Assert.False(sources.TryGet(1, 1, 0, out _));
        var ambiguous = new ContactNavigationSources();
        ambiguous.Record(a, rawA);
        ambiguous.Record(a, rawA with { SurfaceOrdinal = 5 });
        ambiguous.BindLayers(space);
        Assert.False(ambiguous.TryGet(0, 0, 0, out _));
        Assert.False(ambiguous.TryGet(1, 0, 0, out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FootprintHitStillRunsAnalyticBindingAndMediumRoutes(bool physicsWins, bool fail)
    {
        using var fixture = new ContactNavigationReuseFixture(physicsWins);
        fixture.FailProvider = fail;
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            var reference = Run(false);
            SupportQueryDiagnostics.Reset();
            var reused = Run(true);
            Assert.Equal(reference.Trace, reused.Trace);
            Assert.Equal(reference.Finds, reused.Finds);
            Assert.Equal(reference.Results, reused.Results);
            Assert.Equal(reference.Failure, reused.Failure);
            Assert.Equal(reference.Routes, reused.Routes);
            Assert.Equal(reference.Counts.AnalyticSamples, reused.Counts.AnalyticSamples);
            Assert.Equal(reference.Counts.Bindings, reused.Counts.Bindings);
            Assert.True(reused.Counts.FootprintReuseHits > 0);
            Assert.Equal(0, reused.Counts.InitialHoldReuseHits);
            Assert.NotEmpty(reused.Trace);
            Assert.True(reused.Counts.Bindings > 0);
            Assert.Contains(reused.Trace.OfType<ContactNavigationReuseFixture.BindingCall>(),
                call => call.Status == MovementAvailability.Known && call.Request.Static.HasValue == physicsWins);
            Assert.Equal(new[] { true, true }, reused.Results.Take(2));
            Assert.Equal(fail, reused.Failure is not null);
            ContactNavigationReuseFixture.Released();
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }

        (object[] Trace, SupportFindObservation[] Finds, bool[] Results, string? Failure,
            SupportQueryCounts Counts, SupportRouteCounts Routes) Run(bool reuse)
        {
            fixture.Trace.Clear();
            var finds = new List<SupportFindObservation>();
            SupportQueryDiagnostics.SetFindObserver(finds.Add);
            using var proof = fixture.Proof(reuse);
            var results = new List<bool>();
            string? failure = null;
            try
            {
                using var group = proof.BeginSource(ContactNavigationReuseFixture.Source());
                results.Add(proof.TryEdge(fixture.From, fixture.From));
                results.Add(proof.TryEdge(fixture.From, fixture.From));
                results.Add(proof.TryEdge(fixture.From, fixture.To));
            }
            catch (ContactNavigationReuseFixture.ProviderFailure thrown) { failure = thrown.Message; }
            return ([.. fixture.Trace], [.. finds], [.. results], failure,
                SupportQueryDiagnostics.Snapshot(), SupportQueryDiagnostics.Routes);
        }
    }

    [Fact]
    public void SourceResetAndUnsupportedViewUseTheOriginalProof()
    {
        using var fixture = new ContactNavigationReuseFixture();
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            using var proof = fixture.Proof();
            Assert.True(proof.TryEdge(fixture.From, fixture.To));
            Assert.Equal(0, SupportQueryDiagnostics.Snapshot().FootprintReuseHits);
            SupportQueryCounts first = ColdSource(ContactNavigationReuseFixture.Source());
            SupportQueryCounts next = ColdSource(ContactNavigationReuseFixture.Source(3, 1));
            Assert.Equal(first, next);
            Assert.True(first.FootprintReuseMisses > 0);
            using (var independent = fixture.Proof())
            {
                SupportQueryDiagnostics.Reset();
                using (independent.BeginSource(ContactNavigationReuseFixture.Source()))
                    Assert.True(independent.TryEdge(fixture.From, fixture.To));
                Assert.Equal(first, SupportQueryDiagnostics.Snapshot());
            }
            ContactNavigationReuseFixture.Released();

            SupportQueryCounts ColdSource(ContactNavigationSource source)
            {
                SupportQueryDiagnostics.Reset();
                using (proof.BeginSource(source)) Assert.True(proof.TryEdge(fixture.From, fixture.To));
                ContactNavigationReuseFixture.Released();
                return SupportQueryDiagnostics.Snapshot();
            }
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void UnsupportedSaturatedAndForcedBypassPreserveFileAndProviderTrace(bool unsupported, bool saturated)
    {
        using var fixture = new ContactNavigationReuseFixture(unsupported: unsupported);
        var limited = new HashSet<object>();
        fixture.OnHeight = (_, _) =>
        {
            if (!saturated) return;
            foreach (var active in fixture.ActiveSessions())
                if (limited.Add(active.Session)) ContactNavigationReuseFixture.LimitSession(active.Session, 1);
        };
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            byte[] referenceFile;
            fixture.Trace.Clear();
            using (ContactNavigationBuildHooks.Install(new() { DisableFootprintReuse = true }))
                referenceFile = fixture.Build();
            object[] referenceProviderTrace = [.. fixture.Trace];
            SupportQueryCounts reference = SupportQueryDiagnostics.Snapshot();
            fixture.Trace.Clear();
            SupportQueryDiagnostics.Reset();
            byte[] reuseFile;
            using (ContactNavigationBuildHooks.Install(new()
            { DisableFootprintReuse = !unsupported && !saturated, DisableInitialHoldReuse = true }))
                reuseFile = fixture.Build();
            Assert.Equal(referenceFile, reuseFile);
            Assert.Equal(referenceProviderTrace, fixture.Trace);
            var counts = SupportQueryDiagnostics.Snapshot();
            Assert.Equal(reference.Find, counts.Find);
            Assert.Equal(reference.Volumes - counts.FootprintReuseHits, counts.Volumes);
            Assert.Equal(reference.Bindings, counts.Bindings);
            Assert.Equal(0, counts.InitialHoldReuseHits);
            if (saturated)
            {
                Assert.NotEmpty(limited);
                Assert.True(counts.FootprintReuseBudgetSkips > 0);
                Assert.True(counts.FootprintReuseHits > 0);
                Assert.Equal(1, SupportQueryDiagnostics.Retention.PeakKeys);
            }
            else Assert.Equal(0, counts.FootprintReuseHits);
            ContactNavigationReuseFixture.Released();
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void OriginalOrdinalSurvivesRealSerialAndParallelAdmission(int degree)
    {
        using var fixture = new ContactNavigationReuseFixture(skippedFloor: true);
        var observed = new System.Collections.Concurrent.ConcurrentBag<ContactNavigationSource>();
        fixture.OnHeight = (_, _) =>
        {
            foreach (var active in fixture.ActiveSessions())
                observed.Add(Assert.IsType<ContactNavigationSource>(active.Domain.SourceIdentity));
        };
        fixture.Build(new NavBakeExecution(degree));
        Assert.NotEmpty(observed);
        foreach (var source in observed)
        {
            Assert.Equal(1, source.Raw.SurfaceOrdinal);
            Assert.Equal(0, source.Layer);
            var column = fixture.Bake.Columns.GetColumn(source.Raw.ColumnX, source.Raw.ColumnZ);
            Assert.Equal(2, column.Length);
            Assert.Equal(BitConverter.SingleToInt32Bits(column[1].Height), source.Raw.RawHeightBits);
        }
    }

    [Fact]
    public void AdmissionAndStairPhasesRetainNoSourceSession()
    {
        var phases = new System.Collections.Concurrent.ConcurrentBag<ContactNavigationPhase>();
        using var hooks = ContactNavigationBuildHooks.Install(new()
        {
            BandCompleted = (phase, _) =>
            {
                phases.Add(phase);
                ContactNavigationReuseFixture.Released();
                if (phase == ContactNavigationPhase.Edges) return;
                Assert.Equal(0, SupportQueryDiagnostics.Snapshot().FootprintReuseHits);
                Assert.Equal(0, SupportQueryDiagnostics.Snapshot().FootprintReuseStores);
            },
        });
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            ContactNavigationBaselineTests.BuildFixture("stair-accepted-refused", new NavBakeExecution(3));
            Assert.Contains(ContactNavigationPhase.Admission, phases);
            Assert.Contains(ContactNavigationPhase.Edges, phases);
            Assert.Contains(ContactNavigationPhase.Links, phases);
            Assert.True(SupportQueryDiagnostics.Snapshot().FootprintReuseHits > 0);
            ContactNavigationReuseFixture.Released();
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Fact]
    public void ParallelSourceFailureReleasesEveryRetainedPayload()
    {
        using var fixture = new ContactNavigationReuseFixture();
        fixture.OnHeight = (x, _) =>
        {
            foreach (var active in fixture.ActiveSessions())
                if (active.Domain.SourceIdentity is ContactNavigationSource { Raw.ColumnX: 6 } && x > 0)
                    throw new ContactNavigationReuseFixture.ProviderFailure();
        };
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            Assert.Throws<ContactNavigationReuseFixture.ProviderFailure>(() => fixture.Build(new NavBakeExecution(3)));
            Assert.True(SupportQueryDiagnostics.Snapshot().FootprintReuseStores > 0);
            Assert.True(SupportQueryDiagnostics.Snapshot().FootprintReuseHits > 0);
            ContactNavigationReuseFixture.Released();
            fixture.OnHeight = null;
            Assert.NotEmpty(fixture.Build(new NavBakeExecution(3)));
            ContactNavigationReuseFixture.Released();
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Fact]
    public void ReferenceEvaluationIsCapturedAndRestoredInWorkers()
    {
        Assert.False(SupportReferenceEvaluation.Active);
        int bands = 0;
        using var hooks = ContactNavigationBuildHooks.Install(new()
        {
            BandCompleted = (_, _) =>
            {
                Assert.True(SupportReferenceEvaluation.Active);
                System.Threading.Interlocked.Increment(ref bands);
                ContactNavigationReuseFixture.Released();
            },
        });
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            byte[] ordinary = GroundNavigationBakeRoundTripTests.Write(
                ContactNavigationBaselineTests.BuildFixture("stair-accepted-refused").Bake);
            SupportQueryDiagnostics.Reset();
            using (SupportReferenceEvaluation.Begin())
            {
                byte[] reference = GroundNavigationBakeRoundTripTests.Write(
                    ContactNavigationBaselineTests.BuildFixture("stair-accepted-refused", new NavBakeExecution(3)).Bake);
                Assert.Equal(ordinary, reference);
                Assert.True(SupportReferenceEvaluation.Active);
                Assert.Equal(0, SupportQueryDiagnostics.Snapshot().FootprintReuseHits);
                Assert.Equal(0, SupportQueryDiagnostics.Snapshot().FootprintReuseStores);
                Assert.Equal(0, SupportQueryDiagnostics.Snapshot().InitialHoldReuseHits);
            }
            Assert.True(bands > 0);
            Assert.False(SupportReferenceEvaluation.Active);
            ContactNavigationReuseFixture.Released();
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    [Fact]
    public void SourceScopeNeverNestsAnExplicitPhysicsLease()
    {
        using var fixture = new ContactNavigationReuseFixture();
        var prior = SupportQueryDiagnostics.Exchange(SupportQueryDiagnostics.ThreadState.Fresh(true));
        try
        {
            var off = Run(false);
            SupportQueryDiagnostics.Reset();
            var on = Run(true);
            Assert.Equal(off.PhysicsLeases, on.PhysicsLeases);
            Assert.Equal(off.MovementLeases, on.MovementLeases);
            Assert.Equal(off.ProducerPins, on.ProducerPins);
            Assert.True(on.FootprintReuseHits > 0);
            using var proof = fixture.Proof();
            using var group = proof.BeginSource(ContactNavigationReuseFixture.Source());
            using var outer = fixture.Scene.Physics.AcquireQueryReadLease();
            Assert.Throws<InvalidOperationException>(() => proof.TryEdge(fixture.From, fixture.To));
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }

        SupportQueryCounts Run(bool reuse)
        {
            using var proof = fixture.Proof(reuse);
            using (proof.BeginSource(ContactNavigationReuseFixture.Source()))
            {
                SupportQueryCounts before = SupportQueryDiagnostics.Snapshot();
                Assert.False(proof.TryEdge(new(float.NaN, 0, 0), fixture.To));
                Assert.Equal(before, SupportQueryDiagnostics.Snapshot());
                Assert.True(proof.TryEdge(fixture.From, fixture.To));
                Assert.True(proof.TryEdge(fixture.From, fixture.To));
            }
            ContactNavigationReuseFixture.Released();
            return SupportQueryDiagnostics.Snapshot();
        }
    }

    [Fact]
    public void LeaseBridgeAuthenticatesTheCurrentProducerPin()
    {
        using var fixture = new ContactNavigationReuseFixture();
        using var view = fixture.Scene.Physics.CreateQueryViewExcludingStatics([]);
        var producer = new EnvironmentAcquisitionFixture(view);
        var acquired = producer.Acquire();
        Assert.Equal(MovementAvailability.Known, acquired.Status);
        using var queries = Assert.IsType<MovementQueryLease>(acquired.Lease);
        Assert.True(queries.TryOpenSupportFootprintReuse(out var opened));
        using var session = opened!;
        session.ResetSource(new(this, ContactNavigationReuseFixture.Source()));
        queries.AssertSupportFootprintReuseCurrent(session);
        producer.Fault = "stale-pin";
        Assert.Throws<InvalidOperationException>(() => queries.AssertSupportFootprintReuseCurrent(session));
        producer.Fault = "";
        queries.AssertSupportFootprintReuseCurrent(session);
    }

    [Fact]
    public void StaleSourceBasisKeepsTheOriginalAuthenticationFailure()
    {
        string reference = Probe(false);
        string reused = Probe(true);
        Assert.Equal(reference, reused);

        static string Probe(bool reuse)
        {
            using var fixture = new ContactNavigationReuseFixture();
            using var proof = fixture.Proof(reuse);
            var failure = Assert.Throws<InvalidOperationException>(() =>
            {
                using var group = proof.BeginSource(ContactNavigationReuseFixture.Source());
                Assert.True(proof.TryEdge(fixture.From, fixture.From));
                Assert.True(proof.TryEdge(fixture.From, fixture.From));
                fixture.Scene.Physics.AddStatic(new BoxShape(Vector3.One), Pose.At(new(10, 0, 0)));
                proof.TryEdge(fixture.From, fixture.To);
            });
            ContactNavigationReuseFixture.Released();
            if (reuse) Assert.IsType<InvalidOperationException>(failure.Data["SupportFootprintReuseCleanupFailure"]);
            return failure.Message;
        }
    }
}
