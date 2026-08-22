using KhaozEngine.Ecs;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// A player's cosmetic display name, replicated to everyone whose area of interest the player is in, which is what
/// a nameplate needs. Nothing here is a rules input: the name is never matched, compared or authorized against,
/// so a client that receives a wrong one draws a wrong label and nothing more.
/// <para>Kept out of <see cref="TileMoveState"/> deliberately. The move state is replicated every tick a player
/// moves and a name changes about once ever, so folding a string into it would put a length-prefixed allocation on
/// the movement hot path for a value that almost never differs from the last one.</para>
/// </summary>
public struct TileIdentity : IComponent
{
    /// <summary>The verified display name the connect token produced, or null when the head admits anonymously.
    /// Clamped to <see cref="TileProtocol.MaxDisplayNameBytes"/> on the wire in both directions, so a hostile name
    /// costs a bounded number of bytes rather than a snapshot the size of whatever a peer felt like sending.</summary>
    public string? DisplayName;
}
