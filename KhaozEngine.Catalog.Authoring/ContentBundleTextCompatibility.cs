using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The bundle format rules every store shares: which text a bundle carries, which targets it may name, and
/// which format an exact-version export writes.
/// <para>
/// <b>Format 1 is text free by contract</b> and converts EXPLICITLY to empty text, which keeps every old
/// fixture and reader. <b>Format 2 carries its text section</b>, and a format 2 bundle that lost it is refused
/// rather than read as text free. A later format is refused whole. Every refusal here lands before an import
/// resets or stages anything.
/// </para>
/// <para>
/// <b>Export writes format 1 exactly when the requested version declares no language</b>, so a text-free
/// catalog's export stays byte-identical and existing consumer bundles and upgrade baselines do not move.
/// A version declaring any language writes format 2, even with no value. The snapshot handed in is complete
/// or proved by its store, which refuses an unknown one before this is reached.
/// </para>
/// </summary>
internal static class ContentBundleTextCompatibility
{
    /// <summary>The text section a format 1 bundle converts to: no language and no value.</summary>
    public static ContentBundleTextState Empty { get; } = new([], []);

    /// <summary>
    /// The complete text a bundle carries: empty for format 1, its section for format 2, and a refusal for a
    /// format 2 bundle that lost its section or a format this build does not read.
    /// </summary>
    /// <param name="bundle">The bundle handed to an import.</param>
    /// <param name="member">The member reading it, which a refusal names.</param>
    /// <exception cref="ContentAuthoringException">The section is lost, or the format is not 1 or 2, each with <see cref="ContentAuthoringException.BundleFormatReason"/>.</exception>
    public static ContentBundleTextState TextOf(ContentBundle bundle, string member)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.FormatVersion == ContentBundle.CurrentFormatVersion)
        {
            return bundle.TextState ?? Empty;
        }

        if (bundle.FormatVersion == ContentBundle.TextFormatVersion)
        {
            return bundle.TextState ?? throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"{member} is refused because the bundle is format {ContentBundle.TextFormatVersion} and no longer carries its text section. A rebuilt bundle that dropped its text is never read as text free, so nothing was staged."),
                default,
                0,
                ContentAuthoringException.BundleFormatReason);
        }

        throw UnsupportedFormat(bundle.FormatVersion);
    }

    /// <summary>
    /// Refuses a companion import while an open draft holds any work, rows, text intents or language
    /// introductions, because the import's own draft would merge with it and publish or drop work the operator
    /// never reviewed as part of the seed. It runs before an import resets or stages anything.
    /// </summary>
    /// <param name="draft">The store's open draft, or null.</param>
    /// <param name="member">The member importing, which a refusal names.</param>
    /// <exception cref="ContentAuthoringException">The draft holds work, with <see cref="ContentAuthoringException.DraftOpenReason"/>.</exception>
    public static void RequireNoPendingWork(ContentDraft? draft, string member)
    {
        if (draft is not null && draft.TotalWorkCount > 0)
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"{member} is refused because the open draft holds {draft.EditCount} row edit(s), {draft.TextEditCount} text edit(s) and {draft.LanguageIntroductionCount} language introduction(s). Publish or discard that work first. Nothing was staged."),
                default,
                0,
                ContentAuthoringException.DraftOpenReason);
        }
    }

    /// <summary>
    /// Refuses a value whose target names no row the bundle carries, or a type and field no text may target.
    /// It runs before an import stages anything.
    /// </summary>
    /// <param name="bundle">The bundle.</param>
    /// <param name="text">Its complete text.</param>
    /// <param name="registry">The importing store's registry.</param>
    /// <exception cref="ContentAuthoringException">A value names an unknown row or an ineligible target.</exception>
    public static void RequireTargets(ContentBundle bundle, ContentBundleTextState text, ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(registry);

        var rows = new HashSet<(ushort Type, ContentKey Key)>();
        for (int i = 0; i < bundle.Rows.Count; i++)
        {
            rows.Add((bundle.Rows[i].Type.Value, bundle.Rows[i].Key));
        }

        for (int i = 0; i < text.Values.Count; i++)
        {
            ContentTextTarget target = text.Values[i].Target;
            if (!rows.Contains((target.Type.Value, target.Key)))
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"Bundle text value {i} names type {target.Type.Value} row '{target.Key}', which the bundle carries no row of, so the import is refused before anything is staged."),
                    target.Type,
                    0,
                    ContentAuthoringException.UnknownRowReason);
            }

            _ = ContentTextTargetEligibility.Require(registry, target);
        }
    }

    /// <summary>
    /// The bundle an export writes for one exact version: format 1 when the version declares no language, and
    /// format 2 with every declared language, empty ones included, and every value otherwise.
    /// </summary>
    /// <param name="storeEpoch">The store's identity.</param>
    /// <param name="versionNumber">The exported version.</param>
    /// <param name="types">The registered types.</param>
    /// <param name="rows">Every row live at the version, each naming its id.</param>
    /// <param name="families">Every family.</param>
    /// <param name="rules">The rules introduced at or before the version.</param>
    /// <param name="text">The version's complete text.</param>
    /// <exception cref="ContentAuthoringException">A value names a row the version does not hold live.</exception>
    public static ContentBundle Export(
        string storeEpoch,
        int versionNumber,
        IReadOnlyList<ContentBundleType> types,
        IReadOnlyList<ContentBundleRow> rows,
        IReadOnlyList<ContentFamily> families,
        IReadOnlyList<RemapRule> rules,
        ContentVersionTextSnapshot text)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(text);
        if (text.Languages.Count == 0)
        {
            // A snapshot's values all name a recorded language, so no language means no value either.
            return new ContentBundle(
                ContentBundle.CurrentFormatVersion, storeEpoch, versionNumber, types, rows, families, rules);
        }

        var keys = new Dictionary<(ushort Type, int Id), ContentKey>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Id is int id)
            {
                keys[(rows[i].Type.Value, id)] = rows[i].Key;
            }
        }

        var revisions = new List<ContentTextRevision>(text.Revisions);
        revisions.Sort(Order);
        var values = new ContentBundleTextValue[revisions.Count];
        for (int i = 0; i < values.Length; i++)
        {
            ContentTextRevision revision = revisions[i];
            if (!keys.TryGetValue((revision.Type.Value, revision.DefinitionId), out ContentKey key))
            {
                throw ContentTextCompatibility.Unrepresented(
                    nameof(IContentAuthoringStore.ExportBundleAsync),
                    FormattableString.Invariant(
                        $"version {versionNumber} holds text for type {revision.Type.Value} row {revision.DefinitionId}, which it holds no live row of"));
            }

            values[i] = new ContentBundleTextValue(
                new ContentTextTarget(revision.Type, key, revision.FieldName, revision.Language), revision.Value);
        }

        var declarations = new ContentTextLanguageDeclaration[text.Languages.Count];
        for (int i = 0; i < declarations.Length; i++)
        {
            declarations[i] = text.Languages[i].Declaration;
        }

        return new ContentBundle(
            ContentBundle.TextFormatVersion,
            storeEpoch,
            versionNumber,
            types,
            rows,
            families,
            rules,
            new ContentBundleTextState(declarations, values));
    }

    /// <summary>The refusal of a format this build does not read.</summary>
    /// <param name="formatVersion">The declared format.</param>
    public static ContentAuthoringException UnsupportedFormat(int formatVersion)
        => new(
            FormattableString.Invariant(
                $"The bundle declares format version {formatVersion} and this build reads {ContentBundle.CurrentFormatVersion} and {ContentBundle.TextFormatVersion}. A format version is a refusal of the whole document rather than a best-effort partial read."),
            default,
            0,
            ContentAuthoringException.BundleFormatReason);

    /// <summary>Type, id, field and language, ordinally, so two exports of one version write the same bytes.</summary>
    static int Order(ContentTextRevision left, ContentTextRevision right)
    {
        int order = left.Type.Value.CompareTo(right.Type.Value);
        if (order == 0)
        {
            order = left.DefinitionId.CompareTo(right.DefinitionId);
        }

        if (order == 0)
        {
            order = string.CompareOrdinal(left.FieldName, right.FieldName);
        }

        return order != 0 ? order : string.CompareOrdinal(left.Language, right.Language);
    }
}
