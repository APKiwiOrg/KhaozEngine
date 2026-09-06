using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Updates;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// Look, labels, and trigger binding for <see cref="UpdateOverlayView"/>. Every visual is injected (no
/// hard-coded colours in the view); <see cref="Default"/> reproduces a neutral SpaceGame-style palette.
/// Set properties to retheme, or override <see cref="TitleFor(UpdateState, string?)"/>/<see cref="BodyFor"/>/
/// <see cref="AccentFor"/> for fully custom text. Colours are <see cref="Vector4"/> (RGBA 0..1); the
/// <see cref="Color"/> literals convert implicitly.
/// </summary>
/// <remarks>
/// The default <see cref="TitleFor(UpdateState, string?)"/>/<see cref="BodyFor"/> are localization-aware: each
/// line resolves through the ambient <see cref="LocalizationContext.Catalog"/> against the engine-owned
/// <see cref="UpdateOverlayStrings"/> keys, and falls back to the built-in English
/// (<see cref="UpdateOverlayStrings.EnglishDefaults"/>) whenever no catalog is wired or the key is absent. A
/// game gets localized overlay text just by adding the <c>update.overlay.*</c> keys to its catalog, with no
/// theme subclass. Overriding <see cref="TitleFor(UpdateState, string?)"/>/<see cref="BodyFor"/> still fully
/// replaces this (the override never calls back into the catalog resolution). Required updates render via
/// <see cref="TitleFor(UpdateState, IUpdateStatus)"/>, which adds the <c>*.required</c> variants, and
/// <see cref="HintFor"/> adds the third line: the dismiss prompt for a panel the player may decline.
/// </remarks>
public class UpdateOverlayTheme
{
    // Panel + chrome
    public Vector4 DimFill = Color.FromBytes(0, 0, 0, 140);
    public Vector4 PanelFill = Color.FromBytes(12, 16, 28, 230);
    public Vector4 BodyText = Color.FromBytes(180, 190, 210);
    public Vector4 HintText = Color.FromBytes(130, 140, 160);
    public Vector4 ProgressBackground = Color.FromBytes(30, 40, 60, 200);
    public Vector4 ProgressFill = Color.FromBytes(80, 160, 255, 230);

    // Per-state accent (title text + border tint)
    public Vector4 AvailableAccent = Color.FromBytes(100, 200, 255);
    public Vector4 DownloadingAccent = Color.FromBytes(100, 200, 255);
    public Vector4 ReadyAccent = Color.FromBytes(120, 255, 120);
    public Vector4 ApplyingAccent = Color.FromBytes(255, 220, 100);
    public Vector4 FailedAccent = Color.FromBytes(255, 140, 100);
    public Vector4 UntrustedAccent = Color.FromBytes(255, 180, 80);

    // Layout
    public float PanelWidth = 480f;
    public float PanelPadding = 24f;
    public float TitleScale = 0.7f;
    public float BodyScale = 0.5f;
    public float ProgressBarHeight = 6f;
    public float BorderThickness = 1f;
    public float FadeSpeed = 4f; // alpha units/sec (~0.25s fade-in)

    // Trigger binding
    public Key TriggerKey = Key.U;
    public GamepadButton? TriggerButton = GamepadButton.Y;
    public string TriggerKeyLabel = "U";

    // Dismiss binding: the second key, which puts the panel away for a state the player is allowed to decline
    // (see UpdateOverlayView.IsDismissible). Rebind it the same way as the trigger. A required update ignores
    // it entirely, so this can never hide a mandatory update.
    public Key DismissKey = Key.Escape;
    public GamepadButton? DismissButton = GamepadButton.B;
    public string DismissKeyLabel = "Esc";

    /// <summary>Accent colour for <paramref name="state"/> (title text + border).</summary>
    public virtual Vector4 AccentFor(UpdateState state) => state switch
    {
        UpdateState.UpdateAvailable => AvailableAccent,
        UpdateState.Downloading => DownloadingAccent,
        UpdateState.ReadyToApply => ReadyAccent,
        UpdateState.Applying => ApplyingAccent,
        UpdateState.Failed => FailedAccent,
        UpdateState.Untrusted => UntrustedAccent,
        _ => AvailableAccent,
    };

    /// <summary>
    /// Title line for <paramref name="state"/>. Resolves through the ambient
    /// <see cref="LocalizationContext.Catalog"/> against the <see cref="UpdateOverlayStrings"/> keys, falling
    /// back to the built-in English when no catalog is wired or the key is absent.
    /// </summary>
    public virtual string TitleFor(UpdateState state, string? remoteVersion) => state switch
    {
        UpdateState.UpdateAvailable => Localize(UpdateOverlayStrings.AvailableTitle, remoteVersion ?? string.Empty),
        UpdateState.Downloading => Localize(UpdateOverlayStrings.DownloadingTitle),
        UpdateState.ReadyToApply => Localize(UpdateOverlayStrings.ReadyTitle, remoteVersion ?? string.Empty),
        UpdateState.Applying => Localize(UpdateOverlayStrings.ApplyingTitle),
        UpdateState.Failed => Localize(UpdateOverlayStrings.FailedTitle),
        UpdateState.Untrusted => Localize(UpdateOverlayStrings.UntrustedTitle),
        _ => string.Empty,
    };

    /// <summary>
    /// Required-aware title line: for a required update (<see cref="IUpdateStatus.IsRequired"/>) it resolves the
    /// <c>*.required</c> keys (which convey mandatoriness and carry no keypress prompt); otherwise it delegates
    /// to <see cref="TitleFor(UpdateState, string?)"/>, so a theme that only overrides that keeps working for the
    /// ordinary optional case. This is the overload <see cref="UpdateOverlayView"/> renders with; override it to
    /// fully customize required-update titles.
    /// </summary>
    public virtual string TitleFor(UpdateState state, IUpdateStatus status)
    {
        if (!status.IsRequired)
        {
            return TitleFor(state, status.RemoteVersion);
        }
        string version = status.RemoteVersion ?? string.Empty;
        return state switch
        {
            UpdateState.UpdateAvailable => Localize(UpdateOverlayStrings.AvailableTitleRequired, version),
            UpdateState.Downloading => Localize(UpdateOverlayStrings.DownloadingTitleRequired),
            UpdateState.ReadyToApply => Localize(UpdateOverlayStrings.ReadyTitleRequired, version),
            UpdateState.Applying => Localize(UpdateOverlayStrings.ApplyingTitleRequired),
            UpdateState.Failed => Localize(UpdateOverlayStrings.FailedTitleRequired),
            UpdateState.Untrusted => Localize(UpdateOverlayStrings.UntrustedTitle),
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Body line for <paramref name="state"/>. Resolves through the ambient
    /// <see cref="LocalizationContext.Catalog"/> against the <see cref="UpdateOverlayStrings"/> keys, falling
    /// back to the built-in English when no catalog is wired or the key is absent. For a required update
    /// (<see cref="IUpdateStatus.IsRequired"/>) the available/ready bodies drop the now-inapplicable keypress
    /// prompt (the client auto-advances); the other bodies are shared with the optional case. The failed body
    /// likewise drops its retry prompt once the session's apply budget is spent
    /// (<see cref="IUpdateStatus.ApplyAttemptsExhausted"/>), because there is no retry left to offer.
    /// </summary>
    public virtual string BodyFor(UpdateState state, IUpdateStatus status) => state switch
    {
        UpdateState.UpdateAvailable => status.IsRequired
            ? Localize(UpdateOverlayStrings.AvailableBodyRequired)
            : Localize(UpdateOverlayStrings.AvailableBody, TriggerKeyLabel),
        UpdateState.Downloading => Localize(UpdateOverlayStrings.DownloadingBody,
            status.FilesDownloaded, status.TotalFilesToDownload,
            status.BytesDownloaded / (1024d * 1024d), status.TotalDownloadBytes / (1024d * 1024d)),
        UpdateState.ReadyToApply => status.IsRequired
            ? Localize(UpdateOverlayStrings.ReadyBodyRequired)
            : Localize(UpdateOverlayStrings.ReadyBody, TriggerKeyLabel),
        UpdateState.Applying => Localize(UpdateOverlayStrings.ApplyingBody),
        UpdateState.Failed => status.ApplyAttemptsExhausted
            ? Localize(UpdateOverlayStrings.FailedBodyExhausted)
            : Localize(UpdateOverlayStrings.FailedBody, TriggerKeyLabel),
        UpdateState.Untrusted => Localize(UpdateOverlayStrings.UntrustedBody),
        _ => string.Empty,
    };

    /// <summary>
    /// The third line: the dismiss prompt, drawn under the body (and under the progress bar) whenever the
    /// player may put this panel away, and empty otherwise so the panel keeps its two-line layout. Empty for a
    /// required update, which is never dismissible, and for the in-flight states
    /// (<see cref="UpdateState.Downloading"/>, <see cref="UpdateState.Applying"/>) the player already started.
    /// Override to reword it, or return <see cref="string.Empty"/> throughout to drop the line while keeping
    /// the key working.
    /// </summary>
    public virtual string HintFor(UpdateState state, IUpdateStatus status)
        => status.IsRequired || !UpdateOverlayView.IsDismissible(state)
            ? string.Empty
            : Localize(UpdateOverlayStrings.DismissHint, DismissKeyLabel);

    /// <summary>
    /// Resolves an overlay <see cref="StringId"/> with format args. Uses the ambient
    /// <see cref="LocalizationContext.Catalog"/> when it is wired AND carries the key (culture-aware format);
    /// otherwise uses <see cref="UpdateOverlayStrings.EnglishDefaults"/> (invariant English, identical to the
    /// pre-localization overlay). A game that wires a catalog without these keys, or none at all, sees English.
    /// </summary>
    static string Localize(StringId id, params object?[] args)
    {
        IStringCatalog catalog = LocalizationContext.Catalog is { } c && c.TryGet(id.Key, out _)
            ? c
            : UpdateOverlayStrings.EnglishDefaults;
        return args.Length == 0 ? catalog.Get(id.Key) : catalog.Format(id.Key, args);
    }

    /// <summary>A fresh default theme (neutral palette, [U] / gamepad-Y trigger).</summary>
    public static UpdateOverlayTheme Default => new();
}
