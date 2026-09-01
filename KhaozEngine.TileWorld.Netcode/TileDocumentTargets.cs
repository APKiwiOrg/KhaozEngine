using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The document-backed <see cref="ITileTargets"/>: a target id is a <see cref="TileObject.Id"/>, and only an object
/// whose archetype carries <see cref="TileObjectArchetype.Interactive"/> is a target at all. The first consumer of
/// that authored flag, which is what keeps "clickable" a content decision rather than a list of archetype ids
/// compiled into server code.
/// <para>The document is READ THROUGH on every call rather than indexed once. An object can be moved, rotated or
/// deleted between the click and the arrival it was aimed at, and a cached footprint would hand the reach rules a
/// rect the world no longer has. The seam's contract is that an id stops resolving the moment the thing it named
/// stops existing, and only a live read can honour it.</para>
/// <para>Both heads build one of these over the same world files and the same catalogs, so both answer a target
/// identically. That is what lets a client pre-check a click before it sends one, and lets a predicted walk toward
/// a target replay against the server's answer instead of snapping on arrival.</para>
/// <para>The seam answers BOTH directions: <see cref="TryGetFootprint"/> resolves an id to a footprint, which is
/// what the reach rules run on, and <see cref="TryGetTargetAt"/> resolves a tile to the id covering it, which is
/// what a click produces. A client without the second one writes the search itself on every click.</para>
/// <para>An unknown id, an object whose archetype the catalogs do not define, and a non-interactive object all
/// answer false rather than throwing. A stale or hostile click is ordinary traffic on a server tick, and the
/// caller already has to handle the miss.</para>
/// </summary>
public sealed class TileDocumentTargets : ITileTargets
{
    readonly TileWorldDocument document;
    readonly TileWorldCatalogs catalogs;

    /// <summary>Reads targets out of a loaded document plus the catalogs it references by id. Both are HELD rather
    /// than copied, for the same reason <see cref="TileMoveSimulator.Map"/> is: an edit rebaked into the world is
    /// meant to be visible to the next click.</summary>
    /// <param name="document">The world the target ids belong to.</param>
    /// <param name="catalogs">The archetypes that world's objects reference, which carry the footprint size and the
    /// interactive flag.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="catalogs"/> is
    /// null.</exception>
    public TileDocumentTargets(TileWorldDocument document, TileWorldCatalogs catalogs)
    {
        this.document = document ?? throw new ArgumentNullException(nameof(document));
        this.catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
    }

    /// <inheritdoc/>
    /// <remarks>The rect handed back is the ROTATED footprint of the instance, from <see cref="TileFootprint.Of"/>,
    /// so a target turned a quarter turn in the editor carries its reach tiles round with it instead of keeping the
    /// archetype's unrotated shape. The plane is the object's own, which is what the caller compares against the
    /// player's before it walks anywhere.</remarks>
    public bool TryGetFootprint(long target, out TileRect footprint, out int plane)
    {
        footprint = default;
        plane = 0;
        TileObject? o = document.FindObject(target);
        if (o is null) return false;
        TileObjectArchetype? a = catalogs.Archetype(o.ArchetypeId);
        if (a is null || !a.Interactive) return false;
        footprint = TileFootprint.Of(a, o.X, o.Z, o.Rotation);
        plane = o.Plane;
        return true;
    }

    /// <summary>
    /// The INVERSE: which interactive object, if any, covers this tile. What a click-to-interact client asks on
    /// every click, because a ray answers with a GROUND tile
    /// (<see cref="TileRaycast.Pick(TileWorldDocument, int, System.Numerics.Vector3, System.Numerics.Vector3, float)"/>)
    /// and the target it was aimed at is whatever is standing on that tile. Compose the two and a click is resolved
    /// in two lines instead of a per-click scan written again in every tile game.
    /// <para>The whole FOOTPRINT counts, not the anchor tile, so the far half of a rotated two-tile booth answers
    /// the same id as its near half. A search keyed on the anchor is the shape that looks right and misses every
    /// object bigger than one tile.</para>
    /// <para>TWO targets over one tile is authored content rather than an error, so the LOWEST id wins. That is a
    /// rule rather than a preference: both heads run this search over the same document and a click that resolved
    /// differently on each would send the player walking to one thing and the server resolving another.</para>
    /// <para>READ THROUGH, exactly as <see cref="TryGetFootprint"/> is, and for the same reason. The search window
    /// is the anchors that COULD cover this tile, which is the largest footprint in the catalogs square, so an
    /// object anchored in a neighbouring region whose footprint crosses the boundary is found. Only LOADED regions
    /// are searched: a click lands on a tile the clicker can see, and a region nobody has streamed carries nothing
    /// anybody clicked.</para>
    /// </summary>
    /// <param name="tile">The tile the click landed on, plane included.</param>
    /// <param name="target">The covering object's id, 0 when nothing interactive covers the tile.</param>
    /// <returns>False when the tile is empty, carries only non-interactive objects, or lies on another plane.</returns>
    public bool TryGetTargetAt(TileCoord tile, out long target)
    {
        target = 0L;
        // The largest unrotated footprint side in the catalogs, which bounds how far away an anchor can be and
        // still cover this tile. Rotation only ever swaps the two sides, so the square built from the larger of
        // them covers both orientations. Recomputed per call rather than cached, because the catalogs are HELD and
        // a game that edits an archetype expects the next click to see it, which is the same contract the document
        // read above honours.
        int span = 1;
        foreach (TileObjectArchetype a in catalogs.Archetypes.Values)
        {
            if (a.SizeX > span) span = a.SizeX;
            if (a.SizeZ > span) span = a.SizeZ;
        }
        // Anchors are the SW tile, so an object covering this tile is anchored at or below it on both axes.
        var window = new TileRect(tile.X - span + 1, tile.Z - span + 1, span, span);
        foreach (TileObject o in document.ObjectsIn(window, tile.Plane))
        {
            TileObjectArchetype? a = catalogs.Archetype(o.ArchetypeId);
            if (a is null || !a.Interactive) continue;
            if (!TileFootprint.Of(a, o.X, o.Z, o.Rotation).Contains(tile.X, tile.Z)) continue;
            if (target == 0L || o.Id < target) target = o.Id;
        }
        return target != 0L;
    }
}
