using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// <see cref="ItemRow"/> is a SECOND decoder for the <c>item</c> type, a hand written fixed walk that exists
/// because the hot paths cannot afford a field-by-name read. Two decoders for one format is a standing risk,
/// and the risk is concentrated in the TAIL: the generic walk is driven by the schema and moves with it, and
/// this one is written out by hand and does not.
/// <para>
/// So this suite is the tie. It holds the hand walk to the registered codec over every shape a tail can take,
/// and it asserts the item schema still carries exactly one appended field. Appending a second one turns
/// <see cref="TheItemSchemaStillCarriesExactlyOneAppendedField"/> red, which is the intended way for the next
/// contributor to be told that <see cref="ItemRow"/> has to move with the schema.
/// </para>
/// </summary>
public sealed class ItemRowTailAgreementTests
{
    /// <summary>
    /// The bytes a tail is built from: the zero form, an ordinary one byte value, a continuation byte with
    /// nothing after it, and the largest one byte varint. Two of them in sequence reaches a multi-byte varint,
    /// a non-minimal encoding, a trailing byte after a complete field and a doubled zero form.
    /// </summary>
    static readonly byte[] TailAlphabet = [0x00, 0x01, 0x80, 0x7F];

    /// <summary>Every tail of length 0, 1 and 2 over <see cref="TailAlphabet"/>.</summary>
    public static TheoryData<string> Tails
    {
        get
        {
            var data = new TheoryData<string> { string.Empty };
            foreach (byte first in TailAlphabet)
            {
                data.Add(Convert.ToHexStringLower([first]));
                foreach (byte second in TailAlphabet)
                {
                    data.Add(Convert.ToHexStringLower([first, second]));
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Tails))]
    public void TheHandWalkAndTheRegisteredCodecAgreeOnEveryTail(string tailHex)
    {
        ContentTypeRegistry registry = CatalogSnapshotFixtures.Registry();
        Assert.True(registry.TryGetByKey(EngineContentTypes.ItemTypeKey, out ContentTypeRegistration? item));

        byte[] body = [.. BaselineBody(registry), .. Convert.FromHexString(tailHex)];
        string where = "tail '" + tailHex + "'";

        bool byCodec = item.Codec.TryDecode(body, out _, out string? reason);
        bool byHand = ItemRow.TryDecode(body, 0, body.Length, false, out _);

        Assert.True(byCodec == byHand, where + " codec " + byCodec + " reason " + (reason ?? "none"));
    }

    /// <summary>
    /// The three tails worth naming, so a failure above is readable: nothing at all is the canonical short
    /// form, a non zero varint is the long form, and the ZERO form is the redundant encoding both decoders
    /// refuse because the encoder omits it.
    /// </summary>
    [Fact]
    public void TheThreeNamedTailsReadTheWayTheRuleSaysTheyDo()
    {
        ContentTypeRegistry registry = CatalogSnapshotFixtures.Registry();
        Assert.True(registry.TryGetByKey(EngineContentTypes.ItemTypeKey, out ContentTypeRegistration? item));
        byte[] baseline = BaselineBody(registry);

        Assert.True(item.Codec.TryDecode(baseline, out ContentRow? shortForm, out string? reason), reason);
        Assert.True(shortForm.Fields[^1].IsAbsent);
        Assert.True(ItemRow.TryDecode(baseline, 0, baseline.Length, false, out _));

        byte[] withCategory = [.. baseline, 0x04];
        Assert.True(item.Codec.TryDecode(withCategory, out ContentRow? longForm, out reason), reason);
        Assert.Equal(4L, longForm.Fields[^1].Number);
        Assert.True(ItemRow.TryDecode(withCategory, 0, withCategory.Length, false, out _));

        byte[] redundant = [.. baseline, 0x00];
        Assert.False(item.Codec.TryDecode(redundant, out _, out reason));
        Assert.Equal(ContentRowCodecBase.ReasonFieldMalformed, reason);
        Assert.False(ItemRow.TryDecode(redundant, 0, redundant.Length, false, out _));
    }

    /// <summary>
    /// <b>If this is red, you appended a field to the item schema and <see cref="ItemRow"/> did not move.</b>
    /// The hand written walk reads exactly <see cref="ItemContentType.BaselineFieldCount"/> fields and then
    /// ONE optional trailing varint, <c>category</c>. A second appended field needs a second read there, the
    /// zero-form refusal moved onto the new last field, and this number raised. The theory above will already
    /// be red too, and it will say which tail the two decoders disagreed about.
    /// </summary>
    [Fact]
    public void TheItemSchemaStillCarriesExactlyOneAppendedField()
    {
        ContentFieldSchema schema = ItemContentType.CreateSchema();

        Assert.True(
            schema.Fields.Count == ItemContentType.BaselineFieldCount + 1,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The item schema carries {schema.Fields.Count} fields over a baseline of {ItemContentType.BaselineFieldCount}. ItemRow.TryDecode hand reads the tail and only knows about one appended field, so it has to be updated in the same change that appends another."));
        Assert.Equal(ItemContentType.CategoryField, schema.Fields[^1].Name);
        Assert.Equal(ContentFieldKind.KeyReference, schema.Fields[^1].Kind);
        Assert.False(schema.Fields[^1].Required);
    }

    /// <summary>
    /// A canonical item body with no category, which is the sixteen field form and the prefix every tail above
    /// is appended to. It comes from the ENCODER rather than from bytes written out here, so it cannot drift
    /// from the format.
    /// </summary>
    static byte[] BaselineBody(ContentTypeRegistry registry)
    {
        ContentRow row = CatalogSnapshotFixtures.ItemRow(2, "iron_sword", true, 64, 900, 3);
        var buffer = new ArrayBufferWriter<byte>();
        Assert.True(registry.TryGetByKey(EngineContentTypes.ItemTypeKey, out ContentTypeRegistration? item));
        item.Codec.Encode(row, buffer);

        byte[] body = buffer.WrittenSpan.ToArray();
        Assert.Equal(ItemContentType.BaselineFieldCount, WrittenFields(item.Schema, row.Fields));
        return body;
    }

    /// <summary>How many fields the encoder wrote, counted the way the tail rule counts them.</summary>
    static int WrittenFields(ContentFieldSchema schema, IReadOnlyList<ContentFieldValue> values)
    {
        for (int i = schema.Fields.Count - 1; i >= schema.BaselineFieldCount; i--)
        {
            if (!values[i].IsZeroForm)
            {
                return i + 1;
            }
        }

        return schema.BaselineFieldCount;
    }
}
