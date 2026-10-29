using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("NativeDeviceLifecycle")]
public sealed class RendererPerfGpuClockWorkflowTests
{
    [Fact]
    public void Paid_clock_setup_and_cleanup_surround_measurement_on_both_platforms()
    {
        string job = RendererPerfWorkflowText.Read().Job("renderer-bench");
        string Step(string name) => job.Split("      - ").Single(s => s.StartsWith("name: " + name + "\n", StringComparison.Ordinal));
        string setup = Step("Lock paid renderer GPU graphics clock");
        string cleanup = Step("Reset paid renderer GPU graphics clock");
        Assert.Contains("matrix.executionClass == 'performance'", setup, StringComparison.Ordinal);
        Assert.Contains("matrix.executionClass == 'performance'", cleanup, StringComparison.Ordinal);
        Assert.Contains("always()", cleanup, StringComparison.Ordinal);
        Assert.Contains("${{ matrix.lockedGraphicsClockMHz }}", setup, StringComparison.Ordinal);
        Assert.Contains("renderer-perf-trusted/scripts/renderer-perf-gpu-clock.ps1", setup, StringComparison.Ordinal);
        Assert.Contains("-Mode Lock", setup, StringComparison.Ordinal);
        Assert.Contains("-Mode Reset", cleanup, StringComparison.Ordinal);
        Assert.True(job.IndexOf("GRID driver (Windows)", StringComparison.Ordinal) < job.IndexOf(setup, StringComparison.Ordinal));
        Assert.True(job.IndexOf("GPU, driver and Vulkan devices (Linux)", StringComparison.Ordinal) < job.IndexOf(setup, StringComparison.Ordinal));
        Assert.True(job.IndexOf(setup, StringComparison.Ordinal) < job.IndexOf("name: Build isolated renderer", StringComparison.Ordinal));
        Assert.True(job.IndexOf(cleanup, StringComparison.Ordinal) < job.IndexOf("name: Upload renderer evidence", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Linux", "ok", true)]
    [InlineData("Windows", "ok", true)]
    [InlineData("Linux", "exit", false)]
    [InlineData("Windows", "unsupported", false)]
    [InlineData("Linux", "range", false)]
    [InlineData("Windows", "observed", true)]
    public void Clock_setup_retains_driver_output_and_refuses_failed_or_changed_ranges(string platform, string fault, bool accepted)
    {
        using var fixture = Fixture(platform, fault);
        var result = fixture.Execute("pwsh", ["-NoProfile", "-File", Path.Combine(fixture.Directory, "clock-test.ps1"), "Lock"]);
        Assert.Equal(accepted, result.Code == 0);
        string commands = File.ReadAllText(Path.Combine(fixture.Reports, "commands.txt"));
        Assert.Contains("--lock-gpu-clocks=1095,1095", commands, StringComparison.Ordinal);
        Assert.Equal(platform == "Linux", commands.Contains("sudo ", StringComparison.Ordinal));
        Assert.Equal(platform == "Linux", commands.Contains("--persistence-mode=1", StringComparison.Ordinal));
        string log = File.ReadAllText(Path.Combine(fixture.Reports, "gpu-clock-lock.log"));
        Assert.Contains(fault == "unsupported" ? "Not Supported" : "gpuClkMin", log, StringComparison.Ordinal);
        if (accepted)
        {
            string evidence = File.ReadAllText(Path.Combine(fixture.Reports, "gpu-clock-lock.json"));
            var decoded = System.Text.Json.Nodes.JsonNode.Parse(evidence)!;
            Assert.Equal("applied", decoded["state"]!.GetValue<string>());
            Assert.Equal(fault == "observed" ? 1080 : 1095, decoded["observedGraphicsClockMHz"]!.GetValue<int>());
            Assert.Contains("1095", evidence, StringComparison.Ordinal);
            Assert.Contains("gpuClkMax", log, StringComparison.Ordinal);
        }
        else Assert.Contains("GpuClockNotLocked", result.Output, StringComparison.Ordinal);
        var reset = fixture.Execute("pwsh", ["-NoProfile", "-File", Path.Combine(fixture.Directory, "clock-test.ps1"), "Reset"]);
        Assert.True(reset.Code == 0, reset.Output);
        Assert.Contains("--reset-gpu-clocks", File.ReadAllText(Path.Combine(fixture.Reports, "commands.txt")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1200")]
    [InlineData("1215")]
    [InlineData("")]
    public void Clock_setup_refuses_any_matrix_clock_except_the_paid_lock_before_nvidia_smi(string clock)
    {
        using var fixture = Fixture("Linux", "ok");
        fixture.Environment["RENDERER_PERF_LOCKED_GRAPHICS_CLOCK_MHZ"] = clock;
        var result = fixture.Execute("pwsh", ["-NoProfile", "-File", Path.Combine(fixture.Directory, "clock-test.ps1"), "Lock"]);
        Assert.NotEqual(0, result.Code);
        Assert.Contains("GpuClockNotLocked", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.Reports, "commands.txt")));
    }

    [Fact]
    public void Software_rehearsal_never_invokes_nvidia_smi()
    {
        using var fixture = Fixture("Linux", "ok");
        fixture.Environment["RENDERER_PERF_EXECUTION_CLASS"] = "rehearsal";
        var result = fixture.Execute("pwsh", ["-NoProfile", "-File", Path.Combine(fixture.Directory, "clock-test.ps1"), "Lock"]);
        Assert.NotEqual(0, result.Code);
        Assert.False(File.Exists(Path.Combine(fixture.Reports, "commands.txt")));
    }

    static RendererPerfWorkflowContractTests.WorkflowFixture Fixture(string platform, string fault, [CallerFilePath] string source = "")
    {
        var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        fixture.Environment["RUNNER_OS"] = platform;
        fixture.Environment["RENDERER_PERF_EXECUTION_CLASS"] = "performance";
        fixture.Environment["RENDERER_PERF_LOCKED_GRAPHICS_CLOCK_MHZ"] = "1095";
        fixture.Environment["CLOCK_FAULT"] = fault;
        fixture.Environment["CLOCK_SCRIPT"] = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../../scripts/renderer-perf-gpu-clock.ps1"));
        File.WriteAllText(Path.Combine(fixture.Directory, "clock-test.ps1"), """
            param([string]$Mode)
            function global:sudo {
                [IO.File]::AppendAllText((Join-Path $env:KE_RENDERER_PERF_REPORTS 'commands.txt'), "sudo " + ($args -join ' ') + "`n")
                $command = $args[0]
                $rest = @($args | Select-Object -Skip 1)
                & $command @rest
            }
            function global:nvidia-smi {
                [IO.File]::AppendAllText((Join-Path $env:KE_RENDERER_PERF_REPORTS 'commands.txt'), ($args -join ' ') + "`n")
                $global:LASTEXITCODE = 0
                if ($args[0] -like '--lock-gpu-clocks=*') {
                    if ($env:CLOCK_FAULT -eq 'exit') { $global:LASTEXITCODE = 9 }
                    if ($env:CLOCK_FAULT -eq 'unsupported') { 'Setting locked GPU clocks is Not Supported'; return }
                    if ($env:CLOCK_FAULT -eq 'range') { 'GPU clocks set to "(gpuClkMin 1110, gpuClkMax 1110)" for GPU 00000000:00:00.0'; return }
                    'GPU clocks set to "(gpuClkMin 1095, gpuClkMax 1095)" for GPU 00000000:00:00.0'
                } elseif ($args[0] -like '--query-gpu=*') {
                    if ($env:CLOCK_FAULT -eq 'observed') { '1080, 1590' } else { '1095, 1590' }
                } else { 'NVIDIA driver full output retained' }
            }
            & $env:CLOCK_SCRIPT -Mode $Mode
            """);
        return fixture;
    }
}
