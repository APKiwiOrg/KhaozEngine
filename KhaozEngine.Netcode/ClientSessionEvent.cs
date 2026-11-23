using System;

namespace KhaozEngine.Netcode;

/// <summary>What a <see cref="NetClient"/> surfaces per drained event.</summary>
public enum ClientSessionEventKind { Joined, Rejected, Data, Disconnected }

/// <summary>One client-side session event: join accepted (with slot), rejected (with reason), data, or dropped.</summary>
public readonly struct ClientSessionEvent
{
    private readonly byte[] data;
    private readonly bool hasSessionHeader;

    public ClientSessionEvent(ClientSessionEventKind kind, int slot, byte[] data, NetChannelReliability reliability, string rejectReason)
        : this(kind, slot, data, reliability, rejectReason, hasSessionHeader: false) { }

    private ClientSessionEvent(ClientSessionEventKind kind, int slot, byte[] data, NetChannelReliability reliability,
        string rejectReason, bool hasSessionHeader)
    {
        Kind = kind;
        Slot = slot;
        this.data = data ?? Array.Empty<byte>();
        this.hasSessionHeader = hasSessionHeader;
        Reliability = reliability;
        RejectReason = rejectReason ?? string.Empty;
    }

    /// <summary>The event kind.</summary>
    public ClientSessionEventKind Kind { get; }

    /// <summary>The assigned slot on <see cref="ClientSessionEventKind.Joined"/>; -1 otherwise.</summary>
    public int Slot { get; }

    /// <summary>Game payload for a <see cref="ClientSessionEventKind.Data"/> event; empty otherwise.</summary>
    public byte[] Data => hasSessionHeader ? SessionFrame.ReadBody(data) : data;

    internal ReadOnlyMemory<byte> Payload => hasSessionHeader ? data.AsMemory(1) : data;
    internal ClientSessionEvent OwnData() => hasSessionHeader ? FromData(Data, Reliability) : this;

    /// <summary>The channel a <see cref="ClientSessionEventKind.Data"/> event arrived on.</summary>
    public NetChannelReliability Reliability { get; }

    /// <summary>The reason on <see cref="ClientSessionEventKind.Rejected"/>; empty otherwise.</summary>
    public string RejectReason { get; }

    public static ClientSessionEvent Joined(int slot) =>
        new(ClientSessionEventKind.Joined, slot, Array.Empty<byte>(), NetChannelReliability.ReliableOrdered, string.Empty);

    public static ClientSessionEvent Rejected(string reason) =>
        new(ClientSessionEventKind.Rejected, -1, Array.Empty<byte>(), NetChannelReliability.ReliableOrdered, reason);

    public static ClientSessionEvent FromData(byte[] data, NetChannelReliability r) =>
        new(ClientSessionEventKind.Data, -1, data, r, string.Empty);

    internal static ClientSessionEvent FromFrame(byte[] frame, NetChannelReliability reliability) =>
        new(ClientSessionEventKind.Data, -1, frame, reliability, string.Empty, hasSessionHeader: true);

    public static ClientSessionEvent Disconnected() =>
        new(ClientSessionEventKind.Disconnected, -1, Array.Empty<byte>(), NetChannelReliability.ReliableOrdered, string.Empty);
}
