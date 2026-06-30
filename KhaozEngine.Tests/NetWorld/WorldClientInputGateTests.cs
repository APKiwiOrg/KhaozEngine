using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Regression for the reconnect input-backlog bug: a game loop that keeps calling <see cref="WorldClient.SendInput"/>
/// while the client is not Connected (during a long auto-reconnect outage) must NOT predict or transmit. Before the
/// fix, every outage tick predicted one command forward - inflating the sequence counter and marching the predicted
/// avatar away from the authoritative position - which froze/vibrated the player for the outage's duration on rejoin.
/// </summary>
public class WorldClientInputGateTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveCommand Forward = new(new Vector2(0f, 1f), run: false, cameraYaw: 0f);

    static WorldServer NewServer(KhaozEngine.Netcode.INetTransport t, WorldServerConfig config) =>
        new(t, config, Flat, MoveTuning.Default);

    [Fact]
    public void SendInput_BeforeConnect_DoesNothing()
    {
        var rh = new RestartableHub();
        using var client = new WorldClient(rh.Connect, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = 1f / 30f });

        Assert.NotEqual(WorldConnectionState.Connected, client.ConnectionState);

        for (int i = 0; i < 50; i++)
        {
            int seq = client.SendInput(Forward);
            Assert.Equal(-1, seq);                       // sentinel: nothing predicted/sent while not connected
        }
        Assert.Equal(0f, client.LocalHorizontalSpeed);   // Predict never ran (it is the only writer of this)
        Assert.Equal(Vector3.Zero, client.LocalRenderState.Position);
    }

    [Fact]
    public void Input_held_through_a_reconnect_outage_does_not_run_the_predicted_avatar_away()
    {
        var rh = new RestartableHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = NewServer(rh.ServerTransport, config);

        using var client = new WorldClient(rh.Connect, Flat, MoveTuning.Default,
            new WorldClientConfig
            {
                TickSeconds = config.TickSeconds,
                DisconnectTimeoutSeconds = 0.3f,
                Reconnect = new ReconnectBackoff { InitialSeconds = 0.1f, Multiplier = 2f, MaxSeconds = 0.2f },
            });

        // Connect and walk so prediction is live and the avatar has moved off spawn.
        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(0.016f); }
        Assert.Equal(WorldConnectionState.Connected, client.ConnectionState);
        for (int i = 0; i < 30; i++)
        {
            client.SendInput(Forward);
            server.Poll(); server.Tick(config.TickSeconds);
            client.Poll(0.016f); client.AdvancePresentation(config.TickSeconds);
        }

        // Server dies. Drive the client until it gives up the live session and enters Reconnecting.
        rh.Restart();
        for (int i = 0; i < 40 && client.ConnectionState != WorldConnectionState.Reconnecting; i++)
        {
            client.SendInput(Forward);
            client.Poll(0.05f); client.AdvancePresentation(config.TickSeconds);
        }
        Assert.Equal(WorldConnectionState.Reconnecting, client.ConnectionState);

        // Now HOLD the movement key through a long outage (no server). The predicted avatar must stay put: input
        // sent while not Connected is a no-op. Before the fix this marched the avatar forward one step per tick.
        float zAtOutageStart = client.LocalRenderState.Position.Z;
        for (int i = 0; i < 600; i++)
        {
            int seq = client.SendInput(Forward);
            Assert.Equal(-1, seq);
            client.Poll(0.05f); client.AdvancePresentation(config.TickSeconds);
        }
        // The avatar must not RUN AWAY: with the guard the 600 held commands predict nothing, so drift is only the
        // bounded render settle (the reconciliation offset + inter-tick interpolation decaying to zero, < ~0.2m).
        // Before the fix each held tick predicted ~0.1m forward => ~60m of runaway.
        float drift = MathF.Abs(client.LocalRenderState.Position.Z - zAtOutageStart);
        Assert.True(drift < 0.5f,
            $"predicted avatar drifted {drift:F2}m during the outage (input while not Connected must be a no-op)");
    }
}
