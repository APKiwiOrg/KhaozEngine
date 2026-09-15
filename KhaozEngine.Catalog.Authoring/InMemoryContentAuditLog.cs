using System;
using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The field-level audit ledger <see cref="InMemoryContentAuthoringStore"/> appends to, kept as its own type
/// rather than as a list on the store, because rendering a value, capping it and paging the ledger are one
/// job and the store's is another.
/// <para>
/// The unit of an entry is one FIELD, so an update that changes three fields writes three entries sharing an
/// occurred-at, an actor, an operator and a note. An edit with no fields at all, a retire, writes one
/// row-level entry naming the operation instead, so a retire still leaves a trace.
/// </para>
/// </summary>
sealed class InMemoryContentAuditLog(Func<DateTimeOffset> clock)
{
    readonly List<ContentAuditEntry> _entries = [];
    long _nextAuditId = 1;

    /// <summary>Appends one entry, stamped from the store's own clock.</summary>
    /// <param name="action">One of <see cref="ContentAuditActions"/>.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">What the console forwarded, empty when it forwarded none.</param>
    /// <param name="type">The content type, or the default for a store-level action.</param>
    /// <param name="definitionId">The row's id, or 0 for a store-level action.</param>
    /// <param name="key">The row's key, or the default for a store-level action.</param>
    /// <param name="fieldName">The schema field name, empty for a row-level action.</param>
    /// <param name="before">The old value rendered, or null for absent.</param>
    /// <param name="after">The new value rendered, or null for absent.</param>
    /// <param name="versionNumber">0 for a draft edit, the published number for a publish.</param>
    /// <param name="note">The operator's note, empty when none.</param>
    public void Append(
        string action,
        string actor,
        string operatorId,
        ContentTypeId type,
        int definitionId,
        ContentKey key,
        string fieldName,
        string? before,
        string? after,
        int versionNumber,
        string note)
    {
        var staged = new List<ContentAuditEntry>(1);
        Stage(staged, action, actor, operatorId, type, definitionId, key, fieldName, before, after, versionNumber, note);
        Commit(staged);
    }

    /// <summary>
    /// Renders one entry into a STAGING list without appending it, which is how a caller makes the audit
    /// write and the change it describes land together: everything that can fail happens here, and
    /// <see cref="Commit"/> afterwards cannot.
    /// </summary>
    /// <param name="staged">The staging list the entry is added to.</param>
    /// <param name="action">One of <see cref="ContentAuditActions"/>.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">What the console forwarded, empty when it forwarded none.</param>
    /// <param name="type">The content type, or the default for a store-level action.</param>
    /// <param name="definitionId">The row's id, or 0 for a store-level action.</param>
    /// <param name="key">The row's key, or the default for a store-level action.</param>
    /// <param name="fieldName">The schema field name, empty for a row-level action.</param>
    /// <param name="before">The old value rendered, or null for absent.</param>
    /// <param name="after">The new value rendered, or null for absent.</param>
    /// <param name="versionNumber">0 for a draft edit, the published number for a publish.</param>
    /// <param name="note">The operator's note, empty when none.</param>
    public void Stage(
        List<ContentAuditEntry> staged,
        string action,
        string actor,
        string operatorId,
        ContentTypeId type,
        int definitionId,
        ContentKey key,
        string fieldName,
        string? before,
        string? after,
        int versionNumber,
        string note)
    {
        ArgumentNullException.ThrowIfNull(staged);
        staged.Add(new ContentAuditEntry(
            0,
            clock(),
            actor,
            operatorId,
            action,
            type,
            definitionId,
            key,
            fieldName,
            before,
            after,
            versionNumber,
            note));
    }

    /// <summary>
    /// Appends a staged batch, numbering each entry as it lands. It cannot fail, which is the whole point:
    /// the audit ids are taken HERE rather than at staging, so a batch that never committed burns none.
    /// </summary>
    /// <param name="staged">The entries rendered by <see cref="Stage"/>.</param>
    public void Commit(List<ContentAuditEntry> staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        for (int i = 0; i < staged.Count; i++)
        {
            _entries.Add(staged[i] with { AuditId = _nextAuditId++ });
        }
    }

    /// <summary>Writes one edit's entries: one per CHANGED FIELD, or one row-level entry when it changes none.</summary>
    /// <param name="edit">The edit applied to the open draft.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">What the console forwarded.</param>
    /// <param name="note">The operator's note.</param>
    public void AppendEdit(ContentEdit edit, string actor, string operatorId, string note)
    {
        var staged = new List<ContentAuditEntry>();
        StageEdit(staged, edit, actor, operatorId, note);
        Commit(staged);
    }

    /// <summary>
    /// One edit's entries rendered into a staging list, which is what the store uses so a failed audit write
    /// takes the edit down with it rather than leaving a draft edit nothing recorded.
    /// </summary>
    /// <param name="staged">The staging list the entries are added to.</param>
    /// <param name="edit">The edit applied to the open draft.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">What the console forwarded.</param>
    /// <param name="note">The operator's note.</param>
    public void StageEdit(
        List<ContentAuditEntry> staged,
        ContentEdit edit,
        string actor,
        string operatorId,
        string note)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.Fields.Count == 0)
        {
            Stage(
                staged,
                ContentAuditActions.DraftEdit,
                actor,
                operatorId,
                edit.Type,
                edit.DefinitionId,
                edit.Key,
                string.Empty,
                null,
                edit.Operation.ToString(),
                0,
                note);
            return;
        }

        for (int i = 0; i < edit.Fields.Count; i++)
        {
            ContentFieldEdit field = edit.Fields[i];
            Stage(
                staged,
                ContentAuditActions.DraftEdit,
                actor,
                operatorId,
                edit.Type,
                edit.DefinitionId,
                edit.Key,
                field.Name,
                null,
                Render(field.Value),
                0,
                note);
        }
    }

    /// <summary>Entries NEWEST first, filtered then paged. Type id 0 means every type and id 0 every row.</summary>
    /// <param name="type">The content type to filter to, or type id 0 for every type.</param>
    /// <param name="definitionId">The definition id to filter to, or 0 for every row.</param>
    /// <param name="skip">How many entries to skip.</param>
    /// <param name="take">How many to return.</param>
    /// <param name="maxPageSize">The implementation's page cap.</param>
    public IReadOnlyList<ContentAuditEntry> List(
        ContentTypeId type,
        int definitionId,
        int skip,
        int take,
        int maxPageSize)
    {
        var matched = new List<ContentAuditEntry>();
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            ContentAuditEntry entry = _entries[i];
            if (type.Value != 0 && entry.Type != type)
            {
                continue;
            }

            if (definitionId != 0 && entry.DefinitionId != definitionId)
            {
                continue;
            }

            matched.Add(entry);
        }

        int from = Math.Min(skip, matched.Count);
        int count = Math.Min(Math.Min(take, maxPageSize), matched.Count - from);
        return matched.GetRange(from, count);
    }

    /// <summary>A number rendered the way an audit column holds it, invariant culture, null for absent.</summary>
    /// <param name="value">The number, or null.</param>
    public static string? Render(int? value)
        => value is int number ? number.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// One field value rendered through its kind. A rendering that would not fit the audit column is
    /// abbreviated VISIBLY, with a trailing marker, so a reader can never take an abbreviated value for a
    /// complete one.
    /// </summary>
    /// <param name="value">The field value.</param>
    public static string? Render(ContentFieldValue value)
    {
        if (value.IsAbsent)
        {
            return null;
        }

        string rendered = ContentFieldValue.StoresNumber(value.Kind)
            ? value.Number.ToString(CultureInfo.InvariantCulture)
            : Convert.ToHexString(value.Bytes.Span).ToLowerInvariant();

        return rendered.Length <= ContentAuditEntry.MaxValueLength
            ? rendered
            : string.Concat(rendered.AsSpan(0, ContentAuditEntry.MaxValueLength - 5), "[cut]");
    }
}
