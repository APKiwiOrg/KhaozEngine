using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Updates;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// Reusable in-game update-notification overlay: a pure presenter over <see cref="IUpdateStatus"/>. It
/// renders the current update state (available / downloading / ready / applying / failed) as a centred panel
/// with a progress bar, and raises <see cref="OnTrigger"/> when the bound key/button is pressed while a panel
/// is shown. It never calls the service itself: wire <see cref="OnTrigger"/> to
/// <c>KhaozEngine.Updates.UpdateOverlayActions.Trigger</c>. Headless-testable: <see cref="Update"/> needs no
/// GPU. Drop it into any Gui layer, or use <see cref="UpdateOverlayScreen"/> for stack-based games.
///
/// <para>The second bound key (<see cref="UpdateOverlayTheme.DismissKey"/>) puts the panel away: see
/// <see cref="IsShowing"/> for what a dismissal covers and how the panel comes back.</para>
/// </summary>
public sealed class UpdateOverlayView
{
    public UpdateOverlayTheme Theme { get; set; }

    /// <summary>Raised with the current state when the trigger key/button is pressed while visible.</summary>
    public event Action<UpdateState>? OnTrigger;
    /// <summary>Paramless convenience; raised alongside <see cref="OnTrigger"/>.</summary>
    public event Action? Triggered;
    /// <summary>Raised with the state that was declined when the dismiss key/button puts the panel away.</summary>
    public event Action<UpdateState>? OnDismiss;

    float _alpha;      // current fade, 0..1
    int _dismissed;    // bitmask of declined UpdateState values, 1 << (int)state

    public UpdateOverlayView(UpdateOverlayTheme? theme = null) => Theme = theme ?? UpdateOverlayTheme.Default;

    /// <summary>Current fade alpha (0 hidden .. 1 shown); exposed for tests/diagnostics.</summary>
    public float Alpha => _alpha;

    /// <summary>True on the frame the trigger fired (key/button pressed while a panel is showing); reset every
    /// <see cref="Update"/>. <see cref="UpdateOverlayScreen"/> reads it to consume input only on that frame while
    /// non-modal, so an optional prompt never swallows the game's own input.</summary>
    internal bool TriggeredThisFrame { get; private set; }

    /// <summary>True on the frame the dismiss key/button put the panel away; reset every <see cref="Update"/>.
    /// Consumed the same way as <see cref="TriggeredThisFrame"/>, so the dismiss press does not also reach the
    /// game (an Escape that closes the overlay must not open the pause menu in the same frame).</summary>
    internal bool DismissedThisFrame { get; private set; }

    /// <summary>States that show a panel. Idle/Checking are hidden. Whether a shown panel is modal is decided by
    /// the host (see <see cref="UpdateOverlayScreen"/>: modal only for a required update or the apply step).
    /// This is the raw state map: <see cref="IsShowing"/> is what the view actually draws, because it also
    /// accounts for a dismissal.</summary>
    public static bool IsVisible(UpdateState state) => state is
        UpdateState.UpdateAvailable or UpdateState.Downloading or UpdateState.ReadyToApply
        or UpdateState.Applying or UpdateState.Failed;

    /// <summary>
    /// States the player may decline with the dismiss key: the ones that are waiting on a decision rather than
    /// reporting work in flight. <see cref="UpdateState.UpdateAvailable"/> (not now),
    /// <see cref="UpdateState.ReadyToApply"/> (I will restart later, the staged files keep) and
    /// <see cref="UpdateState.Failed"/> (the way out of a retry that cannot succeed).
    /// <see cref="UpdateState.Downloading"/> and <see cref="UpdateState.Applying"/> are excluded: both report an
    /// operation the player already started, and the second is a process that is about to exit.
    /// </summary>
    public static bool IsDismissible(UpdateState state) => state is
        UpdateState.UpdateAvailable or UpdateState.ReadyToApply or UpdateState.Failed;

    /// <summary>True when the dismiss key/button would be accepted for <paramref name="status"/> right now.</summary>
    public bool CanDismiss(IUpdateStatus status)
        => !status.IsRequired && IsDismissible(status.State) && !IsDismissed(status.State);

    /// <summary>True when <paramref name="state"/> has been declined this session (see <see cref="Dismiss"/>).</summary>
    public bool IsDismissed(UpdateState state) => (_dismissed & Bit(state)) != 0;

    /// <summary>
    /// Record <paramref name="state"/> as declined, hiding the panel for it. This is what the dismiss key does,
    /// exposed so a game can also decline from its own UI. It records whatever it is given (the key path is the
    /// one gated by <see cref="CanDismiss"/>), and a required update still shows regardless: see
    /// <see cref="IsShowing"/>.
    /// </summary>
    public void Dismiss(UpdateState state) => _dismissed |= Bit(state);

    /// <summary>
    /// Forget every dismissal, so the panel shows again for states the player already declined. Call it when
    /// the player deliberately re-engages with the updater (a Check for updates menu entry), because a
    /// dismissal otherwise lasts the whole session by design: this view is constructed once per session, so a
    /// new session already starts with nothing declined.
    /// </summary>
    public void ResetDismissed() => _dismissed = 0;

    /// <summary>
    /// Whether the panel is on screen for <paramref name="status"/>: visible by state, and not declined.
    ///
    /// <para>A dismissal is remembered per state, so declining an offer keeps it hidden across the recheck
    /// cycles that keep re-reporting the same state, and the panel comes back on its own only when the flow
    /// reaches a state the player has NOT declined (a <see cref="UpdateState.ReadyToApply"/> after a
    /// background download, say). <see cref="ResetDismissed"/> is the explicit way back.</para>
    ///
    /// <para>A required update (<see cref="IUpdateStatus.IsRequired"/>) always shows. It is not dismissible in
    /// the first place, and the check is repeated here so that even a direct <see cref="Dismiss"/> call cannot
    /// hide a mandatory update that is installing itself.</para>
    /// </summary>
    public bool IsShowing(IUpdateStatus status)
        => IsVisible(status.State) && (status.IsRequired || !IsDismissed(status.State));

    static int Bit(UpdateState state) => 1 << (int)state;

    /// <summary>Download progress 0..1, clamped; 0 when the total is unknown.</summary>
    public static float ProgressFraction(IUpdateStatus s)
    {
        if (s.TotalDownloadBytes <= 0) return 0f;
        float f = (float)s.BytesDownloaded / s.TotalDownloadBytes;
        return f < 0f ? 0f : f > 1f ? 1f : f;
    }

    /// <summary>
    /// Advance the fade, detect the trigger and the dismiss, and report whether the overlay is showing a panel.
    /// Also sets <see cref="TriggeredThisFrame"/> / <see cref="DismissedThisFrame"/>. Pass
    /// <see cref="InputState.Empty"/> to advance visuals without accepting input. The trigger wins a frame that
    /// carries both presses: it is the affirmative action, and the state it advances to was never declined.
    /// </summary>
    public bool Update(IUpdateStatus status, InputState input, float dt)
    {
        bool showing = IsShowing(status);
        float target = showing ? 1f : 0f;
        float step = Theme.FadeSpeed * dt;
        _alpha = target > _alpha ? MathF.Min(target, _alpha + step) : MathF.Max(target, _alpha - step);

        TriggeredThisFrame = showing && Pressed(input, Theme.TriggerKey, Theme.TriggerButton);
        DismissedThisFrame = false;
        if (TriggeredThisFrame)
        {
            OnTrigger?.Invoke(status.State);
            Triggered?.Invoke();
        }
        else if (showing && CanDismiss(status) && Pressed(input, Theme.DismissKey, Theme.DismissButton))
        {
            Dismiss(status.State);
            DismissedThisFrame = true;
            OnDismiss?.Invoke(status.State);
        }
        return showing;
    }

    static bool Pressed(InputState input, Key key, GamepadButton? button)
    {
        if (input.WasPressed(key)) return true;
        if (button is { } btn)
        {
            GamepadState pad = input.PrimaryGamepad;
            if (pad.IsConnected && pad.WasPressed(btn)) return true;
        }
        return false;
    }

    /// <summary>Draw the panel centred in <paramref name="viewport"/>. No-op when the panel is hidden, whether
    /// because the state shows none or because the player dismissed it (see <see cref="IsShowing"/>).</summary>
    public void Draw(SpriteBatch batch, SpriteFont font, Texture2D white, Rect viewport, IUpdateStatus status)
    {
        UpdateState state = status.State;
        if (!IsShowing(status)) return;
        float a = _alpha < 0f ? 0f : _alpha > 1f ? 1f : _alpha;

        float pad = Theme.PanelPadding;
        float titleH = font.LineHeight * Theme.TitleScale;
        float bodyH = font.LineHeight * Theme.BodyScale;
        float gap = pad * 0.5f;
        bool downloading = state == UpdateState.Downloading;
        float progressBlock = downloading ? gap + Theme.ProgressBarHeight : 0f;
        string hint = Theme.HintFor(state, status);
        float hintBlock = hint.Length > 0 ? gap + bodyH : 0f;
        float h = pad + titleH + gap + bodyH + progressBlock + hintBlock + pad;

        float cx = viewport.X + viewport.Width * 0.5f;
        float cy = viewport.Y + viewport.Height * 0.5f;
        var panel = new Rect(cx - Theme.PanelWidth * 0.5f, cy - h * 0.5f, Theme.PanelWidth, h);

        GuiDraw.Fill(batch, white, viewport, Mul(Theme.DimFill, a));
        GuiDraw.Fill(batch, white, panel, Mul(Theme.PanelFill, a));
        GuiDraw.Border(batch, white, panel, Theme.BorderThickness, Mul(Theme.AccentFor(state), a));

        float titleY = panel.Y + pad;
        float bodyY = titleY + titleH + gap;
        DrawCentered(batch, font, Theme.TitleFor(state, status), titleY, Theme.TitleScale, Mul(Theme.AccentFor(state), a), panel);
        DrawCentered(batch, font, Theme.BodyFor(state, status), bodyY, Theme.BodyScale, Mul(Theme.BodyText, a), panel);

        if (downloading)
        {
            float barY = bodyY + bodyH + gap;
            float barX = panel.X + pad;
            float barW = panel.Width - pad * 2f;
            GuiDraw.Fill(batch, white, new Rect(barX, barY, barW, Theme.ProgressBarHeight), Mul(Theme.ProgressBackground, a));
            GuiDraw.Fill(batch, white, new Rect(barX, barY, barW * ProgressFraction(status), Theme.ProgressBarHeight), Mul(Theme.ProgressFill, a));
        }

        if (hint.Length > 0)
        {
            DrawCentered(batch, font, hint, bodyY + bodyH + progressBlock + gap, Theme.BodyScale,
                Mul(Theme.HintText, a), panel);
        }
    }

    static void DrawCentered(SpriteBatch batch, SpriteFont font, string text, float y, float scale, Vector4 color, Rect panel)
    {
        Vector2 size = font.Measure(text) * scale;
        var pos = new Vector2(panel.X + (panel.Width - size.X) * 0.5f, y);
        batch.DrawString(font, text, pos, (Color)color, scale);
    }

    static Vector4 Mul(Vector4 c, float a) => new(c.X, c.Y, c.Z, c.W * a);
}
