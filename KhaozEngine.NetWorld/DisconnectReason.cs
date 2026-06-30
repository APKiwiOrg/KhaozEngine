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
}
