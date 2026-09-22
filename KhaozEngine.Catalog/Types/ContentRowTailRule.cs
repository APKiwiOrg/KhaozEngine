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
/// <b>The redundant long form is REFUSED, not tolerated.</b> A body whose last appended field is the zero form
/// is a second encoding of a row that already has one, and a content-addressed format cannot have two byte
/// strings for one row: the same rows would publish under two chunk hashes. The release that appends a field
/// is the first release that can write it, so no build has ever produced such a body, and refusing costs
/// nothing. It is <c>ContentRowCodecBase.ReasonFieldMalformed</c>, the same token a single trailing byte takes,
/// because it is the same defect: bytes the canonical form does not have.
/// </para>
/// </summary>
static class ContentRowTailRule
{
    /// <summary>
    /// How many of the schema's fields a row's bytes carry: every baseline field, then the appended fields up
    /// to and including the LAST one whose bytes are not the ZERO FORM.
    /// <para>
    /// <b>The scan asks <see cref="ContentFieldValue.IsZeroForm"/> and never
    /// <see cref="ContentFieldValue.IsAbsent"/>, and the difference is the whole correctness of this
    /// method.</b> Absence and zero are the same byte, and the decoder hands an optional field reading as zero
    /// back as absent. A scan on absence would keep a trailing explicit zero, write a field the decoder then
    /// reports absent, and produce a row whose re-encode is shorter than itself. In a content-addressed format
    /// that is the same rows under two chunk hashes.
    /// </para>
    /// <para>
    /// A derived marker writes no bytes at all and is always absent, so it is always zero form and can never
    /// extend the tail, which is exactly right: a body cannot carry bytes for it.
    /// </para>
    /// </summary>
    /// <param name="schema">The type's field list and its baseline.</param>
    /// <param name="values">The row's values, parallel to the schema by index.</param>
    public static int WrittenFieldCount(ContentFieldSchema schema, IReadOnlyList<ContentFieldValue> values)
    {
        int baseline = schema.BaselineFieldCount;
        for (int i = schema.Fields.Count - 1; i >= baseline; i--)
        {
            if (i < values.Count && !values[i].IsZeroForm)
            {
                return i + 1;
            }
        }

        return baseline;
    }

    /// <summary>
    /// True when a decoded body carried BYTES for an appended field the canonical short form leaves out, which
    /// is the redundant long form and is refused.
    /// <para>
    /// A derived marker in that range is skipped rather than counted, because it takes no width: a schema
    /// whose appended tail is a marker alone has one encoding, not two, and flagging it would refuse every row
    /// of that type.
    /// </para>
    /// </summary>
    /// <param name="schema">The type's field list and its baseline.</param>
    /// <param name="values">The values the walk decoded.</param>
    /// <param name="consumedThrough">How many schema fields the walk covered before the body ran out.</param>
    public static bool CarriesRedundantTail(
        ContentFieldSchema schema,
        IReadOnlyList<ContentFieldValue> values,
        int consumedThrough)
    {
        for (int i = WrittenFieldCount(schema, values); i < consumedThrough; i++)
        {
            if (!schema.Fields[i].IsDerivedMarker)
            {
                return true;
            }
        }

        return false;
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
