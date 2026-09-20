using System;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The <c>recipe</c> content type: one processing step, the skill it pays, the station it happens at, and
/// what it costs in time and pays in experience.
/// </summary>
/// <remarks>
/// <b>Recipe ids are APPEND ONLY.</b> A recipe id is the natural key for a per-character memory of what
/// somebody last made, so the number a row carries is durable in exactly the way an item id is: a new recipe
/// appends, and an existing one is never renumbered or reused.
/// <para>
/// The inputs and the outputs are NOT fields here: a repeating child structure is its own content type with
/// a key reference back to its parent, which is what <c>recipe_input</c> and <c>recipe_output</c> are, so the
/// generic editor, the field-level audit and the publish diff all see them.
/// </para>
/// <para>
/// <see cref="SkillField"/>, <see cref="StationField"/> and <see cref="RepeatModeField"/> each carry a raw
/// durable number this package gives no meaning to. What a station is and what a repeat mode does are the
/// game's, and the numbers are stored rather than derived from a declaration order that could move.
/// </para>
/// </remarks>
public static class RecipeContentType
{
    /// <summary>
    /// Id slots per chunk, sized for a type that grows a row per material step. The append-only rule means
    /// the count only ever goes up.
    /// </summary>
    public const int DefaultChunkSlots = 512;

    /// <summary>The visibility the type registers under, which every field here inherits.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The list position a recipe panel draws this row at, unique across live rows.</summary>
    public const string DisplayOrderField = "display_order";

    /// <summary>The skill a completion pays, as the game's own durable number.</summary>
    public const string SkillField = "skill";

    /// <summary>The level of <see cref="SkillField"/> a character needs before the row is workable.</summary>
    public const string LevelRequiredField = "level_required";

    /// <summary>The item the row is listed under.</summary>
    public const string PrimaryItemField = "primary_item";

    /// <summary>The station the step happens at, as the game's own durable number.</summary>
    public const string StationField = "station";

    /// <summary>The duration field's name under <see cref="ContentDurationUnit.Ticks"/>.</summary>
    public const string BaseTicksField = "base_ticks";

    /// <summary>The duration field's name under <see cref="ContentDurationUnit.Seconds"/>.</summary>
    public const string BaseSecondsField = "base_seconds";

    /// <summary>Experience paid per PRODUCT ITEM, so a row that makes two pays twice this.</summary>
    public const string XpPerItemField = "xp_per_item";

    /// <summary>Whether a completion starts the next one, as the game's own durable number.</summary>
    public const string RepeatModeField = "repeat_mode";

    internal const int DisplayOrderIndex = 0;
    internal const int SkillIndex = 1;
    internal const int LevelRequiredIndex = 2;
    internal const int PrimaryItemIndex = 3;
    internal const int StationIndex = 4;
    internal const int BaseDurationIndex = 5;
    internal const int XpPerItemIndex = 6;
    internal const int RepeatModeIndex = 7;
    internal const int FieldCount = 8;

    /// <summary>
    /// How long one completion takes before any tool or bonus is applied, named for
    /// <paramref name="unit"/>.
    /// </summary>
    public static string BaseDurationField(ContentDurationUnit unit)
        => ContentDurationNames.Pick(unit, BaseTicksField, BaseSecondsField);

    /// <summary>The ordered field list, which a row's values are parallel to BY INDEX.</summary>
    public static ContentFieldSchema CreateSchema(ContentDurationUnit unit) => new(
    [
        new ContentFieldEntry(DisplayOrderField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(SkillField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(LevelRequiredField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(
            PrimaryItemField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.ItemTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(StationField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(BaseDurationField(unit), ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(XpPerItemField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(RepeatModeField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>
    /// Registers the type on <paramref name="registry"/>, in the game band, under the id and key
    /// <see cref="GameContentTypeIds"/> declares for it and this type's own visibility and chunk slots.
    /// </summary>
    /// <remarks>
    /// The visibility and the chunk slot count are the TYPE's rather than a caller's: a wrong visibility
    /// moves rows between the two manifests and a wrong slot count moves every content address, and neither
    /// fails loudly.
    /// </remarks>
    /// <param name="registry">A registry that is not frozen and carries neither this id nor this key.</param>
    /// <param name="unit">The game's own time unit, which picks the duration field's name.</param>
    /// <param name="validator">
    /// The type's own validator, or null for none. This package ships NO validators yet, so a game either
    /// passes one of its own or passes null. The package's own arrive separately.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    /// <exception cref="ContentRegistrationException">
    /// The registry is frozen, or already carries this id or this key.
    /// </exception>
    public static void Register(
        ContentTypeRegistry registry,
        ContentDurationUnit unit,
        IContentValidator? validator)
    {
        ArgumentNullException.ThrowIfNull(registry);

        ContentFieldSchema schema = CreateSchema(unit);
        registry.RegisterContentType(
            ContentRegistrationBand.Game,
            GameContentTypeIds.Recipe,
            GameContentTypeIds.RecipeKey,
            new Codec(new ContentTypeId(GameContentTypeIds.Recipe), schema),
            validator,
            schema,
            DefaultVisibility,
            DefaultChunkSlots);
    }

    /// <summary>The recipe row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the recipe schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
