using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// The registration hook of spec 9.4 and 3.6, and the four rules that make it safe: boot step 7b runs after
/// the engine's four and before the validator, in TYPE ID ORDER, an index may read another type's ROWS and
/// may not read another index, and one that fails throws rather than leaving a partial index behind.
/// </summary>
public class ContentLoadIndexTests
{
    [Fact]
    public void A_type_registers_its_index_beside_its_codec_and_gets_it_back_typed()
    {
        var index = new RecordingIndex(new ContentTypeId(1024));
        ContentRuntime runtime = Runtime(index);

        // Nothing has run it yet, so the accessor refuses rather than handing back an empty index.
        Assert.False(runtime.LoadIndexesBuilt);
        Assert.Throws<ContentLoadIndexException>(
            () => runtime.TryGetLoadIndex<RecordingIndex>(new ContentTypeId(1024), out RecordingIndex? _));

        runtime.BuildLoadIndexes();

        Assert.True(runtime.LoadIndexesBuilt);
        Assert.True(runtime.TryGetLoadIndex<RecordingIndex>(new ContentTypeId(1024), out RecordingIndex? built));
        Assert.Same(index, built);
        Assert.Equal(1, built!.BuildCount);

        // A type that registered none, and a type that is not registered at all, are both a plain miss.
        Assert.False(runtime.TryGetLoadIndex<RecordingIndex>(CatalogSnapshotFixtures.ItemType, out RecordingIndex? _));
        Assert.False(runtime.TryGetLoadIndex<RecordingIndex>(new ContentTypeId(2048), out RecordingIndex? _));
    }

    [Fact]
    public void It_runs_after_the_engines_four_and_before_the_validator()
    {
        // After the four, because an index over rows will want the key lookup and the tag lists. The runtime
        // builds them in its constructor, so an index cannot run before them even by accident.
        var index = new RecordingIndex(new ContentTypeId(1024));
        ContentRuntime runtime = Runtime(index);

        runtime.BuildLoadIndexes();

        Assert.True(index.SawTagIndex);
        Assert.True(index.SawKeyIndex);

        // Before the validator: nothing here runs it, and the boot's own order is task 23's. What this pins
        // is that the index build is a step of its own that a caller sequences, rather than something the
        // validator or a first lookup triggers.
        Assert.Equal(1, index.BuildCount);
    }

    [Fact]
    public void It_runs_in_type_id_order_whatever_order_the_types_registered_in()
    {
        var order = new List<int>();
        var high = new RecordingIndex(new ContentTypeId(2048), order);
        var game = new RecordingIndex(new ContentTypeId(1024), order);
        var instances = new RecordingIndex(new ContentTypeId(300), order);

        // Registered in DESCENDING id order, which is the order a host with no discipline would produce.
        ContentTypeRegistry registry = CatalogSnapshotFixtures.Registry();
        Register(registry, ContentRegistrationBand.Game, 2048, "recipe", high);
        Register(registry, ContentRegistrationBand.Game, 1024, "store", game);
        Register(registry, ContentRegistrationBand.Instances, 300, "affix", instances);

        ContentRuntime runtime = ContentRuntime.FromSnapshot(new ContentSnapshotBuilder(registry).Build(), registry);
        runtime.BuildLoadIndexes();

        // Ascending by type id, so an index over engine rows is built before one over a later band's and the
        // order never depends on what a host registered first.
        Assert.Equal(new[] { 300, 1024, 2048 }, order);
    }

    [Fact]
    public void An_index_may_read_another_types_rows_through_the_snapshot()
    {
        var index = new ItemReadingIndex(new ContentTypeId(1024));
        ContentRuntime runtime = Runtime(index);

        runtime.BuildLoadIndexes();

        // It walked the ENGINE item type's rows from a game type's index, which is the whole reason Build
        // takes the snapshot rather than just its own type's rows.
        Assert.Equal(CatalogRuntimeFixtures.ItemCount, index.ItemsSeen);
        Assert.Equal(2, index.IdForKey);
    }

    [Fact]
    public void An_index_may_not_read_another_index()
    {
        var reader = new IndexReadingIndex(new ContentTypeId(1024), new ContentTypeId(300));
        var other = new RecordingIndex(new ContentTypeId(300));

        ContentTypeRegistry registry = CatalogSnapshotFixtures.Registry();
        Register(registry, ContentRegistrationBand.Instances, 300, "affix", other);
        Register(registry, ContentRegistrationBand.Game, 1024, "store", reader);
        ContentRuntime runtime = ContentRuntime.FromSnapshot(
            new ContentSnapshotBuilder(registry).Build(), registry);

        // Type 300 is built first and is finished by the time 1024 runs, so ORDER is not what stops this.
        // The accessor refuses for the whole of step 7b, which is what keeps a registration order from
        // becoming load bearing in a way the registration list cannot express.
        var failure = Assert.Throws<ContentLoadIndexException>(runtime.BuildLoadIndexes);

        Assert.Equal(1024, failure.Type.Value);
        Assert.Equal("store", failure.TypeKey);
        Assert.IsType<ContentLoadIndexException>(failure.InnerException);
        Assert.False(runtime.LoadIndexesBuilt);
    }

    [Fact]
    public void An_index_that_throws_fails_the_boot_closed_with_its_type_named()
    {
        var failing = new ThrowingIndex(new ContentTypeId(1024));
        var healthy = new RecordingIndex(new ContentTypeId(300));

        ContentTypeRegistry registry = CatalogSnapshotFixtures.Registry();
        Register(registry, ContentRegistrationBand.Instances, 300, "affix", healthy);
        Register(registry, ContentRegistrationBand.Game, 1024, "store", failing);
        ContentRuntime runtime = ContentRuntime.FromSnapshot(
            new ContentSnapshotBuilder(registry).Build(), registry);

        var failure = Assert.Throws<ContentLoadIndexException>(runtime.BuildLoadIndexes);

        // The message is what spec 9.6's exit line is built from, so it names the key and the id, and the
        // index's own exception is kept rather than flattened.
        Assert.Contains("store", failure.Message, StringComparison.Ordinal);
        Assert.Contains("1024", failure.Message, StringComparison.Ordinal);
        Assert.Contains("the store rows disagree", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1024, failure.Type.Value);
        Assert.Equal("store", failure.TypeKey);
        Assert.IsType<InvalidOperationException>(failure.InnerException);

        // Fail CLOSED: no partial set is published and the accessor still refuses, so nothing downstream can
        // read a half-built index and get plausible wrong numbers.
        Assert.False(runtime.LoadIndexesBuilt);
        Assert.Throws<ContentLoadIndexException>(
            () => runtime.TryGetLoadIndex<RecordingIndex>(new ContentTypeId(300), out RecordingIndex? _));
    }

    [Fact]
    public void Step_7b_runs_once_and_a_second_call_is_refused()
    {
        var index = new RecordingIndex(new ContentTypeId(1024));
        ContentRuntime runtime = Runtime(index);

        runtime.BuildLoadIndexes();

        Assert.Throws<ContentLoadIndexException>(runtime.BuildLoadIndexes);
        Assert.Equal(1, index.BuildCount);
    }

    [Fact]
    public void One_index_per_type_at_most_because_a_type_registers_once()
    {
        ContentTypeRegistry registry = CatalogSnapshotFixtures.Registry();
        Register(registry, ContentRegistrationBand.Game, 1024, "store", new RecordingIndex(new ContentTypeId(1024)));

        Assert.Throws<ContentRegistrationException>(
            () => Register(registry, ContentRegistrationBand.Game, 1024, "store_again", new RecordingIndex(new ContentTypeId(1024))));
        Assert.True(registry.TryGet(new ContentTypeId(1024), out ContentTypeRegistration? registration));
        Assert.NotNull(registration.LoadIndex);
    }

    [Fact]
    public void A_registered_index_runs_even_when_the_version_carries_no_row_of_its_type()
    {
        // The hook is registration driven rather than snapshot driven, so a type behind an empty table still
        // gets to build whatever it derives, and a boot cannot silently skip one.
        var index = new RecordingIndex(new ContentTypeId(1024));
        ContentRuntime runtime = Runtime(index);

        Assert.Empty(runtime.Rows(new ContentTypeId(1024)));

        runtime.BuildLoadIndexes();

        Assert.Equal(1, index.BuildCount);
    }

    [Fact]
    public void A_version_with_no_registered_index_builds_step_7b_and_does_nothing()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);

        runtime.BuildLoadIndexes();

        Assert.True(runtime.LoadIndexesBuilt);
        Assert.False(runtime.TryGetLoadIndex<RecordingIndex>(CatalogSnapshotFixtures.ItemType, out RecordingIndex? _));
    }

    static ContentRuntime Runtime(IContentLoadIndex index)
    {
        ContentSnapshot snapshot = CatalogRuntimeFixtures.Snapshot(out ContentTypeRegistry registry);
        Register(registry, ContentRegistrationBand.Game, index.Type.Value, "store", index);
        return ContentRuntime.FromSnapshot(snapshot, registry);
    }

    static void Register(
        ContentTypeRegistry registry,
        ContentRegistrationBand band,
        ushort typeId,
        string typeKey,
        IContentLoadIndex index)
    {
        var schema = new ContentFieldSchema(
            [new ContentFieldEntry("rate", ContentFieldKind.Int, null, ContentVisibility.ServerOnly, true)]);

        registry.RegisterContentType(
            band,
            typeId,
            typeKey,
            new TestCodec(new ContentTypeId(typeId), schema),
            validator: null,
            schema,
            ContentVisibility.ServerOnly,
            chunkSlots: 256,
            maxRowBytes: ContentPackFormat.DefaultMaxRowBytes,
            maxDefinitionId: null,
            loadIndex: index);
    }

    sealed class TestCodec(ContentTypeId type, ContentFieldSchema schema) : ContentRowCodecBase(type, schema);

    /// <summary>An index that records that it ran, in the order it ran, and what it could see when it did.</summary>
    internal sealed class RecordingIndex(ContentTypeId type, List<int>? order = null) : IContentLoadIndex
    {
        public ContentTypeId Type { get; } = type;

        public int BuildCount { get; private set; }

        public bool SawTagIndex { get; private set; }

        public bool SawKeyIndex { get; private set; }

        public void Build(IContentSnapshot snapshot)
        {
            BuildCount++;
            order?.Add(Type.Value);

            if (snapshot is ContentRuntime runtime)
            {
                SawTagIndex = !runtime.Indexes.Tags
                    .Ids(CatalogSnapshotFixtures.ItemType, CatalogRuntimeFixtures.SwordTag).IsEmpty;
                SawKeyIndex = runtime.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey("item_2"), out _);
            }
        }
    }

    /// <summary>An index that reads another TYPE's rows, which spec 9.4 allows.</summary>
    sealed class ItemReadingIndex(ContentTypeId type) : IContentLoadIndex
    {
        public ContentTypeId Type { get; } = type;

        public int ItemsSeen { get; private set; }

        public int IdForKey { get; private set; }

        public void Build(IContentSnapshot snapshot)
        {
            ItemsSeen = snapshot.Rows(CatalogSnapshotFixtures.ItemType).Count;
            IdForKey = snapshot.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey("item_2"), out int id)
                ? id
                : 0;
        }
    }

    /// <summary>An index that reads another INDEX, which spec 9.4 refuses.</summary>
    sealed class IndexReadingIndex(ContentTypeId type, ContentTypeId other) : IContentLoadIndex
    {
        public ContentTypeId Type { get; } = type;

        public void Build(IContentSnapshot snapshot)
        {
            var runtime = (ContentRuntime)snapshot;
            runtime.TryGetLoadIndex<RecordingIndex>(other, out RecordingIndex? _);
        }
    }

    /// <summary>An index that cannot build what it was asked for.</summary>
    sealed class ThrowingIndex(ContentTypeId type) : IContentLoadIndex
    {
        public ContentTypeId Type { get; } = type;

        public void Build(IContentSnapshot snapshot)
        {
            _ = snapshot;
            throw new InvalidOperationException("the store rows disagree with the item rows");
        }
    }
}
