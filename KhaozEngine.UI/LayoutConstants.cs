namespace KhaozEngine.UI;

/// <summary>
/// Shared layout constants for screen regions. All values are in virtual pixels
/// (in virtual pixels, consistent across all screen sizes).
/// </summary>
public static class LayoutConstants
{
    /// <summary>Height of the top HUD bar. Settable so each game fits its own chrome.</summary>
    public static int TopBarHeight { get; set; } = 48;

    /// <summary>Height of the bottom navigation bar. Settable so each game fits its own chrome.</summary>
    public static int BottomNavHeight { get; set; } = 52;

    /// <summary>Standard horizontal padding from screen edges.</summary>
    public const int EdgePadding = 6;
}
