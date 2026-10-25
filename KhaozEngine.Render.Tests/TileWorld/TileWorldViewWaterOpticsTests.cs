using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

[Collection("AllocSensitive")]
public sealed class TileWorldViewWaterOpticsTests
{
    static readonly RegionCoord Origin = new(0, 0);
    static readonly Vector3 Focus = new(12f, 0f, -12f);

    static TileWorldViewOptions Authored() => new() { WaterDepthMode = TileWaterDepthMode.AuthoredBed };

    internal static TileWorldDocument World(int plane = 0)
    {
        var doc = new TileWorldDocument { Id = "optical-publication", DisplayName = "Optical publication" };
        for (int z = -1; z <= 1; z++)
            for (int x = -1; x <= 1; x++) doc.GetOrCreateRegion(new RegionCoord(x, z));
        // The bank supplies actual triangles around the certified domain. Zero underlay emits no bed.
        for (int z = 2; z < 32; z++)
            for (int x = 2; x < 32; x++) doc.SetUnderlay(x, z, plane, TileRenderTestData.Grass);
        // An L shaped body needs two row-run rectangles, both at reference level -0.02.
        for (int z = 10; z < 15; z++)
            for (int x = 10; x < (z < 12 ? 15 : 13); x++) doc.SetUnderlay(x, z, plane, TileRenderTestData.Water);
        doc.SetCornerHeightCm(11, 11, 0, -100);
        return doc;
    }

    static TileWorldView View(ITileWorldScene scene, TileWorldDocument doc, TileWorldViewOptions? options = null,
        ManualTileBuildDispatcher? dispatcher = null) => new(scene, doc, TileRenderTestData.Catalogs,
            new GreyboxMeshResolver(), options ?? Authored(),
            new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 8 }, dispatcher);

    static WaterPlane[] Water(TileWorldView view, RecordingTileWorldScene scene)
    {
        scene.ClearWater();
        view.DrawWaterPlanes();
        return scene.WaterDraws.ToArray();
    }

    static float Depth(RecordingTileWorldScene scene, WaterPlane plane, float x = 11f, float z = -11f)
    {
        Assert.True(plane.Optics.IsBound);
        WaterOpticsTriangleField field = scene.TriangleFields[plane.Optics.Field];
        // Physical depth as the shader forms it, without the clamp: rest depth plus level minus the datum.
        return field.SampleDepth((x - plane.Optics.Origin.X) / plane.Optics.Size.X,
            (z - plane.Optics.Origin.Z) / plane.Optics.Size.Y) + plane.SurfaceY - plane.Optics.Origin.Y;
    }

    // The independent mesh-intersection oracle over the accepted actual snapshots.
    static double Oracle(TileWorldView view, WaterPlane plane, float x = 11f, float z = -11f, int surface = 0,
        params RegionCoord[] regions) =>
        plane.SurfaceY - TileWaterBedOracle.Height((regions.Length == 0 ? new[] { Origin } : regions)
            .Select(r => view.AcceptedGroundSurface(r, surface)), x, z);

    [Fact]
    public void LegacyModeKeepsDefaultSceneCompatibility()
    {
        using var view = View(new LegacyScene(), World(), new TileWorldViewOptions());
        view.LoadRegion(Origin);
        view.DrawWaterPlanes();
    }

    [Fact]
    public void AuthoredModeRequiresPreparationCapability()
    {
        var scene = new LegacyScene();
        Assert.Throws<NotSupportedException>(() => View(scene, World()));
        Assert.Equal(0, scene.Uploads);
    }

    [Fact]
    public void PrepareUploadsBeforeAnyWaterSubmission()
    {
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, World());
        view.LoadRegion(Origin);
        Assert.Single(scene.TriangleLoads);
        Assert.Empty(scene.WaterDraws);
        WaterPlane[] planes = Water(view, scene);
        Assert.Equal(2, planes.Length);
        Assert.Equal(planes[0].Optics.Field, planes[1].Optics.Field);
        Assert.Equal(Oracle(view, planes[0]), Depth(scene, planes[0]), 4);
        Assert.Equal(0.98f, Depth(scene, planes[0]), 4);
    }

    [Fact]
    public void UnchangedDrawReusesFields()
    {
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, World());
        view.LoadRegion(Origin);
        var first = Water(view, scene)[0].Optics;
        view.Flush();
        Assert.Equal(first.Field, Water(view, scene)[0].Optics.Field);
        Assert.Single(scene.TriangleLoads);
    }

    [Fact]
    public void ChangingOnlyColourDoesNotUploadBedData()
    {
        var scene = new RecordingTileWorldScene();
        var options = Authored();
        using var view = View(scene, World(), options);
        view.LoadRegion(Origin);
        var first = Water(view, scene)[0].Optics;
        options.WaterLook = new WaterLook { Opacity = 0.4f, SwellAmplitude = 0f };
        view.Flush();
        var next = Water(view, scene)[0];
        Assert.Same(options.WaterLook, next.Look);
        Assert.Equal(first.Field, next.Optics.Field);
        Assert.Single(scene.TriangleLoads);
    }

    [Fact]
    public void HeightEditPublishesGeometryAndFieldTogether()
    {
        var scene = new RecordingTileWorldScene();
        var doc = World();
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        var first = Water(view, scene)[0].Optics.Field;
        doc.SetCornerHeightCm(11, 11, 0, -200);
        view.MarkDirty(Origin, 0);
        // Invalid source revisions cannot keep an older optical generation visible.
        Assert.Empty(Water(view, scene));
        scene.BeforeOpticalLoad = () => Assert.Empty(Water(view, scene));
        view.Flush();
        scene.BeforeOpticalLoad = null;
        view.Draw(Focus);
        var plane = Water(view, scene)[0];
        Assert.NotEqual(first, plane.Optics.Field);
        Assert.Equal(Oracle(view, plane), Depth(scene, plane), 4);
        Assert.Equal(1.98f, Depth(scene, plane), 4);
        var mesh = scene.GroundMeshes[Assert.Single(scene.Drawn).Handle.Index];
        Assert.Contains(mesh.Vertices, v => Math.Abs(v.Position.X - 11f) < 0.001f &&
            Math.Abs(v.Position.Z + 11f) < 0.001f && Math.Abs(v.Position.Y + 2f) < 0.001f);
    }

    [Fact]
    public void NewEarlierCornerOwnerInvalidatesFallback()
    {
        // Tile 63 reads corner x = 64 from the west fallback until its owner arrives. Padding keeps the
        // certified domain inside Origin while reaching that tile's actual triangles.
        var doc = new TileWorldDocument { Id = "earlier-owner", DisplayName = "Earlier owner" };
        doc.GetOrCreateRegion(Origin);
        for (int z = 6; z < 18; z++)
            for (int x = 56; x < 64; x++) doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
        doc.SetUnderlay(62, 11, 0, TileRenderTestData.Water);
        var east = new RegionCoord(1, 0);
        var scene = new RecordingTileWorldScene { OpticalPadding = 0.9f };
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        var first = Water(view, scene)[0];
        float before = Depth(scene, first, 63.75f, -11.25f);
        Assert.Equal(Oracle(view, first, 63.75f, -11.25f), before, 4);
        doc.GetOrCreateRegion(east);
        doc.SetCornerHeightCm(64, 11, 0, 150);
        view.LoadRegion(east);
        Assert.Empty(Water(view, scene));
        view.Flush();
        var plane = Water(view, scene)[0];
        Assert.NotEqual(first.Optics.Field, plane.Optics.Field);
        double expected = Oracle(view, plane, 63.75f, -11.25f);
        Assert.Equal(expected, Depth(scene, plane, 63.75f, -11.25f), 4);
        Assert.True(Math.Abs(expected - before) > 0.3, $"before {before}, after {expected}");
    }

    [Fact]
    public void BasePlaneEditInvalidatesDerivedFields()
    {
        var doc = World(1);
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        var first = Water(view, scene)[0].Optics.Field;
        doc.SetCornerHeightCm(11, 11, 0, -200);
        view.MarkDirty(Origin, 0);
        view.Flush();
        var plane = Water(view, scene)[0];
        Assert.NotEqual(first, plane.Optics.Field);
        Assert.Equal(Oracle(view, plane, surface: 1), Depth(scene, plane), 4);
        Assert.Equal(1.98f, Depth(scene, plane), 4);
    }

    [Fact]
    public void NeighbourUnloadCannotPublishAGuessedBed()
    {
        var doc = World();
        var west = new RegionCoord(-1, 0);
        for (int z = 6; z < 18; z++)
            for (int x = -8; x < 6; x++) doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
        doc.SetUnderlay(0, 11, 0, TileRenderTestData.Water);
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, doc);
        view.LoadRegion(west);
        view.LoadRegion(Origin);
        Assert.Contains(Water(view, scene), p => p.CenterX < 1f && p.Optics.IsBound);
        doc.RemoveRegion(west);
        view.UnloadRegion(west);
        view.MarkRegionStreamed(west);
        view.Flush();
        // The complete region-plane request withdraws. Origin's terrain remains.
        Assert.Empty(Water(view, scene));
        Assert.Equal(west, Assert.Single(view.WaterOpticsFailures).MissingRegion);
        Assert.Empty(scene.TriangleFields);
        view.Draw(Focus);
        Assert.Single(scene.Drawn);
    }

    [Fact]
    public void StaleAsyncResultCannotReplaceCurrentField()
    {
        var doc = World();
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        using var view = View(scene, doc, dispatcher: dispatcher);
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        view.Draw(Focus); // holds the first geometry snapshot
        Assert.True(dispatcher.PendingCount > 0);
        doc.SetCornerHeightCm(11, 11, 0, -200);
        view.MarkDirty(Origin, 0);
        view.Flush();
        dispatcher.RunAll();
        view.Draw(Focus);
        dispatcher.RunReverse();
        scene.ClearFrame();
        view.Draw(Focus);
        var plane = Water(view, scene)[0];
        Assert.Equal(Oracle(view, plane), Depth(scene, plane), 4);
        Assert.Equal(1.98f, Depth(scene, plane), 4);
        Assert.Single(scene.TriangleFields);
    }

    [Fact]
    public void FailedReplacementKeepsOnlyACompleteGeneration()
    {
        var doc = World();
        // A narrower displacement keeps the complete old field, whose certificates still contain it.
        var scene = new RecordingTileWorldScene { OpticalPadding = 2f };
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        scene.OpticalPadding = 1f;
        scene.ThrowOnOpticalLoad = 2;
        Assert.Throws<InvalidOperationException>(() => view.Flush());
        var retained = Water(view, scene)[0];
        Assert.Equal(0.98f, Depth(scene, retained), 4);
        Assert.Single(scene.TriangleFields);
        // A failed geometry upload must also release its unpublished optical resources.
        scene.ThrowOnOpticalLoad = 0;
        scene.ThrowOnMeshLoad = scene.MeshLoads.Count + 1;
        doc.SetCornerHeightCm(11, 11, 0, -200);
        view.MarkDirty(Origin, 0);
        Assert.Throws<InvalidOperationException>(() => view.Flush());
        Assert.Empty(Water(view, scene));
        Assert.Empty(scene.TriangleFields);
    }

    [Fact]
    public void UnloadAndDisposeReleaseSharedFieldsExactlyOnce()
    {
        var scene = new RecordingTileWorldScene();
        var view = View(scene, World());
        view.LoadRegion(Origin);
        Assert.Equal(2, Water(view, scene).Length);
        Assert.Single(scene.TriangleFields);
        view.UnloadRegion(Origin);
        Assert.Empty(scene.TriangleFields);
        Assert.Single(scene.OpticalUnloads);
        view.LoadRegion(Origin);
        view.Dispose();
        view.Dispose();
        Assert.Equal(2, scene.OpticalUnloads.Count);
        Assert.Empty(scene.TriangleFields);
    }

    [Fact]
    public void PreparationLoadsKnownHaloBeforeCollectingTheRim()
    {
        using var tmp = new TempDir();
        var authored = World();
        TileWorldFile.Save(authored, tmp.Path);
        var source = TileWorldSource.Open(tmp.Path);
        source.EnsureLoaded(Origin);
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, source.Document);
        view.LoadRegion(Origin);
        Assert.NotNull(source.Document.GetRegion(new RegionCoord(-1, -1)));
        Assert.Single(scene.TriangleFields);
        Assert.Equal(0.98f, Depth(scene, Water(view, scene)[0]), 4);
        view.Draw(Focus);
        Assert.Single(scene.TriangleLoads);
    }

    [Fact]
    public void RefusalSnapshotAndIoStayUnchangedUntilDependenciesChange()
    {
        var doc = World();
        var absent = new RegionCoord(-1, 0);
        doc.RemoveRegion(absent);
        for (int z = 6; z < 18; z++)
            for (int x = 0; x < 6; x++) doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
        doc.SetUnderlay(0, 11, 0, TileRenderTestData.Water);
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        var failures = view.WaterOpticsFailures;
        TileWaterOpticsFailure failure = Assert.Single(failures);
        Assert.Equal(TileWaterOpticsFailureKind.MissingSurfaceData, failure.Kind);
        Assert.Equal(absent, failure.MissingRegion);
        int loads = scene.TriangleLoads.Count;
        int meshes = scene.MeshLoads.Count;
        view.Flush();
        view.DrawWaterPlanes();
        Assert.Same(failures, view.WaterOpticsFailures);
        Assert.Equal(loads, scene.TriangleLoads.Count);
        Assert.Equal(meshes, scene.MeshLoads.Count);
        doc.GetOrCreateRegion(absent);
        for (int z = 6; z < 18; z++)
            for (int x = -8; x < 0; x++) doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
        view.LoadRegion(absent);
        view.Flush();
        Assert.Empty(view.WaterOpticsFailures);
        Assert.Single(scene.TriangleFields);
    }

    [Fact]
    public void UnrepresentableMappingKeepsItsPositionalRefusalDiagnostic()
    {
        var doc = World();
        var scene = new RecordingTileWorldScene { OpticalPadding = 2000f };
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        Assert.Equal(TileWaterOpticsFailureKind.FieldTooLarge, Assert.Single(view.WaterOpticsFailures).Kind);
        // Invalid water withdraws while unrelated valid terrain still publishes.
        Assert.NotEmpty(scene.MeshLoads);
        Assert.Empty(scene.TriangleLoads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DocumentScaleChangesInvalidateCompleteGenerations(bool tileSize)
    {
        var doc = World();
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        var first = Water(view, scene)[0].Optics;
        if (tileSize) doc.TileSize = 2f;
        else doc.PlaneHeight += 1f;
        view.Flush();
        var next = Water(view, scene)[0].Optics;
        Assert.NotEqual(first.Field, next.Field);
        Assert.Single(scene.TriangleFields);
        if (tileSize) Assert.Equal(first.Size * 2f, next.Size);
    }

    [Fact]
    public void PreparedWaterSubmissionAllocatesNothingAndReadsNoSource()
    {
        var doc = World();
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        view.DrawWaterPlanes();
        scene.ClearWater();
        // The document is intentionally unavailable during this submission. No preparation is run.
        doc.RemoveRegion(Origin);
        AllocationMeasurement.NoPerCallAllocation("prepared water submission", view.DrawWaterPlanes);
        Assert.Equal(2, scene.WaterDraws.Count);
        Assert.Single(scene.TriangleLoads);
    }

    [Fact]
    public void FailureAfterFirstReferenceLevelUploadReleasesUnpublishedFields()
    {
        var doc = World();
        doc.SetUnderlay(30, 30, 0, TileRenderTestData.Water);
        doc.SetCornerHeightCm(30, 30, 0, 200);
        var scene = new RecordingTileWorldScene { ThrowOnOpticalLoad = 2 };
        using var view = View(scene, doc);
        Assert.Throws<InvalidOperationException>(() => view.LoadRegion(Origin));
        Assert.Empty(scene.TriangleFields);
        Assert.Single(scene.OpticalUnloads);
        Assert.Empty(scene.MeshLoads);
        Assert.Equal(0, view.LoadedRegionCount);
    }

    [Fact]
    public void SharedMissingSourceIsNotReadAgainWithoutAnAvailabilityChange()
    {
        using var tmp = new TempDir();
        var doc = World();
        for (int z = -1; z <= 1; z++) doc.GetOrCreateRegion(new RegionCoord(2, z));
        for (int z = -4; z < 4; z++)
            for (int x = 60; x < 68; x++) doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
        doc.SetUnderlay(64, 0, 0, TileRenderTestData.Water);
        TileWorldFile.Save(doc, tmp.Path);
        var source = TileWorldSource.Open(tmp.Path);
        source.EnsureLoaded(Origin);
        var east = new RegionCoord(1, 0);
        source.EnsureLoaded(east);
        var missing = new RegionCoord(0, -1);
        string file = TileWorldFile.RegionPath(tmp.Path, missing);
        byte[] bytes = System.IO.File.ReadAllBytes(file);
        System.IO.File.Delete(file);
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, source.Document);
        var error = Assert.Throws<TileWorldException>(() => view.LoadRegion(Origin));
        Assert.IsType<System.IO.FileNotFoundException>(error.InnerException);
        Assert.Equal(1, view.WaterSourceRefusalCount);
        // This request shares the same unavailable source. It must refuse, without reopening the file.
        view.LoadRegion(east);
        TileWaterOpticsFailure failure = Assert.Single(view.WaterOpticsFailures);
        Assert.Equal(TileWaterOpticsFailureKind.MissingSurfaceData, failure.Kind);
        Assert.Equal(missing, failure.MissingRegion);
        Assert.Empty(scene.TriangleFields);
        view.Flush();
        System.IO.File.WriteAllBytes(file, bytes);
        view.MarkRegionStreamed(missing);
        Assert.Equal(0, view.WaterSourceRefusalCount);
        foreach (RegionCoord support in new[] { missing, Origin, new RegionCoord(1, -1) }) view.LoadRegion(support);
        view.Flush(int.MaxValue);
        Assert.Empty(view.WaterOpticsFailures);
        Assert.Contains(Water(view, scene), p => p.CenterX > 64f && p.Optics.IsBound);
        foreach (RegionCoord loaded in view.LoadedRegions) view.UnloadRegion(loaded);
        Assert.Equal(0, view.WaterSourceRefusalCount);
        Assert.Equal(0, view.WaterRegistry.EpochCount);
    }

    [Fact]
    public void LaterPlaneUploadCannotPublishAnInvalidatedEarlierPlane()
    {
        var doc = World();
        for (int z = 6; z < 15; z++)
            for (int x = 6; x < 15; x++) doc.SetUnderlay(x, z, 1, TileRenderTestData.Grass);
        doc.SetUnderlay(10, 10, 1, TileRenderTestData.Water);
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, doc);
        int uploads = 0;
        scene.BeforeOpticalLoad = () =>
        {
            if (++uploads != 2) return;
            doc.SetCornerHeightCm(11, 11, 0, -200);
            view.MarkDirty(Origin, 0);
        };
        view.LoadRegion(Origin);
        var planes = Water(view, scene);
        Assert.DoesNotContain(planes, p => p.SurfaceY < 0f);
        scene.BeforeOpticalLoad = null;
        view.Flush();
        var lower = Water(view, scene).Where(p => p.SurfaceY < 0f).ToArray();
        Assert.Equal(2, lower.Length);
        Assert.Equal(lower[0].Optics.Field, lower[1].Optics.Field);
        Assert.All(lower, p => Assert.Equal(Oracle(view, p), Depth(scene, p), 4));
        Assert.All(lower, p => Assert.Equal(1.98f, Depth(scene, p), 4));
    }

    [Fact]
    public void PendingBuildCannotPublishAnObsoleteDisplacementWitness()
    {
        var doc = World();
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(),
            Authored(), new TileWorldBuildQueueOptions
            {
                MaxConcurrentBuilds = 8, MaxFullGroundAppliesPerPump = 8,
            }, dispatcher);
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        view.Draw(Focus);
        scene.OpticalPadding = 3f;
        dispatcher.RunAll();
        view.Draw(Focus);
        Assert.Empty(scene.TriangleFields);
        view.Draw(Focus);
        dispatcher.RunAll();
        view.Draw(Focus);
        var field = Assert.Single(scene.TriangleFields).Value;
        // Every certificate contains its displaced rectangle at the current padding.
        foreach (WaterPlane p in Water(view, scene))
            Assert.Contains(field.CertifiedRectangles.ToArray(), c =>
                c.Minimum.X <= p.CenterX - p.HalfExtentX - 3f - p.Optics.Origin.X &&
                c.Maximum.X >= p.CenterX + p.HalfExtentX + 3f - p.Optics.Origin.X &&
                c.Minimum.Y <= p.CenterZ - p.HalfExtentZ - 3f - p.Optics.Origin.Z &&
                c.Maximum.Y >= p.CenterZ + p.HalfExtentZ + 3f - p.Optics.Origin.Z);
    }

    sealed class LegacyScene : ITileWorldScene
    {
        public int Uploads { get; private set; }
        public MeshHandle LoadMesh(GltfMesh mesh) => new(Uploads++, 1);
        public void UnloadMesh(MeshHandle handle) { }
        public void DrawMesh(MeshHandle handle, Matrix4x4 world) { }
        public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts) => Array.Empty<MeshHandle>();
        public void UnloadPropMeshes(IReadOnlyList<MeshHandle> handles) { }
        public int DrawProps(IReadOnlyList<PropPlacement> placements,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus, float drawRadius) => 0;
    }
}
