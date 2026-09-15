using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// What one definition's row did between two versions (spec 10.6). Three of the four are the ordinary
/// direction of travel and <see cref="Removed"/> is not: a definition is NEVER deleted, so it appears only
/// when the diff is asked the question backwards, with an EARLIER version as the destination.
/// </summary>
public enum ContentDiffOperation
{
    /// <summary>The definition is live at the destination and was not live at the source.</summary>
    Add = 1,

    /// <summary>The definition is live at both and at least one field value differs.</summary>
    Update = 2,

    /// <summary>The definition is live at both and the destination's row carries the retired flag.</summary>
    Retire = 3,

    /// <summary>
    /// The definition is live at the SOURCE and not at the destination. A publish can never produce this,
    /// because a definition that leaves play is retired and its row stays in the pack forever. A diff whose
    /// destination is an earlier version can, and saying so is more useful than dropping the row.
    /// </summary>
    Removed = 4,
}

/// <summary>
/// ONE field's before and after, rendered as TEXT through the field's kind. The rendering is the AUDIT's own
/// (spec 4.6), so an operator reading a diff and an operator reading the audit row that publish wrote see
/// the same string for the same value rather than two formattings of one number.
/// </summary>
/// <param name="Field">The schema field name.</param>
/// <param name="Before">The source value rendered, or null when the field was absent there.</param>
/// <param name="After">The destination value rendered, or null when the field is absent there.</param>
public readonly record struct ContentDiffField(string Field, string? Before, string? After);

/// <summary>
/// One row's entry in a diff: what happened to it, and every field that differs. It is FIELD LEVEL and is
/// computed over the per-field values rather than by comparing chunk hashes, so "what changed between 46 and
/// 47" is an answer an operator can read rather than an inequality between two digests.
/// </summary>
/// <param name="Type">The content type.</param>
/// <param name="Id">The definition id.</param>
/// <param name="Key">The row's key, which never changes once published.</param>
/// <param name="Operation">What happened to the definition.</param>
/// <param name="Fields">Every differing field, in schema order. Empty on a retire that changed no value.</param>
public sealed record ContentDiffEntry(
    ContentTypeId Type,
    int Id,
    ContentKey Key,
    ContentDiffOperation Operation,
    IReadOnlyList<ContentDiffField> Fields);

/// <summary>
/// One type's share of the DOWNLOAD an edit costs: how many of its chunks the change touches against how
/// many it has. This is the operator-facing half of the one-item-edit budget, and it is why an operator can
/// see the cost of a publish before making it.
/// </summary>
/// <param name="Type">The content type.</param>
/// <param name="TypeKey">The type's key, so a console renders the row without a second lookup.</param>
/// <param name="ChangedChunks">How many distinct chunks hold a changed row.</param>
/// <param name="TotalChunks">How many distinct chunks the destination holds for this type.</param>
public sealed record ContentChunkSummaryEntry(
    ContentTypeId Type,
    string TypeKey,
    int ChangedChunks,
    int TotalChunks);

/// <summary>
/// The FIELD LEVEL diff of spec 10.6, between two row sets, plus the chunk summary that says what publishing
/// it would cost a client to download.
/// <para>
/// It is computed over the ROWS and never over chunk hashes. Two versions whose chunk hashes differ tell an
/// operator that something changed somewhere in a slot range of 256 ids, which is not an answer, and two
/// versions whose hashes agree can still differ in the server-only half of a field set.
/// </para>
/// <para>
/// The destination is a version NUMBER or null, and null means the draft-applied candidate. That is spec
/// 10.6's <c>"to": 0</c> read as what it is: a row set that has no number yet because it has not been
/// published.
/// </para>
/// </summary>
public sealed class ContentDiff
{
    ContentDiff(
        int fromVersion,
        int? toVersion,
        IReadOnlyList<ContentDiffEntry> changes,
        IReadOnlyList<ContentChunkSummaryEntry> chunkSummary)
    {
        FromVersion = fromVersion;
        ToVersion = toVersion;
        Changes = changes;
        ChunkSummary = chunkSummary;
    }

    /// <summary>The source version's number.</summary>
    public int FromVersion { get; }

    /// <summary>The destination version's number, or null for the draft-applied candidate.</summary>
    public int? ToVersion { get; }

    /// <summary>Every changed definition, ordered by type id then definition id.</summary>
    public IReadOnlyList<ContentDiffEntry> Changes { get; }

    /// <summary>One entry per type that has a chunk at the destination, ordered by type id.</summary>
    public IReadOnlyList<ContentChunkSummaryEntry> ChunkSummary { get; }

    /// <summary>True when nothing differs, which is what a draft of edits that set no new value produces.</summary>
    public bool IsEmpty => Changes.Count == 0;

    /// <summary>
    /// Diffs two row sets field by field.
    /// </summary>
    /// <param name="fromVersion">The source version's number.</param>
    /// <param name="from">The rows live at the source.</param>
    /// <param name="toVersion">The destination version's number, or null for a candidate with no number yet.</param>
    /// <param name="to">The rows live at the destination.</param>
    /// <param name="registry">The registry the rows' types are declared in, which supplies the field names and the chunk slots.</param>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="toVersion"/> is null.</exception>
    public static ContentDiff Between(
        int fromVersion,
        IReadOnlyList<ContentRowRevision> from,
        int? toVersion,
        IReadOnlyList<ContentRowRevision> to,
        ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(registry);

        Dictionary<(ushort Type, int Id), ContentRow> before = Index(from);
        Dictionary<(ushort Type, int Id), ContentRow> after = Index(to);
        var changes = new List<ContentDiffEntry>();
        var changedChunks = new Dictionary<ushort, HashSet<int>>();

        foreach (KeyValuePair<(ushort Type, int Id), ContentRow> entry in after)
        {
            ContentRow destination = entry.Value;
            ContentTypeRegistration registration = Require(registry, destination.Type);
            if (!before.TryGetValue(entry.Key, out ContentRow? source))
            {
                changes.Add(Entry(registration, destination, ContentDiffOperation.Add, Fields(registration, null, destination)));
                Touch(changedChunks, registration, destination.Id);
                continue;
            }

            IReadOnlyList<ContentDiffField> fields = Fields(registration, source, destination);
            bool retired = destination.IsRetired && !source.IsRetired;
            if (fields.Count == 0 && !retired)
            {
                continue;
            }

            changes.Add(Entry(
                registration,
                destination,
                retired ? ContentDiffOperation.Retire : ContentDiffOperation.Update,
                fields));
            Touch(changedChunks, registration, destination.Id);
        }

        foreach (KeyValuePair<(ushort Type, int Id), ContentRow> entry in before)
        {
            if (after.ContainsKey(entry.Key))
            {
                continue;
            }

            ContentRow source = entry.Value;
            ContentTypeRegistration registration = Require(registry, source.Type);
            changes.Add(Entry(registration, source, ContentDiffOperation.Removed, Fields(registration, source, null)));
            Touch(changedChunks, registration, source.Id);
        }

        changes.Sort(static (left, right) => left.Type.Value == right.Type.Value
            ? left.Id.CompareTo(right.Id)
            : left.Type.Value.CompareTo(right.Type.Value));

        return new ContentDiff(fromVersion, toVersion, changes, Summary(registry, to, changedChunks));
    }

    /// <summary>
    /// Every field that differs, in SCHEMA order, rendered through each field's kind. A field absent on one
    /// side and present on the other is a difference, which is what makes an add report its whole field set.
    /// </summary>
    static IReadOnlyList<ContentDiffField> Fields(
        ContentTypeRegistration registration,
        ContentRow? before,
        ContentRow? after)
    {
        IReadOnlyList<ContentFieldEntry> schema = registration.Schema.Fields;
        var fields = new List<ContentDiffField>();
        for (int i = 0; i < schema.Count; i++)
        {
            string? rendered = Render(before, i);
            string? destination = Render(after, i);
            if (!string.Equals(rendered, destination, StringComparison.Ordinal))
            {
                fields.Add(new ContentDiffField(schema[i].Name, rendered, destination));
            }
        }

        return fields;
    }

    static string? Render(ContentRow? row, int index)
        => row is null || index >= row.Fields.Count
            ? null
            : InMemoryContentAuditLog.Render(row.Fields[index]);

    static ContentDiffEntry Entry(
        ContentTypeRegistration registration,
        ContentRow row,
        ContentDiffOperation operation,
        IReadOnlyList<ContentDiffField> fields)
        => new(registration.Type, row.Id, row.Key, operation, fields);

    static void Touch(Dictionary<ushort, HashSet<int>> changed, ContentTypeRegistration registration, int id)
    {
        if (!changed.TryGetValue(registration.Type.Value, out HashSet<int>? chunks))
        {
            chunks = [];
            changed[registration.Type.Value] = chunks;
        }

        chunks.Add(id / registration.ChunkSlots);
    }

    /// <summary>
    /// The per-type chunk counts, over the DESTINATION's rows, so "1 of 13" is read against the version the
    /// operator would be publishing rather than against the one they are leaving.
    /// </summary>
    static IReadOnlyList<ContentChunkSummaryEntry> Summary(
        ContentTypeRegistry registry,
        IReadOnlyList<ContentRowRevision> to,
        Dictionary<ushort, HashSet<int>> changedChunks)
    {
        var total = new Dictionary<ushort, HashSet<int>>();
        for (int i = 0; i < to.Count; i++)
        {
            ContentRow row = to[i].Row;
            ContentTypeRegistration registration = Require(registry, row.Type);
            if (!total.TryGetValue(row.Type.Value, out HashSet<int>? chunks))
            {
                chunks = [];
                total[row.Type.Value] = chunks;
            }

            chunks.Add(row.Id / registration.ChunkSlots);
        }

        var ordered = new List<ushort>(total.Keys);
        foreach (ushort typeId in changedChunks.Keys)
        {
            if (!total.ContainsKey(typeId))
            {
                ordered.Add(typeId);
            }
        }

        ordered.Sort();

        var summary = new List<ContentChunkSummaryEntry>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
        {
            ushort typeId = ordered[i];
            ContentTypeRegistration registration = Require(registry, new ContentTypeId(typeId));
            summary.Add(new ContentChunkSummaryEntry(
                registration.Type,
                registration.TypeKey,
                changedChunks.TryGetValue(typeId, out HashSet<int>? changed) ? changed.Count : 0,
                total.TryGetValue(typeId, out HashSet<int>? held) ? held.Count : 0));
        }

        return summary;
    }

    static Dictionary<(ushort Type, int Id), ContentRow> Index(IReadOnlyList<ContentRowRevision> revisions)
    {
        var index = new Dictionary<(ushort, int), ContentRow>(revisions.Count);
        for (int i = 0; i < revisions.Count; i++)
        {
            ContentRow row = revisions[i].Row;
            index[(row.Type.Value, row.Id)] = row;
        }

        return index;
    }

    static ContentTypeRegistration Require(ContentTypeRegistry registry, ContentTypeId type)
        => registry.TryGet(type, out ContentTypeRegistration? registration)
            ? registration
            : throw new ContentAuthoringException(
                FormattableString.Invariant(
                    $"A diff carries a row of content type {type.Value}, which this registry does not declare, so its field names and its chunk size are unknown."),
                type,
                0,
                ContentAuthoringException.UnknownTypeReason);
}
