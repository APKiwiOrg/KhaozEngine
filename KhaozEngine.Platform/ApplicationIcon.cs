using System;
using System.Runtime.InteropServices;

namespace KhaozEngine.Platform
{
    /// <summary>
    /// The running application's icon. Today this is the macOS Dock / Cmd-Tab icon: GLFW cannot set the Cocoa Dock
    /// icon (so <c>AppWindow.SetIcon</c> is a no-op on macOS), and an app launched via <c>dotnet run</c> has no
    /// <c>.app</c> bundle <c>.icns</c> to supply one, so such a run shows the generic document icon. This sets it
    /// at runtime through <c>NSApplication.setApplicationIconImage:</c>, which works for an unbundled process (the
    /// GLFW window already created the shared <c>NSApplication</c>). Windows/Linux have no equivalent runtime Dock
    /// icon; their taskbar icon is the GLFW window icon (<c>AppWindow.SetIcon</c>) and the Windows Explorer icon is
    /// the per-app <c>&lt;ApplicationIcon&gt;</c>, so this class is a no-op there.
    /// <para>Interop mirrors <see cref="ClipboardInterop"/>'s libobjc pattern (autorelease pool + <c>objc_msgSend</c>);
    /// it is self-contained so it never destabilises the clipboard path. Every call is wrapped so a Cocoa failure
    /// degrades to <c>false</c> rather than throwing into the game loop.</para>
    /// </summary>
    public static class ApplicationIcon
    {
        const string Objc = "/usr/lib/libobjc.A.dylib";

        /// <summary>
        /// macOS only: set the running app's Dock / Cmd-Tab icon from PNG-encoded bytes (any size; macOS scales
        /// it). Decodes via <c>NSImage</c>, so PNG is the input the engine already produces. Returns <c>true</c>
        /// when the Cocoa call chain succeeded, <c>false</c> on a non-macOS OS, on null/empty input, or if any
        /// Cocoa step failed. Safe to call once at startup after the window (hence the shared NSApplication) exists.
        /// The resulting <c>NSImage</c> is intentionally retained for the process lifetime (it is the app icon).
        /// </summary>
        public static bool TrySetMacDockIcon(byte[] pngBytes)
        {
            if (!OperatingSystem.IsMacOS() || pngBytes is null || pngBytes.Length == 0)
                return false;

            IntPtr pool = IntPtr.Zero;
            GCHandle pinned = default;
            try
            {
                pool = CreateAutoreleasePool();
                pinned = GCHandle.Alloc(pngBytes, GCHandleType.Pinned);

                // NSData* data = [NSData dataWithBytes:ptr length:len];  (autoreleased)
                IntPtr dataClass = objc_getClass("NSData");
                if (dataClass == IntPtr.Zero) return false;
                IntPtr data = Send_ptr_nuint(dataClass, sel_registerName("dataWithBytes:length:"),
                                             pinned.AddrOfPinnedObject(), (nuint)pngBytes.Length);
                if (data == IntPtr.Zero) return false;

                // NSImage* img = [[NSImage alloc] initWithData:data];  (owned +1, kept as the app icon for life)
                IntPtr imageClass = objc_getClass("NSImage");
                if (imageClass == IntPtr.Zero) return false;
                IntPtr allocated = Send(imageClass, sel_registerName("alloc"));
                if (allocated == IntPtr.Zero) return false;
                IntPtr image = Send_ptr(allocated, sel_registerName("initWithData:"), data);
                if (image == IntPtr.Zero) return false;

                // NSApplication* app = [NSApplication sharedApplication];  (created by GLFW already)
                IntPtr appClass = objc_getClass("NSApplication");
                if (appClass == IntPtr.Zero) return false;
                IntPtr app = Send(appClass, sel_registerName("sharedApplication"));
                if (app == IntPtr.Zero) return false;

                // [app setApplicationIconImage:img];
                SendVoid_ptr(app, sel_registerName("setApplicationIconImage:"), image);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (pinned.IsAllocated) pinned.Free();
                DrainAutoreleasePool(pool);
            }
        }

        static IntPtr CreateAutoreleasePool()
        {
            IntPtr poolClass = objc_getClass("NSAutoreleasePool");
            if (poolClass == IntPtr.Zero) return IntPtr.Zero;
            IntPtr pool = Send(poolClass, sel_registerName("alloc"));
            return pool == IntPtr.Zero ? IntPtr.Zero : Send(pool, sel_registerName("init"));
        }

        static void DrainAutoreleasePool(IntPtr pool)
        {
            if (pool != IntPtr.Zero) SendVoid(pool, sel_registerName("drain"));
        }

        [DllImport(Objc)]
        static extern IntPtr objc_getClass(string name);

        [DllImport(Objc)]
        static extern IntPtr sel_registerName(string name);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send_ptr(IntPtr receiver, IntPtr selector, IntPtr arg0);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        static extern IntPtr Send_ptr_nuint(IntPtr receiver, IntPtr selector, IntPtr arg0, nuint arg1);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        static extern void SendVoid(IntPtr receiver, IntPtr selector);

        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        static extern void SendVoid_ptr(IntPtr receiver, IntPtr selector, IntPtr arg0);
    }
}
