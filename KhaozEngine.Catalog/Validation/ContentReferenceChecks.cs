using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// Pass 3 of the sweep, REFERENCES (spec 5.3): key references, tag lists and the loot graph. It needs pass
/// 1 to have walked the live rows, which is why it is third rather than first.
/// <para>
/// Only LIVE rows are followed. A retired row keeps its bytes forever so a stored stack still decodes, and
/// the content it pointed at is usually retired beside it, so following one would report a defect nobody
/// can fix and nobody should.
/// </para>
/// <para>
/// A reference of 0 is NO CONTENT rather than a dangling id (contracts 5.1), so it is skipped here. A
/// required field left at 0 is the schema pass's <c>KEC0005</c>.
/// </para>
/// </summary>
internal static class ContentReferenceChecks
{
    internal static void Run(ContentValidationRun run)
    {
        foreach (ContentTypeRegistration registration in run.Registry.ByTypeId)
        {
            foreach (ContentRow row in run.Candidate.Rows(registration.Type))
            {
                if (row.IsRetired)
                {
                    continue;
                }

                CheckRowReferences(run, registration, row);
            }
        }

        CheckLootEntryShape(run);
        CheckLootGraph(run);
    }

    /// <summary>
    /// <c>KEC0007</c> on a reference target that was never registered, <c>KEC0006</c> on one that names a
    /// row which is not live at this version, and <c>KEC0008</c> on a tag list entry that is not a live tag.
    /// </summary>
    static void CheckRowReferences(ContentValidationRun run, ContentTypeRegistration registration, ContentRow row)
    {
        IReadOnlyList<ContentFieldEntry> fields = registration.Schema.Fields;
        for (int i = 0; i < fields.Count; i++)
        {
            ContentFieldEntry field = fields[i];
            if (field.Kind is not (ContentFieldKind.KeyReference or ContentFieldKind.TagList))
            {
                continue;
            }

            ContentFieldValue value = ContentValidationRun.Value(row, i, field.Kind);
            if (value.IsAbsent)
            {
                continue;
            }

            string target = field.Kind == ContentFieldKind.TagList
                ? EngineContentTypes.TagTypeKey
                : field.ReferenceTarget ?? string.Empty;

            if (field.Kind == ContentFieldKind.KeyReference && value.Number == 0)
            {
                continue;
            }

            if (!run.Registry.TryGetByKey(target, out ContentTypeRegistration? targetType))
            {
                run.Add(
                    registration.Type,
                    row.Id,
                    "KEC0007",
                    FormattableString.Invariant(
                        $"Row {row.Id} of type '{registration.TypeKey}' carries a value for '{field.Name}', which points at content type '{target}'. No type is registered under that key, so the field has to be 0 on every row until a game registers one."));
                continue;
            }

            if (field.Kind == ContentFieldKind.KeyReference)
            {
                CheckOneReference(run, registration, row, field, targetType, (int)value.Number);
                continue;
            }

            CheckTagList(run, registration, row, field, targetType, value.Bytes.Span);
        }
    }

    /// <summary><c>KEC0006</c>: the referenced row is absent from this version or is retired in it.</summary>
    static void CheckOneReference(
        ContentValidationRun run,
        ContentTypeRegistration registration,
        ContentRow row,
        ContentFieldEntry field,
        ContentTypeRegistration targetType,
        int id)
    {
        if (run.Candidate.TryGetRow(targetType.Type, id, out _) && !run.Candidate.IsRetired(targetType.Type, id))
        {
            return;
        }

        run.Add(
            registration.Type,
            row.Id,
            "KEC0006",
            FormattableString.Invariant(
                $"Row {row.Id} of type '{registration.TypeKey}' points '{field.Name}' at {targetType.TypeKey} {id}, which is not a live row of this version."));
    }

    /// <summary>
    /// <c>KEC0008</c>: a tag list entry that is not a live tag row. The list is varint ids in AUTHORED
    /// order, and a list this walk cannot read is a codec defect the codec pass reports, so it stops here
    /// rather than guessing.
    /// </summary>
    static void CheckTagList(
        ContentValidationRun run,
        ContentTypeRegistration registration,
        ContentRow row,
        ContentFieldEntry field,
        ContentTypeRegistration tagType,
        ReadOnlySpan<byte> bytes)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (!ContentVarint.TryRead(bytes, ref offset, out uint raw, out _))
            {
                return;
            }

            int id = unchecked((int)raw);
            if (run.Candidate.TryGetRow(tagType.Type, id, out _) && !run.Candidate.IsRetired(tagType.Type, id))
            {
                continue;
            }

            run.Add(
                registration.Type,
                row.Id,
                "KEC0008",
                FormattableString.Invariant(
                    $"Row {row.Id} of type '{registration.TypeKey}' lists tag {id} in '{field.Name}', which is not a live tag row. A tag is content rather than a string, so a list entry names a row."));
        }
    }

    /// <summary>
    /// <c>KEC0023</c>: a loot entry names its draw exactly ONE of three ways, an item, a nested table, or a
    /// non-empty tag filter. Two of them is an author writing down two different things, and a precedence
    /// order would be the engine silently choosing between them.
    /// </summary>
    static void CheckLootEntryShape(ContentValidationRun run)
    {
        if (!run.TryGetEngineType(EngineContentTypes.LootEntryTypeKey, out ContentTypeRegistration? entryType))
        {
            return;
        }

        int itemIndex = ContentValidationRun.FieldIndex(entryType.Schema, LootEntryContentType.ItemField);
        int nestedIndex = ContentValidationRun.FieldIndex(entryType.Schema, LootEntryContentType.NestedTableField);
        int tagsIndex = ContentValidationRun.FieldIndex(entryType.Schema, LootEntryContentType.RequiredTagsField);

        foreach (ContentRow row in run.Candidate.Rows(entryType.Type))
        {
            if (row.IsRetired)
            {
                continue;
            }

            int ways = 0;
            if (ContentValidationRun.Value(row, itemIndex, ContentFieldKind.KeyReference).Number != 0)
            {
                ways++;
            }

            if (ContentValidationRun.Value(row, nestedIndex, ContentFieldKind.KeyReference).Number != 0)
            {
                ways++;
            }

            if (!ContentValidationRun.Value(row, tagsIndex, ContentFieldKind.TagList).Bytes.IsEmpty)
            {
                ways++;
            }

            if (ways == 1)
            {
                continue;
            }

            run.Add(
                entryType.Type,
                row.Id,
                "KEC0023",
                FormattableString.Invariant(
                    $"Loot entry '{row.Key}' names its draw {ways} ways. It sets exactly one of item, nested_table and a non-empty required_tags."));
        }
    }

    /// <summary>
    /// <c>KEC0024</c>: a cycle through <c>nested_table</c>. The graph is loot tables, and an edge is a live
    /// entry whose parent table nests another. A cycle would make a roll recurse forever, and the roller is
    /// written against the acyclicity this check buys it.
    /// </summary>
    static void CheckLootGraph(ContentValidationRun run)
    {
        if (!run.TryGetEngineType(EngineContentTypes.LootEntryTypeKey, out ContentTypeRegistration? entryType)
            || !run.TryGetEngineType(EngineContentTypes.LootTableTypeKey, out ContentTypeRegistration? tableType))
        {
            return;
        }

        int tableIndex = ContentValidationRun.FieldIndex(entryType.Schema, LootEntryContentType.TableField);
        int nestedIndex = ContentValidationRun.FieldIndex(entryType.Schema, LootEntryContentType.NestedTableField);

        var edges = new Dictionary<int, List<int>>();
        foreach (ContentRow row in run.Candidate.Rows(entryType.Type))
        {
            if (row.IsRetired)
            {
                continue;
            }

            int from = (int)ContentValidationRun.Value(row, tableIndex, ContentFieldKind.KeyReference).Number;
            int to = (int)ContentValidationRun.Value(row, nestedIndex, ContentFieldKind.KeyReference).Number;
            if (from == 0 || to == 0)
            {
                continue;
            }

            if (!edges.TryGetValue(from, out List<int>? targets))
            {
                targets = [];
                edges.Add(from, targets);
            }

            targets.Add(to);
        }

        WalkAll(run, tableType, edges);
    }

    /// <summary>
    /// The depth-first walk behind <c>KEC0024</c>. A back edge into a table already OPEN on the stack is the
    /// cycle, and it is reported against the table the edge LEAVES, which is the row an author edits to
    /// break it.
    /// <para>
    /// The stack is explicit rather than the call stack, deliberately. A boot validates a pack that arrived
    /// over the network, so a chain a hundred thousand tables long has to come back as a finding rather than
    /// as a stack overflow nothing can catch.
    /// </para>
    /// </summary>
    static void WalkAll(
        ContentValidationRun run,
        ContentTypeRegistration tableType,
        Dictionary<int, List<int>> edges)
    {
        const int Open = 1;
        const int Done = 2;

        var colour = new Dictionary<int, int>(edges.Count);
        var stack = new Stack<(int Table, int Next)>();

        foreach (int root in edges.Keys)
        {
            if (colour.ContainsKey(root))
            {
                continue;
            }

            colour[root] = Open;
            stack.Push((root, 0));

            while (stack.Count > 0)
            {
                (int table, int next) = stack.Pop();
                if (!edges.TryGetValue(table, out List<int>? targets) || next >= targets.Count)
                {
                    colour[table] = Done;
                    continue;
                }

                stack.Push((table, next + 1));
                int child = targets[next];
                colour.TryGetValue(child, out int seen);
                if (seen == Done)
                {
                    continue;
                }

                if (seen == Open)
                {
                    run.Add(
                        tableType.Type,
                        table,
                        "KEC0024",
                        FormattableString.Invariant(
                            $"Loot table {table} nests {child}, which closes a cycle. A roll walks nested tables, so a cycle is a roll that never ends."));
                    continue;
                }

                colour[child] = Open;
                stack.Push((child, 0));
            }
        }
    }
}
