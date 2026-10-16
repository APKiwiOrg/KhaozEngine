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

// Manifest, report and driver accounting through a fake session and clock. Nothing here opens a device, renders a
// frame or reads elapsed time, so the contract stays checkable in every ordinary test run.
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
        var session = new FakeSession { NativeDevice = false };
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
    public void Recording_session_runs_the_real_workload_with_a_fake_clock_as_a_synthetic_report()
    {
        string json = Valid.Replace("\"backend\": \"vulkan-native\"", "\"backend\": \"recording-cpu\"", StringComparison.Ordinal)
            .Replace("\"actors\": 32", "\"actors\": 1", StringComparison.Ordinal)
            .Replace("\"passes\": \"full\"", "\"passes\": \"colour-only\"", StringComparison.Ordinal)
            .Replace("\"warmupFrames\": 4", "\"warmupFrames\": 2", StringComparison.Ordinal)
            .Replace("\"measuredFrames\": 3", "\"measuredFrames\": 1", StringComparison.Ordinal);
        var report = CoverageCostDriver.Run(Bytes(json), CoverageCostRecordingSession.Open, FakeClock());
        Assert.True(report.Status == CoverageCostRunStatus.Completed, report.Reason);
        Assert.True(report.Synthetic);
        Assert.Equal("recording", report.Session!.AdapterClass);
        Assert.Equal(2000, report.Session.Topology.SourceTriangles);
        Assert.Equal(1, report.Metric("outcome.covered").Value);
        Assert.Equal(0, report.Metric("outcome.declined").Value);
        Assert.Equal(CoverageCostStatus.Unavailable, report.Span("submitWait.wall").Status);
        Assert.All(report.Budgets, b => Assert.Equal(CoverageCostBudgetStatus.Unproved, b.Status));
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
        var report = CoverageCostDriver.Run(Bytes(unauthorized), (_, _) => { sessions++; return new FakeSession(); }, FakeClock());
        Assert.Equal(0, sessions);
        Assert.Equal(CoverageCostRunStatus.Refused, report.Status);
        Assert.Contains("authorization", report.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, report.MeasuredFrames);
        Assert.Contains("\"status\": \"refused\"", report.ToJson());
    }

    [Fact]
    public void Session_refusal_is_reported_before_any_frame()
    {
        var session = new FakeSession();
        var report = CoverageCostDriver.Run(Bytes(Valid), (_, _) => throw new CoverageCostRefusedException("software adapter"), FakeClock());
        Assert.Equal(CoverageCostRunStatus.Refused, report.Status);
        Assert.Contains("software adapter", report.Reason, StringComparison.Ordinal);
        Assert.Equal(0, session.Frames);
    }

    [Fact]
    public void Nested_spans_are_derived_without_double_counting()
    {
        var session = new FakeSession();
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
        Assert.Equal(5, report.Span("prepare.other").Mean);
        Assert.Equal(CoverageCostStatus.Derived, report.Span("prepare.other").Status);
        Assert.Equal(9, report.Span("encode.resolution").Mean);
        Assert.Equal(2, report.Span("encode.renderCertification").Mean);
        Assert.Equal(7, report.Span("encode.resolutionOther").Mean);
        Assert.Equal(1, report.Span("encode.surfaceAppend").Mean);
        Assert.Equal(20, report.Span("encode.other").P50);
        Assert.Equal(22, report.Span("encode.other").Max);
        Assert.Equal(40, report.Span("submitWait.wall").Mean);
        Assert.Equal(new double?[] { 30, 30, 32 }, new[] { report.Span("encode.total").Min, report.Span("encode.total").P50,
            report.Span("encode.total").Max });
        Assert.Contains("not isolated GPU execution", report.Span("submitWait.wall").Includes, StringComparison.Ordinal);

        var gpu = report.Metric("gpu.nativeExecution");
        Assert.Equal(CoverageCostStatus.Unavailable, gpu.Status);
        Assert.Null(gpu.Value);
        // Every actor frame stays in the denominators, declines and fallbacks included.
        Assert.Equal(3 * 32, report.Metric("outcome.actorFrames").Value);
        Assert.Equal(3 * 31, report.Metric("outcome.accepted").Value);
        Assert.Equal(3, report.Metric("outcome.declined").Value);
        Assert.Equal(3 * 30, report.Metric("outcome.covered").Value);
        Assert.Equal(3, report.Metric("outcome.fallback").Value);
        Assert.Equal(2, report.Refusals.Single(r => r.Key == "decline:CapacityExceeded").Value);
        Assert.Equal(CoverageCostStatus.Estimated, report.Metric("frame.trianglesEstimated").Status);
        Assert.Equal(96, report.Metric("upload.partitioned.bytesPerFrame").Value);
        Assert.Equal(0, report.Metric("alloc.prepareSubmit.bytesPerFrame").Value);
        Assert.Contains(report.Capacities, c => c.Name == "actor.scratch.vertices" && c.Status == CoverageCostStatus.Measured);

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
        var session = new FakeSession { Coverage = false };
        var report = CoverageCostDriver.Run(Bytes(With("\"coverage\": \"on\"", "\"coverage\": \"off\"")), (_, _) => session, FakeClock());
        Assert.Equal(CoverageCostRunStatus.Completed, report.Status);
        foreach (string name in new[] { "prepare.skinning", "prepare.geometryEvaluation", "encode.renderCertification" })
        {
            Assert.Equal(CoverageCostStatus.NotApplicable, report.Span(name).Status);
            Assert.Null(report.Span(name).Mean);
        }
        var baseline = report.Metric("baseline.skinning");
        Assert.Equal(CoverageCostStatus.Unavailable, baseline.Status);
        Assert.Null(baseline.Value);
        Assert.Equal(12, report.Span("prepare.total").Mean);
        AssertMissingIsNull(report.ToJson());
    }

    [Fact]
    public void Budgets_cannot_pass_without_measured_native_inputs()
    {
        string off = With("\"coverage\": \"on\"", "\"coverage\": \"off\"");
        var syntheticOn = CoverageCostDriver.Run(Bytes(Valid), (_, _) => new FakeSession(), FakeClock());
        var syntheticOff = CoverageCostDriver.Run(Bytes(off), (_, _) => new FakeSession { Coverage = false }, FakeClock());
        Assert.All(syntheticOn.Budgets, b => Assert.Equal(CoverageCostBudgetStatus.Unproved, b.Status));
        Assert.Equal(CoverageCostBudgetStatus.Unproved, CoverageCostBudgets.TypicalAddedCpu(syntheticOn, syntheticOff).Status);
        var gpu = CoverageCostBudgets.NativeGpuOverhead(syntheticOn, syntheticOff);
        Assert.Equal(CoverageCostBudgetStatus.Unproved, gpu.Status);
        Assert.Null(gpu.Observed);

        // The comparison arithmetic itself, on non-synthetic fake numbers. A native GPU budget stays unproved even then.
        var on = CoverageCostDriver.Run(Bytes(Valid), (_, _) => new FakeSession { Synthetic = false }, FakeClock());
        var cheap = CoverageCostDriver.Run(Bytes(off), (_, _) => new FakeSession { Synthetic = false, Coverage = false, PrepareTicks = 11 }, FakeClock());
        var dear = CoverageCostDriver.Run(Bytes(off), (_, _) => new FakeSession { Synthetic = false, Coverage = false, PrepareTicks = 1 }, FakeClock());
        Assert.Equal((CoverageCostBudgetStatus.Pass, 1d), (CoverageCostBudgets.TypicalAddedCpu(on, cheap).Status, CoverageCostBudgets.TypicalAddedCpu(on, cheap).Observed!.Value));
        Assert.Equal(CoverageCostBudgetStatus.Fail, CoverageCostBudgets.TypicalAddedCpu(on, dear).Status);
        Assert.Equal(CoverageCostBudgetStatus.Unproved, CoverageCostBudgets.NativeGpuOverhead(on, cheap).Status);
        var limit = CoverageCostDriver.Run(Bytes(Valid.Replace("\"fixture\": \"typical\"", "\"fixture\": \"limit\"")),
            (_, _) => new FakeSession { Synthetic = false }, FakeClock());
        Assert.Equal(CoverageCostBudgetStatus.Unproved, CoverageCostBudgets.TypicalAddedCpu(limit, cheap).Status);
    }

    [Fact]
    public void Frame_limit_stops_the_run_with_partial_counts()
    {
        var session = new FakeSession { SlowFrame = 1 };
        var report = CoverageCostDriver.Run(Bytes(Valid), (_, _) => session, FakeClock());
        Assert.Equal(CoverageCostRunStatus.Failed, report.Status);
        Assert.Contains("maxFrameMilliseconds", report.Reason, StringComparison.Ordinal);
        Assert.Equal(2, report.MeasuredFrames);
        Assert.Equal(2 * 32, report.Metric("outcome.actorFrames").Value);
        Assert.True(session.Disposed);
    }

    [Fact]
    public void Wall_limit_stops_the_run()
    {
        long now = 0;
        var clock = new CoverageCostClock(() => now += 20_000, 1000);
        var report = CoverageCostDriver.Run(Bytes(Valid), (_, _) => new FakeSession(), clock);
        Assert.Equal(CoverageCostRunStatus.Failed, report.Status);
        Assert.Contains("maxWallSeconds", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_errors_fail_the_report_and_keep_counted_frames()
    {
        var session = new FakeSession { ThrowAt = 5 };
        var report = CoverageCostDriver.Run(Bytes(Valid), (_, _) => session, FakeClock());
        Assert.Equal(CoverageCostRunStatus.Failed, report.Status);
        Assert.Contains(nameof(InvalidOperationException), report.Reason, StringComparison.Ordinal);
        Assert.Equal(1, report.MeasuredFrames);
        Assert.Equal(1, report.Metric("outcome.errors").Value);
        Assert.True(session.Disposed);
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

    static CoverageCostClock FakeClock()
    {
        long now = 0;
        return new CoverageCostClock(() => ++now, 1000);
    }

    // One tick is one millisecond. Each measured frame reports fixed spans, the third a little slower to encode.
    internal sealed class FakeSession : ICoverageCostSession
    {
        internal bool Coverage { get; init; } = true;
        internal bool Synthetic { get; init; } = true;
        internal long PrepareTicks { get; init; } = 12;
        internal int SlowFrame { get; init; } = -1;
        internal bool NativeDevice { get; init; } = true;
        bool ICoverageCostSession.NativeDevice => NativeDevice;
        internal int ThrowAt { get; init; } = -1;
        internal int Frames { get; private set; }
        internal bool Disposed { get; private set; }
        bool ICoverageCostSession.Synthetic => Synthetic;
        public CoverageCostSessionInfo Info => new("VulkanNative", "fake adapter", "unclassified",
            "camera", "light", "layout", "timing", new CoverageCostTopology(2121, 2000, 200, 96, 2900, 2600, 2, 2, "2", 2));
        public void Frame(bool measured, ref CoverageCostFrameSample sample)
        {
            int frame = Frames++;
            if (frame == ThrowAt) throw new InvalidOperationException("fake failure");
            int measuredIndex = frame - 4;
            sample.PrepareTicks = PrepareTicks;
            sample.SkinningTicks = Coverage ? 3 : 0;
            sample.GeometryTicks = Coverage ? 4 : 0;
            sample.SubmissionTicks = 2;
            sample.ReserveTicks = 1;
            sample.EncodeTicks = measuredIndex == 2 ? 32 : 30;
            sample.ResolutionTicks = 9;
            sample.CertificationTicks = Coverage ? 2 : 0;
            sample.SurfaceTicks = 1;
            sample.SubmitWaitTicks = 40;
            sample.FrameTicks = measuredIndex >= 0 && measuredIndex == SlowFrame ? 51 : 49;
            sample.ActorFrames = 32;
            sample.Accepted = Coverage ? 31 : 0;
            sample.Declined = Coverage ? 1 : 0;
            sample.Covered = Coverage ? 30 : 0;
            sample.Fallback = Coverage ? 1 : 0;
            sample.Skinned = sample.Evaluated = sample.Accepted;
            sample.Certified = sample.Covered + sample.Fallback;
            sample.Passes = 10;
            sample.Stats.DrawCalls = 7;
            sample.Stats.Triangles = 5000;
            sample.Stats.AddSkinnedUpload(64);
            sample.Stats.AddInstanceUpload(32);
        }
        public IReadOnlyList<CoverageCostMetric> ReadCapacities() =>
            [CoverageCostMetric.Measured("actor.scratch.vertices", "elements", 2900, "fake")];
        public IReadOnlyList<KeyValuePair<string, int>> ReadRefusals() =>
            Coverage ? [new("decline:CapacityExceeded", 2), new("decline:IdentityMismatch", 1), new("fallback:UnsupportedTransform", 3)] : [];
        public void Dispose() => Disposed = true;
    }
}
