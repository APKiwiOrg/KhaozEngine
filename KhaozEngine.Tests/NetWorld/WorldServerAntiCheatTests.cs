using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The server-side anomaly hook (<see cref="WorldServer.OnSuspiciousActivity"/>): the engine signals, the game
/// decides policy. Covers all three triggers - a malformed/NaN move packet, a per-connection message flood, and a
/// player repeatedly driving into the authoritative play-area boundary - plus the no-false-positive case.
/// </summary>
public class WorldServerAntiCheatTests
{
    static float Flat(float x, float z) => 0f;
    const NetChannelReliability Unrel = NetChannelReliability.UnreliableSequenced;

    static (WorldServer server, NetClient client) Connect(WorldServerConfig config, WorldBounds? bounds,
        List<SuspiciousActivity> flags)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var server = new WorldServer(st, config, Flat, MoveTuning.Default, bounds: bounds);
        server.OnSuspiciousActivity += flags.Add;
        var client = new NetClient(ct);
        for (int i = 0; i < 100 && server.PlayerCount == 0; i++)
        {
            client.Poll();
            server.Poll();
            server.Tick(config.TickSeconds);
        }
        Assert.Equal(1, server.PlayerCount);
        return (server, client);
    }

    [Fact]
    public void Malformed_packet_fires_the_hook()
    {
        var flags = new List<SuspiciousActivity>();
        var (server, client) = Connect(new WorldServerConfig { MaxPlayers = 2 }, null, flags);

        client.Send(new byte[] { 1, 2, 3 }, Unrel);   // too short to be a move command
        client.Poll();
        server.Poll();

        Assert.Contains(flags, f => f.Reason == SuspiciousReason.MalformedPacket && f.Slot == client.Slot);
    }

    [Fact]
    public void Nan_move_command_fires_the_malformed_hook()
    {
        var flags = new List<SuspiciousActivity>();
        var (server, client) = Connect(new WorldServerConfig { MaxPlayers = 2 }, null, flags);

        // A reverse-engineered client crafts an 18-byte packet whose move axis is NaN; the decode rejects it.
        byte[] poisoned = MoveProtocol.EncodeMove(1, new MoveCommand(new Vector2(float.NaN, 0f), false, 0f));
        client.Send(poisoned, Unrel);
        client.Poll();
        server.Poll();

        Assert.Contains(flags, f => f.Reason == SuspiciousReason.MalformedPacket);
    }

    [Fact]
    public void Message_flood_trips_the_rate_limiter()
    {
        var flags = new List<SuspiciousActivity>();
        var config = new WorldServerConfig
        {
            MaxPlayers = 2,
            AntiCheat = new AntiCheatConfig { MaxMessagesPerSecond = 30f, MessageBurst = 3f },
        };
        var (server, client) = Connect(config, null, flags);
        flags.Clear();   // ignore anything observed during the handshake phase

        var cmd = new MoveCommand(new Vector2(0f, 1f), false, 0f);
        for (int i = 0; i < 8; i++) client.Send(MoveProtocol.EncodeMove(i, cmd), Unrel);
        client.Poll();
        server.Poll();   // bucket refills once (full = 3 burst), so 3 pass and the rest are rejected

        Assert.Contains(flags, f => f.Reason == SuspiciousReason.RateLimited && f.Slot == client.Slot);
    }

    [Fact]
    public void Repeatedly_driving_into_the_boundary_fires_the_correction_hook()
    {
        var flags = new List<SuspiciousActivity>();
        const float R = 5f;
        var config = new WorldServerConfig
        {
            MaxPlayers = 2,
            SpawnPosition = _ => new Vector3(0f, 0f, R),   // start on the +Z edge of the play area
            AntiCheat = new AntiCheatConfig { MaxCorrectionDistance = 0.05f, CorrectionStreak = 3 },
        };
        var (server, client) = Connect(config, new CircleBounds(Vector2.Zero, R), flags);

        // Push straight out through the wall every tick (yaw = PI -> camera-forward = +Z). The clamp denies the full
        // move each tick (~walk*dt = 0.1 > 0.05), so the correction streak builds and the hook fires.
        var pushOut = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: MathF.PI);
        for (int i = 0; i < 12; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i, pushOut), Unrel);
            client.Poll();
            server.Poll();
            server.Tick(config.TickSeconds);
        }

        Assert.Contains(flags, f => f.Reason == SuspiciousReason.MovementCorrection && f.Slot == client.Slot);
    }

    [Fact]
    public void Free_movement_does_not_fire_the_correction_hook()
    {
        var flags = new List<SuspiciousActivity>();
        var config = new WorldServerConfig
        {
            MaxPlayers = 2,
            AntiCheat = new AntiCheatConfig { MaxCorrectionDistance = 0.05f, CorrectionStreak = 3 },
        };
        var (server, client) = Connect(config, null, flags);   // unbounded: nothing to clamp

        var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f);
        for (int i = 0; i < 20; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i, forward), Unrel);
            client.Poll();
            server.Poll();
            server.Tick(config.TickSeconds);
        }

        Assert.DoesNotContain(flags, f => f.Reason == SuspiciousReason.MovementCorrection);
    }
}
