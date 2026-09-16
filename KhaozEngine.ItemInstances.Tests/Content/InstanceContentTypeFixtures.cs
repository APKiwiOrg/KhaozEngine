using System;
using System.Buffers;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;

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
/// The ten types of task 2 and every helper the family test files share, in the file named after it. It sat
/// beside the rarity family while it had one reader and now has five, and a shared fixture reached by
/// <c>using static</c> from five files is not findable from any of them.
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
