using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.NetWorld;

// Not all of these are ADMIN actions in the operator sense - SpeedScale is a gameplay mutation and self-rescue
// already rides Teleport - but they all share the one property this queue exists for: a mutation of authoritative
// state, submitted from an arbitrary thread, that must land at one deterministic point in the tick.
// SetPosition is Teleport's twin: the same placement, applied through the same case, differing only in whether the
// teleport epoch advances. It is a separate KIND rather than a flag on the struct so the drain reads the intent
// straight off the command.
internal enum AdminCommandKind : byte { Teleport, Kick, Broadcast, SpeedScale, SetPosition }

/// <summary>A queued admin mutation, applied on the host thread during the next tick.</summary>
internal readonly struct AdminCommand
{
    public AdminCommandKind Kind { get; init; }
    public PlayerRef Target { get; init; }
    public Vector3 Position { get; init; }
    public string Text { get; init; }

    /// <summary>The horizontal speed multiplier for <see cref="AdminCommandKind.SpeedScale"/>, unused otherwise.</summary>
    public float Scale { get; init; }
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
