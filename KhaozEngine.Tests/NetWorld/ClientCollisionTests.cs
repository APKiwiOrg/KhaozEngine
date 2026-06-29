using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Verifies that WorldClient and WorldServer with no physics world (null = terrain-only) produce
/// identical predictions. The IPhysicsWorld-backed collision path is tested in Physics/NetWorldPhysicsTests.cs.
/// </summary>
public class ClientCollisionTests
{
    const float Dt = 1f / 30f;
    static float Flat(float x, float z) => 0f;
    static MoveCommand Forward => new(new Vector2(0f, 1f), run: false, cameraYaw: 0f); // W at yaw 0 -> -Z

    static WorldServerConfig SpawnAtOrigin() =>
        new() { TickSeconds = Dt, InterestRadius = 500f, SpawnPosition = _ => Vector3.Zero };

    // Settle the session: spawn + first serve (no input) so the client has its local entity and a prediction basis.
    static void EstablishBasis(WorldServer server, WorldClient client)
    {
        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(Dt); client.Poll(); }
        Assert.True(client.Joined && client.LocalNetId > 0, "client never joined / got its local entity");
    }

    // The server's spawn basis is one idle step from the spawn position; an authority that reproduces the client's
    // prediction must start from the same point.
    static PlayerMoveState SpawnBasis(PlayerMoveSimulator authority) =>
        authority.Step(new PlayerMoveState { Position = Vector3.Zero }, MoveCommand.Idle, Dt);

    [Fact]
    public void Client_without_physics_predicts_matching_authority()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = SpawnAtOrigin();
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = Dt });
        EstablishBasis(server, client);

        for (int i = 0; i < 60; i++) client.SendInput(Forward);
        client.AdvancePresentation(Dt);
        Vector3 predicted = LocalPos(client);

        var authority = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        PlayerMoveState auth = SpawnBasis(authority);
        for (int i = 0; i < 60; i++) auth = authority.Step(auth, Forward, Dt);
        Assert.Equal(auth.Position.X, predicted.X, 1);
        Assert.Equal(auth.Position.Z, predicted.Z, 1);
    }

    [Fact]
    public void Client_without_args_is_terrain_only_unchanged()
    {
        // Regression: omit the new optional args. Prediction is terrain-only (unchanged), so the player walks
        // freely, matching a terrain-only authoritative simulator.
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = SpawnAtOrigin();
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = Dt });
        EstablishBasis(server, client);

        for (int i = 0; i < 60; i++) client.SendInput(Forward);
        client.AdvancePresentation(Dt);
        Vector3 predicted = LocalPos(client);
        Assert.True(predicted.Z < -0.5f, $"terrain-only client should move freely, z={predicted.Z}");

        var authority = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        PlayerMoveState auth = SpawnBasis(authority);
        for (int i = 0; i < 60; i++) auth = authority.Step(auth, Forward, Dt);
        Assert.Equal(auth.Position.X, predicted.X, 1);
        Assert.Equal(auth.Position.Z, predicted.Z, 1);
    }

    [Fact]
    public void Client_terrain_height_applies_correctly()
    {
        // Raised terrain height: client settles at capsule half height above the terrain.
        const float TerrainY = 5f;
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = SpawnAtOrigin();
        float RaisedTerrain(float x, float z) => TerrainY;
        var server = new WorldServer(st, config, RaisedTerrain, MoveTuning.Default);
        var client = new WorldClient(ct, RaisedTerrain, MoveTuning.Default, new WorldClientConfig { TickSeconds = Dt });
        EstablishBasis(server, client);

        for (int i = 0; i < 30; i++) client.SendInput(MoveCommand.Idle);
        client.AdvancePresentation(Dt);
        Vector3 predicted = LocalPos(client);

        float expected = TerrainY + MoveTuning.Default.CapsuleHalfHeight;
        Assert.Equal(expected, predicted.Y, 1);
    }

    static Vector3 LocalPos(WorldClient client)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.IsLocal) return e.Position;
        throw new XunitException("no local entity in client snapshot");
    }
}
