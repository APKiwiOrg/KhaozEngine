using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// A completed load-on-join belongs to an ACCOUNT, not to the slot number it was issued for (#646). Both heads hand
/// the lowest free slot to the next connection, so an account that drops while its store read is still in flight
/// frees its seat immediately and a stranger can be sitting in it by the time the record lands. Applying it there
/// wrote the first account's position, teleport and durable blob onto the second player, and since 17.37.0 it could
/// do that SILENTLY: the quiet-restore window is measured against the live state, which on a recycled slot is the new
/// occupant's, so two players standing close enough to each other made the misapplication land with no epoch advance
/// at all.
///
/// <para>The rows below drive the real loopback stack (an <see cref="InMemoryHub"/>, a <see cref="WorldServer"/> or
/// <see cref="ShardedWorldServer"/>, and a <see cref="WorldPersistence"/> over a <see cref="GatedWorldStore"/>) and
/// hold the load at the gate across the whole leave/join, which is the exact window a genuinely remote store opens
/// and a synchronous <see cref="InMemoryWorldStore"/> cannot.</para>
///
/// <para>The later rows carry #654, which is the same question one step in: a SESSION, not an account, is what a
/// completed load belongs to. An account that drops and rejoins before its store read returns has TWO loads
/// outstanding under one key, and the one issued by the session that ended must not apply over whatever the live
/// session has done since, nor clear the guard that is holding the periodic pass off the stored record while its
/// sibling is still in flight.</para>
///
/// <para>The HAPPY path (the account is still on the slot, so the load applies and moves it to the stored position)
/// is already pinned and is not duplicated here: <see cref="WorldPersistenceTests.LoadOnJoin_RestoresSavedPosition"/>
/// for the plain case, <see cref="WorldPersistenceTests.PeriodicSnapshot_DuringInFlightLoad_DoesNotClobberStoredRecord"/>
/// for the same gated in-flight window this class uses, and
/// <see cref="WorldPersistenceReconnectTeleportTests.A_stationary_rejoin_on_a_persisted_server_reports_no_teleport"/>
/// for the seeded rejoin. <see cref="A_rejoin_before_its_first_load_lands_applies_only_the_current_sessions_load"/>
/// re-proves it on this rig too, which is what stops the identity checks quietly dropping everything.</para>
/// </summary>
public class WorldPersistenceSlotRecycleTests
{
    private static float Flat(float x, float z) => 0f;

    private const float Dt = 1f / 30f;
    private const string Alpha = "alpha";
    private const string Beta = "beta";

    // Where alpha's stored record says it is: far enough from the spawn that a misapplication is unmistakable, and
    // the position the reviewer's probe used.
    private static readonly Vector3 Stored = new(333f, 0f, -333f);

    // Where a #654 row walks the LIVE player after its restore has already landed, so a stale load arriving
    // afterwards has something newer to overwrite. Far from both the spawn and Stored.
    private static readonly Vector3 Moved = new(120f, 0f, 120f);

    // Horizontal distance only: the head ground-clamps Y on every tick, so a row that pumps frames between placing a
    // player and reading it back would otherwise be comparing against a capsule half-height it never set.
    private static float PlanarDistance(Vector3 a, Vector3 b) =>
        Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));

    private sealed record Peer(NetClient Client, INetTransport Transport);

    private sealed class Rig
    {
        public required InMemoryHub Hub { get; init; }
        public required GatedWorldStore Store { get; init; }
        public required InMemoryWorldStore Inner { get; init; }
        public required WorldPersistence Persistence { get; init; }
        /// <summary>The server head under test, through the seam persistence itself drives.</summary>
        public required IWorldPersistenceHost Host { get; init; }
        /// <summary>One server frame: poll, tick (which serves), then the persistence drain.</summary>
        public required Action Step { get; init; }

        public List<Peer> Peers { get; } = new();
        /// <summary>Every (accountId, slot) the layer reported dropping, in order.</summary>
        public List<(string Account, int Slot)> Dropped { get; } = new();
        /// <summary>Accounts whose durable blob was re-applied, one entry per apply.</summary>
        public List<string> Applied { get; } = new();

        /// <summary>Connects a client. A null account is a TOKENLESS connection, which both heads key
        /// <c>guest:{slot}</c>.</summary>
        public Peer Connect(string? account)
        {
            INetTransport t = Hub.CreateClient();
            var peer = new Peer(new NetClient(t, account is null ? TestHandshake.Wire() : TestHandshake.Wire(account)), t);
            Peers.Add(peer);
            return peer;
        }

        public void Drop(Peer peer) => Hub.DisconnectClient(peer.Transport);

        public void Pump(Func<bool> until, int frames = 400)
        {
            for (int i = 0; i < frames && !until(); i++)
            {
                foreach (Peer p in Peers) p.Client.Poll();
                Step();
            }
        }

        public PlayerMoveState State(int slot)
        {
            Assert.True(Host.TryGetPlayerState(slot, out PlayerMoveState s), $"slot {slot} has no live player");
            return s;
        }

        public string Account(int slot)
        {
            Assert.True(Host.TryGetAccountId(slot, out string a), $"slot {slot} has no account");
            return a;
        }

        /// <summary>Releases every parked load and settles it, applying (or dropping) on the server thread.</summary>
        public async Task SettleLoadsAsync()
        {
            Store.ReleaseLoads();
            await Persistence.FlushAsync();
            Persistence.Update(0f);
        }

        /// <summary>Releases exactly ONE parked load and settles it, so a row can drive the order two loads
        /// outstanding under one account land in. <see cref="SettleLoadsAsync"/> cannot do this: its FlushAsync would
        /// await the load still parked at the gate. The released load's continuation runs on the thread pool, so this
        /// YIELDS between server frames rather than spinning a fixed frame count - under the fully parallel assembly
        /// the pool routinely takes longer to reach that work item than 400 tight frames take to run, which is a flake
        /// and not a failure.</summary>
        public async Task ReleaseOneAsync(bool oldest, Func<bool> until)
        {
            Assert.True(Store.ReleaseOneLoad(oldest), "no load was parked to release");
            for (int i = 0; i < 500 && !until(); i++)
            {
                Pump(until, 1);
                await Task.Delay(1);
            }
            Assert.True(until(), "the released load never landed: nothing new reached the apply drain");
        }
    }

    // A persistence-backed single-World head on a gated store, with alpha's record pre-seeded. Pass seeded: false
    // for the rows that need alpha's load to come back with NOTHING stored, which is a different code path: it never
    // reaches the apply drain, and clears the guard inline instead.
    private static async Task<Rig> SingleAsync(bool seeded = true)
    {
        var inner = new InMemoryWorldStore();
        if (seeded)
            await inner.SaveAsync("player:" + Alpha, PlayerRecord.From(new PlayerMoveState { Position = Stored },
                Encoding.UTF8.GetBytes("alpha-blob")).Encode());
        var store = new GatedWorldStore(inner);
        var hub = new InMemoryHub();
        var config = new WorldServerConfig
        {
            TickSeconds = Dt,
            InterestRadius = 500f,
            MaxPlayers = 8,
            SpawnPosition = _ => Vector3.Zero,
        };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        return Build(hub, store, inner, server, () => { server.Poll(); server.Tick(Dt); });
    }

    // The multi-cell twin: the same wiring against the sharded head, which implements the same seam.
    private static async Task<Rig> ShardedAsync()
    {
        var inner = new InMemoryWorldStore();
        await inner.SaveAsync("player:" + Alpha, PlayerRecord.From(new PlayerMoveState { Position = Stored },
            Encoding.UTF8.GetBytes("alpha-blob")).Encode());
        var store = new GatedWorldStore(inner);
        var hub = new InMemoryHub();
        var config = new ShardedWorldServerConfig
        {
            TickSeconds = Dt,
            CellSize = 60f,
            OverlapMargin = 24f,
            InterestRadius = 24f,
            MaxPlayers = 8,
            SpawnPosition = _ => Vector3.Zero,
        };
        var server = new ShardedWorldServer(hub.Server, config, Flat, MoveTuning.Default);
        return Build(hub, store, inner, server, () => { server.Poll(); server.Tick(Dt); });
    }

    private static Rig Build(InMemoryHub hub, GatedWorldStore store, InMemoryWorldStore inner,
        IWorldPersistenceHost host, Action tick)
    {
        Rig rig = null!;
        var persistence = new WorldPersistence(host, store, new WorldPersistenceConfig
        {
            SaveIntervalSeconds = 999f,   // no periodic pass can touch a record while a row is holding a load
            ApplyGameState = (in PlayerPersistenceContext ctx, ReadOnlySpan<byte> blob) => rig.Applied.Add(ctx.AccountId),
        });
        rig = new Rig
        {
            Hub = hub,
            Store = store,
            Inner = inner,
            Persistence = persistence,
            Host = host,
            Step = () => { tick(); persistence.Update(Dt); },
        };
        persistence.OnLoadApplyDropped += (acct, slot) => rig.Dropped.Add((acct, slot));
        return rig;
    }

    // Alpha joins with its load parked, drops, and the next connection recycles its slot. Asserts on that new
    // occupant, on alpha's untouched record, and on the drop being announced rather than silent.
    private static async Task RecycledSlotDoesNotTakeAlphasLoad(Rig rig, string? successor)
    {
        byte[] seeded = (await rig.Inner.LoadAsync("player:" + Alpha))!;

        Peer alpha = rig.Connect(Alpha);
        rig.Pump(() => alpha.Client.Slot >= 0 && rig.Host.JoinedSlots.Count == 1);
        Assert.Equal(0, alpha.Client.Slot);                       // the seat the successor has to recycle
        Assert.Equal(1, rig.Store.PendingLoads);                  // alpha's load really is parked mid-flight

        rig.Drop(alpha);
        rig.Pump(() => rig.Host.JoinedSlots.Count == 0);
        Assert.Empty(rig.Host.JoinedSlots);                       // slot 0 is free, and alpha's load is still in flight

        Peer next = rig.Connect(successor);
        rig.Pump(() => next.Client.Slot >= 0 && rig.Host.JoinedSlots.Count == 1);
        Assert.Equal(0, next.Client.Slot);                        // the recycle this whole issue depends on
        string occupant = rig.Account(0);
        Assert.Equal(successor ?? ResumePositionCache.GuestAccountPrefix + "0", occupant);

        PlayerMoveState before = rig.State(0);
        Assert.True(Vector3.Distance(before.Position, Stored) > 1f,
            $"the successor must not already be on alpha's stored position for this row to prove anything ({before.Position})");

        await rig.SettleLoadsAsync();

        PlayerMoveState after = rig.State(0);
        Assert.Equal(before.Position, after.Position);            // was Stored: alpha's record landed on the successor
        Assert.Equal(before.TeleportEpoch, after.TeleportEpoch);  // and since 17.37.0 it could land without even this
        Assert.DoesNotContain(occupant, rig.Applied);             // nor did the successor inherit alpha's durable blob

        Assert.Equal(new[] { (Alpha, 0) }, rig.Dropped);          // announced, not a silent continue
        Assert.DoesNotContain("player:" + Alpha, rig.Store.SavedKeys);
        Assert.Equal(seeded, await rig.Inner.LoadAsync("player:" + Alpha));   // alpha's record is byte-identical
    }

    // ---- (1) the reviewer's reproduction: a second ACCOUNT recycles the slot ----

    [Fact]
    public async Task A_recycled_slot_does_not_take_the_previous_accounts_load()
    {
        Rig rig = await SingleAsync();
        await RecycledSlotDoesNotTakeAlphasLoad(rig, Beta);
    }

    // ---- (2) the same, with a tokenless GUEST recycling the slot ----

    [Fact]
    public async Task A_guest_recycling_a_slot_does_not_take_the_previous_accounts_load()
    {
        // A guest has no account id to compare, which is the whole question this row settles: it is keyed
        // guest:{slot}, and that key is not alpha's, so the same comparison drops the record and the guest keeps its
        // own spawn. Guest FOLLOWING guest on one seat, where both connections share the key guest:{slot} and are
        // indistinguishable here, was the separate keying defect (#647) and is covered by
        // WorldPersistenceGuestKeyingTests: a tokenless connection is not persisted under its seat at all now. This
        // check still compares whatever key the head derived, which is why it kept working across that change.
        Rig rig = await SingleAsync();
        await RecycledSlotDoesNotTakeAlphasLoad(rig, null);
    }

    // ---- (3) the sharded head, which implements the same seam ----

    [Fact]
    public async Task A_recycled_slot_on_a_sharded_server_does_not_take_the_previous_accounts_load()
    {
        Rig rig = await ShardedAsync();
        await RecycledSlotDoesNotTakeAlphasLoad(rig, Beta);
    }

    // ---- (4) the same ACCOUNT rejoining before its first load lands ----

    // Drives alpha to two loads outstanding under one account key: it joins with its load parked, drops, and rejoins
    // before that read returns. Only the CURRENT session's load may apply. The other one was issued for a session
    // that has ended, so applying it writes a record that was already superseded over whatever the live session has
    // done since, and clearing the guard on the way out reopens the exact window the guard exists to close (#654).
    private static async Task OnlyTheCurrentSessionsLoadApplies(Rig rig, bool staleLandsFirst)
    {
        Peer first = rig.Connect(Alpha);
        rig.Pump(() => first.Client.Slot >= 0 && rig.Host.JoinedSlots.Count == 1);
        Assert.Equal(0, first.Client.Slot);
        Assert.Equal(1, rig.Store.PendingLoads);

        rig.Drop(first);
        rig.Pump(() => rig.Host.JoinedSlots.Count == 0);

        Peer again = rig.Connect(Alpha);
        rig.Pump(() => again.Client.Slot >= 0 && rig.Host.JoinedSlots.Count == 1);
        Assert.Equal(0, again.Client.Slot);                       // the same account recycles its own seat
        Assert.Equal(2, rig.Store.PendingLoads);                  // TWO reads outstanding under ONE account key

        if (staleLandsFirst)
        {
            // The superseded session's read returns first. It is dropped, and the guard it must NOT clear is what
            // keeps the periodic pass off the stored record for the rest of the window.
            await rig.ReleaseOneAsync(oldest: true, () => rig.Dropped.Count == 1);
            Assert.Equal(new[] { (Alpha, 0) }, rig.Dropped);
            Assert.Empty(rig.Applied);                            // no part of the stale record reached the player

            rig.Persistence.SaveDirtyPass();                      // the periodic pass, forced into the open window
            Assert.DoesNotContain("player:" + Alpha, rig.Store.SavedKeys);   // still guarded: nothing overwrote the record

            await rig.ReleaseOneAsync(oldest: false, () => rig.Applied.Count == 1);
            Assert.Equal(new[] { Alpha }, rig.Applied);           // the live session's own load, applied once
            Assert.True(PlanarDistance(rig.State(0).Position, Stored) < 1f,
                $"the live session's restore should have landed ({rig.State(0).Position})");
        }
        else
        {
            // The live session's read returns first and applies. Then the player MOVES, and the superseded session's
            // read arrives afterwards carrying what the store held before any of it happened.
            await rig.ReleaseOneAsync(oldest: false, () => rig.Applied.Count == 1);
            Assert.Empty(rig.Dropped);
            Assert.True(PlanarDistance(rig.State(0).Position, Stored) < 1f,
                $"the live session's restore should have landed ({rig.State(0).Position})");

            MoveLivePlayerTo(rig, Moved);

            await rig.ReleaseOneAsync(oldest: true, () => rig.Dropped.Count == 1);
            Assert.Equal(new[] { (Alpha, 0) }, rig.Dropped);
            Assert.Equal(new[] { Alpha }, rig.Applied);           // and the stale durable blob was not re-applied
            Assert.True(PlanarDistance(rig.State(0).Position, Moved) < 1f,
                $"a stale load pulled the live player back onto its own record ({rig.State(0).Position})");
        }

        Assert.DoesNotContain("player:" + Alpha, rig.Store.SavedKeys);   // no row here ever wanted a write
    }

    // Walks the live player through the same seam persistence writes through, then lets the head settle it (the
    // sharded twin has to hand the entity to whichever cell now contains it).
    private static void MoveLivePlayerTo(Rig rig, Vector3 to)
    {
        rig.Host.SetPlayerState(0, new PlayerMoveState { Position = to }, teleport: true);
        rig.Pump(() => false, 30);
        Assert.True(PlanarDistance(rig.State(0).Position, to) < 1f,
            $"the move did not take, so the row that follows proves nothing ({rig.State(0).Position})");
    }

    [Fact]
    public async Task A_superseded_sessions_load_landing_first_is_dropped_and_keeps_the_guard()
    {
        Rig rig = await SingleAsync();
        await OnlyTheCurrentSessionsLoadApplies(rig, staleLandsFirst: true);
    }

    [Fact]
    public async Task A_superseded_sessions_load_landing_last_does_not_overwrite_newer_live_state()
    {
        Rig rig = await SingleAsync();
        await OnlyTheCurrentSessionsLoadApplies(rig, staleLandsFirst: false);
    }

    [Fact]
    public async Task A_superseded_sessions_load_landing_first_on_a_sharded_server_is_dropped()
    {
        Rig rig = await ShardedAsync();
        await OnlyTheCurrentSessionsLoadApplies(rig, staleLandsFirst: true);
    }

    [Fact]
    public async Task A_superseded_sessions_load_landing_last_on_a_sharded_server_is_dropped()
    {
        Rig rig = await ShardedAsync();
        await OnlyTheCurrentSessionsLoadApplies(rig, staleLandsFirst: false);
    }

    [Fact]
    public async Task A_rejoin_before_its_first_load_lands_applies_only_the_current_sessions_load()
    {
        // The order-free twin of the four rows above, and the one that proves the drop rule is not simply dropping
        // everything: whichever of the two loads the thread pool happens to land first, exactly one apply happens
        // (the live session's) and exactly one drop (the session that ended). Was pinned the other way until #654 -
        // loadsInFlight was a SET keyed by account, not a session, so both loads applied and the first of them
        // cleared the guard while its sibling was still outstanding.
        Rig rig = await SingleAsync();

        Peer first = rig.Connect(Alpha);
        rig.Pump(() => first.Client.Slot >= 0 && rig.Host.JoinedSlots.Count == 1);
        Assert.Equal(0, first.Client.Slot);
        Assert.Equal(1, rig.Store.PendingLoads);

        rig.Drop(first);
        rig.Pump(() => rig.Host.JoinedSlots.Count == 0);

        Peer again = rig.Connect(Alpha);
        rig.Pump(() => again.Client.Slot >= 0 && rig.Host.JoinedSlots.Count == 1);
        Assert.Equal(0, again.Client.Slot);                       // the same account recycles its own seat
        Assert.Equal(2, rig.Store.PendingLoads);                  // TWO loads in flight for ONE account

        await rig.SettleLoadsAsync();

        Assert.Equal(new[] { (Alpha, 0) }, rig.Dropped);          // was empty: the superseded session's load applied too
        Assert.Equal(new[] { Alpha }, rig.Applied);               // was [alpha, alpha]
        Assert.Equal(Stored, rig.State(0).Position);              // and the restore itself still works
    }

    // ---- (5) the same rule on the NULL-load path, which never reaches the drain ----

    [Fact]
    public async Task A_superseded_sessions_empty_load_does_not_unguard_the_live_session()
    {
        // The one window in which ClearGuard comparing the VALUE is what does the work. Every other clear runs
        // through the apply drain, which has already matched the token by the time it gets there, so a key-only
        // TryRemove would behave identically. A load that finds NOTHING stored never reaches the drain at all: it
        // clears the guard inline, from its own thread-pool continuation, and for an account that leaves and rejoins
        // inside that read the continuation lands AFTER the successor's join has written its own token. Keyed by
        // account alone it takes the LIVE session's guard with it, and the periodic pass then writes the pre-restore
        // state the live session is still holding while its own read is unanswered. The row pins the invariant (a
        // guard belongs to the session that set it) rather than a data loss: with nothing stored under the key yet
        // there is no record for that write to destroy, which is exactly why this path is easy to leave uncovered.
        Rig rig = await SingleAsync(seeded: false);

        Peer first = rig.Connect(Alpha);
        rig.Pump(() => first.Client.Slot >= 0 && rig.Host.JoinedSlots.Count == 1);
        Assert.Equal(0, first.Client.Slot);
        Assert.Equal(1, rig.Store.PendingLoads);

        rig.Drop(first);
        rig.Pump(() => rig.Host.JoinedSlots.Count == 0);

        Peer again = rig.Connect(Alpha);
        rig.Pump(() => again.Client.Slot >= 0 && rig.Host.JoinedSlots.Count == 1);
        Assert.Equal(0, again.Client.Slot);
        Assert.Equal(2, rig.Store.PendingLoads);                   // two reads outstanding under one key, both of them empty

        // Release the superseded session's read. There is no apply and no drop to wait on, so wait for the read
        // itself to return and then SAMPLE the window repeatedly rather than probing it once: the inline clear runs
        // a continuation behind the read, and a single forced pass could otherwise land in front of the very thing
        // it is there to catch and pass for the wrong reason.
        Assert.True(rig.Store.ReleaseOneLoad(oldest: true), "no load was parked to release");
        for (int i = 0; i < 200 && rig.Store.CompletedLoads == 0; i++)
        {
            rig.Pump(() => false, 1);
            await Task.Delay(1);
        }
        Assert.Equal(1, rig.Store.CompletedLoads);                 // the superseded session's read really did return

        for (int i = 0; i < 60; i++)
        {
            rig.Persistence.SaveDirtyPass();                       // the periodic pass, forced into the window
            Assert.DoesNotContain("player:" + Alpha, rig.Store.SavedKeys);   // the live session's guard held
            rig.Pump(() => false, 1);
            await Task.Delay(1);
        }

        // And it is a guard rather than a wedge: the live session's own read clears it, and the next pass writes.
        await rig.SettleLoadsAsync();
        Assert.Empty(rig.Dropped);                                 // an empty load carries no record, so nothing is ever dropped
        rig.Persistence.SaveDirtyPass();
        await rig.Persistence.FlushAsync();
        Assert.Contains("player:" + Alpha, rig.Store.SavedKeys);
    }
}
