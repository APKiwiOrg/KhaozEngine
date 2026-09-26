using System;
using System.Runtime.InteropServices;

namespace KhaozEngine.Platform;

// Native macOS pasteboard access only. ClipboardInterop owns provider selection and fallback order.
internal static class MacPasteboardBackend
{
    private const string MacOsPasteboardTypeString = "public.utf8-plain-text";
    private const string MacOsPasteboardTypePng = "public.png";

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

    internal static bool TryGetText(out string text)
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

    internal static bool TrySetText(string text)
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

    internal static bool TrySetImagePng(byte[] pngBytes)
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
}
