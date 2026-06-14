using System.Collections.Generic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class SheetAssemblerTests
{
    // All 8 PixelLab direction names, in any order.
    private static readonly string[] Dirs =
    {
        "south", "south-east", "east", "north-east", "north", "north-west", "west", "south-west",
    };

    // A frame with a single opaque pixel at (px, py) on a transparent canvas of (w, h).
    private static Image<Rgba32> Dot(int w, int h, int px, int py)
    {
        var img = new Image<Rgba32>(w, h);
        img[px, py] = new Rgba32(255, 255, 255, 255);
        return img;
    }

    private static CharacterAnimation FullAnim(int w, int h, int frameCount, int px, int py)
    {
        var byDir = new Dictionary<string, IReadOnlyList<FrameEntry>>();
        foreach (var d in Dirs)
        {
            var frames = new List<FrameEntry>();
            for (int i = 0; i < frameCount; i++)
                frames.Add(new FrameEntry(i, Dot(w, h, px, py)));
            byDir[d] = frames;
        }
        return new CharacterAnimation("Test", "walking", byDir);
    }

    [Fact]
    public void Sheet_dimensions_are_8_rows_by_frameCount_columns()
    {
        var anim = FullAnim(w: 16, h: 16, frameCount: 4, px: 8, py: 15);
        using var r = SheetAssembler.Assemble(anim, new AssemblyOptions()).Sheet;

        Assert.Equal(16 * 4, r.Width);
        Assert.Equal(16 * 8, r.Height);
    }

    [Fact]
    public void Cell_size_is_max_of_frame_dimensions()
    {
        // Make one direction's frames larger; the cell must grow to fit the largest.
        var anim = FullAnim(w: 16, h: 16, frameCount: 2, px: 8, py: 15);
        var big = new List<FrameEntry> { new(0, Dot(20, 24, 10, 23)), new(1, Dot(20, 24, 10, 23)) };
        var byDir = new Dictionary<string, IReadOnlyList<FrameEntry>>(anim.FramesByDir) { ["south"] = big };
        var anim2 = anim with { FramesByDir = byDir };

        var result = SheetAssembler.Assemble(anim2, new AssemblyOptions());
        using var sheet = result.Sheet;

        Assert.Equal(20, result.CellWidth);
        Assert.Equal(24, result.CellHeight);
    }

    [Fact]
    public void Feet_land_on_baseline_regardless_of_source_vertical_position()
    {
        // bottomPad 0 => baseline row = cellH-1. The opaque pixel (the "foot") must land there
        // in every cell, even though the source pixel sits at different y in different frames.
        var byDir = new Dictionary<string, IReadOnlyList<FrameEntry>>();
        foreach (var d in Dirs)
        {
            // frame 0 foot high in canvas (y=5), frame 1 foot low (y=15): both must end at baseline.
            byDir[d] = new List<FrameEntry> { new(0, Dot(16, 16, 8, 5)), new(1, Dot(16, 16, 8, 15)) };
        }
        var anim = new CharacterAnimation("Test", "walking", byDir);

        var result = SheetAssembler.Assemble(anim, new AssemblyOptions(BottomPad: 0));
        using var sheet = result.Sheet;

        // south is row 0. Both columns: opaque pixel must be at cell-local y = 15 (baseline).
        Assert.Equal(255, sheet[8, 15].A);        // row 0, col 0, baseline
        Assert.Equal(255, sheet[16 + 8, 15].A);   // row 0, col 1, baseline
        // And NOT floating above (the source y differences are normalized away).
        Assert.Equal(0, sheet[8, 5].A);
    }

    [Fact]
    public void BottomPad_lifts_feet_off_the_cell_bottom()
    {
        var anim = FullAnim(w: 16, h: 16, frameCount: 1, px: 8, py: 15);
        var result = SheetAssembler.Assemble(anim, new AssemblyOptions(BottomPad: 2));
        using var sheet = result.Sheet;

        // baseline row = cellH - bottomPad - 1 = 13.
        Assert.Equal(255, sheet[8, 13].A);
        Assert.Equal(0, sheet[8, 15].A);
    }

    [Fact]
    public void Row_for_each_direction_matches_DirectionRows()
    {
        // Put a unique marker color per direction at the foot, then read it back at each row's baseline.
        var byDir = new Dictionary<string, IReadOnlyList<FrameEntry>>();
        foreach (var d in Dirs)
        {
            var img = new Image<Rgba32>(16, 16);
            img[8, 15] = new Rgba32((byte)(DirectionRows.NameToRow[d] * 10 + 5), 0, 0, 255);
            byDir[d] = new List<FrameEntry> { new(0, img) };
        }
        var anim = new CharacterAnimation("Test", "walking", byDir);

        using var sheet = SheetAssembler.Assemble(anim, new AssemblyOptions()).Sheet;

        foreach (var d in Dirs)
        {
            int row = DirectionRows.NameToRow[d];
            Assert.Equal((byte)(row * 10 + 5), sheet[8, row * 16 + 15].R);
        }
    }

    [Fact]
    public void Mid_gap_is_held_and_reported()
    {
        var byDir = new Dictionary<string, IReadOnlyList<FrameEntry>>();
        foreach (var d in Dirs)
            byDir[d] = new List<FrameEntry> { new(0, Dot(16, 16, 8, 15)), new(1, Dot(16, 16, 8, 15)), new(2, Dot(16, 16, 8, 15)) };
        // north-west drops index 1.
        byDir["north-west"] = new List<FrameEntry> { new(0, Dot(16, 16, 8, 15)), new(2, Dot(16, 16, 8, 15)) };
        var anim = new CharacterAnimation("Test", "walking", byDir);

        var result = SheetAssembler.Assemble(anim, new AssemblyOptions());
        using var sheet = result.Sheet;

        Assert.Equal(3, result.FrameCount);
        Assert.Single(result.Warnings);
        Assert.Contains("north-west", result.Warnings[0]);
        Assert.Contains("frame_001", result.Warnings[0]);
    }

    [Fact]
    public void Missing_whole_direction_throws()
    {
        var byDir = new Dictionary<string, IReadOnlyList<FrameEntry>>();
        foreach (var d in Dirs)
            byDir[d] = new List<FrameEntry> { new(0, Dot(16, 16, 8, 15)) };
        byDir.Remove("east");
        var anim = new CharacterAnimation("Test", "walking", byDir);

        var ex = Assert.Throws<AssemblyException>(() => SheetAssembler.Assemble(anim, new AssemblyOptions()));
        Assert.Contains("east", ex.Message);
    }
}
