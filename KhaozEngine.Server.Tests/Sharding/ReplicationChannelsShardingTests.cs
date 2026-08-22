using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.Sharding;

/// <summary>
/// End-to-end <see cref="ReplicationChannels"/> behaviour through the sharding consumers: cell persistence
/// (<see cref="CellSim.SnapshotOwned(System.Collections.Generic.IReadOnlySet{long})"/> = Persist), cell handoff (<see cref="ShardHost.ProcessHandoffs"/> = Migrate),
/// border ghosting + home-cell serving (<see cref="ShardHost.SyncGhosts"/> / <see cref="ShardHost.SnapshotForClient"/>
/// = owner-scoped Replicate). Verifies each channel's component survives ONLY its intended path, plus the OwnerOnly
/// no-leak guarantee across a real cell handoff.
/// </summary>
public class ReplicationChannelsShardingTests
{
    private struct Pos : IComponent { public float X; public float Y; }      // id 1, built-in (position accessor)
    private struct Aggro : IComponent { public int V; }                       // Persist|Migrate (server-only)
    private struct MigrateOnly : IComponent { public int V; }                 // Migrate only
    private struct PersistOnly : IComponent { public int V; }                 // Persist only
    private struct Priv : IComponent { public int V; }                        // Default|OwnerOnly

    private const ushort Ext = ReplicationRegistry.FirstExtensionTypeId;

    private static ReplicationRegistry Registry()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(1, (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        r.Register<Aggro>(Ext, (a, bw) => bw.Write(a.V), br => new Aggro { V = br.ReadInt32() },
            channels: ReplicationChannels.Persist | ReplicationChannels.Migrate);
        r.Register<MigrateOnly>((ushort)(Ext + 1), (m, bw) => bw.Write(m.V), br => new MigrateOnly { V = br.ReadInt32() },
            channels: ReplicationChannels.Migrate);
        r.Register<PersistOnly>((ushort)(Ext + 2), (p, bw) => bw.Write(p.V), br => new PersistOnly { V = br.ReadInt32() },
            channels: ReplicationChannels.Persist);
        r.Register<Priv>((ushort)(Ext + 3), (p, bw) => bw.Write(p.V), br => new Priv { V = br.ReadInt32() },
            channels: ReplicationChannels.Default | ReplicationChannels.OwnerOnly);
        return r;
    }

    private static bool PosAccessor(World world, Entity e, out float x, out float y)
    {
        if (world.TryGet(e, out Pos p)) { x = p.X; y = p.Y; return true; }
        x = y = 0f;
        return false;
    }

    private static ShardHost Host(ReplicationRegistry r, float margin) =>
        new(cellSize: 100f, tickSeconds: 0.1f, r, interestCellSize: 100f, overlapMargin: margin, positionAccessor: PosAccessor);

    private static Entity SpawnOwned(ShardHost host, int netId, float x, float y, out CellSim cell)
    {
        Entity e = host.SpawnAt(x, y, out cell);
        cell.World.Set(e, new NetId(netId));
        cell.World.Set(e, new Pos { X = x, Y = y });
        return e;
    }

    private static bool ReplicatedHas<T>(ReplicationRegistry r, World world, int netId) where T : struct, IComponent
    {
        byte[] snap = SnapshotWriter.WriteFiltered(world, r, new HashSet<long> { netId });   // Replicate channel
        var client = new World();
        var view = new ClientReplicationView(r);
        view.Apply(client, snap);
        return view.TryGetEntity(netId, out Entity c) && client.Has<T>(c);
    }

    [Fact]
    public void MigrateOnly_SurvivesHandoff_ButIsNotPersistedNorReplicated()
    {
        ReplicationRegistry r = Registry();
        ShardHost host = Host(r, margin: 0f);
        Entity e = SpawnOwned(host, 7, 95f, 50f, out CellSim a);
        a.World.Set(e, new MigrateOnly { V = 42 });

        a.World.Set(e, new Pos { X = 130f, Y = 50f });   // cross into B
        host.ProcessHandoffs();

        Assert.True(host.TryGetOwner(7, out CellSim b, out Entity moved));
        Assert.Equal(42, b.World.Get<MigrateOnly>(moved).V);                     // migrated

        // Persist channel (what the blob captures) drops it: restoring the cell's persist snapshot has no MigrateOnly.
        byte[] persist = b.SnapshotOwned(new HashSet<long>());
        CellSim restored = new(new CellCoord(9, 9), 0.1f, r, 100f);
        restored.RestoreOwned(persist);
        Assert.True(restored.TryGetOwned(7, out Entity rp));
        Assert.False(restored.World.Has<MigrateOnly>(rp));                       // not persisted

        Assert.False(ReplicatedHas<MigrateOnly>(r, b.World, 7));                 // not replicated
    }

    [Fact]
    public void PersistOnly_SurvivesRestart_ButIsNeverReplicated()
    {
        ReplicationRegistry r = Registry();
        var cell = new CellSim(new CellCoord(0, 0), 0.1f, r, 100f);
        Entity e = cell.World.Spawn();
        cell.World.Set(e, new NetId(7));
        cell.World.Set(e, new PersistOnly { V = 99 });

        // Round-trip through the persist blob (a server restart): the component comes back.
        byte[] blob = cell.SnapshotOwned(new HashSet<long>());
        var restored = new CellSim(new CellCoord(0, 0), 0.1f, r, 100f);
        restored.RestoreOwned(blob);
        Assert.True(restored.TryGetOwned(7, out Entity rp));
        Assert.Equal(99, restored.World.Get<PersistOnly>(rp).V);                 // survived the restart

        Assert.False(ReplicatedHas<PersistOnly>(r, cell.World, 7));              // but never on the replication wire
    }

    [Fact]
    public void ServerOnlyAggro_SurvivesHandoffAndPersist_ButNeverReplicates()
    {
        // The MmoServerSample aggro shape: Persist|Migrate, never Replicate.
        ReplicationRegistry r = Registry();
        ShardHost host = Host(r, margin: 0f);
        Entity e = SpawnOwned(host, 7, 95f, 50f, out CellSim a);
        a.World.Set(e, new Aggro { V = 5 });

        a.World.Set(e, new Pos { X = 130f, Y = 50f });   // cross A -> B
        host.ProcessHandoffs();
        Assert.True(host.TryGetOwner(7, out CellSim b, out Entity moved));
        Assert.Equal(5, b.World.Get<Aggro>(moved).V);                            // aggro migrated

        byte[] persist = b.SnapshotOwned(new HashSet<long>());
        var restored = new CellSim(new CellCoord(9, 9), 0.1f, r, 100f);
        restored.RestoreOwned(persist);
        Assert.True(restored.TryGetOwned(7, out Entity rp));
        Assert.Equal(5, restored.World.Get<Aggro>(rp).V);                        // and persisted

        Assert.False(ReplicatedHas<Aggro>(r, b.World, 7));                       // but no client ever sees it
    }

    [Fact]
    public void Ghosts_DoNotCarry_OwnerOnly_Or_ServerOnly_State()
    {
        ReplicationRegistry r = Registry();
        ShardHost host = Host(r, margin: 30f);
        host.CellFor(150f, 50f);                          // B=(1,0) exists so A can mirror into it
        Entity e = SpawnOwned(host, 7, 90f, 50f, out CellSim a);   // within 30 of east edge -> ghosts into B
        a.World.Set(e, new Priv { V = 1 });               // OwnerOnly
        a.World.Set(e, new Aggro { V = 2 });              // Persist|Migrate, never replicated
        host.SyncGhosts();

        Assert.True(host.TryGetCell(new CellCoord(1, 0), out CellSim b));
        Assert.True(b.TryGetGhost(7, out Entity ghost));
        Assert.True(b.World.Has<Pos>(ghost));             // replicated public state IS mirrored
        Assert.False(b.World.Has<Priv>(ghost));           // OwnerOnly private state is NOT ghosted
        Assert.False(b.World.Has<Aggro>(ghost));          // Persist|Migrate-only server state is NOT ghosted
    }

    [Fact]
    public void OwnerOnly_NeverLeaksToAnObserver_IncludingAcrossAHandoff()
    {
        ReplicationRegistry r = Registry();
        ShardHost host = Host(r, margin: 30f);
        // P in A near the east border; Q owned by B near the west border. Each is inside the other's 30-unit AoI.
        Entity p = SpawnOwned(host, 1, 85f, 50f, out CellSim a);
        a.World.Set(p, new Priv { V = 111 });
        Entity q = SpawnOwned(host, 2, 110f, 50f, out _);
        host.TryGetOwner(2, out CellSim bCell, out Entity qEnt);
        bCell.World.Set(qEnt, new Priv { V = 222 });
        host.SyncGhosts();
        host.BindClient(0, 1);   // P's client
        host.BindClient(1, 2);   // Q's client

        AssertServed(r, host, slot: 0, ownNetId: 1, otherNetId: 2);   // P sees its own Priv, not Q's
        AssertServed(r, host, slot: 1, ownNetId: 2, otherNetId: 1);   // Q sees its own Priv, not P's

        // P crosses A -> B. Priv is Default (so it migrates); its owner-scoping must hold on the new home cell.
        a.World.Set(p, new Pos { X = 115f, Y = 50f });
        host.ProcessHandoffs();
        host.SyncGhosts();
        Assert.True(host.TryGetOwner(1, out CellSim home, out _));
        Assert.Equal(new CellCoord(1, 0), home.Coord);                 // P now served from B

        AssertServed(r, host, slot: 0, ownNetId: 1, otherNetId: 2);   // still: own Priv yes, other's no
        AssertServed(r, host, slot: 1, ownNetId: 2, otherNetId: 1);
    }

    // Serve a client and assert it received its OWN player's Priv but not the other player's.
    private static void AssertServed(ReplicationRegistry r, ShardHost host, int slot, int ownNetId, int otherNetId)
    {
        byte[] snap = host.SnapshotForClient(slot, interestRadius: 30f);
        var client = new World();
        var view = new ClientReplicationView(r);
        view.Apply(client, snap);
        Assert.True(view.TryGetEntity(ownNetId, out Entity own), $"slot {slot} should see own player {ownNetId}");
        Assert.True(view.TryGetEntity(otherNetId, out Entity other), $"slot {slot} should see player {otherNetId}");
        Assert.True(client.Has<Priv>(own));                            // own private state present
        Assert.False(client.Has<Priv>(other));                        // the other player's private state hidden
    }
}
