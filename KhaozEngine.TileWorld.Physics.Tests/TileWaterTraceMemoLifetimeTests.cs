using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterTraceMemoLifetimeTests
{
    [Theory]
    [InlineData("short-spans", MovementAvailability.CapacityExceeded)]
    [InlineData("short-contacts", MovementAvailability.CapacityExceeded)]
    [InlineData("candidate-capacity", MovementAvailability.CapacityExceeded)]
    [InlineData("precision", MovementAvailability.PrecisionExceeded)]
    [InlineData("unresolved", MovementAvailability.Unresolved)]
    public void EveryPrismRefusalReplaysItsCapacityWithoutTouchingCallerStorage(string kind, MovementAvailability status)
    {
        var doc = kind == "candidate-capacity" ? TileWaterIslands.Create() : GrazingWaterBank.Document();
        var query = GrazingWaterBank.Lift(110.5f);
        Vector3 origin = default;
        if (kind == "candidate-capacity") query = new(new(new(32, -1, 32), 128, 128,
            MovementWaterFieldTests.Space, null), Vector3.Zero);
        if (kind == "precision")
        {
            doc = TileWorldPhysicsTestData.FlatWorld(RegionCoord.Of(1000000, -1));
            doc.SetUnderlay(1000000, -1, 0, TileWorldPhysicsTestData.Water);
            doc.SetCornerHeightCm(1000000, -1, 0, -200);
            doc.SetCornerHeightCm(1000001, -1, 0, 2);
            query = new(new(new(1000000.25f, 1, .5f), .1f, .25f,
                MovementWaterFieldTests.Space, null), new(.2f, -4, 0));
        }
        if (kind == "unresolved")
        {
            const int tile = 33554434;
            doc = new TileWorldDocument { TileSize = .125f };
            doc.GetOrCreateRegion(RegionCoord.Of(tile, 0));
            doc.SetUnderlay(tile, 0, 0, TileWorldPhysicsTestData.Water);
            doc.SetCornerHeightCm(tile, 0, 0, -400);
            doc.SetCornerHeightCm(tile + 1, 0, 0, 2);
            origin = new(4194304, 0, 0);
            query = new(new(new(0, -1, -.0625f), .03125f, .75f,
                MovementWaterFieldTests.Space, null), Vector3.Zero);
        }
        var field = MovementWaterFieldTests.Field(doc);
        if (kind.StartsWith("short-", StringComparison.Ordinal))
            Assert.Equal(MovementAvailability.Known, WaterTraceMemoComparison.Check(field, query).Result.Availability);
        int spans = kind == "short-spans" ? 0 : 64;
        int contacts = kind == "short-contacts" ? 0 : 256;
        var cold = WaterTraceMemoComparison.Check(field, query, origin, spans, contacts);
        Assert.Equal(status, cold.Result.Availability);
        Assert.Equal(0, cold.Result.SpansWritten);
        Assert.Equal(0, cold.Result.ContactsWritten);
        WaterTraceMemoComparison.Memo(cold, hit: false);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, query, origin, spans, contacts), hit: true);
    }

    [Fact]
    public void OldestInsertionIsEvictedWithoutRefreshingItsAgeOnARead()
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var first = Query(0);
        WaterTraceMemoComparison.Check(field, first);
        for (int i = 1; i < TileWaterTraceMemo.EntryLimit; i++)
            WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, Query(i)), hit: false);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, first), hit: true);
        WaterTraceMemoComparison.Check(field, Query(TileWaterTraceMemo.EntryLimit));
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, first), hit: false);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LargeOutputPrefixesAreNotRetained(bool spans)
    {
        // The direct table boundary also covers future geometry that can emit many contacts in
        // one interval. All copied records come from a real sampler trace.
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var query = GrazingWaterBank.Lift(110.5f);
        var actual = WaterTraceMemoComparison.Read(field, query, bypass: true);
        Assert.Equal(MovementAvailability.Known, actual.Result.Availability);
        var table = new TileWaterTraceMemo.Table();
        var key = new TileWaterTraceMemo.Key(query, Vector3.Zero, 64, 256, 0);
        int spanCount = spans ? TileWaterTraceMemo.SpanLimit : 1;
        int contactCount = spans ? 1 : TileWaterTraceMemo.ContactLimit;
        var outputSpans = Enumerable.Repeat(actual.Spans[0], spanCount + 1).ToArray();
        var outputContacts = Enumerable.Repeat(actual.Contacts[0], contactCount + 1).ToArray();
        var result = new MovementCoverageResult(MovementAvailability.Known, spanCount, spanCount,
            contactCount, contactCount, actual.Result.CertifiedErrorMetres, MovementWaterFieldTests.Identity);
        table.Store(key, result, outputSpans, outputContacts);
        Assert.True(table.TryRead(key, key.Space, MovementWaterFieldTests.Identity, outputSpans, outputContacts, out _));
        table.Clear();
        spanCount += spans ? 1 : 0;
        contactCount += spans ? 0 : 1;
        result = new(MovementAvailability.Known, spanCount, spanCount, contactCount, contactCount,
            actual.Result.CertifiedErrorMetres, MovementWaterFieldTests.Identity);
        table.Store(key, result, outputSpans, outputContacts);
        Assert.False(table.TryRead(key, key.Space, MovementWaterFieldTests.Identity, outputSpans, outputContacts, out _));
    }

    [Fact]
    public void InterleavedWorldsAndDirtyScratchCannotReplaceOwnedPrefixes()
    {
        var doc = GrazingWaterBank.Document();
        var field = MovementWaterFieldTests.Field(doc);
        var query = GrazingWaterBank.Lift(110.5f);
        WaterTraceMemoComparison.Check(field, query);
        doc.SetCornerHeightCm(110, 237, 0, -125);
        var rebuilt = MovementWaterFieldTests.Field(doc);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(rebuilt, query), hit: false);
        WaterTraceMemoComparison.Check(rebuilt, Query(31));
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, query), hit: true);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(rebuilt, query), hit: true);
        field.ResetTraceMemoForTests();
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, query), hit: false);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(rebuilt, query), hit: true);
    }

    [Fact]
    public void IndependentThreadTablesAreBoundedAndExcessThreadsRunUncached()
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        int count = TileWaterTraceMemo.ThreadLimit + 2;
        var results = new WaterTraceMemoComparison.Observation?[count];
        var errors = new Exception?[count];
        using var ready = new Barrier(count);
        var threads = Enumerable.Range(0, count).Select(i => new Thread(() =>
        {
            try
            {
                WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, Query(i)), hit: false);
                ready.SignalAndWait();
                results[i] = WaterTraceMemoComparison.Check(field, Query(i));
            }
            catch (Exception error) { errors[i] = error; ready.RemoveParticipant(); }
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();
        Assert.All(errors, error => Assert.Null(error));
        Assert.All(results, result => Assert.NotNull(result));
        int retained = TileWaterFloatingPointState.TryRead(out _) ? TileWaterTraceMemo.ThreadLimit : 0;
        Assert.Equal(retained, results.Sum(r => r!.Hits));
        Assert.Equal(count - retained, results.Sum(r => r!.Misses));
    }

    [Fact]
    public void OversizedCallerSpaceIdentifiersRunCorrectlyWithoutRetention()
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var query = GrazingWaterBank.Lift(110.5f);
        var space = new MovementSpaceKey(query.Body.CurrentSpace.WorldId,
            new string('s', TileWaterTraceMemo.SpaceIdCharacterLimit + 1));
        query = new(new(query.Body.Centre, query.Body.Radius, query.Body.HalfHeight, space, null), query.Delta);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, query), hit: false);
        WaterTraceMemoComparison.Memo(WaterTraceMemoComparison.Check(field, query), hit: false);
    }

    internal static MovementMediumSweepQuery Query(int i)
    {
        var query = GrazingWaterBank.Lift(110.5f);
        return new(new(query.Body.Centre + new Vector3(0, i * .00001f, 0), query.Body.Radius,
            query.Body.HalfHeight, query.Body.CurrentSpace, null), query.Delta);
    }
}
