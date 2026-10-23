using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Primitives;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldRoofLayerOutlineTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Pending_roof_move_outlines_each_retained_hlod_representation_only_when_visible(bool retainedHidden)
    {
        var doc = new TileWorldDocument { Id = "retained-roof-outline" };
        doc.GetOrCreateRegion(default);
        doc.SetSettings(4, 4, 0, TileSettings.Indoors);
        TileObject roof = doc.AddObject("roof_flat", retainedHidden ? 4 : 12, 4, 1, 0);
        var scene = new RecordingTileWorldScene();
        var worker = new HeldDispatcher();
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs,
            new TileWorldRoofPropLayerTests.Resolver(),
            new TileWorldViewOptions { PropLayers = new[] { TileWorldRoofPropLayerTests.Roofs() } },
            new TileWorldBuildQueueOptions { MaxConcurrentBuilds = 32, MaxHlodAppliesPerPump = 32 }, worker);
        view.LoadRegion(default);
        view.Observer = new TileCoord(4, 4, 0);
        view.RoofMode = RoofVisibility.AlwaysVisible;
        view.SetOutlinedObject(roof.Id, Color.White);
        var focus = TileWorldRoofPropLayerTests.Focus(192f);
        view.Draw(focus);
        Assert.Equal(2, Assert.Single(scene.OutlineGroups).Parts.Count);

        worker.Hold = true;
        doc.MoveObject(roof.Id, retainedHidden ? 12 : 4, 4, 1);
        view.MarkDirty(default(RegionCoord), 1);
        view.RoofMode = RoofVisibility.Interior;
        scene.OutlineGroups.Clear();
        scene.ClusterPropDraws.Clear();
        scene.ClusterMergedDraws.Clear();

        view.Draw(focus);

        Assert.True(worker.Pending > 0, "replacement build must remain held");
        Assert.Equal(!retainedHidden, view.IsRoofHidden(new TileRect(roof.X, 4, 1, 1), 1));
        var parts = scene.OutlineGroups.SelectMany(group => group.Parts).ToArray();
        if (retainedHidden)
        {
            Assert.Empty(scene.ClusterPropDraws);
            Assert.Empty(scene.ClusterMergedDraws);
            Assert.Equal(.5f, Assert.Single(scene.ClusterShadowPropDraws).DissolveFloor);
            Assert.Equal(.5f, Assert.Single(scene.ClusterShadowMergedDraws).Dissolve);
            Assert.Empty(parts);
        }
        else
        {
            Assert.Single(scene.ClusterPropDraws);
            Assert.Single(scene.ClusterMergedDraws);
            Assert.Equal(2, parts.Length);
            Assert.Contains(parts, part => !part.Complement && part.Dissolve == .5f);
            Assert.Contains(parts, part => part.Complement && part.Dissolve == .5f);
        }
    }

    sealed class HeldDispatcher : IChunkBuildDispatcher
    {
        readonly Queue<Action> _pending = new();
        public bool Hold { get; set; }
        public int Pending => _pending.Count;
        public void Schedule(Action build)
        {
            if (Hold) _pending.Enqueue(build);
            else build();
        }
        public void Drain()
        {
            while (_pending.TryDequeue(out Action? build)) build();
        }
    }
}
