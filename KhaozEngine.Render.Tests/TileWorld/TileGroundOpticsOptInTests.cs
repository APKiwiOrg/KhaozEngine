using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileGroundOpticsOptInTests
{
    static readonly RegionCoord Origin = new(0, 0);
    static readonly Vector3 Focus = new(16, 0, -16);

    [Theory]
    [InlineData(TileGroundLod.Full)]
    [InlineData(TileGroundLod.Coarse4)]
    public void LegacyFactoryDoesNoOpticalAuditReadsForDrawableGround(TileGroundLod lod)
    {
        TileWorldDocument doc = World();
        var catalogs = TileRenderTestData.Catalogs;
        GltfMesh legacy = TileGroundMesher.Build(doc, catalogs, Origin, 0, lod)!;
        TileGroundCpuBuild built = TileGroundCpuBuild.Build(doc, catalogs, Origin, 0, lod,
            new TileGroundMesherOptions(), captureOpticalSurface: false);
        Assert.Null(built.Surface);
        Assert.Equal(0, built.AuditReadCount);
        Assert.Equal(legacy.Vertices, built.Mesh!.Vertices);
        Assert.Equal(legacy.Indices32, built.Mesh.Indices32);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyWorkersAndPrimeKeepSourcesAbsentThroughLodChanges(bool prime)
    {
        TileWorldDocument doc = World();
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        using TileWorldView view = ProfileFixture.CreateView(scene, doc, dispatcher, new ProfileBuildWitness());
        view.LoadRegion(Origin, TileRegionResidencyState.Decor);
        view.Draw(Focus);
        dispatcher.RunAll();
        view.Draw(Focus);
        Assert.NotEmpty(scene.GroundMeshes);
        Assert.Null(view.AcceptedGroundSurface(Origin, 0));
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        if (prime) view.SettleForCapture(Origin);
        else
        {
            view.Draw(Focus);
            dispatcher.RunAll();
            view.Draw(Focus);
        }
        Assert.NotEmpty(scene.GroundMeshes);
        Assert.Null(view.AcceptedGroundSurface(Origin, 0));
        view.LoadRegion(Origin, TileRegionResidencyState.Decor);
        view.Draw(Focus);
        dispatcher.RunAll();
        view.Draw(Focus);
        Assert.Null(view.AcceptedGroundSurface(Origin, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdmissionRejectsMissingOrWrongKindSource(bool opticalView)
    {
        TileWorldDocument doc = World();
        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(),
            new TileWorldViewOptions { WaterDepthMode = opticalView ? TileWaterDepthMode.AuthoredBed : TileWaterDepthMode.SceneDepth });
        TileGroundCpuBuild wrong = TileGroundCpuBuild.Build(doc, TileRenderTestData.Catalogs, Origin, 0,
            TileGroundLod.Full, new TileGroundMesherOptions(), captureOpticalSurface: !opticalView);
        var water = new TileWaterPreparation([], new TileWaterSourceStamp(doc, [], null), 0, null);
        Assert.Null(view.StageGround(Origin, 0, wrong, water));
        Assert.Empty(scene.GroundMeshes);
    }

    [Fact]
    public void ChangingPolicyByRecreatingViewClearsAcceptedAndPendingSources()
    {
        TileWorldDocument doc = World();
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        var options = new TileWorldViewOptions { WaterDepthMode = TileWaterDepthMode.AuthoredBed };
        using TileWorldView old = ProfileFixture.CreateView(scene, doc, dispatcher, new ProfileBuildWitness(), options);
        old.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        old.SettleForCapture(Origin);
        Assert.NotNull(old.AcceptedGroundSurface(Origin, 0));
        old.MarkDirty(Origin, 0);
        old.Draw(Focus);
        Assert.True(dispatcher.PendingCount > 0);
        old.Dispose();
        Assert.Null(old.AcceptedGroundSurface(Origin, 0));
        using var legacy = new TileWorldView(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver());
        legacy.LoadRegion(Origin);
        int loads = scene.MeshLoads.Count;
        dispatcher.RunAll();
        Assert.Null(legacy.AcceptedGroundSurface(Origin, 0));
        Assert.Equal(loads, scene.MeshLoads.Count);
        legacy.Dispose();
        // Enabling the policy creates a new source from a new build, even for an empty render mesh.
        doc.SetUnderlay(10, 10, 0, 0);
        using var enabled = new TileWorldView(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(), options);
        Assert.Null(enabled.AcceptedGroundSurface(Origin, 0));
        enabled.LoadRegion(Origin);
        Assert.True(enabled.AcceptedGroundSurface(Origin, 0)!.IsEmpty);
    }

    [Fact]
    public void DetachedChoiceSurvivesLiveDrawAndMesherOptionChanges()
    {
        TileWorldDocument doc = World();
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        var options = new TileWorldViewOptions { WaterDepthMode = TileWaterDepthMode.AuthoredBed };
        using TileWorldView view = ProfileFixture.CreateView(scene, doc, dispatcher, new ProfileBuildWitness(), options);
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        view.Draw(Focus);
        options.DrawWater = false;
        options.Mesher = new TileGroundMesherOptions { OverlayFeatherWidthMetres = 0.1f };
        dispatcher.RunAll();
        view.Draw(Focus);
        Assert.NotNull(view.AcceptedGroundSurface(Origin, 0));
    }

    [Fact]
    public void LegacyDetachmentDoesNotCopyOpticalAvailabilityManifest()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = World();
        var missing = new RegionCoord(1, 0);
        doc.GetOrCreateRegion(missing);
        string directory = tmp.Sub("opt-in-manifest");
        TileWorldFile.Save(doc, directory);
        TileWorldSource source = TileWorldSource.Open(directory);
        source.EnsureLoaded(Origin);
        TileWorldDocument legacy = TileWorldBuildSnapshot.CaptureGround(source.Document, Origin);
        TileWorldDocument optical = TileWorldBuildSnapshot.CaptureGround(source.Document, Origin, captureOpticalProvenance: true);
        Directory.Delete(directory, recursive: true);
        Span<RegionCoord> inspected = stackalloc RegionCoord[4];
        Assert.True(legacy.TryReadCornerHeight(64, 10, 0, inspected, out _, out _, out _));
        Assert.False(optical.TryReadCornerHeight(64, 10, 0, inspected, out _, out _, out RegionCoord blocked));
        Assert.Equal(missing, blocked);
        Assert.Null(legacy.Source);
        Assert.Null(optical.Source);
    }

    static TileWorldDocument World()
    {
        var doc = new TileWorldDocument { Id = "opt-in", DisplayName = "Opt-in", PlaneCount = 1 };
        doc.GetOrCreateRegion(Origin);
        doc.SetUnderlay(10, 10, 0, TileRenderTestData.Grass);
        return doc;
    }
}
