using System;
using System.Buffers;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;

namespace KhaozEngine.Tests.ItemInstances.Content;

/// <summary>One of the ten types of spec 8.5 to 8.8, everything its registration is handed.</summary>
/// <param name="Id">The type id of spec 8.1's table.</param>
/// <param name="Key">The stable type key.</param>
/// <param name="Visibility">The type level visibility of spec 8.1's table.</param>
/// <param name="ChunkSlots">The id slots per chunk of spec 8.1's table.</param>
/// <param name="MaxRowBytes">The row cap the slot count leaves room for.</param>
/// <param name="MaxDefinitionId">The per-type id ceiling a payload format imposes, or null.</param>
/// <param name="CreateSchema">The type's own ordered field list.</param>
/// <param name="CreateCodec">The type's own row codec over that field list.</param>
internal sealed record FamilyType(
    ushort Id,
    string Key,
    ContentVisibility Visibility,
    int ChunkSlots,
    int MaxRowBytes,
    int? MaxDefinitionId,
    Func<ContentFieldSchema> CreateSchema,
    Func<ContentTypeId, ContentFieldSchema, IContentRowCodec> CreateCodec);

/// <summary>
/// The ten types of task 2 and every helper the three family test files share. It lives beside the first
/// family rather than in a file of its own, because the plan's file list is three test files and a fourth
/// would be a file nobody asked for.
/// <para>
/// Every registry a fact builds is its OWN, so nothing here writes process-global state and no
/// <c>DisableParallelization</c> collection is needed.
/// </para>
/// </summary>
internal static class InstanceContentTypeFixtures
{
    /// <summary>The three types of spec 8.5, the rarity family.</summary>
    public static readonly FamilyType[] Rarity =
    [
        new(
            InstanceContentTypeIds.RarityRuleTypeId,
            InstanceContentTypeIds.RarityRuleTypeKey,
            RarityRuleContentType.DefaultVisibility,
            RarityRuleContentType.DefaultChunkSlots,
            RarityRuleContentType.MaxRowBytes,
            RarityRuleContentType.MaxDefinitionId,
            RarityRuleContentType.CreateSchema,
            static (type, schema) => new RarityRuleContentType.Codec(type, schema)),
        new(
            InstanceContentTypeIds.RarityWeightTypeId,
            InstanceContentTypeIds.RarityWeightTypeKey,
            RarityWeightContentType.DefaultVisibility,
            RarityWeightContentType.DefaultChunkSlots,
            RarityWeightContentType.MaxRowBytes,
            null,
            RarityWeightContentType.CreateSchema,
            static (type, schema) => new RarityWeightContentType.Codec(type, schema)),
        new(
            InstanceContentTypeIds.RarityKindLimitTypeId,
            InstanceContentTypeIds.RarityKindLimitTypeKey,
            RarityKindLimitContentType.DefaultVisibility,
            RarityKindLimitContentType.DefaultChunkSlots,
            RarityKindLimitContentType.MaxRowBytes,
            null,
            RarityKindLimitContentType.CreateSchema,
            static (type, schema) => new RarityKindLimitContentType.Codec(type, schema)),
    ];

    /// <summary>The three types of spec 8.6, the unique family.</summary>
    public static readonly FamilyType[] Unique =
    [
        new(
            InstanceContentTypeIds.UniqueTemplateTypeId,
            InstanceContentTypeIds.UniqueTemplateTypeKey,
            UniqueTemplateContentType.DefaultVisibility,
            UniqueTemplateContentType.DefaultChunkSlots,
            UniqueTemplateContentType.MaxRowBytes,
            null,
            UniqueTemplateContentType.CreateSchema,
            static (type, schema) => new UniqueTemplateContentType.Codec(type, schema)),
        new(
            InstanceContentTypeIds.UniqueLineTypeId,
            InstanceContentTypeIds.UniqueLineTypeKey,
            UniqueLineContentType.DefaultVisibility,
            UniqueLineContentType.DefaultChunkSlots,
            UniqueLineContentType.MaxRowBytes,
            null,
            UniqueLineContentType.CreateSchema,
            static (type, schema) => new UniqueLineContentType.Codec(type, schema)),
        new(
            InstanceContentTypeIds.UniqueSocketTypeId,
            InstanceContentTypeIds.UniqueSocketTypeKey,
            UniqueSocketContentType.DefaultVisibility,
            UniqueSocketContentType.DefaultChunkSlots,
            UniqueSocketContentType.MaxRowBytes,
            null,
            UniqueSocketContentType.CreateSchema,
            static (type, schema) => new UniqueSocketContentType.Codec(type, schema)),
    ];

    /// <summary>The four types of spec 8.7 and 8.8, the socket and rare-name families.</summary>
    public static readonly FamilyType[] SocketAndName =
    [
        new(
            InstanceContentTypeIds.SocketTypeTypeId,
            InstanceContentTypeIds.SocketTypeTypeKey,
            SocketTypeContentType.DefaultVisibility,
            SocketTypeContentType.DefaultChunkSlots,
            SocketTypeContentType.MaxRowBytes,
            null,
            SocketTypeContentType.CreateSchema,
            static (type, schema) => new SocketTypeContentType.Codec(type, schema)),
        new(
            InstanceContentTypeIds.SocketTagRuleTypeId,
            InstanceContentTypeIds.SocketTagRuleTypeKey,
            SocketTagRuleContentType.DefaultVisibility,
            SocketTagRuleContentType.DefaultChunkSlots,
            SocketTagRuleContentType.MaxRowBytes,
            null,
            SocketTagRuleContentType.CreateSchema,
            static (type, schema) => new SocketTagRuleContentType.Codec(type, schema)),
        new(
            InstanceContentTypeIds.RareNameWordTypeId,
            InstanceContentTypeIds.RareNameWordTypeKey,
            RareNameWordContentType.DefaultVisibility,
            RareNameWordContentType.DefaultChunkSlots,
            RareNameWordContentType.MaxRowBytes,
            null,
            RareNameWordContentType.CreateSchema,
            static (type, schema) => new RareNameWordContentType.Codec(type, schema)),
        new(
            InstanceContentTypeIds.RareNameWordWeightTypeId,
            InstanceContentTypeIds.RareNameWordWeightTypeKey,
            RareNameWordWeightContentType.DefaultVisibility,
            RareNameWordWeightContentType.DefaultChunkSlots,
            RareNameWordWeightContentType.MaxRowBytes,
            null,
            RareNameWordWeightContentType.CreateSchema,
            static (type, schema) => new RareNameWordWeightContentType.Codec(type, schema)),
    ];

    /// <summary>All ten, in the id order spec 8.1's table assigns them.</summary>
    public static readonly FamilyType[] All = [.. Rarity, .. Unique, .. SocketAndName];

    /// <summary>The type keys of one family, as theory data.</summary>
    public static TheoryData<string> Keys(IReadOnlyList<FamilyType> family)
    {
        var data = new TheoryData<string>();
        foreach (FamilyType type in family)
        {
            data.Add(type.Key);
        }

        return data;
    }

    /// <summary>The descriptor for one type key, out of all ten.</summary>
    public static FamilyType Find(string typeKey)
    {
        foreach (FamilyType type in All)
        {
            if (string.Equals(type.Key, typeKey, StringComparison.Ordinal))
            {
                return type;
            }
        }

        throw new InvalidOperationException(typeKey);
    }

    /// <summary>A fresh registry carrying one family, so no fact shares one with another.</summary>
    public static ContentTypeRegistry Registered(
        IReadOnlyList<FamilyType> family,
        ContentRegistrationBand band = ContentRegistrationBand.Instances)
    {
        var registry = new ContentTypeRegistry();
        foreach (FamilyType type in family)
        {
            Register(registry, type, band);
        }

        return registry;
    }

    /// <summary>Registers one type exactly as task 3's registration helper will.</summary>
    public static void Register(ContentTypeRegistry registry, FamilyType type, ContentRegistrationBand band)
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
            type.MaxRowBytes,
            type.MaxDefinitionId);
    }

    /// <summary>Looks one registration up, failing the fact rather than handing back a null.</summary>
    public static ContentTypeRegistration Lookup(ContentTypeRegistry registry, string typeKey)
    {
        Assert.True(registry.TryGetByKey(typeKey, out ContentTypeRegistration? registration));
        return registration;
    }

    /// <summary>The canonical bytes of one row.</summary>
    public static byte[] Encode(ContentTypeRegistration registration, ContentRow row)
    {
        var buffer = new ArrayBufferWriter<byte>();
        registration.Codec.Encode(row, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The row those bytes decode to, failing the fact with the reason when they do not.</summary>
    public static ContentRow Decode(ContentTypeRegistration registration, byte[] bytes)
    {
        Assert.True(
            registration.Codec.TryDecode(bytes, out ContentRow? row, out string? reason),
            reason ?? "no reason");
        return row;
    }

    /// <summary>An int field's value.</summary>
    public static ContentFieldValue Int(long value) => ContentFieldValue.OfNumber(ContentFieldKind.Int, value);

    /// <summary>A key reference field's value.</summary>
    public static ContentFieldValue Reference(int id)
        => ContentFieldValue.OfNumber(ContentFieldKind.KeyReference, id);

    /// <summary>A localized text key marker, which is always absent and always writes no bytes.</summary>
    public static ContentFieldValue Marker() => ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey);

    /// <summary>A row carrying id 0, which is what a codec round trip needs and no more.</summary>
    public static ContentRow Row(
        ContentTypeRegistration registration,
        string key,
        params ContentFieldValue[] fields)
        => RowAt(registration, 0, key, fields);

    /// <summary>A row carrying a real definition id, which the validator sweep needs.</summary>
    public static ContentRow RowAt(
        ContentTypeRegistration registration,
        int id,
        string key,
        params ContentFieldValue[] fields)
        => new(registration.Type, id, new ContentKey(key), 0, false, fields);

    /// <summary>A row carrying a value for EVERY field, including every optional one.</summary>
    public static ContentRow Populated(ContentTypeRegistration registration) => registration.TypeKey switch
    {
        InstanceContentTypeIds.RarityRuleTypeKey => Row(
            registration,
            "rare",
            Marker(),
            Int(4),
            Int(6),
            Int(3),
            Int(3),
            Int(2),
            Reference(2)),
        InstanceContentTypeIds.RarityWeightTypeKey => Row(
            registration,
            "rare_metal",
            Reference(3),
            Reference(5),
            Int(1200)),
        InstanceContentTypeIds.RarityKindLimitTypeKey => Row(
            registration,
            "rare_implicit",
            Reference(3),
            Int(3),
            Int(1)),
        InstanceContentTypeIds.UniqueTemplateTypeKey => Row(
            registration,
            "sunbrand",
            Reference(40),
            Marker(),
            Int(60),
            Int(850)),
        InstanceContentTypeIds.UniqueLineTypeKey => Row(
            registration,
            "sunbrand_line_1",
            Reference(11),
            Int(1),
            Reference(91),
            Int(1)),
        InstanceContentTypeIds.UniqueSocketTypeKey => Row(
            registration,
            "sunbrand_socket_1",
            Reference(11),
            Int(0),
            Reference(4)),
        InstanceContentTypeIds.SocketTypeTypeKey => Row(registration, "gem_socket", Marker(), Int(96)),
        InstanceContentTypeIds.SocketTagRuleTypeKey => Row(
            registration,
            "gem_socket_accepts_gem",
            Reference(4),
            Int(1),
            Reference(5),
            Int(SocketTagRuleContentType.RuleAccept)),
        InstanceContentTypeIds.RareNameWordTypeKey => Row(registration, "gloom", Marker(), Int(1)),
        InstanceContentTypeIds.RareNameWordWeightTypeKey => Row(
            registration,
            "gloom_metal",
            Reference(21),
            Reference(5),
            Int(400)),
        _ => throw new InvalidOperationException(registration.TypeKey),
    };

    /// <summary>One schema entry, pinned against the spec table that declared it.</summary>
    public static void AssertField(
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

    /// <summary>
    /// Writes a row body the way the generic walk would, bypassing the codec's own refusal, so the DECODE
    /// side can be asked about a value its encoder would never have written. Markers take no slot here
    /// because they take none on the wire either, so a caller passes the non-marker fields in schema order.
    /// </summary>
    public static byte[] Forge(ContentTypeRegistration registration, string key, params int[] values)
    {
        _ = registration;
        var buffer = new ArrayBufferWriter<byte>();
        Span<byte> scratch = stackalloc byte[5];

        ReadOnlySpan<byte> keyBytes = new ContentKey(key).Utf8;
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

    /// <summary>A candidate carrying exactly the rows handed in, built with no store and no file.</summary>
    public static ContentSnapshot Snapshot(ContentTypeRegistry registry, params ContentRow[] rows)
    {
        var builder = new ContentSnapshotBuilder(registry);
        foreach (ContentRow row in rows)
        {
            builder.AddRow(row);
        }

        return builder.Build();
    }

    /// <summary>The sweep, run the way a boot runs it, with no previous snapshot and no rules.</summary>
    public static ContentValidationReport Validate(ContentSnapshot candidate, ContentTypeRegistry registry)
        => ContentValidator.Validate(candidate, previous: null, [], registry);

    /// <summary>The one finding carrying that code, failing the fact when there is not exactly one.</summary>
    public static ContentFinding Single(ContentValidationReport report, string code)
    {
        var matches = new List<ContentFinding>();
        foreach (ContentFinding finding in report.Findings)
        {
            if (string.Equals(finding.Code, code, StringComparison.Ordinal))
            {
                matches.Add(finding);
            }
        }

        return Assert.Single(matches);
    }

    /// <summary>Asserts no finding carries that code, naming every finding when one does.</summary>
    public static void AssertNone(ContentValidationReport report, string code)
    {
        foreach (ContentFinding finding in report.Findings)
        {
            Assert.False(
                string.Equals(finding.Code, code, StringComparison.Ordinal),
                finding.Code + ": " + finding.Message);
        }
    }
}

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
            rule, "beyond", Marker(), Int(4), Int(6), Int(3), Int(3), Int(2), Reference(256));
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
        Assert.Equal(7, rule.Fields.Count);
        AssertField(rule, 0, "display_format", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true);
        AssertField(rule, 1, "min_affixes", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(rule, 2, "max_affixes", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(rule, 3, "max_prefixes", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(rule, 4, "max_suffixes", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(rule, 5, "name_word_positions", ContentFieldKind.Int, null, ContentVisibility.Client, true);
        AssertField(rule, 6, "upgrade_from", ContentFieldKind.KeyReference, "rarity_rule", ContentVisibility.Client, false);

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
        int naive = 1 + contentKey.Length + rule.Schema.Fields.Count;
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
                ContentTextKey.Derive(type.Key, default, field.Name));
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
            ContentFieldValue.Absent(ContentFieldKind.KeyReference));
}
