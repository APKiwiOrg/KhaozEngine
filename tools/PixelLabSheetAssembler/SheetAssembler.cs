using System;
using System.Collections.Generic;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PixelLabSheetAssembler;

/// <summary>
/// Pure (no file IO) compositor: turns a CharacterAnimation into one grid sheet
/// (8 Direction8 rows x frameCount columns, uniform cells). Each frame is placed feet-on-baseline
/// via its opaque bbox bottom; missing frames are gap-filled by <see cref="GapFiller"/>.
/// </summary>
public static class SheetAssembler
{
    public static AssemblyResult Assemble(CharacterAnimation anim, AssemblyOptions opt)
    {
        // 1. Every direction must be present and non-empty.
        foreach (var name in DirectionRows.NameToRow.Keys)
        {
            if (!anim.FramesByDir.TryGetValue(name, out var list) || list.Count == 0)
                throw new AssemblyException($"animation '{anim.AnimName}' is missing direction '{name}'.");
        }

        // 2. frameCount = highest index + 1 across all directions.
        int frameCount = 0;
        foreach (var list in anim.FramesByDir.Values)
            foreach (var f in list)
                frameCount = Math.Max(frameCount, f.Index + 1);
        if (frameCount == 0)
            throw new AssemblyException("no frames found.");

        // 3. Uniform cell = max frame dimensions across all present frames.
        int cellW = 0, cellH = 0;
        foreach (var list in anim.FramesByDir.Values)
            foreach (var f in list)
            {
                cellW = Math.Max(cellW, f.Image.Width);
                cellH = Math.Max(cellH, f.Image.Height);
            }

        var warnings = new List<string>();
        var sheet = new Image<Rgba32>(cellW * frameCount, cellH * DirectionRows.RowCount); // transparent

        // 4. Composite each direction row.
        foreach (var (name, row) in DirectionRows.NameToRow)
        {
            var byIndex = anim.FramesByDir[name].ToDictionary(f => f.Index, f => f.Image);
            var present = new HashSet<int>(byIndex.Keys);
            int[] sources = GapFiller.Resolve(name, anim.AnimName, present, frameCount, opt.Strict, warnings);

            for (int col = 0; col < frameCount; col++)
            {
                Image<Rgba32> frame = byIndex[sources[col]];
                Blit(sheet, frame, col * cellW, row * cellH, cellW, cellH, opt.BottomPad, opt.AlphaThreshold);
            }
        }

        return new AssemblyResult(sheet, frameCount, opt.SuggestedFps, warnings, cellW, cellH);
    }

    // Places one frame into the cell at (cellX, cellY). Horizontally centers the frame canvas;
    // vertically maps the opaque-bbox bottom to the baseline (cellH - bottomPad - 1). A fully
    // transparent frame is bottom-aligned with no normalization. Source pixels outside the cell
    // are clipped (only ever the transparent padding).
    private static void Blit(
        Image<Rgba32> sheet, Image<Rgba32> frame,
        int cellX, int cellY, int cellW, int cellH, int bottomPad, int alphaThreshold)
    {
        int frameW = frame.Width, frameH = frame.Height;
        int xOff = (cellW - frameW) / 2;
        int yOff;

        var bounds = Bbox.OpaqueBounds(frame, alphaThreshold);
        if (bounds is null)
        {
            yOff = cellH - frameH; // bottom-align the canvas; content is empty anyway
        }
        else
        {
            int baselineRow = cellH - bottomPad - 1;
            yOff = baselineRow - bounds.Value.MaxY;
        }

        for (int y = 0; y < frameH; y++)
        {
            int dy = cellY + yOff + y;
            if (dy < cellY || dy >= cellY + cellH) continue;
            for (int x = 0; x < frameW; x++)
            {
                int dx = cellX + xOff + x;
                if (dx < cellX || dx >= cellX + cellW) continue;
                sheet[dx, dy] = frame[x, y];
            }
        }
    }
}
