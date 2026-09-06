using System.Numerics;

namespace KhaozEngine.Gui.Chat;

/// <summary>Colours used by <see cref="ChatBox"/> for its frame, composer, and message kinds.</summary>
public sealed class ChatBoxTheme
{
    /// <summary>Outer frame fill.</summary>
    public Vector4 Background = GuiTheme.Default.Surface;

    /// <summary>Outer frame border.</summary>
    public Vector4 Border = GuiTheme.Default.Border;

    /// <summary>Composer fill.</summary>
    public Vector4 ComposerBackground = GuiTheme.Default.Background;

    /// <summary>Composer border while it is not focused.</summary>
    public Vector4 ComposerBorder = GuiTheme.Default.Border;

    /// <summary>Composer border while it owns keyboard focus.</summary>
    public Vector4 ComposerFocusedBorder = GuiTheme.Default.AccentBright;

    /// <summary>Ordinary message text.</summary>
    public Vector4 OrdinaryText = GuiTheme.Default.Text;

    /// <summary>Message text belonging to the local player.</summary>
    public Vector4 OwnText = GuiTheme.Default.AccentBright;

    /// <summary>System message text. This takes precedence over the own-message flag.</summary>
    public Vector4 SystemText = GuiTheme.Default.TextMuted;

    /// <summary>Timestamp prefix text.</summary>
    public Vector4 TimestampText = GuiTheme.Default.TextMuted;

    /// <summary>A fresh theme derived from the current ambient GUI palette.</summary>
    public static ChatBoxTheme Default => new();
}
