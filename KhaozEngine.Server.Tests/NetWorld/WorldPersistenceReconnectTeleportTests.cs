using System;
using System.Collections.Generic;
using System.Linq;
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
/// The reconnect teleport contract on a PERSISTENCE-BACKED server (#642), which is the half
/// <see cref="WorldClientReconnectTeleportTests"/> deliberately does not cover: its harness restores the departing
/// position synchronously as the next join's spawn, while the real <see cref="WorldPersistence"/> restores
/// asynchronously, after the rejoiner's first snapshot has already gone out at the configured spawn.
///
/// That window produced TWO teleports for anyone away from the spawn: the client reseeds onto the spawn (a
/// discontinuity of however far they were from it) and then takes the restore's teleport epoch advance as a second
/// one. It is the server-side cause of Ruinborne rebuilding its whole terrain ring on every reconnect
/// (https://github.com/APKiwiOrg/Ruinborne/issues/388), which #409's client-side fix could not reach.
///
/// The rows below drive the real loopback stack (a <see cref="WorldClient"/> with auto-reconnect, a
/// <see cref="WorldServer"/> or <see cref="ShardedWorldServer"/>, a <see cref="WorldPersistence"/> over an
/// <see cref="InMemoryWorldStore"/>) in the field's frame order: poll, tick (which SERVES), then
/// <see cref="WorldPersistence.Update"/> (which applies a completed load). A restore therefore always lands at
/// least one snapshot behind the join, exactly as it does on a server whose store is genuinely remote.
/// </summary>
public class WorldPersistenceReconnectTeleportTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    const float Dt = 1f / 30f;
    const string Account = "hero";
    const string RecordKey = "player:hero";

    // Where probe A parks the player: 500 m from the spawn, five times the client's HardSnapDistance, so a resume
    // snapshot that landed on the spawn instead would report a teleport as loudly as it can.
    static readonly Vector3 Parked = new(400f, 0.9f, 300f);

    static WorldClientConfig NewClientConfig() => new()
    {
        TickSeconds = Dt,
        DisconnectTimeoutSeconds = 0.5f,
        Reconnect = new ReconnectBackoff { InitialSeconds = 0.05f, Multiplier = 1f, MaxSeconds = 0.05f },
    };

    // The client's current transport endpoint, written by the connect factory on every attempt, so a test can drop
    // the live one. A holder rather than a Rig member because the factory runs inside the WorldClient constructor.
    sealed class Endpoint { public INetTransport? Live; }

    sealed class Rig
    {
        public required InMemoryHub Hub { get; init; }
        /// <summary>One server frame in the field's order: poll, tick (serves), persistence update (applies).</summary>
        public required Action Step { get; init; }
        public required WorldPersistence Persistence { get; init; }
        public required InMemoryWorldStore Store { get; init; }
        public required WorldClient Client { get; init; }
        public required Endpoint Endpoint { get; init; }
        /// <summary>The authoritative position each join BUILT its entity at, recorded from <c>PlayerJoined</c>
        /// before any restore could run. This is what the join's first snapshot carries.</summary>
        public required List<Vector3> BuiltAt { get; init; }
        public required Action<Vector3> TeleportOnlyPlayer { get; init; }

        /// <summary>The local position on the frame the client first came back with a net id: the RESUME SNAPSHOT,
        /// before any later snapshot could correct it.</summary>
        public Vector3 ResumeSnapshot { get; private set; }

        public void Frame(float dt = Dt)
        {
            Step();
            Client.Poll(dt);
            Client.AdvancePresentation(dt);
        }

        /// <summary>Drops the client's current transport endpoint, then pumps until it has rejoined, capturing the
        /// resume snapshot on the frame it does. False if it never came back.</summary>
        public bool DropAndReconnect()
        {
            Hub.DisconnectClient(Endpoint.Live!);
            bool sawReconnecting = false;
            for (int i = 0; i < 400; i++)
            {
                Frame(0.05f);
                if (Client.ConnectionState == WorldConnectionState.Reconnecting) sawReconnecting = true;
                if (sawReconnecting && Client.ConnectionState == WorldConnectionState.Connected && Client.LocalNetId > 0)
                {
                    ResumeSnapshot = LocalPosition();
                    return true;
                }
            }
            return false;
        }

        public Vector3 LocalPosition() => Client.Snapshot().Single(e => e.IsLocal).Position;

        public void Idle(int frames)
        {
            for (int i = 0; i < frames; i++) { Client.SendInput(MoveCommand.Idle); Frame(); }
        }
    }

    // A persistence-backed single-world server plus an auto-reconnecting client on the same hub, joined and ready.
    static Rig ConnectSingle(InMemoryWorldStore store, Func<int, Vector3>? spawn = null)
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig
        {
            TickSeconds = Dt,
            InterestRadius = 500f,
            MaxPlayers = 8,
            SpawnPosition = spawn ?? (_ => Vector3.Zero),
        };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        // A long interval: no periodic pass can overwrite a record a test injected during the disconnect window.
        var persistence = new WorldPersistence(server, store, new WorldPersistenceConfig { SaveIntervalSeconds = 999f });
        var builtAt = new List<Vector3>();
        server.PlayerJoined += (slot, _) =>
        {
            if (server.TryGetPlayerState(slot, out PlayerMoveState st)) builtAt.Add(st.Position);
        };
        var endpoint = new Endpoint();
        var client = new WorldClient(
            () => { INetTransport t = hub.CreateClient(); endpoint.Live = t; return t; },
            Flat, MoveTuning.Default, NewClientConfig(), Encoding.UTF8.GetBytes(Account));
        var rig = new Rig
        {
            Hub = hub,
            Step = () => { server.Poll(); server.Tick(Dt); persistence.Update(Dt); },
            Persistence = persistence,
            Store = store,
            Client = client,
            Endpoint = endpoint,
            BuiltAt = builtAt,
            TeleportOnlyPlayer = to => server.Teleport(PlayerRef.Slot(server.JoinedSlots.First()), to),
        };
        for (int i = 0; i < 20 && !client.Joined; i++) rig.Frame();
        Assert.True(client.Joined, "the client never joined");
        return rig;
    }

    // The multi-cell twin of ConnectSingle: the same wiring against ShardedWorldServer, whose OnJoin builds the
    // entity in whichever cell the resolved spawn falls in.
    static Rig ConnectSharded(InMemoryWorldStore store)
    {
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
        var persistence = new WorldPersistence(server, store, new WorldPersistenceConfig { SaveIntervalSeconds = 999f });
        var builtAt = new List<Vector3>();
        server.PlayerJoined += (slot, _) =>
        {
            if (server.TryGetPlayerState(slot, out PlayerMoveState st)) builtAt.Add(st.Position);
        };
        var endpoint = new Endpoint();
        var client = new WorldClient(
            () => { INetTransport t = hub.CreateClient(); endpoint.Live = t; return t; },
            Flat, MoveTuning.Default, NewClientConfig(), Encoding.UTF8.GetBytes(Account));
        var rig = new Rig
        {
            Hub = hub,
            Step = () => { server.Poll(); server.Tick(Dt); persistence.Update(Dt); },
            Persistence = persistence,
            Store = store,
            Client = client,
            Endpoint = endpoint,
            BuiltAt = builtAt,
            TeleportOnlyPlayer = to => server.Teleport(PlayerRef.Slot(server.JoinedSlots.First()), to),
        };
        for (int i = 0; i < 40 && !client.Joined; i++) rig.Frame();
        Assert.True(client.Joined, "the client never joined the sharded server");
        return rig;
    }

    // Parks the player 500 m off the spawn and lets the move settle, so the drop that follows persists it there.
    static void ParkAwayFromSpawn(Rig rig)
    {
        rig.TeleportOnlyPlayer(Parked);
        rig.Idle(40);
        Assert.True(Vector3.Distance(rig.LocalPosition(), Parked) < 0.5f,
            $"the player has to be parked off the spawn for this row to prove anything (it is at {rig.LocalPosition()})");
    }

    // ---- (1) probe A: a stationary rejoin against a real persistence layer is not a teleport at all ----

    [Fact]
    public void A_stationary_rejoin_on_a_persisted_server_reports_no_teleport()
    {
        Rig rig = ConnectSingle(new InMemoryWorldStore());
        ParkAwayFromSpawn(rig);

        uint epochBefore = rig.Client.LocalTeleportEpoch;
        int fired = 0;
        rig.Client.LocalTeleported += () => fired++;   // subscribed after the park, so only the rejoin can fire it

        Assert.True(rig.DropAndReconnect(), "the client never reconnected after the transport drop");

        // The join BUILT the entity where the player left, so the first snapshot of the new session was already the
        // truth. This is the fix: before it, the entity was built at the configured spawn and the restore moved it
        // afterwards, which is one teleport the client can only read positionally and a second it reads off the epoch.
        Assert.Equal(2, rig.BuiltAt.Count);                       // the first join, then the rejoin
        Assert.True(Vector3.Distance(rig.BuiltAt[1], Parked) < 0.5f,
            $"the rejoin should have been built where the player left ({rig.BuiltAt[1]})");
        Assert.True(Vector3.Distance(rig.ResumeSnapshot, Parked) < 0.5f,
            $"the resume snapshot should carry the parked position ({rig.ResumeSnapshot})");

        // Let the session settle: the restore lands on a later frame, and any snapshot could still fire the signal.
        rig.Idle(40);

        Assert.True(Vector3.Distance(rig.LocalPosition(), Parked) < 0.5f,
            $"the player should have resumed where it was ({rig.LocalPosition()})");
        Assert.Equal(0, fired);                                    // was two: the reseed onto the spawn, then the restore
        Assert.Equal(epochBefore, rig.Client.LocalTeleportEpoch);
    }

    // ---- (2) a first-ever join is still the configured spawn, and still exactly one teleport ----

    [Fact]
    public void A_first_ever_join_with_no_stored_record_spawns_at_the_configured_spawn()
    {
        var hub = new InMemoryHub();
        var config = new WorldServerConfig
        {
            TickSeconds = Dt,
            InterestRadius = 500f,
            MaxPlayers = 8,
            SpawnPosition = _ => new Vector3(10f, 0f, -5f),
        };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var persistence = new WorldPersistence(server, new InMemoryWorldStore(),
            new WorldPersistenceConfig { SaveIntervalSeconds = 999f });
        using var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, NewClientConfig(),
            Encoding.UTF8.GetBytes(Account));

        Assert.Equal(0u, client.LocalTeleportEpoch);   // nothing has landed yet
        int fired = 0;
        client.LocalTeleported += () => fired++;

        for (int i = 0; i < 40; i++) { server.Poll(); server.Tick(Dt); persistence.Update(Dt); client.Poll(Dt); }

        // A brand-new account has no hint and no stored record, so nothing about this join changed: the configured
        // spawn, ground-clamped, and the one teleport a placement with no prior position to be continuous with owes.
        Vector3 local = client.Snapshot().Single(e => e.IsLocal).Position;
        Assert.True(Vector3.Distance(local, new Vector3(10f, MoveTuning.Default.CapsuleHalfHeight, -5f)) < 0.5f,
            $"a first join belongs at the configured spawn ({local})");
        Assert.Equal(1u, client.LocalTeleportEpoch);
        Assert.Equal(1, fired);
    }

    // ---- (3) a stored position that really moved during the disconnect window still reports ONE teleport ----

    [Fact]
    public async Task A_move_during_the_disconnect_window_resumes_there_with_one_teleport()
    {
        var store = new InMemoryWorldStore();
        Rig rig = ConnectSingle(store);
        ParkAwayFromSpawn(rig);

        int fired = 0;
        rig.Client.LocalTeleported += () => fired++;

        // Drop, then move the account while it is offline: a respawn, an offline admin move, another process. The
        // hint says one thing and the store says another, and the STORE is the authority - the hint only decides
        // where the entity is built, and a load that disagrees still corrects it, as a real teleport.
        rig.Hub.DisconnectClient(rig.Endpoint.Live!);
        rig.Step();                                                 // a SERVER-only frame: it observes the leave and
                                                                    // saves, and with the client unpolled it cannot
                                                                    // rejoin before the record below lands
        Assert.NotNull(await store.LoadAsync(RecordKey));            // save-on-leave really did write the parked one
        var respawn = new Vector3(-800f, 0.9f, -600f);
        await store.SaveAsync(RecordKey, PlayerRecord.From(new PlayerMoveState { Position = respawn }).Encode());

        bool back = false;
        for (int i = 0; i < 400 && !back; i++)
        {
            rig.Frame(0.05f);
            back = rig.Client.ConnectionState == WorldConnectionState.Connected && rig.Client.LocalNetId > 0;
        }
        Assert.True(back, "the client never reconnected after the transport drop");
        rig.Idle(40);

        Assert.True(Vector3.Distance(rig.LocalPosition(), respawn) < 0.5f,
            $"the stored record wins over the hint ({rig.LocalPosition()})");
        Assert.Equal(1, fired);   // exactly one: the restore genuinely moved the player 1.4 km
    }

    // ---- (4) the sharded twin of (1) ----

    [Fact]
    public void A_stationary_rejoin_on_a_persisted_sharded_server_reports_no_teleport()
    {
        Rig rig = ConnectSharded(new InMemoryWorldStore());
        rig.TeleportOnlyPlayer(Parked);
        rig.Idle(60);   // the sharded head also has to hand the entity off to the cell containing the new position
        Assert.True(Vector3.Distance(rig.LocalPosition(), Parked) < 0.5f,
            $"the player has to be parked off the spawn for this row to prove anything (it is at {rig.LocalPosition()})");

        uint epochBefore = rig.Client.LocalTeleportEpoch;
        int fired = 0;
        rig.Client.LocalTeleported += () => fired++;

        Assert.True(rig.DropAndReconnect(), "the client never reconnected after the transport drop");

        // The sharded head resolves the same hint, so the rejoiner is built in the cell it left from rather than in
        // the spawn cell, and its first snapshot is already correct.
        Assert.Equal(2, rig.BuiltAt.Count);
        Assert.True(Vector3.Distance(rig.BuiltAt[1], Parked) < 0.5f,
            $"the rejoin should have been built where the player left ({rig.BuiltAt[1]})");
        Assert.True(Vector3.Distance(rig.ResumeSnapshot, Parked) < 0.5f,
            $"the resume snapshot should carry the parked position ({rig.ResumeSnapshot})");

        rig.Idle(40);

        Assert.True(Vector3.Distance(rig.LocalPosition(), Parked) < 0.5f,
            $"the player should have resumed where it was ({rig.LocalPosition()})");
        Assert.Equal(0, fired);
        Assert.Equal(epochBefore, rig.Client.LocalTeleportEpoch);
    }
}
