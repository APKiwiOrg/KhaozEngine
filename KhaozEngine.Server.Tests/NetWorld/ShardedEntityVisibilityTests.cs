using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// <see cref="ShardedWorldServerConfig.EntityVisibleToSlot"/> through real delta and full-snapshot clients on a small
/// cell grid, including an entity a viewer only sees as a ghost mirrored from a neighbouring cell, plus the explicit
/// interest overload of <see cref="ShardHost.SnapshotForClient(int, World, IReadOnlySet{long}, long?)"/>.
/// </summary>
public sealed class ShardedEntityVisibilityTests
{
    private const float Dt = 1f / 30f;
    private static float Flat(float x, float z) => 0f;

    // Every player spawns in cell (0,0) near its east edge, so the cell to the east is one ghost away.
    private static Vector3 NearEastEdge(int slot) => new(7.5f + slot * 0.5f, 0f, 5f);

    [Fact]
    public void AnOwnerOnlyEntityReachesOnlyItsViewer()
    {
        var rig = new Rig();
        rig.Pump(8);
        long hidden = rig.Server.SpawnEntity(7f, 4f);
        long shown = rig.Server.SpawnEntity(7f, 6f);
        rig.HiddenId = hidden;
        rig.Pump(8);

        Assert.True(rig.Sees(rig.Owner, hidden));
        Assert.True(rig.Sees(rig.Owner, shown));
        foreach (RawDeltaClient viewer in new[] { rig.DeltaViewer, rig.SnapshotViewer })
        {
            Assert.False(rig.Sees(viewer, hidden));
            Assert.True(rig.Sees(viewer, shown));
            Assert.True(rig.Sees(viewer, rig.Owner.LocalNetId));
        }
        Assert.True(rig.DeltaViewer.DeltaFramesApplied > 0);
        Assert.Equal(0, rig.SnapshotViewer.DeltaFramesApplied);
    }

    [Fact]
    public void PolicyChangesRemoveAndRestoreOnTheDeltaPath() =>
        AssertRemoveAndRestore(rig => rig.DeltaViewer);

    [Fact]
    public void PolicyChangesRemoveAndRestoreOnTheSnapshotPath() =>
        AssertRemoveAndRestore(rig => rig.SnapshotViewer);

    private static void AssertRemoveAndRestore(Func<Rig, RawDeltaClient> pick)
    {
        var rig = new Rig();
        rig.Pump(8);
        long id = rig.Server.SpawnEntity(7f, 4f, (w, e) => w.Set(e, new PlayerIdentity { DisplayName = "kept" }));
        rig.HiddenId = id;
        rig.AllowOthers = true;
        rig.Pump(6);
        RawDeltaClient viewer = pick(rig);
        Assert.True(rig.Sees(viewer, id));

        rig.AllowOthers = false;
        rig.Pump(6);
        Assert.False(rig.Sees(viewer, id));
        Assert.False(viewer.View.Entities.ContainsKey(id));

        // The entity must return whole: the position changed while hidden, and the identity, which did not change,
        // must be resent too, since the viewer dropped the entity with everything on it.
        Assert.True(rig.Server.TryGetEntity(id, out World world, out Entity entity));
        Assert.True(world.TryGet(entity, out ReplicatedPosition old));
        world.Set(entity, ReplicatedPosition.FromWorld(new Vector3(6f, 0f, 6f), old.Frame));
        rig.Pump(4);
        Assert.False(rig.Sees(viewer, id));

        rig.AllowOthers = true;
        rig.Pump(6);
        Assert.True(viewer.TryPos(id, out Vector3 pos));
        Assert.Equal(new Vector3(6f, 0f, 6f), pos);
        Assert.True(viewer.View.TryGetEntity(id, out Entity seen));
        Assert.True(viewer.World.TryGet(seen, out PlayerIdentity identity));
        Assert.Equal("kept", identity.DisplayName);
    }

    [Fact]
    public void AViewerEnteringRangeLaterStillNeverReceivesAHiddenEntity()
    {
        var rig = new Rig(spawn: slot => slot == 0 ? NearEastEdge(0) : new Vector3(35f + slot, 0f, 5f));
        rig.Pump(8);
        long hidden = rig.Server.SpawnEntity(7f, 4f);
        long shown = rig.Server.SpawnEntity(7f, 6f);
        rig.HiddenId = hidden;
        rig.Pump(6);
        Assert.False(rig.Sees(rig.DeltaViewer, shown));
        Assert.False(rig.Sees(rig.SnapshotViewer, shown));

        foreach (RawDeltaClient viewer in new[] { rig.DeltaViewer, rig.SnapshotViewer })
        {
            int slot = rig.SlotOf(viewer);
            Assert.True(rig.Server.TryGetPlayerState(slot, out PlayerMoveState state));
            state.Position = new Vector3(6f, 0f, 5f);
            rig.Server.SetPlayerState(slot, state, teleport: true);
        }
        rig.Pump(8);

        foreach (RawDeltaClient viewer in new[] { rig.DeltaViewer, rig.SnapshotViewer })
        {
            Assert.True(rig.Sees(viewer, shown));
            Assert.False(rig.Sees(viewer, hidden));
        }
        Assert.True(rig.Sees(rig.Owner, hidden));
    }

    [Fact]
    public void AReconnectingViewerStillNeverReceivesAHiddenEntity()
    {
        var rig = new Rig();
        rig.Pump(8);
        long hidden = rig.Server.SpawnEntity(7f, 4f);
        long shown = rig.Server.SpawnEntity(7f, 6f);
        rig.HiddenId = hidden;
        rig.Pump(6);
        Assert.False(rig.Sees(rig.DeltaViewer, hidden));

        rig.ReconnectDeltaViewer();
        rig.Pump(8);
        Assert.Equal(3, rig.Server.PlayerCount);
        Assert.True(rig.DeltaViewer.Joined);
        Assert.True(rig.Sees(rig.DeltaViewer, shown));
        Assert.False(rig.Sees(rig.DeltaViewer, hidden));
        Assert.True(rig.Sees(rig.Owner, hidden));
    }

    [Fact]
    public void ANullPredicateServesTodaysBytes()
    {
        List<byte[]> Run(Func<int, long, bool>? visible, bool setProperty)
        {
            var hub = new InMemoryTransportHub();
            var recording = new RecordingTransport(hub.Server);
            ShardedWorldServerConfig config = setProperty ? WithPredicate(NearEastEdge, visible) : Config(NearEastEdge);
            var server = new ShardedWorldServer(recording, config, Flat, MoveTuning.Default);
            var delta = new RawDeltaClient(hub.CreateClient(), server.Registry);
            var legacy = new RawDeltaClient(hub.CreateClient(), server.Registry, advertiseDelta: false);
            Pump(server, 6, delta, legacy);
            long npc = server.SpawnEntity(7f, 4f, (w, e) => w.Set(e, new PlayerIdentity { DisplayName = "npc" }));
            long ghost = server.SpawnEntity(11f, 5f);
            var east = new MoveCommand(new Vector2(1f, 0f), run: false, cameraYaw: 0f);
            for (int i = 0; i < 20; i++)
            {
                delta.SendMove(east);
                legacy.SendMove(east);
                Pump(server, 1, delta, legacy);
            }
            Assert.True(server.DespawnEntity(npc));
            Assert.True(server.DespawnEntity(ghost));
            Pump(server, 4, delta, legacy);
            Assert.True(delta.DeltaFramesApplied > 0);
            Assert.True(legacy.SnapshotFramesApplied > 0);
            return recording.Sends.ConvertAll(s => s.payload);
        }

        List<byte[]> today = Run(null, setProperty: false);
        AssertSameFrames(today, Run(null, setProperty: true));
        AssertSameFrames(today, Run((_, _) => true, setProperty: true));
    }

    [Fact]
    public void ThePredicateNeverHidesTheViewersOwnPlayer()
    {
        var rig = new Rig(visible: (_, _) => false);
        rig.Pump(8);
        long npc = rig.Server.SpawnEntity(7f, 4f);
        rig.Pump(6);

        foreach (RawDeltaClient client in new[] { rig.Owner, rig.DeltaViewer, rig.SnapshotViewer })
        {
            Assert.True(client.LocalNetId > 0);
            Assert.True(rig.Sees(client, client.LocalNetId));
            Assert.Single(client.View.Entities);
            Assert.False(rig.Sees(client, npc));
        }
    }

    [Fact]
    public void AGhostAcrossACellBoundaryIsFilteredTheSameWay()
    {
        var rig = new Rig();
        rig.Pump(8);
        // Owned by cell (1,0), inside the overlap margin of its west edge, so cell (0,0) holds it as a ghost.
        long hidden = rig.Server.SpawnEntity(11f, 5f);
        long shown = rig.Server.SpawnEntity(11f, 5.5f);
        rig.HiddenId = hidden;
        rig.Pump(8);

        Assert.True(rig.Server.Host.TryGetOwner(hidden, out CellSim owner, out _));
        Assert.Equal(new CellCoord(1, 0), owner.Coord);
        foreach (RawDeltaClient client in new[] { rig.Owner, rig.DeltaViewer, rig.SnapshotViewer })
        {
            Assert.True(rig.Server.Host.TryGetHomeCell(rig.SlotOf(client), out CellSim home));
            Assert.Equal(new CellCoord(0, 0), home.Coord);
            Assert.True(home.TryGetGhost(hidden, out _), "the viewer's home cell should hold the entity as a ghost");
        }

        Assert.True(rig.Sees(rig.Owner, hidden));
        foreach (RawDeltaClient viewer in new[] { rig.DeltaViewer, rig.SnapshotViewer })
        {
            Assert.True(rig.Sees(viewer, shown));
            Assert.False(rig.Sees(viewer, hidden));
        }

        rig.AllowOthers = true;
        rig.Pump(6);
        Assert.True(rig.Sees(rig.DeltaViewer, hidden));
        Assert.True(rig.Sees(rig.SnapshotViewer, hidden));

        rig.AllowOthers = false;
        rig.Pump(6);
        Assert.False(rig.Sees(rig.DeltaViewer, hidden));
        Assert.False(rig.Sees(rig.SnapshotViewer, hidden));
    }

    [Fact]
    public void AViewerCrossingACellBoundaryUnderADenyAllRuleSeesOnlyItself()
    {
        var rig = new Rig(visible: (_, _) => false);
        rig.Pump(8);
        long npcWest = rig.Server.SpawnEntity(7f, 4f);
        long npcEast = rig.Server.SpawnEntity(11f, 5f);
        rig.Pump(6);
        RawDeltaClient[] viewers = { rig.DeltaViewer, rig.SnapshotViewer };
        foreach (RawDeltaClient viewer in viewers)
        {
            Assert.True(rig.Server.Host.TryGetHomeCell(rig.SlotOf(viewer), out CellSim home));
            Assert.Equal(new CellCoord(0, 0), home.Coord);
            AssertSeesOnlyItself(rig, viewer, npcWest, npcEast);
        }

        // Both viewers cross into cell (1,0), next to each other and to the east entity.
        float x = 12f;
        foreach (RawDeltaClient viewer in viewers)
        {
            int slot = rig.SlotOf(viewer);
            Assert.True(rig.Server.TryGetPlayerState(slot, out PlayerMoveState state));
            state.Position = new Vector3(x, 0f, 5f);
            rig.Server.SetPlayerState(slot, state, teleport: true);
            x += 0.5f;
        }
        rig.Pump(8);

        float radius = Config(NearEastEdge).InterestRadius;
        foreach (RawDeltaClient viewer in viewers)
        {
            int slot = rig.SlotOf(viewer);
            Assert.True(rig.Server.Host.TryGetHomeCell(slot, out CellSim home));
            Assert.Equal(new CellCoord(1, 0), home.Coord);
            // The rule is what hides them: the east entity and the other viewer are in this viewer's interest.
            (_, HashSet<long> interest) = rig.Server.Host.HomeInterest(slot, radius);
            Assert.Contains(npcEast, interest);
            Assert.True(interest.Count > 2);
            AssertSeesOnlyItself(rig, viewer, npcWest, npcEast);
        }
        Assert.True(rig.DeltaViewer.DeltaFramesApplied > 0);
        Assert.Equal(0, rig.SnapshotViewer.DeltaFramesApplied);
    }

    private static void AssertSeesOnlyItself(Rig rig, RawDeltaClient viewer, long npcWest, long npcEast)
    {
        Assert.True(viewer.LocalNetId > 0);
        Assert.True(rig.Sees(viewer, viewer.LocalNetId));
        Assert.Single(viewer.View.Entities);
        Assert.False(rig.Sees(viewer, npcWest));
        Assert.False(rig.Sees(viewer, npcEast));
    }

    [Fact]
    public void SnapshotForClientRefusesAWorldThatIsNotTheSlotsHome()
    {
        var rig = new Rig();
        rig.Pump(8);
        long east = rig.Server.SpawnEntity(11f, 5f);
        rig.Pump(6);
        ShardHost host = rig.Server.Host;
        int slot = rig.SlotOf(rig.DeltaViewer);
        (World home, HashSet<long> interest) = host.HomeInterest(slot, Config(NearEastEdge).InterestRadius);
        Assert.True(host.TryGetOwner(east, out CellSim eastCell, out _));
        Assert.NotSame(home, eastCell.World);

        Assert.Throws<ArgumentException>(() => host.SnapshotForClient(slot, eastCell.World, interest));
        Assert.Throws<ArgumentException>(() => host.SnapshotForClient(slot, eastCell.World, interest, serveEpoch: 9001));
        Assert.Throws<ArgumentException>(() => host.SnapshotForClient(slot, new World(), interest));
        // The home world is still served, with and without an epoch.
        Assert.Equal(host.SnapshotForClient(slot, home, interest),
            host.SnapshotForClient(slot, home, interest, serveEpoch: 9001));
    }

    [Fact]
    public void SnapshotForClientWithAnExplicitSetMatchesTheRadiusOverloadForTheSameSet()
    {
        var rig = new Rig();
        rig.Pump(8);
        long npc = rig.Server.SpawnEntity(7f, 4f);
        rig.Server.SpawnEntity(11f, 5f);
        rig.Pump(6);
        ShardHost host = rig.Server.Host;
        int slot = rig.SlotOf(rig.DeltaViewer);
        float radius = Config(NearEastEdge).InterestRadius;

        (World world, HashSet<long> interest) = host.HomeInterest(slot, radius);
        Assert.Contains(npc, interest);
        Assert.Equal(host.SnapshotForClient(slot, radius), host.SnapshotForClient(slot, world, interest));
        Assert.Equal(host.SnapshotForClient(slot, radius, serveEpoch: 9001),
            host.SnapshotForClient(slot, world, interest, serveEpoch: 9001));
        // The radius overload now delegates to the set overload, so pin the set overload independently too: the
        // unnarrowed set is served exactly as the full-scan writer serves it.
        Assert.Equal(SnapshotWriter.WriteFiltered(world, rig.Server.Registry, interest,
            ReplicationChannels.Replicate, rig.DeltaViewer.LocalNetId), host.SnapshotForClient(slot, world, interest));

        // A narrower explicit set is served exactly as the full-scan writer would serve that set.
        interest.Remove(npc);
        byte[] narrowed = host.SnapshotForClient(slot, world, interest);
        Assert.Equal(SnapshotWriter.WriteFiltered(world, rig.Server.Registry, interest,
            ReplicationChannels.Replicate, rig.DeltaViewer.LocalNetId), narrowed);
        Assert.NotEqual(host.SnapshotForClient(slot, radius), narrowed);
    }

    private static ShardedWorldServerConfig Config(Func<int, Vector3> spawn) => new()
    {
        TickSeconds = Dt,
        CellSize = 10f,
        OverlapMargin = 4f,
        InterestRadius = 4f,
        MaxPlayers = 8,
        SpawnPosition = spawn,
    };

    private static ShardedWorldServerConfig WithPredicate(Func<int, Vector3> spawn, Func<int, long, bool>? visible) => new()
    {
        TickSeconds = Dt,
        CellSize = 10f,
        OverlapMargin = 4f,
        InterestRadius = 4f,
        MaxPlayers = 8,
        SpawnPosition = spawn,
        EntityVisibleToSlot = visible,
    };

    private static void AssertSameFrames(List<byte[]> expected, List<byte[]> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) Assert.Equal(expected[i], actual[i]);
    }

    private static void Pump(ShardedWorldServer server, int ticks, params RawDeltaClient[] clients)
    {
        for (int i = 0; i < ticks; i++)
        {
            server.Poll();
            server.Tick(Dt);
            foreach (RawDeltaClient c in clients) c.Poll();
        }
    }

    // Three clients joined in order: the owner (delta), a delta viewer and a full-snapshot viewer. The rule hides
    // HiddenId from every slot but the owner's unless AllowOthers is set.
    private sealed class Rig
    {
        private readonly InMemoryTransportHub hub = new();
        private INetTransport deltaViewerTransport;

        public Rig(Func<int, Vector3>? spawn = null, Func<int, long, bool>? visible = null)
        {
            Server = new ShardedWorldServer(hub.Server, WithPredicate(spawn ?? NearEastEdge,
                visible ?? ((slot, id) => id != HiddenId || slot == OwnerSlot || AllowOthers)), Flat, MoveTuning.Default);
            Owner = new RawDeltaClient(hub.CreateClient(), Server.Registry);
            ShardedEntityVisibilityTests.Pump(Server, 6, Owner);
            OwnerSlot = SlotOf(Owner);
            deltaViewerTransport = hub.CreateClient();
            DeltaViewer = new RawDeltaClient(deltaViewerTransport, Server.Registry);
            SnapshotViewer = new RawDeltaClient(hub.CreateClient(), Server.Registry, advertiseDelta: false);
        }

        public ShardedWorldServer Server { get; }
        public RawDeltaClient Owner { get; }
        public RawDeltaClient DeltaViewer { get; private set; }
        public RawDeltaClient SnapshotViewer { get; }
        public int OwnerSlot { get; }
        public long HiddenId { get; set; } = -1;
        public bool AllowOthers { get; set; }

        public void Pump(int ticks) => ShardedEntityVisibilityTests.Pump(Server, ticks, Owner, DeltaViewer, SnapshotViewer);

        public bool Sees(RawDeltaClient client, long netId) =>
            client.View.TryGetEntity(netId, out Entity e) && client.World.IsAlive(e);

        public int SlotOf(RawDeltaClient client)
        {
            foreach (int slot in Server.JoinedSlots)
                if (Server.TryGetPlayerNetId(slot, out long id) && id == client.LocalNetId) return slot;
            throw new InvalidOperationException("client has no slot");
        }

        public void ReconnectDeltaViewer()
        {
            hub.DisconnectClient(deltaViewerTransport);
            Server.Poll();
            deltaViewerTransport = hub.CreateClient();
            DeltaViewer = new RawDeltaClient(deltaViewerTransport, Server.Registry);
        }
    }
}
