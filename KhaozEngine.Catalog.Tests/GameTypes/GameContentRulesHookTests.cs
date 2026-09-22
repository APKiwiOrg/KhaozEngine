using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;
using static KhaozEngine.Tests.Catalog.GameTypes.GameTypeValidationFixtures;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// The hook a game adds its own whole-catalog rules through: <see cref="GameContentSweepOptions.GameRules"/>,
/// run in the sweep's slot after every rule of the package's own.
/// </summary>
/// <remarks>
/// The game rule here is a test literal with a made-up code. What a game's rule says is the game's, and the
/// hook is only about WHERE it runs, over WHAT, into WHICH list and in what ORDER.
/// </remarks>
public class GameContentRulesHookTests
{
    const string GameCode = "XYZ0001";

    [Fact]
    public void AGameRuleRunsAfterThePackageRulesOverTheSameCandidateIntoTheSameFindings()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration recipe = Registration(registry, GameContentTypeIds.Recipe);
        ContentSnapshot candidate = Snapshot(registry, Recipe(recipe, 1));

        var rule = new RecordingRule();
        var slot = new ContentTypeId(GameContentTypeIds.Food);
        var findings = new List<ContentFinding>();
        new GameContentChecks(registry, new GameContentSweepOptions { GameRules = [rule] })
            .Validate(slot, candidate, findings);

        // The package's own finding first, then the game's, in the one list.
        Assert.Equal(
            new[] { GameContentFindings.SweepRecipeWithoutOutput, GameCode },
            findings.Select(f => f.Code).ToArray());

        // Handed exactly what the sweep was handed, and it saw the package's finding already in the list.
        RecordingRule.Call call = Assert.Single(rule.Calls);
        Assert.Equal(slot, call.Type);
        Assert.Same(candidate, call.Candidate);
        Assert.Same(findings, call.Findings);
        Assert.Equal(1, call.FindingsBefore);
    }

    [Fact]
    public void AGameRuleFindingSurfacesInThePublishReportThroughTheOneCallRegistration()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        GameContentTypes.Register(registry, ContentDurationUnit.Ticks, Options(new GameContentSweepOptions
        {
            GameRules = [new RecordingRule()],
        }));

        ContentValidationReport report = ContentValidator.Validate(Snapshot(registry), null, [], registry);

        // Once, under KEC0040, prefixed by the slot's type key with the game's own code in the message.
        Assert.False(report.IsValid);
        ContentFinding only = Assert.Single(
            report.Findings,
            f => string.Equals(f.Code, ContentValidator.TypeValidatorCode, StringComparison.Ordinal));
        Assert.StartsWith(GameContentTypeIds.FoodKey + ": " + GameCode, only.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoGameRuleChangesNothing()
    {
        Assert.Empty(new GameContentSweepOptions().GameRules);
        Assert.Empty(GameContentSweepOptions.None.GameRules);

        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration recipe = Registration(registry, GameContentTypeIds.Recipe);
        ContentSnapshot candidate = Snapshot(registry, Recipe(recipe, 1), Recipe(recipe, 2));
        var slot = new ContentTypeId(GameContentTypeIds.Food);

        (int, string, string)[] without = Rows(Findings(
            new GameContentChecks(registry, GameContentSweepOptions.None), slot, candidate));
        (int, string, string)[] empty = Rows(Findings(
            new GameContentChecks(registry, new GameContentSweepOptions { GameRules = [] }), slot, candidate));

        Assert.Equal(2, without.Length);
        Assert.Equal(without, empty);

        static (int, string, string)[] Rows(List<ContentFinding> found)
            => found.Select(f => (f.Id, f.Code, f.Message)).ToArray();
    }

    /// <summary>
    /// A game registering BY HAND reaches its rules only through <see cref="GameContentChecks"/>, so mounting
    /// them cannot leave the package's sweep out: food's own rules, the sweep and the game's rule all reach
    /// the report.
    /// </summary>
    /// <remarks>
    /// The other by-hand shape, <c>food</c> registered with <see cref="FoodContentType.Validator"/> alone, has
    /// no sweep and no game rule at all, which is what the warning on the by-hand samples says. The last
    /// assertion pins that too, so the warning stays true.
    /// </remarks>
    [Fact]
    public void RegisteringByHandCannotMountAGameRuleWithoutThePackageSweep()
    {
        ContentValidationReport withHook = ByHand(food => new GameContentChecks(
            food,
            new GameContentSweepOptions { GameRules = [new RecordingRule()] },
            new FoodContentType.Validator()));

        string[] codes = CodesIn(withHook);
        Assert.Equal(
            new[] { GameContentFindings.FoodHealsNotPositive, GameContentFindings.SweepRecipeWithoutOutput, GameCode },
            codes);

        ContentValidationReport foodAlone = ByHand(_ => new FoodContentType.Validator());
        Assert.Equal(new[] { GameContentFindings.FoodHealsNotPositive }, CodesIn(foodAlone));

        static ContentValidationReport ByHand(Func<ContentTypeRegistry, IContentValidator> foodSlot)
        {
            var registry = new ContentTypeRegistry();
            EngineContentTypes.Register(registry);
            FoodContentType.Register(registry, ContentDurationUnit.Ticks, foodSlot(registry));
            RecipeContentType.Register(registry, ContentDurationUnit.Ticks, validator: null);
            RecipeOutputContentType.Register(registry, validator: null);

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
                Recipe(recipe, 1));
            return ContentValidator.Validate(candidate, null, [], registry);
        }

        static string[] CodesIn(ContentValidationReport report)
            => report.Findings
                .Where(f => string.Equals(f.Code, ContentValidator.TypeValidatorCode, StringComparison.Ordinal))
                .Select(f => f.Message.Split(' ')[1])
                .ToArray();
    }

    [Fact]
    public void ANullGameRuleListOrMemberIsRefusedAtConstruction()
    {
        ContentTypeRegistry registry = TypeRegistry();
        Assert.Throws<ArgumentException>(
            () => new GameContentChecks(registry, new GameContentSweepOptions { GameRules = null! }));
        Assert.Throws<ArgumentException>(
            () => new GameContentChecks(registry, new GameContentSweepOptions { GameRules = [null!] }));
    }

    static GameContentOptions Options(GameContentSweepOptions sweep) => new()
    {
        Recipe = new RecipeValidatorOptions
        {
            IsKnownRepeatMode = _ => true,
            IsPayableSkill = _ => true,
            IsOpenSkill = _ => true,
            IsNameableStation = _ => true,
        },
        IsKnownSkill = _ => true,
        Sweep = sweep,
    };

    static ContentRow Recipe(ContentTypeRegistration recipe, int id)
        => RowOf(
            recipe,
            id,
            FormattableString.Invariant($"recipe_{id}"),
            (RecipeContentType.DisplayOrderField, Int(id)),
            (RecipeContentType.SkillField, Int(1)),
            (RecipeContentType.LevelRequiredField, Int(1)),
            (RecipeContentType.PrimaryItemField, Ref(11)),
            (RecipeContentType.StationField, Int(1)),
            (RecipeContentType.BaseTicksField, Int(4)),
            (RecipeContentType.XpPerItemField, Int(10)),
            (RecipeContentType.RepeatModeField, Int(0)));

    /// <summary>A game rule that reports one finding and records what it was handed.</summary>
    sealed class RecordingRule : IContentValidator
    {
        internal readonly record struct Call(
            ContentTypeId Type,
            IContentSnapshot Candidate,
            ICollection<ContentFinding> Findings,
            int FindingsBefore);

        internal List<Call> Calls { get; } = [];

        public void Validate(ContentTypeId type, IContentSnapshot candidate, ICollection<ContentFinding> findings)
        {
            Calls.Add(new Call(type, candidate, findings, findings.Count));
            findings.Add(new ContentFinding(type, 0, GameCode, "A rule only this game states."));
        }
    }
}
