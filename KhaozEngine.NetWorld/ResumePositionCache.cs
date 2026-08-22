using System;
using System.Numerics;
using KhaozEngine.WorldStore;

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
/// <para>Structurally identical to <see cref="PositionHintProvider"/>, which is what the record-agnostic core seam
/// speaks. This one keeps its own name because every head in this package implements it and a game may have written
/// one, so it stays the NetWorld spelling and <see cref="IWorldPersistenceHost"/> bridges the two.</para>
/// </summary>
/// <param name="accountId">The joining connection's account id (the same key persistence stores under).</param>
/// <param name="position">The player's last known ABSOLUTE world position when the call returns true.</param>
public delegate bool ResumePositionProvider(string accountId, out Vector3 position);

/// <summary>
/// A bounded, in-process record of where each account was last seen, keyed by account id, so a REJOINING player's
/// entity can be built where they left instead of at the configured spawn.
///
/// <para>The mechanism itself now lives in <see cref="PositionHintCache"/>, which is shared with every other
/// movement stack in the fleet, and this type is a thin forwarder over one of those. It keeps its own name and its
/// exact surface because it is what <see cref="WorldPersistence.ResumeHints"/> hands out and what a game pre-warms
/// at boot. Read <see cref="PositionHintCache"/> for the behaviour: the recency bound, why a guest key is refused
/// outright, and why a hint is deliberately not a second source of truth.</para>
///
/// <para><b>Not thread-safe.</b> Both engine call sites (the join read, and the record on leave / on a restore
/// apply) run on the server thread. Pre-warm it before the server starts polling, or from that same thread.</para>
/// </summary>
public sealed class ResumePositionCache
{
    private readonly PositionHintCache inner;

    /// <summary>The account-id prefix both server heads key a TOKENLESS connection under (<c>guest:{slot}</c>).
    /// This cache holds nothing under it and answers nothing for it: the slot in that key is recycled to the next
    /// connection, so it identifies a seat rather than a player (see <see cref="PositionHintCache"/>).</summary>
    public const string GuestAccountPrefix = PositionHintCache.GuestAccountPrefix;

    /// <summary>Whether <paramref name="accountId"/> is a TOKENLESS connection's key, i.e. it carries
    /// <see cref="GuestAccountPrefix"/>. It is the PREFIX that decides, not the word: an account genuinely named
    /// <c>guest</c> is an account.</summary>
    public static bool IsGuestAccount(string accountId) => PositionHintCache.IsGuestAccount(accountId);

    /// <summary>Creates a cache holding at most <paramref name="capacity"/> accounts (default 1024). Zero or less
    /// holds nothing at all, which turns the resume-spawn seed off for a server wired to this cache.</summary>
    public ResumePositionCache(int capacity = 1024) : this(new PositionHintCache(capacity))
    {
    }

    // Wraps a cache the persistence core already owns, so WorldPersistence.ResumeHints and the hints the core reads
    // and writes are ONE cache rather than two that agree by accident. Internal because the core's cache is an
    // implementation detail out here.
    internal ResumePositionCache(PositionHintCache inner) => this.inner = inner;

    /// <summary>The maximum number of accounts held. Zero or less holds nothing.</summary>
    public int Capacity => inner.Capacity;

    /// <summary>How many accounts are currently held.</summary>
    public int Count => inner.Count;

    /// <summary>Records (or refreshes) an account's last known ABSOLUTE world position, evicting the
    /// least-recently-recorded account when that would exceed <see cref="Capacity"/>. No-op for a null or empty
    /// account id, for a <see cref="GuestAccountPrefix"/> key, and for a cache whose capacity is zero or
    /// less.</summary>
    public void Record(string accountId, Vector3 position) => inner.Record(accountId, position);

    /// <summary>The account's last known ABSOLUTE world position, if it is still held. Always false for a
    /// <see cref="GuestAccountPrefix"/> key. Reading does NOT refresh recency: a join reads once and the leave that
    /// follows records again, so counting the read too would keep an account that never came back alive on a single
    /// failed connect.</summary>
    public bool TryGet(string accountId, out Vector3 position) => inner.TryGet(accountId, out position);

    /// <summary>Drops an account's hint (a character deletion, a forced return-to-spawn). The next join for it
    /// falls back to the configured spawn. No-op for an unknown account.</summary>
    public bool Forget(string accountId) => inner.Forget(accountId);

    /// <summary>Drops every hint.</summary>
    public void Clear() => inner.Clear();
}
