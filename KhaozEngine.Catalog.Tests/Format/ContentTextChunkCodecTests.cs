using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Format;

/// <summary>
/// The <c>KECT</c> per-language text chunk of spec 7.6 and the ONE derivation of a localized key
/// (contracts 12.1). The format ships complete in phase 1 even though nothing reads it yet, because a
/// manifest that gains a section later is a manifest hash that changes for every already-published version.
/// <para>
/// The canonical form includes the VARIABLE header WHOLE, language tag and all, so the same strings under
/// two language tags are two different chunks with two different hashes. Two implementers guessing
/// differently about that shows up as a client that fetches a text chunk, verifies it, fails, and discards
/// it forever.
/// </para>
/// </summary>
public class ContentTextChunkCodecTests
{
    static KeyValuePair<string, string> Entry(string key, string value) => new(key, value);

    static readonly KeyValuePair<string, string>[] Twelve =
    [
        Entry(new string('a', ContentTextKey.MaxKeyLength), "the widest key the length byte may declare"),
        Entry("item.ash_bow.examine", "A bow of pale ash."),
        Entry("item.ash_bow.name", "Ash Bow"),
        Entry("item.oak_logs.name", "Oak Logs"),
        Entry("item.pine_logs.name", "Pine Logs"),
        Entry("item.stone_sword.examine", string.Empty),
        Entry("item.stone_sword.name", "Stone Sword"),
        Entry("stat.attack.display_format", "{0}"),
        Entry("stat.attack.name", "Attack"),
        Entry("tag.metal.name", "Metal"),
        Entry("tag.two_handed.name", "Two Handed"),
        Entry("zzz.last.name", "Last"),
    ];

    static void AddVarint(List<byte> bytes, uint value)
    {
        Span<byte> scratch = stackalloc byte[5];
        int written = ContentVarint.Write(scratch, value);
        for (int i = 0; i < written; i++)
        {
            bytes.Add(scratch[i]);
        }
    }

    /// <summary>A body written field by field, so a declared length can lie about what follows it.</summary>
    static byte[] RawBody(params (byte KeyLength, string Key, int ValueLength, string Value)[] entries)
    {
        var bytes = new List<byte>();
        AddVarint(bytes, (uint)entries.Length);
        foreach ((byte keyLength, string key, int valueLength, string value) in entries)
        {
            bytes.Add(keyLength);
            bytes.AddRange(Encoding.UTF8.GetBytes(key));
            AddVarint(bytes, (uint)valueLength);
            bytes.AddRange(Encoding.UTF8.GetBytes(value));
        }

        return [.. bytes];
    }

    /// <summary>A body built from raw BYTES, which is the only way to put an invalid UTF-8 sequence in one.</summary>
    static byte[] RawByteBody(params (byte[] Key, byte[] Value)[] entries)
    {
        var bytes = new List<byte>();
        AddVarint(bytes, (uint)entries.Length);
        foreach ((byte[] key, byte[] value) in entries)
        {
            bytes.Add((byte)key.Length);
            bytes.AddRange(key);
            AddVarint(bytes, (uint)value.Length);
            bytes.AddRange(value);
        }

        return [.. bytes];
    }

    /// <summary>Wraps a body in an uncompressed header at spec 7.6's offsets, without the codec.</summary>
    static byte[] Wrap(string languageTag, byte[] body)
    {
        byte[] tag = Encoding.UTF8.GetBytes(languageTag);
        int headerBytes = ContentPackFormat.TextHeaderFixedBytes + tag.Length;
        byte[] file = new byte[headerBytes + body.Length];
        "KECT"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), ContentPackFormat.TextChunkFormatVersion);
        file[6] = (byte)tag.Length;
        tag.CopyTo(file.AsSpan(7));
        file[7 + tag.Length] = ContentPackFormat.CompressionNone;
        file[8 + tag.Length] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(9 + tag.Length), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(13 + tag.Length), (uint)body.Length);
        body.CopyTo(file.AsSpan(headerBytes));
        return file;
    }

    static string? Refusal(byte[] file)
    {
        Assert.False(ContentTextChunkCodec.TryDecode(file, out ContentTextChunk? chunk, out string? reason));
        Assert.Null(chunk);
        return reason;
    }

    static List<KeyValuePair<string, string>> Read(ContentTextChunk chunk)
    {
        var entries = new List<KeyValuePair<string, string>>();
        ContentTextChunkEnumerator walker = chunk.EnumerateEntries();
        while (walker.MoveNext())
        {
            entries.Add(new KeyValuePair<string, string>(
                Encoding.UTF8.GetString(walker.Key),
                Encoding.UTF8.GetString(walker.Value)));
        }

        return entries;
    }

    // ---- The variable-length header, spec 7.6 ----

    [Fact]
    public void TheHeaderIsTheDeclaredLayoutAndIsSeventeenPlusTheTagLength()
    {
        byte[] file = ContentTextChunkCodec.Canonical("en-US", Twelve);

        Assert.Equal(17, ContentPackFormat.TextHeaderFixedBytes);
        Assert.Equal("KECT"u8.ToArray(), file[..4]);
        Assert.Equal(ContentPackFormat.TextChunkFormatVersion, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4)));
        Assert.Equal(5, file[6]);
        Assert.Equal("en-US"u8.ToArray(), file[7..12]);
        Assert.Equal(ContentPackFormat.CompressionNone, file[12]);
        Assert.Equal(0, file[13]);

        uint uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(14));
        Assert.Equal(uncompressed, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(18)));
        Assert.Equal(file.Length, 17 + 5 + (int)uncompressed);

        // A longer tag moves every field after it, which is the whole point of calling the header variable.
        byte[] longer = ContentTextChunkCodec.Canonical("zh-Hans-CN", Twelve);
        Assert.Equal(10, longer[6]);
        Assert.Equal(ContentPackFormat.CompressionNone, longer[17]);
    }

    [Fact]
    public void EveryEntrySurvivesTheRoundTrip()
    {
        Assert.True(ContentTextChunkCodec.TryDecode(
            ContentTextChunkCodec.Encode("en-US", Twelve), out ContentTextChunk? chunk, out string? reason));
        Assert.Null(reason);
        Assert.NotNull(chunk);

        Assert.Equal("en-US", chunk.LanguageTag);
        Assert.Equal(Twelve.Length, chunk.EntryCount);
        Assert.Equal(Twelve, Read(chunk));
    }

    [Fact]
    public void AnEmptyValueIsLegalAndIsNotTheSameAsAnAbsentEntry()
    {
        Assert.True(ContentTextChunkCodec.TryDecode(ContentTextChunkCodec.Encode("en-US", Twelve), out ContentTextChunk? chunk, out _));
        Assert.NotNull(chunk);

        List<KeyValuePair<string, string>> read = Read(chunk);
        Assert.Contains(read, e => e.Key == "item.stone_sword.examine" && e.Value.Length == 0);

        var without = new List<KeyValuePair<string, string>>(Twelve);
        without.RemoveAll(e => e.Key == "item.stone_sword.examine");
        Assert.Equal(Twelve.Length - 1, without.Count);
        Assert.NotEqual(ContentTextChunkCodec.Hash("en-US", Twelve), ContentTextChunkCodec.Hash("en-US", without));
    }

    [Fact]
    public void AChunkWithNoEntriesRoundTrips()
    {
        Assert.True(ContentTextChunkCodec.TryDecode(ContentTextChunkCodec.Encode("en-US", []), out ContentTextChunk? chunk, out _));
        Assert.NotNull(chunk);
        Assert.Equal(0, chunk.EntryCount);
        Assert.Empty(Read(chunk));
    }

    // ---- The body refusals, spec 7.6 ----

    [Fact]
    public void EntriesOutOfOrdinalKeyOrderRefuse()
    {
        byte[] file = Wrap("en-US", RawBody((4, "item", 1, "a"), (3, "bow", 1, "b")));

        Assert.Equal(ContentTextChunkCodec.ReasonEntryOrder, Refusal(file));
    }

    [Fact]
    public void ADuplicateKeyRefuses()
    {
        // Ascending is STRICT, so the same key twice is the same refusal as disorder: a canonical chunk has
        // one entry per key or its hash depends on which copy a reader kept.
        byte[] file = Wrap("en-US", RawBody((4, "item", 1, "a"), (4, "item", 1, "b")));

        Assert.Equal(ContentTextChunkCodec.ReasonEntryOrder, Refusal(file));
    }

    [Fact]
    public void AKeyLengthOutsideOneToOneNinetyTwoRefuses()
    {
        Assert.Equal(192, ContentTextChunkCodec.MaxKeyBytes);
        Assert.Equal(ContentTextChunkCodec.ReasonKeyLength, Refusal(Wrap("en-US", RawBody((0, string.Empty, 1, "a")))));

        string tooLong = new('a', ContentTextChunkCodec.MaxKeyBytes + 1);
        Assert.Equal(ContentTextChunkCodec.ReasonKeyLength, Refusal(Wrap("en-US", RawBody((193, tooLong, 1, "a")))));

        // The bound itself is legal, so the refusal is the step past it rather than the edge.
        string atBound = new('a', ContentTextChunkCodec.MaxKeyBytes);
        Assert.True(ContentTextChunkCodec.TryDecode(Wrap("en-US", RawBody((192, atBound, 1, "a"))), out _, out _));
    }

    [Fact]
    public void AValueLengthOverEightThousandOneHundredAndNinetyTwoRefuses()
    {
        Assert.Equal(8192, ContentTextChunkCodec.MaxValueBytes);

        string tooLong = new('x', ContentTextChunkCodec.MaxValueBytes + 1);
        Assert.Equal(ContentTextChunkCodec.ReasonValueLength, Refusal(Wrap("en-US", RawBody((4, "item", 8193, tooLong)))));

        string atBound = new('x', ContentTextChunkCodec.MaxValueBytes);
        Assert.True(ContentTextChunkCodec.TryDecode(Wrap("en-US", RawBody((4, "item", 8192, atBound))), out _, out _));
    }

    [Fact]
    public void ADeclaredLengthThatRunsPastTheBodyRefuses()
    {
        Assert.Equal(ContentTextChunkCodec.ReasonTruncated, Refusal(Wrap("en-US", RawBody((4, "item", 40, "short")))));
    }

    [Fact]
    public void AValueThatIsNotValidUtf8Refuses()
    {
        // 0xFF is not a legal UTF-8 byte anywhere. Without this check the chunk decoded and the entry
        // surfaced later as U+FFFD, so a mojibake string reached a player through a chunk that verified.
        byte[] body = RawByteBody((Encoding.UTF8.GetBytes("item"), [0xFF]));

        Assert.Equal(ContentTextChunkCodec.ReasonValueEncoding, Refusal(Wrap("en-US", body)));
    }

    [Fact]
    public void AKeyThatIsNotValidUtf8Refuses()
    {
        byte[] body = RawByteBody(([0xFF], Encoding.UTF8.GetBytes("a")));

        Assert.Equal(ContentTextChunkCodec.ReasonKeyEncoding, Refusal(Wrap("en-US", body)));
    }

    [Fact]
    public void AValueCarryingAstralAndMultiByteSequencesIsAccepted()
    {
        // The check is validity and not ASCII: the catalog is localized, so every entry a translator writes
        // is multi byte and the refusal has to be narrow enough to let all of it through.
        var entries = new[]
        {
            Entry("item.a.name", "\u00e9p\u00e9e longue"),
            Entry("item.b.name", "\u5927\u5251"),
            Entry("item.c.name", "\U0001F5E1 blade"),
        };

        Assert.True(ContentTextChunkCodec.TryDecode(
            ContentTextChunkCodec.Encode("en-US", entries), out ContentTextChunk? chunk, out string? reason));
        Assert.Null(reason);
        Assert.NotNull(chunk);
        Assert.Equal(entries, Read(chunk));
    }

    [Fact]
    public void TrailingBytesInTheBodyRefuse()
    {
        byte[] body = [.. RawBody((4, "item", 1, "a")), (byte)0];

        Assert.Equal(ContentTextChunkCodec.ReasonTrailingBytes, Refusal(Wrap("en-US", body)));
    }

    // ---- The header refusals, spec 7.6 ----

    [Fact]
    public void ABadMagicRefuses()
    {
        byte[] file = ContentTextChunkCodec.Encode("en-US", Twelve);
        file[3] = (byte)'Z';

        Assert.Equal(ContentTextChunkCodec.ReasonMagic, Refusal(file));
    }

    [Fact]
    public void AFormatVersionOtherThanTheReadersRefusesTheWholeRecord()
    {
        byte[] file = ContentTextChunkCodec.Encode("en-US", Twelve);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), (ushort)(ContentPackFormat.TextChunkFormatVersion + 1));

        Assert.Equal(ContentTextChunkCodec.ReasonFormatVersion, Refusal(file));
    }

    [Fact]
    public void ANonZeroReservedByteRefuses()
    {
        // KECT was the one format in the spec without a reserved byte, which would have made it the only one
        // that could not grow a flag.
        byte[] file = ContentTextChunkCodec.Encode("en-US", Twelve);
        file[8 + 5] = 1;

        Assert.Equal(ContentTextChunkCodec.ReasonReservedSet, Refusal(file));
    }

    [Fact]
    public void ALanguageTagOutsideOneToThirtyFiveBytesRefuses()
    {
        Assert.Equal(35, ContentTextChunkCodec.MaxLanguageTagBytes);

        byte[] file = ContentTextChunkCodec.Encode("en-US", Twelve);
        file[6] = 0;
        Assert.Equal(ContentTextChunkCodec.ReasonLanguageTag, Refusal(file));

        file[6] = 36;
        Assert.Equal(ContentTextChunkCodec.ReasonLanguageTag, Refusal(file));

        Assert.Throws<ArgumentException>(() => ContentTextChunkCodec.Encode(new string('a', 36), Twelve));
    }

    [Fact]
    public void AnUnknownCompressionByteRefuses()
    {
        byte[] file = ContentTextChunkCodec.Encode("en-US", Twelve);
        file[7 + 5] = 9;

        Assert.Equal(ContentTextChunkCodec.ReasonCompression, Refusal(file));
    }

    [Fact]
    public void TheTwoSizeRefusalsAreTakenFromTheHeaderAlone()
    {
        byte[] tooLarge = ContentTextChunkCodec.Encode("en-US", Twelve);
        BinaryPrimitives.WriteUInt32LittleEndian(tooLarge.AsSpan(9 + 5), uint.MaxValue);
        Assert.Equal(ContentTextChunkCodec.ReasonTooLarge, Refusal(tooLarge));

        byte[] wrongStored = ContentTextChunkCodec.Encode("en-US", Twelve);
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(wrongStored.AsSpan(13 + 5));
        BinaryPrimitives.WriteUInt32LittleEndian(wrongStored.AsSpan(13 + 5), declared + 1);
        Assert.Equal(ContentTextChunkCodec.ReasonStoredLength, Refusal(wrongStored));
    }

    [Fact]
    public void EveryTruncationRefusesWithoutThrowing()
    {
        byte[] file = ContentTextChunkCodec.Encode("en-US", Twelve);

        for (int length = 0; length < file.Length; length++)
        {
            Assert.False(ContentTextChunkCodec.TryDecode(file.AsSpan(0, length), out _, out string? reason));
            Assert.False(string.IsNullOrEmpty(reason));
        }
    }

    // ---- The canonical form and the hash, spec 7.6 and 7.8 ----

    [Fact]
    public void TheCanonicalFormCarriesTheWholeHeaderSoTwoLanguagesAreTwoChunks()
    {
        byte[] english = ContentTextChunkCodec.Canonical("en-US", Twelve);
        byte[] french = ContentTextChunkCodec.Canonical("fr-FR", Twelve);

        Assert.Equal(english.Length, french.Length);
        Assert.NotEqual(english, french);
        Assert.NotEqual(ContentTextChunkCodec.Hash("en-US", Twelve), ContentTextChunkCodec.Hash("fr-FR", Twelve));
    }

    [Fact]
    public void TheHashIsOverTheCanonicalBytesUnderTheTextSubDomain()
    {
        byte[] canonical = ContentTextChunkCodec.Canonical("en-US", Twelve);

        Assert.Equal(ContentHash.OfTextChunk(canonical), ContentTextChunkCodec.Hash("en-US", Twelve));
        Assert.NotEqual(ContentHash.OfChunk(canonical), ContentTextChunkCodec.Hash("en-US", Twelve));

        // The decoded chunk verifies itself the same way, over the header and body in place.
        Assert.True(ContentTextChunkCodec.TryDecode(ContentTextChunkCodec.Encode("en-US", Twelve), out ContentTextChunk? chunk, out _));
        Assert.NotNull(chunk);
        Assert.Equal(ContentTextChunkCodec.Hash("en-US", Twelve), chunk.ComputeHash());
    }

    [Fact]
    public void TheCanonicalFormIsIndependentOfWhetherTheBodyCompressed()
    {
        byte[] stored = ContentTextChunkCodec.Encode("en-US", Twelve);
        byte[] canonical = ContentTextChunkCodec.Canonical("en-US", Twelve);

        Assert.Equal(ContentPackFormat.CompressionBrotli, stored[7 + 5]);
        Assert.True(stored.Length < canonical.Length);

        Assert.True(ContentTextChunkCodec.TryDecode(stored, out ContentTextChunk? fromStored, out _));
        Assert.True(ContentTextChunkCodec.TryDecode(canonical, out ContentTextChunk? fromCanonical, out _));
        Assert.NotNull(fromStored);
        Assert.NotNull(fromCanonical);
        Assert.Equal(Read(fromCanonical), Read(fromStored));
        Assert.Equal(fromCanonical.ComputeHash(), fromStored.ComputeHash());
    }

    [Fact]
    public void ABrotliStreamThatExpandsPastItsDeclaredLengthIsRefusedAsTooLarge()
    {
        // The same refusal ContentChunkCodec gives, because spec 8.4 names the overrun kind generically and
        // spec 7.6 takes KECC's header refusals whole. A static TryDecompress cannot tell the two apart.
        byte[] stored = ContentTextChunkCodec.Encode("en-US", Twelve);
        Assert.Equal(ContentPackFormat.CompressionBrotli, stored[7 + 5]);

        // The stream still decompresses, it just claims one byte less room than it needs.
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan(9 + 5));
        BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(9 + 5), declared - 1);

        Assert.False(ContentTextChunkCodec.TryDecode(stored, out ContentTextChunk? chunk, out string? reason));
        Assert.Null(chunk);
        Assert.Equal(ContentTextChunkCodec.ReasonTooLarge, reason);
    }

    // ---- The derived key, contracts 12.1 and 12.2 ----

    [Fact]
    public void TheKeyIsTypeKeyThenContentKeyThenFieldName()
    {
        Assert.Equal("item.stone_sword.name", ContentTextKey.Derive("item", "stone_sword"u8, "name"));
        Assert.Equal("item.stone_sword.examine", ContentTextKey.Derive("item", "stone_sword"u8, "examine"));
        Assert.Equal("tag.metal.name", ContentTextKey.Derive("tag", "metal"u8, "name"));

        // The dot never appears inside a segment, so a key splits on it exactly.
        Assert.Equal(3, ContentTextKey.Derive("item", "stone_sword"u8, "name").Split('.').Length);
    }

    [Fact]
    public void TheOneNinetyTwoCharacterBoundIsReachable()
    {
        // Three 64 character segments plus two dots is 194, so the bound of contracts 12.2 is reachable and
        // KEC0030 has something to check.
        string widest = new('a', 64);
        Assert.True(ContentTextKey.ExceedsBound(widest, 64, widest));
        Assert.Equal(194, ContentTextKey.Derive(widest, Encoding.UTF8.GetBytes(widest), widest).Length);

        Assert.Equal(192, ContentTextKey.MaxKeyLength);
        Assert.False(ContentTextKey.ExceedsBound(widest, 62, widest));
        Assert.True(ContentTextKey.ExceedsBound(widest, 63, widest));
    }

    [Fact]
    public void TheBoundCheckAgreesWithWhatDeriveProduces()
    {
        (string TypeKey, string ContentKey, string Field)[] cases =
        [
            ("item", "stone_sword", "name"),
            ("loot_table", new string('b', 64), "display_format"),
            (new string('c', 64), new string('d', 64), new string('e', 64)),
        ];

        foreach ((string typeKey, string contentKey, string field) in cases)
        {
            bool exceeds = ContentTextKey.ExceedsBound(typeKey, contentKey.Length, field);
            Assert.Equal(ContentTextKey.Derive(typeKey, Encoding.UTF8.GetBytes(contentKey), field).Length > ContentTextKey.MaxKeyLength, exceeds);
        }
    }
}
