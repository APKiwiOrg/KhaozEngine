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

    /// <summary>An operator closed this session with <see cref="TileWorldServer.Kick"/>. Distinct from a ban, which
    /// is refused at the door by the connect gate and never reaches a notice frame.</summary>
    public const string Kicked = "ke:kicked";
}
