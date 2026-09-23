using System;
using System.Buffers;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;

namespace KhaozEngine.Tests.ItemInstances.Content;

/// <summary>
/// The three types of the rarity family, spec 8.5: <c>rarity_rule</c>, <c>rarity_weight</c> and
/// <c>rarity_kind_limit</c>, plus the two facts that span every family the plan implements.
/// <para>
/// The field NAMES are pinned literally rather than read back off the constants that produced them,
/// because a field name is what contracts 12.1 derives a localization key from. Renaming
/// <c>display_format</c> renames <c>rarity_rule.rare.display_format</c> in every translated catalog, so
/// the rename has to go red here first.
/// </para>
/// </summary>
public class RarityFamilyTests
{
    public static TheoryData<string> TypeKeys => Keys(Rarity);

    public static TheoryData<string> AllTypeKeys => Keys(All);

    [Fact]
    public void The_three_register_in_the_Instances_band_at_258_266_and_267()
    {
        ContentTypeRegistry registry = Registered(Rarity);

        var seen = new List<(ushort Id, string Key)>();
        foreach (ContentTypeRegistration registration in registry.ByTypeId)
        {
            seen.Add((registration.Type.Value, registration.TypeKey));
            Assert.Equal(ContentRegistrationBand.Instances, registration.Band);
            Assert.True(registration.Type.IsInstances);
        }

        Assert.Equal(
            new (ushort, string)[] { (258, "rarity_rule"), (266, "rarity_weight"), (267, "rarity_kind_limit") },
            seen);
    }

    [Theory]
    [MemberData(nameof(AllTypeKeys))]
    public void A_registration_naming_the_Engine_or_Game_band_for_one_of_these_ids_throws(string typeKey)
    {
        FamilyType type = Find(typeKey);

        Assert.Throws<ContentRegistrationException>(
            () => Register(new ContentTypeRegistry(), type, ContentRegistrationBand.Engine));
        Assert.Throws<ContentRegistrationException>(
            () => Register(new ContentTypeRegistry(), type, ContentRegistrationBand.Game));
    }

    [Fact]
    public void Every_one_of_the_ten_round_trips_byte_for_byte()
    {
        ContentTypeRegistry registry = Registered(All);

        var seen = new List<string>();
        foreach (FamilyType type in All)
        {
            ContentTypeRegistration registration = Lookup(registry, type.Key);
            ContentRow row = Populated(registration);

            byte[] bytes = Encode(registration, row);
            ContentRow decoded = Decode(registration, bytes);

            Assert.Equal(row.Key, decoded.Key);
            Assert.Equal(row.Fields, decoded.Fields);
            Assert.Equal(bytes, Encode(registration, decoded));
            seen.Add(type.Key);
        }

        Assert.Equal(10, seen.Count);
    }

    [Fact]
    public void The_three_weight_types_are_ServerOnly_as_WHOLE_types()
    {
        // Spec 8.1 marks exactly three of the eighteen ServerOnly at the TYPE level, and the third is task
        // 1's mod_tier_weight rather than anything with "weight" in its name: rarity_kind_limit is Client.
        Assert.Equal(ContentVisibility.ServerOnly, ModTierWeightContentType.DefaultVisibility);
        Assert.Equal(ContentVisibility.ServerOnly, RarityWeightContentType.DefaultVisibility);
        Assert.Equal(ContentVisibility.ServerOnly, RareNameWordWeightContentType.DefaultVisibility);

        ContentTypeRegistry registry = Registered(All);
        var serverOnly = new List<string>();
        foreach (ContentTypeRegistration registration in registry.ByTypeId)
        {
            if (registration.DefaultVisibility != ContentVisibility.ServerOnly)
            {
                continue;
            }

            // A ServerOnly TYPE has no Client field anywhere, which is what makes omitting the whole type
            // from the client manifest equivalent to omitting every field of it.
            foreach (ContentFieldEntry field in registration.Schema.Fields)
            {
                Assert.Equal(ContentVisibility.ServerOnly, field.Visibility);
            }

            serverOnly.Add(registration.TypeKey);
        }

        Assert.Equal(new[] { "rarity_weight", "rare_name_word_weight" }, serverOnly);
    }

    [Fact]
    public void A_rarity_rule_id_above_255_is_refused_because_kind_130_is_ONE_BYTE()
    {
        // A codec never sees a row's own id: TryDecode hands back id 0 and the chunk row table owns it
        // (ContentRowCodecBase.TryDecode), so the ceiling is declared at REGISTRATION and the sweep is what
        // refuses the row. What the codec does refuse is a rarity id sitting in a FIELD.
        ContentTypeRegistry registry = Registered(Rarity);
        ContentTypeRegistration rule = Lookup(registry, InstanceContentTypeIds.RarityRuleTypeKey);

        Assert.Equal(255, RarityRuleContentType.MaxDefinitionId);
        Assert.Equal(RarityRuleContentType.MaxDefinitionId, rule.MaxDefinitionId);

        ContentSnapshot candidate = Snapshot(
            registry, RarityRuleRow(rule, 255, "mythic"), RarityRuleRow(rule, 256, "beyond"));
        ContentValidationReport report = Validate(candidate, registry);

        ContentFinding finding = Single(report, "KEC0042");
        Assert.Equal(256, finding.Id);
        Assert.False(report.IsValid);

        ContentRow parented = Row(
            rule, "beyond", Marker(), Int(4), Int(6), Int(3), Int(3), Int(2), Reference(256),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes));
        Assert.Throws<ArgumentException>(() => Encode(rule, parented));

        byte[] bytes = Forge(rule, "beyond", 4, 6, 3, 3, 2, 256);
        Assert.False(rule.Codec.TryDecode(bytes, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    [Fact]
    public void The_three_schemas_are_the_spec_8_5_tables_field_for_field()
    {
        ContentTypeRegistry registry = Registered(Rarity);

        ContentFieldSchema rule = Lookup(registry, InstanceContentTypeIds.RarityRuleTypeKey).Schema;
        Assert.Equal(7, rule.BaselineFieldCount);
        Assert.Equal(8, rule.Fields.Count);
        AssertField(rule, 0, "display_format", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true);
        AssertField(rule, 1, "min_affixes", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(rule, 2, "max_affixes", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(rule, 3, "max_prefixes", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(rule, 4, "max_suffixes", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(rule, 5, "name_word_positions", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(rule, 6, "upgrade_from", ContentFieldKind.KeyReference, "rarity_rule", ContentVisibility.Client, false);
        AssertField(rule, 7, "display_rgb", ContentFieldKind.OpaqueBytes, null, ContentVisibility.Client, false);

        ContentFieldSchema weight = Lookup(registry, InstanceContentTypeIds.RarityWeightTypeKey).Schema;
        Assert.Equal(3, weight.Fields.Count);
        AssertField(weight, 0, "rarity_rule_id", ContentFieldKind.KeyReference, "rarity_rule", ContentVisibility.ServerOnly, true);
        AssertField(weight, 1, "tag_id", ContentFieldKind.KeyReference, "tag", ContentVisibility.ServerOnly, true);
        AssertField(weight, 2, "weight", ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true);

        ContentFieldSchema limit = Lookup(registry, InstanceContentTypeIds.RarityKindLimitTypeKey).Schema;
        Assert.Equal(3, limit.Fields.Count);
        AssertField(limit, 0, "rarity_rule_id", ContentFieldKind.KeyReference, "rarity_rule", ContentVisibility.Client, true);
        AssertField(limit, 1, "mod_kind", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(limit, 2, "max_count", ContentFieldKind.Int, null, ContentVisibility.Client, true);
    }

    [Fact]
    public void A_seven_field_rarity_row_keeps_its_original_bytes()
    {
        ContentTypeRegistry registry = Registered(Rarity);
        ContentTypeRegistration rule = Lookup(registry, InstanceContentTypeIds.RarityRuleTypeKey);
        byte[] oldBytes = Forge(rule, "normal", 4, 6, 3, 3, 2, 0);

        ContentRow decoded = Decode(rule, oldBytes);

        Assert.True(decoded.Fields[7].IsAbsent);
        Assert.Equal(oldBytes, Encode(rule, decoded));
    }

    [Theory]
    [InlineData("000000")]
    [InlineData("ffffff")]
    public void A_rarity_row_keeps_an_explicit_opaque_rgb_value(string hex)
    {
        ContentTypeRegistry registry = Registered(Rarity);
        ContentTypeRegistration rule = Lookup(registry, InstanceContentTypeIds.RarityRuleTypeKey);
        ContentRow row = Row(rule, "normal", Marker(), Int(0), Int(0), Int(0), Int(0), Int(0),
            ContentFieldValue.Absent(ContentFieldKind.KeyReference),
            ContentFieldValue.OfBytes(ContentFieldKind.OpaqueBytes, Convert.FromHexString(hex)));

        ContentRow decoded = Decode(rule, Encode(rule, row));

        Assert.False(decoded.Fields[7].IsAbsent);
        Assert.Equal(hex, Convert.ToHexString(decoded.Fields[7].Bytes.Span).ToLowerInvariant());
    }

    [Theory]
    [InlineData("11")]
    [InlineData("1122")]
    [InlineData("11223344")]
    public void A_rarity_colour_other_than_three_bytes_is_refused_on_both_sides(string hex)
    {
        ContentTypeRegistry registry = Registered(Rarity);
        ContentTypeRegistration rule = Lookup(registry, InstanceContentTypeIds.RarityRuleTypeKey);
        byte[] colour = Convert.FromHexString(hex);
        ContentRow row = Row(rule, "normal", Marker(), Int(0), Int(0), Int(0), Int(0), Int(0),
            ContentFieldValue.Absent(ContentFieldKind.KeyReference),
            ContentFieldValue.OfBytes(ContentFieldKind.OpaqueBytes, colour));

        Assert.Throws<ArgumentException>(() => Encode(rule, row));

        byte[] prefix = Forge(rule, "normal", 0, 0, 0, 0, 0, 0);
        byte[] malformed = new byte[prefix.Length + 1 + colour.Length];
        prefix.CopyTo(malformed, 0);
        malformed[prefix.Length] = (byte)colour.Length;
        colour.CopyTo(malformed, prefix.Length + 1);
        Assert.False(rule.Codec.TryDecode(malformed, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    [Fact]
    public void The_three_chunk_slot_counts_and_row_caps_are_the_8_1_table_exactly()
    {
        ContentTypeRegistry registry = Registered(Rarity);

        var seen = new List<(string Key, int Slots, int Cap)>();
        foreach (ContentTypeRegistration registration in registry.ByTypeId)
        {
            seen.Add((registration.TypeKey, registration.ChunkSlots, registration.MaxRowBytes));
        }

        Assert.Equal(
            new (string, int, int)[]
            {
                ("rarity_rule", 256, 1024),
                ("rarity_weight", 1024, 1024),
                ("rarity_kind_limit", 1024, 1024),
            },
            seen);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void A_rarity_rule_affix_count_inside_0_to_255_is_an_ordinary_row(int count)
    {
        ContentTypeRegistry registry = Registered(Rarity);
        ContentTypeRegistration rule = Lookup(registry, InstanceContentTypeIds.RarityRuleTypeKey);

        ContentRow row = RarityRuleRow(rule, 0, "tibia_plain", count, count, count, count, 0);
        Assert.Equal(row.Fields, Decode(rule, Encode(rule, row)).Fields);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(-1)]
    public void A_rarity_rule_count_outside_0_to_255_is_refused_on_both_sides(int count)
    {
        // Kind 131's Count is a BYTE (spec 3.3) and kind 134's WordCount is a byte too, so every one of the
        // five counts on this row is capped at 255 by the payload rather than by taste.
        ContentTypeRegistry registry = Registered(Rarity);
        ContentTypeRegistration rule = Lookup(registry, InstanceContentTypeIds.RarityRuleTypeKey);

        for (int field = 0; field < 5; field++)
        {
            int[] c = [4, 6, 3, 3, 2];
            c[field] = count;

            ContentRow row = RarityRuleRow(rule, 0, "beyond", c[0], c[1], c[2], c[3], c[4]);
            Assert.Throws<ArgumentException>(() => Encode(rule, row));

            byte[] bytes = Forge(rule, "beyond", c[0], c[1], c[2], c[3], c[4], 0);
            Assert.False(rule.Codec.TryDecode(bytes, out _, out string? reason));
            Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(256)]
    [InlineData(-1)]
    public void A_rarity_kind_limit_mod_kind_outside_3_to_255_is_refused_on_both_sides(int modKind)
    {
        // Spec 8.5 says the field carries "a mod.kind above 2", because kinds 1 and 2 are already counted by
        // max_prefixes and max_suffixes, and spec 8.2 gives the game 3 to 255.
        ContentTypeRegistry registry = Registered(Rarity);
        ContentTypeRegistration limit = Lookup(registry, InstanceContentTypeIds.RarityKindLimitTypeKey);

        ContentRow row = Row(limit, "rare_implicit", Reference(3), Int(modKind), Int(1));
        Assert.Throws<ArgumentException>(() => Encode(limit, row));

        byte[] bytes = Forge(limit, "rare_implicit", 3, modKind, 1);
        Assert.False(limit.Codec.TryDecode(bytes, out _, out string? reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    [Fact]
    public void A_child_rarity_id_above_255_is_refused_on_both_sides_too()
    {
        ContentTypeRegistry registry = Registered(Rarity);

        ContentTypeRegistration weight = Lookup(registry, InstanceContentTypeIds.RarityWeightTypeKey);
        ContentRow weightRow = Row(weight, "beyond_metal", Reference(256), Reference(5), Int(1200));
        Assert.Throws<ArgumentException>(() => Encode(weight, weightRow));
        Assert.False(weight.Codec.TryDecode(
            Forge(weight, "beyond_metal", 256, 5, 1200), out _, out string? weightReason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, weightReason);

        ContentTypeRegistration limit = Lookup(registry, InstanceContentTypeIds.RarityKindLimitTypeKey);
        ContentRow limitRow = Row(limit, "beyond_implicit", Reference(256), Int(3), Int(1));
        Assert.Throws<ArgumentException>(() => Encode(limit, limitRow));
        Assert.False(limit.Codec.TryDecode(
            Forge(limit, "beyond_implicit", 256, 3, 1), out _, out string? limitReason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, limitReason);
    }

    [Fact]
    public void upgrade_from_is_a_SINGLE_parent_so_the_rarities_form_a_forest()
    {
        ContentTypeRegistry registry = Registered(Rarity);
        ContentTypeRegistration rule = Lookup(registry, InstanceContentTypeIds.RarityRuleTypeKey);

        // One field, one parent, optional. A list kind here would make "what does this rarity upgrade into"
        // a question with several answers, and the set rarity primitive of spec 10.2 walks exactly one edge.
        ContentFieldEntry upgrade = rule.Schema.Fields[6];
        Assert.Equal(ContentFieldKind.KeyReference, upgrade.Kind);
        Assert.False(upgrade.Required);
        Assert.Equal(InstanceContentTypeIds.RarityRuleTypeKey, upgrade.ReferenceTarget);

        byte[] bytes = Encode(rule, RarityRuleRow(rule, 1, "normal"));
        Assert.Equal(0, bytes[^1]);
        Assert.True(Decode(rule, bytes).Fields[6].IsAbsent);
    }

    [Fact]
    public void display_format_is_a_marker_deriving_rarity_rule_rare_display_format()
    {
        ContentTypeRegistry registry = Registered(Rarity);
        ContentTypeRegistration rule = Lookup(registry, InstanceContentTypeIds.RarityRuleTypeKey);

        const string contentKey = "rare";
        byte[] bytes = Encode(rule, RarityRuleRow(rule, 3, contentKey));

        // A naive reader spends at least one byte on every schema field. The marker spends none, so the real
        // row is exactly one byte shorter than that.
        int naive = 1 + contentKey.Length + rule.Schema.BaselineFieldCount;
        Assert.Equal(naive - 1, bytes.Length);

        Assert.Equal(
            "rarity_rule.rare.display_format",
            ContentTextKey.Derive(
                rule.TypeKey, new ContentKey(contentKey).Utf8, RarityRuleContentType.DisplayFormatField));
        Assert.True(rule.Schema.Fields[0].IsDerivedMarker);
        Assert.DoesNotContain(RarityRuleContentType.DisplayFormatField, rule.Codec.WrittenFields);
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

    static ContentRow RarityRuleRow(ContentTypeRegistration rule, int id, string key)
        => RarityRuleRow(rule, id, key, 4, 6, 3, 3, 2);

    static ContentRow RarityRuleRow(
        ContentTypeRegistration rule,
        int id,
        string key,
        int minAffixes,
        int maxAffixes,
        int maxPrefixes,
        int maxSuffixes,
        int nameWordPositions)
        => RowAt(
            rule,
            id,
            key,
            Marker(),
            Int(minAffixes),
            Int(maxAffixes),
            Int(maxPrefixes),
            Int(maxSuffixes),
            Int(nameWordPositions),
            ContentFieldValue.Absent(ContentFieldKind.KeyReference),
            ContentFieldValue.Absent(ContentFieldKind.OpaqueBytes));
}
