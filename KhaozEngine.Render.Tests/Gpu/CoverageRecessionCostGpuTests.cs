using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("GpuTimingSensitive")]
public sealed class CoverageRecessionCostGpuTests(ITestOutputHelper output)
{
    [CoverageRecessionHardwareFact]
    [Trait("Category", "Diagnostic")]
    public void Real_body_size_carry_and_scene_overhead_for_1_and_32_actors()
    {
        long deadline = Stopwatch.GetTimestamp() + 15 * 60 * Stopwatch.Frequency;
        using var context = GpuDeviceContext.CreateHeadless();
        var device = context.GpuDevice;
        int observed = context.Backend == GpuBackendKind.MetalNative ? 9 : 32;
        string? requested = Environment.GetEnvironmentVariable("KE_COVERAGE_CARRY_MEASURED_FRAMES");
        Assert.True(requested is null || requested == observed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (context.Backend != GpuBackendKind.MetalNative)
        {
            Assert.Equal(GpuBackendKind.VulkanNative, context.Backend);
            Assert.Matches(@"^(NVIDIA\s+)?(Tesla\s+)?T4$", device.Capabilities.DeviceName);
        }
        var fixture = new CoverageRecessionBodySizeFixture(Extent("WIDTH", 18), Extent("HEIGHT", 16), Extent("LEVEL", 2));
        Assert.True(fixture.Topology.VertexCount >= 3424); Assert.True(fixture.Topology.PositionGroupCount >= 958);
        var rows = new List<CoverageRecessionCostMeasurement.Row>();
        foreach (int actors in new[] { 1, 32 })
            rows.AddRange(CoverageRecessionCostMeasurement.Measure(device, fixture, actors, observed, deadline));
        var deltas = new[] { 1, 32 }.Select(actors =>
        {
            var baseline = rows.Single(r => r.Actors == actors && r.Layout == "baseline-scene");
            var covered = rows.Single(r => r.Actors == actors && r.Layout == "covered-scene");
            return new
            {
                actors,
                addedCpuPreparationMicroseconds = CoverageRecessionCostMeasurement.Stats(covered.Samples.Zip(baseline.Samples,
                    (c, b) => c.CpuPreparationMicroseconds - b.CpuPreparationMicroseconds)),
                aggregateGpuOverheadPercent = (covered.GpuMicroseconds.Median / baseline.GpuMicroseconds.Median - 1) * 100,
            };
        }).ToArray();
        string json = JsonSerializer.Serialize(new
        {
            schema = "khaozengine.coverage-recession-cost/1",
            fixture = fixture.Extents,
            code = new
            {
                commit = CodeCommit(),
                informationalVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                renderAssemblySha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(Scene3D).Assembly.Location))),
            },
            device = new
            {
                backend = context.Backend.ToString(),
                device.Capabilities.DeviceName,
                identityBasis = "Reported adapter name and backend software classification. PCI IDs are unavailable on this seam.",
                device.Diagnostics.SoftwareAdapter
            },
            conditioningFramesPerLayout = CoverageRecessionCostMeasurement.ConditioningFrames,
            observedFramesPerRow = observed,
            batchFrames = CoverageRecessionCostMeasurement.BatchFrames,
            conditioning = "Adjacent three-frame batches, alternating layout order. All observations retained. One attempt, 15 minute deadline.",
            stateSchedule = "Frame modulo three equal to two is raw hole. Other even frames select row zero and odd frames select row one. The same sequence runs on every layout.",
            timingBoundary = "Carry-only outer timestamps include uniforms, five dispatches, candidate publication and bounds. Scene pairs include actual colour/depth/motion/cascade/point-shadow/full-outline/visible-outline drawing with identical pass settings. Final timestamp follows native End finalization.",
            exclusions = "Cold topology and table construction, shader compilation, resource growth, conditioning, evidence readback and batch drains. No construction or certification kernels. Synthetic geometry does not prove consumer frame rate.",
            candidateBytesPerVertex = 64,
            dispatchSchedule = CoverageRecessionCostMeasurement.DispatchSchedule,
            preciseMath = true,
            rows,
            deltas,
        });
        string directory = Environment.GetEnvironmentVariable("KE_COVERAGE_PROFILE_RESULTS") ?? Path.Combine(Path.GetTempPath(), "khaozengine-coverage-recession");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(directory, "coverage-recession-cost.json"), json + Environment.NewLine);
        output.WriteLine("COVERAGE_RECESSION_COST_JSON=" + json);
        Assert.All(rows, row => Assert.Equal(0, row.WarmManagedBytes.Max));
        var full = deltas.Single(d => d.actors == 32);
        Assert.True(full.addedCpuPreparationMicroseconds.Max <= 2000);
        Assert.True(full.aggregateGpuOverheadPercent <= 15);
    }

    static int Extent(string name, int fallback)
    {
        string? value = Environment.GetEnvironmentVariable("KE_COVERAGE_FIXTURE_" + name);
        return value is null ? fallback : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    static string CodeCommit()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "KhaozEngine.slnx"))) continue;
            var start = new ProcessStartInfo("git") { WorkingDirectory = directory.FullName, RedirectStandardOutput = true, UseShellExecute = false };
            start.ArgumentList.Add("rev-parse"); start.ArgumentList.Add("HEAD");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot read the workload code commit.");
            string sha = process.StandardOutput.ReadToEnd().Trim(); process.WaitForExit();
            Assert.Equal(0, process.ExitCode); Assert.Matches("^[a-f0-9]{40}$", sha);
            return sha;
        }
        throw new DirectoryNotFoundException("The cost unit needs its immutable workload checkout.");
    }
}
