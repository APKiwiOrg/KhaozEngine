using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;

namespace KhaozEngine.Tests.ItemInstances.Content;

/// <summary>
/// The three types of spec 10.4, the currency family: <c>crafting_currency</c> and its <c>currency_step</c>
/// and <c>currency_guard</c> children, plus the one registration entry point that puts all eighteen of
/// spec 8.1's types in the Instances band.
/// <para>
/// The field NAMES are pinned literally rather than read back off the constants that produced them,
/// because a field name is what contracts 12.1 derives a localization key from. Renaming <c>name</c>
/// renames <c>crafting_currency.whetstone.name</c> in every translated catalog, so the rename has to go
/// red here first.
/// </para>
/// <para>
/// Every registry a fact builds is its OWN, so nothing here writes process-global state and no
/// <c>DisableParallelization</c> collection is needed.
/// </para>
/// </summary>
public class CurrencyFamilyTests
{
    /// <summary>The three types of spec 10.4, everything their registrations are handed.</summary>
    static readonly FamilyType[] Currency =
    [
        new(
            InstanceContentTypeIds.CraftingCurrencyTypeId,
            InstanceContentTypeIds.CraftingCurrencyTypeKey,
            CraftingCurrencyContentType.DefaultVisibility,
            CraftingCurrencyContentType.DefaultChunkSlots,
            CraftingCurrencyContentType.MaxRowBytes,
            null,
            CraftingCurrencyContentType.CreateSchema,
            static (type, schema) => new CraftingCurrencyContentType.Codec(type, schema)),
        new(
            InstanceContentTypeIds.CurrencyStepTypeId,
            InstanceContentTypeIds.CurrencyStepTypeKey,
            CurrencyStepContentType.DefaultVisibility,
            CurrencyStepContentType.DefaultChunkSlots,
            CurrencyStepContentType.MaxRowBytes,
            null,
            CurrencyStepContentType.CreateSchema,
            static (type, schema) => new CurrencyStepContentType.Codec(type, schema)),
        new(
            InstanceContentTypeIds.CurrencyGuardTypeId,
            InstanceContentTypeIds.CurrencyGuardTypeKey,
            CurrencyGuardContentType.DefaultVisibility,
            CurrencyGuardContentType.DefaultChunkSlots,
            CurrencyGuardContentType.MaxRowBytes,
            null,
            CurrencyGuardContentType.CreateSchema,
            static (type, schema) => new CurrencyGuardContentType.Codec(type, schema)),
    ];

    [Fact]
    public void The_three_register_in_the_Instances_band_at_261_271_and_272()
    {
        ContentTypeRegistry registry = Registered(Currency);

        var seen = new List<(ushort Id, string Key)>();
        foreach (ContentTypeRegistration registration in registry.ByTypeId)
        {
            seen.Add((registration.Type.Value, registration.TypeKey));
            Assert.Equal(ContentRegistrationBand.Instances, registration.Band);
            Assert.True(registration.Type.IsInstances);
        }

        Assert.Equal(
            new (ushort, string)[]
            {
                (261, "crafting_currency"),
                (271, "currency_step"),
                (272, "currency_guard"),
            },
            seen);
    }

    [Fact]
    public void The_three_schemas_are_the_spec_10_4_tables_field_for_field()
    {
        ContentTypeRegistry registry = Registered(Currency);

        ContentFieldSchema currency = Lookup(registry, InstanceContentTypeIds.CraftingCurrencyTypeKey).Schema;
        Assert.Equal(5, currency.Fields.Count);
        AssertField(currency, 0, "name", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true);
        AssertField(
            currency, 1, "description", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true);
        AssertField(
            currency,
            2,
            "consumes_definition_id",
            ContentFieldKind.KeyReference,
            "item",
            ContentVisibility.Client,
            false);
        AssertField(currency, 3, "consumes_count", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(currency, 4, "max_steps", ContentFieldKind.Int, null, ContentVisibility.Client, true);

        ContentFieldSchema step = Lookup(registry, InstanceContentTypeIds.CurrencyStepTypeKey).Schema;
        Assert.Equal(7, step.Fields.Count);
        AssertField(
            step,
            0,
            "crafting_currency_id",
            ContentFieldKind.KeyReference,
            "crafting_currency",
            ContentVisibility.Client,
            true);
        AssertField(step, 1, "sort", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(step, 2, "operation", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(step, 3, "parameter_a", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(step, 4, "parameter_b", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(step, 5, "parameter_c", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(step, 6, "parameter_d", ContentFieldKind.Int, null, ContentVisibility.Client, true);

        ContentFieldSchema guard = Lookup(registry, InstanceContentTypeIds.CurrencyGuardTypeKey).Schema;
        Assert.Equal(6, guard.Fields.Count);
        AssertField(
            guard,
            0,
            "crafting_currency_id",
            ContentFieldKind.KeyReference,
            "crafting_currency",
            ContentVisibility.Client,
            true);
        AssertField(
            guard,
            1,
            "currency_step_id",
            ContentFieldKind.KeyReference,
            "currency_step",
            ContentVisibility.Client,
            false);
        AssertField(guard, 2, "sort", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(guard, 3, "guard_kind", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(guard, 4, "parameter_a", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(guard, 5, "parameter_b", ContentFieldKind.Int, null, ContentVisibility.Client, true);
    }

    [Fact]
    public void The_three_chunk_slot_counts_and_row_caps_are_the_8_1_table_exactly()
    {
        ContentTypeRegistry registry = Registered(Currency);

        var seen = new List<(string Key, int Slots, int Cap)>();
        foreach (ContentTypeRegistration registration in registry.ByTypeId)
        {
            seen.Add((registration.TypeKey, registration.ChunkSlots, registration.MaxRowBytes));
        }

        // The two children are the ones whose slot count will not carry the 1,024 byte default: at 16,384
        // slots the chunk ceiling leaves 1,015 bytes per row, so both take 512, which is what every other
        // type at that slot count took.
        Assert.Equal(
            new (string, int, int)[]
            {
                ("crafting_currency", 4096, 1024),
                ("currency_step", 16384, 512),
                ("currency_guard", 16384, 512),
            },
            seen);
    }

    [Fact]
    public void Every_one_of_the_three_round_trips_byte_for_byte()
    {
        ContentTypeRegistry registry = Registered(Currency);

        var seen = new List<string>();
        foreach (FamilyType type in Currency)
        {
            ContentTypeRegistration registration = Lookup(registry, type.Key);
            ContentRow row = PopulatedCurrency(registration);

            byte[] bytes = Encode(registration, row);
            ContentRow decoded = Decode(registration, bytes);

            Assert.Equal(row.Key, decoded.Key);
            Assert.Equal(row.Fields, decoded.Fields);
            Assert.Equal(bytes, Encode(registration, decoded));
            seen.Add(type.Key);
        }

        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void A_target_guard_and_a_step_guard_are_one_type_told_apart_by_an_empty_currency_step_id()
    {
        ContentTypeRegistry registry = Registered(Currency);
        ContentTypeRegistration guard = Lookup(registry, InstanceContentTypeIds.CurrencyGuardTypeKey);

        ContentRow target = Row(
            guard,
            "whetstone_target_1",
            Reference(1),
            ContentFieldValue.Absent(ContentFieldKind.KeyReference),
            Int(1),
            Int(12),
            Int(1),
            Int(0));
        ContentRow onStep = Row(guard, "whetstone_1_g1", Reference(1), Reference(1), Int(1), Int(9), Int(0), Int(19));

        // The reference is OPTIONAL, so its zero form reads back absent, which is what makes "empty" a
        // state the evaluator can see rather than a step id of 0 it would have to treat as a sentinel.
        Assert.True(Decode(guard, Encode(guard, target)).Fields[1].IsAbsent);
        Assert.False(Decode(guard, Encode(guard, onStep)).Fields[1].IsAbsent);
        Assert.Equal(1, Decode(guard, Encode(guard, onStep)).Fields[1].Number);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(1024)]
    [InlineData(70000)]
    public void A_currency_step_operation_is_a_primitive_or_a_game_operation(int operation)
    {
        ContentTypeRegistry registry = Registered(Currency);
        ContentTypeRegistration step = Lookup(registry, InstanceContentTypeIds.CurrencyStepTypeKey);

        ContentRow row = Row(step, "whetstone_1", Reference(1), Int(1), Int(operation), Int(0), Int(0), Int(0), Int(0));
        Assert.Equal(operation, Decode(step, Encode(step, row)).Fields[2].Number);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(1023)]
    [InlineData(-1)]
    public void A_currency_step_operation_in_the_gap_between_the_two_vocabularies_is_refused_on_both_sides(
        int operation)
    {
        // 1 to 14 is a primitive and 1,024 up is a game operation. A step naming 20 is an author reaching
        // for a primitive that does not exist, and a step the evaluator skipped would be a craft that did
        // less than its row says.
        ContentTypeRegistry registry = Registered(Currency);
        ContentTypeRegistration step = Lookup(registry, InstanceContentTypeIds.CurrencyStepTypeKey);

        ContentRow row = Row(step, "whetstone_1", Reference(1), Int(1), Int(operation), Int(0), Int(0), Int(0), Int(0));
        Assert.Throws<ArgumentException>(() => Encode(step, row));

        byte[] bytes = Forge(step, "whetstone_1", 1, 1, operation, 0, 0, 0, 0);
        Assert.False(step.Codec.TryDecode(bytes, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(-1)]
    public void A_currency_guard_kind_outside_the_fifteen_of_spec_10_3_is_refused_on_both_sides(int guardKind)
    {
        ContentTypeRegistry registry = Registered(Currency);
        ContentTypeRegistration guard = Lookup(registry, InstanceContentTypeIds.CurrencyGuardTypeKey);

        Assert.Equal(1, CurrencyGuardContentType.MinGuardKind);
        Assert.Equal(15, CurrencyGuardContentType.MaxGuardKind);

        ContentRow row = Row(
            guard, "whetstone_target_1", Reference(1), Reference(0), Int(1), Int(guardKind), Int(0), Int(0));
        Assert.Throws<ArgumentException>(() => Encode(guard, row));

        byte[] bytes = Forge(guard, "whetstone_target_1", 1, 0, 1, guardKind, 0, 0);
        Assert.False(guard.Codec.TryDecode(bytes, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(-1)]
    public void A_max_steps_outside_0_to_16_is_refused_on_both_sides(int maxSteps)
    {
        ContentTypeRegistry registry = Registered(Currency);
        ContentTypeRegistration currency = Lookup(registry, InstanceContentTypeIds.CraftingCurrencyTypeKey);

        Assert.Equal(16, CraftingCurrencyContentType.MaxSteps);

        ContentRow row = Row(currency, "whetstone", Marker(), Marker(), Reference(40), Int(1), Int(maxSteps));
        Assert.Throws<ArgumentException>(() => Encode(currency, row));

        byte[] bytes = Forge(currency, "whetstone", 40, 1, maxSteps);
        Assert.False(currency.Codec.TryDecode(bytes, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    [Fact]
    public void A_free_currency_leaves_consumes_definition_id_empty()
    {
        ContentTypeRegistry registry = Registered(Currency);
        ContentTypeRegistration currency = Lookup(registry, InstanceContentTypeIds.CraftingCurrencyTypeKey);

        ContentRow row = Row(
            currency,
            "bench_recombine",
            Marker(),
            Marker(),
            ContentFieldValue.Absent(ContentFieldKind.KeyReference),
            Int(0),
            Int(2));
        ContentRow decoded = Decode(currency, Encode(currency, row));

        Assert.True(decoded.Fields[2].IsAbsent);

        // consumes_count is REQUIRED, so its zero form reads back present, which is what keeps a free
        // operation an authored row rather than two unset fields.
        Assert.False(decoded.Fields[3].IsAbsent);
        Assert.Equal(0, decoded.Fields[3].Number);
    }

    [Fact]
    public void InstanceContentTypes_Register_puts_all_eighteen_in_the_Instances_band()
    {
        var registry = new ContentTypeRegistry();

        InstanceContentTypes.Register(registry);

        var seen = new List<(ushort Id, string Key)>();
        foreach (ContentTypeRegistration registration in registry.ByTypeId)
        {
            Assert.Equal(ContentRegistrationBand.Instances, registration.Band);
            Assert.True(registration.Type.IsInstances);
            seen.Add((registration.Type.Value, registration.TypeKey));
        }

        Assert.Equal(
            new (ushort, string)[]
            {
                (256, "mod"),
                (257, "mod_group"),
                (258, "rarity_rule"),
                (259, "unique_template"),
                (260, "socket_type"),
                (261, "crafting_currency"),
                (262, "rare_name_word"),
                (263, "mod_tier"),
                (264, "mod_tier_weight"),
                (265, "stat_line"),
                (266, "rarity_weight"),
                (267, "rarity_kind_limit"),
                (268, "unique_line"),
                (269, "unique_socket"),
                (270, "socket_tag_rule"),
                (271, "currency_step"),
                (272, "currency_guard"),
                (273, "rare_name_word_weight"),
            },
            seen);
    }

    [Fact]
    public void A_second_InstanceContentTypes_Register_on_one_registry_throws()
    {
        var registry = new ContentTypeRegistry();
        InstanceContentTypes.Register(registry);

        // Registration runs once, at process start. A second call is the ids already being taken, which is
        // the same refusal EngineContentTypes.Register makes.
        Assert.Throws<ContentRegistrationException>(() => InstanceContentTypes.Register(registry));
    }

    [Fact]
    public void InstanceContentTypes_Register_on_a_frozen_registry_throws()
    {
        var registry = new ContentTypeRegistry();
        registry.Freeze();

        Assert.Throws<ContentRegistrationException>(() => InstanceContentTypes.Register(registry));
    }

    [Fact]
    public void InstanceContentTypes_Register_sits_beside_the_engine_types_without_colliding()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);

        InstanceContentTypes.Register(registry);

        Assert.Equal(24, registry.ByTypeId.Count);
        Assert.True(registry.TryGetByKey(EngineContentTypes.SocketTypeTypeKey, out ContentTypeRegistration? socket));
        Assert.Equal(InstanceContentTypeIds.SocketTypeTypeId, socket.Type.Value);
    }

    [Fact]
    public void Every_currency_field_name_is_snake_case_and_derives_a_key_inside_the_bound()
    {
        foreach (FamilyType type in Currency)
        {
            foreach (ContentFieldEntry field in type.CreateSchema().Fields)
            {
                Assert.False(
                    ContentTextKey.ExceedsBound(type.Key, 64, field.Name),
                    ContentTextKey.Derive(type.Key, ReadOnlySpan<byte>.Empty, field.Name));
                foreach (char c in field.Name)
                {
                    Assert.True(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_', field.Name);
                }
            }
        }
    }

    /// <summary>A row carrying a value for EVERY field of one of the three, including every optional one.</summary>
    static ContentRow PopulatedCurrency(ContentTypeRegistration registration) => registration.TypeKey switch
    {
        InstanceContentTypeIds.CraftingCurrencyTypeKey => Row(
            registration,
            "whetstone",
            Marker(),
            Marker(),
            Reference(40),
            Int(1),
            Int(2)),
        InstanceContentTypeIds.CurrencyStepTypeKey => Row(
            registration,
            "whetstone_1",
            Reference(1),
            Int(1),
            Int(12),
            Int(1),
            Int(0),
            Int(0),
            Int(0)),
        InstanceContentTypeIds.CurrencyGuardTypeKey => Row(
            registration,
            "whetstone_1_g1",
            Reference(1),
            Reference(1),
            Int(1),
            Int(9),
            Int(0),
            Int(19)),
        _ => throw new InvalidOperationException(registration.TypeKey),
    };
}
