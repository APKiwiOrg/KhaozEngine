using System;
using System.Linq;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class SupportQueryMeasurementReductionTests
{
    [Fact]
    public void DetachedMeasurementsCombineInOrdinalOrder()
    {
        var a = Synthetic("a", 1, 9, 10, true);
        var b = Synthetic("A", 2, 4, 20, false);
        var c = Synthetic("a", 3, 7, 15, true);
        var expected = SupportQueryMeasurement.Combine([a, b, c]);
        Assert.Equal(new[] { "A", "a" }, expected.ByLabel.Keys);
        Assert.Equal(4, expected.ByLabel["a"][SupportQueryCounter.Find]);
        foreach (var counter in Enum.GetValues<SupportQueryCounter>()) Assert.Equal(6, expected.Total[counter]);
        Assert.All(typeof(SupportQueryRouteMetrics).GetProperties().Where(p => p.Name != "MaximumMediumRetries"),
            p => Assert.Equal(6L, p.GetValue(expected.Total.Routes)));
        Assert.Equal(9, expected.Total.Routes.MaximumMediumRetries);
        Assert.Equal(9, expected.Total.ProofReuse.PeakKeys);
        Assert.Equal(20, expected.Total.ProofReuse.PeakBytes);
        Assert.False(expected.Total.ProofReuse.HasCompleteLifetimes);
        Equal(expected, SupportQueryMeasurement.Combine([c, b, a]));
        Equal(expected, SupportQueryMeasurement.Combine([SupportQueryMeasurement.Combine([a, b]), c]));
        Equal(expected, SupportQueryMeasurement.Combine([a, SupportQueryMeasurement.Combine([b, c])]));
        var empty = SupportQueryMeasurement.Combine([]);
        Assert.Empty(empty.ByLabel);
        foreach (var counter in Enum.GetValues<SupportQueryCounter>()) Assert.Equal(0, empty.Total[counter]);
        Assert.True(empty.Total.ProofReuse.HasCompleteLifetimes);
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.Total[(SupportQueryCounter)(-1)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.Total[(SupportQueryCounter)48]);
        Assert.Throws<ArgumentNullException>(() => SupportQueryMeasurement.Combine(null!));
        Assert.Throws<ArgumentNullException>(() => SupportQueryMeasurement.Combine([a, null!]));
        Assert.Equal(1, a.Total[SupportQueryCounter.Find]);
    }

    [Fact]
    public void CaptureAndCombinationRefuseLongOverflow()
    {
        using (var capture = SupportQueryCapture.Begin("overflow"))
        {
            long[] counts = new long[48];
            counts[0] = long.MaxValue;
            SupportQueryDiagnostics.Exchange(SupportQueryCaptureTests.State() with { Counts = counts });
            Assert.Throws<OverflowException>(() => SupportQueryDiagnostics.Record(SupportQueryCounter.Find));
            Assert.Equal(long.MaxValue, capture.Snapshot().Total[SupportQueryCounter.Find]);
            var one = Counts(1);
            Assert.Throws<OverflowException>(() => SupportQueryDiagnostics.Merge(one, default));
            Assert.Equal(long.MaxValue, capture.Snapshot().Total[SupportQueryCounter.Find]);
        }
        var huge = Synthetic("large", long.MaxValue, 0, 0, true);
        var small = Synthetic("small", 1, 0, 0, true);
        Assert.Throws<OverflowException>(() => SupportQueryMeasurement.Combine([huge, small]));
        Assert.Equal(long.MaxValue, huge.Total[SupportQueryCounter.Find]);
        using var parent = SupportQueryCapture.Begin("same");
        SupportQueryDiagnostics.Exchange(SupportQueryCaptureTests.State() with { Counts = Enumerable.Repeat(long.MaxValue, 48).ToArray() });
        using (SupportQueryCapture.Begin("same")) SupportQueryDiagnostics.Record(SupportQueryCounter.Find);
        Assert.Throws<OverflowException>(() => parent.Dispose());
        Assert.NotEqual("same", SupportQueryCaptureTests.State().MeasurementLabel);
    }

    static SupportQueryMeasurement Synthetic(string label, long count, long keys, long bytes, bool complete)
    {
        var prior = SupportQueryCaptureTests.State();
        try
        {
            using var capture = SupportQueryCapture.Begin(label);
            SupportQueryDiagnostics.Exchange(SupportQueryCaptureTests.State() with
            {
                Counts = Enumerable.Repeat(count, 48).ToArray(),
                Routes = new(count, count, count, keys, count, count, count, count, count, count),
                Retention = new(0, 0, keys, bytes),
                HasCompleteLifetimes = complete,
            });
            capture.Dispose();
            return capture.Snapshot();
        }
        finally { SupportQueryDiagnostics.Exchange(prior); }
    }

    static SupportQueryCounts Counts(long value) => (SupportQueryCounts)typeof(SupportQueryCounts).GetConstructors().Single()
        .Invoke(Enumerable.Repeat((object)value, 48).ToArray());

    static void Equal(SupportQueryMeasurement expected, SupportQueryMeasurement actual)
    {
        Assert.Equal(expected.ByLabel.Keys, actual.ByLabel.Keys);
        Metrics(expected.Total, actual.Total);
        foreach (string label in expected.ByLabel.Keys) Metrics(expected.ByLabel[label], actual.ByLabel[label]);
        static void Metrics(SupportQueryMetrics expected, SupportQueryMetrics actual)
        {
            foreach (var counter in Enum.GetValues<SupportQueryCounter>()) Assert.Equal(expected[counter], actual[counter]);
            foreach (var property in typeof(SupportQueryRouteMetrics).GetProperties())
                Assert.Equal(property.GetValue(expected.Routes), property.GetValue(actual.Routes));
            Assert.Equal(expected.ProofReuse.PeakKeys, actual.ProofReuse.PeakKeys);
            Assert.Equal(expected.ProofReuse.PeakBytes, actual.ProofReuse.PeakBytes);
            Assert.Equal(expected.ProofReuse.HasCompleteLifetimes, actual.ProofReuse.HasCompleteLifetimes);
        }
    }
}
