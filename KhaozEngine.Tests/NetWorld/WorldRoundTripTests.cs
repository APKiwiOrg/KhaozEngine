using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldRoundTripTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

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
        var hub = new InMemoryHub();
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

    static Vector3 RemotePos(WorldClient observer, int remoteNetId)
    {
        foreach (EntityRenderState e in observer.Snapshot())
            if (!e.IsLocal && e.Id.Value == remoteNetId) return e.Position;
        throw new Xunit.Sdk.XunitException($"remote {remoteNetId} not visible");
    }
}
