using System;
using System.Collections.Generic;

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

    /// <summary>
    /// The recipe's own rules: one list position, a skill that can be paid and is open, a station the game
    /// stands for, a duration and an experience rate above zero, a reachable level, a repeat mode the game
    /// stands for, and a primary item still in play.
    /// </summary>
    /// <remarks>
    /// <b>This is the one validator here that needs the GAME.</b> A skill, a station and a repeat mode are
    /// raw durable numbers the package gives no meaning to, so
    /// <see cref="RecipeValidatorOptions"/> carries the four predicates that answer for them and this class
    /// carries the rules. No roster, no enum and no station list crosses into the engine.
    /// <para>
    /// It ACCUMULATES, so one bulk import comes back with the full picture rather than one row at a time. A
    /// RETIRED row is skipped, the same way the engine's reference pass skips one: a withdrawn recipe holds
    /// no list position and pays nobody.
    /// </para>
    /// <para>
    /// The level CAP is not checked here, and neither is whether the row produces anything. Both are
    /// statements about a global knob or a child type, so both belong to the cross-type pass.
    /// </para>
    /// <para>
    /// The duration rule is unit-neutral: it is about the number's SIGN, which is the same statement whether
    /// the field is spelled for ticks or for seconds.
    /// </para>
    /// </remarks>
    /// <param name="options">The four game answers. Every one is required.</param>
    public sealed class Validator(RecipeValidatorOptions options) : IContentValidator
    {
        readonly RecipeValidatorOptions _options = options
            ?? throw new ArgumentNullException(nameof(options));

        /// <inheritdoc />
        public void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(findings);

            var itemType = new ContentTypeId(EngineContentTypes.ItemTypeId);
            var orders = new Dictionary<long, int>();

            foreach (ContentRow row in candidate.Rows(type))
            {
                // A row whose value count does not match the schema is the engine's own finding, and reading
                // it positionally here would be reading someone else's fields.
                if (row.IsRetired || row.Fields.Count != FieldCount)
                {
                    continue;
                }

                CheckOrder(type, row, orders, findings);
                CheckSkill(type, row, findings);
                CheckStation(type, row, findings);
                CheckPositive(
                    type,
                    row,
                    BaseDurationIndex,
                    GameContentFindings.RecipeBaseDurationNotPositive,
                    "declares a duration of",
                    "A recipe costs real time, and a completion that took none would finish forever inside one step of the clock.",
                    findings);
                CheckPositive(
                    type,
                    row,
                    XpPerItemIndex,
                    GameContentFindings.RecipeXpPerItemNotPositive,
                    "pays",
                    "Experience is paid per product item, so a rate of nothing trains the skill the row is listed under at no rate at all.",
                    findings);

                ContentFieldValue level = row.Fields[LevelRequiredIndex];
                if (!level.IsAbsent && level.Number < 1)
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.RecipeLevelRequiredBelowOne,
                        FormattableString.Invariant(
                            $"Recipe {row.Id} asks for level {level.Number}. One is the level every character starts at, so a row below it is gated on nothing.")));
                }

                ContentFieldValue mode = row.Fields[RepeatModeIndex];
                if (!mode.IsAbsent && !_options.IsKnownRepeatMode(mode.Number))
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.RecipeUnknownRepeatMode,
                        FormattableString.Invariant(
                            $"Recipe {row.Id} carries repeat mode {mode.Number}, which nothing in the game stands for. The number is the game's own durable repeat mode value, so a row may only carry one the game already knows.")));
                }

                ContentFieldValue item = row.Fields[PrimaryItemIndex];

                // A reference of 0 is NO CONTENT rather than a dangling id, and a required field left empty
                // is the engine's KEC0005.
                if (item.IsAbsent || item.Number == 0 || !candidate.IsRetired(itemType, (int)item.Number))
                {
                    continue;
                }

                findings.Add(new ContentFinding(
                    type,
                    row.Id,
                    GameContentFindings.RecipeRetiredPrimaryItem,
                    FormattableString.Invariant(
                        $"Recipe {row.Id} is listed under item {item.Number}, which is retired. The panel lists a row by its primary item, so the row would keep a withdrawn item on screen.")));
            }
        }

        static void CheckOrder(
            ContentTypeId type,
            ContentRow row,
            Dictionary<long, int> orders,
            ICollection<ContentFinding> findings)
        {
            ContentFieldValue order = row.Fields[DisplayOrderIndex];
            if (order.IsAbsent || orders.TryAdd(order.Number, row.Id))
            {
                return;
            }

            findings.Add(new ContentFinding(
                type,
                row.Id,
                GameContentFindings.RecipeDuplicateDisplayOrder,
                FormattableString.Invariant(
                    $"Recipe {row.Id} claims list position {order.Number}, which recipe {orders[order.Number]} already claims. The panel order is the field and never the row order, so two rows at one position have no order between them.")));
        }

        void CheckSkill(ContentTypeId type, ContentRow row, ICollection<ContentFinding> findings)
        {
            ContentFieldValue value = row.Fields[SkillIndex];
            if (value.IsAbsent)
            {
                return;
            }

            if (!_options.IsPayableSkill(value.Number))
            {
                findings.Add(new ContentFinding(
                    type,
                    row.Id,
                    GameContentFindings.RecipeSkillNotPayable,
                    FormattableString.Invariant(
                        $"Recipe {row.Id} pays skill {value.Number}, which nothing can be paid into. A number nothing stands for, and a real skill no recipe may be listed under, both pay nobody.")));
                return;
            }

            if (_options.IsOpenSkill(value.Number))
            {
                return;
            }

            findings.Add(new ContentFinding(
                type,
                row.Id,
                GameContentFindings.RecipeLockedSkill,
                FormattableString.Invariant(
                    $"Recipe {row.Id} pays skill {value.Number}, which is not open yet. A locked skill refuses every award, so the row would cost the time and pay nothing.")));
        }

        void CheckStation(ContentTypeId type, ContentRow row, ICollection<ContentFinding> findings)
        {
            ContentFieldValue value = row.Fields[StationIndex];
            if (value.IsAbsent)
            {
                return;
            }

            if (value.Number == RecipeValidatorOptions.NoStation)
            {
                findings.Add(new ContentFinding(
                    type,
                    row.Id,
                    GameContentFindings.RecipeStationNone,
                    FormattableString.Invariant(
                        $"Recipe {row.Id} names no station. Zero is the PLACE a resolver answers when a character is standing nowhere in particular, never a station a row may name, so a recipe has to name one.")));
                return;
            }

            if (_options.IsNameableStation(value.Number))
            {
                return;
            }

            findings.Add(new ContentFinding(
                type,
                row.Id,
                GameContentFindings.RecipeUnknownStation,
                FormattableString.Invariant(
                    $"Recipe {row.Id} names station {value.Number}, which nothing in the game stands for. The number is the game's own durable station value.")));
        }

        static void CheckPositive(
            ContentTypeId type,
            ContentRow row,
            int index,
            string code,
            string verb,
            string reason,
            ICollection<ContentFinding> findings)
        {
            ContentFieldValue value = row.Fields[index];
            if (value.IsAbsent || value.Number > 0)
            {
                return;
            }

            findings.Add(new ContentFinding(
                type,
                row.Id,
                code,
                FormattableString.Invariant($"Recipe {row.Id} {verb} {value.Number}. {reason}")));
        }
    }
}
