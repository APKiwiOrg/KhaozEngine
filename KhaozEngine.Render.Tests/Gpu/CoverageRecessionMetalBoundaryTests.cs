using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageRecessionMetalBoundaryTests
{
    [Theory]
    [InlineData("setup.writer", "mesh.reader", false, "draw")]
    [InlineData("unrelated.A", "unrelated.B", true, "draw")]
    [InlineData("setup.writer", "mesh.reader", false, "failed-draw")]
    [InlineData("setup.writer", "transfer.reader", false, "copy")]
    [InlineData("setup.writer", "target.reader", false, "rebind")]
    [InlineData("setup.writer", "mesh.reader", false, "resolve-then-draw")]
    [InlineData("setup.writer", "unused.reader", false, "end")]
    public void Later_native_consumption_reports_clears_from_prior_logical_scopes(string writer, string reader, bool overwrite, string consumer)
    {
        using var harness = new MetalRingHarness();
        var render = new FakeMetalRenderCalls();
        var counter = new MetalTimestampTestNative { Raw = Enumerable.Range(0, 16).Select(i => 150ul + (ulong)i * 10).ToArray() };
        using var native = harness.NewList(new object(), render: render, timestampTimeline: harness.Timeline);
        using var commands = new CommandTallyGpuCommandList(native);
        using var target = new RecordedFramebuffer(depth: true);
        using var replacementTarget = new RecordedFramebuffer(depth: true);
        using var copySource = harness.NewTexture(GpuTextureDescription.Texture2D(4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using var copyTarget = harness.NewTexture(GpuTextureDescription.Texture2D(4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        using var pipeline = Pipeline(harness, target.Outputs);
        using var pool = new MetalTimestampPool(new MetalTimestampApi(counter), harness.Timeline, harness.Liveness, 16);
        var timer = new CoverageRecessionPassTiming(new Dictionary<IGpuComputePipeline, int>());
        timer.AttachCommands(commands);
        var source = new RenderPassKey(writer);
        var replacement = new RenderPassKey("replacement", 7);
        var depth = new RenderPassKey("depth.writer");
        var draw = new RenderPassKey(reader);
        var group = new RenderPassKey("parent");
        var before = new RenderPassKey("intervening.work");

        native.Begin(); commands.SetFramebuffer(target); commands.SetPipeline(pipeline);
        timer.Discover(); Record(); timer.Complete(); native.End(); timer.CompleteNativeEnd(native); timer.Freeze(1);
        native.Begin(); commands.SetFramebuffer(target); commands.SetPipeline(pipeline);
        native.BeginTimestamps(pool, timer.MarkerCount); native.WriteTimestamp(pool, 0);
        timer.BeginFrame(native, pool, 0); Record(); timer.Complete();
        native.FinishTimestampsOnEnd(pool, timer.MarkerCount - 1); native.End(); timer.CompleteNativeEnd(native);
        using var fence = harness.Timeline.CreateFence(); fence.Arm(1); harness.Event.Completed = 1;
        var ticks = new ulong[timer.MarkerCount];
        Assert.Equal(GpuTimestampStatus.Ready, pool.TryResolve(fence, ticks, out double period));
        var samples = timer.Resolve(0, ticks, period);
        var consumed = consumer == "end" ? timer.UnscopedDeferredClears(0) : Assert.Single(samples, p => p.Pass == draw).AbsorbedDeferredClears;
        var expectedConsumer = consumer == "end" ? new RenderPassKey("frame.finalization-or-unscoped") : draw;
        Assert.Equal(2, consumed.Length);
        Assert.Contains(consumed, c => c.Source == (overwrite ? replacement : source) && c.Attachment == "colour[0]" && c.ConsumedBy == expectedConsumer);
        Assert.Contains(consumed, c => c.Source == depth && c.Attachment == "depth/stencil" && c.ConsumedBy == expectedConsumer);
        var colour = Assert.Single(consumed, c => c.Attachment == "colour[0]");
        Assert.Equal(Assert.Single(samples, p => p.Pass == (overwrite ? replacement : source)).BeginMarker, colour.SourceBeginMarker);
        Assert.Equal(consumer == "end" ? 0 : 2, Assert.Single(samples, p => p.Pass == group).AbsorbedDeferredClears.Length);
        Assert.All(samples, p => Assert.True(p.MetalClearAttributionAvailable));
        Assert.Empty(Assert.Single(samples, p => p.Pass == source).AbsorbedDeferredClears);
        if (consumer is "failed-draw" or "resolve-then-draw")
            Assert.Empty(Assert.Single(samples, p => p.Pass == before).AbsorbedDeferredClears);

        void Record()
        {
            timer.BeginPass(commands, group);
            timer.BeginPass(commands, source); commands.ClearColorTarget(0, Color.Black); timer.EndPass(commands, source);
            if (overwrite)
            {
                timer.BeginPass(commands, replacement); commands.ClearColorTarget(0, Color.White); timer.EndPass(commands, replacement);
            }
            timer.BeginPass(commands, depth); commands.ClearDepthStencil(1); timer.EndPass(commands, depth);
            Assert.True(native.Passes.HasPendingClears);
            if (consumer is "failed-draw" or "resolve-then-draw")
            {
                timer.BeginPass(commands, before);
                if (consumer == "failed-draw") { render.NextCreateFails = true; commands.Draw(3); }
                else native.StandaloneResolvePass(new IntPtr(1), new IntPtr(2));
                timer.EndPass(commands, before);
                Assert.True(native.Passes.HasPendingClears);
            }
            timer.BeginPass(commands, draw);
            if (consumer == "copy") commands.CopyTexture(copySource, copyTarget);
            else if (consumer == "rebind") commands.SetFramebuffer(replacementTarget);
            else if (consumer != "end") commands.Draw(3);
            timer.EndPass(commands, draw);
            Assert.Equal(consumer == "end", native.Passes.HasPendingClears);
            timer.EndPass(commands, group);
        }
    }

    internal static MetalGraphicsPipeline Pipeline(MetalRingHarness harness, GpuOutputDescription outputs)
    {
        var table = MetalShaderIndexTable.Build([], [], "clear-attribution");
        var shaders = new MetalShaderSet(harness.Liveness,
            [new(MetalShaderStage.Vertex, default, default), new(MetalShaderStage.Fragment, default, default)], table);
        var description = new GpuPipelineDescription
        {
            ShaderSet = shaders,
            ResourceLayouts = [],
            VertexLayouts = [],
            Outputs = outputs,
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
            Topology = GpuPrimitiveTopology.TriangleList,
        };
        return new(harness.Liveness, MetalGraphicsPipelinePlan.Build(harness.Liveness, description), default, default);
    }
}
