using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ShardedWorldServerAdminTests
{
    private static float Flat(float x, float z) => 0f;

    private static ShardedWorldServer JoinOne(string account, out NetClient client, out ShardedWorldServerConfig config, out int slot)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        config = new ShardedWorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 8 };
        var server = new ShardedWorldServer(st, config, Flat, MoveTuning.Default);
        client = new NetClient(ct, TestHandshake.Wire(account));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }
        server.Tick(config.TickSeconds);   // publish snapshot
        slot = server.JoinedSlots.First();
        return server;
    }

    [Fact]
    public void ListOnline_ReflectsJoinedPlayer()
    {
        ShardedWorldServer server = JoinOne("alice", out _, out _, out _);
        IReadOnlyList<OnlinePlayer> online = server.ListOnline();
        Assert.Single(online);
        Assert.Equal("alice", online[0].AccountId);
    }

    [Fact]
    public void Teleport_MovesAuthoritativeState_AcrossCells()
    {
        ShardedWorldServer server = JoinOne("alice", out _, out ShardedWorldServerConfig config, out int slot);
        // CellSize default 60: teleport well into a different cell.
        server.Teleport(PlayerRef.Account("alice"), new Vector3(150f, 0f, 150f));
        for (int i = 0; i < 4; i++) { server.Poll(); server.Tick(config.TickSeconds); }
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState s));
        Assert.Equal(150f, s.Position.X, 2);
        Assert.Equal(150f, s.Position.Z, 2);
    }

    [Fact]
    public void Kick_DisconnectsPlayer()
    {
        ShardedWorldServer server = JoinOne("alice", out NetClient client, out ShardedWorldServerConfig config, out int slot);
        server.Kick(PlayerRef.Slot(slot), "bye");
        for (int i = 0; i < 60 && server.PlayerCount > 0; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.Equal(0, server.PlayerCount);
    }
}
