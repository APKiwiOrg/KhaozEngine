using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The generic live-admin surface implemented by both <see cref="WorldServer"/> and <see cref="ShardedWorldServer"/>.
/// Reads (<see cref="ListOnline"/>) return a snapshot published once per tick (lock-free, at most one tick stale).
/// Mutations are queued and applied on the host thread between ticks, so callers (e.g. an HTTP handler on a foreign
/// thread) never touch the simulation directly.
/// </summary>
public interface IAdminControllable
{
    /// <summary>The most recently published online snapshot (at most one tick stale).</summary>
    IReadOnlyList<OnlinePlayer> ListOnline();

    /// <summary>Queues a teleport of <paramref name="target"/> to <paramref name="position"/> (vertical velocity reset).</summary>
    void Teleport(PlayerRef target, Vector3 position);

    /// <summary>Queues a kick of <paramref name="target"/>; the reason is delivered to that client as a notice.</summary>
    void Kick(PlayerRef target, string reason);

    /// <summary>Queues a broadcast of <paramref name="text"/> to every client (a Custom server notice).</summary>
    void Broadcast(string text);
}
