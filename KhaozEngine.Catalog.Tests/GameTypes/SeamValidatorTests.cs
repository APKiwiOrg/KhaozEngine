using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;
using static KhaozEngine.Tests.Catalog.GameTypes.GameTypeValidationFixtures;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// The two validators that need a GAME answer: <c>recipe</c>, through
/// <see cref="RecipeValidatorOptions"/>, and <c>skill_curve</c>, through one predicate.
/// </summary>
/// <remarks>
/// Every seam gets a fact of its own proving the predicate is CONSULTED: one that answers false raises the
/// code and one that answers true does not. A seam nothing asks is a rule that silently never fires, which
/// is worse than no rule at all, because a publish reports clean and a boot then meets the row the check
/// existed for.
/// <para>
/// The predicates here are test literals. A roster, an enum and a station list are the game's, and the whole
/// point of the seam is that none of them is in the package for a test to reach for.
/// </para>
/// </remarks>
public class SeamValidatorTests
{
    /// <summary>Skill 1 and skill 2 take recipes. Skill 2 is not open yet.</summary>
    static RecipeValidatorOptions Options() => new()
    {
        IsKnownRepeatMode = value => value is 0 or 1,
        IsPayableSkill = value => value is 1 or 2,
        IsOpenSkill = value => value is 1,
        IsNameableStation = value => value is 1 or 2,
    };

    [Fact]
    public void RecipeRaisesEveryOneOfItsTenCodes()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration recipe = Registration(registry, GameContentTypeIds.Recipe);

        ContentSnapshot candidate = Snapshot(
            registry,
            Item(item, 11, "logs"),
            Item(item, 12, "ashen_logs", isRetired: true),
            Recipe(recipe, 1, order: 1),
            Recipe(recipe, 2, order: 1),
            Recipe(recipe, 3, order: 3, skill: 9),
            Recipe(recipe, 4, order: 4, skill: 2),
            Recipe(recipe, 5, order: 5, level: 0),
            Recipe(recipe, 6, order: 6, item: 12),
            Recipe(recipe, 7, order: 7, station: RecipeValidatorOptions.NoStation),
            Recipe(recipe, 8, order: 8, station: 7),
            Recipe(recipe, 9, order: 9, duration: 0),
            Recipe(recipe, 10, order: 10, xp: 0),
            Recipe(recipe, 11, order: 11, repeatMode: 5));

        // One pass, every rule, each on the one row that broke it.
        Assert.Equal(
            new[]
            {
                (2, GameContentFindings.RecipeDuplicateDisplayOrder),
                (3, GameContentFindings.RecipeSkillNotPayable),
                (4, GameContentFindings.RecipeLockedSkill),
                (5, GameContentFindings.RecipeLevelRequiredBelowOne),
                (6, GameContentFindings.RecipeRetiredPrimaryItem),
                (7, GameContentFindings.RecipeStationNone),
                (8, GameContentFindings.RecipeUnknownStation),
                (9, GameContentFindings.RecipeBaseDurationNotPositive),
                (10, GameContentFindings.RecipeXpPerItemNotPositive),
                (11, GameContentFindings.RecipeUnknownRepeatMode),
            },
            Codes(new RecipeContentType.Validator(Options()), recipe.Type, candidate));
    }

    [Fact]
    public void ThePayableSkillPredicateIsConsultedAndKeepsItsCodeApartFromTheOpenOne()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration recipe = Registration(registry, GameContentTypeIds.Recipe);
        ContentSnapshot candidate = Snapshot(registry, Recipe(recipe, 1, order: 1, skill: 77));

        // False raises 0702 and never 0703: a number nothing stands for is not a locked skill.
        var refused = new RecipeValidatorOptions
        {
            IsKnownRepeatMode = _ => true,
            IsPayableSkill = _ => false,
            IsOpenSkill = _ => throw new InvalidOperationException(
                "The open-skill predicate is asked only of a skill the payable one already accepted."),
            IsNameableStation = _ => true,
        };
        (int Id, string Code) only = Assert.Single(
            Codes(new RecipeContentType.Validator(refused), recipe.Type, candidate));
        Assert.Equal((1, GameContentFindings.RecipeSkillNotPayable), only);

        // True says nothing, which is what makes the predicate the thing that decided it.
        var accepted = new RecipeValidatorOptions
        {
            IsKnownRepeatMode = _ => true,
            IsPayableSkill = _ => true,
            IsOpenSkill = _ => true,
            IsNameableStation = _ => true,
        };
        Assert.Empty(Codes(new RecipeContentType.Validator(accepted), recipe.Type, candidate));
    }

    [Fact]
    public void TheOpenSkillPredicateIsConsulted()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration recipe = Registration(registry, GameContentTypeIds.Recipe);
        // Skill 1 is payable and open under the shared options, so the only thing that can move this row is
        // the open predicate answering differently.
        ContentSnapshot candidate = Snapshot(registry, Recipe(recipe, 1, order: 1, skill: 1));

        var closed = new RecipeValidatorOptions
        {
            IsKnownRepeatMode = _ => true,
            IsPayableSkill = _ => true,
            IsOpenSkill = _ => false,
            IsNameableStation = _ => true,
        };
        (int Id, string Code) only = Assert.Single(
            Codes(new RecipeContentType.Validator(closed), recipe.Type, candidate));
        Assert.Equal((1, GameContentFindings.RecipeLockedSkill), only);

        Assert.Empty(Codes(new RecipeContentType.Validator(Options()), recipe.Type, candidate));
    }

    [Fact]
    public void TheStationPredicateIsConsultedAndIsNeverAskedAboutTheNoStationValue()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration recipe = Registration(registry, GameContentTypeIds.Recipe);

        var asked = new List<long>();
        var options = new RecipeValidatorOptions
        {
            IsKnownRepeatMode = _ => true,
            IsPayableSkill = _ => true,
            IsOpenSkill = _ => true,
            IsNameableStation = value =>
            {
                asked.Add(value);
                return false;
            },
        };

        ContentSnapshot candidate = Snapshot(
            registry,
            Recipe(recipe, 1, order: 1, station: RecipeValidatorOptions.NoStation),
            Recipe(recipe, 2, order: 2, station: 6));

        Assert.Equal(
            new[]
            {
                (1, GameContentFindings.RecipeStationNone),
                (2, GameContentFindings.RecipeUnknownStation),
            },
            Codes(new RecipeContentType.Validator(options), recipe.Type, candidate));

        // Zero is reserved and answered here, so a game never has to decide what it means.
        Assert.Equal(new long[] { 6 }, asked);

        // A predicate that accepts station 6 leaves that row alone, and the reserved value is still
        // refused, which is what keeps the two codes apart.
        var nameable = new RecipeValidatorOptions
        {
            IsKnownRepeatMode = _ => true,
            IsPayableSkill = _ => true,
            IsOpenSkill = _ => true,
            IsNameableStation = _ => true,
        };
        Assert.Equal(
            new[] { (1, GameContentFindings.RecipeStationNone) },
            Codes(new RecipeContentType.Validator(nameable), recipe.Type, candidate));
    }

    [Fact]
    public void TheRepeatModePredicateIsConsulted()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration recipe = Registration(registry, GameContentTypeIds.Recipe);
        ContentSnapshot candidate = Snapshot(registry, Recipe(recipe, 1, order: 1, repeatMode: 3));

        var unknown = new RecipeValidatorOptions
        {
            IsKnownRepeatMode = _ => false,
            IsPayableSkill = _ => true,
            IsOpenSkill = _ => true,
            IsNameableStation = _ => true,
        };
        (int Id, string Code) only = Assert.Single(
            Codes(new RecipeContentType.Validator(unknown), recipe.Type, candidate));
        Assert.Equal((1, GameContentFindings.RecipeUnknownRepeatMode), only);

        var known = new RecipeValidatorOptions
        {
            IsKnownRepeatMode = _ => true,
            IsPayableSkill = _ => true,
            IsOpenSkill = _ => true,
            IsNameableStation = _ => true,
        };
        Assert.Empty(Codes(new RecipeContentType.Validator(known), recipe.Type, candidate));
    }

    [Fact]
    public void SkillCurveRaisesEveryOneOfItsThreeCodes()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration curve = Registration(registry, GameContentTypeIds.SkillCurve);

        ContentSnapshot candidate = Snapshot(
            registry,
            Curve(curve, 1, "first", skill: 1, xpPerDamage: 4),
            Curve(curve, 2, "second", skill: 1, xpPerDamage: 4),
            Curve(curve, 3, "unknown", skill: 250, xpPerDamage: 4),
            Curve(curve, 4, "unpaid", skill: 2, xpPerDamage: 0));

        Assert.Equal(
            new[]
            {
                (2, GameContentFindings.SkillCurveDuplicateSkill),
                (3, GameContentFindings.SkillCurveUnknownSkill),
                (4, GameContentFindings.SkillCurveXpPerDamageNotPositive),
            },
            Codes(new SkillCurveContentType.Validator(KnownSkill), curve.Type, candidate));
    }

    [Fact]
    public void TheKnownSkillPredicateIsConsultedAndTheMessageNamesTheRawNumber()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration curve = Registration(registry, GameContentTypeIds.SkillCurve);
        ContentSnapshot candidate = Snapshot(registry, Curve(curve, 1, "odd", skill: 42, xpPerDamage: 4));

        ContentFinding only = Assert.Single(
            Findings(new SkillCurveContentType.Validator(_ => false), curve.Type, candidate));
        Assert.Equal(GameContentFindings.SkillCurveUnknownSkill, only.Code);

        // The RAW number, because a name for it would be the game's vocabulary and the number is what the
        // row carries and what an author edits.
        Assert.Contains("42", only.Message, StringComparison.Ordinal);

        Assert.Empty(Codes(new SkillCurveContentType.Validator(_ => true), curve.Type, candidate));
    }

    /// <summary>
    /// Every validator that touches a duration field behaves IDENTICALLY under both units.
    /// </summary>
    /// <remarks>
    /// The unit picks the field's NAME and nothing else: order, kind, visibility and the codec are the same
    /// either way, and the rules about a duration are about its SIGN. So the same rows under the two
    /// spellings have to produce the same findings in the same order, which is what lets a fixed-tick world
    /// and a wall-clock one share every one of these rules.
    /// </remarks>
    [Fact]
    public void EveryDurationTouchingValidatorAnswersTheSameUnderTicksAndSeconds()
    {
        (int Id, string Code)[] ticks = DurationFindings(ContentDurationUnit.Ticks);
        (int Id, string Code)[] seconds = DurationFindings(ContentDurationUnit.Seconds);

        Assert.Equal(ticks, seconds);

        // And the run is not vacuous: the three duration-touching types each reported something.
        Assert.Equal(
            new[]
            {
                GameContentFindings.FoodAttackDelayNegative,
                GameContentFindings.GatheringNodeLivesNotPositive,
                GameContentFindings.RecipeBaseDurationNotPositive,
            },
            ticks.Select(f => f.Code).ToArray());
    }

    /// <summary>
    /// The three duration-touching validators over one candidate built under <paramref name="unit"/>, each
    /// row filled BY THE NAME the unit selects.
    /// </summary>
    static (int Id, string Code)[] DurationFindings(ContentDurationUnit unit)
    {
        ContentTypeRegistry registry = TypeRegistry(unit);
        ContentTypeRegistration item = Registration(registry, EngineContentTypes.ItemTypeId);
        ContentTypeRegistration food = Registration(registry, GameContentTypeIds.Food);
        ContentTypeRegistration node = Registration(registry, GameContentTypeIds.GatheringNode);
        ContentTypeRegistration recipe = Registration(registry, GameContentTypeIds.Recipe);

        ContentSnapshot candidate = Snapshot(
            registry,
            Item(item, 11, "logs"),
            RowOf(
                food,
                1,
                "cursed_tuna",
                (FoodContentType.ItemField, Ref(11)),
                (FoodContentType.HealsField, Int(4)),
                (FoodContentType.AttackDelayField(unit), Int(-1))),
            RowOf(
                node,
                1,
                "spent",
                (GatheringNodeContentType.SkillField, Int(1)),
                (GatheringNodeContentType.ToolFamilyField, Ref(1)),
                (GatheringNodeContentType.LevelRequiredField, Int(1)),
                (GatheringNodeContentType.BaseChanceBasisPointsField, Int(5_000)),
                (GatheringNodeContentType.LivesField, Int(0)),
                (GatheringNodeContentType.LifeLossBasisPointsField, Int(10_000)),
                (GatheringNodeContentType.YieldXpField, Int(25)),
                (GatheringNodeContentType.YieldItemField, Ref(11)),
                (GatheringNodeContentType.RespawnField(unit), Int(10))),
            RowOf(
                recipe,
                1,
                "timeless",
                (RecipeContentType.DisplayOrderField, Int(1)),
                (RecipeContentType.SkillField, Int(1)),
                (RecipeContentType.LevelRequiredField, Int(1)),
                (RecipeContentType.PrimaryItemField, Ref(11)),
                (RecipeContentType.StationField, Int(1)),
                (RecipeContentType.BaseDurationField(unit), Int(0)),
                (RecipeContentType.XpPerItemField, Int(10)),
                (RecipeContentType.RepeatModeField, Int(0))));

        var found = new List<(int, string)>();
        found.AddRange(Codes(new FoodContentType.Validator(), food.Type, candidate));
        found.AddRange(Codes(new GatheringNodeContentType.Validator(), node.Type, candidate));
        found.AddRange(Codes(new RecipeContentType.Validator(Options()), recipe.Type, candidate));
        return found.ToArray();
    }

    static bool KnownSkill(long value) => value is > 0 and < 100;

    static ContentRow Recipe(
        ContentTypeRegistration recipe,
        int id,
        int order,
        int skill = 1,
        int level = 1,
        int item = 11,
        long station = 1,
        int duration = 4,
        int xp = 10,
        int repeatMode = 0,
        ContentDurationUnit unit = ContentDurationUnit.Ticks)
        => RowOf(
            recipe,
            id,
            FormattableString.Invariant($"recipe_{id}"),
            (RecipeContentType.DisplayOrderField, Int(order)),
            (RecipeContentType.SkillField, Int(skill)),
            (RecipeContentType.LevelRequiredField, Int(level)),
            (RecipeContentType.PrimaryItemField, Ref(item)),
            (RecipeContentType.StationField, Int(station)),
            (RecipeContentType.BaseDurationField(unit), Int(duration)),
            (RecipeContentType.XpPerItemField, Int(xp)),
            (RecipeContentType.RepeatModeField, Int(repeatMode)));

    static ContentRow Curve(
        ContentTypeRegistration curve,
        int id,
        string key,
        int skill,
        int xpPerDamage)
        => RowOf(
            curve,
            id,
            key,
            (SkillCurveContentType.SkillField, Int(skill)),
            (SkillCurveContentType.XpPerDamageField, Int(xpPerDamage)));
}
