using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>A profile prime settles arrivals and the borders they change on the calling thread and streams
/// everything else. Capture is the one explicit offline settle of ground, cover and HLOD.</summary>
public sealed class TileWorldProfilePrimeTests
{
    [Fact]
    public void Mixed_prime_settles_only_arrivals_and_their_nonpromotion_borders()
    {
        using var tmp = new TempDir();
        string directory = TileRenderTestData.SaveRidgeGrid(tmp, 3, 4);
        ProfileFixture.AddMeadow(directory, 3, 4);
        using var fixture = ProfileFixture.Open(directory, new TileRegionResidencyProfile(1, 2, 3),
            new TileWorldViewOptions());
        TileWorldView view = fixture.View;
        RecordingTileWorldScene scene = fixture.Scene;
        var arrival = new RegionCoord(1, 0);
        var west = new RegionCoord(0, 0);
        var edited = new RegionCoord(0, 2);
        RegionCoord[] borders = { west, new(0, 1), new(1, 1) };
        RegionCoord[] promoted = { new(2, 0), new(2, 1), new(2, 2) };
        Vector3 before = ProfileFixture.Focus(new RegionCoord(0, 1));
        Vector3 after = ProfileFixture.Focus(new RegionCoord(1, 1));

        fixture.Residency.PrimeAround(TileRenderTestData.CentreOf(new RegionCoord(0, 1)));
        fixture.Settle(before);
        Assert.All(promoted, r => Assert.Equal(TileRegionResidencyState.Decor, view.ResidencyOf(r)));

        // The arrival leaves the way the ring drops a region, so the west border falls back to flat.
        view.UnloadRegion(arrival);
        view.MarkRegionStreamed(arrival);
        fixture.Source.Unload(arrival);
        fixture.Settle(before);
        Dictionary<RegionCoord, int> previous = fixture.DrawnGround(before);
        Assert.All(BorderHeights(scene, previous[west]), y => Assert.Equal(0d, y, 3));

        // An unrelated edit is still queued when the prime runs.
        fixture.Document.SetCornerHeightCm(edited.OriginX + 32, edited.OriginZ + 32, 0, 150);
        view.MarkDirty(new TileRect(edited.OriginX + 32, edited.OriginZ + 32, 1, 1), 0);
        fixture.Witness.Clear();
        int primeThread = Environment.CurrentManagedThreadId;

        fixture.Residency.PrimeAround(TileRenderTestData.CentreOf(new RegionCoord(1, 1)));

        TileWorldBuildKey[] caller = fixture.Witness.Builds
            .Where(b => b.Thread == primeThread).Select(b => b.Key).ToArray();
        Assert.Equal(fixture.Witness.Builds.Count, caller.Length);
        Assert.Equal(TileRegionResidencyState.Gameplay, view.ResidencyOf(arrival));
        Assert.All(promoted, r => Assert.Equal(TileRegionResidencyState.Gameplay, view.ResidencyOf(r)));
        Assert.All(caller, key => Assert.Equal(TileWorldBuildKind.FullGround, key.Kind));
        Assert.All(caller, key => Assert.Contains(key.Region, borders.Append(arrival)));
        Assert.Contains(caller, key => key.Region == arrival && key.Plane == 0);
        Assert.All(borders, r => Assert.Contains(caller, key => key.Region == r && key.Plane == 0));
        Assert.True(fixture.Dispatcher.PendingCount > 0);

        // Before any worker resumes, the arrival's border is already correct and nothing else moved.
        int built = fixture.Witness.Builds.Count;
        Dictionary<RegionCoord, int> primed = fixture.DrawnGround(after);
        Assert.Equal(built, fixture.Witness.Builds.Count);
        Assert.True(primed.ContainsKey(arrival));
        IReadOnlyList<float> ridge = BorderHeights(scene, primed[west]);
        Assert.NotEmpty(ridge);
        Assert.All(ridge, y => Assert.Equal(TileRenderTestData.RidgeHeightCm / 100d, y, 3));
        Assert.Equal(previous[edited], primed[edited]);
        Assert.All(promoted, r => Assert.Equal(previous[r], primed[r]));

        // The edit, the promotions and every cover dependency settle once the workers run.
        fixture.Settle(after);
        Dictionary<RegionCoord, int> settled = fixture.DrawnGround(after);
        Assert.NotEqual(previous[edited], settled[edited]);
        Assert.All(promoted, r => Assert.NotEqual(previous[r], settled[r]));
        Assert.All(promoted, r => Assert.Equal(fixture.ExpectedCover(r), view.CoverIn(r).Count));
        Assert.Equal(fixture.ExpectedCover(arrival), view.CoverIn(arrival).Count);
        Assert.Contains(fixture.Witness.Builds.Skip(built), b => b.Key.Region == edited);
    }

    [Fact]
    public void Capture_explicitly_settles_profile_ground_cover_and_hlod()
    {
        var region = new RegionCoord(0, 0);
        var doc = new TileWorldDocument { Id = "capture", DisplayName = "Capture", PlaneCount = 1 };
        doc.GetOrCreateRegion(region);
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
        doc.AddObject("tree", 32, 32, 0, 0);
        doc.SetFoliageLayer(ProfileFixture.Meadow(1, 1));
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        var witness = new ProfileBuildWitness();
        using TileWorldView view = ProfileFixture.CreateView(scene, doc, dispatcher, witness,
            new TileWorldViewOptions { PropLayers = new[] { ProfileFixture.Trees() } },
            new TileWorldBuildQueueOptions
            {
                MaxConcurrentBuilds = 1,
                MaxFullGroundAppliesPerPump = 0,
                MaxCoverAppliesPerPump = 0,
                MaxCoarseGroundAppliesPerPump = 0,
                MaxHlodAppliesPerPump = 0,
            });
        Vector3 focus = ProfileFixture.Focus(region);
        Matrix4x4 world = TileGroundMesher.WorldMatrix(doc, region);
        int expected = TileCoverBuildInput.Generate(doc, TileRenderTestData.Catalogs, region,
            new TileGroundMesherOptions().SmoothNormals, doc.FoliageLayers).Count;

        view.LoadRegion(region, TileRegionResidencyState.Gameplay);
        Assert.Empty(witness.Builds);
        view.Draw(focus);
        Assert.Empty(scene.Drawn);
        Assert.Equal(0, view.GeneratedCoverCount);

        view.SettleForCapture(region);

        Assert.True(expected > 0);
        Assert.Equal(expected, view.GeneratedCoverCount);
        Assert.Single(scene.ClusterMeshLoads);
        Assert.Equal(0, view.TrackedProfileBuilds);
        scene.ClearFrame();
        view.Draw(focus);
        MeshHandle ground = Assert.Single(scene.Drawn, d => d.World == world).Handle;
        IReadOnlyList<GroundCoverInstance> cover = view.CoverIn(region);

        doc.SetCornerHeightCm(10, 10, 0, 200);
        view.MarkDirty(new TileRect(10, 10, 1, 1), 0);
        view.SettleForCapture(region);

        Assert.Equal(0, view.TrackedProfileBuilds);
        Assert.Same(cover, Assert.Single(scene.FoliageReleases));
        Assert.NotSame(cover, view.CoverIn(region));
        Assert.Single(scene.MeshUnloads, h => h.Equals(ground));
        scene.ClearFrame();
        view.Draw(focus);
        MeshHandle rebuilt = Assert.Single(scene.Drawn, d => d.World == world).Handle;
        Assert.NotEqual(ground, rebuilt);
        Assert.Contains(scene.GroundMeshes[rebuilt.Index].Vertices, v => Math.Abs(v.Position.Y - 2f) < 0.001f);
    }

    // Every Y on a region mesh's east border column that the east neighbour owns. The far corner of that column
    // belongs to the diagonal neighbour, which stays resident here, so it is left out.
    static IReadOnlyList<float> BorderHeights(RecordingTileWorldScene scene, int handle) =>
        scene.GroundMeshes[handle].Vertices
            .Where(v => Math.Abs(v.Position.X - TileRegion.Size) < 0.001f &&
                        Math.Abs(Math.Abs(v.Position.Z) - TileRegion.Size) > 0.001f)
            .Select(v => v.Position.Y)
            .ToList();
}
