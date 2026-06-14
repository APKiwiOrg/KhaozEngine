using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace KhaozEngine.Platform;

// Cross-platform clipboard native interop. The public entry points live on Clipboard; this class
// holds the per-platform marshaling (SDL2 / Windows GDI / macOS Objective-C / reflection-resolved
// mobile bridge) plus the pure dispatch/fallback spine the headless tests drive with fakes.
//
// The native marshaling below is ported verbatim from SpaceGame's ClipboardInterop; the only
// behavioural changes from that source are (1) the dispatch methods delegate to the pure
// Dispatch*/BuildWindowsDib helpers so the ordering/fallback logic is testable without touching
// native code, and (2) the mobile bridge type name is configurable instead of hard-coded.
internal static class ClipboardInterop
{
    private const uint GlobalMoveable = 0x0002;
    private const int ClipboardOpenRetryCount = 5;
    private const int ClipboardOpenRetryDelayMilliseconds = 8;
    private const uint CfDib = 8;
    private const int DibHeaderSize = 40;
    private const string MacOsPasteboardTypeString = "public.utf8-plain-text";
    private const string MacOsPasteboardTypePng = "public.png";

    private static readonly object MobileClipboardBridgeLock = new();
    private static string? mobileClipboardBridgeTypeName;
    private static bool mobileClipboardBridgeInitialized;
    private static bool mobileClipboardBridgeAvailable;
    private static MethodInfo mobileTryGetClipboardTextMethod = null!;
    private static MethodInfo mobileTrySetClipboardTextMethod = null!;
    private static MethodInfo mobileTrySetClipboardImagePngMethod = null!;

    /// <summary>Delegate matching a try-get-text backend (<c>bool Backend(out string text)</c>).</summary>
    internal delegate bool TryGetTextBackend(out string text);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_GetClipboardText();

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SDL_SetClipboardText(string text);

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)]
    private static extern void SDL_free(IntPtr memory);

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

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend_string(IntPtr receiver, IntPtr selector, string argument);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr IntPtr_objc_msgSend_IntPtr_nuint(IntPtr receiver, IntPtr selector, IntPtr argument0, nuint argument1);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool Bool_objc_msgSend_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr argument0, IntPtr argument1);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint NInt_objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void Void_objc_msgSend(IntPtr receiver, IntPtr selector);

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

    public static string TryGetClipboardText()
    {
        return DispatchGetText(
            SdlGetText,
            OperatingSystem.IsMacOS(),
            TryGetClipboardTextMacOs,
            OperatingSystem.IsAndroid() || OperatingSystem.IsIOS(),
            TryGetClipboardTextMobile);
    }

    public static bool TrySetClipboardText(string text)
    {
        return DispatchSetText(
            text,
            () => SDL_SetClipboardText(text),
            OperatingSystem.IsMacOS(),
            TrySetClipboardTextMacOs,
            OperatingSystem.IsAndroid() || OperatingSystem.IsIOS(),
            TrySetClipboardTextMobile);
    }

    public static bool TrySetClipboardImagePng(byte[] pngBytes)
    {
        return DispatchSetImagePng(
            pngBytes,
            OperatingSystem.IsMacOS(),
            TrySetClipboardImagePngMacOs,
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
        Func<(bool produced, string text)> sdlGet,
        bool isMacOs,
        TryGetTextBackend macOsGet,
        bool isMobile,
        TryGetTextBackend mobileGet)
    {
        (bool produced, string text) = sdlGet();
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
        Func<int> sdlSet,
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
            if (sdlSet() == 0)
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

    private static (bool produced, string text) SdlGetText()
    {
        IntPtr textPointer = IntPtr.Zero;
        try
        {
            textPointer = SDL_GetClipboardText();
            if (textPointer != IntPtr.Zero)
            {
                return (true, Marshal.PtrToStringUTF8(textPointer) ?? string.Empty);
            }
        }
        catch
        {
            // Fall through to platform fallback.
        }
        finally
        {
            if (textPointer != IntPtr.Zero)
            {
                TrySdlFree(textPointer);
            }
        }

        return (false, string.Empty);
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

    private static bool TryGetClipboardTextMacOs(out string text)
    {
        text = string.Empty;
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        IntPtr autoreleasePool = IntPtr.Zero;
        try
        {
            autoreleasePool = CreateMacAutoreleasePool();
            IntPtr pasteboard = GetMacGeneralPasteboard();
            if (pasteboard == IntPtr.Zero)
            {
                return false;
            }

            IntPtr clipboardType = CreateMacString(MacOsPasteboardTypeString);
            if (clipboardType == IntPtr.Zero)
            {
                return false;
            }

            IntPtr stringForTypeSelector = sel_registerName("stringForType:");
            IntPtr textValue = IntPtr_objc_msgSend_IntPtr(pasteboard, stringForTypeSelector, clipboardType);
            if (textValue == IntPtr.Zero)
            {
                return true;
            }

            IntPtr utf8StringSelector = sel_registerName("UTF8String");
            IntPtr utf8Pointer = IntPtr_objc_msgSend(textValue, utf8StringSelector);
            if (utf8Pointer == IntPtr.Zero)
            {
                return true;
            }

            text = Marshal.PtrToStringUTF8(utf8Pointer) ?? string.Empty;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            DisposeMacAutoreleasePool(autoreleasePool);
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
                Type bridgeType = assemblies[i].GetType(bridgeTypeName, throwOnError: false, ignoreCase: false);
                if (bridgeType is null)
                {
                    continue;
                }

                MethodInfo tryGetMethod = bridgeType.GetMethod(
                    "TryGetClipboardText",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(string).MakeByRefType() },
                    modifiers: null);
                MethodInfo trySetTextMethod = bridgeType.GetMethod(
                    "TrySetClipboardText",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(string) },
                    modifiers: null);
                MethodInfo trySetImageMethod = bridgeType.GetMethod(
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

    private static bool TrySetClipboardTextMacOs(string text)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(text))
        {
            return false;
        }

        IntPtr autoreleasePool = IntPtr.Zero;
        try
        {
            autoreleasePool = CreateMacAutoreleasePool();
            IntPtr pasteboard = GetMacGeneralPasteboard();
            if (pasteboard == IntPtr.Zero)
            {
                return false;
            }

            IntPtr textValue = CreateMacString(text);
            IntPtr clipboardType = CreateMacString(MacOsPasteboardTypeString);
            if (textValue == IntPtr.Zero || clipboardType == IntPtr.Zero)
            {
                return false;
            }

            ClearMacPasteboard(pasteboard);
            IntPtr setStringSelector = sel_registerName("setString:forType:");
            return Bool_objc_msgSend_IntPtr_IntPtr(pasteboard, setStringSelector, textValue, clipboardType);
        }
        catch
        {
            return false;
        }
        finally
        {
            DisposeMacAutoreleasePool(autoreleasePool);
        }
    }

    private static bool TrySetClipboardImagePngMacOs(byte[] pngBytes)
    {
        if (!OperatingSystem.IsMacOS() || pngBytes is null || pngBytes.Length == 0)
        {
            return false;
        }

        IntPtr autoreleasePool = IntPtr.Zero;
        GCHandle pinnedBytes = default;
        try
        {
            autoreleasePool = CreateMacAutoreleasePool();
            IntPtr pasteboard = GetMacGeneralPasteboard();
            if (pasteboard == IntPtr.Zero)
            {
                return false;
            }

            pinnedBytes = GCHandle.Alloc(pngBytes, GCHandleType.Pinned);
            IntPtr dataValue = CreateMacData(pinnedBytes.AddrOfPinnedObject(), (nuint)pngBytes.Length);
            IntPtr clipboardType = CreateMacString(MacOsPasteboardTypePng);
            if (dataValue == IntPtr.Zero || clipboardType == IntPtr.Zero)
            {
                return false;
            }

            ClearMacPasteboard(pasteboard);
            IntPtr setDataSelector = sel_registerName("setData:forType:");
            return Bool_objc_msgSend_IntPtr_IntPtr(pasteboard, setDataSelector, dataValue, clipboardType);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pinnedBytes.IsAllocated)
            {
                pinnedBytes.Free();
            }

            DisposeMacAutoreleasePool(autoreleasePool);
        }
    }

    private static IntPtr GetMacGeneralPasteboard()
    {
        IntPtr pasteboardClass = objc_getClass("NSPasteboard");
        if (pasteboardClass == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr generalPasteboardSelector = sel_registerName("generalPasteboard");
        return IntPtr_objc_msgSend(pasteboardClass, generalPasteboardSelector);
    }

    private static IntPtr CreateMacAutoreleasePool()
    {
        IntPtr poolClass = objc_getClass("NSAutoreleasePool");
        if (poolClass == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr allocSelector = sel_registerName("alloc");
        IntPtr initSelector = sel_registerName("init");
        IntPtr pool = IntPtr_objc_msgSend(poolClass, allocSelector);
        if (pool == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        return IntPtr_objc_msgSend(pool, initSelector);
    }

    private static void DisposeMacAutoreleasePool(IntPtr pool)
    {
        if (pool == IntPtr.Zero)
        {
            return;
        }

        IntPtr drainSelector = sel_registerName("drain");
        Void_objc_msgSend(pool, drainSelector);
    }

    private static IntPtr CreateMacString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return IntPtr.Zero;
        }

        IntPtr stringClass = objc_getClass("NSString");
        if (stringClass == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr selector = sel_registerName("stringWithUTF8String:");
        return IntPtr_objc_msgSend_string(stringClass, selector, value);
    }

    private static IntPtr CreateMacData(IntPtr bytesPointer, nuint length)
    {
        if (bytesPointer == IntPtr.Zero || length == 0)
        {
            return IntPtr.Zero;
        }

        IntPtr dataClass = objc_getClass("NSData");
        if (dataClass == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr selector = sel_registerName("dataWithBytes:length:");
        return IntPtr_objc_msgSend_IntPtr_nuint(dataClass, selector, bytesPointer, length);
    }

    private static void ClearMacPasteboard(IntPtr pasteboard)
    {
        if (pasteboard == IntPtr.Zero)
        {
            return;
        }

        IntPtr clearSelector = sel_registerName("clearContents");
        _ = NInt_objc_msgSend(pasteboard, clearSelector);
    }

    private static void TrySdlFree(IntPtr memory)
    {
        try
        {
            SDL_free(memory);
        }
        catch
        {
            // Ignore free failures.
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
