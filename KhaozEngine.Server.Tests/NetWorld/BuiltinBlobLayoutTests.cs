using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
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
/// off it. They are re-derived instead by <see cref="CellBlobFixtures.Movement(int, MovementState, MovementOwnerState)"/>, a field-by-field re-encode of
/// what the codec wrote at each generation, taken from the generation notes on
/// <see cref="MoveProtocol.WireProtocolVersion"/> (3 added <c>Swimming</c>, 4 <c>TeleportEpoch</c>, 5
/// <c>ClimbRateQ</c>, 6 <c>SpeedScaleQ</c>, 7 the two horizontal-velocity shorts, 10 <c>FacingYawQ</c>, 11 the
/// commitment, and 12 moved the two feel timers out to <see cref="MovementOwnerState"/>). Three properties tie that
/// ladder to reality: its newest rung is compared BYTE FOR BYTE against the live codec, each rung below the owner
/// split is a prefix of the next, which is what licenses the zero-padding widening in <c>CellBlobRewriter</c>, and
/// the split rung is exactly the rung below it with the timer bytes cut out, which is what licenses the split.
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
        Swimming = true,
        TeleportEpoch = 42u,
        ClimbRateQ = 5,
        SpeedScaleQ = -3,
        HorizontalVelocityXQ = 1000,
        HorizontalVelocityZQ = -2000,
        FacingYawQ = 12345,
        Commitment = new MovementCommitment(7u, new Vector2(3f, 4f), 11f, 9f, 17f, 0.2f, 0.1f, 4f),
    };

    private static readonly MovementOwnerState SampleOwner = new() { TimeSinceGrounded = 0.75f, JumpBufferRemaining = 0.125f };

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
    [InlineData(11)]
    [InlineData(12)]
    public void MovementPayloadLength_MatchesTheCodecAtThatGeneration(int generation)
    {
        Assert.Equal(CellBlobFixtures.Movement(generation, Sample(), SampleOwner).Length,
            BuiltinBlobLayout.MovementPayloadLength(generation));
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

        Assert.Equal(CellBlobFixtures.Movement(MoveProtocol.WireProtocolVersion, m, SampleOwner), live);
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

        // Persistence writes the owner-only built-in unconditionally (OwnerOnly scopes client serving, not storage).
        byte[] owner = LivePayload(SampleOwner);
        Assert.Equal(CellBlobFixtures.MovementOwner(SampleOwner), owner);
        Assert.Equal(owner.Length, BuiltinBlobLayout.PayloadLength(MoveProtocol.MovementOwnerTypeId, current));

        // Identity is the one built-in whose length lives in the stream: [ushort byteLen][byteLen UTF-8 bytes].
        byte[] identity = LivePayload(new PlayerIdentity { DisplayName = "Runner" });
        Assert.Equal(BuiltinBlobLayout.LengthPrefixed, BuiltinBlobLayout.PayloadLength(MoveProtocol.IdentityTypeId, current));
        Assert.Equal(2 + "Runner".Length, identity.Length);
    }

    /// <summary>
    /// Every generation's encoding is a PREFIX of the next one's on each side of the owner split: the codec only
    /// appended there. That is what makes bringing an old payload forward a matter of trailing bytes rather than a
    /// per-generation rewrite, so if it ever stops holding, the widening in <c>CellBlobRewriter</c> stops being
    /// correct and needs a real case for the generation that broke it. The split generation itself is pinned by
    /// <see cref="TheOwnerSplit_CutsExactlyTheTimerBytes"/>.
    /// </summary>
    [Fact]
    public void EachGenerationsMovementEncoding_IsAPrefixOfTheNext()
    {
        MovementState m = Sample();
        for (int g = BuiltinBlobLayout.OldestKnownWireGeneration; g < MoveProtocol.WireProtocolVersion; g++)
        {
            if (g + 1 == BuiltinBlobLayout.MovementOwnerWireGeneration) continue;
            byte[] older = CellBlobFixtures.Movement(g, m, SampleOwner);
            byte[] newer = CellBlobFixtures.Movement(g + 1, m, SampleOwner);
            Assert.True(older.Length <= newer.Length, $"generation {g + 1} shrank the movement payload");
            Assert.Equal(older, newer[..older.Length]);
        }
    }

    /// <summary>
    /// The owner split's licence: the payload at <see cref="BuiltinBlobLayout.MovementOwnerWireGeneration"/> is the
    /// one below it with exactly the eight timer bytes at <see cref="BuiltinBlobLayout.MovementTimersOffset"/> cut
    /// out, and those eight bytes are exactly what the owner codec writes. That is the rewrite
    /// <c>MovementOwnerBlobSplit</c> performs, so this is what keeps it byte-exact.
    /// </summary>
    [Fact]
    public void TheOwnerSplit_CutsExactlyTheTimerBytes()
    {
        MovementState m = Sample();
        int split = BuiltinBlobLayout.MovementOwnerWireGeneration;
        int at = BuiltinBlobLayout.MovementTimersOffset;
        int cut = BuiltinBlobLayout.MovementOwnerPayloadBytes;
        byte[] before = CellBlobFixtures.Movement(split - 1, m, SampleOwner);
        byte[] after = CellBlobFixtures.Movement(split, m, SampleOwner);

        Assert.Equal(before.Length - cut, after.Length);
        Assert.Equal(before[..at].Concat(before[(at + cut)..]).ToArray(), after);
        Assert.Equal(CellBlobFixtures.MovementOwner(SampleOwner), before[at..(at + cut)]);
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

        byte[] blankOwner = LivePayload(default(MovementOwnerState));
        Assert.All(blankOwner, b => Assert.Equal((byte)0, b));

        // And for a state that DOES set the older fields, the newer encoding is the older one plus zeros, up to the
        // last generation before the owner split (which removes bytes rather than appending them).
        MovementState m = Sample();
        m.FacingYawQ = 0;
        m.HorizontalVelocityXQ = 0;
        m.HorizontalVelocityZQ = 0;
        m.Commitment = default;
        byte[] atSix = CellBlobFixtures.Movement(6, m, SampleOwner);
        byte[] beforeSplit = CellBlobFixtures.Movement(BuiltinBlobLayout.MovementOwnerWireGeneration - 1, m, SampleOwner);
        Assert.Equal(atSix, beforeSplit[..atSix.Length]);
        Assert.All(beforeSplit[atSix.Length..], b => Assert.Equal((byte)0, b));
    }

    /// <summary>
    /// The two bool bytes the inference walk validates (a payload whose ground or swim byte is neither 0 nor 1 was
    /// not written by the codec, so the candidate generation that read it is wrong). Their offsets are derived here
    /// from the same field-by-field re-encode as the lengths, so a field inserted ahead of either one moves the
    /// constant instead of quietly turning the check into noise.
    /// </summary>
    [Fact]
    public void MovementBoolOffsets_AreWhereTheCodecWritesThem()
    {
        MovementState m = Sample();
        m.Grounded = true;
        m.Swimming = false;
        int current = MoveProtocol.WireProtocolVersion;
        byte[] payload = CellBlobFixtures.Movement(current, m, SampleOwner);
        Assert.Equal(1, payload[BuiltinBlobLayout.MovementGroundedOffset]);
        Assert.Equal(0, payload[BuiltinBlobLayout.MovementSwimmingOffset(current)]);

        m.Grounded = false;
        m.Swimming = true;
        int swimGen = BuiltinBlobLayout.SwimmingWireGeneration;
        payload = CellBlobFixtures.Movement(swimGen, m, SampleOwner);
        Assert.Equal(0, payload[BuiltinBlobLayout.MovementGroundedOffset]);
        Assert.Equal(1, payload[BuiltinBlobLayout.MovementSwimmingOffset(swimGen)]);

        // And the swim byte is the last one that generation wrote, so it exists from exactly there.
        Assert.Equal(BuiltinBlobLayout.MovementSwimmingOffset(swimGen) + 1, BuiltinBlobLayout.MovementPayloadLength(swimGen));
        Assert.Equal(BuiltinBlobLayout.MovementSwimmingOffset(swimGen), BuiltinBlobLayout.MovementPayloadLength(swimGen - 1));

        // Every generation from the swim flag on, checked against a payload whose only set byte is the swim flag, so
        // the offset moving at the owner split is caught on both sides of it.
        var swimOnly = new MovementState { Swimming = true };
        for (int g = swimGen; g <= current; g++)
        {
            byte[] p = CellBlobFixtures.Movement(g, swimOnly);
            Assert.Equal(1, p[BuiltinBlobLayout.MovementSwimmingOffset(g)]);
            Assert.Equal(1, p.Count(b => b != 0));
        }
    }

    [Fact]
    public void PayloadLength_UnknownBuiltinId_IsNotPresent()
    {
        // Ids 7..15 are reserved but unclaimed: a body carrying one was not written by any engine build, so a walk
        // rejects the frame instead of guessing a length for it.
        Assert.Equal(BuiltinBlobLayout.NotPresent, BuiltinBlobLayout.PayloadLength(7, BuiltinBlobLayout.CurrentWireGeneration));
    }

    [Fact]
    public void PayloadLength_MovementOwnerBeforeItsGeneration_IsNotPresent()
    {
        Assert.Equal(BuiltinBlobLayout.NotPresent, BuiltinBlobLayout.PayloadLength(MoveProtocol.MovementOwnerTypeId,
            BuiltinBlobLayout.MovementOwnerWireGeneration - 1));
        Assert.Equal(BuiltinBlobLayout.MovementOwnerPayloadBytes, BuiltinBlobLayout.PayloadLength(
            MoveProtocol.MovementOwnerTypeId, BuiltinBlobLayout.MovementOwnerWireGeneration));
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
