using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// The STORED schema of the nine leaf game types, pinned field by field: the name, the declared order, the
/// kind, the reference target, the visibility, the required flag and the scale, plus each type's chunk slots
/// and type-level visibility.
/// <para>
/// Every expectation here is a LITERAL. A field name is what a localization key is derived from and what a
/// reader resolves by, a field's position is what a row's values are parallel to, and a reference target is
/// what the publish validator resolves, so none of them may move without going red here first. Reading them
/// back off the constants that produced them would pin nothing.
/// </para>
/// </summary>
public class LeafSchemaGoldenTests
{
    /// <summary>One expected field, in the order a schema declares it.</summary>
    sealed record Field(
        string Name,
        ContentFieldKind Kind,
        string? ReferenceTarget,
        ContentVisibility Visibility,
        bool Required,
        int Scale = 1);

    static void AssertSchema(ContentFieldSchema schema, params Field[] expected)
    {
        Assert.Equal(
            expected,
            schema.Fields
                .Select(f => new Field(f.Name, f.Kind, f.ReferenceTarget, f.Visibility, f.Required, f.Scale))
                .ToArray());
    }

    const ContentDurationUnit Ticks = ContentDurationUnit.Ticks;

    [Fact]
    public void Food()
    {
        Assert.Equal(256, FoodContentType.DefaultChunkSlots);
        Assert.Equal(ContentVisibility.Client, FoodContentType.DefaultVisibility);

        AssertSchema(
            FoodContentType.CreateSchema(Ticks),
            new Field("item", ContentFieldKind.KeyReference, "item", ContentVisibility.Client, true),
            new Field("heals", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("attack_delay_ticks", ContentFieldKind.Int, null, ContentVisibility.Client, true));
    }

    [Fact]
    public void EquipProfile()
    {
        Assert.Equal(256, EquipProfileContentType.DefaultChunkSlots);
        Assert.Equal(ContentVisibility.Client, EquipProfileContentType.DefaultVisibility);

        AssertSchema(
            EquipProfileContentType.CreateSchema(Ticks),
            new Field("slot", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("weapon_archetype", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("attack_ticks", ContentFieldKind.Int, null, ContentVisibility.Client, true));
    }

    [Fact]
    public void Store()
    {
        Assert.Equal(256, StoreContentType.DefaultChunkSlots);
        Assert.Equal(ContentVisibility.Client, StoreContentType.DefaultVisibility);

        AssertSchema(
            StoreContentType.CreateSchema(),
            new Field("npc_kind", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("sell_rate_bp", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("buy_rate_bp", ContentFieldKind.Int, null, ContentVisibility.Client, true));
    }

    [Fact]
    public void MonsterDrop()
    {
        Assert.Equal(256, MonsterDropContentType.DefaultChunkSlots);

        // ServerOnly at the TYPE level, which keeps the whole family out of every client manifest rather
        // than merely out of a client's view of a row.
        Assert.Equal(ContentVisibility.ServerOnly, MonsterDropContentType.DefaultVisibility);

        AssertSchema(
            MonsterDropContentType.CreateSchema(),
            new Field("monster_kind", ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true),
            new Field("loot_table", ContentFieldKind.KeyReference, "loot_table", ContentVisibility.ServerOnly, true));
    }

    [Fact]
    public void GatheringNode()
    {
        Assert.Equal(256, GatheringNodeContentType.DefaultChunkSlots);
        Assert.Equal(ContentVisibility.Client, GatheringNodeContentType.DefaultVisibility);

        AssertSchema(
            GatheringNodeContentType.CreateSchema(Ticks),
            new Field("skill", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("tool_family", ContentFieldKind.KeyReference, "tag", ContentVisibility.Client, true),
            new Field("level_required", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("base_chance_bp", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("lives", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("life_loss_bp", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("yield_xp", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("yield_item", ContentFieldKind.KeyReference, "item", ContentVisibility.Client, true),
            new Field("respawn_ticks", ContentFieldKind.Int, null, ContentVisibility.Client, true));
    }

    [Fact]
    public void Recipe()
    {
        Assert.Equal(512, RecipeContentType.DefaultChunkSlots);
        Assert.Equal(ContentVisibility.Client, RecipeContentType.DefaultVisibility);

        AssertSchema(
            RecipeContentType.CreateSchema(Ticks),
            new Field("display_order", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("skill", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("level_required", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("primary_item", ContentFieldKind.KeyReference, "item", ContentVisibility.Client, true),
            new Field("station", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("base_ticks", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("xp_per_item", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("repeat_mode", ContentFieldKind.Int, null, ContentVisibility.Client, true));
    }

    [Fact]
    public void ToolTier()
    {
        Assert.Equal(256, ToolTierContentType.DefaultChunkSlots);
        Assert.Equal(ContentVisibility.Client, ToolTierContentType.DefaultVisibility);

        AssertSchema(
            ToolTierContentType.CreateSchema(),
            new Field("item", ContentFieldKind.KeyReference, "item", ContentVisibility.Client, true),
            new Field("family", ContentFieldKind.KeyReference, "tag", ContentVisibility.Client, true),
            new Field("rank", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("success_scale_bp", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("time_scale_bp", ContentFieldKind.Int, null, ContentVisibility.Client, true));
    }

    [Fact]
    public void SkillCurve()
    {
        Assert.Equal(256, SkillCurveContentType.DefaultChunkSlots);
        Assert.Equal(ContentVisibility.Client, SkillCurveContentType.DefaultVisibility);

        // xp_per_damage is the ONE optional field across all thirteen types: a knob most skills have no use
        // for, where an absent value has to stay distinguishable from an authored zero.
        AssertSchema(
            SkillCurveContentType.CreateSchema(),
            new Field("skill", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("xp_per_damage", ContentFieldKind.Int, null, ContentVisibility.Client, false));
    }

    [Fact]
    public void GameTuning()
    {
        Assert.Equal(256, GameTuningContentType.DefaultChunkSlots);
        Assert.Equal(ContentVisibility.Client, GameTuningContentType.DefaultVisibility);

        AssertSchema(
            GameTuningContentType.CreateSchema(),
            new Field("value", ContentFieldKind.ScaledInt, null, ContentVisibility.Client, true, 100));
    }

    [Fact]
    public void EveryLeafFieldIsRequiredExceptTheOneCurveKnob()
    {
        string[] optional = EverySchema(Ticks)
            .SelectMany(schema => schema.Fields)
            .Where(field => !field.Required)
            .Select(field => field.Name)
            .ToArray();

        Assert.Equal(new[] { "xp_per_damage" }, optional);
    }

    [Fact]
    public void SecondsMovesTheFourDurationNamesAndNothingElse()
    {
        // The unit picks a field NAME. Order, kinds, reference targets, visibility, required flags and
        // scales are identical under either unit, which is what lets one reader serve both.
        ContentFieldSchema[] ticks = EverySchema(ContentDurationUnit.Ticks).ToArray();
        ContentFieldSchema[] seconds = EverySchema(ContentDurationUnit.Seconds).ToArray();

        Assert.Equal(ticks.Length, seconds.Length);
        var moved = new List<(string Ticks, string Seconds)>();
        for (int type = 0; type < ticks.Length; type++)
        {
            Assert.Equal(ticks[type].Fields.Count, seconds[type].Fields.Count);
            for (int i = 0; i < ticks[type].Fields.Count; i++)
            {
                ContentFieldEntry left = ticks[type].Fields[i];
                ContentFieldEntry right = seconds[type].Fields[i];
                Assert.Equal(left with { Name = right.Name }, right);
                if (!string.Equals(left.Name, right.Name, System.StringComparison.Ordinal))
                {
                    moved.Add((left.Name, right.Name));
                }
            }
        }

        Assert.Equal(
            new[]
            {
                ("attack_delay_ticks", "attack_delay_seconds"),
                ("attack_ticks", "attack_seconds"),
                ("respawn_ticks", "respawn_seconds"),
                ("base_ticks", "base_seconds"),
            },
            moved.ToArray());
    }

    [Fact]
    public void ADurationNameUnderAnUndefinedUnitIsRefused()
    {
        // An enum value is only a number, so a cast of anything reaches these accessors. Falling back to
        // one of the two spellings would build a schema carrying a field name nobody authored against, and
        // a reader resolving the other name would then refuse a pack that decodes perfectly.
        var undefined = (ContentDurationUnit)7;

        Assert.Throws<System.ArgumentOutOfRangeException>(() => FoodContentType.AttackDelayField(undefined));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => EquipProfileContentType.AttackField(undefined));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => GatheringNodeContentType.RespawnField(undefined));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => RecipeContentType.BaseDurationField(undefined));

        // And so does every schema that carries one, rather than the name alone.
        Assert.Throws<System.ArgumentOutOfRangeException>(() => FoodContentType.CreateSchema(undefined));
    }

    /// <summary>The nine leaf schemas, ASCENDING by type id, which is the order the moved names are read in.</summary>
    static IEnumerable<ContentFieldSchema> EverySchema(ContentDurationUnit unit)
    {
        yield return FoodContentType.CreateSchema(unit);
        yield return EquipProfileContentType.CreateSchema(unit);
        yield return StoreContentType.CreateSchema();
        yield return MonsterDropContentType.CreateSchema();
        yield return GatheringNodeContentType.CreateSchema(unit);
        yield return RecipeContentType.CreateSchema(unit);
        yield return ToolTierContentType.CreateSchema();
        yield return SkillCurveContentType.CreateSchema();
        yield return GameTuningContentType.CreateSchema();
    }
}
