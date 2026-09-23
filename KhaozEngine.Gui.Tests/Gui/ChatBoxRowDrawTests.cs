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
        history.Add(Entry(At(7), "a", content: new string('x', 40), author: null));
        ChatBox box = Box(history);
        box.RefreshLayout(Narrow, Sydney);

        var sink = new RecordingSink();
        box.DrawHistoryRows(ref sink);

        Assert.True(box.CachedLines.Count > 1);
        Assert.Equal(ExpectedRuns(box), Texts(sink));
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
