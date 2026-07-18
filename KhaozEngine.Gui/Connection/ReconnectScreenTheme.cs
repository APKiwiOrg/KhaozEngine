using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui;

/// <summary>
/// Look and layout for <see cref="ReconnectScreen"/>: the scrim, every colour, the optional custom-background
/// hook, the button style, and the layout metrics are all injected here, so a game restyles the takeover
/// without forking the screen. Defaults derive from <see cref="GuiTheme.Default"/>, so a game that sets
/// <see cref="GuiTheme.Default"/> at startup (its house palette) gets a takeover that already matches. A mutable
/// class (not a struct), matching <c>BootScreenTheme</c> / <c>UpdateOverlayTheme</c> in shape - a copied struct
/// carrying a delegate field (<see cref="DrawBackground"/>) is a well-known footgun.
/// </summary>
public sealed class ReconnectScreenTheme
{
    /// <summary>
    /// The full-window scrim colour AND alpha, painted first behind everything else. The alpha channel doubles
    /// as the takeover's opacity: 1 gives an opaque maintenance-page look, a lower value lets the game world show
    /// dimly through. Multiplied by the screen's <see cref="Screen.TransitionAlpha"/> while animating on/off.
    /// </summary>
    public Vector4 Scrim = new(0f, 0f, 0f, 0.55f);

    /// <summary>Title text colour.</summary>
    public Vector4 TitleColor = GuiTheme.Default.Text;

    /// <summary>Countdown text colour.</summary>
    public Vector4 CountdownColor = GuiTheme.Default.AccentBright;

    /// <summary>Reassurance / attempt / retry body text colour.</summary>
    public Vector4 BodyColor = GuiTheme.Default.TextMuted;

    /// <summary>Spinner dot colour.</summary>
    public Vector4 SpinnerColor = GuiTheme.Default.Accent;

    /// <summary>Whether the indeterminate spinner is drawn. Default true.</summary>
    public bool ShowSpinner = true;

    /// <summary>Title shown for an unplanned drop (<see cref="ConnectionStatusKind.Reconnecting"/>, or a planned
    /// update whose countdown has expired).</summary>
    public LocalizedText ReconnectingTitle = ReconnectStrings.Title;

    /// <summary>Title shown for a planned server update (<see cref="ConnectionStatusKind.PlannedUpdate"/>) while
    /// a live countdown is showing.</summary>
    public LocalizedText PlannedUpdateTitle = ReconnectStrings.PlannedTitle;

    /// <summary>Reassurance sub-line drawn under the title/countdown. Skipped when it resolves empty.</summary>
    public LocalizedText Reassurance = ReconnectStrings.Reassurance;

    /// <summary>Format template for the attempt-counter line. Takes <c>{0}</c> = the attempt number.</summary>
    public StringId AttemptLineFormat = ReconnectStrings.AttemptLine;

    /// <summary>Format template for the retry-countdown line. Takes <c>{0}</c> = whole seconds until retry.</summary>
    public StringId RetryLineFormat = ReconnectStrings.RetryLine;

    /// <summary>Style for the action buttons in the button row.</summary>
    public GuiStyle ButtonStyle = GuiStyle.Default;

    /// <summary>
    /// Optional fully-custom background hook. When set it is called once per frame with the batch, a 1x1 white
    /// texture, and the design bounds, painted AFTER the scrim and BEFORE the content - so a consumer can layer
    /// art or a logo over an opaque scrim instead of replacing it.
    /// </summary>
    public Action<SpriteBatch, Texture2D, Rect>? DrawBackground;

    /// <summary>Uniform scale applied to the title text.</summary>
    public float TitleScale = 0.85f;

    /// <summary>Uniform scale applied to the countdown text.</summary>
    public float CountdownScale = 2.2f;

    /// <summary>Uniform scale applied to the body (reassurance / attempt / retry) text.</summary>
    public float BodyScale = 0.6f;

    /// <summary>Radius of the spinner's dot ring, in design units.</summary>
    public float SpinnerRadius = 26f;

    /// <summary>Number of dots in the spinner ring.</summary>
    public int SpinnerDotCount = 10;

    /// <summary>Spinner rotation speed in Hz (full revolutions per second).</summary>
    public float SpinnerSpeedHz = 0.9f;

    /// <summary>Width of each action button.</summary>
    public float ButtonWidth = 200f;

    /// <summary>Height of each action button.</summary>
    public float ButtonHeight = 44f;

    /// <summary>Horizontal gap between adjacent action buttons.</summary>
    public float ButtonGap = 16f;

    /// <summary>A fresh default theme (neutral palette from <see cref="GuiTheme.Default"/>).</summary>
    public static ReconnectScreenTheme Default => new();
}
