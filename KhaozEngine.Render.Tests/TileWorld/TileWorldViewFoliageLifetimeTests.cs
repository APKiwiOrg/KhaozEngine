using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldViewFoliageLifetimeTests
{
    static TileFoliageLayer Layer(float spacing = 2f) => new(
        "meadow", 0, 0f, -64f, 1f, 129, 65,
        Enumerable.Repeat((byte)255, 129 * 65).ToArray(), 19, spacing, 1f, 1f, 0f,
        [new TileFoliageArchetype("bush", 1f)], [TileRenderTestData.Grass], true, true, 0f, 0f);

    static TileWorldView View(RecordingTileWorldScene scene, TileWorldDocument doc) =>
        new(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver());

    [Fact]
    public void RebuildingCoverReleasesThePreviousBatchBeforeItIsReplaced()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        doc.SetFoliageLayer(Layer());
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);
        IReadOnlyList<GroundCoverInstance> before = view.CoverIn(TileRenderTestData.Region);
        view.Draw(Vector3.Zero);

        view.MarkDirty(TileRenderTestData.Region, 0);
        view.Flush(int.MaxValue);

        Assert.Same(before, Assert.Single(scene.FoliageReleases));
        IReadOnlyList<GroundCoverInstance> after = view.CoverIn(TileRenderTestData.Region);
        Assert.NotSame(before, after);
        Assert.Equal(before.Count, after.Count);
        view.Draw(Vector3.Zero);
        Assert.Same(after, scene.FoliageDraws[^1].Instances);
    }

    [Fact]
    public void UnloadingARegionReleasesItsCoverOnce()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        doc.SetFoliageLayer(Layer());
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);
        IReadOnlyList<GroundCoverInstance> before = view.CoverIn(TileRenderTestData.Region);

        view.UnloadRegion(TileRenderTestData.Region);
        view.UnloadRegion(TileRenderTestData.Region);

        Assert.Same(before, Assert.Single(scene.FoliageReleases));
        Assert.Empty(view.CoverIn(TileRenderTestData.Region));
        Assert.Equal(0, view.GeneratedCoverCount);
    }

    [Fact]
    public void ReloadingARegionReleasesTheReplacedCover()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        doc.SetFoliageLayer(Layer());
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);
        IReadOnlyList<GroundCoverInstance> before = view.CoverIn(TileRenderTestData.Region);

        view.LoadRegion(TileRenderTestData.Region);

        Assert.Same(before, Assert.Single(scene.FoliageReleases));
        Assert.NotSame(before, view.CoverIn(TileRenderTestData.Region));
    }

    [Fact]
    public void DisposingTheViewReleasesEveryLoadedRegionsCoverOnce()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = TileWorldFile.Load(TileRenderTestData.SaveGrid(tmp, 2, 1));
        doc.SetFoliageLayer(Layer());
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, doc);
        var west = new RegionCoord(0, 0);
        var east = new RegionCoord(1, 0);
        view.LoadRegion(west);
        view.LoadRegion(east);
        IReadOnlyList<GroundCoverInstance> westCover = view.CoverIn(west);
        IReadOnlyList<GroundCoverInstance> eastCover = view.CoverIn(east);

        view.Dispose();
        view.Dispose();

        Assert.Equal(2, scene.FoliageReleases.Count);
        Assert.Contains(scene.FoliageReleases, cover => ReferenceEquals(westCover, cover));
        Assert.Contains(scene.FoliageReleases, cover => ReferenceEquals(eastCover, cover));
        Assert.Equal(0, view.LoadedRegionCount);
    }

    [Fact]
    public void FailedRebuildKeepsThePreviousCoverAndItsRetainedResources()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        doc.SetFoliageLayer(Layer());
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);
        IReadOnlyList<GroundCoverInstance> before = view.CoverIn(TileRenderTestData.Region);
        doc.SetFoliageLayer(Layer(spacing: .01f));
        view.MarkDirty(TileRenderTestData.Region, 0);

        Assert.Throws<ArgumentException>(() => view.Flush(int.MaxValue));

        Assert.Empty(scene.FoliageReleases);
        Assert.Same(before, view.CoverIn(TileRenderTestData.Region));
        Assert.Equal(before.Count, view.GeneratedCoverCount);
    }
}
