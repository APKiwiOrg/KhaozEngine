using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// The <see cref="AppWindow.TrySetProcessAppUserModelId"/> static forwarder is reachable and guarded without a
    /// real window (it must run BEFORE any window exists). It delegates to
    /// <see cref="KhaozEngine.Platform.WindowsAppId"/>, so the null/empty guard returns <c>false</c> on every
    /// platform; the Windows success path (a process-global side effect) is verified on a real Windows run.
    /// </summary>
    public sealed class AppWindowAppUserModelIdTests
    {
        [Fact]
        public void NullId_ReturnsFalse() =>
            Assert.False(AppWindow.TrySetProcessAppUserModelId(null));

        [Fact]
        public void EmptyId_ReturnsFalse() =>
            Assert.False(AppWindow.TrySetProcessAppUserModelId(string.Empty));
    }
}
