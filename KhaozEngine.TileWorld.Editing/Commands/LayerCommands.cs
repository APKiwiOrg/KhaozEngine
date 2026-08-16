using System;

namespace KhaozEngine.TileWorld.Editing;

/// <summary>A rect fill of any subset of a plane's authored tile layers: the layers given a value are written
/// to every tile in the rect, the ones left null are not read, not captured and not touched. The old values of
/// the layers it does write are captured on the FIRST apply only, so a redo replays the same edit instead of
/// capturing the state its own previous apply left behind, which would turn the next undo into a no-op.
/// </summary>
public sealed class SetTilesCommand : TileCommandBase
{
    readonly TileRect _rect;
    readonly int _plane;
    readonly ushort? _underlay;
    readonly ushort? _overlay;
    readonly TileOverlayShape? _shape;
    readonly int? _rotation;
    readonly TileSettings? _settings;

    ushort[]? _oldUnderlay;
    ushort[]? _oldOverlay;
    TileOverlayShape[]? _oldShape;
    byte[]? _oldRotation;
    TileSettings[]? _oldSettings;
    bool _captured;

    /// <summary>Creates a fill of the layers whose argument is non-null over every tile of the rect.</summary>
    public SetTilesCommand(TileRect rect, int plane, ushort? underlay, ushort? overlay, TileOverlayShape? shape,
        int? rotation, TileSettings? settings)
        : base("Set tiles")
    {
        _rect = rect;
        _plane = plane;
        _underlay = underlay;
        _overlay = overlay;
        _shape = shape;
        _rotation = rotation;
        _settings = settings;
        // A degenerate rect touches nothing, so it reports nothing. The document skips an empty rect when it
        // rebakes, but a renderer reading PendingRebuilds should not have to know that, and the plane of a
        // command that covers no tiles at all is not worth failing an execute over.
        if (!rect.IsEmpty) Dirty.Add(new TileDirtyRect(rect, plane));
    }

    /// <summary>Writes the fill, capturing what it overwrites the first time round.</summary>
    public override void Apply(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        // Every region first, before a single write: a fill that runs off the edge of the authored world must
        // leave the world exactly as it found it rather than painting the half that happened to be there.
        RequireRegions(doc);
        if (_rect.IsEmpty) return;
        if (!_captured) Capture(doc);
        for (int z = _rect.Z; z < _rect.Z1; z++)
            for (int x = _rect.X; x < _rect.X1; x++)
            {
                if (_underlay is ushort u) doc.SetUnderlay(x, z, _plane, u);
                if (_overlay is ushort o) doc.SetOverlay(x, z, _plane, o);
                if (_shape is TileOverlayShape s) doc.SetOverlayShape(x, z, _plane, s);
                if (_rotation is int r) doc.SetOverlayRotation(x, z, _plane, r);
                if (_settings is TileSettings f) doc.SetSettings(x, z, _plane, f);
            }
    }

    /// <summary>Restores the captured values of the layers this command wrote. A command that never applied
    /// (its validation threw) captured nothing and reverts to nothing.</summary>
    public override void Revert(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!_captured) return;
        int i = 0;
        for (int z = _rect.Z; z < _rect.Z1; z++)
            for (int x = _rect.X; x < _rect.X1; x++, i++)
            {
                if (_oldUnderlay is not null) doc.SetUnderlay(x, z, _plane, _oldUnderlay[i]);
                if (_oldOverlay is not null) doc.SetOverlay(x, z, _plane, _oldOverlay[i]);
                if (_oldShape is not null) doc.SetOverlayShape(x, z, _plane, _oldShape[i]);
                if (_oldRotation is not null) doc.SetOverlayRotation(x, z, _plane, _oldRotation[i]);
                if (_oldSettings is not null) doc.SetSettings(x, z, _plane, _oldSettings[i]);
            }
    }

    void Capture(TileWorldDocument doc)
    {
        int n = _rect.Width * _rect.Height;
        if (_underlay is not null) _oldUnderlay = new ushort[n];
        if (_overlay is not null) _oldOverlay = new ushort[n];
        if (_shape is not null) _oldShape = new TileOverlayShape[n];
        if (_rotation is not null) _oldRotation = new byte[n];
        if (_settings is not null) _oldSettings = new TileSettings[n];
        int i = 0;
        for (int z = _rect.Z; z < _rect.Z1; z++)
            for (int x = _rect.X; x < _rect.X1; x++, i++)
            {
                if (_oldUnderlay is not null) _oldUnderlay[i] = doc.GetUnderlay(x, z, _plane);
                if (_oldOverlay is not null) _oldOverlay[i] = doc.GetOverlay(x, z, _plane);
                if (_oldShape is not null) _oldShape[i] = doc.GetOverlayShape(x, z, _plane);
                if (_oldRotation is not null) _oldRotation[i] = (byte)doc.GetOverlayRotation(x, z, _plane);
                if (_oldSettings is not null) _oldSettings[i] = doc.GetSettings(x, z, _plane);
            }
        _captured = true;
    }

    // Walks the region coordinates the rect spans rather than its tiles, which is the same check 4096 times
    // cheaper per region, and reports the first gap in reading order (south row first, west to east).
    void RequireRegions(TileWorldDocument doc)
    {
        if (_rect.IsEmpty) return;
        RegionCoord lo = RegionCoord.Of(_rect.X, _rect.Z), hi = RegionCoord.Of(_rect.X1 - 1, _rect.Z1 - 1);
        for (int rz = lo.Rz; rz <= hi.Rz; rz++)
            for (int rx = lo.Rx; rx <= hi.Rx; rx++)
            {
                var c = new RegionCoord(rx, rz);
                if (doc.GetRegion(c) is not null) continue;
                // Always throws, the region is absent by the line above. Routing it through the document keeps
                // the message that tells a never-created region apart from one that is merely unloaded, and the
                // clamp names a tile this fill actually covers rather than the region's own corner.
                doc.RequireRegion(Math.Max(_rect.X, c.OriginX), Math.Max(_rect.Z, c.OriginZ));
            }
    }
}
