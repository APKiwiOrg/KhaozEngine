using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The ONE read-only proof that a committed version whose text provenance is unknown named no language, so
/// its text is empty rather than unknown. A legacy version committed before text authoring existed carries no
/// completeness record, and a store reads it as empty only when this proof passes.
/// <para>
/// <b>Two proofs, either one enough.</b> The primary proof reads the version's own manifests at the RECORDED
/// hashes from a pack store, verifies each digests to its hash, and requires both language lists empty. It is
/// dependable for publishing because the sweep always keeps the active version's manifests. The secondary
/// proof rebuilds both no-language manifests from the version's recorded chunk rows, its rules and its
/// minimum builds through the publisher's own builder, and requires both to digest to the recorded hashes. It
/// stops proving anything once the registry gains or changes a type, which then refuses rather than guesses.
/// </para>
/// <para>
/// <b>Nothing here writes.</b> A proof is never recorded as provenance, so the next read proves again.
/// </para>
/// </summary>
internal static class ContentTextProvenanceProof
{
    /// <summary>Whether either proof shows the version named no language.</summary>
    /// <param name="record">The version record, whose two manifest hashes are the recorded ones.</param>
    /// <param name="pack">The pack store the version's manifests are read from, or null when there is none.</param>
    /// <param name="registry">The registry the no-language manifests are rebuilt through.</param>
    /// <param name="chunks">Every chunk row the version recorded, at every side.</param>
    /// <param name="rules">The rule list as it stood at the version, in sequence order.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<bool> ProveEmptyAsync(
        ContentVersionRecord record,
        IPackStore? pack,
        ContentTypeRegistry registry,
        IReadOnlyList<ContentChunkRecord> chunks,
        IReadOnlyList<RemapRule> rules,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(rules);

        if (pack is not null
            && await StoredManifestsNameNoLanguageAsync(record, pack, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return RecordedChunksNameNoLanguage(record, registry, chunks, rules);
    }

    /// <summary>
    /// The primary proof: the manifests at the recorded hashes decode, digest to those hashes and name no
    /// language. A missing or unreadable manifest is no proof.
    /// </summary>
    /// <param name="record">The version record.</param>
    /// <param name="pack">The pack store to read.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<bool> StoredManifestsNameNoLanguageAsync(
        ContentVersionRecord record,
        IPackStore pack,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(pack);

        ContentManifestRead server = await ContentPackReader.ReadManifestAsync(
            pack, record.ServerManifestHash, ContentManifestSide.Server, null, cancellationToken).ConfigureAwait(false);
        if (!server.Success || server.Manifest is null || server.Manifest.Languages.Count != 0)
        {
            return false;
        }

        ContentManifestRead client = await ContentPackReader.ReadManifestAsync(
            pack, record.ClientManifestHash, ContentManifestSide.Client, null, cancellationToken).ConfigureAwait(false);
        return client.Success && client.Manifest is not null && client.Manifest.Languages.Count == 0;
    }

    /// <summary>
    /// The secondary proof: both no-language manifests rebuilt from the recorded chunk rows, rules and minimum
    /// builds digest to the recorded hashes. A chunk of a type the registry no longer declares is no proof.
    /// </summary>
    /// <param name="record">The version record.</param>
    /// <param name="registry">The registry the manifests are named through.</param>
    /// <param name="chunks">Every chunk row the version recorded.</param>
    /// <param name="rules">The rule list as it stood at the version.</param>
    public static bool RecordedChunksNameNoLanguage(
        ContentVersionRecord record,
        ContentTypeRegistry registry,
        IReadOnlyList<ContentChunkRecord> chunks,
        IReadOnlyList<RemapRule> rules)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(rules);

        string ruleHash = ContentRuleChunkCodec.Hash(rules);
        ContentManifest server;
        ContentManifest client;
        try
        {
            server = ContentManifestBuilder.Build(
                ContentManifestSide.Server, registry, chunks, [], record.VersionNumber,
                record.MinimumServerBuild, record.MinimumClientBuild, ruleHash);
            client = ContentManifestBuilder.Build(
                ContentManifestSide.Client, registry, chunks, [], record.VersionNumber,
                record.MinimumServerBuild, record.MinimumClientBuild, ruleHash);
        }
        catch (ContentAuthoringException)
        {
            return false;
        }

        return string.Equals(ContentManifestText.Hash(server), record.ServerManifestHash, StringComparison.Ordinal)
            && string.Equals(ContentManifestText.Hash(client), record.ClientManifestHash, StringComparison.Ordinal);
    }

    /// <summary>The refusal a store throws for an unknown version no proof shows empty.</summary>
    /// <param name="versionNumber">The version.</param>
    public static ContentAuthoringException Unknown(int versionNumber)
        => new(
            FormattableString.Invariant(
                $"Version {versionNumber} has no complete text record, and neither its stored manifests nor manifests rebuilt from its recorded chunks prove it named no language. Its text is unknown rather than empty, so nothing was written."),
            default,
            0,
            ContentAuthoringException.TextProvenanceUnknownReason);
}
