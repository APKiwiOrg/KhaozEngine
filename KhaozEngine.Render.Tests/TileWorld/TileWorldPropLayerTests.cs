using System;
using System.Collections.Generic;
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
}
