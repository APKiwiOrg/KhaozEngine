using System;
using System.Collections.Generic;

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
            GameContentTypeIds.GatheringNode,
            GameContentTypeIds.GatheringNodeKey,
            new Codec(new ContentTypeId(GameContentTypeIds.GatheringNode), schema),
            validator,
            schema,
            DefaultVisibility,
            DefaultChunkSlots);
    }

    /// <summary>The node row codec, which is the engine's positional walk with nothing added.</summary>
    public sealed class Codec : ContentRowCodecBase
    {
        /// <summary>Builds the codec over the gathering node schema.</summary>
        public Codec(ContentTypeId type, ContentFieldSchema schema) : base(type, schema)
        {
        }
    }

    /// <summary>
    /// The node's own per-row rules: it holds at least one life, it asks for a level a character can reach,
    /// and it yields something still in play.
    /// </summary>
    /// <remarks>
    /// It takes NO options. The skill number is the game's and this type says nothing about it, and the
    /// three rules read only numbers whose meaning is arithmetic. It ACCUMULATES, so one run over a whole
    /// node list reports every defect rather than the earliest.
    /// <para>
    /// Two bounds are deliberately NOT here, because neither can be read from one row: a chance ceiling and
    /// a level cap are global knobs on <c>game_tuning</c>, so holding a node against either is the
    /// cross-type sweep's rather than this type's.
    /// </para>
    /// <para>
    /// A RETIRED node is skipped, the same way the engine's reference pass skips a retired row. A withdrawn
    /// node stands nowhere and yields nothing.
    /// </para>
    /// </remarks>
    public sealed class Validator : IContentValidator
    {
        /// <inheritdoc />
        public void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ArgumentNullException.ThrowIfNull(findings);

            var itemType = new ContentTypeId(EngineContentTypes.ItemTypeId);
            foreach (ContentRow row in candidate.Rows(type))
            {
                // A row whose value count does not match the schema is the engine's own finding, and reading
                // it positionally here would be reading someone else's fields.
                if (row.IsRetired || row.Fields.Count != FieldCount)
                {
                    continue;
                }

                ContentFieldValue lives = row.Fields[LivesIndex];
                if (!lives.IsAbsent && lives.Number <= 0)
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.GatheringNodeLivesNotPositive,
                        FormattableString.Invariant(
                            $"Node {row.Id} holds {lives.Number} lives. Lives are shared by everyone working the node and the node falls when the last one goes, so a node with none is spent before the first swing lands.")));
                }

                ContentFieldValue level = row.Fields[LevelRequiredIndex];
                if (!level.IsAbsent && level.Number < 1)
                {
                    findings.Add(new ContentFinding(
                        type,
                        row.Id,
                        GameContentFindings.GatheringNodeLevelRequiredBelowOne,
                        FormattableString.Invariant(
                            $"Node {row.Id} asks for level {level.Number}. One is the level every character starts at, so a node below it is quoting a chance at a level nobody is ever under.")));
                }

                ContentFieldValue item = row.Fields[YieldItemIndex];

                // A reference of 0 is NO CONTENT rather than a dangling id, and a required field left empty
                // is the engine's KEC0005.
                if (item.IsAbsent || item.Number == 0 || !candidate.IsRetired(itemType, (int)item.Number))
                {
                    continue;
                }

                findings.Add(new ContentFinding(
                    type,
                    row.Id,
                    GameContentFindings.GatheringNodeRetiredYieldItem,
                    FormattableString.Invariant(
                        $"Node {row.Id} yields item {item.Number}, which is retired. A retired row keeps its bytes so a stored stack still decodes, so the node still resolves and would go on handing out something the game withdrew.")));
            }
        }
    }
}
