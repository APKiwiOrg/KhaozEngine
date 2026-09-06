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

    static ChatBox Box(ChatHistory? history = null) => new(history ?? new ChatHistory(8), BoxBounds)
    {
        Theme = Theme,
    };

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
        int repeatCount = 1) => new(
            timestamp,
            "source",
            LocalizedText.Raw("Alice"),
            LocalizedText.Raw("hello"),
            "hello",
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
