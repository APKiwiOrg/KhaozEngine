using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfFloorPublicationTests
{
    [Theory]
    [InlineData(99, false)]
    [InlineData(200, false)]
    [InlineData(0, true)]
    public void Floor_metrics_obey_existing_report_null_and_status_contract(double observed, bool empty)
    {
        var result = RendererPerfFloorModel.Evaluate(RendererPerfFloorModelTests.Model(empty ? 0 : 50, empty ? 0 : 50, 0),
            new(1e9, null, 1e9, null, null, 0, "fp32-fma", "synthetic explicit rates"), observed);
        RendererPerfReportJson.Metric(result.PeakLowerBoundNs);
        RendererPerfReportJson.Metric(result.CalibratedReferenceNs);
        RendererPerfReportJson.Metric(result.AbsoluteExcessNs);
        RendererPerfReportJson.Metric(result.Ratio);
    }

    [Fact]
    public void Output_checks_reject_removed_writes_in_every_control_color_format()
    {
        RendererPerfControlReadback.Check([5, 8, 15, 0], GpuPixelFormat.R8G8B8A8UNorm, [.02f, .03f, .06f, 0]);
        RendererPerfControlReadback.Check([0, 52, 0, 56, 0, 58, 0, 56], GpuPixelFormat.R16G16B16A16Float, [.25f, .5f, .75f, .5f]);
        RendererPerfControlReadback.Check([0, 0, 0, 63], GpuPixelFormat.R32Float, [.5f]);
        RendererPerfControlReadback.Check([255, 123, 255, 123], GpuPixelFormat.R16G16Float, [65504, 65504]);
        RendererPerfControlReadback.Check([255, 0, 0, 255], GpuPixelFormat.R8G8B8A8UNorm, [1, 0, 0, 1]);
        Assert.Throws<System.InvalidOperationException>(() => RendererPerfControlReadback.Check([0, 0, 0, 0], GpuPixelFormat.R32Float, [.5f]));
        Assert.Throws<System.InvalidOperationException>(() => RendererPerfControlReadback.Check([0, 0, 0, 0], GpuPixelFormat.R16G16Float, [65504, 65504]));
        Assert.Throws<System.InvalidOperationException>(() => RendererPerfControlReadback.Check([0, 0, 0, 0], GpuPixelFormat.R8G8B8A8UNorm, [1, 0, 0, 1]));
    }
    [Fact]
    public void Recorded_target_and_dispatch_facts_remain_metadata_without_invented_traffic()
    {
        var value = new RendererPerfMetric(1, "count", "synthetic seam facts", RendererPerfMetricStatus.Available, null);
        var metrics = System.Collections.Immutable.ImmutableDictionary<string, RendererPerfMetric>.Empty
            .Add("target.0.owner", value).Add("target.0.width", value with { Value = 8, Unit = "pixels" })
            .Add("target.0.height", value with { Value = 4, Unit = "pixels" }).Add("target.0.samples", value with { Value = 4, Unit = "samples" })
            .Add("target.0.colour0", value with { Value = (int)GpuPixelFormat.R16G16B16A16Float, Unit = "format-id" })
            .Add("logical.pass.post.tonemap.0.CopyBytes", value with { Value = 0, Unit = "bytes" })
            .Add("logical.pass.post.tonemap.0.ResolveReadBytes", value with { Value = 0, Unit = "bytes" })
            .Add("logical.pass.post.tonemap.0.ResolveWriteBytes", value with { Value = 0, Unit = "bytes" })
            .Add("logical.pass.post.tonemap.0.UnknownCopyBytes", value with { Value = 0 })
            .Add("dispatch.0.owner", value).Add("dispatch.0.local0", value with { Value = 256, Unit = "threads" });
        var sample = new RendererPerfFrameSample(0, 0, 0, 1, 1, null, null, value,
            [new("post.tonemap", 0, null, null, 1, 2, null, value, false)], [], metrics, []);
        var model = Assert.Single(RendererPerfWorkModels.ForFrame(sample, "synthetic recipe"));
        Assert.Equal(1024, model.Components["target.0.payloadBytes"].Minimum);
        Assert.Equal(4, model.Components["target.0.samples"].Minimum);
        Assert.Equal(256, model.Components["dispatch.0.local0"].Minimum);
        Assert.Equal(0, model.Components["readBytes"].Minimum);
        Assert.Null(model.Components["readBytes"].Maximum);
    }

    [Fact]
    public void Serial_dependencies_name_actual_registry_keys()
    {
        var model = Assert.Single(RendererPerfWorkModels.ForRegistry([new("ocean.mip-copy", 0, -1, 1, 2)], "synthetic recipe"));
        Assert.Equal(new[] { "ocean.column" }, model.SerialDependencies);
    }
}
