namespace KhaozEngine.NetWorld;

/// <summary>Why a <see cref="WorldClient"/> lost (or could not establish) its session.</summary>
public enum DisconnectReason
{
    /// <summary>Healthy / never disconnected.</summary>
    None,
    /// <summary>The server rejected the connect token (bad/expired). <see cref="WorldClient.DisconnectReasonDetail"/>
    /// carries the authenticator's reason string. Not retried by default.</summary>
    RejectedToken,
    /// <summary>The transport dropped with no prior shutdown notice (crash / network loss / unreachable).</summary>
    Unreachable,
    /// <summary>The transport dropped after a <see cref="ServerNoticeKind.Shutdown"/> notice (a planned restart).</summary>
    ServerShutdown,
    /// <summary>No snapshot arrived within the configured timeout while the transport was still nominally up.</summary>
    Timeout,
    /// <summary>The client is incompatible with the server's build/protocol. Set either when the connect-time
    /// version handshake rejected the client (a <see cref="VersionCheckingAuthenticator"/> mismatch -
    /// <see cref="WorldClient.DisconnectReasonDetail"/> carries the server's required version), or as a last-resort
    /// backstop when a snapshot could not be decoded (an unregistered component type id from a newer protocol -
    /// the detail carries the decode error). Not retried: the client must update. Show "client out of date,
    /// please update".</summary>
    IncompatibleVersion,
    /// <summary>This account signed in somewhere else and that session took the seat: the server ended THIS one
    /// (<see cref="Netcode.DuplicateSessionPolicy.KickOlder"/>, the default). Not retried, deliberately: reconnecting
    /// would kick the session that just displaced this one, and the two clients would trade the seat forever. Show
    /// "signed in elsewhere" and let the player decide whether to sign in again.</summary>
    SignedInElsewhere,
    /// <summary>This account already holds a live session on the server, which kept its seat
    /// (<see cref="Netcode.DuplicateSessionPolicy.RefuseNewer"/>): the join was refused rather than the other session
    /// displaced. RETRIED on the backoff, unlike <see cref="SignedInElsewhere"/>: a refusal displaces nobody, so
    /// there is no seat to trade, and the usual holder of that seat is this player's own half-dead connection, which
    /// the server drops once its transport timeout expires (5 s on LiteNetLib's default). Show "already signed in"
    /// while the state is <c>Reconnecting</c>. Cap the asking with <see cref="ReconnectBackoff.MaxAttempts"/> if a
    /// game would rather stop after a few.</summary>
    AlreadySignedIn,
    /// <summary>The transport dropped after a <see cref="ServerNoticeKind.Banned"/> notice: the server refused this
    /// account rather than the connection being lost. Mirrors what <see cref="ServerShutdown"/> does for a planned
    /// restart, so a consumer reading only the disconnect reason can tell a ban from an outage without also
    /// subscribing to <see cref="WorldClient.NoticeReceived"/>. Show "you are banned", not "connection lost".
    /// <para>RETRIED on the backoff, exactly like <see cref="ServerShutdown"/>, and deliberately so: a ban may carry
    /// an expiry (<see cref="IBanStore.BanAsync"/> takes an <c>until</c>), and a client that went terminal here would
    /// sit out a five-minute ban forever. A game that would rather stop asking sets
    /// <see cref="ReconnectBackoff.MaxAttempts"/>, or turns <see cref="WorldClientConfig.AutoReconnect"/> off when it
    /// reads this reason.</para></summary>
    Banned,
}
