using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using KhaozEngine.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class CoverageCarryProfileGpuTests(ITestOutputHelper output)
{
    [CoverageDispatchProfileFact]
    public void F0_complete_carry_and_ordinary_skinning_floor_for_1_and_32_actors()
    {
        using var context = GpuDeviceContext.CreateHeadless();
        int observed = Environment.GetEnvironmentVariable("KE_COVERAGE_CARRY_MEASURED_FRAMES") switch
        {
            null => context.Backend == GpuBackendKind.MetalNative ? 9 : 32,
            "9" => 9,
            "32" => 32,
            _ => throw new InvalidOperationException("Carry diagnostic accepts 9 or 32 observed frames.")
        };
        if (context.Backend == GpuBackendKind.MetalNative) Assert.Equal(9, observed);
        var device = context.GpuDevice;
        var fixture = new CoverageCarryFixture();
        object[] gatherWork = CoverageCarryGatherWork.Describe(fixture);
        var rows = new List<object>();
        foreach (int count in new[] { 1, 32 })
        {
            using (var carry = new CoverageCarryDevice(device, fixture, count))
            {
                rows.AddRange(CoverageCarryMeasurement.Measure(device, carry, observed, ordinary: false));
                rows.AddRange(CoverageCarryMeasurement.Measure(device, carry, observed, ordinary: true));
            }
            using var native = new CoverageCarryDevice(device, fixture, count, nativeFloat: true);
            rows.AddRange(CoverageCarryMeasurement.Measure(device, native, observed, ordinary: false, nativeFloat: true));
        }
        string json = JsonSerializer.Serialize(new
        {
            schema = "khaozengine.coverage-carry-profile/3",
            label = CoverageCarryReference.Label,
            diagnosticOnly = true,
            warmupFrames = CoverageCarryMeasurement.WarmupFrames,
            conditioning = "96 conditioning frames per layout, replayed in adjacent three-frame batches with alternating layout order. No ordinary pass or offline reference work separates paired layouts. Every observed sample is retained.",
            errors = "Per actor maximum Euclidean XYZ error and error/reference-length ratio over the final surface of each row. The separate correctness fact covers both poses, weights and command frames. Zero reference with nonzero error has undefined relative error and is counted separately. Tangent W is separate.",
            nativeFloatRegressionEnvelope = new { positionRelative = 32 * CoverageCarryRounding.FloatEpsilon,
                normalRelative = 256 * CoverageCarryRounding.FloatEpsilon, tangentRelative = 128 * CoverageCarryRounding.FloatEpsilon },
            gatherWork,
            measuredFrames = observed,
            fixture = CoveragePerfManifest.FixtureVersion,
            fixtureTopology = fixture.Geometry.Topology,
            device = device.Capabilities.DeviceName,
            backend = context.Backend.ToString(),
            timingBoundary = "One outer pair includes frame and bounds-reset uploads, all carry dispatches, current/previous publication and tight bounds. No in-frame readback. Outer-pair rows decide the floor. Separate stage rows add eight diagnostic markers.",
            exclusions = "Resident shared topology, kernels and full exact anchors. No certificate validity, atlas lookup, certification or refresh. No drawing. Ordinary control uses the renderer's raw skin graph in compute storage with the same uploads, bounds and history.",
            rows,
        });
        string directory = Environment.GetEnvironmentVariable("KE_COVERAGE_PROFILE_RESULTS")
            ?? Path.Combine(CoveragePerfDispatch.RepositoryRoot(AppContext.BaseDirectory), "KhaozEngine.Render.Tests", "TestResults");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "coverage-carry-profile.json");
        File.WriteAllText(path, json + Environment.NewLine);
        output.WriteLine("COVERAGE_CARRY_PROFILE_JSON=" + json);
        output.WriteLine("JSON file: " + path);
    }
}
