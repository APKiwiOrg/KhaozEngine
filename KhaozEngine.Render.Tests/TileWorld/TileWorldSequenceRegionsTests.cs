using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldSequenceRegionsTests
{
    [Fact]
    public void A_snapshot_settles_every_selected_prop_cluster_before_the_first_draw()
    {
        var doc = new TileWorldDocument { PlaneCount = 1 };
        for (int x = 0; x < 3; x++)
        {
            doc.GetOrCreateRegion(new RegionCoord(x, 0));
            doc.SetUnderlay(x * TileRegion.Size, 0, 0, TileRenderTestData.Grass);
            doc.AddObject("tree", x * TileRegion.Size + 1, 1, 0, 0);
        }
        var options = new TileWorldViewOptions
        {
            PropLayers = new[]
            {
                new TilePropLayerDefinition
                {
                    Id = "trees", ArchetypeIds = new HashSet<string>(StringComparer.Ordinal) { "tree" },
                    DrawRadius = 576f, LodDistance = 64f, HlodDistance = 192f,
                },
            },
        };
        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(),
            options, new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 1, MaxHlodAppliesPerPump = 0 },
            new HeldDispatcher());
        var regions = new TileWorldSequenceRegions(view, doc);
        regions.Update(default);
        view.Draw(Vector3.Zero);
        Assert.Equal(3, scene.ClusterPropDraws.Sum(draw => draw.Placements.Count));
    }

    [Fact]
    public void A_region_transition_keeps_overlapping_mesh_handles_and_only_replaces_the_ring_delta()
    {
        var doc = new TileWorldDocument();
        var origin = new RegionCoord(0, 0);
        var adjacent = new RegionCoord(1, 0);
        var far = new RegionCoord(8, 0);
        foreach (RegionCoord region in new[] { origin, adjacent, far })
        {
            doc.GetOrCreateRegion(region);
            doc.SetUnderlay(region.OriginX, region.OriginZ, 0, TileRenderTestData.Grass);
        }
        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs,
            new GreyboxMeshResolver(doc.TileSize, doc.PlaneHeight));
        var regions = new TileWorldSequenceRegions(view, doc);
        regions.Update(origin);
        view.Draw(Vector3.Zero);
        var originalHandles = scene.Drawn.Select(draw => draw.Handle).ToArray();
        Assert.Equal(2, originalHandles.Length);
        scene.ClearFrame();

        regions.Update(adjacent);
        view.Draw(Vector3.Zero);
        Assert.Equal(2, scene.MeshLoads.Count);
        Assert.Empty(scene.MeshUnloads);
        Assert.Equal(originalHandles, scene.Drawn.Select(draw => draw.Handle).ToArray());

        regions.Update(far);
        Assert.Equal(3, scene.MeshLoads.Count);
        Assert.Equal(2, scene.MeshUnloads.Count);
        Assert.Single(view.LoadedRegions);
        Assert.Equal(TileRegionResidencyState.Gameplay, view.ResidencyOf(far));
        Assert.Equal(0, view.PendingRebuilds);
    }

    sealed class HeldDispatcher : IChunkBuildDispatcher
    {
        readonly List<Action> _work = new();
        public void Schedule(Action build) => _work.Add(build);
        public void Drain()
        {
            foreach (Action build in _work) build();
            _work.Clear();
        }
    }
}
