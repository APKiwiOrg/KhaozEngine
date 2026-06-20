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
/// is shown. It never calls the service itself — wire <see cref="OnTrigger"/> to
/// <c>KhaozEngine.Updates.UpdateOverlayActions.Trigger</c>. Headless-testable: <see cref="Update"/> needs no
/// GPU. Drop it into any Gui layer, or use <see cref="UpdateOverlayScreen"/> for stack-based games.
/// </summary>
public sealed class UpdateOverlayView
{
    public UpdateOverlayTheme Theme { get; set; }

    /// <summary>Raised with the current state when the trigger key/button is pressed while visible.</summary>
    public event Action<UpdateState>? OnTrigger;
    /// <summary>Paramless convenience; raised alongside <see cref="OnTrigger"/>.</summary>
    public event Action? Triggered;

    float _alpha; // current fade, 0..1

    public UpdateOverlayView(UpdateOverlayTheme? theme = null) => Theme = theme ?? UpdateOverlayTheme.Default;

    /// <summary>Current fade alpha (0 hidden .. 1 shown); exposed for tests/diagnostics.</summary>
    public float Alpha => _alpha;

    /// <summary>States that show a panel (and are modal). Idle/Checking are hidden.</summary>
    public static bool IsVisible(UpdateState state) => state is
        UpdateState.UpdateAvailable or UpdateState.Downloading or UpdateState.ReadyToApply
        or UpdateState.Applying or UpdateState.Failed;

    /// <summary>Download progress 0..1, clamped; 0 when the total is unknown.</summary>
    public static float ProgressFraction(IUpdateStatus s)
    {
        if (s.TotalDownloadBytes <= 0) return 0f;
        float f = (float)s.BytesDownloaded / s.TotalDownloadBytes;
        return f < 0f ? 0f : f > 1f ? 1f : f;
    }

    /// <summary>
    /// Advance the fade, detect the trigger, and report whether the overlay is showing a panel (i.e. is modal
    /// / consumed input). Pass <see cref="InputState.Empty"/> to advance visuals without accepting input.
    /// </summary>
    public bool Update(IUpdateStatus status, InputState input, float dt)
    {
        bool visible = IsVisible(status.State);
        float target = visible ? 1f : 0f;
        float step = Theme.FadeSpeed * dt;
        _alpha = target > _alpha ? MathF.Min(target, _alpha + step) : MathF.Max(target, _alpha - step);

        if (visible && TriggerPressed(input))
        {
            OnTrigger?.Invoke(status.State);
            Triggered?.Invoke();
        }
        return visible;
    }

    bool TriggerPressed(InputState input)
    {
        if (input.WasPressed(Theme.TriggerKey)) return true;
        if (Theme.TriggerButton is { } btn)
        {
            GamepadState pad = input.PrimaryGamepad;
            if (pad.IsConnected && pad.WasPressed(btn)) return true;
        }
        return false;
    }

    /// <summary>Draw the panel centred in <paramref name="viewport"/>. No-op when the state is hidden.</summary>
    public void Draw(SpriteBatch batch, SpriteFont font, Texture2D white, Rect viewport, IUpdateStatus status)
    {
        UpdateState state = status.State;
        if (!IsVisible(state)) return;
        float a = _alpha < 0f ? 0f : _alpha > 1f ? 1f : _alpha;

        float pad = Theme.PanelPadding;
        float titleH = font.LineHeight * Theme.TitleScale;
        float bodyH = font.LineHeight * Theme.BodyScale;
        float gap = pad * 0.5f;
        bool downloading = state == UpdateState.Downloading;
        float progressBlock = downloading ? gap + Theme.ProgressBarHeight : 0f;
        float h = pad + titleH + gap + bodyH + progressBlock + pad;

        float cx = viewport.X + viewport.Width * 0.5f;
        float cy = viewport.Y + viewport.Height * 0.5f;
        var panel = new Rect(cx - Theme.PanelWidth * 0.5f, cy - h * 0.5f, Theme.PanelWidth, h);

        GuiDraw.Fill(batch, white, viewport, Mul(Theme.DimFill, a));
        GuiDraw.Fill(batch, white, panel, Mul(Theme.PanelFill, a));
        GuiDraw.Border(batch, white, panel, Theme.BorderThickness, Mul(Theme.AccentFor(state), a));

        float titleY = panel.Y + pad;
        float bodyY = titleY + titleH + gap;
        DrawCentered(batch, font, Theme.TitleFor(state, status.RemoteVersion), titleY, Theme.TitleScale, Mul(Theme.AccentFor(state), a), panel);
        DrawCentered(batch, font, Theme.BodyFor(state, status), bodyY, Theme.BodyScale, Mul(Theme.BodyText, a), panel);

        if (downloading)
        {
            float barY = bodyY + bodyH + gap;
            float barX = panel.X + pad;
            float barW = panel.Width - pad * 2f;
            GuiDraw.Fill(batch, white, new Rect(barX, barY, barW, Theme.ProgressBarHeight), Mul(Theme.ProgressBackground, a));
            GuiDraw.Fill(batch, white, new Rect(barX, barY, barW * ProgressFraction(status), Theme.ProgressBarHeight), Mul(Theme.ProgressFill, a));
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
