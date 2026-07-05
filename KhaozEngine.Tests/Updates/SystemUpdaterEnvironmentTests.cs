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
}
