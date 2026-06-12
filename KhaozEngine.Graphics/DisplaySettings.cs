using Microsoft.Xna.Framework;

namespace KhaozEngine.Graphics;

/// <summary>
/// Declarative description of the wanted window: size, presentation mode, resize behaviour,
/// optional minimum size floor, supported orientations, and title. Immutable — build variants
/// with <c>with</c> expressions. Pure data; <see cref="DisplayManager"/> applies it to the device.
/// </summary>
public sealed record DisplaySettings
{
    /// <summary>Preferred backbuffer width in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Preferred backbuffer height in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Windowed / borderless / exclusive presentation.</summary>
    public WindowMode Mode { get; init; } = WindowMode.Windowed;

    /// <summary>Whether the user can resize the window (desktop).</summary>
    public bool AllowUserResizing { get; init; }

    /// <summary>Minimum client width enforced on resize; 0 = no floor.</summary>
    public int MinWidth { get; init; }

    /// <summary>Minimum client height enforced on resize; 0 = no floor.</summary>
    public int MinHeight { get; init; }

    /// <summary>Supported device orientations (mobile).</summary>
    public DisplayOrientation SupportedOrientations { get; init; } = DisplayOrientation.Default;

    /// <summary>Window title; null leaves the platform/default title untouched.</summary>
    public string? Title { get; init; }

    /// <summary>Landscape settings: the given size with landscape-left/right orientations.</summary>
    public static DisplaySettings Landscape(int width, int height) => new()
    {
        Width = width,
        Height = height,
        SupportedOrientations = DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight,
    };

    /// <summary>Portrait settings: the given size with portrait/portrait-down orientations.</summary>
    public static DisplaySettings Portrait(int width, int height) => new()
    {
        Width = width,
        Height = height,
        SupportedOrientations = DisplayOrientation.Portrait | DisplayOrientation.PortraitDown,
    };
}
