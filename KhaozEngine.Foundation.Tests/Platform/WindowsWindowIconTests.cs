using KhaozEngine.Platform;
using Xunit;

namespace KhaozEngine.Tests.Platform
{
    /// <summary>
    /// Headless coverage for <see cref="WindowsWindowIcon.TrySyncTaskbarIconFromWindow"/>'s guard behaviour. The
    /// success path copies a live window's WM_SETICON handle onto its class icon - a real Win32 window side effect
    /// that only a windowed Windows run can observe (the taskbar-button fix is verified there), so it stays out.
    /// These assert only the cases that return <c>false</c> cleanly on every platform without touching a window: a
    /// zero handle (rejected before any P/Invoke), and - off Windows - any handle (the platform guard
    /// short-circuits). None throw.
    /// </summary>
    public sealed class WindowsWindowIconTests
    {
        [Fact]
        public void ZeroHandle_ReturnsFalse() =>
            Assert.False(WindowsWindowIcon.TrySyncTaskbarIconFromWindow(0));

        [Fact]
        public void OffWindows_NonZeroHandle_ReturnsFalse_AndDoesNotThrow()
        {
            // Off Windows the platform guard short-circuits to false for any handle, never calling user32 or
            // dereferencing the handle. On Windows this line is skipped so the suite never pokes a real HWND.
            if (!System.OperatingSystem.IsWindows())
                Assert.False(WindowsWindowIcon.TrySyncTaskbarIconFromWindow(0x1234));
        }
    }
}
