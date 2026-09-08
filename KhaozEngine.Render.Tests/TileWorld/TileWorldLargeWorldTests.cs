using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>End-to-end synthetic coverage for TileWorld's large-world prop cluster integration.</summary>
public sealed class TileWorldLargeWorldTests
{
    const int RegionCount = 25;
    static readonly RegionCoord Centre = new(12, 12);

    [Fact]
    public void Profile_settles_selected_trees_into_one_live_cluster_per_resident_region()
    {
        using var tmp = new TempDir();
        TileWorldSource source = OpenForest(tmp, out TileWorldCatalogs catalogs);
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualDispatcher();
        var options = new TileWorldViewOptions { PropLayers = new[] { TreeLayer() } };
        using var view = new TileWorldView(scene, source.Document, catalogs, new ForestResolver(), options,
            new TileWorldBuildQueueOptions
            {
                MaxConcurrentBuilds = 4,
                MaxFullGroundAppliesPerPump = 64,
                MaxCoarseGroundAppliesPerPump = 64,
                MaxHlodAppliesPerPump = 64,
            }, dispatcher);
        var residency = new TileRegionResidency(
            source, view,
            new TileResidencyConfig(LoadRadius: 10, UnloadRadius: 12, MaxLoadsPerUpdate: int.MaxValue),
            new TileRegionResidencyProfile(GameplayRadius: 4, DecorRadius: 10, UnloadRadius: 12));

        residency.PrimeAround(TileRenderTestData.CentreOf(Centre));
        Settle(view, scene, dispatcher, Focus(Centre));

        RegionCoord[] all = Enumerable.Range(0, RegionCount)
            .SelectMany(rz => Enumerable.Range(0, RegionCount).Select(rx => new RegionCoord(rx, rz)))
            .ToArray();
        Assert.Equal(81, all.Count(region => view.ResidencyOf(region) == TileRegionResidencyState.Gameplay));
        Assert.Equal(360, all.Count(region => view.ResidencyOf(region) == TileRegionResidencyState.Decor));
        Assert.Equal(184, all.Count(region => view.ResidencyOf(region) == TileRegionResidencyState.Unloaded));
        Assert.DoesNotContain(scene.PropDraws.SelectMany(draw => draw.Placements), placement => placement.Id == "tree");
        Assert.Equal(441, scene.ClusterBuildRequests.Select(request => request.Key).Distinct().Count());
        Assert.Equal(441, scene.ClusterMeshLoads.Count);
        Assert.Equal(441, scene.LiveClusterMeshCount);

        int loadsAtRest = scene.ClusterMeshLoads.Count;
        int liveAtRest = scene.AliveMeshCount;
        for (int frame = 0; frame < 3; frame++)
        {
            scene.ClearFrame();
            view.Draw(Focus(Centre));
        }

        Assert.Equal(loadsAtRest, scene.ClusterMeshLoads.Count);
        Assert.Equal(liveAtRest, scene.AliveMeshCount);
        int passesBefore = scene.ClusterDrawPasses;
        scene.ClearFrame();
        view.Draw(Focus(Centre));
        Assert.Equal(passesBefore + 1, scene.ClusterDrawPasses);

        var moved = new RegionCoord(15, 12);
        residency.PrimeAround(TileRenderTestData.CentreOf(moved));
        Settle(view, scene, dispatcher, Focus(moved));

        Assert.Equal(462, view.LoadedRegionCount);
        Assert.Equal(view.LoadedRegionCount, scene.LiveClusterMeshCount);
        Assert.True(scene.ClusterMeshUnloads.Count >= 21,
            $"moving three regions must unload at least the 21 clusters beyond hysteresis, got {scene.ClusterMeshUnloads.Count}");
        int loadsAfterMove = scene.ClusterMeshLoads.Count;
        int aliveAfterMove = scene.AliveMeshCount;
        for (int frame = 0; frame < 3; frame++) view.Draw(Focus(moved));
        Assert.Equal(loadsAfterMove, scene.ClusterMeshLoads.Count);
        Assert.Equal(aliveAfterMove, scene.AliveMeshCount);
    }

    [Fact]
    public void Two_layers_share_one_background_concurrency_cap_without_superseding_each_other()
    {
        var document = new TileWorldDocument
        {
            Id = "two-layers",
            DisplayName = "Two layers",
            PlaneCount = 1,
        };
        document.GetOrCreateRegion(default);
        document.SetUnderlay(0, 0, 0, TileRenderTestData.Grass);
        document.AddObject("tree", 10, 10, 0, 0);
        document.AddObject("bush", 20, 20, 0, 0);
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualDispatcher();
        var options = new TileWorldViewOptions
        {
            PropLayers = new[]
            {
                TreeLayer("trees", "tree"),
                TreeLayer("shrubs", "bush"),
            },
        };
        using var view = new TileWorldView(scene, document, TileRenderTestData.Catalogs, new ForestResolver(),
            options, new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 1, MaxHlodAppliesPerPump = 2 },
            dispatcher);

        view.LoadRegion(default);

        Assert.Equal(1, dispatcher.PendingCount);
        dispatcher.RunAll();
        view.Draw(Vector3.Zero);
        Assert.Single(scene.ClusterMeshLoads);
        Assert.Equal(1, dispatcher.PendingCount);

        dispatcher.RunAll();
        view.Draw(Vector3.Zero);

        Assert.Equal(2, scene.ClusterMeshLoads.Count);
        Assert.Equal(new[] { "shrubs", "trees" }, scene.ClusterBuildRequests
            .Select(request => request.Key.LayerId).OrderBy(id => id));
        Assert.Equal(2, scene.LiveClusterMeshCount);
    }

    [Fact]
    public void Override_rejects_the_old_generation_and_replaces_only_its_cluster_with_the_stump()
    {
        var document = new TileWorldDocument
        {
            Id = "override-generation",
            DisplayName = "Override generation",
            PlaneCount = 1,
        };
        document.GetOrCreateRegion(default);
        document.SetUnderlay(0, 0, 0, TileRenderTestData.Grass);
        TileObject tree = document.AddObject("tree", 10, 10, 0, 0);
        TileWorldCatalogs catalogs = TileWorldCatalogs.Merge(
            TileRenderTestData.Catalogs,
            TileWorldCatalogs.LoadJson(
                """
                {
                  "archetypes": [
                    { "id": "stump", "name": "stump", "meshRef": "greybox/stump.glb", "sizeX": 1, "sizeZ": 1, "collisionKind": "Solid" }
                  ]
                }
                """, "stump-test"));
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualDispatcher();
        TilePropLayerDefinition trees = TreeLayer() with
        {
            ArchetypeIds = new HashSet<string>(StringComparer.Ordinal) { "tree", "stump" },
        };
        using var view = new TileWorldView(scene, document, catalogs, new ForestResolver(),
            new TileWorldViewOptions { PropLayers = new[] { trees } },
            new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 1, MaxHlodAppliesPerPump = 1 }, dispatcher);
        view.LoadRegion(default);

        Assert.True(view.OverrideArchetype(tree.Id, "stump"));
        dispatcher.RunAt(0);
        view.Draw(Vector3.Zero);

        Assert.Empty(scene.ClusterMeshLoads);
        Assert.Equal(1, dispatcher.PendingCount);

        dispatcher.RunAt(0);
        view.Draw(Vector3.Zero);

        Assert.Single(scene.ClusterMeshLoads);
        Assert.Equal(2, scene.ClusterBuildRequests.Count);
        Assert.Equal(1, scene.LiveClusterMeshCount);
        Assert.Contains(scene.ClusterPropDraws.SelectMany(draw => draw.Placements),
            placement => placement.Id == "stump");
        Assert.DoesNotContain(scene.ClusterPropDraws.SelectMany(draw => draw.Placements),
            placement => placement.Id == "tree");
    }

    static TileWorldSource OpenForest(TempDir tmp, out TileWorldCatalogs catalogs)
    {
        var document = new TileWorldDocument
        {
            Id = "large-world",
            DisplayName = "Large world",
            PlaneCount = 1,
        };
        for (int rz = 0; rz < RegionCount; rz++)
            for (int rx = 0; rx < RegionCount; rx++)
            {
                var region = new RegionCoord(rx, rz);
                document.GetOrCreateRegion(region);
                document.SetUnderlay(region.OriginX, region.OriginZ, 0, TileRenderTestData.Grass);
                document.AddObject("tree", region.OriginX + 32, region.OriginZ + 32, 0, 0);
            }
        string directory = tmp.Sub("large-world");
        TileWorldFile.Save(document, directory);
        catalogs = TileRenderTestData.Catalogs;
        return TileWorldSource.Open(directory);
    }

    static TilePropLayerDefinition TreeLayer() => TreeLayer("trees", "tree");

    static TilePropLayerDefinition TreeLayer(string id, string archetypeId) => new()
    {
        Id = id,
        ArchetypeIds = new HashSet<string>(StringComparer.Ordinal) { archetypeId },
        DrawRadius = 576f,
        LodDistance = 64f,
        LodCrossfadeWidth = 16f,
        HlodDistance = 192f,
        HlodCrossfadeWidth = 32f,
        HlodWeldCell = 1.5f,
    };

    static Vector3 Focus(RegionCoord region) => new(
        region.OriginX + TileRegion.Size * 0.5f,
        0f,
        -(region.OriginZ + TileRegion.Size * 0.5f));

    static void Settle(TileWorldView view, RecordingTileWorldScene scene, ManualDispatcher dispatcher,
                       Vector3 focus)
    {
        for (int iteration = 0; iteration < 512; iteration++)
        {
            dispatcher.RunAll();
            scene.ClearFrame();
            view.Draw(focus);
            if (dispatcher.PendingCount == 0 && scene.LiveClusterMeshCount == view.LoadedRegionCount) return;
        }
        throw new InvalidOperationException(
            $"The large-world queues did not settle. pending={dispatcher.PendingCount}, " +
            $"clusters={scene.LiveClusterMeshCount}, loaded={view.LoadedRegionCount}, " +
            $"clusterLoads={scene.ClusterMeshLoads.Count}.");
    }

    sealed class ForestResolver : ITileMeshResolver, ITileLodMeshResolver
    {
        readonly GltfMesh _tree = MeshPrimitives.Box(4f);
        public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype) =>
            new[] { new GltfMeshPart(_tree, default) };
        public IReadOnlyList<GltfMeshPart>? ResolveLod(TileObjectArchetype archetype) =>
            new[] { new GltfMeshPart(_tree, default) };
        public GltfMesh? ResolveFlatForHlod(TileObjectArchetype archetype) => _tree;
    }

    sealed class ManualDispatcher : IChunkBuildDispatcher
    {
        readonly List<Action> _pending = new();
        public int PendingCount => _pending.Count;
        public void Schedule(Action build) => _pending.Add(build);
        public void RunAt(int index)
        {
            Action build = _pending[index];
            _pending.RemoveAt(index);
            build();
        }
        public void RunAll()
        {
            while (_pending.Count > 0)
            {
                RunAt(0);
            }
        }
        public void Drain() => RunAll();
    }
}
