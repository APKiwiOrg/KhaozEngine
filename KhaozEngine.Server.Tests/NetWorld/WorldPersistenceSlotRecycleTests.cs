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
/// <para>The HAPPY path (the account is still on the slot, so the load applies and moves it to the stored position)
/// is already pinned and is not duplicated here: <see cref="WorldPersistenceTests.LoadOnJoin_RestoresSavedPosition"/>
/// for the plain case, <see cref="WorldPersistenceTests.PeriodicSnapshot_DuringInFlightLoad_DoesNotClobberStoredRecord"/>
/// for the same gated in-flight window this class uses, and
/// <see cref="WorldPersistenceReconnectTeleportTests.A_stationary_rejoin_on_a_persisted_server_reports_no_teleport"/>
/// for the seeded rejoin. <see cref="A_rejoin_by_the_same_account_before_its_first_load_lands_applies_both_loads"/>
/// re-proves it on this rig too, which is what stops the identity check quietly dropping everything.</para>
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
    }

    // A persistence-backed single-World head on a gated store, with alpha's record pre-seeded.
    private static async Task<Rig> SingleAsync()
    {
        var inner = new InMemoryWorldStore();
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
        // own spawn. What it does NOT cover is guest FOLLOWING guest on one slot, where both connections share the
        // key guest:{slot} and are indistinguishable here. That is #647, untouched, and still fixable by giving a
        // tokenless connection durable identity (or by not persisting one at all): this check compares whatever key
        // the head derived, so it keeps working whichever way #647 lands.
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

    [Fact]
    public async Task A_rejoin_by_the_same_account_before_its_first_load_lands_applies_both_loads()
    {
        // Pins what actually happens rather than what ought to: loadsInFlight is a SET keyed by account, not a
        // refcount and not a session, so a rejoin during an in-flight load starts a SECOND load under the same key
        // and both of them apply. Here that is benign (same account, same record, and the identity check passes
        // because alpha really does hold the slot again), and it is the row that proves the drop rule is not just
        // dropping everything. The residual it leaves is the guard: the FIRST apply clears it while the second load
        // is still outstanding, so a save can land in between and the late apply then writes over it. That is out of
        // scope here and filed as #654, which is the issue to change this row from.
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

        Assert.Empty(rig.Dropped);                                // alpha holds the slot, so neither load is a stranger's
        Assert.Equal(new[] { Alpha, Alpha }, rig.Applied);        // both loads applied, the second did not supersede the first
        Assert.Equal(Stored, rig.State(0).Position);              // and the restore itself still works
    }
}
