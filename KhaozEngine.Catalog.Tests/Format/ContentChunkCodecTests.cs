using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Format;

/// <summary>
/// The <c>KECC</c> chunk file of spec 7.2 and 7.3: a 36 byte never-compressed header, a row table of
/// strictly ascending ids, and the row bodies in the same order.
/// <para>
/// The two properties worth more than the round trip are pinned here. The canonical bytes the hash is taken
/// over are the UNCOMPRESSED ones with <c>compression</c> forced to 0 and <c>storedBytes</c> forced equal to
/// <c>uncompressedBytes</c>, so a compressor change is a no-op for every cached client. And the two size
/// refusals are taken from the 36 header bytes BEFORE any allocation, because the hash is over the
/// uncompressed bytes and so cannot bound the decompression it sits behind.
/// </para>
/// </summary>
public class ContentChunkCodecTests
{
    /// <summary>Spec 7.9's worked chunk, 36 header bytes then the 25 body bytes, as the hash sees them.</summary>
    static readonly byte[] SpecSevenNineCanonical =
    [
        0x4B, 0x45, 0x43, 0x43,   // magic KECC
        0x01, 0x00,               // formatVersion 1
        0x01, 0x00,               // typeId 1, tag
        0x00, 0x00, 0x00, 0x00,   // chunkIndex 0
        0x00, 0x00, 0x00, 0x00,   // slotBase 0
        0x00, 0x10, 0x00, 0x00,   // slotCount 4096
        0x02, 0x00, 0x00, 0x00,   // rowCount 2
        0x00,                     // visibility Client
        0x00,                     // compression none
        0x00, 0x00,               // reserved
        0x19, 0x00, 0x00, 0x00,   // uncompressedBytes 25
        0x19, 0x00, 0x00, 0x00,   // storedBytes 25
        0x01, 0x00, 0x07,         // row table: id 1, live, 7 bytes
        0x02, 0x01, 0x0C,         // row table: id 2, retired, 12 bytes
        0x05, 0x6D, 0x65, 0x74, 0x61, 0x6C, 0x0A,                           // "metal", sort 10
        0x0A, 0x74, 0x77, 0x6F, 0x5F, 0x68, 0x61, 0x6E, 0x64, 0x65, 0x64, 0x14,   // "two_handed", sort 20
    ];

    /// <summary>
    /// <c>SHA256(utf8("kec/chunk/1\n") || the 61 canonical bytes)</c>, computed outside this tree so the
    /// assertion pins the spec's arithmetic rather than the implementation's own output.
    /// </summary>
    const string SpecSevenNineHash = "30e4bc839a66a488deea911fb794193c4ae35b0be4115f3edbd9e27500b5a2a1";

    static ContentTypeRegistry Registry()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        return registry;
    }

    static ContentTypeRegistration Tag(ContentTypeRegistry registry)
    {
        Assert.True(registry.TryGetByKey(EngineContentTypes.TagTypeKey, out ContentTypeRegistration? tag));
        return tag;
    }

    static ContentRow TagRow(ContentTypeRegistration tag, int id, string key, int sort, bool retired = false)
        => new(
            tag.Type,
            id,
            new ContentKey(key),
            0,
            retired,
            [
                ContentFieldValue.Absent(ContentFieldKind.LocalizedTextKey),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, sort),
            ]);

    static EncodedContentChunk EncodeTagChunk(
        ContentTypeRegistration tag,
        IEnumerable<ContentRow> rows,
        int chunkIndex = 0,
        int quality = ContentPackFormat.BrotliQuality)
    {
        var assembler = new ContentChunkAssembler();
        foreach (ContentRow row in rows)
        {
            assembler.Add(row, tag.Codec);
        }

        return ContentChunkCodec.Encode(
            tag, chunkIndex, ContentVisibility.Client, assembler.Build(), quality);
    }

    /// <summary>The two rows of spec 7.9, encoded through the tag codec rather than typed in.</summary>
    static EncodedContentChunk SpecSevenNineChunk(ContentTypeRegistration tag) => EncodeTagChunk(
        tag,
        [TagRow(tag, 1, "metal", 10), TagRow(tag, 2, "two_handed", 20, retired: true)]);

    static byte[] Patch(ReadOnlySpan<byte> file, int offset, params byte[] replacement)
    {
        byte[] copy = file.ToArray();
        replacement.CopyTo(copy.AsSpan(offset));
        return copy;
    }

    static byte[] PatchUInt32(ReadOnlySpan<byte> file, int offset, uint value)
    {
        byte[] copy = file.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }

    /// <summary>Wraps a hand-built body in a valid tag-chunk header, so a body test patches nothing else.</summary>
    static byte[] BodyInAHeader(byte[] body, uint rowCount)
    {
        byte[] file = new byte[ContentPackFormat.ChunkHeaderBytes + body.Length];
        ContentPackFormat.ChunkMagic.CopyTo(file);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), ContentPackFormat.ChunkFormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(6), EngineContentTypes.TagTypeId);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), TagContentType.DefaultChunkSlots);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), rowCount);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32), (uint)body.Length);
        body.CopyTo(file.AsSpan(ContentPackFormat.ChunkHeaderBytes));
        return file;
    }

    static string Refuses(byte[] file)
    {
        ContentTypeRegistry registry = Registry();
        Assert.False(ContentChunkCodec.TryDecode(file, registry, out ContentChunk? chunk, out string? reason));
        Assert.Null(chunk);
        Assert.NotNull(reason);
        return reason!;
    }

    [Fact]
    public void TheWorkedChunkOfSpecSevenNineEncodesToItsCanonicalBytes()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = SpecSevenNineChunk(Tag(registry));

        Assert.Equal(SpecSevenNineCanonical, encoded.Canonical.ToArray());
        Assert.Equal(SpecSevenNineHash, encoded.Hash);
        Assert.Equal(61, encoded.Canonical.Length);
        Assert.Equal(25, encoded.UncompressedBytes);

        // At 25 bytes the body does not compress smaller, so the stored file IS the canonical bytes.
        Assert.False(encoded.IsCompressed);
        Assert.Equal(SpecSevenNineCanonical, encoded.StoredFile.ToArray());
    }

    [Fact]
    public void TheWorkedChunkRoundTripsThroughTheDecoder()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);
        EncodedContentChunk encoded = SpecSevenNineChunk(tag);

        Assert.True(
            ContentChunkCodec.TryDecode(encoded.StoredFile.Span, registry, out ContentChunk? chunk, out string? reason),
            reason);
        Assert.Equal(tag.Type, chunk.Type);
        Assert.Equal(0, chunk.ChunkIndex);
        Assert.Equal(0, chunk.SlotBase);
        Assert.Equal(TagContentType.DefaultChunkSlots, chunk.SlotCount);
        Assert.Equal(ContentVisibility.Client, chunk.Visibility);
        Assert.Equal(2, chunk.RowCount);
        Assert.Equal(new[] { 1, 2 }, chunk.Ids.ToArray());
        Assert.Equal(new[] { 7, 12 }, chunk.Lengths.ToArray());
        Assert.Equal(new byte[] { 0x05, 0x6D, 0x65, 0x74, 0x61, 0x6C, 0x0A }, chunk.RowBodyAt(0).ToArray());
    }

    [Fact]
    public void ARowDecodeRebuildsTheIdAndTheRetiredBitFromTheTable()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);
        EncodedContentChunk encoded = SpecSevenNineChunk(tag);
        Assert.True(ContentChunkCodec.TryDecode(encoded.StoredFile.Span, registry, out ContentChunk? chunk, out _));

        Assert.True(chunk.TryDecodeRow(2, tag.Codec, out ContentRow? row, out string? reason), reason);

        // The body carries the key and the fields only, so these two come from the row TABLE.
        Assert.Equal(2, row.Id);
        Assert.True(row.IsRetired);
        Assert.Equal("two_handed", row.Key.ToString());
        Assert.Equal(20, row.Fields[1].Number);
    }

    [Fact]
    public void TheRetiredBitIsAnsweredFromTheTableWithNoRowDecode()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = SpecSevenNineChunk(Tag(registry));
        Assert.True(ContentChunkCodec.TryDecode(encoded.StoredFile.Span, registry, out ContentChunk? chunk, out _));

        Assert.False(chunk.IsRetired(1));
        Assert.True(chunk.IsRetired(2));
        Assert.False(chunk.IsRetired(3));
        Assert.True(chunk.TryGetIndex(2, out int index));
        Assert.Equal(1, index);
        Assert.False(chunk.TryGetIndex(3, out _));
    }

    [Fact]
    public void TheCanonicalFormIsIndependentOfTheCompressor()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);
        List<ContentRow> rows = Enumerable
            .Range(1, 400)
            .Select(i => TagRow(tag, i, FormattableString.Invariant($"tag_{i:0000}"), i))
            .ToList();

        EncodedContentChunk cheap = EncodeTagChunk(tag, rows, quality: 1);
        EncodedContentChunk dear = EncodeTagChunk(tag, rows, quality: 11);

        Assert.True(cheap.IsCompressed);
        Assert.True(dear.IsCompressed);
        Assert.False(cheap.StoredFile.Span.SequenceEqual(dear.StoredFile.Span));
        Assert.Equal(cheap.Hash, dear.Hash);
        Assert.Equal(cheap.Canonical.ToArray(), dear.Canonical.ToArray());

        // And both stored files decode to the same rows, which is what makes the swap a no-op.
        Assert.True(ContentChunkCodec.TryDecode(cheap.StoredFile.Span, registry, out ContentChunk? a, out _));
        Assert.True(ContentChunkCodec.TryDecode(dear.StoredFile.Span, registry, out ContentChunk? b, out _));
        Assert.Equal(a.Body.ToArray(), b.Body.ToArray());
        Assert.True(ContentChunkCodec.TryVerify(dear.StoredFile.Span, cheap.Hash, out string? verifyReason), verifyReason);
    }

    [Fact]
    public void ACompressedChunkIsSmallerThanItsCanonicalBytes()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);
        EncodedContentChunk encoded = EncodeTagChunk(
            tag,
            Enumerable.Range(1, 400).Select(i => TagRow(tag, i, FormattableString.Invariant($"tag_{i:0000}"), i)));

        Assert.True(encoded.IsCompressed);
        Assert.True(encoded.StoredBytes < encoded.UncompressedBytes);
        Assert.True(ContentChunkCodec.TryDecode(encoded.StoredFile.Span, registry, out ContentChunk? chunk, out _));
        Assert.Equal(400, chunk.RowCount);
        Assert.True(chunk.TryDecodeRow(400, tag.Codec, out ContentRow? row, out _));
        Assert.Equal("tag_0400", row.Key.ToString());
    }

    [Fact]
    public void TheHashVerificationRefusesABodyThatWasSwapped()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);
        EncodedContentChunk first = SpecSevenNineChunk(tag);
        EncodedContentChunk second = EncodeTagChunk(tag, [TagRow(tag, 1, "metal", 11)]);

        Assert.False(ContentChunkCodec.TryVerify(second.StoredFile.Span, first.Hash, out string? reason));
        Assert.Equal(ContentChunkCodec.ReasonHashMismatch, reason);
    }

    [Fact]
    public void ABadMagicIsRefused()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = SpecSevenNineChunk(Tag(registry));

        Assert.Equal(
            ContentChunkCodec.ReasonMagic,
            Refuses(Patch(encoded.StoredFile.Span, 0, 0x4B, 0x45, 0x43, 0x4D)));
    }

    [Fact]
    public void AHeaderShorterThanThirtySixBytesIsRefused()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = SpecSevenNineChunk(Tag(registry));

        Assert.Equal(
            ContentChunkCodec.ReasonTruncatedHeader,
            Refuses(encoded.StoredFile.Span[..35].ToArray()));
    }

    [Fact]
    public void AFormatVersionOtherThanOneIsRefused()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = SpecSevenNineChunk(Tag(registry));

        Assert.Equal(
            ContentChunkCodec.ReasonFormatVersion,
            Refuses(Patch(encoded.StoredFile.Span, 4, 0x02, 0x00)));
    }

    [Fact]
    public void ANonZeroReservedIsRefused()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = SpecSevenNineChunk(Tag(registry));

        Assert.Equal(
            ContentChunkCodec.ReasonReservedSet,
            Refuses(Patch(encoded.StoredFile.Span, 26, 0x01, 0x00)));
    }

    [Fact]
    public void AVisibilityByteOutsideTheVocabularyIsRefused()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = SpecSevenNineChunk(Tag(registry));

        Assert.Equal(
            ContentChunkCodec.ReasonVisibility,
            Refuses(Patch(encoded.StoredFile.Span, 24, 0x02)));
    }

    [Fact]
    public void ACompressionByteOutsideTheVocabularyIsRefused()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = SpecSevenNineChunk(Tag(registry));

        Assert.Equal(
            ContentChunkCodec.ReasonCompression,
            Refuses(Patch(encoded.StoredFile.Span, 25, 0x02)));
    }

    [Fact]
    public void ASlotBaseThatIsNotTheIndexTimesTheSlotCountIsRefused()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = SpecSevenNineChunk(Tag(registry));

        Assert.Equal(
            ContentChunkCodec.ReasonRangeMismatch,
            Refuses(PatchUInt32(encoded.StoredFile.Span, 12, 4096)));
    }

    [Fact]
    public void ASlotCountTheRegistryDisagreesWithIsRefused()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = SpecSevenNineChunk(Tag(registry));

        // 1,024 is a legal slot count on its own terms, so only the registry can call this one wrong.
        Assert.Equal(
            ContentChunkCodec.ReasonRangeMismatch,
            Refuses(PatchUInt32(encoded.StoredFile.Span, 16, 1024)));
    }

    [Fact]
    public void ASlotCountOutsideTheLegalRangeIsRefusedWithoutARegistry()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = SpecSevenNineChunk(Tag(registry));
        byte[] file = PatchUInt32(encoded.StoredFile.Span, 16, 4000);

        Assert.False(ContentChunkCodec.TryDecode(file, null, out ContentChunk? chunk, out string? reason));
        Assert.Null(chunk);
        Assert.Equal(ContentChunkCodec.ReasonRangeMismatch, reason);
    }

    [Fact]
    public void ARowIdOutsideTheChunksOwnRangeIsRefused()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);
        EncodedContentChunk encoded = EncodeTagChunk(tag, [TagRow(tag, 4097, "metal", 10)], chunkIndex: 1);

        // Chunk 1 covers ids 4,096 to 8,191. Moving it to index 0 leaves the row outside its own range.
        byte[] file = PatchUInt32(PatchUInt32(encoded.StoredFile.Span, 8, 0), 12, 0);
        Assert.Equal(ContentChunkCodec.ReasonRangeMismatch, Refuses(file));
    }

    [Fact]
    public void ADeclaredUncompressedLengthOverTheCeilingIsRefused()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = SpecSevenNineChunk(Tag(registry));

        Assert.Equal(
            ContentChunkCodec.ReasonTooLarge,
            Refuses(PatchUInt32(encoded.StoredFile.Span, 28, uint.MaxValue)));
    }

    [Fact]
    public void TheSizeRefusalCostsNoAllocation()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = SpecSevenNineChunk(Tag(registry));
        byte[] file = PatchUInt32(encoded.StoredFile.Span, 28, uint.MaxValue);

        // Warm the path so the measurement is the refusal rather than the first-call machinery.
        for (int i = 0; i < 8; i++)
        {
            ContentChunkCodec.TryDecode(file, registry, out _, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool decoded = ContentChunkCodec.TryDecode(file, registry, out ContentChunk? chunk, out string? reason);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.False(decoded);
        Assert.Null(chunk);
        Assert.Equal(ContentChunkCodec.ReasonTooLarge, reason);
        Assert.True(allocated <= 256, FormattableString.Invariant($"the refusal allocated {allocated} bytes"));
    }

    [Fact]
    public void AStoredLengthThatDisagreesWithTheBodyReceivedIsRefused()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = SpecSevenNineChunk(Tag(registry));

        Assert.Equal(
            ContentChunkCodec.ReasonStoredLength,
            Refuses(PatchUInt32(encoded.StoredFile.Span, 32, 24)));

        // A short delivery is the same refusal from the other side.
        Assert.Equal(
            ContentChunkCodec.ReasonStoredLength,
            Refuses(encoded.StoredFile.Span[..^1].ToArray()));
    }

    [Fact]
    public void AnUncompressedBodyWhoseTwoLengthsDisagreeIsRefused()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = SpecSevenNineChunk(Tag(registry));

        Assert.Equal(
            ContentChunkCodec.ReasonStoredLength,
            Refuses(PatchUInt32(encoded.StoredFile.Span, 28, 24)));
    }

    [Fact]
    public void ABrotliStreamThatExpandsPastItsDeclaredLengthIsRefused()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);
        EncodedContentChunk encoded = EncodeTagChunk(
            tag,
            Enumerable.Range(1, 400).Select(i => TagRow(tag, i, FormattableString.Invariant($"tag_{i:0000}"), i)));
        Assert.True(encoded.IsCompressed);

        // The stream still decompresses, it just claims one byte less room than it needs.
        byte[] file = PatchUInt32(encoded.StoredFile.Span, 28, (uint)encoded.UncompressedBytes - 1);
        Assert.Equal(ContentChunkCodec.ReasonTooLarge, Refuses(file));
    }

    [Fact]
    public void ATruncatedBrotliStreamIsRefused()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);
        EncodedContentChunk encoded = EncodeTagChunk(
            tag,
            Enumerable.Range(1, 400).Select(i => TagRow(tag, i, FormattableString.Invariant($"tag_{i:0000}"), i)));
        Assert.True(encoded.IsCompressed);

        // Cut the stream's tail and tell the truth about the delivered length, so the only thing wrong is
        // that the compressed body cannot produce the bytes the header still claims.
        byte[] file = encoded.StoredFile.Span[..^10].ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            file.AsSpan(32), (uint)(file.Length - ContentPackFormat.ChunkHeaderBytes));

        Assert.Equal(ContentChunkCodec.ReasonDecompress, Refuses(file));
    }

    [Fact]
    public void ARepeatedDefinitionIdIsRefused()
    {
        byte[] body = [0x01, 0x00, 0x01, 0x01, 0x00, 0x01, 0x00, 0x00];

        Assert.Equal(ContentChunkCodec.ReasonRowDuplicate, Refuses(BodyInAHeader(body, 2)));
    }

    [Fact]
    public void ANonAscendingDefinitionIdIsRefused()
    {
        byte[] body = [0x02, 0x00, 0x01, 0x01, 0x00, 0x01, 0x00, 0x00];

        Assert.Equal(ContentChunkCodec.ReasonRowOrder, Refuses(BodyInAHeader(body, 2)));
    }

    [Fact]
    public void ARowFlagsByteWithAReservedBitSetIsRefused()
    {
        for (int bit = 1; bit < 8; bit++)
        {
            byte[] body = [0x01, (byte)(1 << bit), 0x01, 0x00];

            Assert.Equal(ContentChunkCodec.ReasonRowFlags, Refuses(BodyInAHeader(body, 1)));
        }
    }

    [Fact]
    public void ARowCountTheBodyCannotHoldIsRefused()
    {
        byte[] body = [0x01, 0x00, 0x01, 0x00];

        Assert.Equal(ContentChunkCodec.ReasonRowTable, Refuses(BodyInAHeader(body, 1000)));
    }

    [Fact]
    public void ARowTableThatRunsOffTheBodyIsRefused()
    {
        byte[] body = [0x01, 0x00, 0x80];

        Assert.Equal(ContentVarint.ReasonTruncated, Refuses(BodyInAHeader(body, 1)));
    }

    [Fact]
    public void ANonMinimalVarintInTheRowTableKeepsTheVarintReadersOwnReason()
    {
        byte[] body = [0x81, 0x00, 0x00, 0x01, 0x00];

        Assert.Equal(ContentVarint.ReasonNotMinimal, Refuses(BodyInAHeader(body, 1)));
    }

    [Fact]
    public void ARowBodyRunningPastTheEndOfTheChunkIsRefused()
    {
        byte[] body = [0x01, 0x00, 0x20, 0x00];

        Assert.Equal(ContentChunkCodec.ReasonRowOverflow, Refuses(BodyInAHeader(body, 1)));
    }

    [Fact]
    public void RowsThatDoNotFillTheBodyAreRefused()
    {
        byte[] body = [0x01, 0x00, 0x01, 0x00, 0x00, 0x00];

        Assert.Equal(ContentChunkCodec.ReasonTrailingBytes, Refuses(BodyInAHeader(body, 1)));
    }

    [Fact]
    public void AnEmptyChunkIsLegalAndRoundTrips()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);
        EncodedContentChunk encoded = EncodeTagChunk(tag, []);

        Assert.Equal(0, encoded.UncompressedBytes);
        Assert.True(ContentChunkCodec.TryDecode(encoded.StoredFile.Span, registry, out ContentChunk? chunk, out _));
        Assert.Equal(0, chunk.RowCount);
        Assert.False(chunk.TryGetIndex(1, out _));
    }

    [Fact]
    public void EncodingRowsOutOfOrderThrows()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);

        Assert.Throws<ArgumentException>(() => EncodeTagChunk(
            tag, [TagRow(tag, 2, "two_handed", 20), TagRow(tag, 1, "metal", 10)]));
        Assert.Throws<ArgumentException>(() => EncodeTagChunk(
            tag, [TagRow(tag, 1, "metal", 10), TagRow(tag, 1, "metal", 10)]));
    }

    [Fact]
    public void EncodingARowOutsideTheChunksRangeThrows()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);

        Assert.Throws<ArgumentOutOfRangeException>(() => EncodeTagChunk(tag, [TagRow(tag, 4096, "metal", 10)]));
    }

    [Fact]
    public void TheAssemblerHoldsEveryRowInOneArena()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);
        var assembler = new ContentChunkAssembler();
        for (int i = 1; i <= 2000; i++)
        {
            assembler.Add(TagRow(tag, i, FormattableString.Invariant($"tag_{i:0000}"), i), tag.Codec);
        }

        IReadOnlyList<ContentChunkRow> rows = assembler.Build();

        Assert.Equal(2000, assembler.RowCount);
        Assert.Equal(2000, rows.Count);
        Assert.Equal(11, assembler.LargestRowBytes);
        Assert.Equal(1, rows[0].DefinitionId);
        Assert.Equal(2000, rows[^1].DefinitionId);

        assembler.Reset();
        Assert.Equal(0, assembler.RowCount);
        Assert.Equal(0, assembler.LargestRowBytes);
    }

    [Fact]
    public void TheAssemblerCarriesTheRetiredBitOntoTheRowTable()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);
        var assembler = new ContentChunkAssembler();
        assembler.Add(TagRow(tag, 1, "metal", 10), tag.Codec);
        assembler.Add(TagRow(tag, 2, "two_handed", 20, retired: true), tag.Codec);

        IReadOnlyList<ContentChunkRow> rows = assembler.Build();

        Assert.False(rows[0].IsRetired);
        Assert.True(rows[1].IsRetired);
        Assert.Equal(7, rows[0].Body.Length);
        Assert.Equal(12, rows[1].Body.Length);
    }

    [Fact]
    public void AServerOnlyChunkCarriesItsVisibilityThroughTheHeader()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);
        var assembler = new ContentChunkAssembler();
        assembler.Add(TagRow(tag, 1, "metal", 10), tag.Codec);
        EncodedContentChunk encoded = ContentChunkCodec.Encode(
            tag, 0, ContentVisibility.ServerOnly, assembler.Build());

        Assert.Equal(ContentPackFormat.VisibilityServerOnly, encoded.Canonical.Span[24]);
        Assert.True(ContentChunkCodec.TryDecode(encoded.StoredFile.Span, registry, out ContentChunk? chunk, out _));
        Assert.Equal(ContentVisibility.ServerOnly, chunk.Visibility);

        // The client chunk of the same rows is DIFFERENT bytes and a different address (spec 6.7).
        EncodedContentChunk client = EncodeTagChunk(tag, [TagRow(tag, 1, "metal", 10)]);
        Assert.NotEqual(client.Hash, encoded.Hash);
    }

    [Fact]
    public void TheHeaderIsReadableWithoutTouchingTheCompressor()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);
        EncodedContentChunk encoded = EncodeTagChunk(
            tag,
            Enumerable.Range(1, 400).Select(i => TagRow(tag, i, FormattableString.Invariant($"tag_{i:0000}"), i)));

        Assert.True(
            ContentChunkCodec.TryReadHeader(encoded.StoredFile.Span, out ContentChunkHeader header, out string? reason),
            reason);
        Assert.Equal(EngineContentTypes.TagTypeId, header.Type.Value);
        Assert.Equal(400u, header.RowCount);
        Assert.Equal(ContentPackFormat.CompressionBrotli, header.Compression);
        Assert.Equal((uint)encoded.UncompressedBytes, header.UncompressedBytes);
        Assert.Equal((uint)(encoded.StoredBytes - ContentPackFormat.ChunkHeaderBytes), header.StoredBytes);
    }

    [Fact]
    public void ABrotliBodyIsCompressedAtTheDeclaredQualityAndWindow()
    {
        ContentTypeRegistry registry = Registry();
        ContentTypeRegistration tag = Tag(registry);
        EncodedContentChunk encoded = EncodeTagChunk(
            tag,
            Enumerable.Range(1, 400).Select(i => TagRow(tag, i, FormattableString.Invariant($"tag_{i:0000}"), i)));

        byte[] expected = new byte[BrotliEncoder.GetMaxCompressedLength(encoded.UncompressedBytes)];
        Assert.True(BrotliEncoder.TryCompress(
            encoded.Canonical.Span[ContentPackFormat.ChunkHeaderBytes..],
            expected,
            out int written,
            ContentPackFormat.BrotliQuality,
            ContentPackFormat.BrotliWindow));

        Assert.Equal(
            expected.AsSpan(0, written).ToArray(),
            encoded.StoredFile.Span[ContentPackFormat.ChunkHeaderBytes..].ToArray());
    }
}
