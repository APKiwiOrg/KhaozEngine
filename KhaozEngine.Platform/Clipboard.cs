namespace KhaozEngine.Platform;

/// <summary>
/// Cross-platform system-clipboard access. Each call tries the available platform backends in order
/// (SDL2, then macOS <c>NSPasteboard</c>, then an optional mobile bridge; Windows GDI for RGBA images)
/// and returns a best-effort result. Nothing here throws: a missing or failing backend yields an empty
/// string or <c>false</c>.
/// </summary>
public static class Clipboard
{
    /// <summary>
    /// Fully-qualified type name of the consumer's mobile clipboard bridge, used to resolve the
    /// Android/iOS backend by reflection across loaded assemblies. The named type must expose static
    /// <c>bool TryGetClipboardText(out string)</c>, <c>bool TrySetClipboardText(string)</c>, and
    /// <c>bool TrySetClipboardImagePng(byte[])</c> methods (public or non-public). Leave <c>null</c>/empty
    /// (the default) to skip the mobile fallback. Set it once at startup; reassigning clears the
    /// resolution cache so a later value is picked up.
    /// </summary>
    public static string? MobileBridgeTypeName
    {
        get => ClipboardInterop.MobileBridgeTypeName;
        set => ClipboardInterop.MobileBridgeTypeName = value;
    }

    /// <summary>
    /// Returns the clipboard's plain text, or an empty string when the clipboard is empty or no backend
    /// is available.
    /// </summary>
    public static string TryGetClipboardText() => ClipboardInterop.TryGetClipboardText();

    /// <summary>
    /// Sets the clipboard's plain text. Returns <c>false</c> for null/empty input or when no backend
    /// could set it.
    /// </summary>
    public static bool TrySetClipboardText(string text) => ClipboardInterop.TrySetClipboardText(text);

    /// <summary>
    /// Puts a PNG image on the clipboard (macOS and mobile). Windows is not attempted for PNG; use
    /// <see cref="TrySetClipboardImageRgba32"/> there. Returns <c>false</c> for null/empty input or when
    /// no backend could set it.
    /// </summary>
    public static bool TrySetClipboardImagePng(byte[] pngBytes) => ClipboardInterop.TrySetClipboardImagePng(pngBytes);

    /// <summary>
    /// Puts a raw RGBA32 (top-down, 4 bytes/pixel) image on the clipboard as a <c>CF_DIB</c> (Windows
    /// only). Returns <c>false</c> on other platforms or when the dimensions and buffer length disagree.
    /// </summary>
    public static bool TrySetClipboardImageRgba32(int width, int height, byte[] rgbaPixels)
        => ClipboardInterop.TrySetClipboardImageRgba32(width, height, rgbaPixels);
}
