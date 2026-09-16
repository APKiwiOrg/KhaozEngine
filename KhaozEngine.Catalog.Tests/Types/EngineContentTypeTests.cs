using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Types;

/// <summary>
/// The six engine content types of spec 3.1 to 3.5, pinned: their ids, their keys, their chunk slots,
/// their row caps, their type-level visibility, and every field NAME in its declared order.
/// <para>
/// The names are pinned LITERALLY rather than read back off the constants that produced them, because a
/// field name is what contracts 12.1 derives a localization key from. Renaming <c>examine</c> renames
/// <c>item.bronze_sword.examine</c> in every translated catalog, so the rename has to go red here first.
/// </para>
/// </summary>
public class EngineContentTypeTests
{
    static ContentTypeRegistry Registered()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        return registry;
    }

    static ContentTypeRegistration Type(ContentTypeRegistry registry, string typeKey)
    {
        Assert.True(registry.TryGetByKey(typeKey, out ContentTypeRegistration? registration));
        return registration;
    }

    static void AssertField(
        ContentFieldSchema schema,
        int index,
        string name,
        ContentFieldKind kind,
        string? referenceTarget,
        ContentVisibility visibility,
        bool required,
        int scale = 1)
    {
        ContentFieldEntry field = schema.Fields[index];
        Assert.Equal(name, field.Name);
        Assert.Equal(kind, field.Kind);
        Assert.Equal(referenceTarget, field.ReferenceTarget);
        Assert.Equal(visibility, field.Visibility);
        Assert.Equal(required, field.Required);
        Assert.Equal(scale, field.Scale);
    }

    [Fact]
    public void SixTypesRegisterWithTheSpecIdsAndKeys()
    {
        ContentTypeRegistry registry = Registered();

        Assert.Equal(
            new (ushort, string)[]
            {
                (1, "tag"),
                (2, "item"),
                (3, "stat"),
                (4, "loot_table"),
                (5, "loot_entry"),
                (6, "base_socket"),
            },
            registry.ByTypeId.Select(r => (r.Type.Value, r.TypeKey)).ToArray());
    }

    [Fact]
    public void EveryTypeRegistersThroughTheEngineBand()
    {
        ContentTypeRegistry registry = Registered();

        Assert.All(registry.ByTypeId, r => Assert.Equal(ContentRegistrationBand.Engine, r.Band));
        Assert.All(registry.ByTypeId, r => Assert.True(r.Type.IsEngine));
    }

    [Fact]
    public void DefaultChunkSlotsAreTheDownloadDecisionOfSpecThreeOne()
    {
        ContentTypeRegistry registry = Registered();

        Assert.Equal(
            new[] { 4096, 1024, 4096, 4096, 16384, 16384 },
            registry.ByTypeId.Select(r => r.ChunkSlots).ToArray());
    }

    [Fact]
    public void OnlyTheTwoChildTypesDeclareTheirOwnRowCap()
    {
        ContentTypeRegistry registry = Registered();

        Assert.Equal(512, Type(registry, "loot_entry").MaxRowBytes);
        Assert.Equal(512, Type(registry, "base_socket").MaxRowBytes);
        foreach (string key in new[] { "tag", "item", "stat", "loot_table" })
        {
            Assert.Equal(ContentPackFormat.DefaultMaxRowBytes, Type(registry, key).MaxRowBytes);
        }
    }

    [Fact]
    public void TheLootFamilyIsServerOnlyAtTheTypeLevelAndTheRestAreClient()
    {
        ContentTypeRegistry registry = Registered();

        Assert.Equal(ContentVisibility.ServerOnly, Type(registry, "loot_table").DefaultVisibility);
        Assert.Equal(ContentVisibility.ServerOnly, Type(registry, "loot_entry").DefaultVisibility);
        foreach (string key in new[] { "tag", "item", "stat", "base_socket" })
        {
            Assert.Equal(ContentVisibility.Client, Type(registry, key).DefaultVisibility);
        }
    }

    [Fact]
    public void NoCodecClaimsToWriteADerivedMarker()
    {
        ContentTypeRegistry registry = Registered();

        foreach (ContentTypeRegistration registration in registry.ByTypeId)
        {
            IReadOnlyList<string> markers = registration.Schema.Fields
                .Where(f => f.IsDerivedMarker)
                .Select(f => f.Name)
                .ToArray();
            Assert.All(markers, name => Assert.DoesNotContain(name, registration.Codec.WrittenFields));
        }
    }

    [Fact]
    public void TagSchemaIsTheDerivedNameAndTheEditorSort()
    {
        ContentFieldSchema schema = Type(Registered(), "tag").Schema;

        Assert.Equal(2, schema.Fields.Count);
        AssertField(schema, 0, "name", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true);
        AssertField(schema, 1, "sort", ContentFieldKind.Int, null, ContentVisibility.Client, false);
    }

    [Fact]
    public void ItemSchemaIsTheSixteenFieldsOfSpecThreeThreeInOrder()
    {
        ContentFieldSchema schema = Type(Registered(), "item").Schema;

        Assert.Equal(16, schema.Fields.Count);
        AssertField(schema, 0, "name", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true);
        AssertField(schema, 1, "examine", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, false);
        AssertField(schema, 2, "tags", ContentFieldKind.TagList, "tag", ContentVisibility.Client, false);
        AssertField(schema, 3, "stackable", ContentFieldKind.Bool, null, ContentVisibility.Client, true);
        AssertField(schema, 4, "max_stack", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(schema, 5, "tradable", ContentFieldKind.Bool, null, ContentVisibility.Client, true);
        AssertField(schema, 6, "value", ContentFieldKind.ScaledInt, null, ContentVisibility.Client, true);
        AssertField(schema, 7, "icon", ContentFieldKind.OpaqueBytes, null, ContentVisibility.Client, false);
        AssertField(schema, 8, "mesh", ContentFieldKind.OpaqueBytes, null, ContentVisibility.Client, false);
        AssertField(schema, 9, "held_mesh", ContentFieldKind.OpaqueBytes, null, ContentVisibility.Client, false);
        AssertField(schema, 10, "ground_pose", ContentFieldKind.Int, null, ContentVisibility.Client, false);
        AssertField(schema, 11, "icon_tilt", ContentFieldKind.ScaledInt, null, ContentVisibility.Client, false, 1000);
        AssertField(schema, 12, "icon_spin", ContentFieldKind.ScaledInt, null, ContentVisibility.Client, false, 1000);
        AssertField(schema, 13, "durability_max", ContentFieldKind.Int, null, ContentVisibility.Client, false);
        AssertField(schema, 14, "socket_max", ContentFieldKind.Int, null, ContentVisibility.Client, false);
        AssertField(schema, 15, "equip_profile", ContentFieldKind.KeyReference, "equip_profile", ContentVisibility.Client, false);
    }

    [Fact]
    public void StatSchemaIsContractsThirteenOneWithNothingAdded()
    {
        ContentFieldSchema schema = Type(Registered(), "stat").Schema;

        Assert.Equal(6, schema.Fields.Count);
        AssertField(schema, 0, "name", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true);
        AssertField(schema, 1, "scale", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(schema, 2, "min", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(schema, 3, "max", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(schema, 4, "tags", ContentFieldKind.TagList, "tag", ContentVisibility.Client, false);
        AssertField(schema, 5, "display_format", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true);
    }

    [Fact]
    public void LootTableSchemaIsServerOnlyThroughout()
    {
        ContentFieldSchema schema = Type(Registered(), "loot_table").Schema;

        // guaranteed is NOT here: it is a loot_entry field, so one table can compose a guaranteed entry with a
        // weighted one. LootRoller settles it and LootTableContentType records why.
        Assert.Equal(2, schema.Fields.Count);
        AssertField(schema, 0, "roll_count", ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true);
        AssertField(schema, 1, "tags", ContentFieldKind.TagList, "tag", ContentVisibility.ServerOnly, false);
    }

    [Fact]
    public void LootEntrySchemaCarriesTheThreeDrawShapesOfSpecThreeFive()
    {
        ContentFieldSchema schema = Type(Registered(), "loot_entry").Schema;

        Assert.Equal(10, schema.Fields.Count);
        AssertField(schema, 0, "table", ContentFieldKind.KeyReference, "loot_table", ContentVisibility.ServerOnly, true);
        AssertField(schema, 1, "item", ContentFieldKind.KeyReference, "item", ContentVisibility.ServerOnly, false);
        AssertField(schema, 2, "nested_table", ContentFieldKind.KeyReference, "loot_table", ContentVisibility.ServerOnly, false);
        AssertField(schema, 3, "weight", ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true);
        AssertField(schema, 4, "chance_bp", ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true);
        AssertField(schema, 5, "guaranteed", ContentFieldKind.Bool, null, ContentVisibility.ServerOnly, true);
        AssertField(schema, 6, "min_count", ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true);
        AssertField(schema, 7, "max_count", ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true);
        AssertField(schema, 8, "sort", ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true);
        AssertField(schema, 9, "required_tags", ContentFieldKind.TagList, "tag", ContentVisibility.ServerOnly, false);
    }

    [Fact]
    public void BaseSocketSchemaCarriesTheAuthoredOrderAndTheLateBoundSocketType()
    {
        ContentFieldSchema schema = Type(Registered(), "base_socket").Schema;

        Assert.Equal(3, schema.Fields.Count);
        AssertField(schema, 0, "item", ContentFieldKind.KeyReference, "item", ContentVisibility.Client, true);
        AssertField(schema, 1, "sort", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(schema, 2, "socket_type", ContentFieldKind.KeyReference, "socket_type", ContentVisibility.Client, true);
    }

    [Fact]
    public void TheTwoLateBoundTargetsAreTypeKeysNoEngineTypeAnswersTo()
    {
        ContentTypeRegistry registry = Registered();

        Assert.False(registry.TryGetByKey("equip_profile", out _));
        Assert.False(registry.TryGetByKey("socket_type", out _));
    }

    [Fact]
    public void RegisteringTwiceIntoOneRegistryIsRefused()
    {
        ContentTypeRegistry registry = Registered();

        Assert.Throws<ContentRegistrationException>(() => EngineContentTypes.Register(registry));
    }
}
