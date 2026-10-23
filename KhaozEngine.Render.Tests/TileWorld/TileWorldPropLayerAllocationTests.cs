using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

[Collection("AllocSensitive")]
public sealed class TileWorldPropLayerAllocationTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(192f)]
    [InlineData(300f)]
    public void Non_roof_retained_layers_draw_without_per_frame_allocation(float distance)
    {
        var doc = new TileWorldDocument { Id = "non-roof-allocation" };
        doc.GetOrCreateRegion(default);
        doc.AddObject("tree", 4, 4, 0, 0);
        doc.AddObject("wall", 12, 4, 0, 0);
        var scene = new TileWorldAllocationScene();
        using var view = View(scene, doc, Layer("trees", "tree"), Layer("structures", "wall"));
        Vector3 focus = TileWorldRoofPropLayerTests.Focus(distance);
        Warm(view, focus);

        AllocAssert.NoPerCallAllocation("non-roof TileWorldView.Draw", () => view.Draw(focus));

        Assert.False(scene.RequiresVisibility);
        Assert.True(scene.VisiblePlacements > 0 || scene.VisibleMerged > 0);
    }

    [Theory]
    [InlineData(RoofVisibility.Interior, 0f)]
    [InlineData(RoofVisibility.Interior, 192f)]
    [InlineData(RoofVisibility.Interior, 300f)]
    [InlineData(RoofVisibility.AlwaysVisible, 192f)]
    [InlineData(RoofVisibility.AlwaysHidden, 192f)]
    public void Roof_retained_layers_draw_without_per_frame_allocation(RoofVisibility mode, float distance)
    {
        var scene = new TileWorldAllocationScene();
        using var view = View(scene, TileWorldRoofPropLayerTests.World(), TileWorldRoofPropLayerTests.Roofs());
        view.Observer = new TileCoord(4, 4, 0);
        view.RoofMode = mode;
        Vector3 focus = TileWorldRoofPropLayerTests.Focus(distance);
        Warm(view, focus);

        AllocAssert.NoPerCallAllocation("roof TileWorldView.Draw", () => view.Draw(focus));

        Assert.True(scene.RequiresVisibility);
        Assert.Equal(mode == RoofVisibility.AlwaysHidden ? 0 : mode == RoofVisibility.Interior ? 1 : 2,
            view.LastDrawnProps);
        Assert.Equal(mode == RoofVisibility.AlwaysVisible ? 0 : mode == RoofVisibility.Interior ? 1 : 2,
            view.LastShadowOnlyProps);
    }

    [Fact]
    public void Non_roof_snapshots_do_not_retain_unused_roof_footprints()
    {
        var doc = new TileWorldDocument { Id = "non-roof-snapshots" };
        doc.GetOrCreateRegion(default);
        doc.AddObject("tree", 4, 4, 0, 0);
        doc.AddObject("wall", 12, 4, 0, 0);
        using var view = View(new TileWorldAllocationScene(), doc, Layer("trees", "tree"), Layer("structures", "wall"));

        Assert.True(view.TryGetRegionProps(default, 0, out TileRegionProps props));
        Assert.Equal(2, props.Layers.Count);
        Assert.All(props.Layers.Values, layer =>
        {
            Assert.Single(layer.Placements);
            Assert.False(layer.IsRoofLayer);
            Assert.Empty(layer.RoofFootprints);
        });
    }

    static TileWorldView View(TileWorldAllocationScene scene, TileWorldDocument doc, params TilePropLayerDefinition[] layers)
    {
        var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs,
            new TileWorldRoofPropLayerTests.Resolver(), new TileWorldViewOptions { PropLayers = layers },
            new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 32, MaxHlodAppliesPerPump = 32 }, new ImmediateDispatcher());
        view.LoadRegion(default);
        return view;
    }

    static TilePropLayerDefinition Layer(string id, string archetype) => TileWorldRoofPropLayerTests.Roofs() with
    {
        Id = id,
        ArchetypeIds = new HashSet<string>(StringComparer.Ordinal) { archetype },
    };

    static void Warm(TileWorldView view, Vector3 focus)
    {
        for (int i = 0; i < 4; i++) view.Draw(focus);
    }

    sealed class ImmediateDispatcher : IChunkBuildDispatcher
    {
        public void Schedule(Action build) => build();
        public void Drain() { }
    }
}
