using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldGroundSurfacePairTests
{
    static readonly RegionCoord Origin = new(0, 0);
    static readonly Vector3 Focus = new(16, 0, -16);

    [Fact]
    public void ProductionUploadUsesTheExactCompletedMeshInstance()
    {
        TileWorldDocument doc = World();
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, doc, catalogs, new GreyboxMeshResolver(), OpticsOptions());
        TileGroundCpuBuild ground = TileGroundCpuBuild.Build(doc, catalogs, Origin, 0, TileGroundLod.Full,
            new TileGroundMesherOptions(), captureOpticalSurface: true);
        var water = new TileWaterPreparation([], new TileWaterSourceStamp(doc, [], null), 0, null);
        TileWaterPublication.Group? group = view.StageGround(Origin, 0, ground, water);
        Assert.NotNull(group);
        Assert.Same(ground.Mesh, scene.GroundMeshes[group.Mesh!.Value.Index]);
        view.DiscardGround(group);
    }

    [Fact]
    public void SynchronousLoadFlushEmptyReplacementAndUnloadKeepPairedSnapshots()
    {
        TileWorldDocument doc = World();
        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(), OpticsOptions());
        Assert.Null(view.AcceptedGroundSurface(Origin, 0));
        view.LoadRegion(Origin);
        TileGroundSurfaceSnapshot first = Assert.IsType<TileGroundSurfaceSnapshot>(view.AcceptedGroundSurface(Origin, 0));
        AssertUploaded(scene, first);
        doc.SetCornerHeightCm(10, 10, 0, 100);
        view.MarkDirty(Origin, 0);
        view.Flush(int.MaxValue);
        TileGroundSurfaceSnapshot second = Assert.IsType<TileGroundSurfaceSnapshot>(view.AcceptedGroundSurface(Origin, 0));
        Assert.True(second.Id.Generation > first.Id.Generation);
        AssertUploaded(scene, second);
        doc.SetUnderlay(10, 10, 0, 0);
        view.MarkDirty(Origin, 0);
        view.Flush(int.MaxValue);
        Assert.True(view.AcceptedGroundSurface(Origin, 0)!.IsEmpty);
        view.UnloadRegion(Origin);
        Assert.Null(view.AcceptedGroundSurface(Origin, 0));
    }

    [Fact]
    public void FailedReplacementKeepsTheOldAcceptedSource()
    {
        TileWorldDocument doc = World();
        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(), OpticsOptions());
        view.LoadRegion(Origin);
        TileGroundSurfaceSnapshot first = view.AcceptedGroundSurface(Origin, 0)!;
        scene.ThrowOnMeshLoad = scene.MeshLoads.Count + 1;
        // A border dependency requests replacement while this accepted pair remains valid. Announcing
        // a change to its own source would correctly withdraw authored-bed ground before replacement.
        view.MarkDirty(new TileRect(64, 10, 1, 1), 0);
        Assert.Throws<InvalidOperationException>(() => view.Flush(int.MaxValue));
        Assert.Same(first, view.AcceptedGroundSurface(Origin, 0));
    }

    [Fact]
    public void WorkerCarriesKnownEmptyInsteadOfMissingSource()
    {
        TileWorldDocument doc = World();
        doc.SetUnderlay(10, 10, 0, 0);
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        using TileWorldView view = ProfileFixture.CreateView(scene, doc, dispatcher, new ProfileBuildWitness(), OpticsOptions());
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        Assert.Null(view.AcceptedGroundSurface(Origin, 0));
        view.SettleForCapture(Origin);
        Assert.True(view.AcceptedGroundSurface(Origin, 0)!.IsEmpty);
        Assert.Empty(scene.GroundMeshes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerAndPrimeCarryActualLodThroughPromotion(bool prime)
    {
        TileWorldDocument doc = World();
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        using TileWorldView view = ProfileFixture.CreateView(scene, doc, dispatcher, new ProfileBuildWitness(), OpticsOptions());
        view.LoadRegion(Origin, TileRegionResidencyState.Decor);
        Assert.Null(view.AcceptedGroundSurface(Origin, 0));
        view.Draw(Focus);
        dispatcher.RunAll();
        view.Draw(Focus);
        TileGroundSurfaceSnapshot coarse = Assert.IsType<TileGroundSurfaceSnapshot>(view.AcceptedGroundSurface(Origin, 0));
        Assert.Equal(TileGroundLod.Coarse4, coarse.Lod);
        AssertUploaded(scene, coarse);
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        if (prime) view.SettleForCapture(Origin);
        else
        {
            view.Draw(Focus);
            dispatcher.RunAll();
            view.Draw(Focus);
        }
        TileGroundSurfaceSnapshot full = Assert.IsType<TileGroundSurfaceSnapshot>(view.AcceptedGroundSurface(Origin, 0));
        Assert.Equal(TileGroundLod.Full, full.Lod);
        Assert.True(full.Id.Generation > coarse.Id.Generation);
        AssertUploaded(scene, full);
    }

    [Fact]
    public void StaleWorkerTopologyCannotBecomeAnAcceptedSurface()
    {
        TileWorldDocument doc = World();
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        using TileWorldView view = ProfileFixture.CreateView(scene, doc, dispatcher, new ProfileBuildWitness(), OpticsOptions());
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        view.Draw(Focus);
        doc.SetSettings(10, 10, 0, TileSettings.NoDraw);
        dispatcher.RunAll();
        view.Draw(Focus);
        Assert.Null(view.AcceptedGroundSurface(Origin, 0));
        Assert.Empty(scene.GroundMeshes);
    }

    static TileWorldViewOptions OpticsOptions() => new() { WaterDepthMode = TileWaterDepthMode.AuthoredBed };

    static TileWorldDocument World()
    {
        var doc = new TileWorldDocument { Id = "paired-surface", DisplayName = "Paired surface", PlaneCount = 1 };
        doc.GetOrCreateRegion(Origin);
        doc.SetUnderlay(10, 10, 0, TileRenderTestData.Grass);
        return doc;
    }

    static void AssertUploaded(RecordingTileWorldScene scene, TileGroundSurfaceSnapshot surface)
    {
        GltfMesh mesh = scene.GroundMeshes.Values.Last();
        Assert.Equal(mesh.Vertices.Select(v => v.Position).ToArray(), surface.Payload.Positions.ToArray());
        Assert.Equal(mesh.Indices32, surface.Payload.Indices.ToArray());
    }
}
