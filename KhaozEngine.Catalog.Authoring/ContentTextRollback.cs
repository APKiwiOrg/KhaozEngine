using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The TEXT half of a rollback: the complete value changes that make every string of a row live at the target
/// version read exactly as it did there, staged as ordinary text intents against the current version.
/// <para>
/// <b>Values only, never languages or retirement.</b> A string that differs is Set to the target's value, and
/// a string the target did not hold is Removed. Every language the current version declares stays declared,
/// including one the target never had, so a removed last value publishes that language's empty chunk. A
/// retired row's strings roll back like any other, and nothing here can unretire a row.
/// </para>
/// <para>
/// <b>A row introduced after the target keeps its text</b>, as it keeps its id and its values in the row
/// rollback, because the target says nothing about a row it did not hold.
/// </para>
/// </summary>
internal static class ContentTextRollback
{
    /// <summary>The text intents restoring the target's values, ordered by type, id, field and language.</summary>
    /// <param name="target">The target version's complete text.</param>
    /// <param name="current">The current version's complete text.</param>
    /// <param name="targetRows">Every row live at the target version.</param>
    /// <param name="currentRows">Every row live at the current version, which supplies each row's key.</param>
    public static IReadOnlyList<ContentTextEdit> Changes(
        ContentVersionTextSnapshot target,
        ContentVersionTextSnapshot current,
        IReadOnlyList<ContentRowRevision> targetRows,
        IReadOnlyList<ContentRowRevision> currentRows)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(targetRows);
        ArgumentNullException.ThrowIfNull(currentRows);

        var keys = new Dictionary<(ushort Type, int Id), ContentKey>(currentRows.Count);
        foreach (ContentRowRevision row in currentRows)
        {
            keys[(row.Row.Type.Value, row.Row.Id)] = row.Row.Key;
        }

        var heldAtTarget = new HashSet<(ushort Type, int Id)>();
        foreach (ContentRowRevision row in targetRows)
        {
            heldAtTarget.Add((row.Row.Type.Value, row.Row.Id));
        }

        var wanted = new Dictionary<StringId, string>(target.Revisions.Count);
        foreach (ContentTextRevision revision in target.Revisions)
        {
            wanted[StringId.Of(revision)] = revision.Value;
        }

        var now = new Dictionary<StringId, string>(current.Revisions.Count);
        foreach (ContentTextRevision revision in current.Revisions)
        {
            now[StringId.Of(revision)] = revision.Value;
        }

        var changes = new List<(StringId Id, string? Value)>();
        foreach (KeyValuePair<StringId, string> was in wanted)
        {
            if (!now.TryGetValue(was.Key, out string? held) || !string.Equals(held, was.Value, StringComparison.Ordinal))
            {
                changes.Add((was.Key, was.Value));
            }
        }

        foreach (StringId held in now.Keys)
        {
            if (!wanted.ContainsKey(held) && heldAtTarget.Contains((held.Type, held.Id)))
            {
                changes.Add((held, null));
            }
        }

        changes.Sort(static (left, right) => StringId.Compare(left.Id, right.Id));
        var edits = new List<ContentTextEdit>(changes.Count);
        foreach ((StringId id, string? value) in changes)
        {
            if (!keys.TryGetValue((id.Type, id.Id), out ContentKey key))
            {
                // A definition is never deleted, so a row holding text at either version is live now. A store
                // that answers otherwise has lost a row, which a rollback cannot repair.
                continue;
            }

            var textTarget = new ContentTextTarget(new ContentTypeId(id.Type), key, id.Field, id.Language);
            edits.Add(value is null ? ContentTextEdit.Remove(textTarget) : ContentTextEdit.Set(textTarget, value));
        }

        return edits;
    }

    /// <summary>One string by type, definition id, marker field and canonical language.</summary>
    readonly record struct StringId(ushort Type, int Id, string Field, string Language)
    {
        public static StringId Of(ContentTextRevision revision)
            => new(revision.Type.Value, revision.DefinitionId, revision.FieldName, revision.Language);

        /// <summary>Type, id, field and language, ordinally.</summary>
        public static int Compare(StringId left, StringId right)
        {
            int order = left.Type.CompareTo(right.Type);
            if (order == 0)
            {
                order = left.Id.CompareTo(right.Id);
            }

            if (order == 0)
            {
                order = string.CompareOrdinal(left.Field, right.Field);
            }

            return order != 0 ? order : string.CompareOrdinal(left.Language, right.Language);
        }
    }
}
