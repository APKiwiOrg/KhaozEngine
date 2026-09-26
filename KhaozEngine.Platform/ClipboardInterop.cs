using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace KhaozEngine.Platform;

// Cross-platform clipboard dispatch. The public entry points live on Clipboard; this class owns the
// provider/fallback spine, Windows DIB marshaling and the reflection-resolved mobile bridge.
// MacPasteboardBackend owns the macOS Objective-C path.
//
// Text get/set goes through an optional registered text provider FIRST, then macOS NSPasteboard, then the
// mobile bridge. KhaozEngine.Windowing's AppWindow registers a GLFW-backed provider at startup (via
// Clipboard.RegisterTextProvider); that is what makes text get/set work on Windows and Linux, and it is the
// primary text path on macOS too (NSPasteboard stays as the fallback for windowless/headless consumers). The
// SDL2 text path this code was ported with is gone: it needed an SDL video subsystem the GLFW host never
// initialises, so it produced nothing on the shipped runtime.
//
// The native marshaling in this package is ported from SpaceGame's ClipboardInterop. The behavioural changes
// from that source are (1) the dispatch methods delegate to the pure Dispatch* / BuildWindowsDib / ReadFromProvider /
// WriteToProvider helpers so the ordering/fallback logic is testable without touching native code, (2) the
// mobile bridge type name is configurable instead of hard-coded, and (3) the SDL2 text path is replaced by
// the registered provider seam.
internal static class ClipboardInterop
{
    private const uint GlobalMoveable = 0x0002;
    private const int ClipboardOpenRetryCount = 5;
    private const int ClipboardOpenRetryDelayMilliseconds = 8;
    private const uint CfDib = 8;
    private const int DibHeaderSize = 40;


    private static readonly object MobileClipboardBridgeLock = new();
    private static string? mobileClipboardBridgeTypeName;
    private static bool mobileClipboardBridgeInitialized;
    private static bool mobileClipboardBridgeAvailable;
    private static MethodInfo mobileTryGetClipboardTextMethod = null!;
    private static MethodInfo mobileTrySetClipboardTextMethod = null!;
    private static MethodInfo mobileTrySetClipboardImagePngMethod = null!;

    /// <summary>Delegate matching a try-get-text backend (<c>bool Backend(out string text)</c>).</summary>
    internal delegate bool TryGetTextBackend(out string text);

    private static readonly object TextProviderLock = new();
    private static Func<string?>? textProviderRead;
    private static Func<string, bool>? textProviderWrite;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    // Fully-qualified type name of the consumer's mobile clipboard bridge (resolved by reflection on
    // Android/iOS). Empty/null disables the mobile fallback. Reassigning clears the resolution cache.
    internal static string? MobileBridgeTypeName
    {
        get => mobileClipboardBridgeTypeName;
        set
        {
            lock (MobileClipboardBridgeLock)
            {
                mobileClipboardBridgeTypeName = value;
                mobileClipboardBridgeInitialized = false;
                mobileClipboardBridgeAvailable = false;
                mobileTryGetClipboardTextMethod = null!;
                mobileTrySetClipboardTextMethod = null!;
                mobileTrySetClipboardImagePngMethod = null!;
            }
        }
    }

    // Registers the window-system text-clipboard provider (GLFW, wired by AppWindow). read() returns the
    // clipboard text, or null when it could not read (e.g. GLFW not initialised) so dispatch falls through to
    // the OS backends; write() returns true on success. Preferred over NSPasteboard / mobile for text get/set.
    internal static void RegisterTextProvider(Func<string?> read, Func<string, bool> write)
    {
        lock (TextProviderLock)
        {
            textProviderRead = read;
            textProviderWrite = write;
        }
    }

    // Removes any registered text provider. AppWindow calls this on dispose, before GLFW is torn down, so a
    // stale window handle can never be dereferenced by a later clipboard call.
    internal static void ClearTextProvider()
    {
        lock (TextProviderLock)
        {
            textProviderRead = null;
            textProviderWrite = null;
        }
    }

    public static string TryGetClipboardText()
    {
        return DispatchGetText(
            () => ReadFromProvider(textProviderRead),
            OperatingSystem.IsMacOS(),
            MacPasteboardBackend.TryGetText,
            OperatingSystem.IsAndroid() || OperatingSystem.IsIOS(),
            TryGetClipboardTextMobile);
    }

    public static bool TrySetClipboardText(string text)
    {
        return DispatchSetText(
            text,
            () => WriteToProvider(textProviderWrite, text),
            OperatingSystem.IsMacOS(),
            MacPasteboardBackend.TrySetText,
            OperatingSystem.IsAndroid() || OperatingSystem.IsIOS(),
            TrySetClipboardTextMobile);
    }

    public static bool TrySetClipboardImagePng(byte[] pngBytes)
    {
        return DispatchSetImagePng(
            pngBytes,
            OperatingSystem.IsMacOS(),
            MacPasteboardBackend.TrySetImagePng,
            OperatingSystem.IsAndroid() || OperatingSystem.IsIOS(),
            TrySetClipboardImagePngMobile);
    }

    public static bool TrySetClipboardImageRgba32(int width, int height, byte[] rgbaPixels)
    {
        return DispatchSetImageRgba32(
            width,
            height,
            rgbaPixels,
            OperatingSystem.IsWindows(),
            TrySetClipboardDibWindows);
    }

    // ---- Pure dispatch/fallback spine (headless-testable; no native calls of its own) ----

    internal static string DispatchGetText(
        Func<(bool produced, string text)> providerGet,
        bool isMacOs,
        TryGetTextBackend macOsGet,
        bool isMobile,
        TryGetTextBackend mobileGet)
    {
        (bool produced, string text) = providerGet();
        if (produced)
        {
            return text;
        }

        if (isMacOs && macOsGet(out string macText))
        {
            return macText;
        }

        if (isMobile && mobileGet(out string mobileText))
        {
            return mobileText;
        }

        return string.Empty;
    }

    internal static bool DispatchSetText(
        string text,
        Func<bool> providerSet,
        bool isMacOs,
        Func<string, bool> macOsSet,
        bool isMobile,
        Func<string, bool> mobileSet)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            if (providerSet())
            {
                return true;
            }
        }
        catch
        {
            // Fall through to platform fallback.
        }

        if (isMacOs)
        {
            return macOsSet(text);
        }

        if (isMobile)
        {
            return mobileSet(text);
        }

        return false;
    }

    // Pure adapter: turns the registered text-read provider into the (produced, text) shape the spine wants.
    // A null provider, a null result, or a provider exception all mean "not produced" so dispatch falls
    // through to the OS backends. An empty (non-null) string is a produced value and wins over the fallbacks.
    internal static (bool produced, string text) ReadFromProvider(Func<string?>? read)
    {
        if (read is null)
        {
            return (false, string.Empty);
        }

        try
        {
            string? text = read();
            return text is null ? (false, string.Empty) : (true, text);
        }
        catch
        {
            return (false, string.Empty);
        }
    }

    // Pure adapter: invokes the registered text-write provider. A null provider or a provider exception is
    // treated as failure (false) so dispatch falls through to the OS backends.
    internal static bool WriteToProvider(Func<string, bool>? write, string text)
    {
        if (write is null)
        {
            return false;
        }

        try
        {
            return write(text);
        }
        catch
        {
            return false;
        }
    }

    internal static bool DispatchSetImagePng(
        byte[] pngBytes,
        bool isMacOs,
        Func<byte[], bool> macOsSet,
        bool isMobile,
        Func<byte[], bool> mobileSet)
    {
        if (pngBytes is null || pngBytes.Length == 0)
        {
            return false;
        }

        if (isMacOs)
        {
            return macOsSet(pngBytes);
        }

        if (isMobile)
        {
            return mobileSet(pngBytes);
        }

        // PNG payloads are not guaranteed to paste as images in all Windows apps.
        // Keep this API but do not attempt text/data-URI fallback.
        return false;
    }

    internal static bool DispatchSetImageRgba32(
        int width,
        int height,
        byte[] rgbaPixels,
        bool isWindows,
        Func<int, int, byte[], bool> windowsSet)
    {
        if (width <= 0 || height <= 0 || rgbaPixels is null || rgbaPixels.Length != width * height * 4)
        {
            return false;
        }

        if (isWindows)
        {
            return windowsSet(width, height, rgbaPixels);
        }

        return false;
    }

    // Packs RGBA32 top-down pixels into a 40-byte BITMAPINFOHEADER + BGRA bottom-up CF_DIB buffer.
    // Pure (no native calls): the Windows clipboard plumbing in TrySetClipboardDibWindows hands it off.
    internal static byte[] BuildWindowsDib(int width, int height, byte[] rgbaPixels)
    {
        int pixelDataSize = width * height * 4;
        int totalSize = DibHeaderSize + pixelDataSize;

        byte[] dibBuffer = new byte[totalSize];
        WriteInt32(dibBuffer, 0, DibHeaderSize);      // biSize
        WriteInt32(dibBuffer, 4, width);              // biWidth
        WriteInt32(dibBuffer, 8, height);             // biHeight (bottom-up)
        WriteInt16(dibBuffer, 12, 1);                 // biPlanes
        WriteInt16(dibBuffer, 14, 32);                // biBitCount
        WriteInt32(dibBuffer, 16, 0);                 // BI_RGB
        WriteInt32(dibBuffer, 20, pixelDataSize);     // biSizeImage
        WriteInt32(dibBuffer, 24, 0);                 // biXPelsPerMeter
        WriteInt32(dibBuffer, 28, 0);                 // biYPelsPerMeter
        WriteInt32(dibBuffer, 32, 0);                 // biClrUsed
        WriteInt32(dibBuffer, 36, 0);                 // biClrImportant

        // Convert RGBA top-down to BGRA bottom-up.
        int sourceStride = width * 4;
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * sourceStride;
            int dstRow = (height - 1 - y) * sourceStride;
            int dstBase = DibHeaderSize + dstRow;
            for (int x = 0; x < width; x++)
            {
                int src = srcRow + (x * 4);
                int dst = dstBase + (x * 4);
                dibBuffer[dst + 0] = rgbaPixels[src + 2]; // B
                dibBuffer[dst + 1] = rgbaPixels[src + 1]; // G
                dibBuffer[dst + 2] = rgbaPixels[src + 0]; // R
                dibBuffer[dst + 3] = rgbaPixels[src + 3]; // A (often ignored for BI_RGB)
            }
        }

        return dibBuffer;
    }

    private static bool TrySetClipboardDibWindows(int width, int height, byte[] rgbaPixels)
    {
        byte[] dibBuffer = BuildWindowsDib(width, height, rgbaPixels);
        int totalSize = dibBuffer.Length;

        IntPtr globalMemory = IntPtr.Zero;
        bool clipboardOpened = false;
        try
        {
            globalMemory = GlobalAlloc(GlobalMoveable, (UIntPtr)totalSize);
            if (globalMemory == IntPtr.Zero)
            {
                return false;
            }

            IntPtr memoryPointer = GlobalLock(globalMemory);
            if (memoryPointer == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                Marshal.Copy(dibBuffer, 0, memoryPointer, dibBuffer.Length);
            }
            finally
            {
                _ = GlobalUnlock(globalMemory);
            }

            for (int attempt = 0; attempt < ClipboardOpenRetryCount; attempt++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    clipboardOpened = true;
                    break;
                }

                Thread.Sleep(ClipboardOpenRetryDelayMilliseconds);
            }

            if (!clipboardOpened)
            {
                return false;
            }

            if (!EmptyClipboard())
            {
                return false;
            }

            if (SetClipboardData(CfDib, globalMemory) == IntPtr.Zero)
            {
                return false;
            }

            // Ownership transfers to the clipboard on success.
            globalMemory = IntPtr.Zero;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (globalMemory != IntPtr.Zero)
            {
                _ = GlobalFree(globalMemory);
            }

            if (clipboardOpened)
            {
                _ = CloseClipboard();
            }
        }
    }

    private static bool TryGetClipboardTextMobile(out string text)
    {
        text = string.Empty;
        if (!TryEnsureMobileClipboardBridge())
        {
            return false;
        }

        try
        {
            object[] args = new object[] { string.Empty };
            bool success = (bool)(mobileTryGetClipboardTextMethod.Invoke(null, args) ?? false);
            text = args[0] as string ?? string.Empty;
            return success;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetClipboardTextMobile(string text)
    {
        if (!TryEnsureMobileClipboardBridge() || string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            object[] args = new object[] { text };
            return (bool)(mobileTrySetClipboardTextMethod.Invoke(null, args) ?? false);
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetClipboardImagePngMobile(byte[] pngBytes)
    {
        if (!TryEnsureMobileClipboardBridge() || pngBytes is null || pngBytes.Length == 0)
        {
            return false;
        }

        try
        {
            object[] args = new object[] { pngBytes };
            return (bool)(mobileTrySetClipboardImagePngMethod.Invoke(null, args) ?? false);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryEnsureMobileClipboardBridge()
    {
        if (mobileClipboardBridgeInitialized)
        {
            return mobileClipboardBridgeAvailable;
        }

        lock (MobileClipboardBridgeLock)
        {
            if (mobileClipboardBridgeInitialized)
            {
                return mobileClipboardBridgeAvailable;
            }

            mobileClipboardBridgeInitialized = true;
            mobileClipboardBridgeAvailable = TryResolveMobileClipboardBridge();
            return mobileClipboardBridgeAvailable;
        }
    }

    private static bool TryResolveMobileClipboardBridge()
    {
        string? bridgeTypeName = mobileClipboardBridgeTypeName;
        if (string.IsNullOrEmpty(bridgeTypeName))
        {
            return false;
        }

        try
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type? bridgeType = assemblies[i].GetType(bridgeTypeName, throwOnError: false, ignoreCase: false);
                if (bridgeType is null)
                {
                    continue;
                }

                MethodInfo? tryGetMethod = bridgeType.GetMethod(
                    "TryGetClipboardText",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(string).MakeByRefType() },
                    modifiers: null);
                MethodInfo? trySetTextMethod = bridgeType.GetMethod(
                    "TrySetClipboardText",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(string) },
                    modifiers: null);
                MethodInfo? trySetImageMethod = bridgeType.GetMethod(
                    "TrySetClipboardImagePng",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(byte[]) },
                    modifiers: null);

                if (tryGetMethod is null || trySetTextMethod is null || trySetImageMethod is null)
                {
                    continue;
                }

                mobileTryGetClipboardTextMethod = tryGetMethod;
                mobileTrySetClipboardTextMethod = trySetTextMethod;
                mobileTrySetClipboardImagePngMethod = trySetImageMethod;
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteInt16(byte[] target, int offset, short value)
    {
        target[offset + 0] = (byte)(value & 0xFF);
        target[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteInt32(byte[] target, int offset, int value)
    {
        target[offset + 0] = (byte)(value & 0xFF);
        target[offset + 1] = (byte)((value >> 8) & 0xFF);
        target[offset + 2] = (byte)((value >> 16) & 0xFF);
        target[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
