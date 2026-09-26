using System;
using KhaozEngine.App;
using KhaozEngine.Gui;

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
        MarkupContent = null;
    }

    /// <summary>Create an entry whose content is trusted localized semantic colour markup.</summary>
    public ChatEntry(
        DateTimeOffset TimestampUtc,
        string SourceKey,
        LocalizedText? Author,
        MarkupText Content,
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
        this.Content = default;
        this.CollapseKey = CollapseKey;
        this.Kind = Kind;
        this.IsOwn = IsOwn;
        this.RepeatCount = RepeatCount;
        MarkupContent = Content;
    }

    public DateTimeOffset TimestampUtc { get; init; }
    public string SourceKey { get; init; }
    public LocalizedText? Author { get; init; }
    public LocalizedText Content { get; init; }
    /// <summary>Opt-in localized semantic colour markup, or null for the plain <see cref="Content"/> path.</summary>
    public MarkupText? MarkupContent { get; init; }
    public string CollapseKey { get; init; }
    public ChatEntryKind Kind { get; init; }
    public bool IsOwn { get; init; }
    public int RepeatCount { get; init; }
}
