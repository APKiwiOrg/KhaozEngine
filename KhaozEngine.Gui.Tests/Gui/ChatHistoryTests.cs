using System;
using System.Linq;
using KhaozEngine.App;
using KhaozEngine.Gui.Chat;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class ChatHistoryTests
{
    [Fact]
    public void Adjacent_equal_entries_collapse_and_take_the_latest_time()
    {
        var history = new ChatHistory(3);
        DateTimeOffset first = new(2026, 9, 6, 1, 2, 0, TimeSpan.Zero);
        history.Add(Entry(first, "42", "hello"));
        history.Add(Entry(first.AddSeconds(1), "42", "hello"));

        ChatEntry kept = Assert.Single(history.Entries);
        Assert.Equal(2, kept.RepeatCount);
        Assert.Equal(first.AddSeconds(1), kept.TimestampUtc);
        Assert.Equal(2, history.Version);
    }

    [Fact]
    public void Source_kind_and_intervening_entry_each_break_collapse()
    {
        var history = new ChatHistory(8);
        history.Add(Entry(Utc(1), "1", "same"));
        history.Add(Entry(Utc(2), "2", "same"));
        history.Add(Entry(Utc(3), "1", "same", ChatEntryKind.System));
        history.Add(Entry(Utc(4), "1", "other"));
        history.Add(Entry(Utc(5), "1", "same"));
        Assert.Equal(5, history.Entries.Count);
    }

    [Fact]
    public void Capacity_evicts_the_oldest_visible_entry()
    {
        var history = new ChatHistory(2);
        history.Add(Entry(Utc(1), "1", "one"));
        history.Add(Entry(Utc(2), "1", "two"));
        history.Add(Entry(Utc(3), "1", "three"));
        Assert.Equal(new[] { "two", "three" },
            history.Entries.Select(e => e.CollapseKey));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Capacity_must_be_positive(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChatHistory(capacity));
    }

    [Fact]
    public void Source_key_must_not_be_null()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CreateEntry(SourceKey: null!));
    }

    [Fact]
    public void Collapse_key_must_not_be_null()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CreateEntry(CollapseKey: null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Repeat_count_must_be_positive(int repeatCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateEntry(RepeatCount: repeatCount));
    }

    [Fact]
    public void Entry_timestamp_is_normalized_to_utc()
    {
        DateTimeOffset local = new(2026, 9, 6, 11, 2, 0, TimeSpan.FromHours(10));

        ChatEntry entry = CreateEntry(TimestampUtc: local);

        Assert.Equal(TimeSpan.Zero, entry.TimestampUtc.Offset);
        Assert.Equal(Utc(2), entry.TimestampUtc);
    }

    [Fact]
    public void Author_and_ownership_do_not_break_collapse()
    {
        var history = new ChatHistory(3);
        history.Add(CreateEntry(Author: LocalizedText.Raw("first"), IsOwn: false));
        history.Add(CreateEntry(
            TimestampUtc: Utc(2),
            Author: LocalizedText.Raw("second"),
            IsOwn: true));

        ChatEntry kept = Assert.Single(history.Entries);
        Assert.Equal("second", kept.Author?.Resolve());
        Assert.True(kept.IsOwn);
        Assert.Equal(2, kept.RepeatCount);
    }

    [Fact]
    public void Source_and_collapse_keys_use_ordinal_equality()
    {
        var history = new ChatHistory(4);
        history.Add(Entry(Utc(1), "source", "message"));
        history.Add(Entry(Utc(2), "SOURCE", "message"));
        history.Add(Entry(Utc(3), "SOURCE", "MESSAGE"));

        Assert.Equal(3, history.Entries.Count);
    }

    static ChatEntry Entry(
        DateTimeOffset timestamp,
        string sourceKey,
        string collapseKey,
        ChatEntryKind kind = ChatEntryKind.Ordinary) =>
        new(
            timestamp,
            sourceKey,
            LocalizedText.Raw(sourceKey),
            LocalizedText.Raw(collapseKey),
            collapseKey,
            kind,
            IsOwn: false);

    static DateTimeOffset Utc(int minute) =>
        new(2026, 9, 6, 1, minute, 0, TimeSpan.Zero);

    static ChatEntry CreateEntry(
        DateTimeOffset? TimestampUtc = null,
        string SourceKey = "source",
        LocalizedText? Author = null,
        LocalizedText? Content = null,
        string CollapseKey = "message",
        ChatEntryKind Kind = ChatEntryKind.Ordinary,
        bool IsOwn = false,
        int RepeatCount = 1) =>
        new(
            TimestampUtc ?? Utc(1),
            SourceKey,
            Author,
            Content ?? LocalizedText.Raw("message"),
            CollapseKey,
            Kind,
            IsOwn,
            RepeatCount);
}
