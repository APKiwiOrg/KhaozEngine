using System;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The engine-provided cell-blob migration for the 10.0.0 <see cref="KhaozEngine.Replication.NetId"/> widening: the
/// first real engine-provided migration on the Prompt-D cell-blob schema chain. It rewrites a persisted snapshot
/// body whose entity ids are 32-bit (schema <see cref="NetId32SchemaVersion"/>, the pre-10.0.0 wire) into the 64-bit
/// form (schema <see cref="NetId64SchemaVersion"/>), leaving every component frame byte-for-byte identical - only the
/// per-entity id field grows from 4 to 8 bytes (little-endian, high 32 bits zero = node 0, so id 42 stays 42). A body
/// that does not decode as a well-formed v1 snapshot throws, so the <see cref="CellPersistence"/> driver quarantines
/// it rather than crash-looping. A <see cref="CellPersistence"/> at schema version &gt;=
/// <see cref="NetId64SchemaVersion"/> includes this step automatically (see
/// <see cref="CellPersistenceConfig.IncludeEngineMigrations"/>); it is also exposed as a plain
/// <see cref="CellSnapshotMigration"/> so a consumer can register it explicitly.
/// </summary>
/// <remarks>
/// The walk it needs to find each entity boundary runs at wire generation
/// <see cref="NetId32WireGeneration"/>'s built-in layout, read from <see cref="BuiltinBlobLayout"/> rather than from
/// a private table. The private table this file used to carry stated the movement payload at 13 bytes, which was
/// right for the generation a v1 blob is written at and was never wrong here, but it was also the SECOND copy of a
/// layout that had grown six times elsewhere, and the copy in <see cref="PositionFrameBlobMigration"/> was the one
/// that went stale (#353). There is one table now.
/// </remarks>
public static class NetIdBlobMigration
{
    /// <summary>The cell-blob schema version whose bodies carry 32-bit entity ids (the pre-10.0.0 layout).</summary>
    public const int NetId32SchemaVersion = 1;

    /// <summary>The cell-blob schema version whose bodies carry 64-bit entity ids (the 10.0.0 layout).</summary>
    public const int NetId64SchemaVersion = 2;

    /// <summary>The wire generation a v<see cref="NetId32SchemaVersion"/> blob was written at. The pre-10.0.0 line is
    /// generation 1 exactly: the schema moved to v2 in the same release that moved the wire to generation 2, so
    /// unlike the later schema versions this one is unambiguous and needs no inference.</summary>
    public const int NetId32WireGeneration = BuiltinBlobLayout.OldestKnownWireGeneration;

    /// <summary>
    /// The <see cref="CellSnapshotMigration"/> that widens a v1 (32-bit netId) snapshot body to v2 (64-bit). Register
    /// it with <c>CellPersistenceConfig.RegisterMigration(1, NetIdBlobMigration.WidenV1ToV2)</c>, or rely on the
    /// engine default (any <see cref="CellPersistence"/> at schema &gt;= 2 folds it in). Component payloads are left
    /// exactly as stored - bringing them forward across the wire generations is the later steps' job - so the output
    /// is a v2 body at generation <see cref="NetId32WireGeneration"/>'s layout, which is byte-identical to a
    /// generation-2 one. Throws on a malformed body so the driver quarantines it.
    /// </summary>
    public static byte[] WidenV1ToV2(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return CellBlobRewriter.RewriteInferring(body, NetId32WireGeneration, NetId32WireGeneration,
            CellBlobRewriter.KeepSourceGeneration, widenNetIds: true, "v1 (32-bit netId)");
    }
}
