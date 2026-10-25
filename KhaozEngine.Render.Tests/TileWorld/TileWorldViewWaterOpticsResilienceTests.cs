using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Optical water never costs terrain. Source I/O failures, latched field attempts and uncertified looks
/// all keep ground building, and scene-depth water scans nothing until it draws.</summary>
public sealed class TileWorldViewWaterOpticsResilienceTests
{
    static readonly RegionCoord Origin = new(0, 0);
    static readonly RegionCoord East = new(1, 0);
    static readonly Vector3 Focus = new(12f, 0f, -12f);

    static TileWorldViewOptions Authored(WaterLook? look = null) =>
        new() { WaterDepthMode = TileWaterDepthMode.AuthoredBed, WaterLook = look };

    static TileWorldView View(ITileWorldScene scene, TileWorldDocument doc, TileWorldViewOptions options,
        ManualTileBuildDispatcher? dispatcher = null, List<TileWorldBuildKey>? builds = null) =>
        new(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(), options,
            new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 8, MaxFullGroundAppliesPerPump = 8 }, dispatcher,
            builds is null ? null : builds.Add);

    static WaterPlane[] Water(TileWorldView view, RecordingTileWorldScene scene)
    {
        scene.ClearWater();
        view.DrawWaterPlanes();
        return scene.WaterDraws.ToArray();
    }

    // Water on Origin's east border tile. Its rim corners at x = 64 are owned by East.
    static TileWorldDocument EastBorderWorld()
    {
        TileWorldDocument doc = TileWorldViewWaterOpticsTests.World();
        for (int z = 6; z < 18; z++)
            for (int x = 58; x < 72; x++) doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
        doc.SetUnderlay(63, 11, 0, TileRenderTestData.Water);
        return doc;
    }

    static TileWorldSource Saved(TempDir tmp)
    {
        TileWorldFile.Save(EastBorderWorld(), tmp.Path);
        TileWorldSource source = TileWorldSource.Open(tmp.Path);
        source.EnsureLoaded(Origin);
        return source;
    }

    // East leaves the view and memory, and its file becomes unreadable, the way a partial save looks.
    static void BreakEast(TempDir tmp, TileWorldSource source, TileWorldView view)
    {
        view.UnloadRegion(East);
        Assert.True(source.Unload(East));
        File.Delete(TileWorldFile.RegionPath(tmp.Path, East));
        view.MarkRegionStreamed(East);
    }

    static void AssertEastDiagnosed(TileWorldView view)
    {
        TileWaterOpticsFailure failure = Assert.Single(view.WaterOpticsFailures);
        Assert.Equal((Origin, TileWaterOpticsFailureKind.MissingHeightData, (RegionCoord?)East),
            (failure.Region, failure.Kind, failure.MissingRegion));
    }

    [Fact]
    public void ThrowingNeighbourSourceStillRebuildsTerrainOnTheGameplayPath()
    {
        using var tmp = new TempDir();
        TileWorldSource source = Saved(tmp);
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, source.Document, Authored());
        view.LoadRegion(Origin);
        Assert.True(source.IsLoaded(East));
        view.LoadRegion(East);
        view.Flush();
        Assert.Empty(view.WaterOpticsFailures);
        Assert.Contains(Water(view, scene), p => p.CenterX > 60f && p.Optics.IsBound);
        BreakEast(tmp, source, view);
        int meshes = scene.MeshLoads.Count;

        view.MarkDirty(Origin, 0);
        view.Flush();
        Assert.Equal(meshes + 1, scene.MeshLoads.Count);
        AssertEastDiagnosed(view);
        Assert.DoesNotContain(Water(view, scene), p => p.CenterX > 60f);
        Assert.Equal(1, view.WaterSourceRefusalCount);

        // The failure is memoised. A second flush neither reopens the file nor throws.
        view.MarkDirty(Origin, 0);
        view.Flush();
        Assert.Equal(meshes + 2, scene.MeshLoads.Count);
        AssertEastDiagnosed(view);
        Assert.False(source.IsLoaded(East));
    }

    [Fact]
    public void ThrowingNeighbourSourceStillRebuildsTerrainOnTheProfilePath()
    {
        using var tmp = new TempDir();
        TileWorldSource source = Saved(tmp);
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        using var view = View(scene, source.Document, Authored(), dispatcher);
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        view.LoadRegion(East, TileRegionResidencyState.Gameplay);
        for (int frame = 0; frame < 3; frame++)
        {
            view.Draw(Focus);
            dispatcher.RunAll();
        }
        view.Draw(Focus);
        Assert.Contains(Water(view, scene), p => p.CenterX > 60f && p.Optics.IsBound);
        BreakEast(tmp, source, view);
        int meshes = scene.MeshLoads.Count;

        view.MarkDirty(Origin, 0);
        view.Flush();
        dispatcher.RunAll();
        view.Draw(Focus);
        Assert.Equal(meshes + 1, scene.MeshLoads.Count);
        AssertEastDiagnosed(view);
        Assert.DoesNotContain(Water(view, scene), p => p.CenterX > 60f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UncertifiedLookPerformsNoNeighbourSourceIo(bool profile)
    {
        using var tmp = new TempDir();
        TileWorldSource source = Saved(tmp);
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        using var view = View(scene, source.Document,
            Authored(new WaterLook { WaveSource = WaterWaveSource.FftOcean }), dispatcher);
        if (profile) view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        else view.LoadRegion(Origin);
        view.Flush();
        dispatcher.RunAll();
        view.Draw(Focus);
        Assert.False(source.IsLoaded(East));
        Assert.Contains(view.WaterOpticsFailures, f => f.Kind == TileWaterOpticsFailureKind.UncertifiedDisplacement);
    }

    // A refused padding refresh keeps the covering old generation and latches its field attempt. The latch must
    // not also hold back the ground: a promotion still replaces the coarse ground with full ground.
    [Fact]
    public void LatchedRegionIsPromotedFromCoarseToFullTerrain()
    {
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        var builds = new List<TileWorldBuildKey>();
        using var view = View(scene, TileWorldViewWaterOpticsTests.World(), Authored(), dispatcher, builds);
        view.LoadRegion(Origin, TileRegionResidencyState.Decor);
        view.Draw(Focus);
        dispatcher.RunAll();
        view.Draw(Focus);
        WaterOpticsHandle coarse = Assert.Single(scene.TriangleFields).Key;

        scene.OpticalPadding = .00005f;
        scene.RefuseSurfaceOnOpticalLoad = scene.TriangleLoads.Count + 1;
        view.Flush();
        TileWaterOpticsFailure kept = Assert.Single(view.WaterOpticsFailures);
        Assert.Equal(coarse, Assert.Single(scene.TriangleFields).Key);
        int loads = scene.TriangleLoads.Count;

        builds.Clear();
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        for (int frame = 0; frame < 4; frame++)
        {
            dispatcher.RunAll();
            view.Draw(Focus);
        }
        Assert.Contains(builds, b => b.Region == Origin && b.Plane == 0 && b.Kind == TileWorldBuildKind.FullGround);
        // The field stays refused without a new attempt, and the coarse water is not left over the full mesh.
        Assert.Equal(loads, scene.TriangleLoads.Count);
        Assert.Empty(scene.TriangleFields);
        Assert.Equal(kept, Assert.Single(view.WaterOpticsFailures));
    }

    [Theory]
    [InlineData(WaterOpticsSurfaceFailure.UnrepresentableSurface, TileWaterOpticsFailureKind.UnrepresentableSurface)]
    [InlineData(WaterOpticsSurfaceFailure.CapacityExceeded, TileWaterOpticsFailureKind.SurfaceCapacityExceeded)]
    [InlineData(WaterOpticsSurfaceFailure.UncertifiedDisplacement, TileWaterOpticsFailureKind.UncertifiedDisplacement)]
    [InlineData((WaterOpticsSurfaceFailure)99, TileWaterOpticsFailureKind.UncertifiedDisplacement)]
    public void UncertifiedWaterReportsTheReasonTheSceneGave(WaterOpticsSurfaceFailure reason,
        TileWaterOpticsFailureKind expected)
    {
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, TileWorldViewWaterOpticsTests.World(),
            Authored(new WaterLook { WaveSource = WaterWaveSource.FftOcean }));
        view.LoadRegion(Origin);
        Assert.Equal(TileWaterOpticsFailureKind.UncertifiedDisplacement, Assert.Single(view.WaterOpticsFailures).Kind);

        scene.PaddingRefusal = reason;
        view.Flush();
        Assert.Equal(expected, Assert.Single(view.WaterOpticsFailures).Kind);
        Assert.Empty(scene.TriangleLoads);
    }

    [Fact]
    public void SceneDepthViewCollectsNoWaterUntilItDraws()
    {
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        var options = new TileWorldViewOptions { DrawWater = false };
        using var view = View(scene, TileWorldViewWaterOpticsTests.World(), options, dispatcher);
        view.LoadRegion(Origin);
        view.MarkDirty(Origin, 0);
        view.Flush();
        view.LoadRegion(East, TileRegionResidencyState.Gameplay);
        dispatcher.RunAll();
        view.Draw(Focus);
        Assert.Equal(0, view.WaterCollections);

        // One collection for the one region-plane with a mesh. Empty planes and East's grassless ground scan nothing.
        WaterPlane[] planes = Water(view, scene);
        Assert.Equal(2, planes.Length);
        Assert.All(planes, p => Assert.False(p.Optics.IsBound));
        int collected = view.WaterCollections;
        Assert.Equal(1, collected);
        Water(view, scene);
        Assert.Equal(collected, view.WaterCollections);
    }
}
