using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui.Chat;

/// <summary>
/// Placement-neutral chat history and single-line composer. The host supplies design-space bounds, appends
/// entries to the retained <see cref="ChatHistory"/>, and handles submitted text through <see cref="Submitted"/>.
/// </summary>
public sealed class ChatBox
{
    const float Padding = 8f;
    const float ComposerHeight = 30f;
    const float ComposerGap = 6f;
    const float RowSpacing = 2f;

    readonly ChatHistory _history;
    readonly Panel _frame;
    readonly ScrollablePanel _scroll;
    readonly List<CachedRow> _rows = new();
    readonly List<string> _cachedLines = new();

    ChatBoxTheme _theme = ChatBoxTheme.Default;
    ITextMeasurer? _cachedMeasurer;
    Rect _cachedBounds;
    bool _cachedShowTimestamps;
    string _cachedCultureName = "";
    string _cachedTimeZoneId = "";

    /// <summary>The design-space rectangle occupied and reserved by the complete chatbox.</summary>
    public Rect Bounds;

    /// <summary>The font used for history and composer text. A null font leaves text drawing disabled.</summary>
    public SpriteFont? Font { get; set; }

    /// <summary>Colours used by the chatbox. Replacing the theme takes effect on the next draw.</summary>
    public ChatBoxTheme Theme
    {
        get => _theme;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _theme = value;
        }
    }

    /// <summary>Whether each entry starts with its timestamp converted to local time.</summary>
    public bool ShowTimestamps { get; set; } = true;

    /// <summary>True while the composer owns keyboard input.</summary>
    public bool OwnsKeyboard => Composer.IsFocused;

    /// <summary>True while the composer is open and focused.</summary>
    public bool ComposerOpen => Composer.IsFocused;

    /// <summary>Receives each non-empty trimmed line submitted by the player.</summary>
    public Action<string>? Submitted { get; set; }

    internal TextInput Composer { get; }
    internal long CachedHistoryVersion { get; private set; } = -1;
    internal IReadOnlyList<string> CachedLines => _cachedLines;

    /// <summary>Create a chatbox over retained <paramref name="history"/> at the supplied design-space bounds.</summary>
    public ChatBox(ChatHistory history, Rect bounds, SpriteFont? font = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        _history = history;
        Bounds = bounds;
        Font = font;
        _frame = new Panel(bounds) { BorderThickness = 1f };
        _scroll = new ScrollablePanel(bounds)
        {
            BlocksPointer = true,
            ItemSpacing = RowSpacing,
        };
        Composer = new TextInput(bounds, font);
        SyncGeometry();
    }

    /// <summary>
    /// Reserve the complete box, update scrollback, and process composer ownership. Enter opens a closed
    /// composer. A later Enter submits and stays open. Escape clears and closes it.
    /// </summary>
    public void Update(Pointer pointer, InputState input, float dt)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentNullException.ThrowIfNull(input);

        SyncGeometry();
        if (Font != null) RefreshLayout(Font, TimeZoneInfo.Local);

        pointer.BlockRegion(Bounds);
        _scroll.Update(pointer, input, dt);

        bool openedThisFrame = false;
        if (!Composer.IsFocused && input.WasPressed(Key.Enter))
        {
            Composer.Focus();
            openedThisFrame = true;
        }

        if (Composer.IsFocused)
            Composer.Update(pointer, input, dt);

        if (!openedThisFrame && Composer.IsFocused && input.WasPressed(Key.Enter))
        {
            string text = Composer.Text.Trim();
            if (text.Length > 0)
                Submitted?.Invoke(text);
            Composer.SetText("");
        }

        if (Composer.IsFocused && input.WasPressed(Key.Escape))
        {
            Composer.SetText("");
            Composer.Unfocus();
        }
    }

    /// <summary>Draw the frame, clipped history rows, and composer.</summary>
    public void Draw(SpriteBatch batch, Texture2D white)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(white);

        SyncGeometry();
        ApplyTheme();
        _frame.Draw(batch, white);

        SpriteFont? font = Font;
        if (font != null)
        {
            RefreshLayout(font, TimeZoneInfo.Local);
            _scroll.BeginClip(batch);
            for (int i = 0; i < _rows.Count; i++)
                DrawRow(batch, font, i, _rows[i]);
            _scroll.EndClip(batch);
        }

        Composer.Draw(batch, white);
    }

    void DrawRow(SpriteBatch batch, SpriteFont font, int index, CachedRow row)
    {
        Rect bounds = _scroll.ItemBounds(index);
        var position = new Vector2(MathF.Floor(bounds.X), MathF.Floor(bounds.Y));
        Vector4 messageColor = SelectColor(row.Entry, Theme);

        if (row.TimestampLength <= 0)
        {
            batch.DrawString(font, row.Text, position, (Color)messageColor);
            return;
        }

        string timestamp = row.Text[..row.TimestampLength];
        batch.DrawString(font, timestamp, position, (Color)Theme.TimestampText);
        if (row.TimestampLength == row.Text.Length) return;

        string message = row.Text[row.TimestampLength..];
        position.X += font.Measure(timestamp).X;
        batch.DrawString(font, message, position, (Color)messageColor);
    }

    void SyncGeometry()
    {
        _frame.Bounds = Bounds;

        float innerWidth = MathF.Max(0f, Bounds.Width - Padding * 2f);
        float historyHeight = MathF.Max(0f, Bounds.Height - Padding * 2f - ComposerHeight - ComposerGap);
        _scroll.Bounds = new Rect(Bounds.X + Padding, Bounds.Y + Padding, innerWidth, historyHeight);
        Composer.Bounds = new Rect(
            Bounds.X + Padding,
            Bounds.Bottom - Padding - ComposerHeight,
            innerWidth,
            ComposerHeight);
        Composer.Font = Font;
    }

    void ApplyTheme()
    {
        _frame.Color = Theme.Background;
        _frame.BorderColor = Theme.Border;
        Composer.Background = Theme.ComposerBackground;
        Composer.Border = Theme.ComposerBorder;
        Composer.BorderFocused = Theme.ComposerFocusedBorder;
        Composer.TextColor = Theme.OrdinaryText;
    }

    internal void RefreshLayout(ITextMeasurer measurer, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(measurer);
        ArgumentNullException.ThrowIfNull(timeZone);
        SyncGeometry();

        string cultureName = CultureInfo.CurrentUICulture.Name;
        if (CachedHistoryVersion == _history.Version
            && _cachedBounds == Bounds
            && ReferenceEquals(_cachedMeasurer, measurer)
            && _cachedShowTimestamps == ShowTimestamps
            && string.Equals(_cachedCultureName, cultureName, StringComparison.Ordinal)
            && string.Equals(_cachedTimeZoneId, timeZone.Id, StringComparison.Ordinal))
            return;

        float previousMaxScroll = _scroll.MaxScroll;
        bool wasAtBottom = CachedHistoryVersion < 0 || _scroll.ScrollOffset >= previousMaxScroll - 0.5f;
        _rows.Clear();
        _cachedLines.Clear();

        float wrapWidth = MathF.Max(1f, _scroll.ContentBounds.Width);
        foreach (ChatEntry entry in _history.Entries)
        {
            string prefix = FormatPrefix(entry, ShowTimestamps, timeZone);
            string text = FormatText(entry, ShowTimestamps, timeZone);
            List<string> lines = TextLayout.Wrap(
                measurer,
                text,
                wrapWidth,
                hardBreak: true,
                preserveSpaceRuns: true);

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                int timestampLength = i == 0 && line.StartsWith(prefix, StringComparison.Ordinal)
                    ? Math.Min(prefix.Length, line.Length)
                    : 0;
                _rows.Add(new CachedRow(line, entry, timestampLength));
                _cachedLines.Add(line);
            }
        }

        _scroll.ItemCount = _rows.Count;
        _scroll.ItemHeight = measurer.LineHeight;
        _scroll.ItemSpacing = RowSpacing;
        _scroll.ScrollTo(wasAtBottom ? _scroll.MaxScroll : _scroll.ScrollOffset);

        CachedHistoryVersion = _history.Version;
        _cachedBounds = Bounds;
        _cachedMeasurer = measurer;
        _cachedShowTimestamps = ShowTimestamps;
        _cachedCultureName = cultureName;
        _cachedTimeZoneId = timeZone.Id;
    }

    internal static string FormatPrefix(ChatEntry entry, bool showTimestamps, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        if (!showTimestamps) return "";
        DateTimeOffset local = TimeZoneInfo.ConvertTime(entry.TimestampUtc, timeZone);
        return $"[{local.ToString("HH:mm", CultureInfo.InvariantCulture)}] ";
    }

    internal static string FormatText(ChatEntry entry, bool showTimestamps, TimeZoneInfo timeZone)
    {
        string author = entry.Author is { } value ? value.Resolve() : "";
        string authorPrefix = author.Length > 0 ? author + ": " : "";
        string repeatSuffix = entry.RepeatCount > 1
            ? $" ({entry.RepeatCount.ToString(CultureInfo.InvariantCulture)})"
            : "";
        return FormatPrefix(entry, showTimestamps, timeZone)
            + authorPrefix
            + entry.Content.Resolve()
            + repeatSuffix;
    }

    internal static Vector4 SelectColor(ChatEntry entry, ChatBoxTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (entry.Kind == ChatEntryKind.System) return theme.SystemText;
        return entry.IsOwn ? theme.OwnText : theme.OrdinaryText;
    }

    readonly record struct CachedRow(string Text, ChatEntry Entry, int TimestampLength);
}
