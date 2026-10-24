using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWaterOpticsTests
{
    static TileWorldDocument LoadedPatch(RegionCoord center = default)
    {
        var doc = new TileWorldDocument();
        for (int z = -1; z <= 1; z++)
            for (int x = -1; x <= 1; x++)
                doc.GetOrCreateRegion(center.Offset(x, z));
        return doc;
    }

    static float Sample(TileWaterOpticsBuild build, float worldX, float worldZ) =>
        build.Field.SampleDepth((worldX - build.Origin.X) / build.Size.X,
            (worldZ - build.Origin.Z) / build.Size.Y);

    [Fact]
    public void FieldMatchesAuthoredBilinearHeightAt()
    {
        TileWorldDocument doc = LoadedPatch();
        // A non-planar saddle. At its center bilinear height is 1 m, while either
        // triangle diagonal would give 0 m or 2 m. Optics promises authored bilinear heights.
        doc.SetCornerHeightCm(10, 10, 0, 0);
        doc.SetCornerHeightCm(11, 10, 0, 200);
        doc.SetCornerHeightCm(10, 11, 0, 200);
        doc.SetCornerHeightCm(11, 11, 0, 0);

        Assert.True(TileWaterOptics.TryBuild(doc, default, 0, 0.75f, 0f, out var build, out _));

        Assert.Equal(new Vector3(-1f, 0.75f, -65f), build.Origin);
        Assert.Equal(new Vector2(66f), build.Size);
        Assert.Equal(67, build.Field.Width);
        Assert.Equal(67, build.Field.Height);
        Assert.Equal(1f, doc.HeightAt(10.5f, -10.5f, 0), 5);
        Assert.Equal(-0.25f, Sample(build, 10.5f, -10.5f), 5);
        Assert.Equal(1.25f, doc.HeightAt(10.25f, -10.75f, 0), 5);
        Assert.Equal(-0.5f, Sample(build, 10.25f, -10.75f), 5);
        Assert.Equal(0.75f, Sample(build, 10f, -10f), 5);
        Assert.Equal(-1.25f, Sample(build, 11f, -10f), 5);
        Assert.All(build.Field.RestDepths.ToArray(), depth => Assert.True(float.IsFinite(depth)));
    }

    [Fact]
    public void NegativeWorldZUsesTheCorrectRows()
    {
        var region = new RegionCoord(-1, 2);
        TileWorldDocument doc = LoadedPatch(region);
        doc.SetCornerHeightCm(-65, 193, 0, 100);
        doc.SetCornerHeightCm(1, 127, 0, 500);
        doc.SetCornerHeightCm(-54, 138, 0, 300);

        Assert.True(TileWaterOptics.TryBuild(doc, region, 0, 10f, 0f, out var build, out _));

        Assert.Equal(new Vector3(-65f, 10f, -193f), build.Origin);
        Assert.Equal(new Vector2(66f), build.Size);
        Assert.Equal(9f, build.Field.SampleDepth(0f, 0f));
        Assert.Equal(5f, build.Field.SampleDepth(1f, 1f));
        Assert.Equal(7f, Sample(build, -54f, -138f), 4);
    }

    [Fact]
    public void PaddingRoundsOutwardThenAddsOneAuthoredCellHalo()
    {
        TileWorldDocument doc = LoadedPatch();
        doc.TileSize = 2f;

        Assert.True(TileWaterOptics.TryBuild(doc, default, 0, 6f, 0.25f, out var build, out _));

        Assert.Equal(new Vector3(-4f, 6f, -132f), build.Origin);
        Assert.Equal(new Vector2(136f), build.Size);
        Assert.Equal(69, build.Field.Width);
        Assert.Equal(69, build.Field.Height);
        Assert.Equal(6f, Sample(build, 64f, -64f));
    }

    [Fact]
    public void SameLevelRowRunsShareOneBuildDescription()
    {
        TileWorldDocument doc = LoadedPatch();
        for (int z = 10; z < 16; z++)
            for (int x = 10; x < (z < 12 ? 16 : 12); x++)
                doc.SetUnderlay(x, z, 0, TileRenderTestData.Water);
        var planes = TileWaterPlanes.Collect(doc, TileRenderTestData.Catalogs, default, 0);
        Assert.Equal(2, planes.Count);

        Assert.True(TileWaterOptics.TryBuild(doc, default, 0, -0.02f, 0f, out var build, out _));

        foreach (WaterPlane plane in planes)
        {
            Assert.Equal(-0.02f, plane.SurfaceY);
            Assert.InRange(plane.CenterX - plane.HalfExtentX, build.Origin.X, build.Origin.X + build.Size.X);
            Assert.InRange(plane.CenterZ - plane.HalfExtentZ, build.Origin.Z, build.Origin.Z + build.Size.Y);
            Assert.InRange(plane.CenterX + plane.HalfExtentX, build.Origin.X, build.Origin.X + build.Size.X);
            Assert.InRange(plane.CenterZ + plane.HalfExtentZ, build.Origin.Z, build.Origin.Z + build.Size.Y);
            Assert.Equal(-0.02f, Sample(build, plane.CenterX, plane.CenterZ), 5);
        }
    }

    [Fact]
    public void DistinctLevelsHaveDistinctReferenceMappings()
    {
        TileWorldDocument doc = LoadedPatch();
        doc.SetCornerHeightCm(10, 10, 0, 250);

        Assert.True(TileWaterOptics.TryBuild(doc, default, 0, 2f, 0f, out var low, out _));
        Assert.True(TileWaterOptics.TryBuild(doc, default, 0, 30f, 0f, out var high, out _));

        Assert.Equal(2f, low.Origin.Y);
        Assert.Equal(30f, high.Origin.Y);
        Assert.Equal(-0.5f, Sample(low, 10f, -10f), 4);
        Assert.Equal(27.5f, Sample(high, 10f, -10f), 4);
        doc.SetCornerHeightCm(10, 10, 0, 1000);
        Assert.Equal(-0.5f, Sample(low, 10f, -10f), 4);
        Assert.Equal(27.5f, Sample(high, 10f, -10f), 4);
    }

    [Fact]
    public void MissingHaloRefusesTheWholeBuild()
    {
        TileWorldDocument doc = LoadedPatch();
        doc.RemoveRegion(new RegionCoord(-1, 1));

        Assert.False(TileWaterOptics.TryBuild(doc, default, 2, 8f, 0f, out var build, out var failure));

        Assert.Null(build);
        Assert.Equal(TileWaterOpticsFailureKind.MissingHeightData, failure.Kind);
        Assert.Equal(new RegionCoord(-1, 1), failure.MissingRegion);
        Assert.Equal(default, failure.Region);
        Assert.Equal(2, failure.Plane);
        Assert.Equal(8f, failure.ReferenceSurfaceY);
    }

    [Theory]
    [InlineData(479f)]
    [InlineData(float.MaxValue)]
    public void OversizedFieldIsRefusedWithoutDownsampling(float padding)
    {
        var doc = new TileWorldDocument();

        Assert.False(TileWaterOptics.TryBuild(doc, default, 0, 3f, padding, out var build, out var failure));

        Assert.Null(build);
        Assert.Equal(TileWaterOpticsFailureKind.FieldTooLarge, failure.Kind);
        Assert.Null(failure.MissingRegion);
    }

    [Fact]
    public void UnrepresentableCoordinatesAreRefusedBeforeReadingWrappedRegions()
    {
        var doc = new TileWorldDocument();
        Assert.False(TileWaterOptics.TryBuild(doc, new RegionCoord(int.MaxValue, 0), 0, 3f, 0f,
            out var build, out var failure));
        Assert.Null(build);
        Assert.Equal(TileWaterOpticsFailureKind.FieldTooLarge, failure.Kind);
        doc.TileSize = float.MaxValue;
        Assert.False(TileWaterOptics.TryBuild(doc, default, 0, 3f, 0f, out build, out failure));
        Assert.Null(build);
        Assert.Equal(TileWaterOpticsFailureKind.FieldTooLarge, failure.Kind);
    }

    [Fact]
    public void DependenciesIncludeNegativeAvailabilityRequestedAndDerivedPlanes()
    {
        TileWorldDocument doc = LoadedPatch();
        // Padding reaches exactly x=z=128. Its absent owners edge-extend from
        // the loaded patch. Creating one of those owners must invalidate.
        doc.SetCornerHeightCm(10, 10, 2, 0);
        doc.SetCornerHeightCm(65, 10, 0, 200);

        Assert.True(TileWaterOptics.TryBuild(doc, default, 2, 10f, 63f, out var build, out _));

        Assert.Equal(2f, Sample(build, 65f, -10f), 4);
        Assert.Equal(build.Dependencies.Count, build.Dependencies.Distinct().Count());
        Assert.Contains(new TileWaterOpticsDependency(new RegionCoord(2, 2), null), build.Dependencies);
        Assert.Contains(new TileWaterOpticsDependency(new RegionCoord(1, 2), null), build.Dependencies);
        Assert.Contains(new TileWaterOpticsDependency(new RegionCoord(2, 1), null), build.Dependencies);
        Assert.DoesNotContain(new TileWaterOpticsDependency(new RegionCoord(2, 2), 2), build.Dependencies);
        Assert.Contains(new TileWaterOpticsDependency(new RegionCoord(1, 0), null), build.Dependencies);
        Assert.Contains(new TileWaterOpticsDependency(new RegionCoord(1, 0), 2), build.Dependencies);
        Assert.Contains(new TileWaterOpticsDependency(new RegionCoord(1, 0), 0), build.Dependencies);
        Assert.Contains(new TileWaterOpticsDependency(default, 2), build.Dependencies);
        Assert.DoesNotContain(new TileWaterOpticsDependency(default, 0), build.Dependencies);
    }

    [Fact]
    public void AdjacentFieldsAgreeOnTheirSharedBoundary()
    {
        TileWorldDocument doc = LoadedPatch();
        for (int rz = -1; rz <= 1; rz++) doc.GetOrCreateRegion(new RegionCoord(2, rz));
        doc.SetCornerHeightCm(64, 10, 0, 100);
        doc.SetCornerHeightCm(64, 11, 0, 300);

        Assert.True(TileWaterOptics.TryBuild(doc, default, 0, 5f, 0f, out var west, out _));
        Assert.True(TileWaterOptics.TryBuild(doc, new RegionCoord(1, 0), 0, 5f, 0f, out var east, out _));

        Assert.Equal(3.5f, Sample(west, 64f, -10.25f), 4);
        Assert.Equal(3.5f, Sample(east, 64f, -10.25f), 4);
    }

    [Fact]
    public void InvalidInputsAreRejectedBeforeSampling()
    {
        var doc = new TileWorldDocument();
        Assert.Throws<ArgumentNullException>(() => TileWaterOptics.TryBuild(null!, default, 0, 0f, 0f, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => TileWaterOptics.TryBuild(doc, default, 4, 0f, 0f, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => TileWaterOptics.TryBuild(doc, default, 0, float.NaN, 0f, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => TileWaterOptics.TryBuild(doc, default, 0, 0f, -1f, out _, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => TileWaterOptics.TryBuild(doc, default, 0, 0f, float.NaN, out _, out _));
        doc.TileSize = 0f;
        Assert.Throws<ArgumentOutOfRangeException>(() => TileWaterOptics.TryBuild(doc, default, 0, 0f, 0f, out _, out _));
    }
}
