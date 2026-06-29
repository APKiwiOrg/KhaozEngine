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
}
