using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.BugReports.Client;

/// <summary>
/// Where <see cref="BugReportScreen"/> puts its panel and widgets, in design space. The panel is
/// <see cref="PanelWidth"/> by <see cref="PanelHeight"/> points centred in the viewport, scaled down to fit 90
/// percent of it and never scaled up. Every other rect is a fixed spot on the panel at that scale.
/// </summary>
internal readonly record struct BugReportScreenLayout(Rect Panel, Rect Thumbnail, Rect Strip, Rect Description,
    Rect Category, Rect ScreenshotToggle, Rect ClipToggle, Rect Send, Rect Cancel)
{
    internal const float PanelWidth = 640f;
    internal const float PanelHeight = 560f;
    const float Fill = 0.9f;

    /// <summary>The panel scale, from 0 to 1.</summary>
    internal float Scale => Panel.Width / PanelWidth;

    /// <summary>Computes the layout for a viewport, usually <c>IDesignViewport.WindowBounds</c>.</summary>
    internal static BugReportScreenLayout Compute(Rect viewportBounds)
    {
        float scale = MathF.Min(1f, MathF.Min(viewportBounds.Width * Fill / PanelWidth, viewportBounds.Height * Fill / PanelHeight));
        scale = MathF.Max(0f, scale);
        float width = PanelWidth * scale;
        float height = PanelHeight * scale;
        var panel = new Rect(viewportBounds.X + (viewportBounds.Width - width) * 0.5f,
            viewportBounds.Y + (viewportBounds.Height - height) * 0.5f, width, height);
        return new BugReportScreenLayout(panel,
            Thumbnail: On(panel, scale, 20, 48, 256, 144),
            Strip: On(panel, scale, 296, 48, 324, 144),
            Description: On(panel, scale, 20, 262, 600, 120),
            Category: On(panel, scale, 140, 204, 200, 28),
            ScreenshotToggle: On(panel, scale, 20, 408, 40, 20),
            ClipToggle: On(panel, scale, 20, 434, 40, 20),
            Send: On(panel, scale, 510, 520, 110, 32),
            Cancel: On(panel, scale, 390, 520, 110, 32));
    }

    /// <summary>A point on the panel, from unscaled panel coordinates.</summary>
    internal Vector2 At(float x, float y) => new(Panel.X + x * Scale, Panel.Y + y * Scale);

    /// <summary>A rect on the panel, from unscaled panel coordinates.</summary>
    internal Rect On(float x, float y, float width, float height) => On(Panel, Scale, x, y, width, height);

    static Rect On(Rect panel, float scale, float x, float y, float width, float height) =>
        new(panel.X + x * scale, panel.Y + y * scale, width * scale, height * scale);
}
