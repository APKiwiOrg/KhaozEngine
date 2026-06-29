namespace KhaozEngine.NetWorld;

/// <summary>The live connection state of a <see cref="WorldClient"/>.</summary>
public enum WorldConnectionState
{
    /// <summary>Initial connect in flight; no first join yet.</summary>
    Connecting,
    /// <summary>Joined and receiving snapshots (healthy).</summary>
    Connected,
    /// <summary>Was connected, lost it, now retrying (auto-reconnect).</summary>
    Reconnecting,
    /// <summary>Terminal: gave up, was rejected, or was closed.</summary>
    Disconnected,
}
