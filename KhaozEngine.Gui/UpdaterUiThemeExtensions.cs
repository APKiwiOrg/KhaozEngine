using System;
using System.Numerics;
using KhaozEngine.Updates;

namespace KhaozEngine.Gui;

/// <summary>
/// Derives the native apply-window palette (<see cref="UpdaterUiOptions"/>, in
/// <c>KhaozEngine.Updates</c>) from an <see cref="UpdateOverlayTheme"/>, so a game themes the in-game update
/// overlay and the shim's progress window from ONE palette instead of hand-syncing two colour sets.
/// The helper lives on the Gui side because <c>KhaozEngine.Updates</c> deliberately has no Gui dependency:
/// Gui already references Updates, so the edge points the right way.
/// </summary>
public static class UpdaterUiThemeExtensions
{
    /// <summary>
    /// Builds an <see cref="UpdaterUiOptions"/> whose colours come from <paramref name="theme"/>:
    /// <see cref="UpdaterUiOptions.AccentColor"/> from <see cref="UpdateOverlayTheme.ProgressFill"/> (the
    /// shim's progress-bar / heading accent), <see cref="UpdaterUiOptions.BackgroundColor"/> from
    /// <see cref="UpdateOverlayTheme.PanelFill"/>, and <see cref="UpdaterUiOptions.TextColor"/> from
    /// <see cref="UpdateOverlayTheme.BodyText"/>. Alpha is dropped (the native window is opaque). The window
    /// title, heading, logo, and the already-localized status lines are the game's, passed in and defaulting
    /// to unset (the shim then uses its own defaults for anything left null).
    /// </summary>
    /// <param name="theme">The overlay theme whose palette drives the shim window.</param>
    /// <param name="windowTitle">Native window caption (e.g. the game name). Optional.</param>
    /// <param name="heading">Large heading inside the window. Defaults to the window title when null.</param>
    /// <param name="logoPath">Install-relative forward-slash path of a logo image. Optional.</param>
    /// <param name="installingText">Localized status line for the Install phase. Optional.</param>
    /// <param name="finishingText">Localized status line for the Finishing (settle) phase. Optional.</param>
    /// <param name="downloadingText">Localized status line for the Download phase. Optional.</param>
    /// <returns>A fresh <see cref="UpdaterUiOptions"/> with the derived palette and the supplied text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="theme"/> is null.</exception>
    public static UpdaterUiOptions ToUpdaterUiOptions(
        this UpdateOverlayTheme theme,
        string? windowTitle = null,
        string? heading = null,
        string? logoPath = null,
        string? installingText = null,
        string? finishingText = null,
        string? downloadingText = null)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return new UpdaterUiOptions
        {
            WindowTitle = windowTitle,
            Heading = heading,
            LogoPath = logoPath,
            InstallingText = installingText,
            FinishingText = finishingText,
            DownloadingText = downloadingText,
            AccentColor = ToRgb(theme.ProgressFill),
            BackgroundColor = ToRgb(theme.PanelFill),
            TextColor = ToRgb(theme.BodyText),
        };
    }

    /// <summary>
    /// Converts an RGBA float colour (0..1, as the overlay theme stores it) to the opaque <c>(R, G, B)</c>
    /// byte tuple <see cref="UpdaterUiOptions"/> takes. Rounds each channel and clamps to 0..255, matching the
    /// per-game converters this helper replaces; alpha is ignored.
    /// </summary>
    public static (byte R, byte G, byte B) ToRgb(Vector4 rgba)
        => (Channel(rgba.X), Channel(rgba.Y), Channel(rgba.Z));

    static byte Channel(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
}
