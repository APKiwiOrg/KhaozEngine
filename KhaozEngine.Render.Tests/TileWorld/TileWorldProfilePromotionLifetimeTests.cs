using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Generation, fallback, failure and release rules for profile ground and cover that stream through
/// the shared queue.</summary>
public sealed class TileWorldProfilePromotionLifetimeTests
{
    static readonly RegionCoord Origin = new(0, 0);
    static readonly Vector3 Focus = ProfileFixture.Focus(Origin);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stale_ground_and_cover_cannot_cross_demotion_or_reload(bool oldCompletesFirst)
    {
        TileWorldDocument doc = Meadow(regions: 1);
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        using TileWorldView view = ProfileFixture.CreateView(scene, doc, dispatcher, new ProfileBuildWitness());

        // The first gameplay generation is still held when the region demotes and promotes again.
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        int held = dispatcher.PendingCount;
        Assert.Equal(2, held);
        view.LoadRegion(Origin, TileRegionResidencyState.Decor);
        doc.SetCornerHeightCm(10, 10, 0, 250);
        doc.SetFoliageLayer(ProfileFixture.Meadow(1, 1, spacing: 8f));
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        Complete(dispatcher, held, oldCompletesFirst);
        Pump(view, dispatcher);

        int accepted = Drawn(scene, view, doc, Origin)!.Value;
        Assert.Equal(2.5f, scene.GroundMeshes[accepted].Vertices.Max(v => v.Position.Y), 3);
        IReadOnlyList<GroundCoverInstance> cover = view.CoverIn(Origin);
        Assert.Equal(ExpectedCover(doc, Origin), cover.Count);
        Assert.Equal(cover.Count, view.GeneratedCoverCount);
        Pump(view, dispatcher);
        Assert.Equal(accepted, Drawn(scene, view, doc, Origin));
        Assert.Same(cover, view.CoverIn(Origin));

        // The same key unloads and reloads while the previous residency's generation is still held.
        view.UnloadRegion(Origin);
        Assert.Single(scene.FoliageReleases, c => ReferenceEquals(c, cover));
        Assert.Equal(0, view.GeneratedCoverCount);
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        held = dispatcher.PendingCount;
        Assert.Equal(2, held);
        view.UnloadRegion(Origin);
        doc.SetCornerHeightCm(10, 10, 0, 300);
        doc.SetFoliageLayer(ProfileFixture.Meadow(1, 1, spacing: 16f));
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        Complete(dispatcher, held, oldCompletesFirst);
        Pump(view, dispatcher);

        int reloaded = Drawn(scene, view, doc, Origin)!.Value;
        Assert.Equal(3f, scene.GroundMeshes[reloaded].Vertices.Max(v => v.Position.Y), 3);
        IReadOnlyList<GroundCoverInstance> reloadedCover = view.CoverIn(Origin);
        Assert.Equal(ExpectedCover(doc, Origin), reloadedCover.Count);
        Assert.Equal(reloadedCover.Count, view.GeneratedCoverCount);
        Assert.Single(scene.MeshUnloads, h => h.Index == accepted);
        Assert.Single(scene.FoliageReleases, c => ReferenceEquals(c, cover));

        // Work still held at Dispose drains without an upload, and every handle is released once.
        doc.SetCornerHeightCm(20, 20, 0, 100);
        view.MarkDirty(Origin, 0);
        view.Flush();
        Assert.True(dispatcher.PendingCount > 0);
        int loads = scene.MeshLoads.Count;
        view.Dispose();
        Assert.Equal(loads, scene.MeshLoads.Count);
        Assert.Equal(0, dispatcher.PendingCount);
        Assert.Equal(0, scene.AliveMeshCount);
        Assert.Single(scene.FoliageReleases, c => ReferenceEquals(c, reloadedCover));
        Assert.Single(scene.MeshUnloads, h => h.Index == reloaded);
    }

    [Fact]
    public void Pending_coarse_fallback_survives_promotion_and_cannot_replace_accepted_full()
    {
        TileWorldDocument doc = Meadow(regions: 2);
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        using TileWorldView view = ProfileFixture.CreateView(scene, doc, dispatcher, new ProfileBuildWitness());
        var late = new RegionCoord(1, 0);
        int coarseVertices = TileGroundMesher.Build(doc, TileRenderTestData.Catalogs, Origin, 0,
            TileGroundLod.Coarse4)!.Vertices.Length;
        int fullVertices = TileGroundMesher.Build(doc, TileRenderTestData.Catalogs, Origin, 0,
            TileGroundLod.Full)!.Vertices.Length;
        Assert.NotEqual(coarseVertices, fullVertices);

        view.LoadRegion(late, TileRegionResidencyState.Decor);
        view.LoadRegion(Origin, TileRegionResidencyState.Decor);
        Assert.Equal(2, dispatcher.PendingCount);
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        view.LoadRegion(late, TileRegionResidencyState.Gameplay);

        // The origin's coarse build was already pending when it promoted. It lands first and draws.
        dispatcher.RunAt(1);
        int fallback = Drawn(scene, view, doc, Origin)!.Value;
        Assert.Equal(coarseVertices, scene.GroundMeshes[fallback].Vertices.Length);
        Assert.Null(Drawn(scene, view, doc, late));

        // Everything but the late region's coarse build completes, and full ground replaces the fallback.
        while (dispatcher.PendingCount > 1) dispatcher.RunAt(1);
        Pump(view, dispatcher, runWorkers: false);
        int full = Drawn(scene, view, doc, Origin)!.Value;
        Assert.Equal(fullVertices, scene.GroundMeshes[full].Vertices.Length);
        Assert.Single(scene.MeshUnloads, h => h.Index == fallback);
        int accepted = Drawn(scene, view, doc, late)!.Value;
        Assert.Equal(fullVertices, scene.GroundMeshes[accepted].Vertices.Length);
        int uploads = scene.MeshLoads.Count;

        // The late coarse result completes after full ground was accepted and is dropped.
        dispatcher.RunAt(0);
        Pump(view, dispatcher);
        Assert.Equal(accepted, Drawn(scene, view, doc, late));
        Assert.Equal(uploads, scene.MeshLoads.Count);
        Assert.DoesNotContain(scene.MeshUnloads, h => h.Index == accepted);
        Assert.Equal(3, scene.MeshLoads.Count);
    }

    [Fact]
    public void Empty_planes_do_not_leak_handles_or_starve_cover()
    {
        TileWorldDocument doc = Meadow(regions: 2, planes: 4);
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        var east = new RegionCoord(1, 0);
        using TileWorldView view = ProfileFixture.CreateView(scene, doc, dispatcher, new ProfileBuildWitness(), null,
            new TileWorldBuildQueueOptions
            {
                MaxConcurrentBuilds = 64,
                MaxFullGroundAppliesPerPump = 1,
                MaxCoverAppliesPerPump = 1,
            });
        int baseline = scene.AliveMeshCount;

        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        view.LoadRegion(east, TileRegionResidencyState.Gameplay);
        dispatcher.RunAll();
        scene.ClearFrame();
        view.Draw(Focus);

        // Six of the eight ground results are empty planes, and they hold no cover back.
        Assert.Single(new[] { Origin, east }, r => view.CoverIn(r).Count > 0);
        Assert.Equal(baseline + 1, scene.AliveMeshCount);
        Pump(view, dispatcher);
        Assert.Equal(baseline + 2, scene.AliveMeshCount);
        Assert.Equal(ExpectedCover(doc, Origin), view.CoverIn(Origin).Count);
        Assert.Equal(ExpectedCover(doc, east), view.CoverIn(east).Count);
        scene.ClearFrame();
        view.Draw(Focus);
        Assert.Equal(2, scene.Drawn.Count);

        // Rebuilding every plane replaces the two drawable meshes and leaves nothing behind.
        for (int plane = 0; plane < 4; plane++)
        {
            view.MarkDirty(Origin, plane);
            view.MarkDirty(east, plane);
        }
        Pump(view, dispatcher);
        Assert.Equal(baseline + 2, scene.AliveMeshCount);
        Assert.Equal(2, scene.MeshUnloads.Count);
        Assert.Equal(view.CoverIn(Origin).Count + view.CoverIn(east).Count, view.GeneratedCoverCount);

        view.Dispose();
        Assert.Equal(0, scene.AliveMeshCount);
    }

    [Fact]
    public void Failed_promotion_keeps_previous_ground_and_clusters()
    {
        TileWorldDocument doc = Meadow(regions: 1);
        doc.AddObject("tree", 32, 32, 0, 0);
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        var witness = new ProfileBuildWitness();
        using TileWorldView view = ProfileFixture.CreateView(scene, doc, dispatcher, witness,
            new TileWorldViewOptions { PropLayers = new[] { ProfileFixture.Trees() } });
        view.LoadRegion(Origin, TileRegionResidencyState.Decor);
        Pump(view, dispatcher);
        int coarse = Drawn(scene, view, doc, Origin)!.Value;
        MeshHandle cluster = Assert.Single(scene.ClusterMeshLoads);

        witness.Refuse = key => key.Region == Origin &&
            key.Kind is TileWorldBuildKind.FullGround or TileWorldBuildKind.Cover;
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        dispatcher.RunAll();
        int failures = 0;
        for (int frame = 0; frame < 8; frame++)
        {
            try
            {
                scene.ClearFrame();
                view.Draw(Focus);
                if (view.TrackedProfileBuilds == 0) break;
            }
            catch (InvalidOperationException error)
            {
                Assert.IsType<InvalidOperationException>(error.InnerException);
                failures++;
            }
        }

        Assert.Equal(2, failures);
        Assert.Equal(TileRegionResidencyState.Gameplay, view.ResidencyOf(Origin));
        Assert.Equal(coarse, Drawn(scene, view, doc, Origin));
        Assert.DoesNotContain(scene.MeshUnloads, h => h.Index == coarse);
        Assert.Empty(scene.ClusterMeshUnloads);
        Assert.Equal(1, scene.LiveClusterMeshCount);
        Assert.Equal(cluster, Assert.Single(scene.ClusterMeshLoads));
        int merged = scene.ClusterMergedDraws.Count;
        view.Draw(Focus + new Vector3(300f, 0f, 0f));
        Assert.Contains(scene.ClusterMergedDraws.Skip(merged), draw => draw.Handle.Equals(cluster));
        Assert.Equal(0, view.GeneratedCoverCount);
        Assert.Empty(view.CoverIn(Origin));
    }

    static TileWorldDocument Meadow(int regions, int planes = 1)
    {
        var doc = new TileWorldDocument { Id = "profile-lifetime", DisplayName = "Profile lifetime", PlaneCount = planes };
        for (int rx = 0; rx < regions; rx++)
        {
            var region = new RegionCoord(rx, 0);
            doc.GetOrCreateRegion(region);
            for (int z = 0; z < TileRegion.Size; z++)
                for (int x = 0; x < TileRegion.Size; x++)
                    doc.SetUnderlay(region.OriginX + x, z, 0, TileRenderTestData.Grass);
        }
        doc.SetFoliageLayer(ProfileFixture.Meadow(regions, 1));
        return doc;
    }

    static int ExpectedCover(TileWorldDocument doc, RegionCoord region) =>
        TileCoverBuildInput.Generate(doc, TileRenderTestData.Catalogs, region,
            new TileGroundMesherOptions().SmoothNormals, doc.FoliageLayers).Count;

    // Runs the held bodies with the previous generation either before or after the replacement's.
    static void Complete(ManualTileBuildDispatcher dispatcher, int held, bool oldFirst)
    {
        if (oldFirst)
        {
            for (int i = 0; i < held; i++) dispatcher.RunAt(0);
            dispatcher.RunAll();
            return;
        }
        while (dispatcher.PendingCount > held) dispatcher.RunAt(held);
        dispatcher.RunAll();
    }

    // Draws a bounded number of frames until the view's profile queue holds nothing.
    static void Pump(TileWorldView view, ManualTileBuildDispatcher dispatcher, bool runWorkers = true)
    {
        for (int frame = 0; frame < 64; frame++)
        {
            if (runWorkers) dispatcher.RunAll();
            view.Draw(Focus);
            if (view.TrackedProfileBuilds == 0 && view.PendingRebuilds == 0 &&
                (!runWorkers || dispatcher.PendingCount == 0))
                return;
        }
        throw new InvalidOperationException($"the profile queue did not settle, {view.TrackedProfileBuilds} tracked.");
    }

    static int? Drawn(RecordingTileWorldScene scene, TileWorldView view, TileWorldDocument doc, RegionCoord region)
    {
        scene.ClearFrame();
        view.Draw(Focus);
        Matrix4x4 world = TileGroundMesher.WorldMatrix(doc, region);
        (MeshHandle Handle, Matrix4x4 World)[] matches = scene.Drawn.Where(d => d.World == world).ToArray();
        return matches.Length == 0 ? null : Assert.Single(matches).Handle.Index;
    }
}
