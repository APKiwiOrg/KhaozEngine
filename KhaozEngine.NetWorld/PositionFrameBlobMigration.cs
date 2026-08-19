using System;
using KhaozEngine.Primitives;

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
/// quarantined as corrupt: the reader would want 16 bytes where the blob has 12.
/// </para>
/// </summary>
/// <remarks>
/// A v<see cref="AbsolutePositionSchemaVersion"/> blob is NOT one layout. The schema version sat at 2 while the wire
/// generation ran from 2 to 8, and the movement built-in grew in five of those steps, so the body on disk carries
/// whichever <see cref="MovementState"/> layout the writing build had and nothing in the header says which (#353,
/// #322). This step therefore infers the generation - it walks the body at every candidate from
/// <see cref="OldestAbsolutePositionWireGeneration"/> to <see cref="NewestAbsolutePositionWireGeneration"/> and needs
/// exactly one result to survive - and rewrites every built-in payload into THIS build's layout, not just the
/// position frame. The private payload table this file used to carry stated a single movement length and was already
/// wrong for six of the seven generations it had to read; there is one shared table now
/// (<see cref="BuiltinBlobLayout"/>), pinned to the codec by test.
/// <para>
/// Inference is not a choice between plausible readings. Every candidate is judged against what the writer is known
/// to do (<see cref="CellBlobWalkPolicy"/>) and the ones that recovered something no build writes are discarded; if
/// more than one survives to a DIFFERENT result the migration throws
/// <see cref="AmbiguousCellBlobGenerationException"/> and the driver quarantines the cell with its bytes intact,
/// rather than a scoring rule picking one. <see cref="CellBlobMigrationOptions.AssumedWireGeneration"/> is how an
/// operator who knows which build wrote the save resolves that instead of losing the cell.
/// </para>
/// </remarks>
public static class PositionFrameBlobMigration
{
    /// <summary>The cell-blob schema version whose position frames are three absolute float32s (the layout before
    /// the framed wire).</summary>
    public const int AbsolutePositionSchemaVersion = 2;

    /// <summary>The cell-blob schema version whose position frames carry an island-frame stamp plus a frame-local
    /// offset (the framed-wire layout).</summary>
    public const int FramedPositionSchemaVersion = 3;

    /// <summary>The oldest wire generation a v<see cref="AbsolutePositionSchemaVersion"/> blob can carry. Generation
    /// 1 shares this layout exactly (10.0.0 widened the entity id, not any payload), so a v1 body brought here by
    /// <see cref="NetIdBlobMigration.WidenV1ToV2(byte[])"/> is covered by this candidate too.</summary>
    public const int OldestAbsolutePositionWireGeneration = 2;

    /// <summary>The newest wire generation a v<see cref="AbsolutePositionSchemaVersion"/> blob can carry: generation
    /// <see cref="BuiltinBlobLayout.FramedPositionWireGeneration"/> is what moved the schema to v3, so v2 stops one
    /// short of it.</summary>
    public const int NewestAbsolutePositionWireGeneration = BuiltinBlobLayout.FramedPositionWireGeneration - 1;

    /// <summary>
    /// The <see cref="CellSnapshotMigration"/> that rewrites a v2 (absolute position) snapshot body to v3 (framed).
    /// Register it with <c>CellPersistenceConfig.RegisterMigration(2, PositionFrameBlobMigration.FrameV2ToV3)</c>, or
    /// rely on the engine default (any <see cref="CellPersistence"/> at schema &gt;= 3 folds it in). The whole body
    /// is brought to this build's built-in layout, not only the position frames, because the stored generation
    /// governs every unframed payload. Throws on a body that walks at no candidate generation, and on one that walks
    /// at several to different results (<see cref="AmbiguousCellBlobGenerationException"/>), so the driver
    /// quarantines it either way rather than guessing.
    /// </summary>
    public static byte[] FrameV2ToV3(byte[] body) => FrameV2ToV3(body, CellBlobMigrationOptions.None);

    /// <summary>
    /// As <see cref="FrameV2ToV3(byte[])"/>, with what the reader knows about the save
    /// (<see cref="CellBlobMigrationOptions"/>): the live registry, which discards candidate generations that
    /// recovered ids nobody registered, and an assumed generation, which replaces the inference outright. A
    /// <see cref="CellPersistence"/> passes its own config through, so this overload is for a consumer registering
    /// the step by hand.
    /// </summary>
    public static byte[] FrameV2ToV3(byte[] body, CellBlobMigrationOptions options)
        => FrameV2ToV3(body, options, new CellBlobMigrationContext());

    // The chain-aware form. It records the generation it produced, so the v3 -> v4 step behind it does not walk the
    // same body all over again at candidates it has already been brought past (#353 fix round: a v2 blob used to pay
    // nine walks and two rewrites, and the second inference was a second independent chance to get it wrong).
    internal static byte[] FrameV2ToV3(byte[] body, CellBlobMigrationOptions options, CellBlobMigrationContext context)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        options.Validate();

        int current = BuiltinBlobLayout.CurrentWireGeneration;
        byte[] result = context.ResolvedGeneration(options) is int known
            ? CellBlobRewriter.Rewrite(body, known, current, widenNetIds: false)
            : CellBlobRewriter.RewriteInferring(body, OldestAbsolutePositionWireGeneration,
                NewestAbsolutePositionWireGeneration, current, widenNetIds: false, "v2 (absolute position)",
                CellBlobWalkPolicy.Inferring(options.KnownExtensionId));
        context.KnownWireGeneration = current;
        return result;
    }
}
