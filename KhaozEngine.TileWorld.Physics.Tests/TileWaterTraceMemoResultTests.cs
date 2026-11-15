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
        var cold = Check(field, query);
        Assert.True(Work(cold) > 0);
        if (!flat) Assert.True(cold.Result.ContactsWritten > TileWaterTraceMemo.ContactLimit);
        for (int i = 1; i < 8; i++) Assert.True(Work(Read(i)) > 0);
        Assert.Equal(0, Work(Read(0)));
        Assert.True(Work(Read(8)) > 0);
        Assert.Equal(0, Work(Read(1))); // A hit does not renew insertion age.
        Assert.True(Work(Read(0)) > 0);
        WaterTraceMemoComparison.Observation Read(int i) => Check(field, Query(i));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapacitiesOriginAndSignedZeroAreExactKeysAndValidationStillPrecedesReplay(bool flat)
    {
        var field = Field(flat);
        var q = Query();
        Check(field, q);
        var id = new MovementQueryIdentity(new("fresh".ToCharArray()), 9, new("scope".ToCharArray()));
        var space = new MovementSpaceKey(new(q.Body.CurrentSpace.WorldId.ToCharArray()), new("ordinary".ToCharArray()));
        q = new(new(q.Body.Centre, q.Body.Radius, q.Body.HalfHeight, space, null), q.Delta);
        var warm = Check(field, q, identity: id);
        Assert.Equal(0, Work(warm));
        Assert.Same(id.ClosureId, warm.Result.Identity.ClosureId);
        Assert.Same(id.ScopeDigest, warm.Result.Identity.ScopeDigest);
        foreach (var capacities in new[] { (64, 257), (65, 256), (0, 0), (64, 0), (0, 256) })
        {
            var cold = Check(field, q, spanCapacity: capacities.Item1, contactCapacity: capacities.Item2);
            Assert.True(Work(cold) > 0);
            Assert.Equal(0, Work(Check(field, q,
                spanCapacity: capacities.Item1, contactCapacity: capacities.Item2)));
        }
        foreach (var origin in new[] { new Vector3(0, .0001f, 0), new Vector3(BitConverter.Int32BitsToSingle(int.MinValue), 0, 0) })
        {
            Assert.True(Work(Check(field, q, origin)) > 0);
            Assert.Equal(0, Work(Check(field, q, origin)));
        }
        Assert.Equal(MovementAvailability.Invalid, Check(field, q, identity: default(MovementQueryIdentity)).Result.Availability);
        Assert.Equal(MovementAvailability.Invalid, Check(field, q, new(float.NaN, 0, 0)).Result.Availability);
        Assert.Throws<ArgumentException>(() => new MovementBodyQuery(q.Body.Centre, q.Body.Radius, q.Body.HalfHeight,
            space, new("foreign-world", "support")));
        Assert.Equal(MovementAvailability.Invalid, Check(field, default).Result.Availability);
        field.ResetTraceMemoForTests();
        Assert.True(Work(Check(field, q)) > 0);
    }

    [Fact]
    public void LargeAndFlatOwnersCannotContaminateEachOtherAndExcessIdentifiersBypassRetention()
    {
        var a = Field(false);
        var b = Field(true);
        foreach (var field in new[] { a, b })
        {
            Assert.True(Work(Check(field, Query())) > 0);
            Assert.Equal(0, Work(Check(field, Query())));
        }
        foreach (var field in new[] { b, a }) Assert.Equal(0, Work(Check(field, Query())));
        var q = Query();
        q = new(new(q.Body.Centre, q.Body.Radius, q.Body.HalfHeight,
            new(q.Body.CurrentSpace.WorldId, new string('s', 257)), null), q.Delta);
        Assert.True(Work(Check(a, q)) > 0);
        Assert.True(Work(Check(a, q)) > 0);
    }

    [Fact]
    public void EmptyDryCapturesKeepTheCheapShortcutWithoutClaimingResultStorage()
    {
        var field = MovementWaterFieldTests.Field(TileWorldPhysicsTestData.FlatWorld());
        var q = Query();
        var first = Check(field, q);
        var second = Check(field, q);
        Assert.Equal(MovementAvailability.Known, second.Result.Availability);
        Assert.Equal(1, second.Result.SpansWritten);
        Assert.Equal(0, second.Result.ContactsWritten);
        Assert.Equal(0, Work(first));
        Assert.Equal(0, Work(second));
        Assert.Null(field.CaptureResultMemoTableForTests());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReentrantControlReadersAndThrowingCallbacksCannotCorruptOwnedResults(bool flat)
    {
        var field = Field(flat);
        var q = Query();
        Check(field, q);
        var previous = TileMediumSampler.ControlReaderForTests;
        bool entered = false;
        WaterTraceMemoComparison.Observation? nested = null;
        TileMediumSampler.ControlReaderForTests = (out ulong token) =>
        {
            if (!entered) { entered = true; nested = Check(field, Query(2)); }
            return TileWaterFloatingPointState.TryRead(out token);
        };
        try
        {
            Check(field, q);
            Assert.True(entered);
            Assert.NotNull(nested);
            Assert.Equal(MovementAvailability.Known, nested.Result.Availability);
            Assert.Equal(0, Work(Check(field, q)));
            TileMediumSampler.ControlReaderForTests = (out ulong token) => { token = 0; throw new InvalidOperationException(); };
            Assert.Throws<InvalidOperationException>(() => WaterTraceMemoComparison.Read(field, q));
        }
        finally { TileMediumSampler.ControlReaderForTests = previous; }
        Assert.Equal(0, Work(Check(field, q)));
        bool bypass = TileMediumSampler.BypassTraceMemoForTests;
        TileMediumSampler.BypassTraceMemoForTests = true;
        try { Assert.True(Work(WaterTraceMemoComparison.Read(field, q, bypass: true)) > 0); }
        finally { TileMediumSampler.BypassTraceMemoForTests = bypass; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullSpanResultsAndTheirEarlierRefusalsMatchFrozenEvaluationAfterReplay(bool oneMore)
    {
        var field = TileMediumSampler.FromTraceForTests(WaterKernelTraceFixture.SpanPressure(oneMore), "water-world");
        var query = new MovementMediumSweepQuery(new(new(.125f, -.5f, -.75f), .125f, .25f,
            MovementWaterFieldTests.Space, null), new(.5f, 0, 0));
        var cold = Check(field, query);
        Assert.Equal(oneMore ? MovementAvailability.CapacityExceeded : MovementAvailability.Known, cold.Result.Availability);
        Assert.Equal(oneMore ? 65 : 64, cold.Result.RequiredSpanCapacity);
        var warm = Check(field, query);
        Assert.Equal(0, Work(warm));
        Check(field, query, spanCapacity: 0, contactCapacity: 0);
        Assert.Equal(0, Work(Check(field, query, spanCapacity: 0, contactCapacity: 0)));
    }

    internal static WaterTraceMemoComparison.Observation Check(TileMediumSampler field, MovementMediumSweepQuery query,
        Vector3 origin = default, int spanCapacity = 64, int contactCapacity = 256, MovementQueryIdentity? identity = null) =>
        WaterTraceMemoComparison.Check(field, query, origin, spanCapacity, contactCapacity, identity,
            new FrozenWaterReference(field.CaptureTraceForTests()));

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
