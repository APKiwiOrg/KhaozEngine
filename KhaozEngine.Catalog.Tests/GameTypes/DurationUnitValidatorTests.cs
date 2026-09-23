using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;
using static KhaozEngine.Tests.Catalog.GameTypes.GameTypeValidationFixtures;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// The two rules that read a duration, driven under BOTH units at the edges that decide them: below zero,
/// zero and the smallest positive number, which under Seconds is one hundredth of a second.
/// </summary>
/// <remarks>
/// Under Seconds a duration is a ScaledInt at scale 100, so both validators read hundredths. A positive scale
/// never moves a sign, so the stored integer has to be refused and accepted at exactly the numbers a tick
/// count is. Each row carries its duration in the kind its unit's schema declares, so the Seconds rows are
/// the rows a wall-clock game really stores.
/// <para>
/// These are the only two. The cross-type sweep and every other per-type rule read none of the four
/// duration fields, and <c>gathering_node</c> and <c>equip_profile</c> carry no rule about theirs.
/// </para>
/// </remarks>
public class DurationUnitValidatorTests
{
    static RecipeValidatorOptions Options() => new()
    {
        IsKnownRepeatMode = value => value is 0,
        IsPayableSkill = value => value is 1,
        IsOpenSkill = value => value is 1,
        IsNameableStation = value => value is 1,
    };

    [Theory]
    [InlineData(ContentDurationUnit.Ticks, -1L, true)]
    [InlineData(ContentDurationUnit.Ticks, 0L, true)]
    [InlineData(ContentDurationUnit.Ticks, 1L, false)]
    [InlineData(ContentDurationUnit.Seconds, -233L, true)]
    [InlineData(ContentDurationUnit.Seconds, -1L, true)]
    [InlineData(ContentDurationUnit.Seconds, 0L, true)]
    [InlineData(ContentDurationUnit.Seconds, 1L, false)]
    [InlineData(ContentDurationUnit.Seconds, 233L, false)]
    public void ARecipeDurationMustBePositiveUnderEitherUnit(ContentDurationUnit unit, long stored, bool refused)
    {
        ContentTypeRegistry registry = TypeRegistry(unit);
        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration recipe = Registration(registry, GameContentTypeIds.Recipe);

        ContentSnapshot candidate = Snapshot(
            registry,
            Item(item, 11, "logs"),
            RowOf(
                recipe,
                1,
                "planks",
                (RecipeContentType.DisplayOrderField, Int(1)),
                (RecipeContentType.SkillField, Int(1)),
                (RecipeContentType.LevelRequiredField, Int(1)),
                (RecipeContentType.PrimaryItemField, Ref(11)),
                (RecipeContentType.StationField, Int(1)),
                (RecipeContentType.BaseDurationField(unit), Duration(unit, stored)),
                (RecipeContentType.XpPerItemField, Int(10)),
                (RecipeContentType.RepeatModeField, Int(0))));

        (int Id, string Code)[] codes = Codes(new RecipeContentType.Validator(Options()), recipe.Type, candidate);

        // KGT0708 or nothing: every other field of the row is sound, so the duration alone decides it.
        (int Id, string Code)[] expected = refused ? [(1, GameContentFindings.RecipeBaseDurationNotPositive)] : [];
        Assert.Equal(expected, codes);
    }

    [Theory]
    [InlineData(ContentDurationUnit.Ticks, -1L, true)]
    [InlineData(ContentDurationUnit.Ticks, 0L, false)]
    [InlineData(ContentDurationUnit.Ticks, 1L, false)]
    [InlineData(ContentDurationUnit.Seconds, -233L, true)]
    [InlineData(ContentDurationUnit.Seconds, -1L, true)]
    [InlineData(ContentDurationUnit.Seconds, 0L, false)]
    [InlineData(ContentDurationUnit.Seconds, 1L, false)]
    [InlineData(ContentDurationUnit.Seconds, 233L, false)]
    public void AFoodDelayMayNotBeNegativeUnderEitherUnit(ContentDurationUnit unit, long stored, bool refused)
    {
        ContentTypeRegistry registry = TypeRegistry(unit);
        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration food = Registration(registry, GameContentTypeIds.Food);

        ContentSnapshot candidate = Snapshot(
            registry,
            Item(item, 11, "tuna"),
            RowOf(
                food,
                1,
                "cooked_tuna",
                (FoodContentType.ItemField, Ref(11)),
                (FoodContentType.HealsField, Int(4)),
                (FoodContentType.AttackDelayField(unit), Duration(unit, stored))));

        (int Id, string Code)[] codes = Codes(new FoodContentType.Validator(), food.Type, candidate);

        // KGT0102 or nothing. Zero is a legal delay, eating that holds nothing up, under either unit.
        (int Id, string Code)[] expected = refused ? [(1, GameContentFindings.FoodAttackDelayNegative)] : [];
        Assert.Equal(expected, codes);
    }
}
