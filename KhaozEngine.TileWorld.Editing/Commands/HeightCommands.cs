using System;

namespace KhaozEngine.TileWorld.Editing;

/// <summary>Writes a rect of the corner-height lattice. The rect is over CORNERS, not tiles: a corner belongs
/// to the four tiles around it, so the dirty rect this reports is one tile wider and one tile taller than the
/// corner rect, covering the tiles on both sides of every corner it moves. Corners whose region does not exist
/// are skipped rather than thrown on (the lattice edge-extends into unauthored space, so a brush that overlaps
/// the edge of the world is a normal thing to do), and the revert restores only the ones that were written.
/// </summary>
public sealed class SetCornerHeightsCommand : TileCommandBase
{
    readonly TileRect _cornerRect;
    readonly int _plane;
    readonly short[] _newCm;

    short[]? _oldCm;
    bool[]? _written;
    bool _captured;

    /// <summary>Creates the write, with one new height per corner of the rect in row-major order (z outer).</summary>
    public SetCornerHeightsCommand(TileRect cornerRect, int plane, short[] newCm)
        : base("Set heights")
    {
        ArgumentNullException.ThrowIfNull(newCm);
        int expected = cornerRect.IsEmpty ? 0 : cornerRect.Width * cornerRect.Height;
        if (newCm.Length != expected)
            throw new ArgumentException(
                $"the corner rect covers {expected} corners, {newCm.Length} heights were given.", nameof(newCm));
        _cornerRect = cornerRect;
        _plane = plane;
        // Copied, so a caller reusing its scratch buffer for the next brush stroke cannot rewrite what this
        // command will replay on redo.
        _newCm = (short[])newCm.Clone();
        if (!cornerRect.IsEmpty)
            Dirty.Add(new TileDirtyRect(
                TileRect.FromCorners(cornerRect.X - 1, cornerRect.Z - 1, cornerRect.X1 - 1, cornerRect.Z1 - 1),
                plane));
    }

    /// <summary>Writes the new heights, capturing the old ones the first time round.</summary>
    public override void Apply(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_cornerRect.IsEmpty) return;
        if (!_captured) Capture(doc);
        int i = 0;
        for (int z = _cornerRect.Z; z < _cornerRect.Z1; z++)
            for (int x = _cornerRect.X; x < _cornerRect.X1; x++, i++)
                // The write result IS the record of what to restore, so it is refreshed on every apply. A redo
                // after a region was deleted under this command writes fewer corners than the first apply did,
                // and the revert has to follow that rather than write into a region that is no longer there.
                _written![i] = doc.TrySetCornerHeightCm(x, z, _plane, _newCm[i]);
    }

    /// <summary>Restores the corners this command actually wrote, leaving the skipped ones alone.</summary>
    public override void Revert(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!_captured) return;
        int i = 0;
        for (int z = _cornerRect.Z; z < _cornerRect.Z1; z++)
            for (int x = _cornerRect.X; x < _cornerRect.X1; x++, i++)
                if (_written![i]) doc.TrySetCornerHeightCm(x, z, _plane, _oldCm![i]);
    }

    // The pre-edit lattice, read once on the first apply. Walked in the same row-major order the value arrays
    // are indexed in, so every pass over the rect agrees on which array slot belongs to which corner.
    void Capture(TileWorldDocument doc)
    {
        _oldCm = new short[_newCm.Length];
        _written = new bool[_newCm.Length];
        int i = 0;
        for (int z = _cornerRect.Z; z < _cornerRect.Z1; z++)
            for (int x = _cornerRect.X; x < _cornerRect.X1; x++, i++)
                _oldCm[i] = doc.CornerHeightCm(x, z, _plane);
        _captured = true;
    }
}
