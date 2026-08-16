using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Editing;

/// <summary>Headless tests for <see cref="SetTilesCommand"/>: the exact-undo round trip over all five layers,
/// the partial fill that leaves the layers it was not given alone, the all-or-nothing region validation, and
/// the dirty rect the editing document rebakes collision from.</summary>
public class LayerCommandTests
{
    static readonly TileWorldCatalogs Cat = TileWorldCatalogs.Greybox();

    static TileEditingDocument Editing(out TileWorldDocument doc)
    {
        doc = TileWorldTestData.FlatWorld();
        return new TileEditingDocument(doc, Cat);
    }

    static SetTilesCommand FullFill(TileRect rect, int plane) =>
        new(rect, plane, (ushort)3, (ushort)4, TileOverlayShape.CornerQuarter, 1, TileSettings.Blocked);

    static void AssertTile(TileWorldDocument doc, int x, int z, ushort underlay, ushort overlay,
        TileOverlayShape shape, int rotation, TileSettings settings)
    {
        Assert.Equal(underlay, doc.GetUnderlay(x, z, 0));
        Assert.Equal(overlay, doc.GetOverlay(x, z, 0));
        Assert.Equal(shape, doc.GetOverlayShape(x, z, 0));
        Assert.Equal(rotation, doc.GetOverlayRotation(x, z, 0));
        Assert.Equal(settings, doc.GetSettings(x, z, 0));
    }

    [Fact]
    public void Fill_round_trip_restores_every_layer_exactly()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        // One tile of the rect carries authored content and the rest are default, so the undo has to restore two
        // different states rather than one blanket value.
        doc.SetUnderlay(2, 2, 0, 6);
        doc.SetOverlay(2, 2, 0, 5);
        doc.SetOverlayShape(2, 2, 0, TileOverlayShape.DiagonalHalf);
        doc.SetOverlayRotation(2, 2, 0, 2);
        doc.SetSettings(2, 2, 0, TileSettings.Indoors);

        ed.Execute(FullFill(new TileRect(2, 2, 2, 2), 0));

        AssertTile(doc, 2, 2, 3, 4, TileOverlayShape.CornerQuarter, 1, TileSettings.Blocked);
        AssertTile(doc, 3, 3, 3, 4, TileOverlayShape.CornerQuarter, 1, TileSettings.Blocked);
        AssertTile(doc, 4, 4, 1, 0, TileOverlayShape.Full, 0, TileSettings.None);   // outside the rect

        Assert.True(ed.Undo());
        AssertTile(doc, 2, 2, 6, 5, TileOverlayShape.DiagonalHalf, 2, TileSettings.Indoors);
        AssertTile(doc, 3, 3, 1, 0, TileOverlayShape.Full, 0, TileSettings.None);

        Assert.True(ed.Redo());
        AssertTile(doc, 2, 2, 3, 4, TileOverlayShape.CornerQuarter, 1, TileSettings.Blocked);
        AssertTile(doc, 3, 3, 3, 4, TileOverlayShape.CornerQuarter, 1, TileSettings.Blocked);

        // The redo must not have recaptured: a second undo restores the ORIGINAL content, not what the redo
        // wrote. This is the whole reason the capture is gated on a first-apply flag.
        Assert.True(ed.Undo());
        AssertTile(doc, 2, 2, 6, 5, TileOverlayShape.DiagonalHalf, 2, TileSettings.Indoors);
        AssertTile(doc, 3, 3, 1, 0, TileOverlayShape.Full, 0, TileSettings.None);
    }

    [Fact]
    public void A_partial_fill_leaves_the_layers_it_was_not_given_alone()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        doc.SetOverlay(2, 2, 0, 5);
        doc.SetOverlayShape(2, 2, 0, TileOverlayShape.DiagonalHalf);
        doc.SetOverlayRotation(2, 2, 0, 3);
        doc.SetSettings(2, 2, 0, TileSettings.Indoors);

        ed.Execute(new SetTilesCommand(new TileRect(2, 2, 2, 2), 0, (ushort)7, null, null, null, null));

        AssertTile(doc, 2, 2, 7, 5, TileOverlayShape.DiagonalHalf, 3, TileSettings.Indoors);
        AssertTile(doc, 3, 3, 7, 0, TileOverlayShape.Full, 0, TileSettings.None);

        Assert.True(ed.Undo());
        AssertTile(doc, 2, 2, 1, 5, TileOverlayShape.DiagonalHalf, 3, TileSettings.Indoors);
        AssertTile(doc, 3, 3, 1, 0, TileOverlayShape.Full, 0, TileSettings.None);
    }

    [Fact]
    public void A_rect_reaching_a_missing_region_throws_before_writing_anything()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);   // region (0, 0) only

        // Tiles 62 and 63 are in the region that exists, 64 and 65 are in the one that does not.
        TileWorldException ex = Assert.Throws<TileWorldException>(
            () => ed.Execute(FullFill(new TileRect(62, 5, 4, 1), 0)));

        Assert.Contains("(1, 0)", ex.Message);
        AssertTile(doc, 62, 5, 1, 0, TileOverlayShape.Full, 0, TileSettings.None);
        AssertTile(doc, 63, 5, 1, 0, TileOverlayShape.Full, 0, TileSettings.None);
        Assert.Equal(0, ed.History.UndoDepth);
        Assert.False(ed.IsDirty);
    }

    [Fact]
    public void The_dirty_rect_is_the_filled_rect_on_the_filled_plane()
    {
        var rect = new TileRect(2, 3, 4, 5);
        SetTilesCommand fill = FullFill(rect, 2);

        Assert.Equal("Set tiles", fill.Label);
        Assert.Equal(new TileDirtyRect(rect, 2), Assert.Single(fill.DirtyRects));
    }

    [Fact]
    public void A_degenerate_rect_reports_no_dirty_rect_at_all()
    {
        Assert.Empty(FullFill(new TileRect(2, 3, 0, 4), 0).DirtyRects);
    }

    [Fact]
    public void A_fill_spanning_two_existing_regions_writes_both()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        var ed = new TileEditingDocument(doc, Cat);

        ed.Execute(FullFill(new TileRect(62, 5, 4, 1), 0));

        AssertTile(doc, 63, 5, 3, 4, TileOverlayShape.CornerQuarter, 1, TileSettings.Blocked);
        AssertTile(doc, 64, 5, 3, 4, TileOverlayShape.CornerQuarter, 1, TileSettings.Blocked);

        Assert.True(ed.Undo());
        AssertTile(doc, 63, 5, 1, 0, TileOverlayShape.Full, 0, TileSettings.None);
        AssertTile(doc, 64, 5, 1, 0, TileOverlayShape.Full, 0, TileSettings.None);
    }
}
