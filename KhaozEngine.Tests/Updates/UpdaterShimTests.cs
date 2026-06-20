using System.IO;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

public sealed class UpdaterShimTests
{
    [Fact]
    public void ResolveLogPath_places_log_next_to_apply_config()
    {
        string[] args = { "--apply", Path.Combine("some", "dir", "apply-update.json") };
        Assert.Equal(Path.Combine("some", "dir", "updater.log"), UpdaterShim.ResolveLogPath(args));
    }

    [Fact]
    public void ResolveLogPath_falls_back_to_current_dir_when_no_path()
    {
        Assert.Equal(Path.Combine(".", "updater.log"), UpdaterShim.ResolveLogPath(new[] { "--apply" }));
    }

    [Fact]
    public void ResolveLogPath_handles_bare_filename()
    {
        Assert.Equal(Path.Combine(".", "updater.log"), UpdaterShim.ResolveLogPath(new[] { "--apply", "apply.json" }));
    }
}
