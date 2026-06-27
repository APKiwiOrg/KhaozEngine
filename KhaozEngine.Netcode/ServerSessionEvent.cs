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

    /// <summary>Game payload for a <see cref="ServerSessionEventKind.Data"/> event; for a
    /// <see cref="ServerSessionEventKind.Joined"/> event it carries the connect token the client presented in
    /// its Hello (empty if none). Empty for <see cref="ServerSessionEventKind.Left"/>.</summary>
    public byte[] Data { get; }

    /// <summary>The channel a <see cref="ServerSessionEventKind.Data"/> event arrived on.</summary>
    public NetChannelReliability Reliability { get; }

    /// <summary>A player joined; <paramref name="token"/> is the connect token from their Hello (carried in Data).</summary>
    public static ServerSessionEvent Joined(int slot, byte[] token) =>
        new(ServerSessionEventKind.Joined, slot, token ?? Array.Empty<byte>(), NetChannelReliability.ReliableOrdered);

    /// <summary>A player joined with no connect token.</summary>
    public static ServerSessionEvent Joined(int slot) => Joined(slot, Array.Empty<byte>());

    public static ServerSessionEvent Left(int slot) =>
        new(ServerSessionEventKind.Left, slot, Array.Empty<byte>(), NetChannelReliability.ReliableOrdered);

    public static ServerSessionEvent FromData(int slot, byte[] data, NetChannelReliability r) =>
        new(ServerSessionEventKind.Data, slot, data, r);
}
