namespace KhaozEngine.NetWorld;

/// <summary>Why a server flagged a connection as suspicious. The engine raises the signal; the game decides the
/// policy (log / kick / ban).</summary>
public enum SuspiciousReason
{
    /// <summary>A move packet failed to decode: wrong length, or a NaN/infinite move axis or camera yaw.</summary>
    MalformedPacket,

    /// <summary>A connection exceeded its configured inbound message rate (the message was dropped).</summary>
    RateLimited,

    /// <summary>The authoritative sim repeatedly had to correct a player's intended move beyond the configured
    /// distance - the client kept driving into a wall slide, static collision, or the play-area boundary.</summary>
    MovementCorrection,

    /// <summary>A client sent a game message (<see cref="WorldClient.SendGameMessage"/>) whose payload exceeded the
    /// server's configured <c>MaxGameMessageBytes</c> cap; the message was dropped (never dispatched to
    /// <c>OnGameMessage</c>). The <see cref="SuspiciousActivity.Magnitude"/> carries the offending payload size in
    /// bytes.</summary>
    OversizedMessage,
}

/// <summary>One server-side anomaly signal, passed to <see cref="WorldServer.OnSuspiciousActivity"/> /
/// <see cref="ShardedWorldServer.OnSuspiciousActivity"/>. A value type, so raising it allocates nothing.</summary>
public readonly struct SuspiciousActivity
{
    public SuspiciousActivity(int slot, SuspiciousReason reason, float magnitude = 0f)
    {
        Slot = slot;
        Reason = reason;
        Magnitude = magnitude;
    }

    /// <summary>The player slot the signal concerns.</summary>
    public int Slot { get; }

    /// <summary>What was detected.</summary>
    public SuspiciousReason Reason { get; }

    /// <summary>Reason-specific magnitude: the correction distance (world units) for
    /// <see cref="SuspiciousReason.MovementCorrection"/>; 0 for the others.</summary>
    public float Magnitude { get; }
}
