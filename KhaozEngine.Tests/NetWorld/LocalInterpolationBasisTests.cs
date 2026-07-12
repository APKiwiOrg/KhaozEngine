using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The local player's reconcile basis is the client world's <see cref="ReplicatedPosition"/> (the last-RECEIVED
/// authoritative value). Remote interpolation must never write into it: the local avatar renders from prediction, not
/// from the fixed-delay buffer, so <c>ClientReplicationView.InterpolateAt</c> writing a delayed value into the local
/// entity's <see cref="ReplicatedPosition"/> poisons the next reconcile's basis. Steady movement masks it (the delta
/// stream re-sends the changing position each tick, overwriting the stale value). After an epoch teleport the entity
/// goes static at the destination, the delta stops carrying the unchanged position, and for ~InterpolationDelayTicks
/// the interpolator writes a stale pre-teleport-blended value back into <see cref="ReplicatedPosition"/>; the reconcile
/// reads that as its basis, the error is under <see cref="PredictionSettings.HardSnapDistance"/>, and the avatar glides
/// off the mark via the render offset (~1 s post-teleport slide, bites on login for returning players and self-rescue).
/// The fix excludes the local entity from the interpolation buffer entirely. These are the integration pins; the
/// view-level exclusion + reconnect-re-id contract is in <c>ClientReplicationBufferTests</c>.
/// </summary>
public class LocalInterpolationBasisTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    static (WorldServer server, WorldClient client, float dt) Connect()
    {
        (LoopbackTransport st, LoopbackTransport ct) = LoopbackTransport.CreatePair();
        var config = new WorldServerConfig { TickSeconds = 1f / 30f, InterestRadius = 500f, MaxPlayers = 8 };
        var server = new WorldServer(st, config, Flat, MoveTuning.Default);
        // Defaults: RequestDeltaReplication = true, InterpolateRemotes = true, InterpolationDelayTicks = 2 - the exact
        // conditions the slide needs (a full snapshot OR no interpolation each independently eliminate it).
        var client = new WorldClient(ct, Flat, MoveTuning.Default, new WorldClientConfig { TickSeconds = config.TickSeconds });
        for (int i = 0; i < 10 && !client.Joined; i++) { server.Poll(); server.Tick(config.TickSeconds); client.Poll(); }
        Assert.True(client.Joined);
        return (server, client, config.TickSeconds);
    }

    // Idle tick with presentation: send an idle command, tick the server, poll the client, advance one render frame so
    // the presentation clock moves (distinct interpolation-sample timestamps) and InterpolateAt runs.
    static void IdleFrame(WorldServer server, WorldClient client, float dt)
    {
        client.SendInput(MoveCommand.Idle);
        server.Poll();
        server.Tick(dt);
        client.Poll();
        client.AdvancePresentation(dt);
    }

    // Teleports the local player 65 m away (far, but under HardSnapDistance(100): the mispredict GLIDES, it does not
    // hard-snap) and drives idle frames until the epoch teleport reaches the client. Returns the destination.
    static Vector3 EpochTeleportAndLand(WorldServer server, WorldClient client, float dt, out int slot)
    {
        // Warm up idle so the fixed-delay buffer fills with distinct-timestamp samples at the spawn position.
        for (int i = 0; i < 40; i++) IdleFrame(server, client, dt);

        slot = server.JoinedSlots.First();
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState before));
        Vector3 dest = before.Position + new Vector3(65f, 0f, 0f);

        bool landed = false;
        client.LocalTeleported += () => landed = true;
        server.Teleport(PlayerRef.Slot(slot), dest);
        for (int i = 0; i < 20 && !landed; i++) IdleFrame(server, client, dt);
        Assert.True(landed, "the epoch teleport never reached the client");
        return dest;
    }

    [Fact]
    public void Local_avatar_does_not_slide_after_an_epoch_teleport_with_delta_and_interpolation()
    {
        (WorldServer server, WorldClient client, float dt) = Connect();
        Vector3 dest = EpochTeleportAndLand(server, client, dt, out _);

        // Hold idle. The authoritative position is pinned at the destination, so the local render must STAY there every
        // frame. The bug: the interpolator writes a stale pre-teleport-blended value into the local ReplicatedPosition,
        // the reconcile reads it as its basis, and the render offset glides the avatar ~10% of the teleport distance off
        // the mark over ~1 s. RED before the fix (the local entity is excluded from the buffer only once the fix wires
        // LocalNetId into RecordInterpolationSample / InterpolateAt).
        float maxGap = 0f;
        for (int i = 0; i < 40; i++)
        {
            IdleFrame(server, client, dt);
            Vector3 r = client.LocalRenderState.Position;
            float gap = new Vector2(r.X - dest.X, r.Z - dest.Z).Length();
            maxGap = MathF.Max(maxGap, gap);
        }
        Assert.True(maxGap < 0.25f,
            $"local avatar slid {maxGap:0.###} m off the teleport destination after landing (post-teleport slide)");
    }

    [Fact]
    public void Local_replicated_position_stays_the_authoritative_value_through_presentation()
    {
        (WorldServer server, WorldClient client, float dt) = Connect();

        // Warm up idle so the fixed-delay buffer fills at spawn, then epoch-teleport the local player 65 m away.
        for (int i = 0; i < 40; i++) IdleFrame(server, client, dt);
        int slot = server.JoinedSlots.First();
        Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState before));
        Vector3 dest = before.Position + new Vector3(65f, 0f, 0f);
        bool landed = false;
        client.LocalTeleported += () => landed = true;
        server.Teleport(PlayerRef.Slot(slot), dest);

        // The direct invariant behind the slide: after AdvancePresentation runs, the local entity's ReplicatedPosition
        // in the CLIENT world must still equal the last-received authoritative value, never a fixed-delay interpolated
        // one - the reconcile reads exactly this as its prediction basis. Measurement starts on the landing frame (the
        // first stale frame) and spans the whole post-teleport interpolation window. RED before the fix: for the
        // ~InterpolationDelayTicks after landing, InterpolateAt clobbers ReplicatedPosition with a delayed value.
        int measured = 0;
        for (int i = 0; i < 80 && measured < 40; i++)
        {
            IdleFrame(server, client, dt);
            if (!landed) continue;   // wait out the in-flight pipeline; the teleport has not reached the client yet
            measured++;
            Assert.True(server.TryGetPlayerState(slot, out PlayerMoveState auth));
            Assert.True(client.TryGetComponent(client.LocalNetId, out ReplicatedPosition rp),
                "the local entity must carry a replicated position");
            Assert.Equal(auth.Position.X, rp.Value.X, 3);
            Assert.Equal(auth.Position.Z, rp.Value.Z, 3);
        }
        Assert.True(landed, "the epoch teleport never reached the client");
        Assert.True(measured >= 40, $"expected to measure the full post-teleport window, only got {measured} frames");
    }
}
