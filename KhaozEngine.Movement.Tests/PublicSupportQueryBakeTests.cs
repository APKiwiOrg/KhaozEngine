using System;
using System.Collections.Concurrent;
using System.Linq;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class PublicSupportQueryBakeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void PublicCaptureCollectsEveryJoinedWorkerUnderItsBodyLabel(int degree)
    {
        using var scene = new ContactNavigationReuseFixture();
        byte[] disabled = Bake(scene, degree);
        var oracle = new BakeOracle("player");
        using var hooks = ContactNavigationBuildHooks.Install(oracle.Hooks);
        using var capture = SupportQueryCapture.Begin("player");
        Assert.Equal(disabled, Bake(scene, degree, oracle));
        SupportQueryMeasurement result = capture.Snapshot();
        oracle.AssertMatches(result.Total, degree);
        Assert.Equal(new[] { "player" }, result.ByLabel.Keys);
        EqualMetrics(result.Total, result.ByLabel["player"]);
        foreach (var counter in new[] { SupportQueryCounter.Find, SupportQueryCounter.Bindings,
            SupportQueryCounter.PhysicsLeases, SupportQueryCounter.MovementLeases,
            SupportQueryCounter.FootprintReuseHits, SupportQueryCounter.InitialHoldReuseHits })
            Assert.True(result.Total[counter] > 0, counter.ToString());
        Assert.True(result.Total.ProofReuse.PeakKeys > 0);
        Assert.True(result.Total.ProofReuse.PeakBytes > 0);
        Assert.True(result.Total.ProofReuse.HasCompleteLifetimes);
    }

    [Fact]
    public void WorkerCountPreservesLogicalProofWorkAndReportsRealSetup()
    {
        var serial = Run(1);
        var parallel = Run(3);
        Assert.Equal(serial.Bytes, parallel.Bytes);
        foreach (var counter in new[] { SupportQueryCounter.Find, SupportQueryCounter.EdgeSteps })
            Assert.Equal(serial.Measurement.Total[counter], parallel.Measurement.Total[counter]);
        foreach (var counter in new[] { SupportQueryCounter.PhysicsLeases,
            SupportQueryCounter.MovementLeases, SupportQueryCounter.ProducerPins })
        {
            long observedSetup = parallel.Oracle.Setup[counter] - serial.Oracle.Setup[counter];
            Assert.True(observedSetup > 0);
            Assert.Equal(observedSetup, parallel.Measurement.Total[counter] - serial.Measurement.Total[counter]);
        }
        Assert.Equal(0, serial.Oracle.WorkerPins);
        Assert.Equal(3, parallel.Oracle.WorkerPins);

        static (byte[] Bytes, SupportQueryMeasurement Measurement, BakeOracle Oracle) Run(int degree)
        {
            using var scene = new ContactNavigationReuseFixture();
            Bake(scene, 1); // Publish world forms before either measured build.
            var oracle = new BakeOracle("player");
            using var hooks = ContactNavigationBuildHooks.Install(oracle.Hooks);
            using var capture = SupportQueryCapture.Begin("player");
            byte[] bytes = Bake(scene, degree, oracle);
            var measurement = capture.Snapshot();
            oracle.AssertMatches(measurement.Total, degree);
            Assert.True(measurement.Total.ProofReuse.HasCompleteLifetimes);
            return (bytes, measurement, oracle);
        }
    }

    [Fact]
    public void WorkerRecordReductionIsOrderAndGroupingIndependent()
    {
        WorkerDiagnosticsRecord[] records = [Record("a", 1, 9, 10, true),
            Record("A", 2, 4, 20, false), Record("a", 3, 7, 15, true)];
        var forward = Reduce(records);
        var reverse = Reduce(records.Reverse().ToArray());
        var left = SupportQueryMeasurement.Combine([Reduce(records[..2]), Reduce(records[2..])]);
        var right = SupportQueryMeasurement.Combine([Reduce(records[..1]), Reduce(records[1..])]);
        Assert.Equal(new[] { "A", "a" }, forward.ByLabel.Keys);
        foreach (var counter in Enum.GetValues<SupportQueryCounter>())
        {
            Assert.Equal(6, forward.Total[counter]);
            Assert.Equal(4, forward.ByLabel["a"][counter]);
            Assert.Equal(2, forward.ByLabel["A"][counter]);
        }
        Assert.All(typeof(SupportQueryRouteMetrics).GetProperties().Where(p => p.Name != "MaximumMediumRetries"),
            p => Assert.Equal(6L, p.GetValue(forward.Total.Routes)));
        Assert.Equal(9, forward.Total.Routes.MaximumMediumRetries);
        Assert.Equal(9, forward.Total.ProofReuse.PeakKeys);
        Assert.Equal(20, forward.Total.ProofReuse.PeakBytes);
        Assert.False(forward.Total.ProofReuse.HasCompleteLifetimes);
        Assert.True(forward.ByLabel["a"].ProofReuse.HasCompleteLifetimes);
        foreach (var result in new[] { reverse, left, right })
        {
            Assert.Equal(forward.ByLabel.Keys, result.ByLabel.Keys);
            EqualMetrics(forward.Total, result.Total);
            foreach (string label in forward.ByLabel.Keys) EqualMetrics(forward.ByLabel[label], result.ByLabel[label]);
        }

        static WorkerDiagnosticsRecord Record(string label, long count, long keys, long bytes, bool complete) => new(
            (SupportQueryCounts)typeof(SupportQueryCounts).GetConstructors().Single()
                .Invoke(Enumerable.Repeat((object)count, 48).ToArray()),
            new(count, count, count, keys, count, count, count, count, count, count),
            new(0, 0, keys, bytes), label, complete);

        static SupportQueryMeasurement Reduce(WorkerDiagnosticsRecord[] values)
        {
            // Each disjoint label is reduced once in its owning recorder, just like separate body builds.
            return SupportQueryMeasurement.Combine(values.GroupBy(record => record.MeasurementLabel!, StringComparer.Ordinal)
                .Select(group =>
                {
                    using var capture = SupportQueryCapture.Begin(group.Key);
                    WorkerDiagnostics.Merge(group.ToArray());
                    return capture.Snapshot();
                }).ToArray());
        }
    }

    internal static byte[] Bake(ContactNavigationReuseFixture scene, int degree, BakeOracle? oracle = null)
    {
        Action<ContactAllocationPhase, bool>? priorObserver = null;
        Action? priorWater = scene.Water.BeforeGenerationRead;
        if (oracle is not null)
        {
            priorObserver = ContactAllocationDiagnostics.Exchange((phase, entering) =>
            {
                oracle.ObserveSetup(phase, entering);
                priorObserver?.Invoke(phase, entering);
            });
            scene.Water.BeforeGenerationRead = () =>
            {
                oracle.ObserveCallerValidation();
                priorWater?.Invoke();
            };
        }
        try { return scene.Build(new NavBakeExecution(degree) { UseStableContactHoldReuse = true }); }
        finally
        {
            if (oracle is not null) ContactAllocationDiagnostics.Exchange(priorObserver);
            scene.Water.BeforeGenerationRead = priorWater;
        }
    }

    internal static void EqualMetrics(SupportQueryMetrics expected, SupportQueryMetrics actual)
    {
        foreach (var counter in Enum.GetValues<SupportQueryCounter>())
            Assert.True(expected[counter] == actual[counter], $"{counter}: expected {expected[counter]}, actual {actual[counter]}");
        foreach (var property in typeof(SupportQueryRouteMetrics).GetProperties())
            Assert.Equal(property.GetValue(expected.Routes), property.GetValue(actual.Routes));
        Assert.Equal(expected.ProofReuse.PeakKeys, actual.ProofReuse.PeakKeys);
        Assert.Equal(expected.ProofReuse.PeakBytes, actual.ProofReuse.PeakBytes);
        Assert.Equal(expected.ProofReuse.HasCompleteLifetimes, actual.ProofReuse.HasCompleteLifetimes);
    }

    internal sealed class BakeOracle(string label)
    {
        readonly ConcurrentDictionary<(ContactNavigationPhase Phase, int Thread), WorkerDiagnosticsRecord> _workers = new();
        internal SupportQueryCounts Setup;
        readonly int _caller = Environment.CurrentManagedThreadId;
        bool _setupCaptured;
        long _callerValidations;
        SupportRouteCounts _setupRoutes;
        SupportProofReuseRetention _setupRetention;
        internal int WorkerPins;
        internal Action<ContactNavigationPhase, int>? AfterBand;

        internal ContactNavigationBuildHooks Hooks => new()
        {
            WorkerPinned = _ =>
            {
                WorkerPins++;
                Setup = SupportQueryDiagnostics.Snapshot();
                _callerValidations = 0;
                _setupRoutes = SupportQueryDiagnostics.Routes;
                _setupRetention = SupportQueryDiagnostics.Retention;
            },
            BandCompleted = (phase, band) =>
            {
                Assert.Equal(label, SupportQueryDiagnostics.MeasurementLabel);
                var retention = SupportQueryDiagnostics.Retention;
                Assert.Equal(0, retention.Keys);
                Assert.Equal(0, retention.Bytes);
                _workers[(phase, Environment.CurrentManagedThreadId)] = new(SupportQueryDiagnostics.Snapshot(),
                    SupportQueryDiagnostics.Routes, retention, label, SupportQueryDiagnostics.HasCompleteLifetimes);
                AfterBand?.Invoke(phase, band);
            },
        };

        internal void ObserveSetup(ContactAllocationPhase phase, bool entering)
        {
            if (_setupCaptured || entering || phase != ContactAllocationPhase.MovementLease) return;
            _setupCaptured = true;
            Setup = SupportQueryDiagnostics.Snapshot();
        }

        internal void ObserveCallerValidation()
        {
            if (WorkerPins > 0 && Environment.CurrentManagedThreadId == _caller) _callerValidations++;
        }

        internal void AssertMatches(SupportQueryMetrics actual, int degree)
        {
            if (degree == 1)
            {
                Assert.Empty(_workers);
                // Serial proofs stay on the caller. Their retention oracle is that same interval.
                EqualMetrics(new(SupportQueryDiagnostics.Snapshot(), SupportQueryDiagnostics.Routes,
                    SupportQueryDiagnostics.Retention, SupportQueryDiagnostics.HasCompleteLifetimes), actual);
                return;
            }
            Assert.NotEmpty(_workers);
            SupportQueryCounts counts = Setup with { PhysicsLeases = Setup.PhysicsLeases + _callerValidations };
            SupportRouteCounts routes = _setupRoutes;
            long keys = _setupRetention.PeakKeys, bytes = _setupRetention.PeakBytes;
            bool complete = true;
            foreach (var record in _workers.Values)
            {
                counts += record.Counts;
                routes += record.Routes;
                keys = Math.Max(keys, record.Retention.PeakKeys);
                bytes = Math.Max(bytes, record.Retention.PeakBytes);
                complete &= record.HasCompleteLifetimes;
            }
            EqualMetrics(new(counts, routes, new(0, 0, keys, bytes), complete), actual);
        }
    }
}
