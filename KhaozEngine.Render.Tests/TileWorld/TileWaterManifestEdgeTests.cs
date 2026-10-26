using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWaterManifestEdgeTests
{
    static readonly RegionCoord River = new(1, 0);
    static readonly RegionCoord North = new(1, -1);

    static TileWorldDocument Document(params RegionCoord[] regions)
    {
        var doc = new TileWorldDocument { Id = "manifest-edge", DisplayName = "Manifest edge", PlaneCount = 1 };
        foreach (RegionCoord region in regions) doc.GetOrCreateRegion(region);
        return doc;
    }

    static void Fill(TileWorldDocument doc, int x0, int z0, int x1, int z1)
    {
        for (int z = z0; z < z1; z++)
            for (int x = x0; x < x1; x++) doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
    }

    static TileWorldDocument RiverWorld()
    {
        TileWorldDocument doc = Document(River);
        Fill(doc, 66, 0, 76, 6);
        for (int x = 68; x < 72; x++) doc.SetUnderlay(x, 0, 0, TileRenderTestData.Water);
        for (int z = 0; z <= 6; z++)
            for (int x = 66; x <= 76; x++) doc.SetCornerHeightCm(x, z, 0, -35);
        for (int x = 66; x <= 76; x++) doc.SetCornerHeightCm(x, 0, 0, (short)(-100 + (x - 66) * 10));
        return doc;
    }

    static TileWorldView View(RecordingTileWorldScene scene, TileWorldDocument doc) =>
        new(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(),
            new TileWorldViewOptions { WaterDepthMode = TileWaterDepthMode.AuthoredBed });

    static WaterPlane[] Water(TileWorldView view, RecordingTileWorldScene scene)
    {
        scene.ClearWater();
        view.DrawWaterPlanes();
        return scene.WaterDraws.ToArray();
    }

    static float RestDepth(WaterOpticsTriangleField field, float originX, float originZ, float x, float z)
    {
        Assert.True(field.TrySampleDepth((x - originX) / field.Size.X, (z - originZ) / field.Size.Y, out float depth));
        return depth;
    }

    static TileWaterTriangleOutcome Prepare(TileWorldDocument doc, RegionCoord region, float padding, TileRect rect,
        out Dictionary<RegionCoord, TileGroundSurfaceSnapshot> sources, TileWaterDependencyRegistry? registry = null)
    {
        sources = doc.LoadedRegions.ToDictionary(r => r.Coord, r => TileGroundCpuBuild.Build(doc,
            TileRenderTestData.Catalogs, r.Coord, 0, TileGroundLod.Full, new TileGroundMesherOptions(),
            captureOpticalSurface: true).Surface!);
        Dictionary<RegionCoord, TileGroundSurfaceSnapshot> table = sources;
        return TileWaterTrianglePreparation.Prepare(new TileWaterPreparation(
            [TileWaterPlanes.ToPlane(rect, -0.37f, doc.TileSize)], new TileWaterSourceStamp(doc, [], null), padding, null),
            region, 0, r => table.GetValueOrDefault(r),
            new TileWaterSourceContext(doc, TileRenderTestData.Catalogs, new TileGroundMesherOptions(),
                registry ?? new TileWaterDependencyRegistry()));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    public void NorthManifestEdgePublishesUntrimmedWaterAndClampsTheSlopedBed(float padding)
    {
        TileWorldDocument doc = RiverWorld();
        var scene = new RecordingTileWorldScene { OpticalPadding = padding };
        using var view = View(scene, doc);
        view.LoadRegion(River);

        Assert.Empty(view.WaterOpticsFailures);
        WaterPlane water = Assert.Single(Water(view, scene));
        Assert.Equal(-0.37f, water.SurfaceY, 5);
        Assert.Equal((70f, -0.5f, 2f, 0.5f), (water.CenterX, water.CenterZ, water.HalfExtentX, water.HalfExtentZ));
        WaterOpticsTriangleField field = scene.TriangleFields[water.Optics.Field];
        foreach (float x in new[] { 68.125f, 69.5f, 71.875f })
        {
            float depth = RestDepth(field, water.Optics.Origin.X, water.Optics.Origin.Z, x,
                padding == 0f ? 0.00005f : 0.375f);
            Assert.Equal(-TileWaterBedOracle.Height([view.AcceptedGroundSurface(River, 0)], x, 0), depth, 5);
        }
        Assert.InRange(field.ReconstructionErrorBoundMetres, 0d, 0.0001d);
    }

    [Fact]
    public void KnownUnloadedNorthStillWithdrawsWaterAndKeepsTerrain()
    {
        using var tmp = new TempDir();
        TileWorldDocument authored = RiverWorld();
        authored.GetOrCreateRegion(North);
        TileWorldFile.Save(authored, tmp.Path);
        TileWorldSource source = TileWorldSource.Open(tmp.Path);
        source.EnsureLoaded(River);
        File.Delete(TileWorldFile.RegionPath(tmp.Path, North));
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, source.Document);
        view.LoadRegion(River);

        TileWaterOpticsFailure failure = Assert.Single(view.WaterOpticsFailures);
        Assert.Equal(TileWaterOpticsFailureKind.MissingSurfaceData, failure.Kind);
        Assert.Equal(North, failure.MissingRegion);
        Assert.Empty(Water(view, scene));
        Assert.NotNull(view.AcceptedGroundSurface(River, 0));
        Assert.NotEmpty(scene.MeshLoads);
        Assert.False(source.IsLoaded(North));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(63, 0)]
    [InlineData(0, 63)]
    [InlineData(63, 63)]
    public void ConvexCornersContinueTheActualCornerHeight(int x, int z)
    {
        TileWorldDocument doc = Document(default(RegionCoord));
        Fill(doc, Math.Max(0, x - 2), Math.Max(0, z - 2), Math.Min(64, x + 3), Math.Min(64, z + 3));
        int cx = x == 0 ? 0 : 64, cz = z == 0 ? 0 : 64;
        doc.SetCornerHeightCm(Math.Min(cx, 63), Math.Min(cz, 63), 0, -125);
        TileWaterTriangleOutcome outcome = Prepare(doc, default, 0.5f, new TileRect(x, z, 1, 1), out var sources);

        Assert.Null(outcome.Failure);
        TileWaterTriangleBuild build = Assert.Single(outcome.Fields);
        float outsideX = cx + (x == 0 ? -0.25f : 0.25f);
        float outsideZ = -cz + (z == 0 ? 0.25f : -0.25f);
        Assert.Equal(-TileWaterBedOracle.Height(sources.Values, cx, -cz),
            RestDepth(build.Field, build.Origin.X, build.Origin.Z, outsideX, outsideZ), 5);
    }

    [Theory]
    [InlineData(-1, 1, 0.1f, 0, 0)]
    [InlineData(-1, 1, 0.1f, 63, 63)]
    [InlineData(2, -2, 0.3f, 0, 63)]
    [InlineData(2, -2, 0.3f, 63, 0)]
    [InlineData(-3, 20, 2.54f, 0, 0)]
    [InlineData(-3, 20, 2.54f, 63, 63)]
    public void FractionalTileEdgesStayAttachedToTheUploadedBoundary(int rx, int rz, float tileSize, int x, int z)
    {
        var region = new RegionCoord(rx, rz);
        TileWorldDocument doc = Document(region);
        doc.TileSize = tileSize;
        Fill(doc, region.OriginX + Math.Max(0, x - 2), region.OriginZ + Math.Max(0, z - 2),
            region.OriginX + Math.Min(64, x + 3), region.OriginZ + Math.Min(64, z + 3));
        doc.SetCornerHeightCm(region.OriginX + x, region.OriginZ + z, 0, -125);
        TileWaterTriangleOutcome outcome = Prepare(doc, region, tileSize * 0.5f,
            new TileRect(region.OriginX + x, region.OriginZ + z, 1, 1), out var sources);

        Assert.Null(outcome.Failure);
        TileWaterTriangleBuild build = Assert.Single(outcome.Fields);
        TileGroundSurfaceSnapshot source = sources[region];
        float span = 64 * tileSize;
        double edgeX = source.WorldTransform.M41 + (x == 0 ? 0d : span);
        double edgeZ = source.WorldTransform.M43 - (z == 0 ? 0d : span);
        float outsideX = (float)(edgeX + (x == 0 ? -0.25 : 0.25) * tileSize);
        float outsideZ = (float)(edgeZ + (z == 0 ? 0.25 : -0.25) * tileSize);
        Assert.Equal(-TileWaterBedOracle.Height([source], edgeX, edgeZ),
            RestDepth(build.Field, build.Origin.X, build.Origin.Z, outsideX, outsideZ), 4);
        Assert.InRange(build.ConversionErrorBoundMetres + build.Field.ReconstructionErrorBoundMetres,
            0d, Math.Min(0.0001d, tileSize / 1024d));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.75f)]
    public void ConcaveDiagonalAbsenceHasContinuousCoverageWithDifferentEdgeSlopes(float padding)
    {
        TileWorldDocument doc = Document(default(RegionCoord), new RegionCoord(1, 0), new RegionCoord(0, 1));
        Fill(doc, 59, 59, 64, 64);
        Fill(doc, 64, 59, 68, 64);
        Fill(doc, 59, 64, 64, 68);
        // The absent southeast quadrant has two grounded sides with different profiles.
        doc.SetCornerHeightCm(63, 64, 0, -100);
        doc.SetCornerHeightCm(65, 63, 0, -200);
        doc.SetCornerHeightCm(63, 65, 0, -300);
        TileWaterTriangleOutcome outcome = Prepare(doc, default, padding, new TileRect(63, 63, 1, 1), out var sources);

        Assert.Null(outcome.Failure);
        TileWaterTriangleBuild build = Assert.Single(outcome.Fields);
        float step = padding == 0f ? 0.00004f : 0.5f;
        for (int j = 0; j <= 4; j++)
            for (int i = 0; i <= 4; i++)
                RestDepth(build.Field, build.Origin.X, build.Origin.Z, 64 + step * i / 4, -64 - step * j / 4);
        foreach ((float x, float z) in new[] { (64f, -64 - step), (64 + step, -64f), (64f, -64f) })
            Assert.Equal(-TileWaterBedOracle.Height(sources.Values, x, z),
                RestDepth(build.Field, build.Origin.X, build.Origin.Z, x, z), 4);
        Assert.InRange(build.ConversionErrorBoundMetres + build.Field.ReconstructionErrorBoundMetres, 0d, 0.0001d);
    }

    [Fact]
    public void SeamTieUsesTheEquallyNearSourceThatHasTheEdge()
    {
        TileWorldDocument doc = Document(default, River);
        Fill(doc, 60, 8, 68, 13);
        Fill(doc, 64, 0, 68, 2);
        doc.SetUnderlay(65, 0, 0, TileRenderTestData.Water);
        doc.SetUnderlay(64, 10, 0, TileRenderTestData.Water);

        IReadOnlyList<WaterPlane> planes = TileWaterPlanes.Collect(doc, TileRenderTestData.Catalogs, River, 0);
        Assert.Equal(2, planes.Count);
        var sources = doc.LoadedRegions.ToDictionary(r => r.Coord, r => TileGroundCpuBuild.Build(doc,
            TileRenderTestData.Catalogs, r.Coord, 0, TileGroundLod.Full, new TileGroundMesherOptions(),
            captureOpticalSurface: true).Surface!);
        TileWaterTriangleOutcome outcome = TileWaterTrianglePreparation.Prepare(new TileWaterPreparation(
            planes.ToArray(), new TileWaterSourceStamp(doc, [], null), 0.5f, null), River, 0,
            r => sources.GetValueOrDefault(r), new TileWaterSourceContext(doc, TileRenderTestData.Catalogs,
                new TileGroundMesherOptions(), new TileWaterDependencyRegistry()));

        Assert.Null(outcome.Failure);
        TileWaterTriangleBuild build = Assert.Single(outcome.Fields);
        Assert.Contains(planes, p => p.CenterZ > -1f);
        Assert.Equal(-TileWaterBedOracle.Height([sources[River]], 64.75, 0),
            RestDepth(build.Field, build.Origin.X, build.Origin.Z, 64.75f, 0.25f), 5);
    }

    [Fact]
    public void AppearingNorthInvalidatesTheContinuationAndRebuildsFromRealGround()
    {
        TileWorldDocument doc = RiverWorld();
        var scene = new RecordingTileWorldScene { OpticalPadding = 0.5f };
        using var view = View(scene, doc);
        view.LoadRegion(River);
        WaterPlane before = Assert.Single(Water(view, scene));
        var dependents = new List<(RegionCoord Region, int Plane)>();
        view.WaterRegistry.CollectDependents((North, 0), dependents);
        Assert.Contains((River, 0), dependents);

        doc.GetOrCreateRegion(North);
        Fill(doc, 66, -4, 76, 0);
        for (int x = 66; x <= 76; x++) doc.SetCornerHeightCm(x, -1, 0, -300);
        view.MarkRegionStreamed(North);
        view.LoadRegion(North);
        view.Flush();

        Assert.Empty(view.WaterOpticsFailures);
        WaterPlane after = Assert.Single(Water(view, scene));
        Assert.NotEqual(before.Optics.Field, after.Optics.Field);
        Assert.Contains(before.Optics.Field, scene.OpticalUnloads);
        Assert.Equal(-TileWaterBedOracle.Height([view.AcceptedGroundSurface(North, 0)], 70.25, 0.375),
            RestDepth(scene.TriangleFields[after.Optics.Field], after.Optics.Origin.X, after.Optics.Origin.Z, 70.25f, 0.375f), 4);
    }

    [Fact]
    public void AbsenceStampDetectsANeighbourBecomingKnownWithoutLoading()
    {
        TileWorldDocument doc = RiverWorld();
        var registry = new TileWaterDependencyRegistry();
        TileWaterTriangleOutcome outcome = Prepare(doc, River, 0f, new TileRect(68, 0, 4, 1), out var sources, registry);
        Assert.Null(outcome.Failure);
        Assert.True(outcome.Availability!.IsCurrent(doc, registry));
        doc.UnloadedRegionHashes.Add(North, "new-manifest-entry");
        Assert.False(outcome.Availability.IsCurrent(doc, registry));
        Assert.False(TileWaterTrianglePreparation.SourcesCurrent(outcome, r => sources.GetValueOrDefault(r),
            new TileWaterSourceContext(doc, TileRenderTestData.Catalogs, new TileGroundMesherOptions(), registry)));
    }

    [Fact]
    public void PaddingDependencyBecomingKnownDuringUploadCannotPublishTheContinuation()
    {
        TileWorldDocument doc = RiverWorld();
        for (int x = 68; x < 72; x++)
        {
            doc.SetUnderlay(x, 0, 0, TileRenderTestData.Grass);
            doc.SetUnderlay(x, 1, 0, TileRenderTestData.Water);
        }
        // The body's rim stays inside River. Only the padded bed depends on North's absence.
        var scene = new RecordingTileWorldScene { OpticalPadding = 1.5f };
        scene.BeforeOpticalLoad = () => doc.UnloadedRegionHashes.Add(North, "new-manifest-entry");
        using var view = View(scene, doc);
        view.LoadRegion(River);

        Assert.Single(scene.TriangleLoads);
        Assert.Empty(Water(view, scene));
        Assert.Equal(scene.TriangleLoads, scene.OpticalUnloads);
        Assert.Empty(scene.TriangleFields);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingGroundAtTheManifestEdgeIsNotManufactured(bool noDraw)
    {
        TileWorldDocument doc = RiverWorld();
        for (int x = 68; x < 72; x++)
            if (noDraw) doc.SetSettings(x, 0, 0, TileSettings.NoDraw);
            else doc.SetUnderlay(x, 0, 0, 0);
        TileWaterTriangleOutcome outcome = Prepare(doc, River, 0f, new TileRect(68, 0, 4, 1), out _);
        Assert.Equal(TileWaterOpticsFailureKind.IncompleteSurfaceCoverage, outcome.Failure!.Value.Kind);
        Assert.Empty(outcome.Fields);
    }
}
