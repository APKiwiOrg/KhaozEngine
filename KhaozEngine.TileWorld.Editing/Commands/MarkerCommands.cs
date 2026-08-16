using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.TileWorld.Editing;

// Markers are names on tiles: nothing derived reads them. The collision baker walks settings and objects, the
// renderer walks layers and objects, and neither ever asks a region for its markers. So both commands here
// report NO dirty rects, and the editing document rebakes nothing when one applies. If a later round gives a
// marker a footprint or a collision contribution, that is the moment these two grow rects.
static class TileMarkerEdit
{
    internal static TileMarker Require(TileWorldDocument doc, string name) =>
        doc.FindMarker(name) ?? throw new TileWorldException($"marker '{name}' does not exist");

    // SetMarker validates the plane and then the region before it drops the marker it is replacing, and a
    // command that captures before that pre-flight records state for a write that may never happen. The
    // document's own plane check is private, so the same two checks are repeated here, in the same order and
    // with the same message, to run BEFORE the capture rather than in the middle of it.
    internal static void RequireDestination(TileWorldDocument doc, int x, int z, int plane)
    {
        if ((uint)plane >= (uint)doc.PlaneCount)
            throw new ArgumentOutOfRangeException("plane", plane, $"plane {plane} is outside 0..{doc.PlaneCount - 1}");
        doc.RequireRegion(x, z);
    }
}

/// <summary>Places or re-homes the uniquely named marker, capturing whatever the name held before: another
/// marker's position and tags, or the fact that the name was free. Reports no dirty rects, because a marker
/// contributes neither collision nor geometry.</summary>
public sealed class SetMarkerCommand : TileCommandBase
{
    readonly string _name;
    readonly int _x;
    readonly int _z;
    readonly int _plane;
    readonly List<string>? _tags;
    int _oldX;
    int _oldZ;
    int _oldPlane;
    List<string>? _oldTags;
    bool _existed;
    bool _captured;

    /// <summary>Creates the marker write at (x, z) on the plane, with tags or null for none.</summary>
    public SetMarkerCommand(string name, int x, int z, int plane, IEnumerable<string>? tags = null)
        : base("Set marker")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        _x = x;
        _z = z;
        _plane = plane;
        _tags = tags?.ToList();
    }

    /// <summary>Writes the marker, capturing the state of the name the first time round.</summary>
    public override void Apply(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        // Destination first, before anything is captured: a placement into a missing region or onto a plane the
        // world does not have must leave BOTH the document and this command exactly as it found them, so a
        // later retry still records the true pre-state rather than one this failed attempt already took.
        TileMarkerEdit.RequireDestination(doc, _x, _z, _plane);
        if (!_captured)
        {
            if (doc.FindMarker(_name) is TileMarker old)
            {
                (_oldX, _oldZ, _oldPlane) = (old.X, old.Z, old.Plane);
                _oldTags = old.Tags?.ToList();
                _existed = true;
            }
            _captured = true;
        }
        doc.SetMarker(_name, _x, _z, _plane, _tags);
    }

    /// <summary>Puts the name back the way it was: the old marker where it stood, or nothing at all.</summary>
    public override void Revert(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!_captured) return;
        if (_existed) doc.SetMarker(_name, _oldX, _oldZ, _oldPlane, _oldTags);
        else doc.RemoveMarker(_name);
    }
}

/// <summary>Deletes the named marker, capturing its position, plane and tags so the revert can put it back
/// exactly. Throws when the name is not in the document, because a delete of nothing is a mistake worth
/// telling the caller about rather than a silent no-op in the undo stack. Reports no dirty rects.</summary>
public sealed class RemoveMarkerCommand : TileCommandBase
{
    readonly string _name;
    int _x;
    int _z;
    int _plane;
    List<string>? _tags;
    bool _captured;

    /// <summary>Creates the delete of the named marker.</summary>
    public RemoveMarkerCommand(string name)
        : base("Remove marker")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    /// <summary>Removes the marker, capturing it the first time round.</summary>
    public override void Apply(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        TileMarker m = TileMarkerEdit.Require(doc, _name);
        if (!_captured)
        {
            (_x, _z, _plane) = (m.X, m.Z, m.Plane);
            _tags = m.Tags?.ToList();
            _captured = true;
        }
        doc.RemoveMarker(_name);
    }

    /// <summary>Puts the marker back where it was, tags and all.</summary>
    public override void Revert(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_captured) doc.SetMarker(_name, _x, _z, _plane, _tags);
    }
}
