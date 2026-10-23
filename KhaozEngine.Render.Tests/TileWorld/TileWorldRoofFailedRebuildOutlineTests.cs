using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldRoofFailedRebuildOutlineTests
{
    [Fact]
    public void Failed_rebuild_hidden_individual_does_not_outline_the_visible_retained_merge()
    {
        using var rig = new Rig();
        rig.MoveRoofAndFailRebuild(4);

        Assert.Empty(rig.Scene.ClusterPropDraws);
        var shadows = Assert.Single(rig.Scene.ClusterShadowPropDraws);
        Assert.Equal(2, shadows.Placements.Count);
        Assert.All(shadows.Placements, placement => Assert.Equal(4.5f, placement.X));
        Assert.Equal(0f, shadows.DissolveFloor);
        Assert.Equal(0, rig.View.LastDrawnProps);
        Assert.Equal(2, rig.View.LastShadowOnlyProps);
        Assert.Empty(rig.Scene.OutlineGroups.SelectMany(group => group.Parts));
    }

    [Fact]
    public void Failed_rebuild_visible_fallback_outlines_the_current_individual_at_zero_hlod_floor()
    {
        using var rig = new Rig();
        rig.MoveRoofAndFailRebuild(20);

        var visible = Assert.Single(rig.Scene.ClusterPropDraws);
        Assert.Equal(20.5f, Assert.Single(visible.Placements).X);
        Assert.Equal(0f, visible.DissolveFloor);
        Assert.Equal(4.5f, Assert.Single(Assert.Single(rig.Scene.ClusterShadowPropDraws).Placements).X);
        Assert.Equal(1, rig.View.LastDrawnProps);
        Assert.Equal(1, rig.View.LastShadowOnlyProps);
        Assert.True(rig.View.TryGetRegionProps(default, 1, out TileRegionProps props));
        var part = Assert.Single(rig.Scene.OutlineGroups.SelectMany(group => group.Parts));
        Assert.Equal(props.Layers["roofs"].Layer.PartMeshes!["roof_flat"][0], part.Handle);
        Assert.Equal(new Vector3(20.5f, 3f, -4.5f), part.World.Translation);
        Assert.Equal(0f, part.Dissolve);
        Assert.False(part.Complement);
    }

    sealed class Rig : IDisposable
    {
        readonly TileWorldDocument _doc = new() { Id = "failed-roof-outline" };
        readonly TileObject _roof;
        readonly Vector3 _focus = TileWorldRoofPropLayerTests.Focus(300f);
        public RecordingTileWorldScene Scene { get; } = new();
        public TileWorldView View { get; }

        public Rig()
        {
            _doc.GetOrCreateRegion(default);
            _doc.SetSettings(4, 4, 0, TileSettings.Indoors);
            _doc.AddObject("roof_flat", 4, 4, 1, 0);
            _roof = _doc.AddObject("roof_flat", 12, 4, 1, 0);
            View = new TileWorldView(Scene, _doc, TileRenderTestData.Catalogs,
                new TileWorldRoofPropLayerTests.Resolver(),
                new TileWorldViewOptions { PropLayers = new[] { TileWorldRoofPropLayerTests.Roofs() } },
                new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 32, MaxHlodAppliesPerPump = 32 },
                new ImmediateDispatcher());
            View.LoadRegion(default);
            View.Observer = new TileCoord(4, 4, 0);
            View.SetOutlinedObject(_roof.Id, Color.White);
            View.Draw(_focus);
            Assert.Single(Scene.ClusterMeshLoads);
            Assert.True(View.IsRoofHidden(new TileRect(4, 4, 1, 1), 1));
            Assert.False(View.IsRoofHidden(new TileRect(12, 4, 1, 1), 1));
        }

        public void MoveRoofAndFailRebuild(int x)
        {
            Scene.FailClusterBuilds = true;
            _doc.MoveObject(_roof.Id, x, 4, 1);
            View.MarkDirty(default(RegionCoord), 1);
            Scene.ClusterPropDraws.Clear();
            Scene.ClusterShadowPropDraws.Clear();
            Scene.ClusterMergedDraws.Clear();
            Scene.ClusterShadowMergedDraws.Clear();
            Scene.OutlineGroups.Clear();

            View.Draw(_focus);

            Assert.Contains(Scene.ClusterBuildRequests, request => request.Generation == 2);
            Assert.Single(Scene.ClusterMeshLoads);
            Assert.Empty(Scene.ClusterMeshUnloads);
            Assert.Empty(Scene.ClusterMergedDraws);
            Assert.Empty(Scene.ClusterShadowMergedDraws);
        }

        public void Dispose() => View.Dispose();
    }

    sealed class ImmediateDispatcher : IChunkBuildDispatcher
    {
        public void Schedule(Action build) => build();
        public void Drain() { }
    }
}
