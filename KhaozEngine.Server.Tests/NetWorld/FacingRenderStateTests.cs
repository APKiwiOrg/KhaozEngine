using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The client-presentation half of authoritative facing: <see cref="EntityRenderState.FacingYaw"/> is the PREDICTED
/// heading for the local player and the DECODED replicated one for remotes, exactly as <c>Grounded</c> and
/// <c>VerticalVelocity</c> already flow. This is what lets a game stop deriving model facing from a position delta -
/// the derivation that cannot turn a stationary character at all, and that reads a fast slope walk as a turn.
/// </summary>
public class FacingRenderStateTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static MoveCommand Face(float yaw) => new(Vector2.Zero, run: false, cameraYaw: yaw, jump: false, faceCamera: true);

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
    public void TheLocalPlayersPredictedHeading_ReachesEntityRenderState()
    {
        (WorldServer server, WorldClient client, WorldServerConfig cfg) = Connect();

        client.SendInput(Face(2.35f));

        // Prediction includes publication before a snapshot arrives, so rendering sees the canonical heading.
        float target = MovementState.DecodeFacingYaw(MovementState.QuantizeFacingYaw(2.35f));
        Assert.Equal(target, Local(client).FacingYaw);

        server.Poll(); server.Tick(cfg.TickSeconds); client.Poll();
        Assert.True(server.TryGetPlayerState(0, out PlayerMoveState auth));
        Assert.Equal(target, auth.Move.FacingYaw);
        Assert.Equal(target, Local(client).FacingYaw);

    }

    [Fact]
    public void ARemotesHeadingComesFromTheReplicatedComponent()
    {
        // Remotes read the decoded MovementState.FacingYawQ, discrete-sampled onto the same delayed render timeline
        // as the interpolated position, so an animator turns a remote from the authoritative fact rather than from
        // the direction its terrain-following position happens to drift.
        (WorldServer server, WorldClient client, WorldServerConfig cfg) = Connect();
        long npc = server.SpawnEntity(2f, 2f, (world, e) =>
            world.Set(e, new MovementState { Grounded = true, FacingYawQ = MovementState.QuantizeFacingYaw(-1.25f) }));
        for (int i = 0; i < 10; i++) { server.Poll(); server.Tick(cfg.TickSeconds); client.Poll(); }

        bool sawRemote = false;
        foreach (EntityRenderState e in client.Snapshot())
        {
            if (e.IsLocal || e.Id.Value != npc) continue;
            sawRemote = true;
            Assert.Equal(-1.25f, e.FacingYaw, 3);   // within the wire quantum of what the server authored
        }
        Assert.True(sawRemote, "the spawned remote entity never entered the client's area of interest");
    }

    [Fact]
    public void ARemoteWithNoMovementComponentFacesTheDefaultHeading()
    {
        // 0 is a legal heading, so a remote that has not replicated a MovementState yet reads as facing -Z rather
        // than as facing nowhere - the same "the zero default means the harmless thing" rule the codec follows.
        (WorldServer server, WorldClient client, WorldServerConfig cfg) = Connect();
        long npc = server.SpawnEntity(3f, 3f);
        for (int i = 0; i < 10; i++) { server.Poll(); server.Tick(cfg.TickSeconds); client.Poll(); }

        bool sawRemote = false;
        foreach (EntityRenderState e in client.Snapshot())
        {
            if (e.IsLocal || e.Id.Value != npc) continue;
            sawRemote = true;
            Assert.Equal(0f, e.FacingYaw);
        }
        Assert.True(sawRemote, "the spawned remote entity never entered the client's area of interest");
    }

    [Fact]
    public void TheLegacyConstructorsDefaultToTheDefaultHeading()
    {
        Assert.Equal(0f, new EntityRenderState(new NetId(1), Vector3.Zero, isLocal: true).FacingYaw);
        Assert.Equal(0f, new EntityRenderState(new NetId(1), Vector3.Zero, isLocal: true, "name").FacingYaw);
        Assert.Equal(0f, new EntityRenderState(new NetId(1), Vector3.Zero, isLocal: true, "name",
            grounded: true, verticalVelocity: 0f, swimming: false, climbRate: 0f, stepCumulativeY: 0f,
            landingImpactSpeed: 3f).FacingYaw);
        Assert.Equal(-0.75f, new EntityRenderState(new NetId(1), Vector3.Zero, isLocal: true, "name",
            grounded: true, verticalVelocity: 0f, swimming: false, climbRate: 0f, stepCumulativeY: 0f,
            landingImpactSpeed: 0f, facingYaw: -0.75f).FacingYaw);
    }
}
