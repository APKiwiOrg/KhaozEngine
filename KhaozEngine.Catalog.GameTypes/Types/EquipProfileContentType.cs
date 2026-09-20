namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The <c>equip_profile</c> content type: where an item is worn, how it swings and how long a swing takes.
/// </summary>
/// <remarks>
/// It registers under <see cref="EngineContentTypes.EquipProfileTypeKey"/> rather than a key of a game's own
/// spelling. The engine's <c>item</c> type carries an <c>equip_profile</c> key reference that is LATE BOUND:
/// the engine writes the key down and registers no type under it, so a game supplies the schema the key
/// resolves to and the binding closes at registration. A type under any other key is one nothing points at,
/// and every item's <c>equip_profile</c> would have to stay 0.
/// <para>
/// <see cref="SlotField"/> and <see cref="WeaponArchetypeField"/> are DURABLE numbers and never indexes into
/// a list. A slot is a container index in stored player state, so a game reordering whatever it derives them
/// from would move every equipped item on every character.
/// </para>
/// <para>
/// The swing duration is per PROFILE rather than per archetype. Two profiles sharing an archetype share a
/// speed only because there is one number for the pair, and a third that wants its own has nowhere to say so.
/// </para>
/// </remarks>
public static class EquipProfileContentType
{
    /// <summary>Id slots per chunk, the engine's floor. A profile per equippable is tens of rows.</summary>
    public const int DefaultChunkSlots = ContentTypeRegistry.MinChunkSlots;

    /// <summary>The visibility the type registers under, which every field here inherits.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>The durable equip slot number.</summary>
    public const string SlotField = "slot";

    /// <summary>The durable weapon archetype number.</summary>
    public const string WeaponArchetypeField = "weapon_archetype";

    /// <summary>The swing field's name under <see cref="ContentDurationUnit.Ticks"/>.</summary>
    public const string AttackTicksField = "attack_ticks";

    /// <summary>The swing field's name under <see cref="ContentDurationUnit.Seconds"/>.</summary>
    public const string AttackSecondsField = "attack_seconds";

    internal const int SlotIndex = 0;
    internal const int WeaponArchetypeIndex = 1;
    internal const int AttackIndex = 2;
    internal const int FieldCount = 3;

    /// <summary>How long one swing takes for this profile, named for <paramref name="unit"/>.</summary>
    public static string AttackField(ContentDurationUnit unit)
        => ContentDurationNames.Pick(unit, AttackTicksField, AttackSecondsField);

    /// <summary>The ordered field list, which a row's values are parallel to BY INDEX.</summary>
    public static ContentFieldSchema CreateSchema(ContentDurationUnit unit) => new(
    [
        new ContentFieldEntry(SlotField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(WeaponArchetypeField, ContentFieldKind.Int, null, ContentVisibility.Client, true),
        new ContentFieldEntry(AttackField(unit), ContentFieldKind.Int, null, ContentVisibility.Client, true),
    ]);

    /// <summary>The equip profile row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the equip profile schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
