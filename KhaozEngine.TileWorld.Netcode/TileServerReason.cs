using KhaozEngine.Netcode;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The stable wire reason tokens a tile server sends, on the notice frame
/// (<see cref="TileProtocol.EncodeNotice"/>). NOT display text: a client matches the token and shows its OWN
/// localized string, which is the same contract the connect-gate refusals follow
/// (<c>KhaozEngine.Netcode.HandshakeToken</c>). The server owns no string catalog and must never author player
/// facing prose, so a token is the only thing it can honestly put on the wire.
/// <para>Tokens are prefixed <c>ke:</c> because a game is expected to add its own alongside them. The prefix is
/// what keeps an engine token and a game token from ever colliding, and what lets a client route an unknown
/// <c>ke:</c> token to a generic fallback rather than to silence.</para>
/// </summary>
public static class TileServerReason
{
    /// <summary>The thing the player clicked has no reachable tile, or the player cannot get to one. Sent on the
    /// tick the walk toward it ends without arriving, so a client can drop its own pending action at the same
    /// moment the server drops the authoritative one.</summary>
    public const string CannotReach = "ke:cannot-reach";

    /// <summary>The server is draining and will close the session when its grace expires. Broadcast the moment
    /// <see cref="TileWorldServer.BeginDrain"/> runs, so a client has the whole grace to show a countdown and log
    /// out cleanly rather than discovering the shutdown as a dropped connection.</summary>
    public const string Draining = "ke:draining";

    /// <summary>An operator closed this session with <see cref="TileWorldServer.Kick(int, string)"/>, or with the
    /// admin surface's <see cref="TileWorldServer.Kick(KhaozEngine.NetWorld.PlayerRef, string)"/> when it was handed
    /// no reason that fits on the wire. Distinct from <see cref="Banned"/>.</summary>
    public const string Kicked = "ke:kicked";

    /// <summary>This session's account is banned, and the session is closed. Sent when a ban reaches a player the
    /// door already admitted: one recorded in <see cref="TileWorldServerConfig.BanStore"/> (or answered by
    /// <see cref="TileWorldServerConfig.IsBanned"/>) between the door and the join, or while they play, and any
    /// admin kick of a banned account, which is how <c>ServerAdmin.BanAsync</c>'s kick arrives. The SAME string as
    /// <see cref="HandshakeToken.BannedReason"/>, the refusal the door sends a banned account that has not joined
    /// yet, so a client maps one token to its one localized line wherever the ban lands.</summary>
    public const string Banned = HandshakeToken.BannedReason;
}
