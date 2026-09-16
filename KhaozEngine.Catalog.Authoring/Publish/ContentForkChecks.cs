using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// <c>KEC0041</c>, the <c>Fork</c> edit's PRECONDITIONS (spec 3.7, 5.2). It is the authoring half's own
/// check and not the validator's: the validator takes a candidate, and three of the four things that can be
/// wrong with a fork are statements about the EDIT rather than about any row a candidate would hold.
/// <para>
/// <b>It runs BEFORE the candidate is built</b>, because a fork of a row that is not there cannot be applied
/// at all, and a refusal an operator can read beats an exception from the middle of the walk. Every fork in
/// the draft is checked, so a console gets every bad fork in one answer rather than the first.
/// </para>
/// <para>
/// A fork is refused as a WHOLE or applied as a whole. Allocating the copy's id, copying the field set and
/// appending the kind 3 rule are one atomic statement about one definition, and an author who did them as
/// three edits could have the publish succeed with the rule missing, which is the state nothing can detect
/// afterwards: the pages are already migrated and the original values are gone.
/// </para>
/// </summary>
static class ContentForkChecks
{
    /// <summary>The code every precondition failure carries, whichever of the four it is.</summary>
    public const string Code = "KEC0041";

    /// <summary>
    /// The ordinal a key the BASE VERSION already carries is recorded under. It is below every edit ordinal,
    /// so a fork can never be the edit that introduced a key a published row holds.
    /// </summary>
    const int BaselineOrdinal = -1;

    /// <summary>
    /// Checks every <c>Fork</c> edit in the change set against the base version's rows.
    /// </summary>
    /// <param name="baseline">The base version, whose live rows a fork's source must be one of.</param>
    /// <param name="changes">The frozen change set.</param>
    /// <param name="registry">The registry the edits' types are declared in.</param>
    /// <returns>One finding per failed precondition, empty when every fork may proceed.</returns>
    public static IReadOnlyList<ContentFinding> Check(
        ContentPublishBaseline baseline,
        ContentChangeSet changes,
        ContentTypeRegistry registry)
    {
        var findings = new List<ContentFinding>();
        IReadOnlyList<ContentEdit> edits = changes.Edits;
        Dictionary<(ushort Type, string Key), int>? taken = null;

        for (int ordinal = 0; ordinal < edits.Count; ordinal++)
        {
            ContentEdit edit = edits[ordinal];
            if (edit.Operation != ContentEditOperation.Fork)
            {
                continue;
            }

            taken ??= TakenKeys(baseline, edits);
            Check(findings, baseline, registry, taken, edit, ordinal);
        }

        return findings;
    }

    static void Check(
        List<ContentFinding> findings,
        ContentPublishBaseline baseline,
        ContentTypeRegistry registry,
        Dictionary<(ushort Type, string Key), int> taken,
        ContentEdit edit,
        int ordinal)
    {
        if (!registry.TryGet(edit.Type, out ContentTypeRegistration? registration))
        {
            // Not a fork precondition: an unregistered type is refused for every operation alike, and the
            // candidate builder's own refusal names it.
            return;
        }

        ContentRow? source = Live(baseline, edit.Type, edit.DefinitionId);
        if (source is null)
        {
            findings.Add(new ContentFinding(
                edit.Type,
                edit.DefinitionId,
                Code,
                FormattableString.Invariant(
                    $"Edit {ordinal} forks row {edit.DefinitionId} of type '{registration.TypeKey}', which the base version carries no live row for. A fork copies a row's whole field set, so there has to be one to copy.")));
        }
        else if (source.IsRetired)
        {
            findings.Add(new ContentFinding(
                edit.Type,
                edit.DefinitionId,
                Code,
                FormattableString.Invariant(
                    $"Edit {ordinal} forks row {edit.DefinitionId} ('{source.Key}') of type '{registration.TypeKey}', which is already retired. A fork keeps BOTH rows live, the original under its own id with the new values and the copy under a new id with the flag set, so a retired definition has nothing to keep.")));
        }

        string forkKey = edit.ForkKey.ToString();
        string? defect = ContentKeyShape.Defect(forkKey);
        if (defect is not null)
        {
            findings.Add(new ContentFinding(
                edit.Type,
                edit.DefinitionId,
                Code,
                FormattableString.Invariant(
                    $"Edit {ordinal} forks row {edit.DefinitionId} of type '{registration.TypeKey}' under key '{forkKey}', which is {defect}. {ContentKeyShape.Rule}")));
        }
        else if (taken.TryGetValue((edit.Type.Value, forkKey), out int introducedBy) && introducedBy != ordinal)
        {
            findings.Add(new ContentFinding(
                edit.Type,
                edit.DefinitionId,
                Code,
                FormattableString.Invariant(
                    $"Edit {ordinal} forks row {edit.DefinitionId} of type '{registration.TypeKey}' under key '{forkKey}', which is already taken on that type. A key is unique within its type and is immutable once published, so the engine will not invent a free one.")));
        }

        CheckFlagField(findings, registration, edit, ordinal);
    }

    /// <summary>
    /// The flag field is the CALLER's and the engine has no opinion about which boolean means superseded on a
    /// type it did not define, so it checks only that the field exists and is <c>Bool</c>.
    /// </summary>
    static void CheckFlagField(
        List<ContentFinding> findings,
        ContentTypeRegistration registration,
        ContentEdit edit,
        int ordinal)
    {
        string flagField = edit.ForkFlagField ?? string.Empty;
        if (registration.Schema.TryGet(flagField, out ContentFieldEntry? entry)
            && entry.Kind == ContentFieldKind.Bool)
        {
            return;
        }

        findings.Add(new ContentFinding(
            edit.Type,
            edit.DefinitionId,
            Code,
            FormattableString.Invariant(
                $"Edit {ordinal} forks row {edit.DefinitionId} of type '{registration.TypeKey}' and names '{flagField}' as the flag to set on the copy, which that type's schema does not declare as a Bool field. The flag field is the caller's, and the engine checks only that it exists and is the right kind.")));
    }

    /// <summary>
    /// Every key that is already spoken for on each type, mapped to WHAT introduced it: the base version's
    /// live rows under <see cref="BaselineOrdinal"/>, then every key an edit in this same draft introduces
    /// under that edit's own ordinal. A draft that adds <c>sword_legacy</c> and then forks a row under the
    /// same key collides at publish, and saying so as a precondition is more useful than a duplicate-key
    /// finding on a candidate that should never have been built.
    /// <para>
    /// <b>A fork's key is a key it INTRODUCES, exactly as an add's is.</b> Two forks under one key would
    /// otherwise reach the candidate and be refused by <c>KEC0002</c> instead, naming two ids the author
    /// never wrote. The ordinal is what keeps a fork from colliding with itself: the edit that introduced
    /// the key is the one holding it, and only a LATER edit naming it is a collision.
    /// </para>
    /// </summary>
    static Dictionary<(ushort Type, string Key), int> TakenKeys(
        ContentPublishBaseline baseline,
        IReadOnlyList<ContentEdit> edits)
    {
        var taken = new Dictionary<(ushort Type, string Key), int>();
        for (int i = 0; i < baseline.Rows.Count; i++)
        {
            ContentRow row = baseline.Rows[i].Row;
            taken[(row.Type.Value, row.Key.ToString())] = BaselineOrdinal;
        }

        for (int i = 0; i < edits.Count; i++)
        {
            ContentEdit edit = edits[i];
            (ushort Type, string Key)? introduced = edit.Operation switch
            {
                ContentEditOperation.Add => (edit.Type.Value, edit.Key.ToString()),
                ContentEditOperation.Fork => (edit.Type.Value, edit.ForkKey.ToString()),
                _ => null,
            };

            if (introduced is { } key && !taken.ContainsKey(key))
            {
                taken.Add(key, i);
            }
        }

        return taken;
    }

    static ContentRow? Live(ContentPublishBaseline baseline, ContentTypeId type, int definitionId)
    {
        for (int i = 0; i < baseline.Rows.Count; i++)
        {
            ContentRow row = baseline.Rows[i].Row;
            if (row.Type == type && row.Id == definitionId)
            {
                return row;
            }
        }

        return null;
    }

    // The key shape rule itself lives in ContentKeyShape, because it has three callers at three layers now:
    // the validator's KEC0001 sweep, this fork precondition (whose copy key never reaches that sweep, since
    // the row it would go on does not exist until publish) and the admin boundary, which refuses an add's key
    // before the edit enters the draft.
}
