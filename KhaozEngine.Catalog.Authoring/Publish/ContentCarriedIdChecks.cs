using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The preconditions of an <c>Add</c> that CARRIES its own definition id, which a bulk import and a content
/// upgrade both write (contracts 5.1, spec 6.3, 10.9).
/// <para>
/// <b>It runs BEFORE the candidate is built, and that placement is the whole point.</b> Step 3 seeds the
/// type's reserved and issued marks up to the largest carried id, and those two writes commit on their own
/// before step 4 validates anything. A carried id refused by the sweep would therefore leave the marks raised
/// for a version nobody published, which is the one thing a refusal is supposed not to do. Checking the EDIT
/// costs a walk over the change set and keeps the durable state exactly where it was.
/// </para>
/// <para>
/// <b>It also closes the row map.</b> <c>ContentCandidateBuilder</c> indexes the base version's rows by
/// <c>(type, id)</c> and a carried add writes its own entry into that map, so an add carrying an id a base
/// row already holds would take the map entry over and a later <c>Update</c>, <c>Retire</c> or <c>Fork</c> of
/// that id in the same draft would resolve to the new row instead. Refusing the collision here is what makes
/// that overwrite unreachable rather than merely unlikely.
/// </para>
/// <para>
/// The three codes are the ones already issued for these invariants: <c>KEC0036</c> for an id that is taken,
/// <c>KEC0042</c> for one over the type's declared ceiling and <c>KEC0037</c> for one that disagrees with the
/// family blocks. A finding rather than a throw, so a caller reads every bad add in one answer.
/// </para>
/// </summary>
static class ContentCarriedIdChecks
{
    /// <summary>An id another row or another edit already holds, which is never reused.</summary>
    public const string TakenCode = "KEC0036";

    /// <summary>An id over the ceiling the type declared at registration.</summary>
    public const string CeilingCode = "KEC0042";

    /// <summary>An id that disagrees with the type's family blocks, in either direction.</summary>
    public const string FamilyCode = "KEC0037";

    /// <summary>
    /// Whether the change set holds an add that carries its own id at all. Every ordinary draft answers
    /// false, and the family read the check needs is only paid when it answers true.
    /// </summary>
    /// <param name="changes">The frozen change set.</param>
    public static bool CarriesAnId(ContentChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        IReadOnlyList<ContentEdit> edits = changes.Edits;
        for (int i = 0; i < edits.Count; i++)
        {
            if (IsCarried(edits[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks every carried add in the change set against the base version's rows, the type's declared
    /// ceiling and the store's family blocks.
    /// </summary>
    /// <param name="baseline">The base version, whose rows already hold ids.</param>
    /// <param name="changes">The frozen change set.</param>
    /// <param name="registry">The registry the edits' types are declared in.</param>
    /// <param name="families">Every family the store holds, with its blocks.</param>
    /// <returns>One finding per failed precondition, empty when every carried add may proceed.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<ContentFinding> Check(
        ContentPublishBaseline baseline,
        ContentChangeSet changes,
        ContentTypeRegistry registry,
        IReadOnlyList<ContentFamily> families)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(families);

        var findings = new List<ContentFinding>();
        IReadOnlyList<ContentEdit> edits = changes.Edits;
        Dictionary<(ushort Type, int Id), string>? taken = null;

        for (int ordinal = 0; ordinal < edits.Count; ordinal++)
        {
            ContentEdit edit = edits[ordinal];
            if (!IsCarried(edit) || !registry.TryGet(edit.Type, out ContentTypeRegistration? registration))
            {
                // An unregistered type is refused for every operation alike, and the candidate builder's own
                // refusal names it.
                continue;
            }

            taken ??= TakenIds(baseline);
            Check(findings, registration, families, taken, edit, ordinal);
        }

        return findings;
    }

    static void Check(
        List<ContentFinding> findings,
        ContentTypeRegistration registration,
        IReadOnlyList<ContentFamily> families,
        Dictionary<(ushort Type, int Id), string> taken,
        ContentEdit edit,
        int ordinal)
    {
        int id = edit.DefinitionId;
        (ushort, int) slot = (registration.Type.Value, id);
        if (taken.TryGetValue(slot, out string? holder))
        {
            findings.Add(new ContentFinding(
                edit.Type,
                id,
                TakenCode,
                FormattableString.Invariant(
                    $"Edit {ordinal} adds '{edit.Key}' to type '{registration.TypeKey}' under definition id {id}, which {holder} already holds. A carried id is written by a bulk import and by a content upgrade, and in both cases it names a row the catalog does not have yet: an id is unique within its type and is never reused, not even by a retired row.")));
        }
        else
        {
            taken.Add(slot, FormattableString.Invariant($"edit {ordinal} ('{edit.Key}')"));
        }

        if (registration.MaxDefinitionId is int ceiling && id > ceiling)
        {
            findings.Add(new ContentFinding(
                edit.Type,
                id,
                CeilingCode,
                FormattableString.Invariant(
                    $"Edit {ordinal} adds '{edit.Key}' to type '{registration.TypeKey}' under definition id {id}, over the ceiling of {ceiling} that type declared at registration. A ceiling is a format constraint rather than a preference, so the id the bundle names cannot be written.")));
        }

        CheckFamily(findings, registration, families, edit, ordinal);
    }

    /// <summary>
    /// The carried id against the type's family blocks, in BOTH directions. An add naming no family may not
    /// land inside a block, which is <c>KEC0037</c> as issued, and an add naming one may not land outside
    /// that family's own blocks, which is the same rule read from the other end: a row's membership is the
    /// two-comparison block test of contracts 5.2 rather than the column it was written with, so an id and a
    /// family that disagree would make one of the two a lie.
    /// </summary>
    static void CheckFamily(
        List<ContentFinding> findings,
        ContentTypeRegistration registration,
        IReadOnlyList<ContentFamily> families,
        ContentEdit edit,
        int ordinal)
    {
        int id = edit.DefinitionId;
        ContentFamily? holder = null;
        for (int i = 0; i < families.Count; i++)
        {
            ContentFamily family = families[i];
            if (family.Type.Value == registration.Type.Value && family.Contains(id))
            {
                holder = family;
                break;
            }
        }

        if (edit.FamilyId is not long named)
        {
            if (holder is not null)
            {
                findings.Add(new ContentFinding(
                    edit.Type,
                    id,
                    FamilyCode,
                    FormattableString.Invariant(
                        $"Edit {ordinal} adds '{edit.Key}' to type '{registration.TypeKey}' under definition id {id}, which is inside the reserved block of family '{holder.FamilyKey}', and the edit names no family. A block is reserved for its family alone, and an id inside one answers the membership test whatever column the row was written with.")));
            }

            return;
        }

        if (holder is null || holder.FamilyId != named)
        {
            findings.Add(new ContentFinding(
                edit.Type,
                id,
                FamilyCode,
                FormattableString.Invariant(
                    $"Edit {ordinal} adds '{edit.Key}' to type '{registration.TypeKey}' under definition id {id} and names family {named}, whose reserved blocks do not hold that id. A family's members come from its aligned blocks, so an id outside them would report the wrong membership for the life of the row.")));
        }
    }

    /// <summary>
    /// Every id of every type the base version already holds, mapped to how a refusal names the holder. A
    /// RETIRED row is in here exactly like a live one: it keeps its id forever so a stored stack still
    /// decodes, and reissuing it is the defect this map exists to catch.
    /// </summary>
    static Dictionary<(ushort Type, int Id), string> TakenIds(ContentPublishBaseline baseline)
    {
        var taken = new Dictionary<(ushort Type, int Id), string>(baseline.Rows.Count);
        for (int i = 0; i < baseline.Rows.Count; i++)
        {
            ContentRow row = baseline.Rows[i].Row;
            taken[(row.Type.Value, row.Id)] = FormattableString.Invariant(
                $"the {(row.IsRetired ? "retired" : "live")} row '{row.Key}'");
        }

        return taken;
    }

    static bool IsCarried(ContentEdit edit)
        => edit.Operation == ContentEditOperation.Add && edit.DefinitionId != 0;
}
