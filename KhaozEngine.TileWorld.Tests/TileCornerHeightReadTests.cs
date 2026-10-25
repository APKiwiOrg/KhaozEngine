using System;
using System.IO;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileCornerHeightReadTests
{
    [Fact]
    public void KnownUnloadedOwnerDoesNotUseFallback()
    {
        using var temp = new TempDir();
        var owner = new RegionCoord(1, 0);
        var west = new RegionCoord(0, 0);
        TileWorldDocument saved = TileWorldTestData.FlatWorld(4, owner, west);
        saved.SetCornerHeightCm(63, 10, 0, 125);
        saved.SetCornerHeightCm(64, 10, 0, 975);
        string path = temp.Sub("strict-height");
        TileWorldFile.Save(saved, path);
        TileWorldSource source = TileWorldSource.Open(path);
        source.EnsureLoaded(west);
        Directory.Delete(path, recursive: true);
        Span<RegionCoord> inspected = stackalloc RegionCoord[4];

        Assert.False(source.Document.TryReadCornerHeight(64, 10, 0, inspected, out int count,
            out TileCornerHeightRead value, out RegionCoord missing));

        Assert.Equal(owner, missing);
        Assert.Equal(default, value);
        Assert.Equal(new[] { owner }, inspected[..count].ToArray());
        Assert.Null(source.Document.GetRegion(owner));
        Assert.Equal(1.25f, source.Document.CornerHeight(64, 10, 0));
    }

    [Theory]
    [InlineData(0, 1, 2, 1.25f)]
    [InlineData(1, 0, 3, 2.5f)]
    [InlineData(0, 0, 4, 3.75f)]
    public void EdgeFallbackRecordsInspectedRegions(int rx, int rz, int count, float height)
    {
        var owner = new RegionCoord(rx, rz);
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, owner);
        doc.SetCornerHeightCm(rx == 0 ? 63 : 64, rz == 0 ? 63 : 64, 0, (short)(height * 100));
        Span<RegionCoord> inspected = stackalloc RegionCoord[4];

        Assert.True(doc.TryReadCornerHeight(64, 64, 0, inspected, out int actualCount,
            out TileCornerHeightRead value, out _));

        Assert.Equal(count, actualCount);
        RegionCoord[] priority = { new(1, 1), new(0, 1), new(1, 0), new(0, 0) };
        Assert.Equal(priority.AsSpan(0, count).ToArray(), inspected[..actualCount].ToArray());
        Assert.Equal(new TileCornerHeightRead(height, owner, 0), value);
        Assert.Equal(height, doc.CornerHeight(64, 64, 0));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void EarlierUnloadedFallbackBlocksLoadedSouthwest(int rx, int rz)
    {
        var missingOwner = new RegionCoord(rx, rz);
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.UnloadedRegionHashes[missingOwner] = "known-but-not-loaded";
        doc.SetCornerHeightCm(63, 63, 0, 400);
        Span<RegionCoord> inspected = stackalloc RegionCoord[4];

        Assert.False(doc.TryReadCornerHeight(64, 64, 0, inspected, out int count, out _, out RegionCoord missing));

        Assert.Equal(missingOwner, missing);
        Assert.Equal(rx == 0 ? 2 : 3, count);
        Assert.Equal(missingOwner, inspected[count - 1]);
        Assert.Equal(4f, doc.CornerHeight(64, 64, 0));
    }

    [Fact]
    public void HigherPriorityLoadedOwnerWins()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4,
            new RegionCoord(1, 1), new RegionCoord(0, 1), new RegionCoord(1, 0), new RegionCoord(0, 0));
        doc.SetCornerHeightCm(64, 64, 0, 125);
        doc.SetCornerHeightCm(63, 64, 0, 250);
        doc.SetCornerHeightCm(64, 63, 0, 375);
        doc.SetCornerHeightCm(63, 63, 0, 500);
        Span<RegionCoord> inspected = stackalloc RegionCoord[4];

        Assert.True(doc.TryReadCornerHeight(64, 64, 0, inspected, out int count, out var value, out _));

        Assert.Equal(1, count);
        Assert.Equal(new TileCornerHeightRead(1.25f, new RegionCoord(1, 1), 0), value);
        doc.RemoveRegion(new RegionCoord(1, 1));
        Assert.True(doc.TryReadCornerHeight(64, 64, 0, inspected, out count, out value, out _));
        Assert.Equal(2, count);
        Assert.Equal(new TileCornerHeightRead(2.5f, new RegionCoord(0, 1), 0), value);
    }

    [Theory]
    [InlineData(10, 10, 1)]
    [InlineData(64, 64, 4)]
    public void NoOwnerIsUnavailableRatherThanLiftedZero(int x, int z, int expectedCount)
    {
        var doc = new TileWorldDocument();
        Span<RegionCoord> inspected = stackalloc RegionCoord[4];

        Assert.False(doc.TryReadCornerHeight(x, z, 2, inspected, out int count, out var value, out var missing));

        Assert.Equal(expectedCount, count);
        Assert.Equal(RegionCoord.Of(x, z), missing);
        Assert.Equal(default, value);
        Assert.Equal(6f, doc.CornerHeight(x, z, 2));
    }

    [Theory]
    [InlineData(0, 0f)]
    [InlineData(2, 6f)]
    public void ImplicitFlatRegionIsAvailable(int plane, float expected)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(-1, -1));
        Span<RegionCoord> inspected = stackalloc RegionCoord[4];

        Assert.True(doc.TryReadCornerHeight(-1, -64, plane, inspected, out int count, out var value, out _));

        Assert.Equal(1, count);
        Assert.Equal(new TileCornerHeightRead(expected, new RegionCoord(-1, -1), 0), value);
        Assert.Null(doc.GetRegion(new RegionCoord(-1, -1))!.Plane(plane).Heights);
    }

    [Theory]
    [InlineData(3f, 32760, 327.67f)]
    [InlineData(-3f, -32760, -327.68f)]
    public void DerivedPlaneReportsBaseSourceAndPreservesSaturation(float lift, short baseCm, float expected)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.PlaneHeight = lift;
        doc.SetCornerHeightCm(10, 10, 0, baseCm);
        Span<RegionCoord> inspected = stackalloc RegionCoord[4];

        Assert.True(doc.TryReadCornerHeight(10, 10, 1, inspected, out _, out var value, out _));
        Assert.Equal(0, value.SourcePlane);
        Assert.Equal(expected, value.Height, 4);
        Assert.Equal(doc.CornerHeight(10, 10, 1), value.Height);

        doc.SetCornerHeightCm(10, 10, 1, 123);
        Assert.True(doc.TryReadCornerHeight(10, 10, 1, inspected, out _, out value, out _));
        Assert.Equal(1, value.SourcePlane);
        Assert.Equal(1.23f, value.Height);
    }

    [Fact]
    public void AuthoredPlaneMatchingItsDerivedLiftStillReportsRequestedSource()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetCornerHeightCm(10, 10, 1, 300);
        Span<RegionCoord> inspected = stackalloc RegionCoord[4];

        Assert.True(doc.TryReadCornerHeight(10, 10, 1, inspected, out _, out var value, out _));

        Assert.Equal(3f, value.Height);
        Assert.Equal(1, value.SourcePlane);
    }

    [Fact]
    public void StrictFallbackDoesNotWrapAcrossTheCoordinateDomain()
    {
        var doc = new TileWorldDocument();
        doc.GetOrCreateRegion(RegionCoord.Of(int.MaxValue, 10));
        doc.SetCornerHeightCm(int.MaxValue, 10, 0, 900);
        Span<RegionCoord> inspected = stackalloc RegionCoord[4];

        Assert.False(doc.TryReadCornerHeight(int.MinValue, 10, 0, inspected, out int count,
            out _, out RegionCoord missing));

        Assert.Equal(new RegionCoord(-33554432, 0), missing);
        Assert.Equal(new[] { new RegionCoord(-33554432, 0) }, inspected[..count].ToArray());
        // Preserve the legacy overflow behavior rather than changing existing callers in this API addition.
        Assert.Equal(9f, doc.CornerHeight(int.MinValue, 10, 0));
    }

    [Fact]
    public void InspectedSpanRequiresCapacityFour()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        Assert.Throws<ArgumentException>(() => doc.TryReadCornerHeight(0, 0, 0,
            new RegionCoord[3], out _, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.TryReadCornerHeight(0, 0, 4,
            new RegionCoord[4], out _, out _, out _));
    }
}
