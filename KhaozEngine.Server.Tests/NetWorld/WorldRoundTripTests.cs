using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Netcode.LiteNetLib;
using KhaozEngine.NetWorld;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.NetWorld;

public class WorldRoundTripTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    private readonly ITestOutputHelper output;
    public WorldRoundTripTests(ITestOutputHelper output) => this.output = output;

    static (WorldServer server, WorldServerConfig config) NewServer(INetTransport t)
    {
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        return (new WorldServer(t, config, Flat, MoveTuning.Default), config);
    }

    [Fact]
    public void Client_move_command_moves_its_server_entity_and_returns_via_replication()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        (WorldServer server, WorldServerConfig config) = NewServer(st);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });

        // Connect + first serve (no input) to establish the prediction basis.
        for (int i = 0; i < 4; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        Assert.True(client.LocalNetId > 0);

        float zBefore = LocalZ(client);

        // Walk forward (W = +Y axis at yaw 0 -> -Z) for several ticks.
        var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f);
        for (int i = 0; i < 10; i++)
        {
            client.SendInput(forward);
            server.Poll();
            server.Tick(config.TickSeconds);
            client.Poll();
            client.AdvancePresentation(config.TickSeconds); // drive the render smoothing toward the predicted position
        }

        float zAfter = LocalZ(client);
        Assert.True(zAfter < zBefore - 0.1f, $"expected forward motion, before {zBefore} after {zAfter}");
    }

    static float LocalZ(WorldClient client)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.IsLocal) return e.Position.Z;
        throw new Xunit.Sdk.XunitException("no local entity in client snapshot");
    }

    [Fact]
    public void Two_clients_each_see_the_other_move()
    {
        var hub = new InMemoryTransportHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var a = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });
        var b = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });

        // Connect both + establish bases.
        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); a.Poll(); b.Poll(); }
        Assert.True(a.Joined && b.Joined);
        Assert.Equal(2, server.PlayerCount);
        Assert.NotEqual(a.LocalNetId, b.LocalNetId);

        Vector3 bSeenByA_before = RemotePos(a, b.LocalNetId);
        Vector3 aSeenByB_before = RemotePos(b, a.LocalNetId);

        var aForward = new MoveCommand(new Vector2(0f, 1f), false, 0f);   // -Z
        var bRight = new MoveCommand(new Vector2(1f, 0f), false, 0f);     // +X
        for (int i = 0; i < 12; i++)
        {
            a.SendInput(aForward);
            b.SendInput(bRight);
            server.Poll();
            server.Tick(config.TickSeconds);
            a.Poll();
            b.Poll();
        }

        Vector3 aSeenByB_after = RemotePos(b, a.LocalNetId);
        Vector3 bSeenByA_after = RemotePos(a, b.LocalNetId);
        Assert.True(aSeenByB_after.Z < aSeenByB_before.Z - 0.1f, "B should see A move -Z");
        Assert.True(bSeenByA_after.X > bSeenByA_before.X + 0.1f, "A should see B move +X");
    }

    static Vector3 RemotePos(WorldClient observer, long remoteNetId)
    {
        foreach (EntityRenderState e in observer.Snapshot())
            if (!e.IsLocal && e.Id.Value == remoteNetId) return e.Position;
        throw new Xunit.Sdk.XunitException($"remote {remoteNetId} not visible");
    }

    [Fact]
    public void Remote_render_state_surfaces_replicated_grounded_and_vertical_velocity()
    {
        // The animator bridge needs the EXACT air state for remotes (deriving it from a remote's terrain-following
        // position misfires - the faster it moves over a slope, the more it looks like falling). Snapshot() must
        // surface the replicated MovementState (grounded + vertical velocity) on each remote's EntityRenderState.
        var hub = new InMemoryTransportHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);
        var a = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });
        var b = new WorldClient(hub.CreateClient(), Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });

        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(config.TickSeconds); a.Poll(); b.Poll(); }
        Assert.True(a.Joined && b.Joined);

        // At rest on flat ground, B sees A grounded with ~zero vertical velocity.
        for (int i = 0; i < 6; i++)
        {
            a.SendInput(MoveCommand.Idle); b.SendInput(MoveCommand.Idle);
            server.Poll(); server.Tick(config.TickSeconds); a.Poll(); b.Poll();
        }
        EntityRenderState rest = RemoteState(b, a.LocalNetId);
        Assert.True(rest.Grounded, "B should see A grounded at rest");
        Assert.True(MathF.Abs(rest.VerticalVelocity) < 1e-2f, $"vertical velocity should be ~0 at rest, got {rest.VerticalVelocity}");

        // A jumps: B must read A airborne + rising from the replicated MovementState, not derived from position.
        a.SendInput(new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: true));
        for (int i = 0; i < 2; i++)
        {
            b.SendInput(MoveCommand.Idle);
            server.Poll(); server.Tick(config.TickSeconds); a.Poll(); b.Poll();
        }
        EntityRenderState jumping = RemoteState(b, a.LocalNetId);
        Assert.False(jumping.Grounded, "B should see A leave the ground");
        Assert.True(jumping.VerticalVelocity > 0f, $"B should see A rising, got {jumping.VerticalVelocity}");
    }

    static EntityRenderState RemoteState(WorldClient observer, long remoteNetId)
    {
        foreach (EntityRenderState e in observer.Snapshot())
            if (!e.IsLocal && e.Id.Value == remoteNetId) return e;
        throw new Xunit.Sdk.XunitException($"remote {remoteNetId} not visible");
    }

    [Fact]
    public void Reconnect_on_recycled_slot_can_move()
    {
        var hub = new InMemoryTransportHub();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(hub.Server, config, Flat, MoveTuning.Default);

        // Client A joins on slot 0 and plays enough ticks to push that slot's processed high-water mark up.
        INetTransport aTransport = hub.CreateClient();
        var a = new NetClient(aTransport, TestHandshake.Wire());
        int slotA = JoinNetClient(server, a, config);

        var forward = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f);   // -Z
        const int played = 40;
        for (int seq = 0; seq < played; seq++)
        {
            a.Send(MoveProtocol.EncodeMove(seq, forward), NetChannelReliability.ReliableOrdered);
            a.Poll(); server.Poll(); server.Tick(config.TickSeconds);
        }
        Assert.True(server.TryGetPlayerState(slotA, out PlayerMoveState aState));
        Assert.True(aState.Position.Z < -0.1f, "A should have moved before leaving");

        // A disconnects; the server frees slot 0 for reuse.
        hub.DisconnectClient(aTransport);
        server.Poll(); server.Tick(config.TickSeconds);
        Assert.Equal(0, server.PlayerCount);

        // Client B joins on the recycled slot 0, sending forward commands that legitimately restart at seq 0.
        var b = new NetClient(hub.CreateClient(), TestHandshake.Wire());
        int slotB = JoinNetClient(server, b, config);
        Assert.Equal(slotA, slotB);   // same slot, recycled

        Assert.True(server.TryGetPlayerState(slotB, out PlayerMoveState bSpawn));
        float zSpawn = bSpawn.Position.Z;
        for (int seq = 0; seq < 20; seq++)
        {
            b.Send(MoveProtocol.EncodeMove(seq, forward), NetChannelReliability.ReliableOrdered);
            b.Poll(); server.Poll(); server.Tick(config.TickSeconds);
        }
        Assert.True(server.TryGetPlayerState(slotB, out PlayerMoveState bMoved));
        Assert.True(bMoved.Position.Z < zSpawn - 0.1f,
            $"reconnect on recycled slot froze the player: spawn z {zSpawn} -> {bMoved.Position.Z}");
    }

    static int JoinNetClient(WorldServer server, NetClient client, WorldServerConfig cfg)
    {
        for (int i = 0; i < 200; i++)
        {
            client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds);
            if (client.Slot >= 0 && server.TryGetPlayerNetId(client.Slot, out _)) return client.Slot;
        }
        throw new Xunit.Sdk.XunitException("client never joined");
    }

    [Trait("Category", "LiveSocket")]
    [Fact]
    public void LiveSocket_client_connects_and_is_served_its_player()
    {
        // Bind to an OS-assigned ephemeral port, never a fixed one: a hardcoded port collides with any other
        // process (a stale server, a parallel test run) that happens to hold it.
        using LiteNetLibServerTransport? st = LiveSocketSupport.TryBindServer(out int port);
        if (st is null) { output.WriteLine(LiveSocketSupport.NoFreePortReason); return; }

        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        using var ct = new LiteNetLibClientTransport("127.0.0.1", port);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool served = false;
        while (sw.ElapsedMilliseconds < 3000 && !served)
        {
            server.Poll();
            server.Tick(config.TickSeconds);
            client.Poll();
            if (client.Joined && client.LocalNetId > 0)
                foreach (EntityRenderState e in client.Snapshot())
                    if (e.IsLocal) served = true;
            System.Threading.Thread.Sleep(10);
        }
        Assert.True(served, "client never received its player over a live socket");
    }
}
