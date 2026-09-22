using System.Collections.Generic;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The ONE list operation an upgrade performs on a field a row already carries: append an element that is
/// not there yet, keeping everything that is.
/// <para>
/// It sits apart from <see cref="ContentUpgradePlanBuilder"/> because the list rule is not the value-patch
/// rule. A patch names the old shipped default it replaces, which is right for a scalar and wrong for a
/// list: a list an operator has extended equals no default this build ever shipped, so a patch would leave
/// that row alone forever. Identity here is the ELEMENT rather than the whole value.
/// </para>
/// <para>
/// The encoding is the engine's own, through <see cref="ContentRowCodecBase.TagListValue"/> and
/// <see cref="ContentRowCodecBase.ReadTagList"/>, so an appended list is the same bytes a codec writes for a
/// row authored with that list from the start. Nothing is sorted and nothing is deduplicated: the order is
/// the authored order of contracts 4.6, and every element an operator wrote keeps the position they gave it.
/// </para>
/// </summary>
static class ContentUpgradeTagList
{
    /// <summary>
    /// The value <paramref name="current"/> becomes with <paramref name="tagId"/> appended at the END, or
    /// false when the list already holds it, which is the SATISFIED answer that makes a re-run a no-op.
    /// </summary>
    /// <param name="current">The field's current value, which may be absent.</param>
    /// <param name="tagId">The element to append.</param>
    /// <param name="appended">The appended value, set only when this returns true.</param>
    internal static bool TryAppend(in ContentFieldValue current, int tagId, out ContentFieldValue appended)
    {
        List<int> ids = Read(in current);
        if (ids.Contains(tagId))
        {
            appended = default;
            return false;
        }

        ids.Add(tagId);
        appended = ContentRowCodecBase.TagListValue(ids);
        return true;
    }

    /// <summary>
    /// The ids a tag-list value holds, in authored order. The BYTE count is the ceiling on the id count,
    /// because every varint the codec writes is at least one byte, so a single read fills a destination that
    /// cannot turn out too small.
    /// </summary>
    /// <param name="value">The field's current value.</param>
    static List<int> Read(in ContentFieldValue value)
    {
        if (value.IsAbsent || value.Bytes.Length == 0)
        {
            return [];
        }

        int[] scratch = new int[value.Bytes.Length];
        int count = ContentRowCodecBase.ReadTagList(in value, scratch);
        var ids = new List<int>(count + 1);
        for (int i = 0; i < count; i++)
        {
            ids.Add(scratch[i]);
        }

        return ids;
    }
}
