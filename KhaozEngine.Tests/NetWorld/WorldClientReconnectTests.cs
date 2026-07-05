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
        long firstNetId = client.LocalNetId;
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

    [Fact]
    public void Input_in_the_pre_first_snapshot_reconnect_gap_does_not_break_post_reconnect_movement()
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

        var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f);

        // Initial connect.
        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(0.016f); }
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);

        // Move for a while so the command sequence number climbs well above 0. A brand-new client sitting at
        // seq 0 cannot desync; the real repro is "connect, MOVE the player, restart" - the player has been
        // walking, so its seq counter is high when the server restarts.
        for (int i = 0; i < 100; i++)
        {
            client.SendInput(forward);
            server.Poll(); server.Tick(config.TickSeconds);
            client.Poll(0.016f); client.AdvancePresentation(config.TickSeconds);
        }

        // Restart: a fresh server process. Keep holding the movement key (send input every frame, exactly as a
        // real game loop does) all the way through the reconnect, INCLUDING the handshake. Those commands carry
        // the continuing high seq, so the fresh server stores + acks them as the slot joins - filling the
        // post-join / pre-first-snapshot gap. The existing reconnect test never does this (it only sends input
        // after the first snapshot has landed), which is why it misses the desync.
        rh.Restart();
        var server2 = NewServer(rh.ServerTransport, config);

        bool sawReconnecting = false;
        for (int i = 0; i < 400; i++)
        {
            client.SendInput(forward);
            server2.Poll(); server2.Tick(config.TickSeconds);
            client.Poll(0.05f); client.AdvancePresentation(config.TickSeconds);
            if (client.ConnectionState == WorldConnectionState.Reconnecting) sawReconnecting = true;
            if (client.ConnectionState == WorldConnectionState.Connected && sawReconnecting && client.LocalNetId > 0)
                break;
        }

        Assert.True(sawReconnecting, "client never entered Reconnecting");
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
        Assert.True(client.LocalNetId > 0, "no local net id after reconnect");

        // Let a couple of snapshots settle so any reconnect re-seed has happened.
        for (int i = 0; i < 4; i++)
        {
            client.SendInput(forward);
            server2.Poll(); server2.Tick(config.TickSeconds);
            client.Poll(0.016f); client.AdvancePresentation(config.TickSeconds);
        }

        // The real assertion: post-reconnect input must actually move the player. On the bug the fresh server
        // advanced its acknowledged seq from the gap commands while the client reset its seq back to 0, so the
        // server rejects every command (seq <= ack) and the avatar is pinned at the authoritative spawn while
        // prediction rubber-bands. ~40 ticks of forward at 3 m/s is ~4 m of genuine movement; the bug leaves
        // only the bounded (~0.1 m, decaying) prediction wobble around spawn.
        float zBefore = LocalZ(client);
        for (int i = 0; i < 40; i++)
        {
            client.SendInput(forward);
            server2.Poll(); server2.Tick(config.TickSeconds);
            client.Poll(0.016f); client.AdvancePresentation(config.TickSeconds);
        }
        float zAfter = LocalZ(client);
        Assert.True(zAfter < zBefore - 2.0f,
            $"avatar did not respond to input after reconnect (zBefore={zBefore}, zAfter={zAfter}); " +
            "post-reconnect commands are being dropped by the server (sequence desync)");
    }

    static float LocalZ(WorldClient client)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.IsLocal) return e.Position.Z;
        throw new Xunit.Sdk.XunitException("no local entity after reconnect");
    }
}
