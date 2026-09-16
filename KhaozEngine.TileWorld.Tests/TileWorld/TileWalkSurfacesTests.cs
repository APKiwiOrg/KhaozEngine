using System;
using System.Numerics;
using KhaozEngine.TileWorld;
using Xunit;
using static KhaozEngine.Tests.TileWorld.TileWalkSurfaceTestData;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The walk-surface queries over the bridge crossing: which point a deck covers, under every rotation and a
/// yaw offset, which top wins where two overlap, what the terrain has no say in, and where a ray lands.</summary>
public class TileWalkSurfacesTests
{
    const float Tolerance = 1e-4f;

    static float? HeightAt(TileWorldDocument doc, TileWorldCatalogs catalogs, float worldX, float worldZ, int plane = 0) =>
        TileWalkSurfaces.TryHeightAt(doc, catalogs, worldX, worldZ, plane, out float height) ? height : null;

    [Fact]
    public void The_deck_centre_stands_at_the_anchor_plus_the_deck_height()
    {
        TileWorldDocument doc = BridgeWorld();

        float? deck = HeightAt(doc, Catalogs(), CentreX, CentreZ);

        Assert.NotNull(deck);
        // The anchor is the bed, not the bank, and not the plane floor.
        Assert.Equal(-0.8f, doc.HeightAt(CentreX, CentreZ, 0), Tolerance);
        Assert.Equal(0.025f, deck!.Value, Tolerance);
        Assert.Equal(DeckTop, deck.Value, Tolerance);
    }

    [Fact]
    public void The_deck_overhangs_the_footprint_onto_both_bank_tiles()
    {
        TileWorldDocument doc = BridgeWorld();
        TileWorldCatalogs catalogs = Catalogs();

        // Tile 19 west of the footprint and tile 23 east of it, on the slope from the bank down to the bed.
        Assert.Equal(DeckTop, HeightAt(doc, catalogs, 19.5f, CentreZ)!.Value, Tolerance);
        Assert.Equal(DeckTop, HeightAt(doc, catalogs, 23.5f, CentreZ)!.Value, Tolerance);
        Assert.Equal(-0.4f, doc.HeightAt(19.5f, CentreZ, 0), Tolerance);
    }

    [Fact]
    public void Edges_are_inclusive_and_a_point_just_outside_the_rect_is_not_covered()
    {
        TileWorldDocument doc = BridgeWorld();
        TileWorldCatalogs catalogs = Catalogs();

        Assert.NotNull(HeightAt(doc, catalogs, 19f, CentreZ));
        Assert.NotNull(HeightAt(doc, catalogs, 24f, CentreZ));
        Assert.NotNull(HeightAt(doc, catalogs, CentreX, -23f));
        Assert.NotNull(HeightAt(doc, catalogs, CentreX, -20f));

        Assert.Null(HeightAt(doc, catalogs, 18.99f, CentreZ));
        Assert.Null(HeightAt(doc, catalogs, 24.01f, CentreZ));
        Assert.Null(HeightAt(doc, catalogs, CentreX, -23.01f));
        Assert.Null(HeightAt(doc, catalogs, CentreX, -19.99f));
    }

    [Fact]
    public void Rotation_1_carries_the_overhang_onto_the_z_axis()
    {
        TileWorldDocument doc = BridgeWorld(rotation: 1);
        TileWorldCatalogs catalogs = Catalogs();

        // Local +x turns to world +z, which is tile SOUTH, so the overhang now reaches tile z 19 and tile z 23.
        Assert.Equal(DeckTop, HeightAt(doc, catalogs, CentreX, -19.5f)!.Value, Tolerance);
        Assert.Equal(DeckTop, HeightAt(doc, catalogs, CentreX, -23.5f)!.Value, Tolerance);
        Assert.NotNull(HeightAt(doc, catalogs, CentreX, -19f));
        // And no longer reaches the west and east banks.
        Assert.Null(HeightAt(doc, catalogs, 19.5f, CentreZ));
        Assert.Null(HeightAt(doc, catalogs, 23.5f, CentreZ));
        Assert.Null(HeightAt(doc, catalogs, 19.99f, CentreZ));
    }

    [Fact]
    public void Every_rotation_places_the_rect_where_the_renderer_places_its_corner()
    {
        TileWorldCatalogs catalogs = Catalogs();
        TileObjectArchetype bridge = catalogs.Archetype("bridge")!;
        for (int rotation = 0; rotation < 4; rotation++)
        {
            TileWorldDocument doc = BridgeWorld(rotation);
            TileObject o = doc.FindObject(1)!;
            Matrix4x4 world = TileObjectPlacement.LocalToWorld(doc, bridge, o);
            // The deck's local (+2.25, +1.25) corner region, carried through the drawn matrix, is covered, and the
            // mirror of it past the far corner is not.
            Vector3 inside = Vector3.Transform(new Vector3(2.25f, 0f, 1.25f), world);
            Vector3 outside = Vector3.Transform(new Vector3(2.75f, 0f, 1.25f), world);
            Assert.NotNull(HeightAt(doc, catalogs, inside.X, inside.Z));
            Assert.Null(HeightAt(doc, catalogs, outside.X, outside.Z));
        }
    }

    [Fact]
    public void The_archetype_yaw_offset_is_honoured()
    {
        TileWorldCatalogs quarter = Catalogs();
        quarter.Archetype("bridge")!.YawOffsetDegrees = 90f;
        TileWorldDocument doc = BridgeWorld();

        // A quarter-turn offset at rotation 0 is rotation 1.
        Assert.NotNull(HeightAt(doc, quarter, CentreX, -19.5f));
        Assert.Null(HeightAt(doc, quarter, 19.5f, CentreZ));

        // An off-axis offset: local (2.4, 0) turned 45 degrees clockwise lands 1.697 m east and 1.697 m south,
        // which is outside the unturned deck's 1.5 m half depth.
        TileWorldCatalogs diagonal = Catalogs();
        diagonal.Archetype("bridge")!.YawOffsetDegrees = 45f;
        float d = 2.4f * MathF.Sqrt(0.5f);
        Assert.NotNull(HeightAt(doc, diagonal, CentreX + d, CentreZ + d));
        Assert.Null(HeightAt(doc, Catalogs(), CentreX + d, CentreZ + d));
    }

    [Fact]
    public void Two_overlapping_surfaces_answer_the_higher()
    {
        TileWorldDocument doc = BridgeWorld();
        doc.AddObject("platform", 21, 21, 0, 0);   // tile (21, 21), 1.5 m over the bed
        doc.AddObject("stepped", 20, 20, 0, 0);    // tiles 20..22 on row 20, 1 m and a 1.2 m strip over the bed
        TileWorldCatalogs catalogs = Catalogs();

        // Deck and platform: the platform.
        Assert.Equal(-0.8f + 1.5f, HeightAt(doc, catalogs, CentreX, CentreZ)!.Value, Tolerance);
        // Deck and both of the stepped piece's tops: the strip, which also beats its own object's lower top.
        Assert.Equal(-0.8f + 1.2f, HeightAt(doc, catalogs, CentreX, -20.5f)!.Value, Tolerance);
        // Deck and the stepped piece's lower top alone.
        Assert.Equal(-0.8f + 1f, HeightAt(doc, catalogs, 20.25f, -20.5f)!.Value, Tolerance);
        // Deck alone.
        Assert.Equal(DeckTop, HeightAt(doc, catalogs, 19.5f, CentreZ)!.Value, Tolerance);
    }

    [Fact]
    public void A_surface_buried_under_the_terrain_is_still_reported()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.AddObject("cellar", 5, 5, 0, 0);

        float? top = HeightAt(doc, Catalogs(), 5.5f, -5.5f);

        // The terrain is at 0, so the query answers below it. The higher of the two is TileDocumentGroundHeight's.
        Assert.Equal(-0.5f, top!.Value, Tolerance);
    }

    [Fact]
    public void A_surface_on_another_plane_is_ignored()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.AddObject("bridge", 40, 40, 1, 0);
        TileWorldCatalogs catalogs = Catalogs();

        Assert.Null(HeightAt(doc, catalogs, 41.5f, -41.5f, plane: 0));
        Assert.Null(HeightAt(doc, catalogs, 41.5f, -41.5f, plane: 2));
        Assert.Null(HeightAt(doc, catalogs, 41.5f, -41.5f, plane: 99));
        Assert.Equal(doc.PlaneHeight + DeckHeight, HeightAt(doc, catalogs, 41.5f, -41.5f, plane: 1)!.Value, Tolerance);
    }

    [Fact]
    public void A_catalog_with_no_surfaced_archetype_answers_false()
    {
        TileWorldDocument doc = BridgeWorld(archetype: "rock_large");

        Assert.False(TileWalkSurfaces.TryHeightAt(doc, TileWorldCatalogs.Greybox(), CentreX, CentreZ, 0, out float height));
        Assert.Equal(0f, height);
        Assert.Equal(-1, TileWalkSurfaces.ReachTiles(TileWorldCatalogs.Greybox(), doc.TileSize));
    }

    [Fact]
    public void An_object_the_catalogs_do_not_define_is_skipped()
    {
        TileWorldDocument doc = BridgeWorld();
        doc.AddObject("not_in_any_catalog", 21, 21, 0, 0);

        Assert.Equal(DeckTop, HeightAt(doc, Catalogs(), CentreX, CentreZ)!.Value, Tolerance);
    }

    [Fact]
    public void An_overhang_across_a_region_border_is_found_from_the_neighbouring_region()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        doc.AddObject("bridge", TileRegion.Size - 2, 10, 0, 0);   // footprint 62..64, deck 61..66

        float? top = HeightAt(doc, Catalogs(), TileRegion.Size + 1.5f, -11.5f);

        Assert.Equal(DeckHeight, top!.Value, Tolerance);
    }

    [Fact]
    public void Edits_are_visible_to_the_next_call()
    {
        TileWorldDocument doc = BridgeWorld();
        TileWorldCatalogs catalogs = Catalogs();
        Assert.NotNull(HeightAt(doc, catalogs, 19.5f, CentreZ));

        catalogs.Archetype("bridge")!.WalkSurfaces![0].MinX = -1.5f;
        Assert.Null(HeightAt(doc, catalogs, 19.5f, CentreZ));

        doc.RemoveObject(1);
        Assert.Null(HeightAt(doc, catalogs, CentreX, CentreZ));
    }

    [Fact]
    public void A_ray_straight_down_lands_on_the_deck_with_its_tile_and_distance()
    {
        TileWorldDocument doc = BridgeWorld();

        TileHit hit = Assert.IsType<TileHit>(TileWalkSurfaces.Raycast(doc, Catalogs(), 0,
            new Vector3(CentreX, 10f, CentreZ), new Vector3(0f, -2f, 0f), 2000f));

        Assert.Equal((BridgeX + 1, BridgeZ + 1, 0), (hit.X, hit.Z, hit.Plane));
        Assert.Equal(DeckTop, hit.Point.Y, Tolerance);
        Assert.Equal(10f - DeckTop, hit.Distance, Tolerance);
    }

    [Fact]
    public void An_oblique_ray_lands_on_the_overhang_and_measures_world_metres()
    {
        TileWorldDocument doc = BridgeWorld();
        var origin = new Vector3(16.5f, DeckTop + 3f, CentreZ);

        TileHit hit = Assert.IsType<TileHit>(TileWalkSurfaces.Raycast(doc, Catalogs(), 0, origin,
            new Vector3(1f, -1f, 0f), 2000f));

        Assert.Equal((19, BridgeZ + 1), (hit.X, hit.Z));
        Assert.Equal(19.5f, hit.Point.X, Tolerance);
        Assert.Equal(3f * MathF.Sqrt(2f), hit.Distance, Tolerance);
        Assert.Null(TileWalkSurfaces.Raycast(doc, Catalogs(), 0, origin, new Vector3(1f, -1f, 0f), 4.2f));
    }

    [Fact]
    public void A_rising_or_level_ray_never_lands_on_a_top()
    {
        TileWorldDocument doc = BridgeWorld();
        TileWorldCatalogs catalogs = Catalogs();

        Assert.Null(TileWalkSurfaces.Raycast(doc, catalogs, 0, new Vector3(CentreX, -0.5f, CentreZ), Vector3.UnitY, 2000f));
        Assert.Null(TileWalkSurfaces.Raycast(doc, catalogs, 0, new Vector3(16f, DeckTop, CentreZ), Vector3.UnitX, 2000f));
    }

    [Fact]
    public void A_ray_past_the_deck_or_on_another_plane_misses()
    {
        TileWorldDocument doc = BridgeWorld();
        TileWorldCatalogs catalogs = Catalogs();

        Assert.Null(TileWalkSurfaces.Raycast(doc, catalogs, 0, new Vector3(18.5f, 10f, CentreZ), -Vector3.UnitY, 2000f));
        Assert.Null(TileWalkSurfaces.Raycast(doc, catalogs, 1, new Vector3(CentreX, 10f, CentreZ), -Vector3.UnitY, 2000f));
    }

    [Fact]
    public void The_include_filter_excludes_an_object()
    {
        TileWorldDocument doc = BridgeWorld();
        TileWorldCatalogs catalogs = Catalogs();
        var origin = new Vector3(CentreX, 10f, CentreZ);

        Assert.Null(TileWalkSurfaces.Raycast(doc, catalogs, 0, origin, -Vector3.UnitY, 2000f, o => o.Id != 1));
        Assert.NotNull(TileWalkSurfaces.Raycast(doc, catalogs, 0, origin, -Vector3.UnitY, 2000f, o => o.Id == 1));
    }

    [Fact]
    public void The_nearest_top_wins_where_two_stack()
    {
        TileWorldDocument doc = BridgeWorld();
        doc.AddObject("platform", 21, 21, 0, 0);

        TileHit hit = Assert.IsType<TileHit>(TileWalkSurfaces.Raycast(doc, Catalogs(), 0,
            new Vector3(CentreX, 10f, CentreZ), -Vector3.UnitY, float.PositiveInfinity));

        Assert.Equal(-0.8f + 1.5f, hit.Point.Y, Tolerance);
    }
}
