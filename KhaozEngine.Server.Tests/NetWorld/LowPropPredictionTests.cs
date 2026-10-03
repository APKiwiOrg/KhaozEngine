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

/// <summary>Client prediction and server authority walk a player across a low flat prop top above the terrain
/// (KhaozEngine #1253). Both run the same core, so both must stand on the prop, and reconciliation must not
/// correct the client beyond the dead zone.</summary>
public class LowPropPredictionTests
{
    const float Dt = 1f / 30f;
    static float Flat(float x, float z) => 0f;
    static MoveCommand Forward => new(new Vector2(0f, 1f), run: false, cameraYaw: 0f); // W at yaw 0 -> -Z

    [Theory]
    [InlineData(0.025f)]
    [InlineData(0.1f)]
    public void PredictionAndAuthorityBothStandOnALowPropTop(float lip)
    {
        using IPhysicsWorld serverPhysics = DeckWorld(lip);
        using IPhysicsWorld clientPhysics = DeckWorld(lip);
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = Dt, InterestRadius = 500f, SpawnPosition = _ => Vector3.Zero };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default, physics: serverPhysics);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = Dt },
            physics: clientPhysics);
        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(Dt); client.Poll(); }
        Assert.True(client.Joined && client.LocalNetId > 0, "client never joined");

        float worstCorrection = 0f;
        for (int tick = 0; tick < 60; tick++)
        {
            client.SendInput(tick < 30 ? Forward : MoveCommand.Idle);
            server.Poll();
            server.Tick(Dt);
            client.Poll();
            client.AdvancePresentation(Dt);
            worstCorrection = MathF.Max(worstCorrection, client.NetStats.LastCorrectionMeters);
        }

        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState authority));
        Vector3 predicted = LocalPos(client);
        float halfHeight = MoveTuning.Default.CapsuleHalfHeight;
        Assert.True(authority.Position.Z < -2f, $"the player never reached the deck, authority {authority.Position}");
        Assert.InRange(authority.Position.Y - halfHeight, lip - 0.001f, lip + 0.001f);
        Assert.InRange(predicted.Y - halfHeight, lip - 0.001f, lip + 0.001f);
        Assert.InRange(Vector3.Distance(authority.Position, predicted), 0f, 0.001f);
        Assert.InRange(worstCorrection, 0f, PredictionSettings.Default.CorrectionDeadZone);
    }

    // A deck from z -1 to -9 whose top is the lip height, over terrain at 0.
    static IPhysicsWorld DeckWorld(float lip)
    {
        var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(5f, lip / 2f, 4f)), Pose.At(new Vector3(0f, lip / 2f, -5f)));
        world.Step(Dt);
        return world;
    }

    static Vector3 LocalPos(WorldClient client)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.IsLocal) return e.Position;
        throw new XunitException("no local entity in client snapshot");
    }
}
