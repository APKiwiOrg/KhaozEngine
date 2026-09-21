using System;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Runtime;

/// <summary>
/// The typed view of spec 9.1 over the engine <c>item</c> type: the five fields Scope B reads per
/// operation, decoded from the row body in one pass with no allocation and no walk by field name.
/// <para>
/// Joins <c>AllocSensitive</c> because the budget test reads
/// <c>GC.GetAllocatedBytesForCurrentThread</c>.
/// </para>
/// </summary>
[Collection("AllocSensitive")]
public class ItemRowTests
{
    [Fact]
    public void TryGetItem_decodes_the_five_hot_fields_and_the_key()
    {
        ContentSnapshot snapshot = Snapshot(out _);

        Assert.True(snapshot.TryGetItem(2, out ItemRow row));
        Assert.True(row.Stackable);
        Assert.Equal(64, row.MaxStack);
        Assert.Equal(250, row.Value);
        Assert.Equal(900, row.DurabilityMax);
        Assert.Equal(3, row.SocketMax);
        Assert.Equal("iron_sword", row.Key.ToString());
        Assert.False(row.IsRetired);
    }

    [Fact]
    public void A_retired_item_answers_IsRetired()
    {
        ContentSnapshot snapshot = Snapshot(out _);

        Assert.True(snapshot.TryGetItem(5, out ItemRow row));
        Assert.True(row.IsRetired);
        Assert.Equal("bronze_sword", row.Key.ToString());
    }

    [Fact]
    public void An_id_with_no_row_answers_false()
    {
        ContentSnapshot snapshot = Snapshot(out _);

        Assert.False(snapshot.TryGetItem(11, out _));
        Assert.False(snapshot.TryGetItem(0, out _));
    }

    [Fact]
    public void An_item_row_the_builder_was_handed_no_body_for_answers_false()
    {
        // The typed view reads the encoded body. A snapshot built from rows alone, which is what a publish
        // candidate and a validator test are, carries no bodies, and the generic read side is its answer.
        ContentSnapshot snapshot = new ContentSnapshotBuilder(CatalogSnapshotFixtures.Registry())
            .AddRow(CatalogSnapshotFixtures.ItemRow(2, "iron_sword", true, 64, 900, 3))
            .Build();

        Assert.False(snapshot.TryGetItem(2, out _));
        Assert.True(snapshot.TryGetRow(CatalogSnapshotFixtures.ItemType, 2, out _));
    }

    [Fact]
    public void The_hot_path_allocates_nothing()
    {
        ContentSnapshot snapshot = Snapshot(out _);

        // Warm the JIT and the lookup before the window opens, so the measurement is steady state.
        long warm = 0;
        for (int i = 0; i < 64; i++)
        {
            Assert.True(snapshot.TryGetItem(2, out ItemRow row));
            warm += row.MaxStack;
        }

        Assert.True(warm > 0);

        CatalogAllocAssert.NoPerCallAllocation("TryGetItem over a loaded snapshot", () =>
        {
            long total = 0;
            for (int i = 0; i < 1000; i++)
            {
                snapshot.TryGetItem(2, out ItemRow row);
                total += row.MaxStack + row.Value + row.DurabilityMax + row.SocketMax + (row.Stackable ? 1 : 0);
            }

            Assert.True(total > 0);
        });
    }

    [Fact]
    public void The_key_is_read_out_of_the_body_rather_than_copied()
    {
        ContentSnapshot snapshot = Snapshot(out _);

        Assert.True(snapshot.TryGetItem(2, out ItemRow first));
        Assert.True(snapshot.TryGetItem(2, out ItemRow second));

        // The same bytes in the same buffer on both reads: an id-to-key read is the row read one varint
        // further in, out of the loaded bodies, rather than a string materialised per call.
        Assert.True(first.Key.Utf8.Overlaps(second.Key.Utf8));
        Assert.True(first.Key.Equals(new ContentKey("iron_sword")));
    }

    [Fact]
    public void A_truncated_body_is_refused_unless_it_ends_where_an_earlier_schema_ended()
    {
        ContentTypeRegistry registry = CatalogSnapshotFixtures.Registry();
        ContentRow row = CatalogSnapshotFixtures.ItemRow(2, "iron_sword", true, 64, 900, 3);
        byte[] body = CatalogSnapshotFixtures.Body(registry, EngineContentTypes.ItemTypeKey, row);

        // The fixture's category is absent, so its last byte is that field's zero form and the body without
        // it is exactly what a publish before the field existed wrote. Everything shorter than that is a real
        // truncation and is refused.
        for (int length = 0; length < body.Length - 1; length++)
        {
            Assert.False(ItemRow.TryDecode(body, 0, length, false, out _));
        }

        Assert.True(ItemRow.TryDecode(body, 0, body.Length - 1, false, out _));
        Assert.True(ItemRow.TryDecode(body, 0, body.Length, false, out _));
    }

    /// <summary>
    /// A body written under the item schema as it stood before <c>category</c> was appended reads through the
    /// typed view with the same hot fields. This is the shipped client pack case: the bytes on disk are one
    /// field short of what this build's schema declares and they are still item rows.
    /// </summary>
    [Fact]
    public void A_body_from_before_the_category_field_reads_the_same_hot_fields()
    {
        ContentTypeRegistry registry = CatalogSnapshotFixtures.Registry();
        ContentRow row = CatalogSnapshotFixtures.ItemRow(2, "iron_sword", true, 64, 900, 3);
        byte[] body = CatalogSnapshotFixtures.Body(registry, EngineContentTypes.ItemTypeKey, row);
        byte[] older = body[..^1];

        Assert.True(ItemRow.TryDecode(older, 0, older.Length, false, out ItemRow view));
        Assert.True(view.Stackable);
        Assert.Equal(64, view.MaxStack);
        Assert.Equal(250, view.Value);
        Assert.Equal(900, view.DurabilityMax);
        Assert.Equal(3, view.SocketMax);
        Assert.Equal("iron_sword", view.Key.ToString());
    }

    [Fact]
    public void A_body_whose_key_length_runs_past_the_end_is_refused()
    {
        // The key length is the first varint, so a body claiming a key longer than itself is the shortest
        // malformed row there is, and a total decoder answers false rather than slicing past the end.
        byte[] body = [0x7F, (byte)'a'];

        Assert.False(ItemRow.TryDecode(body, 0, body.Length, false, out _));
    }

    [Fact]
    public void A_range_outside_the_buffer_is_a_programming_error_rather_than_a_decode_failure()
    {
        byte[] body = [0x01, (byte)'a'];

        Assert.Throws<ArgumentNullException>(() => { ItemRow.TryDecode(null!, 0, 0, false, out _); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { ItemRow.TryDecode(body, -1, 1, false, out _); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { ItemRow.TryDecode(body, 1, 5, false, out _); });
    }

    static ContentSnapshot Snapshot(out byte[] swordBody)
    {
        ContentTypeRegistry registry = CatalogSnapshotFixtures.Registry();
        ContentRow sword = CatalogSnapshotFixtures.ItemRow(2, "iron_sword", stackable: true, 64, 900, 3);
        ContentRow bronze = CatalogSnapshotFixtures.ItemRow(
            5, "bronze_sword", stackable: false, 1, 40, 0, isRetired: true);

        swordBody = CatalogSnapshotFixtures.Body(registry, EngineContentTypes.ItemTypeKey, sword);

        return new ContentSnapshotBuilder(registry)
            .WithIdentity(3, new string('b', 64))
            .AddRow(sword, swordBody)
            .AddRow(bronze, CatalogSnapshotFixtures.Body(registry, EngineContentTypes.ItemTypeKey, bronze))
            .Build();
    }
}
