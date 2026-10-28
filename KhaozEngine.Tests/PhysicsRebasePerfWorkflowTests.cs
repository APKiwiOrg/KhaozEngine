using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class PhysicsRebasePerfWorkflowTests
{
    [Fact]
    public void PerfLane_IsScheduledAndManualOnHostedMain_OutsideMergeGates()
    {
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!,
            ".github/workflows/physics-rebase-perf.yml")), "The hosted rebase performance workflow must exist.");
        string workflow = Read(".github/workflows/physics-rebase-perf.yml");
        Assert.Contains("workflow_dispatch:", workflow);
        Assert.Contains("schedule:", workflow);
        Assert.Contains("cron:", workflow);
        Assert.Contains("runs-on: ubuntu-24.04", workflow);
        Assert.Contains("ref: main", workflow);
        Assert.DoesNotContain("self-hosted", workflow);
        Assert.DoesNotContain("pull_request:", workflow);
        Assert.DoesNotContain("push:", workflow);
        Assert.DoesNotContain("continue-on-error", workflow);
        Assert.Contains("tools/Physics.Rebase.Perf.Tests/Physics.Rebase.Perf.Tests.csproj", workflow);
        Assert.Contains("--filter FullyQualifiedName=KhaozEngine.Tests.Physics.PhysicsRebasePerfTests.Rebase_StaysWithinThreeSteps", workflow);
        Assert.Contains("GITHUB_STEP_SUMMARY", workflow);
        Assert.Contains("if: always()", workflow);
        Assert.DoesNotContain("physics-rebase-perf", Read(".github/workflows/ci.yml"));
        Assert.DoesNotContain("Physics.Rebase.Perf.Tests", Read("KhaozEngine.slnx"));
    }

    static string SourcePath([CallerFilePath] string source = "") => source;

    static string Read(string relative, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(source))!, relative))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
}
