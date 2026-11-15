using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Trait("Category", "WaterKernelContract")]
public sealed class TileWaterTraceMemoResultTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EightOwnedResultsReplayEveryBitAndTheNinthEvictsTheOldestInsertion(bool flat)
    {
        var field = Field(flat);
        var query = Query();
        var cold = WaterTraceMemoComparison.Check(field, query);
        Assert.True(Work(cold) > 0);
        if (!flat) Assert.True(cold.Result.ContactsWritten > TileWaterTraceMemo.ContactLimit);
        for (int i = 1; i < 8; i++) Assert.True(Work(Read(i)) > 0);
        Assert.Equal(0, Work(Read(0)));
        Assert.True(Work(Read(8)) > 0);
        Assert.Equal(0, Work(Read(1))); // A hit does not renew insertion age.
        Assert.True(Work(Read(0)) > 0);
        WaterTraceMemoComparison.Observation Read(int i) => WaterTraceMemoComparison.Check(field, Query(i));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapacitiesOriginAndSignedZeroAreExactKeysAndValidationStillPrecedesReplay(bool flat)
    {
        var field = Field(flat);
        var q = Query();
        WaterTraceMemoComparison.Check(field, q);
        var id = new MovementQueryIdentity(new("fresh".ToCharArray()), 9, new("scope".ToCharArray()));
        var space = new MovementSpaceKey(new(q.Body.CurrentSpace.WorldId.ToCharArray()), new("ordinary".ToCharArray()));
        q = new(new(q.Body.Centre, q.Body.Radius, q.Body.HalfHeight, space, null), q.Delta);
        var warm = WaterTraceMemoComparison.Check(field, q, identity: id);
        Assert.Equal(0, Work(warm));
        Assert.Same(id.ClosureId, warm.Result.Identity.ClosureId);
        Assert.Same(id.ScopeDigest, warm.Result.Identity.ScopeDigest);
        foreach (var capacities in new[] { (64, 257), (65, 256), (0, 0), (64, 0), (0, 256) })
        {
            var cold = WaterTraceMemoComparison.Check(field, q, spanCapacity: capacities.Item1, contactCapacity: capacities.Item2);
            Assert.True(Work(cold) > 0);
            Assert.Equal(0, Work(WaterTraceMemoComparison.Check(field, q,
                spanCapacity: capacities.Item1, contactCapacity: capacities.Item2)));
        }
        foreach (var origin in new[] { new Vector3(0, .0001f, 0), new Vector3(BitConverter.Int32BitsToSingle(int.MinValue), 0, 0) })
        {
            Assert.True(Work(WaterTraceMemoComparison.Check(field, q, origin)) > 0);
            Assert.Equal(0, Work(WaterTraceMemoComparison.Check(field, q, origin)));
        }
        Assert.Equal(MovementAvailability.Invalid, WaterTraceMemoComparison.Check(field, q, identity: default(MovementQueryIdentity)).Result.Availability);
        Assert.Equal(MovementAvailability.Invalid, WaterTraceMemoComparison.Check(field, q, new(float.NaN, 0, 0)).Result.Availability);
        var invalidSupport = new MovementMediumSweepQuery(new(q.Body.Centre, q.Body.Radius, q.Body.HalfHeight,
            space, new("foreign-world", "support")), q.Delta);
        Assert.Equal(MovementAvailability.Invalid, WaterTraceMemoComparison.Check(field, invalidSupport).Result.Availability);
        field.ResetTraceMemoForTests();
        Assert.True(Work(WaterTraceMemoComparison.Check(field, q)) > 0);
    }

    [Fact]
    public void LargeAndFlatOwnersCannotContaminateEachOtherAndExcessIdentifiersBypassRetention()
    {
        var a = Field(false);
        var b = Field(true);
        foreach (var field in new[] { a, b })
        {
            Assert.True(Work(WaterTraceMemoComparison.Check(field, Query())) > 0);
            Assert.Equal(0, Work(WaterTraceMemoComparison.Check(field, Query())));
        }
        foreach (var field in new[] { b, a }) Assert.Equal(0, Work(WaterTraceMemoComparison.Check(field, Query())));
        var q = Query();
        q = new(new(q.Body.Centre, q.Body.Radius, q.Body.HalfHeight,
            new(q.Body.CurrentSpace.WorldId, new string('s', 257)), null), q.Delta);
        Assert.True(Work(WaterTraceMemoComparison.Check(a, q)) > 0);
        Assert.True(Work(WaterTraceMemoComparison.Check(a, q)) > 0);
    }

    internal static TileMediumSampler Field(bool flat)
    {
        var geometry = WaterKernelTraceFixture.RepeatedFaces(64, wet: true);
        if (flat)
        {
            var columns = geometry.Nonuniform;
            geometry = geometry with { Columns = columns, Nonuniform = [],
                Boundaries = TileWaterBoundary.Build(columns, true), Exterior = TileWaterBoundary.Build(columns, false) };
        }
        return TileMediumSampler.FromTraceForTests(geometry, "water-world");
    }

    internal static MovementMediumSweepQuery Query(int i = 0)
    {
        var q = WaterKernelTraceFixture.Query();
        return new(new(q.Body.Centre + new Vector3(0, i * .0001f, 0), q.Body.Radius, q.Body.HalfHeight,
            q.Body.CurrentSpace, null), q.Delta);
    }

    internal static int Work(WaterTraceMemoComparison.Observation read) => read.Solves + read.FlatSolves;
}
