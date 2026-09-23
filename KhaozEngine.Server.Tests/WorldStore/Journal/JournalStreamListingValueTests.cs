using System;
using KhaozEngine.WorldStore.Journal;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class JournalStreamListingValueTests
{
    [Fact]
    public void Query_bounds_its_page_and_validates_prefix_and_continuation_before_io()
    {
        var all = new JournalStreamQuery(JournalStreamQuery.MaximumStreamsPerPage);

        Assert.Equal(string.Empty, all.KeyPrefix);
        Assert.Null(all.AfterStreamKey);
        Assert.Equal(string.Empty, new JournalStreamQuery(1, string.Empty).KeyPrefix);
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalStreamQuery(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalStreamQuery(JournalStreamQuery.MaximumStreamsPerPage + 1));
        Assert.Throws<ArgumentException>(() => new JournalStreamQuery(1, "player a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalStreamQuery(1, new string('a', JournalLimits.EngineMaximumStreamKeyCharacters + 1)));
        Assert.Throws<ArgumentException>(() => new JournalStreamQuery(1, afterStreamKey: string.Empty));
        Assert.Throws<ArgumentException>(() => new JournalStreamQuery(1, afterStreamKey: "player%"));
    }

    [Fact]
    public void Query_validation_applies_configured_stream_key_limits()
    {
        var limits = new JournalLimits(streamKeyCharacters: 4);

        new JournalStreamQuery(1, "abcd", "abcd").Validate(limits);
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalStreamQuery(1, "abcde").Validate(limits));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalStreamQuery(1, afterStreamKey: "abcde").Validate(limits));
    }

    [Fact]
    public void Page_requires_ordered_unique_streams_and_a_continuation_at_the_last_key()
    {
        var a = new JournalStreamEntry("player/a", 0);
        var b = new JournalStreamEntry("player/b", 3);

        var page = new JournalStreamPage(new[] { a, b }, "player/b");

        Assert.Equal("player/b", page.ContinuationKey);
        Assert.Equal(3, page.Streams[1].HeadVersion);
        Assert.Throws<ArgumentException>(() => new JournalStreamPage(new[] { b, a }, null));
        Assert.Throws<ArgumentException>(() => new JournalStreamPage(new[] { a, a }, null));
        Assert.Throws<ArgumentException>(() => new JournalStreamPage(new[] { a, b }, "player/a"));
        Assert.Throws<ArgumentException>(() => new JournalStreamPage(Array.Empty<JournalStreamEntry>(), "player/a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalStreamEntry("player/a", -1));
    }
}
