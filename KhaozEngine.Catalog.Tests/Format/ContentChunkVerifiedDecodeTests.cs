using System;
using System.Buffers.Binary;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Format;

/// <summary>
/// <c>TryDecodeVerified</c>, the load path's one pass: verify and decode over a SINGLE decompression, where
/// calling <c>TryVerify</c> and then <c>TryDecode</c> decompresses the same bytes twice.
/// <para>
/// <b>Hash before trust is the property these tests exist to hold.</b> The saving is only legitimate while
/// the digest is compared BEFORE the row table is walked, so no row can escape a buffer nothing signed. The
/// header refusals are the full <c>CheckRange</c> set rather than <c>TryVerify</c>'s lengths alone, so the
/// row-count guard that bounds the four parallel arrays is still taken from the header before the body is
/// sized.
/// </para>
/// </summary>
public class ContentChunkVerifiedDecodeTests
{
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

    static EncodedContentChunk TwoRowChunk(ContentTypeRegistration tag)
    {
        var assembler = new ContentChunkAssembler();
        assembler.Add(TagRow(tag, 1, "metal", 10), tag.Codec);
        assembler.Add(TagRow(tag, 2, "two_handed", 20, retired: true), tag.Codec);
        return ContentChunkCodec.Encode(
            tag, 0, ContentVisibility.Client, assembler.Build(), ContentPackFormat.BrotliQuality);
    }

    static byte[] PatchUInt32(ReadOnlySpan<byte> file, int offset, uint value)
    {
        byte[] copy = file.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }

    [Fact]
    public void AHashMismatchRefusesAndHandsBackNoChunk()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = TwoRowChunk(Tag(registry));

        Assert.False(ContentChunkCodec.TryDecodeVerified(
            encoded.StoredFile.Span,
            registry,
            new string('0', 64),
            out ContentChunk? chunk,
            out string? reason));

        Assert.Null(chunk);
        Assert.Equal(ContentChunkCodec.ReasonHashMismatch, reason);
    }

    [Fact]
    public void AGoodChunkDecodesToTheSameRowsAPlainDecodeGives()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = TwoRowChunk(Tag(registry));

        Assert.True(ContentChunkCodec.TryDecodeVerified(
            encoded.StoredFile.Span, registry, encoded.Hash, out ContentChunk? verified, out string? reason));
        Assert.Null(reason);
        Assert.NotNull(verified);

        Assert.True(ContentChunkCodec.TryDecode(encoded.StoredFile.Span, registry, out ContentChunk? plain, out _));
        Assert.NotNull(plain);

        Assert.Equal(plain.Type, verified.Type);
        Assert.Equal(plain.ChunkIndex, verified.ChunkIndex);
        Assert.Equal(plain.SlotBase, verified.SlotBase);
        Assert.Equal(plain.SlotCount, verified.SlotCount);
        Assert.Equal(plain.Visibility, verified.Visibility);
        Assert.Equal(plain.RowCount, verified.RowCount);
        Assert.True(plain.Body.SequenceEqual(verified.Body));
        for (int i = 0; i < plain.RowCount; i++)
        {
            Assert.Equal(plain.DefinitionIdAt(i), verified.DefinitionIdAt(i));
            Assert.Equal(plain.IsRetiredAt(i), verified.IsRetiredAt(i));
            Assert.True(plain.RowBodyAt(i).SequenceEqual(verified.RowBodyAt(i)));
        }
    }

    [Fact]
    public void ASlotCountThatDisagreesWithTheRegistryStillRefuses()
    {
        // TryVerify ran CheckLengths alone, so the whole range half would have been lost by folding the two
        // calls into one. It is taken here from the header, before the body is sized.
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = TwoRowChunk(Tag(registry));
        byte[] file = PatchUInt32(encoded.StoredFile.Span, 16, 1024);

        Assert.False(ContentChunkCodec.TryDecodeVerified(
            file, registry, encoded.Hash, out ContentChunk? chunk, out string? reason));

        Assert.Null(chunk);
        Assert.Equal(ContentChunkCodec.ReasonRangeMismatch, reason);
    }

    [Fact]
    public void ARowCountTheBodyCannotHoldStillRefusesFromTheHeader()
    {
        ContentTypeRegistry registry = Registry();
        EncodedContentChunk encoded = TwoRowChunk(Tag(registry));
        byte[] file = PatchUInt32(encoded.StoredFile.Span, 20, 1_000_000);

        Assert.False(ContentChunkCodec.TryDecodeVerified(
            file, registry, encoded.Hash, out ContentChunk? chunk, out string? reason));

        Assert.Null(chunk);
        Assert.Equal(ContentChunkCodec.ReasonRowTable, reason);
    }
}
