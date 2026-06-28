using System;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The networked client predicting against the SAME static prop/building colliders + walkable surfaces the
/// authoritative server runs, so a solid-prop consumer (Ruinborne) predicts straight rather than rubber-banding.
/// To isolate pure prediction from reconciliation (at zero loopback latency the server would correct a
/// collider-less client every tick, masking the bug), each test bursts commands AHEAD of the server and reads the
/// client's predicted position, then compares it to the same command stream stepped through a server-side
/// <see cref="PlayerMoveSimulator"/> built with the same colliders/surfaces.
/// </summary>
public class ClientCollisionTests
{
    const float Dt = 1f / 30f;
    static float Flat(float x, float z) => 0f;
    static MoveCommand Forward => new(new Vector2(0f, 1f), run: false, cameraYaw: 0f); // W at yaw 0 -> -Z

    // A tree at (0, -1.5): a 1 m cylinder the player walks into when moving forward (-Z) from the origin.
    static WorldColliders OneTree() => new(new[] { WorldCollider.Cylinder(new Vector2(0f, -1.5f), 1f) });

    // A 0.6 m flat-topped slab covering the origin (3x3 unit grid). Standing on it raises the support height.
    static WorldSurfaces OneSlab(float top) =>
        new(new[] { new WorldSurface(new PropSurface(3, 3, 1f, -1.5f, -1.5f, new[] { top, top, top, top, top, top, top, top, top }), Vector2.Zero, 1f, 0f, 0f) });

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
    public void Client_with_colliders_predicts_around_blocker_matching_authority()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = SpawnAtOrigin();
        var server = new WorldServer(st, config, Flat, MoveTuning.Default, colliders: OneTree());
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = Dt }, colliders: OneTree());
        EstablishBasis(server, client);

        // Predict 60 forward commands AHEAD of the server (no server tick): pure client prediction into the tree.
        for (int i = 0; i < 60; i++) client.SendInput(Forward);
        client.AdvancePresentation(Dt);   // settle the inter-tick render onto the latest predicted tick
        Vector3 predicted = LocalPos(client);

        // The client predicted AROUND the tree (did not walk through): capsule rests at ~ radius + capsule from centre.
        float dist = new Vector2(predicted.X, predicted.Z + 1.5f).Length();
        Assert.True(dist >= 1.4f - 0.05f, $"client prediction walked through the tree: dist={dist}");

        // ... and matches the same stream stepped through a server-side simulator with the same colliders, so the
        // authoritative basis reconciles as a no-op rather than a snap.
        var authority = new PlayerMoveSimulator(Flat, MoveTuning.Default, colliders: OneTree());
        PlayerMoveState auth = SpawnBasis(authority);
        for (int i = 0; i < 60; i++) auth = authority.Step(auth, Forward, Dt);
        Assert.Equal(auth.Position.X, predicted.X, 1);
        Assert.Equal(auth.Position.Z, predicted.Z, 1);
    }

    [Fact]
    public void Client_without_collider_args_is_terrain_only_unchanged()
    {
        // Regression: omit the new optional args. Prediction is terrain-only (unchanged), so the player walks
        // straight through where a tree would be, matching a colliderless authoritative simulator.
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = SpawnAtOrigin();
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);                          // no colliders
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = Dt }); // no new args
        EstablishBasis(server, client);

        for (int i = 0; i < 60; i++) client.SendInput(Forward);
        client.AdvancePresentation(Dt);
        Vector3 predicted = LocalPos(client);
        Assert.True(predicted.Z < -1.5f - 0.5f, $"terrain-only client should pass through the tree zone, z={predicted.Z}");

        var authority = new PlayerMoveSimulator(Flat, MoveTuning.Default);
        PlayerMoveState auth = SpawnBasis(authority);
        for (int i = 0; i < 60; i++) auth = authority.Step(auth, Forward, Dt);
        Assert.Equal(auth.Position.X, predicted.X, 1);
        Assert.Equal(auth.Position.Z, predicted.Z, 1);
    }

    [Fact]
    public void Client_with_surface_predicts_raised_support_matching_authority()
    {
        const float Top = 0.6f;
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = SpawnAtOrigin();
        var server = new WorldServer(st, config, Flat, MoveTuning.Default, surfaces: OneSlab(Top));
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = Dt }, surfaces: OneSlab(Top));
        EstablishBasis(server, client);

        // Stand still on the slab, predicting ahead of the server: a surface-less client would fall to ground height,
        // so a predicted Y at the raised support proves the surfaces are threaded into client prediction.
        for (int i = 0; i < 30; i++) client.SendInput(MoveCommand.Idle);
        client.AdvancePresentation(Dt);
        Vector3 predicted = LocalPos(client);

        float restingOnSlab = Top + MoveTuning.Default.CapsuleHalfHeight;
        float restingOnGround = MoveTuning.Default.CapsuleHalfHeight;
        Assert.Equal(restingOnSlab, predicted.Y, 1);
        Assert.True(predicted.Y > restingOnGround + 0.3f, $"client did not predict the raised surface: y={predicted.Y}");

        var authority = new PlayerMoveSimulator(Flat, MoveTuning.Default, surfaces: OneSlab(Top));
        PlayerMoveState auth = SpawnBasis(authority);
        for (int i = 0; i < 30; i++) auth = authority.Step(auth, MoveCommand.Idle, Dt);
        Assert.Equal(auth.Position.Y, predicted.Y, 2);
    }

    static Vector3 LocalPos(WorldClient client)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.IsLocal) return e.Position;
        throw new XunitException("no local entity in client snapshot");
    }
}
