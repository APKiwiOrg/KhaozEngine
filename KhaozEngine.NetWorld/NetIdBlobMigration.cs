using System;
using System.IO;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The engine-provided cell-blob migration for the 10.0.0 <see cref="NetId"/> widening: the first real engine-provided
/// migration on the Prompt-D cell-blob schema chain. It rewrites a persisted snapshot body whose entity ids are 32-bit
/// (schema <see cref="NetId32SchemaVersion"/>, the pre-10.0.0 wire) into the 64-bit form (schema
/// <see cref="NetId64SchemaVersion"/>), leaving every component frame byte-for-byte identical - only the per-entity id
/// field grows from 4 to 8 bytes (little-endian, high 32 bits zero = node 0, so id 42 stays 42). A body that does not
/// decode as a well-formed v1 snapshot throws, so the <see cref="CellPersistence"/> driver quarantines it rather than
/// crash-looping. A <see cref="CellPersistence"/> at schema version &gt;= <see cref="NetId64SchemaVersion"/> includes
/// this step automatically (see <see cref="CellPersistenceConfig.IncludeEngineMigrations"/>); it is also exposed as a
/// plain <see cref="CellSnapshotMigration"/> so a consumer can register it explicitly.
/// </summary>
public static class NetIdBlobMigration
{
    /// <summary>The cell-blob schema version whose bodies carry 32-bit entity ids (the pre-10.0.0 layout).</summary>
    public const int NetId32SchemaVersion = 1;

    /// <summary>The cell-blob schema version whose bodies carry 64-bit entity ids (the 10.0.0 layout).</summary>
    public const int NetId64SchemaVersion = 2;

    /// <summary>
    /// The <see cref="CellSnapshotMigration"/> that widens a v1 (32-bit netId) snapshot body to v2 (64-bit). Register it
    /// with <c>CellPersistenceConfig.RegisterMigration(1, NetIdBlobMigration.WidenV1ToV2)</c>, or rely on the engine
    /// default (any <see cref="CellPersistence"/> at schema &gt;= 2 folds it in). Throws on a malformed body so the
    /// driver quarantines it.
    /// </summary>
    public static byte[] WidenV1ToV2(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var input = new MemoryStream(body, writable: false);
        using var br = new BinaryReader(input);
        using var output = new MemoryStream(body.Length + 16);
        using var bw = new BinaryWriter(output);

        int count = ReadInt32(br, input, "entity count");
        if (count < 0) throw new InvalidOperationException($"Snapshot entity count {count} is negative.");
        bw.Write(count);   // entity count stays a 32-bit int; only the per-entity id field widens

        for (int i = 0; i < count; i++)
        {
            int oldId = ReadInt32(br, input, "entity net id");
            // Widen the 32-bit id into node 0's low-48 counter space. Reading it as UNSIGNED keeps a large counter
            // (up to 2^32-1) positive; a normal id (1, 2, 3, …) is numerically unchanged.
            bw.Write((long)(uint)oldId);
            CopyEntityComponents(br, input, bw);
        }

        if (input.Position != input.Length)
            throw new InvalidOperationException(
                $"Snapshot has {input.Length - input.Position} trailing byte(s) after {count} entities.");

        bw.Flush();
        return output.ToArray();
    }

    // Copies one entity's component-frame stream (up to and including the [ushort 0] terminator) verbatim, parsing
    // enough of each frame to find the entity boundary. Component payload bytes are unchanged by the widening.
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
            }
            else
            {
                CopyBytes(br, input, bw, BuiltinPayloadLength(typeId, br, input, bw), typeId);
            }
        }
    }

    // The remaining payload byte count of a built-in (unframed) frame at the v1 layout - unchanged by the widening
    // (only the entity id grew). For the length-prefixed PlayerIdentity it copies its 2-byte length prefix through and
    // returns the utf8 byte count. Players are excluded from cell blobs so id 3 does not normally appear, but the walk
    // handles it for robustness. Throws on an unknown built-in id (an undecodable body -> the driver quarantines it).
    private static int BuiltinPayloadLength(ushort typeId, BinaryReader br, MemoryStream input, BinaryWriter bw) => typeId switch
    {
        MoveProtocol.PositionTypeId => 12,   // 3 * float
        MoveProtocol.MovementTypeId => 13,   // float + bool + float + float
        MoveProtocol.IdentityTypeId => CopyIdentityLengthPrefix(br, input, bw),   // [ushort len] then len utf8 bytes
        _ => throw new InvalidOperationException(
            $"Snapshot built-in component {typeId} is unknown at the v1 layout; cannot migrate."),
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

    private static ushort ReadUInt16(BinaryReader br, MemoryStream ms, string what)
    {
        if (ms.Position + 2 > ms.Length) throw new InvalidOperationException($"Snapshot truncated reading {what}.");
        return br.ReadUInt16();
    }
}
