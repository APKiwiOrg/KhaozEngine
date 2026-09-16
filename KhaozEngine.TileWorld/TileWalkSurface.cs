namespace KhaozEngine.TileWorld;

/// <summary>
/// One flat, walkable TOP of an object archetype: a bridge deck, a dock, a pier. A horizontal rectangle authored
/// in the MESH's own local metres, the frame its vertices are written in before the instance rotation and the
/// archetype's yaw offset turn it, at <see cref="Height"/> metres above the mesh base.
/// <para>The mesh base is the anchor <see cref="TileObjectPlacement.AnchorPosition"/> places, so a surface rides
/// every edit that moves, turns or re-grounds the object. The rectangle may OVERHANG the footprint, the way a bridge
/// landing reaches onto the bank beyond the tiles the bridge occupies, because a pose standing on the deck is a
/// question about the picture rather than about collision.</para>
/// <para>A null extent is the footprint edge on that side, unrotated: <c>-SizeX * tileSize / 2</c> for
/// <see cref="MinX"/>, <c>+SizeX * tileSize / 2</c> for <see cref="MaxX"/>, and the same on z from
/// <see cref="TileObjectArchetype.SizeZ"/>. Resolved against the document's tile size at query time, because a
/// catalog does not know which world it is loaded against.</para>
/// <para>Queried by <see cref="TileWalkSurfaces"/>. The catalog loader refuses a non-finite height or extent, and a
/// min that is not below its max when both are given. A rectangle that comes out inverted only once a null side
/// resolves covers nothing rather than failing the load, since the tile size that would expose it is not known
/// there.</para>
/// </summary>
public sealed class TileWalkSurface
{
    /// <summary>Metres above the mesh base, which is the anchor. May be negative, for a top sunk below the anchor.</summary>
    public float Height { get; set; }
    /// <summary>West edge in mesh-local metres, null for the footprint's own west edge.</summary>
    public float? MinX { get; set; }
    /// <summary>East edge in mesh-local metres, null for the footprint's own east edge.</summary>
    public float? MaxX { get; set; }
    /// <summary>Low z edge in mesh-local metres, null for the footprint's own edge on that side.</summary>
    public float? MinZ { get; set; }
    /// <summary>High z edge in mesh-local metres, null for the footprint's own edge on that side.</summary>
    public float? MaxZ { get; set; }
}
