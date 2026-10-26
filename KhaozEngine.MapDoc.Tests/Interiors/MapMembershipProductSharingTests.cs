using System;
using System.Reflection;
using System.Runtime.InteropServices;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipProductSharingTests
{
    [Fact]
    public void FrozenBoundRows_HaveAThirtyTwoByteLayout()
    {
        var type = typeof(MapRoofEvaluationBuffer).Assembly.GetType("KhaozEngine.MapDoc.Spaces.MapMembershipFrozenBoundStore+Row");
        Assert.NotNull(type);
        Assert.Equal(32, Marshal.SizeOf(type));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void FootprintIdentity_RequiresEveryGeometryInputRatherThanEqualCounts(int changed)
    {
        var type = typeof(MapRoofEvaluationBuffer).Assembly.GetType("KhaozEngine.MapDoc.Spaces.MapMembershipFootprintIdentity");
        Assert.NotNull(type);
        object source = new();
        object?[] Values() => [source, "closure", "generation", "record", "record-version", "lattice-version",
            new MapPatchKey("floor", 0, 0), MapLatticeFrame.ImportedMetreCentimetre, "instance/floor", null, new[] { 0 }, 1, 1925];
        object Create(object?[] values) => Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, values, null)!;
        var a = Values(); var b = Values();
        Assert.Equal(Create(a), Create(b));
        b[changed] = changed switch
        {
            0 => new object(),
            1 => "another-closure",
            2 => "another-generation",
            3 => "another-record",
            4 => "another-record-version",
            5 => "another-lattice-version",
            6 => new MapPatchKey("floor", 1, 0),
            7 => new MapLatticeFrame(new(2, 1), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0),
            8 => "another-instance/floor",
            9 => new MapEffectiveTransform(.25, 0, 0, 0, 1),
            10 => new[] { 1 },
            11 => 2,
            12 => 1924,
            _ => throw new ArgumentOutOfRangeException(nameof(changed))
        };
        Assert.NotEqual(Create(a), Create(b));
    }

    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(0, 2, 1)]
    [InlineData(1, 0, 2)]
    [InlineData(1, 2, 0)]
    [InlineData(2, 0, 1)]
    [InlineData(2, 1, 0)]
    public void MatchingBuffers_SharePhysicalFootprintPagesAndRetireInAnyOrder(int a, int b, int c)
    {
        using var native = MapMembershipIntervalFixtures.Create("plain");
        var scene = NativeSceneFixtures.Open(native, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var admission = new MapSceneAdmission(new());
        var first = MapRoofEvaluationBuffer.Prepare(scene, admission).Buffer!;
        var second = MapRoofEvaluationBuffer.Prepare(scene, admission).Buffer!;
        var third = MapRoofEvaluationBuffer.Prepare(scene, admission).Buffer!;
        var buffers = new[] { first, second, third };
        try
        {
            Assert.True(admission.RetainedBytes < first.RetainedBytes + second.RetainedBytes + third.RetainedBytes,
                "Matching footprint geometry must have one physical root receipt across three buffers.");
            var inside = new MapFramePoint(WorldFrame.Origin, new(.25f, 1, .25f));
            var outside = new MapFramePoint(WorldFrame.Origin, new(-1, 1, -1));
            first.Evaluate(inside, MapRoofMode.Auto);
            second.Evaluate(outside, MapRoofMode.Auto);
            third.Evaluate(inside, MapRoofMode.Auto);
            Assert.Equal(MapMembershipStatus.Resolved, first.Status);
            Assert.Equal(MapMembershipStatus.Outside, second.Status);
            Assert.Equal("world", third.SpaceId);
            Assert.Equal(3 * (MapMembershipCompactInventory.Retained - MapMembershipCompactInventory.Shared) +
                MapMembershipCompactInventory.Shared + MapMembershipCompactInventory.Cache, admission.RetainedBytes);
            Assert.Equal(0, first.Counts.FootprintCacheHits);
            Assert.Equal(1, second.Counts.FootprintCacheHits);
            Assert.Equal(1, third.Counts.FootprintCacheHits);
            buffers[a].PreparationOwnership!.Dispose();
            buffers[b].PreparationOwnership!.Dispose();
            buffers[c].Evaluate(inside, MapRoofMode.Auto);
            Assert.Equal("world", buffers[c].SpaceId);
            Assert.Equal(MapMembershipCompactInventory.Root, admission.RetainedBytes);
        }
        finally
        {
            first.PreparationOwnership!.Dispose(); second.PreparationOwnership!.Dispose(); third.PreparationOwnership!.Dispose();
        }
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    [Fact]
    public void TwoWindows_ChargeTheWholeSharedObligationOnceRegardlessOfBufferCount()
    {
        using var native = MapMembershipIntervalFixtures.Create("plain");
        var scene = NativeSceneFixtures.Open(native, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var admission = new MapSceneAdmission(new());
        MapRoofEvaluationBuffer first;
        using (admission.LimitPreparation(1000000, "First")) first = MapRoofEvaluationBuffer.Prepare(scene, admission).Buffer!;
        using var secondWindow = admission.LimitPreparation(1000000, "Second");
        var windowIdentity = Assert.IsType<MapPreparationLedger.Window>(admission.WindowIdentity);
        var second = MapRoofEvaluationBuffer.Prepare(scene, admission).Buffer!;
        var third = MapRoofEvaluationBuffer.Prepare(scene, admission).Buffer!;
        try
        {
            long privateBytes = MapMembershipCompactInventory.Retained - MapMembershipCompactInventory.Shared;
            Assert.Equal(2 * privateBytes + MapMembershipCompactInventory.Shared, windowIdentity.Total);
            Assert.Equal(3 * privateBytes + MapMembershipCompactInventory.Footprint + 2 * 512 +
                MapMembershipCompactInventory.Cache, admission.RetainedBytes);
            first.PreparationOwnership!.Dispose();
            Assert.Equal(2 * privateBytes + MapMembershipCompactInventory.Shared, windowIdentity.Total);
            second.PreparationOwnership!.Dispose();
            Assert.Equal(MapMembershipCompactInventory.Retained, windowIdentity.Total);
        }
        finally
        {
            first.PreparationOwnership!.Dispose(); second.PreparationOwnership!.Dispose(); third.PreparationOwnership!.Dispose();
        }
        Assert.Equal(0, windowIdentity.Total);
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void SharedGeometry_PreservesPrivateScopeFailuresAndBoundRows()
    {
        using var native = MapMembershipIntervalFixtures.Create("prism");
        var source = NativeSceneFixtures.Open(native, false);
        var scope = MapMembershipIntervalTests.Scope();
        var complete = source.Acquire(scope).Scene!;
        var floorOnly = source.Acquire(scope with { Surfaces = scope.Surfaces with { MaxY = .5f } }).Scene!;
        var admission = new MapSceneAdmission(new());
        var first = MapRoofEvaluationBuffer.Prepare(complete, admission).Buffer!;
        var second = MapRoofEvaluationBuffer.Prepare(floorOnly, admission).Buffer!;
        try
        {
            var point = new MapFramePoint(WorldFrame.Origin, new(.25f, 1, .25f));
            first.Evaluate(point, MapRoofMode.Auto); second.Evaluate(point, MapRoofMode.Auto);
            Assert.Equal(MapMembershipStatus.Resolved, first.Status);
            Assert.Equal(MapMembershipStatus.MissingGeometry, second.Status);
            Assert.Equal(1, second.Counts.FootprintCacheHits);
            Assert.NotSame(View(first).Geometry.Bounds, View(second).Geometry.Bounds);
        }
        finally { first.PreparationOwnership!.Dispose(); second.PreparationOwnership!.Dispose(); }
        Assert.Equal(0, admission.RetainedBytes);
        static MapMembershipPreparedView View(MapRoofEvaluationBuffer buffer)
        {
            var scratch = typeof(MapRoofEvaluationBuffer).GetField("_scratch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(buffer)!;
            return (MapMembershipPreparedView)typeof(MapSpaceMembershipScratch).GetField("_prepared", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scratch)!;
        }
    }

    [Fact]
    public void EqualCountsAcrossSourceGenerations_DoNotShareProducts()
    {
        using var a = MapMembershipIntervalFixtures.Create("plain");
        using var b = MapMembershipIntervalFixtures.Create("plain");
        var firstScene = NativeSceneFixtures.Open(a, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var secondScene = NativeSceneFixtures.Open(b, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var admission = new MapSceneAdmission(new());
        var first = MapRoofEvaluationBuffer.Prepare(firstScene, admission).Buffer!;
        var second = MapRoofEvaluationBuffer.Prepare(secondScene, admission).Buffer!;
        try { Assert.True(admission.RetainedBytes >= first.RetainedBytes + second.RetainedBytes); }
        finally { first.PreparationOwnership!.Dispose(); second.PreparationOwnership!.Dispose(); }
        Assert.Equal(0, admission.RetainedBytes);
    }
}
