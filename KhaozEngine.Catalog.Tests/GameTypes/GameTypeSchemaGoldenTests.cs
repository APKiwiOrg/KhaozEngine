using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// The STORED schema of all thirteen game types, pinned field by field: the name, the declared order, the
/// kind, the reference target, the visibility, the required flag and the scale, plus each type's chunk slots
/// and type-level visibility.
/// <para>
/// Every expectation here is a LITERAL. A field name is what a localization key is derived from and what a
/// reader resolves by, a field's position is what a row's values are parallel to, and a reference target is
/// what the publish validator resolves, so none of them may move without going red here first. Reading them
/// back off the constants that produced them would pin nothing.
/// </para>
/// </summary>
public class GameTypeSchemaGoldenTests
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
    public void EquipStatLine()
    {
        // Four times its parent's floor: lines outnumber profiles by roughly the stats a piece of
        // equipment touches.
        Assert.Equal(1024, EquipStatLineContentType.DefaultChunkSlots);
        Assert.Equal(ContentVisibility.Client, EquipStatLineContentType.DefaultVisibility);

        AssertSchema(
            EquipStatLineContentType.CreateSchema(),
            new Field("profile", ContentFieldKind.KeyReference, "equip_profile", ContentVisibility.Client, true),
            new Field("stat", ContentFieldKind.KeyReference, "stat", ContentVisibility.Client, true),

            // A plain Int and not a scaled one: the stat row owns the scale, and a second copy of it here
            // would be a copy the two could disagree about.
            new Field("value", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("sort", ContentFieldKind.Int, null, ContentVisibility.Client, true));
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
    public void StoreShelf()
    {
        Assert.Equal(512, StoreShelfContentType.DefaultChunkSlots);
        Assert.Equal(ContentVisibility.Client, StoreShelfContentType.DefaultVisibility);

        AssertSchema(
            StoreShelfContentType.CreateSchema(),
            new Field("store", ContentFieldKind.KeyReference, "store", ContentVisibility.Client, true),
            new Field("item", ContentFieldKind.KeyReference, "item", ContentVisibility.Client, true),
            new Field("sort", ContentFieldKind.Int, null, ContentVisibility.Client, true));
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
    public void RecipeInput()
    {
        Assert.Equal(1024, RecipeInputContentType.DefaultChunkSlots);
        Assert.Equal(ContentVisibility.Client, RecipeInputContentType.DefaultVisibility);

        AssertSchema(
            RecipeInputContentType.CreateSchema(),
            new Field("recipe", ContentFieldKind.KeyReference, "recipe", ContentVisibility.Client, true),
            new Field("item", ContentFieldKind.KeyReference, "item", ContentVisibility.Client, true),
            new Field("count", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("sort", ContentFieldKind.Int, null, ContentVisibility.Client, true));
    }

    [Fact]
    public void RecipeOutput()
    {
        // Byte for byte the input's schema, and still its own type. The two sides are read at different
        // moments, and a side flag would be a field every reader has to filter on.
        Assert.Equal(1024, RecipeOutputContentType.DefaultChunkSlots);
        Assert.Equal(ContentVisibility.Client, RecipeOutputContentType.DefaultVisibility);

        AssertSchema(
            RecipeOutputContentType.CreateSchema(),
            new Field("recipe", ContentFieldKind.KeyReference, "recipe", ContentVisibility.Client, true),
            new Field("item", ContentFieldKind.KeyReference, "item", ContentVisibility.Client, true),
            new Field("count", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("sort", ContentFieldKind.Int, null, ContentVisibility.Client, true));
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

    // The four types with a duration, under Seconds. The duration is HUNDREDTHS of a second, a ScaledInt at
    // scale 100, so 2.33 seconds is stored as 233. Every other field is its Ticks golden above, restated as
    // literals so a change to either unit goes red on its own.
    const ContentDurationUnit Seconds = ContentDurationUnit.Seconds;

    [Fact]
    public void FoodUnderSeconds()
    {
        AssertSchema(
            FoodContentType.CreateSchema(Seconds),
            new Field("item", ContentFieldKind.KeyReference, "item", ContentVisibility.Client, true),
            new Field("heals", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("attack_delay_seconds", ContentFieldKind.ScaledInt, null, ContentVisibility.Client, true, 100));
    }

    [Fact]
    public void EquipProfileUnderSeconds()
    {
        AssertSchema(
            EquipProfileContentType.CreateSchema(Seconds),
            new Field("slot", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("weapon_archetype", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("attack_seconds", ContentFieldKind.ScaledInt, null, ContentVisibility.Client, true, 100));
    }

    [Fact]
    public void GatheringNodeUnderSeconds()
    {
        AssertSchema(
            GatheringNodeContentType.CreateSchema(Seconds),
            new Field("skill", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("tool_family", ContentFieldKind.KeyReference, "tag", ContentVisibility.Client, true),
            new Field("level_required", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("base_chance_bp", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("lives", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("life_loss_bp", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("yield_xp", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("yield_item", ContentFieldKind.KeyReference, "item", ContentVisibility.Client, true),
            new Field("respawn_seconds", ContentFieldKind.ScaledInt, null, ContentVisibility.Client, true, 100));
    }

    [Fact]
    public void RecipeUnderSeconds()
    {
        AssertSchema(
            RecipeContentType.CreateSchema(Seconds),
            new Field("display_order", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("skill", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("level_required", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("primary_item", ContentFieldKind.KeyReference, "item", ContentVisibility.Client, true),
            new Field("station", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("base_seconds", ContentFieldKind.ScaledInt, null, ContentVisibility.Client, true, 100),
            new Field("xp_per_item", ContentFieldKind.Int, null, ContentVisibility.Client, true),
            new Field("repeat_mode", ContentFieldKind.Int, null, ContentVisibility.Client, true));
    }

    [Fact]
    public void EveryFieldIsRequiredExceptTheOneCurveKnob()
    {
        string[] optional = EverySchema(Ticks)
            .SelectMany(schema => schema.Fields)
            .Where(field => !field.Required)
            .Select(field => field.Name)
            .ToArray();

        Assert.Equal(new[] { "xp_per_damage" }, optional);
    }

    [Fact]
    public void SecondsMovesTheFourDurationsToHundredthsAndNothingElse()
    {
        // The unit picks a duration field's NAME, and under Seconds its KIND and SCALE as well: a plain Int
        // of ticks becomes a ScaledInt at scale 100, hundredths of a second, because whole seconds cannot
        // hold a timing between two of them. Order, reference targets, visibility and required flags are
        // identical under either unit, and every field that is not one of the four is identical outright.
        ContentFieldSchema[] ticks = EverySchema(ContentDurationUnit.Ticks).ToArray();
        ContentFieldSchema[] seconds = EverySchema(ContentDurationUnit.Seconds).ToArray();

        Assert.Equal(ticks.Length, seconds.Length);
        var moved = new List<(string, ContentFieldKind, int, string, ContentFieldKind, int)>();
        for (int type = 0; type < ticks.Length; type++)
        {
            Assert.Equal(ticks[type].Fields.Count, seconds[type].Fields.Count);
            for (int i = 0; i < ticks[type].Fields.Count; i++)
            {
                ContentFieldEntry left = ticks[type].Fields[i];
                ContentFieldEntry right = seconds[type].Fields[i];
                Assert.Equal(left with { Name = right.Name, Kind = right.Kind, Scale = right.Scale }, right);
                if (left != right)
                {
                    moved.Add((left.Name, left.Kind, left.Scale, right.Name, right.Kind, right.Scale));
                }
            }
        }

        Assert.Equal(
            new[]
            {
                ("attack_delay_ticks", ContentFieldKind.Int, 1, "attack_delay_seconds", ContentFieldKind.ScaledInt, 100),
                ("attack_ticks", ContentFieldKind.Int, 1, "attack_seconds", ContentFieldKind.ScaledInt, 100),
                ("respawn_ticks", ContentFieldKind.Int, 1, "respawn_seconds", ContentFieldKind.ScaledInt, 100),
                ("base_ticks", ContentFieldKind.Int, 1, "base_seconds", ContentFieldKind.ScaledInt, 100),
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

    /// <summary>All thirteen schemas, ASCENDING by type id, which is the order the moved names are read in.</summary>
    static IEnumerable<ContentFieldSchema> EverySchema(ContentDurationUnit unit)
    {
        yield return FoodContentType.CreateSchema(unit);
        yield return EquipProfileContentType.CreateSchema(unit);
        yield return EquipStatLineContentType.CreateSchema();
        yield return StoreContentType.CreateSchema();
        yield return StoreShelfContentType.CreateSchema();
        yield return MonsterDropContentType.CreateSchema();
        yield return GatheringNodeContentType.CreateSchema(unit);
        yield return RecipeContentType.CreateSchema(unit);
        yield return RecipeInputContentType.CreateSchema();
        yield return RecipeOutputContentType.CreateSchema();
        yield return ToolTierContentType.CreateSchema();
        yield return SkillCurveContentType.CreateSchema();
        yield return GameTuningContentType.CreateSchema();
    }
}
