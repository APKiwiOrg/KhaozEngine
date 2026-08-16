using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.TileWorld.Editing;

/// <summary>Runs an arbitrary mutation over a world rect and makes it undoable by capturing the pre-image
/// first: every authored layer and every corner height inside the rect on each listed plane, plus every object
/// and marker ANCHORED inside it there. This is the general answer for an edit whose reach is a rect but whose
/// shape is not one command's worth of writes, and it is what a prefab stamp goes through.
///
/// The capture happens once, on the first apply, so a redo replays the same mutation rather than capturing the
/// state its own previous apply left behind, which would turn the next undo into a no-op.
///
/// Two limits worth knowing, stated rather than solved. An object whose ANCHOR is outside the rect but whose
/// footprint reaches into it is NOT captured, so a mutation that moves or deletes one is not undone by this
/// command. The same goes for an object or marker the mutation moves INTO the rect from outside: the revert
/// sweeps the rect clean before re-adding what it captured, so that one is removed and not put back. Both are
/// the same rule, that the snapshot owns what was inside the rect when it looked and nothing else.
///
/// The revert also assumes the mutation does not DELETE a region inside the rect. If it does, re-adding a
/// captured object there throws rather than dropping it quietly, because a snapshot that cannot restore should
/// say so instead of losing content.</summary>
public sealed class SnapshotRectCommand : TileCommandBase
{
    readonly TileRect _rect;
    readonly int[] _planes;
    readonly Action<TileWorldDocument> _mutate;
    PlaneCapture[]? _captured;

    /// <summary>Creates the snapshot of <paramref name="rect"/> on <paramref name="planes"/> around
    /// <paramref name="mutate"/>, which runs on every apply.</summary>
    public SnapshotRectCommand(string label, TileRect rect, IReadOnlyList<int> planes, Action<TileWorldDocument> mutate)
        : base(label)
    {
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(mutate);
        if (rect.IsEmpty) throw new ArgumentException("a snapshot of an empty rect captures nothing", nameof(rect));
        if (planes.Count == 0) throw new ArgumentException("a snapshot needs at least one plane", nameof(planes));
        _rect = rect;
        _planes = planes.ToArray();
        _mutate = mutate;
        foreach (int plane in _planes) Dirty.Add(new TileDirtyRect(rect, plane));
    }

    /// <summary>The rect this snapshot owns.</summary>
    public TileRect Rect => _rect;

    /// <summary>The planes it owns that rect on, in the order the revert walks them.</summary>
    public IReadOnlyList<int> Planes => _planes;

    /// <summary>Captures the pre-image the first time round, then runs the mutation.</summary>
    public override void Apply(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        _captured ??= Capture(doc);
        _mutate(doc);
    }

    /// <summary>Puts the captured rect back: layers and corners first, then the layers that were not
    /// materialised at all, then the objects and markers.</summary>
    public override void Revert(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_captured is null) return;
        foreach (PlaneCapture capture in _captured) Restore(doc, capture);
    }

    PlaneCapture[] Capture(TileWorldDocument doc)
    {
        var captures = new PlaneCapture[_planes.Length];
        for (int i = 0; i < _planes.Length; i++) captures[i] = CapturePlane(doc, _planes[i]);
        return captures;
    }

    PlaneCapture CapturePlane(TileWorldDocument doc, int plane)
    {
        int tiles = _rect.Width * _rect.Height;
        int corners = (_rect.Width + 1) * (_rect.Height + 1);
        var c = new PlaneCapture(plane, tiles, corners);
        int t = 0;
        for (int z = _rect.Z; z < _rect.Z1; z++)
            for (int x = _rect.X; x < _rect.X1; x++, t++)
            {
                // A tile with no region reads defaults and cannot be written, so it is recorded as absent
                // rather than captured as zeroes that a later restore would push into a region the mutation
                // created underneath it.
                if (doc.RegionAt(x, z) is null) continue;
                c.TileWritable[t] = true;
                c.Underlay[t] = doc.GetUnderlay(x, z, plane);
                c.Overlay[t] = doc.GetOverlay(x, z, plane);
                c.Shape[t] = (byte)doc.GetOverlayShape(x, z, plane);
                c.Rotation[t] = (byte)doc.GetOverlayRotation(x, z, plane);
                c.Settings[t] = (byte)doc.GetSettings(x, z, plane);
            }
        // Corners run one past the tiles on both axes: the rect's tiles are bounded by corners X..X1 and
        // Z..Z1 inclusive, and the far row and column belong to the rect's own tiles as much as the near ones.
        int k = 0;
        for (int z = _rect.Z; z <= _rect.Z1; z++)
            for (int x = _rect.X; x <= _rect.X1; x++, k++)
            {
                if (doc.RegionAt(x, z) is null) continue;
                c.CornerWritable[k] = true;
                c.Corners[k] = doc.CornerHeightCm(x, z, plane);
            }
        foreach (TileRegion region in doc.RegionsTouching(_rect))
            c.Layers.Add(NullLayers.Of(region, plane));
        foreach (TileObject o in doc.ObjectsIn(_rect, plane))
            c.Objects.Add(new ObjectState(o.Id, o.ArchetypeId, o.X, o.Z, o.Plane, o.Rotation, o.Tags?.ToList()));
        foreach (TileMarker m in doc.AllMarkers())
            if (m.Plane == plane && _rect.Contains(m.X, m.Z))
                c.Markers.Add(new MarkerState(m.Name, m.X, m.Z, m.Plane, m.Tags?.ToList()));
        return c;
    }

    void Restore(TileWorldDocument doc, PlaneCapture c)
    {
        int plane = c.Plane;
        int t = 0;
        for (int z = _rect.Z; z < _rect.Z1; z++)
            for (int x = _rect.X; x < _rect.X1; x++, t++)
            {
                if (!c.TileWritable[t] || doc.RegionAt(x, z) is null) continue;
                doc.SetUnderlay(x, z, plane, c.Underlay[t]);
                doc.SetOverlay(x, z, plane, c.Overlay[t]);
                doc.SetOverlayShape(x, z, plane, (TileOverlayShape)c.Shape[t]);
                doc.SetOverlayRotation(x, z, plane, c.Rotation[t]);
                doc.SetSettings(x, z, plane, (TileSettings)c.Settings[t]);
            }
        int k = 0;
        for (int z = _rect.Z; z <= _rect.Z1; z++)
            for (int x = _rect.X; x <= _rect.X1; x++, k++)
                if (c.CornerWritable[k]) doc.TrySetCornerHeightCm(x, z, plane, c.Corners[k]);
        // A layer that was null before the mutation is put back to null AFTER the writes above, which means the
        // writes materialised an array this line then throws away. That is deliberate and cheap next to being
        // wrong: null is the complete prior state of that layer, so restoring it is exact whatever the mutation
        // did, and for the height layer of a plane above 0 it is the ONLY way back. Null there means "derive
        // from plane 0 plus the plane lift", which is a different thing on disk from an authored copy of the
        // same numbers, and no per-corner write can undo the difference.
        foreach (NullLayers layers in c.Layers)
            if (doc.GetRegion(layers.Coord) is TileRegion region) layers.Reapply(region, plane);
        RestoreObjects(doc, c);
        RestoreMarkers(doc, c);
    }

    void RestoreObjects(TileWorldDocument doc, PlaneCapture c)
    {
        // ToList first: removing an object edits the very region list ObjectsIn walks.
        foreach (TileObject o in doc.ObjectsIn(_rect, c.Plane).ToList()) doc.RemoveObject(o.Id);
        foreach (ObjectState s in c.Objects)
        {
            // A captured object the mutation MOVED out of the rect survived the sweep above and still holds
            // this id, so it is dropped before the re-add rather than colliding with it.
            doc.RemoveObject(s.Id);
            doc.AddObjectWithId(s.Id, s.ArchetypeId, s.X, s.Z, s.Plane, s.Rotation, s.Tags);
        }
    }

    void RestoreMarkers(TileWorldDocument doc, PlaneCapture c)
    {
        foreach (TileMarker m in doc.AllMarkers().Where(m => m.Plane == c.Plane && _rect.Contains(m.X, m.Z)).ToList())
            doc.RemoveMarker(m.Name);
        // SetMarker is document-unique by name, so a captured marker the mutation moved elsewhere is re-homed
        // rather than duplicated.
        foreach (MarkerState s in c.Markers) doc.SetMarker(s.Name, s.X, s.Z, s.Plane, s.Tags);
    }

    // One plane's pre-image over the rect: the five authored tile layers, the corner heights, which of the six
    // layers each touched region had not materialised, and the objects and markers anchored inside.
    sealed class PlaneCapture
    {
        public PlaneCapture(int plane, int tiles, int corners)
        {
            Plane = plane;
            TileWritable = new bool[tiles];
            Underlay = new ushort[tiles];
            Overlay = new ushort[tiles];
            Shape = new byte[tiles];
            Rotation = new byte[tiles];
            Settings = new byte[tiles];
            CornerWritable = new bool[corners];
            Corners = new short[corners];
        }

        public int Plane { get; }
        public bool[] TileWritable { get; }
        public ushort[] Underlay { get; }
        public ushort[] Overlay { get; }
        public byte[] Shape { get; }
        public byte[] Rotation { get; }
        public byte[] Settings { get; }
        public bool[] CornerWritable { get; }
        public short[] Corners { get; }
        public List<NullLayers> Layers { get; } = new();
        public List<ObjectState> Objects { get; } = new();
        public List<MarkerState> Markers { get; } = new();
    }

    // Which of one region-plane's six layers were unallocated when the snapshot looked.
    readonly record struct NullLayers(RegionCoord Coord, bool Heights, bool Underlay, bool Overlay, bool Shape,
        bool Rotation, bool Settings)
    {
        public static NullLayers Of(TileRegion region, int plane)
        {
            TilePlaneData p = region.Plane(plane);
            return new NullLayers(region.Coord, p.Heights is null, p.Underlay is null, p.Overlay is null,
                p.OverlayShape is null, p.OverlayRotation is null, p.Settings is null);
        }

        public void Reapply(TileRegion region, int plane)
        {
            TilePlaneData p = region.Plane(plane);
            if (Heights) p.Heights = null;
            if (Underlay) p.Underlay = null;
            if (Overlay) p.Overlay = null;
            if (Shape) p.OverlayShape = null;
            if (Rotation) p.OverlayRotation = null;
            if (Settings) p.Settings = null;
            region.Dirty = true;
        }
    }

    readonly record struct ObjectState(long Id, string ArchetypeId, int X, int Z, int Plane, int Rotation,
        List<string>? Tags);

    readonly record struct MarkerState(string Name, int X, int Z, int Plane, List<string>? Tags);
}
