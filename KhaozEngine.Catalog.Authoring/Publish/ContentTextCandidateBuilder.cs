using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// Builds the merged text candidate of a publish from the frozen snapshot and the phase-one row plan, whose
/// ids are final. It starts from the exact baseline, applies fork copies and then the frozen intents through
/// <see cref="ContentTextExpectedValues"/>, RECHECKS every resulting value against the registry and the final
/// rows, and derives the temporal closes and inserts the commit applies.
/// <para>
/// <b>Every value is rechecked, inherited ones included.</b> Row-only edits never recheck held text, so a
/// value whose type or marker is no longer registered and CLIENT visible, or whose row the final plan does not
/// hold, is a typed refusal here. It must not disappear from the version silently, and it must not reach a
/// shared manifest that a client downloads.
/// </para>
/// <para>
/// The declared languages are the baseline's in their historical wire spelling plus the draft's
/// introductions in canonical spelling, ordered ordinally by actual wire tag, which is the manifest order.
/// </para>
/// </summary>
internal static class ContentTextCandidateBuilder
{
    /// <summary>Builds the candidate.</summary>
    /// <param name="registry">The registry eligibility is checked against.</param>
    /// <param name="snapshot">The frozen snapshot.</param>
    /// <param name="rows">The validated phase-one row plan, ids final.</param>
    /// <exception cref="ContentAuthoringException">A value is ineligible, names no live row, or is in an undeclared language.</exception>
    public static ContentTextCandidate Build(
        ContentTypeRegistry registry,
        ContentTextPublishSnapshot snapshot,
        ContentPublishRowPhase rows)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(rows);

        IReadOnlyList<ContentTextRevision> baseline = snapshot.BaselineText.Revisions;
        Dictionary<ContentTextSlot, string> expected = ContentTextExpectedValues.Compute(
            baseline, rows.FrozenEdits, snapshot.TextState, rows.LiveRows);
        ContentTextLanguageDeclaration[] declarations = Declarations(snapshot);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContentTextLanguageDeclaration declaration in declarations)
        {
            declared.Add(declaration.Language);
        }

        var live = new HashSet<(ushort, int)>();
        foreach (ContentRowRevision row in rows.LiveRows)
        {
            live.Add((row.Row.Type.Value, row.Row.Id));
        }

        var ordered = new List<ContentTextSlot>(expected.Keys);
        ordered.Sort(Compare);
        foreach (ContentTextSlot slot in ordered)
        {
            if (!live.Contains((slot.Type, slot.DefinitionId)))
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"Text of type {slot.Type} row {slot.DefinitionId} field '{slot.FieldName}' names a row the final row plan does not hold."),
                    new ContentTypeId(slot.Type),
                    slot.DefinitionId,
                    ContentAuthoringException.UnknownRowReason);
            }

            RequireEligible(registry, slot);
            if (!declared.Contains(slot.Language))
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"Text of type {slot.Type} row {slot.DefinitionId} is in language '{slot.Language}', which neither the base version nor the draft declares."),
                    new ContentTypeId(slot.Type),
                    slot.DefinitionId,
                    ContentAuthoringException.TextLanguageUndeclaredReason);
            }
        }

        var closes = new List<ContentTextRevision>();
        var values = new List<ContentTextRevision>(expected.Count);
        var held = new Dictionary<ContentTextSlot, string>(baseline.Count);
        foreach (ContentTextRevision revision in baseline)
        {
            ContentTextSlot slot = ContentTextSlot.Of(revision);
            held[slot] = revision.Value;
            if (expected.TryGetValue(slot, out string? wanted) && string.Equals(wanted, revision.Value, StringComparison.Ordinal))
            {
                values.Add(revision);
            }
            else
            {
                closes.Add(revision);
            }
        }

        var inserts = new List<ContentTextRevision>();
        foreach (ContentTextSlot slot in ordered)
        {
            string value = expected[slot];
            if (held.TryGetValue(slot, out string? before) && string.Equals(before, value, StringComparison.Ordinal))
            {
                continue;
            }

            var insert = new ContentTextRevision(
                new ContentTypeId(slot.Type), slot.DefinitionId, slot.FieldName, slot.Language, value, rows.VersionNumber, null);
            inserts.Add(insert);
            values.Add(insert);
        }

        values.Sort(static (left, right) => Compare(ContentTextSlot.Of(left), ContentTextSlot.Of(right)));
        return new ContentTextCandidate(values, declarations, closes, inserts);
    }

    /// <summary>
    /// Refuses a value whose type is unregistered or not CLIENT visible, or whose field is not a registered
    /// CLIENT visible localized text marker. Both levels are checked because every language chunk is named by
    /// the shared client manifest.
    /// </summary>
    /// <param name="registry">The registry.</param>
    /// <param name="slot">The value's identity.</param>
    /// <exception cref="ContentAuthoringException">The value is ineligible.</exception>
    public static ContentTypeRegistration RequireEligible(ContentTypeRegistry registry, ContentTextSlot slot)
    {
        var type = new ContentTypeId(slot.Type);
        if (!registry.TryGet(type, out ContentTypeRegistration? registration)
            || registration.DefaultVisibility != ContentVisibility.Client
            || !registration.Schema.TryGet(slot.FieldName, out ContentFieldEntry? field)
            || !field.IsDerivedMarker
            || field.Visibility != ContentVisibility.Client)
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Text of type {slot.Type} row {slot.DefinitionId} field '{slot.FieldName}' language '{slot.Language}' needs a registered CLIENT-visible type and a CLIENT-visible localized text marker, and the registry no longer offers both. It is refused rather than dropped from the version or leaked into a shared manifest."),
                type,
                slot.DefinitionId,
                ContentAuthoringException.TextTargetIneligibleReason);
        }

        return registration;
    }

    /// <summary>The baseline's languages plus the draft's introductions, ordinal by actual wire tag.</summary>
    static ContentTextLanguageDeclaration[] Declarations(ContentTextPublishSnapshot snapshot)
    {
        var output = new List<ContentTextLanguageDeclaration>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContentTextLanguage language in snapshot.BaselineText.Languages)
        {
            seen.Add(language.Language);
            output.Add(language.Declaration);
        }

        foreach (ContentTextLanguageDeclaration introduced in snapshot.TextState.Introductions)
        {
            if (seen.Add(introduced.Language))
            {
                output.Add(introduced);
            }
        }

        output.Sort(static (left, right) => string.CompareOrdinal(left.WireTag, right.WireTag));
        return [.. output];
    }

    static int Compare(ContentTextSlot left, ContentTextSlot right)
    {
        int order = left.Type.CompareTo(right.Type);
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
