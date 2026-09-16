using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The <c>stat_line</c> content type of spec 8.4, one stat a tier grants with its range. Child of
/// <c>mod_tier</c>, type id <see cref="InstanceContentTypeIds.StatLineTypeId"/>. A line is what turns a
/// stored roll position into a number the evaluator folds.
/// <para>
/// <b>One roll position drives every line on the tier.</b> A tier with two lines, an added damage pair for
/// instance, resolves both from the SAME stored position, so the two move together and the payload stores
/// one position for the affix rather than one per line. <see cref="SortField"/> is what makes "the tier's
/// second line" a stable phrase across a republish.
/// </para>
/// <para>
/// Whether a line's units are scaled units or basis points is decided by <see cref="CombineField"/> and by
/// nothing else: <see cref="CombineFlat"/> is in the stat's scaled units, <see cref="CombineIncreased"/>
/// and <see cref="CombineMore"/> are in basis points where 10,000 is 100 percent. There is no float
/// anywhere on this path.
/// </para>
/// <para>
/// <see cref="TagScopeField"/> is the ONE multi-valued field in any type of this band, and it is the
/// contracts' own tag list kind rather than an ad hoc list: it renders in a generic editor, it diffs
/// element by element in the publish diff, and it has a declared reference target. An empty tag scope
/// means the stat itself. <see cref="ConditionIdField"/> of 0 means unconditional, which an absent field
/// reads back as.
/// </para>
/// <para>
/// The <see cref="CombineField"/> domain below is the only rule this type checks. The CROSS-ROW rules the
/// validator carries are spec 8.9 check 4 exactly: a parent that resolves (<c>KEC0100</c>), and a
/// <see cref="StatIdField"/> that resolves, a <see cref="CombineField"/> of 1, 2 or 3 and
/// <see cref="MinField"/> at or below <see cref="MaxField"/> (<c>KEC0104</c>).
/// </para>
/// <para>
/// <b>Spec 8.4's further sentence, refusing <see cref="MinField"/> equal to <see cref="MaxField"/> on a
/// tier whose only line it is when the mod's kind is a ROLLED kind, is NOT checked and has no code.</b>
/// Nothing the rows carry says which kinds are rolled: spec 8.2 assigns 1 prefix and 2 suffix and leaves 3
/// to 255 to the game, and it says the generator treats every kind above 2 as its own counted pool, so
/// every kind is rolled. What actually distinguishes a unique's guaranteed line is that its mod carries no
/// <c>mod_tier_weight</c> row (spec 8.6), which is a different statement and is already
/// <c>KEC0107</c>. Reading the sentence off the weight rows instead would be a rule spec 8.4 does not
/// state, so it is recorded as
/// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/967">967</see> rather than invented here.
/// </para>
/// </summary>
public static class StatLineContentType
{
    /// <summary>Id slots per chunk, spec 8.1's table, sized for a child that outnumbers its parent.</summary>
    public const int DefaultChunkSlots = 32768;

    /// <summary>
    /// This type's own row cap, which its slot count requires: 32,768 slots at the 1,024 byte default
    /// would build a chunk past the uncompressed ceiling no reader loads. A stat line is the widest row in
    /// the family because of its tag scope, and 384 bytes holds a scope far past any authored one.
    /// </summary>
    public const int MaxRowBytes = 384;

    /// <summary>The type level visibility, spec 8.1. A tooltip computes a line's range on both sides.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>A flat addition, in the stat's own scaled units.</summary>
    public const int CombineFlat = 1;

    /// <summary>An additive percentage, in basis points where 10,000 is 100 percent.</summary>
    public const int CombineIncreased = 2;

    /// <summary>A multiplicative percentage, in basis points where 10,000 is 100 percent.</summary>
    public const int CombineMore = 3;

    /// <summary>The tier this line belongs to.</summary>
    public const string ModTierIdField = "mod_tier_id";

    /// <summary>The authored order, which is what makes "the tier's second line" a stable phrase.</summary>
    public const string SortField = "sort";

    /// <summary>The stat this line grants.</summary>
    public const string StatIdField = "stat_id";

    /// <summary>How the line folds, one of the three combine kinds.</summary>
    public const string CombineField = "combine";

    /// <summary>The inclusive range floor, in the units <see cref="CombineField"/> decides.</summary>
    public const string MinField = "min";

    /// <summary>The inclusive range ceiling, in the units <see cref="CombineField"/> decides.</summary>
    public const string MaxField = "max";

    /// <summary>The tags the line applies to, in authored order. Empty means the stat itself.</summary>
    public const string TagScopeField = "tag_scope";

    /// <summary>The condition the line is gated on, or absent for unconditional.</summary>
    public const string ConditionIdField = "condition_id";

    /// <summary>The ordered field list, spec 8.4's table exactly.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(
            ModTierIdField,
            ContentFieldKind.KeyReference,
            InstanceContentTypeIds.ModTierTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(SortField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(
            StatIdField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.StatTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(CombineField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(MinField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(MaxField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(
            TagScopeField,
            ContentFieldKind.TagList,
            ContentFieldEntry.TagReferenceTarget,
            ContentVisibility.Client,
            false),
        new ContentFieldEntry(ConditionIdField, ContentFieldKind.Int, null, ContentVisibility.Client, false),
    ]);

    /// <summary>
    /// The stat line row codec. It adds the one IN-ROW domain the generic walk cannot express, checked on
    /// BOTH sides so an encoder cannot write a row its own decoder refuses.
    /// </summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the stat line schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }

        /// <inheritdoc />
        protected override string? CheckFieldValue(int fieldIndex, ContentFieldEntry field, in ContentFieldValue value)
            => field.Name switch
            {
                CombineField when value.Number is not (CombineFlat or CombineIncreased or CombineMore)
                    => ReasonFieldMalformed,
                _ => null,
            };
    }
}
