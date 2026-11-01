using System;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfWorkModelTests
{
    [Theory]
    [InlineData("all")]
    [InlineData("msaa-blob")]
    [InlineData("split")]
    [InlineData("outdoors")]
    [InlineData("legacy")]
    public void Every_registry_owner_has_work_or_a_specific_missing_reason(string mode)
    {
        using var rig = new PostTraceRig(mode);
        for (int i = 64; i < 68; i++) rig.Frame(i);
        var observer = new PostTraceObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(68);
        var registry = observer.Freeze();
        var models = RendererPerfWorkModels.ForRegistry(registry, "public correctness fixture");
        Assert.Equal(registry.Length, models.Length);
        Assert.All(registry, pass => Assert.Contains(models, m => m.PassId == pass.Id && m.Occurrence == pass.Occurrence));
        Assert.All(models, model => Assert.NotEmpty(model.MissingEvidence));
        Save(mode, models);
        Assert.Contains(models, m => m.PassId == "device.scene.begin" && m.MissingEvidence.Any(s => s.Contains("CPU-only", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Geometry_and_all_shadow_routes_have_named_evidence(bool gpu)
    {
        using var rig = new RendererPerfGeometryTraceTests.GeometryRig(gpu);
        rig.Frame(); rig.Frame();
        var observer = new RendererPerfGeometryTraceTests.GeometryObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(rigidOffset: .2f);
        var registry = observer.Freeze();
        var models = RendererPerfWorkModels.ForRegistry(registry, "public geometry correctness fixture");
        Assert.Equal(registry.Length, models.Length);
        Assert.All(models, model => Assert.NotEmpty(model.MissingEvidence));
        Assert.Contains(models, model => model.PassId == (gpu ? "opaque.gpu-skinned" : "opaque.cpu-skinned"));
        Save(gpu ? "geometry-gpu" : "geometry-cpu", models);
    }

    static void Save(string mode, ImmutableArray<RendererPassWorkModel> models)
    {
        string? directory = Environment.GetEnvironmentVariable("KE_RENDERER_PERF_TASK12_EVIDENCE");
        if (string.IsNullOrEmpty(directory)) return;
        System.IO.Directory.CreateDirectory(directory);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, "registry-" + mode + ".json"),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(models));
    }

    [Fact]
    public void Copy_counts_include_reads_writes_and_do_not_invent_shader_traffic()
    {
        var metric = new RendererPerfMetric(4096, "bytes", "synthetic exclusive copy", RendererPerfMetricStatus.Available, null);
        var frame = new RendererPerfFrameSample(0, 0, 0, 1, 1, null, null, metric,
            [new("copy.opaque-temporal", 0, null, null, 1, 2, null, metric, false),
             new("post.tonemap", 0, null, null, 3, 4, null, metric, false)], [],
            ImmutableDictionary<string, RendererPerfMetric>.Empty.Add("logical.pass.copy.opaque-temporal.0.CopyBytes", metric), []);
        var models = RendererPerfWorkModels.ForFrame(frame, "synthetic recipe");
        Assert.Equal(4096, models[0].Components["readBytes"].Minimum);
        Assert.Equal(4096, models[0].Components["writeBytes"].Minimum);
        Assert.Null(models[1].Components["readBytes"].Minimum);
        Assert.Contains(models[1].MissingEvidence, s => s.Contains("texture", StringComparison.Ordinal));
    }

    [Fact]
    public void Texture_access_counts_formats_mips_samples_and_both_directions()
    {
        var traffic = RendererPerfWorkModels.TextureAccess(8, 4, 4, 2, 4, GpuPixelFormat.R16G16Float, 3, 2);
        Assert.Equal(4128, traffic.ReadBytes);
        Assert.Equal(2752, traffic.WriteBytes);
        var mip = RendererPerfWorkModels.MipTraffic(8, 4, 4, 2, GpuPixelFormat.R16G16Float);
        Assert.Equal(352, mip.ReadBytes);
        Assert.Equal(88, mip.WriteBytes);
    }
}
