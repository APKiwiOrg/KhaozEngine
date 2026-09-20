using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// One row on its way through a publish: the values it will carry, the id it does not have yet, and which
/// of the two temporal writes it is (spec 6.5).
/// <para>
/// It is MUTABLE and internal, deliberately, because step 3 fills in the id after step 2 built the row.
/// <see cref="ContentRow"/> is immutable and a row's id is not known until the allocator has run, so the
/// candidate is BUILT here and MATERIALISED as a snapshot once every id is in.
/// </para>
/// </summary>
sealed class ContentCandidateRow
{
    /// <summary>The ordinal a row that was not touched by any edit carries.</summary>
    public const int NoEdit = -1;

    /// <summary>The content type.</summary>
    public required ContentTypeRegistration Registration { get; init; }

    /// <summary>The row's key, which an update never changes.</summary>
    public required ContentKey Key { get; init; }

    /// <summary>The values, parallel BY INDEX to the type's schema.</summary>
    public required ContentFieldValue[] Fields { get; init; }

    /// <summary>The definition id, 0 on a new row until step 3 has run.</summary>
    public int DefinitionId { get; set; }

    /// <summary>The inheritance parent, 0 throughout phase 1.</summary>
    public int ParentId { get; init; }

    /// <summary>Whether the row is retired.</summary>
    public bool IsRetired { get; set; }

    /// <summary>The family the row was allocated from, or null.</summary>
    public long? FamilyId { get; set; }

    /// <summary>The version this revision is valid from, which is the new one for an entering row.</summary>
    public int ValidFromVersion { get; set; }

    /// <summary>True when this revision ENTERS at the new version.</summary>
    public bool IsEntering { get; init; }

    /// <summary>The valid-from of the revision this one replaces, or null when it replaces none.</summary>
    public int? ClosesValidFrom { get; init; }

    /// <summary>The edit that produced it, or <see cref="NoEdit"/> for an untouched row.</summary>
    public int EditOrdinal { get; init; } = NoEdit;

    /// <summary>
    /// True when this revision introduces a NEW definition id, which an add and a fork's copy do and an
    /// update's or a retire's successor does not. It is what step 3 walks.
    /// </summary>
    public bool IsNewDefinition { get; init; }

    /// <summary>True when step 3 must ISSUE the id, rather than the edit having carried one.</summary>
    public bool NeedsId { get; init; }

    /// <summary>Where the id came from, which step 3 fills in.</summary>
    public ContentIdSource Source { get; set; }

    /// <summary>The row as it will stand, which is only complete once step 3 has run.</summary>
    public ContentRow ToRow()
        => new(Registration.Type, DefinitionId, Key, ParentId, IsRetired, Fields);
}

/// <summary>
/// One remap rule an edit implies, held until step 3 has issued the ids its endpoints name (spec 6.5).
/// <para>
/// A <c>Fork</c>'s kind 3 rule names a <c>to_id</c> that does not exist when the edit is applied, so the
/// rule cannot be built at step 2. Holding the ROW rather than the number is what keeps the two in step.
/// </para>
/// </summary>
sealed class ContentPendingRule
{
    /// <summary>The content type the rule operates on.</summary>
    public required ContentTypeId Type { get; init; }

    /// <summary>Which of the four v1 kinds it is.</summary>
    public required RemapRuleKind Kind { get; init; }

    /// <summary>The row being remapped, whose id may still be pending.</summary>
    public required ContentCandidateRow From { get; init; }

    /// <summary>The destination row, or null for a kind that names none in <c>to_id</c>.</summary>
    public ContentCandidateRow? To { get; init; }

    /// <summary>The payload, complete except for a replacement id the edit already named.</summary>
    public required byte[] Payload { get; init; }
}

/// <summary>
/// Step 2 of spec 6.1: the candidate is the BASE version's rows with the draft's edits applied, and nothing
/// walks a row no edit names.
/// <para>
/// <b>That is what makes step 6 cheap.</b> A row not named by any edit is untouched, keeps the
/// <c>valid_from_version</c> of whichever old version it entered in, and its chunk is therefore not in the
/// affected set. The publish cost is a function of the DIFF and not of the catalog.
/// </para>
/// <para>
/// It also computes step 5's temporal writes, because the two are the same walk: an <c>Update</c> that
/// closes a revision and inserts its successor is one decision, and splitting it across two passes would be
/// two places to get the merge wrong.
/// </para>
/// </summary>
static class ContentCandidateBuilder
{
    /// <summary>
    /// Applies the frozen change set to the base version's rows.
    /// </summary>
    /// <param name="baseline">The base version, whose rows are copied rather than mutated.</param>
    /// <param name="changes">The frozen change set, in edit ordinal order.</param>
    /// <param name="registry">The registry the edits' types are declared in.</param>
    /// <param name="version">The new version number, which every entering revision is valid from.</param>
    /// <returns>Every live row at the new version, and the rules the edits imply.</returns>
    /// <exception cref="ContentAuthoringException">An edit names a type, a row, a field or a fork flag the schema or the base version does not carry.</exception>
    public static (List<ContentCandidateRow> Rows, List<ContentPendingRule> Rules) Build(
        ContentPublishBaseline baseline,
        ContentChangeSet changes,
        ContentTypeRegistry registry,
        int version)
    {
        var rows = new List<ContentCandidateRow>(baseline.Rows.Count + changes.Count);
        var byId = new Dictionary<(ushort Type, int Id), int>(baseline.Rows.Count);
        var pendingByKey = new Dictionary<(ushort Type, ContentKey Key), int>();
        var rules = new List<ContentPendingRule>();

        for (int i = 0; i < baseline.Rows.Count; i++)
        {
            ContentCandidateRow row = FromBase(registry, baseline.Rows[i]);
            byId[(row.Registration.Type.Value, row.DefinitionId)] = rows.Count;
            rows.Add(row);
        }

        IReadOnlyList<ContentEdit> edits = changes.Edits;
        for (int ordinal = 0; ordinal < edits.Count; ordinal++)
        {
            Apply(registry, edits[ordinal], ordinal, version, rows, byId, pendingByKey, rules);
        }

        return (rows, rules);
    }

    static void Apply(
        ContentTypeRegistry registry,
        ContentEdit edit,
        int ordinal,
        int version,
        List<ContentCandidateRow> rows,
        Dictionary<(ushort Type, int Id), int> byId,
        Dictionary<(ushort Type, ContentKey Key), int> pendingByKey,
        List<ContentPendingRule> rules)
    {
        ContentTypeRegistration registration = RequireType(registry, edit);
        switch (edit.Operation)
        {
            case ContentEditOperation.Add:
                ContentCandidateRow added = NewRow(registration, edit, ordinal, version);
                pendingByKey[(registration.Type.Value, edit.Key)] = rows.Count;
                if (added.DefinitionId != 0)
                {
                    byId[(registration.Type.Value, added.DefinitionId)] = rows.Count;
                }

                rows.Add(added);
                break;

            case ContentEditOperation.Update:
                _ = Succeed(registration, edit, ordinal, version, rows, byId, edit.Fields);
                break;

            case ContentEditOperation.Retire:
                Retire(registration, edit, ordinal, version, rows, byId, pendingByKey, rules);
                break;

            case ContentEditOperation.Fork:
                Fork(registration, edit, ordinal, version, rows, byId, rules);
                break;

            default:
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"Edit {ordinal} carries operation {(int)edit.Operation}, which is not one of the four of spec 3.7."),
                    edit.Type,
                    edit.DefinitionId,
                    ContentAuthoringException.UnknownEditOperationReason);
        }
    }

    /// <summary>
    /// A <c>Retire</c> closes the row and writes a retired successor, then appends a kind 2 rule carrying
    /// the policy. A retire of a row ADDED in the same draft has nothing to close, so it retires the row the
    /// draft is about to write rather than inventing a revision for it to replace.
    /// </summary>
    static void Retire(
        ContentTypeRegistration registration,
        ContentEdit edit,
        int ordinal,
        int version,
        List<ContentCandidateRow> rows,
        Dictionary<(ushort Type, int Id), int> byId,
        Dictionary<(ushort Type, ContentKey Key), int> pendingByKey,
        List<ContentPendingRule> rules)
    {
        ContentCandidateRow target;
        if (edit.DefinitionId == 0)
        {
            if (!pendingByKey.TryGetValue((registration.Type.Value, edit.Key), out int pending))
            {
                throw UnknownRow(registration, edit, ordinal);
            }

            target = rows[pending];
            target.IsRetired = true;
        }
        else
        {
            target = Succeed(registration, edit, ordinal, version, rows, byId, []);
            target.IsRetired = true;
        }

        rules.Add(new ContentPendingRule
        {
            Type = registration.Type,
            Kind = RemapRuleKind.Retired,
            From = target,
            Payload = RetirePayload(edit),
        });
    }

    /// <summary>
    /// A <c>Fork</c> writes THREE things and the order is the one that matters (spec 6.5). The copy is
    /// written FIRST, so the kind 3 rule appended last names a <c>to_id</c> that is already live at the new
    /// version, which is what <c>KEC0017</c> checks and what keeps the rule set applicable the moment it is
    /// published.
    /// <para>
    /// The legacy marker goes on the COPY, because the copy is what stored payloads are moved onto and the
    /// original id keeps serving new rolls. The source row's successor is an ordinary update successor and
    /// carries no flag.
    /// </para>
    /// </summary>
    static void Fork(
        ContentTypeRegistration registration,
        ContentEdit edit,
        int ordinal,
        int version,
        List<ContentCandidateRow> rows,
        Dictionary<(ushort Type, int Id), int> byId,
        List<ContentPendingRule> rules)
    {
        if (!byId.TryGetValue((registration.Type.Value, edit.DefinitionId), out int sourceIndex))
        {
            throw UnknownRow(registration, edit, ordinal);
        }

        ContentCandidateRow source = rows[sourceIndex];
        int flagIndex = FlagFieldIndex(registration, edit, ordinal);
        var copyFields = new ContentFieldValue[source.Fields.Length];
        Array.Copy(source.Fields, copyFields, copyFields.Length);
        copyFields[flagIndex] = ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1);

        var copy = new ContentCandidateRow
        {
            Registration = registration,
            Key = edit.ForkKey,
            Fields = copyFields,
            DefinitionId = 0,
            ParentId = source.ParentId,
            IsRetired = false,

            // The copy inherits the SOURCE row's family, so the legacy row is a member of the same family
            // the row it came from belongs to. The ID still comes from the plain counter (spec 6.3), because
            // a fork allocates through branch 3.
            FamilyId = source.FamilyId,
            ValidFromVersion = version,
            IsEntering = true,
            EditOrdinal = ordinal,
            IsNewDefinition = true,
            NeedsId = true,
            Source = ContentIdSource.Plain,
        };
        rows.Add(copy);

        ContentCandidateRow successor = Succeed(registration, edit, ordinal, version, rows, byId, edit.Fields);
        rules.Add(new ContentPendingRule
        {
            Type = registration.Type,
            Kind = RemapRuleKind.MovedToLegacy,
            From = successor,
            To = copy,
            Payload = [],
        });
    }

    /// <summary>
    /// Closes the live revision an edit names and puts its successor in its place, carrying the MERGED field
    /// set: the current row's fields with the edit's fields overlaid.
    /// </summary>
    static ContentCandidateRow Succeed(
        ContentTypeRegistration registration,
        ContentEdit edit,
        int ordinal,
        int version,
        List<ContentCandidateRow> rows,
        Dictionary<(ushort Type, int Id), int> byId,
        IReadOnlyList<ContentFieldEdit> changes)
    {
        (ushort, int) key = (registration.Type.Value, edit.DefinitionId);
        if (!byId.TryGetValue(key, out int index))
        {
            throw UnknownRow(registration, edit, ordinal);
        }

        ContentCandidateRow current = rows[index];
        if (current.IsEntering)
        {
            // The row is already this version's, so there is nothing to close: the draft holds one pending
            // intent per row and a second edit of one target under another operation is refused earlier.
            Overlay(registration, current.Fields, changes, edit, ordinal);
            return current;
        }

        var merged = new ContentFieldValue[current.Fields.Length];
        Array.Copy(current.Fields, merged, merged.Length);
        Overlay(registration, merged, changes, edit, ordinal);

        var successor = new ContentCandidateRow
        {
            Registration = registration,
            Key = current.Key,
            Fields = merged,
            DefinitionId = current.DefinitionId,
            ParentId = current.ParentId,
            IsRetired = current.IsRetired,
            FamilyId = current.FamilyId,
            ValidFromVersion = version,
            IsEntering = true,
            ClosesValidFrom = current.ValidFromVersion,
            EditOrdinal = ordinal,
            NeedsId = false,
            Source = ContentIdSource.Carried,
        };

        rows[index] = successor;
        return successor;
    }

    static ContentCandidateRow FromBase(ContentTypeRegistry registry, ContentRowRevision revision)
    {
        ContentRow row = revision.Row;
        if (!registry.TryGet(row.Type, out ContentTypeRegistration? registration))
        {
            throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"The base version carries a row of content type {row.Type.Value}, which this registry does not declare. A type is registered once at process start and a published type is never unregistered."),
                row.Type,
                row.Id,
                ContentAuthoringException.UnknownTypeReason);
        }

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
            FamilyId = revision.FamilyId,
            ValidFromVersion = revision.ValidFromVersion,
            IsEntering = false,
            NeedsId = false,
            Source = ContentIdSource.Carried,
        };
    }

    static ContentCandidateRow NewRow(
        ContentTypeRegistration registration,
        ContentEdit edit,
        int ordinal,
        int version)
    {
        IReadOnlyList<ContentFieldEntry> schema = registration.Schema.Fields;
        var fields = new ContentFieldValue[schema.Count];
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = ContentFieldValue.Absent(schema[i].Kind);
        }

        Overlay(registration, fields, edit.Fields, edit, ordinal);

        // The id is a property of the EDIT: an add carrying one keeps it, and one carrying 0 is allocated
        // at step 3. Only a bulk import into an empty database writes the first kind.
        bool carried = edit.DefinitionId != 0;
        return new ContentCandidateRow
        {
            Registration = registration,
            Key = edit.Key,
            Fields = fields,
            DefinitionId = edit.DefinitionId,
            ParentId = 0,

            // Only an import ever enters ALREADY retired, and only because the bundle it came from carries
            // the rule that retired the row. Every other path reaches the flag through a Retire edit.
            IsRetired = edit.ImportedAsRetired,
            FamilyId = edit.FamilyId,
            ValidFromVersion = version,
            IsEntering = true,
            EditOrdinal = ordinal,
            IsNewDefinition = true,
            NeedsId = !carried,
            Source = carried
                ? ContentIdSource.Carried
                : edit.FamilyId is null ? ContentIdSource.Plain : ContentIdSource.Family,
        };
    }

    static void Overlay(
        ContentTypeRegistration registration,
        ContentFieldValue[] fields,
        IReadOnlyList<ContentFieldEdit> changes,
        ContentEdit edit,
        int ordinal)
    {
        for (int i = 0; i < changes.Count; i++)
        {
            ContentFieldEdit change = changes[i];
            int index = FieldIndex(registration, change.Name);
            if (index < 0)
            {
                throw new ContentAuthoringException(
                    FormattableString.Invariant(
                        $"Edit {ordinal} names field '{change.Name}' of content type {registration.Type.Value} '{registration.TypeKey}', whose schema does not declare it."),
                    edit.Type,
                    edit.DefinitionId,
                    ContentAuthoringException.UnknownFieldReason);
            }

            fields[index] = change.Value;
        }
    }

    static int FlagFieldIndex(ContentTypeRegistration registration, ContentEdit edit, int ordinal)
    {
        int index = edit.ForkFlagField is null ? -1 : FieldIndex(registration, edit.ForkFlagField);
        if (index >= 0 && registration.Schema.Fields[index].Kind == ContentFieldKind.Bool)
        {
            return index;
        }

        throw new ContentAuthoringException(
            FormattableString.Invariant(
                $"Edit {ordinal} forks row {edit.DefinitionId} of content type {registration.Type.Value} '{registration.TypeKey}' and names '{edit.ForkFlagField}' as the flag to set on the copy. The engine has no opinion about which boolean means superseded on a type it did not define, so it checks only that the field exists and is Bool."),
            edit.Type,
            edit.DefinitionId,
            ContentAuthoringException.ForkFlagFieldReason);
    }

    static int FieldIndex(ContentTypeRegistration registration, string name)
        => registration.Schema.IndexOf(name);

    static byte[] RetirePayload(ContentEdit edit)
    {
        if (edit.RetirePolicy != ContentRetirePolicy.Replacement)
        {
            return [(byte)edit.RetirePolicy];
        }

        var payload = new byte[1 + sizeof(int)];
        payload[0] = RemapRule.RetirePolicyReplacement;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1), edit.ReplacementId);
        return payload;
    }

    static ContentTypeRegistration RequireType(ContentTypeRegistry registry, ContentEdit edit)
        => registry.TryGet(edit.Type, out ContentTypeRegistration? registration)
            ? registration
            : throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"Content type {edit.Type.Value} is not registered, so an edit naming it cannot be published."),
                edit.Type,
                edit.DefinitionId,
                ContentAuthoringException.UnknownTypeReason);

    static ContentAuthoringException UnknownRow(
        ContentTypeRegistration registration,
        ContentEdit edit,
        int ordinal)
        => new(
            FormattableString.Invariant(
                $"Edit {ordinal} is a {edit.Operation} of row {edit.DefinitionId} ('{edit.Key}') of content type {registration.Type.Value} '{registration.TypeKey}', which the base version carries no live row for."),
            edit.Type,
            edit.DefinitionId,
            ContentAuthoringException.UnknownRowReason);
}
