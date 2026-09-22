using System;
using System.Collections.Generic;

namespace KhaozEngine.Catalog.GameTypes;

/// <summary>
/// The cross-type sweep: the content rules no single type can state, because each one holds a row of one
/// type against a row of another or against a global knob.
/// </summary>
/// <remarks>
/// <b>It rides ONE registration slot.</b> The engine takes one <see cref="IContentValidator"/> per type and
/// hands each of them the WHOLE candidate, so a whole-registry check registered on all thirteen game types
/// would report every defect thirteen times. It goes on the lowest game id instead, <c>food</c>, and runs
/// once. That is the shape the engine's own instance band uses for the same reason, and it is what
/// <see cref="GameContentTypes.Register"/> wires up.
/// <para>
/// <b>It COMPOSES rather than replaces.</b> <c>food</c> has rules of its own, and a slot that holds one
/// validator means the sweep has to carry the type's own beside it: it runs <c>beside</c> first and then its
/// own pass, so a food defect and a cross-type defect both reach the report, each under its own code.
/// </para>
/// <para>
/// <b>A game's own whole-catalog rules ride the same slot, LAST.</b>
/// <see cref="GameContentSweepOptions.GameRules"/> runs after every rule of the package's own, over the same
/// candidate and into the same findings, so a game adds a cross-type rule without a second slot and without
/// a way to mount it that leaves the package's sweep out.
/// </para>
/// <para>
/// <b>Every knob is read out of the CANDIDATE.</b> Nothing here reads a running process: a sweep that did
/// would pass or fail the same pack differently depending on what a server happened to have loaded, which
/// is the opposite of what a publish check is for.
/// </para>
/// <para>
/// <b>The two rules that read an ITEM row resolve their field positions off the live registry</b>, at
/// validation time, through <see cref="ContentFieldSchema.IndexOf"/>. A static index taken from a schema
/// this class built at type load is this build's idea of the item type rather than the one the candidate
/// was registered against, so an engine release that appended or reordered an item field would leave both
/// rules reading a neighbour, silently.
/// </para>
/// <para>
/// It ACCUMULATES and it never throws for a content reason. A row whose value count does not match its
/// schema is skipped, because reading it positionally would be reading someone else's fields, and that
/// mismatch is already the engine's own finding.
/// </para>
/// <para>
/// <b>What it deliberately does NOT restate.</b> A reference that dangles or names a retired row is the
/// engine's <c>KEC0006</c> on every key reference in the pack, so the shelf rule here adds only the half
/// the engine cannot see: whether the item is TRADABLE. Restating the rest would double report every defect
/// an author has already been shown.
/// </para>
/// </remarks>
/// <param name="registry">
/// The registry the candidate's types are declared in, which is where the two <c>item</c> field positions
/// come from at validation time.
/// </param>
/// <param name="options">
/// Which knob-driven rules to run, under what names, and the game's own rules to run after them.
/// <see cref="GameContentSweepOptions.None"/> runs the three that read no knob and no game rule.
/// </param>
/// <param name="beside">The registered type's own validator, run first, or null for the sweep alone.</param>
public sealed class GameContentChecks(
    ContentTypeRegistry registry,
    GameContentSweepOptions options,
    IContentValidator? beside = null) : IContentValidator
{
    readonly ContentTypeRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    readonly GameContentSweepOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    readonly IContentValidator? _beside = beside;

    readonly IContentValidator[] _gameRules = GameRulesOf(options);

    /// <inheritdoc />
    public void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(findings);

        _beside?.Validate(type, candidate, findings);

        var knobs = new KnobTable(candidate);
        CheckShelfItemsAreTradable(candidate, findings);
        CheckGatheringNodes(candidate, knobs, findings);
        CheckRecipes(candidate, knobs, findings);
        CheckToolTierFamilies(candidate, findings);
        CheckKnobsAreComplete(knobs, findings);

        foreach (IContentValidator rule in _gameRules)
        {
            rule.Validate(type, candidate, findings);
        }
    }

    /// <summary>
    /// The game's own rules, copied once so a list the game later edits cannot change what a mounted sweep
    /// runs, and refused at construction when a member is null rather than at the first publish.
    /// </summary>
    static IContentValidator[] GameRulesOf(GameContentSweepOptions? options)
    {
        if (options is null)
        {
            return [];
        }

        IReadOnlyList<IContentValidator> rules = options.GameRules
            ?? throw new ArgumentException("The sweep options carry a null game rule list.", nameof(options));
        var copy = new IContentValidator[rules.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = rules[i] ?? throw new ArgumentException(
                FormattableString.Invariant($"Game rule {i} is null."),
                nameof(options));
        }

        return copy;
    }

    /// <summary>
    /// Every SHELF item is tradable. A store is the counter both ways, so an untradable item on a shelf is a
    /// row the buy half refuses at the moment a player clicks it, with nothing upstream to say why.
    /// </summary>
    void CheckShelfItemsAreTradable(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        int tradableIndex = EngineSchemaFields.IndexIn(
            _registry,
            EngineContentTypes.ItemTypeKey,
            ItemContentType.TradableField);
        if (tradableIndex < 0)
        {
            return;
        }

        var shelfType = new ContentTypeId(GameContentTypeIds.StoreShelf);
        var itemType = new ContentTypeId(EngineContentTypes.ItemTypeId);

        foreach (ContentRow row in candidate.Rows(shelfType))
        {
            if (row.IsRetired || row.Fields.Count != StoreShelfContentType.FieldCount)
            {
                continue;
            }

            ContentFieldValue item = row.Fields[StoreShelfContentType.ItemIndex];
            if (item.IsAbsent
                || item.Number == 0
                || !candidate.TryGetRow(itemType, (int)item.Number, out ContentRow? sold)
                || sold.Fields.Count <= tradableIndex)
            {
                continue;
            }

            ContentFieldValue tradable = sold.Fields[tradableIndex];
            if (tradable.IsAbsent || tradable.Number != 0)
            {
                continue;
            }

            findings.Add(new ContentFinding(
                shelfType,
                row.Id,
                GameContentFindings.SweepShelfItemNotTradable,
                FormattableString.Invariant(
                    $"Shelf '{row.Key}' sells item {item.Number}, which is not tradable. A store is the counter both ways, so the row draws on the shelf and then refuses the click.")));
        }
    }

    /// <summary>
    /// Two global knobs a node row cannot answer alone: the chance ceiling every quoted chance is held
    /// under, and the level cap every requirement is gated by. A game that names neither runs neither.
    /// </summary>
    void CheckGatheringNodes(
        IContentSnapshot candidate,
        KnobTable knobs,
        ICollection<ContentFinding> findings)
    {
        if (_options.MaxChanceKnob is null && _options.MaxLevelKnob is null)
        {
            return;
        }

        var nodeType = new ContentTypeId(GameContentTypeIds.GatheringNode);

        foreach (ContentRow row in candidate.Rows(nodeType))
        {
            if (row.IsRetired || row.Fields.Count != GatheringNodeContentType.FieldCount)
            {
                continue;
            }

            ContentFieldValue chance = row.Fields[GatheringNodeContentType.BaseChanceBasisPointsIndex];
            if (!chance.IsAbsent
                && _options.MaxChanceKnob is string chanceKnob
                && knobs.TryGet(chanceKnob, out long ceiling)
                && Over(chance.Number, ceiling))
            {
                findings.Add(new ContentFinding(
                    nodeType,
                    row.Id,
                    GameContentFindings.SweepGatheringChanceOverCeiling,
                    FormattableString.Invariant(
                        $"Node '{row.Key}' quotes {chance.Number} basis points, over the {chanceKnob} knob at {Whole(ceiling)}. The runtime clamps to the ceiling, so the authored number is one nobody ever rolls against.")));
            }

            ContentFieldValue level = row.Fields[GatheringNodeContentType.LevelRequiredIndex];
            if (!level.IsAbsent
                && _options.MaxLevelKnob is string levelKnob
                && knobs.TryGet(levelKnob, out long cap)
                && Over(level.Number, cap))
            {
                findings.Add(new ContentFinding(
                    nodeType,
                    row.Id,
                    GameContentFindings.SweepGatheringLevelOverCap,
                    FormattableString.Invariant(
                        $"Node '{row.Key}' asks for level {level.Number}, over the {levelKnob} knob at {Whole(cap)}. Nobody reaches that level, so the node stands and can never be worked.")));
            }
        }
    }

    /// <summary>
    /// A recipe is gated by the same level cap, and it has to PRODUCE something: the outputs are a child
    /// type, so a recipe with none is a row the panel lists, the step costs time on, and the bag never sees.
    /// </summary>
    /// <remarks>
    /// The output rule reads no knob, so it runs whether or not a game named a level cap.
    /// </remarks>
    void CheckRecipes(IContentSnapshot candidate, KnobTable knobs, ICollection<ContentFinding> findings)
    {
        var recipeType = new ContentTypeId(GameContentTypeIds.Recipe);
        var outputType = new ContentTypeId(GameContentTypeIds.RecipeOutput);

        var produces = new HashSet<int>();
        foreach (ContentRow row in candidate.Rows(outputType))
        {
            if (row.IsRetired || row.Fields.Count != RecipeOutputContentType.FieldCount)
            {
                continue;
            }

            ContentFieldValue parent = row.Fields[RecipeOutputContentType.RecipeIndex];
            if (!parent.IsAbsent && parent.Number != 0)
            {
                produces.Add((int)parent.Number);
            }
        }

        foreach (ContentRow row in candidate.Rows(recipeType))
        {
            if (row.IsRetired || row.Fields.Count != RecipeContentType.FieldCount)
            {
                continue;
            }

            ContentFieldValue level = row.Fields[RecipeContentType.LevelRequiredIndex];
            if (!level.IsAbsent
                && _options.MaxLevelKnob is string levelKnob
                && knobs.TryGet(levelKnob, out long cap)
                && Over(level.Number, cap))
            {
                findings.Add(new ContentFinding(
                    recipeType,
                    row.Id,
                    GameContentFindings.SweepRecipeLevelOverCap,
                    FormattableString.Invariant(
                        $"Recipe '{row.Key}' asks for level {level.Number}, over the {levelKnob} knob at {Whole(cap)}. Nobody reaches that level, so the row is listed and can never be worked.")));
            }

            if (produces.Contains(row.Id))
            {
                continue;
            }

            findings.Add(new ContentFinding(
                recipeType,
                row.Id,
                GameContentFindings.SweepRecipeWithoutOutput,
                FormattableString.Invariant(
                    $"Recipe '{row.Key}' has no live {GameContentTypeIds.RecipeOutputKey} row. The outputs are a child type, so a recipe with none consumes its inputs, costs its time and lands nothing in the bag.")));
        }
    }

    /// <summary>
    /// Every tool tier's item carries the tag its family names. The family is a tag reference, so the engine
    /// resolves it against any live tag and has no way to know which tags are tool families: what makes a
    /// tool one of a family is the ITEM carrying the family tag, and a tier whose item does not is a tool no
    /// swing ever selects.
    /// </summary>
    void CheckToolTierFamilies(IContentSnapshot candidate, ICollection<ContentFinding> findings)
    {
        int tagsIndex = EngineSchemaFields.IndexIn(
            _registry,
            EngineContentTypes.ItemTypeKey,
            ItemContentType.TagsField);
        if (tagsIndex < 0)
        {
            return;
        }

        var tierType = new ContentTypeId(GameContentTypeIds.ToolTier);
        var itemType = new ContentTypeId(EngineContentTypes.ItemTypeId);

        foreach (ContentRow row in candidate.Rows(tierType))
        {
            if (row.IsRetired)
            {
                continue;
            }

            ContentFieldValue item = row.Fields.Count > ToolTierContentType.ItemIndex
                ? row.Fields[ToolTierContentType.ItemIndex]
                : ContentFieldValue.Absent(ContentFieldKind.KeyReference);
            ContentFieldValue family = row.Fields.Count > ToolTierContentType.FamilyIndex
                ? row.Fields[ToolTierContentType.FamilyIndex]
                : ContentFieldValue.Absent(ContentFieldKind.KeyReference);

            if (item.IsAbsent
                || item.Number == 0
                || family.IsAbsent
                || family.Number == 0
                || !candidate.TryGetRow(itemType, (int)item.Number, out ContentRow? tool)
                || tool.Fields.Count <= tagsIndex)
            {
                continue;
            }

            if (!TryReadTags(tool.Fields[tagsIndex], (int)family.Number, out bool carries) || carries)
            {
                continue;
            }

            findings.Add(new ContentFinding(
                tierType,
                row.Id,
                GameContentFindings.SweepToolTierItemLacksFamilyTag,
                FormattableString.Invariant(
                    $"Tool tier '{row.Key}' puts item {item.Number} in family {family.Number}, and the item does not carry that tag. A swing reads the family off the ITEM, so this tier is never selected by anything.")));
        }
    }

    /// <summary>
    /// Every knob this build reads has a row, asymmetrically: a missing one is a finding and an UNKNOWN one
    /// is ignored, so a pack a newer build wrote still loads on an older server.
    /// </summary>
    void CheckKnobsAreComplete(KnobTable knobs, ICollection<ContentFinding> findings)
    {
        if (_options.RequiredKnobs.Count == 0 || !knobs.Authored)
        {
            return;
        }

        var tuningType = new ContentTypeId(GameContentTypeIds.GameTuning);
        foreach (string knob in _options.RequiredKnobs)
        {
            if (knobs.TryGet(knob, out _))
            {
                continue;
            }

            findings.Add(new ContentFinding(
                tuningType,
                0,
                GameContentFindings.SweepTuningKnobMissing,
                FormattableString.Invariant(
                    $"The {GameContentTypeIds.GameTuningKey} table carries rows and none of them is '{knob}', which this build reads. A half filled table leaves the boot mixing authored knobs with defaults the pack does not name.")));
        }
    }

    /// <summary>
    /// Whether a plain authored number is over a knob held at
    /// <see cref="GameTuningContentType.ValueScale"/>.
    /// </summary>
    /// <remarks>
    /// The authored number is scaled UP rather than the knob divided down, so a fractional knob is compared
    /// exactly instead of through a rounding nobody authored.
    /// </remarks>
    static bool Over(long authored, long scaledKnob) => authored * GameTuningContentType.ValueScale > scaledKnob;

    /// <summary>A knob's stored number as the value an author typed, for a message.</summary>
    static decimal Whole(long scaled) => scaled / (decimal)GameTuningContentType.ValueScale;

    /// <summary>
    /// Whether an item's tag list carries one tag id. False out of <c>readable</c> for bytes that are not a
    /// run of varints, which is the codec pass's own finding: guessing here would report a family defect on
    /// a row whose real defect is its encoding.
    /// </summary>
    static bool TryReadTags(in ContentFieldValue tags, int tagId, out bool carries)
    {
        carries = false;
        if (tags.IsAbsent)
        {
            return true;
        }

        ReadOnlySpan<byte> bytes = tags.Bytes.Span;
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (!ContentVarint.TryRead(bytes, ref offset, out uint value, out _))
            {
                return false;
            }

            if (unchecked((int)value) == tagId)
            {
                carries = true;
                return true;
            }
        }

        return true;
    }

    /// <summary>
    /// The candidate's <c>game_tuning</c> rows as a lookup, read once per sweep rather than once per rule.
    /// </summary>
    /// <remarks>
    /// A duplicate knob name OVERWRITES rather than throwing, because two rows under one key is already the
    /// engine's <c>KEC0002</c> and a validator that threw would be reported as a defective validator instead
    /// of letting the real finding through.
    /// </remarks>
    sealed class KnobTable
    {
        readonly Dictionary<string, long> _byName = new(StringComparer.Ordinal);

        internal KnobTable(IContentSnapshot candidate)
        {
            foreach (ContentRow row in candidate.Rows(new ContentTypeId(GameContentTypeIds.GameTuning)))
            {
                if (row.IsRetired || row.Fields.Count != GameTuningContentType.FieldCount)
                {
                    continue;
                }

                Authored = true;
                ContentFieldValue value = row.Fields[GameTuningContentType.ValueIndex];
                if (!value.IsAbsent)
                {
                    _byName[row.Key.ToString()] = value.Number;
                }
            }
        }

        /// <summary>Whether the candidate carries a live tuning row at all, which gates the set rule.</summary>
        internal bool Authored { get; }

        /// <summary>One knob's stored number, at <see cref="GameTuningContentType.ValueScale"/>.</summary>
        internal bool TryGet(string knob, out long scaled) => _byName.TryGetValue(knob, out scaled);
    }
}
