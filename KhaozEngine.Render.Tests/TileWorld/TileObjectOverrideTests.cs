using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The per-object archetype override: drawing one placed object as a different archetype without
/// touching the document, what the narrow rebuild leaves alone, and the two cases that fall back to rebuilding
/// the region-plane's prop list. Every test drives the recording fake, so none of this needs a device.</summary>
public class TileObjectOverrideTests
{
    // Near enough to the house that every one of its props is inside the default draw radius, so a missing draw
    // is the override or a lost handle, never the distance cull. TileWorldViewTests' own focus.
    static readonly Vector3 HouseFocus = new(11f, 0f, -10.5f);

    static TileWorldView View(RecordingTileWorldScene scene, TileWorldDocument doc) =>
        new(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver());

    // The house's walls in document order, which is the order the ground placements come out in.
    static IReadOnlyList<TileObject> Walls(TileWorldDocument doc) =>
        doc.GetRegion(TileRenderTestData.Region)!.Objects.Where(o => o.ArchetypeId == "wall").ToList();

    // Every ground placement one frame drew, flattened: the house is one region-plane, so this is that plane's
    // list verbatim.
    static IReadOnlyList<PropPlacement> GroundDrawn(RecordingTileWorldScene scene) =>
        scene.PropDraws.SelectMany(r => r.Placements).Where(p => p.Id != "roof_flat").ToList();

    static void AssertSamePlacement(PropPlacement expected, PropPlacement got)
    {
        Assert.Equal(expected.Id, got.Id);
        Assert.Equal(expected.X, got.X);
        Assert.Equal(expected.Y, got.Y);
        Assert.Equal(expected.Z, got.Z);
        Assert.Equal(expected.Scale, got.Scale);
        Assert.Equal(expected.Yaw, got.Yaw);
        Assert.Equal(expected.Variant, got.Variant);
    }

    [Fact]
    public void Build_draws_an_overridden_object_as_the_override_and_leaves_the_rest_authored()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        TileObject wall = Walls(doc)[0];
        TileRegionProps authored =
            TileObjectProps.Build(doc, TileRenderTestData.Catalogs, TileRenderTestData.Region, 0);
        TileRegionProps overridden = TileObjectProps.Build(doc, TileRenderTestData.Catalogs,
            TileRenderTestData.Region, 0, id => id == wall.Id ? "bush" : null);

        Assert.Equal(authored.Ground.Count, overridden.Ground.Count);
        // The ids ride alongside so a caller can find the one entry a per-object change touches, which is what
        // the narrow rebuild is built on.
        Assert.Equal(authored.GroundObjectIds, overridden.GroundObjectIds);
        int at = authored.GroundObjectIds.ToList().IndexOf(wall.Id);
        Assert.True(at >= 0);
        Assert.Equal("wall", authored.Ground[at].Id);
        Assert.Equal("bush", overridden.Ground[at].Id);
        for (int i = 0; i < authored.Ground.Count; i++)
        {
            if (i == at) continue;
            AssertSamePlacement(authored.Ground[i], overridden.Ground[i]);
        }
        // The DOCUMENT is untouched, which is the whole reason this is a view fact: a client that edited its own
        // world copy would answer every later pick, reach test and save out of the edit.
        Assert.Equal("wall", doc.FindObject(wall.Id)!.ArchetypeId);
    }

    [Fact]
    public void An_override_rewrites_one_placement_and_leaves_the_rest_of_the_plane_byte_identical()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);
        view.Observer = new TileCoord(0, 0, 0);

        view.Draw(HouseFocus);
        List<PropPlacement> before = GroundDrawn(scene).ToList();
        int meshesBefore = scene.MeshLoads.Count;
        scene.ClearFrame();

        TileObject wall = Walls(doc)[0];
        Assert.True(view.OverrideArchetype(wall.Id, "bush"));
        Assert.Equal(1, view.ArchetypeOverrideCount);
        view.Draw(HouseFocus);
        List<PropPlacement> after = GroundDrawn(scene).ToList();

        Assert.Equal(before.Count, after.Count);
        int changed = 0;
        for (int i = 0; i < before.Count; i++)
        {
            if (before[i].Id == after[i].Id) { AssertSamePlacement(before[i], after[i]); continue; }
            changed++;
            Assert.Equal("wall", before[i].Id);
            Assert.Equal("bush", after[i].Id);
            // Same tile, same rotation, same footprint, so the anchor and the yaw are the authored ones: only
            // the mesh the placement names moved.
            Assert.Equal(before[i].X, after[i].X);
            Assert.Equal(before[i].Z, after[i].Z);
            Assert.Equal(before[i].Yaw, after[i].Yaw);
        }
        Assert.Equal(1, changed);
        // The ground mesh is untouched: a prop swap moves no vertex and no height, so remeshing and re-uploading
        // the region-plane is the cost this seam exists to avoid.
        Assert.Equal(meshesBefore, scene.MeshLoads.Count);
        Assert.Equal(0, view.PendingRebuilds);
    }

    [Fact]
    public void Clearing_an_override_puts_the_object_back_to_its_authored_archetype()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);
        view.Observer = new TileCoord(0, 0, 0);

        TileObject wall = Walls(doc)[0];
        view.OverrideArchetype(wall.Id, "bush");
        view.Draw(HouseFocus);
        Assert.Contains(GroundDrawn(scene), p => p.Id == "bush");
        scene.ClearFrame();

        Assert.True(view.ClearOverride(wall.Id));
        Assert.False(view.ClearOverride(wall.Id));   // nothing left to clear
        Assert.Equal(0, view.ArchetypeOverrideCount);
        view.Draw(HouseFocus);
        Assert.DoesNotContain(GroundDrawn(scene), p => p.Id == "bush");
    }

    [Fact]
    public void ClearOverrides_drops_every_override_at_once()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);
        view.Observer = new TileCoord(0, 0, 0);

        IReadOnlyList<TileObject> walls = Walls(doc);
        view.OverrideArchetype(walls[0].Id, "bush");
        view.OverrideArchetype(walls[1].Id, "tree");
        Assert.Equal(2, view.ArchetypeOverrideCount);
        Assert.True(view.TryGetOverride(walls[1].Id, out string drawnAs));
        Assert.Equal("tree", drawnAs);

        view.ClearOverrides();
        Assert.Equal(0, view.ArchetypeOverrideCount);
        Assert.False(view.TryGetOverride(walls[0].Id, out _));
        view.Draw(HouseFocus);
        Assert.DoesNotContain(GroundDrawn(scene), p => p.Id is "bush" or "tree");
    }

    [Fact]
    public void An_override_set_before_a_region_loads_applies_when_it_arrives()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        TileObject wall = Walls(doc)[0];
        using TileWorldView view = View(scene, doc);
        view.Observer = new TileCoord(0, 0, 0);

        // Nothing loaded, so nothing is rewritten now. Recorded rather than refused, the silhouette's contract:
        // a server message routinely arrives before the region it names.
        Assert.False(view.OverrideArchetype(wall.Id, "bush"));
        Assert.Equal(1, view.ArchetypeOverrideCount);

        view.LoadRegion(TileRenderTestData.Region);
        view.Draw(HouseFocus);
        Assert.Contains(GroundDrawn(scene), p => p.Id == "bush");
    }

    [Fact]
    public void An_override_to_an_archetype_the_catalogs_do_not_hold_draws_nothing_for_that_object()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);
        view.Observer = new TileCoord(0, 0, 0);

        view.Draw(HouseFocus);
        int before = GroundDrawn(scene).Count;
        scene.ClearFrame();

        // The same answer an object whose AUTHORED archetype is missing already gets: skipped, never thrown on,
        // because content routinely outlives a catalog edit.
        TileObject wall = Walls(doc)[0];
        Assert.True(view.OverrideArchetype(wall.Id, "no_such_archetype"));
        view.Draw(HouseFocus);
        Assert.Equal(before - 1, GroundDrawn(scene).Count);

        scene.ClearFrame();
        view.ClearOverride(wall.Id);
        view.Draw(HouseFocus);
        Assert.Equal(before, GroundDrawn(scene).Count);
    }

    [Fact]
    public void An_override_that_turns_an_object_into_a_roof_moves_it_between_the_two_lists()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);
        // Outdoors, so every roof draws and the count below is the roof list rather than the roof rule.
        view.Observer = new TileCoord(0, 0, 0);

        view.Draw(HouseFocus);
        int groundBefore = GroundDrawn(scene).Count;
        int roofsBefore = scene.PropDraws.SelectMany(r => r.Placements).Count(p => p.Id == "roof_flat");
        scene.ClearFrame();

        // The classification changed, so the narrow splice cannot answer where the entry belongs in its new list
        // and the view rebuilds the region-plane's props instead. Still no ground mesh.
        int meshesBefore = scene.MeshLoads.Count;
        TileObject wall = Walls(doc)[0];
        Assert.True(view.OverrideArchetype(wall.Id, "roof_flat"));
        view.Draw(HouseFocus);

        Assert.Equal(groundBefore - 1, GroundDrawn(scene).Count);
        Assert.Equal(roofsBefore + 1,
            scene.PropDraws.SelectMany(r => r.Placements).Count(p => p.Id == "roof_flat"));
        Assert.Equal(meshesBefore, scene.MeshLoads.Count);
    }

    [Fact]
    public void The_silhouette_follows_the_override_rather_than_the_document()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);
        view.Observer = new TileCoord(0, 0, 0);

        TileObject wall = Walls(doc)[0];
        view.SetSilhouettedObject(wall.Id, new KhaozEngine.Primitives.Color(255, 0, 0, 255));
        view.OverrideArchetype(wall.Id, "rock_large");
        view.Draw(HouseFocus);

        // rock_large is 2x2, so its anchor sits half a tile further along both axes than the 1x1 wall's. A hull
        // built off the document would sit on the wall's transform and float beside the mesh it is outlining.
        TileObjectArchetype rock = TileRenderTestData.Catalogs.Archetype("rock_large")!;
        Vector3 anchor = TileObjectProps.AnchorPosition(doc, rock, wall);
        Assert.NotEmpty(scene.Silhouettes);
        foreach ((_, Matrix4x4 world, _, _) in scene.Silhouettes)
            Assert.Equal(anchor, world.Translation);
    }
}
