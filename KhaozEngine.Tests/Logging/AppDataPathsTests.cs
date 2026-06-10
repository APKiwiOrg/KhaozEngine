using System;
using System.IO;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Logging;

public class AppDataPathsTests
{
    [Fact]
    public void ResolveReturnsPathEndingInAppFolderName()
    {
        string app = "KhaozEngineTest_" + Guid.NewGuid().ToString("N");
        try
        {
            string dir = AppDataPaths.Resolve(app);
            Assert.EndsWith(app, dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        finally { TryDelete(app); }
    }

    [Fact]
    public void ResolveCreatesTheDirectory()
    {
        string app = "KhaozEngineTest_" + Guid.NewGuid().ToString("N");
        try
        {
            string dir = AppDataPaths.Resolve(app);
            Assert.True(Directory.Exists(dir));
        }
        finally { TryDelete(app); }
    }

    [Fact]
    public void ResolveIsCachedPerName()
    {
        string app = "KhaozEngineTest_" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Equal(AppDataPaths.Resolve(app), AppDataPaths.Resolve(app));
        }
        finally { TryDelete(app); }
    }

    [Fact]
    public void CombineJoinsUnderTheBaseDirectory()
    {
        string app = "KhaozEngineTest_" + Guid.NewGuid().ToString("N");
        try
        {
            string baseDir = AppDataPaths.Resolve(app);
            string logPath = AppDataPaths.Combine(app, "game.log");
            Assert.Equal(Path.Combine(baseDir, "game.log"), logPath);
        }
        finally { TryDelete(app); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveThrowsOnBlankAppFolderName(string? appFolderName)
    {
        Assert.Throws<ArgumentException>(() => AppDataPaths.Resolve(appFolderName!));
    }

    private static void TryDelete(string app)
    {
        try
        {
            string dir = AppDataPaths.Resolve(app);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { /* best-effort cleanup */ }
    }
}
