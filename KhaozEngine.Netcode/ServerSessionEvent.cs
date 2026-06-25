using System;

namespace KhaozEngine.Netcode;

/// <summary>What a <see cref="NetServer"/> surfaces per drained event.</summary>
public enum ServerSessionEventKind { Joined, Left, Data }

/// <summary>One server-side session event: a player joined/left, or sent game data.</summary>
public readonly struct ServerSessionEvent
{
    public ServerSessionEvent(ServerSessionEventKind kind, int slot, byte[] data, NetChannelReliability reliability)
    {
        Kind = kind;
        Slot = slot;
        Data = data ?? Array.Empty<byte>();
        Reliability = reliability;
    }

    /// <summary>The event kind.</summary>
    public ServerSessionEventKind Kind { get; }

    /// <summary>The player slot the event concerns.</summary>
    public int Slot { get; }

    /// <summary>Game payload for a <see cref="ServerSessionEventKind.Data"/> event; empty otherwise.</summary>
    public byte[] Data { get; }

    /// <summary>The channel a <see cref="ServerSessionEventKind.Data"/> event arrived on.</summary>
    public NetChannelReliability Reliability { get; }

    public static ServerSessionEvent Joined(int slot) =>
        new(ServerSessionEventKind.Joined, slot, Array.Empty<byte>(), NetChannelReliability.ReliableOrdered);

    public static ServerSessionEvent Left(int slot) =>
        new(ServerSessionEventKind.Left, slot, Array.Empty<byte>(), NetChannelReliability.ReliableOrdered);

    public static ServerSessionEvent FromData(int slot, byte[] data, NetChannelReliability r) =>
        new(ServerSessionEventKind.Data, slot, data, r);
}
