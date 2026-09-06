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

        HeightResult set = f.Mutate.HeightsSet(rect, 0, Flat(3, 3, 100));
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

    /// <summary>The read verb and the write verb must agree on which end of the array is north, or an author
    /// who reads a patch, nudges one corner and writes it back silently mirrors their own terrain.</summary>
    [Fact]
    public void HeightsSet_TakesTheSameRowOrderHeightGetRectHandsBack()
    {
        using var f = new Fixture();
        var rect = new TileRect(0, 0, 4, 3);
        // Asymmetric on BOTH axes, so a flip in either direction shows up.
        f.Mutate.HeightsSet(rect, 0, new[]
        {
            new short[] { 10, 20, 30, 40 },
            new short[] { 50, 60, 70, 80 },
            new short[] { 90, 100, 110, 120 },
        });

        HeightMapResult read = f.Query.HeightGetRect(rect, 0);
        Assert.Equal(new short[] { 10, 20, 30, 40 }, read.Rows[0]);
        Assert.Equal(new short[] { 90, 100, 110, 120 }, read.Rows[2]);
        // Row 0 is the NORTH row, so its values sit on the rect's highest corner z.
        Assert.Equal(10, f.Session.Read(e => e.Document.CornerHeightCm(0, 2, 0)));
        Assert.Equal(90, f.Session.Read(e => e.Document.CornerHeightCm(0, 0, 0)));

        // Reading and writing straight back moves nothing at all, hash included.
        string before = f.Session.Summary().WorldHash;
        f.Mutate.HeightsSet(rect, 0, read.Rows);
        Assert.Equal(before, f.Session.Summary().WorldHash);
        Assert.Equal(read.Rows[0], f.Query.HeightGetRect(rect, 0).Rows[0]);
    }

    [Fact]
    public void HeightsSet_RefusesRowsThatDoNotMatchTheCornerRect()
    {
        using var f = new Fixture();
        var rect = new TileRect(0, 0, 3, 2);

        Assert.Throws<ArgumentException>(() => f.Mutate.HeightsSet(rect, 0, Flat(3, 3, 10)));
        Assert.Throws<ArgumentException>(() => f.Mutate.HeightsSet(rect, 0, Flat(2, 2, 10)));
        Assert.Throws<ArgumentNullException>(() => f.Mutate.HeightsSet(rect, 0, null!));
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
        // Without this, a scatter that placed NOTHING passes every line above by comparing two empty sets,
        // which is exactly how a bad plane went unnoticed for seven review rounds.
        Assert.NotEmpty(firstTiles);
        Assert.Equal(firstTiles, f.Query.ObjectFind(archetypeId: "bush").Select(o => o.X * 1000 + o.Z).ToArray());
    }

    [Fact]
    public void ObjectScatter_RefusesAPlaneTheWorldDoesNotHave()
    {
        using var f = new Fixture();

        // The collision map answers Blocked for a plane it does not have, so an unchecked scatter would skip
        // every point and report a successful placement of nothing.
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => f.Mutate.ObjectScatter("tree", new TileRect(20, 20, 8, 8), 9, 3, 1, seed: 7));
        Assert.Contains("0..3", ex.Message, StringComparison.Ordinal);
        Assert.Empty(f.Query.ObjectFind(archetypeId: "tree"));
    }

    [Fact]
    public void ObjectScatter_RefusesAnArchetypeTheCatalogsDoNotDefine()
    {
        using var f = new Fixture();

        // The archetype is otherwise only checked inside a PlaceObjectCommand, which a scatter that survives
        // no point never builds.
        TileWorldException ex = Assert.Throws<TileWorldException>(
            () => f.Mutate.ObjectScatter("no-such-archetype", new TileRect(20, 20, 8, 8), 0, 3, 1, seed: 7));
        Assert.Contains("no-such-archetype", ex.Message, StringComparison.Ordinal);
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

    /// <summary>Every verb is its own undo step, including two moves of the same object, which the command
    /// layer would otherwise coalesce into one. Over MCP each call is a discrete instruction a client issued on
    /// purpose, so a client stepping back through its own edits must land on each of them.</summary>
    [Fact]
    public void EachVerbIsOneUndoStep_EvenTwoMovesOfTheSameObject()
    {
        using var f = new Fixture();
        long id = f.Mutate.ObjectPlace("tree", 4, 4, 0).ObjectId;

        f.Mutate.ObjectMove(id, 5, 5, 0);
        MutationResult second = f.Mutate.ObjectMove(id, 6, 6, 0);

        // The fixture's fill, the placement, and the two moves as two separate steps.
        Assert.Equal(4, second.UndoDepth);
        Assert.Equal(6, f.Query.ObjectGet(id).X);

        // One undo goes back one MOVE, to where the first one left it, not all the way home.
        f.Mutate.Undo();
        Assert.Equal(5, f.Query.ObjectGet(id).X);
        f.Mutate.Undo();
        Assert.Equal(4, f.Query.ObjectGet(id).X);
    }

    [Fact]
    public void FoliageVerbs_SetDensityPaintRemoveAndUndoAsSingleSteps()
    {
        using var f = new Fixture();
        var spec = new FoliageLayerInfo("meadow", 0, -1f, -2f, 1f, 3, 2,
            new byte[] { 0, 0, 0, 0, 0, 0 }, 17, 0.3f, 0.8f, 1.2f, -0.04f,
            new[] { new FoliageArchetypeInfo("tree", 1f) }, new[] { 1 }, true, true, 1.5f, 0.5f);

        MutationResult configured = f.Mutate.FoliageLayerSet(spec);
        Assert.Equal("Set foliage layer", configured.Label);
        Assert.Equal(2, configured.UndoDepth);
        FoliageLayerInfo configuredLayer = f.Query.FoliageGet("meadow");
        Assert.Equal(spec.Id, configuredLayer.Id);
        Assert.Equal(spec.Density, configuredLayer.Density);
        Assert.Equal(spec.Archetypes, configuredLayer.Archetypes);
        Assert.Equal(spec.AllowedUnderlays, configuredLayer.AllowedUnderlays);

        MutationResult density = f.Mutate.FoliageDensitySet("meadow", 3, 2, new[]
        {
            new[] { 1, 2, 3 },
            new[] { 4, 5, 6 },
        });
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, f.Query.FoliageGet("meadow").Density);

        MutationResult painted = f.Mutate.FoliagePaint("meadow", 0f, -1f, 1.1f, 255, 1f);
        Assert.Equal("Set foliage layer", painted.Label);
        Assert.Equal((byte)255, f.Query.FoliageGet("meadow").Density[4]);

        MutationResult removed = f.Mutate.FoliageRemove("meadow");
        Assert.Equal("Remove foliage layer", removed.Label);
        Assert.Null(f.Session.Read(e => e.Document.GetFoliageLayer("meadow")));

        Assert.Equal(1, f.Mutate.Undo().Steps);
        Assert.NotNull(f.Query.FoliageGet("meadow"));
        Assert.Equal(1, f.Mutate.Undo().Steps);
        Assert.Equal((byte)5, f.Query.FoliageGet("meadow").Density[4]);
        Assert.Equal(1, f.Mutate.Redo().Steps);
        Assert.Equal((byte)255, f.Query.FoliageGet("meadow").Density[4]);
    }

    [Fact]
    public void RejectedFoliageRequestsChangeNeitherDocumentNorHistory()
    {
        using var f = new Fixture();
        int depth = f.Session.Summary().UndoDepth;
        string hash = f.Session.Summary().WorldHash;
        var missingArchetype = new FoliageLayerInfo("bad", 0, 0f, 0f, 1f, 1, 1,
            new byte[] { 255 }, 1, 0.3f, 1f, 1f, 0f,
            new[] { new FoliageArchetypeInfo("not-in-catalog", 1f) }, new[] { 1 }, true, true, 0f, 0f);

        Assert.Throws<TileWorldException>(() => f.Mutate.FoliageLayerSet(missingArchetype));
        Assert.Throws<TileWorldException>(() => f.Mutate.FoliageDensitySet("missing", 1, 1,
            new[] { new[] { 1 } }));
        Assert.Throws<TileWorldException>(() => f.Mutate.FoliagePaint("missing", 0f, 0f, 1f, 2, 0.5f));

        Assert.Equal(depth, f.Session.Summary().UndoDepth);
        Assert.Equal(hash, f.Session.Summary().WorldHash);
    }

    // The north-west corner of the 3 by 3 test lattice, which is row 0 (highest z) column 0 (lowest x).
    static short NorthWest(Fixture f) => f.Query.HeightGetRect(new TileRect(0, 0, 3, 3), 0).Rows[0][0];

    // A rows-shaped lattice of one repeated height, in the north-first shape HeightsSet takes.
    static short[][] Flat(int width, int height, short cm) =>
        Enumerable.Range(0, height).Select(_ => Enumerable.Repeat(cm, width).ToArray()).ToArray();
}
