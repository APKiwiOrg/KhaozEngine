using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("GpuBackendProvidersSerial")]
public sealed class RendererPerfRehearsalLegExecutionTests
{
    internal static RendererPerfReport Report(string preset, string leg)
    {
        var report = RendererPerfRehearsalExecutionTests.Report();
        var manifest = RendererPerfRehearsalManifestTests.Parse(RendererPerfRehearsalClassesTests.ManifestJson(preset, leg));
        var sample = report.Conditioning[0];
        ImmutableArray<RendererPerfFrameSample> Frames(int count, int first, int warmup) =>
            [.. Enumerable.Range(first, count).Select(i => sample with
            { FrameId = i, SequenceIndex = RendererPerfRecipe.SequenceIndex(i - warmup) })];
        report = report with
        {
            Manifest = manifest,
            Identity = report.Identity with
            {
                Backend = manifest.Execution.Backend,
                Adapter = leg == "windows-d3d11" ? "Microsoft Basic Render Driver" : "llvmpipe",
                VendorId = leg == "windows-d3d11" ? 0x1414u : 0x10005u,
                DeviceId = leg == "windows-d3d11" ? 0x8cu : 0,
                Os = leg == "windows-d3d11" ? "Windows" : "Linux"
            },
            Recipes = [.. manifest.Recipes.Select(r => report.Recipes[0] with { Identity = r })],
            Blocks = [.. manifest.Blocks.Select(b => new RendererPerfBlockReport(b,
                Frames(b.WarmupFrames, 0, b.WarmupFrames), Frames(b.MeasuredFrames, b.WarmupFrames, b.WarmupFrames), []))],
            Controls = [.. manifest.Controls.Select(c => report.Controls[0] with
            {
                Control = c, Warmup = Frames(c.WarmupFrames, 0, c.WarmupFrames),
                Measured = Frames(c.MeasuredFrames, c.WarmupFrames, c.WarmupFrames)
            })]
        };
        return RendererPerfExecutionEvidenceTests.WithTrace(report);
    }

    [Theory]
    [MemberData(nameof(RendererPerfRehearsalClassesTests.Classes), MemberType = typeof(RendererPerfRehearsalClassesTests))]
    public void Every_class_admits_software_refuses_hardware_and_keeps_its_correctness_verdict(string preset, string leg)
    {
        var report = Report(preset, leg);
        RendererPerfAdmission.Identity(report.Manifest, report.Identity);
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfAdmission.Identity(report.Manifest,
            report.Identity with { SoftwareAdapter = false, VendorId = 0x10de, DeviceId = 0x1eb8 }));
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfAdmission.Identity(report.Manifest,
            report.Identity with { SoftwareAdapter = null }));
        RendererPerfVerdict.RequireSuccess(new(report, null, null, []) { Certified = true });
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfVerdict.RequireSuccess(new(report, null, null, [])));
        var rig = new RendererPerfDriverTests.DriverRig(template: report) { Drift = true };
        var driven = rig.Run();
        Assert.Equal(report.Manifest.TotalFrameCount, rig.Frames.Count);
        Assert.False(driven.Assessment.ValidForPromotion);
        Assert.Contains(RendererPerfRehearsalPolicy.Reason, driven.Assessment.Reasons);
        Assert.DoesNotContain(driven.Assessment.Reasons, reason => reason.Contains("ClockOrWorkDrift", StringComparison.Ordinal));
        Assert.All(rig.Writes, write => Assert.False(write.Report.Assessment.ValidForPromotion));
    }

    [Theory]
    [InlineData("vulkan")]
    [InlineData("d3d11")]
    public void Adapter_policy_is_fail_closed_for_both_software_legs(string backend)
    {
        Assert.True(RendererPerfAdapterPolicy.Matches(RendererPerfExecutionClass.Rehearsal, true, backend, 0, 0));
        Assert.False(RendererPerfAdapterPolicy.Matches(RendererPerfExecutionClass.Rehearsal, false, backend, 0x10de, 0x1eb8));
        Assert.False(RendererPerfAdapterPolicy.Matches(RendererPerfExecutionClass.Performance, true, backend, 0x10de, 0x1eb8));
        Assert.True(RendererPerfAdapterPolicy.Matches(RendererPerfExecutionClass.Performance, false, backend, 0x10de, 0x1eb8));
        Assert.False(RendererPerfAdapterPolicy.Matches(RendererPerfExecutionClass.Rehearsal, true, "metal", 0, 0));
    }

    [Fact]
    public void D3D_identity_checks_actual_DXGI_software_before_the_one_shot_calibration()
    {
        string text = NativeIdentitySource();
        Assert.Contains("ReadD3D(d3d, executionClass)", text, StringComparison.Ordinal);
        Assert.Contains("ReadD3D(D3D11GpuDevice device, RendererPerfExecutionClass executionClass)", text, StringComparison.Ordinal);
        int flag = text.IndexOf("description.Flags & AdapterFlags.Software", StringComparison.Ordinal);
        Assert.True(flag >= 0 && flag < text.IndexOf("ProbePeriod(device)", StringComparison.Ordinal));
        Assert.Contains("RendererPerfAdapterPolicy.Matches(executionClass", text[flag..], StringComparison.Ordinal);
        Assert.Contains("tail.FinishTimestampsOnEnd(pool, 1)", text, StringComparison.Ordinal);
        Assert.Contains("No retry is permitted", text, StringComparison.Ordinal);
    }
    static string NativeIdentitySource([CallerFilePath] string source = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "RendererPerfNativeIdentity.cs"));
}
