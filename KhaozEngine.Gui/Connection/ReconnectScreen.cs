using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui;

/// <summary>One button in a <see cref="ReconnectScreen"/>'s action row (e.g. "Cancel", "Quit").</summary>
public readonly struct ReconnectAction
{
    /// <summary>The button caption.</summary>
    public LocalizedText Label { get; init; }

    /// <summary>Invoked when the button is tapped. May be null for a no-op button.</summary>
    public Action? OnInvoke { get; init; }

    /// <summary>Creates an action.</summary>
    /// <param name="label">The button caption.</param>
    /// <param name="onInvoke">Invoked on tap, or null for a no-op button.</param>
    public ReconnectAction(LocalizedText label, Action? onInvoke = null)
    {
        Label = label;
        OnInvoke = onInvoke;
    }
}

/// <summary>
/// A full-screen, themeable, asset-free modal <see cref="Screen"/> shown while the game is disconnected or the
/// server is undergoing planned maintenance. Polls a <see cref="ConnectionStatusView"/> supplier every
/// <see cref="Draw"/> (typically wired to the latest <see cref="ConnectionStatusController.Update"/> result) and
/// renders a scrim, a title, a countdown or attempt/retry lines, a reassurance line, an indeterminate spinner,
/// and an optional row of action buttons (e.g. Cancel / Quit). Sits below <see cref="UpdateOverlayScreen"/> in
/// draw order, so a required client update still wins over a connection takeover.
/// </summary>
public sealed class ReconnectScreen : Screen
{
    readonly Texture2D _white;
    readonly SpriteFont _font;
    readonly IDesignViewport _viewport;
    readonly ReconnectScreenTheme _theme;
    readonly Func<ConnectionStatusView> _currentView;
    readonly IReadOnlyList<ReconnectAction> _actions;
    readonly List<Button> _buttons = new();

    float _elapsed;

    ReconnectScreen(
        Texture2D white, SpriteFont font, IDesignViewport viewport,
        ReconnectScreenTheme theme, Func<ConnectionStatusView> currentView,
        IReadOnlyList<ReconnectAction> actions)
    {
        _white = white;
        _font = font;
        _viewport = viewport;
        _theme = theme;
        _currentView = currentView;
        _actions = actions;
        DrawOrder = 9_000;          // below UpdateOverlayScreen's 10_000: a required update still wins
        PassUpdateThrough = false;  // modal: suppresses world input while the takeover is up
        TransitionOnDuration = 0.15f;
        TransitionOffDuration = 0.12f;
    }

    /// <summary>
    /// Creates a <see cref="ReconnectScreen"/>. The screen renders unconditionally from whatever
    /// <paramref name="currentView"/> returns (it does not gate its own drawing on
    /// <see cref="ConnectionStatusView.Mode"/>), so the caller controls visibility by push/pop on the
    /// <see cref="ScreenStack"/> - this keeps the screen rendering through its own exit transition instead of
    /// blanking for a frame when the consumer pops it.
    /// </summary>
    /// <param name="white">A 1x1 white texture used for every fill.</param>
    /// <param name="font">The font used for every label.</param>
    /// <param name="viewport">The design viewport to size the scrim from and center content within.</param>
    /// <param name="currentView">Supplies the current connection-status view each frame.</param>
    /// <param name="theme">The visual theme, or <see cref="ReconnectScreenTheme.Default"/> when null (the default).</param>
    /// <param name="actions">Optional action buttons (e.g. Cancel / Quit), or none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="white"/>, <paramref name="font"/>,
    /// <paramref name="viewport"/>, or <paramref name="currentView"/> is null.</exception>
    public static ReconnectScreen Create(
        Texture2D white, SpriteFont font, IDesignViewport viewport,
        Func<ConnectionStatusView> currentView,
        ReconnectScreenTheme? theme = null,
        IReadOnlyList<ReconnectAction>? actions = null)
    {
        ArgumentNullException.ThrowIfNull(white);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(currentView);
        theme ??= ReconnectScreenTheme.Default;
        return new ReconnectScreen(white, font, viewport, theme, currentView, actions ?? Array.Empty<ReconnectAction>());
    }

    /// <summary>Builds one retained <see cref="Button"/> per action, laid out as a horizontally centred row.</summary>
    public override void LoadContent()
    {
        _buttons.Clear();
        int count = _actions.Count;
        if (count == 0) return;

        Rect design = _viewport.DesignBounds;
        float cx = design.X + design.Width * 0.5f;
        float y = design.Y + design.Height * 0.5f + 110f;
        float totalWidth = count * _theme.ButtonWidth + (count - 1) * _theme.ButtonGap;
        float x = cx - totalWidth * 0.5f;

        foreach (ReconnectAction action in _actions)
        {
            var bounds = new Rect(x, y, _theme.ButtonWidth, _theme.ButtonHeight);
            _buttons.Add(new Button(bounds, action.Label, _font, action.OnInvoke) { Style = _theme.ButtonStyle });
            x += _theme.ButtonWidth + _theme.ButtonGap;
        }
    }

    /// <inheritdoc />
    public override bool Update(float dt, bool receivesInput)
    {
        // Wrapped modulo the spin period so the clock stays bounded however long an outage runs.
        float period = 1f / MathF.Max(_theme.SpinnerSpeedHz, 0.0001f);
        _elapsed = (_elapsed + MathF.Max(dt, 0f)) % period;

        for (int i = 0; i < _buttons.Count; i++)
        {
            // Pre-resolve through the fallback catalog every frame (not just once in LoadContent): Button
            // resolves only against the ambient catalog, which would miss the engine English fallback, and
            // re-resolving here keeps a runtime locale switch working.
            _buttons[i].Content = LocalizedText.Raw(Resolve(_actions[i].Label));
            if (receivesInput) _buttons[i].Update(Manager.Pointer);
        }

        return receivesInput; // modal: consumes input, and never true when receivesInput is false
    }

    /// <inheritdoc />
    public override void Draw(SpriteBatch batch)
    {
        // Poll unconditionally: the consumer pops the screen by latch, and this must keep rendering through its
        // own exit transition rather than blanking for a frame.
        ConnectionStatusView view = _currentView();

        Vector4 scrim = _theme.Scrim;
        scrim.W *= TransitionAlpha;
        // WindowBounds, not DesignBounds: under a letterbox scale the fill must cover the whole window
        // (bars included), so the game never shows through at the edges.
        batch.Draw(_white, _viewport.WindowBounds, (Color)scrim);

        _theme.DrawBackground?.Invoke(batch, _white, _viewport.DesignBounds);

        Rect design = _viewport.DesignBounds;
        if (design.Width <= 0f || design.Height <= 0f) return; // nothing sensible to centre content within

        float cx = design.X + design.Width * 0.5f;
        float cy = design.Y + design.Height * 0.5f;

        // Recomputed every frame from the absolute EtaUtc (never accumulated), so it cannot drift or freeze.
        bool hasCountdown = view.EtaUtc is { } eta && eta - DateTime.UtcNow > TimeSpan.Zero;

        // The at/after-zero clamp: an expired ETA falls back to the reconnecting title and shows no timer,
        // never a negative one.
        LocalizedText title = view.Kind == ConnectionStatusKind.PlannedUpdate && hasCountdown
            ? _theme.PlannedUpdateTitle
            : _theme.ReconnectingTitle;
        DrawCentered(batch, Resolve(title), cx, cy - 92f, _theme.TitleColor, _theme.TitleScale);

        if (hasCountdown)
        {
            double remainingSeconds = (view.EtaUtc!.Value - DateTime.UtcNow).TotalSeconds;
            int totalSeconds = (int)Math.Ceiling(Math.Max(remainingSeconds, 0d));
            int minutes = totalSeconds / 60;
            int seconds = totalSeconds % 60;
            string mmss = string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}", minutes, seconds);
            DrawCentered(batch, Resolve(CountdownText(mmss)), cx, cy - 40f, _theme.CountdownColor, _theme.CountdownScale);
        }
        else
        {
            if (view.Attempt > 0)
            {
                string attemptLine = Resolve(LocalizedText.Of(_theme.AttemptLineFormat, view.Attempt));
                DrawCentered(batch, attemptLine, cx, cy - 30f, _theme.BodyColor, _theme.BodyScale);
            }
            if (view.SecondsUntilRetry is { } secondsUntilRetry)
            {
                int retrySeconds = (int)MathF.Ceiling(MathF.Max(secondsUntilRetry, 0f));
                string retryLine = Resolve(LocalizedText.Of(_theme.RetryLineFormat, retrySeconds));
                DrawCentered(batch, retryLine, cx, cy + 2f, _theme.BodyColor, _theme.BodyScale);
            }
        }

        DrawCentered(batch, Resolve(_theme.Reassurance), cx, cy + 60f, _theme.BodyColor, _theme.BodyScale);

        if (_theme.ShowSpinner)
            DrawSpinner(batch, cx, cy - 150f);

        foreach (Button button in _buttons)
            button.Draw(batch, _white);
    }

    // N small axis-aligned squares placed on a circle (positions from cos/sin - this Draw overload has no
    // rotation, which is exactly why a ring of dots stands in for a rotated segment). Per-dot alpha pulses on a
    // phase offset so one bright dot appears to chase around the ring.
    void DrawSpinner(SpriteBatch batch, float centerX, float centerY)
    {
        int count = Math.Max(_theme.SpinnerDotCount, 1);
        float side = _theme.SpinnerRadius * 0.22f;

        for (int i = 0; i < count; i++)
        {
            float angle = i / (float)count * MathF.Tau;
            float x = centerX + MathF.Cos(angle) * _theme.SpinnerRadius;
            float y = centerY + MathF.Sin(angle) * _theme.SpinnerRadius;

            float phase = _elapsed * _theme.SpinnerSpeedHz - i / (float)count;
            float frac = phase - MathF.Floor(phase);

            Vector4 c = _theme.SpinnerColor;
            c.W *= frac * TransitionAlpha;
            batch.Draw(_white, new Rect(x - side * 0.5f, y - side * 0.5f, side, side), (Color)c);
        }
    }

    // The countdown digits are a non-localizable numeric token (not player-facing prose), kept greppable via the
    // Raw escape hatch and marked exempt, mirroring ServerStatusScreen.ReadoutValue: numbers are not localized.
    [LocalizationExempt]
    static LocalizedText CountdownText(string mmss) => LocalizedText.Raw(mmss);

    // Resolve through the fallback catalog: an engine reconnect.* key shows English with no wired catalog, a
    // game's own key resolves through its wired catalog, and a Raw value passes through verbatim.
    static string Resolve(LocalizedText text) => text.Resolve(ReconnectStrings.FallbackCatalog);

    void DrawCentered(SpriteBatch batch, string text, float centerX, float y, Vector4 color, float scale)
    {
        if (string.IsNullOrEmpty(text)) return;
        Vector2 size = _font.Measure(text) * scale;
        batch.DrawString(_font, text, new Vector2(centerX - size.X * 0.5f, y), (Color)color, scale);
    }
}
