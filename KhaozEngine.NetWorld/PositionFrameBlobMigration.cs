using System;
using System.IO;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The engine-provided cell-blob migration for the floating-origin wire: it brings a persisted snapshot body whose
/// <see cref="ReplicatedPosition"/> frames are three ABSOLUTE float32s (schema
/// <see cref="AbsolutePositionSchemaVersion"/>, the layout before the framed wire) forward to the framed layout
/// (schema <see cref="FramedPositionSchemaVersion"/>), where each one is
/// <c>[frameX:short][frameZ:short][local:3 float]</c>.
/// <para>
/// The conversion is a pure widening and it loses nothing: the stored triple is an absolute world position, and
/// <see cref="WorldFrame.Origin"/> has an exactly-zero anchor, so stamping the frame as Origin and keeping the triple
/// verbatim denotes the identical world position bit-for-bit. The owning cell converts it into its own frame on
/// restore, exactly, so a save written by an unframed server boots straight into a framed one.
/// </para>
/// <para>
/// Without this step every persisted cell would fail to decode on the first boot after the upgrade and be
/// quarantined as corrupt: the reader would want 16 bytes where the blob has 12. Every other component frame is
/// copied through byte-for-byte. A body that does not decode as a well-formed snapshot throws, so the
/// <see cref="CellPersistence"/> driver quarantines it rather than crash-looping.
/// </para>
/// </summary>
/// <remarks>
/// The built-in payload lengths below are the CURRENT layout of each built-in other than position. The cell-blob
/// schema version was never bumped as the movement built-in grew across wire generations 3 to 8, so a blob written
/// by one of those older builds already walks wrong here, exactly as it does in
/// <see cref="NetIdBlobMigration"/>'s own chain. That is a pre-existing gap in the schema chain rather than one this
/// step introduces, and it is filed rather than papered over.
/// </remarks>
public static class PositionFrameBlobMigration
{
    /// <summary>The cell-blob schema version whose position frames are three absolute float32s (the layout before
    /// the framed wire).</summary>
    public const int AbsolutePositionSchemaVersion = 2;

    /// <summary>The cell-blob schema version whose position frames carry an island-frame stamp plus a frame-local
    /// offset (the framed-wire layout).</summary>
    public const int FramedPositionSchemaVersion = 3;

    /// <summary>
    /// The <see cref="CellSnapshotMigration"/> that rewrites a v2 (absolute position) snapshot body to v3 (framed).
    /// Register it with <c>CellPersistenceConfig.RegisterMigration(2, PositionFrameBlobMigration.FrameV2ToV3)</c>, or
    /// rely on the engine default (any <see cref="CellPersistence"/> at schema &gt;= 3 folds it in). Throws on a
    /// malformed body so the driver quarantines it.
    /// </summary>
    public static byte[] FrameV2ToV3(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var input = new MemoryStream(body, writable: false);
        using var br = new BinaryReader(input);
        using var output = new MemoryStream(body.Length + 32);
        using var bw = new BinaryWriter(output);

        int count = ReadInt32(br, input, "entity count");
        if (count < 0) throw new InvalidOperationException($"Snapshot entity count {count} is negative.");
        bw.Write(count);

        for (int i = 0; i < count; i++)
        {
            bw.Write(ReadInt64(br, input, "entity net id"));   // ids are already 64-bit at v2
            CopyEntityComponents(br, input, bw);
        }

        if (input.Position != input.Length)
            throw new InvalidOperationException(
                $"Snapshot has {input.Length - input.Position} trailing byte(s) after {count} entities.");

        bw.Flush();
        return output.ToArray();
    }

    // Copies one entity's component-frame stream (up to and including the [ushort 0] terminator), rewriting only the
    // position frame and passing every other payload through verbatim.
    private static void CopyEntityComponents(BinaryReader br, MemoryStream input, BinaryWriter bw)
    {
        while (true)
        {
            ushort typeId = ReadUInt16(br, input, "component type id");
            bw.Write(typeId);
            if (typeId == 0) return;   // end-of-entity terminator

            if (ReplicationRegistry.IsExtension(typeId))
            {
                int len = br.Read7BitEncodedInt();   // extension payloads carry their own length
                if (len < 0) throw new InvalidOperationException($"Extension component {typeId} has a negative length {len}.");
                bw.Write7BitEncodedInt(len);
                CopyBytes(br, input, bw, len, typeId);
                continue;
            }

            if (typeId == MoveProtocol.PositionTypeId)
            {
                // The one rewrite: stamp WorldFrame.Origin ahead of the untouched absolute triple. Origin's anchor is
                // exactly Vector3.Zero, so {Origin, absolute} reads back the identical world position.
                bw.Write((short)0);   // frame X
                bw.Write((short)0);   // frame Z
                CopyBytes(br, input, bw, 12, typeId);
                continue;
            }

            CopyBytes(br, input, bw, BuiltinPayloadLength(typeId, br, input, bw), typeId);
        }
    }

    // The remaining payload byte count of a non-position built-in (unframed) frame at the v2 layout. For the
    // length-prefixed PlayerIdentity it copies its 2-byte length prefix through and returns the utf8 byte count.
    // Throws on an unknown built-in id (an undecodable body, which the driver quarantines).
    private static int BuiltinPayloadLength(ushort typeId, BinaryReader br, MemoryStream input, BinaryWriter bw) => typeId switch
    {
        MoveProtocol.MovementTypeId => 24,   // float + bool + 2 float + bool + uint + 2 sbyte + 2 short
        MoveProtocol.IdentityTypeId => CopyIdentityLengthPrefix(br, input, bw),   // [ushort len] then len utf8 bytes
        MoveProtocol.DynamicBodyTypeId => 40,   // quaternion + 2 * Vector3
        MoveProtocol.PickupTypeId => 16,        // 2 * long
        _ => throw new InvalidOperationException(
            $"Snapshot built-in component {typeId} is unknown at the v2 layout; cannot migrate."),
    };

    private static int CopyIdentityLengthPrefix(BinaryReader br, MemoryStream input, BinaryWriter bw)
    {
        ushort byteLen = ReadUInt16(br, input, "display-name length");
        bw.Write(byteLen);
        return byteLen;
    }

    private static void CopyBytes(BinaryReader br, MemoryStream input, BinaryWriter bw, int len, ushort typeId)
    {
        if (len < 0 || input.Position + len > input.Length)
            throw new InvalidOperationException($"Snapshot component {typeId} payload (len {len}) runs past the buffer.");
        byte[] payload = br.ReadBytes(len);
        if (payload.Length != len) throw new InvalidOperationException($"Snapshot component {typeId} payload truncated.");
        bw.Write(payload);
    }

    private static int ReadInt32(BinaryReader br, MemoryStream ms, string what)
    {
        if (ms.Position + 4 > ms.Length) throw new InvalidOperationException($"Snapshot truncated reading {what}.");
        return br.ReadInt32();
    }

    private static long ReadInt64(BinaryReader br, MemoryStream ms, string what)
    {
        if (ms.Position + 8 > ms.Length) throw new InvalidOperationException($"Snapshot truncated reading {what}.");
        return br.ReadInt64();
    }

    private static ushort ReadUInt16(BinaryReader br, MemoryStream ms, string what)
    {
        if (ms.Position + 2 > ms.Length) throw new InvalidOperationException($"Snapshot truncated reading {what}.");
        return br.ReadUInt16();
    }
}
