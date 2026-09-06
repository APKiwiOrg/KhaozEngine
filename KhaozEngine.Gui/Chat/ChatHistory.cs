using System;
using System.Collections.Generic;

namespace KhaozEngine.Gui.Chat;

public sealed class ChatHistory
{
    readonly List<ChatEntry> entries;

    public ChatHistory(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        Capacity = capacity;
        entries = new List<ChatEntry>(capacity);
    }

    public int Capacity { get; }
    public IReadOnlyList<ChatEntry> Entries => entries;
    public long Version { get; private set; }

    public void Add(ChatEntry entry)
    {
        if (entries.Count > 0 && CanCollapse(entries[^1], entry))
            entries[^1] = entry with { RepeatCount = entries[^1].RepeatCount + 1 };
        else
            entries.Add(entry with { RepeatCount = 1 });

        if (entries.Count > Capacity)
            entries.RemoveAt(0);

        Version++;
    }

    static bool CanCollapse(ChatEntry previous, ChatEntry next) =>
        previous.Kind == next.Kind &&
        string.Equals(previous.SourceKey, next.SourceKey, StringComparison.Ordinal) &&
        string.Equals(previous.CollapseKey, next.CollapseKey, StringComparison.Ordinal);
}
