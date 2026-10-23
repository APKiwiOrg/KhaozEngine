using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldRoofPropLayerTests
{
    [Fact]
    public void Roof_only_layer_is_accepted_and_never_reaches_ordinary_draws()
    {
        using var rig = new Rig();
        rig.View.RoofMode = RoofVisibility.AlwaysVisible;
        rig.View.Draw(Vector3.Zero);

        Assert.Empty(rig.Scene.PropDraws);
        Assert.Empty(rig.Scene.ShadowOnlyPropDraws);
        Assert.Equal(2, rig.Scene.ClusterPropDraws.SelectMany(d => d.Placements).Count());
        Assert.Equal(2, rig.View.LastDrawnProps);
    }

    [Fact]
    public void Roof_layer_refuses_interior_cut_before_uploading()
    {
        var scene = new RecordingTileWorldScene();
        ArgumentException error = Assert.Throws<ArgumentException>(() => new TileWorldView(scene,
            World(), TileRenderTestData.Catalogs, new Resolver(),
            new TileWorldViewOptions { PropLayers = new[] { Roofs() with { CutInsideInteriors = true } } }));

        Assert.Contains("roofs", error.Message, StringComparison.Ordinal);
        Assert.Contains("CutInsideInteriors", error.Message, StringComparison.Ordinal);
        Assert.Empty(scene.MaterialLoads);
        Assert.Empty(scene.PropMeshLoads);
    }

    [Fact]
    public void Indoor_observer_hides_exactly_its_house_through_the_layer()
    {
        using var rig = new Rig();
        rig.View.Observer = new TileCoord(4, 4, 0);
        rig.View.Draw(Vector3.Zero);

        Assert.True(rig.View.ObserverIndoors);
        Assert.True(rig.View.IsObserverInterior(new TileCoord(4, 4, 0)));
        Assert.False(rig.View.IsObserverInterior(new TileCoord(12, 4, 0)));
        Assert.Equal(12.5f, Assert.Single(rig.Scene.ClusterPropDraws.SelectMany(d => d.Placements)).X);
        Assert.Equal(1, rig.View.LastDrawnProps);
        Assert.Equal(1, rig.View.LastShadowOnlyProps);
    }

    [Theory]
    [InlineData(RoofVisibility.AlwaysHidden, 0f, 0, 2)]
    [InlineData(RoofVisibility.AlwaysVisible, 0f, 2, 0)]
    [InlineData(RoofVisibility.AlwaysHidden, 192f, 0, 2)]
    [InlineData(RoofVisibility.AlwaysVisible, 192f, 2, 0)]
    [InlineData(RoofVisibility.AlwaysHidden, 300f, 0, 2)]
    [InlineData(RoofVisibility.AlwaysVisible, 300f, 2, 0)]
    public void Mode_overrides_apply_to_individuals_and_merged_handles(RoofVisibility mode, float distance,
        int visible, int hidden)
    {
        using var rig = new Rig();
        rig.View.RoofMode = mode;
        rig.View.Draw(Focus(distance));

        Assert.Equal(visible, rig.View.LastDrawnProps);
        Assert.Equal(hidden, rig.View.LastShadowOnlyProps);
        if (mode == RoofVisibility.AlwaysHidden)
        {
            Assert.Empty(rig.Scene.ClusterPropDraws);
            Assert.Empty(rig.Scene.ClusterMergedDraws);
            if (distance < 208f) Assert.Single(rig.Scene.ClusterShadowPropDraws);
            if (distance > 176f) Assert.Single(rig.Scene.ClusterShadowMergedDraws);
        }
        else
        {
            Assert.Empty(rig.Scene.ClusterShadowPropDraws);
            Assert.Empty(rig.Scene.ClusterShadowMergedDraws);
            if (distance < 208f) Assert.Single(rig.Scene.ClusterPropDraws);
            if (distance > 176f) Assert.Single(rig.Scene.ClusterMergedDraws);
        }
        if (distance == 192f)
        {
            float floor = mode == RoofVisibility.AlwaysHidden
                ? Assert.Single(rig.Scene.ClusterShadowPropDraws).DissolveFloor
                : Assert.Single(rig.Scene.ClusterPropDraws).DissolveFloor;
            Assert.Equal(0.5f, floor);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partially_hidden_cluster_falls_back_to_individuals_even_at_hlod_distance(bool decor)
    {
        using var rig = new Rig();
        rig.View.Observer = new TileCoord(4, 4, 0);
        if (decor) rig.View.LoadRegion(TileRenderTestData.Region, TileRegionResidencyState.Decor);
        rig.View.Draw(Focus(300f));

        Assert.Empty(rig.Scene.ClusterMergedDraws);
        Assert.Equal(12.5f, Assert.Single(Assert.Single(rig.Scene.ClusterPropDraws).Placements).X);
        Assert.Equal(4.5f, Assert.Single(Assert.Single(rig.Scene.ClusterShadowPropDraws).Placements).X);
        Assert.Equal(0f, Assert.Single(rig.Scene.ClusterPropDraws).DissolveFloor);
        Assert.Equal(0f, Assert.Single(rig.Scene.ClusterShadowPropDraws).DissolveFloor);
        Assert.Equal(1, rig.View.LastDrawnProps);
        Assert.Equal(1, rig.View.LastShadowOnlyProps);
    }

    [Theory]
    [InlineData(RoofVisibility.AlwaysVisible)]
    [InlineData(RoofVisibility.AlwaysHidden)]
    public void Far_roof_cluster_keeps_merged_geometry_through_decor_residency(RoofVisibility mode)
    {
        using var rig = new Rig();
        rig.View.Draw(Vector3.Zero);
        MeshHandle handle = Assert.Single(rig.Scene.ClusterMeshLoads);
        rig.ClearDraws();
        rig.View.LoadRegion(TileRenderTestData.Region, TileRegionResidencyState.Decor);
        rig.View.RoofMode = mode;
        rig.View.Draw(Focus(300f));

        Assert.Empty(rig.Scene.ClusterMeshUnloads);
        Assert.Empty(rig.Scene.ClusterPropDraws);
        Assert.Empty(rig.Scene.ClusterShadowPropDraws);
        Assert.Equal(handle, mode == RoofVisibility.AlwaysHidden
            ? Assert.Single(rig.Scene.ClusterShadowMergedDraws).Handle
            : Assert.Single(rig.Scene.ClusterMergedDraws).Handle);
    }

    [Theory]
    [InlineData(RoofVisibility.AlwaysVisible, true)]
    [InlineData(RoofVisibility.AlwaysHidden, true)]
    [InlineData(RoofVisibility.Interior, true)]
    [InlineData(RoofVisibility.AlwaysHidden, false)]
    public void Past_draw_radius_queues_neither_colour_nor_shadow(RoofVisibility mode, bool hlod)
    {
        using var rig = new Rig(roofs: Roofs() with { HlodDistance = hlod ? 192f : 0f });
        rig.View.RoofMode = mode;
        rig.View.Observer = new TileCoord(4, 4, 0);
        rig.View.Draw(Focus(700f));

        Assert.Empty(rig.Scene.ClusterPropDraws);
        Assert.Empty(rig.Scene.ClusterShadowPropDraws);
        Assert.Empty(rig.Scene.ClusterMergedDraws);
        Assert.Empty(rig.Scene.ClusterShadowMergedDraws);
        Assert.Equal(0, rig.View.LastDrawnProps);
        Assert.Equal(0, rig.View.LastShadowOnlyProps);
    }

    [Fact]
    public void Shadow_opt_out_queues_no_hidden_geometry()
    {
        using var rig = new Rig(roofs: Roofs() with { CastsShadows = false });
        rig.View.RoofMode = RoofVisibility.AlwaysHidden;
        rig.View.Draw(Focus(192f));

        Assert.Empty(rig.Scene.ClusterPropDraws);
        Assert.Empty(rig.Scene.ClusterMergedDraws);
        Assert.Empty(rig.Scene.ClusterShadowPropDraws);
        Assert.Empty(rig.Scene.ClusterShadowMergedDraws);
        Assert.Equal(0, rig.View.LastShadowOnlyProps);
    }

    [Fact]
    public void Roof_override_invalidates_only_its_cluster_and_keeps_authored_identity()
    {
        TileWorldDocument doc = World();
        TileObject roof = doc.AllObjects().First();
        doc.AddObject("wall", 4, 4, 1, 0);
        using var rig = new Rig(doc);
        rig.View.Draw(Vector3.Zero);
        int requests = rig.Scene.ClusterBuildRequests.Count;
        Assert.True(rig.View.TryGetRegionProps(default, 1, out var before));

        Assert.True(rig.View.OverrideArchetype(roof.Id, "bush"));
        rig.View.Draw(Vector3.Zero);
        var changed = rig.Scene.ClusterBuildRequests.Skip(requests).ToArray();
        Assert.Single(changed);
        Assert.All(changed, r => Assert.Equal(1, r.Key.Plane));
        Assert.True(rig.View.TryGetRegionProps(default, 1, out var after));
        Assert.True(after.Generation > before.Generation);
        Assert.Contains(roof.Id, after.GroundObjectIds);
        Assert.DoesNotContain(roof.Id, after.Layers["roofs"].ObjectIds);
        Assert.Equal("roof_flat", doc.FindObject(roof.Id)!.ArchetypeId);

        requests = rig.Scene.ClusterBuildRequests.Count;
        Assert.True(rig.View.ClearOverride(roof.Id));
        rig.View.Draw(Vector3.Zero);
        Assert.Equal(1, rig.Scene.ClusterBuildRequests.Count - requests);
        Assert.True(rig.View.TryGetRegionProps(default, 1, out var restored));
        Assert.Contains(roof.Id, restored.Layers["roofs"].ObjectIds);
    }

    [Fact]
    public void Zero_weld_cell_preserves_every_roof_vertex_and_corner_in_the_merge()
    {
        using var rig = new Rig();
        rig.View.Draw(Focus(300f));
        var request = Assert.Single(rig.Scene.ClusterBuildRequests, r => r.Key.LayerId == "roofs" && r.Key.Plane == 1);
        GltfMesh merged = PropHlod.BuildMergedMesh(request.Placements, request.Layer.HlodSourceMeshes!, 0f);
        GltfMesh source = request.Layer.HlodSourceMeshes!["roof_flat"];

        Assert.Equal(2 * source.Vertices.Length, merged.Vertices.Length);
        Assert.Equal(2 * source.Indices32.Length, merged.Indices32.Length);
        Assert.Equal(0f, request.Layer.HlodWeldCell);
        Assert.Single(rig.Scene.ClusterMergedDraws);
    }

    [Fact]
    public void Interior_cut_still_reads_roofs_that_draw_through_a_layer()
    {
        using var rig = new Rig(cutStructures: true);
        var meshes = new InteriorTestMeshes();
        rig.View.InteriorMeshes = meshes;
        rig.View.Observer = new TileCoord(4, 4, 0);
        rig.View.Draw(Vector3.Zero);

        Assert.NotNull(rig.Scene.InteriorCut);
        Assert.NotEmpty(meshes.Threads);
        Assert.True(rig.Scene.InteriorCut.Contains(new Vector3(4.5f, 1f, -4.5f)));
        Assert.True(rig.Scene.InteriorCut.Contains(new Vector3(12.5f, 1f, -4.5f)));
        Assert.False(rig.Scene.InteriorCut.Contains(new Vector3(8.5f, 1f, -4.5f)));
        Assert.True(rig.View.IsRoofHidden(new TileRect(4, 4, 1, 1), 1));
        Assert.False(rig.View.IsRoofHidden(new TileRect(12, 4, 1, 1), 1));
        Assert.False(rig.View.IsRoofHidden(new TileRect(4, 4, 1, 1), 0));
    }

    [Fact]
    public void Roof_snapshot_retains_sorted_detached_footprints_and_authored_ids()
    {
        TileWorldDocument doc = World();
        TileObject[] objects = doc.AllObjects().ToArray();
        doc.GetRegion(default)!.Objects.Reverse();
        using var rig = new Rig(doc);
        Assert.True(rig.View.TryGetRegionProps(default, 1, out var props));
        TilePropLayerSnapshot roofs = props.Layers["roofs"];
        Assert.True(roofs.IsRoofLayer);
        Assert.Equal(1, roofs.Plane);
        Assert.Equal(new[] { objects[0].Id, objects[1].Id }, roofs.ObjectIds);
        Assert.Equal(new TileRect(4, 4, 1, 1), roofs.RoofFootprints[0]);
        Assert.Equal(new TileRect(12, 4, 1, 1), roofs.RoofFootprints[1]);

        objects[0].X = 40;
        Assert.Equal(new TileRect(4, 4, 1, 1), roofs.RoofFootprints[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<TileRect>)roofs.RoofFootprints)[0] = default);
    }

    internal static Vector3 Focus(float distance) => new(32f + distance, 0f, -32f);

    internal static TilePropLayerDefinition Roofs() => new()
    {
        Id = "roofs",
        ArchetypeIds = new HashSet<string>(StringComparer.Ordinal) { "roof_flat" },
        DrawRadius = 576f,
        LodDistance = 0f,
        HlodDistance = 192f,
        HlodCrossfadeWidth = 32f,
        HlodWeldCell = 0f,
        CastsShadows = true,
        CutInsideInteriors = false,
    };

    internal static TileWorldDocument World()
    {
        var doc = new TileWorldDocument { Id = "roof-layers" };
        doc.GetOrCreateRegion(TileRenderTestData.Region);
        foreach (int x in new[] { 4, 12 })
        {
            doc.SetSettings(x, 4, 0, TileSettings.Indoors);
            doc.AddObject("roof_flat", x, 4, 1, 0);
        }
        return doc;
    }

    internal sealed class Rig : IDisposable
    {
        public readonly TileWorldDocument Doc;
        public readonly RecordingTileWorldScene Scene = new();
        public readonly TileWorldView View;
        public Rig(TileWorldDocument? doc = null, TilePropLayerDefinition? roofs = null, bool cutStructures = false)
        {
            Doc = doc ?? World();
            TilePropLayerDefinition structures = Roofs() with
            {
                Id = "structures",
                CutInsideInteriors = cutStructures,
                ArchetypeIds = new HashSet<string>(StringComparer.Ordinal) { "wall" },
            };
            View = new TileWorldView(Scene, Doc, TileRenderTestData.Catalogs, new Resolver(),
                new TileWorldViewOptions { PropLayers = new[] { structures, roofs ?? Roofs() } },
                new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 32, MaxHlodAppliesPerPump = 32 },
                new ImmediateDispatcher());
            View.LoadRegion(TileRenderTestData.Region, TileRegionResidencyState.Gameplay);
        }
        public void ClearDraws()
        {
            Scene.ClusterPropDraws.Clear();
            Scene.ClusterShadowPropDraws.Clear();
            Scene.ClusterMergedDraws.Clear();
            Scene.ClusterShadowMergedDraws.Clear();
        }
        public void Dispose() => View.Dispose();
    }

    internal sealed class Resolver : ITileMeshResolver, ITileLodMeshResolver
    {
        readonly GreyboxMeshResolver _inner = new();
        public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype) => _inner.Resolve(archetype);
        public IReadOnlyList<GltfMeshPart>? ResolveLod(TileObjectArchetype archetype) => _inner.Resolve(archetype);
        public GltfMesh? ResolveFlatForHlod(TileObjectArchetype archetype) => _inner.Resolve(archetype)?[0].Mesh;
    }

    sealed class ImmediateDispatcher : IChunkBuildDispatcher
    {
        public void Schedule(Action build) => build();
        public void Drain() { }
    }
}
