using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>One string's identity at a definition id: type, id, marker field and canonical language.</summary>
/// <param name="Type">The content type id.</param>
/// <param name="DefinitionId">The row's final definition id.</param>
/// <param name="FieldName">The localized text marker field.</param>
/// <param name="Language">The canonical language.</param>
internal readonly record struct ContentTextSlot(ushort Type, int DefinitionId, string FieldName, string Language)
{
    /// <summary>The slot a revision occupies.</summary>
    public static ContentTextSlot Of(ContentTextRevision revision)
        => new(revision.Type.Value, revision.DefinitionId, revision.FieldName, revision.Language);
}

/// <summary>
/// The ONE definition of the live text a publish must produce: the baseline's live values, then each fork
/// copy of its source row's baseline values at the copy's final id, then every frozen Set and Remove in
/// order, each bound to its row's final id through the row plan's live rows. The text plan proves its
/// candidate equals this set exactly, and the candidate builder produces it, so a candidate that drops,
/// omits or changes a string no intent names cannot pass for a complete one.
/// <para>
/// A copy is applied BEFORE the explicit edits, so an edit on the original key changes the original alone
/// and an edit on the legacy key overrides the copied value. Ids come from the final row plan and never from
/// an allocator branch, so a family fork binds wherever its family allocated.
/// </para>
/// </summary>
internal static class ContentTextExpectedValues
{
    /// <summary>The exact live values the new version holds.</summary>
    /// <param name="baseline">The baseline version's live revisions.</param>
    /// <param name="frozenRows">The frozen row edits, whose forks owe copies.</param>
    /// <param name="frozenText">The frozen text intents.</param>
    /// <param name="liveRows">Every row live at the new version, ids final.</param>
    /// <exception cref="ContentAuthoringException">A fork copy or a text target names no single live row.</exception>
    public static Dictionary<ContentTextSlot, string> Compute(
        IReadOnlyList<ContentTextRevision> baseline,
        IReadOnlyList<ContentEdit> frozenRows,
        ContentDraftTextState frozenText,
        IReadOnlyList<ContentRowRevision> liveRows)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(frozenRows);
        ArgumentNullException.ThrowIfNull(frozenText);
        ArgumentNullException.ThrowIfNull(liveRows);

        Dictionary<(ushort, ContentKey), int> ids = IdsByKey(liveRows);
        var expected = new Dictionary<ContentTextSlot, string>(baseline.Count);
        for (int i = 0; i < baseline.Count; i++)
        {
            expected[ContentTextSlot.Of(baseline[i])] = baseline[i].Value;
        }

        for (int i = 0; i < frozenRows.Count; i++)
        {
            ContentEdit edit = frozenRows[i];
            if (edit.Operation != ContentEditOperation.Fork)
            {
                continue;
            }

            int copy = RequireId(ids, edit.Type, edit.ForkKey);
            for (int b = 0; b < baseline.Count; b++)
            {
                ContentTextRevision source = baseline[b];
                if (source.Type == edit.Type && source.DefinitionId == edit.DefinitionId)
                {
                    expected[new ContentTextSlot(edit.Type.Value, copy, source.FieldName, source.Language)] = source.Value;
                }
            }
        }

        foreach (ContentTextEdit edit in frozenText.Edits)
        {
            ContentTextTarget target = edit.Target;
            var slot = new ContentTextSlot(
                target.Type.Value, RequireId(ids, target.Type, target.Key), target.FieldName, target.Language);
            if (edit.Operation == ContentTextEditOperation.Set)
            {
                expected[slot] = edit.Value!;
            }
            else
            {
                expected.Remove(slot);
            }
        }

        return expected;
    }

    /// <summary>
    /// The final id of every live row by type and key. A key two live rows share is ambiguous and maps to 0,
    /// which <see cref="RequireId"/> refuses.
    /// </summary>
    static Dictionary<(ushort, ContentKey), int> IdsByKey(IReadOnlyList<ContentRowRevision> liveRows)
    {
        var ids = new Dictionary<(ushort, ContentKey), int>(liveRows.Count);
        for (int i = 0; i < liveRows.Count; i++)
        {
            ContentRow row = liveRows[i].Row;
            (ushort, ContentKey) key = (row.Type.Value, row.Key);
            if (!ids.TryAdd(key, row.Id) && ids[key] != row.Id)
            {
                ids[key] = 0;
            }
        }

        return ids;
    }

    static int RequireId(Dictionary<(ushort, ContentKey), int> ids, ContentTypeId type, ContentKey key)
    {
        if (ids.TryGetValue((type.Value, key), out int id) && id != 0)
        {
            return id;
        }

        throw new ContentAuthoringException(
            FormattableString.Invariant(
                $"Text names type {type.Value} row '{key}', which the final row plan holds no single live row of, so the text has nowhere to land."),
            type,
            0,
            ContentAuthoringException.UnknownRowReason);
    }
}
