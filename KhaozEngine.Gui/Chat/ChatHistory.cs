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

    /// <summary>
    /// Adds one entry, folding it into a matching one already in the history instead when there is one.
    /// </summary>
    /// <param name="entry">The entry to add.</param>
    /// <remarks>
    /// A repeat is CONSOLIDATED WHEREVER IT IS, not only against the entry immediately before. A player who
    /// does the same thing forty times wants one line counting up, and against the previous entry alone any
    /// other line arriving between two of them starts the count over, which is how a chat box fills with the
    /// same sentence at slightly different moments.
    /// <para>The fold keeps the matched entry's PLACE and takes everything else from the arriving one: its
    /// time, its content, its author and its ownership, plus the count. Holding the place keeps the history
    /// stable under the reader's eye rather than making old lines jump to the bottom, and it means a run of
    /// one repeated line does not push everything else off the top. The consequence to know about is that
    /// times are no longer strictly ascending down the list: an older line that just repeated carries a newer
    /// stamp than the line under it.</para>
    /// <para>The search runs BACKWARDS because the adjacent match is much the most common one and is found
    /// first. At most one entry can match, since every earlier repeat has already been folded into it.</para>
    /// </remarks>
    public void Add(ChatEntry entry)
    {
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (!CanCollapse(entries[i], entry)) continue;
            entries[i] = entry with { RepeatCount = entries[i].RepeatCount + 1 };
            Version++;
            return;
        }

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
