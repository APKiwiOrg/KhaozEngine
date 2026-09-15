using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Format;

/// <summary>
/// The <c>KECM</c> manifest file of spec 7.4 and the canonical TEXT of spec 6.8 its two digests are taken
/// over, which are deliberately two different things: the file carries a per-chunk
/// <c>uncompressedBytes</c> the text does not, so the size sits outside the manifest hash and one refusal
/// binds it to reality instead (contracts 7.3, 11.3).
/// </summary>
public class ContentManifestCodecTests
{
    sealed class StubRowCodec : IContentRowCodec
    {
        public IReadOnlyList<string> WrittenFields => ["sort"];

        public void Encode(ContentRow row, IBufferWriter<byte> destination) => throw new NotSupportedException();

        public bool TryDecode(ReadOnlySpan<byte> body, [MaybeNullWhen(false)] out ContentRow row, out string? reason)
            => throw new NotSupportedException();
    }

    static string Digest(byte seed)
    {
        byte[] raw = new byte[32];
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(seed + i);
        }

        return Convert.ToHexStringLower(raw);
    }

    static readonly string TagChunkHash = Digest(0x10);
    static readonly string ItemChunkHash = Digest(0x40);
    static readonly string ItemChunkThreeHash = Digest(0x70);
    static readonly string RuleHash = Digest(0xA0);
    static readonly string EnglishHash = Digest(0xC0);
    static readonly string FrenchHash = Digest(0xE0);

    static ContentManifest Server() => new()
    {
        Side = ContentManifestSide.Server,
        VersionNumber = 47,
        FormatGeneration = (uint)ContentPackFormat.Generation,
        MinimumServerBuild = 12,
        MinimumClientBuild = 9,
        RemapRuleChunkHash = RuleHash,
        Types =
        [
            new ManifestTypeEntry(1, "tag", 4096, ContentVisibility.Client, [new ManifestChunkEntry(0, 44, TagChunkHash)]),
            new ManifestTypeEntry(2, "item", 1024, ContentVisibility.ServerOnly,
            [
                new ManifestChunkEntry(0, 120, ItemChunkHash),
                new ManifestChunkEntry(3, 90, ItemChunkThreeHash),
            ]),
        ],
        Languages = [new ManifestLanguageEntry("en-US", EnglishHash), new ManifestLanguageEntry("fr-FR", FrenchHash)],
    };

    static ContentManifest Client() => new()
    {
        Side = ContentManifestSide.Client,
        VersionNumber = 47,
        FormatGeneration = (uint)ContentPackFormat.Generation,
        MinimumServerBuild = 12,
        MinimumClientBuild = 9,
        RemapRuleChunkHash = RuleHash,
        Types = [new ManifestTypeEntry(1, "tag", 4096, ContentVisibility.Client, [new ManifestChunkEntry(0, 44, TagChunkHash)])],
        Languages = [new ManifestLanguageEntry("en-US", EnglishHash)],
    };

    static ContentTypeRegistry RegistryWith(params (ushort TypeId, string Key, int ChunkSlots)[] types)
    {
        var registry = new ContentTypeRegistry();
        foreach ((ushort typeId, string key, int chunkSlots) in types)
        {
            registry.RegisterContentType(
                ContentRegistrationBand.Engine,
                typeId,
                key,
                new StubRowCodec(),
                validator: null,
                new ContentFieldSchema([new ContentFieldEntry("sort", ContentFieldKind.Int, null, ContentVisibility.Client, false)]),
                ContentVisibility.Client,
                chunkSlots);
        }

        return registry;
    }

    static string? Refusal(byte[] file, ContentManifestSide side)
    {
        Assert.False(ContentManifestCodec.TryDecode(file, side, out ContentManifest? manifest, out string? reason));
        Assert.Null(manifest);
        return reason;
    }

    static bool Holds(byte[] haystack, byte[] needle)
    {
        for (int start = 0; start + needle.Length <= haystack.Length; start++)
        {
            if (haystack.AsSpan(start, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    // ---- The file layout, spec 7.4 ----

    [Fact]
    public void TheFixedHeaderIsTheDeclaredLayout()
    {
        byte[] file = ContentManifestCodec.Encode(Server());

        Assert.Equal(56, ContentManifestCodec.FixedHeaderBytes);
        Assert.Equal("KECM"u8.ToArray(), file[..4]);
        Assert.Equal(ContentPackFormat.ManifestFormatVersion, BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4)));
        Assert.Equal((byte)ContentManifestSide.Server, file[6]);
        Assert.Equal(0, file[7]);
        Assert.Equal(47u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8)));
        Assert.Equal((uint)ContentPackFormat.Generation, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12)));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(16)));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(20)));
        Assert.Equal(Convert.FromHexString(RuleHash), file[24..56]);

        int cursor = 56;
        Assert.True(ContentVarint.TryRead(file, ref cursor, out uint typeCount, out _));
        Assert.Equal(2u, typeCount);
    }

    [Fact]
    public void EveryFieldSurvivesTheRoundTrip()
    {
        ContentManifest source = Server();

        Assert.True(ContentManifestCodec.TryDecode(
            ContentManifestCodec.Encode(source), ContentManifestSide.Server, out ContentManifest? decoded, out string? reason));
        Assert.Null(reason);
        Assert.NotNull(decoded);

        Assert.Equal(source.Side, decoded.Side);
        Assert.Equal(source.VersionNumber, decoded.VersionNumber);
        Assert.Equal(source.FormatGeneration, decoded.FormatGeneration);
        Assert.Equal(source.MinimumServerBuild, decoded.MinimumServerBuild);
        Assert.Equal(source.MinimumClientBuild, decoded.MinimumClientBuild);
        Assert.Equal(source.RemapRuleChunkHash, decoded.RemapRuleChunkHash);
        Assert.Equal(source.Languages, decoded.Languages);
        Assert.Equal(source.Types.Count, decoded.Types.Count);
        for (int i = 0; i < source.Types.Count; i++)
        {
            Assert.Equal(source.Types[i].TypeId, decoded.Types[i].TypeId);
            Assert.Equal(source.Types[i].TypeKey, decoded.Types[i].TypeKey);
            Assert.Equal(source.Types[i].ChunkSlots, decoded.Types[i].ChunkSlots);
            Assert.Equal(source.Types[i].Visibility, decoded.Types[i].Visibility);
            Assert.Equal(source.Types[i].Chunks, decoded.Types[i].Chunks);
        }
    }

    [Fact]
    public void ReEncodingADecodedManifestReproducesTheBytes()
    {
        byte[] file = ContentManifestCodec.Encode(Server());

        Assert.True(ContentManifestCodec.TryDecode(file, ContentManifestSide.Server, out ContentManifest? decoded, out _));
        Assert.NotNull(decoded);
        Assert.Equal(file, ContentManifestCodec.Encode(decoded));
    }

    [Fact]
    public void AHashIsRawInTheFileAndLowerHexOnlyAsText()
    {
        byte[] file = ContentManifestCodec.Encode(Client());

        Assert.True(Holds(file, Convert.FromHexString(TagChunkHash)));
        Assert.False(Holds(file, Encoding.ASCII.GetBytes(TagChunkHash)));
        Assert.Contains(TagChunkHash, ContentManifestText.Canonical(Client()), StringComparison.Ordinal);
    }

    // ---- The refusals, spec 7.4 ----

    [Fact]
    public void AReaderHandedTheWrongSideRefuses()
    {
        Assert.Equal(ContentManifestCodec.ReasonWrongSide, Refusal(ContentManifestCodec.Encode(Server()), ContentManifestSide.Client));
        Assert.Equal(ContentManifestCodec.ReasonWrongSide, Refusal(ContentManifestCodec.Encode(Client()), ContentManifestSide.Server));
    }

    [Fact]
    public void ASideByteNamingNeitherSideRefuses()
    {
        byte[] file = ContentManifestCodec.Encode(Server());
        file[6] = 2;

        Assert.Equal(ContentManifestCodec.ReasonWrongSide, Refusal(file, ContentManifestSide.Server));
    }

    [Fact]
    public void AChunkSlotCountDisagreeingWithTheLocalRegistrationRefuses()
    {
        byte[] file = ContentManifestCodec.Encode(Client());

        Assert.False(ContentManifestCodec.TryDecode(
            file, ContentManifestSide.Client, RegistryWith((1, "tag", 2048)), out _, out string? reason));
        Assert.Equal(ContentManifestCodec.ReasonChunkSlots, reason);

        // The same bytes against a registry that agrees decode fine, so the refusal is the disagreement
        // rather than anything about the file.
        Assert.True(ContentManifestCodec.TryDecode(file, ContentManifestSide.Client, RegistryWith((1, "tag", 4096)), out _, out _));
    }

    [Fact]
    public void ASlotCountNoRegistrationCouldEverDeclareRefusesWithoutARegistry()
    {
        // 300 is neither a power of two nor inside contracts 4.5's range, so no registry anywhere agrees
        // with it and the codec does not need one to say so.
        ContentManifest manifest = Client() with
        {
            Types = [new ManifestTypeEntry(1, "tag", 300, ContentVisibility.Client, [new ManifestChunkEntry(0, 44, TagChunkHash)])],
        };

        Assert.Equal(ContentManifestCodec.ReasonChunkSlots, Refusal(ContentManifestCodec.Encode(manifest), ContentManifestSide.Client));
    }

    [Fact]
    public void TypesOutOfTypeIdOrderRefuse()
    {
        ContentManifest manifest = Server() with { Types = [.. Server().Types.Reverse()] };

        Assert.Equal(ContentManifestCodec.ReasonTypeOrder, Refusal(ContentManifestCodec.Encode(manifest), ContentManifestSide.Server));
    }

    [Fact]
    public void ATypeIdAppearingTwiceRefuses()
    {
        ManifestTypeEntry tag = Server().Types[0];
        ContentManifest manifest = Server() with { Types = [tag, tag] };

        Assert.Equal(ContentManifestCodec.ReasonTypeOrder, Refusal(ContentManifestCodec.Encode(manifest), ContentManifestSide.Server));
    }

    [Fact]
    public void ChunksOutOfIndexOrderRefuse()
    {
        ContentManifest manifest = Server() with
        {
            Types =
            [
                Server().Types[0],
                Server().Types[1] with { Chunks = [.. Server().Types[1].Chunks.Reverse()] },
            ],
        };

        Assert.Equal(ContentManifestCodec.ReasonChunkOrder, Refusal(ContentManifestCodec.Encode(manifest), ContentManifestSide.Server));
    }

    [Fact]
    public void LanguagesOutOfOrdinalTagOrderRefuse()
    {
        ContentManifest manifest = Server() with { Languages = [.. Server().Languages.Reverse()] };

        Assert.Equal(ContentManifestCodec.ReasonLanguageOrder, Refusal(ContentManifestCodec.Encode(manifest), ContentManifestSide.Server));
    }

    [Fact]
    public void AClientManifestNamingAServerOnlyTypeRefuses()
    {
        // Contracts 11.3: the client manifest OMITS every ServerOnly chunk. A client file that names one is
        // a leak whatever its hash says, so it is refused rather than filtered on the way in.
        ContentManifest manifest = Client() with
        {
            Types = [new ManifestTypeEntry(2, "item", 1024, ContentVisibility.ServerOnly, [new ManifestChunkEntry(0, 120, ItemChunkHash)])],
        };

        Assert.Equal(ContentManifestCodec.ReasonVisibility, Refusal(ContentManifestCodec.Encode(manifest), ContentManifestSide.Client));
    }

    [Fact]
    public void ABadMagicRefuses()
    {
        byte[] file = ContentManifestCodec.Encode(Server());
        file[3] = (byte)'Z';

        Assert.Equal(ContentManifestCodec.ReasonMagic, Refusal(file, ContentManifestSide.Server));
    }

    [Fact]
    public void AFormatVersionOtherThanTheReadersRefusesTheWholeRecord()
    {
        byte[] file = ContentManifestCodec.Encode(Server());
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), (ushort)(ContentPackFormat.ManifestFormatVersion + 1));

        Assert.Equal(ContentManifestCodec.ReasonFormatVersion, Refusal(file, ContentManifestSide.Server));
    }

    [Fact]
    public void AFormatGenerationAboveTheReadersRefuses()
    {
        // Contracts 7.4: a reader whose generation is below the manifest's refuses, with no consumer
        // involvement at all, and a reader ABOVE it is always fine.
        ContentManifest ahead = Server() with { FormatGeneration = (uint)ContentPackFormat.Generation + 1 };
        Assert.Equal(ContentManifestCodec.ReasonFormatGeneration, Refusal(ContentManifestCodec.Encode(ahead), ContentManifestSide.Server));

        ContentManifest behind = Server() with { FormatGeneration = 0 };
        Assert.True(ContentManifestCodec.TryDecode(ContentManifestCodec.Encode(behind), ContentManifestSide.Server, out _, out _));
    }

    [Fact]
    public void ANonZeroReservedByteRefuses()
    {
        byte[] file = ContentManifestCodec.Encode(Server());
        file[7] = 1;

        Assert.Equal(ContentManifestCodec.ReasonReservedSet, Refusal(file, ContentManifestSide.Server));
    }

    [Fact]
    public void TrailingBytesRefuse()
    {
        byte[] file = [.. ContentManifestCodec.Encode(Server()), (byte)0];

        Assert.Equal(ContentManifestCodec.ReasonTrailingBytes, Refusal(file, ContentManifestSide.Server));
    }

    [Fact]
    public void EveryTruncationRefusesWithoutThrowing()
    {
        byte[] file = ContentManifestCodec.Encode(Server());

        for (int length = 0; length < file.Length; length++)
        {
            Assert.False(ContentManifestCodec.TryDecode(file.AsSpan(0, length), ContentManifestSide.Server, out _, out string? reason));
            Assert.False(string.IsNullOrEmpty(reason));
        }
    }

    [Fact]
    public void AManifestNamingNoTypeAndNoLanguageRoundTrips()
    {
        ContentManifest empty = Client() with { Types = [], Languages = [] };

        Assert.True(ContentManifestCodec.TryDecode(
            ContentManifestCodec.Encode(empty), ContentManifestSide.Client, out ContentManifest? decoded, out _));
        Assert.NotNull(decoded);
        Assert.Empty(decoded.Types);
        Assert.Empty(decoded.Languages);
    }

    // ---- The canonical text, spec 6.8 ----

    [Fact]
    public void TheCanonicalTextIsTheDeclaredLayout()
    {
        string expected = string.Concat(
            "kec/manifest/server/", ContentHash.SchemeVersion.ToString(CultureInfo.InvariantCulture), "\n",
            "47\n",
            ContentPackFormat.Generation.ToString(CultureInfo.InvariantCulture), "\n",
            "12\n",
            "9\n",
            "1 3:tag  1\n",
            "0 ", TagChunkHash, "\n",
            "2 4:item  2\n",
            "0 ", ItemChunkHash, "\n",
            "3 ", ItemChunkThreeHash, "\n",
            "64:", RuleHash, " \n",
            "5:en-US  ", EnglishHash, "\n",
            "5:fr-FR  ", FrenchHash, "\n");

        Assert.Equal(expected, ContentManifestText.Canonical(Server()));
    }

    [Fact]
    public void TheTwoSidesTakeTheirOwnSubDomainsAndNeverAgree()
    {
        ContentManifest server = Server();
        ContentManifest client = Client();

        Assert.StartsWith(ContentHash.ServerManifestDomain, ContentManifestText.Canonical(server), StringComparison.Ordinal);
        Assert.StartsWith(ContentHash.ClientManifestDomain, ContentManifestText.Canonical(client), StringComparison.Ordinal);
        Assert.Equal(ContentHash.OfServerManifest(ContentManifestText.Canonical(server)), ContentManifestText.Hash(server));
        Assert.Equal(ContentHash.OfClientManifest(ContentManifestText.Canonical(client)), ContentManifestText.Hash(client));

        // The same version, one side each, and never the same digest: the sub-domain alone guarantees it
        // even for a version whose two sides carry identical content.
        ContentManifest sameContent = client with { Side = ContentManifestSide.Server };
        Assert.NotEqual(ContentManifestText.Hash(client), ContentManifestText.Hash(sameContent));
        Assert.NotEqual(ContentManifestText.Hash(server), ContentManifestText.Hash(client));
    }

    [Fact]
    public void ATypeKeyCarryingASpaceCannotCollideWithTwoKeys()
    {
        // The length prefix is the whole reason. Without it "item stone" and the pair "item", "stone" would
        // flatten into the same run of characters.
        ContentManifest one = Client() with
        {
            Types = [new ManifestTypeEntry(1, "item stone", 4096, ContentVisibility.Client, [])],
        };
        ContentManifest two = Client() with
        {
            Types =
            [
                new ManifestTypeEntry(1, "item", 4096, ContentVisibility.Client, []),
                new ManifestTypeEntry(2, "stone", 4096, ContentVisibility.Client, []),
            ],
        };

        Assert.NotEqual(ContentManifestText.Canonical(one), ContentManifestText.Canonical(two));
        Assert.NotEqual(ContentManifestText.Hash(one), ContentManifestText.Hash(two));
    }

    [Fact]
    public void TheLanguageOrderIsOrdinalAndNeverCultureAware()
    {
        // Ordinal puts every upper-case letter before every lower-case one, and a culture-aware compare does
        // not, so this pair is ascending under one rule and descending under the other. The FILE reader and
        // the digest text have to agree on which, or a manifest one publisher wrote is one the next reader
        // refuses.
        ContentManifest ascending = Client() with
        {
            Languages = [new ManifestLanguageEntry("ZZ", EnglishHash), new ManifestLanguageEntry("aa", FrenchHash)],
        };

        Assert.True(ContentManifestCodec.TryDecode(
            ContentManifestCodec.Encode(ascending), ContentManifestSide.Client, out ContentManifest? decoded, out _));
        Assert.NotNull(decoded);
        Assert.Equal(["ZZ", "aa"], decoded.Languages.Select(l => l.Tag));

        ContentManifest descending = ascending with
        {
            Languages = [new ManifestLanguageEntry("aa", FrenchHash), new ManifestLanguageEntry("ZZ", EnglishHash)],
        };
        Assert.Equal(ContentManifestCodec.ReasonLanguageOrder, Refusal(ContentManifestCodec.Encode(descending), ContentManifestSide.Client));
        Assert.Equal(ContentManifestText.Canonical(ascending), ContentManifestText.Canonical(descending));
    }

    [Fact]
    public void TheCanonicalTextSortsWhateverOrderItIsHandedIn()
    {
        // Contracts 7.3 sorts collections before digesting, so two publishers that walked their registries
        // in different orders produce one digest. The FILE order is a separate rule the decoder enforces.
        ContentManifest scrambled = Server() with
        {
            Types = [Server().Types[1] with { Chunks = [.. Server().Types[1].Chunks.Reverse()] }, Server().Types[0]],
            Languages = [.. Server().Languages.Reverse()],
        };

        Assert.Equal(ContentManifestText.Canonical(Server()), ContentManifestText.Canonical(scrambled));
    }

    // ---- uncompressedBytes sits outside the hash, spec 7.4 ----

    [Fact]
    public void ThePerChunkSizeIsInTheFileAndNotInTheText()
    {
        ContentManifest resized = Client() with
        {
            Types = [new ManifestTypeEntry(1, "tag", 4096, ContentVisibility.Client, [new ManifestChunkEntry(0, 44_000, TagChunkHash)])],
        };

        Assert.NotEqual(ContentManifestCodec.Encode(Client()), ContentManifestCodec.Encode(resized));
        Assert.Equal(ContentManifestText.Canonical(Client()), ContentManifestText.Canonical(resized));
        Assert.Equal(ContentManifestText.Hash(Client()), ContentManifestText.Hash(resized));
    }

    [Fact]
    public void AChunkHeaderDeclaringADifferentSizeThanTheManifestIsRefused()
    {
        // The only thing binding the un-digested field to reality. The header here is built by hand at spec
        // 7.2's offsets rather than through a codec, because the binding is what is under test.
        byte[] header = new byte[ContentPackFormat.ChunkHeaderBytes];
        "KECC"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), ContentPackFormat.ChunkFormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 44);

        ContentManifest manifest = Client();
        var type = new ContentTypeId(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)));
        uint chunkIndex = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));

        Assert.True(manifest.TryMatchChunkHeader(type, chunkIndex, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28)), out string? agreed));
        Assert.Null(agreed);

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 45);
        Assert.False(manifest.TryMatchChunkHeader(type, chunkIndex, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28)), out string? reason));
        Assert.Equal(ContentManifest.ReasonChunkRangeMismatch, reason);
    }

    [Fact]
    public void AChunkTheManifestNeverNamedIsTheSameRefusal()
    {
        ContentManifest manifest = Client();

        Assert.False(manifest.TryMatchChunkHeader(new ContentTypeId(1), 9, 44, out string? unknownChunk));
        Assert.Equal(ContentManifest.ReasonChunkRangeMismatch, unknownChunk);

        Assert.False(manifest.TryMatchChunkHeader(new ContentTypeId(7), 0, 44, out string? unknownType));
        Assert.Equal(ContentManifest.ReasonChunkRangeMismatch, unknownType);
    }
}
