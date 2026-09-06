using System;
using KhaozEngine.App;

namespace KhaozEngine.Gui.Chat;

public enum ChatEntryKind
{
    Ordinary,
    System,
}

public readonly record struct ChatEntry
{
    public ChatEntry(
        DateTimeOffset TimestampUtc,
        string SourceKey,
        LocalizedText? Author,
        LocalizedText Content,
        string CollapseKey,
        ChatEntryKind Kind,
        bool IsOwn,
        int RepeatCount = 1)
    {
        ArgumentNullException.ThrowIfNull(SourceKey);
        ArgumentNullException.ThrowIfNull(CollapseKey);
        if (RepeatCount < 1)
            throw new ArgumentOutOfRangeException(nameof(RepeatCount));

        this.TimestampUtc = TimestampUtc.ToUniversalTime();
        this.SourceKey = SourceKey;
        this.Author = Author;
        this.Content = Content;
        this.CollapseKey = CollapseKey;
        this.Kind = Kind;
        this.IsOwn = IsOwn;
        this.RepeatCount = RepeatCount;
    }

    public DateTimeOffset TimestampUtc { get; init; }
    public string SourceKey { get; init; }
    public LocalizedText? Author { get; init; }
    public LocalizedText Content { get; init; }
    public string CollapseKey { get; init; }
    public ChatEntryKind Kind { get; init; }
    public bool IsOwn { get; init; }
    public int RepeatCount { get; init; }
}
