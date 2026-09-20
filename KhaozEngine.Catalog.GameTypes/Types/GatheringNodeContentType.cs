namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The <c>gathering_node</c> content type: one standing source a character works, and what a swing at it
/// pays.
/// </summary>
/// <remarks>
/// <b>Every kind of node is ONE type.</b> What separates one loop from another is
/// <see cref="ToolFamilyField"/>, the ranked tool family a swing selects from, so a second content type here
/// would be a copy of this one with different field names and a third family would be a third copy.
/// <para>
/// <c>Client</c> at the type level. A client draws the progress bar, the level gate and the swing cadence
/// before the server is asked anything, so a server-only chance would mean a round trip per swing.
/// </para>
/// <para>
/// Every chance is basis points, so nothing in the gathering path is ever a float. A ceiling the summed
/// chance is held under is a global knob rather than a per-row one and belongs on <c>game_tuning</c>.
/// </para>
/// </remarks>
public static class GatheringNodeContentType
{
    /// <summary>Id slots per chunk, the engine's floor. One row per node kind is a small type.</summary>
    public const int DefaultChunkSlots = ContentTypeRegistry.MinChunkSlots;

    /// <summary>The visibility the type registers under, which every field here inherits.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The skill a successful swing pays, as the game's own durable number.</summary>
    public const string SkillField = "skill";

    /// <summary>The tag naming the ranked tool family that works this node.</summary>
    public const string ToolFamilyField = "tool_family";

    /// <summary>The level the character needs before a swing starts at all.</summary>
    public const string LevelRequiredField = "level_required";

    /// <summary>What one swing pays in basis points, quoted at <see cref="LevelRequiredField"/>.</summary>
    public const string BaseChanceBasisPointsField = "base_chance_bp";

    /// <summary>Lives a standing node holds, SHARED by everyone working it.</summary>
    public const string LivesField = "lives";

    /// <summary>The chance in basis points that one yield takes one of those lives.</summary>
    public const string LifeLossBasisPointsField = "life_loss_bp";

    /// <summary>Experience one yield pays.</summary>
    public const string YieldXpField = "yield_xp";

    /// <summary>The item one yield lands in the bag as.</summary>
    public const string YieldItemField = "yield_item";

    /// <summary>The respawn field's name under <see cref="ContentDurationUnit.Ticks"/>.</summary>
    public const string RespawnTicksField = "respawn_ticks";

    /// <summary>The respawn field's name under <see cref="ContentDurationUnit.Seconds"/>.</summary>
    public const string RespawnSecondsField = "respawn_seconds";

    internal const int SkillIndex = 0;
    internal const int ToolFamilyIndex = 1;
    internal const int LevelRequiredIndex = 2;
    internal const int BaseChanceBasisPointsIndex = 3;
    internal const int LivesIndex = 4;
    internal const int LifeLossBasisPointsIndex = 5;
    internal const int YieldXpIndex = 6;
    internal const int YieldItemIndex = 7;
    internal const int RespawnIndex = 8;
    internal const int FieldCount = 9;

    /// <summary>
    /// How long a spent node lies spent before it comes back with a full set of lives, named for
    /// <paramref name="unit"/>.
    /// </summary>
    public static string RespawnField(ContentDurationUnit unit)
        => ContentDurationNames.Pick(unit, RespawnTicksField, RespawnSecondsField);

    /// <summary>The ordered field list, which a row's values are parallel to BY INDEX.</summary>
    public static ContentFieldSchema CreateSchema(ContentDurationUnit unit) => new(
    [
        new ContentFieldEntry(SkillField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(
            ToolFamilyField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.TagTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(LevelRequiredField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(BaseChanceBasisPointsField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(LivesField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(LifeLossBasisPointsField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(YieldXpField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(
            YieldItemField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.ItemTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(RespawnField(unit), ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>The node row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the gathering node schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
