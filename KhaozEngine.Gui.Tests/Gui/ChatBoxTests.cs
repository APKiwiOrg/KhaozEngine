using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui.Chat;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Tests.App;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

[Collection("AmbientLocalization")]
public sealed class ChatBoxTests
{
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
    static readonly FixedMeasurer Font = new();

    [Fact]
    public void Composer_placeholder_resolves_through_the_ambient_catalog()
    {
        IStringCatalog? previous = LocalizationContext.Catalog;
        try
        {
            var box = Box();
            box.ComposerPlaceholder = new StringId("Chat.Placeholder");
            LocalizationContext.Catalog = new DictionaryCatalog().Add("Chat.Placeholder", "Say something");

            Assert.Equal("Say something", box.Composer.PlaceholderContent.Resolve());

            LocalizationContext.Catalog = new DictionaryCatalog().Add("Chat.Placeholder", "Dis quelque chose");
            Assert.Equal("Dis quelque chose", box.Composer.PlaceholderContent.Resolve());
        }
        finally
        {
            LocalizationContext.Catalog = previous;
        }
    }

    [Fact]
    public void Composer_prefix_defaults_empty_and_forwards_localized_content()
    {
        IStringCatalog? previous = LocalizationContext.Catalog;
        try
        {
            var box = Box();
            Assert.Equal("", box.ComposerPrefix.Resolve());

            box.ComposerPrefix = new StringId("Chat.PlayerPrefix");
            LocalizationContext.Catalog = new DictionaryCatalog().Add("Chat.PlayerPrefix", "Alice: ");
            Assert.Equal("Alice: ", box.ComposerPrefix.Resolve());
            Assert.Equal(box.ComposerPrefix, box.Composer.PrefixContent);

            LocalizationContext.Catalog = new DictionaryCatalog().Add("Chat.PlayerPrefix", "Alicia: ");
            Assert.Equal("Alicia: ", box.ComposerPrefix.Resolve());
        }
        finally
        {
            LocalizationContext.Catalog = previous;
        }
    }

    [Fact]
    public void Max_input_length_limits_typed_composer_input()
    {
        var box = Box();
        var pointer = new Pointer();
        box.MaxInputLength = 2;

        Update(box, pointer, Press(Key.Enter));
        Update(box, pointer, Press(Key.A));
        Update(box, pointer, Press(Key.B));
        Update(box, pointer, Press(Key.C));

        Assert.Equal("ab", box.Composer.Text);
    }

    [Fact]
    public void Lowering_max_input_length_reclamps_existing_text_as_a_change()
    {
        var box = OpenBox("abcdef");
        var pointer = new Pointer();
        Update(box, pointer, InputState.Empty);

        box.MaxInputLength = 3;
        Update(box, pointer, InputState.Empty);

        Assert.Equal("abc", box.Composer.Text);
        Assert.True(box.Composer.TextChanged);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Max_input_length_rejects_non_positive_values(int value)
    {
        var box = Box();

        Assert.Throws<ArgumentOutOfRangeException>(() => box.MaxInputLength = value);
    }

    [Fact]
    public void Max_input_length_preserves_the_existing_32_character_default()
    {
        var box = Box();
        box.Composer.SetText(new string('x', 33));

        Assert.Equal(32, box.MaxInputLength);
        Assert.Equal(new string('x', 32), box.Composer.Text);
    }

    [Fact]
    public void Enter_opens_without_sending_then_sends_and_stays_open()
    {
        var box = Box();
        var pointer = new Pointer();
        var sent = new List<string>();
        box.Submitted = sent.Add;

        Update(box, pointer, Press(Key.Enter));

        Assert.True(box.ComposerOpen);
        Assert.True(box.OwnsKeyboard);
        Assert.Empty(sent);

        box.Composer.SetText("  hello  ");
        Update(box, pointer, Release(Key.Enter));
        Update(box, pointer, Press(Key.Enter));

        Assert.Equal(new[] { "hello" }, sent);
        Assert.True(box.ComposerOpen);
        Assert.Equal("", box.Composer.Text);
    }

    [Fact]
    public void Composer_prefix_does_not_open_focus_or_join_the_submitted_value()
    {
        var box = Box();
        var pointer = new Pointer();
        var sent = new List<string>();
        box.ComposerPrefix = LocalizedText.Raw("Alice: ");
        box.Submitted = sent.Add;

        Assert.False(box.ComposerOpen);
        Assert.False(box.OwnsKeyboard);

        Update(box, pointer, Press(Key.Enter));
        box.Composer.SetText("hello");
        Update(box, pointer, Release(Key.Enter));
        Update(box, pointer, Press(Key.Enter));

        Assert.Equal(new[] { "hello" }, sent);
        Assert.True(box.ComposerOpen);
        Assert.True(box.OwnsKeyboard);
        Assert.Equal("", box.Composer.Text);
        Assert.Equal("Alice: ", box.ComposerPrefix.Resolve());
    }

    [Fact]
    public void Escape_clears_and_closes_the_composer()
    {
        var box = OpenBox("unsent");
        var pointer = new Pointer();

        Update(box, pointer, Press(Key.Escape));

        Assert.False(box.ComposerOpen);
        Assert.False(box.OwnsKeyboard);
        Assert.Equal("", box.Composer.Text);
    }

    [Fact]
    public void Empty_input_is_cleared_without_submission()
    {
        var box = OpenBox("   ");
        var pointer = new Pointer();
        var sent = new List<string>();
        box.Submitted = sent.Add;

        Update(box, pointer, Press(Key.Enter));

        Assert.Empty(sent);
        Assert.True(box.ComposerOpen);
        Assert.Equal("", box.Composer.Text);
    }

    [Fact]
    public void Pointer_movement_and_wheel_over_the_full_box_are_reserved()
    {
        var box = Box();
        var pointer = new Pointer();
        InputState input = Frame(
            new Vector2(BoxBounds.X + 2f, BoxBounds.Y + 2f),
            scroll: -1f,
            mouseDelta: new Vector2(6f, -4f));

        Update(box, pointer, input);

        Assert.False(box.ComposerOpen);
        Assert.True(pointer.IsBlocked(input.MousePosition));
    }

    [Fact]
    public void Outside_pointer_input_remains_available_while_closed()
    {
        var box = Box();
        var pointer = new Pointer();
        InputState input = Frame(new Vector2(20f, 20f), scroll: -1f, mouseDelta: new Vector2(2f, 3f));

        Update(box, pointer, input);

        Assert.False(box.ComposerOpen);
        Assert.False(pointer.IsBlocked(input.MousePosition));
    }

    [Fact]
    public void Timestamp_and_style_are_presentation_only()
    {
        ChatEntry own = Entry(new DateTimeOffset(2026, 9, 6, 1, 7, 0, TimeSpan.Zero), isOwn: true);
        ChatEntry ordinary = Entry(new DateTimeOffset(2026, 9, 6, 1, 7, 0, TimeSpan.Zero));
        ChatEntry system = Entry(
            new DateTimeOffset(2026, 9, 6, 1, 7, 0, TimeSpan.Zero),
            kind: ChatEntryKind.System,
            isOwn: true);

        Assert.Equal("[11:07] ", ChatBox.FormatPrefix(own, true, Sydney));
        Assert.Equal("", ChatBox.FormatPrefix(own, false, Sydney));
        Assert.Equal(Theme.OwnText, ChatBox.SelectColor(own, Theme));
        Assert.Equal(Theme.OrdinaryText, ChatBox.SelectColor(ordinary, Theme));
        Assert.Equal(Theme.SystemText, ChatBox.SelectColor(system, Theme));
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 1, 7, 0, TimeSpan.Zero), own.TimestampUtc);
    }

    [Fact]
    public void Repeated_entry_appends_the_count_to_the_final_message()
    {
        ChatEntry entry = Entry(
            new DateTimeOffset(2026, 9, 6, 1, 7, 0, TimeSpan.Zero),
            repeatCount: 4);

        Assert.Equal("[11:07] Alice: hello (4)", ChatBox.FormatText(entry, true, Sydney));
    }

    [Fact]
    public void Collapsing_an_entry_rebuilds_the_cached_layout_at_the_new_history_version()
    {
        var history = new ChatHistory(8);
        history.Add(Entry(new DateTimeOffset(2026, 9, 6, 1, 7, 0, TimeSpan.Zero)));
        var box = Box(history);
        box.RefreshLayout(Font, Sydney);
        long before = box.CachedHistoryVersion;

        history.Add(Entry(new DateTimeOffset(2026, 9, 6, 1, 8, 0, TimeSpan.Zero)));
        box.RefreshLayout(Font, Sydney);

        Assert.Equal(1, before);
        Assert.Equal(2, box.CachedHistoryVersion);
        Assert.Equal(new[] { "[11:08] Alice: hello (2)" }, box.CachedLines);
    }

    [Fact]
    public void History_alignment_defaults_to_top_for_sparse_rows()
    {
        var box = Box(HistoryWithLines(1));
        box.ShowTimestamps = false;

        box.RefreshLayout(Font, Sydney);

        Assert.Equal(ChatHistoryAlignment.Top, box.HistoryAlignment);
        Assert.Equal(new Rect(108f, 108f, 284f, 16f), box.RowBounds(0));
    }

    [Fact]
    public void Bottom_alignment_keeps_an_empty_history_empty()
    {
        var box = Box();
        box.HistoryAlignment = ChatHistoryAlignment.Bottom;

        box.RefreshLayout(Font, Sydney);

        Assert.Empty(box.CachedLines);
    }

    [Theory]
    [InlineData(1, 220f)]
    [InlineData(2, 202f)]
    public void Bottom_alignment_grows_sparse_rows_upward_in_oldest_to_newest_order(int count, float firstY)
    {
        var box = Box(HistoryWithLines(count));
        box.ShowTimestamps = false;
        box.HistoryAlignment = ChatHistoryAlignment.Bottom;

        box.RefreshLayout(Font, Sydney);

        Assert.Equal(firstY, box.RowBounds(0).Y);
        Assert.Equal(236f, box.RowBounds(count - 1).Bottom);
        for (int i = 1; i < count; i++)
            Assert.True(box.RowBounds(i - 1).Y < box.RowBounds(i).Y);
    }

    [Fact]
    public void Bottom_alignment_places_wrapped_sparse_rows_against_the_viewport_bottom()
    {
        var history = new ChatHistory(8);
        history.Add(Entry(DateTimeOffset.UnixEpoch, content: new string('x', 40), author: null));
        var box = Box(history);
        box.ShowTimestamps = false;
        box.HistoryAlignment = ChatHistoryAlignment.Bottom;

        box.RefreshLayout(Font, Sydney);

        Assert.Equal(2, box.CachedLines.Count);
        Assert.Equal(202f, box.RowBounds(0).Y);
        Assert.Equal(236f, box.RowBounds(1).Bottom);
    }

    [Fact]
    public void Bottom_alignment_places_the_last_nearly_full_sparse_row_at_the_viewport_bottom()
    {
        var box = Box(HistoryWithLines(8), BoxBounds with { Height = 195f });
        box.ShowTimestamps = false;
        box.HistoryAlignment = ChatHistoryAlignment.Bottom;

        box.RefreshLayout(Font, Sydney);

        Assert.Equal(251f, box.RowBounds(7).Bottom);
    }

    [Theory]
    [InlineData(194f)]
    [InlineData(180f)]
    public void Full_and_overflowing_histories_have_identical_bounds_and_scrolling(float boxHeight)
    {
        ChatHistory history = HistoryWithLines(8);
        Rect bounds = BoxBounds with { Height = boxHeight };
        var top = Box(history, bounds);
        var bottom = Box(history, bounds);
        top.ShowTimestamps = false;
        bottom.ShowTimestamps = false;
        bottom.HistoryAlignment = ChatHistoryAlignment.Bottom;
        top.RefreshLayout(Font, Sydney);
        bottom.RefreshLayout(Font, Sydney);

        Rect[] before = RowBounds(top, 8);
        Assert.Equal(before, RowBounds(bottom, 8));

        Update(top, new Pointer(), Frame(new Vector2(120f, 120f), scroll: 1f));
        Update(bottom, new Pointer(), Frame(new Vector2(120f, 120f), scroll: 1f));

        Rect[] after = RowBounds(top, 8);
        Assert.NotEqual(before, after);
        Assert.Equal(after, RowBounds(bottom, 8));
    }

    [Fact]
    public void Opening_and_closing_the_composer_does_not_move_bottom_aligned_history()
    {
        var box = Box(HistoryWithLines(1));
        var pointer = new Pointer();
        box.ShowTimestamps = false;
        box.HistoryAlignment = ChatHistoryAlignment.Bottom;
        box.RefreshLayout(Font, Sydney);
        Rect closed = box.RowBounds(0);

        Update(box, pointer, Press(Key.Enter));
        Rect open = box.RowBounds(0);
        Update(box, pointer, Release(Key.Enter));
        Update(box, pointer, Press(Key.Escape));

        Assert.Equal(closed, open);
        Assert.Equal(closed, box.RowBounds(0));
    }

    static ChatBox Box(ChatHistory? history = null, Rect? bounds = null) => new(history ?? new ChatHistory(8), bounds ?? BoxBounds)
    {
        Theme = Theme,
    };

    static ChatHistory HistoryWithLines(int count)
    {
        var history = new ChatHistory(Math.Max(1, count));
        for (int i = 0; i < count; i++)
            history.Add(Entry(DateTimeOffset.UnixEpoch, content: $"message {i}", sourceKey: $"source-{i}"));
        return history;
    }

    static Rect[] RowBounds(ChatBox box, int count)
    {
        var rows = new Rect[count];
        for (int i = 0; i < count; i++) rows[i] = box.RowBounds(i);
        return rows;
    }

    static ChatBox OpenBox(string text)
    {
        ChatBox box = Box();
        box.Composer.Focus();
        box.Composer.SetText(text);
        return box;
    }

    static ChatEntry Entry(
        DateTimeOffset timestamp,
        ChatEntryKind kind = ChatEntryKind.Ordinary,
        bool isOwn = false,
        int repeatCount = 1,
        string content = "hello",
        string sourceKey = "source",
        string? author = "Alice") => new(
            timestamp,
            sourceKey,
            author is null ? null : LocalizedText.Raw(author),
            LocalizedText.Raw(content),
            content,
            kind,
            isOwn,
            repeatCount);

    static void Update(ChatBox box, Pointer pointer, InputState input)
    {
        pointer.Update(input);
        box.Update(pointer, input, 0.016f);
    }

    static InputState Press(Key key) => Frame(Vector2.Zero, pressed: new[] { key });

    static InputState Release(Key key) => Frame(Vector2.Zero, released: new[] { key });

    static InputState Frame(
        Vector2 position,
        IEnumerable<Key>? pressed = null,
        IEnumerable<Key>? released = null,
        float scroll = 0f,
        Vector2 mouseDelta = default)
    {
        var keysPressed = new HashSet<Key>(pressed ?? Array.Empty<Key>());
        return new InputState(
            keysPressed,
            keysPressed,
            new HashSet<Key>(released ?? Array.Empty<Key>()),
            new HashSet<MouseButton>(),
            new HashSet<MouseButton>(),
            position,
            mouseDelta,
            scroll,
            960,
            540);
    }

    sealed class FixedMeasurer : ITextMeasurer
    {
        public float LineHeight => 16f;

        public Vector2 Measure(string text) => new(text.Length * 8f, LineHeight);
    }
}
