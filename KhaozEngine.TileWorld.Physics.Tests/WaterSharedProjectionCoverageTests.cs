using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

// #1755. Whole prism traces with shared parent projections keep every frozen trace bit, refuse sharing
// when the table is full or the bypass is set, allocate nothing once warm and retain no capture afterward.
[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed class WaterSharedProjectionCoverageTests(ITestOutputHelper output) : IDisposable
{
    // Sharing stays on here even in the corpus run that forces every other test onto the original path.
    readonly SharedProjectionBypass _sharing = new(false);

    public void Dispose() => _sharing.Dispose();

    [Theory]
    [InlineData(false, "shared")]
    [InlineData(true, "shared")]
    [InlineData(false, "bypassed")]
    [InlineData(true, "bypassed")]
    public void RealFacesShareParentProjectionsWithFrozenParity(bool sloped, string mode)
    {
        var geometry = WaterKernelTraceFixture.RealFaceTypes(sloped: sloped);
        var query = Moving();
        using var bypass = new SharedProjectionBypass(mode == "bypassed");
        Trace(geometry, query);
        var stats = TileWaterPrismCoverage.CaptureSharedProjectionsForTests();
        Assert.Equal(0, TileWaterPrismCoverage.HeldSharedProjectionOwnersForTests());
        if (mode == "shared")
        {
            Assert.True(stats.Lends > 0);
            Assert.True(stats.Copies > 0);
            Assert.Equal(0, stats.Bypassed);
        }
        else
        {
            Assert.Equal(0, stats.Lends);
            Assert.Equal(0, stats.Copies);
            Assert.True(stats.Bypassed > 0);
        }
        FrozenWaterComparison.CheckPrisms(geometry, query);
        output.WriteLine($"SHARED_TRACE sloped={sloped} {mode}: {stats}");
    }

    [Fact]
    public void ParentsBeyondTheTableFallBackWithFrozenParity()
    {
        var geometry = ManyParents(TileWaterSharedProjections.Slots + 4);
        var query = WaterKernelTraceFixture.Query();
        var result = Trace(geometry, query);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        var stats = TileWaterPrismCoverage.CaptureSharedProjectionsForTests();
        Assert.Equal(TileWaterSharedProjections.Slots, stats.Published + stats.Borrowed);
        Assert.Equal(4, stats.Exhausted);
        Assert.Equal(TileWaterSharedProjections.Slots, stats.Lends);
        Assert.Equal(4, stats.Misses);
        FrozenWaterComparison.CheckPrisms(geometry, query);
        output.WriteLine($"SHARED_TRACE exhausted: {stats}");
    }

    [Theory]
    [InlineData("shared")]
    [InlineData("exhausted")]
    [InlineData("bypassed")]
    public void WarmTracesAllocateNothingWhetherOrNotTheyShare(string mode)
    {
        // Surface faces only. A contact on a diagonal side face builds its joined-triangle boundary id
        // string, an allocation outside the projection path that these sharing rows do not measure.
        var geometry = ManyParents(mode == "exhausted" ? TileWaterSharedProjections.Slots + 4 : 2);
        var query = Moving();
        using var bypass = new SharedProjectionBypass(mode == "bypassed");
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        MovementCoverageResult result = default;
        Action trace = () => result = TileWaterPrismCoverage.Trace(geometry.Prisms, geometry.Faces, geometry.Ground,
            geometry.PointIndex, geometry.PrismStarts, geometry.FaceStarts, query, Vector3.Zero,
            MovementWaterFieldTests.Identity, spans, contacts);
        trace();
        var stats = TileWaterPrismCoverage.CaptureSharedProjectionsForTests();
        Assert.Equal(mode != "bypassed", stats.Copies > 0);
        Assert.Equal(mode == "exhausted", stats.Exhausted > 0);
        Assert.Equal(mode == "bypassed", stats.Bypassed > 0);
        long bytes = AllocationMeasurement.Measure($"warm {mode} shared projection trace", trace);
        Assert.Equal(0, bytes);
        Assert.Equal(stats, TileWaterPrismCoverage.CaptureSharedProjectionsForTests());
    }

    [Fact]
    public void ADiscardedCaptureIsCollectedAfterASharingTrace()
    {
        var references = TraceAndRelease();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        Assert.NotEmpty(references);
        Assert.All(references, reference => Assert.False(reference.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference[] TraceAndRelease()
    {
        var geometry = WaterKernelTraceFixture.RealFaceTypes(sloped: true);
        Trace(geometry, Moving());
        Assert.True(TileWaterPrismCoverage.CaptureSharedProjectionsForTests().Copies > 0);
        Assert.Equal(0, TileWaterPrismCoverage.HeldSharedProjectionOwnersForTests());
        return geometry.Prisms.Select(prism => new WeakReference(prism.Shape))
            .Concat(geometry.Faces.Select(face => new WeakReference(face.Shape))).ToArray();
    }

    // Moves the body from the first prism towards the lower second prism, crossing faces of both.
    static MovementMediumSweepQuery Moving() =>
        new(new(new(0.75f, -0.5f, -0.25f), 0.25f, 0.75f, MovementWaterFieldTests.Space, null), new(0.5f, 0.0625f, -0.125f));

    static MovementCoverageResult Trace(TileWaterTraceTestSnapshot geometry, MovementMediumSweepQuery query) =>
        TileWaterPrismCoverage.Trace(geometry.Prisms, geometry.Faces, geometry.Ground, geometry.PointIndex,
            geometry.PrismStarts, geometry.FaceStarts, query, Vector3.Zero, MovementWaterFieldTests.Identity,
            new MovementCoverageSpan[64], new MovementDomainContact[256]);

    // Distinct parents with one real surface face each, so every parent claims its own table slot.
    static TileWaterTraceTestSnapshot ManyParents(int count)
    {
        var pieces = Enumerable.Range(0, count).Select(_ => WaterKernelTraceFixture.Piece()).ToArray();
        var faces = pieces.Select((piece, i) => new TileWaterPrismFace(i, 4, true, true,
            new TileWaterConvexShape([.. piece.Planes, piece.Planes[4].Opposite], null, piece.Shape))).ToArray();
        return WaterKernelTraceFixture.Snapshot([pieces[0].Column], pieces, faces, WaterKernelTraceFixture.Ground());
    }
}
