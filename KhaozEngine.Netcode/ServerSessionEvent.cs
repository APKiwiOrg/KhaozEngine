using System;

namespace KhaozEngine.Netcode;

/// <summary>What a <see cref="NetServer"/> surfaces per drained event.</summary>
public enum ServerSessionEventKind { Joined, Left, Data }

/// <summary>One server-side session event: a player joined/left, or sent game data.</summary>
public readonly struct ServerSessionEvent
{
    public ServerSessionEvent(ServerSessionEventKind kind, int slot, byte[] data, NetChannelReliability reliability, string subject = "")
    {
        Kind = kind;
        Slot = slot;
        Data = data ?? Array.Empty<byte>();
        Reliability = reliability;
        Subject = subject ?? string.Empty;
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

    /// <summary>For a <see cref="ServerSessionEventKind.Joined"/> event, the verified identity the connection was
    /// bound to by the <see cref="IConnectionAuthenticator"/> (empty for an anonymous/guest connection). Empty for
    /// the other event kinds.</summary>
    public string Subject { get; }

    /// <summary>A player joined; <paramref name="token"/> is the connect token from their Hello (carried in Data),
    /// <paramref name="subject"/> the verified identity the authenticator bound the connection to.</summary>
    public static ServerSessionEvent Joined(int slot, byte[] token, string subject) =>
        new(ServerSessionEventKind.Joined, slot, token ?? Array.Empty<byte>(), NetChannelReliability.ReliableOrdered, subject);

    /// <summary>A player joined; <paramref name="token"/> is the connect token from their Hello (carried in Data).</summary>
    public static ServerSessionEvent Joined(int slot, byte[] token) => Joined(slot, token, string.Empty);

    /// <summary>A player joined with no connect token.</summary>
    public static ServerSessionEvent Joined(int slot) => Joined(slot, Array.Empty<byte>(), string.Empty);

    public static ServerSessionEvent Left(int slot) =>
        new(ServerSessionEventKind.Left, slot, Array.Empty<byte>(), NetChannelReliability.ReliableOrdered);

    public static ServerSessionEvent FromData(int slot, byte[] data, NetChannelReliability r) =>
        new(ServerSessionEventKind.Data, slot, data, r);
}
