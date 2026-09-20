using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// The nine leaf codecs, registered on a real registry and round-tripped.
/// <para>
/// Registration is half the proof: <c>RegisterContentType</c> checks a codec's written fields against the
/// schema it was built over, checks the id against the band the caller claims and checks the chunk shape, so
/// a type that registers here is one a game can register the same way.
/// </para>
/// </summary>
public class LeafCodecTests
{
    const ContentDurationUnit Ticks = ContentDurationUnit.Ticks;

    /// <summary>The nine leaf types, ascending by id, each as the four things a registration needs.</summary>
    static IEnumerable<(ushort Id, string Key, ContentFieldSchema Schema, ContentVisibility Visibility, int Slots)>
        Leaves(ContentDurationUnit unit)
    {
        yield return (
            GameContentTypeIds.Food,
            GameContentTypeIds.FoodKey,
            FoodContentType.CreateSchema(unit),
            FoodContentType.DefaultVisibility,
            FoodContentType.DefaultChunkSlots);
        yield return (
            GameContentTypeIds.EquipProfile,
            GameContentTypeIds.EquipProfileKey,
            EquipProfileContentType.CreateSchema(unit),
            EquipProfileContentType.DefaultVisibility,
            EquipProfileContentType.DefaultChunkSlots);
        yield return (
            GameContentTypeIds.Store,
            GameContentTypeIds.StoreKey,
            StoreContentType.CreateSchema(),
            StoreContentType.DefaultVisibility,
            StoreContentType.DefaultChunkSlots);
        yield return (
            GameContentTypeIds.MonsterDrop,
            GameContentTypeIds.MonsterDropKey,
            MonsterDropContentType.CreateSchema(),
            MonsterDropContentType.DefaultVisibility,
            MonsterDropContentType.DefaultChunkSlots);
        yield return (
            GameContentTypeIds.GatheringNode,
            GameContentTypeIds.GatheringNodeKey,
            GatheringNodeContentType.CreateSchema(unit),
            GatheringNodeContentType.DefaultVisibility,
            GatheringNodeContentType.DefaultChunkSlots);
        yield return (
            GameContentTypeIds.Recipe,
            GameContentTypeIds.RecipeKey,
            RecipeContentType.CreateSchema(unit),
            RecipeContentType.DefaultVisibility,
            RecipeContentType.DefaultChunkSlots);
        yield return (
            GameContentTypeIds.ToolTier,
            GameContentTypeIds.ToolTierKey,
            ToolTierContentType.CreateSchema(),
            ToolTierContentType.DefaultVisibility,
            ToolTierContentType.DefaultChunkSlots);
        yield return (
            GameContentTypeIds.SkillCurve,
            GameContentTypeIds.SkillCurveKey,
            SkillCurveContentType.CreateSchema(),
            SkillCurveContentType.DefaultVisibility,
            SkillCurveContentType.DefaultChunkSlots);
        yield return (
            GameContentTypeIds.GameTuning,
            GameContentTypeIds.GameTuningKey,
            GameTuningContentType.CreateSchema(),
            GameTuningContentType.DefaultVisibility,
            GameTuningContentType.DefaultChunkSlots);
    }

    static IContentRowCodec CodecFor(ushort id, string key, ContentFieldSchema schema)
    {
        var type = new ContentTypeId(id);
        return key switch
        {
            GameContentTypeIds.FoodKey => new FoodContentType.Codec(type, schema),
            GameContentTypeIds.EquipProfileKey => new EquipProfileContentType.Codec(type, schema),
            GameContentTypeIds.StoreKey => new StoreContentType.Codec(type, schema),
            GameContentTypeIds.MonsterDropKey => new MonsterDropContentType.Codec(type, schema),
            GameContentTypeIds.GatheringNodeKey => new GatheringNodeContentType.Codec(type, schema),
            GameContentTypeIds.RecipeKey => new RecipeContentType.Codec(type, schema),
            GameContentTypeIds.ToolTierKey => new ToolTierContentType.Codec(type, schema),
            GameContentTypeIds.SkillCurveKey => new SkillCurveContentType.Codec(type, schema),
            _ => new GameTuningContentType.Codec(type, schema),
        };
    }

    static ContentTypeRegistry Registered(ContentDurationUnit unit)
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        foreach ((ushort id, string key, ContentFieldSchema schema, ContentVisibility visibility, int slots)
            in Leaves(unit))
        {
            registry.RegisterContentType(
                ContentRegistrationBand.Game,
                id,
                key,
                CodecFor(id, key, schema),
                validator: null,
                schema,
                visibility,
                slots);
        }

        return registry;
    }

    [Theory]
    [InlineData(ContentDurationUnit.Ticks)]
    [InlineData(ContentDurationUnit.Seconds)]
    public void TheNineRegisterInTheGameBandUnderEitherUnit(ContentDurationUnit unit)
    {
        ContentTypeRegistry registry = Registered(unit);

        Assert.Equal(
            new (ushort, string)[]
            {
                (1024, "food"),
                (1025, "equip_profile"),
                (1027, "store"),
                (1029, "monster_drop"),
                (1030, "gathering_node"),
                (1031, "recipe"),
                (1034, "tool_tier"),
                (1035, "skill_curve"),
                (1036, "game_tuning"),
            },
            registry.ByTypeId
                .Where(r => r.Band == ContentRegistrationBand.Game)
                .Select(r => (r.Type.Value, r.TypeKey))
                .ToArray());
    }

    [Theory]
    [InlineData(ContentDurationUnit.Ticks)]
    [InlineData(ContentDurationUnit.Seconds)]
    public void EveryLeafRowRoundTrips(ContentDurationUnit unit)
    {
        ContentTypeRegistry registry = Registered(unit);

        foreach (ContentTypeRegistration registration in
            registry.ByTypeId.Where(r => r.Band == ContentRegistrationBand.Game))
        {
            ContentRow row = Populated(registration);
            var buffer = new ArrayBufferWriter<byte>();
            registration.Codec.Encode(row, buffer);

            Assert.True(
                registration.Codec.TryDecode(buffer.WrittenSpan, out ContentRow? back, out string? reason),
                reason ?? registration.TypeKey);
            Assert.Equal(row.Key.ToString(), back.Key.ToString());
            Assert.Equal(
                row.Fields.Select(f => f.Number).ToArray(),
                back.Fields.Select(f => f.Number).ToArray());
        }
    }

    [Fact]
    public void TheUnitChangesNoEncodedByte()
    {
        // A field NAME is not written into a row body, so the same numbers encode to the same bytes under
        // either unit. That is what lets a game move its unit without restating a single authored row.
        ContentTypeRegistry other = Registered(ContentDurationUnit.Seconds);

        foreach (ContentTypeRegistration registration in
            Registered(Ticks).ByTypeId.Where(r => r.Band == ContentRegistrationBand.Game))
        {
            Assert.True(other.TryGetByKey(registration.TypeKey, out ContentTypeRegistration? twin));

            var mine = new ArrayBufferWriter<byte>();
            var theirs = new ArrayBufferWriter<byte>();
            registration.Codec.Encode(Populated(registration), mine);
            twin.Codec.Encode(Populated(twin), theirs);

            Assert.Equal(mine.WrittenSpan.ToArray(), theirs.WrittenSpan.ToArray());
        }
    }

    [Theory]
    [InlineData(ContentDurationUnit.Ticks)]
    [InlineData(ContentDurationUnit.Seconds)]
    public void TheOneOptionalFieldRoundTripsAbsent(ContentDurationUnit unit)
    {
        // xp_per_damage is the only optional field across the whole package, and absence and zero share an
        // encoding, so this is the one row shape where the decoder's resolution is load bearing: an unset
        // knob has to come back unset rather than as a skill that pays zero per point of damage.
        ContentTypeRegistry registry = Registered(unit);
        Assert.True(registry.TryGetByKey(
            GameContentTypeIds.SkillCurveKey, out ContentTypeRegistration? curve));

        var row = new ContentRow(
            curve.Type,
            0,
            new ContentKey("curve_row"),
            0,
            false,
            new[]
            {
                ContentFieldValue.OfNumber(ContentFieldKind.Int, 4),
                ContentFieldValue.Absent(ContentFieldKind.Int),
            });

        var buffer = new ArrayBufferWriter<byte>();
        curve.Codec.Encode(row, buffer);

        Assert.True(
            curve.Codec.TryDecode(buffer.WrittenSpan, out ContentRow? back, out string? reason),
            reason ?? curve.TypeKey);
        Assert.Equal(4, back.Fields[0].Number);
        Assert.True(back.Fields[1].IsAbsent);

        // The required field beside it does not: a required knob of zero is an ordinary row rather than a
        // missing one, and reporting it absent would turn it into a schema finding.
        var zeroed = new ContentRow(
            curve.Type,
            0,
            new ContentKey("curve_zero"),
            0,
            false,
            new[]
            {
                ContentFieldValue.OfNumber(ContentFieldKind.Int, 0),
                ContentFieldValue.Absent(ContentFieldKind.Int),
            });

        var second = new ArrayBufferWriter<byte>();
        curve.Codec.Encode(zeroed, second);

        Assert.True(curve.Codec.TryDecode(second.WrittenSpan, out ContentRow? zeroBack, out reason), reason);
        Assert.False(zeroBack.Fields[0].IsAbsent);
        Assert.Equal(0, zeroBack.Fields[0].Number);
    }

    /// <summary>A row carrying a distinct positive value for every field of its type.</summary>
    static ContentRow Populated(ContentTypeRegistration registration)
    {
        var values = new ContentFieldValue[registration.Schema.Fields.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = ContentFieldValue.OfNumber(registration.Schema.Fields[i].Kind, i + 1);
        }

        return new ContentRow(
            registration.Type,
            0,
            new ContentKey(registration.TypeKey + "_row"),
            0,
            false,
            values);
    }
}
