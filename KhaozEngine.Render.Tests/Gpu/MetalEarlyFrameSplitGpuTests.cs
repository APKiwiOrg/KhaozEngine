using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using KhaozEngine.Primitives;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class MetalEarlyFrameSplitGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Headless_offscreen_passes_have_identical_bytes_with_an_early_command_buffer_commit()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        RunNative();
    }

    [SupportedOSPlatform("macos")]
    void RunNative()
    {
        using var pool = ObjCAutoreleasePool.Enter();
        using var device = (MetalGpuDevice)new MetalBackendProvider().CreateHeadless().Device;
        using var objects = new NativeObjects();
        var native = device.Handle;
        var options = new MTLCompileOptions(objects.Own(MTLCompileOptions.New().Handle));
        var library = MTLLibrary.NewWithSource(native, NSString.FromManaged("""
            #include <metal_stdlib>
            using namespace metal;
            vertex float4 v(uint i [[vertex_id]]) {
                const float2 p[3] = {float2(-.5,-.5), float2(.5,-.5), float2(0,.5)};
                return float4(p[i], 0, 1);
            }
            fragment float4 f() { return float4(1,1,1,1); }
            """), options, out var error);
        Assert.True(!library.IsNull, error.IsNull ? null : error.LocalizedDescription());
        objects.Own(library.Handle);
        var descriptor = new MTLRenderPipelineDescriptor(objects.Own(MTLRenderPipelineDescriptor.New().Handle));
        descriptor.SetVertexFunction(new(objects.Own(library.NewFunction(NSString.FromManaged("v")).Handle)));
        descriptor.SetFragmentFunction(new(objects.Own(library.NewFunction(NSString.FromManaged("f")).Handle)));
        ObjCMsgSend.SendVoidNUInt(descriptor.ColorAttachmentAt(0).Handle, ObjCRuntime.Sel("setPixelFormat:"), (nuint)MTLPixelFormat.BGRA8Unorm);
        var pipeline = native.NewRenderPipelineState(descriptor, out var pipelineError);
        Assert.True(pipeline.Handle != IntPtr.Zero, pipelineError.IsNull ? null : pipelineError.LocalizedDescription());
        objects.Own(pipeline.Handle);
        var unsplit = Render(device, pipeline, 0);
        var split = Render(device, pipeline, 72);
        Assert.NotEmpty(unsplit);
        Assert.Equal(unsplit, split);
        // A colour control proves that an empty or all-zero readback cannot satisfy identity.
        Assert.Contains((byte)255, split);
        Assert.Contains((byte)0, split);
        output.WriteLine("One queue, three offscreen draw passes, a dependent final copy and a final draw. Compared " + split.Length + " pixel bytes.");
        Assert.Null(device.Diagnostics.DeviceLossReason);
    }

    [SupportedOSPlatform("macos")]
    static byte[] Render(MetalGpuDevice device, MTLRenderPipelineState pipeline, int callback)
    {
        const uint size = 16;
        const GpuPixelFormat format = GpuPixelFormat.B8G8R8A8UNorm;
        IGpuTexture Target() => device.Factory.CreateTexture(GpuTextureDescription.Texture2D(size, size, format, GpuTextureUsage.RenderTarget));
        using var a = Target();
        using var b = Target();
        using var c = Target();
        using var final = Target();
        using var fa = device.Factory.CreateFramebuffer(null, a);
        using var fb = device.Factory.CreateFramebuffer(null, b);
        using var fc = device.Factory.CreateFramebuffer(null, c);
        IGpuTexture Staging() => device.Factory.CreateTexture(GpuTextureDescription.Texture2D(size, size, format, GpuTextureUsage.Staging));
        using var readA = Staging();
        using var readB = Staging();
        using var readC = Staging();
        using var readFinal = Staging();
        using var list = device.CreateCommandList();
        var early = new FrameEarlySubmitCapture(100);
        using var capture = new FrameTimingCapture(list, earlySubmit: early);
        early.Boundary(callback, callback);
        capture.Start();
        capture.CallbackIndex = callback;
        MetalSwapchainFramebuffer? swapchain = null;
        swapchain = new(format, new(size, size), () => swapchain!.Adopt(new(((MetalTexture)final).Handle.Handle, format), new(size, size)));
        void Draw()
        {
            var encoder = new MTLRenderCommandEncoder(list.Passes.PrepareDraw());
            encoder.SetRenderPipelineState(pipeline);
            encoder.DrawPrimitives(MTLPrimitiveType.Triangle, 0, 3, 1, 0);
        }
        list.Begin();
        list.SetFramebuffer(fa);
        list.ClearColorTarget(0, new Color(1, 0, 0, 1));
        Draw();
        list.SetFramebuffer(fb);
        list.ClearColorTarget(0, new Color(0, 1, 0, 1));
        Draw();
        list.SetFramebuffer(fc);
        list.ClearColorTarget(0, new Color(0, 0, 1, 1));
        Draw();
        list.SetFramebuffer(swapchain);
        // Same dependent operations in both states, all after the split point. No additional probe encoders.
        list.CopyTexture(c, final);
        Draw();
        list.CopyTexture(a, readA);
        list.CopyTexture(b, readB);
        list.CopyTexture(c, readC);
        list.CopyTexture(final, readFinal);
        list.End();
        device.Submit(list);
        device.WaitForIdle();
        Assert.Equal(callback == 72, early.Snapshot(100).AppliedPerCallback[callback]);
        var submissions = capture.Snapshot().Submissions.Records;
        Assert.Equal(callback == 72 ? 1 : 0, submissions.Count(s => s.Kind == FrameTimingCommandKind.EarlyFrameRecording));
        Assert.Single(submissions, s => s.Kind == FrameTimingCommandKind.FrameRecording);
        Assert.Equal(0, device.Uncommitted.Outstanding);
        byte[] Read(IGpuTexture staging, byte[] firstPixel)
        {
            var mapped = device.Map(staging, GpuMapMode.Read);
            try
            {
                int rowBytes = (int)size * 4;
                var bytes = new byte[rowBytes * size];
                for (int row = 0; row < size; row++) Marshal.Copy(mapped.Data + row * (int)mapped.RowPitch, bytes, row * rowBytes, rowBytes);
                Assert.Equal(firstPixel, bytes.Take(4));
                int center = ((int)size / 2 * (int)size + (int)size / 2) * 4;
                Assert.Equal(new byte[] { 255, 255, 255, 255 }, bytes.Skip(center).Take(4));
                return bytes;
            }
            finally { device.Unmap(staging); }
        }
        return Read(readA, [0, 0, 255, 255]).Concat(Read(readB, [0, 255, 0, 255]))
            .Concat(Read(readC, [255, 0, 0, 255])).Concat(Read(readFinal, [255, 0, 0, 255])).ToArray();
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
