namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// Reads one numeric field off a row BY POSITION, for the validators in this package.
/// </summary>
/// <remarks>
/// A row's values are parallel by index to its type's schema, but a row is DATA rather than a promise: a
/// hand-built or truncated one may carry fewer values than the schema declares, and an optional field may be
/// absent. A validator that indexed straight into <see cref="ContentRow.Fields"/> would throw on exactly the
/// malformed row it exists to report, and a validator that throws is reported as a defective validator
/// rather than as the content defect it found.
/// <para>
/// A missing or absent value reads as 0, which is the same number the absence convention writes for it, so a
/// field an author left empty and a field an author set to zero are refused by the same rule.
/// </para>
/// </remarks>
static class GameRowNumbers
{
    /// <summary>The number at <paramref name="index"/>, or 0 when the row carries nothing there.</summary>
    internal static long At(ContentRow row, int index)
        => index >= 0 && index < row.Fields.Count && !row.Fields[index].IsAbsent ? row.Fields[index].Number : 0;
}
