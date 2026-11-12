using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
public class TileWaterTraceMemoObservationTests
{
    [Fact]
    public void EqualSpaceValuesReplayTheCurrentCallersStringReferences()
    {
        // #1755. Record value equality hides references borrowed from an earlier caller.
        using var original = WaterFloatingPointEnvironment.Capture();
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var a = new MovementSpaceKey(Copy("water-world"), Copy("ordinary"));
        var b = new MovementSpaceKey(Copy("water-world"), Copy("ordinary"));
        Assert.Equal(a, b);
        Assert.NotSame(a.WorldId, b.WorldId);
        Assert.NotSame(a.LocalId, b.LocalId);
        WaterTraceMemoComparison.Read(field, Query(a));
        var identity = new MovementQueryIdentity(Copy("closure"), 1, Copy("scope"));
        var actual = WaterTraceMemoComparison.Check(field, Query(b), identity: identity);
        WaterTraceMemoComparison.Memo(actual, hit: true);
        Assert.True(actual.Result.ContactsWritten > 0);
        Assert.Same(identity.ClosureId, actual.Result.Identity.ClosureId);
        Assert.Same(identity.ScopeDigest, actual.Result.Identity.ScopeDigest);
        var expected = WaterTraceMemoComparison.Read(field, Query(b), bypass: true);
        for (int i = 0; i < actual.Result.ContactsWritten; i++)
        {
            var cached = actual.Contacts[i];
            var fresh = expected.Contacts[i];
            Assert.Same(fresh.Domain.WorldId, cached.Domain.WorldId);
            Assert.Same(fresh.Domain.LocalId, cached.Domain.LocalId);
            Assert.Same(fresh.Interval.LowerBoundaryId, cached.Interval.LowerBoundaryId);
            Assert.Same(fresh.Interval.UpperBoundaryId, cached.Interval.UpperBoundaryId);
            if (!fresh.BoundaryId.EndsWith("/joined-triangle", StringComparison.Ordinal))
                Assert.Same(fresh.BoundaryId, cached.BoundaryId);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReplayPreservesOutgoingFlagsForWetAndZeroContactKnownResults(bool dry, bool preset)
    {
        // #1755. The expected computation must not pre-set flags for the measured cache lookup.
        using var original = WaterFloatingPointEnvironment.Capture();
        if (original is null) return;
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var query = Query(MovementWaterFieldTests.Space, dry);
        var prime = WaterTraceMemoComparison.Read(field, query);
        Assert.Equal(MovementAvailability.Known, prime.Result.Availability);
        Assert.Equal(dry, prime.Result.ContactsWritten == 0);
        if (!preset) Assert.True(original.ClearStatus());
        var start = WaterFloatingPointEnvironment.Capture();
        Assert.NotNull(start);
        Assert.True(WaterFloatingPointEnvironment.TryReadStatus(out ulong incoming));
        var expected = WaterTraceMemoComparison.Read(field, query, bypass: true, environment: start);
        var actual = WaterTraceMemoComparison.Read(field, query, environment: start);
        WaterCoverageComparison.Equal(expected.Result, actual.Result, expected.Spans, actual.Spans,
            expected.Contacts, actual.Contacts);
        Assert.True(expected.HasStatus && actual.HasStatus);
        if (!preset) Assert.NotEqual(incoming, expected.Status);
        else Assert.Equal(incoming, expected.Status);
        Assert.Equal(expected.Status, actual.Status);
        WaterTraceMemoComparison.Memo(actual, hit: preset);
        Assert.Equal(dry, actual.Result.ContactsWritten == 0);
    }

    internal static MovementMediumSweepQuery Query(MovementSpaceKey space, bool dry = false) =>
        new(new(new(110.5f, dry ? 10 : -.5f, -236.75f), .125f, .75f, space, null), Vector3.Zero);

    [Fact]
    public void DiscardedSamplerStorageIsCollectable()
    {
        var sampler = ReleaseSampler();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Assert.False(sampler.Sampler.IsAlive);
        if (sampler.Table is { } table) Assert.False(table.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (WeakReference Sampler, WeakReference? Table) ReleaseSampler()
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var result = WaterTraceMemoComparison.Read(field, Query(MovementWaterFieldTests.Space));
        Assert.Equal(MovementAvailability.Known, result.Result.Availability);
        var table = field.CaptureTraceMemoTableForTests();
        if (TileWaterFloatingPointState.TryRead(out _, out _)) Assert.NotNull(table);
        return (new(field), table);
    }

    static string Copy(string value) => new(value.ToCharArray());
}
