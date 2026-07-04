using System.IO;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>
/// The resolved look and localized text handed to <see cref="IUpdaterUi.Show"/>: the apply core builds
/// one from the optional <see cref="ApplyUpdateUiConfig"/> on the config, filling every gap with a
/// sensible default so a window can always render. Colors are plain RGB tuples (no rendering-stack
/// dependency); the logo path is resolved to an absolute path under the install dir.
/// </summary>
public sealed class UpdaterUiTheme
{
    /// <summary>Native window caption text.</summary>
    public string WindowTitle { get; init; } = "Updating";

    /// <summary>Heading drawn inside the window.</summary>
    public string Heading { get; init; } = "Updating";

    /// <summary>Progress-bar / heading accent color.</summary>
    public (byte R, byte G, byte B) Accent { get; init; } = (80, 160, 240);

    /// <summary>Window background (panel) color.</summary>
    public (byte R, byte G, byte B) Background { get; init; } = (18, 20, 26);

    /// <summary>Heading + status text color.</summary>
    public (byte R, byte G, byte B) Text { get; init; } = (235, 238, 245);

    /// <summary>Absolute path of a logo image to draw, or null. Resolved against the install dir.</summary>
    public string? LogoPath { get; init; }

    /// <summary>Status line for the Install phase.</summary>
    public string InstallingText { get; init; } = "Installing update";

    /// <summary>Status line for the Finishing (settle) phase.</summary>
    public string FinishingText { get; init; } = "Finishing up, checking with your security software...";

    /// <summary>Status line for the Download phase (unused by the shim today).</summary>
    public string DownloadingText { get; init; } = "Downloading update";

    /// <summary>
    /// Builds a theme from the optional apply-config UI block, defaulting every unset field. A relative
    /// <see cref="ApplyUpdateUiConfig.LogoPath"/> is resolved against <paramref name="installDir"/>.
    /// Returns the all-default theme when <paramref name="ui"/> is null.
    /// </summary>
    public static UpdaterUiTheme FromConfig(ApplyUpdateUiConfig? ui, string installDir)
    {
        var defaults = new UpdaterUiTheme();
        if (ui is null)
        {
            return defaults;
        }

        string title = string.IsNullOrEmpty(ui.WindowTitle) ? defaults.WindowTitle : ui.WindowTitle!;
        string heading = !string.IsNullOrEmpty(ui.Heading) ? ui.Heading!
            : !string.IsNullOrEmpty(ui.WindowTitle) ? ui.WindowTitle!
            : defaults.Heading;

        string? logo = null;
        if (!string.IsNullOrEmpty(ui.LogoPath))
        {
            string native = ui.LogoPath!.Replace('/', Path.DirectorySeparatorChar);
            logo = string.IsNullOrEmpty(installDir) ? native : Path.Combine(installDir, native);
        }

        return new UpdaterUiTheme
        {
            WindowTitle = title,
            Heading = heading,
            Accent = ToRgb(ui.Accent, defaults.Accent),
            Background = ToRgb(ui.Background, defaults.Background),
            Text = ToRgb(ui.Text, defaults.Text),
            LogoPath = logo,
            InstallingText = string.IsNullOrEmpty(ui.InstallingText) ? defaults.InstallingText : ui.InstallingText!,
            FinishingText = string.IsNullOrEmpty(ui.FinishingText) ? defaults.FinishingText : ui.FinishingText!,
            DownloadingText = string.IsNullOrEmpty(ui.DownloadingText) ? defaults.DownloadingText : ui.DownloadingText!,
        };
    }

    private static (byte R, byte G, byte B) ToRgb(UpdaterUiColor? color, (byte R, byte G, byte B) fallback)
        => color is null ? fallback : (Clamp(color.R), Clamp(color.G), Clamp(color.B));

    private static byte Clamp(int channel) => (byte)(channel < 0 ? 0 : channel > 255 ? 255 : channel);
}
