using System;
using System.Linq;
using KhaozEngine.Platform;
using Xunit;

namespace KhaozEngine.Tests.Platform
{
    /// <summary>
    /// Coverage for the real <see cref="ProcessControl"/> that can be asserted deterministically on every
    /// OS: it reflects the running process's own identity, reports an unknown pid as already gone, and times
    /// out cleanly on a still-live one. The fire-and-forget <see cref="ProcessControl.StartDetached"/> is a
    /// thin <c>ProcessStartInfo</c> wrapper over the same idiom the browser-launch and updater relaunch use;
    /// exercising a real spawn needs an OS-specific child binary, so the relaunch orchestration that drives
    /// it is verified against a fake in <see cref="KhaozEngine.Tests.App.AppRelaunchTests"/> instead.
    /// </summary>
    public sealed class ProcessControlTests
    {
        [Fact]
        public void CurrentProcessId_MatchesEnvironment() =>
            Assert.Equal(Environment.ProcessId, ProcessControl.System.CurrentProcessId);

        [Fact]
        public void CurrentExecutablePath_MatchesEnvironment() =>
            Assert.Equal(Environment.ProcessPath, ProcessControl.System.CurrentExecutablePath);

        [Fact]
        public void CurrentCommandLineArguments_ExcludesTheExecutable() =>
            Assert.Equal(Environment.GetCommandLineArgs().Skip(1), ProcessControl.System.CurrentCommandLineArguments);

        [Fact]
        public void WaitForProcessExit_UnknownPid_ReturnsTrueImmediately() =>
            // No process maps to int.MaxValue, so there is nothing to wait for: report it as gone.
            Assert.True(ProcessControl.System.WaitForProcessExit(int.MaxValue, timeoutMilliseconds: 0));

        [Fact]
        public void WaitForProcessExit_LiveProcess_TimesOut_ReturnsFalse() =>
            // Wait on our own still-running process with a short timeout: it cannot exit, so the wait times out.
            Assert.False(ProcessControl.System.WaitForProcessExit(Environment.ProcessId, timeoutMilliseconds: 50));
    }
}
