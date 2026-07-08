using System;
using KhaozEngine.Platform;
using Xunit;

namespace KhaozEngine.Tests.Platform
{
    /// <summary>
    /// Headless coverage for <see cref="WindowsConsole"/>. The actual AttachConsole/stream-rewire is a Windows
    /// runtime side effect that needs a real WinExe launched from a terminal to observe, so it stays out; these
    /// cover the pure attach decision (<see cref="WindowsConsole.ShouldAttach"/>, exercised on every OS) and the
    /// guard behaviour: <see cref="WindowsConsole.HasConsole"/> reports a console off Windows, and
    /// <see cref="WindowsConsole.EnsureParentConsoleAttached"/> never throws and is a one-shot. Off Windows the
    /// whole attach path short-circuits to false, which is exercised whenever the suite runs on macOS/Linux.
    /// </summary>
    public sealed class WindowsConsoleTests
    {
        // isWindows, enable, hasConsole, outRedirected, errRedirected -> expected
        [Theory]
        // Attach: Windows, enabled, no console, and at least one live (non-redirected) stream to rewire.
        [InlineData(true, true, false, false, false, true)]   // clean terminal launch: rewire both
        [InlineData(true, true, false, true, false, true)]    // stdout piped: still attach to rewire stderr
        [InlineData(true, true, false, false, true, true)]    // stderr piped: still attach to rewire stdout
        // No attach.
        [InlineData(true, true, false, true, true, false)]    // both redirected (CI/full capture): leave it alone
        [InlineData(true, true, true, false, false, false)]   // console-subsystem exe already owns a console
        [InlineData(true, false, false, false, false, false)] // opt-out
        [InlineData(false, true, false, false, false, false)] // macOS/Linux: no WinExe/no-console problem
        public void ShouldAttach_DecidesCorrectly(
            bool isWindows, bool enable, bool hasConsole, bool outRedirected, bool errRedirected, bool expected)
        {
            Assert.Equal(
                expected,
                WindowsConsole.ShouldAttach(isWindows, enable, hasConsole, outRedirected, errRedirected));
        }

        [Fact]
        public void HasConsole_OffWindows_IsTrue_AndNeverThrows()
        {
            // Off Windows there is no Windows-subsystem/no-console case, so a console is always reported (so a
            // caller's no-console fallback never fires spuriously). On Windows the value depends on the test host,
            // so only assert it does not throw there.
            bool value = WindowsConsole.HasConsole;
            if (!OperatingSystem.IsWindows())
                Assert.True(value);
        }

        [Fact]
        public void EnsureParentConsoleAttached_NeverThrows_AndIsOneShot()
        {
            // First call: on macOS/Linux the platform guard returns false with no side effect; on a Windows test
            // host (which owns a console) the has-console guard returns false too. Either way it must not throw.
            var first = Record.Exception(() => WindowsConsole.EnsureParentConsoleAttached());
            Assert.Null(first);

            // Second call: the one-shot guard means it returns false without re-attempting, and never throws.
            Assert.False(WindowsConsole.EnsureParentConsoleAttached());
            Assert.False(WindowsConsole.EnsureParentConsoleAttached(enable: false));
        }
    }
}
