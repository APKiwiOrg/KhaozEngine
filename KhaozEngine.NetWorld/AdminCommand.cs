using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.NetWorld;

internal enum AdminCommandKind : byte { Teleport, Kick, Broadcast }

/// <summary>A queued admin mutation, applied on the host thread during the next tick.</summary>
internal readonly struct AdminCommand
{
    public AdminCommandKind Kind { get; init; }
    public PlayerRef Target { get; init; }
    public Vector3 Position { get; init; }
    public string Text { get; init; }
}

/// <summary>
/// The thread-safety bridge for the admin surface, shared by both servers. <see cref="Enqueue"/> is called from any
/// thread; <see cref="Drain"/> runs on the host thread at the top of a tick; <see cref="Publish"/> stores the
/// online snapshot at the end of a tick and <see cref="Online"/> reads it lock-free.
/// </summary>
internal sealed class AdminCommandBuffer
{
    private readonly ConcurrentQueue<AdminCommand> queue = new();
    private volatile IReadOnlyList<OnlinePlayer> online = Array.Empty<OnlinePlayer>();

    public void Enqueue(in AdminCommand command) => queue.Enqueue(command);

    public void Drain(Action<AdminCommand> apply)
    {
        while (queue.TryDequeue(out AdminCommand cmd)) apply(cmd);
    }

    public void Publish(IReadOnlyList<OnlinePlayer> snapshot) => online = snapshot;

    public IReadOnlyList<OnlinePlayer> Online => online;
}
