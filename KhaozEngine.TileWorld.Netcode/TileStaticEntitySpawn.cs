namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Placement for an idle retained entity that participates in interest and entity interaction.</summary>
/// <param name="Facing">Which way the retained body faces.</param>
/// <param name="FootprintSize">The edge of its square footprint in tiles.</param>
public readonly record struct TileStaticEntitySpawn(TileDirection Facing, int FootprintSize = 1);
