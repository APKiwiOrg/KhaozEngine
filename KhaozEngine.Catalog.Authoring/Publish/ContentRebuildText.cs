using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>What the text half of a rebuild produced, or why it refused.</summary>
/// <param name="Refusal">The refusal, or null when the rebuild may continue.</param>
/// <param name="Languages">The manifest language entries, recorded wire spelling and regenerated hash, in ordinal wire-tag order.</param>
/// <param name="Chunks">The regenerated text chunk files, which the rebuild writes before the manifests.</param>
/// <param name="ProvenanceUnknown">True when the store holds no complete text record for the version.</param>
internal sealed record ContentRebuildTextResult(
    ContentRebuildRefusal? Refusal,
    IReadOnlyList<ManifestLanguageEntry> Languages,
    IReadOnlyList<ContentRegeneratedText> Chunks,
    bool ProvenanceUnknown);

/// <summary>
/// The TEXT half of an exact-version rebuild. Through the companion it reads version N's own values and
/// recorded language mappings, regenerates every chunk in its recorded wire spelling and compares each hash
/// with the mapping, all before the rebuild writes anything. A store without the companion keeps the row-only
/// rebuild's behavior: it is refused when its baseline names any language, because it has no way to read
/// version N's text.
/// <para>
/// <b>Unknown legacy provenance is not empty.</b> It counts as empty only on a read-only proof: both
/// regenerated no-language manifests digest to the recorded hashes, or verified stored manifests at the
/// recorded hashes carry empty language lists. Nothing here writes provenance, and nothing writes at all.
/// </para>
/// </summary>
internal static class ContentRebuildText
{
    /// <summary>Reads and regenerates version N's text.</summary>
    /// <param name="store">The authoring store.</param>
    /// <param name="registry">The registry keys are derived through.</param>
    /// <param name="versionNumber">The version being rebuilt.</param>
    /// <param name="rows">The version's rows, which supply each value's content key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<ContentRebuildTextResult> ReadAsync(
        IContentAuthoringStore store,
        ContentTypeRegistry registry,
        int versionNumber,
        ContentRebuildSnapshot rows,
        CancellationToken cancellationToken)
    {
        if (store is not IContentTextAuthoringStore text)
        {
            ContentRebuildRefusal? capability = ContentRebuildVerification.CheckText(versionNumber, rows.Languages);
            return new ContentRebuildTextResult(capability, [], [], false);
        }

        ContentVersionTextSnapshot snapshot;
        try
        {
            snapshot = await text.ReadTextSnapshotAsync(versionNumber, cancellationToken).ConfigureAwait(false);
        }
        catch (ContentAuthoringException unknown)
            when (string.Equals(unknown.Reason, ContentAuthoringException.TextProvenanceUnknownReason, StringComparison.Ordinal))
        {
            return new ContentRebuildTextResult(null, [], [], true);
        }

        var keys = new Dictionary<(ushort, int), ContentKey>(rows.Rows.Count);
        foreach (ContentCandidateRow row in rows.Rows)
        {
            keys[(row.Registration.Type.Value, row.DefinitionId)] = row.Key;
        }

        IReadOnlyList<ContentRegeneratedText> regenerated;
        try
        {
            regenerated = ContentTextChunkBuilder.Regenerate(registry, snapshot, keys);
        }
        catch (ContentAuthoringException invalid)
        {
            return Refused(ContentPackRebuild.RefusedTextValues, FormattableString.Invariant(
                $"The text of version {versionNumber} cannot be regenerated, so nothing was written: {invalid.Message}"));
        }

        if (regenerated.Count != snapshot.Languages.Count)
        {
            return Refused(ContentPackRebuild.RefusedTextMismatch, FormattableString.Invariant(
                $"Version {versionNumber} records {snapshot.Languages.Count} language(s) and {regenerated.Count} were regenerated, so nothing was written."));
        }

        var languages = new List<ManifestLanguageEntry>(regenerated.Count);
        foreach (ContentRegeneratedText chunk in regenerated)
        {
            if (!string.Equals(chunk.Hash, chunk.Recorded.Hash, StringComparison.Ordinal))
            {
                return Refused(ContentPackRebuild.RefusedTextMismatch, FormattableString.Invariant(
                    $"The regenerated '{chunk.Recorded.WireTag}' text chunk of version {versionNumber} digests to {chunk.Hash} and the version records {chunk.Recorded.Hash}. The values the store holds are not the ones the version published, so nothing was written."));
            }

            languages.Add(new ManifestLanguageEntry(chunk.Recorded.WireTag, chunk.Hash));
        }

        languages.Sort(static (left, right) => string.CompareOrdinal(left.Tag, right.Tag));
        return new ContentRebuildTextResult(null, languages, regenerated, false);
    }

    /// <summary>
    /// Whether verified stored manifests prove both of version N's language lists empty: the manifests at the
    /// RECORDED hashes, read from the rebuild target or the store's own pack, decode, digest to those hashes
    /// and name no language. Read only. Each pack is checked through <see cref="ContentTextProvenanceProof"/>,
    /// the one proof the stores' own snapshot reads run.
    /// </summary>
    /// <param name="record">The version record.</param>
    /// <param name="target">The rebuild target.</param>
    /// <param name="source">The store's own pack, or null.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<bool> StoredManifestsProveEmptyAsync(
        ContentVersionRecord record,
        IPackStore target,
        IPackStore? source,
        CancellationToken cancellationToken)
    {
        if (await ContentTextProvenanceProof.StoredManifestsNameNoLanguageAsync(record, target, cancellationToken)
            .ConfigureAwait(false))
        {
            return true;
        }

        return source is not null
            && !ReferenceEquals(source, target)
            && await ContentTextProvenanceProof.StoredManifestsNameNoLanguageAsync(record, source, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>The provenance refusal.</summary>
    /// <param name="versionNumber">The version.</param>
    public static ContentRebuildRefusal Unknown(int versionNumber)
        => new(
            ContentPackRebuild.RefusedTextProvenance,
            FormattableString.Invariant(
                $"Version {versionNumber} has no complete text record, its no-language manifests do not digest to the recorded hashes and no verified stored manifest proves its language lists empty. Its text is unknown rather than empty, so nothing was written."));

    static ContentRebuildTextResult Refused(string reason, string detail)
        => new(new ContentRebuildRefusal(reason, detail), [], [], false);
}
