using System;
using System.Runtime.InteropServices;

namespace KhaozEngine.Platform
{
    /// <summary>
    /// Points a window's Windows <b>class</b> icon (<c>GCLP_HICON</c>/<c>GCLP_HICONSM</c>) at the icon already
    /// assigned to that window via <c>WM_SETICON</c>, so the running app's <b>taskbar button</b> shows the app icon
    /// instead of the generic default. This is the piece GLFW leaves out: <c>glfwSetWindowIcon</c> only sends
    /// <c>WM_SETICON</c> (ICON_BIG/ICON_SMALL), which drives the title bar and Alt-Tab, while the Windows taskbar
    /// button is resolved from the window <b>class</b> icon - which GLFW registers as the generic
    /// <c>IDI_APPLICATION</c> whenever the exe has no resource literally named <c>GLFW_ICON</c> (a .NET
    /// <c>&lt;ApplicationIcon&gt;</c> is embedded under a different name, so GLFW never finds it). The result is the
    /// classic split: correct title-bar icon, generic taskbar icon. Copying the window's live <c>WM_SETICON</c>
    /// handle onto the class icon closes that gap.
    /// <para>Pure BCL P/Invoke (user32), guarded by <see cref="OperatingSystem.IsWindows"/> and wrapped so any
    /// failure degrades to <c>false</c> rather than throwing into startup. A no-op returning <c>false</c> off
    /// Windows, on a null handle, or when the window has no icon set yet. Sibling to <see cref="WindowsAppId"/>
    /// (taskbar identity) and <see cref="ApplicationIcon"/> (the macOS Dock icon); macOS/Linux have no window-class
    /// icon, so this is a no-op there too.</para>
    /// </summary>
    public static class WindowsWindowIcon
    {
        /// <summary>
        /// Copy the window's current large/small <c>WM_SETICON</c> handles onto its window <b>class</b> icon
        /// (<c>GCLP_HICON</c>/<c>GCLP_HICONSM</c>) so the taskbar button uses them. Call AFTER the window icon has
        /// been set (e.g. right after <c>glfwSetWindowIcon</c>) and, for the taskbar button to be <i>born</i> with
        /// the icon, while the window is still hidden - the button is created on first show and reads the class icon
        /// then. <paramref name="hwnd"/> is the native Win32 window handle. Returns <c>true</c> when at least one
        /// class icon was set, <c>false</c> off Windows, on a zero handle, or when the window exposes no icon
        /// (nothing to copy). Never throws.
        /// </summary>
        public static bool TrySyncTaskbarIconFromWindow(nint hwnd)
        {
            if (!OperatingSystem.IsWindows()) return false;
            if (hwnd == 0) return false;
            try
            {
                // The handles GLFW just installed via WM_SETICON. WM_GETICON round-trips through DefWindowProc
                // (GLFW does not special-case it), so these are the exact HICONs GLFW built from the RGBA images -
                // reused as-is, so we never rebuild an icon from pixels. lParam 0 = the window's current DPI.
                nint big = SendMessageW(hwnd, WM_GETICON, ICON_BIG, 0);
                nint small = SendMessageW(hwnd, WM_GETICON, ICON_SMALL, 0);
                bool any = false;
                if (big != 0) { SetClassLongPtrW(hwnd, GCLP_HICON, big); any = true; }
                if (small != 0) { SetClassLongPtrW(hwnd, GCLP_HICONSM, small); any = true; }
                return any;
            }
            catch
            {
                // Includes EntryPointNotFoundException on a hypothetical 32-bit host (SetClassLongPtrW is a
                // 64-bit-only export); degrade to a no-op rather than crash startup. Windows testers run x64.
                return false;
            }
        }

        const int WM_GETICON = 0x007F;
        const nint ICON_SMALL = 0;
        const nint ICON_BIG = 1;
        const int GCLP_HICON = -14;
        const int GCLP_HICONSM = -34;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern nint SendMessageW(nint hWnd, int msg, nint wParam, nint lParam);

        // 64-bit-safe (SetClassLongW truncates a handle on x64). The ...Ptr entry point is a 64-bit-only export;
        // the caller's try/catch turns a failed resolve on a 32-bit host into a clean no-op.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern nint SetClassLongPtrW(nint hWnd, int nIndex, nint dwNewLong);
    }
}
