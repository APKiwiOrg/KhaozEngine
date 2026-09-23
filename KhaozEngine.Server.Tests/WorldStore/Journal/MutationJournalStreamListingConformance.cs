using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>The store conformance plus the optional <see cref="IMutationJournalStreamListing"/> capability. A store
/// that lists derives from this rather than from <see cref="MutationJournalStoreConformance"/> directly.</summary>
public abstract class MutationJournalStreamListingConformance : MutationJournalStoreConformance
{
    private static readonly string[] OrderedKeys =
    {
        "guild/x",
        "player/A",
        "player/a",
        "player/a/pet",
        "player/b",
        "zone/z",
        "zone/za",
    };

    [Fact]
    public async Task Stream_listing_returns_every_stream_in_ordinal_key_order_with_its_head()
    {
        MutationJournalStoreHarness harness = CreateStore();
        IMutationJournalStreamListing listing = await SeedAsync(harness);

        JournalStreamPage page = await listing.ListStreamsAsync(new JournalStreamQuery(100));

        Assert.Equal(OrderedKeys, page.Streams.Select(value => value.StreamKey));
        Assert.Null(page.ContinuationKey);
        Assert.Equal(2, page.Streams.Single(value => value.StreamKey == "player/a").HeadVersion);
        Assert.All(page.Streams.Where(value => value.StreamKey != "player/a"), value => Assert.Equal(0, value.HeadVersion));
    }

    [Fact]
    public async Task Stream_listing_filters_by_an_ordinal_case_sensitive_key_prefix()
    {
        MutationJournalStoreHarness harness = CreateStore();
        IMutationJournalStreamListing listing = await SeedAsync(harness);

        Assert.Equal(new[] { "player/a", "player/a/pet" }, await KeysAsync(listing, new JournalStreamQuery(100, "player/a")));
        Assert.Equal(new[] { "player/A", "player/a", "player/a/pet", "player/b" }, await KeysAsync(listing, new JournalStreamQuery(100, "player/")));
        Assert.Equal(new[] { "zone/z", "zone/za" }, await KeysAsync(listing, new JournalStreamQuery(100, "zone/z")));
        Assert.Empty(await KeysAsync(listing, new JournalStreamQuery(100, "Player/")));
        Assert.Empty(await KeysAsync(listing, new JournalStreamQuery(100, "nobody/")));
        Assert.Equal(new[] { "player/A", "player/a", "player/a/pet", "player/b" }, await KeysAsync(listing, new JournalStreamQuery(100, "player/", "guild/zz")));
        Assert.Equal(new[] { "player/a/pet", "player/b" }, await KeysAsync(listing, new JournalStreamQuery(100, "player/", "player/a")));
        Assert.Empty(await KeysAsync(listing, new JournalStreamQuery(100, "player/", "zone/")));
    }

    [Fact]
    public async Task Stream_listing_pages_through_a_continuation_until_the_listing_is_complete()
    {
        MutationJournalStoreHarness harness = CreateStore();
        IMutationJournalStreamListing listing = await SeedAsync(harness);

        var pages = new List<JournalStreamPage>();
        var query = new JournalStreamQuery(3);
        while (true)
        {
            JournalStreamPage page = await listing.ListStreamsAsync(query);
            pages.Add(page);
            if (page.ContinuationKey is null) break;
            query = new JournalStreamQuery(3, afterStreamKey: page.ContinuationKey);
        }

        Assert.Equal(new[] { 3, 3, 1 }, pages.Select(value => value.Streams.Count));
        Assert.Equal(new[] { "player/a", "zone/z", null }, pages.Select(value => value.ContinuationKey));
        Assert.Equal(OrderedKeys, pages.SelectMany(value => value.Streams).Select(value => value.StreamKey));

        JournalStreamPage exactFirst = await listing.ListStreamsAsync(new JournalStreamQuery(2, "player/"));
        JournalStreamPage exactLast = await listing.ListStreamsAsync(new JournalStreamQuery(2, "player/", exactFirst.ContinuationKey));
        Assert.Equal(new[] { "player/A", "player/a" }, exactFirst.Streams.Select(value => value.StreamKey));
        Assert.Equal("player/a", exactFirst.ContinuationKey);
        Assert.Equal(new[] { "player/a/pet", "player/b" }, exactLast.Streams.Select(value => value.StreamKey));
        Assert.Null(exactLast.ContinuationKey);
    }

    [Fact]
    public async Task Stream_listing_continuation_is_a_key_so_later_streams_ahead_of_it_are_listed()
    {
        MutationJournalStoreHarness harness = CreateStore();
        IMutationJournalStreamListing listing = Listing(harness);
        await harness.Store.InitializeAsync(Initialization(Operation(1), "stream/b"));
        await harness.Store.InitializeAsync(Initialization(Operation(2), "stream/d"));
        JournalStreamPage first = await listing.ListStreamsAsync(new JournalStreamQuery(1, "stream/"));

        await harness.Store.InitializeAsync(Initialization(Operation(3), "stream/a"));
        await harness.Store.InitializeAsync(Initialization(Operation(4), "stream/c"));
        JournalStreamPage second = await listing.ListStreamsAsync(new JournalStreamQuery(10, "stream/", first.ContinuationKey));

        Assert.Equal("stream/b", Assert.Single(first.Streams).StreamKey);
        Assert.Equal("stream/b", first.ContinuationKey);
        Assert.Equal(new[] { "stream/c", "stream/d" }, second.Streams.Select(value => value.StreamKey));
        Assert.Null(second.ContinuationKey);
    }

    [Fact]
    public async Task Stream_listing_of_an_empty_store_is_one_complete_empty_page()
    {
        MutationJournalStoreHarness harness = CreateStore();

        JournalStreamPage page = await Listing(harness).ListStreamsAsync(new JournalStreamQuery(10));

        Assert.Empty(page.Streams);
        Assert.Null(page.ContinuationKey);
    }

    private static IMutationJournalStreamListing Listing(MutationJournalStoreHarness harness)
        => Assert.IsAssignableFrom<IMutationJournalStreamListing>(harness.Store);

    private static async Task<IMutationJournalStreamListing> SeedAsync(MutationJournalStoreHarness harness)
    {
        string[] creationOrder = { "player/b", "zone/za", "guild/x", "player/a/pet", "player/A", "zone/z", "player/a" };
        for (int i = 0; i < creationOrder.Length; i++)
            await harness.Store.InitializeAsync(Initialization(Operation(i + 1), creationOrder[i]));
        JournalCommitResult commit = await harness.Store.CommitAsync(Commit(Operation(50), Mutation("player/a", 0, Event(1), Event(2))));
        Assert.Equal(JournalCommitStatus.Applied, commit.Status);
        return Listing(harness);
    }

    private static async Task<string[]> KeysAsync(IMutationJournalStreamListing listing, JournalStreamQuery query)
    {
        JournalStreamPage page = await listing.ListStreamsAsync(query);
        Assert.Null(page.ContinuationKey);
        return page.Streams.Select(value => value.StreamKey).ToArray();
    }
}
