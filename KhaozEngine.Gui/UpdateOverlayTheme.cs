using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Updates;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// Look, labels, and trigger binding for <see cref="UpdateOverlayView"/>. Every visual is injected (no
/// hard-coded colours in the view); <see cref="Default"/> reproduces a neutral SpaceGame-style palette.
/// Set properties to retheme, or override <see cref="TitleFor"/>/<see cref="BodyFor"/>/<see cref="AccentFor"/>
/// for fully custom (e.g. localized) text. Colours are <see cref="Vector4"/> (RGBA 0..1); the
/// <see cref="Color"/> literals convert implicitly.
/// </summary>
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

    /// <summary>Title line for <paramref name="state"/>.</summary>
    public virtual string TitleFor(UpdateState state, string? remoteVersion) => state switch
    {
        UpdateState.UpdateAvailable => $"Update Available - v{remoteVersion}",
        UpdateState.Downloading => "Downloading Update...",
        UpdateState.ReadyToApply => $"Update v{remoteVersion} Ready",
        UpdateState.Applying => "Applying Update...",
        UpdateState.Failed => "Update Failed",
        _ => string.Empty,
    };

    /// <summary>Body line for <paramref name="state"/>.</summary>
    public virtual string BodyFor(UpdateState state, IUpdateStatus status) => state switch
    {
        UpdateState.UpdateAvailable => $"Press [{TriggerKeyLabel}] to download",
        UpdateState.Downloading => FormatDownloading(status),
        UpdateState.ReadyToApply => $"Press [{TriggerKeyLabel}] to restart and apply",
        UpdateState.Applying => "Game will restart shortly",
        UpdateState.Failed => $"Press [{TriggerKeyLabel}] to retry",
        _ => string.Empty,
    };

    static string FormatDownloading(IUpdateStatus s)
    {
        double mb = s.BytesDownloaded / (1024d * 1024d);
        double totalMb = s.TotalDownloadBytes / (1024d * 1024d);
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "Downloading {0}/{1} files ({2:0.0}/{3:0.0} MB)",
            s.FilesDownloaded, s.TotalFilesToDownload, mb, totalMb);
    }

    /// <summary>A fresh default theme (neutral palette, [U] / gamepad-Y trigger).</summary>
    public static UpdateOverlayTheme Default => new();
}
