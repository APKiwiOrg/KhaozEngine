using System;
using System.IO;
using System.Linq;
using KhaozEngine.TileEdit;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileEdit;

/// <summary>Every mutation verb once through the service: the result it hands back, and that undoing it puts the
/// world back. The undo half matters more than it looks: a verb that mutated the document directly instead of
/// through a command would pass every other assertion here and fail only that one.</summary>
public class MutationServiceTests
{
    sealed class Fixture : IDisposable
    {
        public TempDir Temp { get; } = new();
        public TileEditSession Session { get; }
        public QueryService Query { get; }
        public MutationService Mutate { get; }

        public Fixture()
        {
            Session = TileEditTestWorld.NewSession(Temp.Sub("world"));
            Query = new QueryService(Session);
            Mutate = new MutationService(Session);
            Mutate.TilesFill(new TileRect(0, 0, TileRegion.Size, TileRegion.Size), 0, underlay: 1);
        }

        public void Dispose() => Temp.Dispose();
    }

    [Fact]
    public void TilesFillAndClear_WriteTheLayersAndUndoBack()
    {
        using var f = new Fixture();

        MutationResult filled = f.Mutate.TilesFill(new TileRect(1, 1, 2, 2), 0,
            underlay: 2, overlay: 3, shape: TileOverlayShape.CornerQuarter, rotation: 2,
            settings: TileSettings.Indoors);

        Assert.Equal("Set tiles", filled.Label);
        Assert.True(filled.Dirty);
        Assert.Single(filled.DirtyRects);
        TileInfo tile = f.Query.TileGet(1, 1, 0);
        Assert.Equal((ushort)2, tile.Underlay);
        Assert.Equal((ushort)3, tile.Overlay);
        Assert.Equal("CornerQuarter", tile.Shape);
        Assert.Equal(2, tile.Rotation);
        Assert.Equal("Indoors", tile.Settings);

        f.Mutate.TilesClear(new TileRect(1, 1, 2, 2), 0);
        TileInfo cleared = f.Query.TileGet(1, 1, 0);
        Assert.Equal((ushort)0, cleared.Underlay);
        Assert.Equal("Full", cleared.Shape);
        Assert.Equal("None", cleared.Settings);
        // Void ground is impassable, which is what makes clear a destructive verb worth undoing.
        Assert.True(cleared.Blocked);

        f.Mutate.Undo(2);
        Assert.Equal((ushort)1, f.Query.TileGet(1, 1, 0).Underlay);
    }

    [Fact]
    public void HeightVerbs_WriteTheLatticeAndReportTheCornerCounts()
    {
        using var f = new Fixture();
        var rect = new TileRect(0, 0, 3, 3);

        HeightResult set = f.Mutate.HeightsSet(rect, 0, Enumerable.Repeat((short)100, 9).ToArray());
        Assert.Equal(9, set.CornerCount);
        Assert.Equal(9, set.WrittenCount);
        Assert.Equal(100, NorthWest(f));

        f.Mutate.HeightsRaise(rect, 0, 50);
        Assert.Equal(150, NorthWest(f));

        f.Mutate.HeightsFlatten(rect, 0, 20);
        Assert.Equal(20, NorthWest(f));

        // One blur pass over a flat 20 patch: the middle corner keeps its neighbours and stays put, while the
        // north-west one averages five corners of 20 with four of the zero ground outside the rect, which is
        // 80 over 9 rounded away from zero.
        f.Mutate.HeightsSmooth(rect, 0, 1);
        Assert.Equal(20, f.Query.HeightGetRect(rect, 0).Rows[1][1]);
        Assert.Equal(9, NorthWest(f));

        // Four commands back to a flat zero lattice.
        f.Mutate.Undo(4);
        Assert.Equal(0, NorthWest(f));
    }

    [Fact]
    public void HeightsRaise_FadesTowardTheEdgeWithFalloff()
    {
        using var f = new Fixture();
        var rect = new TileRect(0, 0, 3, 3);

        f.Mutate.HeightsRaise(rect, 0, 100, falloff: 1f);

        short[][] rows = f.Query.HeightGetRect(rect, 0).Rows.ToArray();
        Assert.Equal(100, rows[1][1]);
        Assert.Equal(0, rows[0][0]);
    }

    [Fact]
    public void HeightsImport_ResamplesAPgmAgainstTheWorldDirectory()
    {
        using var f = new Fixture();
        // A 2 by 2 binary PGM (P5, maxval 255): black on the north row, white on the south one.
        var pgm = new byte[] { (byte)'P', (byte)'5', (byte)'\n', (byte)'2', (byte)' ', (byte)'2', (byte)'\n',
            (byte)'2', (byte)'5', (byte)'5', (byte)'\n', 0, 0, 255, 255 };
        File.WriteAllBytes(Path.Combine(f.Session.DocumentPath!, "hills.pgm"), pgm);

        HeightResult imported = f.Mutate.HeightsImport("hills.pgm", new TileRect(0, 0, 2, 2), 0, 0, 1000);

        Assert.Equal(4, imported.CornerCount);
        Assert.Equal(4, imported.WrittenCount);
        short[][] rows = f.Query.HeightGetRect(new TileRect(0, 0, 2, 2), 0).Rows.ToArray();
        // Image row 0 is NORTH, so the black row lands on the rect's highest z.
        Assert.Equal(new short[] { 0, 0 }, rows[0]);
        Assert.Equal(new short[] { 1000, 1000 }, rows[1]);
    }

    [Fact]
    public void ObjectVerbs_PlaceMoveRotateTagAndRemove()
    {
        using var f = new Fixture();

        ObjectPlaceResult placed = f.Mutate.ObjectPlace("bench", 4, 4, 0, rotation: 0, tags: new[] { "seat" });
        Assert.True(placed.ObjectId > 0);
        Assert.Equal("Place object", placed.Label);
        Assert.Equal(new RectInfo(4, 4, 1, 2), f.Query.ObjectGet(placed.ObjectId).Footprint);

        f.Mutate.ObjectMove(placed.ObjectId, 10, 10, 0);
        Assert.Equal(10, f.Query.ObjectGet(placed.ObjectId).X);

        f.Mutate.ObjectRotate(placed.ObjectId, 1);
        Assert.Equal(new RectInfo(10, 10, 2, 1), f.Query.ObjectGet(placed.ObjectId).Footprint);

        f.Mutate.ObjectSetTags(placed.ObjectId, new[] { "seat", "park" });
        Assert.Equal(new[] { "seat", "park" }, f.Query.ObjectGet(placed.ObjectId).Tags);

        MutationResult removed = f.Mutate.ObjectRemove(placed.ObjectId);
        Assert.Equal("Remove object", removed.Label);
        Assert.Throws<TileWorldException>(() => f.Query.ObjectGet(placed.ObjectId));

        // The undo puts it back with the SAME id, so nothing that referred to it is left dangling.
        f.Mutate.Undo();
        Assert.Equal(10, f.Query.ObjectGet(placed.ObjectId).X);
    }

    [Fact]
    public void ObjectLineAndScatter_LandAsOneStepAndReportTheirIds()
    {
        using var f = new Fixture();

        PlacementBatchResult line = f.Mutate.ObjectLine("wall", 2, 2, 6, 2, 0);

        Assert.Equal("Line", line.Label);
        Assert.Equal(5, line.Count);
        Assert.Equal(5, line.ObjectIds.Count);
        Assert.Equal(line.ObjectIds, f.Query.ObjectFind(archetypeId: "wall").Select(o => o.Id).ToArray());

        PlacementBatchResult scatter = f.Mutate.ObjectScatter("tree", new TileRect(20, 20, 8, 8), 0, 3, 1, seed: 7);
        Assert.Equal("Scatter", scatter.Label);
        Assert.True(scatter.Count > 0, "a scatter over open ground placed nothing.");
        Assert.Equal(scatter.Count, f.Query.ObjectFind(archetypeId: "tree").Count);

        // Each batch is ONE undo step, not one per object.
        f.Mutate.Undo();
        Assert.Empty(f.Query.ObjectFind(archetypeId: "tree"));
        f.Mutate.Undo();
        Assert.Empty(f.Query.ObjectFind(archetypeId: "wall"));
    }

    [Fact]
    public void ObjectScatter_IsDeterministic()
    {
        using var f = new Fixture();

        PlacementBatchResult first = f.Mutate.ObjectScatter("bush", new TileRect(30, 30, 10, 10), 0, 4, 2, seed: 11);
        int[] firstTiles = f.Query.ObjectFind(archetypeId: "bush").Select(o => o.X * 1000 + o.Z).ToArray();
        f.Mutate.Undo();
        PlacementBatchResult second = f.Mutate.ObjectScatter("bush", new TileRect(30, 30, 10, 10), 0, 4, 2, seed: 11);

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(firstTiles, f.Query.ObjectFind(archetypeId: "bush").Select(o => o.X * 1000 + o.Z).ToArray());
    }

    [Fact]
    public void MarkerVerbs_SetAndRemoveWithUndo()
    {
        using var f = new Fixture();

        MutationResult set = f.Mutate.MarkerSet("bank", 12, 12, 0, new[] { "shop" });
        Assert.Equal("Set marker", set.Label);
        Assert.Empty(set.DirtyRects);
        MarkerInfo marker = Assert.Single(f.Query.MarkerList());
        Assert.Equal(12, marker.X);

        f.Mutate.MarkerRemove("bank");
        Assert.Empty(f.Query.MarkerList());

        f.Mutate.Undo();
        Assert.Equal("bank", Assert.Single(f.Query.MarkerList()).Name);
    }

    [Fact]
    public void RegionVerbs_CreateAndDeleteWholeRegions()
    {
        using var f = new Fixture();

        f.Mutate.RegionCreate(1, 0);
        Assert.Equal(2, f.Query.RegionList().Count);
        // A fresh region is void ground, so it reads blocked until something paints it.
        Assert.True(f.Query.TileGet(70, 0, 0).Blocked);
        Assert.Equal("(1, 0)", f.Query.TileGet(70, 0, 0).Region);

        f.Mutate.TilesFill(new TileRect(64, 0, 4, 4), 0, underlay: 1);
        Assert.False(f.Query.TileGet(65, 1, 0).Blocked);

        MutationResult deleted = f.Mutate.RegionDelete(1, 0);
        Assert.Equal("Delete region", deleted.Label);
        Assert.Equal("missing", f.Query.TileGet(65, 1, 0).Region);

        f.Mutate.Undo();
        Assert.Equal("(1, 0)", f.Query.TileGet(65, 1, 0).Region);
        Assert.False(f.Query.TileGet(65, 1, 0).Blocked);
    }

    [Fact]
    public void PrefabExtractAndPlace_RoundTripThroughAFile()
    {
        using var f = new Fixture();
        f.Mutate.TilesFill(new TileRect(0, 0, 2, 2), 0, underlay: 2);
        f.Mutate.ObjectPlace("tree", 1, 1, 0);

        PrefabSaveResult saved = f.Mutate.PrefabExtract(new TileRect(0, 0, 2, 2), 0, 1, "prefabs/corner.json");

        Assert.Equal("corner", saved.Name);
        Assert.Equal(2, saved.Width);
        Assert.Equal(1, saved.ObjectCount);
        Assert.True(File.Exists(saved.Path));

        MutationResult placed = f.Mutate.PrefabPlace("prefabs/corner.json", 40, 40, 0);

        Assert.Equal("Place prefab", placed.Label);
        Assert.Equal((ushort)2, f.Query.TileGet(40, 40, 0).Underlay);
        Assert.Single(f.Query.ObjectsInRect(new TileRect(40, 40, 2, 2), 0));

        f.Mutate.Undo();
        Assert.Equal((ushort)1, f.Query.TileGet(40, 40, 0).Underlay);
        Assert.Empty(f.Query.ObjectsInRect(new TileRect(40, 40, 2, 2), 0));
    }

    [Fact]
    public void Moves_CoalesceIntoOneStepAfterAGestureSeal()
    {
        using var f = new Fixture();
        long id = f.Mutate.ObjectPlace("tree", 4, 4, 0).ObjectId;
        // The seal ends the placement's gesture, so the drag that follows starts its own undo step instead of
        // being absorbed into the placement.
        f.Mutate.SealGesture();

        f.Mutate.ObjectMove(id, 5, 5, 0);
        MutationResult second = f.Mutate.ObjectMove(id, 6, 6, 0);

        // The fixture's fill, the placement, and the two moves as ONE step.
        Assert.Equal(3, second.UndoDepth);
        Assert.Equal(6, f.Query.ObjectGet(id).X);

        f.Mutate.Undo();
        Assert.Equal(4, f.Query.ObjectGet(id).X);
    }

    // The north-west corner of the 3 by 3 test lattice, which is row 0 (highest z) column 0 (lowest x).
    static short NorthWest(Fixture f) => f.Query.HeightGetRect(new TileRect(0, 0, 3, 3), 0).Rows[0][0];
}
