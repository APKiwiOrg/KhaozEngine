using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;
using static KhaozEngine.Tests.Catalog.GameTypes.GameTypeValidationFixtures;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// The cross-type sweep: the rules no single content type can state, because each one holds a row of one
/// type against a row of another or against a global knob.
/// </summary>
/// <remarks>
/// The knob NAMES here are made up. Which knobs a world tunes is that world's vocabulary, and the whole
/// point of <see cref="GameContentSweepOptions"/> is that none of them is in the package for a test to
/// reach for, so these are three names no game uses.
/// <para>
/// Most facts drive the sweep DIRECTLY, with the type id the registry hands it. The last two go through the
/// engine's whole validation, so the composition with <c>food</c>'s own validator and the once-per-run
/// shape are checked where they actually happen.
/// </para>
/// </remarks>
public class GameContentChecksTests
{
    const string LevelCapKnob = "highest_rank";
    const string ChanceCeilingKnob = "gather_ceiling_bp";
    const string ThirdKnob = "swing_cadence";

    static GameContentSweepOptions Sweep() => new()
    {
        MaxLevelKnob = LevelCapKnob,
        MaxChanceKnob = ChanceCeilingKnob,
        RequiredKnobs = [LevelCapKnob, ChanceCeilingKnob, ThirdKnob],
    };

    [Fact]
    public void AShelfSellingAnUntradableItemIsRefused()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration store = Registration(registry, GameContentTypeIds.Store);
        ContentTypeRegistration shelf = Registration(registry, GameContentTypeIds.StoreShelf);

        ContentSnapshot candidate = Snapshot(
            registry,
            Item(item, 11, "bread"),
            Item(item, 12, "quest_token", tradable: false),
            RowOf(store, 1, "general", (StoreContentType.NpcKindField, Int(4))),
            Shelf(shelf, 20, store: 1, item: 11, sort: 0),
            Shelf(shelf, 21, store: 1, item: 12, sort: 1));

        Assert.Equal(
            new[] { (21, GameContentFindings.SweepShelfItemNotTradable) },
            SweepCodes(registry, candidate));
    }

    [Fact]
    public void ANodeOverTheChanceCeilingOrTheLevelCapIsRefused()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration tuning = Registration(registry, GameContentTypeIds.GameTuning);
        ContentTypeRegistration node = Registration(registry, GameContentTypeIds.GatheringNode);

        Dictionary<string, int> knobs = Knobs();
        knobs[ChanceCeilingKnob] = 10_000;
        knobs[LevelCapKnob] = 99;

        ContentSnapshot candidate = Snapshot(
            registry,
            [
                .. TuningRows(tuning, knobs),
                Node(node, 1, "ordinary", level: 40, chance: 9_000),
                Node(node, 2, "impossible", level: 40, chance: 10_001),
                Node(node, 3, "unreachable", level: 100, chance: 1_000),
            ]);

        Assert.Equal(
            new[]
            {
                (2, GameContentFindings.SweepGatheringChanceOverCeiling),
                (3, GameContentFindings.SweepGatheringLevelOverCap),
            },
            SweepCodes(registry, candidate));

        // The knob is read out of THIS candidate's rows. Raise the ceiling in the pack and the same node is
        // legal, which is what makes the sweep a statement about the content rather than about the process.
        knobs[ChanceCeilingKnob] = 10_001;
        Assert.Empty(SweepCodes(
            registry,
            Snapshot(
                registry,
                [.. TuningRows(tuning, knobs), Node(node, 2, "impossible", level: 40, chance: 10_001)])));
    }

    [Fact]
    public void ARecipeOverTheLevelCapOrProducingNothingIsRefused()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration tuning = Registration(registry, GameContentTypeIds.GameTuning);
        ContentTypeRegistration recipe = Registration(registry, GameContentTypeIds.Recipe);
        ContentTypeRegistration output = Registration(registry, GameContentTypeIds.RecipeOutput);

        Dictionary<string, int> knobs = Knobs();
        knobs[LevelCapKnob] = 50;

        ContentSnapshot candidate = Snapshot(
            registry,
            [
                .. TuningRows(tuning, knobs),
                Recipe(recipe, 1, level: 1),
                Recipe(recipe, 2, level: 1),
                Recipe(recipe, 3, level: 60),
                RowOf(
                    output,
                    30,
                    "one_out",
                    (RecipeOutputContentType.RecipeField, Ref(1)),
                    (RecipeOutputContentType.ItemField, Ref(11)),
                    (RecipeOutputContentType.CountField, Int(1)),
                    (RecipeOutputContentType.SortField, Int(0))),
                RowOf(
                    output,
                    31,
                    "three_out",
                    (RecipeOutputContentType.RecipeField, Ref(3)),
                    (RecipeOutputContentType.ItemField, Ref(11)),
                    (RecipeOutputContentType.CountField, Int(1)),
                    (RecipeOutputContentType.SortField, Int(0))),
            ]);

        Assert.Equal(
            new[]
            {
                (2, GameContentFindings.SweepRecipeWithoutOutput),
                (3, GameContentFindings.SweepRecipeLevelOverCap),
            },
            SweepCodes(registry, candidate).OrderBy(f => f.Id).ToArray());
    }

    [Fact]
    public void AToolTierWhoseItemLacksItsFamilyTagIsRefused()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration tag = Registration(registry, EngineContentTypes.TagTypeId);
        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration tier = Registration(registry, GameContentTypeIds.ToolTier);

        // Tag 1 is the family and tag 2 is an ordinary material tag. Item 11 carries the family, item 12
        // carries only the material, and item 13 carries no tags at all.
        ContentSnapshot candidate = Snapshot(
            registry,
            RowOf(tag, 1, "cutters"),
            RowOf(tag, 2, "metal"),
            Item(item, 11, "bronze_cutter", tags: [1, 2]),
            Item(item, 12, "bronze_bar", tags: [2]),
            Item(item, 13, "driftwood", tags: []),
            Tier(tier, 1, item: 11, family: 1),
            Tier(tier, 2, item: 12, family: 1),
            Tier(tier, 3, item: 13, family: 1));

        // Which tags name a tool family is a GAME fact the engine's reference pass cannot see: any live tag
        // resolves, so the only thing that can refuse these two reads the item's own tag list.
        Assert.Equal(
            new[]
            {
                (2, GameContentFindings.SweepToolTierItemLacksFamilyTag),
                (3, GameContentFindings.SweepToolTierItemLacksFamilyTag),
            },
            SweepCodes(registry, candidate));
    }

    [Fact]
    public void AKnobTheBuildReadsIsRequiredAndOneItDoesNotKnowIsIgnored()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration tuning = Registration(registry, GameContentTypeIds.GameTuning);

        // Every knob the options name, plus one a LATER build writes. An older server has to load that pack.
        ContentRow[] rows = TuningRows(tuning, Knobs());
        Assert.Empty(SweepCodes(
            registry,
            Snapshot(
                registry,
                [
                    .. rows,
                    RowOf(
                        tuning,
                        rows.Length + 1,
                        "weather_storm_chance_bp",
                        (GameTuningContentType.ValueField, Scaled(250))),
                ])));

        // The other direction is a finding: a knob the build DOES read, missing from a table that carries
        // the rest, would leave the boot silently on a default nothing in the pack names.
        Dictionary<string, int> incomplete = Knobs();
        Assert.True(incomplete.Remove(ThirdKnob));
        ContentFinding only = Assert.Single(
            SweepFindings(registry, Snapshot(registry, TuningRows(tuning, incomplete))));
        Assert.Equal(GameContentFindings.SweepTuningKnobMissing, only.Code);
        Assert.Contains(ThirdKnob, only.Message, StringComparison.Ordinal);

        // A candidate carrying NO tuning rows at all says nothing about the table, so it is not a half
        // filled one. That is what lets a fixture, a partial import and a pack that ships no tuning sweep
        // clean.
        Assert.Empty(SweepCodes(registry, Snapshot(registry)));
    }

    /// <summary>
    /// A null knob name disables its rule and an empty required list disables the set rule, while the four
    /// rules that read no knob keep running.
    /// </summary>
    [Fact]
    public void ANullKnobNameDisablesItsRuleAndAnEmptyListDisablesTheSetRule()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration tuning = Registration(registry, GameContentTypeIds.GameTuning);
        ContentTypeRegistration node = Registration(registry, GameContentTypeIds.GatheringNode);
        ContentTypeRegistration recipe = Registration(registry, GameContentTypeIds.Recipe);

        Dictionary<string, int> knobs = Knobs();
        knobs[ChanceCeilingKnob] = 5_000;
        knobs[LevelCapKnob] = 50;
        Assert.True(knobs.Remove(ThirdKnob));

        ContentSnapshot candidate = Snapshot(
            registry,
            [
                .. TuningRows(tuning, knobs),
                Node(node, 1, "greedy", level: 60, chance: 6_000),
                Recipe(recipe, 1, level: 60),
            ]);

        // Everything on: both node rules, the recipe cap, the missing knob, and the output rule that needs
        // no knob at all.
        Assert.Equal(
            new[]
            {
                GameContentFindings.SweepGatheringChanceOverCeiling,
                GameContentFindings.SweepGatheringLevelOverCap,
                GameContentFindings.SweepRecipeLevelOverCap,
                GameContentFindings.SweepRecipeWithoutOutput,
                GameContentFindings.SweepTuningKnobMissing,
            }.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
            SweepCodes(registry, candidate, Sweep()).Select(f => f.Code).OrderBy(c => c, StringComparer.Ordinal).ToArray());

        // A null chance name drops 1302 and leaves 1303 alone, which is what makes the two rules separate
        // even though a level cap drives two codes of its own.
        Assert.DoesNotContain(
            GameContentFindings.SweepGatheringChanceOverCeiling,
            SweepCodes(registry, candidate, new GameContentSweepOptions
            {
                MaxLevelKnob = LevelCapKnob,
                MaxChanceKnob = null,
                RequiredKnobs = [LevelCapKnob],
            }).Select(f => f.Code));

        // A null level name drops BOTH level codes, because they are one fact read on two types.
        (int Id, string Code)[] noLevel = SweepCodes(registry, candidate, new GameContentSweepOptions
        {
            MaxLevelKnob = null,
            MaxChanceKnob = ChanceCeilingKnob,
            RequiredKnobs = [],
        });
        Assert.DoesNotContain(GameContentFindings.SweepGatheringLevelOverCap, noLevel.Select(f => f.Code));
        Assert.DoesNotContain(GameContentFindings.SweepRecipeLevelOverCap, noLevel.Select(f => f.Code));
        Assert.DoesNotContain(GameContentFindings.SweepTuningKnobMissing, noLevel.Select(f => f.Code));

        // None runs only the rules that read no knob, and the recipe with no output is one of them.
        Assert.Equal(
            new[] { (1, GameContentFindings.SweepRecipeWithoutOutput) },
            SweepCodes(registry, candidate, GameContentSweepOptions.None));
    }

    /// <summary>
    /// The second of the two latent defects this package was extracted to close: the item TAGS index the
    /// family rule reads comes off the live registry rather than off a schema built at type load.
    /// </summary>
    /// <remarks>
    /// The proof is a registry whose <c>item</c> type carries an extra leading field, which moves every item
    /// field one place along. A rule holding a static index would read the neighbour of <c>tags</c>, find
    /// bytes that are not a tag list, and report nothing at all for a tier whose item plainly lacks the tag.
    /// </remarks>
    [Fact]
    public void TheToolTierFamilyRuleResolvesTheItemTagsIndexOffTheRegistry()
    {
        var registry = new ContentTypeRegistry();
        ContentFieldSchema shifted = new(
            [
                new ContentFieldEntry("shim", ContentFieldKind.Int, null, ContentVisibility.Client, false),
                .. ItemContentType.CreateSchema().Fields,
            ]);
        registry.RegisterContentType(
            ContentRegistrationBand.Engine,
            EngineContentTypes.ItemTypeId,
            EngineContentTypes.ItemTypeKey,
            new ShimmedItemCodec(new ContentTypeId(EngineContentTypes.ItemTypeId), shifted),
            validator: null,
            shifted,
            ContentVisibility.Client,
            ContentTypeRegistry.MinChunkSlots);
        ToolTierContentType.Register(registry, validator: null);

        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration tier = Registration(registry, GameContentTypeIds.ToolTier);
        Assert.Equal(
            ItemContentType.CreateSchema().IndexOf(ItemContentType.TagsField) + 1,
            shifted.IndexOf(ItemContentType.TagsField));

        ContentSnapshot candidate = Snapshot(
            registry,
            RowOf(
                item,
                12,
                "bronze_bar",
                (ItemContentType.StackableField, Bool(false)),
                (ItemContentType.MaxStackField, Int(1)),
                (ItemContentType.TradableField, Bool(true)),
                (ItemContentType.ValueField, Scaled(60)),
                (ItemContentType.TagsField, ContentRowCodecBase.TagListValue([2]))),
            Tier(tier, 1, item: 12, family: 1));

        Assert.Equal(
            new[] { (1, GameContentFindings.SweepToolTierItemLacksFamilyTag) },
            SweepCodes(registry, candidate));
    }

    [Fact]
    public void TheSweepRidesOneSlotComposedOverFoodsOwnValidator()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        GameContentTypes.Register(registry, ContentDurationUnit.Ticks, new GameContentOptions
        {
            Recipe = new RecipeValidatorOptions
            {
                IsKnownRepeatMode = _ => true,
                IsPayableSkill = _ => true,
                IsOpenSkill = _ => true,
                IsNameableStation = _ => true,
            },
            IsKnownSkill = _ => true,
            Sweep = Sweep(),
        });

        // Exactly ONE registration carries the sweep, and it is the lowest game id. Thirteen would report
        // every cross-type defect thirteen times.
        ContentTypeRegistration[] carrying = registry.ByTypeId
            .Where(r => r.Validator is GameContentChecks)
            .ToArray();
        ContentTypeRegistration only = Assert.Single(carrying);
        Assert.Equal(GameContentTypeIds.Food, only.Type.Value);

        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration food = Registration(registry, GameContentTypeIds.Food);
        ContentTypeRegistration recipe = Registration(registry, GameContentTypeIds.Recipe);

        ContentSnapshot candidate = Snapshot(
            registry,
            Item(item, 11, "tuna"),
            RowOf(
                food,
                1,
                "tuna_food",
                (FoodContentType.ItemField, Ref(11)),
                (FoodContentType.HealsField, Int(0)),
                (FoodContentType.AttackDelayTicksField, Int(3))),
            Recipe(recipe, 1, level: 1));

        ContentValidationReport report = ContentValidator.Validate(candidate, null, [], registry);
        Assert.False(report.IsValid);

        string[] own = report.Findings
            .Where(f => string.Equals(f.Code, ContentValidator.TypeValidatorCode, StringComparison.Ordinal))
            .Select(f => f.Message)
            .ToArray();

        // The sweep's own finding reaches the report exactly ONCE, under the prefix of the slot it rides,
        // and food's own rule reaches it beside the sweep rather than instead of it. That pair is what
        // composing on one registration slot has to preserve.
        Assert.Single(own, m => Carries(m, GameContentFindings.SweepRecipeWithoutOutput));
        Assert.Single(own, m => Carries(m, GameContentFindings.FoodHealsNotPositive));

        static bool Carries(string message, string code)
            => message.StartsWith(GameContentTypeIds.FoodKey + ": " + code, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullRegistryOrOptionsIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new GameContentChecks(null!, Sweep()));
        Assert.Throws<ArgumentNullException>(() => new GameContentChecks(TypeRegistry(), null!));
    }

    static (int Id, string Code)[] SweepCodes(
        ContentTypeRegistry registry,
        ContentSnapshot candidate,
        GameContentSweepOptions? options = null)
        => SweepFindings(registry, candidate, options).Select(f => (f.Id, f.Code)).ToArray();

    /// <summary>The sweep alone, without food's own rules beside it.</summary>
    static List<ContentFinding> SweepFindings(
        ContentTypeRegistry registry,
        ContentSnapshot candidate,
        GameContentSweepOptions? options = null)
        => Findings(
            new GameContentChecks(registry, options ?? Sweep()),
            new ContentTypeId(GameContentTypeIds.Food),
            candidate);

    /// <summary>The three made-up knobs, at values a world would recognise.</summary>
    static Dictionary<string, int> Knobs() => new(StringComparer.Ordinal)
    {
        [LevelCapKnob] = 99,
        [ChanceCeilingKnob] = 10_000,
        [ThirdKnob] = 4,
    };

    /// <summary>One row per knob in the map, keyed by the knob name and holding its value at scale 100.</summary>
    static ContentRow[] TuningRows(ContentTypeRegistration tuning, IReadOnlyDictionary<string, int> knobs)
    {
        var rows = new List<ContentRow>(knobs.Count);
        foreach (string knob in knobs.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            rows.Add(RowOf(
                tuning,
                rows.Count + 1,
                knob,
                (GameTuningContentType.ValueField,
                    Scaled((long)knobs[knob] * GameTuningContentType.ValueScale))));
        }

        return rows.ToArray();
    }

    static ContentRow Shelf(ContentTypeRegistration shelf, int id, int store, int item, int sort)
        => RowOf(
            shelf,
            id,
            FormattableString.Invariant($"shelf_{id}"),
            (StoreShelfContentType.StoreField, Ref(store)),
            (StoreShelfContentType.ItemField, Ref(item)),
            (StoreShelfContentType.SortField, Int(sort)));

    static ContentRow Node(ContentTypeRegistration node, int id, string key, int level, int chance)
        => RowOf(
            node,
            id,
            key,
            (GatheringNodeContentType.SkillField, Int(1)),
            (GatheringNodeContentType.ToolFamilyField, Ref(1)),
            (GatheringNodeContentType.LevelRequiredField, Int(level)),
            (GatheringNodeContentType.BaseChanceBasisPointsField, Int(chance)),
            (GatheringNodeContentType.LivesField, Int(1)),
            (GatheringNodeContentType.LifeLossBasisPointsField, Int(10_000)),
            (GatheringNodeContentType.YieldXpField, Int(25)),
            (GatheringNodeContentType.YieldItemField, Ref(11)),
            (GatheringNodeContentType.RespawnTicksField, Int(10)));

    static ContentRow Recipe(ContentTypeRegistration recipe, int id, int level)
        => RowOf(
            recipe,
            id,
            FormattableString.Invariant($"recipe_{id}"),
            (RecipeContentType.DisplayOrderField, Int(id)),
            (RecipeContentType.SkillField, Int(1)),
            (RecipeContentType.LevelRequiredField, Int(level)),
            (RecipeContentType.PrimaryItemField, Ref(11)),
            (RecipeContentType.StationField, Int(1)),
            (RecipeContentType.BaseTicksField, Int(4)),
            (RecipeContentType.XpPerItemField, Int(10)),
            (RecipeContentType.RepeatModeField, Int(0)));

    static ContentRow Tier(ContentTypeRegistration tier, int id, int item, int family)
        => RowOf(
            tier,
            id,
            FormattableString.Invariant($"tier_{id}"),
            (ToolTierContentType.ItemField, Ref(item)),
            (ToolTierContentType.FamilyField, Ref(family)),
            (ToolTierContentType.RankField, Int(id)),
            (ToolTierContentType.SuccessScaleBasisPointsField, Int(10_000)),
            (ToolTierContentType.TimeScaleBasisPointsField, Int(10_000)));

    /// <summary>The positional walk over an item schema carrying one extra leading field.</summary>
    sealed class ShimmedItemCodec(ContentTypeId type, ContentFieldSchema schema)
        : ContentRowCodecBase(type, schema)
    {
    }
}
