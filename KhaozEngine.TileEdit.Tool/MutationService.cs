using System;
using System.Collections.Generic;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;

namespace KhaozEngine.TileEdit;

/// <summary>Every write to the open world, one method per MCP verb. Each one builds an
/// <see cref="ITileCommand"/> (directly, or through a <see cref="TileEditOps"/> factory that reads the document
/// first) and hands it to <see cref="TileEditSession.Execute"/>, so an edit made over MCP lands on the same undo
/// stack, rebakes the same collision and reports the same dirty rects a GUI edit would. Nothing here mutates the
/// document by hand, which is what makes undo total by construction rather than by discipline.
///
/// <para>This part carries the tile and height verbs plus the history controls. The objects, markers, regions
/// and prefabs live in the other part.</para></summary>
public sealed partial class MutationService(TileEditSession session)
{
    /// <summary>Writes any subset of a plane's authored layers over every tile of the rect. A layer left null is
    /// not touched, so a fill can repaint the ground without disturbing what was built on it.</summary>
    public MutationResult TilesFill(TileRect rect, int plane, ushort? underlay = null, ushort? overlay = null,
        TileOverlayShape? shape = null, int? rotation = null, TileSettings? settings = null) =>
        session.Execute(_ => new SetTilesCommand(rect, plane, underlay, overlay, shape, rotation, settings));

    /// <summary>Resets every authored layer of every tile in the rect to its default: void ground, no overlay, a
    /// full unrotated shape and no settings. Objects and markers are not touched, they are not tile layers.</summary>
    public MutationResult TilesClear(TileRect rect, int plane) =>
        session.Execute(_ => new SetTilesCommand(rect, plane, 0, 0, TileOverlayShape.Full, 0, TileSettings.None));

    /// <summary>Writes the corner-height lattice from rows given NORTH FIRST: row 0 is the highest z of the
    /// rect, each row west to east and <c>cornerRect.Width</c> long, with <c>cornerRect.Height</c> rows. That is
    /// exactly the shape <c>QueryService.HeightGetRect</c> hands back, so reading a patch and writing it
    /// straight back is a no-op.
    ///
    /// <para>The command underneath takes ONE FLAT array in the opposite order (row major with z RISING, so
    /// south first), which is the document's own convention. The reorder happens here, once, rather than being
    /// left as a trap between a read verb and a write verb that look like a matching pair.</para></summary>
    /// <exception cref="ArgumentException">The rows do not match the corner rect.</exception>
    public HeightResult HeightsSet(TileRect cornerRect, int plane, IReadOnlyList<short[]> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int height = cornerRect.IsEmpty ? 0 : cornerRect.Height;
        int width = cornerRect.IsEmpty ? 0 : cornerRect.Width;
        if (rows.Count != height)
            throw new ArgumentException(
                $"the corner rect is {height} rows deep, {rows.Count} rows were given.", nameof(rows));
        var cm = new short[width * height];
        for (int row = 0; row < height; row++)
        {
            short[] values = rows[row] ?? throw new ArgumentException($"row {row} is null.", nameof(rows));
            if (values.Length != width)
                throw new ArgumentException(
                    $"the corner rect is {width} wide, row {row} carries {values.Length} heights.", nameof(rows));
            // Row 0 is the NORTHERNMOST, and the flat array starts at the SOUTHERNMOST, so the rows are laid
            // down back to front.
            values.CopyTo(cm, (height - 1 - row) * width);
        }
        return Heights(e => TileEditOps.SetHeights(e.Document, cornerRect, plane, cm));
    }

    /// <summary>Raises (or lowers, with a negative delta) the corner rect, optionally fading the delta out
    /// toward its edge ring with <paramref name="falloff"/> between 0 and 1.</summary>
    public HeightResult HeightsRaise(TileRect cornerRect, int plane, int deltaCm, float falloff = 0f) =>
        Heights(e => TileEditOps.Raise(e.Document, cornerRect, plane, deltaCm, falloff));

    /// <summary>Levels the corner rect to <paramref name="toCm"/>, or to its own rounded average when that is
    /// null.</summary>
    public HeightResult HeightsFlatten(TileRect cornerRect, int plane, short? toCm = null) =>
        Heights(e => TileEditOps.Flatten(e.Document, cornerRect, plane, toCm));

    /// <summary>Runs an iterated box blur over the corner rect, blending into the terrain around it.</summary>
    public HeightResult HeightsSmooth(TileRect cornerRect, int plane, int iterations = 1) =>
        Heights(e => TileEditOps.Smooth(e.Document, cornerRect, plane, iterations));

    /// <summary>Resamples a binary PGM heightmap onto the corner rect, mapping its greyscale linearly onto
    /// <paramref name="minCm"/>..<paramref name="maxCm"/>. A relative path resolves against the world's own
    /// directory.
    ///
    /// <para>No row flip here, unlike <see cref="HeightsSet"/>: a PGM's row 0 is its north edge and
    /// <c>TileEditOps.ImportHeights</c> already lands it on the rect's highest z.</para></summary>
    public HeightResult HeightsImport(string pgmPath, TileRect cornerRect, int plane, short minCm, short maxCm)
    {
        string resolved = session.ResolvePath(pgmPath);
        return Heights(_ => TileEditOps.ImportHeights(resolved, cornerRect, plane, minCm, maxCm));
    }

    /// <summary>Undoes up to <paramref name="steps"/> edits, reporting how many actually moved.</summary>
    public UndoResult Undo(int steps = 1) => session.Undo(steps);

    /// <summary>Redoes up to <paramref name="steps"/> edits, reporting how many actually moved.</summary>
    public UndoResult Redo(int steps = 1) => session.Redo(steps);

    /// <summary>Ends the current gesture, so the next edit starts its own undo step.</summary>
    public void SealGesture() => session.SealGesture();

    // The height verbs all end in the same command and all want the same two counts off it, so they share one
    // execute-and-read-back rather than each repeating the capture.
    HeightResult Heights(Func<TileEditingDocument, SetCornerHeightsCommand> build)
    {
        (MutationResult result, SetCornerHeightsCommand command) = ExecuteCapturing(build);
        return new HeightResult(result, command.WrittenCount, command.CornerCount);
    }

    // Runs a command through the session and hands the CONSTRUCTED command back alongside the result, for the
    // verbs whose answer is only known once the command has applied (an allocated object id, a written corner
    // count). The builder runs under the session lock, so nothing can move between building and applying.
    (MutationResult Result, TCommand Command) ExecuteCapturing<TCommand>(Func<TileEditingDocument, TCommand> build)
        where TCommand : class, ITileCommand
    {
        TCommand? built = null;
        MutationResult result = session.Execute(e => built = build(e));
        return (result, built!);
    }
}
