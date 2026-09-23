using System;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// Each type's own <c>Register</c>, run into a fresh registry, and what it declared once there.
/// <para>
/// The expectations are LITERALS. A game that writes the eight-argument registration by hand can pass a
/// wrong visibility or a wrong chunk slot count and neither fails: a server-only type registered as
/// <see cref="ContentVisibility.Client"/> puts every row's key in the client manifest, and a wrong slot
/// count moves every content address, so two worlds authoring the same facts would stop agreeing about
/// where they live. Reading the numbers back off the constants that produced them would pin nothing.
/// </para>
/// </summary>
public class GameTypeRegisterTests
{
    /// <summary>Every type registered through its own helper, over the seven engine types.</summary>
    static ContentTypeRegistry RegisterAll(ContentDurationUnit unit)
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);

        FoodContentType.Register(registry, unit, validator: null);
        EquipProfileContentType.Register(registry, unit, validator: null);
        EquipStatLineContentType.Register(registry, validator: null);
        StoreContentType.Register(registry, validator: null);
        StoreShelfContentType.Register(registry, validator: null);
        MonsterDropContentType.Register(registry, validator: null);
        GatheringNodeContentType.Register(registry, unit, validator: null);
        RecipeContentType.Register(registry, unit, validator: null);
        RecipeInputContentType.Register(registry, validator: null);
        RecipeOutputContentType.Register(registry, validator: null);
        ToolTierContentType.Register(registry, validator: null);
        SkillCurveContentType.Register(registry, validator: null);
        GameTuningContentType.Register(registry, validator: null);

        return registry;
    }

    [Theory]
    [InlineData(ContentDurationUnit.Ticks)]
    [InlineData(ContentDurationUnit.Seconds)]
    public void EveryTypeRegistersAtItsOwnIdKeyVisibilityAndChunkSlots(ContentDurationUnit unit)
    {
        ContentTypeRegistry registry = RegisterAll(unit);

        Assert.Equal(
            new (ushort Id, string Key, ContentVisibility Visibility, int Slots)[]
            {
                (1024, "food", ContentVisibility.Client, 256),
                (1025, "equip_profile", ContentVisibility.Client, 256),

                // A child takes a bigger chunk than its parent, because it outnumbers it. Three take four
                // times the floor and the shelf takes twice.
                (1026, "equip_stat_line", ContentVisibility.Client, 1024),

                (1027, "store", ContentVisibility.Client, 256),
                (1028, "store_shelf", ContentVisibility.Client, 512),

                // The one ServerOnly type of the set. A client that can read a drop table knows every roll
                // before it happens, and the visibility is at the TYPE level so the whole family stays out
                // of the client manifest rather than merely out of a client's view of a row.
                (1029, "monster_drop", ContentVisibility.ServerOnly, 256),

                (1030, "gathering_node", ContentVisibility.Client, 256),
                (1031, "recipe", ContentVisibility.Client, 512),
                (1032, "recipe_input", ContentVisibility.Client, 1024),
                (1033, "recipe_output", ContentVisibility.Client, 1024),
                (1034, "tool_tier", ContentVisibility.Client, 256),
                (1035, "skill_curve", ContentVisibility.Client, 256),
                (1036, "game_tuning", ContentVisibility.Client, 256),
            },
            registry.ByTypeId
                .Where(r => r.Band == ContentRegistrationBand.Game)
                .Select(r => (r.Type.Value, r.TypeKey, r.DefaultVisibility, r.ChunkSlots))
                .ToArray());
    }

    [Theory]
    [InlineData(ContentDurationUnit.Ticks)]
    [InlineData(ContentDurationUnit.Seconds)]
    public void EachRegistrationCarriesItsOwnSchemaAndACodecBuiltOverIt(ContentDurationUnit unit)
    {
        ContentTypeRegistry registry = RegisterAll(unit);

        foreach (ContentTypeRegistration registration in
            registry.ByTypeId.Where(r => r.Band == ContentRegistrationBand.Game))
        {
            // RegisterContentType checks the codec's written fields against the schema, so reaching here
            // is already half the proof. This is the other half: the registration carries a real schema and
            // the null validator the caller passed, rather than one the helper invented.
            Assert.NotNull(registration.Codec);
            Assert.NotEmpty(registration.Schema.Fields);
            Assert.Null(registration.Validator);
        }
    }

    [Fact]
    public void TheValidatorIsTheCallersToSupply()
    {
        // The package ships none yet. What a game passes is what the registration carries, unchanged.
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        var validator = new CountingValidator();

        StoreContentType.Register(registry, validator);

        Assert.True(registry.TryGetByKey(GameContentTypeIds.StoreKey, out ContentTypeRegistration? registration));
        Assert.Same(validator, registration.Validator);
    }

    [Fact]
    public void ANullRegistryIsRefused()
    {
        Assert.Throws<ArgumentNullException>(
            () => FoodContentType.Register(null!, ContentDurationUnit.Ticks, validator: null));
        Assert.Throws<ArgumentNullException>(() => StoreContentType.Register(null!, validator: null));
    }

    [Fact]
    public void RegisteringTwiceIsRefusedRatherThanQuietlyReplacing()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        ToolTierContentType.Register(registry, validator: null);

        Assert.Throws<ContentRegistrationException>(
            () => ToolTierContentType.Register(registry, validator: null));
    }

    sealed class CountingValidator : IContentValidator
    {
        public void Validate(
            ContentTypeId type,
            IContentSnapshot candidate,
            System.Collections.Generic.ICollection<ContentFinding> findings)
        {
        }
    }
}
