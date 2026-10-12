using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Profile cover shares the ground capture it is requested with, and the cover object index exists
/// only once a view actually requests profile cover.</summary>
public sealed class TileWorldProfileCoverCaptureTests
{
    static readonly RegionCoord Middle = new(1, 1);

    [Fact]
    public void Promotion_captures_the_ground_ring_and_materials_once_per_region()
    {
        using var tmp = new TempDir();
        using var fixture = ProfileFixture.PrimedMeadow(tmp, new RegionCoord(5, 1));
        TileWorldView view = fixture.View;
        RegionCoord[] promoted = ProfileFixture.Column(0, ProfileFixture.RegionsZ);
        int groundBefore = view.GroundCaptures;
        int materialsBefore = view.MaterialCaptures;
        fixture.Witness.Clear();

        fixture.Residency.Update(TileRenderTestData.CentreOf(new RegionCoord(4, 1)));

        Assert.All(promoted, r => Assert.Equal(TileRegionResidencyState.Gameplay, view.ResidencyOf(r)));
        Assert.Equal(promoted.Length, view.GroundCaptures - groundBefore);
        Assert.Equal(promoted.Length, view.MaterialCaptures - materialsBefore);

        fixture.Settle(ProfileFixture.Focus(new RegionCoord(4, 1)));
        TileWorldBuildKey[] cover = fixture.Witness.Builds.Select(b => b.Key)
            .Where(k => k.Kind == TileWorldBuildKind.Cover).ToArray();
        Assert.Equal(promoted.Length, cover.Length);
        Assert.Equal(promoted.ToHashSet(), cover.Select(k => k.Region).ToHashSet());
        Assert.All(cover, k => Assert.Equal(-1, k.Plane));
        Assert.All(promoted, r => Assert.Equal(fixture.ExpectedCover(r), view.CoverIn(r).Count));
    }

    [Fact]
    public void Flush_requests_ground_and_cover_from_one_capture()
    {
        using var tmp = new TempDir();
        using var fixture = ProfileFixture.PrimedMeadow(tmp, new RegionCoord(3, 2));
        TileWorldView view = fixture.View;
        var region = new RegionCoord(3, 2);
        int groundBefore = view.GroundCaptures;
        int materialsBefore = view.MaterialCaptures;
        fixture.Witness.Clear();

        view.MarkDirty(region, 0);
        view.Flush();

        Assert.Equal(1, view.GroundCaptures - groundBefore);
        Assert.Equal(1, view.MaterialCaptures - materialsBefore);
        fixture.Settle(ProfileFixture.Focus(region));
        Assert.Contains(fixture.Witness.Builds, b => b.Key == new TileWorldBuildKey(region, -1, TileWorldBuildKind.Cover));
        Assert.Equal(fixture.ExpectedCover(region), view.CoverIn(region).Count);
    }

    [Fact]
    public void Legacy_view_never_builds_the_cover_index()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = Grid(tmp, foliage: true);
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = ProfileFixture.CreateView(scene, doc, new ManualTileBuildDispatcher(),
            new ProfileBuildWitness());

        foreach (RegionCoord region in doc.Regions.Keys.ToArray()) view.LoadRegion(region);
        Assert.NotEmpty(view.CoverIn(Middle));
        Edit(view, doc);
        view.Flush(int.MaxValue);

        Assert.False(view.HasCoverObjectIndex);
        Assert.Equal(0, view.StaleCoverObjectRegions);
    }

    [Fact]
    public void Profile_view_without_foliage_never_builds_the_cover_index()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = Grid(tmp, foliage: false);
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        using TileWorldView view = ProfileFixture.CreateView(scene, doc, dispatcher, new ProfileBuildWitness());

        foreach (RegionCoord region in doc.Regions.Keys.ToArray())
            view.LoadRegion(region, region == Middle ? TileRegionResidencyState.Gameplay : TileRegionResidencyState.Decor);
        view.LoadRegion(new RegionCoord(0, 1), TileRegionResidencyState.Gameplay);
        Edit(view, doc);
        Settle(view, dispatcher, scene);

        Assert.False(view.HasCoverObjectIndex);
        Assert.Equal(0, view.StaleCoverObjectRegions);
    }

    [Fact]
    public void First_cover_request_after_edits_sees_current_objects()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = Grid(tmp, foliage: true);
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        using TileWorldView view = ProfileFixture.CreateView(scene, doc, dispatcher, new ProfileBuildWitness());
        foreach (RegionCoord region in doc.Regions.Keys.ToArray())
            view.LoadRegion(region, TileRegionResidencyState.Decor);
        Settle(view, dispatcher, scene);
        GroundCoverInstance[] bare = Expected(doc, Middle);

        long[] trees = Enumerable.Range(0, 8)
            .Select(i => doc.AddObject("tree", Middle.OriginX + 4 + i * 7, Middle.OriginZ + 20, 0, 0).Id).ToArray();
        view.MarkDirty(Middle.Rect, 0);
        Assert.False(view.HasCoverObjectIndex);
        Assert.Equal(0, view.StaleCoverObjectRegions);

        view.LoadRegion(Middle, TileRegionResidencyState.Gameplay);
        Assert.True(view.HasCoverObjectIndex);
        Assert.Equal(0, view.StaleCoverObjectRegions);
        Settle(view, dispatcher, scene);
        GroundCoverInstance[] withTrees = Expected(doc, Middle);
        Assert.NotEqual(bare.Length, withTrees.Length);
        Assert.Equal(withTrees, view.CoverIn(Middle));

        // Once the index exists, later edits are recorded and refreshed before the next capture.
        foreach (long id in trees) doc.RemoveObject(id);
        view.MarkDirty(Middle.Rect, 0);
        Assert.NotEqual(0, view.StaleCoverObjectRegions);
        Settle(view, dispatcher, scene);
        Assert.Equal(bare, view.CoverIn(Middle));
    }

    static TileWorldDocument Grid(TempDir tmp, bool foliage)
    {
        TileWorldDocument doc = TileWorldFile.Load(TileRenderTestData.SaveGrid(tmp, 3, 3));
        if (foliage) doc.SetFoliageLayer(ProfileFixture.Meadow(3, 3));
        return doc;
    }

    // Every notification that can make a region's cover objects stale.
    static void Edit(TileWorldView view, TileWorldDocument doc)
    {
        doc.AddObject("tree", Middle.OriginX + 8, Middle.OriginZ + 8, 0, 0);
        view.MarkDirty(Middle, 0);
        view.MarkDirty(new TileRect(Middle.OriginX + 4, Middle.OriginZ + 4, 8, 8), 0);
        view.MarkRegionStreamed(Middle);
    }

    static GroundCoverInstance[] Expected(TileWorldDocument doc, RegionCoord region)
    {
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        return TileCoverBuildInput.Capture(doc, catalogs, region, new TileGroundMesherOptions().SmoothNormals,
            new TileCoverObjectIndex(doc, catalogs)).Build().ToArray();
    }

    static void Settle(TileWorldView view, ManualTileBuildDispatcher dispatcher, RecordingTileWorldScene scene)
    {
        Vector3 focus = ProfileFixture.Focus(Middle);
        for (int frame = 0; frame < 64; frame++)
        {
            dispatcher.RunAll();
            scene.ClearFrame();
            view.Draw(focus);
            if (dispatcher.PendingCount == 0 && view.TrackedProfileBuilds == 0 && view.PendingRebuilds == 0) return;
        }
        throw new InvalidOperationException($"the profile queue did not settle, {view.TrackedProfileBuilds} tracked.");
    }
}
