using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// The loaded active version of spec 9.1 to 9.3 and 9.7: the per-type arrays, the lookup that is one array
/// read and a span slice, the open-addressed key index, and the atomically swapped holder.
/// <para>
/// Joins <c>AllocSensitive</c> because the lookup budget reads
/// <c>GC.GetAllocatedBytesForCurrentThread</c>.
/// </para>
/// </summary>
[Collection("AllocSensitive")]
public class ContentRuntimeTests
{
    [Fact]
    public void Offsets_is_sized_to_the_highest_live_id_plus_one_and_not_to_the_chunk_slot_sum()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out ContentTypeRegistry registry);

        Assert.True(runtime.TryGetTable(CatalogSnapshotFixtures.ItemType, out ContentTypeTable? items));

        // The fixture's highest item id is 35 and the item type takes 1,024 slots a chunk. A chunk slot is a
        // TRANSPORT unit and the runtime does not inherit its sparseness, so the table is 36 entries.
        Assert.Equal(1024, CatalogSnapshotFixtures.Registration(registry, EngineContentTypes.ItemTypeKey).ChunkSlots);
        Assert.Equal(35, CatalogRuntimeFixtures.HighestItemId);
        Assert.Equal(36, items.Offsets.Length);
        Assert.Equal(36, items.Lengths.Length);
        Assert.Equal(36, items.RowIndex.Length);
        Assert.Single(items.RetiredBits);
    }

    [Fact]
    public void A_sparse_family_block_sizes_the_table_to_its_top_row_and_no_further()
    {
        // Spec 9.1's own worked case: a family block based at 65,536 with a handful of members. The table
        // covers the id space up to the highest row and stops, whatever the chunk boundaries were.
        var registry = CatalogSnapshotFixtures.Registry();
        var builder = new ContentSnapshotBuilder(registry).WithIdentity(1, new string('a', 64));
        for (int i = 0; i < 40; i++)
        {
            ContentRow row = CatalogSnapshotFixtures.ItemRow(65_536 + i, "sword_" + i, true, 1, 0, 0);
            builder.AddRow(row, CatalogSnapshotFixtures.Body(registry, EngineContentTypes.ItemTypeKey, row));
        }

        ContentRuntime runtime = ContentRuntime.FromSnapshot(builder.Build(), registry);

        Assert.True(runtime.TryGetTable(CatalogSnapshotFixtures.ItemType, out ContentTypeTable? items));
        Assert.Equal(65_576, items.Offsets.Length);
        Assert.Equal(40, items.RowCount);
        Assert.True(runtime.HasRow(CatalogSnapshotFixtures.ItemType, 65_536));
        Assert.False(runtime.HasRow(CatalogSnapshotFixtures.ItemType, 65_535));
    }

    [Fact]
    public void A_lookup_is_one_offset_read_and_a_span_slice_of_the_shared_bodies_blob()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);
        Assert.True(runtime.TryGetTable(CatalogSnapshotFixtures.ItemType, out ContentTypeTable? items));

        ReadOnlySpan<byte> body = runtime.Body(CatalogSnapshotFixtures.ItemType, 2);

        Assert.False(body.IsEmpty);
        Assert.True(body.Overlaps(items.Bodies));
        Assert.True(body.SequenceEqual(items.Bodies.AsSpan(items.Offsets[2], items.Lengths[2])));
        Assert.Equal(items.Lengths[2], body.Length);

        // An id the version does not carry answers empty rather than throwing, and so does an id past the
        // end of the table, which is what a stale reference into a rolled-back version looks like.
        Assert.True(runtime.Body(CatalogSnapshotFixtures.ItemType, 3).IsEmpty);
        Assert.True(runtime.Body(CatalogSnapshotFixtures.ItemType, 1_000_000).IsEmpty);
        Assert.True(runtime.Body(new ContentTypeId(1024), 2).IsEmpty);
    }

    [Fact]
    public void The_table_holds_no_dictionary_no_lock_and_no_keys_array()
    {
        // Spec 9.1 states the shape as a negative: no dictionary and no lock on the lookup path, and no Keys
        // array because the key blob IS Bodies. A field of the wrong kind is how all three come back.
        FieldInfo[] fields = typeof(ContentTypeTable)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotEmpty(fields);
        foreach (FieldInfo field in fields)
        {
            Type type = field.FieldType;
            Assert.False(
                type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>),
                $"{field.Name} is a dictionary, and a lookup is an array read plus a span slice");
            Assert.True(
                type != typeof(object) && type != typeof(SemaphoreSlim) && type != typeof(Lock),
                $"{field.Name} looks like a lock, and the swap of spec 9.7 is why there is none");
            Assert.True(
                type != typeof(ContentKey[]) && type != typeof(string[]),
                $"{field.Name} is a second copy of the keys, which already open every row body");
            Assert.True(field.IsInitOnly, $"{field.Name} is writable, and a loaded table is immutable");
        }

        Assert.DoesNotContain(
            typeof(ContentTypeTable).GetProperties(BindingFlags.Instance | BindingFlags.NonPublic
                | BindingFlags.Public | BindingFlags.DeclaredOnly),
            static p => string.Equals(p.Name, "Keys", StringComparison.Ordinal));
    }

    [Fact]
    public void The_lookup_path_allocates_nothing()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);
        byte[] key = Encoding.UTF8.GetBytes("item_2");

        long warm = 0;
        for (int i = 0; i < 64; i++)
        {
            warm += runtime.Body(CatalogSnapshotFixtures.ItemType, 2).Length;
            warm += runtime.TryGetId(CatalogSnapshotFixtures.ItemType, key, out int id) ? id : 0;
            warm += runtime.TryGetItem(2, out ItemRow row) ? row.MaxStack : 0;
            warm += runtime.TryGetRow(CatalogSnapshotFixtures.ItemType, 2, out _) ? 1 : 0;
            warm += runtime.IsRetired(CatalogSnapshotFixtures.ItemType, 2) ? 1 : 0;
        }

        Assert.True(warm > 0);

        CatalogAllocAssert.NoPerCallAllocation("the loaded runtime's lookup path", () =>
        {
            long total = 0;
            for (int i = 0; i < 1000; i++)
            {
                total += runtime.Body(CatalogSnapshotFixtures.ItemType, 2).Length;
                total += runtime.TryGetId(CatalogSnapshotFixtures.ItemType, key, out int id) ? id : 0;
                total += runtime.TryGetItem(2, out ItemRow row) ? row.MaxStack : 0;
                total += runtime.TryGetRow(CatalogSnapshotFixtures.ItemType, 2, out _) ? 1 : 0;
                total += runtime.IsRetired(CatalogSnapshotFixtures.ItemType, 2) ? 1 : 0;
                total += runtime.Rows(CatalogSnapshotFixtures.ItemType).Count;
            }

            Assert.True(total > 0);
        });
    }

    [Fact]
    public void KeyIds_is_open_addressed_with_zero_meaning_empty()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);
        Assert.True(runtime.TryGetTable(CatalogSnapshotFixtures.ItemType, out ContentTypeTable? items));

        int[] buckets = items.KeyIds;

        // A power of two of at least twice the row count, so a probe stays at one cache line on average.
        Assert.True(buckets.Length >= items.RowCount * 2);
        Assert.Equal(0, buckets.Length & (buckets.Length - 1));

        int occupied = 0;
        foreach (int id in buckets)
        {
            if (id == 0)
            {
                continue;
            }

            // 0 is not a legal definition id (contracts 5.1), which is what makes an empty bucket
            // unambiguous without a parallel occupancy bitset.
            occupied++;
            Assert.True(items.HasRow(id));
        }

        Assert.Equal(items.RowCount, occupied);
    }

    [Fact]
    public void A_probe_compares_the_candidates_key_slice_ordinally()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);

        foreach (ContentRow row in runtime.Rows(CatalogSnapshotFixtures.ItemType))
        {
            Assert.True(runtime.TryGetId(CatalogSnapshotFixtures.ItemType, row.Key.Utf8, out int id));
            Assert.Equal(row.Id, id);

            // The key handed back is a SLICE of the loaded bodies rather than a copy, which is the whole
            // reason there is no Keys array to compare against.
            ContentKey loaded = runtime.Key(CatalogSnapshotFixtures.ItemType, row.Id);
            Assert.True(loaded.Utf8.Overlaps(runtime.Body(CatalogSnapshotFixtures.ItemType, row.Id)));
            Assert.True(loaded.Equals(row.Key));
        }

        // A key that is a PREFIX of a live key is a miss, which a length-blind compare would answer wrong,
        // and so is a key differing only in case, because every comparison here is ordinal bytes.
        Assert.False(runtime.TryGetId(CatalogSnapshotFixtures.ItemType, Encoding.UTF8.GetBytes("item_"), out _));
        Assert.False(runtime.TryGetId(CatalogSnapshotFixtures.ItemType, Encoding.UTF8.GetBytes("ITEM_2"), out _));
        Assert.False(runtime.TryGetId(CatalogSnapshotFixtures.ItemType, ReadOnlySpan<byte>.Empty, out _));
        Assert.True(runtime.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey("item_2"), out int found));
        Assert.Equal(2, found);
    }

    [Fact]
    public void A_colliding_bucket_walks_linearly_rather_than_answering_the_first_candidate()
    {
        // Every key in a table small enough to have two buckets collides on one of them, so the linear walk
        // is the only thing that can tell these four apart.
        var registry = CatalogSnapshotFixtures.Registry();
        var builder = new ContentSnapshotBuilder(registry).WithIdentity(1, new string('c', 64));
        string[] keys = ["alpha", "beta", "gamma", "delta"];
        for (int i = 0; i < keys.Length; i++)
        {
            ContentRow row = CatalogSnapshotFixtures.ItemRow(i + 1, keys[i], false, 1, 0, 0);
            builder.AddRow(row, CatalogSnapshotFixtures.Body(registry, EngineContentTypes.ItemTypeKey, row));
        }

        ContentRuntime runtime = ContentRuntime.FromSnapshot(builder.Build(), registry);

        for (int i = 0; i < keys.Length; i++)
        {
            Assert.True(runtime.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey(keys[i]), out int id));
            Assert.Equal(i + 1, id);
        }

        Assert.False(runtime.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey("epsilon"), out _));
    }

    [Fact]
    public void The_runtime_answers_every_member_of_the_read_seam_over_the_tables()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);
        IContentSnapshot seam = runtime;

        Assert.Equal(7, seam.VersionNumber);
        Assert.Equal(7, seam.Identity.Number);
        Assert.Equal(CatalogRuntimeFixtures.Identity.ManifestHash, seam.Identity.ManifestHash);
        Assert.True(seam.TryGetRow(CatalogSnapshotFixtures.ItemType, 2, out ContentRow? row));
        Assert.Equal("item_2", row.Key.ToString());
        Assert.False(seam.TryGetRow(CatalogSnapshotFixtures.ItemType, 3, out _));
        Assert.True(seam.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey("item_35"), out int id));
        Assert.Equal(35, id);
        Assert.Equal(CatalogRuntimeFixtures.ItemCount, seam.Rows(CatalogSnapshotFixtures.ItemType).Count);
        Assert.Empty(seam.Rows(new ContentTypeId(1024)));
        Assert.Single(seam.Rules);
        Assert.True(seam.IsRetired(CatalogSnapshotFixtures.ItemType, 35));
        Assert.False(seam.IsRetired(CatalogSnapshotFixtures.ItemType, 2));
        Assert.False(seam.IsRetired(CatalogSnapshotFixtures.ItemType, 3));

        // The rows come back ascending by id whatever order they went in, and the type list with them.
        int previous = 0;
        foreach (ContentRow each in seam.Rows(CatalogSnapshotFixtures.ItemType))
        {
            Assert.True(each.Id > previous);
            previous = each.Id;
        }

        Assert.Equal(
            new[] { CatalogSnapshotFixtures.TagType, CatalogSnapshotFixtures.ItemType },
            runtime.Types);
    }

    [Fact]
    public void A_retired_row_keeps_its_slot_and_its_bytes_and_answers_from_the_bitset()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);

        Assert.True(runtime.HasRow(CatalogSnapshotFixtures.ItemType, 35));
        Assert.False(runtime.Body(CatalogSnapshotFixtures.ItemType, 35).IsEmpty);
        Assert.True(runtime.TryGetItem(35, out ItemRow row));
        Assert.True(row.IsRetired);
        Assert.True(runtime.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey("item_35"), out _));
    }

    [Fact]
    public void The_runtime_shares_the_snapshots_body_blob_rather_than_copying_the_catalog_twice()
    {
        ContentSnapshot snapshot = CatalogRuntimeFixtures.Snapshot(out ContentTypeRegistry registry);
        ContentRuntime runtime = ContentRuntime.FromSnapshot(snapshot, registry);

        Assert.True(snapshot.TryGetItem(2, out ItemRow fromSnapshot));
        Assert.True(runtime.TryGetItem(2, out ItemRow fromRuntime));

        // The same bytes in the same buffer, which is what keeps boot's peak at one copy of the catalog.
        Assert.True(fromSnapshot.Key.Utf8.Overlaps(fromRuntime.Key.Utf8));
        Assert.Equal(fromSnapshot.MaxStack, fromRuntime.MaxStack);
    }

    [Fact]
    public void A_row_that_arrived_without_its_body_is_still_a_row()
    {
        // A snapshot built row by row, which a publish candidate and a validator test are, carries no encoded
        // bodies. The generic seam still answers over it, and the typed view says no.
        var registry = CatalogSnapshotFixtures.Registry();
        ContentSnapshot snapshot = new ContentSnapshotBuilder(registry)
            .AddRow(CatalogSnapshotFixtures.ItemRow(2, "item_2", true, 64, 900, 3))
            .Build();

        ContentRuntime runtime = ContentRuntime.FromSnapshot(snapshot, registry);

        Assert.True(runtime.HasRow(CatalogSnapshotFixtures.ItemType, 2));
        Assert.True(runtime.TryGetRow(CatalogSnapshotFixtures.ItemType, 2, out _));
        Assert.True(runtime.Body(CatalogSnapshotFixtures.ItemType, 2).IsEmpty);
        Assert.False(runtime.TryGetItem(2, out _));
        Assert.True(runtime.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey("item_2"), out int id));
        Assert.Equal(2, id);
    }

    [Fact]
    public void A_duplicate_id_and_a_duplicate_key_are_findings_rather_than_load_failures()
    {
        var registry = CatalogSnapshotFixtures.Registry();
        ContentRow first = CatalogSnapshotFixtures.ItemRow(2, "item_2", true, 64, 900, 3);
        ContentRow second = CatalogSnapshotFixtures.ItemRow(2, "item_2_again", false, 1, 0, 0);
        ContentRow third = CatalogSnapshotFixtures.ItemRow(3, "item_2", false, 1, 0, 0);
        ContentSnapshot snapshot = new ContentSnapshotBuilder(registry)
            .AddRow(first, CatalogSnapshotFixtures.Body(registry, EngineContentTypes.ItemTypeKey, first))
            .AddRow(second, CatalogSnapshotFixtures.Body(registry, EngineContentTypes.ItemTypeKey, second))
            .AddRow(third, CatalogSnapshotFixtures.Body(registry, EngineContentTypes.ItemTypeKey, third))
            .Build();

        ContentRuntime runtime = ContentRuntime.FromSnapshot(snapshot, registry);

        // The first row in id order answers, and the loser stays in Rows for the validator to report.
        Assert.True(runtime.TryGetRow(CatalogSnapshotFixtures.ItemType, 2, out ContentRow? row));
        Assert.Equal("item_2", row.Key.ToString());
        Assert.Equal(3, runtime.Rows(CatalogSnapshotFixtures.ItemType).Count);
        Assert.True(runtime.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey("item_2"), out int id));
        Assert.Equal(2, id);
    }

    [Fact]
    public void An_empty_version_loads_and_answers_nothing()
    {
        var registry = CatalogSnapshotFixtures.Registry();
        ContentRuntime runtime = ContentRuntime.FromSnapshot(new ContentSnapshotBuilder(registry).Build(), registry);

        Assert.Empty(runtime.Types);
        Assert.Empty(runtime.Rows(CatalogSnapshotFixtures.ItemType));
        Assert.False(runtime.TryGetItem(2, out _));
        Assert.False(runtime.TryGetId(CatalogSnapshotFixtures.ItemType, new ContentKey("item_2"), out _));
        Assert.Equal(0, runtime.VersionNumber);
    }

    [Fact]
    public void ApproximateBytes_is_the_index_arrays_of_spec_9_2_plus_the_body_blobs()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);
        Assert.True(runtime.TryGetTable(CatalogSnapshotFixtures.ItemType, out ContentTypeTable? items));
        Assert.True(runtime.TryGetTable(CatalogSnapshotFixtures.TagType, out ContentTypeTable? tags));

        // Spec 9.2's arithmetic, worked by hand. The item table covers ids 1 to 35, so Offsets, Lengths and
        // RowIndex are 36 ints each, the retired bitset is one ulong, and the key index is the first power of
        // two at twice its 5 rows.
        const long itemArrays = (36 * 4 * 3) + 8 + (16 * 4);

        // The tag table covers ids 1 to 9 and holds 2 rows, so its key index is 4 buckets.
        const long tagArrays = (10 * 4 * 3) + 8 + (4 * 4);

        // The tag INDEX: one type with a tagged row, two tags under it, and 3 plus 2 row ids beneath those.
        // The family and loot indexes are empty for this version, so they count nothing.
        const long tagIndex = (1 * 2) + ((1 + 1 + 2) * 4) + ((2 + 2 + 5) * 4);

        Assert.Equal(tagIndex, runtime.Indexes.ApproximateBytes());
        Assert.Equal(
            itemArrays + tagArrays + tagIndex + items.Bodies.LongLength + tags.Bodies.LongLength,
            runtime.ApproximateBytes());

        // The decoded rows are NOT in it, which is what the summary says: the loader allocated them before
        // the runtime existed, so counting them would be counting someone else's memory.
        Assert.NotEmpty(items.Rows);
        Assert.True(items.Bodies.LongLength > 0);
    }

    [Fact]
    public void FromSnapshot_refuses_a_null_argument()
    {
        var registry = CatalogSnapshotFixtures.Registry();
        Assert.Throws<ArgumentNullException>(() => ContentRuntime.FromSnapshot(null!, registry));
        Assert.Throws<ArgumentNullException>(
            () => ContentRuntime.FromSnapshot(new ContentSnapshotBuilder(registry).Build(), null!));
    }

    [Fact]
    public void The_holder_is_one_field_read_and_written_volatile()
    {
        FieldInfo[] fields = typeof(ContentRuntimeHolder)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        // Spec 9.7: the runtime is ONE field. A second one would be a second thing to keep consistent across
        // a swap, which is exactly what the single volatile write buys its way out of.
        FieldInfo field = Assert.Single(fields);
        Assert.Equal(typeof(ContentRuntime), field.FieldType);
        Assert.False(field.IsInitOnly);
    }

    [Fact]
    public void An_unloaded_holder_throws_rather_than_serving_a_default_catalog()
    {
        var holder = new ContentRuntimeHolder();

        Assert.False(holder.IsLoaded);
        Assert.False(holder.TryGetCurrent(out _));
        Assert.Throws<InvalidOperationException>(() => holder.Current);
        Assert.Throws<ArgumentNullException>(() => holder.Publish(null!));
    }

    [Fact]
    public void A_reader_that_takes_the_reference_once_keeps_one_version_across_a_swap()
    {
        ContentRuntime first = CatalogRuntimeFixtures.Runtime(out ContentTypeRegistry registry);
        ContentRuntime second = ContentRuntime.FromSnapshot(
            new ContentSnapshotBuilder(registry).WithIdentity(8, new string('e', 64)).Build(), registry);

        var holder = new ContentRuntimeHolder();
        holder.Publish(first);

        // The discipline of spec 9.7: one read at the top of the operation, that instance for the rest of it.
        ContentRuntime taken = holder.Current;
        holder.Publish(second);

        Assert.Equal(7, taken.VersionNumber);
        Assert.True(taken.TryGetItem(2, out _));
        Assert.Equal(8, holder.Current.VersionNumber);
        Assert.False(holder.Current.TryGetItem(2, out _));
        Assert.True(holder.TryGetCurrent(out ContentRuntime? current));
        Assert.Same(second, current);
    }

    [Fact]
    public void A_published_runtime_is_the_same_instance_every_reader_sees()
    {
        ContentRuntime runtime = CatalogRuntimeFixtures.Runtime(out _);
        var holder = new ContentRuntimeHolder();
        holder.Publish(runtime);

        ContentRuntime? seen = null;
        var thread = new Thread(() => seen = holder.Current);
        thread.Start();
        thread.Join();

        Assert.Same(runtime, seen);
        Assert.Same(runtime, holder.Current);
    }

    [Fact]
    public void Everything_the_runtime_reaches_is_immutable_after_construction()
    {
        // The swap needs no lock and no barrier beyond the publishing write BECAUSE of this: a writable field
        // anywhere in the graph would be a torn read waiting to happen.
        AssertReadOnlyFields(typeof(ContentTypeTable));
        AssertReadOnlyFields(typeof(ContentTagIndex));
        AssertReadOnlyFields(typeof(ContentFamilyIndex));
        AssertReadOnlyFields(typeof(ContentLootIndex));
        AssertReadOnlyFields(typeof(ContentDerivedIndexes));

        // The runtime's ONE exception is the registered load-index map of boot step 7b, which is null while
        // step 7b runs and assigned exactly once when it finishes. That null IS the refusal an index reading
        // another index meets, so it is state with a job rather than a leftover setter. It is written and
        // read volatile, because the public API allows the step to run after a publish, where the holder's
        // own write no longer orders it.
        AssertReadOnlyFields(typeof(ContentRuntime), "_loadIndexes");
    }

    static void AssertReadOnlyFields(Type type, string? except = null)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (string.Equals(field.Name, except, StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(field.IsInitOnly, $"{type.Name}.{field.Name} is writable after construction");
        }
    }
}
