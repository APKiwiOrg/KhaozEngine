using KhaozEngine.Platform;
using Xunit;

namespace KhaozEngine.Tests.Platform
{
    /// <summary>
    /// Headless coverage for <see cref="WindowsAppId.TrySetProcessAppUserModelId"/>'s guard behaviour. The success
    /// path actually mutates the running process's Windows AppUserModelID (and only on Windows), a side effect
    /// that needs a real Windows run to observe, so it stays out; these assert only the cases that return
    /// <c>false</c> cleanly on every platform: a null or empty id (rejected before any shell call). None throw.
    /// Off Windows every input returns <c>false</c> (guarded), which is exercised whenever the suite runs on
    /// macOS/Linux.
    /// </summary>
    public sealed class WindowsAppIdTests
    {
        [Fact]
        public void NullId_ReturnsFalse() =>
            Assert.False(WindowsAppId.TrySetProcessAppUserModelId(null));

        [Fact]
        public void EmptyId_ReturnsFalse() =>
            Assert.False(WindowsAppId.TrySetProcessAppUserModelId(string.Empty));

        [Fact]
        public void OffWindows_AnyId_ReturnsFalse_AndDoesNotThrow()
        {
            // On a non-Windows OS the platform guard short-circuits to false for a well-formed id, never throwing.
            // On Windows this line is skipped so the suite never mutates the test host's process identity.
            if (!System.OperatingSystem.IsWindows())
                Assert.False(WindowsAppId.TrySetProcessAppUserModelId("APKiwi.Nullwake"));
        }
    }
}
