using System;
using KhaozEngine.WorldStore.Journal;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class JournalResetResultTests
{
    private static readonly Guid Epoch = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    [Fact]
    public void Every_count_is_kept_by_its_own_table_and_the_total_adds_them()
    {
        var result = new JournalResetResult(Epoch, 2, 5, 3, 7, 11, 13);

        Assert.Equal(Epoch, result.StoreEpoch);
        Assert.Equal(2, result.StreamsDeleted);
        Assert.Equal(5, result.EventsDeleted);
        Assert.Equal(3, result.SnapshotsDeleted);
        Assert.Equal(7, result.ProjectionsDeleted);
        Assert.Equal(11, result.OperationsDeleted);
        Assert.Equal(13, result.OperationStreamsDeleted);
        Assert.Equal(41, result.RowsDeleted);
    }

    [Fact]
    public void The_summary_names_every_count_and_the_kept_epoch()
    {
        string summary = new JournalResetResult(Epoch, 2, 5, 3, 7, 11, 13).Summary;

        Assert.Equal(
            "Journal reset. Deleted 2 streams, 5 events, 3 snapshots, 7 projection sections, 11 operation receipts and 13 receipt stream ranges. The store epoch 0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0 was kept.",
            summary);
    }

    [Fact]
    public void A_reset_of_an_empty_journal_reports_zero_rows()
    {
        var result = new JournalResetResult(Epoch, 0, 0, 0, 0, 0, 0);

        Assert.Equal(0, result.RowsDeleted);
        Assert.Equal(new JournalResetResult(Epoch, 0, 0, 0, 0, 0, 0), result);
    }

    [Fact]
    public void An_empty_epoch_is_refused()
        => Assert.Throws<ArgumentException>(() => new JournalResetResult(Guid.Empty, 0, 0, 0, 0, 0, 0));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void A_negative_count_is_refused_whichever_table_it_names(int negative)
    {
        long[] counts = new long[6];
        counts[negative] = -1;

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new JournalResetResult(Epoch, counts[0], counts[1], counts[2], counts[3], counts[4], counts[5]));
    }

    [Fact]
    public void Counts_that_break_the_foreign_keys_are_still_reported_as_they_were_read()
    {
        // A store whose rows break its own keys still has to be resettable, so the record does not refuse them.
        var orphans = new JournalResetResult(Epoch, 0, 4, 0, 1, 0, 2);

        Assert.Equal(7, orphans.RowsDeleted);
    }
}
