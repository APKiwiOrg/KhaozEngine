using System;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfConstructionTests
{
    [Fact]
    public void Actual_descriptor_byte_exhaustion_keeps_forwarding_and_bounded_partial_tables()
    {
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation()); capture.BeginOperation(0);
        var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
        var elements = Enumerable.Repeat(new GpuResourceLayoutElement(new string('x', 1024), GpuResourceKind.Sampler, GpuShaderStages.Fragment), 128).ToArray();
        for (int n = 0; n < 126; n++) device.Factory.CreateResourceLayout(new(elements)).Dispose();
        var last = elements.Take(49).Append(new GpuResourceLayoutElement(new string('x', 639), GpuResourceKind.Sampler, GpuShaderStages.Fragment)).ToArray();
        device.Factory.CreateResourceLayout(new(last)).Dispose();
        capture.EndOperation(); var i = capture.Freeze();
        Assert.False(i.Complete); Assert.Contains("descriptor byte", i.Reason!, StringComparison.Ordinal);
        Assert.Equal(126, i.Layouts.Length); Assert.Equal(127, native.FactoryAttempts); Assert.Equal(1, i.Counts.OmittedFactoryEvents);
        _output.WriteLine("descriptor bytes before=16725744 requested=51473 oneOver=16777217 limit=16777216 retained=126 forwarded=127 omitted=1");
    }

    [Fact]
    public void Actual_repeated_witness_bytes_are_charged_before_copy_and_keep_first_capacity_cause()
    {
        using var native = new RendererPerfConstructionFixture { Flags = new string('x', 16384) };
        var manifest = OneOperation() with
        {
            Blocks = [new("a", RendererPerfLayout.TotalOnly, 0, 0, 1),
            new("b", RendererPerfLayout.TotalOnly, 0, 0, 1), new("c", RendererPerfLayout.TotalOnly, 0, 0, 1)]
        };
        var capture = new RendererPerfConstructionCapture(manifest);
        for (int op = 0; op < 4; op++)
        {
            capture.BeginOperation(op);
            if (capture.Failure is null)
            {
                var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
                using var shader = device.Factory.CreateComputeShaderFromSpirv(ComputeSource);
                for (int n = 0; n < 256 && capture.Failure is null; n++) device.Factory.CreateComputePipeline(new(shader)).Dispose();
            }
            capture.EndOperation();
        }
        var i = capture.Freeze(); Assert.False(i.Complete); Assert.Contains("event/occurrence/witness byte", i.Reason!, StringComparison.Ordinal);
        Assert.Equal(native.ComputeAttempts - 1, i.Occurrences.Length); Assert.Equal(1, i.Counts.OmittedFactoryEvents);
        Assert.Equal(native.ComputeAttempts, i.Counts.SuccessfulCreations.Compute);
        Assert.Equal(1, i.Counts.ReplayEligibleRecipes);
        _output.WriteLine("witness bytes actual occurrences={0} forwardedPipelines={1} omitted=1 limit=16777216", i.Occurrences.Length, native.ComputeAttempts);
    }
}
