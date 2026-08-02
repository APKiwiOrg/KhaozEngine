using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Proves <see cref="PositionFrameBlobMigration.FrameV2ToV3"/> round-trips a REAL v2 snapshot body end to end: seed
/// a server world with position + movement + identity on one entity (plus a dynamic body + a pickup on a second,
/// for breadth), write it with a v2-shaped registry (the pre-16.0.0 absolute-position wire), migrate it through the
/// shipped <see cref="PositionFrameBlobMigration.FrameV2ToV3"/> step, and decode the migrated body through
/// <see cref="ClientReplicationView"/> with the CURRENT production registry (<see cref="MoveProtocol.CreateRegistry"/>).
/// Every field must come back exactly.
/// <para>
/// This is the empirical proof the final release review demanded: with
/// <see cref="PositionFrameBlobMigration"/>'s <c>MovementTypeId</c> payload length wrongly stated as 26, a body
/// containing a <see cref="MovementState"/> throws during migration, because the walk reads the wrong number of bytes
/// out of the movement payload and then misreads the following bytes as the next component's type id. The length is
/// whatever the CURRENT movement layout is, and it MOVES whenever that built-in grows: 26 as of wire generation 10
/// (float + bool + float + float + bool + uint + sbyte + sbyte + short + short + short =
/// 4+1+4+4+1+4+1+1+2+2+2), which is also what the comment beside the constant says.
/// </para>
/// </summary>
public class PositionFrameBlobMigrationTests
{
    // Builds a registry matching the pre-16.0.0 (schema v2) wire: PositionTypeId writes three raw ABSOLUTE floats
    // (no frame stamp - the layout PositionFrameBlobMigration widens from), and every other built-in matches the
    // CURRENT MoveProtocol wire layout byte for byte (unchanged since v2 - only position widened at schema v3). The
    // write bodies are copied from MoveProtocol.CreateRegistry rather than reused directly because ComponentCodec/
    // ReplicationRegistry.Ordered are internal to KhaozEngine.Replication; the layouts themselves are the shipped,
    // documented wire format (MoveProtocol.MovementTypeId/IdentityTypeId/DynamicBodyTypeId/PickupTypeId doc comments).
    private static ReplicationRegistry CreateV2Registry()
    {
        var r = new ReplicationRegistry();
        r.Register<ReplicatedPosition>(
            MoveProtocol.PositionTypeId,
            write: (p, bw) => { bw.Write(p.Value.X); bw.Write(p.Value.Y); bw.Write(p.Value.Z); },
            read: _ => throw new NotSupportedException("The v2 registry only seeds the pre-migration body; it never reads."));
        r.Register<MovementState>(
            MoveProtocol.MovementTypeId,
            write: (m, bw) =>
            {
                bw.Write(m.VerticalVelocity);
                bw.Write(m.Grounded);
                bw.Write(m.TimeSinceGrounded);
                bw.Write(m.JumpBufferRemaining);
                bw.Write(m.Swimming);
                bw.Write(m.TeleportEpoch);
                bw.Write(m.ClimbRateQ);
                bw.Write(m.SpeedScaleQ);
                bw.Write(m.HorizontalVelocityXQ);
                bw.Write(m.HorizontalVelocityZQ);
                bw.Write(m.FacingYawQ);
            },
            read: _ => throw new NotSupportedException("The v2 registry only seeds the pre-migration body; it never reads."));
        r.Register<PlayerIdentity>(
            MoveProtocol.IdentityTypeId,
            write: (pi, bw) =>
            {
                byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(pi.DisplayName ?? string.Empty);
                bw.Write((ushort)utf8.Length);
                bw.Write(utf8);
            },
            read: _ => throw new NotSupportedException("The v2 registry only seeds the pre-migration body; it never reads."));
        r.Register<DynamicBodyState>(
            MoveProtocol.DynamicBodyTypeId,
            write: (d, bw) =>
            {
                bw.Write(d.Orientation.X); bw.Write(d.Orientation.Y); bw.Write(d.Orientation.Z); bw.Write(d.Orientation.W);
                bw.Write(d.LinearVelocity.X); bw.Write(d.LinearVelocity.Y); bw.Write(d.LinearVelocity.Z);
                bw.Write(d.AngularVelocity.X); bw.Write(d.AngularVelocity.Y); bw.Write(d.AngularVelocity.Z);
            },
            read: _ => throw new NotSupportedException("The v2 registry only seeds the pre-migration body; it never reads."));
        r.Register<PickupState>(
            MoveProtocol.PickupTypeId,
            write: (p, bw) => { bw.Write(p.PayloadId); bw.Write(p.OwnerNetId); },
            read: _ => throw new NotSupportedException("The v2 registry only seeds the pre-migration body; it never reads."));
        return r;
    }

    [Fact]
    public void FrameV2ToV3_RealBody_MigratesAndRoundTripsThroughClientReplicationView()
    {
        var server = new World();

        // Entity 1: position + movement + identity - the exact shape the review proved breaks migration.
        Entity player = server.Spawn();
        server.Set(player, new NetId(1));
        var playerPos = new Vector3(123.5f, 45f, -678.25f);
        server.Set(player, ReplicatedPosition.InFrame(WorldFrame.Origin, playerPos));
        var movement = new MovementState
        {
            VerticalVelocity = -2.5f,
            Grounded = true,
            TimeSinceGrounded = 0.75f,
            JumpBufferRemaining = 0.1f,
            Swimming = true,
            TeleportEpoch = 42u,
            ClimbRateQ = 5,
            SpeedScaleQ = -3,
            HorizontalVelocityXQ = 1000,
            HorizontalVelocityZQ = -2000,
            FacingYawQ = 12345,
        };
        server.Set(player, movement);
        server.Set(player, new PlayerIdentity { DisplayName = "Runner" });

        // Entity 2: dynamic body + pickup, for breadth.
        Entity prop = server.Spawn();
        server.Set(prop, new NetId(2));
        var propPos = new Vector3(-40f, 3f, 90.5f);
        server.Set(prop, ReplicatedPosition.InFrame(WorldFrame.Origin, propPos));
        var dynamicBody = new DynamicBodyState
        {
            Orientation = Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f)),
            LinearVelocity = new Vector3(1f, 2f, 3f),
            AngularVelocity = new Vector3(0.5f, -0.5f, 0.25f),
        };
        server.Set(prop, dynamicBody);
        var pickup = new PickupState { PayloadId = 999, OwnerNetId = 2 };
        server.Set(prop, pickup);

        byte[] v2Body = SnapshotWriter.Write(server, CreateV2Registry());

        byte[] v3Body = PositionFrameBlobMigration.FrameV2ToV3(v2Body);

        var clientWorld = new World();
        var view = new ClientReplicationView(MoveProtocol.CreateRegistry());
        view.Apply(clientWorld, v3Body);

        Assert.True(view.TryGetEntity(1, out Entity clientPlayer));
        Assert.Equal(playerPos, clientWorld.Get<ReplicatedPosition>(clientPlayer).Value);
        MovementState roundTrippedMovement = clientWorld.Get<MovementState>(clientPlayer);
        Assert.Equal(movement.VerticalVelocity, roundTrippedMovement.VerticalVelocity);
        Assert.Equal(movement.Grounded, roundTrippedMovement.Grounded);
        Assert.Equal(movement.TimeSinceGrounded, roundTrippedMovement.TimeSinceGrounded);
        Assert.Equal(movement.JumpBufferRemaining, roundTrippedMovement.JumpBufferRemaining);
        Assert.Equal(movement.Swimming, roundTrippedMovement.Swimming);
        Assert.Equal(movement.TeleportEpoch, roundTrippedMovement.TeleportEpoch);
        Assert.Equal(movement.ClimbRateQ, roundTrippedMovement.ClimbRateQ);
        Assert.Equal(movement.SpeedScaleQ, roundTrippedMovement.SpeedScaleQ);
        Assert.Equal(movement.HorizontalVelocityXQ, roundTrippedMovement.HorizontalVelocityXQ);
        Assert.Equal(movement.HorizontalVelocityZQ, roundTrippedMovement.HorizontalVelocityZQ);
        Assert.Equal(movement.FacingYawQ, roundTrippedMovement.FacingYawQ);
        Assert.Equal("Runner", clientWorld.Get<PlayerIdentity>(clientPlayer).DisplayName);

        Assert.True(view.TryGetEntity(2, out Entity clientProp));
        Assert.Equal(propPos, clientWorld.Get<ReplicatedPosition>(clientProp).Value);
        DynamicBodyState roundTrippedBody = clientWorld.Get<DynamicBodyState>(clientProp);
        Assert.Equal(dynamicBody.Orientation, roundTrippedBody.Orientation);
        Assert.Equal(dynamicBody.LinearVelocity, roundTrippedBody.LinearVelocity);
        Assert.Equal(dynamicBody.AngularVelocity, roundTrippedBody.AngularVelocity);
        PickupState roundTrippedPickup = clientWorld.Get<PickupState>(clientProp);
        Assert.Equal(pickup.PayloadId, roundTrippedPickup.PayloadId);
        Assert.Equal(pickup.OwnerNetId, roundTrippedPickup.OwnerNetId);
    }
}
