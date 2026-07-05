using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldServerPersistenceHooksTests
{
    private static float FlatGround(float x, float z) => 0f;

    // Drives a loopback client into a WorldServer until it has joined, returning the server + captured joins.
    private static WorldServer JoinOneClient(byte[] token, out int joinedSlot, out List<(int slot, string acct)> joins)
    {
        var (serverTransport, clientTransport) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, MaxPlayers = 4 };
        var server = new WorldServer(serverTransport, config, FlatGround, MoveTuning.Default);
        var captured = new List<(int slot, string acct)>();
        server.PlayerJoined += (slot, acct) => captured.Add((slot, acct));

        var client = new NetClient(clientTransport, TestHandshake.Wire(token));
        for (int i = 0; i < 200 && captured.Count == 0; i++)
        {
            client.Poll();
            server.Poll();
            server.Tick(config.TickSeconds);
        }
        joins = captured;
        joinedSlot = captured.Count > 0 ? captured[0].slot : -1;
        return server;
    }

    [Fact]
    public void PlayerJoined_DerivesAccountIdFromConnectToken()
    {
        WorldServer server = JoinOneClient(Encoding.UTF8.GetBytes("acct-123"), out int slot, out var joins);
        Assert.Single(joins);
        Assert.Equal("acct-123", joins[0].acct);
        Assert.True(server.TryGetAccountId(slot, out string acct));
        Assert.Equal("acct-123", acct);
    }

    [Fact]
    public void SetPlayerState_OverridesPositionAndState()
    {
        WorldServer server = JoinOneClient(Encoding.UTF8.GetBytes("acct-x"), out int slot, out _);
        var target = new PlayerMoveState { Position = new Vector3(50f, 0f, -25f) };
        server.SetPlayerState(slot, target);
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState got));
        Assert.Equal(target.Position, got.Position);
        Assert.Contains(slot, server.JoinedSlots);
    }
}
