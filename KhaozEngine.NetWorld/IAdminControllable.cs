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

    /// <summary>
    /// Queues the SAME placement as <see cref="Teleport"/> without advertising a discontinuity: the player is moved
    /// to <paramref name="position"/> with vertical velocity reset, and the teleport epoch is left exactly where it
    /// was, so nothing downstream of the epoch cuts.
    /// <para>Use it whenever the move is not a jump through space that a player should perceive as one: a per-tick
    /// position clamp, a death lock holding a body still, a soft boundary push. Teleport is the right lever for a
    /// portal, an admin yank or an unstuck, and it is unchanged. The difference matters because a client reacts to
    /// the epoch and a client reacts hard: a camera cut, a streaming-ring prime and rebuild, an avatar render-height
    /// snap. A game clamping a position every tick was claiming a teleport every tick, which cost nothing for eight
    /// minor versions and then cost a full world reload per tick as soon as one consumer grew a reaction to the
    /// epoch (#379).</para>
    /// <para>The move itself is no gentler than a teleport's. It is a server-authoritative correction like any
    /// other, so a large one still rubber-bands a predicting client. What this lever buys is honesty about whether
    /// the move was a cut, not a smoothing of it.</para>
    /// <para>A default interface method, so a head written before this existed keeps compiling. The default forwards
    /// to <see cref="Teleport"/>: the position is right and the cut is spurious, which is exactly the behaviour that
    /// head had before, and never worse. Both engine heads override it.</para>
    /// </summary>
    void SetPosition(PlayerRef target, Vector3 position) => Teleport(target, position);

    /// <summary>Queues a kick of <paramref name="target"/>; the reason is delivered to that client as a notice.</summary>
    void Kick(PlayerRef target, string reason);

    /// <summary>Queues a broadcast of <paramref name="text"/> to every client (a Custom server notice).</summary>
    void Broadcast(string text);
}
