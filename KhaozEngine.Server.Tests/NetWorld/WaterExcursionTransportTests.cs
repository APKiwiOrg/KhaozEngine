using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WaterExcursionTransportTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SnapshotAndPersistChannelsCarryExcursionWithTheExistingFields(int excursion)
    {
        MovementState source = Set(new MovementState
        {
            VerticalVelocity = 4.5f,
            Swimming = excursion == 1,
            TeleportEpoch = 73,
            FacingYawQ = 1234,
            SpeedScaleQ = 4
        }, excursion);
        foreach (var channel in new[] { ReplicationChannels.Default, ReplicationChannels.Persist })
        {
            byte[] snapshot = Snapshot(source, channel);
            MovementState restored = Decode(snapshot);
            Assert.Equal(excursion, Read(restored));
            Assert.Equal(source.VerticalVelocity, restored.VerticalVelocity);
            Assert.Equal(source.TeleportEpoch, restored.TeleportEpoch);
            Assert.Equal(source.FacingYawQ, restored.FacingYawQ);
            Assert.Equal(source.SpeedScaleQ, restored.SpeedScaleQ);
            byte[] payload = snapshot[14..^2];
            Assert.Equal(57, payload.Length);
            Assert.Equal((byte)excursion, payload[^1]);
            Assert.Equal(CellBlobFixtures.Movement(13, source), payload[..^1]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AuthoritativeRebuildAndFrameRebindingPreserveOnlyTheReceivedExcursion(int excursion)
    {
        var original = new PlayerMoveState
        {
            Move = Set(new MoveState { Position = new(-5, -600, 7), VerticalVelocity = 3 }, excursion),
            FrameAnchor = new(512, -512),
            TeleportEpoch = 9
        };
        var movement = MovementState.From(original);
        Assert.Equal(excursion, Read(movement));
        var corrected = PlayerMoveState.From(original.Position, movement, MovementOwnerState.From(original));
        Assert.Equal(excursion, Read(corrected.Move));
        corrected.FrameAnchor = original.FrameAnchor;
        var rebased = corrected.ToAnchor(new(1024, -1024));
        Assert.Equal(excursion, Read(rebased.Move));
        Assert.Equal(-600f, rebased.Position.Y);
        Assert.Equal(original.Absolute.Position, rebased.Absolute.Position);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void OlderPersistedMovementDefaultsExcursionWithoutLosingLegacySwimming(int generation)
    {
        MovementState old = new() { Swimming = true, VerticalVelocity = -2, TeleportEpoch = 77 };
        byte[] body = new CellBlobFixtures.BodyBuilder().Entity(1,
            (MoveProtocol.MovementTypeId, CellBlobFixtures.Movement(generation, old))).ToBody();
        byte[] migrated = BuiltinBlobLayout.NormalizeToCurrent(body, generation);
        MovementState restored = Decode(migrated);
        Assert.Equal(0, Read(restored));
        Assert.True(restored.Swimming);
        Assert.Equal(-2f, restored.VerticalVelocity);
        if (generation >= 4) Assert.Equal(77u, restored.TeleportEpoch);
        Assert.Equal(migrated, BuiltinBlobLayout.NormalizeToCurrent(migrated, MoveProtocol.WireProtocolVersion));
    }

    [Fact]
    public void RemoteExcursionUsesTheDelayedDiscreteTimeline()
    {
        var view = new ClientReplicationView(MoveProtocol.CreateRegistry());
        var world = new World();
        view.Apply(world, Snapshot(Set(new MovementState { Swimming = true }, 1)));
        view.RecordInterpolationSample(0);
        view.Apply(world, Snapshot(Set(new MovementState { Swimming = false, VerticalVelocity = 5 }, 2)));
        view.RecordInterpolationSample(1);
        Assert.True(view.TryGetEntity(1, out var entity));
        view.InterpolateAt(world, 0.5);
        Assert.Equal(1, Read(world.Get<MovementState>(entity)));
        view.InterpolateAt(world, 1);
        Assert.Equal(2, Read(world.Get<MovementState>(entity)));
    }

    [Fact]
    public void InvalidExcursionByteRejectsTheSnapshot()
    {
        byte[] snapshot = Snapshot(Set(default(MovementState), 2));
        snapshot[^3] = 255;
        var view = new ClientReplicationView(MoveProtocol.CreateRegistry());
        Assert.False(view.TryApply(new World(), snapshot, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void InvalidExcursionCannotBeWritten()
    {
        var state = Set(default(MovementState), 255);
        Assert.Throws<InvalidDataException>(() => Snapshot(state));
    }

    [Fact]
    public void ExcursionPayloadIsCoveredByTheAutomaticWireHandshakeGeneration()
    {
        Assert.True(MoveProtocol.WireProtocolVersion > 13);
        Assert.Equal(57, BuiltinBlobLayout.MovementPayloadLength(MoveProtocol.WireProtocolVersion));
        Assert.Equal(56, BuiltinBlobLayout.MovementPayloadLength(13));
    }

    static T Set<T>(T state, int value) where T : struct
    {
        FieldInfo? field = typeof(T).GetField("WaterExcursion");
        Assert.NotNull(field);
        Assert.True(field.FieldType.IsEnum);
        object boxed = state;
        field.SetValue(boxed, Enum.ToObject(field.FieldType, value));
        return (T)boxed;
    }

    static int Read<T>(T state) where T : struct
    {
        FieldInfo? field = typeof(T).GetField("WaterExcursion");
        Assert.NotNull(field);
        return Convert.ToInt32(field.GetValue(state));
    }

    static byte[] Snapshot(MovementState movement, ReplicationChannels channel = ReplicationChannels.Default)
    {
        var world = new World();
        var entity = world.Spawn();
        world.Set(entity, new NetId(1));
        world.Set(entity, movement);
        return SnapshotWriter.Write(world, MoveProtocol.CreateRegistry(), channel);
    }

    static MovementState Decode(byte[] snapshot)
    {
        var view = new ClientReplicationView(MoveProtocol.CreateRegistry());
        var world = new World();
        view.Apply(world, snapshot);
        Assert.True(view.TryGetEntity(1, out var entity));
        return world.Get<MovementState>(entity);
    }
}
