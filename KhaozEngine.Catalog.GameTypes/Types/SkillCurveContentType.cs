namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The <c>skill_curve</c> content type: ONE ROW PER SKILL, carrying that skill's own knobs.
/// </summary>
/// <remarks>
/// A row per SKILL rather than a column per skill on one row, because the alternative grows the schema every
/// time a skill gains a knob and leaves every other skill carrying an absent field for it. A knob that is not
/// any one skill's belongs on <c>game_tuning</c>.
/// <para>
/// A skill with no knob worth tuning authors a row carrying nothing but its own number, or no row at all,
/// which are the same thing to a reader. That is why <see cref="XpPerDamageField"/> is the ONE optional field
/// across all thirteen types: it is a knob most skills have no use for, and an absent value has to be
/// distinguishable from an authored zero.
/// </para>
/// <para>
/// <see cref="SkillField"/> carries the game's own durable skill number rather than a name. A committed
/// progression record carries the raw number, so it is the one spelling of a skill that cannot drift.
/// </para>
/// </remarks>
public static class SkillCurveContentType
{
    /// <summary>Id slots per chunk, the engine's floor. One row per skill is tens of rows.</summary>
    public const int DefaultChunkSlots = ContentTypeRegistry.MinChunkSlots;

    /// <summary>The visibility the type registers under, which every field here inherits.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The skill this row's knobs belong to, as the game's own durable number.</summary>
    public const string SkillField = "skill";

    /// <summary>Experience paid per point of damage dealt. OPTIONAL: most skills carry no such rate.</summary>
    public const string XpPerDamageField = "xp_per_damage";

    internal const int SkillIndex = 0;
    internal const int XpPerDamageIndex = 1;
    internal const int FieldCount = 2;

    /// <summary>The ordered field list, which a row's values are parallel to BY INDEX.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(SkillField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(XpPerDamageField, ContentFieldKind.Int, null, ContentVisibility.Client, false),
    ]);

    /// <summary>The curve row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the skill curve schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
