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
/// generation by walking the body at every candidate, discarding the ones that recovered something no build writes
/// (<see cref="CellBlobWalkPolicy"/>), and rewriting the built-in payloads into this build's layout. A body that
/// walks at no candidate throws, and so does one that walks at several to different results
/// (<see cref="AmbiguousCellBlobGenerationException"/>), so the <see cref="CellPersistence"/> driver quarantines it
/// rather than restoring garbage or guessing.
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
    /// at no candidate generation, and on one that walks at several to different results
    /// (<see cref="AmbiguousCellBlobGenerationException"/>), so the driver quarantines it either way.
    /// </summary>
    public static byte[] NormalizeV3ToV4(byte[] body) => NormalizeV3ToV4(body, CellBlobMigrationOptions.None);

    /// <summary>
    /// As <see cref="NormalizeV3ToV4(byte[])"/>, with what the reader knows about the save
    /// (<see cref="CellBlobMigrationOptions"/>): the live registry, which discards candidate generations that
    /// recovered ids nobody registered, and an assumed generation, which replaces the inference outright. A
    /// <see cref="CellPersistence"/> passes its own config through, so this overload is for a consumer registering
    /// the step by hand.
    /// </summary>
    public static byte[] NormalizeV3ToV4(byte[] body, CellBlobMigrationOptions options)
        => NormalizeV3ToV4(body, options, new CellBlobMigrationContext());

    // The chain-aware form. When an earlier step in the same chain already brought the body to this build's
    // generation (the v2 -> v3 step does), there is nothing here to infer and nothing to rewrite: before the #353 fix
    // round this walked a v2 blob's already-normalized body all over again at candidates 9 and 10, which was a second
    // independent chance to mis-infer for no gain at all.
    internal static byte[] NormalizeV3ToV4(byte[] body, CellBlobMigrationOptions options,
        CellBlobMigrationContext context)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        options.Validate();

        int current = BuiltinBlobLayout.CurrentWireGeneration;
        if (context.KnownWireGeneration == current) return body;   // already normalized by an earlier chain step

        // The v3 vintage starts where the v2 one stops and runs to this build's generation. An assumption below it
        // is about the v2 bodies in the same store, so this step infers rather than refusing every v3 body.
        byte[] result = context.ResolvedGeneration(options, OldestUnstampedWireGeneration, current) is int known
            ? CellBlobRewriter.Rewrite(body, known, current, widenNetIds: false)
            : CellBlobRewriter.RewriteInferring(body, OldestUnstampedWireGeneration, current, current,
                widenNetIds: false, "v3 (framed position, wire generation unrecorded)",
                CellBlobWalkPolicy.Inferring(options.KnownExtensionId));
        context.KnownWireGeneration = current;
        return result;
    }
}
