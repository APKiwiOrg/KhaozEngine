using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.TileWorld.Editing;

/// <summary>Places one object from a catalog archetype. The id is allocated by the FIRST apply and kept, so a
/// redo re-adds the same object rather than a new one wearing a new id: anything that refers to an object by
/// id (a region file on disk, a quest hook, a later command in the same history) survives an undo and a redo
/// unchanged. The document's allocator is deliberately not rewound by the revert, so an id this command
/// released is never handed to something else while the redo still expects it back.</summary>
public sealed class PlaceObjectCommand : TileCommandBase
{
    readonly string _archetypeId;
    readonly int _x;
    readonly int _z;
    readonly int _plane;
    readonly int _rotation;
    readonly List<string>? _tags;

    /// <summary>Creates the placement, refusing an archetype the catalogs do not define (the footprint this
    /// command reports as dirty comes from that archetype, so there is nothing to report without it).</summary>
    public PlaceObjectCommand(TileWorldCatalogs catalogs, string archetypeId, int x, int z, int plane, int rotation,
        IEnumerable<string>? tags = null)
        : base("Place object")
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentException.ThrowIfNullOrWhiteSpace(archetypeId);
        TileObjectArchetype a = catalogs.Archetype(archetypeId)
            ?? throw new TileWorldException($"archetype '{archetypeId}' is not in the catalogs");
        _archetypeId = archetypeId;
        _x = x;
        _z = z;
        _plane = plane;
        _rotation = rotation & 3;
        // Copied, so a caller reusing its tag list for the next placement cannot rewrite what this command
        // replays on redo.
        _tags = tags?.ToList();
        Dirty.Add(new TileDirtyRect(TileFootprint.Of(a, x, z, _rotation), plane));
    }

    /// <summary>The id the placed object holds, null until the first <see cref="Apply"/> allocates it. The tool
    /// hands this back to its caller, which is the only way an AI client learns what it just placed.</summary>
    public long? ObjectId { get; private set; }

    /// <summary>Adds the object, taking a fresh id the first time and re-using it on every later apply.</summary>
    public override void Apply(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (ObjectId is long id) doc.AddObjectWithId(id, _archetypeId, _x, _z, _plane, _rotation, _tags);
        else ObjectId = doc.AddObject(_archetypeId, _x, _z, _plane, _rotation, _tags).Id;
    }

    /// <summary>Removes the placed object. A command whose first apply threw placed nothing and removes
    /// nothing.</summary>
    public override void Revert(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (ObjectId is long id) doc.RemoveObject(id);
    }
}

/// <summary>Moves one object's anchor, coalescing with a later move of the SAME object so a drag lands as one
/// undo step. The dirty rects cover the footprint the object left AND the one it arrived on, and a merge takes
/// the newer command's rects over too, because the surviving command is the only one left to revert.</summary>
public sealed class MoveObjectCommand : TileCommandBase
{
    readonly TileWorldCatalogs _catalogs;
    readonly long _id;
    int _toX;
    int _toZ;
    int _toPlane;
    int _fromX;
    int _fromZ;
    int _fromPlane;
    bool _captured;

    /// <summary>Creates the move of object <paramref name="id"/> to (x, z) on the given plane.</summary>
    public MoveObjectCommand(TileWorldCatalogs catalogs, long id, int x, int z, int plane)
        : base("Move object")
    {
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _id = id;
        _toX = x;
        _toZ = z;
        _toPlane = plane;
    }

    /// <summary>The object this command moves, the key a merge matches on.</summary>
    public long ObjectId => _id;

    /// <summary>Moves the object, capturing where it came from the first time round.</summary>
    public override void Apply(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        // Every check before anything is captured: a move that cannot land must leave the object where it was
        // AND leave this command uncaptured, so a later retry still records the true origin. MoveObject
        // validates the plane, the id and the destination region before it writes a field, so reading the
        // origin off the object and only then calling it gives that ordering for all three, and the plane
        // range keeps the document's own message instead of a second copy of it here.
        TileObject o = TileObjectEdit.Require(doc, _id);
        (int fromX, int fromZ, int fromPlane, int rotation) = (o.X, o.Z, o.Plane, o.Rotation);
        doc.MoveObject(_id, _toX, _toZ, _toPlane);
        if (!_captured)
        {
            (_fromX, _fromZ, _fromPlane) = (fromX, fromZ, fromPlane);
            AddFootprint(doc, fromX, fromZ, fromPlane, rotation);
            AddFootprint(doc, _toX, _toZ, _toPlane, rotation);
            _captured = true;
        }
    }

    /// <summary>Puts the object back on the tile it started this gesture on.</summary>
    public override void Revert(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_captured) doc.MoveObject(_id, _fromX, _fromZ, _fromPlane);
    }

    /// <summary>Swallows a later move of the same object: this command keeps its own ORIGIN (the tile the whole
    /// gesture started on) and takes the newer target, so one undo returns the object all the way home.</summary>
    public override bool TryMerge(ITileCommand next)
    {
        if (next is not MoveObjectCommand m || m._id != _id) return false;
        (_toX, _toZ, _toPlane) = (m._toX, m._toZ, m._toPlane);
        AbsorbDirty(m);
        return true;
    }

    void AddFootprint(TileWorldDocument doc, int x, int z, int plane, int rotation)
    {
        TileRect rect = TileObjectEdit.Footprint(_catalogs, doc, x, z, rotation, _id);
        Dirty.Add(new TileDirtyRect(rect, plane));
    }
}

/// <summary>Turns one object in place. A footprint is not always square, so the two rotations can cover
/// different tiles and the command reports BOTH: turning a 2x3 hall to 3x2 frees a tile to the north and
/// claims one to the east, and a rebake that only saw one of the two shapes would strand the other.</summary>
public sealed class RotateObjectCommand : TileCommandBase
{
    readonly TileWorldCatalogs _catalogs;
    readonly long _id;
    readonly int _rotation;
    int _oldRotation;
    bool _captured;

    /// <summary>Creates the turn to <paramref name="rotation"/> quarter turns clockwise, masked into 0..3.</summary>
    public RotateObjectCommand(TileWorldCatalogs catalogs, long id, int rotation)
        : base("Rotate object")
    {
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _id = id;
        _rotation = rotation & 3;
    }

    /// <summary>Writes the new rotation, capturing the old one the first time round.</summary>
    public override void Apply(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        TileObject o = TileObjectEdit.Require(doc, _id);
        if (!_captured)
        {
            _oldRotation = o.Rotation;
            Dirty.Add(new TileDirtyRect(TileObjectEdit.Footprint(_catalogs, doc, o.X, o.Z, _oldRotation, _id), o.Plane));
            Dirty.Add(new TileDirtyRect(TileObjectEdit.Footprint(_catalogs, doc, o.X, o.Z, _rotation, _id), o.Plane));
            _captured = true;
        }
        TileObjectEdit.SetRotation(doc, o, _rotation);
    }

    /// <summary>Turns the object back to the rotation it had before this command.</summary>
    public override void Revert(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_captured) TileObjectEdit.SetRotation(doc, TileObjectEdit.Require(doc, _id), _oldRotation);
    }
}

/// <summary>Deletes one object, capturing everything needed to put it back: id, archetype, anchor, plane,
/// rotation and tags. The dirty rect is the object's FULL footprint measured before the delete, because a
/// rebake re-derives collision from what is in the document and a deleted object is no longer there to
/// measure.</summary>
public sealed class RemoveObjectCommand : TileCommandBase
{
    readonly TileWorldCatalogs _catalogs;
    readonly long _id;
    string _archetypeId = "";
    int _x;
    int _z;
    int _plane;
    int _rotation;
    List<string>? _tags;
    bool _captured;

    /// <summary>Creates the delete of object <paramref name="id"/>.</summary>
    public RemoveObjectCommand(TileWorldCatalogs catalogs, long id)
        : base("Remove object")
    {
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        _id = id;
    }

    /// <summary>Removes the object, capturing it whole the first time round.</summary>
    public override void Apply(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        TileObject o = TileObjectEdit.Require(doc, _id);
        if (!_captured)
        {
            (_archetypeId, _x, _z, _plane, _rotation) = (o.ArchetypeId, o.X, o.Z, o.Plane, o.Rotation);
            _tags = o.Tags?.ToList();
            Dirty.Add(new TileDirtyRect(TileObjectEdit.Footprint(_catalogs, doc, o.X, o.Z, o.Rotation, _id), o.Plane));
            _captured = true;
        }
        doc.RemoveObject(_id);
    }

    /// <summary>Puts the object back with the id it had, so every reference to it still resolves.</summary>
    public override void Revert(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_captured) doc.AddObjectWithId(_id, _archetypeId, _x, _z, _plane, _rotation, _tags);
    }
}

/// <summary>Replaces one object's authoring tags. Reports NO dirty rects: tags are metadata for the authoring
/// tools and the game's own content code, and nothing derived reads them (the collision baker takes the
/// archetype, the anchor, the plane and the rotation, and the renderer takes the mesh), so this command moves
/// no tile.</summary>
public sealed class SetObjectTagsCommand : TileCommandBase
{
    readonly long _id;
    readonly List<string>? _tags;
    List<string>? _oldTags;
    bool _captured;

    /// <summary>Creates the tag write, with null meaning "no tags at all" rather than an empty list.</summary>
    public SetObjectTagsCommand(long id, IEnumerable<string>? tags)
        : base("Set object tags")
    {
        _id = id;
        _tags = tags?.ToList();
    }

    /// <summary>Writes the tags, capturing the old list the first time round.</summary>
    public override void Apply(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        TileObject o = TileObjectEdit.Require(doc, _id);
        if (!_captured)
        {
            _oldTags = o.Tags?.ToList();
            _captured = true;
        }
        o.Tags = _tags?.ToList();
        TileObjectEdit.MarkRegionDirty(doc, o);
    }

    /// <summary>Restores the tag list the object carried before this command.</summary>
    public override void Revert(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!_captured) return;
        TileObject o = TileObjectEdit.Require(doc, _id);
        o.Tags = _oldTags?.ToList();
        TileObjectEdit.MarkRegionDirty(doc, o);
    }
}

// The three things every object command needs and the document does not offer directly: the lookup that turns a
// missing id into the same message MoveObject gives, the footprint of an instance, and the region dirty flag
// for the fields (rotation, tags) that are written through the object rather than through a document method.
static class TileObjectEdit
{
    internal static TileObject Require(TileWorldDocument doc, long id) =>
        doc.FindObject(id) ?? throw new TileWorldException($"object {id} does not exist");

    // An archetype the catalogs do not define is a content error the validator reports, never a reason to fault
    // an edit, so a dangling one falls back to the single tile the object is anchored on. The fallback is not
    // about matching what the baker writes for such an object, which is nothing at all: it is that a rect has
    // to cover at least the anchor tile, or the rebake never visits the place the edit happened and whatever
    // the previous content left in the collision map there survives the edit.
    internal static TileRect Footprint(TileWorldCatalogs catalogs, TileWorldDocument doc, int x, int z, int rotation, long id)
    {
        TileObject? o = doc.FindObject(id);
        TileObjectArchetype? a = o is null ? null : catalogs.Archetype(o.ArchetypeId);
        return a is null ? new TileRect(x, z, 1, 1) : TileFootprint.Of(a, x, z, rotation);
    }

    internal static void SetRotation(TileWorldDocument doc, TileObject o, int rotation)
    {
        o.Rotation = rotation & 3;
        MarkRegionDirty(doc, o);
    }

    internal static void MarkRegionDirty(TileWorldDocument doc, TileObject o)
    {
        if (doc.RegionAt(o.X, o.Z) is TileRegion r) r.Dirty = true;
    }
}
