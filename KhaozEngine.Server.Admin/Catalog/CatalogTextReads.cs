using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// How the read actions render COMPLETE text state in one place: a version's text read through the companion,
/// a draft's pending text intents and introductions, one row's strings, and the text half of a diff.
/// <para>
/// <b>Unknown is never rendered as empty.</b> A store without the text companion, or a version whose text the
/// store cannot prove, yields no snapshot, and each payload says so through its known flag rather than
/// listing nothing as though nothing were there.
/// </para>
/// </summary>
internal static class CatalogTextReads
{
    /// <summary>
    /// The complete text of one version, an empty baseline at 0, or null when the store has no companion or
    /// cannot prove that version's text.
    /// </summary>
    /// <param name="store">The authoring store.</param>
    /// <param name="versionNumber">The version, 0 for the empty baseline.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task<ContentVersionTextSnapshot?> SnapshotAsync(
        IContentAuthoringStore store,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        if (store is not IContentTextAuthoringStore text)
        {
            return null;
        }

        if (versionNumber == 0)
        {
            string epoch = await store.GetStoreEpochAsync(cancellationToken).ConfigureAwait(false);
            return ContentVersionTextSnapshot.EmptyBaseline(epoch);
        }

        try
        {
            return await text.ReadTextSnapshotAsync(versionNumber, cancellationToken).ConfigureAwait(false);
        }
        // Defensive: no in-tree store throws the unavailable reason, so it guards a third-party store breaking the contract.
        catch (ContentAuthoringException failure) when (failure.Reason
            is ContentAuthoringException.TextProvenanceUnknownReason
            or ContentAuthoringException.TextOperationUnavailableReason)
        {
            return null;
        }
    }

    /// <summary>A draft's pending text intents, expanded, in the order they were first applied.</summary>
    /// <param name="draft">The open draft.</param>
    /// <param name="registry">The registry type keys are named through.</param>
    public static IReadOnlyList<CatalogDraftTextEditPayload> Edits(ContentDraft draft, ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(draft);
        IReadOnlyList<ContentTextEdit> edits = draft.TextState?.Edits ?? [];
        var rendered = new List<CatalogDraftTextEditPayload>(edits.Count);
        for (int i = 0; i < edits.Count; i++)
        {
            ContentTextEdit edit = edits[i];
            ContentTextTarget target = edit.Target;
            string typeKey = TypeKey(registry, target.Type);
            rendered.Add(new CatalogDraftTextEditPayload(
                edit.Operation == ContentTextEditOperation.Set ? "set" : "remove",
                typeKey,
                target.Key.ToString(),
                target.FieldName,
                target.Language,
                edit.Value,
                ContentTextKey.Derive(typeKey, target.Key.Utf8, target.FieldName)));
        }

        return rendered;
    }

    /// <summary>The languages a draft introduces, in order.</summary>
    /// <param name="draft">The open draft.</param>
    public static IReadOnlyList<CatalogTextLanguagePayload> Introductions(ContentDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        IReadOnlyList<ContentTextLanguageDeclaration> introductions = draft.TextState?.Introductions ?? [];
        var rendered = new List<CatalogTextLanguagePayload>(introductions.Count);
        for (int i = 0; i < introductions.Count; i++)
        {
            rendered.Add(new CatalogTextLanguagePayload(introductions[i].Language, introductions[i].WireTag));
        }

        return rendered;
    }

    /// <summary>Every string one row holds in a snapshot, ordered by field then language.</summary>
    /// <param name="snapshot">The version's complete text.</param>
    /// <param name="type">The row's type.</param>
    /// <param name="id">The row's definition id.</param>
    /// <param name="key">The row's content key.</param>
    public static IReadOnlyList<CatalogTextValuePayload> RowValues(
        ContentVersionTextSnapshot snapshot,
        ContentTypeRegistration type,
        int id,
        ContentKey key)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(type);
        var values = new List<CatalogTextValuePayload>();
        foreach (ContentTextRevision revision in snapshot.Revisions)
        {
            if (revision.Type == type.Type && revision.DefinitionId == id)
            {
                values.Add(new CatalogTextValuePayload(
                    revision.FieldName,
                    revision.Language,
                    revision.Value,
                    ContentTextKey.Derive(type.TypeKey, key.Utf8, revision.FieldName),
                    revision.ValidFromVersion));
            }
        }

        values.Sort(static (left, right) =>
        {
            int field = string.CompareOrdinal(left.Field, right.Field);
            return field != 0 ? field : string.CompareOrdinal(left.Language, right.Language);
        });
        return values;
    }

    /// <summary>The text half of a diff between two committed versions or baselines.</summary>
    /// <param name="from">The source's complete text.</param>
    /// <param name="fromRows">The source's live rows, which name each id's key.</param>
    /// <param name="to">The destination's complete text.</param>
    /// <param name="toRows">The destination's live rows.</param>
    /// <param name="registry">The registry type keys are named through.</param>
    public static (IReadOnlyList<CatalogTextChangePayload> Changes, IReadOnlyList<CatalogTextLanguagePayload> Introduced)
        Between(
            ContentVersionTextSnapshot from,
            IReadOnlyList<ContentRowRevision> fromRows,
            ContentVersionTextSnapshot to,
            IReadOnlyList<ContentRowRevision> toRows,
            ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return (
            Compare(Values(from, fromRows), Values(to, toRows), registry),
            Introduced(from, Declarations(to.Languages)));
    }

    /// <summary>
    /// The text half of a diff against the draft-applied candidate: the active version's text with every
    /// pending intent applied, compared with the source's, and every language the candidate declares that
    /// the source does not. A pending add's string carries the row's provisional id.
    /// </summary>
    /// <param name="from">The source's complete text.</param>
    /// <param name="fromRows">The source's live rows.</param>
    /// <param name="active">The active version's complete text, which the draft stands on.</param>
    /// <param name="candidateRows">The draft-applied candidate's rows, provisional ids included.</param>
    /// <param name="draft">The open draft, or null.</param>
    /// <param name="registry">The registry type keys are named through.</param>
    public static (IReadOnlyList<CatalogTextChangePayload> Changes, IReadOnlyList<CatalogTextLanguagePayload> Introduced)
        Pending(
            ContentVersionTextSnapshot from,
            IReadOnlyList<ContentRowRevision> fromRows,
            ContentVersionTextSnapshot active,
            IReadOnlyList<ContentRowRevision> candidateRows,
            ContentDraft? draft,
            ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(candidateRows);

        var ids = new Dictionary<(ushort, ContentKey), int>(candidateRows.Count);
        for (int i = 0; i < candidateRows.Count; i++)
        {
            ids[(candidateRows[i].Row.Type.Value, candidateRows[i].Row.Key)] = candidateRows[i].Row.Id;
        }

        Dictionary<TextIdentity, (int Id, string Value)> after = Values(active, candidateRows);
        IReadOnlyList<ContentTextEdit> edits = draft?.TextState?.Edits ?? [];
        for (int i = 0; i < edits.Count; i++)
        {
            ContentTextTarget target = edits[i].Target;
            var at = new TextIdentity(target.Type.Value, target.Key, target.FieldName, target.Language);
            if (edits[i].Value is string value)
            {
                after[at] = (ids.TryGetValue((target.Type.Value, target.Key), out int id) ? id : 0, value);
            }
            else
            {
                after.Remove(at);
            }
        }

        List<CatalogTextLanguagePayload> declared = Declarations(active.Languages);
        IReadOnlyList<ContentTextLanguageDeclaration> introductions = draft?.TextState?.Introductions ?? [];
        for (int i = 0; i < introductions.Count; i++)
        {
            declared.Add(new CatalogTextLanguagePayload(introductions[i].Language, introductions[i].WireTag));
        }

        return (Compare(Values(from, fromRows), after, registry), Introduced(from, declared));
    }

    /// <summary>Every string whose value differs between two sides, absent on either side included, in order.</summary>
    static List<CatalogTextChangePayload> Compare(
        Dictionary<TextIdentity, (int Id, string Value)> before,
        Dictionary<TextIdentity, (int Id, string Value)> after,
        ContentTypeRegistry registry)
    {
        var changes = new List<CatalogTextChangePayload>();
        foreach (KeyValuePair<TextIdentity, (int Id, string Value)> pair in after)
        {
            string? old = before.TryGetValue(pair.Key, out (int Id, string Value) held) ? held.Value : null;
            if (!string.Equals(old, pair.Value.Value, StringComparison.Ordinal))
            {
                changes.Add(Change(registry, pair.Key, pair.Value.Id, old, pair.Value.Value));
            }
        }

        foreach (KeyValuePair<TextIdentity, (int Id, string Value)> pair in before)
        {
            if (!after.ContainsKey(pair.Key))
            {
                changes.Add(Change(registry, pair.Key, pair.Value.Id, pair.Value.Value, null));
            }
        }

        changes.Sort(Order);
        return changes;
    }

    /// <summary>The destination's languages the source does not declare, in the destination's order.</summary>
    static List<CatalogTextLanguagePayload> Introduced(
        ContentVersionTextSnapshot from,
        List<CatalogTextLanguagePayload> destination)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContentTextLanguage language in from.Languages)
        {
            declared.Add(language.Language);
        }

        return destination.FindAll(language => !declared.Contains(language.Language));
    }

    static List<CatalogTextLanguagePayload> Declarations(IReadOnlyList<ContentTextLanguage> languages)
    {
        var declarations = new List<CatalogTextLanguagePayload>(languages.Count);
        for (int i = 0; i < languages.Count; i++)
        {
            declarations.Add(new CatalogTextLanguagePayload(languages[i].Language, languages[i].WireTag));
        }

        return declarations;
    }

    /// <summary>
    /// Every value of a snapshot keyed by its string identity, with the row id it was stored under. A value no
    /// live row of the snapshot names is skipped, because it has no key to render.
    /// </summary>
    static Dictionary<TextIdentity, (int Id, string Value)> Values(
        ContentVersionTextSnapshot snapshot,
        IReadOnlyList<ContentRowRevision> rows)
    {
        var keys = new Dictionary<(ushort, int), ContentKey>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            keys[(rows[i].Row.Type.Value, rows[i].Row.Id)] = rows[i].Row.Key;
        }

        var values = new Dictionary<TextIdentity, (int Id, string Value)>();
        foreach (ContentTextRevision revision in snapshot.Revisions)
        {
            if (keys.TryGetValue((revision.Type.Value, revision.DefinitionId), out ContentKey key))
            {
                values[new TextIdentity(revision.Type.Value, key, revision.FieldName, revision.Language)] =
                    (revision.DefinitionId, revision.Value);
            }
        }

        return values;
    }

    static CatalogTextChangePayload Change(
        ContentTypeRegistry registry, TextIdentity at, int id, string? before, string? after)
    {
        string typeKey = TypeKey(registry, new ContentTypeId(at.Type));
        return new CatalogTextChangePayload(
            typeKey,
            id,
            at.Key.ToString(),
            at.Field,
            at.Language,
            before,
            after,
            ContentTextKey.Derive(typeKey, at.Key.Utf8, at.Field));
    }

    static int Order(CatalogTextChangePayload left, CatalogTextChangePayload right)
    {
        int order = string.CompareOrdinal(left.Type, right.Type);
        order = order != 0 ? order : string.CompareOrdinal(left.Key, right.Key);
        order = order != 0 ? order : string.CompareOrdinal(left.Field, right.Field);
        return order != 0 ? order : string.CompareOrdinal(left.Language, right.Language);
    }

    static string TypeKey(ContentTypeRegistry registry, ContentTypeId type)
        => registry.TryGet(type, out ContentTypeRegistration? registration) ? registration.TypeKey : string.Empty;

    /// <summary>One string's identity in a diff: type, immutable row key, field and canonical language.</summary>
    readonly record struct TextIdentity(ushort Type, ContentKey Key, string Field, string Language);
}
