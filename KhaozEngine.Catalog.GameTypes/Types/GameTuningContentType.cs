using System;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The <c>game_tuning</c> content type: ONE ROW PER GLOBAL KNOB, keyed by the knob's own name.
/// </summary>
/// <remarks>
/// A row per knob rather than one wide row of named fields, because a wide row makes every new knob a schema
/// change, a restatement of the single authored row and a new column in the generic editor. A knob that is
/// not any one skill's lives here, and a knob that belongs to a skill belongs on <c>skill_curve</c>.
/// <para>
/// <b>One <see cref="ContentFieldKind.ScaledInt"/> at <see cref="ValueScale"/> holds every knob.</b> An
/// integer knob is a whole number at that scale and reads as one in a console, and a fractional one is the
/// hundredths it was always stored as. There is no float anywhere in content, so a second field kind for the
/// fractional knobs would be a second arithmetic rather than a convenience.
/// </para>
/// <para>
/// <b>The knob NAMES are the game's, not this package's.</b> A row key is the engine's key charset, lower
/// case letters, digits and underscore, and which knobs a world reads is a statement about that world's own
/// systems. The type declares the one field every knob shares and nothing about what any of them means.
/// </para>
/// </remarks>
public static class GameTuningContentType
{
    /// <summary>Id slots per chunk, the engine's floor. A tuning table is tens of rows.</summary>
    public const int DefaultChunkSlots = ContentTypeRegistry.MinChunkSlots;

    /// <summary>The visibility the type registers under, which every field here inherits.</summary>
    public const ContentVisibility DefaultVisibility = ContentVisibility.Client;

    /// <summary>
    /// The fixed scale every knob is stored at, so the row holds the value times this number. Hundredths,
    /// which is the finest a tuning knob is worth authoring to.
    /// </summary>
    public const int ValueScale = 100;

    /// <summary>The knob's value, at <see cref="ValueScale"/>.</summary>
    public const string ValueField = "value";

    internal const int ValueIndex = 0;
    internal const int FieldCount = 1;

    /// <summary>The ordered field list, which a row's values are parallel to BY INDEX.</summary>
    public static ContentFieldSchema CreateSchema() => new(
    [
        new ContentFieldEntry(ValueField, ContentFieldKind.ScaledInt, null, ContentVisibility.Client, true, ValueScale),
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
    /// <param name="validator">
    /// The type's own validator, or null for none. This package ships NONE for this type: it carries no
    /// rule a schema does not already make, so <see cref="GameContentTypes.Register"/> passes null here.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    /// <exception cref="ContentRegistrationException">
    /// The registry is frozen, or already carries this id or this key.
    /// </exception>
    public static void Register(ContentTypeRegistry registry, IContentValidator? validator)
    {
        ArgumentNullException.ThrowIfNull(registry);

        ContentFieldSchema schema = CreateSchema();
        registry.RegisterContentType(
            ContentRegistrationBand.Game,
            GameContentTypeIds.GameTuning,
            GameContentTypeIds.GameTuningKey,
            new Codec(new ContentTypeId(GameContentTypeIds.GameTuning), schema),
            validator,
            schema,
            DefaultVisibility,
            DefaultChunkSlots);
    }

    /// <summary>The tuning row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the game tuning schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }
}
