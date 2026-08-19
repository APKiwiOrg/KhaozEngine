using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// A TOKENLESS connection is not persisted under the seat it happens to be sitting in (#647). Both heads key one
/// <c>guest:{slot}</c>, and both hand a freed slot straight to the next connection, so that key names a chair rather
/// than a player: the record a guest wrote on its way out loaded onto whoever took the seat next and moved them, as a
/// teleport, onto a stranger's last position.
///
/// <para>The half #642 introduced was already closed by <see cref="ResumePositionCache"/> refusing the prefix outright,
/// so a guest join was never BUILT on the last occupant's position. What remained was the persistence keying itself,
/// which predates all of it, and the fix is that a tokenless connection is not persisted at all: no load-on-join, no
/// save-on-leave, no periodic pass, and the configured spawn every time. A game that runs tokenless by design opts back
/// in with <see cref="WorldPersistenceConfig.PersistGuests"/>, which files each guest under a durable key minted for
/// that one session and never under the seat.</para>
///
/// <para>The rows drive the real loopback stack (an <see cref="InMemoryHub"/>, a <see cref="WorldServer"/> and a
/// <see cref="WorldPersistence"/> over an <see cref="InMemoryWorldStore"/>), because the seat-shaped key is derived by
/// the HEAD and the whole question is what the persistence layer then does with it. A synchronous store is enough:
/// nothing here depends on the in-flight window that <see cref="WorldPersistenceSlotRecycleTests"/> needs a gate for.
/// The sharded head derives the identical key on the identical line and shares this code path outright, so it is
/// covered by <see cref="WorldPersistenceSlotRecycleTests"/> rather than duplicated here.</para>
/// </summary>
public class WorldPersistenceGuestKeyingTests
{
    private static float Flat(float x, float z) => 0f;

    private const float Dt = 1f / 30f;

    // What the record a guest used to leave behind was filed under, and the one key no row here may ever see.
    private const string SeatKey = "player:" + ResumePositionCache.GuestAccountPrefix + "0";
    private const string GuestKeyPrefix = "player:" + ResumePositionCache.GuestAccountPrefix;

    // Where a guest is walked before it drops: 500 m off the spawn, so a successor that inherited the record would
    // be somewhere no fresh spawn could put it.
    private static readonly Vector3 Parked = new(400f, 0.9f, 300f);

    private static readonly Vector3 Spawn = new(0f, MoveTuning.Default.CapsuleHalfHeight, 0f);

    private sealed class Rig
    {
        private readonly List<NetClient> peers = new();

        public required InMemoryHub Hub { get; init; }
        public required InMemoryWorldStore Store { get; init; }
        public required WorldPersistence Persistence { get; init; }
        public required WorldServer Server { get; init; }

        public void Pump(Func<bool> until, int frames = 400)
        {
            for (int i = 0; i < frames && !until(); i++)
            {
                foreach (NetClient p in peers) p.Poll();
                Server.Poll();
                Server.Tick(Dt);
                Persistence.Update(Dt);
            }
        }

        /// <summary>Connects a TOKENLESS client and pumps until it holds slot 0, which every row needs: the seat has
        /// to be the recycled one for a successor to be able to inherit anything.</summary>
        public INetTransport ConnectGuest()
        {
            INetTransport t = Hub.CreateClient();
            var client = new NetClient(t, TestHandshake.Wire());
            peers.Add(client);
            Pump(() => client.Slot >= 0 && Server.JoinedSlots.Count == 1);
            Assert.Equal(0, client.Slot);
            Assert.Equal(ResumePositionCache.GuestAccountPrefix + "0", Account(0));
            return t;
        }

        public void Drop(INetTransport transport)
        {
            Hub.DisconnectClient(transport);
            Pump(() => Server.JoinedSlots.Count == 0);
            Assert.Empty(Server.JoinedSlots);
        }

        public string Account(int slot)
        {
            Assert.True(Server.TryGetAccountId(slot, out string a), $"slot {slot} has no account");
            return a;
        }

        public Vector3 Position(int slot)
        {
            Assert.True(Server.TryGetPlayerState(slot, out PlayerMoveState s), $"slot {slot} has no live player");
            return s.Position;
        }

        /// <summary>Walks the seated guest 500 m off the spawn and lets the move settle, so its leave has something
        /// worth persisting and a successor inheriting it would be unmistakable.</summary>
        public void Park(int slot)
        {
            Server.Teleport(PlayerRef.Slot(slot), Parked);
            Pump(() => false, 20);
            Assert.True(Vector3.Distance(Position(slot), Parked) < 0.5f,
                $"the park did not take, so the row proves nothing ({Position(slot)})");
        }

        /// <summary>Settles the layer, then lets a load-on-join apply if one was ever issued. Long enough that a row
        /// asserting nothing was restored is asserting on a settled server rather than on a slow one.</summary>
        public async Task SettleAsync()
        {
            Pump(() => false, 20);
            await Persistence.FlushAsync();
            Persistence.Update(0f);
        }

        /// <summary>Every key currently in the store.</summary>
        public async Task<List<string>> KeysAsync()
        {
            var keys = new List<string>();
            await foreach (WorldStoreEntry e in Store.EnumerateAsync()) keys.Add(e.Key);
            return keys;
        }
    }

    private static Rig Build(WorldPersistenceConfig? pcfg = null)
    {
        var hub = new InMemoryHub();
        var store = new InMemoryWorldStore();
        var config = new WorldServerConfig
        {
            TickSeconds = Dt,
            InterestRadius = 500f,
            MaxPlayers = 8,
            SpawnPosition = _ => Vector3.Zero,
        };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var persistence = new WorldPersistence(server, store,
            pcfg ?? new WorldPersistenceConfig { SaveIntervalSeconds = 999f });
        return new Rig { Hub = hub, Store = store, Persistence = persistence, Server = server };
    }

    // ---- (1) the defect itself: one guest's record landing on the next guest to take the seat ----

    [Fact]
    public async Task A_guest_on_a_recycled_seat_does_not_inherit_the_last_guests_record()
    {
        Rig rig = Build();

        INetTransport first = rig.ConnectGuest();
        rig.Park(0);
        rig.Drop(first);
        await rig.Persistence.FlushAsync();          // any save-on-leave has landed before the successor connects

        INetTransport second = rig.ConnectGuest();   // the same seat, a different person
        await rig.SettleAsync();

        Assert.True(Vector3.Distance(rig.Position(0), Spawn) < 0.5f,
            $"the second guest was moved onto the first one's stored position ({rig.Position(0)})");
        Assert.DoesNotContain(SeatKey, await rig.KeysAsync());
    }

    // ---- (2) nothing is written under a seat-shaped key in the first place ----

    [Fact]
    public async Task Neither_save_path_files_a_tokenless_connection_under_its_seat()
    {
        Rig rig = Build();
        INetTransport guest = rig.ConnectGuest();
        rig.Park(0);

        rig.Persistence.SaveDirtyPass();             // the periodic pass, forced: it skips a tokenless connection too
        await rig.Persistence.FlushAsync();
        Assert.Empty(await rig.KeysAsync());

        rig.Drop(guest);                             // and so does save-on-leave
        await rig.Persistence.FlushAsync();

        List<string> keys = await rig.KeysAsync();
        Assert.DoesNotContain(keys, k => k.StartsWith(GuestKeyPrefix, StringComparison.Ordinal));
        Assert.Empty(keys);                          // a guest-only server writes no player records at all
    }

    // ---- (3) the opt-in: persisted, but under a durable key rather than a chair ----

    [Fact]
    public async Task PersistGuests_files_each_guest_session_under_its_own_durable_key()
    {
        Rig rig = Build(new WorldPersistenceConfig { SaveIntervalSeconds = 999f, PersistGuests = true });

        INetTransport first = rig.ConnectGuest();
        rig.Park(0);
        rig.Drop(first);
        await rig.Persistence.FlushAsync();

        string key = Assert.Single(await rig.KeysAsync());
        Assert.StartsWith(GuestKeyPrefix, key, StringComparison.Ordinal);
        Assert.NotEqual(SeatKey, key);               // durable, and never the chair the guest happened to sit in

        // The successor mints its own key and still starts on the configured spawn. Opting in buys crash-safety
        // within a session and an audit trail, never a guest's return: nothing can ever present the minted id again.
        INetTransport second = rig.ConnectGuest();
        await rig.SettleAsync();
        Assert.True(Vector3.Distance(rig.Position(0), Spawn) < 0.5f,
            $"an opted-in guest still starts fresh on the configured spawn ({rig.Position(0)})");

        rig.Park(0);
        rig.Drop(second);
        await rig.Persistence.FlushAsync();

        List<string> both = await rig.KeysAsync();
        Assert.Equal(2, both.Count);                 // two sessions, two records, and neither of them is the seat
        Assert.DoesNotContain(SeatKey, both);
    }
}
