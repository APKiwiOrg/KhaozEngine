using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Updates;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// Look, labels, and trigger binding for <see cref="UpdateOverlayView"/>. Every visual is injected (no
/// hard-coded colours in the view); <see cref="Default"/> reproduces a neutral SpaceGame-style palette.
/// Set properties to retheme, or override <see cref="TitleFor"/>/<see cref="BodyFor"/>/<see cref="AccentFor"/>
/// for fully custom text. Colours are <see cref="Vector4"/> (RGBA 0..1); the <see cref="Color"/> literals
/// convert implicitly.
/// </summary>
/// <remarks>
/// The default <see cref="TitleFor"/>/<see cref="BodyFor"/> are localization-aware: each line resolves through
/// the ambient <see cref="LocalizationContext.Catalog"/> against the engine-owned
/// <see cref="UpdateOverlayStrings"/> keys, and falls back to the built-in English
/// (<see cref="UpdateOverlayStrings.EnglishDefaults"/>) whenever no catalog is wired or the key is absent. A
/// game gets localized overlay text just by adding the <c>update.overlay.*</c> keys to its catalog, with no
/// theme subclass. Overriding <see cref="TitleFor"/>/<see cref="BodyFor"/> still fully replaces this (the
/// override never calls back into the catalog resolution).
/// </remarks>
public class UpdateOverlayTheme
{
    // Panel + chrome
    public Vector4 DimFill = Color.FromBytes(0, 0, 0, 140);
    public Vector4 PanelFill = Color.FromBytes(12, 16, 28, 230);
    public Vector4 BodyText = Color.FromBytes(180, 190, 210);
    public Vector4 ProgressBackground = Color.FromBytes(30, 40, 60, 200);
    public Vector4 ProgressFill = Color.FromBytes(80, 160, 255, 230);

    // Per-state accent (title text + border tint)
    public Vector4 AvailableAccent = Color.FromBytes(100, 200, 255);
    public Vector4 DownloadingAccent = Color.FromBytes(100, 200, 255);
    public Vector4 ReadyAccent = Color.FromBytes(120, 255, 120);
    public Vector4 ApplyingAccent = Color.FromBytes(255, 220, 100);
    public Vector4 FailedAccent = Color.FromBytes(255, 140, 100);

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

    /// <summary>Accent colour for <paramref name="state"/> (title text + border).</summary>
    public virtual Vector4 AccentFor(UpdateState state) => state switch
    {
        UpdateState.UpdateAvailable => AvailableAccent,
        UpdateState.Downloading => DownloadingAccent,
        UpdateState.ReadyToApply => ReadyAccent,
        UpdateState.Applying => ApplyingAccent,
        UpdateState.Failed => FailedAccent,
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
        _ => string.Empty,
    };

    /// <summary>
    /// Body line for <paramref name="state"/>. Resolves through the ambient
    /// <see cref="LocalizationContext.Catalog"/> against the <see cref="UpdateOverlayStrings"/> keys, falling
    /// back to the built-in English when no catalog is wired or the key is absent.
    /// </summary>
    public virtual string BodyFor(UpdateState state, IUpdateStatus status) => state switch
    {
        UpdateState.UpdateAvailable => Localize(UpdateOverlayStrings.AvailableBody, TriggerKeyLabel),
        UpdateState.Downloading => Localize(UpdateOverlayStrings.DownloadingBody,
            status.FilesDownloaded, status.TotalFilesToDownload,
            status.BytesDownloaded / (1024d * 1024d), status.TotalDownloadBytes / (1024d * 1024d)),
        UpdateState.ReadyToApply => Localize(UpdateOverlayStrings.ReadyBody, TriggerKeyLabel),
        UpdateState.Applying => Localize(UpdateOverlayStrings.ApplyingBody),
        UpdateState.Failed => Localize(UpdateOverlayStrings.FailedBody, TriggerKeyLabel),
        _ => string.Empty,
    };

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
