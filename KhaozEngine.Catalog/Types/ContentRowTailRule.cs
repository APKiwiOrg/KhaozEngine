using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// The engine's schema evolution rule, in one place: what a type's
/// <see cref="ContentFieldSchema.BaselineFieldCount"/> means to the bytes of a row.
/// <para>
/// <b>A type's field list may gain OPTIONAL fields at the END and nothing else.</b> A row body is a
/// positional walk with no presence bits, so inserting a field anywhere earlier moves every field after it
/// and repoints every published row. The baseline is the count the type first shipped with, and the two
/// halves of the rule below are what let one field list read and write two generations of bytes.
/// </para>
/// <para>
/// <b>ENCODE is canonical and SHORT.</b> Every baseline field is written, always, exactly as it was before
/// the type gained anything. An appended field is written only when it or a later appended field carries a
/// value, so a row that sets none of them encodes to the bytes the type's first release produced, BYTE FOR
/// BYTE. That is what makes a republish of an untouched row reproduce its old chunk hash, and it is what
/// keeps <c>ContentPackRebuild</c> able to reproduce a version published before the append.
/// </para>
/// <para>
/// <b>DECODE may end at the baseline boundary and never inside it.</b> A body that runs out at or after
/// <see cref="ContentFieldSchema.BaselineFieldCount"/> is an older row and the rest of the fields come back
/// absent. A body that runs out INSIDE the baseline is refused exactly as it always was, including at the
/// boundary of an optional baseline field, because no release ever wrote such a body and accepting one would
/// turn a corrupt row that happens to end on a field edge into a short row.
/// </para>
/// <para>
/// <b>The explicit zero form of a trailing appended field still decodes.</b> The encoder never writes it, but
/// reading it costs nothing: it is an ordinary field read that lands on the zero form and comes back absent,
/// and the walk needs no branch for it. Accepting it means a row hand assembled the long way, or written by
/// an engine build between the append and this rule, is read rather than refused.
/// </para>
/// </summary>
static class ContentRowTailRule
{
    /// <summary>
    /// How many of the schema's fields a row's bytes carry: every baseline field, then the appended fields up
    /// to and including the LAST one that carries a value.
    /// <para>
    /// A derived marker writes no bytes at all, so one sitting past the last valued field is dropped with the
    /// rest of the tail and one sitting before it costs nothing to keep. Either way the bytes are the same,
    /// which is why the scan looks only at the values.
    /// </para>
    /// </summary>
    /// <param name="schema">The type's field list and its baseline.</param>
    /// <param name="values">The row's values, parallel to the schema by index.</param>
    public static int WrittenFieldCount(ContentFieldSchema schema, IReadOnlyList<ContentFieldValue> values)
    {
        int baseline = schema.BaselineFieldCount;
        for (int i = schema.Fields.Count - 1; i >= baseline; i--)
        {
            if (i < values.Count && !values[i].IsAbsent)
            {
                return i + 1;
            }
        }

        return baseline;
    }

    /// <summary>
    /// True when a body is allowed to END with field <paramref name="index"/> unread, which is every index at
    /// or after the baseline. The fields from there on are optional by construction, so the caller fills them
    /// absent and has nothing to refuse.
    /// </summary>
    /// <param name="schema">The type's field list and its baseline.</param>
    /// <param name="index">The field the body ran out before.</param>
    public static bool MayEndAt(ContentFieldSchema schema, int index) => index >= schema.BaselineFieldCount;
}
