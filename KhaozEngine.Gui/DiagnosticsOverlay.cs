using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using KhaozEngine.Diagnostics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// Reusable in-game diagnostics/telemetry overlay: a pure presenter modeled on
/// <see cref="UpdateOverlayView"/>. The game assembles <see cref="OverlaySection"/>s each frame and feeds them
/// in via <see cref="SetSections"/>; <see cref="Update"/> handles the toggle key (default F1) and a fade, and
/// <see cref="Draw"/> renders a corner panel of section titles and right-aligned label/value rows. The widget
/// is content-agnostic (the metric catalog stays game-owned); <see cref="PerformanceSection"/> and
/// <see cref="NetworkSection"/> are convenience populators for the common cases. Headless-testable:
/// <see cref="Update"/> and the populators need no GPU. Drop it into any Gui layer.
/// </summary>
public sealed class DiagnosticsOverlay
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public DiagnosticsOverlayTheme Theme { get; set; }

    /// <summary>Whether the panel is shown. Flipped by <see cref="Toggle"/> / the toggle key in <see cref="Update"/>.</summary>
    public bool Visible { get; set; }

    /// <summary>The panel rect of the most recent <see cref="Draw"/> (empty <see cref="Rect"/> when the last draw
    /// was a no-op, i.e. hidden/faded-out/empty). Lets a caller place an adjacent panel off its edge, e.g. an
    /// <see cref="OverlayLegend"/> at <see cref="Rect.Right"/> + a gap.</summary>
    public Rect Bounds { get; private set; }

    IReadOnlyList<OverlaySection> _sections = Array.Empty<OverlaySection>();
    float _alpha; // current fade, 0..1

    public DiagnosticsOverlay(DiagnosticsOverlayTheme? theme = null) => Theme = theme ?? DiagnosticsOverlayTheme.Default;

    /// <summary>Current fade alpha (0 hidden .. 1 shown); exposed for tests/diagnostics.</summary>
    public float Alpha => _alpha;

    /// <summary>Flip <see cref="Visible"/>.</summary>
    public void Toggle() => Visible = !Visible;

    /// <summary>
    /// Detect the toggle key/button (flipping <see cref="Visible"/> on press), advance the fade, and return
    /// the resulting <see cref="Visible"/>. Pass <see cref="InputState.Empty"/> to advance the fade without
    /// accepting input.
    /// </summary>
    public bool Update(InputState input, float dt)
    {
        if (TogglePressed(input)) Toggle();

        float target = Visible ? 1f : 0f;
        if (Theme.FadeSpeed <= 0f)
        {
            _alpha = target;
        }
        else
        {
            float step = Theme.FadeSpeed * dt;
            _alpha = target > _alpha ? MathF.Min(target, _alpha + step) : MathF.Max(target, _alpha - step);
        }
        return Visible;
    }

    bool TogglePressed(InputState input)
    {
        if (input.WasPressed(Theme.ToggleKey)) return true;
        if (Theme.TriggerButton is { } btn)
        {
            GamepadState pad = input.PrimaryGamepad;
            if (pad.IsConnected && pad.WasPressed(btn)) return true;
        }
        return false;
    }

    /// <summary>
    /// Set the sections rendered next <see cref="Draw"/>. The reference is stored as-is (no copy), so a game
    /// may reuse its section/row buffers between frames to stay allocation-light.
    /// </summary>
    public void SetSections(IReadOnlyList<OverlaySection> sections) =>
        _sections = sections ?? Array.Empty<OverlaySection>();

    /// <summary>Build a standard "Performance" section from a <see cref="FrameStats"/> meter.</summary>
    public static OverlaySection PerformanceSection(FrameStats f)
    {
        if (f is null) throw new ArgumentNullException(nameof(f));
        var rows = new[]
        {
            new OverlayRow("fps", f.Fps.ToString("0", Inv)),
            new OverlayRow("frame ms", string.Format(Inv, "{0:0.0}/{1:0.0}/{2:0.0}", f.FrameMsAvg, f.FrameMsMin, f.FrameMsMax)),
            new OverlayRow("managed MB", (f.ManagedBytes / (1024d * 1024d)).ToString("0.0", Inv)),
        };
        return new OverlaySection("Performance", rows);
    }

    /// <summary>
    /// Build a standard "Pass timings" section from a <see cref="PassTimings"/> meter: one row per pass name
    /// (in first-sampled order), showing that pass's rolling avg/min/max milliseconds. Empty (no rows) when no
    /// pass has been sampled yet, e.g. the producing renderer has per-pass timing disabled (the default) or has
    /// not rendered a frame. This is CPU encode time, not true GPU execution time - see <see cref="PassTimings"/>
    /// remarks and the USING doc for what is and is not measured.
    /// </summary>
    public static OverlaySection PassTimingsSection(PassTimings t)
    {
        if (t is null) throw new ArgumentNullException(nameof(t));
        var names = t.PassNames;
        var rows = new OverlayRow[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            string pass = names[i];
            rows[i] = new OverlayRow(pass,
                string.Format(Inv, "{0:0.00}/{1:0.00}/{2:0.00}", t.AvgMs(pass), t.MinMs(pass), t.MaxMs(pass)));
        }
        return new OverlaySection("Pass timings", rows);
    }

    /// <summary>Build a standard "Network" section from a <see cref="ClientNetStats"/> snapshot.</summary>
    public static OverlaySection NetworkSection(in ClientNetStats n)
    {
        if (!n.Connected)
            return new OverlaySection("Network", new[] { new OverlayRow("status", "not connected") });

        var rows = new[]
        {
            new OverlayRow("ping", n.RttMs.ToString("0", Inv) + " ms"),
            new OverlayRow("loss", (n.PacketLoss * 100f).ToString("0.0", Inv) + " %"),
            new OverlayRow("in/out", string.Format(Inv, "{0:0.0}/{1:0.0} KB/s", n.BytesInPerSec / 1024f, n.BytesOutPerSec / 1024f)),
            new OverlayRow("snapshots", n.SnapshotsPerSec.ToString("0.0", Inv) + "/s"),
            new OverlayRow("correction", string.Format(Inv, "{0:0.00}/{1:0.00} m", n.LastCorrectionMeters, n.AvgCorrectionMeters)),
        };
        return new OverlaySection("Network", rows);
    }

    /// <summary>Draw the corner panel. No-op when hidden / fully faded out / empty.</summary>
    public void Draw(SpriteBatch batch, SpriteFont font, Texture2D white, Rect viewport)
    {
        float a = _alpha < 0f ? 0f : _alpha > 1f ? 1f : _alpha;
        if (a <= 0f || _sections.Count == 0) { Bounds = default; return; }

        float pad = Theme.Padding;
        float titleH = font.LineHeight * Theme.TitleScale;
        float rowH = font.LineHeight * Theme.Scale;

        // Measure the content box: widest title-or-row, and the stacked height.
        float contentW = 0f;
        float contentH = 0f;
        for (int si = 0; si < _sections.Count; si++)
        {
            OverlaySection s = _sections[si];
            contentW = MathF.Max(contentW, font.Measure(s.Title).X * Theme.TitleScale);

            if (si > 0) contentH += Theme.SectionSpacing;
            contentH += titleH;

            for (int ri = 0; ri < s.Rows.Count; ri++)
            {
                OverlayRow row = s.Rows[ri];
                float labelW = font.Measure(row.Label).X * Theme.Scale;
                float valueW = font.Measure(row.Value).X * Theme.Scale;
                contentW = MathF.Max(contentW, labelW + Theme.ColumnGap + valueW);
                contentH += Theme.RowSpacing + rowH;
            }
        }

        float panelW = contentW + pad * 2f;
        float panelH = contentH + pad * 2f;
        (float px, float py) = Anchor(viewport, panelW, panelH);
        var panel = new Rect(px, py, panelW, panelH);
        Bounds = panel;

        GuiDraw.Fill(batch, white, panel, Mul(Theme.PanelFill, a));
        GuiDraw.Border(batch, white, panel, Theme.BorderThickness, Mul(Theme.BorderColor, a));

        float x = px + pad;
        float right = px + panelW - pad;
        float y = py + pad;
        for (int si = 0; si < _sections.Count; si++)
        {
            OverlaySection s = _sections[si];
            if (si > 0) y += Theme.SectionSpacing;

            batch.DrawString(font, s.Title, new Vector2(x, y), (Color)Mul(Theme.TitleText, a), Theme.TitleScale);
            y += titleH;

            for (int ri = 0; ri < s.Rows.Count; ri++)
            {
                OverlayRow row = s.Rows[ri];
                y += Theme.RowSpacing;
                batch.DrawString(font, row.Label, new Vector2(x, y), (Color)Mul(Theme.LabelText, a), Theme.Scale);
                float valueW = font.Measure(row.Value).X * Theme.Scale;
                batch.DrawString(font, row.Value, new Vector2(right - valueW, y), (Color)Mul(Theme.ValueText, a), Theme.Scale);
                y += rowH;
            }
        }
    }

    (float x, float y) Anchor(Rect vp, float w, float h)
    {
        float m = Theme.Margin;
        float left = vp.X + m;
        float rightX = vp.Right - m - w;
        float top = vp.Y + m;
        float bottomY = vp.Bottom - m - h;
        return Theme.Corner switch
        {
            OverlayCorner.TopRight => (rightX, top),
            OverlayCorner.BottomLeft => (left, bottomY),
            OverlayCorner.BottomRight => (rightX, bottomY),
            _ => (left, top), // TopLeft
        };
    }

    static Vector4 Mul(Vector4 c, float a) => new(c.X, c.Y, c.Z, c.W * a);
}
