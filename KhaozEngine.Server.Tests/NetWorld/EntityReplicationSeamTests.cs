using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// End-to-end coverage of the server-owned non-player entity seam: a consumer-injected <see cref="ReplicationRegistry"/>
/// carrying an extension component, <see cref="ShardedWorldServer.SpawnEntity"/>, <see cref="ShardedWorldServer.OnBeforeTick"/>,
/// and <see cref="WorldClient.TryGetComponent{T}"/>, plus the hard version-skew guarantee that a client which never
/// registered the extension still sees the entity and does not disconnect.
/// </summary>
public class EntityReplicationSeamTests
{
    private static float Flat(float x, float z) => 0f;

    /// <summary>A consumer discriminator: which model/behaviour a non-player entity uses. Registered above the floor.</summary>
    private struct NpcKind : IComponent { public int Kind; }

    // The SAME extended registry construction, called identically on server and client.
    private static ReplicationRegistry ExtendedRegistry() => MoveProtocol.CreateRegistry(r =>
        r.Register<NpcKind>(
            MoveProtocol.FirstConsumerTypeId,
            write: (n, bw) => bw.Write(n.Kind),
            read: br => new NpcKind { Kind = br.ReadInt32() }));

    private static ShardedWorldServerConfig SmallCells() => new()
    {
        TickSeconds = 1f / 30f,
        CellSize = 10f,
        OverlapMargin = 4f,
        InterestRadius = 4f,
        MaxPlayers = 8,
        SpawnPosition = _ => new Vector3(5f, 0f, 5f),   // player home cell (0,0), near origin
    };

    private static void Pump(ShardedWorldServer server, WorldClient client, ShardedWorldServerConfig cfg, int frames)
    {
        for (int i = 0; i < frames; i++) { server.Poll(); server.Tick(cfg.TickSeconds); client.Poll(); }
    }

    private static bool ClientSees(WorldClient client, long netId)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.Id.Value == netId) return true;
        return false;
    }

    [Fact]
    public void SpawnEntity_NonCollidingNetId_VisibleToNearbyClient_ClientReadsItsKind()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = SmallCells();
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default, registry: ExtendedRegistry());

        // Spawn a server-owned NPC one metre from the player's spawn, tagged with a consumer kind.
        long npcNetId = server.SpawnEntity(6f, 5f, (w, e) => w.Set(e, new NpcKind { Kind = 7 }));

        var client = new WorldClient(ct, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = cfg.TickSeconds, InterpolateRemotes = false }, registry: ExtendedRegistry());
        Pump(server, client, cfg, 12);

        Assert.True(client.Joined);
        Assert.True(client.LocalNetId > 0);
        Assert.NotEqual(npcNetId, client.LocalNetId);            // non-colliding with the player id
        Assert.True(ClientSees(client, npcNetId));               // the NPC entered the client's area of interest

        Assert.True(client.TryGetComponent(npcNetId, out NpcKind kind));
        Assert.Equal(7, kind.Kind);                              // client read the server-assigned discriminator

        Assert.False(client.TryGetComponent(client.LocalNetId, out NpcKind _));   // the player carries no NpcKind
        Assert.False(client.TryGetComponent(987654, out NpcKind _));              // unknown net id
    }

    [Fact]
    public void OnBeforeTick_FiresOncePerTick_AndItsWritesReachTheSameTickSnapshot()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = SmallCells();
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default, registry: ExtendedRegistry());
        long npcNetId = server.SpawnEntity(6f, 5f, (w, e) => w.Set(e, new NpcKind { Kind = 1 }));

        int calls = 0;
        bool jump = false;
        server.OnBeforeTick += _ =>
        {
            calls++;
            if (jump && server.Host.TryGetOwner(npcNetId, out CellSim cell, out Entity e))
                cell.World.Set(e, ReplicatedPosition.FromWorld(new Vector3(7.5f, 0f, 5f), WorldFrame.Origin));
        };

        var client = new WorldClient(ct, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = cfg.TickSeconds, InterpolateRemotes = false }, registry: ExtendedRegistry());
        Pump(server, client, cfg, 12);
        Assert.True(client.Joined);

        Assert.True(client.TryGetComponent(npcNetId, out ReplicatedPosition before));
        Assert.True(before.Value.X < 6.5f);                      // still near its spawn x=6

        // One tick where the brain jumps the NPC: the write happens inside Tick BEFORE the snapshot pass, so the
        // snapshot the client applies on the very next Poll already carries the new position.
        jump = true;
        int callsBefore = calls;
        server.Poll(); server.Tick(cfg.TickSeconds); client.Poll();

        Assert.Equal(callsBefore + 1, calls);                    // exactly one OnBeforeTick per Tick
        Assert.True(client.TryGetComponent(npcNetId, out ReplicatedPosition after));
        Assert.True(after.Value.X > 7.0f, $"brain write did not reach the same-tick snapshot (x={after.Value.X})");
    }

    [Fact]
    public void OldClient_WithoutExtension_StillSeesEntity_AndDoesNotDisconnect()
    {
        // Server replicates NpcKind (id 16); the client's registry is the plain movement protocol: it never
        // registered id 16. The old client must SKIP the unknown extension component: keep the session, still see
        // the entity, and simply report the component as absent.
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = SmallCells();
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default, registry: ExtendedRegistry());
        long npcNetId = server.SpawnEntity(6f, 5f, (w, e) => w.Set(e, new NpcKind { Kind = 3 }));

        var oldClient = new WorldClient(ct, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = cfg.TickSeconds, InterpolateRemotes = false });   // default (movement-only) registry
        bool decodeFailed = false;
        oldClient.SnapshotDecodeFailed += _ => decodeFailed = true;
        Pump(server, oldClient, cfg, 12);

        Assert.True(oldClient.Joined);
        Assert.False(decodeFailed);
        Assert.Equal(DisconnectReason.None, oldClient.DisconnectReason);
        Assert.True(ClientSees(oldClient, npcNetId));                              // the entity is visible...
        Assert.False(oldClient.TryGetComponent(npcNetId, out NpcKind _));          // ...just without the unknown kind
    }

    [Fact]
    public void WorldServer_SpawnEntity_AndOnBeforeTick_SingleWorldParity()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4, InterestRadius = 50f,
            SpawnPosition = _ => new Vector3(0f, 0f, 0f) };
        var server = new WorldServer(st, cfg, Flat, MoveTuning.Default, registry: ExtendedRegistry());
        long npcNetId = server.SpawnEntity(3f, 0f, (w, e) => w.Set(e, new NpcKind { Kind = 42 }));

        int calls = 0;
        server.OnBeforeTick += _ => calls++;

        var client = new WorldClient(ct, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = cfg.TickSeconds, InterpolateRemotes = false }, registry: ExtendedRegistry());
        Pump3(server, client, cfg, 12);

        Assert.True(client.Joined);
        Assert.NotEqual(npcNetId, client.LocalNetId);
        Assert.Equal(12, calls);                                 // once per Tick
        Assert.True(client.TryGetComponent(npcNetId, out NpcKind kind));
        Assert.Equal(42, kind.Kind);
    }

    private static void Pump3(WorldServer server, WorldClient client, WorldServerConfig cfg, int frames)
    {
        for (int i = 0; i < frames; i++) { server.Poll(); server.Tick(cfg.TickSeconds); client.Poll(); }
    }
}
