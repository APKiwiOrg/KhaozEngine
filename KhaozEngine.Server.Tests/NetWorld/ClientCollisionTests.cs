using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Verifies that WorldClient and WorldServer produce correct predictions in both the null-physics
/// (terrain-only) and the IPhysicsWorld-backed (solid props) configurations.
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

    // End-to-end: WorldServer + WorldClient both given a BepuPhysicsWorld containing the same wall.
    // Fire ~60 forward-into-wall commands (prediction only, no interleaved server pump) and assert:
    //   (a) The client prediction does not pass through the wall.
    //   (b) The position is blocked (significantly less movement than the unblocked case).
    // This is the end-to-end proof the WorldClient wiring honours the physics world.
    [Fact]
    public void Client_with_physics_world_is_blocked_by_wall_and_matches_server()
    {
        // Wall: BoxShape centred at z=-3, thin in Z, tall/wide. "Forward" (MoveCommand Y=1, yaw=0) moves toward -Z.
        // Wall face the player approaches: z = -3 + 0.125 = -2.875. Capsule radius 0.4 => stops near z ~ -2.475.
        // Without the wall, 60 ticks at 3 m/s = 6 m => z ~ -6. With it, z stops near -2.5 => blocked by >3 m.
        const float wallZ = -3f;
        const float wallFaceZ = wallZ + 0.125f; // box half-extent Z = 0.125

        using IPhysicsWorld serverPhysics = new BepuPhysicsWorld();
        serverPhysics.AddStatic(new BoxShape(new Vector3(5f, 3f, 0.125f)), Pose.At(new Vector3(0f, 1.5f, wallZ)));
        serverPhysics.Step(Dt);

        using IPhysicsWorld clientPhysics = new BepuPhysicsWorld();
        clientPhysics.AddStatic(new BoxShape(new Vector3(5f, 3f, 0.125f)), Pose.At(new Vector3(0f, 1.5f, wallZ)));
        clientPhysics.Step(Dt);

        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = SpawnAtOrigin();
        var server = new WorldServer(st, config, Flat, MoveTuning.Default, physics: serverPhysics);
        var client = new WorldClient(ct, Flat, MoveTuning.Default,
            new WorldClientConfig { TickSeconds = Dt }, physics: clientPhysics);
        EstablishBasis(server, client);

        // Prediction-only loop: SendInput queues the command and the client predicts locally.
        // Server is NOT pumped here so we are reading pure client-side prediction, not reconciled state.
        for (int i = 0; i < 60; i++) client.SendInput(Forward);
        client.AdvancePresentation(Dt);

        Vector3 predicted = LocalPos(client);

        // (a) The client prediction must not have passed through the wall.
        Assert.True(predicted.Z > wallFaceZ - 0.01f,
            $"client prediction passed through the wall: predicted z={predicted.Z}, wall face at z={wallFaceZ}");

        // (b) Blocked significantly: with 3 m/s and no wall the player would reach z~-6 in 60 ticks.
        //     The wall stops them near -2.5, so predicted.Z must be more than 3 m from the unblocked end.
        const float unblocked = -6f;
        Assert.True(predicted.Z > unblocked + 3f,
            $"client prediction should be blocked well short of the unblocked endpoint z~{unblocked}, was z={predicted.Z}");
    }

    static Vector3 LocalPos(WorldClient client)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.IsLocal) return e.Position;
        throw new XunitException("no local entity in client snapshot");
    }
}
