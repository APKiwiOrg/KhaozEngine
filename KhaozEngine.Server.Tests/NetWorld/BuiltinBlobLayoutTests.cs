using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Pins <see cref="BuiltinBlobLayout"/>'s per-wire-generation payload table to the codec it describes, which is the
/// whole point of the type: the table is what every cell-blob migration uses to find an entity boundary in a stored
/// body, and the private copy it replaced sat stale for six wire generations without anything going red (#353).
/// <para>
/// <see cref="MoveProtocol.CreateRegistry"/> keeps only the CURRENT encoder, so the historical rows cannot be read
/// off it. They are re-derived instead by <see cref="CellBlobFixtures.Movement"/>, a field-by-field re-encode of
/// what the codec wrote at each generation, taken from the generation notes on
/// <see cref="MoveProtocol.WireProtocolVersion"/> (3 added <c>Swimming</c>, 4 <c>TeleportEpoch</c>, 5
/// <c>ClimbRateQ</c>, 6 <c>SpeedScaleQ</c>, 7 the two horizontal-velocity shorts, 10 <c>FacingYawQ</c>). Two
/// properties tie that ladder to reality: its newest rung is compared BYTE FOR BYTE against the live codec, and each
/// rung is a prefix of the next, which is what licenses the zero-padding widening in <c>CellBlobRewriter</c>.
/// </para>
/// </summary>
public class BuiltinBlobLayoutTests
{
    // A movement state with every field non-default, so a dropped or reordered field shows up as a byte difference
    // rather than as two zeros that happen to match.
    private static MovementState Sample() => new()
    {
        VerticalVelocity = -2.5f,
        Grounded = true,
        TimeSinceGrounded = 0.75f,
        JumpBufferRemaining = 0.125f,
        Swimming = true,
        TeleportEpoch = 42u,
        ClimbRateQ = 5,
        SpeedScaleQ = -3,
        HorizontalVelocityXQ = 1000,
        HorizontalVelocityZQ = -2000,
        FacingYawQ = 12345,
    };

    // The payload the LIVE codec writes for one component, taken out of a real one-entity snapshot:
    // [count:4][netId:8][typeId:2] payload [terminator:2].
    private static byte[] LivePayload<T>(T component) where T : struct, IComponent
    {
        var world = new World();
        Entity e = world.Spawn();
        world.Set(e, new NetId(1));
        world.Set(e, component);
        byte[] body = SnapshotWriter.Write(world, MoveProtocol.CreateRegistry(), ReplicationChannels.Persist);
        return body[14..^2];
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void MovementPayloadLength_MatchesTheCodecAtThatGeneration(int generation)
    {
        Assert.Equal(CellBlobFixtures.Movement(generation, Sample()).Length, BuiltinBlobLayout.MovementPayloadLength(generation));
    }

    /// <summary>
    /// The staleness tripwire. If the movement codec grows (or shrinks) without a row being added to
    /// <see cref="BuiltinBlobLayout.MovementPayloadLength"/> for the new generation, this goes red: either the
    /// re-encode above no longer matches the codec's bytes, or the newest row no longer matches its length. Both
    /// failures point at the same fix, and both are what a silent mis-walk of every stored blob would otherwise look
    /// like months later.
    /// </summary>
    [Fact]
    public void MovementTable_NewestRow_IsTheLiveCodecsOwnPayload()
    {
        MovementState m = Sample();
        byte[] live = LivePayload(m);

        Assert.Equal(CellBlobFixtures.Movement(MoveProtocol.WireProtocolVersion, m), live);
        Assert.Equal(live.Length, BuiltinBlobLayout.MovementPayloadLength(MoveProtocol.WireProtocolVersion));
        Assert.Equal(live.Length, BuiltinBlobLayout.PayloadLength(MoveProtocol.MovementTypeId, BuiltinBlobLayout.CurrentWireGeneration));
    }

    /// <summary>The same tripwire for the other built-ins: every one of them is walked by the same table.</summary>
    [Fact]
    public void EveryBuiltinsNewestRow_IsTheLiveCodecsOwnPayload()
    {
        int current = BuiltinBlobLayout.CurrentWireGeneration;

        byte[] position = LivePayload(ReplicatedPosition.InFrame(new WorldFrame(3, -4), new Vector3(1f, 2f, 3f)));
        Assert.Equal(position.Length, BuiltinBlobLayout.PayloadLength(MoveProtocol.PositionTypeId, current));

        byte[] body = LivePayload(new DynamicBodyState
        {
            Orientation = Quaternion.Identity,
            LinearVelocity = new Vector3(1f, 2f, 3f),
            AngularVelocity = new Vector3(4f, 5f, 6f),
        });
        Assert.Equal(body.Length, BuiltinBlobLayout.PayloadLength(MoveProtocol.DynamicBodyTypeId, current));

        byte[] pickup = LivePayload(new PickupState { PayloadId = 7, OwnerNetId = 8 });
        Assert.Equal(pickup.Length, BuiltinBlobLayout.PayloadLength(MoveProtocol.PickupTypeId, current));

        // Identity is the one built-in whose length lives in the stream: [ushort byteLen][byteLen UTF-8 bytes].
        byte[] identity = LivePayload(new PlayerIdentity { DisplayName = "Runner" });
        Assert.Equal(BuiltinBlobLayout.LengthPrefixed, BuiltinBlobLayout.PayloadLength(MoveProtocol.IdentityTypeId, current));
        Assert.Equal(2 + "Runner".Length, identity.Length);
    }

    /// <summary>
    /// Every generation's encoding is a PREFIX of the next one's: the codec has only ever appended. That is what makes
    /// bringing an old payload forward a matter of trailing bytes rather than a per-generation rewrite, so if it ever
    /// stops holding, the widening in <c>CellBlobRewriter</c> stops being correct and needs a real case for the
    /// generation that broke it.
    /// </summary>
    [Fact]
    public void EachGenerationsMovementEncoding_IsAPrefixOfTheNext()
    {
        MovementState m = Sample();
        for (int g = BuiltinBlobLayout.OldestKnownWireGeneration; g < MoveProtocol.WireProtocolVersion; g++)
        {
            byte[] older = CellBlobFixtures.Movement(g, m);
            byte[] newer = CellBlobFixtures.Movement(g + 1, m);
            Assert.True(older.Length <= newer.Length, $"generation {g + 1} shrank the movement payload");
            Assert.Equal(older, newer[..older.Length]);
        }
    }

    /// <summary>
    /// The other half of the widening's licence: the fields each generation appended all encode to ZERO at their
    /// defaults, so padding an old payload with zeros produces exactly what the newer codec would have written for a
    /// state that never set them.
    /// </summary>
    [Fact]
    public void AppendedFieldsAtTheirDefaults_EncodeAsZeroBytes()
    {
        byte[] blank = LivePayload(default(MovementState));
        Assert.All(blank, b => Assert.Equal((byte)0, b));

        // And for a state that DOES set the older fields, the newer encoding is the older one plus zeros.
        MovementState m = Sample();
        m.FacingYawQ = 0;
        m.HorizontalVelocityXQ = 0;
        m.HorizontalVelocityZQ = 0;
        byte[] atSix = CellBlobFixtures.Movement(6, m);
        byte[] atCurrent = CellBlobFixtures.Movement(MoveProtocol.WireProtocolVersion, m);
        Assert.Equal(atSix, atCurrent[..atSix.Length]);
        Assert.All(atCurrent[atSix.Length..], b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public void PayloadLength_UnknownBuiltinId_IsNotPresent()
    {
        // Ids 6..15 are reserved but unclaimed: a body carrying one was not written by any engine build, so a walk
        // rejects the frame instead of guessing a length for it.
        Assert.Equal(BuiltinBlobLayout.NotPresent, BuiltinBlobLayout.PayloadLength(6, BuiltinBlobLayout.CurrentWireGeneration));
    }

    [Fact]
    public void PayloadLength_PickupBeforeItsGeneration_IsNotPresent()
    {
        Assert.Equal(BuiltinBlobLayout.NotPresent,
            BuiltinBlobLayout.PayloadLength(MoveProtocol.PickupTypeId, BuiltinBlobLayout.PickupWireGeneration - 1));
        Assert.Equal(BuiltinBlobLayout.PickupPayloadBytes,
            BuiltinBlobLayout.PayloadLength(MoveProtocol.PickupTypeId, BuiltinBlobLayout.PickupWireGeneration));
    }

    [Fact]
    public void PayloadLength_PositionCrossesTheFramedGeneration()
    {
        Assert.Equal(BuiltinBlobLayout.AbsolutePositionPayloadBytes,
            BuiltinBlobLayout.PayloadLength(MoveProtocol.PositionTypeId, BuiltinBlobLayout.FramedPositionWireGeneration - 1));
        Assert.Equal(BuiltinBlobLayout.FramedPositionPayloadBytes,
            BuiltinBlobLayout.PayloadLength(MoveProtocol.PositionTypeId, BuiltinBlobLayout.FramedPositionWireGeneration));
    }

    [Fact]
    public void MovementPayloadLength_UnknownGeneration_ThrowsRatherThanGuessing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BuiltinBlobLayout.MovementPayloadLength(MoveProtocol.WireProtocolVersion + 1));
    }
}
