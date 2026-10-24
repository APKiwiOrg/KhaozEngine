using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("AllocSensitive")]
public sealed class RendererPerfCounterTests
{
    static RendererPerfRecorder Recorder(RendererPerfPassDefinition[]? passes = null, Func<long>? now = null,
        GpuPerfTimestampRing? ring = null, bool enabled = true, Func<long?>? native = null)
        => new(4, RendererPerfLayout.PerPass, passes ?? [], now ?? (() => 0), 1000, ring, enabled, native);

    [Fact]
    public void Counts_each_forwarded_update_once()
    {
        using var native = new TestDevice();
        var recorder = Recorder();
        var device = RendererPerfDevice.Wrap(native, recorder);
        var commands = device.Factory.CreateCommandList();
        var buffer = device.Factory.CreateBuffer(new(128, GpuBufferUsage.UniformBuffer));
        var texture = device.Factory.CreateTexture(new(8, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled, mipLevels: 2, arrayLayers: 2));
        int value = 3;
        int[] values = [1, 2, 3];
        byte[] pixels = new byte[256];
        device.UpdateBuffer(buffer, 0, in value);
        device.UpdateTexture(texture, pixels, 0, 0, 8, 4);
        recorder.BeginFrame(0, 41, 7);
        commands.Begin();
        native.ForwardUpdates = commands;
        device.UpdateBuffer(buffer, 0, in value);
        device.UpdateBuffer(buffer, 4, values);
        device.UpdateBuffer<int>(buffer, 16, values.AsSpan());
        commands.UpdateBuffer(buffer, 28, in value);
        commands.UpdateBuffer<int>(buffer, 32, values.AsSpan());
        device.UpdateTexture(texture, pixels, 0, 0, 8, 4);
        device.UpdateTexture(texture, pixels, 0, 0, 4, 2, 1, 1);
        commands.End();
        recorder.EndFrame();
        Assert.Equal(44, recorder.ReadCounter(0, RendererPerfCounter.BufferUploadBytes));
        Assert.Equal(5, recorder.ReadCounter(0, RendererPerfCounter.BufferUpdates));
        Assert.Equal(3, recorder.ReadCounter(0, RendererPerfCounter.DeviceBufferUpdates));
        Assert.Equal(2, recorder.ReadCounter(0, RendererPerfCounter.CommandBufferUpdates));
        Assert.Equal(160, recorder.ReadCounter(0, RendererPerfCounter.TextureUploadBytes));
        Assert.Equal(4, recorder.ReadCounter(-1, RendererPerfCounter.BufferUploadBytes));
        Assert.Equal(128, recorder.ReadCounter(-1, RendererPerfCounter.TextureUploadBytes));
        Assert.Equal(320, recorder.ReadCounter(-1, RendererPerfCounter.RequestedTextureBytes));
        Assert.Equal(5, native.Commands.Uploads);
        Assert.Equal(4, native.BufferUploads);
        var sample = recorder.Harvest(0);
        Assert.Equal(41, sample.FrameId);
        Assert.Equal(7, sample.SequenceIndex);
        Assert.Equal(44, sample.Metrics["logical.total.BufferUploadBytes"].Value);
    }

    [Fact]
    public void Owns_every_draw_dispatch_copy_clear_and_bind()
    {
        long tick = 0;
        var recorder = Recorder([new("opaque", 0, -1, 1, 4), new("skin", 0, 0, 2, 3), new("cached", 0, -1, 5, 6)], () => tick);
        using var native = new TestDevice();
        var device = RendererPerfDevice.Wrap(native, recorder);
        var commands = device.Factory.CreateCommandList();
        var buffer = device.Factory.CreateBuffer(new(128, GpuBufferUsage.VertexBuffer));
        var texture = device.Factory.CreateTexture(new(8, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled, mipLevels: 2, arrayLayers: 2));
        var target = device.Factory.CreateTexture(new(8, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
        var msaa = device.Factory.CreateTexture(new(8, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget, sampleCount: 4));
        var fb = device.Factory.CreateFramebuffer(null, target);
        var pipeline = device.Factory.CreateGraphicsPipeline(new() { Topology = GpuPrimitiveTopology.TriangleList });
        var compute = device.Factory.CreateComputePipeline(new(new TestComputeShader()));
        var set = device.Factory.CreateResourceSet(default);
        recorder.BeginFrame(0, 0, 0);
        commands.Begin();
        commands.SetFramebuffer(fb); commands.SetFramebuffer(fb);
        commands.ClearColorTarget(0, Color.Black); commands.ClearDepthStencil(1);
        tick = 10;
        recorder.BeginCpuPhase("prepare");
        tick = 20;
        recorder.BeginCpuPhase("packing");
        tick = 30;
        recorder.EndCpuPhase("packing");
        tick = 40;
        recorder.EndCpuPhase("prepare");
        recorder.BeginPass(commands, 0);
        commands.SetPipeline(pipeline); commands.SetPipeline(pipeline);
        commands.SetGraphicsResourceSet(0, set); commands.SetGraphicsResourceSet(0, set, 0);
        commands.SetGraphicsResourceSet(1, set, 0); commands.SetGraphicsResourceSet(0, set, 16);
        commands.SetVertexBuffer(0, buffer); commands.SetVertexBuffer(0, buffer, 0);
        commands.SetVertexBuffer(0, buffer, 4);
        commands.SetIndexBuffer(buffer, GpuIndexFormat.UInt16); commands.SetIndexBuffer(buffer, GpuIndexFormat.UInt16);
        commands.SetScissorRect(0, 1, 2, 3, 4); commands.SetScissorRect(0, 1, 2, 3, 4);
        commands.SetFullScissorRects(); commands.SetFullScissorRects();
        commands.Draw(6, 2, 3, 4); commands.Draw(3);
        recorder.BeginPass(commands, 1);
        commands.DrawIndexed(12, 3, 1, 2, 3);
        commands.SetComputePipeline(compute); commands.SetComputePipeline(compute);
        commands.SetComputeResourceSet(0, set); commands.SetComputeResourceSet(0, set, 0);
        commands.Dispatch(2, 3, 4);
        recorder.EndPass(commands, 1);
        commands.CopyBuffer(buffer, 0, buffer, 64, 16);
        commands.CopyTexture(texture, texture);
        commands.CopyTextureSubresource(texture, 1, 1, target, 4, 2);
        commands.CopyTextureSubresource(texture, 1, 1, target, 0, 0, 4, 2);
        commands.ResolveTexture(msaa, target);
        commands.GenerateMipmaps(texture);
        recorder.EndPass(commands, 0);
        recorder.SkipPass(commands, 2);
        tick = 100;
        commands.End(); recorder.EndFrame();
        Assert.Equal(2, recorder.ReadCounter(0, RendererPerfCounter.Draws));
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.IndexedDraws));
        Assert.Equal(15, recorder.ReadCounter(0, RendererPerfCounter.Vertices));
        Assert.Equal(36, recorder.ReadCounter(0, RendererPerfCounter.Indices));
        Assert.Equal(17, recorder.ReadCounter(0, RendererPerfCounter.Triangles));
        Assert.Equal(24, recorder.ReadCounter(0, RendererPerfCounter.DispatchGroups));
        Assert.Equal(384, recorder.ReadCounter(0, RendererPerfCounter.DispatchInvocations));
        Assert.Equal(400, recorder.ReadCounter(0, RendererPerfCounter.CopyBytes));
        Assert.Equal(512, recorder.ReadCounter(0, RendererPerfCounter.ResolveReadBytes));
        Assert.Equal(128, recorder.ReadCounter(0, RendererPerfCounter.ResolveWriteBytes));
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.ColorClears, 0));
        Assert.Equal(0, recorder.ReadCounter(0, RendererPerfCounter.IndexedDraws, 1));
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.IndexedDraws, 2));
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.RedundantGraphicsResources));
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.RedundantVertexBuffers));
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.RedundantFramebuffers));
        (RendererPerfCounter Counter, long Expected)[] expectedCommands =
        [
            (RendererPerfCounter.FramebufferBinds, 2), (RendererPerfCounter.ColorClears, 1), (RendererPerfCounter.DepthClears, 1),
            (RendererPerfCounter.GraphicsPipelineBinds, 2), (RendererPerfCounter.RedundantGraphicsPipelines, 1),
            (RendererPerfCounter.ComputePipelineBinds, 2), (RendererPerfCounter.RedundantComputePipelines, 1),
            (RendererPerfCounter.GraphicsResources, 4), (RendererPerfCounter.ComputeResources, 2),
            (RendererPerfCounter.RedundantComputeResources, 1), (RendererPerfCounter.VertexBuffers, 3),
            (RendererPerfCounter.IndexBuffers, 2), (RendererPerfCounter.RedundantIndexBuffers, 1),
            (RendererPerfCounter.Scissors, 2), (RendererPerfCounter.RedundantScissors, 1),
            (RendererPerfCounter.FullScissors, 2), (RendererPerfCounter.RedundantFullScissors, 1),
            (RendererPerfCounter.BufferCopies, 1), (RendererPerfCounter.TextureCopies, 1),
            (RendererPerfCounter.TextureSubresourceCopies, 2), (RendererPerfCounter.Resolves, 1),
            (RendererPerfCounter.MipGenerations, 1), (RendererPerfCounter.Dispatches, 1)
        ];
        foreach (var expected in expectedCommands) Assert.Equal(expected.Expected, recorder.ReadCounter(0, expected.Counter));
        foreach (var counter in Enum.GetValues<RendererPerfCounter>())
            Assert.Equal(recorder.ReadCounter(0, counter), Enumerable.Range(0, 4).Sum(owner => recorder.ReadCounter(0, counter, owner)));
        var sample = recorder.Harvest(0);
        Assert.Equal(100, sample.CpuPhases.Sum(span => span.EndTicks - span.StartTicks));
        Assert.Equal(new[] { "cpu.frameWall", "cpu.frameWall", "prepare", "cpu.frameWall", "cpu.frameWall" }, sample.CpuPhases.Select(span => span.ParentId));
        Assert.Equal(new[] { "cpu.unattributed", "prepare", "packing", "prepare", "cpu.unattributed" }, sample.CpuPhases.Select(span => span.Id));
        Assert.Equal(8, sample.Metrics["dispatch.0.local0"].Value);
        Assert.Equal(2, sample.Metrics["dispatch.0.local1"].Value);
        Assert.Equal(1, sample.Metrics["dispatch.0.local2"].Value);
        Assert.Equal(8, sample.Metrics["target.0.width"].Value);
        Assert.Equal(4, sample.Metrics["target.0.height"].Value);
        Assert.Equal("R8G8B8A8UNorm", sample.Metrics["target.0.colour0"].Source);
        Assert.Equal(RendererPerfMetricStatus.NotExecuted, sample.Passes[2].Duration.Status);
        Assert.Null(sample.Metrics["native.stateCalls"].Value);
        Assert.Equal(RendererPerfMetricStatus.Unsupported, sample.Metrics["native.stateCalls"].Status);
        Assert.Contains("current-thread", sample.Metrics["allocation.currentThread.bytes"].Source, StringComparison.Ordinal);
        Assert.Contains("process", sample.Metrics["allocation.process.bytes"].Source, StringComparison.Ordinal);
        Assert.Contains("approximate", sample.Metrics["allocation.process.bytes"].Source, StringComparison.Ordinal);
    }

    [Fact]
    public void Preserves_optional_interfaces_and_disabled_sequence()
    {
        using var native = new TestDevice();
        var disabled = Recorder(enabled: false);
        Assert.Same(native, RendererPerfDevice.Wrap(native, disabled));
        Assert.Same(native.Commands, RendererPerfCommandList.Wrap(native.Commands, disabled));
        var bytes = new byte[4];
        var buffer = new FakeBuffer(16);
        Action nativeWork = () => { native.Commands.Begin(); native.Commands.UpdateBuffer<byte>(buffer, 0, bytes); native.Commands.Draw(3); native.Commands.End(); };
        nativeWork();
        int[] expected = native.Commands.Events[..native.Commands.EventCount];
        native.Commands.EventCount = 0;
        var direct = RendererPerfCommandList.Wrap(native.Commands, disabled);
        Action disabledWork = () => { direct.Begin(); direct.UpdateBuffer<byte>(buffer, 0, bytes); direct.Draw(3); direct.End(); };
        // Warm the exact interface path, including the runtime's generic virtual dispatch cache.
        disabledWork();
        native.Commands.EventCount = 0;
        AllocationMeasurement.NoPerCallAllocation("disabled renderer forwarding", disabledWork);
        Assert.Equal(expected, native.Commands.Events[..native.Commands.EventCount]);
        using var ring = new GpuPerfTimestampRing(native, 1, 2, (_, result) => Assert.True(result.Valid));
        var recorder = Recorder(ring: ring);
        var device = RendererPerfDevice.Wrap(native, recorder);
        using var submission = new GpuFrameSubmission(device);
        var commands = device.Factory.CreateCommandList();
        Assert.IsAssignableFrom<IGpuSwapchainCapture>(device);
        Assert.IsAssignableFrom<IGpuFencedReadback>(device);
        Assert.IsAssignableFrom<IGpuResourceMemoryDiagnostics>(device);
        Assert.IsAssignableFrom<IGpuDeviceLifecycle>(device);
        Assert.IsAssignableFrom<ID3D11RemovedReason>(device);
        Assert.IsAssignableFrom<IGpuCommandListTimestampEnd>(commands);
        Assert.IsAssignableFrom<IVulkanRenderingScope>(commands);
        Assert.IsAssignableFrom<ID3D11SwapchainBindTracking>(commands);
        native.Commands.EventCount = 0;
        submission.Begin(); recorder.BeginFrame(0, 0, 0, submission);
        commands.Begin(); commands.ClearColorTarget(0, Color.Black); commands.End();
        submission.Submit(commands); device.Present(); recorder.EndFrame();
        device.WaitForIdle(); ring.PollCompleted();
        Assert.Equal(new[] { 1, 10, 11, 2, 3, 4, 11, 12, 5 }, native.Commands.Events[..native.Commands.EventCount]);
        Assert.True(ring.CompleteBlock());
        Assert.Same(native.Commands, native.LastSubmitted);
        Assert.Equal(1, native.Pools[0].Begins);
        Assert.Equal(2U, native.Pools[0].Written);
    }

    [Fact]
    public void Exclusive_cpu_spans_and_native_deltas_are_retained_outside_the_frame()
    {
        long tick = 0, stateCalls = 7;
        var recorder = Recorder(now: () => tick, native: () => stateCalls);
        recorder.BeginFrame(0, 2, 3);
        tick = 10; recorder.BeginCpuPhase("encode");
        tick = 25; recorder.EndCpuPhase("encode");
        stateCalls = 11; tick = 40; recorder.EndFrame();
        var sample = recorder.Harvest(0);
        Assert.Equal(4, sample.Metrics["native.stateCalls"].Value);
        Assert.Equal(40, sample.CpuEndTicks - sample.CpuStartTicks);
        Assert.Equal(40, sample.CpuPhases.Sum(span => span.EndTicks - span.StartTicks));
        Assert.Empty(sample.Errors);
    }

    [Fact]
    public void Missing_scope_unknown_copy_and_capacity_fail_closed()
    {
        var recorder = Recorder([new("leaf", 0, -1, 1, 2)]);
        var commands = RendererPerfCommandList.Wrap(new TailCommands(), recorder);
        recorder.BeginFrame(0, 0, 0); commands.Begin();
        recorder.BeginPass(commands, 5);
        commands.CopyTexture(new FakeTexture(8, 4, 1, 1, GpuPixelFormat.R8UNorm), new FakeTexture(8, 4, 1, 1, GpuPixelFormat.R8UNorm));
        commands.End(); recorder.EndFrame();
        var sample = recorder.Harvest(0);
        Assert.NotEmpty(sample.Errors);
        Assert.Equal(1, recorder.ReadCounter(0, RendererPerfCounter.UnknownCopyBytes));
        Assert.Throws<InvalidOperationException>(() => recorder.BeginFrame(0, 0, 0));
    }

    [Fact]
    public void Enabled_recording_uses_only_preallocated_storage()
    {
        using var native = new TestDevice();
        var recorder = Recorder();
        var device = RendererPerfDevice.Wrap(native, recorder);
        var commands = device.Factory.CreateCommandList();
        var buffer = device.Factory.CreateBuffer(new(64, GpuBufferUsage.UniformBuffer));
        int[] values = [1, 2];
        recorder.BeginFrame(0, 0, 0); commands.Begin();
        commands.UpdateBuffer<int>(buffer, 0, values); commands.Draw(3); commands.End(); recorder.EndFrame();
        Action work = () =>
        {
            recorder.BeginFrame(1, 1, 1); commands.Begin();
            commands.UpdateBuffer<int>(buffer, 0, values); commands.Draw(3); commands.End(); recorder.EndFrame();
        };
        AllocationMeasurement.NoPerCallAllocation("enabled preallocated renderer recording", work);
        Assert.Equal(0, recorder.Harvest(1).Metrics["allocation.currentThread.bytes"].Value);
    }

    [Theory]
    [InlineData(true, 8U, 175.0)]
    [InlineData(false, 2U, 25.0)]
    public void Fixed_layout_skips_and_outer_tail_are_written_exactly_once(bool detailed, uint markers, double nanoseconds)
    {
        using var native = new TestDevice();
        GpuPerfTimestampResult? result = null;
        using var ring = new GpuPerfTimestampRing(native, 1, markers, (_, value) => result = value);
        var recorder = new RendererPerfRecorder(1, detailed ? RendererPerfLayout.PerPass : RendererPerfLayout.TotalOnly,
            [new("opaque", 0, -1, 1, 4), new("skin", 0, 0, 2, 3), new("cached", 0, -1, 5, 6)], () => 0, 1000, ring);
        var device = RendererPerfDevice.Wrap(native, recorder);
        using var submission = new GpuFrameSubmission(device);
        var commands = device.Factory.CreateCommandList();
        submission.Begin(); recorder.BeginFrame(0, 999, 131, submission); commands.Begin();
        recorder.BeginPass(commands, 0); commands.Draw(3);
        recorder.BeginPass(commands, 1); commands.DrawIndexed(6, 1, 0, 0, 0); recorder.EndPass(commands, 1);
        recorder.EndPass(commands, 0); recorder.SkipPass(commands, 2);
        commands.End(); submission.Submit(commands); recorder.EndFrame();
        native.WaitForIdle(); ring.PollCompleted();
        Assert.True(ring.CompleteBlock());
        Assert.Equal(markers, native.Pools[0].Written);
        var sample = recorder.Harvest(0, Assert.IsType<GpuPerfTimestampResult>(result));
        Assert.Equal(nanoseconds, sample.GpuFrame.Value);
        if (detailed)
        {
            Assert.Equal(75.0, sample.Passes[0].Duration.Value);
            Assert.Equal(RendererPerfMetricStatus.NotExecuted, sample.Passes[2].Duration.Status);
        }
        else Assert.Empty(sample.Passes);
        Assert.Equal(detailed ? 0 : 1, recorder.ReadCounter(0, RendererPerfCounter.Draws, 0));
        Assert.Empty(sample.Errors);
    }

    [Fact]
    public void Optional_capabilities_are_never_invented_and_forward_the_native_result()
    {
        TestDeviceBase[] profiles = [new(), new TestDevice(), new CaptureDevice(), new ReadbackDevice(), new MemoryDevice(), new LifecycleDevice(), new RemovedDevice()];
        foreach (var native in profiles)
        {
            using (native)
            {
                var device = RendererPerfDevice.Wrap(native, Recorder());
                Assert.Equal(native is IGpuSwapchainCapture, device is IGpuSwapchainCapture);
                Assert.Equal(native is IGpuFencedReadback, device is IGpuFencedReadback);
                Assert.Equal(native is IGpuResourceMemoryDiagnostics, device is IGpuResourceMemoryDiagnostics);
                Assert.Equal(native is IGpuDeviceLifecycle, device is IGpuDeviceLifecycle);
                Assert.Equal(native is ID3D11RemovedReason, device is ID3D11RemovedReason);
                if (device is ID3D11RemovedReason removed) Assert.Equal(17, removed.GetDeviceRemovedReason());
                if (device is IGpuSwapchainCapture capture)
                {
                    capture.SwapchainCopySourceRequested = true;
                    Assert.True(native.SwapchainCopySourceRequested);
                    Assert.True(capture.SwapchainCopySupported); Assert.True(capture.SwapchainCopyable);
                }
                if (device is IGpuFencedReadback readback)
                    Assert.Equal(new IntPtr(123), readback.MapForRead(new FakeTexture(4, 4, 1, 1, GpuPixelFormat.R8G8B8A8UNorm), device.Factory.CreateFence()).Data);
                if (device is IGpuResourceMemoryDiagnostics memory)
                    Assert.Equal("test memory snapshot", Assert.Throws<NotSupportedException>(() => memory.ReadMemorySnapshot()).Message);
                if (device is IGpuDeviceLifecycle lifecycle) { lifecycle.MarkDeviceDisposed(); Assert.Equal(42, native.Commands.Events[0]); }
            }
        }
        var recorder = Recorder();
        var plain = RendererPerfCommandList.Wrap(new TestCommands(), recorder);
        Assert.False(plain is IGpuCommandListTimestampEnd);
        Assert.False(plain is IVulkanRenderingScope);
        Assert.False(plain is ID3D11SwapchainBindTracking);
        var nativeCommands = new TailCommands();
        var wrapper = RendererPerfCommandList.Wrap(nativeCommands, recorder);
        Assert.True(((ID3D11SwapchainBindTracking)wrapper).BoundSwapchainFramebuffer);
        ((IVulkanRenderingScope)wrapper).EndActiveRendering();
        Assert.Equal(41, nativeCommands.Events[0]);
        nativeCommands.EventCount = 0;
        using var pool = new GpuPerfTimestampTestPool(2);
        wrapper.Begin(); wrapper.BeginTimestamps(pool, 2); wrapper.WriteTimestamp(pool, 0);
        ((IGpuCommandListTimestampEnd)wrapper).FinishTimestampsOnEnd(pool, 1); wrapper.End();
        Assert.Equal(new[] { 1, 10, 11, 3, 4, 11, 12, 5 }, nativeCommands.Events[..nativeCommands.EventCount]);
    }

    [Fact]
    public void Cpu_capacity_is_bounded_and_unknown_bytes_are_not_zero_success()
    {
        long tick = 0;
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.TotalOnly, [], () => tick, 1000, cpuSpanCapacity: 1);
        var commands = RendererPerfCommandList.Wrap(new TestCommands(), recorder);
        recorder.BeginFrame(0, 0, 0); commands.Begin();
        tick = 1; recorder.BeginCpuPhase("encode");
        commands.CopyTexture(new FakeTexture(4, 4, 1, 1, GpuPixelFormat.R8UNorm), new FakeTexture(4, 4, 1, 1, GpuPixelFormat.R8UNorm));
        tick = 2; recorder.EndCpuPhase("encode"); commands.End(); tick = 3; recorder.EndFrame();
        var sample = recorder.Harvest(0);
        Assert.Single(sample.CpuPhases);
        Assert.NotEmpty(sample.Errors);
        Assert.Equal(RendererPerfMetricStatus.ModelIncomplete, sample.Metrics["logical.total.CopyBytes"].Status);
        Assert.Null(sample.Metrics["logical.total.CopyBytes"].Value);
    }

    sealed class TestComputeShader : IGpuComputeShader
    {
        public uint ThreadGroupSizeX => 8;
        public uint ThreadGroupSizeY => 2;
        public uint ThreadGroupSizeZ => 1;
        public void Dispose() { }
    }

    class TestCommands : IGpuCommandList
    {
        internal readonly int[] Events = new int[512];
        internal int EventCount, Uploads;
        internal GpuPerfTimestampTestPool? Pool;
        internal void Event(int value) => Events[EventCount++] = value;
        public virtual void Begin() { Pool = null; Event(1); }
        public virtual void End() { Event(3); Event(4); Event(5); }
        public void SetFramebuffer(IGpuFramebuffer fb) => Event(20);
        public void ClearColorTarget(uint index, Color rgba) => Event(2);
        public void ClearDepthStencil(float depth) => Event(21);
        public void SetPipeline(IGpuPipeline p) => Event(22);
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) => Event(23);
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) => Event(24);
        public void SetVertexBuffer(uint slot, IGpuBuffer b) => Event(25);
        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes) => Event(26);
        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) => Event(27);
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) => Event(28);
        public void SetFullScissorRects() => Event(29);
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart) => Event(7);
        public void Draw(uint vertexCount) => Event(7);
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart) => Event(30);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged { Uploads++; Event(6); }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged { Uploads++; Event(6); }
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes) => Event(31);
        public void CopyTexture(IGpuTexture src, IGpuTexture dst) => Event(32);
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst, uint width, uint height) => Event(33);
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst, uint dstMipLevel, uint dstArrayLayer, uint width, uint height) => Event(34);
        public void GenerateMipmaps(IGpuTexture texture) => Event(35);
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst) => Event(36);
        public void SetComputePipeline(IGpuComputePipeline p) => Event(37);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set) => Event(38);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) => Event(39);
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) => Event(40);
        public void BeginTimestamps(IGpuTimestampPool pool, uint count) { Pool = (GpuPerfTimestampTestPool)pool; Pool.Begin(count); Event(10); }
        public void WriteTimestamp(IGpuTimestampPool pool, uint index) { ((GpuPerfTimestampTestPool)pool).Write(index); Event(11); }
        public void EndTimestamps(IGpuTimestampPool pool) { ((GpuPerfTimestampTestPool)pool).End(); Event(12); }
        public void Dispose() { }
    }

    sealed class TailCommands : TestCommands, IGpuCommandListTimestampEnd, IVulkanRenderingScope, ID3D11SwapchainBindTracking
    {
        bool _armed;
        uint _tail;
        public bool BoundSwapchainFramebuffer => true;
        public void EndActiveRendering() => Event(41);
        public override void Begin() { base.Begin(); _armed = false; }
        public void FinishTimestampsOnEnd(IGpuTimestampPool pool, uint finalIndex)
        {
            if (_armed || !ReferenceEquals(pool, Pool) || Pool.Written != finalIndex || finalIndex != Pool.Count - 1)
                throw new InvalidOperationException("Invalid native End tail.");
            _tail = finalIndex; _armed = true;
        }
        public override void End()
        {
            Event(3); Event(4);
            if (_armed) { WriteTimestamp(Pool!, _tail); EndTimestamps(Pool!); _armed = false; }
            Event(5);
        }
    }

    class TestDeviceBase : IGpuDevice, IGpuResourceFactory
    {
        readonly GpuPerfTimestampTestDevice _inner = new();
        internal readonly TailCommands Commands = new();
        internal System.Collections.Generic.List<GpuPerfTimestampTestPool> Pools => _inner.Pools;
        internal IGpuCommandList? ForwardUpdates, LastSubmitted;
        internal int BufferUploads;
        public IGpuResourceFactory Factory => this;
        public GpuBackendKind Backend => _inner.Backend;
        public GpuCapabilities Capabilities => _inner.Capabilities;
        public GpuDeviceDiagnostics Diagnostics => _inner.Diagnostics;
        public IGpuFramebuffer? SwapchainFramebuffer => null;
        public IGpuSampler PointSampler => _inner.PointSampler;
        public IGpuSampler LinearSampler => _inner.LinearSampler;
        public bool SyncToVerticalBlank { get; set; }
        public bool SwapchainCopySourceRequested { get; set; }
        public bool SwapchainCopySupported => true;
        public bool SwapchainCopyable => true;
        public void CopySwapchainColor(IGpuCommandList commands, IGpuTexture destination) => commands.CopyTexture(destination, destination);
        public MappedData MapForRead(IGpuTexture staging, IGpuFence fence) => new(new IntPtr(123), 16, 64);
        public GpuResourceMemorySnapshot ReadMemorySnapshot() => throw new NotSupportedException("test memory snapshot");
        public void MarkDeviceDisposed() => Commands.Event(42);
        public int GetDeviceRemovedReason() => 17;
        public void Submit(IGpuCommandList commands) { LastSubmitted = commands; }
        public void Submit(IGpuCommandList commands, IGpuFence completion) => Submit(commands, [completion]);
        public void Submit(IGpuCommandList commands, ReadOnlySpan<IGpuFence> completions)
        {
            LastSubmitted = commands;
            if (commands is TailCommands { Pool: { } pool }) pool.SubmittedFence = completions[0];
        }
        public void WaitForIdle() => _inner.WaitForIdle();
        public void Present() { }
        public void ResizeSwapchain(uint w, uint h) { }
        public void UpdateBuffer<T>(IGpuBuffer b, uint o, ReadOnlySpan<T> d) where T : unmanaged { BufferUploads++; ForwardUpdates?.UpdateBuffer(b, o, d); }
        public void UpdateBuffer<T>(IGpuBuffer b, uint o, T[] d) where T : unmanaged { BufferUploads++; ForwardUpdates?.UpdateBuffer<T>(b, o, d.AsSpan()); }
        public void UpdateBuffer<T>(IGpuBuffer b, uint o, in T d) where T : unmanaged { BufferUploads++; ForwardUpdates?.UpdateBuffer(b, o, in d); }
        public void UpdateTexture(IGpuTexture t, byte[] d, uint x, uint y, uint w, uint h) { }
        public void UpdateTexture(IGpuTexture t, byte[] d, uint x, uint y, uint w, uint h, uint m, uint l) { }
        public MappedData Map(IGpuTexture t, GpuMapMode m) => default;
        public MappedData Map(IGpuBuffer b, GpuMapMode m) => default;
        public void Unmap(IGpuTexture t) { }
        public void Unmap(IGpuBuffer b) { }
        public bool TryReadBuffer(IGpuBuffer b, IGpuFence f, uint o, Span<byte> d) => true;
        public IGpuCommandList CreateCommandList() => Commands;
        public IGpuTimestampPool CreateTimestampPool(uint capacity) => _inner.CreateTimestampPool(capacity);
        public IGpuFence CreateFence() => _inner.CreateFence();
        public IGpuBuffer CreateBuffer(in GpuBufferDescription d) => _inner.CreateBuffer(d);
        public IGpuTexture CreateTexture(in GpuTextureDescription d) => _inner.CreateTexture(d);
        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour) => _inner.CreateFramebuffer(depth, colour);
        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d) => new FakeResourceSet([]);
        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d) => _inner.CreateResourceLayout(d);
        public IGpuSampler CreateSampler(in GpuSamplerDescription d) => _inner.CreateSampler(d);
        public IGpuShaderSet CreateShadersFromSpirv(string v, string f) => _inner.CreateShadersFromSpirv(v, f);
        public IGpuComputeShader CreateComputeShaderFromSpirv(string c) => new TestComputeShader();
        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) => new FakePipeline();
        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => new TestComputePipeline();
        public void Dispose() => _inner.Dispose();
    }

    sealed class TestDevice : TestDeviceBase, IGpuSwapchainCapture, IGpuFencedReadback, IGpuResourceMemoryDiagnostics, IGpuDeviceLifecycle, ID3D11RemovedReason;
    sealed class CaptureDevice : TestDeviceBase, IGpuSwapchainCapture;
    sealed class ReadbackDevice : TestDeviceBase, IGpuFencedReadback;
    sealed class MemoryDevice : TestDeviceBase, IGpuResourceMemoryDiagnostics;
    sealed class LifecycleDevice : TestDeviceBase, IGpuDeviceLifecycle;
    sealed class RemovedDevice : TestDeviceBase, ID3D11RemovedReason;

    sealed class TestComputePipeline : IGpuComputePipeline { public void Dispose() { } }
}
