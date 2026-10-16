using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// Manifest, report and driver accounting through fake sessions and a fake clock. Nothing here opens a device, builds
// a cost fixture, renders a frame or reads elapsed time. Real recording-session behaviour is checked once, bounded,
// in CoverageCostFixtureTests.
public sealed class CoverageCostContractTests
{
    internal const string Valid = """
        {
          "schema": "khaozengine.coverage-cost-manifest/1",
          "case": "typical-a32-on-cpu-full",
          "actors": 32,
          "coverage": "on",
          "gpuSkinning": false,
          "fixture": "typical",
          "passes": "full",
          "device": { "backend": "vulkan-native", "rejectSoftwareAdapter": false, "adapterContains": null },
          "warmupFrames": 4,
          "measuredFrames": 3,
          "limits": { "maxWallSeconds": 60, "maxFrameMilliseconds": 50 },
          "gpuExecutionMetric": "unavailable",
          "authorization": { "identity": "unit-test-only", "approvedBy": "nobody", "reference": "fake-session" }
        }
        """;

    static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);
    static string With(string from, string to)
    {
        Assert.Contains(from, Valid);
        return Valid.Replace(from, to, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_manifest_parses_every_field_and_hashes_the_exact_bytes()
    {
        byte[] bytes = Bytes(Valid);
        var manifest = CoverageCostManifest.Parse(bytes);
        Assert.Equal("typical-a32-on-cpu-full", manifest.Case);
        Assert.Equal(32, manifest.Actors);
        Assert.True(manifest.Coverage);
        Assert.False(manifest.GpuSkinning);
        Assert.Equal(CoverageCostFixtureMode.Typical, manifest.Fixture);
        Assert.Equal(CoverageCostPassSet.Full, manifest.Passes);
        Assert.Equal(GpuBackendKind.VulkanNative, manifest.Backend);
        Assert.False(manifest.RejectSoftwareAdapter);
        Assert.Null(manifest.AdapterContains);
        Assert.Equal(4, manifest.WarmupFrames);
        Assert.Equal(3, manifest.MeasuredFrames);
        Assert.Equal(60, manifest.MaxWallSeconds);
        Assert.Equal(50, manifest.MaxFrameMilliseconds);
        Assert.Equal("unit-test-only", manifest.AuthorizationIdentity);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), manifest.Sha256);

        var limit = CoverageCostManifest.Parse(Bytes(With("\"fixture\": \"typical\"", "\"fixture\": \"limit\"")
            .Replace("\"passes\": \"full\"", "\"passes\": \"colour-only\"").Replace("\"actors\": 32", "\"actors\": 128")
            .Replace("\"coverage\": \"on\"", "\"coverage\": \"off\"").Replace("\"gpuSkinning\": false", "\"gpuSkinning\": true")
            .Replace("\"backend\": \"vulkan-native\"", "\"backend\": \"metal-native\"")
            .Replace("\"adapterContains\": null", "\"adapterContains\": \"Apple M\"")));
        Assert.Equal((CoverageCostFixtureMode.Limit, CoverageCostPassSet.ColourOnly, 128, false, true, (GpuBackendKind?)GpuBackendKind.MetalNative, "Apple M"),
            (limit.Fixture, limit.Passes, limit.Actors, limit.Coverage, limit.GpuSkinning, limit.Backend, limit.AdapterContains));
    }

    public static TheoryData<string, string> Invalid => new()
    {
        { "\"actors\": 32", "\"actors\": 33" },
        { "\"actors\": 32", "\"actors\": 0" },
        { "\"actors\": 32", "\"actors\": -32" },
        { "\"actors\": 32", "\"actors\": \"32\"" },
        { "\"actors\": 32", "\"actors\": 32.0" },
        { "\"actors\": 32", "\"actors\": 32, \"actors\": 1" },
        { "\"actors\": 32", "\"actors\": 32, \"extra\": 1" },
        { "\"coverage\": \"on\"", "\"coverage\": true" },
        { "\"coverage\": \"on\"", "\"coverage\": \"maybe\"" },
        { "\"gpuSkinning\": false", "\"gpuSkinning\": 0" },
        { "\"fixture\": \"typical\"", "\"fixture\": \"Typical\"" },
        { "\"passes\": \"full\"", "\"passes\": \"shadows\"" },
        { "\"backend\": \"vulkan-native\"", "\"backend\": \"metal\"" },
        { "\"backend\": \"vulkan-native\"", "\"backend\": \"null\"" },
        { "\"rejectSoftwareAdapter\": false", "\"rejectSoftwareAdapter\": false, \"gpu\": \"any\"" },
        { "\"adapterContains\": null", "\"adapterContains\": \"\"" },
        { "\"warmupFrames\": 4", "\"warmupFrames\": 1" },
        { "\"warmupFrames\": 4", "\"warmupFrames\": 601" },
        { "\"measuredFrames\": 3", "\"measuredFrames\": 0" },
        { "\"measuredFrames\": 3", "\"measuredFrames\": 5001" },
        { "\"maxWallSeconds\": 60", "\"maxWallSeconds\": 0" },
        { "\"maxWallSeconds\": 60", "\"maxWallSeconds\": -1" },
        { "\"maxWallSeconds\": 60", "\"maxWallSeconds\": 1e999" },
        { "\"maxWallSeconds\": 60", "\"maxWallSeconds\": 3601" },
        { "\"maxFrameMilliseconds\": 50", "\"maxFrameMilliseconds\": 0" },
        { "\"maxFrameMilliseconds\": 50", "\"maxFrameMilliseconds\": 10001" },
        { "\"gpuExecutionMetric\": \"unavailable\"", "\"gpuExecutionMetric\": \"timestamp\"" },
        { "\"identity\": \"unit-test-only\"", "\"identity\": \"  \"" },
        { "\"approvedBy\": \"nobody\", ", "" },
        { "\"schema\": \"khaozengine.coverage-cost-manifest/1\"", "\"schema\": \"khaozengine.coverage-cost-manifest/2\"" },
        { "\"case\": \"typical-a32-on-cpu-full\"", "\"case\": \"Typical 32!\"" },
        { "\"measuredFrames\": 3,", "\"measuredFrames\": 3,," },
        { "\"warmupFrames\": 4,", "\"warmupFrames\": 4, // comment\n" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Malformed_unknown_or_excessive_fields_are_rejected_not_coerced(string from, string to) =>
        Assert.Throws<InvalidDataException>(() => CoverageCostManifest.Parse(Bytes(With(from, to))));

    [Fact]
    public void Recording_cpu_device_parses_and_rejects_native_adapter_requirements()
    {
        string recording = With("\"backend\": \"vulkan-native\"", "\"backend\": \"recording-cpu\"");
        var manifest = CoverageCostManifest.Parse(Bytes(recording));
        Assert.True(manifest.Recording);
        Assert.Null(manifest.Backend);
        Assert.Throws<InvalidDataException>(() => CoverageCostManifest.Parse(Bytes(recording
            .Replace("\"rejectSoftwareAdapter\": false", "\"rejectSoftwareAdapter\": true", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => CoverageCostManifest.Parse(Bytes(recording
            .Replace("\"adapterContains\": null", "\"adapterContains\": \"NVIDIA\"", StringComparison.Ordinal))));
    }

    [Fact]
    public void Recording_device_reports_submission_and_gpu_time_as_unavailable()
    {
        var session = new CoverageCostFakeSession { NativeDevice = false };
        var report = CoverageCostDriver.Run(Bytes(With("\"backend\": \"vulkan-native\"", "\"backend\": \"recording-cpu\"")),
            (_, _) => session, FakeClock());
        Assert.Equal(CoverageCostRunStatus.Completed, report.Status);
        var wait = report.Span("submitWait.wall");
        Assert.Equal(CoverageCostStatus.Unavailable, wait.Status);
        Assert.Null(wait.Mean);
        Assert.Equal(12, report.Span("prepare.total").Mean);
        Assert.Contains("\"backend\": \"recording-cpu\"", report.ToJson(), StringComparison.Ordinal);
        AssertMissingIsNull(report.ToJson());
    }

    [Fact]
    public void Missing_authorization_and_non_object_roots_are_rejected()
    {
        int start = Valid.IndexOf(",\n  \"authorization\"", StringComparison.Ordinal);
        Assert.True(start > 0);
        string unauthorized = Valid[..start] + "\n}";
        Assert.Throws<InvalidDataException>(() => CoverageCostManifest.Parse(Bytes(unauthorized)));
        Assert.Throws<InvalidDataException>(() => CoverageCostManifest.Parse(Bytes("[]")));
        Assert.Throws<InvalidDataException>(() => CoverageCostManifest.Parse([]));
    }

    [Fact]
    public void Refused_authorization_never_opens_a_session()
    {
        int sessions = 0;
        string unauthorized = With("\"identity\": \"unit-test-only\"", "\"identity\": \"\"");
        var report = CoverageCostDriver.Run(Bytes(unauthorized), (_, _) => { sessions++; return new CoverageCostFakeSession(); }, FakeClock());
        Assert.Equal(0, sessions);
        Assert.Equal(CoverageCostRunStatus.Refused, report.Status);
        Assert.Contains("authorization", report.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, report.MeasuredFrames);
        Assert.Contains("\"status\": \"refused\"", report.ToJson());
    }

    [Fact]
    public void Session_refusal_is_reported_before_any_frame()
    {
        var session = new CoverageCostFakeSession();
        var report = CoverageCostDriver.Run(Bytes(Valid), (_, _) => throw new CoverageCostRefusedException("software adapter"), FakeClock());
        Assert.Equal(CoverageCostRunStatus.Refused, report.Status);
        Assert.Contains("software adapter", report.Reason, StringComparison.Ordinal);
        Assert.Equal(0, session.Frames);
    }

    [Fact]
    public void Nested_spans_are_derived_without_double_counting()
    {
        var session = new CoverageCostFakeSession();
        var report = CoverageCostDriver.Run(Bytes(Valid), (_, _) => session, FakeClock());
        Assert.Equal(CoverageCostRunStatus.Completed, report.Status);
        Assert.True(report.Synthetic);
        Assert.Equal(4 + 3, session.Frames);
        Assert.True(session.Disposed);
        Assert.Equal(3, report.MeasuredFrames);
        // Ticks run at 1000 per second, so one tick is one millisecond.
        Assert.Equal(12, report.Span("prepare.total").Mean);
        Assert.Equal(3, report.Span("prepare.skinning").Mean);
        Assert.Equal(4, report.Span("prepare.geometryEvaluation").Mean);
        Assert.Equal(1, report.Span("prepare.geometryCertification").Mean);
        Assert.Equal(3, report.Span("prepare.geometryConstruction").Mean);
        Assert.Equal(CoverageCostStatus.Derived, report.Span("prepare.geometryConstruction").Status);
        Assert.Equal(5, report.Span("prepare.other").Mean);
        Assert.Equal(CoverageCostStatus.Derived, report.Span("prepare.other").Status);
        Assert.Equal(9, report.Span("encode.resolution").Mean);
        Assert.Equal(2, report.Span("encode.renderCertification").Mean);
        Assert.Equal(7, report.Span("encode.resolutionOther").Mean);
        Assert.Equal(1, report.Span("encode.surfaceAppend").Mean);
        Assert.Equal(0, report.Span("encode.ordinarySkinning").Mean);
        Assert.Equal(1, report.Span("encode.skinnedUpload").Mean);
        Assert.Equal(19, report.Span("encode.other").P50);
        Assert.Equal(21, report.Span("encode.other").Max);
        Assert.Equal(40, report.Span("submitWait.wall").Mean);
        Assert.Equal(new double?[] { 30, 30, 32 }, new[] { report.Span("encode.total").Min, report.Span("encode.total").P50,
            report.Span("encode.total").Max });
        Assert.Contains("not isolated GPU execution", report.Span("submitWait.wall").Includes, StringComparison.Ordinal);

        var gpu = report.Metric("gpu.nativeExecution");
        Assert.Equal(CoverageCostStatus.Unavailable, gpu.Status);
        Assert.Null(gpu.Value);
        // Every actor frame stays in the denominators, declines and fallbacks included.
        Assert.Equal(3 * 32, report.Metric("outcome.actorFrames").Value);
        Assert.Equal(0, report.Metric("ordinary.cpuSkinnedVertices").Value);
        Assert.Equal(3 * 31, report.Metric("outcome.accepted").Value);
        Assert.Equal(3, report.Metric("outcome.declined").Value);
        Assert.Equal(3 * 30, report.Metric("outcome.covered").Value);
        Assert.Equal(3, report.Metric("outcome.fallback").Value);
        Assert.Equal(2, report.Refusals.Single(r => r.Key == "decline:CapacityExceeded").Value);
        Assert.Equal(CoverageCostStatus.Estimated, report.Metric("frame.trianglesEstimated").Status);
        Assert.Equal(96, report.Metric("upload.partitioned.bytesPerFrame").Value);
        Assert.Equal(0, report.Metric("alloc.prepareSubmit.bytesPerFrame").Value);
        Assert.Contains(report.Capacities, c => c.Name == "actor.scratch.vertices" && c.Status == CoverageCostStatus.Measured);
        Assert.Contains(report.Evidence, e => e.Name == "certifiedContext.host.main.total");
        Assert.Equal(4, report.Span("warmup.frame.wall").Samples);

        string json = report.ToJson();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal(CoverageCostManifest.Parse(Bytes(Valid)).Sha256, root.GetProperty("manifest").GetProperty("sha256").GetString());
        var gpuJson = root.GetProperty("metrics").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "gpu.nativeExecution");
        Assert.Equal(JsonValueKind.Null, gpuJson.GetProperty("value").ValueKind);
        Assert.Equal("unavailable", gpuJson.GetProperty("status").GetString());
        Assert.True(root.GetProperty("synthetic").GetBoolean());
        foreach (string key in new[] { "commit", "runtime", "os", "cpu", "stopwatchFrequency" })
            Assert.Contains(root.GetProperty("environment").EnumerateArray(), e => e.GetProperty("name").GetString() == key);
        Assert.Equal("fake adapter", root.GetProperty("session").GetProperty("adapter").GetString());
        Assert.Equal(2000, root.GetProperty("session").GetProperty("topology").GetProperty("sourceTriangles").GetInt32());
        AssertMissingIsNull(json);
    }

    // Unavailable and not-applicable values serialize as null, never as a measured zero.
    static void AssertMissingIsNull(string json)
    {
        using var document = JsonDocument.Parse(json);
        int missing = 0;
        foreach (var metric in document.RootElement.GetProperty("metrics").EnumerateArray())
            if (metric.GetProperty("status").GetString() is "unavailable" or "not-applicable")
            {
                Assert.Equal(JsonValueKind.Null, metric.GetProperty("value").ValueKind);
                missing++;
            }
        foreach (var span in document.RootElement.GetProperty("spans").EnumerateArray())
            if (span.GetProperty("status").GetString() is "unavailable" or "not-applicable")
            {
                foreach (string key in new[] { "mean", "min", "p50", "p95", "max" })
                    Assert.Equal(JsonValueKind.Null, span.GetProperty(key).ValueKind);
                missing++;
            }
        Assert.True(missing > 0);
    }

    [Fact]
    public void Coverage_off_reports_unobserved_baseline_stages_as_unavailable_not_zero()
    {
        var session = new CoverageCostFakeSession { Coverage = false };
        var report = CoverageCostDriver.Run(Bytes(With("\"coverage\": \"on\"", "\"coverage\": \"off\"")), (_, _) => session, FakeClock());
        Assert.Equal(CoverageCostRunStatus.Completed, report.Status);
        Assert.Equal(2, report.Span("encode.ordinarySkinning").Mean);
        Assert.Equal(CoverageCostStatus.Measured, report.Span("encode.ordinarySkinning").Status);
        Assert.Equal(3 * 1092 * 32, report.Metric("ordinary.cpuSkinnedVertices").Value);
        foreach (string name in new[] { "prepare.skinning", "prepare.geometryEvaluation", "prepare.geometryCertification",
            "encode.renderCertification" })
        {
            Assert.Equal(CoverageCostStatus.NotApplicable, report.Span(name).Status);
            Assert.Null(report.Span(name).Mean);
        }
        // A session that cannot observe the ordinary route reports it unavailable, never as a measured zero.
        var blind = CoverageCostDriver.Run(Bytes(With("\"coverage\": \"on\"", "\"coverage\": \"off\"")),
            (_, _) => new CoverageCostFakeSession { Coverage = false, ObservesOrdinarySkinning = false }, FakeClock());
        Assert.Equal(CoverageCostStatus.Unavailable, blind.Span("encode.ordinarySkinning").Status);
        Assert.Null(blind.Span("encode.ordinarySkinning").Mean);
        Assert.Equal(12, report.Span("prepare.total").Mean);
        AssertMissingIsNull(report.ToJson());
    }

    [Fact]
    public void Frame_limit_stops_the_run_with_partial_counts()
    {
        var session = new CoverageCostFakeSession { SlowFrame = 1 };
        var report = CoverageCostDriver.Run(Bytes(Valid), (_, _) => session, FakeClock());
        Assert.Equal(CoverageCostRunStatus.Failed, report.Status);
        Assert.Contains("maxFrameMilliseconds", report.Reason, StringComparison.Ordinal);
        Assert.Contains("measured frame 1 took 51 ms", report.Reason, StringComparison.Ordinal);
        Assert.Equal(2, report.MeasuredFrames);
        Assert.Equal(2 * 32, report.Metric("outcome.actorFrames").Value);
        Assert.True(session.Disposed);
    }

    [Fact]
    public void Warm_up_limit_reports_the_observed_time_and_warm_up_samples()
    {
        var report = CoverageCostDriver.Run(Bytes(Valid), (_, _) => new CoverageCostFakeSession { SlowFrame = -3 }, FakeClock());
        Assert.Equal(CoverageCostRunStatus.Failed, report.Status);
        Assert.Contains("warm-up frame 1 took 51 ms, above limits.maxFrameMilliseconds 50 ms", report.Reason, StringComparison.Ordinal);
        Assert.Equal(0, report.MeasuredFrames);
        var warm = report.Span("warmup.frame.wall");
        Assert.Equal((CoverageCostStatus.Measured, 2, 51d), (warm.Status, warm.Samples, warm.Max!.Value));
        Assert.Equal(50, report.Metric("limit.maxFrameMilliseconds").Value);
        Assert.Equal(CoverageCostStatus.Measured, report.Metric("run.elapsedMilliseconds").Status);
        Assert.Equal(CoverageCostStatus.Unavailable, report.Span("prepare.total").Status);
    }

    [Fact]
    public void Wall_limit_stops_the_run()
    {
        long now = 0;
        var clock = new CoverageCostClock(() => now += 20_000, 1000);
        var report = CoverageCostDriver.Run(Bytes(Valid), (_, _) => new CoverageCostFakeSession(), clock);
        Assert.Equal(CoverageCostRunStatus.Failed, report.Status);
        Assert.Contains("above limits.maxWallSeconds 60 s", report.Reason, StringComparison.Ordinal);
        Assert.Contains("elapsed 80 s after warm-up frame 3", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_errors_fail_the_report_and_keep_counted_frames()
    {
        var session = new CoverageCostFakeSession { ThrowAt = 5 };
        var report = CoverageCostDriver.Run(Bytes(Valid), (_, _) => session, FakeClock());
        Assert.Equal(CoverageCostRunStatus.Failed, report.Status);
        Assert.Contains("measured frame 1 failed: InvalidOperationException", report.Reason, StringComparison.Ordinal);
        Assert.Equal(1, report.MeasuredFrames);
        Assert.Equal(1, report.Metric("outcome.errors").Value);
        Assert.True(session.Disposed);
    }

    [Fact]
    public void Factory_failure_is_a_failed_report_with_its_cause()
    {
        var report = CoverageCostDriver.Run(Bytes(Valid), (_, _) => throw new InvalidOperationException("equipment was refused"), FakeClock());
        Assert.Equal(CoverageCostRunStatus.Failed, report.Status);
        Assert.Contains("InvalidOperationException: equipment was refused", report.Reason, StringComparison.Ordinal);
        Assert.Equal(0, report.MeasuredFrames);
        Assert.Contains("\"status\": \"failed\"", report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void Secondary_diagnostic_and_cleanup_failures_never_replace_the_first_failure()
    {
        var session = new CoverageCostFakeSession { ThrowAt = 5, ThrowDiagnostics = true, ThrowDispose = true };
        var report = CoverageCostDriver.Run(Bytes(Valid), (_, _) => session, FakeClock());
        Assert.Equal(CoverageCostRunStatus.Failed, report.Status);
        Assert.StartsWith("measured frame 1 failed: InvalidOperationException: fake failure", report.Reason, StringComparison.Ordinal);
        Assert.Contains("capacity read failed", report.Reason, StringComparison.Ordinal);
        Assert.Contains("dispose failed", report.Reason, StringComparison.Ordinal);
        Assert.True(session.Disposed);
        Assert.Contains(report.Capacities, c => c.Status == CoverageCostStatus.Unavailable && c.Provenance.Contains("capacity read failed", StringComparison.Ordinal));
        Assert.Contains(report.Evidence, c => c.Status == CoverageCostStatus.Unavailable);
        Assert.Equal(1, report.Metric("outcome.errors").Value);
        using var json = JsonDocument.Parse(report.ToJson());
        Assert.Equal("failed", json.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Diagnostic_or_cleanup_failure_alone_fails_an_otherwise_complete_run(bool diagnostics, bool dispose)
    {
        var report = CoverageCostDriver.Run(Bytes(Valid), (_, _) => new CoverageCostFakeSession
        { AllCovered = true, ThrowDiagnostics = diagnostics, ThrowDispose = dispose }, FakeClock());
        Assert.Equal(CoverageCostRunStatus.Failed, report.Status);
        Assert.Contains(diagnostics ? "diagnostics failed" : "cleanup failed", report.Reason, StringComparison.Ordinal);
        Assert.Equal(3, report.MeasuredFrames);
        Assert.All(report.Budgets, b => Assert.Equal(CoverageCostBudgetStatus.Unproved, b.Status));
    }

    [Fact]
    public void Cost_project_is_outside_the_solution_and_every_ci_test_selection()
    {
        string root = RepoRoot();
        const string Cost = "EquipmentCoverage.Cost.Tests";
        Assert.True(File.Exists(Path.Combine(root, "tools", Cost, Cost + ".csproj")));
        Assert.DoesNotContain(Cost, File.ReadAllText(Path.Combine(root, "KhaozEngine.slnx")), StringComparison.Ordinal);
        Assert.Equal(new[] { "KhaozEngine.slnx" }, Directory.EnumerateFiles(root, "*.sln*").Select(Path.GetFileName));
        Assert.Empty(Directory.EnumerateFiles(root, "*.csproj"));
        string selective = File.ReadAllText(Path.Combine(root, "scripts", "ci-selective-test.sh"));
        Assert.Contains("SLNX=\"KhaozEngine.slnx\"", selective);
        Assert.Contains("grep -oE 'Path=\"[^\"]+\"' \"$SLNX\"", selective);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, ".github", "workflows")).Append(Path.Combine(root, "scripts", "ci-selective-test.sh")))
            Assert.DoesNotContain(Cost, File.ReadAllText(file), StringComparison.Ordinal);
        // No other project references it. Projects sit one to three directories below the root.
        foreach (string project in Projects(root, 3).Where(p => !p.EndsWith(Cost + ".csproj", StringComparison.Ordinal)))
            Assert.DoesNotContain(Cost, File.ReadAllText(project), StringComparison.Ordinal);
    }

    static IEnumerable<string> Projects(string directory, int depth)
    {
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(child);
            if (name is "bin" or "obj" or "node_modules" || name.StartsWith('.')) continue;
            foreach (string project in Directory.EnumerateFiles(child, "*.csproj")) yield return project;
            if (depth > 1) foreach (string project in Projects(child, depth - 1)) yield return project;
        }
    }

    static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));

    static CoverageCostClock FakeClock() => CoverageCostFakes.Clock();
}
