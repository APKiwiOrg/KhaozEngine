using System;
using System.IO;
using KhaozEngine.Diagnostics;
using KhaozEngine.Game;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    /// <summary>
    /// THE ARMING DECISION FOR THE LAST-CHANCE CRASH FILE, which used to live inline in a constructor that
    /// needs a window: neither the opt-out flag nor the collision with a head's own arming was reachable from a
    /// test, and both were wrong in the same direction (the flag had no cover at all, and arming was
    /// unconditional, so a consumer's earlier Install was silently replaced).
    /// <para>
    /// Serial with the other tests that touch the ambient crash-report state, because arming is process-global.
    /// </para>
    /// </summary>
    [Collection("CrashReportSerial")]
    public sealed class GameAppCrashReportTests : IDisposable
    {
        public void Dispose() => CrashReport.Uninstall();

        [Fact]
        public void Arms_by_default()
        {
            CrashReport.Uninstall();

            Assert.True(GameApp.TryArmCrashReport(GameAppOptions.For("t", 640, 480)));
            Assert.True(CrashReport.IsInstalled);
        }

        /// <summary>The opt-out, which had no test at all: the flag existed and nothing proved it did anything.</summary>
        [Fact]
        public void Suppress_flag_arms_nothing()
        {
            CrashReport.Uninstall();

            var opts = GameAppOptions.For("t", 640, 480);
            opts.SuppressCrashReportFile = true;

            Assert.False(GameApp.TryArmCrashReport(opts));
            Assert.False(CrashReport.IsInstalled);
        }

        /// <summary>
        /// FIRST WINS. A head that armed its own crash file before constructing its GameApp picked a directory,
        /// a label and a retention count deliberately. Install replaces rather than stacks, so arming
        /// unconditionally here pointed every report at the default location instead, which is not where that
        /// head was looking. The proof is that a crash written after the arming still lands in the directory
        /// the head chose.
        /// </summary>
        [Fact]
        public void Does_not_clobber_a_head_that_installed_its_own()
        {
            CrashReport.Uninstall();
            string dir = Path.Combine(Path.GetTempPath(), "khaoz-gameapp-crash-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                CrashReport.Install(new CrashReportOptions { Directory = dir, ProcessLabel = "chosen-by-the-head" });

                Assert.False(GameApp.TryArmCrashReport(GameAppOptions.For("t", 640, 480)));

                string? path = CrashReport.OnCrash("Unhandled exception", new InvalidOperationException("x"), null);
                Assert.NotNull(path);
                Assert.StartsWith("chosen-by-the-head-crash-", Path.GetFileName(path), StringComparison.Ordinal);
                Assert.Equal(dir, Path.GetDirectoryName(path));
            }
            finally
            {
                CrashReport.Uninstall();
                Directory.Delete(dir, true);
            }
        }
    }
}
