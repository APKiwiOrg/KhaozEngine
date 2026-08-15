using System.IO;
using System.Linq;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TilePrefabTests
{
    static readonly TileWorldCatalogs Cat = TileWorldCatalogs.Greybox();

    static TileWorldDocument HouseWorld()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0), new RegionCoord(0, 1), new RegionCoord(1, 1));
        for (int x = 10; x < 13; x++) for (int z = 10; z < 12; z++) doc.SetOverlay(x, z, 0, 5);
        doc.SetOverlayShape(10, 10, 0, TileOverlayShape.DiagonalHalf);
        doc.SetOverlayRotation(10, 10, 0, 1);
        doc.SetSettings(11, 11, 0, TileSettings.Indoors);
        doc.SetCornerHeightCm(10, 10, 0, 100);
        doc.SetCornerHeightCm(13, 12, 0, 300);
        doc.AddObject("wall", 10, 10, 0, 0);
        doc.AddObject("rock_large", 11, 10, 0, 0);
        doc.SetMarker("door", 12, 10, 0);
        doc.SetUnderlay(10, 10, 1, 5);
        return doc;
    }

    [Fact]
    public void Extract_captures_layers_relative_heights_objects_and_markers()
    {
        TileWorldDocument doc = HouseWorld();
        TilePrefab p = TilePrefabs.Extract(doc, Cat, new TileRect(10, 10, 3, 2), 0, 2, name: "house");
        Assert.Equal((3, 2, 2), (p.Width, p.Height, p.PlaneCount));
        Assert.Equal(5, p.Planes[0]!.Overlay![0]);
        Assert.Equal((byte)TileOverlayShape.DiagonalHalf, p.Planes[0]!.OverlayShape![0]);
        Assert.Equal((byte)TileSettings.Indoors, p.Planes[0]!.Settings![1 * 3 + 1]);
        Assert.Equal(0, p.Planes[0]!.HeightsRelative![0]);
        Assert.Equal(200, p.Planes[0]!.HeightsRelative![2 * 4 + 3]);
        Assert.Equal(2, p.Objects.Count);
        Assert.Contains(p.Objects, o => o.ArchetypeId == "rock_large" && o.X == 1 && o.Z == 0);
        Assert.Single(p.Markers);
        Assert.Equal(5, p.Planes[1]!.Underlay![0]);
    }

    [Fact]
    public void Rotate_once_clockwise_moves_north_to_east()
    {
        TilePrefab p = TilePrefabs.Extract(HouseWorld(), Cat, new TileRect(10, 10, 3, 2), 0, 1);
        TilePrefab r = TilePrefabs.Rotate(p, 1);
        Assert.Equal((2, 3), (r.Width, r.Height));
        TilePrefabObject rock = r.Objects.First(o => o.ArchetypeId == "rock_large");
        Assert.Equal((0, 0), (rock.X, rock.Z));
        TilePrefabObject wall = r.Objects.First(o => o.ArchetypeId == "wall");
        Assert.Equal((0, 2, 1), (wall.X, wall.Z, wall.Rotation));
        Assert.Equal(2, r.Planes[0]!.OverlayRotation![0 * 2 + 0 + 2 * 2]);
        // The rotated SW corner came from the old (3, 0) corner at relative -100, so re-basing lifts every corner by 100.
        Assert.Equal(300 - 100 + 100, r.Planes[0]!.HeightsRelative![0 * 3 + 2]);
        Assert.Equal(0, r.Planes[0]!.HeightsRelative![0]);
        TilePrefabMarker door = r.Markers.Single();
        Assert.Equal((0, 0), (door.X, door.Z));
    }

    [Fact]
    public void Rotating_bumps_overlay_rotation_even_when_the_layer_was_trimmed()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(2, new RegionCoord(0, 0));
        for (int x = 4; x < 6; x++)
            for (int z = 4; z < 6; z++)
            {
                doc.SetOverlay(x, z, 0, 6);
                doc.SetOverlayShape(x, z, 0, TileOverlayShape.DiagonalHalf);
            }
        TilePrefab p = TilePrefabs.Extract(doc, Cat, new TileRect(4, 4, 2, 2), 0, 1);
        Assert.Null(p.Planes[0]!.OverlayRotation);
        TilePrefab r = TilePrefabs.Rotate(p, 1);
        Assert.NotNull(r.Planes[0]!.OverlayRotation);
        for (int i = 0; i < r.Planes[0]!.Overlay!.Length; i++)
            Assert.Equal(r.Planes[0]!.Overlay![i] != 0 ? 1 : 0, r.Planes[0]!.OverlayRotation![i]);
    }

    [Fact]
    public void Rotating_four_times_is_the_identity()
    {
        TilePrefab p = TilePrefabs.Extract(HouseWorld(), Cat, new TileRect(10, 10, 3, 2), 0, 2);
        TilePrefab r = TilePrefabs.Rotate(TilePrefabs.Rotate(TilePrefabs.Rotate(TilePrefabs.Rotate(p, 1), 1), 1), 1);
        Assert.Equal(p.Planes[0]!.Overlay, r.Planes[0]!.Overlay);
        Assert.Equal(p.Planes[0]!.HeightsRelative, r.Planes[0]!.HeightsRelative);
        Assert.Equal(p.Planes[1]!.HeightsRelative, r.Planes[1]!.HeightsRelative);
        Assert.Equal(p.Objects.Select(o => (o.X, o.Z, o.Rotation)), r.Objects.Select(o => (o.X, o.Z, o.Rotation)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Place_then_extract_round_trips_under_every_rotation(int rotation)
    {
        TileWorldDocument doc = HouseWorld();
        TilePrefab p = TilePrefabs.Extract(doc, Cat, new TileRect(10, 10, 3, 2), 0, 2);
        doc.SetCornerHeightCm(40, 40, 0, 50);
        TileRect touched = TilePrefabs.Place(doc, p, 40, 40, 0, rotation);
        TilePrefab expected = TilePrefabs.Rotate(p, rotation);
        TilePrefab back = TilePrefabs.Extract(doc, Cat, new TileRect(40, 40, expected.Width, expected.Height), 0, 2);
        Assert.Equal(expected.Planes[0]!.Overlay, back.Planes[0]!.Overlay);
        Assert.Equal(expected.Planes[0]!.OverlayShape, back.Planes[0]!.OverlayShape);
        Assert.Equal(expected.Planes[0]!.OverlayRotation, back.Planes[0]!.OverlayRotation);
        Assert.Equal(expected.Planes[0]!.Settings, back.Planes[0]!.Settings);
        Assert.Equal(expected.Planes[0]!.HeightsRelative, back.Planes[0]!.HeightsRelative);
        Assert.Equal(expected.Planes[1]!.Underlay, back.Planes[1]!.Underlay);
        Assert.Equal(expected.Objects.Select(o => (o.ArchetypeId, o.X, o.Z, o.Plane, o.Rotation)).OrderBy(t => t),
                     back.Objects.Select(o => (o.ArchetypeId, o.X, o.Z, o.Plane, o.Rotation)).OrderBy(t => t));
        // The datum invariant: the prefab's SW corner lands on the target's existing ground under every rotation,
        // and the re-base moves every plane by the same shift, so plane 1 keeps its lift above plane 0.
        Assert.Equal(50, doc.CornerHeightCm(40, 40, 0));
        Assert.Equal(50 + doc.PlaneHeightCm, doc.CornerHeightCm(40, 40, 1));
        Assert.True(touched.Contains(39, 39));
        Assert.True(touched.Contains(40 + expected.Width, 40 + expected.Height));
        Assert.All(doc.AllObjects().Where(o => o.X >= 40), o => Assert.True(o.Id > 2));
    }

    [Fact]
    public void Place_into_a_missing_region_throws_before_writing()
    {
        TileWorldDocument doc = HouseWorld();
        TilePrefab p = TilePrefabs.Extract(doc, Cat, new TileRect(10, 10, 3, 2), 0, 1);
        Assert.Throws<TileWorldException>(() => TilePrefabs.Place(doc, p, 126, 5, 0, 0));
        Assert.Equal(1, doc.GetUnderlay(126, 5, 0));
        Assert.Equal(0, doc.GetOverlay(126, 5, 0));
    }

    [Fact]
    public void Place_at_the_edge_of_the_authored_world_skips_the_far_corners_and_still_stamps_the_tiles()
    {
        TilePrefab p = TilePrefabs.Extract(HouseWorld(), Cat, new TileRect(10, 10, 3, 2), 0, 1, name: "house");
        // One region only, so the stamp's east corner column at x = 64 falls in a region that does not exist.
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0));
        TilePrefabs.Place(doc, p, 61, 10, 0, 0);
        Assert.Equal(5, doc.GetOverlay(61, 10, 0));
        Assert.Equal(5, doc.GetOverlay(63, 11, 0));
        Assert.Equal(TileOverlayShape.DiagonalHalf, doc.GetOverlayShape(61, 10, 0));
        Assert.Equal(TileSettings.Indoors, doc.GetSettings(62, 11, 0));
        // The dropped corner is edge-extended from the last authored column, which is the value it would have
        // read whatever the stamp wrote there.
        Assert.Equal(doc.CornerHeightCm(63, 10, 0), doc.CornerHeightCm(64, 10, 0));
        Assert.Equal(0, doc.CornerHeightCm(61, 10, 0));
    }

    [Fact]
    public void Place_rejects_a_prefab_whose_object_sits_on_a_plane_it_does_not_have()
    {
        TileWorldDocument doc = HouseWorld();
        TilePrefab p = TilePrefabs.Extract(doc, Cat, new TileRect(10, 10, 3, 2), 0, 1);
        p.Objects[0].Plane = -1;
        TileWorldException ex = Assert.Throws<TileWorldException>(() => TilePrefabs.Place(doc, p, 40, 40, 0, 0));
        Assert.Contains("plane -1", ex.Message);
        // Nothing was written: the layers go down before the first object is placed, so a plane caught late
        // would have torn the stamp.
        Assert.Equal(0, doc.GetOverlay(40, 40, 0));
        Assert.Equal(TileOverlayShape.Full, doc.GetOverlayShape(40, 40, 0));
    }

    [Fact]
    public void Loading_a_prefab_with_a_marker_on_a_plane_it_does_not_have_throws()
    {
        using var tmp = new TempDir();
        TilePrefab p = TilePrefabs.Extract(HouseWorld(), Cat, new TileRect(10, 10, 3, 2), 0, 1);
        p.Markers[0].Plane = 4;
        TilePrefabFile.Save(p, tmp.Sub("bad-plane.json"));
        TileWorldException ex = Assert.Throws<TileWorldException>(() => TilePrefabFile.Load(tmp.Sub("bad-plane.json")));
        Assert.Contains("bad-plane.json", ex.Message);
        Assert.Contains("plane 4", ex.Message);
    }

    [Fact]
    public void Prefab_file_round_trips()
    {
        using var tmp = new TempDir();
        TilePrefab p = TilePrefabs.Extract(HouseWorld(), Cat, new TileRect(10, 10, 3, 2), 0, 2, name: "house");
        TilePrefabFile.Save(p, tmp.Sub("house.json"));
        TilePrefab back = TilePrefabFile.Load(tmp.Sub("house.json"));
        Assert.Equal("house", back.Name);
        Assert.Equal(p.Planes[0]!.HeightsRelative, back.Planes[0]!.HeightsRelative);
        Assert.Equal(p.Objects.Count, back.Objects.Count);
        TilePrefabObject rock = back.Objects.First(o => o.ArchetypeId == "rock_large");
        Assert.Equal((2, 2), (rock.SizeX, rock.SizeZ));
        Assert.Null(back.Planes[1]!.Overlay);
    }

    [Fact]
    public void Place_rejects_a_prefab_whose_layers_do_not_match_its_size()
    {
        TileWorldDocument doc = HouseWorld();
        TilePrefab p = TilePrefabs.Extract(doc, Cat, new TileRect(10, 10, 3, 2), 0, 1);
        p.Planes[0]!.Underlay = new ushort[5];
        Assert.Throws<TileWorldException>(() => TilePrefabs.Place(doc, p, 40, 40, 0, 1));
        Assert.Equal(0, doc.GetOverlay(40, 40, 0));
    }

    [Fact]
    public void Loading_a_prefab_with_an_object_outside_its_footprint_throws()
    {
        using var tmp = new TempDir();
        TilePrefab p = TilePrefabs.Extract(HouseWorld(), Cat, new TileRect(10, 10, 3, 2), 0, 1);
        p.Objects[0].X = 7;
        TilePrefabFile.Save(p, tmp.Sub("bad.json"));
        TileWorldException ex = Assert.Throws<TileWorldException>(() => TilePrefabFile.Load(tmp.Sub("bad.json")));
        Assert.Contains("bad.json", ex.Message);
    }

    [Fact]
    public void Loading_a_prefab_with_null_object_and_marker_arrays_gives_empty_lists()
    {
        using var tmp = new TempDir();
        string path = tmp.Sub("sparse.json");
        File.WriteAllText(path, "{\"name\":\"s\",\"width\":1,\"height\":1,\"planeCount\":1,\"planes\":[null],\"objects\":null,\"markers\":null}");
        TilePrefab p = TilePrefabFile.Load(path);
        Assert.Empty(p.Objects);
        Assert.Empty(p.Markers);
    }
}
