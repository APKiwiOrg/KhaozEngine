using System;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The on-disk payload layout of the engine's BUILT-IN replicated components, indexed by the wire generation
/// (<see cref="MoveProtocol.WireProtocolVersion"/>) that wrote them.
/// <para>
/// A built-in frame is UNFRAMED (no length prefix, see <see cref="KhaozEngine.Replication.ReplicationRegistry.IsExtension"/>),
/// so anything that has to walk a persisted snapshot body frame by frame - every cell-blob migration, and the
/// driver's own bring-forward pass - needs each built-in's payload byte count. That count is a function of the wire
/// generation the body was written at, not of the build reading it: the movement built-in alone grew six times
/// between generations 2 and 10. This type is the single table both sides read, so the two migrations cannot drift
/// apart, and <c>BuiltinBlobLayoutTests</c> derives every row from the shipped codec rather than trusting the
/// numbers here.
/// </para>
/// <para>
/// Every growth so far APPENDED a field whose default encoding is zero bytes, so bringing a payload forward from an
/// older generation is the old bytes followed by zeros - which is what <see cref="NormalizeToCurrent"/> does. The one
/// exception is <see cref="MoveProtocol.PositionTypeId"/>, which was restructured (not appended to) at generation
/// <see cref="FramedPositionWireGeneration"/> and gets an explicit rewrite. A future change that is neither an
/// append nor covered here needs its own case in <see cref="CellBlobRewriter"/>, and the layout row below it.
/// </para>
/// </summary>
public static class BuiltinBlobLayout
{
    /// <summary>Payload-length sentinel for a frame that carries its own length inside the stream:
    /// <see cref="MoveProtocol.IdentityTypeId"/> is <c>[ushort byteLen][byteLen UTF-8 bytes]</c>.</summary>
    public const int LengthPrefixed = -1;

    /// <summary>Payload-length sentinel for a component that did not exist at the queried wire generation (so a body
    /// carrying it was NOT written at that generation).</summary>
    public const int NotPresent = -2;

    /// <summary>The oldest wire generation this table describes: the pre-10.0.0 line, whose bodies carry 32-bit
    /// entity ids. Generations 1 and 2 share an identical component layout (the 10.0.0 break widened the entity id,
    /// not any payload), which is why the <see cref="PositionFrameBlobMigration"/> candidate range can start at 2.</summary>
    public const int OldestKnownWireGeneration = 1;

    /// <summary>The generation at which <see cref="ReplicatedPosition"/> stopped encoding three absolute float32s and
    /// started encoding <c>[frameX:short][frameZ:short][local:3 float]</c> (the floating-origin wire).</summary>
    public const int FramedPositionWireGeneration = 9;

    /// <summary>The generation that added <see cref="MovementState.Swimming"/>, the second bool in the movement
    /// payload. Below it a movement payload has one bool byte, from it two.</summary>
    public const int SwimmingWireGeneration = 3;

    /// <summary>Byte offset of <see cref="MovementState.Grounded"/> in a movement payload, at every generation: it
    /// follows <see cref="MovementState.VerticalVelocity"/>, which has always been the first field.</summary>
    public const int MovementGroundedOffset = 4;

    /// <summary>Byte offset of <see cref="MovementState.Swimming"/> in a movement payload written at
    /// <see cref="SwimmingWireGeneration"/> or later: it was appended after <c>JumpBufferRemaining</c> and nothing
    /// has been inserted before it since.</summary>
    public const int MovementSwimmingOffset = 13;

    /// <summary>The generation that introduced <see cref="PickupState"/> as a built-in id. A body carrying id
    /// <see cref="MoveProtocol.PickupTypeId"/> cannot have been written before it.</summary>
    public const int PickupWireGeneration = 8;

    /// <summary>Payload bytes of a pre-<see cref="FramedPositionWireGeneration"/> <see cref="ReplicatedPosition"/>:
    /// three absolute float32s.</summary>
    public const int AbsolutePositionPayloadBytes = 12;

    /// <summary>Payload bytes of a framed <see cref="ReplicatedPosition"/>: the island-frame stamp then the
    /// frame-local offset.</summary>
    public const int FramedPositionPayloadBytes = 16;

    /// <summary>Payload bytes of a <see cref="DynamicBodyState"/>: an orientation quaternion plus two Vector3s. It
    /// has never changed shape.</summary>
    public const int DynamicBodyPayloadBytes = 40;

    /// <summary>Payload bytes of a <see cref="PickupState"/>: two int64s. It has never changed shape.</summary>
    public const int PickupPayloadBytes = 16;

    /// <summary>The wire generation this build writes, and the layout <see cref="NormalizeToCurrent"/> brings a body
    /// to. Always <see cref="MoveProtocol.WireProtocolVersion"/>.</summary>
    public static int CurrentWireGeneration => MoveProtocol.WireProtocolVersion;

    /// <summary>
    /// The payload byte count of built-in <paramref name="typeId"/> in a body written at
    /// <paramref name="wireGeneration"/>, or <see cref="LengthPrefixed"/> / <see cref="NotPresent"/>. An id the
    /// engine does not own reports <see cref="NotPresent"/> rather than throwing, so a walk can reject the candidate
    /// generation instead of blowing up on a mis-parsed byte pair.
    /// </summary>
    public static int PayloadLength(ushort typeId, int wireGeneration)
    {
        EnsureKnownGeneration(wireGeneration);
        return typeId switch
        {
            MoveProtocol.PositionTypeId => wireGeneration >= FramedPositionWireGeneration
                ? FramedPositionPayloadBytes
                : AbsolutePositionPayloadBytes,
            MoveProtocol.MovementTypeId => MovementPayloadLength(wireGeneration),
            MoveProtocol.IdentityTypeId => LengthPrefixed,
            MoveProtocol.DynamicBodyTypeId => DynamicBodyPayloadBytes,
            MoveProtocol.PickupTypeId => wireGeneration >= PickupWireGeneration ? PickupPayloadBytes : NotPresent,
            _ => NotPresent,
        };
    }

    /// <summary>
    /// The payload byte count of a <see cref="MovementState"/> frame written at <paramref name="wireGeneration"/>.
    /// The ladder below is the codec's own append-only history (<see cref="MoveProtocol.CreateRegistry"/>, and the
    /// generation notes on <see cref="MoveProtocol.WireProtocolVersion"/>), and every row is pinned against a
    /// field-by-field re-encode in <c>BuiltinBlobLayoutTests</c>.
    /// </summary>
    public static int MovementPayloadLength(int wireGeneration) => wireGeneration switch
    {
        // VerticalVelocity(4) + Grounded(1) + TimeSinceGrounded(4) + JumpBufferRemaining(4). Generation 2 widened
        // the entity id, not this payload, so it shares generation 1's row.
        1 or 2 => 13,
        3 => 14,        // + Swimming (bool, 1)
        4 => 18,        // + TeleportEpoch (uint, 4)
        5 => 19,        // + ClimbRateQ (sbyte, 1)
        6 => 20,        // + SpeedScaleQ (sbyte, 1)
        // + HorizontalVelocityXQ / HorizontalVelocityZQ (short, 2 each). Generation 8 added the pickup built-in and
        // 9 reshaped position, neither of which touched this payload, so all three share generation 7's row.
        7 or 8 or 9 => 24,
        10 => 26,       // + FacingYawQ (short, 2)
        _ => throw new ArgumentOutOfRangeException(nameof(wireGeneration), wireGeneration,
            $"No cell-blob movement layout is recorded for wire generation {wireGeneration}. Add its row here (and " +
            "its CellBlobRewriter case if the change was not a plain append) in the same change that bumps " +
            "MoveProtocol.WireProtocolVersion."),
    };

    /// <summary>
    /// Rewrites a persisted snapshot <paramref name="body"/> whose built-in payloads are at
    /// <paramref name="fromWireGeneration"/>'s layout into this build's layout, so the live registry decodes it.
    /// Entity ids are untouched (they are 64-bit from cell-blob schema v2 on). Throws on a body that does not walk
    /// cleanly at that generation, so the <see cref="CellPersistence"/> driver quarantines it rather than restoring
    /// garbage.
    /// </summary>
    public static byte[] NormalizeToCurrent(byte[] body, int fromWireGeneration)
    {
        ArgumentNullException.ThrowIfNull(body);
        EnsureKnownGeneration(fromWireGeneration);
        return CellBlobRewriter.Rewrite(body, fromWireGeneration, CurrentWireGeneration, widenNetIds: false);
    }

    private static void EnsureKnownGeneration(int wireGeneration)
    {
        if (wireGeneration < OldestKnownWireGeneration || wireGeneration > CurrentWireGeneration)
            throw new ArgumentOutOfRangeException(nameof(wireGeneration), wireGeneration,
                $"Wire generation must be between {OldestKnownWireGeneration} and {CurrentWireGeneration}.");
    }
}
