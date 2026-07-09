using KhaozEngine.Primitives;

namespace KhaozEngine.Gui;

/// <summary>
/// Look for the patch-notes chrome (panel, header, body, muted dates, code spans, and category tags),
/// derived live from <see cref="GuiTheme.Default"/> so a game that retunes its Gui palette gets a matching
/// patch-notes screen automatically. Every color is a virtual member, mirroring the injectable-look shape
/// <see cref="UpdateOverlayTheme"/> established for the update overlay, so a subclass can override just the
/// ones it wants without inventing a new theming pattern.
/// </summary>
public class PatchNotesTheme
{
    /// <summary>Panel background fill. From <see cref="GuiTheme.Surface"/>.</summary>
    public virtual Color PanelFill => (Color)GuiTheme.Default.Surface;

    /// <summary>Header strip fill, slightly raised above the panel body. From <see cref="GuiTheme.SurfaceHover"/>.</summary>
    public virtual Color HeaderFill => (Color)GuiTheme.Default.SurfaceHover;

    /// <summary>Header title text color. From <see cref="GuiTheme.Text"/>.</summary>
    public virtual Color HeaderText => (Color)GuiTheme.Default.Text;

    /// <summary>Body note text color. From <see cref="GuiTheme.Text"/>.</summary>
    public virtual Color BodyText => (Color)GuiTheme.Default.Text;

    /// <summary>Muted text for build dates. From <see cref="GuiTheme.TextMuted"/>.</summary>
    public virtual Color MutedText => (Color)GuiTheme.Default.TextMuted;

    /// <summary>Text color for backtick-wrapped code spans. From <see cref="GuiTheme.AccentBright"/>.</summary>
    public virtual Color CodeText => (Color)GuiTheme.Default.AccentBright;

    /// <summary>
    /// The tag color for <paramref name="category"/>: <see cref="PatchNoteCategory.New"/> reads the theme
    /// accent, <see cref="PatchNoteCategory.Major"/> the bright accent, <see cref="PatchNoteCategory.Minor"/>
    /// and <see cref="PatchNoteCategory.Other"/> the muted text color (both are "no strong category"
    /// styling), <see cref="PatchNoteCategory.Rebalance"/> a warm tone blended from the accent toward the
    /// danger color (<see cref="GuiTheme"/> has no dedicated warm swatch to read instead), and
    /// <see cref="PatchNoteCategory.Bug"/> the danger color.
    /// </summary>
    public virtual Color CategoryColor(PatchNoteCategory category)
    {
        GuiTheme t = GuiTheme.Default;
        return category switch
        {
            PatchNoteCategory.New => (Color)t.Accent,
            PatchNoteCategory.Major => (Color)t.AccentBright,
            PatchNoteCategory.Minor => (Color)t.TextMuted,
            PatchNoteCategory.Rebalance => Color.Lerp((Color)t.Accent, (Color)t.Danger, 0.5f),
            PatchNoteCategory.Bug => (Color)t.Danger,
            _ => (Color)t.TextMuted,
        };
    }

    /// <summary>A fresh default theme, deriving every color live from the current <see cref="GuiTheme.Default"/>.</summary>
    public static PatchNotesTheme Default => new();
}
