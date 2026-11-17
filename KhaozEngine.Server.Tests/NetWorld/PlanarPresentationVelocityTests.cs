using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;
using ScriptedTransport = KhaozEngine.Tests.Netcode.RecordingTransport;

namespace KhaozEngine.Tests.NetWorld;

public class PlanarPresentationVelocityTests
{
    private const float TickSeconds = 1f / 30f;
    // The endpoints are float coordinates. Subtracting them and dividing by one tick magnifies their rounding.
    private const float VelocityRoundingTolerance = 0.0001f;
    private const float PositionRoundingTolerance = 0.000001f;

    [Theory]
    [InlineData(2.0, false)]
    [InlineData(2.0, true)]
    [InlineData(4.8, false)]
    [InlineData(4.8, true)]
    [InlineData(5.5, false)]
    [InlineData(5.5, true)]
    public void Tick_velocity_follows_every_drawn_bracket_despite_arrival_spacing(double ratio, bool jitter)
    {
        const int snapshots = 24;
        Vector3 velocity = new(3, 2, -4);
        float dt = (float)(TickSeconds / ratio);
        using var rig = new Rig();
        var stamps = new List<double>();
        double clock = 0;
        int tick = 0;
        int compared = 0;
        int lastFrame = ArrivalFrame(snapshots - 1, ratio, jitter) + (int)Math.Ceiling(4 * ratio);
        for (int frame = 0; frame <= lastFrame; frame++)
        {
            if (tick < snapshots && frame == ArrivalFrame(tick, ratio, jitter))
            {
                rig.Push(tick, velocity * (tick * TickSeconds));
                stamps.Add(clock);
                tick++;
            }

            EntityRenderState remote = rig.Present(dt);
            clock += dt;
            double renderTime = clock - 2f * TickSeconds;
            bool bracketed = stamps.Count > 1 && renderTime >= stamps[0] && renderTime < stamps[^1];
            Assert.Equal(bracketed, remote.HasPlanarVelocity);
            if (bracketed)
            {
                AssertVelocity(new Vector2(velocity.X, velocity.Z), remote.PlanarVelocity);
                compared++;
            }
        }
        Assert.Equal(snapshots, tick);
        Assert.True(compared > 0);
    }

    [Fact]
    public void Frame_shift_uses_the_rebased_endpoints_instead_of_rounded_world_positions()
    {
        using var rig = new Rig();
        var previous = ReplicatedPosition.InFrame(new WorldFrame(2047, -2047), new Vector3(96, 0, -96));
        var next = ReplicatedPosition.InFrame(new WorldFrame(2048, -2048),
            new Vector3(-32 + 1f / 128f, 2, 32 - 1f / 64f));
        rig.Push(10, previous);
        rig.Present(TickSeconds);
        rig.Push(11, next);

        EntityRenderState remote = rig.Present(1.5f * TickSeconds);

        Assert.True(remote.HasPlanarVelocity);
        AssertVelocity(new Vector2(1f / 128f, -1f / 64f) / TickSeconds, remote.PlanarVelocity);
    }

    [Fact]
    public void Teleport_cut_drops_the_velocity_of_the_preceding_bracket()
    {
        using var rig = new Rig();
        rig.Push(10, Vector3.Zero);
        rig.Present(TickSeconds);
        rig.Push(11, new Vector3(1, 0, 0));
        Assert.True(rig.Present(1.5f * TickSeconds).HasPlanarVelocity);

        Vector3 destination = new(100, 0, -50);
        rig.Push(12, destination, teleportEpoch: 1);
        EntityRenderState remote = rig.Present(0);

        Assert.Equal(destination, remote.Position);
        Assert.False(remote.HasPlanarVelocity);
    }

    [Fact]
    public void Lost_frame_reports_the_mean_over_both_ticks()
    {
        using var rig = new Rig();
        rig.Push(10, Vector3.Zero);
        rig.Present(2f * TickSeconds);
        Vector3 displacement = new(0.5f, 1, -0.25f);
        rig.Push(12, displacement);

        EntityRenderState remote = rig.Present(TickSeconds);

        Assert.True(remote.HasPlanarVelocity);
        AssertVelocity(new Vector2(displacement.X, displacement.Z) / (2f * TickSeconds), remote.PlanarVelocity);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void Velocity_needs_both_the_tick_opt_in_and_ticked_samples(bool receiveTick, bool sendTick)
    {
        using var rig = new Rig(receiveTick);
        rig.Push(10, Vector3.Zero, ticked: sendTick);
        rig.Present(TickSeconds);
        rig.Push(11, new Vector3(2, 0, 0), ticked: sendTick);

        EntityRenderState remote = rig.Present(1.5f * TickSeconds);

        Assert.False(remote.HasPlanarVelocity);
        Assert.InRange(MathF.Abs(remote.Position.X - 1f), 0f, PositionRoundingTolerance);
    }

    [Fact]
    public void Local_velocity_is_the_predicted_step_with_the_local_speed_magnitude()
    {
        using var rig = new Rig();
        rig.Push(10, Vector3.Zero);
        Assert.False(rig.Local.HasPlanarVelocity);
        Vector3 before = rig.Client.LocalPredictedState.Position;
        Assert.True(rig.Client.SendInput(new MoveCommand(new Vector2(1, 1), run: false, cameraYaw: 0)) >= 0);
        Vector3 after = rig.Client.LocalPredictedState.Position;

        EntityRenderState local = rig.Local;

        Assert.True(rig.Client.LocalHorizontalSpeed > 0f);
        Assert.True(local.HasPlanarVelocity);
        AssertVelocity(new Vector2(after.X - before.X, after.Z - before.Z) / TickSeconds, local.PlanarVelocity);
        Assert.InRange(MathF.Abs(rig.Client.LocalHorizontalSpeed - local.PlanarVelocity.Length()), 0f,
            VelocityRoundingTolerance);
    }

    private static int ArrivalFrame(int tick, double ratio, bool jitter) =>
        (int)Math.Ceiling(tick * ratio) + (jitter && tick % 3 == 1 ? 1 : 0);

    private static void AssertVelocity(Vector2 expected, Vector2 actual) =>
        Assert.InRange(Vector2.Distance(expected, actual), 0f, VelocityRoundingTolerance);

    private sealed class Rig : IDisposable
    {
        private static readonly NetConnectionId Connection = new(1);
        private const long LocalId = 41;
        private const long RemoteId = 42;
        private readonly ScriptedTransport transport = new();
        private readonly World server = new();
        private readonly ReplicationRegistry registry = MoveProtocol.CreateRegistry();
        private readonly Entity remote;

        public Rig(bool receiveTick = true)
        {
            Client = new WorldClient(transport, (_, _) => 0f, MoveTuning.Default, new WorldClientConfig
            {
                TickSeconds = TickSeconds,
                ReceiveServerTick = receiveTick,
            });
            transport.Deliver(NetEvent.Connected(Connection));
            Deliver(SessionOpcode.Welcome, new byte[sizeof(int)]);
            Client.Poll();
            Assert.True(Client.Joined);

            Entity local = server.Spawn();
            server.Set(local, new NetId(LocalId));
            server.Set(local, ReplicatedPosition.InFrame(WorldFrame.Origin, Vector3.Zero));
            server.Set(local, new MovementState { Grounded = true });
            remote = server.Spawn();
            server.Set(remote, new NetId(RemoteId));
        }

        public WorldClient Client { get; }
        public EntityRenderState Local => Assert.Single(Client.Snapshot(), entry => entry.IsLocal);

        public void Push(long tick, Vector3 position, uint teleportEpoch = 0, bool ticked = true) =>
            Push(tick, ReplicatedPosition.InFrame(WorldFrame.Origin, position), teleportEpoch, ticked);

        public void Push(long tick, ReplicatedPosition position, uint teleportEpoch = 0, bool ticked = true)
        {
            server.Set(remote, position);
            server.Set(remote, new MovementState { Grounded = true, TeleportEpoch = teleportEpoch });
            byte[] body = SnapshotWriter.Write(server, registry);
            byte[] frame = ticked
                ? MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.TickedSnapshot,
                    MoveProtocol.EncodeTickedSnapshotFrame(tick, LocalId, -1, body))
                : MoveProtocol.EncodeServerFrame(MoveProtocol.ServerFrameKind.Snapshot,
                    MoveProtocol.EncodeSnapshotFrame(LocalId, -1, body));
            Deliver(SessionOpcode.Data, frame);
            Client.Poll();
        }

        public EntityRenderState Present(float dt)
        {
            Client.AdvancePresentation(dt);
            return Assert.Single(Client.Snapshot(), entry => entry.Id.Value == RemoteId);
        }

        private void Deliver(SessionOpcode opcode, byte[] body) =>
            transport.Deliver(NetEvent.FromData(Connection, SessionFrame.Write(opcode, body),
                NetChannelReliability.ReliableOrdered));

        public void Dispose() => Client.Dispose();
    }
}
