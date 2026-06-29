using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldServerAdminTests
{
    private static float Flat(float x, float z) => 0f;

    private static WorldServer JoinOne(string account, out NetClient client, out WorldServerConfig config, out int slot)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        client = new NetClient(ct, Encoding.UTF8.GetBytes(account));
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++) { client.Poll(); server.Poll(); server.Tick(config.TickSeconds); }
        server.Tick(config.TickSeconds);   // publish the online snapshot
        slot = server.JoinedSlots.First();
        return server;
    }

    [Fact]
    public void ListOnline_ReflectsJoinedPlayer()
    {
        WorldServer server = JoinOne("alice", out _, out _, out _);
        IReadOnlyList<OnlinePlayer> online = server.ListOnline();
        Assert.Single(online);
        Assert.Equal("alice", online[0].AccountId);
    }

    [Fact]
    public void Teleport_ByAccount_MovesAuthoritativeState()
    {
        WorldServer server = JoinOne("alice", out NetClient client, out WorldServerConfig config, out int slot);
        server.Teleport(PlayerRef.Account("alice"), new Vector3(50f, 0f, 70f));
        server.Poll(); server.Tick(config.TickSeconds);
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState s));
        Assert.Equal(50f, s.Position.X, 3);
        Assert.Equal(70f, s.Position.Z, 3);
    }

    [Fact]
    public void Kick_BySlot_DisconnectsPlayer()
    {
        WorldServer server = JoinOne("alice", out NetClient client, out WorldServerConfig config, out int slot);
        server.Kick(PlayerRef.Slot(slot), "bye");
        for (int i = 0; i < 60 && server.PlayerCount > 0; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.Equal(0, server.PlayerCount);
    }
}
