using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;
using static KhaozEngine.Tests.Catalog.GameTypes.GameTypeValidationFixtures;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// The per-type validators that need nothing from a game: <c>food</c>, <c>equip_stat_line</c>,
/// <c>monster_drop</c>, <c>gathering_node</c>, <c>tool_tier</c> and <c>store</c>.
/// </summary>
/// <remarks>
/// Every fact asserts the CODE and the row it landed on. A test that only asserted "something was reported"
/// would pass while a rule emitted the wrong token, and the token is the whole contract: a counter, a test
/// and an operator runbook all key on it.
/// <para>
/// Each drives the validator DIRECTLY rather than through the engine's sweep, so a fact stays about one
/// type's rules. The composition and the folding into <c>KEC0040</c> are pinned where they happen, in the
/// registration and sweep tests.
/// </para>
/// </remarks>
public class LeafValidatorTests
{
    [Fact]
    public void FoodRefusesAZeroHealANegativeDelayAndASecondRowClaimingOneItem()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration food = Registration(registry, GameContentTypeIds.Food);

        ContentSnapshot candidate = Snapshot(
            registry,
            Food(food, 1, "tuna", item: 11, heals: 10, delay: 3),
            Food(food, 2, "burnt_tuna", item: 12, heals: 0, delay: 3),
            Food(food, 3, "cursed_tuna", item: 13, heals: -4, delay: -1),
            Food(food, 4, "tuna_again", item: 11, heals: 10, delay: 3));

        // ONE pass reports all four defects. A validator that stopped at the earliest would need four
        // publish attempts to get this candidate clean.
        Assert.Equal(
            new[]
            {
                (2, GameContentFindings.FoodHealsNotPositive),
                (3, GameContentFindings.FoodHealsNotPositive),
                (3, GameContentFindings.FoodAttackDelayNegative),
                (4, GameContentFindings.FoodDuplicateItem),
            },
            Codes(new FoodContentType.Validator(), food.Type, candidate));
    }

    [Fact]
    public void FoodSkipsARetiredRowAndARowNamingNoItem()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration food = Registration(registry, GameContentTypeIds.Food);

        ContentSnapshot candidate = Snapshot(
            registry,
            Food(food, 1, "tuna", item: 11, heals: 10, delay: 3),

            // A retired row keeps its bytes so a stored stack still decodes, and the item it named is
            // usually retired beside it, so it is not held against the live row that replaced it.
            RetiredRowOf(
                food,
                2,
                "old_tuna",
                true,
                (FoodContentType.ItemField, Ref(11)),
                (FoodContentType.HealsField, Int(0)),
                (FoodContentType.AttackDelayTicksField, Int(3))),

            // Two rows both naming NO item are not two rows claiming one item. A reference of 0 is no
            // content, which is the engine's own required-field finding rather than this rule's.
            Food(food, 3, "nothing", item: 0, heals: 5, delay: 1),
            Food(food, 4, "nothing_else", item: 0, heals: 5, delay: 1));

        Assert.Empty(Codes(new FoodContentType.Validator(), food.Type, candidate));
    }

    [Fact]
    public void EquipStatLineRefusesASecondLineForOnePairAndATiedDrawPosition()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration line = Registration(registry, GameContentTypeIds.EquipStatLine);

        ContentSnapshot candidate = Snapshot(
            registry,
            StatLine(line, 1, "sword_accuracy", profile: 1, stat: 1, sort: 0),
            StatLine(line, 2, "sword_accuracy_twice", profile: 1, stat: 1, sort: 1),
            StatLine(line, 3, "sword_power", profile: 1, stat: 2, sort: 0),
            StatLine(line, 4, "axe_accuracy", profile: 2, stat: 1, sort: 0));

        // Row 2 repeats a pair, row 3 repeats a draw position, and row 4 does neither because both rules
        // are scoped to ONE profile. All of it in a single pass.
        Assert.Equal(
            new[]
            {
                (2, GameContentFindings.EquipStatLineDuplicatePair),
                (3, GameContentFindings.EquipStatLineDuplicateSort),
            },
            Codes(new EquipStatLineContentType.Validator(), line.Type, candidate));
    }

    [Fact]
    public void MonsterDropRefusesASecondRuleClaimingOneCreatureKind()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration drop = Registration(registry, GameContentTypeIds.MonsterDrop);

        ContentSnapshot candidate = Snapshot(
            registry,
            Drop(drop, 1, "goblin", kind: 41, table: 900),
            Drop(drop, 2, "goblin_again", kind: 41, table: 901),
            Drop(drop, 3, "ogre", kind: 42, table: 902));

        (int Id, string Code) only = Assert.Single(
            Codes(new MonsterDropContentType.Validator(), drop.Type, candidate));
        Assert.Equal((2, GameContentFindings.MonsterDropDuplicateMonsterKind), only);
    }

    [Fact]
    public void GatheringNodeRefusesNoLivesALevelBelowOneAndARetiredYield()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration node = Registration(registry, GameContentTypeIds.GatheringNode);

        ContentSnapshot candidate = Snapshot(
            registry,
            Item(item, 11, "logs"),
            Item(item, 12, "ashen_logs", isRetired: true),
            Node(node, 1, "tree", yieldItem: 11),
            Node(node, 2, "ashen", yieldItem: 12),
            Node(node, 3, "spent", yieldItem: 11, lives: 0),
            Node(node, 4, "levelless", yieldItem: 11, levelRequired: 0));

        Assert.True(candidate.IsRetired(item.Type, 12));

        Assert.Equal(
            new[]
            {
                (2, GameContentFindings.GatheringNodeRetiredYieldItem),
                (3, GameContentFindings.GatheringNodeLivesNotPositive),
                (4, GameContentFindings.GatheringNodeLevelRequiredBelowOne),
            },
            Codes(new GatheringNodeContentType.Validator(), node.Type, candidate));
    }

    [Fact]
    public void ToolTierRefusesASecondTierAtOneRankAndEitherScaleAtOrBelowZero()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration tier = Registration(registry, GameContentTypeIds.ToolTier);

        ContentSnapshot candidate = Snapshot(
            registry,
            Tier(tier, 1, "bronze_first", item: 11, family: 1, rank: 1),
            Tier(tier, 2, "iron_first", item: 12, family: 1, rank: 1),
            Tier(tier, 3, "other_family_first", item: 13, family: 2, rank: 1),
            Tier(tier, 4, "broken", item: 14, family: 1, rank: 9, success: 0, time: -5));

        // Rank 1 of family 2 is fine beside rank 1 of family 1, which is what makes the rule per family.
        Assert.Equal(
            new[]
            {
                (2, GameContentFindings.ToolTierDuplicateRank),
                (4, GameContentFindings.ToolTierSuccessScaleNotPositive),
                (4, GameContentFindings.ToolTierTimeScaleNotPositive),
            },
            Codes(new ToolTierContentType.Validator(), tier.Type, candidate));
    }

    [Fact]
    public void StoreRefusesANegativeRateOnEitherSideAndASecondStoreOnOneNpcKind()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration store = Registration(registry, GameContentTypeIds.Store);
        var validator = new StoreContentType.Validator(registry);

        // A rate below zero is the validator's rather than the schema's: the field kind can hold it, so
        // something has to report it, and one run reports BOTH rates rather than the first.
        List<ContentFinding> rates = Findings(
            validator,
            store.Type,
            Snapshot(registry, Store(store, 1, "general", npcKind: 7, sellRate: -1, buyRate: -40)));

        Assert.Equal(2, rates.Count);
        Assert.All(rates, f => Assert.Equal(GameContentFindings.StoreNegativeRate, f.Code));
        Assert.All(rates, f => Assert.Equal(1, f.Id));
        Assert.Contains(
            rates,
            f => f.Message.Contains(StoreContentType.SellRateBasisPointsField, StringComparison.Ordinal));
        Assert.Contains(
            rates,
            f => f.Message.Contains(StoreContentType.BuyRateBasisPointsField, StringComparison.Ordinal));

        ContentSnapshot two = Snapshot(
            registry,
            Store(store, 1, "general", npcKind: 7),
            Store(store, 2, "smith", npcKind: 7));

        (int Id, string Code) only = Assert.Single(Codes(validator, store.Type, two));
        Assert.Equal((2, GameContentFindings.StoreDuplicateNpcKind), only);
    }

    [Fact]
    public void StoreRefusesARateThatPricesTheDearestItemOutsideTheCurrencyRange()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration store = Registration(registry, GameContentTypeIds.Store);

        // An item worth a fifth of the range. Nothing refuses it: the engine item type writes its value as
        // a 32 bit number, so this is an ordinary authored number rather than a malformed row.
        const int RichItemValue = int.MaxValue / 5;

        // The bound the validator derives, asked for rather than retyped, and pinned to the number it works
        // out to so a change in the derivation shows up here.
        int ceiling = StoreContentType.LargestSafeRateBasisPoints(RichItemValue);
        Assert.Equal(50_000, ceiling);
        Assert.Equal(10_000, StoreContentType.BasisPointDenominator);

        ContentSnapshot candidate = Snapshot(
            registry,
            Item(item, 11, "bread", value: 12),
            Item(item, 12, "crown", value: RichItemValue),
            Store(store, 1, "general", npcKind: 7, sellRate: ceiling + 1, buyRate: ceiling));

        // The sell rate alone. A rate AT the bound is legal, which is what keeps this a ceiling rather than
        // a guess about what a shop ought to charge.
        ContentFinding over = Assert.Single(
            Findings(new StoreContentType.Validator(registry), store.Type, candidate));
        Assert.Equal(GameContentFindings.StoreRateOverCurrencyCeiling, over.Code);
        Assert.Equal(1, over.Id);
        Assert.Equal(store.Type, over.Type);
        Assert.Contains(StoreContentType.SellRateBasisPointsField, over.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(StoreContentType.BuyRateBasisPointsField, over.Message, StringComparison.Ordinal);

        // A catalog of items worth nothing has nothing to overflow, so the widest rate the field holds is
        // allowed rather than a special case being written for it.
        Assert.Equal(int.MaxValue, StoreContentType.LargestSafeRateBasisPoints(0));
    }

    /// <summary>
    /// The first of the two latent defects this package was extracted to close: the item VALUE index the
    /// ceiling rule reads comes off the live registry rather than off a schema the validator built at type
    /// load.
    /// </summary>
    /// <remarks>
    /// The proof is a registry whose <c>item</c> type carries an EXTRA leading field, which moves
    /// <c>value</c> one place along. A validator holding a static index would read the neighbour, find the
    /// dearest item worth nothing, compute a ceiling of <see cref="int.MaxValue"/> and let the overflowing
    /// rate through. One that resolves the index at validation time still refuses it.
    /// </remarks>
    [Fact]
    public void StoreResolvesTheItemValueIndexOffTheRegistryRatherThanOffAStaticSchema()
    {
        const int RichItemValue = int.MaxValue / 5;
        int ceiling = StoreContentType.LargestSafeRateBasisPoints(RichItemValue);

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
        StoreContentType.Register(registry, validator: null);

        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration store = Registration(registry, GameContentTypeIds.Store);
        // The shim moved value one place along, which is the whole point: a static index would now read
        // the field before it.
        Assert.Equal(
            ItemContentType.CreateSchema().IndexOf(ItemContentType.ValueField) + 1,
            shifted.IndexOf(ItemContentType.ValueField));

        ContentSnapshot candidate = Snapshot(
            registry,
            RowOf(
                item,
                12,
                "crown",
                (ItemContentType.StackableField, Bool(false)),
                (ItemContentType.MaxStackField, Int(1)),
                (ItemContentType.TradableField, Bool(true)),
                (ItemContentType.ValueField, Scaled(RichItemValue))),
            Store(store, 1, "general", npcKind: 7, sellRate: ceiling + 1, buyRate: ceiling));

        ContentFinding over = Assert.Single(
            Findings(new StoreContentType.Validator(registry), store.Type, candidate));
        Assert.Equal(GameContentFindings.StoreRateOverCurrencyCeiling, over.Code);
    }

    static ContentRow Food(ContentTypeRegistration food, int id, string key, int item, int heals, int delay)
        => RowOf(
            food,
            id,
            key,
            (FoodContentType.ItemField, Ref(item)),
            (FoodContentType.HealsField, Int(heals)),
            (FoodContentType.AttackDelayTicksField, Int(delay)));

    static ContentRow StatLine(
        ContentTypeRegistration line,
        int id,
        string key,
        int profile,
        int stat,
        int sort)
        => RowOf(
            line,
            id,
            key,
            (EquipStatLineContentType.ProfileField, Ref(profile)),
            (EquipStatLineContentType.StatField, Ref(stat)),
            (EquipStatLineContentType.ValueField, Int(10)),
            (EquipStatLineContentType.SortField, Int(sort)));

    static ContentRow Drop(ContentTypeRegistration drop, int id, string key, int kind, int table)
        => RowOf(
            drop,
            id,
            key,
            (MonsterDropContentType.MonsterKindField, Int(kind)),
            (MonsterDropContentType.LootTableField, Ref(table)));

    static ContentRow Node(
        ContentTypeRegistration node,
        int id,
        string key,
        int yieldItem,
        int lives = 1,
        int levelRequired = 1)
        => RowOf(
            node,
            id,
            key,
            (GatheringNodeContentType.SkillField, Int(3)),
            (GatheringNodeContentType.ToolFamilyField, Ref(1)),
            (GatheringNodeContentType.LevelRequiredField, Int(levelRequired)),
            (GatheringNodeContentType.BaseChanceBasisPointsField, Int(5_000)),
            (GatheringNodeContentType.LivesField, Int(lives)),
            (GatheringNodeContentType.LifeLossBasisPointsField, Int(10_000)),
            (GatheringNodeContentType.YieldXpField, Int(25)),
            (GatheringNodeContentType.YieldItemField, Ref(yieldItem)),
            (GatheringNodeContentType.RespawnTicksField, Int(10)));

    static ContentRow Tier(
        ContentTypeRegistration tier,
        int id,
        string key,
        int item,
        int family,
        int rank,
        int success = 10_000,
        int time = 10_000)
        => RowOf(
            tier,
            id,
            key,
            (ToolTierContentType.ItemField, Ref(item)),
            (ToolTierContentType.FamilyField, Ref(family)),
            (ToolTierContentType.RankField, Int(rank)),
            (ToolTierContentType.SuccessScaleBasisPointsField, Int(success)),
            (ToolTierContentType.TimeScaleBasisPointsField, Int(time)));

    static ContentRow Store(
        ContentTypeRegistration store,
        int id,
        string key,
        int npcKind,
        int sellRate = 10_000,
        int buyRate = 4_000)
        => RowOf(
            store,
            id,
            key,
            (StoreContentType.NpcKindField, Int(npcKind)),
            (StoreContentType.SellRateBasisPointsField, Int(sellRate)),
            (StoreContentType.BuyRateBasisPointsField, Int(buyRate)));

    /// <summary>The positional walk over an item schema carrying one extra leading field.</summary>
    sealed class ShimmedItemCodec(ContentTypeId type, ContentFieldSchema schema)
        : ContentRowCodecBase(type, schema)
    {
    }
}
