using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class MetalPassTimingSmokeTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Headless_stage_boundary_render_compute_and_blit_resolve_real_timestamps()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        RunNative();
    }

    [SupportedOSPlatform("macos")]
    void RunNative()
    {
        using var pool = ObjCAutoreleasePool.Enter();
        using var objects = new NativeObjects();
        var device = new MTLDevice(objects.Own(MTLDevice.CreateSystemDefault()));
        var queue = new MTLCommandQueue(objects.Own(device.NewCommandQueue().Handle));
        var native = new MetalPassTimingNative(device.Handle);
        using var passes = new MetalPassTimingCapture(native);
        Assert.True(passes.Supported, passes.Reason);
        output.WriteLine("Device " + device.Name());
        var timestamps = new MTLTimestampInterop(device.Handle);
        IntPtr oversized = timestamps.CreateSampleBuffer(4097);
        output.WriteLine("4097-sample creation " + (oversized == IntPtr.Zero ? timestamps.SampleBufferFailureReason : "accepted"));
        timestamps.Release(oversized);

        var textureDescriptor = new MTLTextureDescriptor(objects.Own(MTLTextureDescriptor.New().Handle));
        textureDescriptor.Configure(MTLTextureType.Type2D, MTLPixelFormat.RGBA8Unorm, 16, 16, 1, 1, 1,
            MTLTextureUsage.RenderTarget, MTLStorageMode.Private);
        var texture = new MTLTexture(objects.Own(device.NewTexture(textureDescriptor).Handle));
        var descriptor = new MTLRenderPassDescriptor(objects.Own(MTLRenderPassDescriptor.Create().Handle));
        descriptor.ColourAttachment(0).SetTexture(texture);
        descriptor.ColourAttachment(0).SetLoadAction(MTLLoadAction.Clear);
        descriptor.ColourAttachment(0).SetStoreAction(MTLStoreAction.Store);
        var options = new MTLCompileOptions(objects.Own(MTLCompileOptions.New().Handle));
        var source = NSString.FromManaged("""
            #include <metal_stdlib>
            using namespace metal;
            vertex float4 v(uint i [[vertex_id]]) {
                const float2 p[3] = {float2(-1,-1), float2(3,-1), float2(-1,3)};
                return float4(p[i], 0, 1);
            }
            fragment float4 f() { return float4(1,0,0,1); }
            kernel void k(device uint* value [[buffer(0)]]) { value[0] = 42; }
            """);
        // FromManaged returns an autoreleased NSString. The enclosing pool owns these strings.
        var library = MTLLibrary.NewWithSource(device, source, options, out var compileError);
        Assert.True(!library.IsNull, compileError.IsNull ? null : compileError.LocalizedDescription());
        objects.Own(library.Handle);
        MTLFunction Function(string name)
        {
            var text = NSString.FromManaged(name);
            return new(objects.Own(library.NewFunction(text).Handle));
        }
        var pipelineDescriptor = new MTLRenderPipelineDescriptor(objects.Own(MTLRenderPipelineDescriptor.New().Handle));
        pipelineDescriptor.SetVertexFunction(Function("v"));
        pipelineDescriptor.SetFragmentFunction(Function("f"));
        ObjCMsgSend.SendVoidNUInt(pipelineDescriptor.ColorAttachmentAt(0).Handle, ObjCRuntime.Sel("setPixelFormat:"), (nuint)MTLPixelFormat.RGBA8Unorm);
        var graphics = device.NewRenderPipelineState(pipelineDescriptor, out var graphicsError);
        Assert.True(graphics.Handle != IntPtr.Zero, graphicsError.IsNull ? null : graphicsError.LocalizedDescription());
        objects.Own(graphics.Handle);
        var compute = device.NewComputePipelineState(Function("k"), out var computeError);
        Assert.True(compute.Handle != IntPtr.Zero, computeError.IsNull ? null : computeError.LocalizedDescription());
        objects.Own(compute.Handle);
        var sourceBuffer = new MTLBuffer(objects.Own(device.NewBuffer(4, 0).Handle));
        var destination = new MTLBuffer(objects.Own(device.NewBuffer(4, 0).Handle));
        var buffer = queue.CommandBuffer();
        using var capture = new FrameTimingCapture(null, passTiming: passes);
        passes.Boundary(0, System.Diagnostics.Stopwatch.GetTimestamp());
        capture.Start();
        var sink = new MetalEncoderSink();
        IntPtr render = sink.BeginRenderEncoder(buffer.Handle, descriptor.Handle);
        var renderEncoder = new MTLRenderCommandEncoder(render);
        renderEncoder.SetRenderPipelineState(graphics);
        renderEncoder.SetViewport(new(0, 0, 16, 16, 0, 1));
        renderEncoder.SetScissorRect(new(0, 0, 16, 16));
        renderEncoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3, 1, 0);
        sink.EndEncoding(MetalEncoderKind.Render, render);
        // A descriptor variant must keep the serial dispatch type of the released selector.
        var checkResources = native.Create(4);
        try { Assert.Equal((nuint)MTLDispatchType.Serial, ObjCMsgSend.SendNUInt(checkResources.Compute, ObjCRuntime.Sel("dispatchType"))); }
        finally { native.Release(checkResources); }
        IntPtr dispatch = sink.BeginComputeEncoder(buffer.Handle);
        var computeEncoder = new MTLComputeCommandEncoder(dispatch);
        computeEncoder.SetComputePipelineState(compute);
        computeEncoder.SetBuffers([sourceBuffer.Handle], [(nuint)0], 0);
        computeEncoder.DispatchThreadgroups(new(1, 1, 1), new(1, 1, 1));
        sink.EndEncoding(MetalEncoderKind.Compute, dispatch);
        IntPtr blit = sink.BeginBlitEncoder(buffer.Handle);
        new MTLBlitCommandEncoder(blit).CopyFromBufferToBuffer(sourceBuffer, 0, destination, 0, 4);
        sink.EndEncoding(MetalEncoderKind.Blit, blit);
        Assert.True(MetalCompletionHandler.AttachTo(buffer.Handle));
        buffer.Commit();
        buffer.WaitUntilCompleted();
        capture.Stop();
        passes.End(1, System.Diagnostics.Stopwatch.GetTimestamp());
        Assert.Equal(4, (int)buffer.Status());
        Assert.Equal(42, System.Runtime.InteropServices.Marshal.ReadInt32(destination.Contents()));
        var report = passes.Snapshot();
        output.WriteLine(JsonSerializer.Serialize(report));
        Assert.Equal(3, report.Encoders.Records.Length);
        Assert.All(report.Encoders.Records, e => Assert.Equal("valid", e.Status));
        var rendered = Assert.Single(report.Encoders.Records, e => e.Kind == "render");
        Assert.True(rendered.StartVertex > 0 && rendered.EndVertex >= rendered.StartVertex);
        Assert.True(rendered.StartFragment > 0 && rendered.EndFragment >= rendered.StartFragment);
        Assert.Equal(0, report.ResolveFailures);
        Assert.Equal(0, report.ErrorValues);
        Assert.Equal(0, report.PendingCommandBuffers);
        var first = report.ClockPairs.Records[0];
        var last = report.ClockPairs.Records[^1];
        Assert.True(last.CpuNanoseconds > first.CpuNanoseconds && last.GpuTicks > first.GpuTicks);
        output.WriteLine("Measured nanoseconds per GPU tick " + (last.CpuNanoseconds - first.CpuNanoseconds) / (double)(last.GpuTicks - first.GpuTicks));
    }

    [SupportedOSPlatform("macos")]
    sealed class NativeObjects : IDisposable
    {
        readonly List<IntPtr> _handles = [];
        internal IntPtr Own(IntPtr handle)
        {
            Assert.NotEqual(IntPtr.Zero, handle);
            _handles.Add(handle);
            return handle;
        }
        public void Dispose()
        {
            for (int i = _handles.Count - 1; i >= 0; i--) ObjCRuntime.ObjcRelease(_handles[i]);
        }
    }
}
