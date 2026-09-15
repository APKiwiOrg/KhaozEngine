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
        public StubRowCodec(params string[] writtenFields) => WrittenFields = writtenFields;

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

        Assert.Contains("0", error.Message, StringComparison.Ordinal);
        Assert.Empty(registry.ByTypeId);
    }

    [Fact]
    public void AGameCallerCannotRegisterIntoTheInstancesRange()
    {
        var registry = new ContentTypeRegistry();

        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Game, 300, "store"));

        Assert.Contains("Game", error.Message, StringComparison.Ordinal);
        Assert.Contains("1024", error.Message, StringComparison.Ordinal);
        Assert.Contains("65535", error.Message, StringComparison.Ordinal);
        Assert.Contains("300", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEngineCallerCannotRegisterIntoTheGameRange()
    {
        var registry = new ContentTypeRegistry();

        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 1024, "item"));

        Assert.Contains("Engine", error.Message, StringComparison.Ordinal);
        Assert.Contains("255", error.Message, StringComparison.Ordinal);
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

        Assert.Contains("2", error.Message, StringComparison.Ordinal);
        Assert.Single(registry.ByTypeId);
    }

    [Fact]
    public void TwoTypesSharingATypeKeyAreRefused()
    {
        var registry = new ContentTypeRegistry();
        Register(registry, ContentRegistrationBand.Engine, 2, "item");

        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Game, 1024, "item"));

        Assert.Contains("item", error.Message, StringComparison.Ordinal);
        Assert.Single(registry.ByTypeId);
    }

    [Theory]
    [InlineData(128)]
    [InlineData(255)]
    [InlineData(1000)]
    [InlineData(131072)]
    public void AChunkSlotCountOutsideThePowerOfTwoBandIsRefused(int chunkSlots)
    {
        var registry = new ContentTypeRegistry();

        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 2, "item", chunkSlots, maxRowBytes: 64));

        Assert.Contains(chunkSlots.ToString(CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.Contains("256", error.Message, StringComparison.Ordinal);
        Assert.Contains("65536", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMaxRowBytesAboveTheAbsoluteCeilingIsRefused()
    {
        var registry = new ContentTypeRegistry();

        ContentRegistrationException error = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 2, "item", chunkSlots: 256,
                maxRowBytes: ContentPackFormat.MaxContentRowBytes + 1));

        Assert.Contains(
            ContentPackFormat.MaxContentRowBytes.ToString(CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
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

        Assert.Contains("16384", error.Message, StringComparison.Ordinal);
        Assert.Contains("1024", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            ContentPackFormat.MaxChunkUncompressedBytes.ToString(CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);

        Register(registry, ContentRegistrationBand.Engine, 5, "loot_entry", chunkSlots: 16384, maxRowBytes: 512);
        Assert.True(registry.TryGet(new ContentTypeId(5), out _));
    }

    [Fact]
    public void ACodecWhoseWrittenFieldsDifferFromTheSchemaIsRefused()
    {
        var registry = new ContentTypeRegistry();

        ContentRegistrationException missing = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 2, "item",
                codec: new StubRowCodec("sort"), schema: Schema("sort", "max_stack")));

        Assert.Contains("max_stack", missing.Message, StringComparison.Ordinal);

        ContentRegistrationException extra = Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Engine, 2, "item",
                codec: new StubRowCodec("sort", "tradable"), schema: Schema("sort")));

        Assert.Contains("tradable", extra.Message, StringComparison.Ordinal);
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

        Assert.Contains("frozen", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(registry.ByTypeId);
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
        // Contracts 4.3: nothing anywhere derives an ordinal from registration order, because Ruinborne's
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
