using System;
using KhaozEngine.ShaderBuild;
using Xunit;

namespace KhaozEngine.Tests.ShaderBuild;

public sealed class ToolProcessTests
{
    [Fact]
    public void A_tool_that_outlives_its_bound_is_stopped_and_refused()
    {
        var (executable, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", new[] { "/c", "ping -n 600 127.0.0.1 > nul" })
            : ("/bin/sh", new[] { "-c", "sleep 600" });

        var refusal = Assert.Throws<ShaderBuildException>(
            () => ToolProcess.Run(executable, arguments, timeout: TimeSpan.FromMilliseconds(100)));

        Assert.Equal(ShaderBuildFailure.ToolTimedOut, refusal.Failure);
    }
}
