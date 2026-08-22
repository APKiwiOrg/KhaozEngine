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

    [Fact]
    public void A_viewer_never_receives_an_entity_on_another_plane()
    {
        var hub = new InMemoryTransportHub();
        using TileWorldServer s = TileWorldServerTickTests.Server(
            TileMoveSimulatorTests.FlatWorld(), hub.Server, new TileCoord(10, 10, 0));
        long ground = s.SpawnPlayer(0, "ground", "G");
        long upstairs = s.SpawnPlayer(1, "upstairs", "U");
        s.SetPlayerState(1, TileMoveState.At(new TileCoord(11, 10, 1), TileDirection.W));

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
