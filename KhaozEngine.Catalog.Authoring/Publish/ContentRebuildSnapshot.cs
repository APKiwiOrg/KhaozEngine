using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The READ half of a pack rebuild: one published version's rows, its rule list and its languages, taken off
/// the authoring store through members that already exist on the seam.
/// <para>
/// <b>Nothing here reads the PACK.</b> <c>LoadSnapshotAsync</c> decodes a version out of the chunk files,
/// which is precisely what a rebuild does not have, so the rows come from <c>ListRowsAsync</c> at the version
/// number instead. That is also why no member was added to <see cref="IContentAuthoringStore"/> for this: the
/// seam already answers every question a rebuild asks.
/// </para>
/// <para>
/// <b>Retired rows are IN, which is not an option.</b> A retired definition keeps its row in the pack forever
/// (contracts 8.6) and the chunk it sits in carries it with the retired bit set, so a read that dropped
/// retired rows would rebuild a chunk that hashes to something no version record names.
/// </para>
/// <para>
/// <b>The rules are the full list filtered to <c>IntroducedIn &lt;= N</c></b>, the same filter a bundle export
/// uses. The rule list is append only and sequences are contiguous from 1, so that filter is a prefix and the
/// rule chunk it hashes to is the one the version published.
/// </para>
/// </summary>
sealed class ContentRebuildSnapshot
{
    /// <summary>
    /// The page a read asks for. The seam caps a page at 500 rows, so asking for more would silently take
    /// whatever the provider felt like giving and walk off the end of a big type.
    /// </summary>
    public const int PageRows = 500;

    ContentRebuildSnapshot(
        IReadOnlyList<ContentCandidateRow> rows,
        IReadOnlyList<RemapRule> rules,
        IReadOnlyList<ManifestLanguageEntry> languages)
    {
        Rows = rows;
        Rules = rules;
        Languages = languages;
    }

    /// <summary>Every row live at the version, retired ones included, as the chunk builder takes them.</summary>
    public IReadOnlyList<ContentCandidateRow> Rows { get; }

    /// <summary>The rule list as it stood at the version, in sequence order.</summary>
    public IReadOnlyList<RemapRule> Rules { get; }

    /// <summary>
    /// The languages the manifests would name, taken from the ACTIVE version's baseline rather than from the
    /// version being read, because no store member returns version N's. Every published language list is
    /// empty today, so the two are the same list.
    /// <para>
    /// A NON-empty list here refuses the whole rebuild, with
    /// <see cref="ContentPackRebuild.RefusedTextChunks"/>. A named language is a text chunk hash in both
    /// manifests, the rebuild writes no text chunk, and the digest comparison cannot see the difference, so
    /// the list being empty is a precondition of the rebuild rather than a detail of it.
    /// </para>
    /// </summary>
    public IReadOnlyList<ManifestLanguageEntry> Languages { get; }

    /// <summary>Reads one published version, walking every registered type's rows a page at a time.</summary>
    /// <param name="store">The authoring store, through read members only. The baseline read clears a stale freeze marker, which is the one side effect a rebuild can have.</param>
    /// <param name="registry">The registry whose types are walked and whose id ranges the rows fall into.</param>
    /// <param name="versionNumber">The published version to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static async Task<ContentRebuildSnapshot> ReadAsync(
        IContentAuthoringStore store,
        ContentTypeRegistry registry,
        int versionNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);

        var rows = new List<ContentCandidateRow>();
        IReadOnlyList<ContentTypeRegistration> registered = registry.ByTypeId;
        for (int i = 0; i < registered.Count; i++)
        {
            await ReadTypeAsync(store, registered[i], versionNumber, rows, cancellationToken).ConfigureAwait(false);
        }

        ContentPublishBaseline baseline = await store
            .ReadPublishBaselineAsync(cancellationToken).ConfigureAwait(false);
        var rules = new List<RemapRule>(baseline.Rules.Count);
        for (int i = 0; i < baseline.Rules.Count; i++)
        {
            RemapRule rule = baseline.Rules[i];
            if (rule.IntroducedIn <= versionNumber)
            {
                rules.Add(rule);
            }
        }

        return new ContentRebuildSnapshot(rows, rules, baseline.Languages);
    }

    static async Task ReadTypeAsync(
        IContentAuthoringStore store,
        ContentTypeRegistration registration,
        int versionNumber,
        List<ContentCandidateRow> rows,
        CancellationToken cancellationToken)
    {
        int skip = 0;
        while (true)
        {
            ContentRowPage page = await store.ListRowsAsync(
                registration.Type, versionNumber, null, true, skip, PageRows, cancellationToken)
                .ConfigureAwait(false);
            if (page.Rows.Count == 0)
            {
                return;
            }

            for (int i = 0; i < page.Rows.Count; i++)
            {
                rows.Add(Project(registration, page.Rows[i]));
            }

            skip += page.Rows.Count;
            if (skip >= page.Total)
            {
                return;
            }
        }
    }

    /// <summary>
    /// One stored row as the chunk builder's candidate. Only the six things a chunk is encoded from are
    /// carried across, because the temporal columns belong to a publish and a rebuild is not one: it never
    /// decides what enters, what closes or what an id should be.
    /// </summary>
    static ContentCandidateRow Project(ContentTypeRegistration registration, ContentRow row)
    {
        var fields = new ContentFieldValue[row.Fields.Count];
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = row.Fields[i];
        }

        return new ContentCandidateRow
        {
            Registration = registration,
            Key = row.Key,
            Fields = fields,
            DefinitionId = row.Id,
            ParentId = row.ParentId,
            IsRetired = row.IsRetired,
        };
    }
}
