using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The chunk half of a text commit's backend confirmation, shared by every store: each output language's
/// chunk hash is REGENERATED from the plan's own output values and live rows through the producer's encoder,
/// an encoded chunk's stored bytes must be the regenerated stored file, and a reused chunk must also be the
/// hash the base version recorded for that language. The plan's constructor proves its values are the right
/// ones, and this proves its chunks are those values, so a commit can never record a manifest naming a chunk
/// the committed text does not produce.
/// <para>
/// It guards what a commit records, not what the pack holds. The chunk files are put before the commit and a
/// refused commit leaves them behind as orphans, so readers re-verify every chunk's digest rather than trust
/// the pack. The writer that puts them, <see cref="ContentTextPackWriter"/>, is internal, so no caller outside
/// the catalog assemblies can put bytes under a chunk hash through it.
/// </para>
/// <para>
/// It runs inside the store's gate or transaction, after the store confirmed the plan's baseline text is
/// its own, and before the commit writes any row.
/// </para>
/// </summary>
internal static class ContentTextChunkConfirmation
{
    /// <summary>Refuses a plan whose chunks are not exactly its values.</summary>
    /// <param name="registry">The store's registry, which keys are derived through.</param>
    /// <param name="plan">The plan being committed.</param>
    /// <exception cref="ContentAuthoringException">A chunk hash or an encoded chunk's stored bytes disagree with the regenerated ones, a reused hash is not the base's recorded one, or a value cannot be regenerated.</exception>
    public static void Require(ContentTypeRegistry registry, ContentTextPublishPlan plan)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(plan);

        var keys = new Dictionary<(ushort, int), ContentKey>(plan.RowPlan.LiveRows.Count);
        foreach (ContentRowRevision live in plan.RowPlan.LiveRows)
        {
            keys[(live.Row.Type.Value, live.Row.Id)] = live.Row.Key;
        }

        var output = new ContentVersionTextSnapshot(
            plan.StoreEpoch, plan.VersionNumber, plan.Candidate.Values, plan.Languages);
        IReadOnlyList<ContentRegeneratedText> regenerated = ContentTextChunkBuilder.Regenerate(registry, output, keys);

        var recorded = new Dictionary<string, ContentTextLanguage>(StringComparer.Ordinal);
        foreach (ContentTextLanguage language in plan.Snapshot.BaselineText.Languages)
        {
            recorded[language.Language] = language;
        }

        for (int i = 0; i < regenerated.Count; i++)
        {
            ContentRegeneratedText chunk = regenerated[i];
            if (!string.Equals(chunk.Hash, chunk.Recorded.Hash, StringComparison.Ordinal))
            {
                throw Mismatch(FormattableString.Invariant(
                    $"the '{chunk.Recorded.WireTag}' chunk is named {chunk.Recorded.Hash} and the plan's values regenerate to {chunk.Hash}"));
            }

            // An encoded chunk's bytes are what the pack writer puts under its hash, so they must be the
            // regenerated stored file itself. The encoder is deterministic, one fixed Brotli quality and window.
            if (!plan.Chunks[i].IsReused && !plan.Chunks[i].StoredFile.Span.SequenceEqual(chunk.StoredFile))
            {
                throw Mismatch(FormattableString.Invariant(
                    $"the '{chunk.Recorded.WireTag}' chunk is named {chunk.Hash} and its stored bytes are not the file the plan's values encode to"));
            }

            if (plan.Chunks[i].IsReused
                && (!recorded.TryGetValue(chunk.Recorded.Language, out ContentTextLanguage? held)
                    || !string.Equals(held.WireTag, chunk.Recorded.WireTag, StringComparison.Ordinal)
                    || !string.Equals(held.Hash, chunk.Hash, StringComparison.Ordinal)))
            {
                throw Mismatch(FormattableString.Invariant(
                    $"the '{chunk.Recorded.WireTag}' chunk is reused and version {plan.BaseVersion} recorded no such chunk for it"));
            }
        }
    }

    static ContentAuthoringException Mismatch(string detail)
        => new(
            FormattableString.Invariant(
                $"The text commit is refused because {detail}. Nothing was written, and the plan is rebuilt from a fresh freeze."),
            default,
            0,
            ContentAuthoringException.TextChunkMismatchReason);
}
