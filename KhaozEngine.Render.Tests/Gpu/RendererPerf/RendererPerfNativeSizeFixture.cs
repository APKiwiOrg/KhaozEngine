using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Replay complete recorder-produced traces at each admitted frame's clock origin. Share immutable
// counters, not wire bytes, so the full-unit size proof does not allocate millions of duplicate metrics.
internal sealed class RendererPerfNativeSizeFixture
{
    const long Origin = 9_000_000_000_000_000;
    readonly RendererPerfFrameSample _total, _detailed;
    internal RendererPerfNativeSizeFixture(bool retainHeldFailure = true)
    { _total = Record(false, retainHeldFailure); _detailed = Record(true, retainHeldFailure); }

    internal RendererPerfFrameSample Replay(RendererPerfFrameSample frame, bool detailed)
    {
        var sample = detailed ? _detailed : _total;
        long delta = checked(Origin + frame.CpuStartTicks * 1_000_000 - sample.CpuStartTicks);
        return sample with
        {
            FrameId = frame.FrameId,
            SequenceIndex = frame.SequenceIndex,
            CpuStartTicks = checked(sample.CpuStartTicks + delta),
            CpuEndTicks = checked(sample.CpuEndTicks + delta),
            CpuPhases = [.. sample.CpuPhases.Select(s => s with
            { StartTicks = checked(s.StartTicks + delta), EndTicks = checked(s.EndTicks + delta) })],
            Errors = [.. sample.Errors.Concat(frame.Errors)]
        };
    }

    static RendererPerfFrameSample Record(bool detailed, bool retainHeldFailure)
    {
        string[] ids = ["shadow.cascade.rigid", "shadow.cascade.skinned", "shadow.cascade.foliage", "opaque.rigid",
            "opaque.terrain", "opaque.foliage", "opaque.skinned", "water.surface", "ocean.fft.column", "ocean.fft.row",
            "post.bloom.bright", "post.bloom.horizontal", "post.bloom.vertical", "post.bloom.composite", "post.tonemap",
            "post.sharpen", "post.fxaa", "post.blit", "temporal.resolve", "temporal.depth-store", "background.sky",
            "background.starfield", "atmosphere.transmittance", "atmosphere.multiple-scattering", "atmosphere.sky-view",
            "outline.mask", "outline.composite", "display.fills", "display.lines", "ui.sprites",
            "device.scene.begin", "device.water.geometry", "device.water.bathymetry"];
        var passes = Enumerable.Range(0, 255).Select(i => new RendererPerfPassDefinition(
            ids[i < 252 ? i % 30 : i - 222], i < 252 ? i / 30 : 0, -1,
            i < 252 ? (uint)(i * 2 + 1) : 0, i < 252 ? (uint)(i * 2 + 2) : 0, i >= 252)).ToArray();
        long tick = Origin;
        var recorder = new RendererPerfRecorder(1, detailed ? RendererPerfLayout.PerPass : RendererPerfLayout.TotalOnly,
            detailed ? passes : [], () => tick += 1_001, 1_000_000_000);
        using var commands = RendererPerfCommandList.Wrap(new NullGpuCommandList(), recorder);
        using var graphics = new FakePipeline();
        using var compute = new ComputePipeline();
        using var target = new FakeFramebuffer(new(GpuPixelFormat.D24UNormS8UInt, GpuPixelFormat.R16G16B16A16Float), 1920, 1080);
        recorder.Register(graphics, new(true, Topology: GpuPrimitiveTopology.TriangleList));
        recorder.Register(compute, new(true, LocalX: 8, LocalY: 4, LocalZ: 1));
        recorder.BeginFrame(0, 0, 0);
        foreach (string id in ids.Skip(30))
        {
            recorder.BeginCpuPhase(id);
            recorder.BeginUpload(262144, true); recorder.EndUpload();
            recorder.EndCpuPhase(id);
        }
        recorder.BeginCpuPhase("prepare"); recorder.BeginCpuPhase("packing");
        recorder.EndCpuPhase("packing"); recorder.EndCpuPhase("prepare");
        commands.Begin();
        for (int i = 0; i < 252; i++)
        {
            recorder.BeginPass(commands, i);
            if (i % 30 is 8 or 9 or 22 or 23 or 24)
            {
                commands.SetComputePipeline(compute);
                commands.Dispatch(16, 8, 1);
            }
            else
            {
                commands.SetFramebuffer(target);
                commands.SetPipeline(graphics);
                commands.Draw(6, 4, 0, 0);
            }
            if (i == 251) recorder.CopyBytes(null);
            recorder.EndPass(commands, i);
        }
        commands.End();
        Assert.Null(recorder.FrameFailure(0));
        if (detailed && retainHeldFailure) recorder.Fail("held recorder failure retained");
        recorder.EndFrame();
        var sample = recorder.Harvest(0, new GpuPerfTimestampResult(
            [.. Enumerable.Range(0, (int)recorder.MarkerCount).Select(i => (ulong)(i * 3))], 1, true, "held marker ticks"));
        Assert.Equal(detailed ? 255 : 0, sample.Passes.Length);
        if (detailed)
        {
            Assert.True(sample.CpuPhases.Length >= 510);
            foreach (var pass in passes)
            {
                string prefix = $"logical.pass.{pass.Id}.{pass.Occurrence}.";
                Assert.Contains(sample.Metrics, p => p.Key.StartsWith(prefix, StringComparison.Ordinal) && p.Value.Value > 0);
            }
        }
        Assert.Contains(sample.Metrics, p => p.Key == "target.0.width" && p.Value.Value == 1920);
        Assert.Contains(sample.Metrics, p => p.Key == "dispatch.0.local0" && p.Value.Value == 8);
        return sample;
    }

    internal static Dictionary<string, long> ByteBreakdown(ReadOnlySpan<byte> bytes)
    {
        var result = new Dictionary<string, long> { ["cpuPhases"] = 0, ["passes"] = 0, ["metricVectors"] = 0, ["rawSchema"] = 0 };
        var reader = new Utf8JsonReader(bytes);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            string? section = reader.CurrentDepth == 1 && reader.ValueTextEquals("raw") ? "rawSchema"
                : reader.CurrentDepth >= 3 && reader.ValueTextEquals("cpuPhases") ? "cpuPhases"
                : reader.CurrentDepth >= 3 && reader.ValueTextEquals("passes") ? "passes"
                : reader.CurrentDepth >= 3 && reader.ValueTextEquals("metrics") ? "metricVectors" : null;
            if (section is null) continue;
            reader.Read();
            long start = reader.TokenStartIndex;
            reader.Skip();
            result[section] += reader.BytesConsumed - start;
        }
        result["other"] = bytes.Length - result.Values.Sum();
        result["total"] = bytes.Length;
        return result;
    }

    sealed class ComputePipeline : IGpuComputePipeline { public void Dispose() { } }
}
