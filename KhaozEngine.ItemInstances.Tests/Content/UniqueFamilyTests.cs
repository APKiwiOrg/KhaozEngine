using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;

namespace KhaozEngine.Tests.ItemInstances.Content;

/// <summary>
/// The three types of the unique family, spec 8.6: <c>unique_template</c>, <c>unique_line</c> and
/// <c>unique_socket</c>.
/// <para>
/// Two facts here are about the ONE mixed-visibility row in the band. <c>unique_template.weight</c> is a
/// <see cref="ContentVisibility.ServerOnly"/> FIELD on a <see cref="ContentVisibility.Client"/> type, which
/// contracts 4.7 allows because visibility is per field, and it needs no child type of its own because it
/// does not repeat.
/// </para>
/// <para>
/// The registry each fact builds is its own, so nothing here writes process-global state and no
/// <c>DisableParallelization</c> collection is needed.
/// </para>
/// </summary>
public class UniqueFamilyTests
{
    public static TheoryData<string> TypeKeys => Keys(Unique);

    [Fact]
    public void The_three_register_in_the_Instances_band_at_259_268_and_269()
    {
        ContentTypeRegistry registry = Registered(Unique);

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
                (259, "unique_template"),
                (268, "unique_line"),
                (269, "unique_socket"),
            },
            seen);
    }

    [Fact]
    public void The_three_schemas_are_the_spec_8_6_tables_field_for_field()
    {
        ContentTypeRegistry registry = Registered(Unique);

        ContentFieldSchema template = Lookup(registry, InstanceContentTypeIds.UniqueTemplateTypeKey).Schema;
        Assert.Equal(4, template.Fields.Count);
        AssertField(template, 0, "base_id", ContentFieldKind.KeyReference, "item", ContentVisibility.Client, true);
        AssertField(template, 1, "name", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true);
        AssertField(template, 2, "item_level_min", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(template, 3, "weight", ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true);

        ContentFieldSchema line = Lookup(registry, InstanceContentTypeIds.UniqueLineTypeKey).Schema;
        Assert.Equal(4, line.Fields.Count);
        AssertField(
            line, 0, "unique_template_id", ContentFieldKind.KeyReference, "unique_template", ContentVisibility.Client, true);
        AssertField(line, 1, "sort", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(line, 2, "mod_id", ContentFieldKind.KeyReference, "mod", ContentVisibility.Client, true);
        AssertField(line, 3, "tier_ordinal", ContentFieldKind.Int, null, ContentVisibility.Client, true);

        ContentFieldSchema socket = Lookup(registry, InstanceContentTypeIds.UniqueSocketTypeKey).Schema;
        Assert.Equal(3, socket.Fields.Count);
        AssertField(
            socket, 0, "unique_template_id", ContentFieldKind.KeyReference, "unique_template", ContentVisibility.Client, true);
        AssertField(socket, 1, "sort", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(
            socket, 2, "socket_type_id", ContentFieldKind.KeyReference, "socket_type", ContentVisibility.Client, false);
    }

    [Fact]
    public void The_three_chunk_slot_counts_and_row_caps_are_the_8_1_table_exactly()
    {
        ContentTypeRegistry registry = Registered(Unique);

        var seen = new List<(string Key, int Slots, int Cap)>();
        foreach (ContentTypeRegistration registration in registry.ByTypeId)
        {
            seen.Add((registration.TypeKey, registration.ChunkSlots, registration.MaxRowBytes));
        }

        // unique_line is the one of the three whose slot count will not carry the 1,024 byte default: at
        // 16,384 slots the chunk ceiling leaves 1,015 bytes per row, so the cap is the next power of two
        // under it.
        Assert.Equal(
            new (string, int, int)[]
            {
                ("unique_template", 4096, 1024),
                ("unique_line", 16384, 512),
                ("unique_socket", 4096, 1024),
            },
            seen);
    }

    [Fact]
    public void unique_template_weight_is_a_ServerOnly_FIELD_on_a_Client_type()
    {
        ContentTypeRegistry registry = Registered(Unique);
        ContentTypeRegistration template = Lookup(registry, InstanceContentTypeIds.UniqueTemplateTypeKey);

        Assert.Equal(ContentVisibility.Client, template.DefaultVisibility);

        var serverOnly = new List<string>();
        foreach (ContentFieldEntry field in template.Schema.Fields)
        {
            if (field.Visibility == ContentVisibility.ServerOnly)
            {
                serverOnly.Add(field.Name);
            }
        }

        // Exactly one field, and a scalar rather than a child type, because it does not repeat. The three
        // WHOLE ServerOnly types of spec 8.1 exist because a repeating weight had no way out of a row a
        // client downloads, and this one is the case that argument does not cover.
        Assert.Equal(new[] { UniqueTemplateContentType.WeightField }, serverOnly);
        Assert.Equal(ContentFieldKind.Int, template.Schema.Fields[3].Kind);
        Assert.True(template.Schema.Fields[3].Required);
    }

    [Fact]
    public void The_client_encode_check_omits_that_one_field_and_keeps_the_rest_of_the_row()
    {
        // No field-omitting client encoder ships yet, which is why KEC0014 is the validator's named quiet
        // hook. What this pins is the SHAPE that encoder has to produce: the ServerOnly field's slot writes
        // its zero form, every Client field keeps its bytes, and the row still decodes.
        ContentTypeRegistry registry = Registered(Unique);
        ContentTypeRegistration template = Lookup(registry, InstanceContentTypeIds.UniqueTemplateTypeKey);

        ContentRow authored = Row(template, "sunbrand", Reference(40), Marker(), Int(60), Int(850));
        byte[] serverBytes = Encode(template, authored);

        ContentRow client = ClientProjection(template, authored);
        byte[] clientBytes = Encode(template, client);

        // weight is the last field, so omitting it leaves every other byte where it was and replaces its two
        // varint bytes with the single zero byte of the absence convention.
        Assert.Equal(serverBytes.Length - 1, clientBytes.Length);
        Assert.Equal(serverBytes[..^2], clientBytes[..^1]);
        Assert.Equal(0, clientBytes[^1]);

        ContentRow decoded = Decode(template, clientBytes);
        Assert.Equal(authored.Key, decoded.Key);
        Assert.Equal(40, decoded.Fields[0].Number);
        Assert.True(decoded.Fields[1].IsAbsent);
        Assert.Equal(60, decoded.Fields[2].Number);

        // weight is REQUIRED, so its zero form reads back as a present 0 rather than as absent. A client
        // therefore sees the field with no information in it, which is the point.
        Assert.False(decoded.Fields[3].IsAbsent);
        Assert.Equal(0, decoded.Fields[3].Number);
    }

    [Fact]
    public void unique_socket_sort_IS_the_socket_index_of_kind_132_and_carries_authored_order()
    {
        ContentTypeRegistry registry = Registered(Unique);
        ContentTypeRegistration socket = Lookup(registry, InstanceContentTypeIds.UniqueSocketTypeKey);

        // One row per socket, and the sort IS the index, so the rows can seed kind 132 directly. A
        // (socket type, count) pair could not say what order the sockets sat in, and kind 132 is authored
        // order (spec 3.5).
        ContentRow first = Row(socket, "sunbrand_socket_1", Reference(11), Int(0), Reference(4));
        ContentRow second = Row(socket, "sunbrand_socket_2", Reference(11), Int(1), Reference(7));

        Assert.Equal(0, Decode(socket, Encode(socket, first)).Fields[1].Number);
        Assert.Equal(1, Decode(socket, Encode(socket, second)).Fields[1].Number);
        Assert.NotEqual(Encode(socket, first), Encode(socket, second));

        // Kind 132's Count is a VARINT rather than a byte (spec 3.3), so a socket index has no byte ceiling
        // and the codec puts none on it.
        ContentRow deep = Row(socket, "sunbrand_socket_300", Reference(11), Int(300), Reference(4));
        Assert.Equal(300, Decode(socket, Encode(socket, deep)).Fields[1].Number);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_unique_socket_sort_below_zero_is_refused_on_both_sides(int sort)
    {
        ContentTypeRegistry registry = Registered(Unique);
        ContentTypeRegistration socket = Lookup(registry, InstanceContentTypeIds.UniqueSocketTypeKey);

        ContentRow row = Row(socket, "sunbrand_socket_1", Reference(11), Int(sort), Reference(4));
        Assert.Throws<ArgumentException>(() => Encode(socket, row));

        byte[] bytes = Forge(socket, "sunbrand_socket_1", 11, sort, 4);
        Assert.False(socket.Codec.TryDecode(bytes, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    [Fact]
    public void A_unique_socket_socket_type_id_of_zero_means_no_restriction()
    {
        ContentTypeRegistry registry = Registered(Unique);
        ContentTypeRegistration socket = Lookup(registry, InstanceContentTypeIds.UniqueSocketTypeKey);

        ContentRow row = Row(
            socket,
            "sunbrand_socket_1",
            Reference(11),
            Int(0),
            ContentFieldValue.Absent(ContentFieldKind.KeyReference));
        byte[] bytes = Encode(socket, row);

        Assert.Equal(0, bytes[^1]);
        Assert.True(Decode(socket, bytes).Fields[2].IsAbsent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    [InlineData(-1)]
    public void A_unique_line_tier_ordinal_outside_1_to_255_is_refused_on_both_sides(int ordinal)
    {
        // Kind 131's entry stores the tier as ONE byte (spec 3.4), which is the same bound ModTierContentType
        // puts on the ordinal it is naming.
        ContentTypeRegistry registry = Registered(Unique);
        ContentTypeRegistration line = Lookup(registry, InstanceContentTypeIds.UniqueLineTypeKey);

        ContentRow row = Row(line, "sunbrand_line_1", Reference(11), Int(1), Reference(91), Int(ordinal));
        Assert.Throws<ArgumentException>(() => Encode(line, row));

        byte[] bytes = Forge(line, "sunbrand_line_1", 11, 1, 91, ordinal);
        Assert.False(line.Codec.TryDecode(bytes, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void A_unique_template_item_level_min_outside_1_to_65535_is_refused_on_both_sides(int itemLevel)
    {
        ContentTypeRegistry registry = Registered(Unique);
        ContentTypeRegistration template = Lookup(registry, InstanceContentTypeIds.UniqueTemplateTypeKey);

        ContentRow row = Row(template, "sunbrand", Reference(40), Marker(), Int(itemLevel), Int(850));
        Assert.Throws<ArgumentException>(() => Encode(template, row));

        byte[] bytes = Forge(template, "sunbrand", 40, itemLevel, 850);
        Assert.False(template.Codec.TryDecode(bytes, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    [Fact]
    public void A_unique_line_names_an_ORDINARY_mod_row_and_one_of_its_tier_ordinals()
    {
        ContentTypeRegistry registry = Registered(Unique);
        ContentFieldSchema line = Lookup(registry, InstanceContentTypeIds.UniqueLineTypeKey).Schema;

        // Spec 8.10's asymmetry, pinned: a unique's lines are not a second line shape. mod_id points at the
        // mod type task 1 shipped, and tier_ordinal is that mod's own authored ordinal, so kind 131's entry
        // needs no second decode path and a reroll never has to know what a unique is.
        Assert.Equal(InstanceContentTypeIds.ModTypeKey, line.Fields[2].ReferenceTarget);
        Assert.Equal(ContentFieldKind.KeyReference, line.Fields[2].Kind);
        Assert.Equal(ContentFieldKind.Int, line.Fields[3].Kind);
        Assert.Equal(ModTierContentType.MinOrdinal, UniqueLineContentType.MinTierOrdinal);
        Assert.Equal(ModTierContentType.MaxOrdinal, UniqueLineContentType.MaxTierOrdinal);
    }

    [Fact]
    public void unique_template_name_is_a_marker_deriving_unique_template_sunbrand_name()
    {
        ContentTypeRegistry registry = Registered(Unique);
        ContentTypeRegistration template = Lookup(registry, InstanceContentTypeIds.UniqueTemplateTypeKey);

        const string contentKey = "sunbrand";
        Assert.Equal(
            "unique_template.sunbrand.name",
            ContentTextKey.Derive(
                template.TypeKey, new ContentKey(contentKey).Utf8, UniqueTemplateContentType.NameField));
        Assert.True(template.Schema.Fields[1].IsDerivedMarker);
        Assert.DoesNotContain(UniqueTemplateContentType.NameField, template.Codec.WrittenFields);
    }

    [Theory]
    [MemberData(nameof(TypeKeys))]
    public void Every_field_name_is_snake_case_under_the_content_key_rules(string typeKey)
    {
        FamilyType type = Find(typeKey);

        foreach (ContentFieldEntry field in type.CreateSchema().Fields)
        {
            Assert.False(
                ContentTextKey.ExceedsBound(type.Key, 64, field.Name),
                ContentTextKey.Derive(type.Key, default, field.Name));
            foreach (char c in field.Name)
            {
                Assert.True(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_', field.Name);
            }
        }
    }

    /// <summary>
    /// The row a client chunk would carry: every <see cref="ContentVisibility.ServerOnly"/> field dropped to
    /// its absent form and every other value left exactly as authored.
    /// </summary>
    static ContentRow ClientProjection(ContentTypeRegistration registration, ContentRow row)
    {
        var fields = new ContentFieldValue[row.Fields.Count];
        for (int i = 0; i < fields.Length; i++)
        {
            ContentFieldEntry field = registration.Schema.Fields[i];
            fields[i] = field.Visibility == ContentVisibility.ServerOnly
                ? ContentFieldValue.Absent(field.Kind)
                : row.Fields[i];
        }

        return new ContentRow(row.Type, row.Id, row.Key, row.ParentId, row.IsRetired, fields);
    }
}
