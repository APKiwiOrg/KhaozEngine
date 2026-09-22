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
    /// Every prefix of a whole body, held to the schema evolution rule exactly: a prefix decodes IF AND ONLY
    /// IF it is the canonical SHORT form of the same row, the one the type's first release would have written
    /// with every appended field absent. Every other prefix is refused with a listed reason, and nothing
    /// throws either way because the bytes arrive from a remote peer.
    /// <para>
    /// Stating it as an equivalence is what makes it a real assertion. The permissive half alone would pass a
    /// decoder that accepted a body ending on any optional field boundary inside the BASELINE, which is a
    /// corrupt row reading as an older one, and every type whose baseline is its whole field list would then
    /// have lost a refusal it had before this rule existed. For those types the short form IS the whole body,
    /// so no prefix may decode at all and this theory is the flat refusal it always was.
    /// </para>
    /// <para>
    /// <b>This theory does NOT cover the redundant long form.</b> It generates prefixes of a whole body, so
    /// every case it runs is shorter than that body and the long form is never built. The facts that do cover
    /// it are <see cref="AnExplicitZeroForAnAppendedFieldIsRefusedAsARedundantEncoding"/>, which appends the
    /// zero byte to the short form of a real item row, and
    /// <see cref="EveryKindsZeroFormEncodesToNothingAtTheTail"/>, which does the same per field kind.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(TypeKeys))]
    public void APrefixDecodesOnlyWhenItIsTheCanonicalShortFormOfTheSameRow(string typeKey)
    {
        ContentTypeRegistration registration = Type(typeKey);
        ContentRow row = Populated(registration);
        byte[] whole = Encode(registration, row);
        byte[] shortForm = Encode(registration, WithAppendedAbsent(registration, row));

        for (int length = 0; length < whole.Length; length++)
        {
            string where = length.ToString(CultureInfo.InvariantCulture);
            bool decoded = registration.Codec.TryDecode(
                whole.AsSpan(0, length), out ContentRow? read, out string? reason);

            bool isShortForm = length == shortForm.Length
                && whole.AsSpan(0, length).SequenceEqual(shortForm);
            Assert.True(decoded == isShortForm, where + " decoded " + decoded);

            if (!decoded || read is null)
            {
                Assert.Contains(reason, DecodeReasons);
                continue;
            }

            // What came back re-encodes to the very bytes it was read from, which is what says the decoder
            // neither invented a value for a field the prefix never carried nor lost one it did.
            Assert.True(shortForm.AsSpan().SequenceEqual(Encode(registration, read)), where);
            for (int i = registration.Schema.BaselineFieldCount; i < read.Fields.Count; i++)
            {
                Assert.True(read.Fields[i].IsAbsent, where);
            }
        }
    }

    /// <summary>
    /// A body that stops at the schema's BASELINE is exactly what the type's first release wrote, and it comes
    /// back with the appended tail absent rather than refused. The item type is the one this matters for:
    /// every catalog published before <c>category</c> existed ends at field sixteen.
    /// </summary>
    [Fact]
    public void ABodyEndingAtTheBaselineDecodesWithTheAppendedFieldAbsent()
    {
        ContentTypeRegistration registration = Type("item");
        ContentRow row = Populated(registration);
        byte[] whole = Encode(registration, row);

        // The populated row's category is a one byte varint, so the body an older publish wrote is this one
        // without its last byte, and the encoder produces exactly that for a row that sets no category.
        byte[] older = whole[..^1];
        Assert.Equal(older, Encode(registration, WithAppendedAbsent(registration, row)));

        Assert.True(registration.Codec.TryDecode(older, out ContentRow? read, out string? reason), reason);

        int category = IndexOf(registration, ItemContentType.CategoryField);
        Assert.Equal(ItemContentType.BaselineFieldCount, category);
        Assert.True(read.Fields[category].IsAbsent);
        for (int i = 0; i < category; i++)
        {
            Assert.Equal(row.Fields[i], read.Fields[i]);
        }
    }

    /// <summary>
    /// The explicit zero form of a trailing appended field is REFUSED. It is a second encoding of a row that
    /// already has one, and a content-addressed format cannot carry two byte strings for one row: accepting it
    /// would let the same rows publish under two chunk hashes. The release that appends a field is the first
    /// that could write such a body, so nothing has ever produced one and nothing is being taken away.
    /// </summary>
    [Fact]
    public void AnExplicitZeroForAnAppendedFieldIsRefusedAsARedundantEncoding()
    {
        ContentTypeRegistration registration = Type("item");
        byte[] shortForm = Encode(registration, WithAppendedAbsent(registration, Populated(registration)));
        byte[] longForm = [.. shortForm, (byte)0];

        Assert.True(registration.Codec.TryDecode(shortForm, out ContentRow? read, out string? reason), reason);
        Assert.True(read.Fields[^1].IsAbsent);

        Assert.False(registration.Codec.TryDecode(longForm, out _, out reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    /// <summary>
    /// <b>An explicit ZERO in an appended field encodes to the short form, byte identical to absent.</b>
    /// Absence and zero are the same byte, and the decoder hands an optional field reading as zero back as
    /// absent, so an encoder that kept a trailing explicit zero would write a row its own decoder reports
    /// differently and would give one row two chunk hashes.
    /// <para>
    /// A key reference of 0 is not a hypothetical: the engine documents it as the legal stored value of
    /// <c>item.equip_profile</c> when no type is registered under the key, and the SQLite store persists any
    /// value that is not absent.
    /// </para>
    /// </summary>
    [Fact]
    public void AnExplicitZeroInAnAppendedFieldEncodesToTheShortForm()
    {
        ContentTypeRegistration registration = Type("item");
        ContentRow absent = WithAppendedAbsent(registration, Populated(registration));

        var zeroed = new ContentFieldValue[absent.Fields.Count];
        for (int i = 0; i < zeroed.Length; i++)
        {
            zeroed[i] = i < registration.Schema.BaselineFieldCount
                ? absent.Fields[i]
                : ContentFieldValue.OfNumber(registration.Schema.Fields[i].Kind, 0);
        }

        byte[] shortForm = Encode(registration, absent);
        byte[] fromZero = Encode(
            registration,
            new ContentRow(absent.Type, absent.Id, absent.Key, absent.ParentId, absent.IsRetired, zeroed));

        Assert.Equal(shortForm, fromZero);
        Assert.True(registration.Codec.TryDecode(fromZero, out ContentRow? read, out string? reason), reason);
        Assert.True(read.Fields[^1].IsAbsent);
        Assert.Equal(fromZero, Encode(registration, read));
    }

    /// <summary>
    /// The zero form is a per KIND question, not a numeric one, so it is asserted over every kind an appended
    /// optional field may declare. A tag list and an opaque bytes field carry BYTES, and their zero form is an
    /// empty payload rather than a zero number, which a predicate written for varints alone would miss.
    /// </summary>
    [Theory]
    [InlineData(ContentFieldKind.Int)]
    [InlineData(ContentFieldKind.ScaledInt)]
    [InlineData(ContentFieldKind.Bool)]
    [InlineData(ContentFieldKind.KeyReference)]
    [InlineData(ContentFieldKind.TagList)]
    [InlineData(ContentFieldKind.OpaqueBytes)]
    [InlineData(ContentFieldKind.LocalizedTextKey)]
    public void EveryKindsZeroFormEncodesToNothingAtTheTail(ContentFieldKind kind)
    {
        var schema = new ContentFieldSchema(
            [
                new ContentFieldEntry("value", ContentFieldKind.Int, null, ContentVisibility.Client, true),
                new ContentFieldEntry(
                    "appended", kind, ReferenceTargetFor(kind), ContentVisibility.Client, false),
            ],
            baselineFieldCount: 1);
        var codec = new BareCodec(new ContentTypeId(2048), schema);

        // LocalizedTextKey is the one row here that compares absent with absent, and it has to: OfNumber and
        // OfBytes both refuse the marker kind, so its zero form IS absent and there is no second value to
        // build. Its long form refusal below therefore lands through the trailing byte check rather than
        // through CarriesRedundantTail, since a marker writes no bytes and can never be a redundant tail.
        byte[] absent = Encode(codec, Bare(schema, ContentFieldValue.Absent(kind)));
        byte[] zero = Encode(codec, Bare(schema, ZeroFormOf(kind)));

        Assert.Equal(absent, zero);
        Assert.True(codec.TryDecode(zero, out ContentRow? read, out string? reason), reason);
        Assert.True(read.Fields[1].IsAbsent);

        // And the long form of that same field is refused, whatever the kind writes as its zero byte.
        Assert.False(codec.TryDecode([.. absent, (byte)0], out _, out reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
    }

    /// <summary>
    /// The shape the rule is written for and that no engine type has reached yet: TWO appended fields, from
    /// two separate releases. Everything above exercises a tail of one, where "the last appended field that
    /// carries a value" and "the only appended field" are the same thing and a scan that stopped at the first
    /// field would pass.
    /// <para>
    /// The four bodies are the whole rule. <c>[base]</c> is the first release. <c>[base][05]</c> is the second.
    /// <c>[base][00][05]</c> is the THIRD, and it is why the tail is a scan rather than a check of the last
    /// field alone: the first appended field is the zero form and still has to be written, because a later one
    /// carries a value and the walk is positional. <c>[base][05][00]</c> is the redundant encoding of
    /// <c>[base][05]</c> and is refused.
    /// </para>
    /// </summary>
    [Fact]
    public void ASchemaWithTwoAppendedFieldsKeepsOneEncodingPerRow()
    {
        var schema = new ContentFieldSchema(
            [
                new ContentFieldEntry("value", ContentFieldKind.Int, null, ContentVisibility.Client, true),
                new ContentFieldEntry("first", ContentFieldKind.Int, null, ContentVisibility.Client, false),
                new ContentFieldEntry("second", ContentFieldKind.Int, null, ContentVisibility.Client, false),
            ],
            baselineFieldCount: 1);
        var codec = new BareCodec(new ContentTypeId(2049), schema);

        ContentFieldValue seven = ContentFieldValue.OfNumber(ContentFieldKind.Int, 7);
        ContentFieldValue five = ContentFieldValue.OfNumber(ContentFieldKind.Int, 5);
        ContentFieldValue zero = ContentFieldValue.OfNumber(ContentFieldKind.Int, 0);
        ContentFieldValue absent = ContentFieldValue.Absent(ContentFieldKind.Int);

        byte[] baseline = Encode(codec, Three(seven, absent, absent));
        Assert.Equal(baseline, Encode(codec, Three(seven, zero, zero)));

        // An explicit zero in the LAST appended field is dropped and the one before it is kept, which is the
        // scan doing its job rather than a check of one field.
        Assert.Equal<byte[]>([.. baseline, 0x05], Encode(codec, Three(seven, five, zero)));
        Assert.Equal<byte[]>([.. baseline, 0x05], Encode(codec, Three(seven, five, absent)));

        // A zero FIRST appended field is written when a later one carries a value, because the walk is
        // positional and dropping it would shift the value that follows.
        Assert.Equal<byte[]>([.. baseline, 0x00, 0x05], Encode(codec, Three(seven, zero, five)));

        foreach (byte[] canonical in new[]
        {
            baseline,
            (byte[])[.. baseline, 0x05],
            (byte[])[.. baseline, 0x00, 0x05],
        })
        {
            Assert.True(codec.TryDecode(canonical, out ContentRow? read, out string? reason), reason);
            Assert.Equal(canonical, Encode(codec, read));
        }

        // The redundant encoding of [base][05], one field longer with the tail at its zero form.
        Assert.False(codec.TryDecode([.. baseline, 0x05, 0x00], out _, out string? refusal));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, refusal);

        // And so is the redundant encoding of the baseline itself, at either width.
        Assert.False(codec.TryDecode([.. baseline, 0x00], out _, out refusal));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, refusal);
        Assert.False(codec.TryDecode([.. baseline, 0x00, 0x00], out _, out refusal));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, refusal);
    }

    static ContentRow Three(ContentFieldValue value, ContentFieldValue first, ContentFieldValue second)
        => new(new ContentTypeId(2049), 1, new ContentKey("bare"), 0, false, [value, first, second]);

    static string? ReferenceTargetFor(ContentFieldKind kind) => kind switch
    {
        ContentFieldKind.KeyReference => "some_type",
        ContentFieldKind.TagList => ContentFieldEntry.TagReferenceTarget,
        _ => null,
    };

    /// <summary>A value that is NOT absent and still encodes to the zero byte, per kind.</summary>
    static ContentFieldValue ZeroFormOf(ContentFieldKind kind) => kind switch
    {
        ContentFieldKind.TagList or ContentFieldKind.OpaqueBytes
            => ContentFieldValue.OfBytes(kind, Array.Empty<byte>()),
        ContentFieldKind.LocalizedTextKey => ContentFieldValue.Absent(kind),
        _ => ContentFieldValue.OfNumber(kind, 0),
    };

    static ContentRow Bare(ContentFieldSchema schema, ContentFieldValue appended)
        => new(
            new ContentTypeId(2048),
            1,
            new ContentKey("bare"),
            0,
            false,
            [ContentFieldValue.OfNumber(ContentFieldKind.Int, 7), appended]);

    static byte[] Encode(IContentRowCodec codec, ContentRow row)
    {
        var buffer = new ArrayBufferWriter<byte>();
        codec.Encode(row, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>The generic walk with nothing added, over a schema this suite builds by hand.</summary>
    sealed class BareCodec(ContentTypeId type, ContentFieldSchema schema) : ContentRowCodecBase(type, schema);

    /// <summary>The same row with every APPENDED field absent, which is what the type's first release wrote.</summary>
    static ContentRow WithAppendedAbsent(ContentTypeRegistration registration, ContentRow row)
    {
        var values = new ContentFieldValue[row.Fields.Count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = i < registration.Schema.BaselineFieldCount
                ? row.Fields[i]
                : ContentFieldValue.Absent(registration.Schema.Fields[i].Kind);
        }

        return new ContentRow(row.Type, row.Id, row.Key, row.ParentId, row.IsRetired, values);
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
