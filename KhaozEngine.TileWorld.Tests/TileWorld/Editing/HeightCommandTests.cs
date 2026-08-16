using System;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Editing;

/// <summary>Headless tests for <see cref="SetCornerHeightsCommand"/> and the <see cref="TileEditOps"/> height
/// factories: the corner round trip (including the corners outside any region, which are skipped rather than
/// thrown on), the dirty rect covering the tiles on BOTH sides of every corner, and the exact values raise,
/// flatten and smooth compute.</summary>
public class HeightCommandTests
{
    static readonly TileWorldCatalogs Cat = TileWorldCatalogs.Greybox();

    static TileEditingDocument Editing(out TileWorldDocument doc)
    {
        doc = TileWorldTestData.FlatWorld();
        return new TileEditingDocument(doc, Cat);
    }

    [Fact]
    public void Corner_height_round_trip_restores_the_old_values()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        doc.SetCornerHeightCm(10, 10, 0, 250);

        var cmd = new SetCornerHeightsCommand(new TileRect(10, 10, 2, 2), 0, new short[] { 100, 200, 300, 400 });
        ed.Execute(cmd);

        Assert.Equal("Set heights", cmd.Label);
        Assert.Equal(100, doc.CornerHeightCm(10, 10, 0));
        Assert.Equal(200, doc.CornerHeightCm(11, 10, 0));
        Assert.Equal(300, doc.CornerHeightCm(10, 11, 0));
        Assert.Equal(400, doc.CornerHeightCm(11, 11, 0));

        Assert.True(ed.Undo());
        Assert.Equal(250, doc.CornerHeightCm(10, 10, 0));
        Assert.Equal(0, doc.CornerHeightCm(11, 10, 0));
        Assert.Equal(0, doc.CornerHeightCm(10, 11, 0));
        Assert.Equal(0, doc.CornerHeightCm(11, 11, 0));

        Assert.True(ed.Redo());
        Assert.Equal(100, doc.CornerHeightCm(10, 10, 0));

        // The redo must not recapture, so a second undo still restores the ORIGINAL 250.
        Assert.True(ed.Undo());
        Assert.Equal(250, doc.CornerHeightCm(10, 10, 0));
    }

    [Fact]
    public void A_corner_whose_region_is_missing_is_skipped_and_revert_leaves_it()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);   // region (0, 0) only, tiles 0..63

        // Corner 63 is writable. Corner 64 reads by edge-extension from this region but has no region of its
        // own, so it cannot be written, and corner 65 resolves to nothing at all.
        var cmd = new SetCornerHeightsCommand(new TileRect(63, 5, 3, 1), 0, new short[] { 500, 700, 900 });
        ed.Execute(cmd);

        // The counts are what lets a tool say how many corners of the brush fell outside the world.
        Assert.Equal(3, cmd.CornerCount);
        Assert.Equal(1, cmd.WrittenCount);

        Assert.Equal(500, doc.CornerHeightCm(63, 5, 0));
        Assert.Equal(500, doc.CornerHeightCm(64, 5, 0));   // edge-extended from 63, NOT the 700 we asked for
        Assert.Equal(0, doc.CornerHeightCm(65, 5, 0));

        Assert.True(ed.Undo());
        Assert.Equal(0, doc.CornerHeightCm(63, 5, 0));
        Assert.Equal(0, doc.CornerHeightCm(64, 5, 0));
        Assert.Equal(0, doc.CornerHeightCm(65, 5, 0));
    }

    [Fact]
    public void The_dirty_rect_covers_the_tiles_on_both_sides_of_every_corner()
    {
        // A single corner at (5, 7) is shared by the four tiles (4, 6), (5, 6), (4, 7) and (5, 7).
        var one = new SetCornerHeightsCommand(new TileRect(5, 7, 1, 1), 0, new short[] { 10 });
        Assert.Equal(new TileDirtyRect(new TileRect(4, 6, 2, 2), 0), Assert.Single(one.DirtyRects));

        // Corners x 5..7 touch tiles 4..7, corners z 7..8 touch tiles 6..8.
        var many = new SetCornerHeightsCommand(new TileRect(5, 7, 3, 2), 2, new short[6]);
        Assert.Equal(new TileDirtyRect(new TileRect(4, 6, 4, 3), 2), Assert.Single(many.DirtyRects));
    }

    [Fact]
    public void A_value_array_that_does_not_match_the_rect_is_rejected()
    {
        Assert.Throws<ArgumentException>(
            () => new SetCornerHeightsCommand(new TileRect(0, 0, 2, 2), 0, new short[3]));
        Assert.Throws<ArgumentNullException>(
            () => new SetCornerHeightsCommand(new TileRect(0, 0, 2, 2), 0, null!));
    }

    [Fact]
    public void Raise_adds_a_flat_delta_when_there_is_no_falloff()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        doc.SetCornerHeightCm(10, 10, 0, 250);

        ed.Execute(TileEditOps.Raise(doc, new TileRect(10, 10, 2, 2), 0, 150));

        Assert.Equal(400, doc.CornerHeightCm(10, 10, 0));   // a delta over what was there, not an absolute
        Assert.Equal(150, doc.CornerHeightCm(11, 10, 0));
        Assert.Equal(150, doc.CornerHeightCm(10, 11, 0));
        Assert.Equal(150, doc.CornerHeightCm(11, 11, 0));

        Assert.True(ed.Undo());
        Assert.Equal(250, doc.CornerHeightCm(10, 10, 0));
        Assert.Equal(0, doc.CornerHeightCm(11, 10, 0));
    }

    [Fact]
    public void Raise_with_a_falloff_of_one_reaches_zero_on_the_edge_ring()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        // An edge corner with something already on it, so the zero delta out there is visible as "left where it
        // was" rather than being indistinguishable from the flat zero lattice around it.
        doc.SetCornerHeightCm(10, 10, 0, 250);

        // A 5 by 5 corner rect centred on (12, 12) reaches 2 corners out, so the weight is 1 - chebyshev / 2:
        // ring 0 keeps 100, ring 1 gets 50 and the edge ring gets nothing at all.
        ed.Execute(TileEditOps.Raise(doc, new TileRect(10, 10, 5, 5), 0, 100, falloff: 1f));

        Assert.Equal(100, doc.CornerHeightCm(12, 12, 0));
        Assert.Equal(50, doc.CornerHeightCm(11, 12, 0));
        Assert.Equal(50, doc.CornerHeightCm(12, 13, 0));
        Assert.Equal(50, doc.CornerHeightCm(13, 13, 0));
        Assert.Equal(250, doc.CornerHeightCm(10, 10, 0));   // edge ring, untouched by a zero delta
        Assert.Equal(0, doc.CornerHeightCm(12, 10, 0));
        Assert.Equal(0, doc.CornerHeightCm(14, 14, 0));
    }

    [Fact]
    public void Raise_falloff_handles_an_even_sized_rect_with_a_half_corner_centre()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        // A 4 by 4 corner rect has no middle corner: its centre falls on (11.5, 11.5) and its half extent is
        // 1.5. The inner 2 by 2 sits at chebyshev 0.5, so it keeps 1 - 0.5 / 1.5 of the delta, which rounds to
        // 67, and the outer ring sits at 1.5, exactly the extent, so it gets nothing.
        ed.Execute(TileEditOps.Raise(doc, new TileRect(10, 10, 4, 4), 0, 100, falloff: 1f));

        Assert.Equal(67, doc.CornerHeightCm(11, 11, 0));
        Assert.Equal(67, doc.CornerHeightCm(12, 11, 0));
        Assert.Equal(67, doc.CornerHeightCm(11, 12, 0));
        Assert.Equal(67, doc.CornerHeightCm(12, 12, 0));
        Assert.Equal(0, doc.CornerHeightCm(10, 10, 0));
        Assert.Equal(0, doc.CornerHeightCm(13, 13, 0));
        Assert.Equal(0, doc.CornerHeightCm(11, 10, 0));
        Assert.Equal(0, doc.CornerHeightCm(13, 11, 0));
    }

    [Fact]
    public void Raise_keeps_the_full_delta_when_the_rect_is_too_small_to_fade_across()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        // One corner wide, so there is no ring to fade out to and the falloff has nothing to divide by.
        ed.Execute(TileEditOps.Raise(doc, new TileRect(10, 10, 1, 1), 0, 100, falloff: 1f));

        Assert.Equal(100, doc.CornerHeightCm(10, 10, 0));
    }

    [Fact]
    public void Raise_clamps_to_the_short_range()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        doc.SetCornerHeightCm(20, 20, 0, 32000);

        ed.Execute(TileEditOps.Raise(doc, new TileRect(20, 20, 1, 1), 0, 1000));

        Assert.Equal(short.MaxValue, doc.CornerHeightCm(20, 20, 0));
    }

    [Fact]
    public void Flatten_writes_the_given_height_to_every_corner()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        doc.SetCornerHeightCm(10, 10, 0, 250);

        ed.Execute(TileEditOps.Flatten(doc, new TileRect(10, 10, 2, 2), 0, (short)75));

        Assert.Equal(75, doc.CornerHeightCm(10, 10, 0));
        Assert.Equal(75, doc.CornerHeightCm(11, 11, 0));
    }

    [Fact]
    public void Flatten_with_no_height_uses_the_rounded_average_of_the_corners()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        doc.SetCornerHeightCm(10, 10, 0, 100);
        doc.SetCornerHeightCm(11, 10, 0, 200);
        doc.SetCornerHeightCm(10, 11, 0, 300);
        // (11, 11) stays 0, so the average of 100, 200, 300 and 0 is 150.

        ed.Execute(TileEditOps.Flatten(doc, new TileRect(10, 10, 2, 2), 0, null));

        Assert.Equal(150, doc.CornerHeightCm(10, 10, 0));
        Assert.Equal(150, doc.CornerHeightCm(11, 10, 0));
        Assert.Equal(150, doc.CornerHeightCm(10, 11, 0));
        Assert.Equal(150, doc.CornerHeightCm(11, 11, 0));
    }

    [Fact]
    public void Flatten_rounds_a_half_centimetre_average_away_from_zero()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        doc.SetCornerHeightCm(10, 10, 0, 2);   // the other three corners are 0, so the average is 0.5

        ed.Execute(TileEditOps.Flatten(doc, new TileRect(10, 10, 2, 2), 0, null));

        Assert.Equal(1, doc.CornerHeightCm(11, 11, 0));
    }

    [Fact]
    public void Smooth_box_blurs_the_lattice_and_reads_its_neighbours_from_the_document()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        doc.SetCornerHeightCm(5, 5, 0, 900);
        doc.SetCornerHeightCm(7, 5, 0, 90);   // outside the smoothed rect, so it only ever feeds into it

        ed.Execute(TileEditOps.Smooth(doc, new TileRect(4, 4, 3, 3), 0, 1));

        // Every corner of the rect sees the 900 spike, so all nine land on 100, except the x = 6 column, whose
        // 3 by 3 window also reaches the untouched 90 at x = 7 and lands on 110.
        for (int z = 4; z <= 6; z++)
        {
            Assert.Equal(100, doc.CornerHeightCm(4, z, 0));
            Assert.Equal(100, doc.CornerHeightCm(5, z, 0));
            Assert.Equal(110, doc.CornerHeightCm(6, z, 0));
        }

        Assert.Equal(90, doc.CornerHeightCm(7, 5, 0));   // a neighbour outside the rect is never written

        Assert.True(ed.Undo());
        Assert.Equal(900, doc.CornerHeightCm(5, 5, 0));
        Assert.Equal(0, doc.CornerHeightCm(4, 4, 0));
    }

    [Fact]
    public void Smooth_iterates_over_its_own_result()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);
        doc.SetCornerHeightCm(5, 5, 0, 900);

        // One corner, so each pass divides by nine against eight unchanged zero neighbours: 900 to 100 to 11.
        ed.Execute(TileEditOps.Smooth(doc, new TileRect(5, 5, 1, 1), 0, 2));

        Assert.Equal(11, doc.CornerHeightCm(5, 5, 0));
    }

    [Fact]
    public void Smooth_rejects_an_iteration_count_outside_its_bounds()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();

        Assert.Throws<ArgumentOutOfRangeException>(() => TileEditOps.Smooth(doc, new TileRect(4, 4, 3, 3), 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TileEditOps.Smooth(doc, new TileRect(4, 4, 3, 3), 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => TileEditOps.Smooth(doc, new TileRect(4, 4, 3, 3), 0, 65));
        TileEditOps.Smooth(doc, new TileRect(4, 4, 3, 3), 0, 64);   // the ceiling itself is allowed
    }

    [Fact]
    public void SetHeights_builds_the_plain_command_over_the_given_values()
    {
        TileEditingDocument ed = Editing(out TileWorldDocument doc);

        SetCornerHeightsCommand cmd = TileEditOps.SetHeights(doc, new TileRect(10, 10, 2, 1), 0, new short[] { 40, 80 });
        ed.Execute(cmd);

        Assert.Equal(new TileDirtyRect(new TileRect(9, 9, 3, 2), 0), Assert.Single(cmd.DirtyRects));
        Assert.Equal(40, doc.CornerHeightCm(10, 10, 0));
        Assert.Equal(80, doc.CornerHeightCm(11, 10, 0));
    }
}
