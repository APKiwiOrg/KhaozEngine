namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The <c>food</c> content type: what eating an item restores, and how long it holds the next attack up.
/// </summary>
/// <remarks>
/// It declares nothing an item already carries. The engine's own <c>item</c> type owns the name, the examine
/// text, the value, the tags and the meshes, so this type is the EDIBLE facts alone and points at the item by
/// key reference rather than restating one of them.
/// <para>
/// Every field is required. There is no such thing as a food row that does not say what it heals, and the
/// absence convention writes the same zero form for an unset field and an authored zero, so the schema says
/// required and a validator refuses the zero.
/// </para>
/// </remarks>
public static class FoodContentType
{
    /// <summary>
    /// Id slots per chunk, the engine's floor. A chunk is the unit of re-download and a world has tens of
    /// foods rather than thousands, so the smallest legal chunk is the right one.
    /// </summary>
    public const int DefaultChunkSlots = ContentTypeRegistry.MinChunkSlots;

    /// <summary>The visibility the type registers under, which every field here inherits.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The item this row makes edible.</summary>
    public const string ItemField = "item";

    /// <summary>What one bite restores.</summary>
    public const string HealsField = "heals";

    /// <summary>The delay field's name under <see cref="ContentDurationUnit.Ticks"/>.</summary>
    public const string AttackDelayTicksField = "attack_delay_ticks";

    /// <summary>The delay field's name under <see cref="ContentDurationUnit.Seconds"/>.</summary>
    public const string AttackDelaySecondsField = "attack_delay_seconds";

    internal const int ItemIndex = 0;
    internal const int HealsIndex = 1;
    internal const int AttackDelayIndex = 2;
    internal const int FieldCount = 3;

    /// <summary>How long eating holds the next attack up, named for <paramref name="unit"/>.</summary>
    public static string AttackDelayField(ContentDurationUnit unit)
        => ContentDurationNames.Pick(unit, AttackDelayTicksField, AttackDelaySecondsField);

    /// <summary>The ordered field list, which a row's values are parallel to BY INDEX.</summary>
    public static ContentFieldSchema CreateSchema(ContentDurationUnit unit) => new(
    [
        new ContentFieldEntry(
            ItemField,
            ContentFieldKind.KeyReference,
            EngineContentTypes.ItemTypeKey,
            ContentVisibility.Client,
            true),
        new ContentFieldEntry(HealsField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(AttackDelayField(unit), ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>The food row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the food schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
