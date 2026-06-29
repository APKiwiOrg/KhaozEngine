using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldClientReconnectTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    static WorldServer NewServer(KhaozEngine.Netcode.INetTransport t, WorldServerConfig config) =>
        new(t, config, Flat, MoveTuning.Default);

    [Fact]
    public void Reconnects_through_Reconnecting_back_to_Connected_after_a_restart()
    {
        var rh = new RestartableHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = NewServer(rh.ServerTransport, config);

        using var client = new WorldClient(rh.Connect, Flat, MoveTuning.Default,
            new WorldClientConfig
            {
                TickSeconds = config.TickSeconds,
                DisconnectTimeoutSeconds = 0.5f,
                Reconnect = new ReconnectBackoff { InitialSeconds = 0.1f, Multiplier = 2f, MaxSeconds = 0.2f },
            });

        // Initial connect.
        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(0.016f); }
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
        int firstNetId = client.LocalNetId;
        Assert.True(firstNetId > 0);

        // Restart: new server process. Stop ticking the old one; the client starves into Reconnecting.
        rh.Restart();
        var server2 = NewServer(rh.ServerTransport, config);

        bool sawReconnecting = false;
        for (int i = 0; i < 200; i++)
        {
            server2.Poll(); server2.Tick(config.TickSeconds); client.Poll(0.05f);
            if (client.ConnectionState == WorldConnectionState.Reconnecting) sawReconnecting = true;
            if (client.ConnectionState == WorldConnectionState.Connected && sawReconnecting) break;
        }

        Assert.True(sawReconnecting, "client never entered Reconnecting");
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
        Assert.True(client.LocalNetId > 0, "no local net id after reconnect");

        // Replication resumed: the avatar is visible and controllable again.
        var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f);
        float zBefore = LocalZ(client);
        for (int i = 0; i < 12; i++)
        {
            client.SendInput(forward);
            server2.Poll(); server2.Tick(config.TickSeconds);
            client.Poll(0.016f); client.AdvancePresentation(config.TickSeconds);
        }
        Assert.True(LocalZ(client) < zBefore - 0.1f, "avatar not controllable after reconnect");
    }

    static float LocalZ(WorldClient client)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.IsLocal) return e.Position.Z;
        throw new Xunit.Sdk.XunitException("no local entity after reconnect");
    }
}
