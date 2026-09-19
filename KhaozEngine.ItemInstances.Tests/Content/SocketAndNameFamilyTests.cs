using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;

namespace KhaozEngine.Tests.ItemInstances.Content;

/// <summary>
/// The four types of spec 8.7 and 8.8: <c>socket_type</c> and its <c>socket_tag_rule</c> children, and
/// <c>rare_name_word</c> with its <c>rare_name_word_weight</c> children.
/// <para>
/// <c>socket_type</c> is the one registration in this plan that closes a LATE BINDING the shipped engine
/// already carries. <c>base_socket.socket_type</c> points at the type key
/// <see cref="EngineContentTypes.SocketTypeTypeKey"/>, and until something registers under it every
/// <c>base_socket</c> row naming a socket type draws <c>KEC0007</c>. Two facts here are that before and
/// that after.
/// </para>
/// <para>
/// The registry each fact builds is its own, so nothing here writes process-global state and no
/// <c>DisableParallelization</c> collection is needed.
/// </para>
/// </summary>
public class SocketAndNameFamilyTests
{
    public static TheoryData<string> TypeKeys => Keys(SocketAndName);

    [Fact]
    public void The_four_register_in_the_Instances_band_at_260_262_270_and_273()
    {
        ContentTypeRegistry registry = Registered(SocketAndName);

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
                (260, "socket_type"),
                (262, "rare_name_word"),
                (270, "socket_tag_rule"),
                (273, "rare_name_word_weight"),
            },
            seen);
    }

    [Fact]
    public void The_four_schemas_are_the_spec_8_7_and_8_8_tables_field_for_field()
    {
        ContentTypeRegistry registry = Registered(SocketAndName);

        ContentFieldSchema socket = Lookup(registry, InstanceContentTypeIds.SocketTypeTypeKey).Schema;
        Assert.Equal(2, socket.Fields.Count);
        AssertField(
            socket, 0, "display_format", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true);
        AssertField(socket, 1, "max_nested_bytes", ContentFieldKind.Int, null, ContentVisibility.Client, true);

        ContentFieldSchema rule = Lookup(registry, InstanceContentTypeIds.SocketTagRuleTypeKey).Schema;
        Assert.Equal(4, rule.Fields.Count);
        AssertField(
            rule, 0, "socket_type_id", ContentFieldKind.KeyReference, "socket_type", ContentVisibility.Client, true);
        AssertField(rule, 1, "sort", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(rule, 2, "tag_id", ContentFieldKind.KeyReference, "tag", ContentVisibility.Client, true);
        AssertField(rule, 3, "rule", ContentFieldKind.Int, null, ContentVisibility.Client, true);

        ContentFieldSchema word = Lookup(registry, InstanceContentTypeIds.RareNameWordTypeKey).Schema;
        Assert.Equal(2, word.Fields.Count);
        AssertField(word, 0, "text", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true);
        AssertField(word, 1, "position", ContentFieldKind.Int, null, ContentVisibility.Client, true);

        ContentFieldSchema weight = Lookup(registry, InstanceContentTypeIds.RareNameWordWeightTypeKey).Schema;
        Assert.Equal(3, weight.Fields.Count);
        AssertField(
            weight,
            0,
            "rare_name_word_id",
            ContentFieldKind.KeyReference,
            "rare_name_word",
            ContentVisibility.ServerOnly,
            true);
        AssertField(weight, 1, "tag_id", ContentFieldKind.KeyReference, "tag", ContentVisibility.ServerOnly, true);
        AssertField(weight, 2, "weight", ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true);
    }

    [Fact]
    public void The_four_chunk_slot_counts_and_row_caps_are_the_8_1_table_exactly()
    {
        ContentTypeRegistry registry = Registered(SocketAndName);

        var seen = new List<(string Key, int Slots, int Cap)>();
        foreach (ContentTypeRegistration registration in registry.ByTypeId)
        {
            seen.Add((registration.TypeKey, registration.ChunkSlots, registration.MaxRowBytes));
        }

        // rare_name_word_weight is the one of the four whose slot count will not carry the 1,024 byte
        // default: at 16,384 slots the chunk ceiling leaves 1,015 bytes per row.
        Assert.Equal(
            new (string, int, int)[]
            {
                ("socket_type", 256, 1024),
                ("rare_name_word", 4096, 1024),
                ("socket_tag_rule", 1024, 1024),
                ("rare_name_word_weight", 16384, 512),
            },
            seen);
    }

    [Fact]
    public void socket_type_registering_at_260_resolves_the_shipped_base_socket_late_binding()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);

        ContentFieldEntry socketField = SocketTypeFieldOfBaseSocket(registry);
        string target = Assert.IsType<string>(socketField.ReferenceTarget);
        Assert.Equal(EngineContentTypes.SocketTypeTypeKey, target);

        // Before: the shipped engine type points at a key nothing is registered under, which is what its own
        // doc comment calls the late binding.
        Assert.False(registry.TryGetByKey(target, out _));

        Register(registry, Find(InstanceContentTypeIds.SocketTypeTypeKey), ContentRegistrationBand.Instances);

        Assert.True(registry.TryGetByKey(target, out ContentTypeRegistration? registered));
        Assert.Equal(InstanceContentTypeIds.SocketTypeTypeId, registered.Type.Value);
        Assert.Equal(260, registered.Type.Value);
        Assert.Equal(ContentRegistrationBand.Instances, registered.Band);

        // One key, written once in the engine and re-exported by the band that fills it, so the two cannot
        // drift into two spellings of the same late binding.
        Assert.Equal(EngineContentTypes.SocketTypeTypeKey, InstanceContentTypeIds.SocketTypeTypeKey);
    }

    [Fact]
    public void A_base_socket_row_naming_a_socket_type_id_resolves_once_both_are_registered()
    {
        var engineOnly = new ContentTypeRegistry();
        EngineContentTypes.Register(engineOnly);

        ContentValidationReport dangling = Validate(
            Snapshot(engineOnly, ItemRow(engineOnly, 40), BaseSocketRow(engineOnly, 7, item: 40, socketType: 4)),
            engineOnly);

        ContentFinding finding = Single(dangling, "KEC0007");
        Assert.Equal(EngineContentTypes.BaseSocketTypeId, finding.Type.Value);
        Assert.Equal(7, finding.Id);
        Assert.Contains(EngineContentTypes.SocketTypeTypeKey, finding.Message, StringComparison.Ordinal);

        var both = new ContentTypeRegistry();
        EngineContentTypes.Register(both);
        Register(both, Find(InstanceContentTypeIds.SocketTypeTypeKey), ContentRegistrationBand.Instances);
        ContentTypeRegistration socketType = Lookup(both, InstanceContentTypeIds.SocketTypeTypeKey);

        ContentValidationReport resolved = Validate(
            Snapshot(
                both,
                ItemRow(both, 40),
                BaseSocketRow(both, 7, item: 40, socketType: 4),
                RowAt(socketType, 4, "gem_socket", Marker(), Int(96))),
            both);

        AssertNone(resolved, "KEC0007");
        AssertNone(resolved, "KEC0006");
        Assert.True(resolved.IsValid, Describe(resolved));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    public void rare_name_word_position_is_1_to_255(int position)
    {
        ContentTypeRegistry registry = Registered(SocketAndName);
        ContentTypeRegistration word = Lookup(registry, InstanceContentTypeIds.RareNameWordTypeKey);

        Assert.Equal(1, RareNameWordContentType.MinPosition);
        Assert.Equal(255, RareNameWordContentType.MaxPosition);

        ContentRow row = Row(word, "gloom", Marker(), Int(position));
        Assert.Equal(position, Decode(word, Encode(word, row)).Fields[1].Number);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    [InlineData(-1)]
    public void A_rare_name_word_position_outside_1_to_255_is_refused_on_both_sides(int position)
    {
        // Kind 134's WordCount is a byte (spec 3.3), so a position past 255 is a slot the payload cannot
        // name and a position of 0 is no slot at all.
        ContentTypeRegistry registry = Registered(SocketAndName);
        ContentTypeRegistration word = Lookup(registry, InstanceContentTypeIds.RareNameWordTypeKey);

        ContentRow row = Row(word, "gloom", Marker(), Int(position));
        Assert.Throws<ArgumentException>(() => Encode(word, row));

        byte[] bytes = Forge(word, "gloom", position);
        Assert.False(word.Codec.TryDecode(bytes, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    [Fact]
    public void rare_name_word_text_is_a_marker_deriving_rare_name_word_gloom_text()
    {
        ContentTypeRegistry registry = Registered(SocketAndName);
        ContentTypeRegistration word = Lookup(registry, InstanceContentTypeIds.RareNameWordTypeKey);

        const string contentKey = "gloom";
        Assert.Equal(
            "rare_name_word.gloom.text",
            ContentTextKey.Derive(word.TypeKey, new ContentKey(contentKey).Utf8, RareNameWordContentType.TextField));
        Assert.True(word.Schema.Fields[0].IsDerivedMarker);
        Assert.DoesNotContain(RareNameWordContentType.TextField, word.Codec.WrittenFields);

        // The marker writes no bytes, so the row is the key plus the one position varint and nothing else.
        byte[] bytes = Encode(word, Row(word, contentKey, Marker(), Int(1)));
        Assert.Equal(1 + contentKey.Length + 1, bytes.Length);
    }

    [Fact]
    public void A_socket_type_max_nested_bytes_of_zero_means_the_whole_payload_budget()
    {
        ContentTypeRegistry registry = Registered(SocketAndName);
        ContentTypeRegistration socket = Lookup(registry, InstanceContentTypeIds.SocketTypeTypeKey);

        // 0 is not "no nesting". It is the full budget, named by the one constant that already holds the
        // number rather than by a second copy of 512.
        Assert.Equal(0, SocketTypeContentType.FullPayloadBudget);
        Assert.Equal(ItemInstancePayload.MaxInstancePayloadBytes, SocketTypeContentType.MaxNestedBytesCeiling);

        ContentRow row = Row(socket, "gem_socket", Marker(), Int(SocketTypeContentType.FullPayloadBudget));
        ContentRow decoded = Decode(socket, Encode(socket, row));

        // max_nested_bytes is REQUIRED, so its zero form reads back present rather than absent, which is
        // what keeps 0 an authored value with a meaning instead of an empty field.
        Assert.False(decoded.Fields[1].IsAbsent);
        Assert.Equal(0, decoded.Fields[1].Number);
    }

    [Theory]
    [InlineData(513)]
    [InlineData(-1)]
    public void A_socket_type_max_nested_bytes_outside_the_payload_budget_is_refused_on_both_sides(int budget)
    {
        ContentTypeRegistry registry = Registered(SocketAndName);
        ContentTypeRegistration socket = Lookup(registry, InstanceContentTypeIds.SocketTypeTypeKey);

        ContentRow row = Row(socket, "gem_socket", Marker(), Int(budget));
        Assert.Throws<ArgumentException>(() => Encode(socket, row));

        byte[] bytes = Forge(socket, "gem_socket", budget);
        Assert.False(socket.Codec.TryDecode(bytes, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    [Fact]
    public void socket_tag_rule_rule_is_1_accept_and_2_reject_and_nothing_else()
    {
        ContentTypeRegistry registry = Registered(SocketAndName);
        ContentTypeRegistration rule = Lookup(registry, InstanceContentTypeIds.SocketTagRuleTypeKey);

        Assert.Equal(1, SocketTagRuleContentType.RuleAccept);
        Assert.Equal(2, SocketTagRuleContentType.RuleReject);

        foreach (int value in new[] { SocketTagRuleContentType.RuleAccept, SocketTagRuleContentType.RuleReject })
        {
            ContentRow row = Row(rule, "gem_socket_rule", Reference(4), Int(1), Reference(5), Int(value));
            Assert.Equal(value, Decode(rule, Encode(rule, row)).Fields[3].Number);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-1)]
    public void A_socket_tag_rule_rule_that_is_not_1_or_2_is_refused_on_both_sides(int value)
    {
        ContentTypeRegistry registry = Registered(SocketAndName);
        ContentTypeRegistration rule = Lookup(registry, InstanceContentTypeIds.SocketTagRuleTypeKey);

        ContentRow row = Row(rule, "gem_socket_rule", Reference(4), Int(1), Reference(5), Int(value));
        Assert.Throws<ArgumentException>(() => Encode(rule, row));

        byte[] bytes = Forge(rule, "gem_socket_rule", 4, 1, 5, value);
        Assert.False(rule.Codec.TryDecode(bytes, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
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
                ContentTextKey.Derive(type.Key, ReadOnlySpan<byte>.Empty, field.Name));
            foreach (char c in field.Name)
            {
                Assert.True(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_', field.Name);
            }
        }
    }

    static ContentFieldEntry SocketTypeFieldOfBaseSocket(ContentTypeRegistry registry)
    {
        ContentTypeRegistration baseSocket = Lookup(registry, EngineContentTypes.BaseSocketTypeKey);
        Assert.True(baseSocket.Schema.TryGet(BaseSocketContentType.SocketTypeField, out ContentFieldEntry? field));
        return field;
    }

    /// <summary>A clean item base carrying two socket slots and nothing else worth a finding.</summary>
    static ContentRow ItemRow(ContentTypeRegistry registry, int id)
    {
        ContentTypeRegistration item = Lookup(registry, EngineContentTypes.ItemTypeKey);
        return RowAt(
            item,
            id,
            "greatsword",
            Marker(),
            Marker(),
            ContentFieldValue.Absent(ContentFieldKind.TagList),
            ContentFieldValue.OfNumber(ContentFieldKind.Bool, 0),
            Int(1),
            ContentFieldValue.OfNumber(ContentFieldKind.Bool, 1),
            ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, 10),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes),
            ContentFieldValue.Absent(ContentFieldKind.Int),
            ContentFieldValue.Absent(ContentFieldKind.ScaledInt),
            ContentFieldValue.Absent(ContentFieldKind.ScaledInt),
            ContentFieldValue.Absent(ContentFieldKind.Int),
            Int(2),
            ContentFieldValue.Absent(ContentFieldKind.KeyReference));
    }

    /// <summary>One authored socket on that base, naming a socket type by id.</summary>
    static ContentRow BaseSocketRow(ContentTypeRegistry registry, int id, int item, int socketType)
    {
        ContentTypeRegistration baseSocket = Lookup(registry, EngineContentTypes.BaseSocketTypeKey);
        return RowAt(baseSocket, id, "greatsword_socket_1", Reference(item), Int(0), Reference(socketType));
    }

    static string Describe(ContentValidationReport report)
    {
        var lines = new List<string>(report.Findings.Count);
        foreach (ContentFinding finding in report.Findings)
        {
            lines.Add(finding.Code + " " + finding.Message);
        }

        return string.Join(Environment.NewLine, lines);
    }
}
