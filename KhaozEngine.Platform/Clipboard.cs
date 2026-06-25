namespace KhaozEngine.Platform;

/// <summary>
/// Cross-platform system-clipboard access. Text get/set tries a registered window/GLFW provider first, then
/// macOS <c>NSPasteboard</c>, then an optional mobile bridge; RGBA images use Windows GDI (and macOS / mobile
/// for PNG). Each call returns a best-effort result. Nothing here throws: a missing or failing backend yields
/// an empty string or <c>false</c>.
/// </summary>
/// <remarks>
/// The text provider is the GLFW clipboard wired by <c>KhaozEngine.Windowing.AppWindow</c> at startup. It is
/// what makes text get/set work on Windows and Linux (and is the primary text path on macOS too); a windowless
/// or headless consumer that never opens an <c>AppWindow</c> registers no provider and so has no text clipboard
/// on Windows/Linux, falling back to <c>NSPasteboard</c> on macOS. See <see cref="RegisterTextProvider"/>.
/// </remarks>
public static class Clipboard
{
    /// <summary>
    /// Registers the window-system text-clipboard provider (the GLFW clipboard, wired by
    /// <c>KhaozEngine.Windowing.AppWindow</c>). It is preferred over <c>NSPasteboard</c> / the mobile bridge for
    /// text get and set. <paramref name="read"/> returns the clipboard text, or <c>null</c> when it could not
    /// read (so dispatch falls through to the OS backends); <paramref name="write"/> returns <c>true</c> on
    /// success. Provider exceptions are swallowed and treated as a fall-through. Games do not call this directly;
    /// <c>AppWindow</c> does it, and calls <see cref="ClearTextProvider"/> on dispose.
    /// </summary>
    public static void RegisterTextProvider(System.Func<string?> read, System.Func<string, bool> write)
        => ClipboardInterop.RegisterTextProvider(read, write);

    /// <summary>
    /// Removes any registered text provider, reverting text get/set to the <c>NSPasteboard</c> / mobile
    /// backends. <c>AppWindow</c> calls this on dispose so a torn-down GLFW handle is never dereferenced.
    /// </summary>
    public static void ClearTextProvider() => ClipboardInterop.ClearTextProvider();

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
