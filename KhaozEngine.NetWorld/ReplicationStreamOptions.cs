using System;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Limits and cadence of negotiated unreliable delta replication (format 2), shared by both servers and the client.
/// Read only when the host's opt-in and its existing delta switch are both on, so a reliable-only host never has
/// these budgets checked. The defaults are the approved review defaults, not measured capacity.
/// </summary>
public sealed class ReplicationStreamOptions
{
    /// <summary>Projection retention, keyframe, entity and component limits, and the no-ack send window
    /// (<see cref="DeltaRebuildOptions.NoAckSendWindow"/>, default 31). Leave
    /// <see cref="DeltaRebuildOptions.EnvelopeBytes"/> at 0 or set it to 12. NetWorld charges its own 12-byte
    /// envelope and refuses any other value at validation.</summary>
    public DeltaRebuildOptions Limits { get; init; } = new();

    /// <summary>The largest transport payload sent, in bytes, session frame and NetWorld envelope included. The
    /// selected cap is the smaller of this and the connection's actual unfragmented limit. It must leave room for
    /// the keyframe budget in at most 255 chunks of <c>cap - 23</c> bytes. Default 512.</summary>
    public int MaxTransportPayloadBytes { get; init; } = 512;

    /// <summary>Reliable keyframe chunks sent per viewer per replication tick. Default 4.</summary>
    public int MaxChunksPerTick { get; init; } = 4;

    /// <summary>Replication ticks between repeated missing-baseline repair requests. Default 30.</summary>
    public int RepairRequestIntervalTicks { get; init; } = 30;

    /// <summary>Replication ticks negotiation or repair may take before the session ends with
    /// <see cref="DisconnectReason.ReplicationRecoveryFailed"/>. Default 90.</summary>
    public int RecoveryDeadlineTicks { get; init; } = 90;

    /// <summary>The fewest retained projections a stream can run with.</summary>
    internal const int MinRetainedProjections = 4;

    /// <summary>Per-chunk overhead: session byte, kind byte, epoch, sequence, total length and the generic fragment
    /// header.</summary>
    internal const int KeyframeChunkOverheadBytes = 1 + RebuildProtocol.KeyframeChunkHeaderBytes
        + MessageFragmenter.HeaderBytes;

    /// <summary>The complete empty state datagram: session byte, kind byte, envelope, format 2 header and the two
    /// empty counts. A smaller cap can never carry state.</summary>
    internal const int MinStateDatagramBytes = 40;

    /// <summary>True when format 2 applies: the opt-in and the existing delta switch are both on.</summary>
    internal static bool IsEnabled(bool deltaReplication, bool optIn) => deltaReplication && optIn;

    /// <summary>The fixed keyframe chunk payload width for a packet cap.</summary>
    internal static int ChunkWidth(int packetCap) => packetCap - KeyframeChunkOverheadBytes;

    /// <summary>Chunks a complete keyframe object of <paramref name="maxKeyframeBytes"/> takes at
    /// <paramref name="packetCap"/>, or <see cref="int.MaxValue"/> when the cap leaves no chunk payload.</summary>
    internal static int KeyframeChunkCount(int maxKeyframeBytes, int packetCap)
    {
        int width = ChunkWidth(packetCap);
        if (width <= 0) return int.MaxValue;
        return (int)(((long)maxKeyframeBytes + width - 1) / width);
    }

    /// <summary>True when <paramref name="packetCap"/> carries an empty state datagram and a complete keyframe in at
    /// most <see cref="MessageFragmenter.MaxChunks"/> chunks.</summary>
    internal static bool IsFeasiblePacketCap(int packetCap, int maxKeyframeBytes) =>
        packetCap >= MinStateDatagramBytes
        && KeyframeChunkCount(maxKeyframeBytes, packetCap) <= MessageFragmenter.MaxChunks;

    /// <summary>Selects the packet cap for one connection: the smallest of the configured cap and both actual
    /// limits, never raised. False when a limit is unknown (0 or less) or the result is infeasible, which selects
    /// reliable fallback with <see cref="ReplicationSelectionReason.UnavailableTransportLimit"/>.</summary>
    internal bool TrySelectPacketCap(int unreliableLimit, int reliableLimit, out int packetCap)
    {
        packetCap = 0;
        if (unreliableLimit <= 0 || reliableLimit <= 0) return false;
        int cap = Math.Min(MaxTransportPayloadBytes, Math.Min(unreliableLimit, reliableLimit));
        if (!IsFeasiblePacketCap(cap, Limits.MaxKeyframeBytes)) return false;
        packetCap = cap;
        return true;
    }

    /// <summary>Validates a host or client config. Returns null and checks nothing when the opt-in is off, so a
    /// reliable-only caller keeps its current behavior. An opt-in without the existing delta switch is a
    /// configuration error. Otherwise returns <see cref="ValidateForUnreliable"/>.</summary>
    /// <exception cref="ArgumentException">The opt-in is on and the delta switch is off. <c>ParamName</c> is
    /// <c>optIn</c>. Raised before any budget is read.</exception>
    internal DeltaRebuildOptions? ValidateConfig(bool deltaReplication, bool optIn, float tickSeconds)
    {
        if (!optIn) return null;
        if (!deltaReplication)
            throw new ArgumentException(
                "Unreliable delta replication needs delta replication. Turn on DeltaReplication (servers) or " +
                "RequestDeltaReplication (client), or turn the unreliable opt-in off.", nameof(optIn));
        return ValidateForUnreliable(tickSeconds);
    }

    /// <summary>Validates every limit for a format 2 stream and returns <see cref="Limits"/> with the 12-byte
    /// envelope charge. Nothing is raised or replaced silently.</summary>
    /// <exception cref="ArgumentNullException"><see cref="Limits"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The first invalid value, named by <c>ParamName</c>.</exception>
    internal DeltaRebuildOptions ValidateForUnreliable(float tickSeconds)
    {
        if (Limits is null) throw new ArgumentNullException(nameof(Limits));
        if (Limits.EnvelopeBytes is not (0 or RebuildProtocol.EnvelopeBytes))
            throw Invalid("Limits.EnvelopeBytes", Limits.EnvelopeBytes, "must be 0 or 12");
        Limits.Validate();
        if (Limits.MaxRetainedProjections > ushort.MaxValue)
            throw Invalid("Limits.MaxRetainedProjections", Limits.MaxRetainedProjections, "must fit a ushort");
        if (!float.IsFinite(tickSeconds) || tickSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(tickSeconds), tickSeconds,
                "TickSeconds must be positive and finite when unreliable delta replication is enabled.");
        if (MaxChunksPerTick is < 1 or > ushort.MaxValue)
            throw Invalid(nameof(MaxChunksPerTick), MaxChunksPerTick, "must be in 1..65535");
        if (RepairRequestIntervalTicks < 1)
            throw Invalid(nameof(RepairRequestIntervalTicks), RepairRequestIntervalTicks, "must be at least 1");
        if (RecoveryDeadlineTicks < 1)
            throw Invalid(nameof(RecoveryDeadlineTicks), RecoveryDeadlineTicks, "must be at least 1");
        if (!IsFeasiblePacketCap(MaxTransportPayloadBytes, Limits.MaxKeyframeBytes))
            throw Invalid(nameof(MaxTransportPayloadBytes), MaxTransportPayloadBytes,
                "must be at least 40 and carry Limits.MaxKeyframeBytes in at most 255 chunks of (cap - 23) bytes");
        return StreamLimits();
    }

    /// <summary>The stream limits with NetWorld's 12-byte envelope charged. The only place they are derived, so the
    /// validated value, the mode offer and the writer always agree.</summary>
    internal DeltaRebuildOptions StreamLimits() => Limits.WithEnvelopeBytes(RebuildProtocol.EnvelopeBytes);

    private static ArgumentOutOfRangeException Invalid(string property, int value, string rule) =>
        new(property, value, $"ReplicationStreamOptions.{property} {rule}.");
}
