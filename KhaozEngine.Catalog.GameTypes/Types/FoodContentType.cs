using System;
using System.Collections.Generic;

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
        ContentDurationFields.Entry(unit, AttackDelayField(unit), ContentVisibility.Client, true),
    ]);

    /// <summary>
    /// Registers the type on <paramref name="registry"/>, in the game band, under the id and key
    /// <see cref="GameContentTypeIds"/> declares for it and this type's own visibility and chunk slots.
    /// </summary>
    /// <remarks>
    /// The visibility and the chunk slot count are the TYPE's rather than a caller's: a wrong visibility
    /// moves rows between the two manifests and a wrong slot count moves every content address, and neither
    /// fails loudly.
    /// <para>
    /// <b>This slot carries the CROSS-TYPE sweep as well as food's own rules.</b> Food holds the lowest game
    /// id, and the engine hands every per-type validator the whole candidate, so a whole-registry check
    /// mounted on all thirteen game types would report each cross-type defect thirteen times.
    /// <see cref="GameContentTypes.Register"/> passes a <see cref="GameContentChecks"/> here, which runs
    /// <see cref="Validator"/> first and then the sweep. A game registering by hand that passes
    /// <see cref="Validator"/> alone registers food's own rules and silently drops the sweep.
    /// </para>
    /// </remarks>
    /// <param name="registry">A registry that is not frozen and carries neither this id nor this key.</param>
    /// <param name="unit">The game's own time unit, which picks the duration field's name, kind and scale.</param>
    /// <param name="validator">
    /// The type's own validator, or null for none. <see cref="Validator"/> is the one this package ships
    /// for it, and a game passes that, one of its own, a wrapper over both, or null.
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
            GameContentTypeIds.Food,
            GameContentTypeIds.FoodKey,
            new Codec(new ContentTypeId(GameContentTypeIds.Food), schema),
            validator,
            schema,
            DefaultVisibility,
            DefaultChunkSlots);
    }

    /// <summary>The food row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the food schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }

    /// <summary>
    /// The three rules the schema alone cannot say: a heal above zero, a delay at or above zero, and one
    /// food row per item.
    /// </summary>
    /// <remarks>
    /// It takes NO options, because none of the three reads a number this package gives meaning to. It runs
    /// STANDALONE and it COMPOSES: the engine registry takes one validator per type, so a cross-type sweep
    /// mounted on this id wraps this one and forwards to it rather than replacing it, which is why the class
    /// holds no state of its own.
    /// <para>
    /// It ACCUMULATES. One pass reports every defect it finds instead of the earliest, so a bulk import is
    /// fixed in one round rather than one row at a time.
    /// </para>
    /// <para>
    /// The delay rule holds under either unit. The field is named for the game's own clock and the rule is
    /// about the stored integer's SIGN. Under <see cref="ContentDurationUnit.Seconds"/> that integer is
    /// hundredths of a second, and a positive scale never moves a sign, so the rule refuses the same numbers
    /// under either spelling and reads the position rather than a name.
    /// </para>
    /// </remarks>
    public sealed class Validator : IContentValidator
    {
        /// <inheritdoc />
        public void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(findings);

            var firstByItem = new Dictionary<int, int>();
            foreach (ContentRow row in candidate.Rows(type))
            {
                // A retired row keeps its bytes so a stored stack still decodes, and the item it named is
                // usually retired beside it. Following one reports a defect nobody can fix.
                if (row.IsRetired)
                {
                    continue;
                }

                long heals = GameRowNumbers.At(row, HealsIndex);
                if (heals <= 0)
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.FoodHealsNotPositive,
                        FormattableString.Invariant(
                            $"Food '{row.Key}' heals {heals}. A food that heals nothing is not food, and the boot refuses one.")));
                }

                long delay = GameRowNumbers.At(row, AttackDelayIndex);
                if (delay < 0)
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.FoodAttackDelayNegative,
                        FormattableString.Invariant(
                            $"Food '{row.Key}' delays the next attack by {delay}. Eating never gives time back.")));
                }

                int item = (int)GameRowNumbers.At(row, ItemIndex);
                if (item == 0)
                {
                    // A reference of 0 is no content, which the engine's own required-field check owns. Two
                    // rows both saying nothing are not a duplicate.
                    continue;
                }

                if (!firstByItem.TryAdd(item, row.Id))
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.FoodDuplicateItem,
                        FormattableString.Invariant(
                            $"Food '{row.Key}' claims item {item}, which food row {firstByItem[item]} already claims. One item eats one way.")));
                }
            }
        }
    }
}
