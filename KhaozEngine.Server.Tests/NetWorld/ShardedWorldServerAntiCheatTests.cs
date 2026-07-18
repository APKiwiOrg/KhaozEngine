using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The same server-side anomaly hook on the multi-cell <see cref="ShardedWorldServer"/> as on
/// <see cref="WorldServer"/> (parity): malformed/NaN packets, message floods, and a player driving into the
/// authoritative play-area boundary all surface through <see cref="ShardedWorldServer.OnSuspiciousActivity"/>.
/// </summary>
public class ShardedWorldServerAntiCheatTests
{
    static float Flat(float x, float z) => 0f;
    const NetChannelReliability Unrel = NetChannelReliability.UnreliableSequenced;

    // One large cell so the play area fits inside it (no handoff complicates the correction test).
    static ShardedWorldServerConfig Config(AntiCheatConfig anti, Func<int, Vector3>? spawn = null) => new()
    {
        TickSeconds = 1f / 30f,
        CellSize = 120f,
        OverlapMargin = 24f,
        InterestRadius = 24f,
        MaxPlayers = 8,
        SpawnPosition = spawn,
        AntiCheat = anti,
    };

    static (ShardedWorldServer server, NetClient client) Connect(ShardedWorldServerConfig cfg, WorldBounds? bounds,
        List<SuspiciousActivity> flags)
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default, bounds: bounds);
        server.OnSuspiciousActivity += flags.Add;
        var client = new NetClient(ct, TestHandshake.Wire());
        for (int i = 0; i < 200 && server.PlayerCount == 0; i++)
        {
            client.Poll();
            server.Poll();
            server.Tick(cfg.TickSeconds);
        }
        Assert.Equal(1, server.PlayerCount);
        return (server, client);
    }

    [Fact]
    public void Malformed_packet_fires_the_hook()
    {
        var flags = new List<SuspiciousActivity>();
        var (server, client) = Connect(Config(new AntiCheatConfig()), null, flags);

        client.Send(new byte[] { 9, 9 }, Unrel);
        client.Poll();
        server.Poll();

        Assert.Contains(flags, f => f.Reason == SuspiciousReason.MalformedPacket && f.Slot == client.Slot);
    }

    [Fact]
    public void Message_flood_trips_the_rate_limiter()
    {
        var flags = new List<SuspiciousActivity>();
        var (server, client) = Connect(
            Config(new AntiCheatConfig { MaxMessagesPerSecond = 30f, MessageBurst = 3f }), null, flags);
        flags.Clear();

        var cmd = new MoveCommand(new Vector2(1f, 0f), false, 0f);
        for (int i = 0; i < 8; i++) client.Send(MoveProtocol.EncodeMove(i, cmd), Unrel);
        client.Poll();
        server.Poll();

        Assert.Contains(flags, f => f.Reason == SuspiciousReason.RateLimited && f.Slot == client.Slot);
    }

    [Fact]
    public void Repeatedly_driving_into_the_boundary_fires_the_correction_hook()
    {
        var flags = new List<SuspiciousActivity>();
        const float R = 5f;
        var cfg = Config(new AntiCheatConfig { MaxCorrectionDistance = 0.05f, CorrectionStreak = 3 },
            spawn: _ => new Vector3(0f, 0f, R));
        var (server, client) = Connect(cfg, new CircleBounds(Vector2.Zero, R), flags);

        var pushOut = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: MathF.PI);   // forward = +Z, into the wall
        for (int i = 0; i < 12; i++)
        {
            client.Send(MoveProtocol.EncodeMove(i, pushOut), Unrel);
            client.Poll();
            server.Poll();
            server.Tick(cfg.TickSeconds);
        }

        Assert.Contains(flags, f => f.Reason == SuspiciousReason.MovementCorrection && f.Slot == client.Slot);
    }
}
