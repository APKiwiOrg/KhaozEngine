namespace KhaozEngine.Diagnostics;

/// <summary>
/// Immutable per-frame snapshot of a networked client's connection health, produced by
/// <c>KhaozEngine.NetWorld.WorldClient.NetStats</c> and rendered by
/// <see cref="KhaozEngine.Gui.DiagnosticsOverlay"/>'s network section.
/// <para>
/// It lives in <c>KhaozEngine.Diagnostics</c> (the low telemetry leaf) rather than alongside
/// <c>WorldClient</c> so the Gui overlay can name the type without <c>KhaozEngine.Gui</c> taking a
/// dependency on the server / netcode / sharding stack. <c>WorldClient</c> fills it from its otherwise
/// private <c>NetClient</c> / transport.
/// </para>
/// <para>
/// Rates are smoothed over a rolling ~1s window. Correction magnitudes are in world metres (the
/// predicted-vs-authoritative reconciliation delta). <see cref="Connected"/> is false before the session
/// joins and after it drops; the remaining fields are 0 in that state.
/// </para>
/// </summary>
public readonly struct ClientNetStats
{
    /// <summary>Round-trip ping in milliseconds (0 when unavailable, e.g. a loopback transport).</summary>
    public float RttMs { get; init; }

    /// <summary>Packet loss fraction, 0..1 (0 when unavailable).</summary>
    public float PacketLoss { get; init; }

    /// <summary>Smoothed inbound throughput in bytes/sec.</summary>
    public float BytesInPerSec { get; init; }

    /// <summary>Smoothed outbound throughput in bytes/sec.</summary>
    public float BytesOutPerSec { get; init; }

    /// <summary>Authoritative AoI snapshots ingested per second (rolling window).</summary>
    public float SnapshotsPerSec { get; init; }

    /// <summary>Magnitude of the most recent prediction-reconciliation correction, in world metres.</summary>
    public float LastCorrectionMeters { get; init; }

    /// <summary>Rolling average of recent correction magnitudes, in world metres.</summary>
    public float AvgCorrectionMeters { get; init; }

    /// <summary>True once the session has joined and while it remains connected.</summary>
    public bool Connected { get; init; }
}
