using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterTraceMemoSweepTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("bank", 1755)]
    [InlineData("crease", 1761)]
    [InlineData("islands", 1729)]
    public void SeededSlopedBedQueriesKeepEveryBitWhenRepeatedAndShuffled(string scene, int seed)
    {
        var document = scene == "islands" ? TileWaterIslands.Create() : GrazingWaterBank.Document();
        if (scene == "crease") document.SetCornerHeightCm(110, 237, 0, -125);
        var field = MovementWaterFieldTests.Field(document);
        var random = new Random(seed);
        var queries = new MovementMediumSweepQuery[48];
        for (int i = 0; i < queries.Length; i++)
        {
            Vector3 centre = scene == "islands" ? new(Next(0, 7), Next(-3, 0), Next(0, 7)) :
                new(Next(109.8f, 111.2f), Next(-1.3f, .1f), Next(-238.1f, -235.9f));
            if (i == 0 && scene != "islands") centre = GrazingWaterBank.Lift(110.5f).Body.Centre;
            var delta = i % 3 == 0 ? Vector3.Zero : new(Next(-2, 2), Next(-.5f, .5f), Next(-2, 2));
            queries[i] = new(new(centre, i % 2 == 0 ? .125f : .3f, .75f, MovementWaterFieldTests.Space, null), delta);
        }
        int logical = 0, hits = 0;
        using var first = new WaterCoverageDigest();
        using var second = new WaterCoverageDigest();
        foreach (var query in queries)
        {
            var cold = WaterTraceMemoComparison.Check(field, query);
            var warm = WaterTraceMemoComparison.Check(field, query);
            first.Add(query, cold.Result, cold.Spans.AsSpan(0, cold.Result.SpansWritten), cold.Contacts.AsSpan(0, cold.Result.ContactsWritten));
            second.Add(query, warm.Result, warm.Spans.AsSpan(0, warm.Result.SpansWritten), warm.Contacts.AsSpan(0, warm.Result.ContactsWritten));
            logical += warm.Logical;
            hits += warm.Hits;
            if (cold.Logical != 0)
                WaterTraceMemoComparison.Memo(warm, cold.Result.SpansWritten <= TileWaterTraceMemo.SpanLimit &&
                    cold.Result.ContactsWritten <= TileWaterTraceMemo.ContactLimit);
        }
        Assert.True(logical > 0);
        if (TileWaterFloatingPointState.TryRead(out _)) Assert.True(hits > 0);
        Assert.Equal(first.Digest(), second.Digest());
        output.WriteLine($"scene={scene}, cases={queries.Length}, cold-and-warm-digest={first.Digest()}");
        random.Shuffle(queries);
        foreach (var query in queries) WaterTraceMemoComparison.Check(field, query);
        float Next(float min, float max) => min + (max - min) * random.NextSingle();
    }
}
