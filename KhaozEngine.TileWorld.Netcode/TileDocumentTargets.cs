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
}
