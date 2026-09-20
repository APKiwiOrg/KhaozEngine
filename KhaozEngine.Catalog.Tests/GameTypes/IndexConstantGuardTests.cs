using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// Every internal positional constant of the package, pinned against the schema that produced it, under
/// BOTH duration units.
/// <para>
/// The constants stay internal because a position is not a consumer's to write down, but the package's own
/// validators index rows with them, so an off-by-one would read the neighbouring field and the neighbour
/// answers. This is the one place the two halves are compared, and it is written as a name plus an expected
/// constant so an edit to either side has to be made twice to pass.
/// </para>
/// </summary>
public class IndexConstantGuardTests
{
    /// <summary>One type's declared positions: its schema, its field count constant, and name to constant.</summary>
    sealed record TypeIndexes(
        string Key,
        ContentFieldSchema Schema,
        int FieldCount,
        IReadOnlyList<(string Field, int Index)> Positions);

    static IEnumerable<TypeIndexes> EveryType(ContentDurationUnit unit)
    {
        yield return new TypeIndexes(
            GameContentTypeIds.FoodKey,
            FoodContentType.CreateSchema(unit),
            FoodContentType.FieldCount,
            [
                (FoodContentType.ItemField, FoodContentType.ItemIndex),
                (FoodContentType.HealsField, FoodContentType.HealsIndex),
                (FoodContentType.AttackDelayField(unit), FoodContentType.AttackDelayIndex),
            ]);

        yield return new TypeIndexes(
            GameContentTypeIds.EquipProfileKey,
            EquipProfileContentType.CreateSchema(unit),
            EquipProfileContentType.FieldCount,
            [
                (EquipProfileContentType.SlotField, EquipProfileContentType.SlotIndex),
                (EquipProfileContentType.WeaponArchetypeField, EquipProfileContentType.WeaponArchetypeIndex),
                (EquipProfileContentType.AttackField(unit), EquipProfileContentType.AttackIndex),
            ]);

        yield return new TypeIndexes(
            GameContentTypeIds.EquipStatLineKey,
            EquipStatLineContentType.CreateSchema(),
            EquipStatLineContentType.FieldCount,
            [
                (EquipStatLineContentType.ProfileField, EquipStatLineContentType.ProfileIndex),
                (EquipStatLineContentType.StatField, EquipStatLineContentType.StatIndex),
                (EquipStatLineContentType.ValueField, EquipStatLineContentType.ValueIndex),
                (EquipStatLineContentType.SortField, EquipStatLineContentType.SortIndex),
            ]);

        yield return new TypeIndexes(
            GameContentTypeIds.StoreKey,
            StoreContentType.CreateSchema(),
            StoreContentType.FieldCount,
            [
                (StoreContentType.NpcKindField, StoreContentType.NpcKindIndex),
                (StoreContentType.SellRateBasisPointsField, StoreContentType.SellRateBasisPointsIndex),
                (StoreContentType.BuyRateBasisPointsField, StoreContentType.BuyRateBasisPointsIndex),
            ]);

        yield return new TypeIndexes(
            GameContentTypeIds.StoreShelfKey,
            StoreShelfContentType.CreateSchema(),
            StoreShelfContentType.FieldCount,
            [
                (StoreShelfContentType.StoreField, StoreShelfContentType.StoreIndex),
                (StoreShelfContentType.ItemField, StoreShelfContentType.ItemIndex),
                (StoreShelfContentType.SortField, StoreShelfContentType.SortIndex),
            ]);

        yield return new TypeIndexes(
            GameContentTypeIds.MonsterDropKey,
            MonsterDropContentType.CreateSchema(),
            MonsterDropContentType.FieldCount,
            [
                (MonsterDropContentType.MonsterKindField, MonsterDropContentType.MonsterKindIndex),
                (MonsterDropContentType.LootTableField, MonsterDropContentType.LootTableIndex),
            ]);

        yield return new TypeIndexes(
            GameContentTypeIds.GatheringNodeKey,
            GatheringNodeContentType.CreateSchema(unit),
            GatheringNodeContentType.FieldCount,
            [
                (GatheringNodeContentType.SkillField, GatheringNodeContentType.SkillIndex),
                (GatheringNodeContentType.ToolFamilyField, GatheringNodeContentType.ToolFamilyIndex),
                (GatheringNodeContentType.LevelRequiredField, GatheringNodeContentType.LevelRequiredIndex),
                (GatheringNodeContentType.BaseChanceBasisPointsField, GatheringNodeContentType.BaseChanceBasisPointsIndex),
                (GatheringNodeContentType.LivesField, GatheringNodeContentType.LivesIndex),
                (GatheringNodeContentType.LifeLossBasisPointsField, GatheringNodeContentType.LifeLossBasisPointsIndex),
                (GatheringNodeContentType.YieldXpField, GatheringNodeContentType.YieldXpIndex),
                (GatheringNodeContentType.YieldItemField, GatheringNodeContentType.YieldItemIndex),
                (GatheringNodeContentType.RespawnField(unit), GatheringNodeContentType.RespawnIndex),
            ]);

        yield return new TypeIndexes(
            GameContentTypeIds.RecipeKey,
            RecipeContentType.CreateSchema(unit),
            RecipeContentType.FieldCount,
            [
                (RecipeContentType.DisplayOrderField, RecipeContentType.DisplayOrderIndex),
                (RecipeContentType.SkillField, RecipeContentType.SkillIndex),
                (RecipeContentType.LevelRequiredField, RecipeContentType.LevelRequiredIndex),
                (RecipeContentType.PrimaryItemField, RecipeContentType.PrimaryItemIndex),
                (RecipeContentType.StationField, RecipeContentType.StationIndex),
                (RecipeContentType.BaseDurationField(unit), RecipeContentType.BaseDurationIndex),
                (RecipeContentType.XpPerItemField, RecipeContentType.XpPerItemIndex),
                (RecipeContentType.RepeatModeField, RecipeContentType.RepeatModeIndex),
            ]);

        yield return new TypeIndexes(
            GameContentTypeIds.RecipeInputKey,
            RecipeInputContentType.CreateSchema(),
            RecipeInputContentType.FieldCount,
            [
                (RecipeInputContentType.RecipeField, RecipeInputContentType.RecipeIndex),
                (RecipeInputContentType.ItemField, RecipeInputContentType.ItemIndex),
                (RecipeInputContentType.CountField, RecipeInputContentType.CountIndex),
                (RecipeInputContentType.SortField, RecipeInputContentType.SortIndex),
            ]);

        yield return new TypeIndexes(
            GameContentTypeIds.RecipeOutputKey,
            RecipeOutputContentType.CreateSchema(),
            RecipeOutputContentType.FieldCount,
            [
                (RecipeOutputContentType.RecipeField, RecipeOutputContentType.RecipeIndex),
                (RecipeOutputContentType.ItemField, RecipeOutputContentType.ItemIndex),
                (RecipeOutputContentType.CountField, RecipeOutputContentType.CountIndex),
                (RecipeOutputContentType.SortField, RecipeOutputContentType.SortIndex),
            ]);

        yield return new TypeIndexes(
            GameContentTypeIds.ToolTierKey,
            ToolTierContentType.CreateSchema(),
            ToolTierContentType.FieldCount,
            [
                (ToolTierContentType.ItemField, ToolTierContentType.ItemIndex),
                (ToolTierContentType.FamilyField, ToolTierContentType.FamilyIndex),
                (ToolTierContentType.RankField, ToolTierContentType.RankIndex),
                (ToolTierContentType.SuccessScaleBasisPointsField, ToolTierContentType.SuccessScaleBasisPointsIndex),
                (ToolTierContentType.TimeScaleBasisPointsField, ToolTierContentType.TimeScaleBasisPointsIndex),
            ]);

        yield return new TypeIndexes(
            GameContentTypeIds.SkillCurveKey,
            SkillCurveContentType.CreateSchema(),
            SkillCurveContentType.FieldCount,
            [
                (SkillCurveContentType.SkillField, SkillCurveContentType.SkillIndex),
                (SkillCurveContentType.XpPerDamageField, SkillCurveContentType.XpPerDamageIndex),
            ]);

        yield return new TypeIndexes(
            GameContentTypeIds.GameTuningKey,
            GameTuningContentType.CreateSchema(),
            GameTuningContentType.FieldCount,
            [
                (GameTuningContentType.ValueField, GameTuningContentType.ValueIndex),
            ]);
    }

    [Theory]
    [InlineData(ContentDurationUnit.Ticks)]
    [InlineData(ContentDurationUnit.Seconds)]
    public void EveryIndexConstantIsThePositionItsOwnSchemaGivesTheField(ContentDurationUnit unit)
    {
        foreach (TypeIndexes type in EveryType(unit))
        {
            foreach ((string field, int index) in type.Positions)
            {
                Assert.Equal(type.Schema.IndexOf(field), index);
            }
        }
    }

    [Theory]
    [InlineData(ContentDurationUnit.Ticks)]
    [InlineData(ContentDurationUnit.Seconds)]
    public void EveryFieldCountConstantIsItsSchemasFieldCount(ContentDurationUnit unit)
    {
        foreach (TypeIndexes type in EveryType(unit))
        {
            Assert.Equal(type.Schema.Fields.Count, type.FieldCount);
        }
    }

    [Theory]
    [InlineData(ContentDurationUnit.Ticks)]
    [InlineData(ContentDurationUnit.Seconds)]
    public void EveryFieldOfEveryTypeCarriesExactlyOneConstant(ContentDurationUnit unit)
    {
        // Without this a field could gain a position and lose its pin in the same edit, and the two checks
        // above would stay green over a constant nothing names any more.
        foreach (TypeIndexes type in EveryType(unit))
        {
            Assert.Equal(
                type.Schema.Fields.Select(f => f.Name).ToArray(),
                type.Positions.OrderBy(p => p.Index).Select(p => p.Field).ToArray());
        }
    }

    [Fact]
    public void ThePinnedTypesAreEveryTypeTheIdTableDeclares()
    {
        // A type added to the id table with no constants pinned here is a failing test rather than a type
        // whose positions nothing ever compares.
        Assert.Equal(
            GameContentTypeIds.TypeKeys.Select(t => t.Key).ToArray(),
            EveryType(ContentDurationUnit.Ticks).Select(t => t.Key).ToArray());
    }
}
