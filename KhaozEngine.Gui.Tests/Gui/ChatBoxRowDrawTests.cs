using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui.Chat;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

/// <summary>
/// What the chat history sends to the sprite batch, run by run. A <see cref="SpriteBatch"/> needs a GPU device, so
/// the rows reach it through the internal <see cref="IChatRowSink"/> the real draw also uses, and a recording sink
/// stands in for the batch here. Raw text only, so no ambient catalog is read or written.
/// </summary>
public sealed class ChatBoxRowDrawTests
{
    const string Stamp = "[11:07] ";

    static readonly Rect BoxBounds = new(100f, 100f, 300f, 180f);
    static readonly TimeZoneInfo Sydney = TimeZoneInfo.CreateCustomTimeZone(
        "Australia/Sydney",
        TimeSpan.FromHours(10),
        "Australian Eastern Standard Time",
        "Australian Eastern Standard Time");
    static readonly ChatBoxTheme Theme = new()
    {
        OrdinaryText = new Vector4(0.7f, 0.7f, 0.7f, 1f),
        OwnText = new Vector4(0.9f, 0.7f, 0.4f, 1f),
        SystemText = new Vector4(0.5f, 0.8f, 0.9f, 1f),
        TimestampText = new Vector4(0.5f, 0.5f, 0.5f, 1f),
    };
    static readonly FixedMeasurer Narrow = new(8f);

    [Fact]
    public void A_timestamped_row_draws_the_stamp_then_the_message_at_the_measured_offset()
    {
        var history = new ChatHistory(8);
        history.Add(Entry(At(7), "a"));
        history.Add(Entry(At(8), "b", isOwn: true));
        history.Add(Entry(At(9), "c", kind: ChatEntryKind.System));
        ChatBox box = Box(history);
        box.RefreshLayout(Narrow, Sydney);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        // Rows start at the viewport's top left (108, 108) and step by the 16 px line plus the 2 px spacing. The
        // message starts after the eight-character stamp, 64 px at 8 px a character.
        Assert.Equal(new[]
        {
            new Drawn("[11:07] ", new Vector2(108f, 108f), (Color)Theme.TimestampText),
            new Drawn("Alice: hello", new Vector2(172f, 108f), (Color)Theme.OrdinaryText),
            new Drawn("[11:08] ", new Vector2(108f, 126f), (Color)Theme.TimestampText),
            new Drawn("Alice: hello", new Vector2(172f, 126f), (Color)Theme.OwnText),
            new Drawn("[11:09] ", new Vector2(108f, 144f), (Color)Theme.TimestampText),
            new Drawn("Alice: hello", new Vector2(172f, 144f), (Color)Theme.SystemText),
        }, sink.Runs);
    }

    [Fact]
    public void A_row_without_a_stamp_draws_its_whole_line_in_the_message_colour()
    {
        var history = new ChatHistory(8);
        history.Add(Entry(At(7), "a", isOwn: true));
        ChatBox box = Box(history);
        box.ShowTimestamps = false;
        box.RefreshLayout(Narrow, Sydney);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        Assert.Equal(new[] { new Drawn("Alice: hello", new Vector2(108f, 108f), (Color)Theme.OwnText) }, sink.Runs);
    }

    [Fact]
    public void A_wrapped_entry_draws_the_stamp_once_and_its_other_lines_whole()
    {
        var history = new ChatHistory(8);
        history.Add(Entry(At(7), "a", content: "hi " + new string('x', 40), author: null));
        ChatBox box = Box(history);
        box.RefreshLayout(Narrow, Sydney);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        // The wrap keeps "hi" on the stamp's line and moves the run of x below it, so the first line splits into
        // the stamp and the message at the measured offset, and every later line draws whole in the message colour.
        Assert.Equal("[11:07] hi", box.CachedLines[0]);
        Assert.True(box.CachedLines.Count > 1);
        Assert.Equal(new Drawn(Stamp, new Vector2(108f, 108f), (Color)Theme.TimestampText), sink.Runs[0]);
        Assert.Equal(new Drawn("hi", new Vector2(172f, 108f), (Color)Theme.OrdinaryText), sink.Runs[1]);
        Assert.All(sink.Runs.GetRange(2, sink.Runs.Count - 2),
            run => Assert.Equal((Color)Theme.OrdinaryText, run.Color));
        Assert.Equal(ExpectedRuns(box), Texts(sink));
    }

    // The history viewport of BoxBounds: inset by the 8 px padding, less the 30 px composer and its 6 px gap.
    static readonly Rect Viewport = new(108f, 108f, 284f, 128f);
    static readonly FixedMeasurer Wide = new(10f);

    [Fact]
    public void A_full_history_draws_only_the_rows_on_screen()
    {
        ChatBox box = Box(Numbered(100));
        box.ShowTimestamps = false;
        box.RefreshLayout(Narrow, Sydney);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        // A new layout starts scrolled to the newest row, so the viewport shows rows 93 to 99 and the one-row
        // margin adds 92. Row 92 ends exactly at the viewport top, which counts as outside.
        Assert.Equal(ExpectedVisible(box, 100), Texts(sink));
        Assert.Equal("message 92", sink.Runs[0].Text);
        Assert.Equal("message 99", sink.Runs[^1].Text);
    }

    [Theory]
    [InlineData(ChatHistoryAlignment.Top, 1f)]
    [InlineData(ChatHistoryAlignment.Top, 10f)]
    [InlineData(ChatHistoryAlignment.Top, 55f)]
    [InlineData(ChatHistoryAlignment.Top, 200f)]
    [InlineData(ChatHistoryAlignment.Bottom, 1f)]
    [InlineData(ChatHistoryAlignment.Bottom, 55f)]
    public void Scrolling_mid_history_draws_only_the_rows_meeting_the_viewport(ChatHistoryAlignment alignment,
        float notches)
    {
        ChatBox box = Box(Numbered(100));
        box.ShowTimestamps = false;
        box.HistoryAlignment = alignment;
        box.RefreshLayout(Narrow, Sydney);
        ScrollUp(box, notches);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        Assert.Equal(ExpectedVisible(box, 100), Texts(sink));
        Assert.InRange(sink.Runs.Count, 7, 10);
    }

    [Fact]
    public void A_partly_visible_row_at_either_edge_is_drawn()
    {
        ChatBox box = Box(Numbered(100));
        box.ShowTimestamps = false;
        box.RefreshLayout(Narrow, Sydney);
        ScrollUp(box, 1f);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        // One 30 px notch up from the bottom leaves row 91 cut by the viewport top (104 to 120) and row 98 cut by
        // the viewport bottom (230 to 246).
        Assert.Equal(104f, box.RowBounds(91).Y);
        Assert.Equal(230f, box.RowBounds(98).Y);
        List<string> texts = Texts(sink);
        Assert.Contains("message 91", texts);
        Assert.Contains("message 98", texts);
    }

    [Theory]
    [InlineData(0, ChatHistoryAlignment.Top)]
    [InlineData(0, ChatHistoryAlignment.Bottom)]
    [InlineData(1, ChatHistoryAlignment.Top)]
    [InlineData(1, ChatHistoryAlignment.Bottom)]
    [InlineData(3, ChatHistoryAlignment.Top)]
    [InlineData(3, ChatHistoryAlignment.Bottom)]
    public void A_history_shorter_than_the_viewport_draws_every_row(int rows, ChatHistoryAlignment alignment)
    {
        ChatBox box = Box(Numbered(rows));
        box.ShowTimestamps = false;
        box.HistoryAlignment = alignment;
        box.RefreshLayout(Narrow, Sydney);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        Assert.Equal(Names(0, rows), Texts(sink));
    }

    [Fact]
    public void A_nearly_full_bottom_aligned_history_draws_every_row()
    {
        ChatBox box = Box(Numbered(8), BoxBounds with { Height = 195f });
        box.ShowTimestamps = false;
        box.HistoryAlignment = ChatHistoryAlignment.Bottom;
        box.RefreshLayout(Narrow, Sydney);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        Assert.Equal(Names(0, 8), Texts(sink));
    }

    [Fact]
    public void A_font_change_remeasures_the_message_offset()
    {
        var history = new ChatHistory(8);
        history.Add(Entry(At(7), "a"));
        ChatBox box = Box(history);
        box.RefreshLayout(Narrow, Sydney);
        var before = new RecordingSink();
        box.DrawHistoryRows(ref before);

        box.RefreshLayout(Wide, Sydney);
        var after = new RecordingSink();
        box.DrawHistoryRows(ref after);

        // The eight-character stamp is 64 px at 8 px a character and 80 px at 10.
        Assert.Equal(new Vector2(172f, 108f), before.Runs[1].Position);
        Assert.Equal(new Vector2(188f, 108f), after.Runs[1].Position);
    }

    [Fact]
    public void A_width_change_resplits_the_rows_the_draw_sends()
    {
        var history = new ChatHistory(8);
        history.Add(Entry(At(7), "a", content: "the quick brown fox jumps over the lazy dog"));
        ChatBox box = Box(history);
        box.RefreshLayout(Narrow, Sydney);
        int wideLines = box.CachedLines.Count;

        box.Bounds = BoxBounds with { Width = 136f };   // 120 px of text, fifteen characters a row
        box.RefreshLayout(Narrow, Sydney);
        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        Assert.True(box.CachedLines.Count > wideLines);
        Assert.Equal(ExpectedRuns(box), Texts(sink));
    }

    // The rows whose bounds meet the viewport, grown by one row at each end and clamped to the history, which is
    // the range the draw promises. Computed from RowBounds, so it carries the same sparse-space offset.
    static List<string> ExpectedVisible(ChatBox box, int rows)
    {
        int first = -1, last = -1;
        for (int i = 0; i < rows; i++)
        {
            Rect row = box.RowBounds(i);
            if (row.Bottom <= Viewport.Y || row.Y >= Viewport.Bottom) continue;
            if (first < 0) first = i;
            last = i;
        }
        return first < 0 ? new List<string>() : Names(Math.Max(0, first - 1), Math.Min(rows, last + 2));
    }

    static List<string> Names(int first, int end)
    {
        var names = new List<string>();
        for (int i = first; i < end; i++) names.Add($"message {i}");
        return names;
    }

    static ChatHistory Numbered(int count)
    {
        var history = new ChatHistory(Math.Max(1, count));
        for (int i = 0; i < count; i++)
            history.Add(Entry(DateTimeOffset.UnixEpoch, $"source-{i}", content: $"message {i}", author: null));
        return history;
    }

    // One wheel frame over the history. A positive delta scrolls up by 30 px a notch.
    static void ScrollUp(ChatBox box, float notches)
    {
        var pointer = new Pointer();
        var input = new InputState(
            new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            new Vector2(120f, 120f), Vector2.Zero, notches, 960, 540);
        pointer.Update(input);
        box.Update(pointer, input, 0.016f);
    }

    // The runs one entry's cached lines should produce: its first line split after the stamp when it starts with
    // one, every other line whole, and empty runs skipped because drawing them paints nothing.
    static List<string> ExpectedRuns(ChatBox box)
    {
        var runs = new List<string>();
        for (int i = 0; i < box.CachedLines.Count; i++)
        {
            string line = box.CachedLines[i];
            if (i == 0 && line.StartsWith(Stamp, StringComparison.Ordinal))
            {
                runs.Add(Stamp);
                if (line.Length > Stamp.Length) runs.Add(line[Stamp.Length..]);
            }
            else if (line.Length > 0)
            {
                runs.Add(line);
            }
        }
        return runs;
    }

    static List<string> Texts(RecordingSink sink) => sink.Runs.ConvertAll(run => run.Text);

    static DateTimeOffset At(int minute) => new(2026, 9, 6, 1, minute, 0, TimeSpan.Zero);

    static ChatBox Box(ChatHistory history, Rect? bounds = null) => new(history, bounds ?? BoxBounds)
    {
        Theme = Theme,
    };

    static ChatEntry Entry(
        DateTimeOffset timestamp,
        string sourceKey,
        ChatEntryKind kind = ChatEntryKind.Ordinary,
        bool isOwn = false,
        string content = "hello",
        string? author = "Alice") => new(
            timestamp,
            sourceKey,
            author is null ? null : LocalizedText.Raw(author),
            LocalizedText.Raw(content),
            content,
            kind,
            isOwn);

    readonly record struct Drawn(string Text, Vector2 Position, Color Color);

    sealed class RecordingSink : IChatRowSink
    {
        public List<Drawn> Runs { get; } = new();

        public void DrawText(string text, Vector2 position, Color color) => Runs.Add(new Drawn(text, position, color));
    }

    sealed class FixedMeasurer : ITextMeasurer
    {
        readonly float _advance;

        public FixedMeasurer(float advance) => _advance = advance;

        public float LineHeight => 16f;

        public Vector2 Measure(string text) => new(text.Length * _advance, LineHeight);
    }
}
