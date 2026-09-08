using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Opt-in TileWorld prop layers and their detached region-plane snapshots.</summary>
public class TileWorldPropLayerTests
{
    static TilePropLayerDefinition Trees() => new()
    {
        Id = "trees",
        ArchetypeIds = new HashSet<string>(StringComparer.Ordinal) { "tree" },
        DrawRadius = 576f,
        LodDistance = 64f,
        LodCrossfadeWidth = 16f,
        HlodDistance = 192f,
        HlodCrossfadeWidth = 32f,
        HlodWeldCell = 1.5f,
    };

    [Fact]
    public void Prop_layers_are_empty_by_default()
    {
        Assert.Empty(new TileWorldViewOptions().PropLayers);
    }

    [Fact]
    public void Approved_tree_profile_keeps_every_authored_value()
    {
        TilePropLayerDefinition trees = Trees();

        Assert.Equal("trees", trees.Id);
        Assert.Equal(new[] { "tree" }, trees.ArchetypeIds);
        Assert.Equal(576f, trees.DrawRadius);
        Assert.Equal(64f, trees.LodDistance);
        Assert.Equal(16f, trees.LodCrossfadeWidth);
        Assert.Equal(192f, trees.HlodDistance);
        Assert.Equal(32f, trees.HlodCrossfadeWidth);
        Assert.Equal(1.5f, trees.HlodWeldCell);
        Assert.True(trees.CastsShadows);
    }

    [Fact]
    public void View_refuses_an_archetype_selected_by_two_layers_before_uploading()
    {
        var scene = new RecordingTileWorldScene();
        TilePropLayerDefinition second = Trees() with { Id = "other-trees" };
        var options = new TileWorldViewOptions { PropLayers = new[] { Trees(), second } };

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new TileWorldView(scene, TileRenderTestData.HouseWorld(), TileRenderTestData.Catalogs,
                new GreyboxMeshResolver(), options));

        Assert.Contains("tree", error.Message, StringComparison.Ordinal);
        Assert.Empty(scene.MaterialLoads);
        Assert.Empty(scene.PropMeshLoads);
    }

    [Theory]
    [InlineData(RoofVisibility.Interior)]
    [InlineData(RoofVisibility.AlwaysHidden)]
    public void Roof_archetypes_cannot_join_a_prop_layer_that_bypasses_visibility(RoofVisibility mode)
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        TilePropLayerDefinition roofs = Trees() with
        {
            Id = "roofs",
            ArchetypeIds = new HashSet<string>(StringComparer.Ordinal) { "roof_flat" },
        };

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            TileWorldPropClusters.Validate(TileRenderTestData.Catalogs, new[] { roofs }));
        Assert.Contains("roof_flat", error.Message, StringComparison.Ordinal);

        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver());
        view.LoadRegion(TileRenderTestData.Region);
        view.RoofMode = mode;
        view.Observer = new TileCoord(TileRenderTestData.HouseMinX, TileRenderTestData.HouseMinZ, 0);
        view.Draw(Vector3.Zero);

        Assert.DoesNotContain(scene.PropDraws.SelectMany(draw => draw.Placements),
            placement => placement.Id == "roof_flat");
    }

    [Fact]
    public void Successful_view_construction_enumerates_each_definition_set_once()
    {
        var archetypes = new SingleEnumerationSet("tree");
        TilePropLayerDefinition trees = Trees() with { ArchetypeIds = archetypes };

        using TileWorldView view = ViewWith(trees);

        Assert.Equal(1, archetypes.EnumerationCount);
    }

    [Theory]
    [InlineData(64f, 48f, 576f)]
    [InlineData(600f, 700f, 576f)]
    public void View_refuses_invalid_distance_ordering(float lodDistance, float hlodDistance, float drawRadius)
    {
        TilePropLayerDefinition invalid = Trees() with
        {
            LodDistance = lodDistance,
            HlodDistance = hlodDistance,
            DrawRadius = drawRadius,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => ViewWith(invalid));
    }

    [Theory]
    [InlineData(-1f, 32f, 1.5f)]
    [InlineData(16f, -1f, 1.5f)]
    [InlineData(16f, 32f, -1f)]
    public void View_refuses_negative_widths(float lodWidth, float hlodWidth, float weldCell)
    {
        TilePropLayerDefinition invalid = Trees() with
        {
            LodCrossfadeWidth = lodWidth,
            HlodCrossfadeWidth = hlodWidth,
            HlodWeldCell = weldCell,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => ViewWith(invalid));
    }

    [Fact]
    public void Snapshot_is_detached_sorted_by_object_id_and_uses_current_overrides()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        TileObject first = doc.AddObject("bush", 25, 25, 0, 0);
        TileObject second = doc.AddObject("tree", 27, 25, 0, 1);
        TileRegion region = doc.GetRegion(TileRenderTestData.Region)!;
        region.Objects.Reverse();
        var resolver = new SnapshotResolver();
        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs, resolver,
            new TileWorldViewOptions { PropLayers = new[] { Trees() } });
        Assert.False(view.OverrideArchetype(first.Id, "tree"));

        view.LoadRegion(TileRenderTestData.Region);
        Assert.True(view.TryGetRegionProps(TileRenderTestData.Region, 0, out TileRegionProps snapshot));
        TilePropLayerSnapshot trees = snapshot.Layers["trees"];

        Assert.Equal(new[] { first.Id, second.Id }, trees.ObjectIds);
        Assert.Equal(25.5f, trees.Placements[0].X);
        Assert.Equal(-25.5f, trees.Placements[0].Z);
        Assert.Equal(-MathF.PI / 2f, trees.Placements[1].Yaw, 5);
        Assert.DoesNotContain(snapshot.Ground, placement => placement.Id == "tree");
        Assert.Equal(snapshot.GroundObjectIds.OrderBy(id => id), snapshot.GroundObjectIds);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<PropPlacement>)trees.Placements)[0] = default);
        Assert.Equal("bush", doc.FindObject(first.Id)!.ArchetypeId);

        first.X = 2;
        first.Z = 3;
        region.Objects.Clear();
        Assert.Equal(25.5f, trees.Placements[0].X);
        Assert.Equal(-25.5f, trees.Placements[0].Z);
        Assert.Equal(2, trees.Placements.Count);
    }

    [Fact]
    public void Override_replaces_only_the_affected_snapshot_and_advances_its_generation()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        TileObject tree = doc.AddObject("tree", 25, 25, 0, 0);
        var scene = new RecordingTileWorldScene();
        var resolver = new SnapshotResolver();
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs, resolver,
            new TileWorldViewOptions { PropLayers = new[] { Trees() } });
        view.LoadRegion(TileRenderTestData.Region);
        Assert.True(view.TryGetRegionProps(TileRenderTestData.Region, 0, out TileRegionProps before));
        Assert.True(view.TryGetRegionProps(TileRenderTestData.Region, 1, out TileRegionProps otherPlane));

        Assert.True(view.OverrideArchetype(tree.Id, "bush"));
        Assert.True(view.TryGetRegionProps(TileRenderTestData.Region, 0, out TileRegionProps overridden));
        Assert.True(view.TryGetRegionProps(TileRenderTestData.Region, 1, out TileRegionProps unchangedPlane));

        Assert.True(overridden.Generation > before.Generation);
        Assert.Same(otherPlane, unchangedPlane);
        Assert.Empty(overridden.Layers["trees"].Placements);
        Assert.Contains(overridden.Ground, placement => placement.Id == "bush");
        Assert.Equal("tree", doc.FindObject(tree.Id)!.ArchetypeId);

        Assert.True(view.ClearOverride(tree.Id));
        Assert.True(view.TryGetRegionProps(TileRenderTestData.Region, 0, out TileRegionProps restored));
        Assert.True(restored.Generation > overridden.Generation);
        Assert.Single(restored.Layers["trees"].Placements);
    }

    [Fact]
    public void Resolver_cpu_data_is_resolved_once_before_snapshots_are_built()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        doc.AddObject("tree", 25, 25, 0, 0);
        var resolver = new SnapshotResolver();
        using var view = new TileWorldView(new RecordingTileWorldScene(), doc, TileRenderTestData.Catalogs, resolver,
            new TileWorldViewOptions { PropLayers = new[] { Trees() } });

        int lodAtConstruction = resolver.LodCalls;
        int flatAtConstruction = resolver.FlatCalls;
        view.LoadRegion(TileRenderTestData.Region);
        TileObject tree = doc.GetRegion(TileRenderTestData.Region)!.Objects.Single(o => o.ArchetypeId == "tree");
        view.OverrideArchetype(tree.Id, "bush");
        view.ClearOverride(tree.Id);

        Assert.Equal(lodAtConstruction, resolver.LodCalls);
        Assert.Equal(flatAtConstruction, resolver.FlatCalls);
        Assert.Equal(1, lodAtConstruction);
        Assert.Equal(1, flatAtConstruction);
    }

    [Fact]
    public void Resolver_without_lod_capability_keeps_lod0_and_disables_hlod()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        doc.AddObject("tree", 25, 25, 0, 0);
        using var view = new TileWorldView(new RecordingTileWorldScene(), doc, TileRenderTestData.Catalogs,
            new GreyboxMeshResolver(), new TileWorldViewOptions { PropLayers = new[] { Trees() } });
        view.LoadRegion(TileRenderTestData.Region);

        Assert.True(view.TryGetRegionProps(TileRenderTestData.Region, 0, out TileRegionProps snapshot));
        PropLayer layer = snapshot.Layers["trees"].Layer;
        Assert.Null(layer.LodPartMeshes);
        Assert.False(layer.HasHlod);
    }

    [Fact]
    public void Missing_flattened_variant_disables_only_clusters_that_place_it()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        var second = new RegionCoord(1, 0);
        doc.GetOrCreateRegion(second);
        doc.AddObject("tree", 25, 25, 0, 0);
        doc.AddObject("bush", second.OriginX + 25, 25, 0, 0);
        var scene = new RecordingTileWorldScene();
        TilePropLayerDefinition mixed = Trees() with
        {
            ArchetypeIds = new HashSet<string>(StringComparer.Ordinal) { "tree", "bush" },
        };
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs,
            new MissingBushFlatResolver(), new TileWorldViewOptions { PropLayers = new[] { mixed } },
            new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 16, MaxHlodAppliesPerPump = 16 },
            new ImmediateDispatcher());
        view.LoadRegion(TileRenderTestData.Region, TileRegionResidencyState.Gameplay);
        view.LoadRegion(second, TileRegionResidencyState.Gameplay);
        view.Draw(Vector3.Zero);

        Assert.True(view.TryGetRegionProps(TileRenderTestData.Region, 0, out TileRegionProps firstProps));
        Assert.True(view.TryGetRegionProps(second, 0, out TileRegionProps secondProps));
        Assert.True(firstProps.Layers["trees"].Layer.HasHlod);
        Assert.False(secondProps.Layers["trees"].Layer.HasHlod);
        Assert.Equal(1, scene.LiveClusterMeshCount);

        Vector3 secondFocus = new(second.OriginX + 32f, 0f, -32f);
        int gameplayDraws = scene.ClusterPropDraws.Count;
        view.Draw(secondFocus);
        Assert.Contains(scene.ClusterPropDraws.Skip(gameplayDraws).SelectMany(draw => draw.Placements),
            placement => placement.Id == "bush");

        view.LoadRegion(second, TileRegionResidencyState.Decor);
        int decorDraws = scene.ClusterPropDraws.Count;
        view.Draw(secondFocus);
        Assert.Contains(scene.ClusterPropDraws.Skip(decorDraws).SelectMany(draw => draw.Placements),
            placement => placement.Id == "bush");
    }

    [Fact]
    public void Selected_props_never_reach_ordinary_draws()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        doc.AddObject("tree", 25, 25, 0, 0);
        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs, new SnapshotResolver(),
            new TileWorldViewOptions { PropLayers = new[] { Trees() } });
        view.LoadRegion(TileRenderTestData.Region);

        view.Draw(Vector3.Zero);

        Assert.DoesNotContain(scene.PropDraws.SelectMany(draw => draw.Placements), placement => placement.Id == "tree");
    }

    [Fact]
    public void Selected_props_reach_neither_ordinary_nor_animated_foliage_draws_when_both_select_them()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        doc.AddObject("tree", 25, 25, 0, 0);
        var scene = new RecordingTileWorldScene();
        var options = new TileWorldViewOptions
        {
            PropLayers = new[] { Trees() },
            AnimatedFoliageArchetypes = new HashSet<string>(StringComparer.Ordinal) { "tree" },
        };
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs,
            new SnapshotResolver(), options);
        view.LoadRegion(TileRenderTestData.Region);

        view.Draw(Vector3.Zero);

        Assert.DoesNotContain(scene.PropDraws.SelectMany(draw => draw.Placements), placement => placement.Id == "tree");
        Assert.DoesNotContain(scene.FoliageDraws.SelectMany(draw => draw.Instances), instance => instance.ModelId == "tree");
    }

    [Fact]
    public void Clear_all_rebuilds_only_region_planes_that_held_overrides()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        var secondRegion = new RegionCoord(1, 0);
        doc.GetOrCreateRegion(secondRegion);
        TileObject first = doc.AddObject("tree", 25, 25, 0, 0);
        TileObject second = doc.AddObject("tree", secondRegion.OriginX + 2, 2, 1, 0);
        using var view = new TileWorldView(new RecordingTileWorldScene(), doc, TileRenderTestData.Catalogs,
            new SnapshotResolver(), new TileWorldViewOptions { PropLayers = new[] { Trees() } });
        view.LoadRegion(TileRenderTestData.Region);
        view.LoadRegion(secondRegion);
        view.OverrideArchetype(first.Id, "bush");
        view.OverrideArchetype(second.Id, "bush");

        var before = Snapshots(view, TileRenderTestData.Region, secondRegion);
        view.ClearOverrides();
        var after = Snapshots(view, TileRenderTestData.Region, secondRegion);

        foreach (KeyValuePair<(RegionCoord Region, int Plane), TileRegionProps> item in before)
        {
            TileRegionProps current = after[item.Key];
            bool affected = item.Key is { Region: var region, Plane: var plane } &&
                (region == TileRenderTestData.Region && plane == 0 || region == secondRegion && plane == 1);
            if (affected)
            {
                Assert.Equal(item.Value.Generation + 1, current.Generation);
                Assert.NotSame(item.Value, current);
            }
            else
            {
                Assert.Equal(item.Value.Generation, current.Generation);
                Assert.Same(item.Value, current);
            }
        }
    }

    static Dictionary<(RegionCoord Region, int Plane), TileRegionProps> Snapshots(
        TileWorldView view, params RegionCoord[] regions)
    {
        var snapshots = new Dictionary<(RegionCoord Region, int Plane), TileRegionProps>();
        foreach (RegionCoord region in regions)
            for (int plane = 0; plane < TileWorldDocument.DefaultPlaneCount; plane++)
            {
                Assert.True(view.TryGetRegionProps(region, plane, out TileRegionProps snapshot));
                snapshots.Add((region, plane), snapshot);
            }
        return snapshots;
    }

    static TileWorldView ViewWith(TilePropLayerDefinition layer) => new(
        new RecordingTileWorldScene(), TileRenderTestData.HouseWorld(), TileRenderTestData.Catalogs,
        new GreyboxMeshResolver(), new TileWorldViewOptions { PropLayers = new[] { layer } });

    sealed class SnapshotResolver : ITileMeshResolver, ITileLodMeshResolver
    {
        readonly GreyboxMeshResolver _inner = new();
        public int LodCalls { get; private set; }
        public int FlatCalls { get; private set; }

        public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype) => _inner.Resolve(archetype);

        public IReadOnlyList<GltfMeshPart>? ResolveLod(TileObjectArchetype archetype)
        {
            LodCalls++;
            return _inner.Resolve(archetype);
        }

        public GltfMesh? ResolveFlatForHlod(TileObjectArchetype archetype)
        {
            FlatCalls++;
            return _inner.Resolve(archetype)?[0].Mesh;
        }
    }

    sealed class MissingBushFlatResolver : ITileMeshResolver, ITileLodMeshResolver
    {
        readonly GreyboxMeshResolver _inner = new();
        public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype) => _inner.Resolve(archetype);
        public IReadOnlyList<GltfMeshPart>? ResolveLod(TileObjectArchetype archetype) => _inner.Resolve(archetype);
        public GltfMesh? ResolveFlatForHlod(TileObjectArchetype archetype) =>
            archetype.Id == "bush" ? null : _inner.Resolve(archetype)?[0].Mesh;
    }

    sealed class ImmediateDispatcher : IChunkBuildDispatcher
    {
        public void Schedule(Action build) => build();
        public void Drain() { }
    }

    sealed class SingleEnumerationSet : IReadOnlySet<string>
    {
        readonly HashSet<string> _values;
        public SingleEnumerationSet(params string[] values) =>
            _values = new HashSet<string>(values, StringComparer.Ordinal);
        public int EnumerationCount { get; private set; }
        public int Count => _values.Count;
        public bool Contains(string item) => _values.Contains(item);
        public bool IsProperSubsetOf(IEnumerable<string> other) => _values.IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<string> other) => _values.IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<string> other) => _values.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<string> other) => _values.IsSupersetOf(other);
        public bool Overlaps(IEnumerable<string> other) => _values.Overlaps(other);
        public bool SetEquals(IEnumerable<string> other) => _values.SetEquals(other);
        public IEnumerator<string> GetEnumerator()
        {
            EnumerationCount++;
            if (EnumerationCount > 1)
                throw new InvalidOperationException("the archetype set was enumerated more than once");
            return _values.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
