using System;
using System.Buffers;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;

namespace KhaozEngine.Tests.ItemInstances.Content;

/// <summary>
/// The five types of the <c>mod</c> family, spec 8.1 to 8.4: their ids, their keys, their chunk slot
/// counts, their type level visibility and every field of every schema, pinned.
/// <para>
/// The field NAMES are pinned literally rather than read back off the constants that produced them,
/// because a field name is what contracts 12.1 derives a localization key from. Renaming <c>line</c>
/// renames <c>mod.fine_crafted.line</c> in every translated catalog, so the rename has to go red here
/// first.
/// </para>
/// <para>
/// The registry is per instance, so every fact here builds its own and nothing writes process-global
/// state. No collection attribute is needed and none should be added.
/// </para>
/// </summary>
public class ModFamilyTests
{
    sealed record FamilyType(
        ushort Id,
        string Key,
        ContentVisibility Visibility,
        int ChunkSlots,
        int MaxRowBytes,
        Func<ContentFieldSchema> CreateSchema,
        Func<ContentTypeId, ContentFieldSchema, IContentRowCodec> CreateCodec);

    static readonly FamilyType[] Family =
    [
        new(
            InstanceContentTypeIds.ModTypeId,
            InstanceContentTypeIds.ModTypeKey,
            ModContentType.DefaultVisibility,
            ModContentType.DefaultChunkSlots,
            ModContentType.MaxRowBytes,
            ModContentType.CreateSchema,
            static (type, schema) => new ModContentType.Codec(type, schema)),
        new(
            InstanceContentTypeIds.ModGroupTypeId,
            InstanceContentTypeIds.ModGroupTypeKey,
            ModGroupContentType.DefaultVisibility,
            ModGroupContentType.DefaultChunkSlots,
            ModGroupContentType.MaxRowBytes,
            ModGroupContentType.CreateSchema,
            static (type, schema) => new ModGroupContentType.Codec(type, schema)),
        new(
            InstanceContentTypeIds.ModTierTypeId,
            InstanceContentTypeIds.ModTierTypeKey,
            ModTierContentType.DefaultVisibility,
            ModTierContentType.DefaultChunkSlots,
            ModTierContentType.MaxRowBytes,
            ModTierContentType.CreateSchema,
            static (type, schema) => new ModTierContentType.Codec(type, schema)),
        new(
            InstanceContentTypeIds.ModTierWeightTypeId,
            InstanceContentTypeIds.ModTierWeightTypeKey,
            ModTierWeightContentType.DefaultVisibility,
            ModTierWeightContentType.DefaultChunkSlots,
            ModTierWeightContentType.MaxRowBytes,
            ModTierWeightContentType.CreateSchema,
            static (type, schema) => new ModTierWeightContentType.Codec(type, schema)),
        new(
            InstanceContentTypeIds.StatLineTypeId,
            InstanceContentTypeIds.StatLineTypeKey,
            StatLineContentType.DefaultVisibility,
            StatLineContentType.DefaultChunkSlots,
            StatLineContentType.MaxRowBytes,
            StatLineContentType.CreateSchema,
            static (type, schema) => new StatLineContentType.Codec(type, schema)),
    ];

    public static TheoryData<string> TypeKeys
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (FamilyType type in Family)
            {
                data.Add(type.Key);
            }

            return data;
        }
    }

    static FamilyType Find(string typeKey)
    {
        foreach (FamilyType type in Family)
        {
            if (string.Equals(type.Key, typeKey, StringComparison.Ordinal))
            {
                return type;
            }
        }

        throw new InvalidOperationException(typeKey);
    }

    static ContentTypeRegistry Registered(ContentRegistrationBand band = ContentRegistrationBand.Instances)
    {
        var registry = new ContentTypeRegistry();
        foreach (FamilyType type in Family)
        {
            Register(registry, type, band);
        }

        return registry;
    }

    static void Register(ContentTypeRegistry registry, FamilyType type, ContentRegistrationBand band)
    {
        ContentFieldSchema schema = type.CreateSchema();
        registry.RegisterContentType(
            band,
            type.Id,
            type.Key,
            type.CreateCodec(new ContentTypeId(type.Id), schema),
            validator: null,
            schema,
            type.Visibility,
            type.ChunkSlots,
            type.MaxRowBytes);
    }

    static ContentTypeRegistration Lookup(ContentTypeRegistry registry, string typeKey)
    {
        Assert.True(registry.TryGetByKey(typeKey, out ContentTypeRegistration? registration));
        return registration;
    }

    static byte[] Encode(ContentTypeRegistration registration, ContentRow row)
    {
        var buffer = new ArrayBufferWriter<byte>();
        registration.Codec.Encode(row, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    static ContentRow Decode(ContentTypeRegistration registration, byte[] bytes)
    {
        Assert.True(
            registration.Codec.TryDecode(bytes, out ContentRow? row, out string? reason),
            reason ?? "no reason");
        return row;
    }

    static ContentFieldValue Int(long value) => ContentFieldValue.OfNumber(ContentFieldKind.Int, value);

    static ContentFieldValue Reference(int id) => ContentFieldValue.OfNumber(ContentFieldKind.KeyReference, id);

    static ContentFieldValue Flag(bool value) => ContentFieldValue.OfNumber(ContentFieldKind.Bool, value ? 1 : 0);

    static ContentFieldValue Marker() => ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey);

    static ContentRow Row(ContentTypeRegistration registration, string key, params ContentFieldValue[] fields)
        => new(registration.Type, 0, new ContentKey(key), 0, false, fields);

    /// <summary>A row carrying a value for EVERY field, including every optional one.</summary>
    static ContentRow Populated(ContentTypeRegistration registration) => registration.TypeKey switch
    {
        InstanceContentTypeIds.ModTypeKey => Row(
            registration,
            "fine_crafted",
            Int(2),
            Reference(7),
            Flag(true),
            Marker()),
        InstanceContentTypeIds.ModGroupTypeKey => Row(registration, "fire_damage", Int(2)),
        InstanceContentTypeIds.ModTierTypeKey => Row(
            registration,
            "fine_crafted_t3",
            Reference(11),
            Int(3),
            Int(40),
            Int(65535)),
        InstanceContentTypeIds.ModTierWeightTypeKey => Row(
            registration,
            "fine_crafted_t3_metal",
            Reference(31),
            Reference(5),
            Int(1200)),
        InstanceContentTypeIds.StatLineTypeKey => Row(
            registration,
            "fine_crafted_t3_armour",
            Reference(31),
            Int(1),
            Reference(9),
            Int(StatLineContentType.CombineFlat),
            Int(-20),
            Int(40),
            ContentRowCodecBase.TagListValue([5, 9]),
            Int(3)),
        _ => throw new InvalidOperationException(registration.TypeKey),
    };

    static void AssertField(
        ContentFieldSchema schema,
        int index,
        string name,
        ContentFieldKind kind,
        string? referenceTarget,
        ContentVisibility visibility,
        bool required)
    {
        ContentFieldEntry field = schema.Fields[index];
        Assert.Equal(name, field.Name);
        Assert.Equal(kind, field.Kind);
        Assert.Equal(referenceTarget, field.ReferenceTarget);
        Assert.Equal(visibility, field.Visibility);
        Assert.Equal(required, field.Required);
        Assert.Equal(1, field.Scale);
    }

    [Fact]
    public void The_five_register_in_the_Instances_band_at_256_257_263_264_and_265()
    {
        ContentTypeRegistry registry = Registered();

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
                (256, "mod"),
                (257, "mod_group"),
                (263, "mod_tier"),
                (264, "mod_tier_weight"),
                (265, "stat_line"),
            },
            seen);
    }

    [Theory]
    [MemberData(nameof(TypeKeys))]
    public void A_registration_naming_the_Engine_or_Game_band_for_one_of_these_ids_throws(string typeKey)
    {
        FamilyType type = Find(typeKey);

        Assert.Throws<ContentRegistrationException>(
            () => Register(new ContentTypeRegistry(), type, ContentRegistrationBand.Engine));
        Assert.Throws<ContentRegistrationException>(
            () => Register(new ContentTypeRegistry(), type, ContentRegistrationBand.Game));
    }

    [Theory]
    [MemberData(nameof(TypeKeys))]
    public void Every_field_name_is_snake_case_under_the_content_key_rules(string typeKey)
    {
        FamilyType type = Find(typeKey);
        Assert.Null(KeyDefect(type.Key));

        foreach (ContentFieldEntry field in type.CreateSchema().Fields)
        {
            Assert.Null(KeyDefect(field.Name));
            Assert.False(
                ContentTextKey.ExceedsBound(type.Key, 64, field.Name),
                ContentTextKey.Derive(type.Key, default, field.Name));
        }
    }

    /// <summary>Contracts 5.3's character rules, written out rather than read off an internal helper.</summary>
    static string? KeyDefect(string name)
    {
        if (name.Length is 0 or > 64)
        {
            return "length";
        }

        if (name[0] is '_' or (>= '0' and <= '9'))
        {
            return "leading character";
        }

        if (name[^1] == '_')
        {
            return "trailing underscore";
        }

        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool legal = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_';
            if (!legal)
            {
                return "character set";
            }

            if (c == '_' && i + 1 < name.Length && name[i + 1] == '_')
            {
                return "double underscore";
            }
        }

        return null;
    }

    [Theory]
    [MemberData(nameof(TypeKeys))]
    public void A_row_round_trips_through_its_codec_byte_for_byte(string typeKey)
    {
        ContentTypeRegistry registry = Registered();
        ContentTypeRegistration registration = Lookup(registry, typeKey);
        ContentRow row = Populated(registration);

        byte[] bytes = Encode(registration, row);
        ContentRow decoded = Decode(registration, bytes);

        Assert.Equal(row.Key, decoded.Key);
        Assert.Equal(row.Fields, decoded.Fields);
        Assert.Equal(bytes, Encode(registration, decoded));
    }

    [Fact]
    public void An_absent_optional_field_writes_the_zero_form_and_reads_back_absent()
    {
        ContentTypeRegistry registry = Registered();

        ContentTypeRegistration mod = Lookup(registry, InstanceContentTypeIds.ModTypeKey);
        byte[] modBytes = Encode(
            mod,
            Row(mod, "fine_crafted", Int(2), ContentFieldValue.Absent(ContentFieldKind.KeyReference), Flag(true), Marker()));

        // The key is one length byte plus twelve, so the three written fields are the last three bytes.
        Assert.Equal(16, modBytes.Length);
        Assert.Equal(0, modBytes[14]);
        ContentRow decodedMod = Decode(mod, modBytes);
        Assert.True(decodedMod.Fields[1].IsAbsent);
        Assert.Equal(2, decodedMod.Fields[0].Number);

        ContentTypeRegistration line = Lookup(registry, InstanceContentTypeIds.StatLineTypeKey);
        byte[] lineBytes = Encode(
            line,
            Row(
                line,
                "fine_crafted_t3_armour",
                Reference(31),
                Int(1),
                Reference(9),
                Int(StatLineContentType.CombineIncreased),
                Int(10),
                Int(40),
                ContentFieldValue.Absent(ContentFieldKind.TagList),
                ContentFieldValue.Absent(ContentFieldKind.Int)));

        Assert.Equal(0, lineBytes[^1]);
        Assert.Equal(0, lineBytes[^2]);
        ContentRow decodedLine = Decode(line, lineBytes);
        Assert.True(decodedLine.Fields[6].IsAbsent);
        Assert.True(decodedLine.Fields[7].IsAbsent);
    }

    [Fact]
    public void A_localized_text_key_field_writes_NO_bytes_and_derives_mod_fine_crafted_line()
    {
        ContentTypeRegistry registry = Registered();
        ContentTypeRegistration mod = Lookup(registry, InstanceContentTypeIds.ModTypeKey);
        ContentFieldSchema schema = mod.Schema;

        const string contentKey = "fine_crafted";
        byte[] bytes = Encode(
            mod,
            Row(mod, contentKey, Int(2), Reference(7), Flag(true), Marker()));

        // A naive reader spends at least one byte on every schema field. The marker spends none, so the
        // real row is exactly one byte shorter than that, and adding a stored value to it would show up
        // here first.
        int keyBytes = 1 + contentKey.Length;
        int naive = keyBytes + schema.Fields.Count;
        Assert.Equal(naive - 1, bytes.Length);

        Assert.Equal(
            "mod.fine_crafted.line",
            ContentTextKey.Derive(mod.TypeKey, new ContentKey(contentKey).Utf8, ModContentType.LineField));

        ContentFieldEntry marker = schema.Fields[^1];
        Assert.Equal(ModContentType.LineField, marker.Name);
        Assert.True(marker.IsDerivedMarker);
        Assert.DoesNotContain(ModContentType.LineField, mod.Codec.WrittenFields);
        Assert.True(Decode(mod, bytes).Fields[^1].IsAbsent);
    }

    [Fact]
    public void mod_tier_weight_is_ServerOnly_as_a_WHOLE_TYPE_rather_than_per_field()
    {
        ContentTypeRegistry registry = Registered();

        ContentTypeRegistration weight = Lookup(registry, InstanceContentTypeIds.ModTierWeightTypeKey);
        Assert.Equal(ContentVisibility.ServerOnly, weight.DefaultVisibility);
        foreach (ContentFieldEntry field in weight.Schema.Fields)
        {
            Assert.Equal(ContentVisibility.ServerOnly, field.Visibility);
        }

        // The other four are Client throughout, so no weight hides as a ServerOnly field inside a row a
        // client downloads. The client chunk builder omits whole FIELDS, which is why the split is a type.
        foreach (ContentTypeRegistration registration in registry.ByTypeId)
        {
            if (registration.Type.Value == InstanceContentTypeIds.ModTierWeightTypeId)
            {
                continue;
            }

            Assert.Equal(ContentVisibility.Client, registration.DefaultVisibility);
            foreach (ContentFieldEntry field in registration.Schema.Fields)
            {
                Assert.Equal(ContentVisibility.Client, field.Visibility);
            }
        }
    }

    [Fact]
    public void The_five_chunk_slot_counts_are_the_8_1_table_exactly()
    {
        ContentTypeRegistry registry = Registered();

        var seen = new List<(string Key, int Slots)>();
        foreach (ContentTypeRegistration registration in registry.ByTypeId)
        {
            seen.Add((registration.TypeKey, registration.ChunkSlots));
        }

        Assert.Equal(
            new (string, int)[]
            {
                ("mod", 4096),
                ("mod_group", 256),
                ("mod_tier", 16384),
                ("mod_tier_weight", 65536),
                ("stat_line", 32768),
            },
            seen);
    }

    [Theory]
    [MemberData(nameof(TypeKeys))]
    public void A_codec_that_writes_a_field_the_schema_omits_throws_at_registration(string typeKey)
    {
        FamilyType type = Find(typeKey);
        ContentFieldSchema schema = type.CreateSchema();

        var widened = new List<ContentFieldEntry>(schema.Fields)
        {
            new("extra", ContentFieldKind.Int, null, ContentVisibility.Client, false),
        };

        IContentRowCodec codec = type.CreateCodec(new ContentTypeId(type.Id), new ContentFieldSchema(widened));

        var registry = new ContentTypeRegistry();
        Assert.Throws<ContentRegistrationException>(() => registry.RegisterContentType(
            ContentRegistrationBand.Instances,
            type.Id,
            type.Key,
            codec,
            validator: null,
            schema,
            type.Visibility,
            type.ChunkSlots,
            type.MaxRowBytes));
    }

    [Fact]
    public void The_five_schemas_are_the_spec_tables_field_for_field()
    {
        ContentTypeRegistry registry = Registered();

        ContentFieldSchema mod = Lookup(registry, InstanceContentTypeIds.ModTypeKey).Schema;
        Assert.Equal(4, mod.Fields.Count);
        AssertField(mod, 0, "kind", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(mod, 1, "group_id", ContentFieldKind.KeyReference, "mod_group", ContentVisibility.Client, false);
        AssertField(mod, 2, "legacy", ContentFieldKind.Bool, null, ContentVisibility.Client, true);
        AssertField(mod, 3, "line", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true);

        ContentFieldSchema group = Lookup(registry, InstanceContentTypeIds.ModGroupTypeKey).Schema;
        Assert.Single(group.Fields);
        AssertField(group, 0, "max_per_item", ContentFieldKind.Int, null, ContentVisibility.Client, true);

        ContentFieldSchema tier = Lookup(registry, InstanceContentTypeIds.ModTierTypeKey).Schema;
        Assert.Equal(4, tier.Fields.Count);
        AssertField(tier, 0, "mod_id", ContentFieldKind.KeyReference, "mod", ContentVisibility.Client, true);
        AssertField(tier, 1, "ordinal", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(tier, 2, "item_level_min", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(tier, 3, "item_level_max", ContentFieldKind.Int, null, ContentVisibility.Client, true);

        ContentFieldSchema weight = Lookup(registry, InstanceContentTypeIds.ModTierWeightTypeKey).Schema;
        Assert.Equal(3, weight.Fields.Count);
        AssertField(weight, 0, "mod_tier_id", ContentFieldKind.KeyReference, "mod_tier", ContentVisibility.ServerOnly, true);
        AssertField(weight, 1, "tag_id", ContentFieldKind.KeyReference, "tag", ContentVisibility.ServerOnly, true);
        AssertField(weight, 2, "weight", ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true);

        ContentFieldSchema line = Lookup(registry, InstanceContentTypeIds.StatLineTypeKey).Schema;
        Assert.Equal(8, line.Fields.Count);
        AssertField(line, 0, "mod_tier_id", ContentFieldKind.KeyReference, "mod_tier", ContentVisibility.Client, true);
        AssertField(line, 1, "sort", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(line, 2, "stat_id", ContentFieldKind.KeyReference, "stat", ContentVisibility.Client, true);
        AssertField(line, 3, "combine", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(line, 4, "min", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(line, 5, "max", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(line, 6, "tag_scope", ContentFieldKind.TagList, "tag", ContentVisibility.Client, false);
        AssertField(line, 7, "condition_id", ContentFieldKind.Int, null, ContentVisibility.Client, false);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void A_mod_kind_outside_1_to_255_is_refused_on_both_sides(int kind)
    {
        ContentTypeRegistry registry = Registered();
        ContentTypeRegistration mod = Lookup(registry, InstanceContentTypeIds.ModTypeKey);

        ContentRow row = Row(mod, "fine_crafted", Int(kind), Reference(7), Flag(false), Marker());
        Assert.Throws<ArgumentException>(() => Encode(mod, row));

        byte[] bytes = Forge(mod, "fine_crafted", kind, 7, 0);
        Assert.False(mod.Codec.TryDecode(bytes, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    [Theory]
    [InlineData(ModContentType.PrefixKind)]
    [InlineData(ModContentType.MaxKind)]
    public void A_mod_kind_inside_1_to_255_round_trips(int kind)
    {
        ContentTypeRegistry registry = Registered();
        ContentTypeRegistration mod = Lookup(registry, InstanceContentTypeIds.ModTypeKey);
        ContentRow row = Row(mod, "fine_crafted", Int(kind), Reference(7), Flag(false), Marker());

        Assert.Equal(row.Fields, Decode(mod, Encode(mod, row)).Fields);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    [InlineData(-1)]
    public void A_mod_tier_ordinal_outside_1_to_255_is_refused_on_both_sides(int ordinal)
    {
        ContentTypeRegistry registry = Registered();
        ContentTypeRegistration tier = Lookup(registry, InstanceContentTypeIds.ModTierTypeKey);

        ContentRow row = Row(tier, "fine_crafted_t3", Reference(11), Int(ordinal), Int(40), Int(65535));
        Assert.Throws<ArgumentException>(() => Encode(tier, row));

        byte[] bytes = Forge(tier, "fine_crafted_t3", 11, ordinal, 40, 65535);
        Assert.False(tier.Codec.TryDecode(bytes, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(40, 0)]
    [InlineData(65536, 65536)]
    [InlineData(-1, 40)]
    public void A_mod_tier_item_level_outside_1_to_65535_is_refused_on_both_sides(int min, int max)
    {
        ContentTypeRegistry registry = Registered();
        ContentTypeRegistration tier = Lookup(registry, InstanceContentTypeIds.ModTierTypeKey);

        ContentRow row = Row(tier, "fine_crafted_t3", Reference(11), Int(3), Int(min), Int(max));
        Assert.Throws<ArgumentException>(() => Encode(tier, row));

        byte[] bytes = Forge(tier, "fine_crafted_t3", 11, 3, min, max);
        Assert.False(tier.Codec.TryDecode(bytes, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void A_stat_line_combine_that_is_not_1_or_2_or_3_is_refused_on_both_sides(int combine)
    {
        ContentTypeRegistry registry = Registered();
        ContentTypeRegistration line = Lookup(registry, InstanceContentTypeIds.StatLineTypeKey);

        ContentRow row = Row(
            line,
            "fine_crafted_t3_armour",
            Reference(31),
            Int(1),
            Reference(9),
            Int(combine),
            Int(10),
            Int(40),
            ContentFieldValue.Absent(ContentFieldKind.TagList),
            ContentFieldValue.Absent(ContentFieldKind.Int));
        Assert.Throws<ArgumentException>(() => Encode(line, row));

        byte[] bytes = Forge(line, "fine_crafted_t3_armour", 31, 1, 9, combine, 10, 40, 0, 0);
        Assert.False(line.Codec.TryDecode(bytes, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    /// <summary>
    /// Writes a row body the way the generic walk would, bypassing the codec's own refusal, so the DECODE
    /// side can be asked about a value its encoder would never have written.
    /// </summary>
    static byte[] Forge(ContentTypeRegistration registration, string key, params int[] values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Span<byte> scratch = stackalloc byte[5];

        var keyBytes = new ContentKey(key).Utf8;
        int written = ContentVarint.Write(scratch, (uint)keyBytes.Length);
        buffer.Write(scratch[..written]);
        buffer.Write(keyBytes);

        foreach (int value in values)
        {
            written = ContentVarint.Write(scratch, unchecked((uint)value));
            buffer.Write(scratch[..written]);
        }

        return buffer.WrittenSpan.ToArray();
    }
}
