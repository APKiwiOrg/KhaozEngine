using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class MovementPayloadRewriteTests
{
    public static IEnumerable<object[]> HistoricalLayouts()
    {
        for (int generation = 1; generation <= 14; generation++)
            yield return new object[] { generation, (sbyte)-6 };
    }

    [Theory]
    [MemberData(nameof(HistoricalLayouts))]
    [InlineData(6, (sbyte)-128)] // corrupt but floorable, must sign-extend before widening
    [InlineData(11, (sbyte)-17)] // owner split and speed widening must both run
    [InlineData(12, (sbyte)-16)] // exact historical root
    [InlineData(13, (sbyte)0)] // historical default
    [InlineData(14, (sbyte)127)] // retain corrupt high values without a new ceiling
    public void Every_historical_layout_preserves_fields_and_old_decoded_speed(int generation, sbyte oldQ)
    {
        byte[] body = HistoricalBody(generation, oldQ);
        byte[] normalized = BuiltinBlobLayout.NormalizeToCurrent(body, generation);
        var world = new World();
        var view = new ClientReplicationView(MoveProtocol.CreateRegistry());
        view.Apply(world, normalized);
        Assert.True(view.TryGetEntity(7, out Entity entity));
        MovementState actual = world.Get<MovementState>(entity);
        var expected = new MovementState
        {
            VerticalVelocity = -2.5f,
            Grounded = true,
            Swimming = generation >= 3,
            TeleportEpoch = generation >= 4 ? 0x12345678u : 0,
            ClimbRateQ = generation >= 5 ? (sbyte)-5 : (sbyte)0,
            HorizontalVelocityXQ = generation >= 7 ? (short)1234 : (short)0,
            HorizontalVelocityZQ = generation >= 7 ? (short)-2345 : (short)0,
            FacingYawQ = generation >= 10 ? (short)3456 : (short)0,
            Commitment = generation >= 11 ? LegacyCommitment : default,
            WaterExcursion = generation >= 14 ? WaterExcursionState.AirborneFromWater : default,
        };
        // Independent historical rule. Do not use the current quantizer to generate the expectation.
        short newQ = generation >= 6 ? (short)(oldQ * 64) : (short)0;
        Assert.Equal(newQ, actual.SpeedScaleQ);
        Assert.Equal(generation >= 6 ? MathF.Max(0, 1f + oldQ / 16f) : 1f,
            MovementState.DecodeSpeedScale(actual.SpeedScaleQ));
        expected.SpeedScaleQ = actual.SpeedScaleQ;
        Assert.Equal(expected, actual);
        Assert.Equal(new MovementOwnerState { TimeSinceGrounded = 0.75f, JumpBufferRemaining = 0.125f },
            world.Get<MovementOwnerState>(entity));
        Assert.Equal(new Vector3(1.5f, 2.5f, -3.5f), world.Get<ReplicatedPosition>(entity).Value);
        Assert.Equal("Legacy", world.Get<PlayerIdentity>(entity).DisplayName);
        if (generation >= 8)
            Assert.Equal(new PickupState { PayloadId = 91, OwnerNetId = 7 }, world.Get<PickupState>(entity));
        else
            Assert.False(world.TryGet<PickupState>(entity, out _));

        // The owner frame follows other built-ins and precedes opaque extensions and the entity terminator.
        var components = new List<(ushort, byte[])>
        {
            (1, CellBlobFixtures.Position(15, new Vector3(1.5f, 2.5f, -3.5f))),
            (2, CellBlobFixtures.Movement(15, expected)),
            (3, CellBlobFixtures.Identity("Legacy")),
        };
        if (generation >= 8) components.Add((5, CellBlobFixtures.Pickup(91, 7)));
        components.Add((6, CellBlobFixtures.MovementOwner(new() { TimeSinceGrounded = 0.75f, JumpBufferRemaining = 0.125f })));
        components.Add((16, CellBlobFixtures.Extension(new byte[] { 0, 255, 6, 0, 128 })));
        byte[] expectedBody = new CellBlobFixtures.BodyBuilder().Entity(7, components.ToArray()).Entity(8).ToBody();
        Assert.Equal(expectedBody, normalized);
        Assert.Equal(normalized, BuiltinBlobLayout.NormalizeToCurrent(normalized, 15));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(11)]
    [InlineData(14)]
    public async Task Truncated_historical_payload_is_quarantined(int generation)
    {
        byte[] body = new CellBlobFixtures.BodyBuilder().Entity(7,
            (2, LegacyPayload(generation, -6)[..^1])).ToBody();
        byte[] before = [.. body];
        var refusal = Assert.Throws<CellBlobMigrationException>(() => BuiltinBlobLayout.NormalizeToCurrent(body, generation));
        Assert.Equal(CellBlobMigrationRefusal.KnownGenerationUnwalkable, refusal.Reason);
        Assert.Equal(generation, refusal.FromGeneration);
        Assert.Equal(MoveProtocol.WireProtocolVersion, refusal.ToGeneration);
        Assert.Equal(before, body);
        var store = new InMemoryWorldStore();
        await store.SaveAsync("cell:0:0", CellBlobFixtures.Wrap(4, generation, body));
        var host = new ShardPersistenceHost(MoveProtocol.CreateRegistry(), exposeRegistry: true);
        var persistence = new CellPersistence(host, store);
        var issues = new List<CellPersistenceIssue>();
        persistence.Issue += issues.Add;
        await persistence.PreloadAsync();
        await persistence.FlushAsync();
        Assert.Contains(issues, i => i.Kind == CellPersistenceIssueKind.QuarantinedCorrupt);
        byte[] control = new CellBlobFixtures.BodyBuilder().Entity(7, (2, LegacyPayload(generation, -6))).ToBody();
        Assert.NotEmpty(BuiltinBlobLayout.NormalizeToCurrent(control, generation));
    }

    private static readonly MovementCommitment LegacyCommitment = new()
    {
        Sequence = 71, Phase = MovementCommitmentPhase.Airborne, Direction = new Vector2(0.6f, -0.8f),
        HorizontalSpeed = 7.5f, VerticalSpeed = 8.25f, Gravity = 17f, PreparationRemaining = 0.125f,
        RecoveryRemaining = 0.25f, TimeoutRemaining = 3.75f, EndReason = MovementCommitmentEndReason.Blocked,
    };

    // Hand-encoded historical fields, independent of both the live codec and production rewriter.
    private static byte[] LegacyPayload(int generation, sbyte q)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(-2.5f);
        writer.Write(true);
        if (generation < 12) { writer.Write(0.75f); writer.Write(0.125f); }
        if (generation >= 3) writer.Write(true);
        if (generation >= 4) writer.Write(0x12345678u);
        if (generation >= 5) writer.Write((sbyte)-5);
        if (generation >= 6) writer.Write(q);
        if (generation >= 7) { writer.Write((short)1234); writer.Write((short)-2345); }
        if (generation >= 10) writer.Write((short)3456);
        if (generation >= 11)
        {
            writer.Write(71u); writer.Write((byte)2); writer.Write(0.6f); writer.Write(-0.8f);
            writer.Write(7.5f); writer.Write(8.25f); writer.Write(17f); writer.Write(0.125f);
            writer.Write(0.25f); writer.Write(3.75f); writer.Write((byte)2);
        }
        if (generation >= 14) writer.Write((byte)2);
        return stream.ToArray();
    }

    private static byte[] HistoricalBody(int generation, sbyte q)
    {
        var components = new List<(ushort, byte[])>
        {
            (1, CellBlobFixtures.Position(generation, new Vector3(1.5f, 2.5f, -3.5f))),
            (2, LegacyPayload(generation, q)),
            (3, CellBlobFixtures.Identity("Legacy")),
        };
        if (generation >= 8) components.Add((5, CellBlobFixtures.Pickup(91, 7)));
        if (generation >= 12) components.Add((6,
            CellBlobFixtures.MovementOwner(new() { TimeSinceGrounded = 0.75f, JumpBufferRemaining = 0.125f })));
        components.Add((16, CellBlobFixtures.Extension(new byte[] { 0, 255, 6, 0, 128 })));
        return new CellBlobFixtures.BodyBuilder().Entity(7, components.ToArray()).Entity(8).ToBody();
    }
}
