using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Format;

/// <summary>
/// The remap rule encoding of contracts 8.4 and the <c>KECR</c> chunk of spec 7.7, which carries the FULL
/// rule list from sequence 1 rather than a delta because a page can be arbitrarily old.
/// <para>
/// The refusals here are the fail-closed half of contracts 8.5: a gap in the sequence, a non-ascending
/// sequence and a kind the reader does not know are all REFUSALS and never skips, which is what
/// <c>FormatGeneration</c> exists to announce in advance.
/// </para>
/// </summary>
public class RemapRuleCodecTests
{
    static RemapRule Rule(
        int sequence,
        int introducedIn,
        ushort type,
        RemapRuleKind kind,
        int fromId,
        int toId,
        params byte[] payload)
        => new(sequence, introducedIn, new ContentTypeId(type), kind, fromId, toId, payload);

    static byte[] Retire(byte policy, int destination)
    {
        if (policy != RemapRule.RetirePolicyReplacement)
        {
            return [policy];
        }

        byte[] payload = new byte[5];
        payload[0] = policy;
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1), destination);
        return payload;
    }

    static byte[] Cap(int newCap)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, newCap);
        return payload;
    }

    static byte[] Encoded(RemapRule rule)
    {
        byte[] buffer = new byte[RemapRuleCodec.Size(rule)];
        Assert.Equal(buffer.Length, RemapRuleCodec.Write(buffer, rule));
        return buffer;
    }

    static void AddVarint(List<byte> bytes, uint value)
    {
        Span<byte> scratch = stackalloc byte[5];
        int written = ContentVarint.Write(scratch, value);
        for (int i = 0; i < written; i++)
        {
            bytes.Add(scratch[i]);
        }
    }

    /// <summary>
    /// A rule written field by field WITHOUT the value type, which is the only way to hand the reader a
    /// shape the constructor refuses to build. The declared payload length is separate from the payload, so
    /// a length that lies about what follows it is expressible too.
    /// </summary>
    static byte[] RawRule(uint sequence, uint introducedIn, ushort type, byte kind, uint fromId, uint toId, byte declaredLength, params byte[] payload)
    {
        var bytes = new List<byte>();
        AddVarint(bytes, sequence);
        AddVarint(bytes, introducedIn);
        bytes.Add((byte)(type & 0xFF));
        bytes.Add((byte)(type >> 8));
        bytes.Add(kind);
        AddVarint(bytes, fromId);
        AddVarint(bytes, toId);
        bytes.Add(declaredLength);
        bytes.AddRange(payload);
        return [.. bytes];
    }

    static string? ChunkRefusal(byte[] file)
    {
        Assert.False(ContentRuleChunkCodec.TryDecode(file, out RemapRuleSet? rules, out string? reason));
        Assert.Null(rules);
        return reason;
    }

    // ---- The rule encoding, contracts 8.4 ----

    [Fact]
    public void TheRuleEncodingIsTheDeclaredFieldOrderAndNotOneByteMore()
    {
        byte[] bytes = Encoded(Rule(1, 2, 1, RemapRuleKind.ReplacedBy, 9, 12));

        Assert.Equal<byte[]>([0x01, 0x02, 0x01, 0x00, 0x01, 0x09, 0x0C, 0x00], bytes);
    }

    [Fact]
    public void ATypicalRuleIsNineToTwelveBytes()
    {
        // Realistic content ids, which is where the figure in contracts 8.4 comes from. The floor is the
        // eight bytes a rule between single-digit ids takes, which is stated here so the range is not read
        // as a minimum.
        Assert.InRange(RemapRuleCodec.Size(Rule(12, 47, 1, RemapRuleKind.ReplacedBy, 1337, 4200)), 9, 12);
        Assert.Equal(8, RemapRuleCodec.Size(Rule(1, 1, 1, RemapRuleKind.ReplacedBy, 1, 2)));

        // Ten thousand of them is a rounding error against a pack.
        var rules = new List<RemapRule>();
        for (int i = 1; i <= 10_000; i++)
        {
            rules.Add(Rule(i, 47, 1, RemapRuleKind.ReplacedBy, i + 1000, i + 50_000));
        }

        Assert.InRange(ContentRuleChunkCodec.Canonical(rules).Length, 90_000, 140_000);
    }

    [Fact]
    public void TheIdsAreUnsignedVarintsAndAreNeverZigZagged()
    {
        // Contracts 15: content ids, counts and version numbers are declared UNSIGNED, so a small id costs
        // one byte and 64 is one byte rather than the two a zig-zag transform would spend on it.
        byte[] bytes = Encoded(Rule(1, 1, 1, RemapRuleKind.ReplacedBy, 64, 127));

        Assert.Equal<byte[]>([0x01, 0x01, 0x01, 0x00, 0x01, 0x40, 0x7F, 0x00], bytes);
    }

    [Theory]
    [InlineData(RemapRuleKind.ReplacedBy)]
    [InlineData(RemapRuleKind.MovedToLegacy)]
    public void EveryIdRemappingKindRoundTrips(RemapRuleKind kind)
    {
        RemapRule source = Rule(3, 18, 1024, kind, 700, 900);
        byte[] bytes = Encoded(source);

        int offset = 0;
        Assert.True(RemapRuleCodec.TryRead(bytes, ref offset, out RemapRule? decoded, out string? reason));
        Assert.Null(reason);
        Assert.NotNull(decoded);
        Assert.Equal(bytes.Length, offset);
        Assert.Equal(source.Sequence, decoded.Sequence);
        Assert.Equal(source.IntroducedIn, decoded.IntroducedIn);
        Assert.Equal(source.Type, decoded.Type);
        Assert.Equal(source.Kind, decoded.Kind);
        Assert.Equal(source.FromId, decoded.FromId);
        Assert.Equal(source.ToId, decoded.ToId);
        Assert.Equal(900, decoded.Destination);
        Assert.True(decoded.Payload.IsEmpty);
    }

    [Fact]
    public void TheFourKindsAreTheDeclaredNumbersAndThereIsNoDeletePolicy()
    {
        Assert.Equal(1, (byte)RemapRuleKind.ReplacedBy);
        Assert.Equal(2, (byte)RemapRuleKind.Retired);
        Assert.Equal(3, (byte)RemapRuleKind.MovedToLegacy);
        Assert.Equal(4, (byte)RemapRuleKind.StackCapLowered);
        Assert.Equal(4, Enum.GetValues<RemapRuleKind>().Length);

        // A definition is never deleted and is retired instead, so the vocabulary has nowhere to put one.
        Assert.DoesNotContain(Enum.GetNames<RemapRuleKind>(), n => n.Contains("Delete", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0x01, RemapRule.RetirePolicyPlaceholder);
        Assert.Equal(0x02, RemapRule.RetirePolicyReplacement);
    }

    [Fact]
    public void ARetirePlaceholderPayloadIsItsPolicyByteAlone()
    {
        RemapRule source = Rule(1, 4, 1, RemapRuleKind.Retired, 31, 0, Retire(RemapRule.RetirePolicyPlaceholder, 0));
        byte[] bytes = Encoded(source);

        int offset = 0;
        Assert.True(RemapRuleCodec.TryRead(bytes, ref offset, out RemapRule? decoded, out _));
        Assert.NotNull(decoded);
        Assert.True(decoded.IsRetiredPlaceholder);
        Assert.Equal(0, decoded.Destination);
        Assert.Equal<byte[]>([RemapRule.RetirePolicyPlaceholder], decoded.Payload.ToArray());
    }

    [Fact]
    public void ARetireReplacementPayloadCarriesAnInt32Destination()
    {
        RemapRule source = Rule(1, 4, 1, RemapRuleKind.Retired, 31, 0, Retire(RemapRule.RetirePolicyReplacement, 4242));

        int offset = 0;
        Assert.True(RemapRuleCodec.TryRead(Encoded(source), ref offset, out RemapRule? decoded, out _));
        Assert.NotNull(decoded);
        Assert.False(decoded.IsRetiredPlaceholder);

        // Contracts 8.2: the behaviour is kind 1, so the effective destination is the payload's.
        Assert.Equal(4242, decoded.Destination);
    }

    [Fact]
    public void AStackCapLoweredPayloadIsAnInt32Cap()
    {
        RemapRule source = Rule(1, 9, 1, RemapRuleKind.StackCapLowered, 77, 0, Cap(16));

        int offset = 0;
        Assert.True(RemapRuleCodec.TryRead(Encoded(source), ref offset, out RemapRule? decoded, out _));
        Assert.NotNull(decoded);
        Assert.True(decoded.TryGetStackCap(out int cap));
        Assert.Equal(16, cap);

        // The id does not move, so there is no destination to carry.
        Assert.Equal(0, decoded.Destination);
        Assert.False(Rule(1, 9, 1, RemapRuleKind.ReplacedBy, 77, 78).TryGetStackCap(out _));
    }

    [Fact]
    public void AnUnknownKindFailsClosedAndIsNeverSkipped()
    {
        byte[] unknownKinds = [0, 5, 255];

        foreach (byte kind in unknownKinds)
        {
            byte[] bytes = RawRule(1, 1, 1, kind, 9, 12, 0);

            int offset = 0;
            Assert.False(RemapRuleCodec.TryRead(bytes, ref offset, out RemapRule? rule, out string? reason));
            Assert.Null(rule);
            Assert.Equal(RemapRuleCodec.ReasonKind, reason);
        }
    }

    [Fact]
    public void APayloadThatIsNotTheShapeItsKindDeclaresRefuses()
    {
        // A Retired rule with no policy byte, one with a policy nobody defined, one whose replacement policy
        // is missing its destination, and a stack cap that is not an int32. A reader that skipped any of
        // them would keep a retired id playable or apply a cap it invented.
        byte[][] bad =
        [
            RawRule(1, 1, 1, (byte)RemapRuleKind.Retired, 9, 0, 0),
            RawRule(1, 1, 1, (byte)RemapRuleKind.Retired, 9, 0, 1, 0x09),
            RawRule(1, 1, 1, (byte)RemapRuleKind.Retired, 9, 0, 3, RemapRule.RetirePolicyReplacement, 1, 2),
            RawRule(1, 1, 1, (byte)RemapRuleKind.StackCapLowered, 9, 0, 2, 1, 2),
        ];

        foreach (byte[] bytes in bad)
        {
            int offset = 0;
            Assert.False(RemapRuleCodec.TryRead(bytes, ref offset, out _, out string? reason));
            Assert.Equal(RemapRuleCodec.ReasonPayload, reason);
        }
    }

    [Fact]
    public void APayloadLengthOverSixtyFourRefuses()
    {
        byte[] bytes = RawRule(1, 1, 1, (byte)RemapRuleKind.ReplacedBy, 9, 12, 65, new byte[65]);

        int offset = 0;
        Assert.False(RemapRuleCodec.TryRead(bytes, ref offset, out _, out string? reason));
        Assert.Equal(RemapRuleCodec.ReasonPayload, reason);

        // The value type refuses to build one at all, because an over-long payload is a producer error
        // rather than something a decoder has to be total about.
        Assert.Throws<ArgumentException>(() => Rule(1, 1, 1, RemapRuleKind.ReplacedBy, 9, 12, new byte[65]));
    }

    [Fact]
    public void EveryTruncationOfOneRuleRefusesWithoutThrowing()
    {
        byte[] bytes = Encoded(Rule(1, 1, 1, RemapRuleKind.Retired, 9, 0, Retire(RemapRule.RetirePolicyReplacement, 12)));

        for (int length = 0; length < bytes.Length; length++)
        {
            int offset = 0;
            Assert.False(RemapRuleCodec.TryRead(bytes.AsSpan(0, length), ref offset, out _, out string? reason));
            Assert.False(string.IsNullOrEmpty(reason));
            Assert.Equal(0, offset);
        }
    }

    // ---- The KECR chunk, spec 7.7 ----

    static readonly RemapRule[] SixRules =
    [
        Rule(1, 4, 1, RemapRuleKind.ReplacedBy, 10, 20),
        Rule(2, 4, 1, RemapRuleKind.Retired, 11, 0, Retire(RemapRule.RetirePolicyPlaceholder, 0)),
        Rule(3, 5, 1, RemapRuleKind.Retired, 12, 0, Retire(RemapRule.RetirePolicyReplacement, 21)),
        Rule(4, 5, 2, RemapRuleKind.MovedToLegacy, 13, 22),
        Rule(5, 7, 2, RemapRuleKind.StackCapLowered, 14, 0, Cap(16)),
        Rule(6, 9, 1024, RemapRuleKind.ReplacedBy, 15, 23),
    ];

    [Fact]
    public void TheChunkHeaderIsTheDeclaredLayout()
    {
        byte[] file = ContentRuleChunkCodec.Canonical(SixRules);

        Assert.Equal(20, ContentPackFormat.RuleHeaderBytes);
        Assert.Equal("KECR"u8.ToArray(), file[..4]);
        Assert.Equal(ContentPackFormat.RuleChunkFormatVersion, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4)));
        Assert.Equal(ContentPackFormat.CompressionNone, file[6]);
        Assert.Equal(0, file[7]);
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8)));

        uint uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
        Assert.Equal(uncompressed, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16)));
        Assert.Equal(file.Length, ContentPackFormat.RuleHeaderBytes + (int)uncompressed);
    }

    [Fact]
    public void EveryKindSurvivesTheChunkRoundTrip()
    {
        Assert.True(ContentRuleChunkCodec.TryDecode(ContentRuleChunkCodec.Encode(SixRules), out RemapRuleSet? set, out string? reason));
        Assert.Null(reason);
        Assert.NotNull(set);
        Assert.Equal(SixRules.Length, set.Rules.Count);

        for (int i = 0; i < SixRules.Length; i++)
        {
            Assert.Equal(SixRules[i].Sequence, set.Rules[i].Sequence);
            Assert.Equal(SixRules[i].IntroducedIn, set.Rules[i].IntroducedIn);
            Assert.Equal(SixRules[i].Type, set.Rules[i].Type);
            Assert.Equal(SixRules[i].Kind, set.Rules[i].Kind);
            Assert.Equal(SixRules[i].FromId, set.Rules[i].FromId);
            Assert.Equal(SixRules[i].ToId, set.Rules[i].ToId);
            Assert.Equal(SixRules[i].Payload.ToArray(), set.Rules[i].Payload.ToArray());
        }

        Assert.Equal(ContentRuleChunkCodec.Canonical(SixRules), ContentRuleChunkCodec.Canonical(set.Rules));
    }

    [Fact]
    public void AGapInTheSequenceRefuses()
    {
        RemapRule[] gapped = [SixRules[0], Rule(3, 4, 1, RemapRuleKind.ReplacedBy, 11, 21)];

        Assert.Equal(ContentRuleChunkCodec.ReasonSequenceGap, ChunkRefusal(ContentRuleChunkCodec.Encode(gapped)));
    }

    [Fact]
    public void ANonAscendingSequenceRefuses()
    {
        RemapRule[] disordered = [Rule(2, 4, 1, RemapRuleKind.ReplacedBy, 10, 20), Rule(1, 4, 1, RemapRuleKind.ReplacedBy, 11, 21)];

        // The first rule is already wrong: a chunk holds the FULL list from sequence 1 (contracts 8.5), so a
        // chunk opening at 2 has lost a rule rather than reordered one.
        Assert.Equal(ContentRuleChunkCodec.ReasonSequenceGap, ChunkRefusal(ContentRuleChunkCodec.Encode(disordered)));

        RemapRule[] backwards = [SixRules[0], SixRules[1], Rule(2, 4, 1, RemapRuleKind.ReplacedBy, 12, 22)];
        Assert.Equal(ContentRuleChunkCodec.ReasonSequenceOrder, ChunkRefusal(ContentRuleChunkCodec.Encode(backwards)));
    }

    [Fact]
    public void AnUnknownKindInAChunkRefusesTheWholeChunk()
    {
        byte[] file = ContentRuleChunkCodec.Canonical([Rule(1, 4, 1, RemapRuleKind.ReplacedBy, 10, 20)]);
        file[ContentPackFormat.RuleHeaderBytes + 4] = 9;

        Assert.Equal(RemapRuleCodec.ReasonKind, ChunkRefusal(file));
    }

    [Fact]
    public void ABadMagicRefuses()
    {
        byte[] file = ContentRuleChunkCodec.Encode(SixRules);
        file[3] = (byte)'Z';

        Assert.Equal(ContentRuleChunkCodec.ReasonMagic, ChunkRefusal(file));
    }

    [Fact]
    public void AFormatVersionOtherThanTheReadersRefusesTheWholeRecord()
    {
        byte[] file = ContentRuleChunkCodec.Encode(SixRules);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), (ushort)(ContentPackFormat.RuleChunkFormatVersion + 1));

        Assert.Equal(ContentRuleChunkCodec.ReasonFormatVersion, ChunkRefusal(file));
    }

    [Fact]
    public void ANonZeroReservedByteRefuses()
    {
        byte[] file = ContentRuleChunkCodec.Encode(SixRules);
        file[7] = 1;

        Assert.Equal(ContentRuleChunkCodec.ReasonReservedSet, ChunkRefusal(file));
    }

    [Fact]
    public void AnUnknownCompressionByteRefuses()
    {
        byte[] file = ContentRuleChunkCodec.Encode(SixRules);
        file[6] = 9;

        Assert.Equal(ContentRuleChunkCodec.ReasonCompression, ChunkRefusal(file));
    }

    [Fact]
    public void TheTwoSizeRefusalsAreTakenFromTheHeaderAlone()
    {
        byte[] tooLarge = ContentRuleChunkCodec.Encode(SixRules);
        BinaryPrimitives.WriteUInt32LittleEndian(tooLarge.AsSpan(12), uint.MaxValue);
        Assert.Equal(ContentRuleChunkCodec.ReasonTooLarge, ChunkRefusal(tooLarge));

        byte[] shortBody = ContentRuleChunkCodec.Encode(SixRules);
        BinaryPrimitives.WriteUInt32LittleEndian(shortBody.AsSpan(16), (uint)(shortBody.Length - ContentPackFormat.RuleHeaderBytes + 1));
        Assert.Equal(ContentRuleChunkCodec.ReasonStoredLength, ChunkRefusal(shortBody));
    }

    [Fact]
    public void TrailingBytesInTheBodyRefuse()
    {
        byte[] canonical = ContentRuleChunkCodec.Canonical(SixRules);
        byte[] file = [.. canonical, (byte)0];
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)(file.Length - ContentPackFormat.RuleHeaderBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), (uint)(file.Length - ContentPackFormat.RuleHeaderBytes));

        Assert.Equal(ContentRuleChunkCodec.ReasonTrailingBytes, ChunkRefusal(file));
    }

    [Fact]
    public void EveryTruncationOfAChunkRefusesWithoutThrowing()
    {
        byte[] file = ContentRuleChunkCodec.Encode(SixRules);

        for (int length = 0; length < file.Length; length++)
        {
            Assert.False(ContentRuleChunkCodec.TryDecode(file.AsSpan(0, length), out _, out string? reason));
            Assert.False(string.IsNullOrEmpty(reason));
        }
    }

    [Fact]
    public void AnEmptyRuleListRoundTrips()
    {
        Assert.True(ContentRuleChunkCodec.TryDecode(ContentRuleChunkCodec.Encode([]), out RemapRuleSet? set, out _));
        Assert.NotNull(set);
        Assert.Empty(set.Rules);
        Assert.Equal(0, set.ActiveStamp);
    }

    // ---- The hash, spec 7.7 and 7.8 ----

    [Fact]
    public void TheHashIsOverTheCanonicalBytesUnderTheRulesSubDomain()
    {
        Assert.Equal(ContentHash.OfRuleChunk(ContentRuleChunkCodec.Canonical(SixRules)), ContentRuleChunkCodec.Hash(SixRules));
        Assert.NotEqual(ContentHash.OfChunk(ContentRuleChunkCodec.Canonical(SixRules)), ContentRuleChunkCodec.Hash(SixRules));
    }

    [Fact]
    public void TheCanonicalFormIsIndependentOfWhetherTheBodyCompressed()
    {
        // A list long enough to compress, so the stored file and the canonical file are different bytes with
        // one hash. That is what makes a compressor change a no-op for every cached client.
        var many = new List<RemapRule>();
        for (int i = 1; i <= 400; i++)
        {
            many.Add(Rule(i, 4, 1, RemapRuleKind.ReplacedBy, i, i + 1000));
        }

        byte[] stored = ContentRuleChunkCodec.Encode(many);
        byte[] canonical = ContentRuleChunkCodec.Canonical(many);

        Assert.Equal(ContentPackFormat.CompressionBrotli, stored[6]);
        Assert.True(stored.Length < canonical.Length);
        Assert.NotEqual(canonical, stored);

        Assert.True(ContentRuleChunkCodec.TryDecode(stored, out RemapRuleSet? fromStored, out _));
        Assert.NotNull(fromStored);
        Assert.Equal(many.Count, fromStored.Rules.Count);
        Assert.Equal(ContentRuleChunkCodec.Hash(many), ContentRuleChunkCodec.Hash(fromStored.Rules));
        Assert.Equal(canonical, ContentRuleChunkCodec.Canonical(fromStored.Rules));
    }

    [Fact]
    public void ABrotliStreamThatExpandsPastItsDeclaredLengthIsRefusedAsTooLarge()
    {
        // The same refusal ContentChunkCodec gives, because spec 8.4 names the overrun kind generically. A
        // static TryDecompress collapses a resource refusal and a corrupt stream into one false.
        var many = new List<RemapRule>();
        for (int i = 1; i <= 400; i++)
        {
            many.Add(Rule(i, 4, 1, RemapRuleKind.ReplacedBy, i, i + 1000));
        }

        byte[] stored = ContentRuleChunkCodec.Encode(many);
        Assert.Equal(ContentPackFormat.CompressionBrotli, stored[6]);

        // The stream still decompresses, it just claims one byte less room than it needs.
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(12), declared - 1);

        Assert.False(ContentRuleChunkCodec.TryDecode(stored, out RemapRuleSet? rules, out string? reason));
        Assert.Null(rules);
        Assert.Equal(ContentRuleChunkCodec.ReasonTooLarge, reason);
    }

    [Fact]
    public void AChunkIsNeverStoredLargerThanItsCanonicalForm()
    {
        // A body whose compressed form is not SMALLER is stored uncompressed, so the compression byte and
        // the stored length always agree with each other. An empty body is the case that can never shrink.
        IReadOnlyList<RemapRule>[] lists = [Array.Empty<RemapRule>(), SixRules];

        foreach (IReadOnlyList<RemapRule> rules in lists)
        {
            byte[] stored = ContentRuleChunkCodec.Encode(rules);
            byte[] canonical = ContentRuleChunkCodec.Canonical(rules);

            Assert.True(stored.Length <= canonical.Length);
            if (stored[6] == ContentPackFormat.CompressionNone)
            {
                Assert.Equal(canonical, stored);
            }
            else
            {
                Assert.True(stored.Length < canonical.Length);
            }
        }

        Assert.Equal(ContentPackFormat.CompressionNone, ContentRuleChunkCodec.Encode(Array.Empty<RemapRule>())[6]);
    }
}
