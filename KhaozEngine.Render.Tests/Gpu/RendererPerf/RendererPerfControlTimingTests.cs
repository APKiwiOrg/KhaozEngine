using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Tests.Gpu.CoveragePerf;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("GpuBackendProvidersSerial")]
public sealed class RendererPerfControlTimingTests
{
    [Fact]
    public void Ready_D3D11_equal_query_values_retain_zero_ticks_and_a_null_duration()
    {
        object gate = new();
        var timeline = new D3D11TimestampTestTimeline { Completed = 1 };
        using var fences = new D3D11FenceSubsystem(timeline, gate);
        var queries = new D3D11TimestampTestQueries { Frequency = 1_000_000_000 };
        using var pool = new D3D11TimestampPool(new D3D11TimestampApi(queries), fences, gate, 2);
        var emitter = new D3D11NativeTraceEmitter(new D3D11DeviceState(), new D3D11NativeCallLog());
        using var list = D3D11CommandDrivers.Create(D3D11RecordMode.Deferred, emitter, fences);
        using var completion = fences.CreateFence();
        list.Begin();
        list.BeginTimestamps(pool, 2);
        list.WriteTimestamp(pool, 0);
        Assert.IsAssignableFrom<IGpuCommandListTimestampEnd>(list).FinishTimestampsOnEnd(pool, 1);
        list.End();
        D3D11CommandDrivers.Submit(gate, list, ref emitter, fences, completion);
        ulong[] ticks = new ulong[2];
        Assert.Equal(GpuTimestampStatus.Ready, pool.TryResolve(completion, ticks, out double period));
        Assert.Equal(new ulong[] { 0, 0 }, ticks);
        Assert.Equal(1, period);
        long clock = 0;
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.TotalOnly, [], () => ++clock, 1000);
        recorder.BeginFrame(0, 0, 0);
        recorder.EndFrame();
        var sample = recorder.Harvest(0, new([.. ticks], period, true, "ready D3D11 queries"));
        Assert.Equal(RendererPerfMetricStatus.BelowResolution, sample.GpuFrame.Status);
        Assert.Equal(0ul, sample.GpuElapsedTicks);
        Assert.Null(sample.GpuFrame.Value);
        Assert.Equal(0, timeline.Flushes);
        Assert.Equal(0, timeline.Waits);
    }

    [Theory]
    [InlineData("linux-vulkan")]
    [InlineData("windows-d3d11")]
    public void Driver_retains_available_then_zero_query_results_without_dropping_frames(string leg)
    {
        var report = RendererPerfControlTimingFixture.MatchControls(RendererPerfAnalysisFixture.Report(full: true));
        var identity = report.Identity with { Backend = leg == "linux-vulkan" ? "vulkan" : "d3d11" };
        var rig = new RendererPerfDriverTests.DriverRig(template: report with { Identity = identity })
        {
            RetainFrame = (block, frame) => block.SceneId is "control:1" or "control:2" or "control:3"
                ? RendererPerfControlTimingFixture.Sample(int.Parse(block.SceneId.AsSpan(8)), frame, frame.FrameId == 0 ? 2528ul : 0ul) : frame
        };
        // Driver admission also checks the manifest backend.
        if (leg == "windows-d3d11")
        {
            var json = RendererPerfProtocolTests.ManifestJson("full", leg);
            var manifest = RendererPerfManifest.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
            report = RendererPerfControlTimingFixture.MatchControls(report with
            { Manifest = manifest, Identity = identity with { Environment = manifest.Runtime.Environment } });
            rig = new RendererPerfDriverTests.DriverRig(template: report) { RetainFrame = rig.RetainFrame };
        }
        var execution = rig.Execute();
        var result = Assert.IsType<RendererPerfReport>(execution.Report);
        Assert.Empty(execution.Failures);
        Assert.True(result.Assessment.ValidForPromotion);
        Assert.Empty(result.Assessment.Reasons);
        Assert.Null(result.Publication.OriginalFailure);
        Assert.Equal(16944, rig.Frames.Count);
        foreach (var control in result.Controls.Take(3))
        {
            Assert.Equal(2528, control.Warmup[0].GpuFrame.Value);
            Assert.Equal(63, control.Warmup.Count(f => f.GpuFrame.Status == RendererPerfMetricStatus.BelowResolution));
            Assert.Equal(128, control.Measured.Length);
            Assert.All(control.Measured, frame =>
            {
                Assert.Equal(0ul, frame.GpuElapsedTicks);
                Assert.Equal(1, frame.GpuPeriodNs);
                Assert.Null(frame.GpuFrame.Value);
                Assert.Equal(RendererPerfMetricStatus.BelowResolution, frame.GpuFrame.Status);
            });
        }
    }

    [Theory]
    [InlineData("conditioning:S3")]
    [InlineData("S0")]
    [InlineData("control:4")]
    [InlineData("control:5")]
    [InlineData("control:6")]
    [InlineData("control:7")]
    [InlineData("control:8")]
    [InlineData("control:9")]
    [InlineData("control:10")]
    [InlineData("control:11")]
    [InlineData("control:12")]
    public void Zero_outer_interval_with_real_work_still_refuses(string scene)
    {
        var rig = new RendererPerfDriverTests.DriverRig(full: true)
        {
            RetainFrame = (block, frame) => block.SceneId == scene ? RendererPerfControlTimingFixture.Sample(1, frame) : frame
        };
        var report = rig.Run();
        Assert.Equal("Invalid GPU frame timing.", report.Publication.OriginalFailure);
        Assert.False(report.Publication.FinalUnitPublished);
    }

    [Theory]
    [InlineData("missing-clear")]
    [InlineData("draw")]
    [InlineData("missing-counter")]
    [InlineData("period")]
    [InlineData("positive-ticks")]
    [InlineData("failure")]
    [InlineData("unsupported")]
    public void Malformed_or_broken_clear_only_bracket_still_refuses(string fault)
    {
        var rig = new RendererPerfDriverTests.DriverRig
        {
            RetainFrame = (block, frame) =>
            {
                if (block.SceneId != "control:1") return frame;
                var zero = RendererPerfControlTimingFixture.Sample(1, frame);
                string key = "logical.total." + (fault == "missing-clear" ? "ColorClears" : "Draws");
                return fault switch
                {
                    "missing-clear" => zero with { Metrics = zero.Metrics.SetItem(key, RendererPerfDriver.Value(0, "count", "broken clear")) },
                    "draw" => zero with { Metrics = zero.Metrics.SetItem(key, RendererPerfDriver.Value(1, "count", "unexpected work")) },
                    "missing-counter" => zero with { Metrics = zero.Metrics.Remove(key) },
                    "period" => zero with { GpuPeriodNs = 2 },
                    "positive-ticks" => zero with { GpuElapsedTicks = 1 },
                    "failure" => zero with { Errors = ["query failure"] },
                    _ => zero with { GpuFrame = zero.GpuFrame with { Status = RendererPerfMetricStatus.Unsupported } }
                };
            }
        };
        var report = rig.Run();
        Assert.NotNull(report.Publication.OriginalFailure);
        Assert.False(report.Publication.FinalUnitPublished);
    }

    [Fact]
    public void Mixed_control_measurements_have_no_numeric_distribution_or_reference_rate()
    {
        var report = RendererPerfControlTimingFixture.Censor(RendererPerfAnalysisFixture.Report());
        var control = report.Controls[0];
        control = control with
        {
            Measured = control.Measured.SetItem(0,
            RendererPerfControlTimingFixture.Sample(1, control.Measured[0], 2528))
        };
        report = report with { Controls = report.Controls.SetItem(0, control) };
        var summary = RendererPerfAnalysis.Analyze(report);
        var duration = summary.Controls[0].Metrics["gpu.frame"];
        Assert.Equal(1, duration.Count);
        Assert.Equal(64, duration.ExpectedCount);
        Assert.Equal(63, duration.Unavailable.Length);
        Assert.Equal(RendererPerfMetricStatus.ModelIncomplete, duration.Mean.Status);
        Assert.Null(duration.Mean.Value);
        var rates = RendererPerfControls.AchievedRates(control);
        Assert.Null(rates["frame.meanNs"].Value);
        Assert.Equal(RendererPerfMetricStatus.ModelIncomplete, rates["frame.meanNs"].Status);
        Assert.Equal(RendererPerfMetricStatus.Unsupported, rates["bandwidth.bytesPerSecond"].Status);
    }

    [Fact]
    public void Clear_only_zero_intervals_require_checked_outputs_in_both_terminal_policies()
    {
        var report = RendererPerfControlTimingFixture.Censor(RendererPerfAnalysisFixture.Report(full: true));
        Assert.True(RendererPerfRehearsalPolicy.FramesPassed(report));
        var control = report.Controls[0];
        report = report with
        {
            Controls = report.Controls.SetItem(0,
            control with { Metrics = control.Metrics.Remove("control.outputValidated") })
        };
        Assert.False(RendererPerfRehearsalPolicy.FramesPassed(report));
        Assert.Equal("Publication", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report)).Code);
    }

    [Fact]
    public void Censored_control_requires_the_frozen_work_identity()
    {
        var report = RendererPerfControlTimingFixture.Censor(RendererPerfAnalysisFixture.Report());
        var control = report.Controls[0];
        control = control with { Control = control.Control with { WorkSha256 = new('f', 64) } };
        report = report with
        {
            Manifest = report.Manifest with { Controls = report.Manifest.Controls.SetItem(0, control.Control) },
            Controls = report.Controls.SetItem(0, control)
        };
        Assert.Equal("Publication", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report)).Code);
    }

    [Fact]
    public void Zero_pass_interval_keeps_a_positive_scene_outer_interval_and_unavailable_partition()
    {
        var report = RendererPerfAnalysisFixture.Report();
        var block = report.Blocks[1];
        var frame = block.Measured[0];
        frame = frame with
        {
            Passes = [new("opaque", 0, null, null, 1, 2, 0,
                new(null, "ns", "native GPU timestamp interval", RendererPerfMetricStatus.BelowResolution,
                    "The interval is below the timer resolution."), false)]
        };
        report = report with { Blocks = report.Blocks.SetItem(1, block with { Measured = block.Measured.SetItem(0, frame) }) };
        var summary = RendererPerfAnalysis.Analyze(report);
        Assert.Equal(RendererPerfMetricStatus.Available, summary.Blocks[1].Metrics["gpu.frame"].Mean.Status);
        Assert.Null(summary.Blocks[1].Metrics["gpu.unattributed"].Mean.Value);
        Assert.Null(summary.Blocks[1].Metrics["gpu.pass.opaque.0"].Mean.Value);
    }

    [Fact]
    public void Below_resolution_descriptor_cannot_hide_positive_ticks_in_a_partial_report()
    {
        var report = RendererPerfAnalysisFixture.Failed(RendererPerfControlTimingFixture.Censor(RendererPerfAnalysisFixture.Report()));
        var control = report.Controls[0];
        var sample = control.Measured[0] with { GpuElapsedTicks = 1 };
        report = report with
        {
            Controls = report.Controls.SetItem(0,
            control with { Measured = control.Measured.SetItem(0, sample) })
        };
        Assert.Equal("Sample", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(report)).Code);
    }

    [Theory]
    [InlineData(false, 1, 1, 0)]
    [InlineData(true, 1, 1, 0)]
    [InlineData(false, 2, 5, 1)]
    [InlineData(true, 2, 5, 1)]
    [InlineData(false, 3, 1, 1)]
    [InlineData(true, 3, 1, 1)]
    public void D3D11_trace_brackets_every_clear_before_the_end_tail(bool deferred, int id, int colors, int depths)
    {
        using var rig = new D3D11TimestampEndFixture(deferred);
        using var device = new PostTraceDevice();
        var facts = RendererPerfControls.Definition(id);
        using var work = RendererPerfControls.Create(device, new(id, "1", facts.WorkSha256, 64, 128));
        work.PrepareCold(-64);
        rig.Start();
        rig.List.SetFramebuffer(work.Target);
        rig.List.ClearColorTarget(0, work.ClearColor);
        work.Record(rig.List, -64);
        rig.Arm();
        rig.List.End();
        rig.Submit();
        string[] trace = rig.Log.Trace.ToArray();
        Assert.Equal("ClearState()", trace[0]);
        Assert.Equal("Begin(r0)", trace[1]);
        Assert.Equal("End(r1)", trace[2]);
        Assert.Equal(new[] { "End(" + rig.Log.Id(rig.Pool.TimestampQuery(1)) + ")", "End(r0)" }, trace.TakeLast(2));
        Assert.Equal(colors, trace.Count(t => t.StartsWith("ClearRenderTargetView(", StringComparison.Ordinal)));
        Assert.Equal(depths, trace.Count(t => t.StartsWith("ClearDepthStencilView(", StringComparison.Ordinal)));
        Assert.DoesNotContain(trace, t => t.StartsWith("Draw", StringComparison.Ordinal) || t.StartsWith("Dispatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Verified_publisher_retains_censored_controls_and_never_uses_positive_subset()
    {
        using var fixture = new RendererPerfBaselineFixture();
        var request = fixture.Request();
        var windows = fixture.Censored("windows-d3d11");
        request = request with { Legs = [request.Legs[0], windows] };
        var summary = RendererPerfAnalysis.ReadDirectory(windows.Directory, windows.ReportName);
        Assert.True(summary.PromotionEligible);
        foreach (var control in summary.Controls.Take(3))
        {
            var duration = control.Metrics["gpu.frame"];
            Assert.Equal(0, duration.Count);
            Assert.Equal(128, duration.ExpectedCount);
            Assert.Equal(128, duration.Unavailable.Length);
            Assert.Null(duration.Mean.Value);
            Assert.Equal(RendererPerfMetricStatus.BelowResolution, duration.Mean.Status);
            Assert.Null(control.Metrics["achieved.frame.meanNs"].Mean.Value);
        }
        string offline = RendererPerfAnalysis.Markdown(summary);
        Assert.Contains("warmup 63/64, measured 128/128", offline);
        Assert.Contains("reported tick periods 1 ns", offline);
        Assert.Contains("not an execution-time upper bound", offline);
        var paths = RendererPerfBaselinePublisher.Publish(request);
        string published = File.ReadAllText(Path.Combine(request.OutputDirectory, paths[1]));
        Assert.Contains("warmup 63/64, measured 128/128", published);
        Assert.Contains("BelowResolution", published);
        Assert.DoesNotContain("| control:1 | gpu.frame | 0/128 | 0 ns", published);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(RendererPerfCompilerPublicationTests.Verifier());
        start.ArgumentList.Add(windows.Directory);
        start.ArgumentList.Add(windows.ReportName);
        start.Environment.Remove("KE_GPU_TESTS");
        start.Environment.Remove("KE_RENDERER_PERF_UNIT");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stdout + await stderr);

        // A received report cannot promote a zero scene interval even if its original assessment claims success.
        string path = Path.Combine(windows.Directory, windows.ReportName);
        var raw = JsonNode.Parse(File.ReadAllBytes(path))!;
        var zero = raw["controls"]![0]!["measured"]![0]!;
        var scene = raw["blocks"]![0]!["measured"]![0]!;
        scene["gpuElapsedTicks"] = 0;
        scene["gpuFrame"] = zero["gpuFrame"]!.DeepClone();
        byte[] changed = Encoding.UTF8.GetBytes(raw.ToJsonString());
        Assert.Equal("Publication", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Parse(changed)).Code);
        File.WriteAllBytes(path, changed);
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfArtifactVerification.Verify(windows.Directory, windows.ReportName));
        request = request with { OutputDirectory = fixture.Output() };
        Assert.Equal("EvidenceInvalid", Assert.Throws<RendererPerfBaselineException>(() => RendererPerfBaselinePublisher.Publish(request)).Code);
        Assert.False(Directory.Exists(request.OutputDirectory));
    }
}
