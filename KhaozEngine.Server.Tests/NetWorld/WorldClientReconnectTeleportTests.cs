using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The teleport contract across a transport reconnect (#409). A reconnect rebuilds everything about the session - a
/// fresh transport, a fresh server slot, a fresh net id, a fresh authoritative entity whose teleport epoch counts from
/// its own zero - but it does not move the player. It used to surface as a teleport anyway, because the reseed the
/// reconnect needs reported one unconditionally, so a consumer honouring the contract paid its full teleport reaction
/// on every drop (Ruinborne rebuilt its whole terrain ring while the player stood still,
/// https://github.com/APKiwiOrg/Ruinborne/issues/388), and a client on a lossy link paid it repeatedly.
///
/// What the signal means now: the local player's world position changed DISCONTINUOUSLY. A first-ever join reports it
/// (there is no prior position to be continuous with), a genuine server-side teleport reports it, and a reconnect
/// reports it only if the player really did move while the client was away.
/// </summary>
public class WorldClientReconnectTeleportTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    const float Dt = 1f / 30f;

    // Spawn every slot at the same place, so a recycled-vs-fresh server slot cannot smuggle a real displacement into
    // the reconnect and make the "no teleport" assertion pass for the wrong reason.
    static WorldServerConfig NewServerConfig() => new()
    {
        TickSeconds = Dt,
        InterestRadius = 500f,
        MaxPlayers = 8,
        SpawnPosition = _ => Vector3.Zero,
    };

    static WorldClientConfig NewClientConfig() => new()
    {
        TickSeconds = Dt,
        DisconnectTimeoutSeconds = 0.5f,
        Reconnect = new ReconnectBackoff { InitialSeconds = 0.05f, Multiplier = 1f, MaxSeconds = 0.05f },
    };

    // A live rig over InMemoryHub: one server, one auto-reconnecting client, and a handle on the client's CURRENT
    // transport endpoint so a test can drop it (a transport drop against a server that never restarted - the case
    // #409 is about, and the one RestartableHub's server-restart model does not cover).
    // The client's current transport endpoint, written by the connect factory on every attempt. A holder rather than a
    // Rig property because the factory runs inside the WorldClient constructor, before a Rig can exist.
    sealed class Endpoint { public INetTransport? Live; }

    sealed class Rig
    {
        public required InMemoryHub Hub { get; init; }
        public required WorldServer Server { get; init; }
        public required WorldServerConfig Config { get; init; }
        public required WorldClient Client { get; init; }
        public required Endpoint Endpoint { get; init; }

        public void Frame(float dt = Dt)
        {
            Server.Poll();
            Server.Tick(Config.TickSeconds);
            Client.Poll(dt);
            Client.AdvancePresentation(dt);
        }

        /// <summary>Drops the client's current transport endpoint, then pumps until it has rejoined. Returns false if
        /// it never came back.</summary>
        public bool DropAndReconnect()
        {
            Hub.DisconnectClient(Endpoint.Live!);
            bool sawReconnecting = false;
            for (int i = 0; i < 400; i++)
            {
                Frame(0.05f);
                if (Client.ConnectionState == WorldConnectionState.Reconnecting) sawReconnecting = true;
                if (sawReconnecting && Client.ConnectionState == WorldConnectionState.Connected && Client.LocalNetId > 0)
                    return true;
            }
            return false;
        }

        public Vector3 LocalPosition() => Client.Snapshot().Single(e => e.IsLocal).Position;
    }

    static Rig Connect()
    {
        var hub = new InMemoryHub();
        WorldServerConfig config = NewServerConfig();
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var endpoint = new Endpoint();
        var client = new WorldClient(
            () => { INetTransport t = hub.CreateClient(); endpoint.Live = t; return t; },
            Flat, MoveTuning.Default, NewClientConfig());
        var rig = new Rig { Hub = hub, Server = server, Config = config, Client = client, Endpoint = endpoint };
        for (int i = 0; i < 20 && !client.Joined; i++) rig.Frame();
        Assert.True(client.Joined, "the client never joined");
        return rig;
    }

    // ---- (c) a first-ever join IS a teleport: no prior state to be continuous with ----

    [Fact]
    public void First_join_reports_a_teleport()
    {
        var hub = new InMemoryHub();
        WorldServerConfig config = NewServerConfig();
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        using var client = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, NewClientConfig());

        Assert.Equal(0u, client.LocalTeleportEpoch);   // nothing has landed yet
        int fired = 0;
        client.LocalTeleported += () => fired++;

        for (int i = 0; i < 20 && client.LocalTeleportEpoch == 0u; i++)
        {
            server.Poll(); server.Tick(Dt); client.Poll(Dt);
        }

        Assert.Equal(1u, client.LocalTeleportEpoch);
        Assert.Equal(1, fired);
    }

    // ---- (a) a transport drop + reconnect with the player stationary is NOT a teleport ----

    [Fact]
    public void Transport_drop_and_reconnect_while_stationary_is_not_a_teleport()
    {
        Rig rig = Connect();
        for (int i = 0; i < 30; i++) { rig.Client.SendInput(MoveCommand.Idle); rig.Frame(); }

        long netIdBefore = rig.Client.LocalNetId;
        uint teleportsBefore = rig.Client.LocalTeleportEpoch;
        Vector3 posBefore = rig.LocalPosition();
        int fired = 0;
        rig.Client.LocalTeleported += () => fired++;   // subscribed AFTER the join, so only the resume can fire it

        Assert.True(rig.DropAndReconnect(), "the client never reconnected after the transport drop");

        // Let the resumed session settle: several more snapshots, any of which could still fire the signal late.
        for (int i = 0; i < 20; i++) { rig.Client.SendInput(MoveCommand.Idle); rig.Frame(); }

        // The reconnect really did rebuild the session (otherwise the assertions below prove nothing): the server
        // allocates a fresh net id per join, so a resumed client is a DIFFERENT entity than the one that dropped.
        Assert.True(rig.Client.LocalNetId > 0);
        Assert.NotEqual(netIdBefore, rig.Client.LocalNetId);
        Assert.Equal(WorldConnectionState.Connected, rig.Client.ConnectionState);

        // ... and the player did not move across it.
        Vector3 posAfter = rig.LocalPosition();
        Assert.True(Vector3.Distance(posBefore, posAfter) < 0.5f,
            $"the player should have resumed where it was ({posBefore} -> {posAfter})");

        // The whole point: the consumer-visible teleport signal stayed quiet. This is what Ruinborne's streamer wired
        // its ring rebuild to, and what fired on every reconnect before the fix.
        Assert.Equal(0, fired);
        Assert.Equal(teleportsBefore, rig.Client.LocalTeleportEpoch);
    }

    // ---- (b) a genuine server-side teleport still reports one, including after a reconnect ----

    [Fact]
    public void An_in_session_server_teleport_still_reports_a_teleport()
    {
        Rig rig = Connect();
        for (int i = 0; i < 20; i++) { rig.Client.SendInput(MoveCommand.Idle); rig.Frame(); }

        uint before = rig.Client.LocalTeleportEpoch;
        int fired = 0;
        rig.Client.LocalTeleported += () => fired++;

        int slot = rig.Server.JoinedSlots.First();
        rig.Server.Teleport(PlayerRef.Slot(slot), new Vector3(140f, 0f, -70f));
        for (int i = 0; i < 20 && fired == 0; i++) { rig.Client.SendInput(MoveCommand.Idle); rig.Frame(); }

        Assert.True(fired >= 1, "a server-side teleport must still surface to the consumer");
        Assert.True(rig.Client.LocalTeleportEpoch > before);
    }

    [Fact]
    public void A_server_teleport_after_a_reconnect_still_reports_a_teleport()
    {
        // The reconnect re-baselines the epoch onto the new entity's counter (which starts far below the old one), so
        // the guard against a spurious reconnect signal must not also swallow the next REAL teleport.
        Rig rig = Connect();
        for (int i = 0; i < 20; i++) { rig.Client.SendInput(MoveCommand.Idle); rig.Frame(); }
        Assert.True(rig.DropAndReconnect(), "the client never reconnected after the transport drop");
        for (int i = 0; i < 10; i++) { rig.Client.SendInput(MoveCommand.Idle); rig.Frame(); }

        uint before = rig.Client.LocalTeleportEpoch;
        int fired = 0;
        rig.Client.LocalTeleported += () => fired++;

        int slot = rig.Server.JoinedSlots.First();
        rig.Server.Teleport(PlayerRef.Slot(slot), new Vector3(-120f, 0f, 90f));
        for (int i = 0; i < 20 && fired == 0; i++) { rig.Client.SendInput(MoveCommand.Idle); rig.Frame(); }

        Assert.True(fired >= 1, "a teleport after a reconnect must still surface");
        Assert.True(rig.Client.LocalTeleportEpoch > before);
    }

    // ---- the remote-side compare: an epoch dip is not a teleport either ----

    [Fact]
    public void A_remote_epoch_that_dips_and_recovers_is_not_a_remote_teleport()
    {
        // The remote flush read the replicated epoch as a teleport on ANY change. That epoch reads 0 whenever the
        // movement component is momentarily unreadable (here when it has not replicated onto the entity yet, and on
        // the server where it rebuilds the state), so a real stream can dip and recover - which cut the remote once on
        // the dip and once on the recovery, streaking nothing but costing every observer a snap.
        var (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
        var server = new NetServer(serverTransport, maxPlayers: 4, new AllowAllAuthenticator());
        using var client = new WorldClient(clientTransport, Flat, MoveTuning.Default, new WorldClientConfig());

        int slot = -1;
        for (int i = 0; i < 20 && slot < 0; i++)
        {
            server.Poll();
            while (server.TryDequeueEvent(out ServerSessionEvent ev))
                if (ev.Kind == ServerSessionEventKind.Joined) slot = ev.Slot;
            client.Poll();
        }
        Assert.True(slot >= 0);

        const long LocalId = 1, RemoteId = 2;
        Assert.Empty(Push(server, client, slot, Snapshot(LocalId, RemoteId, epoch: 5)));   // first sight: records only
        Assert.Empty(Push(server, client, slot, Snapshot(LocalId, RemoteId, epoch: 5)));   // steady
        Assert.Empty(Push(server, client, slot, Snapshot(LocalId, RemoteId, epoch: 0)));   // the dip: backwards
        Assert.Empty(Push(server, client, slot, Snapshot(LocalId, RemoteId, epoch: 5)));   // the recovery: already seen
        Assert.Equal(new[] { RemoteId }, Push(server, client, slot, Snapshot(LocalId, RemoteId, epoch: 6)));  // real
    }

    // Sends one server->client snapshot frame and returns the remote teleports that landed with it. RemoteTeleports
    // reflects only the most recent Poll, so it is read on the poll that ingested the frame.
    static long[] Push(NetServer server, WorldClient client, int slot, byte[] frame)
    {
        server.SendTo(slot, frame, NetChannelReliability.ReliableOrdered);
        server.Poll();
        client.Poll();
        return client.RemoteTeleports.ToArray();
    }

    // One full snapshot carrying the local player plus a stationary remote at the given teleport epoch.
    static byte[] Snapshot(long localId, long remoteId, uint epoch)
    {
        ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        var world = new World();
        Add(world, localId, Vector3.Zero, 0u);
        Add(world, remoteId, new Vector3(10f, 0f, 0f), epoch);
        return MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Snapshot,
            MoveProtocol.EncodeSnapshotFrame(localId, ackSeq: -1, SnapshotWriter.Write(world, registry)));

        static void Add(World w, long netId, Vector3 position, uint epoch)
        {
            Entity e = w.Spawn();
            w.Set(e, new NetId(netId));
            w.Set(e, ReplicatedPosition.FromWorld(position, WorldFrame.Origin));
            w.Set(e, new MovementState { Grounded = true, TeleportEpoch = epoch });
        }
    }
}
