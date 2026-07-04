using System;
using System.Runtime.InteropServices;

namespace KhaozEngine.Platform
{
    /// <summary>
    /// The running process's Windows taskbar identity (AppUserModelID). On Windows 10/11 the taskbar groups,
    /// pins, and resolves a running window's icon by the process's <b>explicit</b> AppUserModelID; a .NET apphost
    /// that never sets one gets a process-derived identity that fails to resolve the window/exe icon, so the
    /// running app's taskbar button shows the generic <c>.exe</c> placeholder even though the title-bar icon and
    /// the Explorer <c>.exe</c> icon (the <c>&lt;ApplicationIcon&gt;</c> PE resource) are correct. Setting an
    /// explicit AUMID once, BEFORE the process creates its first window, fixes the running app's taskbar icon and
    /// makes grouping/pinning stable.
    /// <para>Pure BCL P/Invoke (shell32), guarded by <see cref="OperatingSystem.IsWindows"/> and wrapped so a
    /// failure degrades to <c>false</c> rather than throwing into startup. A no-op returning <c>false</c> off
    /// Windows or on a null/empty id. The macOS Dock / Cmd-Tab counterpart is <see cref="ApplicationIcon"/>;
    /// Linux has no equivalent taskbar-identity call, so this is a no-op there too.</para>
    /// </summary>
    public static class WindowsAppId
    {
        /// <summary>
        /// Set the current process's explicit Windows AppUserModelID. Call ONCE at startup, before the first
        /// window is created, so Windows 10/11 keys the taskbar button to <paramref name="appId"/> and resolves
        /// the app icon for the running app. <paramref name="appId"/> is the app's identity string, e.g.
        /// <c>"APKiwi.Nullwake"</c> (a dotted <c>CompanyName.ProductName</c> is the convention). Returns
        /// <c>true</c> when the shell call succeeded, <c>false</c> on a non-Windows OS, on a null/empty id, or if
        /// the call failed. Never throws.
        /// </summary>
        public static bool TrySetProcessAppUserModelId(string? appId)
        {
            if (!OperatingSystem.IsWindows()) return false;
            if (string.IsNullOrEmpty(appId)) return false;
            try
            {
                return SetCurrentProcessExplicitAppUserModelID(appId) == 0; // HRESULT S_OK
            }
            catch
            {
                return false;
            }
        }

        // HRESULT SetCurrentProcessExplicitAppUserModelID(PCWSTR AppID); PreserveSig so we read the HRESULT
        // ourselves (S_OK == 0) instead of it surfacing as a thrown COMException.
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SetCurrentProcessExplicitAppUserModelID(
            [MarshalAs(UnmanagedType.LPWStr)] string appId);
    }
}
