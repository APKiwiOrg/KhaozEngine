using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Resolves where a JOINING player's entity should be built, by account id, before the first snapshot goes out.
/// Returns false when nothing is known about that account, which leaves the join on
/// <see cref="WorldServerConfig.SpawnPosition"/> (or the sharded equivalent) exactly as before.
/// <para>Installed on a server head through <see cref="IWorldPersistenceHost.SetResumePositionProvider"/>.
/// <see cref="WorldPersistence"/> installs one backed by its own <see cref="WorldPersistence.ResumeHints"/> cache;
/// a game with its own account store can install its own instead (after constructing the persistence layer, which
/// installs at construction time).</para>
/// <para>Invoked on the server thread inside the join, so it must be a fast in-memory lookup. It is a HINT, never
/// the authority: a persistence layer's asynchronous load-on-join still runs and still corrects the position when
/// the two disagree.</para>
/// </summary>
/// <param name="accountId">The joining connection's account id (the same key persistence stores under).</param>
/// <param name="position">The player's last known ABSOLUTE world position when the call returns true.</param>
public delegate bool ResumePositionProvider(string accountId, out Vector3 position);

/// <summary>
/// A bounded, in-process record of where each account was last seen, keyed by account id, so a REJOINING player's
/// entity can be built where they left instead of at the configured spawn.
///
/// <para>This exists because the resume snapshot is what a client measures (see
/// <see cref="WorldClient.LocalTeleported"/>): a server that spawns the rejoiner at its configured spawn and
/// restores the stored position afterwards serves one snapshot at the wrong place first, which the client can only
/// read as a teleport. Seeding the join from this cache makes the FIRST snapshot already correct, so the restore
/// that follows has nothing left to move (#642).</para>
///
/// <para>It is a HINT and deliberately not a second source of truth. The stored record still loads asynchronously
/// and still wins: when the two disagree (another process wrote the record, or the hint is stale) the load applies
/// the stored position over the seeded one, which is exactly the behaviour a server with no hints at all has.
/// Nothing here is ever persisted, so a restarted process starts empty and every rejoin falls back to the
/// configured spawn until the account has been seen again. A game that wants the cross-restart case covered can
/// pre-warm this from its own store at boot (see <see cref="WorldPersistence.ResumeHints"/>).</para>
///
/// <para>Bounded by <see cref="Capacity"/>, least-recently-recorded evicted first, so a long-running server that
/// has seen a million accounts holds a fixed number of them. A capacity of zero (or less) holds nothing, which is
/// how a game opts the whole mechanism out.</para>
///
/// <para><b>Not thread-safe.</b> Both engine call sites (the join read, and the record on leave / on a restore
/// apply) run on the server thread. Pre-warm it before the server starts polling, or from that same thread.</para>
/// </summary>
public sealed class ResumePositionCache
{
    private readonly Dictionary<string, LinkedListNode<Entry>> byAccount;
    // Recency order, least-recently-recorded at the head: the eviction end. Recording a known account moves its
    // node to the tail rather than allocating a new one, so a busy account never ages out under an idle one.
    private readonly LinkedList<Entry> order = new();

    private sealed class Entry
    {
        public Entry(string accountId, Vector3 position) { AccountId = accountId; Position = position; }
        public string AccountId { get; }
        public Vector3 Position { get; set; }
    }

    /// <summary>Creates a cache holding at most <paramref name="capacity"/> accounts (default 1024). Zero or less
    /// holds nothing at all, which turns the resume-spawn seed off for a server wired to this cache.</summary>
    public ResumePositionCache(int capacity = 1024)
    {
        Capacity = capacity;
        byAccount = new Dictionary<string, LinkedListNode<Entry>>(Math.Max(0, Math.Min(capacity, 64)), StringComparer.Ordinal);
    }

    /// <summary>The maximum number of accounts held. Zero or less holds nothing.</summary>
    public int Capacity { get; }

    /// <summary>How many accounts are currently held.</summary>
    public int Count => byAccount.Count;

    /// <summary>Records (or refreshes) an account's last known ABSOLUTE world position, evicting the
    /// least-recently-recorded account when that would exceed <see cref="Capacity"/>. No-op for a null or empty
    /// account id, and for a cache whose capacity is zero or less.</summary>
    public void Record(string accountId, Vector3 position)
    {
        if (Capacity <= 0 || string.IsNullOrEmpty(accountId)) return;
        if (byAccount.TryGetValue(accountId, out LinkedListNode<Entry>? existing))
        {
            existing.Value.Position = position;
            order.Remove(existing);
            order.AddLast(existing);
            return;
        }
        while (byAccount.Count >= Capacity && order.First is { } oldest)
        {
            order.RemoveFirst();
            byAccount.Remove(oldest.Value.AccountId);
        }
        byAccount[accountId] = order.AddLast(new Entry(accountId, position));
    }

    /// <summary>The account's last known ABSOLUTE world position, if it is still held. Reading does NOT refresh
    /// recency: a join reads once and the leave that follows records again, so counting the read too would keep an
    /// account that never came back alive on a single failed connect.</summary>
    public bool TryGet(string accountId, out Vector3 position)
    {
        if (!string.IsNullOrEmpty(accountId) && byAccount.TryGetValue(accountId, out LinkedListNode<Entry>? node))
        {
            position = node.Value.Position;
            return true;
        }
        position = default;
        return false;
    }

    /// <summary>Drops an account's hint (a character deletion, a forced return-to-spawn). The next join for it
    /// falls back to the configured spawn. No-op for an unknown account.</summary>
    public bool Forget(string accountId)
    {
        if (string.IsNullOrEmpty(accountId) || !byAccount.TryGetValue(accountId, out LinkedListNode<Entry>? node)) return false;
        order.Remove(node);
        byAccount.Remove(accountId);
        return true;
    }

    /// <summary>Drops every hint.</summary>
    public void Clear()
    {
        byAccount.Clear();
        order.Clear();
    }
}
