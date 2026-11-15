using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class MetalDrawPathTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANullPipelineBindIsRefusedBeforeSpendingAnything(bool compute)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1808
        var (list, calls, render, computeCalls) = NewList();
        using var graphics = Pipeline();
        using var kernel = new MetalComputePipeline(_harness.Liveness,
            new MetalComputeShader(_harness.Liveness, default,
                MetalShaderIndexTable.Build([], [], "hand-built"), 1, 1, 1), [], default);

        list.Begin();
        list.SetFramebuffer(new RecordedFramebuffer());
        if (compute) list.SetComputePipeline(kernel);
        else list.SetPipeline(graphics);

        Action bindNull = compute ? () => list.SetComputePipeline(null!) : () => list.SetPipeline(null!);
        var refusal = Assert.Throws<ArgumentNullException>(bindNull);
        Assert.Equal("p", refusal.ParamName);
        Assert.Empty(calls.Log);
        Assert.Empty(render.Log);
        Assert.Empty(computeCalls.States);
        Assert.Equal(MetalEncoderKind.None, list.Encoders.Open);

        if (compute)
        {
            Assert.Same(kernel, list.Pipelines.Compute);
            list.Dispatch(1, 1, 1);
            Assert.Equal(MetalEncoderKind.Compute, list.Encoders.Open);
        }
        else
        {
            Assert.Same(graphics, list.Pipelines.Graphics);
            list.Draw(3);
            Assert.Single(render.Passes);
        }
        list.End();
    }

    /// <summary>
    /// A draw with no graphics pipeline is refused before opening a pass or consuming pending clears, so
    /// the mis-sequenced frame does not also pay a boundary and leave a pass open behind the throw.
    /// </summary>
    [Theory]
    [InlineData("Draw")]
    [InlineData("DrawSingle")]
    [InlineData("DrawIndexed")]
    public void ADrawWithNoGraphicsPipelineIsRefusedBeforeThePassOpens(string command)
    {
        (MetalCommandList list, FakeMetalEncoderCalls calls, FakeMetalRenderCalls render, _) = NewList();

        list.Begin();
        list.SetFramebuffer(new RecordedFramebuffer());

        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1690
        list.SetIndexBuffer(_harness.NewBuffer(64, GpuBufferUsage.IndexBuffer), GpuIndexFormat.UInt16);
        Color owed = new(0.2f, 0.4f, 0.6f, 1);
        list.ClearColorTarget(0, owed);
        Action issue = command switch
        {
            "DrawSingle" => () => list.Draw(3),
            "DrawIndexed" => () => list.DrawIndexed(3, 2, 0, 0, 0),
            _ => () => list.Draw(3, 2, 0, 0),
        };
        var refusal = Assert.Throws<GpuRecordingStateException>(issue);
        Assert.Equal(GpuRecordingStateRefusal.GraphicsPipelineNotBound, refusal.Reason);
        Assert.Equal(GpuBackendKind.MetalNative, refusal.Backend);
        Assert.Equal(command == "DrawIndexed" ? "DrawIndexed" : "Draw", refusal.Command);

        Assert.Empty(render.Passes);
        Assert.Equal(0, calls.EncoderBoundaries);
        Assert.Equal(MetalEncoderKind.None, list.Encoders.Open);

        list.SetPipeline(Pipeline());
        issue();
        Assert.Single(render.Passes);
        Assert.Equal(owed, render.ClearOn(0));
        list.End();

        list.Begin();
        list.SetFramebuffer(new RecordedFramebuffer());
        list.SetIndexBuffer(_harness.NewBuffer(64, GpuBufferUsage.IndexBuffer), GpuIndexFormat.UInt16);
        var next = Assert.Throws<GpuRecordingStateException>(issue);
        Assert.Equal(refusal.Reason, next.Reason);
        Assert.Equal(refusal.Backend, next.Backend);
        Assert.Equal(refusal.Command, next.Command);
        list.End();
    }

    /// <summary>
    /// Drawing and dispatching outside a recording retain their existing command identifiers.
    /// </summary>
    [Fact]
    public void TheThreeCommandsRefuseAListThatIsNotRecording()
    {
        (MetalCommandList list, FakeMetalEncoderCalls calls, FakeMetalRenderCalls render, _) = NewList();

        var draw = Assert.Throws<GpuRecordingStateException>(() => list.Draw(3));
        Assert.Equal(GpuRecordingStateRefusal.CommandOutsideRecording, draw.Reason);
        Assert.Equal(GpuBackendKind.MetalNative, draw.Backend);
        Assert.Equal("Drawing", draw.Command);
        var instanced = Assert.Throws<GpuRecordingStateException>(() => list.Draw(3, 1, 0, 0));
        Assert.Equal(GpuRecordingStateRefusal.CommandOutsideRecording, instanced.Reason);
        Assert.Equal(GpuBackendKind.MetalNative, instanced.Backend);
        Assert.Equal("Drawing", instanced.Command);
        var indexed = Assert.Throws<GpuRecordingStateException>(() => list.DrawIndexed(3, 1, 0, 0, 0));
        Assert.Equal(GpuRecordingStateRefusal.CommandOutsideRecording, indexed.Reason);
        Assert.Equal(GpuBackendKind.MetalNative, indexed.Backend);
        Assert.Equal("Drawing indexed", indexed.Command);
        var compute = Assert.Throws<GpuRecordingStateException>(() => list.Dispatch(1, 1, 1));
        Assert.Equal(GpuRecordingStateRefusal.CommandOutsideRecording, compute.Reason);
        Assert.Equal(GpuBackendKind.MetalNative, compute.Backend);
        Assert.Equal("Dispatching compute work", compute.Command);

        Assert.Empty(calls.Log);
        Assert.Empty(render.Log);
    }

}
