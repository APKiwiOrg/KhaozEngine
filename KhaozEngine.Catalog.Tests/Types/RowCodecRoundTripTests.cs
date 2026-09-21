using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Types;

/// <summary>
/// The positional row walk of spec 7.3: the key first, then each schema field in order, a derived marker
/// contributing nothing at all, and a decode that is TOTAL because the bytes arrive from a remote peer.
/// <para>
/// The worked example of spec 7.9 is pinned byte for byte here, because it is the one place the format is
/// written out in full and a codec that drifts from it produces a pack no other reader agrees with.
/// </para>
/// </summary>
public class RowCodecRoundTripTests
{
    static readonly string[] EveryTypeKey =
        ["tag", "item", "stat", "loot_table", "loot_entry", "base_socket", "item_category"];

    /// <summary>The reason tokens a row decode may return, contracts 9.7 plus the varint tokens.</summary>
    static readonly string[] DecodeReasons =
    [
        "field-truncated",
        "field-malformed",
        "varint-not-minimal",
        "varint-overflow",
    ];

    public static TheoryData<string> TypeKeys
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string key in EveryTypeKey)
            {
                data.Add(key);
            }

            return data;
        }
    }

    static ContentTypeRegistration Type(string typeKey)
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
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

    static ContentFieldValue Number(ContentFieldKind kind, long value)
        => ContentFieldValue.OfNumber(kind, value);

    static ContentFieldValue Asset(string reference)
        => ContentFieldValue.OfBytes(ContentFieldKind.OpaqueBytes, Encoding.UTF8.GetBytes(reference));

    static ContentRow Row(ContentTypeRegistration registration, string key, params ContentFieldValue[] fields)
        => new(registration.Type, 0, new ContentKey(key), 0, false, fields);

    /// <summary>A row carrying a value for EVERY field, including every optional one.</summary>
    static ContentRow Populated(ContentTypeRegistration registration) => registration.TypeKey switch
    {
        "tag" => Row(
            registration,
            "metal",
            ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
            Number(ContentFieldKind.Int, 10)),
        "item" => Row(
            registration,
            "bronze_sword",
            ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
            ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
            ContentRowCodecBase.TagListValue([4, 1, 9]),
            Number(ContentFieldKind.Bool, 0),
            Number(ContentFieldKind.Int, 1),
            Number(ContentFieldKind.Bool, 1),
            Number(ContentFieldKind.ScaledInt, 250),
            Asset("ui/icons/bronze_sword.png"),
            Asset("kit/bronze_sword.glb"),
            Asset("kit/held/bronze_sword.glb"),
            Number(ContentFieldKind.Int, 1),
            Number(ContentFieldKind.ScaledInt, -30500),
            Number(ContentFieldKind.ScaledInt, 45000),
            Number(ContentFieldKind.Int, 100),
            Number(ContentFieldKind.Int, 3),
            Number(ContentFieldKind.KeyReference, 7),
            Number(ContentFieldKind.KeyReference, 2)),
        "item_category" => Row(
            registration,
            "tool",
            ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
            Number(ContentFieldKind.Int, 30)),
        "stat" => Row(
            registration,
            "attack_speed",
            ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
            Number(ContentFieldKind.Int, 100),
            Number(ContentFieldKind.Int, -5000),
            Number(ContentFieldKind.Int, 5000),
            ContentRowCodecBase.TagListValue([3, 1]),
            ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey)),
        "loot_table" => Row(
            registration,
            "goblin_drops",
            Number(ContentFieldKind.Int, 2),
            ContentRowCodecBase.TagListValue([9])),
        "loot_entry" => Row(
            registration,
            "goblin_drops_coin",
            Number(ContentFieldKind.KeyReference, 4),
            Number(ContentFieldKind.KeyReference, 12),
            Number(ContentFieldKind.KeyReference, 5),
            Number(ContentFieldKind.Int, 50),
            Number(ContentFieldKind.Int, 2500),
            Number(ContentFieldKind.Bool, 1),
            Number(ContentFieldKind.Int, 1),
            Number(ContentFieldKind.Int, 6),
            Number(ContentFieldKind.Int, 10),
            ContentRowCodecBase.TagListValue([2, 8])),
        _ => Row(
            registration,
            "bronze_sword_socket_0",
            Number(ContentFieldKind.KeyReference, 12),
            Number(ContentFieldKind.Int, 0),
            Number(ContentFieldKind.KeyReference, 2048)),
    };

    /// <summary>A row carrying the required fields only, every optional field absent.</summary>
    static ContentRow AbsentOptionals(ContentTypeRegistration registration)
    {
        var values = new ContentFieldValue[registration.Schema.Fields.Count];
        for (int i = 0; i < values.Length; i++)
        {
            ContentFieldEntry field = registration.Schema.Fields[i];
            values[i] = field.Required && !field.IsDerivedMarker
                ? Minimal(field)
                : ContentFieldValue.Absent(field.Kind);
        }

        return new ContentRow(registration.Type, 0, new ContentKey("bare"), 0, false, values);
    }

    static ContentFieldValue Minimal(ContentFieldEntry field) => field.Kind switch
    {
        ContentFieldKind.TagList => ContentRowCodecBase.TagListValue([1]),
        ContentFieldKind.OpaqueBytes => Asset("a"),
        _ => Number(field.Kind, 1),
    };

    [Theory]
    [MemberData(nameof(TypeKeys))]
    public void EveryTypeRoundTripsAFullyPopulatedRowByteForByte(string typeKey)
    {
        ContentTypeRegistration registration = Type(typeKey);
        ContentRow row = Populated(registration);

        byte[] encoded = Encode(registration, row);
        ContentRow decoded = Decode(registration, encoded);

        Assert.Equal(row.Key, decoded.Key);
        Assert.Equal(registration.Type, decoded.Type);
        Assert.Equal(row.Fields, decoded.Fields);
        Assert.Equal(encoded, Encode(registration, decoded));
    }

    [Theory]
    [MemberData(nameof(TypeKeys))]
    public void EveryTypeRoundTripsARowWithEveryOptionalFieldAbsent(string typeKey)
    {
        ContentTypeRegistration registration = Type(typeKey);
        ContentRow row = AbsentOptionals(registration);

        byte[] encoded = Encode(registration, row);
        ContentRow decoded = Decode(registration, encoded);

        Assert.Equal(row.Fields, decoded.Fields);
        Assert.Equal(encoded, Encode(registration, decoded));
        for (int i = 0; i < registration.Schema.Fields.Count; i++)
        {
            if (!registration.Schema.Fields[i].Required)
            {
                Assert.True(decoded.Fields[i].IsAbsent, registration.Schema.Fields[i].Name);
            }
        }
    }

    [Fact]
    public void TheTagRowsOfSpecSevenNineEncodeToTheirWorkedBytes()
    {
        ContentTypeRegistration registration = Type("tag");

        byte[] metal = Encode(registration, Populated(registration));
        byte[] twoHanded = Encode(
            registration,
            Row(
                registration,
                "two_handed",
                ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
                Number(ContentFieldKind.Int, 20)));

        Assert.Equal(new byte[] { 0x05, 0x6D, 0x65, 0x74, 0x61, 0x6C, 0x0A }, metal);
        Assert.Equal(
            new byte[] { 0x0A, 0x74, 0x77, 0x6F, 0x5F, 0x68, 0x61, 0x6E, 0x64, 0x65, 0x64, 0x14 },
            twoHanded);
    }

    [Fact]
    public void AMarkerFieldContributesZeroBytes()
    {
        ContentTypeRegistration registration = Type("tag");

        byte[] encoded = Encode(registration, Populated(registration));

        // The key is six bytes, a varint length of 5 then "metal", so everything the two-field schema
        // encodes is the one byte after it: the marker wrote nothing and sort 10 wrote 0A.
        Assert.Equal(new byte[] { 0x0A }, encoded[6..]);
    }

    [Fact]
    public void ATagListPreservesAuthoredOrderAndIsNeverSorted()
    {
        ContentTypeRegistration registration = Type("loot_table");
        ContentRow row = Row(
            registration,
            "ordered",
            Number(ContentFieldKind.Int, 1),
            ContentRowCodecBase.TagListValue([9, 2, 7]));

        byte[] encoded = Encode(registration, row);
        ContentRow decoded = Decode(registration, encoded);

        // key "ordered" is 8 bytes, roll_count 1 is one byte, then the tag list: count 3 then 9, 2, 7.
        Assert.Equal(new byte[] { 0x03, 0x09, 0x02, 0x07 }, encoded[9..13]);
        Assert.Equal(new byte[] { 0x09, 0x02, 0x07 }, decoded.Fields[1].Bytes.ToArray());
    }

    [Fact]
    public void ReadTagListReturnsIdsInAuthoredOrder()
    {
        ContentFieldValue value = ContentRowCodecBase.TagListValue([9, 2, 7]);
        Span<int> destination = stackalloc int[4];

        int count = ContentRowCodecBase.ReadTagList(in value, destination);

        Assert.Equal(3, count);
        Assert.Equal([9, 2, 7], destination[..count].ToArray());
    }

    [Fact]
    public void ReadTagListKeepsTheIdsBeforeAMalformedVarint()
    {
        ContentFieldValue value = ContentFieldValue.OfBytes(ContentFieldKind.TagList, new byte[] { 9, 0x80 });
        Span<int> destination = stackalloc int[2];

        int count = ContentRowCodecBase.ReadTagList(in value, destination);

        Assert.Equal(1, count);
        Assert.Equal(9, destination[0]);
    }

    [Fact]
    public void ANegativeScaledValueRoundTripsThroughTheUnsignedVarint()
    {
        ContentTypeRegistration registration = Type("stat");
        ContentRow decoded = Decode(registration, Encode(registration, Populated(registration)));

        Assert.Equal(-5000L, decoded.Fields[2].Number);
        Assert.Equal(5000L, decoded.Fields[3].Number);
    }

    [Fact]
    public void AnAssetReferenceOverTheCapIsRefused()
    {
        ContentTypeRegistration registration = Type("item");
        byte[] body = ItemBodyWithIcon(new string('a', 129));

        Assert.False(registration.Codec.TryDecode(body, out _, out string? reason));
        Assert.Equal("field-malformed", reason);
    }

    [Fact]
    public void AnAssetReferenceOutsideTheCharacterSetIsRefused()
    {
        ContentTypeRegistration registration = Type("item");
        byte[] body = ItemBodyWithIcon("UI/Icons/Sword.png");

        Assert.False(registration.Codec.TryDecode(body, out _, out string? reason));
        Assert.Equal("field-malformed", reason);
    }

    [Fact]
    public void EncodeRefusesAnAssetReferenceTheDecoderWouldRefuse()
    {
        ContentTypeRegistration registration = Type("item");
        ContentRow row = Populated(registration);
        var fields = new List<ContentFieldValue>(row.Fields) { [7] = Asset(new string('a', 129)) };

        Assert.Throws<ArgumentException>(
            () => Encode(registration, new ContentRow(row.Type, 0, row.Key, 0, false, fields)));
    }

    /// <summary>
    /// Every prefix of a whole body, held to the schema evolution rule rather than to a flat refusal.
    /// <para>
    /// A prefix that ends INSIDE a field, or that leaves a required field with no bytes, is refused with a
    /// listed reason. A prefix that ends exactly on a field boundary with only optional fields left is a row
    /// written under an earlier version of this schema, and it DECODES with that tail absent. Nothing throws
    /// either way, because the bytes arrive from a remote peer.
    /// </para>
    /// <para>
    /// The decode is pinned rather than merely allowed: re-encoding what came back has to reproduce the prefix
    /// exactly and then write the zero form of each missing field and nothing else. A decoder that invented a
    /// value for a field the bytes never carried would fail that, and so would one that lost a field the bytes
    /// did carry.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TypeKeys))]
    public void ATruncatedBodyIsRefusedOrReadsAsAnEarlierSchemaAndNeverThrows(string typeKey)
    {
        ContentTypeRegistration registration = Type(typeKey);
        byte[] whole = Encode(registration, Populated(registration));

        for (int length = 0; length < whole.Length; length++)
        {
            string where = length.ToString(CultureInfo.InvariantCulture);
            if (!registration.Codec.TryDecode(whole.AsSpan(0, length), out ContentRow? row, out string? reason))
            {
                Assert.Contains(reason, DecodeReasons);
                continue;
            }

            byte[] again = Encode(registration, row);
            Assert.True(again.Length >= length, where);
            Assert.True(whole.AsSpan(0, length).SequenceEqual(again.AsSpan(0, length)), where);
            for (int i = length; i < again.Length; i++)
            {
                Assert.True(again[i] == 0, where);
            }
        }
    }

    /// <summary>
    /// A body that stops after the LAST required field of a schema whose tail is optional is exactly what an
    /// older publish wrote, and it comes back with that tail absent rather than refused. The item type is the
    /// one this matters for: every catalog published before <c>category</c> existed ends one field early.
    /// </summary>
    [Fact]
    public void ABodyEndingWhereAnEarlierSchemaEndedDecodesWithTheNewFieldAbsent()
    {
        ContentTypeRegistration registration = Type("item");
        byte[] whole = Encode(registration, Populated(registration));

        // The populated row's category is a one byte varint, so the body an older publish wrote is this one
        // without its last byte.
        byte[] older = whole[..^1];

        Assert.True(registration.Codec.TryDecode(older, out ContentRow? row, out string? reason), reason);

        int category = IndexOf(registration, ItemContentType.CategoryField);
        Assert.True(row.Fields[category].IsAbsent);
        for (int i = 0; i < category; i++)
        {
            Assert.Equal(Populated(registration).Fields[i], row.Fields[i]);
        }
    }

    static int IndexOf(ContentTypeRegistration registration, string fieldName)
    {
        for (int i = 0; i < registration.Schema.Fields.Count; i++)
        {
            if (string.Equals(registration.Schema.Fields[i].Name, fieldName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        Assert.Fail("The schema carries no field named " + fieldName);
        return -1;
    }

    [Theory]
    [MemberData(nameof(TypeKeys))]
    public void ATrailingByteIsRefusedRatherThanIgnored(string typeKey)
    {
        ContentTypeRegistration registration = Type(typeKey);
        byte[] whole = [.. Encode(registration, Populated(registration)), 0x00];

        Assert.False(registration.Codec.TryDecode(whole, out _, out string? reason));
        Assert.Equal("field-malformed", reason);
    }

    [Fact]
    public void ABoolByteAboveOneIsRefusedBecauseTheFormatIsCanonical()
    {
        // item, whose fourth field is the first Bool in the schema: key "ok", the two derived markers writing
        // nothing, an empty tag list, then a stackable byte of 2.
        ContentTypeRegistration registration = Type("item");
        byte[] body = [0x02, 0x6F, 0x6B, 0x00, 0x02];

        Assert.False(registration.Codec.TryDecode(body, out _, out string? reason));
        Assert.Equal("field-malformed", reason);
    }

    [Fact]
    public void ANonMinimalVarintIsRefused()
    {
        ContentTypeRegistration registration = Type("tag");
        byte[] body = [0x02, 0x6F, 0x6B, 0x8A, 0x00];

        Assert.False(registration.Codec.TryDecode(body, out _, out string? reason));
        Assert.Equal("varint-not-minimal", reason);
    }

    [Fact]
    public void AKeyLengthPastTheEndIsTruncatedRatherThanAThrow()
    {
        ContentTypeRegistration registration = Type("tag");
        byte[] body = [0x40, 0x6F, 0x6B];

        Assert.False(registration.Codec.TryDecode(body, out _, out string? reason));
        Assert.Equal("field-truncated", reason);
    }

    /// <summary>An item body carrying the given icon reference and the zero form of every other field.</summary>
    static byte[] ItemBodyWithIcon(string icon)
    {
        byte[] key = "probe"u8.ToArray();
        byte[] reference = Encoding.UTF8.GetBytes(icon);
        var body = new List<byte>();
        AppendVarint(body, (uint)key.Length);
        body.AddRange(key);

        // tags, stackable, max_stack, tradable and value all take their zero form, then the icon.
        body.AddRange([0x00, 0x00, 0x00, 0x00, 0x00]);
        AppendVarint(body, (uint)reference.Length);
        body.AddRange(reference);

        // mesh, held_mesh, ground_pose, icon_tilt, icon_spin, durability_max, socket_max, equip_profile,
        // category.
        body.AddRange([0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        return [.. body];
    }

    static void AppendVarint(List<byte> destination, uint value)
    {
        Span<byte> scratch = stackalloc byte[5];
        int written = ContentVarint.Write(scratch, value);
        for (int i = 0; i < written; i++)
        {
            destination.Add(scratch[i]);
        }
    }
}
