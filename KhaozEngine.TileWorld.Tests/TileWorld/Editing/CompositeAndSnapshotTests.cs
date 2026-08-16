using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Editing;

/// <summary>Headless tests for <see cref="CompositeCommand"/> and <see cref="SnapshotRectCommand"/>: the
/// apply-forwards revert-backwards order, the atomicity of a composite whose child throws part way, and the
/// snapshot's exact restore of layers, corner heights, objects and markers, including the derived height layer
/// that only a re-null puts back byte for byte.</summary>
public class CompositeAndSnapshotTests
{
    static readonly TileWorldCatalogs Cat = TileWorldTestData.EditingCatalogs();

    static TileEditingDocument Editing(out TileWorldDocument doc)
    {
        doc = TileWorldTestData.FlatWorld();
        return new TileEditingDocument(doc, Cat);
    }

    // Writes one underlay and appends to a shared log on both directions, so a test can read the exact order a
    // composite drove its children in rather than inferring it from the document.
    sealed class RecordingCommand : TileCommandBase
    {
        readonly List<string> _log;
        readonly string _name;
        readonly int _x;
        readonly bool _throwOnApply;
        readonly bool _throwOnRevert;
        ushort _old;

        public RecordingCommand(List<string> log, string name, int x, bool throwOnApply = false,
            bool throwOnRevert = false) : base(name)
        {
            _log = log;
            _name = name;
            _x = x;
            _throwOnApply = throwOnApply;
            _throwOnRevert = throwOnRevert;
            Dirty.Add(new TileDirtyRect(new TileRect(x, 0, 1, 1), 0));
        }

        public override void Apply(TileWorldDocument doc)
        {
            if (_throwOnApply) throw new TileWorldException($"{_name} refuses to apply");
            _log.Add($"apply {_name}");
            _old = doc.GetUnderlay(_x, 0, 0);
            doc.SetUnderlay(_x, 0, 0, 9);
        }

        public override void Revert(TileWorldDocument doc)
        {
            _log.Add($"revert {_name}");
            if (_throwOnRevert) throw new TileWorldException($"{_name} refuses to revert");
            doc.SetUnderlay(_x, 0, 0, _old);
        }
    }

    [Fact]
    public void A_composite_applies_forwards_and_reverts_backwards()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        var log = new List<string>();
        var composite = new CompositeCommand("Three", new ITileCommand[]
        {
            new RecordingCommand(log, "a", 1),
            new RecordingCommand(log, "b", 2),
            new RecordingCommand(log, "c", 3),
        });

        ed.Execute(composite);

        Assert.Equal("Three", composite.Label);
        Assert.Equal(new[] { "apply a", "apply b", "apply c" }, log);
        Assert.Equal(9, doc.GetUnderlay(2, 0, 0));

        log.Clear();
        Assert.True(ed.Undo());
        Assert.Equal(new[] { "revert c", "revert b", "revert a" }, log);
        Assert.Equal(1, doc.GetUnderlay(2, 0, 0));

        log.Clear();
        Assert.True(ed.Redo());
        Assert.Equal(new[] { "apply a", "apply b", "apply c" }, log);
    }

    [Fact]
    public void A_composite_hands_out_every_child_rect_and_never_merges()
    {
        var log = new List<string>();
        var composite = new CompositeCommand("Two", new ITileCommand[]
        {
            new RecordingCommand(log, "a", 1),
            new RecordingCommand(log, "b", 2),
        });

        Assert.Equal(
            new[] { new TileDirtyRect(new TileRect(1, 0, 1, 1), 0), new TileDirtyRect(new TileRect(2, 0, 1, 1), 0) },
            composite.DirtyRects.ToArray());
        Assert.Equal(2, composite.Commands.Count);
        Assert.False(composite.TryMerge(new CompositeCommand("Two", composite.Commands)));
    }

    [Fact]
    public void A_child_that_throws_reverts_the_children_already_applied_and_rethrows()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        var log = new List<string>();
        var composite = new CompositeCommand("Doomed", new ITileCommand[]
        {
            new RecordingCommand(log, "a", 1),
            new RecordingCommand(log, "b", 2),
            new RecordingCommand(log, "c", 3, throwOnApply: true),
        });

        TileWorldException ex = Assert.Throws<TileWorldException>(() => ed.Execute(composite));

        Assert.Contains("c refuses", ex.Message);
        Assert.Equal(new[] { "apply a", "apply b", "revert b", "revert a" }, log);
        Assert.Equal(1, doc.GetUnderlay(1, 0, 0));
        Assert.Equal(1, doc.GetUnderlay(2, 0, 0));
        Assert.Equal(0, ed.History.UndoDepth);
        Assert.False(ed.IsDirty);
    }

    [Fact]
    public void A_rollback_that_throws_finishes_the_rest_and_keeps_the_original_failure_first()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        var log = new List<string>();
        var composite = new CompositeCommand("Stuck", new ITileCommand[]
        {
            new RecordingCommand(log, "a", 1),
            new RecordingCommand(log, "b", 2, throwOnRevert: true),
            new RecordingCommand(log, "c", 3, throwOnApply: true),
        });

        AggregateException ex = Assert.Throws<AggregateException>(() => ed.Execute(composite));

        // b's failed revert must not stop a's, and must not become the exception the caller sees first: the
        // apply failure is the one that explains the edit.
        Assert.Equal(new[] { "apply a", "apply b", "revert b", "revert a" }, log);
        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.Contains("c refuses to apply", ex.InnerExceptions[0].Message);
        Assert.Contains("b refuses to revert", ex.InnerExceptions[1].Message);
        Assert.Equal(1, doc.GetUnderlay(1, 0, 0));
        Assert.Equal(0, ed.History.UndoDepth);
    }

    [Fact]
    public void An_empty_composite_applies_and_reverts_cleanly()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        var composite = new CompositeCommand("Nothing", Array.Empty<ITileCommand>());

        ed.Execute(composite);

        Assert.Empty(composite.DirtyRects);
        Assert.True(ed.Undo());
        Assert.Single(doc.Regions);
    }

    [Fact]
    public void A_snapshot_restores_layers_heights_objects_and_markers_exactly()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetUnderlay(5, 5, 0, 3);
        doc.SetOverlay(5, 5, 0, 4);
        doc.SetOverlayShape(5, 5, 0, TileOverlayShape.DiagonalHalf);
        doc.SetOverlayRotation(5, 5, 0, 2);
        doc.SetSettings(5, 5, 0, TileSettings.Indoors);
        doc.SetCornerHeightCm(5, 5, 0, 150);
        TileObject inside = doc.AddObject("tree", 6, 6, 0, 1, new[] { "old" });
        doc.SetMarker("inside", 6, 5, 0, new[] { "m" });
        var ed = new TileEditingDocument(doc, Cat);
        string before = TileWorldHash.OfWorld(doc);

        var snapshot = new SnapshotRectCommand("Stamp", new TileRect(4, 4, 4, 4), new[] { 0 }, d =>
        {
            d.SetUnderlay(5, 5, 0, 6);
            d.SetOverlay(5, 5, 0, 0);
            d.SetOverlayShape(5, 5, 0, TileOverlayShape.Full);
            d.SetOverlayRotation(5, 5, 0, 0);
            d.SetSettings(5, 5, 0, TileSettings.None);
            d.SetCornerHeightCm(5, 5, 0, -40);
            d.RemoveObject(inside.Id);
            d.AddObject("bush", 4, 4, 0, 0);
            d.RemoveMarker("inside");
            d.SetMarker("added", 7, 7, 0, new[] { "new" });
        });
        ed.Execute(snapshot);

        Assert.Equal(6, doc.GetUnderlay(5, 5, 0));
        Assert.Equal(-40, doc.CornerHeightCm(5, 5, 0));
        Assert.Null(doc.FindObject(inside.Id));
        Assert.Null(doc.FindMarker("inside"));
        Assert.NotNull(doc.FindMarker("added"));
        Assert.NotEqual(before, TileWorldHash.OfWorld(doc));

        Assert.True(ed.Undo());

        Assert.Equal(3, doc.GetUnderlay(5, 5, 0));
        Assert.Equal(4, doc.GetOverlay(5, 5, 0));
        Assert.Equal(TileOverlayShape.DiagonalHalf, doc.GetOverlayShape(5, 5, 0));
        Assert.Equal(2, doc.GetOverlayRotation(5, 5, 0));
        Assert.Equal(TileSettings.Indoors, doc.GetSettings(5, 5, 0));
        Assert.Equal(150, doc.CornerHeightCm(5, 5, 0));
        TileObject back = doc.FindObject(inside.Id)!;
        Assert.Equal(inside.Id, back.Id);
        Assert.Equal(6, back.X);
        Assert.Equal(6, back.Z);
        Assert.Equal(1, back.Rotation);
        Assert.Equal(new[] { "old" }, back.Tags);
        TileMarker marker = doc.FindMarker("inside")!;
        Assert.Equal(6, marker.X);
        Assert.Equal(new[] { "m" }, marker.Tags);
        Assert.Null(doc.FindMarker("added"));
        Assert.Single(doc.AllObjects());
        Assert.Equal(before, TileWorldHash.OfWorld(doc));
    }

    [Fact]
    public void A_snapshot_reruns_its_mutation_on_redo_without_recapturing()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        int runs = 0;
        var snapshot = new SnapshotRectCommand("Paint", new TileRect(4, 4, 2, 2), new[] { 0 }, d =>
        {
            runs++;
            d.SetUnderlay(4, 4, 0, (ushort)(10 + runs));
        });

        ed.Execute(snapshot);
        Assert.Equal(11, doc.GetUnderlay(4, 4, 0));
        Assert.True(ed.Undo());
        Assert.Equal(1, doc.GetUnderlay(4, 4, 0));
        Assert.True(ed.Redo());
        Assert.Equal(2, runs);
        Assert.Equal(12, doc.GetUnderlay(4, 4, 0));

        // The redo must not have recaptured, so a second undo still restores the ORIGINAL underlay rather than
        // the 11 the first apply left behind.
        Assert.True(ed.Undo());
        Assert.Equal(1, doc.GetUnderlay(4, 4, 0));
    }

    [Fact]
    public void A_snapshot_puts_a_derived_height_layer_back_to_derived_on_a_higher_plane()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        var region = new RegionCoord(0, 0);
        Assert.Null(doc.GetRegion(region)!.Plane(1).Heights);
        string before = TileWorldHash.OfWorld(doc);

        var snapshot = new SnapshotRectCommand("Lift", new TileRect(4, 4, 4, 4), new[] { 1 },
            d => d.SetCornerHeightCm(5, 5, 1, 900));
        ed.Execute(snapshot);

        // The first write materialises the whole plane, which is a different thing on disk from "derive from
        // plane 0 plus the plane lift", so only putting the null back restores the document byte for byte.
        Assert.NotNull(doc.GetRegion(region)!.Plane(1).Heights);
        Assert.Equal(900, doc.CornerHeightCm(5, 5, 1));

        Assert.True(ed.Undo());

        Assert.Null(doc.GetRegion(region)!.Plane(1).Heights);
        Assert.Equal(300, doc.CornerHeightCm(5, 5, 1));
        Assert.Equal(before, TileWorldHash.OfWorld(doc));
    }

    [Fact]
    public void A_mutation_that_throws_part_way_is_rolled_back_and_the_exception_carries_on()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetUnderlay(5, 5, 0, 3);
        doc.SetCornerHeightCm(5, 5, 0, 150);
        TileObject standing = doc.AddObject("tree", 6, 6, 0, 1, new[] { "old" });
        doc.SetMarker("here", 6, 5, 0, null);
        var ed = new TileEditingDocument(doc, Cat);
        string before = TileWorldHash.OfWorld(doc);

        var snapshot = new SnapshotRectCommand("Half a stamp", new TileRect(4, 4, 4, 4), new[] { 0 }, d =>
        {
            d.SetUnderlay(5, 5, 0, 9);
            d.SetCornerHeightCm(5, 5, 0, -40);
            d.RemoveObject(standing.Id);
            d.AddObject("bush", 4, 4, 0, 0);
            d.RemoveMarker("here");
            throw new TileWorldException("the stamp gave up half way");
        });

        TileWorldException ex = Assert.Throws<TileWorldException>(() => ed.Execute(snapshot));

        // The pre-image is already in hand when the mutation throws, so there is no reason to leave the rect
        // half stamped and every reason not to: this command is discarded rather than pushed onto the stack.
        Assert.Contains("gave up half way", ex.Message);
        Assert.Equal(3, doc.GetUnderlay(5, 5, 0));
        Assert.Equal(150, doc.CornerHeightCm(5, 5, 0));
        Assert.Equal(6, doc.FindObject(standing.Id)!.X);
        Assert.NotNull(doc.FindMarker("here"));
        Assert.Single(doc.AllObjects());
        Assert.Equal(before, TileWorldHash.OfWorld(doc));
        Assert.Equal(0, ed.History.UndoDepth);
        Assert.False(ed.IsDirty);
    }

    [Fact]
    public void A_snapshot_reports_its_rect_on_every_listed_plane()
    {
        var rect = new TileRect(4, 4, 6, 7);
        var snapshot = new SnapshotRectCommand("Stamp", rect, new[] { 0, 2 }, _ => { });

        Assert.Equal(
            new[] { new TileDirtyRect(rect, 0), new TileDirtyRect(rect, 2) },
            snapshot.DirtyRects.ToArray());
    }

    [Fact]
    public void A_snapshot_does_not_capture_an_object_whose_anchor_is_outside_the_rect()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        // The hall is anchored at x 3, one tile west of the rect, and is 2 wide, so its footprint reaches into
        // the rect at x 4. Only the ANCHOR decides, which is the documented limitation of the snapshot.
        TileObject reaching = doc.AddObject("hall", 3, 5, 0, 0, null);
        var ed = new TileEditingDocument(doc, Cat);

        var snapshot = new SnapshotRectCommand("Clear", new TileRect(4, 4, 4, 4), new[] { 0 },
            d => d.RemoveObject(reaching.Id));
        ed.Execute(snapshot);
        Assert.Null(doc.FindObject(reaching.Id));

        Assert.True(ed.Undo());
        Assert.Null(doc.FindObject(reaching.Id));
    }
}
