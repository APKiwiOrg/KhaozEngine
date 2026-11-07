using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Registry;

/// <summary>
/// Registration under contracts 4.2 to 4.5 and 4.7: the reserved type id ranges of the three bands, the
/// chunk slot rule, the row cap and the chunk bound that relates them, the codec checked against the
/// schema, and the freeze at first pack load.
/// <para>
/// The registry is per instance and is NOT a static, which is why this class writes no process-global
/// state and needs no <c>DisableParallelization</c> collection (spec 2.6).
/// </para>
/// </summary>
public class ContentTypeRegistryTests
{
    sealed class StubRowCodec : IContentRowCodec
    {
        public StubRowCodec(params string[]? writtenFields) => WrittenFields = writtenFields!;

        public IReadOnlyList<string> WrittenFields { get; }

        public void Encode(ContentRow row, IBufferWriter<byte> destination) => throw new NotSupportedException();

        public bool TryDecode(ReadOnlySpan<byte> body, [MaybeNullWhen(false)] out ContentRow row, out string? reason)
            => throw new NotSupportedException();
    }

    static ContentFieldSchema Schema(params string[] names)
        => new(names.Select(n => new ContentFieldEntry(n, ContentFieldKind.Int, null, ContentVisibility.Client, false)).ToArray());

    static void Register(
        ContentTypeRegistry registry,
        ContentRegistrationBand band,
        ushort typeId,
        string typeKey,
        int chunkSlots = 4096,
        int maxRowBytes = ContentPackFormat.DefaultMaxRowBytes,
        IContentRowCodec? codec = null,
        ContentFieldSchema? schema = null)
        => registry.RegisterContentType(
            band,
            typeId,
            typeKey,
            codec ?? new StubRowCodec("sort"),
            validator: null,
            schema ?? Schema("sort"),
            ContentVisibility.Client,
            chunkSlots,
            maxRowBytes);

    [Fact]
    public void TypeIdZeroIsRefused()
    {
        var registry = new ContentTypeRegistry();

        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 0, "nothing"));

        Assert.Equal(ContentRegistrationRefusal.TypeIdReserved, error.Reason);
        Assert.Equal(new ContentTypeId(0), error.TypeId);
        Assert.Equal("nothing", error.TypeKey);
        Assert.Empty(registry.ByTypeId);
        Register(registry, ContentRegistrationBand.Engine, 1, "nothing");
    }

    [Theory]
    [InlineData(ContentRegistrationBand.Game, 300, 1024, 65535, 1024)]
    [InlineData(ContentRegistrationBand.Engine, 1024, 1, 255, 255)]
    [InlineData(ContentRegistrationBand.Instances, 255, 256, 1023, 256)]
    [InlineData(ContentRegistrationBand.Instances, 1024, 256, 1023, 1023)]
    public void ACallerCannotRegisterOutsideItsBand(
        ContentRegistrationBand band, int typeId, int minimum, int maximum, int controlId)
    {
        var registry = new ContentTypeRegistry();

        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, band, (ushort)typeId, "store"));

        Assert.Equal(ContentRegistrationRefusal.BandTypeIdOutsideRange, error.Reason);
        Assert.Equal(new ContentTypeId((ushort)typeId), error.TypeId);
        Assert.Equal("store", error.TypeKey);
        Assert.Equal(band, error.Band);
        Assert.Equal((ushort)minimum, error.MinimumTypeId);
        Assert.Equal((ushort)maximum, error.MaximumTypeId);
        Assert.Empty(registry.ByTypeId);
        Register(registry, band, (ushort)controlId, "store");
    }

    [Fact]
    public void AnUndefinedBandHasNoPermittedTypeIdRange()
    {
        var registry = new ContentTypeRegistry();

        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, (ContentRegistrationBand)99, 2, "item"));

        Assert.Equal(ContentRegistrationRefusal.BandUnrecognized, error.Reason);
        Assert.Equal(new ContentTypeId(2), error.TypeId);
        Assert.Equal("item", error.TypeKey);
        Assert.Equal((ContentRegistrationBand)99, error.Band);
        Assert.Null(error.MinimumTypeId);
        Assert.Null(error.MaximumTypeId);
        Assert.Empty(registry.ByTypeId);
        Register(registry, ContentRegistrationBand.Engine, 2, "item");
    }

    [Theory]
    [InlineData(ContentRegistrationBand.Engine, 1)]
    [InlineData(ContentRegistrationBand.Engine, 255)]
    [InlineData(ContentRegistrationBand.Instances, 256)]
    [InlineData(ContentRegistrationBand.Instances, 1023)]
    [InlineData(ContentRegistrationBand.Game, 1024)]
    [InlineData(ContentRegistrationBand.Game, 65535)]
    public void EachBandAcceptsItsOwnEdges(ContentRegistrationBand band, int typeId)
    {
        var registry = new ContentTypeRegistry();

        Register(registry, band, (ushort)typeId, "edge_" + typeId.ToString(CultureInfo.InvariantCulture));

        Assert.True(registry.TryGet(new ContentTypeId((ushort)typeId), out ContentTypeRegistration? registration));
        Assert.NotNull(registration);
        Assert.Equal(band, registration.Band);
    }

    [Fact]
    public void TheSameTypeIdTwiceIsRefused()
    {
        var registry = new ContentTypeRegistry();
        Register(registry, ContentRegistrationBand.Engine, 2, "item");

        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 2, "stat"));

        Assert.Equal(ContentRegistrationRefusal.TypeIdDuplicate, error.Reason);
        Assert.Equal(new ContentTypeId(2), error.TypeId);
        Assert.Equal("stat", error.TypeKey);
        Assert.Equal("item", error.ExistingTypeKey);
        Assert.Equal(new[] { (new ContentTypeId(2), "item") }, registry.ByTypeId.Select(r => (r.Type, r.TypeKey)));
        Register(registry, ContentRegistrationBand.Engine, 3, "stat");
    }

    [Fact]
    public void TwoTypesSharingATypeKeyAreRefused()
    {
        var registry = new ContentTypeRegistry();
        Register(registry, ContentRegistrationBand.Engine, 2, "item");

        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Game, 1024, "item"));

        Assert.Equal(ContentRegistrationRefusal.TypeKeyDuplicate, error.Reason);
        Assert.Equal(new ContentTypeId(1024), error.TypeId);
        Assert.Equal("item", error.TypeKey);
        Assert.Equal(new ContentTypeId(2), error.ExistingTypeId);
        Assert.Equal(new[] { (new ContentTypeId(2), "item") }, registry.ByTypeId.Select(r => (r.Type, r.TypeKey)));
        Register(registry, ContentRegistrationBand.Game, 1024, "store");
    }

    [Theory]
    [InlineData(128, ContentRegistrationRefusal.ChunkSlotsOutsideRange, 256)]
    [InlineData(255, ContentRegistrationRefusal.ChunkSlotsOutsideRange, 256)]
    [InlineData(1000, ContentRegistrationRefusal.ChunkSlotsNotPowerOfTwo, 1024)]
    [InlineData(131072, ContentRegistrationRefusal.ChunkSlotsOutsideRange, 65536)]
    [InlineData(65537, ContentRegistrationRefusal.ChunkSlotsOutsideRange, 65536)]
    public void AChunkSlotCountOutsideThePowerOfTwoBandIsRefused(
        int chunkSlots, ContentRegistrationRefusal reason, int controlSlots)
    {
        var registry = new ContentTypeRegistry();

        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 2, "item", chunkSlots, maxRowBytes: 64));

        Assert.Equal(reason, error.Reason);
        Assert.Equal(new ContentTypeId(2), error.TypeId);
        Assert.Equal("item", error.TypeKey);
        Assert.Equal(chunkSlots, error.ActualSlots);
        Assert.Equal(256, error.MinimumSlots);
        Assert.Equal(65536, error.MaximumSlots);
        Assert.Empty(registry.ByTypeId);
        Register(registry, ContentRegistrationBand.Engine, 2, "item", controlSlots, maxRowBytes: 64);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(4097, 4096)]
    public void ARowCapOutsideTheAbsoluteRangeIsRefused(int maxRowBytes, int controlBytes)
    {
        var registry = new ContentTypeRegistry();

        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 2, "item", chunkSlots: 256,
                maxRowBytes: maxRowBytes));

        Assert.Equal(ContentRegistrationRefusal.RowBytesOutsideRange, error.Reason);
        Assert.Equal(new ContentTypeId(2), error.TypeId);
        Assert.Equal("item", error.TypeKey);
        Assert.Equal((long)maxRowBytes, error.ActualBytes);
        Assert.Equal(1L, error.MinimumBytes);
        Assert.Equal(4096L, error.MaximumBytes);
        Assert.Empty(registry.ByTypeId);
        Register(registry, ContentRegistrationBand.Engine, 2, "item", chunkSlots: 256, maxRowBytes: controlBytes);
    }

    [Fact]
    public void ARegistrationThatWouldBuildAnUnloadableChunkIsRefused()
    {
        // Spec 7.1: chunkSlots * (maxRowBytes + 8) + 36 <= MaxChunkUncompressedBytes. loot_entry at 16,384
        // slots and the 1,024 default is 16.9 MB, which is over, and is exactly the case the bound exists
        // for. It registers once it declares maxRowBytes: 512, which is what section 3.1 has it do.
        var registry = new ContentTypeRegistry();

        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 5, "loot_entry", chunkSlots: 16384));

        Assert.Equal(ContentRegistrationRefusal.ChunkBytesExceeded, error.Reason);
        Assert.Equal(new ContentTypeId(5), error.TypeId);
        Assert.Equal("loot_entry", error.TypeKey);
        Assert.Equal(16384, error.ChunkSlots);
        Assert.Equal(1024, error.MaximumRowBytes);
        Assert.Equal(16908324L, error.ActualBytes);
        Assert.Equal(16777216L, error.MaximumBytes);
        Assert.Empty(registry.ByTypeId);

        // At 16,384 slots the largest admitted row cap is 1,015: (1,015 + 8) * 16,384 + 36
        // is 16,760,868 bytes. The next cap is 16,777,252, already over 16 MiB.
        Register(registry, ContentRegistrationBand.Engine, 5, "loot_entry", chunkSlots: 16384, maxRowBytes: 1015);
        Assert.True(registry.TryGet(new ContentTypeId(5), out _));
        Register(new ContentTypeRegistry(), ContentRegistrationBand.Engine, 5, "loot_entry",
            chunkSlots: 16384, maxRowBytes: 1014);
    }

    [Fact]
    public void ACodecWhoseWrittenFieldsDifferFromTheSchemaIsRefused()
    {
        var registry = new ContentTypeRegistry();

        ContentRegistrationException missing = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 2, "item",
                codec: new StubRowCodec("sort"), schema: Schema("sort", "max_stack")));

        Assert.Equal(ContentRegistrationRefusal.CodecSchemaFieldsMismatch, missing.Reason);
        Assert.Equal(new ContentTypeId(2), missing.TypeId);
        Assert.Equal("item", missing.TypeKey);
        Assert.Equal(new[] { "max_stack" }, missing.MissingFields);
        Assert.NotNull(missing.ExtraFields);
        Assert.Empty(missing.ExtraFields);
        Assert.Empty(registry.ByTypeId);

        ContentRegistrationException extra = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 2, "item",
                codec: new StubRowCodec("sort", "tradable"), schema: Schema("sort")));

        Assert.Equal(ContentRegistrationRefusal.CodecSchemaFieldsMismatch, extra.Reason);
        Assert.Equal(new ContentTypeId(2), extra.TypeId);
        Assert.Equal("item", extra.TypeKey);
        Assert.NotNull(extra.MissingFields);
        Assert.Empty(extra.MissingFields);
        Assert.Equal(new[] { "tradable" }, extra.ExtraFields);
        Assert.Empty(registry.ByTypeId);
        Register(registry, ContentRegistrationBand.Engine, 2, "item");
    }

    [Fact]
    public void ACodecNeedNotWriteADerivedMarkerField()
    {
        // A localized text key carries no value in the row and no bytes in the chunk (contracts 4.7), so a
        // codec that wrote one would be writing nothing. The set comparison skips markers on both sides.
        var registry = new ContentTypeRegistry();
        var schema = new ContentFieldSchema(new[]
        {
            new ContentFieldEntry("name", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true),
            new ContentFieldEntry("sort", ContentFieldKind.Int, null, ContentVisibility.Client, false),
        });

        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 1, "tag",
                codec: new StubRowCodec("sort", "name"), schema: schema));

        Assert.Equal(ContentRegistrationRefusal.CodecWritesDerivedMarkers, error.Reason);
        Assert.Equal(new ContentTypeId(1), error.TypeId);
        Assert.Equal("tag", error.TypeKey);
        Assert.Equal(new[] { "name" }, error.FieldNames);
        Assert.Empty(registry.ByTypeId);
        Register(registry, ContentRegistrationBand.Engine, 1, "tag", codec: new StubRowCodec("sort"), schema: schema);
        Assert.True(registry.TryGetByKey("tag", out _));
    }

    [Fact]
    public void RegistrationAfterTheFreezeThrows()
    {
        var registry = new ContentTypeRegistry();
        Register(registry, ContentRegistrationBand.Engine, 2, "item");
        registry.Freeze();

        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 3, "stat"));

        Assert.Equal(ContentRegistrationRefusal.RegistryFrozen, error.Reason);
        Assert.Equal(new ContentTypeId(3), error.TypeId);
        Assert.Equal("stat", error.TypeKey);
        Assert.Equal(new[] { (new ContentTypeId(2), "item") }, registry.ByTypeId.Select(r => (r.Type, r.TypeKey)));
        var unfrozen = new ContentTypeRegistry();
        Register(unfrozen, ContentRegistrationBand.Engine, 2, "item");
        Register(unfrozen, ContentRegistrationBand.Engine, 3, "stat");
    }

    [Fact]
    public void AnEmptyTypeKeyIsRefused()
    {
        var registry = new ContentTypeRegistry();
        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 2, string.Empty));

        Assert.Equal(ContentRegistrationRefusal.TypeKeyEmpty, error.Reason);
        Assert.Equal(new ContentTypeId(2), error.TypeId);
        Assert.Equal(string.Empty, error.TypeKey);
        Assert.Empty(registry.ByTypeId);
        Register(registry, ContentRegistrationBand.Engine, 2, "item");
    }

    [Fact]
    public void ACodecMustDeclareItsWrittenFieldList()
    {
        var registry = new ContentTypeRegistry();
        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 2, "item", codec: new StubRowCodec(null)));

        Assert.Equal(ContentRegistrationRefusal.CodecWrittenFieldsMissing, error.Reason);
        Assert.Equal(new ContentTypeId(2), error.TypeId);
        Assert.Equal("item", error.TypeKey);
        Assert.Empty(registry.ByTypeId);
        Register(registry, ContentRegistrationBand.Engine, 2, "item");
    }

    [Fact]
    public void RefusalFieldListsOwnReadOnlySnapshots()
    {
        string[] source = ["sort"];
        var error = new ContentRegistrationException(ContentRegistrationRefusal.CodecSchemaFieldsMismatch, "detail")
        {
            MissingFields = source,
            ExtraFields = source,
            FieldNames = source,
        };
        source[0] = "changed";

        AssertOwned(error.MissingFields);
        AssertOwned(error.ExtraFields);
        AssertOwned(error.FieldNames);
    }

    static void AssertOwned(IReadOnlyList<string>? fields)
    {
        Assert.NotNull(fields);
        Assert.Equal(new[] { "sort" }, fields);
        if (fields is IList<string> mutable)
        {
            Assert.Throws<NotSupportedException>(() => mutable[0] = "changed");
            Assert.Equal(new[] { "sort" }, fields);
        }
    }

    [Fact]
    public void TheFreezeIsIdempotentAndBothLookupsStillAnswer()
    {
        var registry = new ContentTypeRegistry();
        Register(registry, ContentRegistrationBand.Engine, 2, "item");
        Register(registry, ContentRegistrationBand.Game, 1024, "store");

        Assert.False(registry.IsFrozen);
        registry.Freeze();
        registry.Freeze();
        registry.Freeze();
        Assert.True(registry.IsFrozen);

        Assert.True(registry.TryGet(new ContentTypeId(2), out ContentTypeRegistration? byId));
        Assert.NotNull(byId);
        Assert.Equal("item", byId.TypeKey);
        Assert.True(registry.TryGetByKey("store", out ContentTypeRegistration? byKey));
        Assert.NotNull(byKey);
        Assert.Equal(1024, byKey.Type.Value);
        Assert.False(registry.TryGet(new ContentTypeId(3), out _));
        Assert.False(registry.TryGetByKey("Store", out _));
        Assert.False(registry.TryGetByKey("missing", out _));
    }

    [Fact]
    public void TheRegistrationCarriesEveryDeclaredValue()
    {
        var registry = new ContentTypeRegistry();
        var codec = new StubRowCodec("sort");
        ContentFieldSchema schema = Schema("sort");

        registry.RegisterContentType(
            ContentRegistrationBand.Instances,
            typeId: 257,
            typeKey: "rarity_rule",
            codec: codec,
            validator: null,
            schema: schema,
            defaultVisibility: ContentVisibility.ServerOnly,
            chunkSlots: 256,
            maxRowBytes: 512,
            maxDefinitionId: 255,
            loadIndex: null);

        Assert.True(registry.TryGet(new ContentTypeId(257), out ContentTypeRegistration? registration));
        Assert.NotNull(registration);
        Assert.Equal(ContentRegistrationBand.Instances, registration.Band);
        Assert.Equal(257, registration.Type.Value);
        Assert.Equal("rarity_rule", registration.TypeKey);
        Assert.Same(codec, registration.Codec);
        Assert.Null(registration.Validator);
        Assert.Same(schema, registration.Schema);
        Assert.Equal(ContentVisibility.ServerOnly, registration.DefaultVisibility);
        Assert.Equal(256, registration.ChunkSlots);
        Assert.Equal(512, registration.MaxRowBytes);
        Assert.Equal(255, registration.MaxDefinitionId);
        Assert.Null(registration.LoadIndex);
    }

    [Fact]
    public void RegistrationOrderIsNotAnOrdinal()
    {
        // Contracts 4.3: nothing anywhere derives an ordinal from registration order, because the reference consumer's
        // wire index is a list POSITION and differs between its two sources for the same five items.
        (ContentRegistrationBand Band, ushort Id, string Key)[] types =
        {
            (ContentRegistrationBand.Engine, 1, "tag"),
            (ContentRegistrationBand.Engine, 2, "item"),
            (ContentRegistrationBand.Instances, 256, "mod"),
            (ContentRegistrationBand.Game, 1024, "store"),
            (ContentRegistrationBand.Game, 1035, "equip_stat_line"),
        };

        var forward = new ContentTypeRegistry();
        foreach ((ContentRegistrationBand band, ushort id, string key) in types)
        {
            Register(forward, band, id, key);
        }

        var shuffled = new ContentTypeRegistry();
        foreach ((ContentRegistrationBand band, ushort id, string key) in new[] { types[3], types[0], types[4], types[2], types[1] })
        {
            Register(shuffled, band, id, key);
        }

        Assert.Equal(
            forward.ByTypeId.Select(r => (r.Type.Value, r.TypeKey)),
            shuffled.ByTypeId.Select(r => (r.Type.Value, r.TypeKey)));
        Assert.Equal(new ushort[] { 1, 2, 256, 1024, 1035 }, forward.ByTypeId.Select(r => r.Type.Value));
    }

    [Fact]
    public void TheThreeRangePredicatesFollowTheReservedRanges()
    {
        Assert.False(new ContentTypeId(0).IsEngine);
        Assert.True(new ContentTypeId(1).IsEngine);
        Assert.True(new ContentTypeId(255).IsEngine);
        Assert.False(new ContentTypeId(256).IsEngine);
        Assert.True(new ContentTypeId(256).IsInstances);
        Assert.True(new ContentTypeId(1023).IsInstances);
        Assert.False(new ContentTypeId(1024).IsInstances);
        Assert.True(new ContentTypeId(1024).IsGame);
        Assert.True(new ContentTypeId(65535).IsGame);
    }
}
