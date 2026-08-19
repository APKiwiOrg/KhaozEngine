using System;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The engine-provided cell-blob migration that closes the schema chain's oldest hole: an unframed built-in's byte
/// layout is a function of the WIRE GENERATION the blob was written at, and until schema
/// <see cref="StampedSchemaVersion"/> the blob header did not record it.
/// <para>
/// Wire generation ran from 2 to 10 while the cell-blob schema sat first at v2 and then at v3, so a stored body
/// could carry any of seven different <see cref="MovementState"/> layouts with nothing on disk to tell them apart.
/// From v<see cref="StampedSchemaVersion"/> on, <see cref="CellPersistence"/> stamps
/// <see cref="MoveProtocol.WireProtocolVersion"/> into the header, so a later generation bump needs no schema bump
/// and no new migration at all: the driver reads the stored generation and brings the body forward through
/// <see cref="BuiltinBlobLayout.NormalizeToCurrent"/>, and a blob from a NEWER generation is skipped cleanly instead
/// of misread.
/// </para>
/// <para>
/// This step covers the blobs written before the stamp existed. A v<see cref="UnstampedSchemaVersion"/> body was
/// written somewhere in generations <see cref="OldestUnstampedWireGeneration"/>..current, so it infers the
/// generation by walking the body at each candidate newest-first and keeping the first that parses whole, then
/// rewrites the built-in payloads into this build's layout. A body that walks at no candidate throws, so the
/// <see cref="CellPersistence"/> driver quarantines it rather than restoring garbage.
/// </para>
/// </summary>
public static class WireGenerationBlobMigration
{
    /// <summary>The last cell-blob schema version whose header does NOT record the wire generation its body was
    /// written at (the framed-position layout, <see cref="PositionFrameBlobMigration.FramedPositionSchemaVersion"/>).</summary>
    public const int UnstampedSchemaVersion = PositionFrameBlobMigration.FramedPositionSchemaVersion;

    /// <summary>The cell-blob schema version whose header carries the writing build's
    /// <see cref="MoveProtocol.WireProtocolVersion"/> after the schema version, so a body's built-in layout is never
    /// guessed again.</summary>
    public const int StampedSchemaVersion = 4;

    /// <summary>The oldest wire generation a v<see cref="UnstampedSchemaVersion"/> blob can have been written at: the
    /// schema moved to v3 for the floating-origin wire, so its bodies start at
    /// <see cref="BuiltinBlobLayout.FramedPositionWireGeneration"/>.</summary>
    public const int OldestUnstampedWireGeneration = BuiltinBlobLayout.FramedPositionWireGeneration;

    /// <summary>
    /// The <see cref="CellSnapshotMigration"/> that brings a v<see cref="UnstampedSchemaVersion"/> body to
    /// v<see cref="StampedSchemaVersion"/>: the built-in payloads are rewritten into this build's wire generation and
    /// the driver stamps that generation into the header. Register it with
    /// <c>CellPersistenceConfig.RegisterMigration(3, WireGenerationBlobMigration.NormalizeV3ToV4)</c>, or rely on the
    /// engine default (any <see cref="CellPersistence"/> at schema &gt;= 4 folds it in). Throws on a body that walks
    /// at no candidate generation, so the driver quarantines it.
    /// </summary>
    public static byte[] NormalizeV3ToV4(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return CellBlobRewriter.RewriteInferring(body, OldestUnstampedWireGeneration,
            BuiltinBlobLayout.CurrentWireGeneration, BuiltinBlobLayout.CurrentWireGeneration,
            widenNetIds: false, "v3 (framed position, wire generation unrecorded)");
    }
}
