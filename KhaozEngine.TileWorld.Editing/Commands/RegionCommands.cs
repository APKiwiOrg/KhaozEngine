using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Editing;

// Both region commands report the region's FULL tile rect on EVERY plane. The derived collision map keeps its
// storage per region, and the rebake over these rects is what gives a new region storage and takes it away
// again on the undo. Without them the map keeps stale storage for a region the document no longer has, which
// reads walkable rather than blocked, the wrong direction to be wrong in. The plane count is a property of the
// document, so the rects can only be built once a command has one, which is on its first apply.
static class TileRegionEdit
{
    internal static void AddPlaneRects(List<TileDirtyRect> dirty, TileWorldDocument doc, RegionCoord coord)
    {
        for (int p = 0; p < doc.PlaneCount; p++) dirty.Add(new TileDirtyRect(coord.Rect, p));
    }
}

/// <summary>Materialises an empty region. A fresh region is void ground, so it still reads blocked until
/// something paints it, but the collision map has to hold the region at all before any paint of it can make a
/// single tile walkable.</summary>
public sealed class CreateRegionCommand : TileCommandBase
{
    readonly RegionCoord _coord;

    /// <summary>Creates the command for the region at <paramref name="coord"/>.</summary>
    public CreateRegionCommand(RegionCoord coord)
        : base("Create region") => _coord = coord;

    /// <summary>The region this command creates.</summary>
    public RegionCoord Coord => _coord;

    /// <summary>False until the first <see cref="Apply"/>, then whether that apply actually created the region.
    /// An apply over a region that was already there is a no-op, and so is its revert.</summary>
    public bool Created { get; private set; }

    /// <summary>Creates the region, or leaves the existing one exactly as it is.</summary>
    public override void Apply(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        // Asked before the create, because GetOrCreateRegion cannot tell the caller which of the two it did and
        // a revert that guessed would delete a region this command never made.
        bool fresh = doc.GetRegion(_coord) is null;
        doc.GetOrCreateRegion(_coord);
        Created = fresh;
        if (Dirty.Count == 0) TileRegionEdit.AddPlaneRects(Dirty, doc, _coord);
    }

    /// <summary>Permanently drops the region again, but only when this command is the one that created it.</summary>
    public override void Revert(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (Created) doc.DeleteRegion(_coord);
    }
}

/// <summary>Deletes a whole region and puts it back on the undo, layers, objects and markers included.
///
/// The capture is the region OBJECT itself, not a copy of it. <see cref="TileWorldDocument.DeleteRegion"/>
/// detaches the instance without touching a byte of it, so the thing this command holds IS the pre-delete
/// state, with no copy that could fall out of step and no 64x64 array clone per layer per plane. Re-attaching
/// the same instance also means a caller that took a reference before the delete is still holding the live
/// region after the undo, which a fresh clone would silently break.
///
/// The aliasing worry a clone would answer is a redo that re-detaches state the document handed out in the
/// meantime, and it cannot arise through this layer: executing any command clears the redo stack, so there is
/// no window between an undo and its redo in which the command layer can edit the region at all. What it does
/// not defend against is code mutating the detached region directly, outside the command layer, which is
/// outside the contract every command here already assumes.</summary>
public sealed class DeleteRegionCommand : TileCommandBase
{
    readonly RegionCoord _coord;
    TileRegion? _region;

    /// <summary>Creates the delete of the region at <paramref name="coord"/>.</summary>
    public DeleteRegionCommand(RegionCoord coord)
        : base("Delete region") => _coord = coord;

    /// <summary>The region this command deletes.</summary>
    public RegionCoord Coord => _coord;

    /// <summary>Permanently deletes the region, holding on to the detached instance the first time round.</summary>
    public override void Apply(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        TileRegion region = doc.GetRegion(_coord)
            ?? throw new TileWorldException($"region {_coord} does not exist, there is nothing to delete");
        if (_region is null)
        {
            _region = region;
            TileRegionEdit.AddPlaneRects(Dirty, doc, _coord);
        }
        doc.DeleteRegion(_coord);
    }

    /// <summary>Re-attaches the region exactly as it was, re-indexing its objects and marking it for a save.</summary>
    public override void Revert(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (_region is null) return;
        doc.RestoreRegion(_region);
        // The delete took the region out of a world that may since have been saved without it, so the next save
        // has to write it back rather than trust what it wrote last time.
        _region.Dirty = true;
    }
}
