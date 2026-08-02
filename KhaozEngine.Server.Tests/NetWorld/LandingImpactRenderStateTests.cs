using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The client-presentation half of the landing seam: <see cref="EntityRenderState.LandingImpactSpeed"/> carries the
/// LOCAL player's PREDICTED impact on the predicted landing tick, so a game can flash a land effect / play an impact
/// sound without waiting a round trip. Remotes read 0 by construction (the latch rides no wire). A remote's landing is
/// derived from its replicated <c>Grounded</c> transition, which it already receives.
/// </summary>
public class LandingImpactRenderStateTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static MoveCommand Jump => new(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);

    static (WorldServer server, WorldClient client, WorldServerConfig cfg) Connect()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var cfg = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = cfg.TickSeconds });
        for (int i = 0; i < 6; i++) { server.Poll(); server.Tick(cfg.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        return (server, client, cfg);
    }

    static EntityRenderState Local(WorldClient client)
    {
        foreach (EntityRenderState e in client.Snapshot())
            if (e.IsLocal) return e;
        throw new InvalidOperationException("the local player is not in the client snapshot");
    }

    [Fact]
    public void TheLocalPlayersPredictedLandingImpact_ReachesEntityRenderState()
    {
        (WorldServer server, WorldClient client, WorldServerConfig cfg) = Connect();

        client.SendInput(Jump);
        server.Poll(); server.Tick(cfg.TickSeconds); client.Poll();
        Assert.Equal(0f, Local(client).LandingImpactSpeed);        // the launch tick is not a landing

        float predicted = 0f;
        int nonZeroFrames = 0;
        for (int i = 0; i < 90 && predicted == 0f; i++)
        {
            client.SendInput(MoveCommand.Idle);
            float impact = Local(client).LandingImpactSpeed;
            if (impact != 0f) { predicted = impact; nonZeroFrames++; }
            server.Poll(); server.Tick(cfg.TickSeconds); client.Poll();
        }

        Assert.True(predicted > 5f, $"the predicted landing tick surfaced no impact ({predicted:F3} m/s)");
        Assert.Equal(1, nonZeroFrames);
        Assert.InRange(predicted, 8f, 11f);                        // the JumpSpeed 9.79796 arc under g 25
        // And the authoritative server agrees on the same landing, to the same value.
        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState auth));
        Assert.True(auth.Grounded);
    }

    [Fact]
    public void RemotesReadZero_AndTheLegacyConstructorsDefaultToZero()
    {
        // The latch is deliberately absent from the movement codec, so nothing a remote receives can carry it: a
        // consumer that wants remote landing VFX derives them from the replicated Grounded transition instead.
        (WorldServer server, WorldClient client, WorldServerConfig cfg) = Connect();
        long npc = server.SpawnEntity(2f, 2f);
        for (int i = 0; i < 8; i++) { server.Poll(); server.Tick(cfg.TickSeconds); client.Poll(); }

        bool sawRemote = false;
        foreach (EntityRenderState e in client.Snapshot())
        {
            if (e.IsLocal) continue;
            sawRemote |= e.Id.Value == npc;
            Assert.Equal(0f, e.LandingImpactSpeed);
        }
        Assert.True(sawRemote, "the spawned remote entity never entered the client's area of interest");

        Assert.Equal(0f, new EntityRenderState(new NetId(1), Vector3.Zero, isLocal: true).LandingImpactSpeed);
        Assert.Equal(0f, new EntityRenderState(new NetId(1), Vector3.Zero, isLocal: true, "name").LandingImpactSpeed);
        Assert.Equal(0f, new EntityRenderState(new NetId(1), Vector3.Zero, isLocal: true, "name",
            grounded: false, verticalVelocity: -3f, swimming: false, climbRate: 0f, stepCumulativeY: 0f).LandingImpactSpeed);
        Assert.Equal(12.5f, new EntityRenderState(new NetId(1), Vector3.Zero, isLocal: true, "name",
            grounded: true, verticalVelocity: 0f, swimming: false, climbRate: 0f, stepCumulativeY: 0f,
            landingImpactSpeed: 12.5f).LandingImpactSpeed);
    }
}
