using KhaozEngine.Diagnostics;
using KhaozEngine.Showcase;
using Xunit;

namespace KhaozEngine.Tests.Showcase
{
    /// <summary>The showcase is the engine's windowed testbed, and until #607 it configured no logging at all,
    /// so a one-off managed exception went to whatever terminal launched it and was gone. This pins the boot
    /// options the head hands to SessionLog. It only reads them, so it opens no window, writes no file and
    /// touches no process-global logging state.</summary>
    public class ShowcaseBootLogTests
    {
        [Fact]
        public void TheBootOptionsInstallTheCrashHandler()
        {
            SessionLogOptions options = ShowcaseApp.BootLogOptions();

            // The point of the item: an unhandled managed exception has to reach a file that outlives the run.
            Assert.True(options.InstallCrashHandler);
            Assert.False(string.IsNullOrWhiteSpace(options.Directory));
            Assert.Equal("KhaozEngine.Showcase", options.ProcessLabel);
            // Launched from a terminal as often as not, so the console sink stays on beside the file.
            Assert.True(options.Console);
        }
    }
}
