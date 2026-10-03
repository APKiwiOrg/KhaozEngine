using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// <see cref="WorldServerConfig.EntityVisibleToSlot"/> through real delta and full-snapshot clients: a hidden entity
/// never reaches the viewers the rule excludes, leaves and returns whole when the rule flips, stays hidden across a
/// late range entry and a reconnect, never hides the viewer's own player, and a null rule serves today's bytes.
/// </summary>
public sealed class EntityVisibilityTests
{
    private static readonly Func<float, float, float> Flat = (x, z) => 0f;
    private const float Dt = 1f / 30f;

    [Fact]
    public void AnOwnerOnlyEntityReachesOnlyItsViewer()
    {
        var rig = new Rig();
        rig.Pump(8);
        long hidden = rig.Server.SpawnEntity(4f, 0f);
        long shown = rig.Server.SpawnEntity(5f, 0f);
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
        long id = rig.Server.SpawnEntity(4f, 0f, (w, e) => w.Set(e, new PlayerIdentity { DisplayName = "kept" }));
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
        world.Set(entity, ReplicatedPosition.FromWorld(new Vector3(6f, 0f, 2f), old.Frame));
        rig.Pump(4);
        Assert.False(rig.Sees(viewer, id));

        rig.AllowOthers = true;
        rig.Pump(6);
        Assert.True(viewer.TryPos(id, out Vector3 pos));
        Assert.Equal(new Vector3(6f, 0f, 2f), pos);
        Assert.True(viewer.View.TryGetEntity(id, out Entity seen));
        Assert.True(viewer.World.TryGet(seen, out PlayerIdentity identity));
        Assert.Equal("kept", identity.DisplayName);
    }

    [Fact]
    public void FlipsInsideTheAckWindowRestoreTheEntityWholeAndRemoveItCleanly()
    {
        var rig = new Rig();
        rig.Pump(8);
        long id = rig.Server.SpawnEntity(4f, 0f, (w, e) => w.Set(e, new PlayerIdentity { DisplayName = "kept" }));
        rig.HiddenId = id;
        rig.AllowOthers = true;
        rig.Pump(6);
        RawDeltaClient viewer = rig.DeltaViewer;
        Assert.True(rig.Sees(viewer, id));

        // Hidden for one tick and shown on the next, with the viewer's ack for the hiding tick still unread. The
        // server diffs from the projection it last sent, which lacks the entity, so the show is a full spawn.
        rig.AllowOthers = false;
        rig.TickWithoutReadingAcks();
        Assert.False(rig.Sees(viewer, id));
        rig.AllowOthers = true;
        rig.TickWithoutReadingAcks();
        rig.Pump(6);

        Assert.True(viewer.TryPos(id, out Vector3 pos));
        Assert.Equal(new Vector3(4f, 0f, 0f), pos);
        Assert.True(viewer.View.TryGetEntity(id, out Entity seen));
        Assert.True(viewer.World.TryGet(seen, out PlayerIdentity identity));
        Assert.Equal("kept", identity.DisplayName);

        // The mirror: hidden long enough for every ack to land, then shown for one tick and hidden on the next with the
        // show ack unread. The last sent projection holds it, so the hide is a removal and the viewer must not keep it.
        rig.AllowOthers = false;
        rig.Pump(6);
        Assert.False(rig.Sees(viewer, id));
        rig.AllowOthers = true;
        rig.TickWithoutReadingAcks();
        Assert.True(rig.Sees(viewer, id));
        rig.AllowOthers = false;
        rig.TickWithoutReadingAcks();
        rig.Pump(6);
        Assert.False(rig.Sees(viewer, id));
        Assert.False(viewer.View.Entities.ContainsKey(id));
    }

    [Fact]
    public void AViewerEnteringRangeLaterStillNeverReceivesAHiddenEntity()
    {
        var rig = new Rig(radius: 20f, spawn: slot => slot == 0 ? Vector3.Zero : new Vector3(200f + slot * 10f, 0f, 0f));
        rig.Pump(8);
        long hidden = rig.Server.SpawnEntity(4f, 0f);
        long shown = rig.Server.SpawnEntity(5f, 0f);
        rig.HiddenId = hidden;
        rig.Pump(6);
        Assert.False(rig.Sees(rig.DeltaViewer, shown));
        Assert.False(rig.Sees(rig.SnapshotViewer, shown));

        foreach (RawDeltaClient viewer in new[] { rig.DeltaViewer, rig.SnapshotViewer })
        {
            int slot = rig.SlotOf(viewer);
            Assert.True(rig.Server.TryGetPlayerState(slot, out PlayerMoveState state));
            state.Position = new Vector3(2f, 0f, 3f);
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
        long hidden = rig.Server.SpawnEntity(4f, 0f);
        long shown = rig.Server.SpawnEntity(5f, 0f);
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
            WorldServerConfig config = setProperty
                ? new WorldServerConfig { TickSeconds = Dt, InterestRadius = 500f, MaxPlayers = 8, EntityVisibleToSlot = visible }
                : new WorldServerConfig { TickSeconds = Dt, InterestRadius = 500f, MaxPlayers = 8 };
            var server = new WorldServer(recording, config, Flat, MoveTuning.Default);
            var delta = new RawDeltaClient(hub.CreateClient(), server.Registry);
            var legacy = new RawDeltaClient(hub.CreateClient(), server.Registry, advertiseDelta: false);
            Pump(server, 6, delta, legacy);
            long npc = server.SpawnEntity(4f, 0f, (w, e) => w.Set(e, new PlayerIdentity { DisplayName = "npc" }));
            var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f);
            for (int i = 0; i < 10; i++)
            {
                delta.SendMove(forward);
                legacy.SendMove(forward);
                Pump(server, 1, delta, legacy);
            }
            Assert.True(server.DespawnEntity(npc));
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
        long npc = rig.Server.SpawnEntity(4f, 0f);
        rig.Pump(6);

        foreach (RawDeltaClient client in new[] { rig.Owner, rig.DeltaViewer, rig.SnapshotViewer })
        {
            Assert.True(client.LocalNetId > 0);
            Assert.True(rig.Sees(client, client.LocalNetId));
            Assert.Single(client.View.Entities);
            Assert.False(rig.Sees(client, npc));
        }
    }

    private static void AssertSameFrames(List<byte[]> expected, List<byte[]> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++) Assert.Equal(expected[i], actual[i]);
    }

    private static void Pump(WorldServer server, int ticks, params RawDeltaClient[] clients)
    {
        for (int i = 0; i < ticks; i++)
        {
            server.Poll();
            server.Tick(Dt);
            foreach (RawDeltaClient c in clients) c.Poll();
        }
    }

    // Three clients joined in order: the owner (slot 0, delta), a delta viewer and a full-snapshot viewer. The rule
    // hides HiddenId from every slot but the owner's unless AllowOthers is set.
    private sealed class Rig
    {
        private readonly InMemoryTransportHub hub = new();
        private INetTransport deltaViewerTransport;

        public Rig(float radius = 500f, Func<int, Vector3>? spawn = null, Func<int, long, bool>? visible = null)
        {
            Server = new WorldServer(hub.Server, new WorldServerConfig
            {
                TickSeconds = Dt,
                InterestRadius = radius,
                MaxPlayers = 8,
                SpawnPosition = spawn,
                EntityVisibleToSlot = visible ?? ((slot, id) => id != HiddenId || slot == OwnerSlot || AllowOthers),
            }, Flat, MoveTuning.Default);
            Owner = new RawDeltaClient(hub.CreateClient(), Server.Registry);
            EntityVisibilityTests.Pump(Server, 6, Owner);
            OwnerSlot = SlotOf(Owner);
            deltaViewerTransport = hub.CreateClient();
            DeltaViewer = new RawDeltaClient(deltaViewerTransport, Server.Registry);
            SnapshotViewer = new RawDeltaClient(hub.CreateClient(), Server.Registry, advertiseDelta: false);
        }

        public WorldServer Server { get; }
        public RawDeltaClient Owner { get; }
        public RawDeltaClient DeltaViewer { get; private set; }
        public RawDeltaClient SnapshotViewer { get; }
        public int OwnerSlot { get; }
        public long HiddenId { get; set; } = -1;
        public bool AllowOthers { get; set; }

        public void Pump(int ticks) => EntityVisibilityTests.Pump(Server, ticks, Owner, DeltaViewer, SnapshotViewer);

        // One tick that skips the server's Poll, so acks the clients sent since the last Poll stay unread.
        public void TickWithoutReadingAcks()
        {
            Server.Tick(Dt);
            foreach (RawDeltaClient c in new[] { Owner, DeltaViewer, SnapshotViewer }) c.Poll();
        }

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
