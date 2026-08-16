using System.Linq;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Editing;

/// <summary>Headless tests for the <see cref="TileEditOps"/> object factories: the Bresenham tile set a line
/// walks, the determinism and the skip rules of a scatter, and the prefab stamp whose undo has to leave the
/// world hashing byte for byte the way it did before the stamp.</summary>
public class TileEditOpsTests
{
    static readonly TileWorldCatalogs Cat = TileWorldTestData.EditingCatalogs();

    static TileEditingDocument Editing(out TileWorldDocument doc)
    {
        doc = TileWorldTestData.FlatWorld();
        return new TileEditingDocument(doc, Cat);
    }

    static (int X, int Z)[] Anchors(TileWorldDocument doc, string archetypeId) =>
        doc.AllObjects().Where(o => o.ArchetypeId == archetypeId).Select(o => (o.X, o.Z)).ToArray();

    [Fact]
    public void Line_walks_the_Bresenham_tiles_of_a_diagonal_and_includes_both_ends()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        CompositeCommand line = TileEditOps.Line(Cat, "wall", (10, 10), (14, 12), 0, 1);
        ed.Execute(line);

        Assert.Equal("Line", line.Label);
        Assert.Equal(5, line.Commands.Count);
        Assert.Equal(new[] { (10, 10), (11, 11), (12, 11), (13, 12), (14, 12) }, Anchors(doc, "wall"));
        Assert.All(doc.AllObjects(), o => Assert.Equal(1, o.Rotation));

        Assert.True(ed.Undo());
        Assert.Empty(doc.AllObjects());
    }

    [Fact]
    public void Line_along_an_axis_places_every_tile_between_the_ends()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        ed.Execute(TileEditOps.Line(Cat, "fence", (2, 2), (2, 6), 0, 0));

        Assert.Equal(new[] { (2, 2), (2, 3), (2, 4), (2, 5), (2, 6) }, Anchors(doc, "fence"));
    }

    [Fact]
    public void Line_that_starts_where_it_ends_places_exactly_one_object()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        CompositeCommand line = TileEditOps.Line(Cat, "tree", (5, 5), (5, 5), 0, 0);
        ed.Execute(line);

        Assert.Single(line.Commands);
        Assert.Equal(new[] { (5, 5) }, Anchors(doc, "tree"));
    }

    [Fact]
    public void Line_runs_west_and_south_as_readily_as_east_and_north()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        ed.Execute(TileEditOps.Line(Cat, "tree", (14, 12), (10, 10), 0, 0));

        Assert.Equal(new[] { (14, 12), (13, 11), (12, 11), (11, 10), (10, 10) }, Anchors(doc, "tree"));
    }

    [Fact]
    public void Scatter_skips_blocked_tiles_and_tiles_that_already_hold_an_anchor()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetSettings(11, 11, 0, TileSettings.Blocked);
        // A bush blocks nothing at all, so this pins the ANCHOR rule rather than the collision one: a tile that
        // already carries an object must not get a second one stacked on it.
        doc.AddObject("bush", 10, 12, 0, 0, null);
        var ed = new TileEditingDocument(doc, Cat);

        CompositeCommand scatter = TileEditOps.Scatter(ed, "tree", new TileRect(10, 10, 3, 3), 0, 1, 0, 7);
        ed.Execute(scatter);

        Assert.Equal("Scatter", scatter.Label);
        Assert.Equal(
            new[] { (10, 10), (11, 10), (12, 10), (10, 11), (12, 11), (11, 12), (12, 12) },
            Anchors(doc, "tree").OrderBy(t => t.Z).ThenBy(t => t.X).ToArray());

        Assert.True(ed.Undo());
        Assert.Equal(new[] { (10, 12) }, Anchors(doc, "bush"));
        Assert.Empty(Anchors(doc, "tree"));
    }

    [Fact]
    public void Scatter_over_a_region_that_does_not_exist_places_nothing()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        // The collision map answers Blocked for a region it does not have, which is the same skip as a wall.
        CompositeCommand scatter = TileEditOps.Scatter(ed, "tree", new TileRect(200, 200, 8, 8), 0, 2, 0, 3);
        ed.Execute(scatter);

        Assert.Empty(scatter.Commands);
        Assert.Empty(doc.AllObjects());
    }

    [Fact]
    public void Scatter_lands_on_its_golden_positions()
    {
        // The golden. Scatter is world CONTENT, not an implementation detail: an author who scatters a forest,
        // saves the world and comes back expects the same forest, and a consumer that re-runs a seeded scatter
        // expects the world it already shipped. Self-consistency between two runs in one process cannot see a
        // change to Mix or Jitter, which is exactly the change that would silently move every tree in every
        // world ever scattered. Changing either of those is a content-breaking change, and it has to update
        // these literals deliberately, with the worlds already authored against the old ones accounted for.
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        ed.Execute(TileEditOps.Scatter(ed, "tree", new TileRect(10, 10, 12, 12), 0, 4, 1, 20260816));

        // Nine grid points, seven placements: two of them jitter out of the rect (one off the south edge to
        // z 9, one off the west edge to x 9) and are dropped, which is the skip rule doing its job rather than
        // a gap in the golden.
        Assert.Equal(
            new[] { (11, 10), (14, 11), (13, 13), (19, 14), (10, 17), (13, 17), (19, 18) },
            Anchors(doc, "tree"));
    }

    [Fact]
    public void Scatter_is_deterministic_per_seed_and_a_different_seed_lands_differently()
    {
        (int X, int Z)[] first = ScatterAnchors(1234);
        (int X, int Z)[] again = ScatterAnchors(1234);
        (int X, int Z)[] other = ScatterAnchors(4321);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        // 5 grid columns by 5 grid rows at spacing 4 over a 20 by 20 rect: the jitter can only push a point off
        // the grid, never mint a new one.
        Assert.True(first.Length <= 25, $"{first.Length} placements from 25 grid points");
        Assert.NotEmpty(first);
    }

    static (int X, int Z)[] ScatterAnchors(int seed)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        var ed = new TileEditingDocument(doc, Cat);
        ed.Execute(TileEditOps.Scatter(ed, "tree", new TileRect(10, 10, 20, 20), 0, 4, 1, seed));
        return Anchors(doc, "tree");
    }

    [Fact]
    public void Scatter_with_no_jitter_lands_exactly_on_the_grid()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        ed.Execute(TileEditOps.Scatter(ed, "tree", new TileRect(10, 10, 9, 9), 0, 4, 0, 99));

        Assert.Equal(
            new[]
            {
                (10, 10), (14, 10), (18, 10),
                (10, 14), (14, 14), (18, 14),
                (10, 18), (14, 18), (18, 18),
            },
            Anchors(doc, "tree").OrderBy(t => t.Z).ThenBy(t => t.X).ToArray());
    }

    [Fact]
    public void Scatter_never_stacks_two_of_its_own_placements_on_one_tile()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        // Spacing 1 with jitter drives neighbouring grid points onto the same tile, which is exactly the case
        // the claimed-tile set exists for.
        ed.Execute(TileEditOps.Scatter(ed, "tree", new TileRect(10, 10, 10, 10), 0, 1, 2, 55));

        (int X, int Z)[] anchors = Anchors(doc, "tree");
        Assert.Equal(anchors.Length, anchors.Distinct().Count());
    }

    // A 2 wide by 3 deep stamp carrying every layer plus an object and a marker, so a rotation moves real
    // content around rather than shuffling zeroes.
    static TilePrefab SamplePrefab()
    {
        TileWorldDocument src = TileWorldTestData.FlatWorld();
        src.SetUnderlay(2, 2, 0, 3);
        src.SetOverlay(2, 3, 0, 5);
        src.SetOverlayShape(2, 3, 0, TileOverlayShape.CornerQuarter);
        src.SetSettings(3, 2, 0, TileSettings.Indoors);
        src.SetCornerHeightCm(3, 3, 0, 120);
        src.AddObject("tree", 2, 2, 0, 1, new[] { "prefab" });
        src.SetMarker("prefab_spawn", 3, 4, 0, null);
        return TilePrefabs.Extract(src, Cat, new TileRect(2, 2, 2, 3), 0, 1, name: "cottage");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PlacePrefab_claims_exactly_the_rect_that_TilePrefabs_Place_touches(int rotation)
    {
        TilePrefab prefab = SamplePrefab();
        TileWorldDocument scratch = TileWorldTestData.FlatWorld();
        TileRect touched = TilePrefabs.Place(scratch, prefab, 20, 20, 0, rotation);

        SnapshotRectCommand cmd = TileEditOps.PlacePrefab(prefab, 20, 20, 0, rotation);

        Assert.Equal("Place prefab", cmd.Label);
        Assert.Equal(new TileDirtyRect(touched, 0), Assert.Single(cmd.DirtyRects));
    }

    [Fact]
    public void PlacePrefab_rect_for_a_known_prefab_and_rotation_is_the_hand_computed_one()
    {
        // The sample prefab is 2 wide by 3 deep. A quarter turn swaps that to 3 by 2, and Place touches the
        // tile rect grown one tile west and south and one row and column north and east, for the corner writes
        // on its far edges: FromCorners(20 - 1, 20 - 1, 20 + 3, 20 + 2), which is (19, 19) 5 wide by 4 deep.
        // A literal rather than a second call to the same code, so a change to either the rotation swap or the
        // touched-rect formula has to be a deliberate edit here.
        SnapshotRectCommand cmd = TileEditOps.PlacePrefab(SamplePrefab(), 20, 20, 0, 1);

        Assert.Equal(new TileRect(19, 19, 5, 4), Assert.Single(cmd.DirtyRects).Rect);
    }

    [Fact]
    public void PlacePrefab_across_a_region_border_on_a_higher_plane_still_undoes_byte_for_byte()
    {
        // At x 61 a 2 wide stamp keeps its TILES inside region (0, 0), but the snapshot's far corner column
        // lands at x 64, in region (1, 0). Above plane 0 a write to that corner materialises region (1, 0)'s
        // whole derived height lattice, which is a different thing on disk from deriving it, so the restore has
        // to know that region had no height layer even though no tile of the stamp is in it.
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        var ed = new TileEditingDocument(doc, Cat);
        string before = TileWorldHash.OfWorld(doc);
        Assert.Null(doc.GetRegion(new RegionCoord(1, 0))!.Plane(1).Heights);

        ed.Execute(TileEditOps.PlacePrefab(SamplePrefab(), 61, 20, 1, 0));

        Assert.NotEqual(before, TileWorldHash.OfWorld(doc));

        Assert.True(ed.Undo());

        Assert.Null(doc.GetRegion(new RegionCoord(1, 0))!.Plane(1).Heights);
        Assert.Null(doc.GetRegion(new RegionCoord(0, 0))!.Plane(1).Heights);
        Assert.Equal(before, TileWorldHash.OfWorld(doc));
    }

    [Fact]
    public void PlacePrefab_claims_one_rect_per_plane_the_prefab_carries()
    {
        var prefab = new TilePrefab { Name = "stack", Width = 2, Height = 2, PlaneCount = 3 };
        for (int i = 0; i < 3; i++) prefab.Planes.Add(new TilePrefabPlane { Underlay = new ushort[] { 1, 1, 1, 1 } });

        SnapshotRectCommand cmd = TileEditOps.PlacePrefab(prefab, 20, 20, 1, 0);

        Assert.Equal(new[] { 1, 2, 3 }, cmd.DirtyRects.Select(d => d.Plane).ToArray());
    }

    [Fact]
    public void PlacePrefab_then_undo_leaves_the_world_hashing_byte_for_byte_as_it_did()
    {
        TilePrefab prefab = SamplePrefab();
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        // Authored content the stamp lands on top of, so the undo has to restore values rather than blanks: a
        // tile it overwrites, a corner height it moves, an object and a marker inside the rect it must not eat.
        doc.SetUnderlay(21, 21, 0, 6);
        doc.SetCornerHeightCm(21, 21, 0, 45);
        TileObject bush = doc.AddObject("bush", 20, 22, 0, 0, new[] { "native" });
        doc.SetMarker("kept", 22, 20, 0, new[] { "here" });
        var ed = new TileEditingDocument(doc, Cat);
        string before = TileWorldHash.OfWorld(doc);

        ed.Execute(TileEditOps.PlacePrefab(prefab, 20, 20, 0, 1));

        Assert.NotEqual(before, TileWorldHash.OfWorld(doc));
        Assert.NotNull(doc.FindMarker("prefab_spawn"));

        Assert.True(ed.Undo());

        Assert.Equal(before, TileWorldHash.OfWorld(doc));
        Assert.Null(doc.FindMarker("prefab_spawn"));
        Assert.Equal(6, doc.GetUnderlay(21, 21, 0));
        Assert.Equal(45, doc.CornerHeightCm(21, 21, 0));
        TileObject backBush = doc.FindObject(bush.Id)!;
        Assert.Equal(20, backBush.X);
        Assert.Equal(22, backBush.Z);
        Assert.Equal(new[] { "native" }, backBush.Tags);
        TileMarker kept = doc.FindMarker("kept")!;
        Assert.Equal(22, kept.X);
        Assert.Equal(new[] { "here" }, kept.Tags);
    }
}
