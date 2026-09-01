using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using KhaozEngine.Netcode;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

public class TileWorldServerShardingTests
{
    const float Dt = 0.25f;

    [Fact]
    public void A_watcher_in_the_next_cell_sees_the_crosser_as_a_ghost_then_as_a_resident()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(doc, hub.Server, new TileCoord(60, 10, 0));
        long walker = s.SpawnPlayer(0, "walker", "Wal");
        s.SetPlayerState(0, TileMoveState.At(new TileCoord(60, 10, 0), TileDirection.E));
        s.SpawnPlayer(1, "watcher", "Wat");
        s.SetPlayerState(1, TileMoveState.At(new TileCoord(70, 10, 0), TileDirection.W));
        s.Tick(Dt);

        Assert.True(s.Host.TryGetOwner(walker, out CellSim before, out _));
        Assert.Equal(new CellCoord(0, 0), before.Coord);
        Assert.True(s.Host.TryGetHomeCell(1, out CellSim watcherCell));
        Assert.Equal(new CellCoord(1, 0), watcherCell.Coord);
        Assert.True(watcherCell.TryGetGhost(walker, out _));       // mirrored across the border, not owned

        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(66, 10, 0), TileMoveMode.Run));
        for (int i = 0; i < 16; i++) s.Tick(Dt);

        Assert.True(s.Host.TryGetOwner(walker, out CellSim after, out _));
        Assert.Equal(new CellCoord(1, 0), after.Coord);
        Assert.False(watcherCell.TryGetGhost(walker, out _));       // now a resident of the watcher's own cell
        Assert.True(after.TryGetOwned(walker, out _));
    }

    // PendingTileCommand SURVIVES the handoff. It used to be deliberately unregistered, so a migrated entity
    // arrived in the destination cell without it and fell straight out of TileMovementSystem's query, which reads
    // the three components together. Nothing ever noticed, because the server writes a fresh command onto every
    // player at step 1 and onto every actor at step 1b, both of which run before the next movement pass. That is a
    // rewrite propping up a component the handoff dropped, and any later change that skips the write for an idle
    // entity strands it unmovable on one tile of one map edge.
    //
    // Asserted on the tick the handoff LANDS, before the next tick's write can put it back, which is the only
    // window in which the two behaviours differ. The value is the movement pass's own reset, Continue at the mode
    // the step left the player in, so the mode a running player crossed the border in crosses with them.
    [Fact]
    public void A_migrated_player_arrives_still_carrying_its_pending_command()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(doc, hub.Server, new TileCoord(60, 10, 0));
        long walker = s.SpawnPlayer(0, "walker", "Wal");
        s.SetPlayerState(0, TileMoveState.At(new TileCoord(60, 10, 0), TileDirection.E));
        s.Tick(Dt);
        Assert.True(s.Host.TryGetOwner(walker, out CellSim before, out _));
        Assert.Equal(new CellCoord(0, 0), before.Coord);

        s.Enqueue(0, 0, TileCommand.WalkTo(new TileCoord(66, 10, 0), TileMoveMode.Run));
        CellSim? arrived = null;
        Entity entity = default;
        for (int i = 0; i < 24 && arrived is null; i++)
        {
            s.Tick(Dt);
            Assert.True(s.Host.TryGetOwner(walker, out CellSim owner, out Entity e));
            if (owner.Coord != new CellCoord(0, 0)) { arrived = owner; entity = e; }
        }

        Assert.NotNull(arrived);
        Assert.Equal(new CellCoord(1, 0), arrived.Coord);
        Assert.True(arrived.World.TryGet(entity, out PendingTileCommand pending),
            "the migrated entity arrived carrying its pending command");
        Assert.Equal(TileCommand.Continue(TileMoveMode.Run), pending.Command);
    }

    [Fact]
    public void A_viewer_never_receives_an_entity_on_another_plane()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(
            TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        // Slots the joining connection cannot take: NetServer seats it at the lowest free one, and a manual spawn
        // on that seat would be released by the join rather than observed by it.
        long ground = s.SpawnPlayer(4, "ground", "G");
        long upstairs = s.SpawnPlayer(5, "upstairs", "U");
        s.SetPlayerState(5, TileMoveState.At(new TileCoord(11, 10, 1), TileDirection.W));

        INetTransport c = hub.CreateClient();
        var net = new NetClient(c, System.Text.Encoding.UTF8.GetBytes("ground"));
        net.Poll();
        s.Poll();
        s.Tick(Dt);
        net.Poll();

        byte[]? frame = null;
        while (net.TryDequeueEvent(out ClientSessionEvent ev))
            if (ev.Kind == ClientSessionEventKind.Data && TileProtocol.ServerFrameTag(ev.Data) == TileProtocol.ServerFrameSnapshot)
                frame = ev.Data;
        Assert.NotNull(frame);
        Assert.True(TileProtocol.TryDecodeSnapshotFrame(frame, out long localNetId, out _, out _, out byte[] snapshot));

        var world = new World();
        new ClientReplicationView(TileProtocol.CreateRegistry()).Apply(world, snapshot);
        var seen = new List<long>();
        world.ForEach<NetId>((Entity e, ref NetId id) => seen.Add(id.Value));
        Assert.Contains(localNetId, seen);
        Assert.DoesNotContain(upstairs, seen);
        Assert.Contains(ground, seen);
    }
}
