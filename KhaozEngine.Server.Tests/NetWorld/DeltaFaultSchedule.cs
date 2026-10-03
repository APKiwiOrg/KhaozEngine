using System;
using System.Collections.Generic;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>The direction a scheduled fault travels.</summary>
internal enum FaultDirection
{
    ServerToClient,
    ClientToServer,
}

/// <summary>What a scheduled fault does to the frame it names.</summary>
internal enum FaultAction
{
    /// <summary>The frame is never handed on. Unreliable frames only: a reliable frame is never truly lost.</summary>
    Drop,

    /// <summary>The frame is held and handed on at its release subtick. Reliable frames behind it wait in order.</summary>
    Delay,

    /// <summary>The frame is handed on now and a copy at its release subtick. Unreliable frames only.</summary>
    Duplicate,
}

/// <summary>
/// One named fault of a finite schedule: the <paramref name="Ordinal"/>th frame of <paramref name="FrameKind"/> in
/// <paramref name="Direction"/> on one connection, counted from 1, gets <paramref name="Action"/>. Ordinals count per
/// direction and recognized family, so Hello, MOVE and notice traffic never consume a state's ordinal.
/// </summary>
/// <param name="Ordinal">The 1-based ordinal within the frame family on one connection.</param>
/// <param name="Direction">The direction the frame travels.</param>
/// <param name="FrameKind">A <see cref="FaultFrameKind"/> family.</param>
/// <param name="Action">What happens to the frame.</param>
/// <param name="ReleaseSubtick">The 120 Hz subtick a delayed frame or a duplicate copy is handed on. Zero for a
/// drop.</param>
internal readonly record struct DeltaFault(int Ordinal, FaultDirection Direction, byte FrameKind, FaultAction Action,
    int ReleaseSubtick);

/// <summary>
/// Frame families the fault transport classifies. Server frames use the NetWorld server frame kind byte. Client frames
/// use the length-validated control family, so an 18-byte MOVE is always <see cref="Move"/> whatever its prefix, and a
/// format 2 acknowledgement splits by channel into <see cref="RoutineAck"/> and <see cref="KeyframeAck"/>.
/// </summary>
internal static class FaultFrameKind
{
    public const byte Snapshot = (byte)MoveProtocol.ServerFrameKind.Snapshot;
    public const byte Notice = (byte)MoveProtocol.ServerFrameKind.Notice;
    public const byte LegacyDelta = (byte)MoveProtocol.ServerFrameKind.Delta;
    public const byte GameMessage = (byte)MoveProtocol.ServerFrameKind.GameMessage;
    public const byte RebuildDelta = (byte)MoveProtocol.ServerFrameKind.RebuildDelta;
    public const byte KeyframeChunk = (byte)MoveProtocol.ServerFrameKind.RebuildKeyframeChunk;
    public const byte ReplicationMode = (byte)MoveProtocol.ServerFrameKind.ReplicationMode;

    /// <summary>Every 18-byte client payload.</summary>
    public const byte Move = 18;

    /// <summary>A 2-byte legacy client control: capability, rescue.</summary>
    public const byte Control = 0xC5;

    public const byte LegacyAck = 0xA0;
    public const byte Accept = (byte)RebuildControlKind.Accept;

    /// <summary>A format 2 acknowledgement on the unreliable channel.</summary>
    public const byte RoutineAck = (byte)RebuildControlKind.Acknowledge;

    public const byte Repair = (byte)RebuildControlKind.Repair;

    /// <summary>A format 2 acknowledgement on the reliable channel. Test-local family value: the wire marker is
    /// still 0xA2.</summary>
    public const byte KeyframeAck = 0xA4;

    public const byte ClientGameMessage = 0xB0;

    /// <summary>A malformed recognized control or anything else. Never faulted.</summary>
    public const byte Unclassified = 0xFF;

    /// <summary>The family of a server payload as the transport carries it, session frame byte first. Session control
    /// frames are unclassified.</summary>
    public static byte ClassifyServer(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != (byte)SessionOpcode.Data) return Unclassified;
        return payload[1] <= ReplicationMode ? payload[1] : Unclassified;
    }

    /// <summary>The family of a client payload as the transport carries it, session frame byte first. Length comes
    /// first: an 18-byte body is a MOVE before any marker is read.</summary>
    public static byte ClassifyClient(ReadOnlySpan<byte> payload, NetChannelReliability reliability)
    {
        if (payload.Length < 2 || payload[0] != (byte)SessionOpcode.Data) return Unclassified;
        ReadOnlySpan<byte> body = payload[1..];
        if (body.Length == RebuildServerStream.MoveFrameBytes) return Move;
        switch (RebuildProtocol.DecodeClientControl(body, out RebuildClientControl control))
        {
            case ControlReadResult.Valid when control.Kind == RebuildControlKind.Acknowledge:
                return reliability == NetChannelReliability.ReliableOrdered ? KeyframeAck : RoutineAck;
            case ControlReadResult.Valid:
                return (byte)control.Kind;
            case ControlReadResult.Malformed:
                return Unclassified;
        }
        if (MoveProtocol.TryDecodeReplicationAck(body, out _)) return LegacyAck;
        if (MoveProtocol.TryDecodeClientControl(body, out _)) return Control;
        if (MoveProtocol.TryDecodeGameMessage(body, out _, out _)) return ClientGameMessage;
        return Unclassified;
    }
}

/// <summary>
/// The fixed time base and hand-derived join timeline every finite schedule is written against, plus schedule
/// builders and validation. Time is 120 Hz integer subticks: a 30 Hz server tick is four subticks and a 60 Hz
/// presentation frame two.
/// </summary>
/// <remarks>
/// <para>Join timeline, derived from the protocol and the rig's event order, independent of the client phase. Server
/// tick 0 accepts the connections. Each client polls after it and sends Hello. Tick 1 joins both players and serves
/// one legacy snapshot. The clients answer with both capabilities, tick 2 sends each mode offer, tick 3 receives the
/// acceptance and sends the keyframe, and tick 4 receives the reliable keyframe acknowledgement and sends the first
/// routine state datagram. So state datagram ordinal n is built and sent at server tick n + 3.</para>
/// <para>A client answers every server frame before the next server tick, so routine acknowledgement ordinal n
/// advertises datagram n - 1 (ordinal 1 advertises the keyframe) and reaches the server at tick n + 3. A legacy
/// acknowledgement ordinal n answers the legacy delta sent at tick n + 1.</para>
/// </remarks>
internal static class DeltaFaultSchedule
{
    public const int SubticksPerServerTick = 4;
    public const int SubticksPerFrame = 2;

    /// <summary>Ordinary unreliable reordering delay bound: two server ticks.</summary>
    public const int MaxOrdinaryDelaySubticks = 2 * SubticksPerServerTick;

    /// <summary>Frames a fault transport may hold at once.</summary>
    public const int MaxQueuedFrames = 64;

    /// <summary>Every fault action ends by this server tick.</summary>
    public const int LastFaultTick = 60;

    /// <summary>The server tick of state datagram ordinal 1.</summary>
    public const int FirstRoutineStateTick = 4;

    public const float TickSeconds = 1f / 30f;
    public const float FrameSeconds = 1f / 60f;

    public static int Subtick(int serverTick) => serverTick * SubticksPerServerTick;

    /// <summary>The state datagram ordinal the server sends at <paramref name="serverTick"/>.</summary>
    public static int StateOrdinalSentAt(int serverTick) => serverTick - FirstRoutineStateTick + 1;

    /// <summary>The routine acknowledgement ordinal that advertises the datagram sent at
    /// <paramref name="serverTick"/>.</summary>
    public static int RoutineAckOrdinalFor(int serverTick) => StateOrdinalSentAt(serverTick) + 1;

    public static DeltaFault DropState(int ordinal) =>
        new(ordinal, FaultDirection.ServerToClient, FaultFrameKind.RebuildDelta, FaultAction.Drop, 0);

    public static DeltaFault DelayState(int ordinal, int releaseSubtick) =>
        new(ordinal, FaultDirection.ServerToClient, FaultFrameKind.RebuildDelta, FaultAction.Delay, releaseSubtick);

    public static DeltaFault DuplicateState(int ordinal, int releaseSubtick) =>
        new(ordinal, FaultDirection.ServerToClient, FaultFrameKind.RebuildDelta, FaultAction.Duplicate, releaseSubtick);

    public static DeltaFault DropRoutineAck(int ordinal) =>
        new(ordinal, FaultDirection.ClientToServer, FaultFrameKind.RoutineAck, FaultAction.Drop, 0);

    public static DeltaFault DelayRoutineAck(int ordinal, int releaseSubtick) =>
        new(ordinal, FaultDirection.ClientToServer, FaultFrameKind.RoutineAck, FaultAction.Delay, releaseSubtick);

    /// <summary>Drops routine acknowledgement ordinals <paramref name="first"/> through <paramref name="last"/>.</summary>
    public static IEnumerable<DeltaFault> DropRoutineAcks(int first, int last)
    {
        for (int ordinal = first; ordinal <= last; ordinal++) yield return DropRoutineAck(ordinal);
    }

    /// <summary>Rejects a schedule a finite table may not contain: a non-positive ordinal, a repeated frame, a drop
    /// or duplicate of a reliable-only family, or a release after <see cref="LastFaultTick"/>.</summary>
    /// <exception cref="InvalidOperationException">The schedule is a fixture error.</exception>
    public static void Validate(IReadOnlyList<DeltaFault> faults)
    {
        var seen = new HashSet<(FaultDirection, byte, int)>();
        foreach (DeltaFault fault in faults)
        {
            if (fault.Ordinal < 1) throw FixtureError($"ordinal {fault.Ordinal} is not positive");
            if (!seen.Add((fault.Direction, fault.FrameKind, fault.Ordinal)))
                throw FixtureError($"{fault.Direction} kind 0x{fault.FrameKind:X2} ordinal {fault.Ordinal} is scheduled twice");
            if (fault.Action != FaultAction.Delay && IsReliableOnly(fault.Direction, fault.FrameKind))
                throw FixtureError($"{fault.Action} of reliable family 0x{fault.FrameKind:X2}");
            if (fault.Action != FaultAction.Drop && fault.ReleaseSubtick > Subtick(LastFaultTick))
                throw FixtureError($"release subtick {fault.ReleaseSubtick} is after tick {LastFaultTick}");
        }
    }

    /// <summary>True for a family that only ever travels reliably.</summary>
    public static bool IsReliableOnly(FaultDirection direction, byte kind) => direction == FaultDirection.ServerToClient
        ? kind is FaultFrameKind.KeyframeChunk or FaultFrameKind.ReplicationMode or FaultFrameKind.Snapshot
            or FaultFrameKind.LegacyDelta or FaultFrameKind.Notice
        : kind is FaultFrameKind.Move or FaultFrameKind.LegacyAck or FaultFrameKind.Accept or FaultFrameKind.Repair
            or FaultFrameKind.KeyframeAck or FaultFrameKind.Control;

    public static InvalidOperationException FixtureError(string detail) => new($"Fault fixture error: {detail}.");
}
