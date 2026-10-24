using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfConstructionTests
{
    [Theory]
    [InlineData("source")]
    [InlineData("name")]
    [InlineData("layout-elements")]
    [InlineData("layout-sets")]
    [InlineData("vertex-streams")]
    [InlineData("vertex-elements")]
    [InlineData("blend-attachments")]
    [InlineData("colour-attachments")]
    public void One_over_each_descriptor_limit_forwards_and_refuses_before_retention(string limit)
    {
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation()); capture.BeginOperation(0);
        var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
        if (limit == "source") device.Factory.CreateComputeShaderFromSpirv(new string('x', 1048577)).Dispose();
        else if (limit is "name" or "layout-elements")
            device.Factory.CreateResourceLayout(new(limit == "name" ? [new(new string('x', 1025), GpuResourceKind.Sampler, GpuShaderStages.Fragment)]
                : Enumerable.Repeat(new GpuResourceLayoutElement("x", GpuResourceKind.Sampler, GpuShaderStages.Fragment), 129).ToArray())).Dispose();
        else
        {
            using var shader = device.Factory.CreateShadersFromSpirv("vertex", "fragment");
            using var layout = device.Factory.CreateResourceLayout(new([]));
            var d = new GpuPipelineDescription { ShaderSet = shader, ResourceLayouts = [], VertexLayouts = [], BlendAttachments = [], Outputs = new(null) };
            switch (limit)
            {
                case "layout-sets": d.ResourceLayouts = Enumerable.Repeat(layout, 33).ToArray(); break;
                case "vertex-streams": d.VertexLayouts = Enumerable.Repeat(new GpuVertexLayoutDescription([]), 33).ToList(); break;
                case "vertex-elements": d.VertexLayouts = [new(Enumerable.Repeat(new GpuVertexElement("x", GpuVertexElementFormat.Float1), 65).ToArray())]; break;
                case "blend-attachments": d.BlendAttachments = Enumerable.Repeat(GpuBlendAttachment.OverrideBlend, 9).ToArray(); break;
                case "colour-attachments": d.Outputs = new(null, Enumerable.Repeat(GpuPixelFormat.R8UNorm, 9).ToArray()); break;
            }
            device.Factory.CreateGraphicsPipeline(d).Dispose();
        }
        capture.EndOperation(); var i = capture.Freeze();
        Assert.False(i.Complete); Assert.Contains("capacity", i.Reason!, StringComparison.Ordinal);
        Assert.Equal(native.FactoryAttempts, i.Counts.FactoryAttempts); Assert.Equal(1, i.Counts.OmittedFactoryEvents);
        Assert.Empty(i.Occurrences); _output.WriteLine("one-over {0} forwarded={1} omitted=1", limit, native.FactoryAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void One_over_operation_factory_and_pipeline_envelopes_keeps_emergency_counts(bool pipelines)
    {
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation()); capture.BeginOperation(0);
        var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
        using var shader = pipelines ? device.Factory.CreateComputeShaderFromSpirv(ComputeSource) : null;
        int count = pipelines ? 257 : 1025;
        for (int n = 0; n < count; n++)
        {
            if (pipelines) device.Factory.CreateComputePipeline(new(shader!)).Dispose();
            else device.Factory.CreateResourceLayout(new([])).Dispose();
        }
        capture.EndOperation(); var i = capture.Freeze();
        Assert.False(i.Complete); Assert.Equal(pipelines ? 258 : 1025, native.FactoryAttempts);
        Assert.Equal(native.FactoryAttempts, i.Counts.FactoryAttempts); Assert.Equal(1, i.Counts.OmittedFactoryEvents);
        Assert.Equal(pipelines ? 256 : 1024, pipelines ? i.Occurrences.Length : i.Layouts.Length);
        Assert.Equal(pipelines ? 257 : 0, i.Counts.SuccessfulCreations.Compute);
    }

    [Fact]
    public void Operation_and_unique_recipe_limits_refuse_without_hash_ordering_or_silent_pruning()
    {
        var full = Manifest("full");
        Assert.ThrowsAny<ArgumentException>(() => new RendererPerfConstructionCapture(full with { Blocks = full.Blocks.Add(full.Blocks[0]) }));
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(full);
        // Five operations reach the unique cap without exceeding either operation envelope.
        for (int op = 0; op < 5; op++)
        {
            capture.BeginOperation(op); var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
            for (int n = 0; n < (op == 4 ? 1 : 256); n++) Compute(device, ComputeSource + "\n// role " + (op * 256 + n)).Dispose();
            capture.EndOperation();
        }
        for (int op = 5; op < 43; op++) { capture.BeginOperation(op); capture.EndOperation(); }
        var i = capture.Freeze(); Assert.False(i.Complete); Assert.Contains("unique recipe", i.Reason!, StringComparison.Ordinal);
        Assert.Equal(1024, i.Recipes.Length); Assert.Equal(1024, i.Occurrences.Length); Assert.Equal(1025, i.Counts.SuccessfulCreations.Compute);
        Assert.Equal(1, i.Counts.OmittedFactoryEvents); Assert.Equal(2050, i.Counts.FactoryAttempts);
        Assert.Equal(1023, i.Occurrences[^1].RecipeId); Assert.Equal(255, i.Occurrences[^1].CreationOrdinal);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("descriptor")]
    [InlineData("evidence")]
    public void Logical_byte_admission_is_checked_atomic_and_exact_at_each_partition(string partition)
    {
        var admission = new RendererPerfConstructionAdmission();
        long s = partition == "source" ? 33554432 : 0, d = partition == "descriptor" ? 16777216 : 0, e = partition == "evidence" ? 16777216 : 0;
        admission.Reserve(s, d, e);
        Assert.ThrowsAny<ArgumentException>(() => admission.Reserve(s == 0 ? 0 : 1, d == 0 ? 0 : 1, e == 0 ? 0 : 1));
        // A rejected reservation cannot consume another partition.
        admission.Reserve(33554432 - s, 16777216 - d, 16777216 - e);
        Assert.Throws<OverflowException>(() => admission.Reserve(long.MaxValue, 0, 0));
        _output.WriteLine("exact partition {0} source=33554432 descriptor=16777216 evidence=16777216 total=67108864", partition);
    }

    [Fact]
    public void Source_byte_exhaustion_keeps_first_cause_and_forwards_the_next_native_failure()
    {
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation()); capture.BeginOperation(0);
        var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
        var source = new string('x', 1048576);
        for (int n = 0; n < 31; n++) device.Factory.CreateComputeShaderFromSpirv(source).Dispose();
        device.Factory.CreateComputeShaderFromSpirv(new string('x', 1048445)).Dispose();
        Assert.Null(capture.Failure);
        // 33554429 retained bytes plus the empty source's four-byte length is one over the partition.
        device.Factory.CreateComputeShaderFromSpirv("").Dispose();
        var first = capture.Failure; Assert.Contains("source byte", first!, StringComparison.Ordinal);
        var original = new InvalidOperationException("native failure after observer refusal"); native.FactoryFailure = original;
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => device.Factory.CreateComputeShaderFromSpirv("x")));
        Assert.Equal(first, capture.Failure);
        capture.EndOperation(); var i = capture.Freeze();
        Assert.Equal(34, native.FactoryAttempts); Assert.Equal(34, i.Counts.FactoryAttempts); Assert.Equal(2, i.Counts.OmittedFactoryEvents);
        Assert.Equal(32, i.Shaders.Length);
        _output.WriteLine("source bytes before=33554429 requested=4 oneOver=33554433 limit=33554432 forwarded=34 omitted=2");
    }
}
