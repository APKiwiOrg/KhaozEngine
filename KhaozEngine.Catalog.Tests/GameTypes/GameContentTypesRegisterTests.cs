using System;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// The one-call registration: all thirteen types, each with the validator this package ships for it, from a
/// single <see cref="GameContentTypes.Register"/> and one options object.
/// </summary>
/// <remarks>
/// What this pins is which registration ended up carrying WHICH validator. Thirteen hand-written calls are
/// thirteen chances to pass a wrong validator or none at all, and a registration carrying the wrong one is
/// silent: the publish reports clean and the rule the type needed never runs.
/// </remarks>
public class GameContentTypesRegisterTests
{
    static GameContentOptions Options() => new()
    {
        Recipe = new RecipeValidatorOptions
        {
            IsKnownRepeatMode = value => value is 0 or 1,
            IsPayableSkill = value => value is 1 or 2,
            IsOpenSkill = value => value is 1,
            IsNameableStation = value => value is 1 or 2,
        },
        IsKnownSkill = value => value is > 0 and < 100,

        // A world with no tuning table says so rather than leaving the member out, which the compiler would
        // not let it do anyway. The three rules that read no knob still run.
        Sweep = GameContentSweepOptions.None,
    };

    static ContentTypeRegistry Registered(ContentDurationUnit unit = ContentDurationUnit.Ticks)
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        GameContentTypes.Register(registry, unit, Options());
        return registry;
    }

    [Theory]
    [InlineData(ContentDurationUnit.Ticks)]
    [InlineData(ContentDurationUnit.Seconds)]
    public void OneCallRegistersAllThirteenWithThePackagesOwnValidators(ContentDurationUnit unit)
    {
        ContentTypeRegistry registry = Registered(unit);

        // The expectations are LITERALS, including the two types that deliberately carry none. Reading the
        // answer back off the call that produced it would pin nothing.
        Assert.Equal(
            new (ushort Id, string Key, string? Validator)[]
            {
                // The lowest game id carries the cross-type sweep, composed over food's own rules, because
                // the engine hands every per-type validator the whole candidate and thirteen mountings
                // would report every cross-type defect thirteen times.
                (1024, "food", nameof(GameContentChecks)),


                // equip_profile carries three durable numbers and no rule a schema does not already make.
                (1025, "equip_profile", null),

                (1026, "equip_stat_line", nameof(EquipStatLineContentType.Validator)),
                (1027, "store", nameof(StoreContentType.Validator)),
                (1028, "store_shelf", nameof(StoreShelfContentType.Validator)),
                (1029, "monster_drop", nameof(MonsterDropContentType.Validator)),
                (1030, "gathering_node", nameof(GatheringNodeContentType.Validator)),
                (1031, "recipe", nameof(RecipeContentType.Validator)),
                (1032, "recipe_input", nameof(RecipeInputContentType.Validator)),
                (1033, "recipe_output", nameof(RecipeOutputContentType.Validator)),
                (1034, "tool_tier", nameof(ToolTierContentType.Validator)),
                (1035, "skill_curve", nameof(SkillCurveContentType.Validator)),

                // Every rule about game_tuning is a statement about the SET of rows or about a type that
                // reads a knob, and the one rule a single row could carry is already the engine's KEC0002.
                (1036, "game_tuning", null),
            },
            registry.ByTypeId
                .Where(r => r.Band == ContentRegistrationBand.Game)
                .Select(r => (r.Type.Value, r.TypeKey, r.Validator?.GetType().Name))
                .ToArray());
    }

    [Fact]
    public void TheValidatorsItBuildsCarryTheOptionsItWasHanded()
    {
        ContentTypeRegistry registry = Registered();
        Assert.True(registry.TryGetByKey(GameContentTypeIds.RecipeKey, out ContentTypeRegistration? recipe));
        Assert.True(registry.TryGetByKey(GameContentTypeIds.SkillCurveKey, out ContentTypeRegistration? curve));

        ContentSnapshot candidate = GameTypeValidationFixtures.Snapshot(
            registry,
            GameTypeValidationFixtures.RowOf(
                recipe,
                1,
                "locked_step",
                (RecipeContentType.DisplayOrderField, GameTypeValidationFixtures.Int(1)),

                // Skill 2 is payable under the options above and is not open yet.
                (RecipeContentType.SkillField, GameTypeValidationFixtures.Int(2)),
                (RecipeContentType.LevelRequiredField, GameTypeValidationFixtures.Int(1)),
                (RecipeContentType.PrimaryItemField, GameTypeValidationFixtures.Ref(11)),
                (RecipeContentType.StationField, GameTypeValidationFixtures.Int(1)),
                (RecipeContentType.BaseTicksField, GameTypeValidationFixtures.Int(4)),
                (RecipeContentType.XpPerItemField, GameTypeValidationFixtures.Int(10)),
                (RecipeContentType.RepeatModeField, GameTypeValidationFixtures.Int(0))),
            GameTypeValidationFixtures.RowOf(
                curve,
                1,
                "off_roster",
                (SkillCurveContentType.SkillField, GameTypeValidationFixtures.Int(250)),
                (SkillCurveContentType.XpPerDamageField, GameTypeValidationFixtures.Int(4))));

        (int Id, string Code) locked = Assert.Single(
            GameTypeValidationFixtures.Codes(recipe.Validator!, recipe.Type, candidate));
        Assert.Equal((1, GameContentFindings.RecipeLockedSkill), locked);

        (int Id, string Code) unknown = Assert.Single(
            GameTypeValidationFixtures.Codes(curve.Validator!, curve.Type, candidate));
        Assert.Equal((1, GameContentFindings.SkillCurveUnknownSkill), unknown);
    }

    [Fact]
    public void TheStoreValidatorItBuildsReadsTheSameRegistryItRegisteredInto()
    {
        // The store validator resolves the engine item type's value position off the registry at validation
        // time, so the one-call path has to hand it the registry the candidate's types live in rather than
        // a schema of its own.
        ContentTypeRegistry registry = Registered();
        Assert.True(registry.TryGetByKey(GameContentTypeIds.StoreKey, out ContentTypeRegistration? store));
        Assert.True(registry.TryGetByKey(EngineContentTypes.ItemTypeKey, out ContentTypeRegistration? item));

        const int RichItemValue = int.MaxValue / 5;
        int ceiling = StoreContentType.LargestSafeRateBasisPoints(RichItemValue);

        ContentSnapshot candidate = GameTypeValidationFixtures.Snapshot(
            registry,
            GameTypeValidationFixtures.Item(item, 11, "crown", value: RichItemValue),
            GameTypeValidationFixtures.RowOf(
                store,
                1,
                "general",
                (StoreContentType.NpcKindField, GameTypeValidationFixtures.Int(7)),
                (StoreContentType.SellRateBasisPointsField, GameTypeValidationFixtures.Int(ceiling + 1)),
                (StoreContentType.BuyRateBasisPointsField, GameTypeValidationFixtures.Int(ceiling))));

        (int Id, string Code) over = Assert.Single(
            GameTypeValidationFixtures.Codes(store.Validator!, store.Type, candidate));
        Assert.Equal((1, GameContentFindings.StoreRateOverCurrencyCeiling), over);
    }

    [Fact]
    public void ANullRegistryOrOptionsIsRefused()
    {
        Assert.Throws<ArgumentNullException>(
            () => GameContentTypes.Register(null!, ContentDurationUnit.Ticks, Options()));

        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        Assert.Throws<ArgumentNullException>(
            () => GameContentTypes.Register(registry, ContentDurationUnit.Ticks, null!));
    }

    [Fact]
    public void RegisteringTwiceIsRefusedRatherThanQuietlyReplacing()
    {
        ContentTypeRegistry registry = Registered();

        Assert.Throws<ContentRegistrationException>(
            () => GameContentTypes.Register(registry, ContentDurationUnit.Ticks, Options()));
    }
}
