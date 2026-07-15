using KhaozEngine.Game;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    // The GameAppOptions knobs that govern the single-instance guard (GameApp.ResolveSingleInstanceKey is the
    // pure fallback decision, headless-testable without standing up a window - mirrors GameAppDiagnosticsTests
    // / GameAppJobSchedulerTests). The guard itself (KhaozEngine.App.SingleInstanceGuard) has its own coverage
    // in KhaozEngine.Tests.App.SingleInstanceGuardTests; this file only covers GameApp's option resolution.
    public sealed class GameAppSingleInstanceTests
    {
        [Fact]
        public void ResolveKey_ExplicitSingleInstanceId_Wins()
        {
            var opts = GameAppOptions.For("t", 640, 480);
            opts.SingleInstanceId = "explicit-key";
            opts.AppUserModelId = "Company.App";

            Assert.Equal("explicit-key", GameApp.ResolveSingleInstanceKey(opts));
        }

        [Fact]
        public void ResolveKey_NoSingleInstanceId_FallsBackToAppUserModelId()
        {
            var opts = GameAppOptions.For("t", 640, 480);
            opts.AppUserModelId = "Company.App";

            Assert.Equal("Company.App", GameApp.ResolveSingleInstanceKey(opts));
        }

        [Fact]
        public void ResolveKey_NeitherSet_ReturnsNull()
        {
            var opts = GameAppOptions.For("t", 640, 480);
            Assert.Null(GameApp.ResolveSingleInstanceKey(opts));
        }

        [Fact]
        public void ResolveKey_EmptySingleInstanceId_FallsBackToAppUserModelId()
        {
            var opts = GameAppOptions.For("t", 640, 480);
            opts.SingleInstanceId = string.Empty;
            opts.AppUserModelId = "Company.App";

            Assert.Equal("Company.App", GameApp.ResolveSingleInstanceKey(opts));
        }

        [Fact]
        public void SingleInstance_DefaultsToFalse()
        {
            // Both GameAppOptions.For and a raw default(GameAppOptions) must keep multi-instance the historic
            // default - opting in is explicit.
            Assert.False(GameAppOptions.For("t", 640, 480).SingleInstance);
            Assert.False(default(GameAppOptions).SingleInstance);
        }
    }
}
