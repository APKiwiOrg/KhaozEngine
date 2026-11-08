using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterFieldEquivalenceTests
{
    [Theory]
    [InlineData(false, 1728)]
    [InlineData(true, 1729)]
    public void SeededCapturedFlatAndNonuniformBanksMatchTheOriginalPathSelection(bool nonuniform, int seed)
    {
        var doc = TileWaterIslands.Create();
        if (nonuniform)
            for (int z = -61; z < 0; z += 4)
                for (int x = 1; x < 64; x += 4) doc.SetCornerHeightCm(x, z, 0, -200);
        var field = new TileMediumSampler(doc, TileWorldPhysicsTestData.Catalogs(),
            new[] { new KhaozEngine.TileWorld.RegionCoord(0, -1) }, "water-world");
        var reference = field.CaptureTraceForTests();
        Assert.NotEmpty(reference.Nonuniform);
        var random = new Random(seed);
        var expectedSpans = new MovementCoverageSpan[64];
        var actualSpans = new MovementCoverageSpan[64];
        var expectedContacts = new MovementDomainContact[256];
        var actualContacts = new MovementDomainContact[256];
        for (int i = 0; i < 256; i++)
        {
            float x = i < 128 ? i % 16 * 4 + 1.5f : random.NextSingle() * 68 - 2;
            float z = i < 128 ? i / 16 * 4 + .5f : random.NextSingle() * 68 - 2;
            var query = new MovementMediumSweepQuery(new(new(x, -1, z), .25f, .75f,
                MovementWaterFieldTests.Space, null), i % 5 == 0 ? Vector3.Zero :
                new(random.NextSingle() * 3 - 1.5f, random.NextSingle() * 2 - 1, random.NextSingle() * 3 - 1.5f));
            int spanCapacity = i % 31 == 0 ? 0 : 64;
            int contactCapacity = i % 29 == 0 ? 0 : 256;
            var expected = WaterTraceReference.Trace(reference, query, Vector3.Zero, MovementWaterFieldTests.Identity,
                expectedSpans.AsSpan(0, spanCapacity), expectedContacts.AsSpan(0, contactCapacity));
            var actual = field.TraceWater(query, Vector3.Zero, MovementWaterFieldTests.Identity,
                actualSpans.AsSpan(0, spanCapacity), actualContacts.AsSpan(0, contactCapacity));
            WaterCoverageComparison.Equal(expected, actual, expectedSpans, actualSpans, expectedContacts, actualContacts);
        }
    }
}
