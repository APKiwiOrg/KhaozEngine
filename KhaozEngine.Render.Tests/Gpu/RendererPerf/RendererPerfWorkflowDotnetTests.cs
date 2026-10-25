using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfWorkflowDotnetTests
{
    [Fact]
    public void Renderer_job_cannot_install_a_floating_sdk_or_runtime()
    {
        string job = RendererJob();
        Assert.DoesNotContain("setup-dotnet", job, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet-install", job, StringComparison.Ordinal);
        Assert.DoesNotContain("--channel", job, StringComparison.Ordinal);
        Assert.DoesNotContain("-Channel", job, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.x", job, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Linux", "linux-x64.tar.gz", "1904a98dda2db34ba78ba3c4fb1b224455329af91c341d83547843c1839e112b04ae699ee92fa2449a8f62249196a938723078c0c60b9f65c88539d5687729c2")]
    [InlineData("Windows", "win-x64.zip", "ae4fd528cb7e9f6af6aaf7de36715a69f761f183744f1bb3025d19f5a92447090ec561410e46266c1d10a63781ed6f3de771760bc76c876d87daabc367b898cd")]
    public void Renderer_sdk_archives_have_exact_versions_and_published_hashes(string os, string archive, string hash)
    {
        string step = Step("Install isolated renderer SDK (" + os + ")");
        Assert.Contains("if: runner.os == '" + os + "'", step, StringComparison.Ordinal);
        Assert.Equal("https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.402/dotnet-sdk-10.0.402-" + archive, Setting(step, "DOTNET_URL"));
        Assert.Equal(hash, Setting(step, "DOTNET_SHA512"));
        string run = Run(step);
        if (os == "Linux")
        {
            Assert.Contains("set -euo pipefail", run, StringComparison.Ordinal);
            Assert.Contains("mkdir \"$dir\"", run, StringComparison.Ordinal);
            Assert.Contains("curl -fsSL --retry 3 -o \"$archive\" \"$DOTNET_URL\"", run, StringComparison.Ordinal);
            Assert.Contains("echo \"$DOTNET_SHA512  $archive\" | sha512sum -c -", run, StringComparison.Ordinal);
            Assert.True(run.IndexOf("sha512sum", StringComparison.Ordinal) < run.IndexOf("tar -xzf", StringComparison.Ordinal));
        }
        else
        {
            Assert.Contains("$ErrorActionPreference = 'Stop'", run, StringComparison.Ordinal);
            Assert.Contains("if (Test-Path -LiteralPath $dir) { throw", run, StringComparison.Ordinal);
            Assert.Contains("Invoke-WebRequest -Uri $env:DOTNET_URL -OutFile $archive -UseBasicParsing", run, StringComparison.Ordinal);
            Assert.Contains("Get-FileHash -Algorithm SHA512 $archive", run, StringComparison.Ordinal);
            Assert.Contains("if ($actual -cne $env:DOTNET_SHA512) { throw", run, StringComparison.Ordinal);
            Assert.True(run.IndexOf("$actual -cne", StringComparison.Ordinal) < run.IndexOf("Expand-Archive", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("Linux")]
    [InlineData("Windows")]
    public void Renderer_sdk_root_is_prepended_and_shared_by_all_launches(string os)
    {
        string run = Run(Step("Install isolated renderer SDK (" + os + ")"));
        if (os == "Linux")
        {
            Assert.Contains("dir=\"$RUNNER_TEMP/renderer-dotnet\"", run, StringComparison.Ordinal);
            Assert.Contains("echo \"$dir\" >> \"$GITHUB_PATH\"", run, StringComparison.Ordinal);
            Assert.Contains("""
                {
                  echo "DOTNET_ROOT=$dir"
                  echo "DOTNET_ROOT_X64=$dir"
                  echo "DOTNET_HOST_PATH=$dir/dotnet"
                } >> "$GITHUB_ENV"
                """, run, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("$dir = Join-Path $env:RUNNER_TEMP 'renderer-dotnet'", run, StringComparison.Ordinal);
            Assert.Contains("[IO.File]::AppendAllText($env:GITHUB_PATH, $dir + [Environment]::NewLine)", run, StringComparison.Ordinal);
            foreach (string setting in new[] { "DOTNET_ROOT=$dir", "DOTNET_ROOT_X64=$dir", "DOTNET_HOST_PATH=$hostPath" })
                Assert.Contains("[IO.File]::AppendAllText($env:GITHUB_ENV, \"" + setting + "\" + [Environment]::NewLine)", run, StringComparison.Ordinal);
            Assert.Contains("$hostPath = Join-Path $dir 'dotnet.exe'", run, StringComparison.Ordinal);
        }
        string job = RendererJob();
        int check = job.IndexOf("name: Check exact renderer SDK and runtime", StringComparison.Ordinal);
        Assert.True(check > job.IndexOf("name: Install isolated renderer SDK (Windows)", StringComparison.Ordinal));
        Assert.True(check > job.IndexOf("name: Install isolated renderer SDK (Linux)", StringComparison.Ordinal));
        Assert.True(check < job.IndexOf("& dotnet build", StringComparison.Ordinal));
        Assert.True(check < job.IndexOf("& dotnet test", StringComparison.Ordinal));
        Assert.Contains("renderer-perf-trusted/scripts/renderer-perf-workflow.ps1') -Mode Verify", job, StringComparison.Ordinal);
        Assert.DoesNotContain("    if:", Step("Check exact renderer SDK and runtime"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.0.402", "Microsoft.NETCore.App 10.0.13 [/isolated/shared/Microsoft.NETCore.App]", 0, 0, true)]
    [InlineData("10.0.402", "Microsoft.AspNetCore.App 10.0.13 [/isolated/shared/Microsoft.AspNetCore.App]|Microsoft.NETCore.App 10.0.13 [/isolated/shared/Microsoft.NETCore.App]", 0, 0, true)]
    [InlineData("10.0.403", "Microsoft.NETCore.App 10.0.13 [/isolated/shared/Microsoft.NETCore.App]", 0, 0, false)]
    [InlineData("10.0.402", "Microsoft.NETCore.App 10.0.14 [/isolated/shared/Microsoft.NETCore.App]", 0, 0, false)]
    [InlineData("10.0.402", "Microsoft.NETCore.App 10.0.13 [/isolated/shared/Microsoft.NETCore.App]|Microsoft.NETCore.App 10.0.14 [/isolated/shared/Microsoft.NETCore.App]", 0, 0, false)]
    [InlineData("10.0.402", "Microsoft.AspNetCore.App 10.0.13 [/isolated/shared/Microsoft.AspNetCore.App]", 0, 0, false)]
    [InlineData("10.0.402", "Microsoft.NETCore.App 10.0.13 [/isolated/shared/Microsoft.NETCore.App]", 1, 0, false)]
    [InlineData("10.0.402", "Microsoft.NETCore.App 10.0.13 [/isolated/shared/Microsoft.NETCore.App]", 0, 1, false)]
    public async Task Renderer_setup_refuses_selected_sdk_or_runtime_drift(string sdk, string runtimes, int sdkExit, int runtimeExit, bool accepted)
    {
        string check = Run(Step("Check exact renderer SDK and runtime"));
        string stub = """
            function dotnet {
                if ($args[0] -ceq '--version') {
                    $global:LASTEXITCODE = [int]$env:TEST_SDK_EXIT
                    $env:TEST_SDK
                } elseif ($args[0] -ceq '--list-runtimes') {
                    $global:LASTEXITCODE = [int]$env:TEST_RUNTIME_EXIT
                    $env:TEST_RUNTIMES -split '\|'
                } else { throw 'Unexpected dotnet command' }
            }
            """;
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoProfile", "-Command", stub + "\n" + check }) start.ArgumentList.Add(arg);
        start.Environment["TEST_SDK"] = sdk;
        start.Environment["TEST_RUNTIMES"] = runtimes;
        start.Environment["TEST_SDK_EXIT"] = sdkExit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["TEST_RUNTIME_EXIT"] = runtimeExit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment.Remove("KE_GPU_TESTS");
        start.Environment.Remove("KE_RENDERER_PERF_UNIT");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True((process.ExitCode == 0) == accepted, await stdout + await stderr);
    }

    static string Step(string name) => RendererJob().Split("      - ")
        .Single(step => step.StartsWith("name: " + name + "\n", StringComparison.Ordinal));

    static string Run(string step) => string.Join('\n', step.Split("        run: |\n", 2)[1]
        .TrimEnd().Split('\n').Select(line => line.Length >= 10 ? line[10..] : line));

    static string? Setting(string block, string name) => block.Split('\n')
        .Select(line => line.Trim().Split(':', 2))
        .SingleOrDefault(pair => pair.Length == 2 && pair[0] == name)?[1].Trim().Trim('\'', '"');

    static string RendererJob([CallerFilePath] string source = "")
    {
        string repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../.."));
        return File.ReadAllText(Path.Combine(repo, ".github/workflows/gpu-hosted-bench.yml")).Split("  renderer-bench:\n", 2)[1];
    }
}
