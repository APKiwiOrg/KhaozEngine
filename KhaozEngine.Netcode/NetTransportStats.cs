namespace KhaozEngine.Netcode;

/// <summary>
/// Transport-agnostic connection statistics surfaced by <see cref="INetTransport.Stats"/>: a snapshot of the
/// live link health with no LiteNetLib (or other backend) types leaking through the abstraction. Byte counters
/// are cumulative since the connection opened; callers derive per-second rates by diffing over time. The
/// loopback transport reports <see cref="Unavailable"/>; the LiteNetLib UDP binding fills it in.
/// </summary>
public readonly struct NetTransportStats
{
    /// <summary>True when the transport currently has a live peer.</summary>
    public bool Connected { get; }

    /// <summary>Round-trip time to the peer in milliseconds (0 when unavailable).</summary>
    public float RttMs { get; }

    /// <summary>Packet loss fraction, 0..1 (0 when unavailable).</summary>
    public float PacketLoss { get; }

    /// <summary>Total bytes received from the peer since the connection opened.</summary>
    public long BytesReceivedTotal { get; }

    /// <summary>Total bytes sent to the peer since the connection opened.</summary>
    public long BytesSentTotal { get; }

    public NetTransportStats(bool connected, float rttMs, float packetLoss, long bytesReceivedTotal, long bytesSentTotal)
    {
        Connected = connected;
        RttMs = rttMs;
        PacketLoss = packetLoss;
        BytesReceivedTotal = bytesReceivedTotal;
        BytesSentTotal = bytesSentTotal;
    }

    /// <summary>The "no statistics" value: disconnected, all zero. Returned by transports that don't track stats.</summary>
    public static NetTransportStats Unavailable => default;
}
