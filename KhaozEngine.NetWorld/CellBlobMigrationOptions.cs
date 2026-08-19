using System;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>
/// What a cell-blob migration is allowed to know about the save it is reading, for the two steps that have to deal
/// with a body whose wire generation nobody recorded (<see cref="PositionFrameBlobMigration.FrameV2ToV3(byte[])"/> and
/// <see cref="WireGenerationBlobMigration.NormalizeV3ToV4(byte[])"/>).
/// <para>
/// Both are optional and both make the inference stricter rather than looser. <see cref="Registry"/> lets the walk
/// reject a candidate generation that recovered a component id nobody registered, which is the single most effective
/// filter there is: an under-read built-in payload leaves bytes behind, and those bytes re-sync into frames whose ids
/// are essentially random, so almost every wrong candidate names an id the live registry has never heard of.
/// <see cref="AssumedWireGeneration"/> removes the guessing entirely for an operator who knows which build wrote the
/// save.
/// </para>
/// <para>
/// A <see cref="CellPersistence"/> passes these through to the engine migrations from
/// <see cref="CellPersistenceConfig.Registry"/> and <see cref="CellPersistenceConfig.AssumedWireGeneration"/>; the
/// options object is only needed when registering an engine migration by hand.
/// </para>
/// </summary>
public sealed class CellBlobMigrationOptions
{
    private Func<ushort, bool>? knownExtensionId;

    /// <summary>Neither a registry nor an assumed generation: the inference validates structure only. What the
    /// single-argument migration overloads use.</summary>
    public static CellBlobMigrationOptions None { get; } = new();

    /// <summary>
    /// The live replication registry (the one this build restores cells with), or null to skip registry-aware
    /// validation. Supplying it is strongly recommended: a candidate generation whose walk recovers an extension id
    /// this registry does not know is rejected, which is usually what turns an ambiguous blob (quarantined) into an
    /// unambiguous one (migrated). It never makes a wrong parse win, only fewer parses survive, and it can never cost
    /// a blob that an unsupplied registry would have migrated.
    /// </summary>
    /// <remarks>
    /// That last guarantee needs one carve-out, because there is a blob this rule is wrong about rather than strict
    /// about: a body carrying a RETAINED unknown extension frame (an id dropped from the registry, see
    /// <see cref="CellPersistenceIssueKind.RetainedUnknownExtensions"/>) has EVERY candidate retired by it, while
    /// being bytes a real build wrote and retain-and-rewrite exists to carry forward. So when this rule is what
    /// emptied the field, <see cref="CellBlobRewriter.RewriteInferring"/> decides again with it dropped and every
    /// other evidence rule kept, which lands on exactly what an unsupplied registry would have produced. Supplying
    /// the registry therefore only ever tightens an ambiguity.
    /// </remarks>
    public ReplicationRegistry? Registry { get; init; }

    /// <summary>
    /// The wire generation the pre-v4 blobs in this save were written at, or null (the default) to infer it from the
    /// body. Set it when the save's provenance is known - the engine version that wrote it maps to a generation, see
    /// the table in <c>docs/USING-KHAOZENGINE.md</c> - and the migrations walk the body at exactly that generation
    /// instead of trying candidates, which is both cheaper and incapable of choosing wrong. A body that does not walk
    /// at the stated generation is quarantined, never re-guessed.
    /// </summary>
    /// <remarks>
    /// It names the generation of ONE vintage, and a long-lived store legitimately holds two: the v2 bodies span
    /// generations <see cref="BuiltinBlobLayout.OldestKnownWireGeneration"/>..<see cref="PositionFrameBlobMigration.NewestAbsolutePositionWireGeneration"/>
    /// and the v3 bodies <see cref="WireGenerationBlobMigration.OldestUnstampedWireGeneration"/>..this build's. A
    /// migration step whose own range does not contain the stated generation therefore IGNORES it and infers, rather
    /// than refusing every body it was never meant to describe. So the knob resolves the vintage it names and costs
    /// the other one nothing.
    /// </remarks>
    public int? AssumedWireGeneration { get; init; }

    /// <summary>The registry's membership test as a predicate, or null when no registry was supplied. Cached: the
    /// walk asks it once per recovered extension frame.</summary>
    internal Func<ushort, bool>? KnownExtensionId =>
        Registry is null ? null : knownExtensionId ??= Registry.IsRegistered;

    /// <summary>Throws when <see cref="AssumedWireGeneration"/> names a generation no layout table row exists for, so
    /// a typo fails at construction rather than quarantining every cell at boot.</summary>
    internal void Validate()
    {
        if (AssumedWireGeneration is not int g) return;
        if (g < BuiltinBlobLayout.OldestKnownWireGeneration || g > BuiltinBlobLayout.CurrentWireGeneration)
            throw new ArgumentOutOfRangeException(nameof(AssumedWireGeneration), g,
                $"An assumed wire generation must be between {BuiltinBlobLayout.OldestKnownWireGeneration} and " +
                $"{BuiltinBlobLayout.CurrentWireGeneration} (this build's).");
    }
}

/// <summary>
/// The one thing a migration chain step needs to tell the next one: which wire generation the body it produced is at,
/// when it knows. Threaded through the chain by <see cref="CellPersistence"/> (server thread, one instance per loaded
/// blob) so the v2 -&gt; v3 step's inference is not thrown away and re-run by the v3 -&gt; v4 step over a body the
/// chain has already normalized. A consumer-registered step clears it, since only that step knows what it produced.
/// </summary>
internal sealed class CellBlobMigrationContext
{
    /// <summary>The generation the body is known to be at, or null when nobody has established one.</summary>
    internal int? KnownWireGeneration { get; set; }

    /// <summary>
    /// The generation the calling step should walk at: what a previous step established, else the operator's
    /// assumption when it falls inside <paramref name="oldest"/>..<paramref name="newest"/> (the step's OWN vintage),
    /// else null for "infer it". The range test is what lets one knob serve both pre-v4 vintages, see
    /// <see cref="CellBlobMigrationOptions.AssumedWireGeneration"/>.
    /// </summary>
    internal int? ResolvedGeneration(CellBlobMigrationOptions options, int oldest, int newest)
    {
        if (KnownWireGeneration is int known) return known;
        return options.AssumedWireGeneration is int assumed && assumed >= oldest && assumed <= newest
            ? assumed
            : null;
    }
}
