namespace KhaozEngine.TileWorld.Netcode;

/// <summary>Why <see cref="TileWorldServer.Teleport"/> left a player where they were, raised through
/// <see cref="TileWorldServer.TeleportRefused"/>. A refused teleport moves nobody, advances no epoch and sends the
/// player nothing, because the player did not ask for it.</summary>
public enum TileTeleportRefusal : byte
{
    /// <summary>The position names no tile the world has: its region is not loaded in the server's collision map, or
    /// it is too far out to be a tile coordinate at all. The collision map reads such a tile as blocked, and this is
    /// that answer told apart from a wall.</summary>
    OutsideWorld = 1,

    /// <summary>The tile is loaded and blocked whole (<see cref="TileCollisionFlags.Blocked"/>): a wall, a rock, an
    /// object footprint, water a player cannot stand in. Placing the player there would stand them inside it with no
    /// legal step out.</summary>
    Blocked = 2,
}
