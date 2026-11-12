using System;
using System.Linq;
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

    [Fact]
    public void KnownZeroContactReplayPreservesItsSpanBitsAndCallerBuffers()
    {
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var query = Query(MovementWaterFieldTests.Space, dry: true);
        var cold = WaterTraceMemoComparison.Check(field, query);
        Assert.Equal(MovementAvailability.Known, cold.Result.Availability);
        Assert.True(cold.Result.SpansWritten > 0);
        Assert.Equal(0, cold.Result.ContactsWritten);
        WaterTraceMemoComparison.Memo(cold, hit: false);
        var warm = WaterTraceMemoComparison.Check(field, query);
        WaterTraceMemoComparison.Memo(warm, hit: true);
        Assert.Equal(MovementAvailability.Known, warm.Result.Availability);
        Assert.Equal(0, warm.Result.ContactsWritten);
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
        if (TileMediumSampler.TryReadTraceControls(out _)) Assert.NotNull(table);
        return (new(field), table);
    }

    static string Copy(string value) => new(value.ToCharArray());

    [Fact]
    public void AnExposedJoinedTriangleBoundaryReplaysItsStringValue()
    {
        // #1755. One triangle is wet, while its neighbour is entirely at or above the surface.
        // Their shared diagonal is a zero-height water boundary and has a freshly allocated id.
        var document = TileWorldPhysicsTestData.FlatWorld();
        document.SetUnderlay(0, 0, 0, TileWorldPhysicsTestData.Water);
        document.SetCornerHeightCm(0, 0, 0, -100);
        document.SetCornerHeightCm(1, 0, 0, 0);
        document.SetCornerHeightCm(0, 1, 0, 0);
        document.SetCornerHeightCm(1, 1, 0, 2);
        var field = MovementWaterFieldTests.Field(document);
        Assert.Single(field.CaptureTraceForTests().Prisms);
        var query = new MovementMediumSweepQuery(new(new(.5f, -.5f, -.5f), .125f, .75f,
            MovementWaterFieldTests.Space, null), Vector3.Zero);
        var expected = WaterTraceMemoComparison.Read(field, query, bypass: true);
        var cold = WaterTraceMemoComparison.Read(field, query);
        Assert.Equal(MovementAvailability.Known, cold.Result.Availability);
        var boundaries = cold.Contacts.Take(cold.Result.ContactsWritten)
            .Where(c => c.BoundaryId.EndsWith("/joined-triangle", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(boundaries);
        var warm = WaterTraceMemoComparison.Read(field, query);
        WaterTraceMemoComparison.Memo(warm, hit: true);
        WaterCoverageComparison.Equal(expected.Result, warm.Result, expected.Spans, warm.Spans,
            expected.Contacts, warm.Contacts);
        Assert.Equal(boundaries.Select(c => c.BoundaryId), warm.Contacts.Take(warm.Result.ContactsWritten)
            .Where(c => c.BoundaryId.EndsWith("/joined-triangle", StringComparison.Ordinal)).Select(c => c.BoundaryId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AControlsOnlyReaderEnablesTheMemoAndAnUnavailableReaderBypassesIt(bool available)
    {
        var previous = TileMediumSampler.ControlReaderForTests;
        TileMediumSampler.ControlReaderForTests = (out ulong token) => { token = 0x1234; return available; };
        try
        {
            var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
            var query = Query(MovementWaterFieldTests.Space);
            var cold = WaterTraceMemoComparison.Check(field, query);
            Assert.Equal(MovementAvailability.Known, cold.Result.Availability);
            Assert.Equal(0, cold.Hits);
            Assert.Equal(1, cold.Misses);
            var warm = WaterTraceMemoComparison.Check(field, query);
            Assert.Equal(available ? 1 : 0, warm.Hits);
            Assert.Equal(available ? 0 : 1, warm.Misses);
            if (available) Assert.Equal(0, warm.Solves);
            else Assert.True(warm.Solves > 0);
        }
        finally { TileMediumSampler.ControlReaderForTests = previous; }
    }
}
