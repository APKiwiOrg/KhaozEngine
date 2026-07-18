using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Updates;

/// <summary>
/// Headless-testable pieces of the real <see cref="SystemUpdaterEnvironment"/>. The process/relaunch and
/// file IO are exercised on a real box; here we pin the NTSTATUS classifier that decides whether a fast
/// relaunch exit is the Windows AV/image startup race (worth retrying) or a genuine run (leave it alone).
/// </summary>
public sealed class SystemUpdaterEnvironmentTests
{
    [Theory]
    [InlineData(unchecked((int)0xC0000142))] // STATUS_DLL_INIT_FAILED - "unable to start correctly (0xc0000142)"
    [InlineData(unchecked((int)0xC0000409))] // STATUS_STACK_BUFFER_OVERRUN - the ucrtbase fast-fail from the report
    [InlineData(unchecked((int)0xC0000005))] // STATUS_ACCESS_VIOLATION
    [InlineData(unchecked((int)0xC0000135))] // STATUS_DLL_NOT_FOUND
    [InlineData(unchecked((int)0xC0000139))] // STATUS_ENTRYPOINT_NOT_FOUND
    public void IsStartupFailureCode_TrueForImageLoadNtStatuses(int exitCode)
    {
        Assert.True(SystemUpdaterEnvironment.IsStartupFailureCode(exitCode));
    }

    [Theory]
    [InlineData(0)]                              // clean exit - the game ran and closed
    [InlineData(1)]                              // ordinary non-zero exit
    [InlineData(unchecked((int)0xE0434352))]     // CLR unhandled managed exception - a real crash, not the AV race
    public void IsStartupFailureCode_FalseForNormalExitsAndManagedCrashes(int exitCode)
    {
        Assert.False(SystemUpdaterEnvironment.IsStartupFailureCode(exitCode));
    }

    [Fact]
    public void EnclosingAppBundle_ReturnsBundlePath_ForExeInsideBundle()
    {
        Assert.Equal(
            "/Applications/Game.app",
            SystemUpdaterEnvironment.EnclosingAppBundle("/Applications/Game.app/Contents/MacOS/Game"));
    }

    [Fact]
    public void EnclosingAppBundle_ReturnsNull_ForBareExecutable()
    {
        Assert.Null(SystemUpdaterEnvironment.EnclosingAppBundle("/opt/game/Game"));
    }

    // ---- Protected-root detection: the writability gate that decides whether the applier must elevate ----
    // A per-machine install under Program Files / Windows can pass a naive "create a new file at the root"
    // probe while the operations the apply actually performs (overwrite the existing installed binaries,
    // clear an admin-owned rollback dir) fail with Access Denied. IsUnderProtectedRoot is the deterministic,
    // OS-independent decision the real environment uses instead, so it is pinned here with Windows-style paths.
    private static readonly string[] WinProtectedRoots =
    {
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\Windows",
    };

    [Theory]
    [InlineData(@"C:\Program Files\Nullwake")]              // the reported install
    [InlineData(@"C:\Program Files\Nullwake\sub\deep")]     // nested under a protected root
    [InlineData(@"C:\Program Files")]                       // exact match of a root
    [InlineData(@"c:\program files\nullwake")]              // Windows paths are case-insensitive
    [InlineData(@"C:\Program Files (x86)\Nullwake")]        // the 32-bit hive
    [InlineData(@"C:\Windows\System32\config")]             // the system root
    public void IsUnderProtectedRoot_TrueForProgramFilesAndWindows(string installDir)
    {
        Assert.True(SystemUpdaterEnvironment.IsUnderProtectedRoot(installDir, WinProtectedRoots));
    }

    [Theory]
    [InlineData(@"C:\Users\drift\AppData\Roaming\APKiwi\Nullwake")] // a per-user install (writable, no elevation)
    [InlineData(@"D:\Games\Nullwake")]                             // a second-drive install
    [InlineData(@"C:\Program FilesX\Nullwake")]                    // prefix collision, NOT actually under the root
    [InlineData("")]                                               // no install dir
    public void IsUnderProtectedRoot_FalseForUserAndNonProtectedLocations(string installDir)
    {
        Assert.False(SystemUpdaterEnvironment.IsUnderProtectedRoot(installDir, WinProtectedRoots));
    }

    [Fact]
    public void IsUnderProtectedRoot_ToleratesTrailingSeparatorsAndMixedSlashes()
    {
        Assert.True(SystemUpdaterEnvironment.IsUnderProtectedRoot(
            @"C:\Program Files\Nullwake\", new[] { @"C:/Program Files/" }));
    }

    [Fact]
    public void IsUnderProtectedRoot_SkipsEmptyRootsAndFalseWhenNoneMatch()
    {
        Assert.False(SystemUpdaterEnvironment.IsUnderProtectedRoot(
            @"C:\Program Files\Nullwake", new[] { "", "   " }));
    }
}
