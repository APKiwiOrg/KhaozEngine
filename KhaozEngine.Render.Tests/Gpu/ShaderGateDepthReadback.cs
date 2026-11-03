using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

// Test-only readback after the frame has drained. Metal stores depth and stencil in separate planes.
internal static class ShaderGateDepthReadback
{
    internal static (byte[] Depth, byte[] Stencil) Read(IGpuDevice device, IGpuTexture source)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        return ReadMetal((MetalGpuDevice)device, (MetalTexture)source);
    }

    [SupportedOSPlatform("macos")]
    static unsafe (byte[] Depth, byte[] Stencil) ReadMetal(MetalGpuDevice device, MetalTexture source)
    {
        using var pool = ObjCAutoreleasePool.Enter();
        MTLCommandQueue queue = device.Handle.NewCommandQueue();
        nuint depthPitch = source.Width * 4;
        nuint stencilPitch = source.Width;
        MTLBuffer depth = device.Handle.NewBuffer(depthPitch * source.Height, 0);
        MTLBuffer stencil = device.Handle.NewBuffer(stencilPitch * source.Height, 0);
        try
        {
            MTLCommandBuffer commands = queue.CommandBuffer();
            MTLBlitCommandEncoder blit = commands.BlitCommandEncoder();
            IntPtr selector = ObjCRuntime.Sel("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:options:");
            var size = new MTLSize(source.Width, source.Height, 1);
            CopyPlane(blit.Handle, selector, source.Handle.Handle, 0, 0, default, size, depth.Handle, 0, depthPitch, depthPitch * source.Height, 1);
            CopyPlane(blit.Handle, selector, source.Handle.Handle, 0, 0, default, size, stencil.Handle, 0, stencilPitch, stencilPitch * source.Height, 2);
            blit.EndEncoding();
            commands.Commit();
            commands.WaitUntilCompleted();
            Assert.Equal(MTLCommandBufferStatus.Completed, commands.Status());
            return (new ReadOnlySpan<byte>((void*)depth.Contents(), checked((int)(depthPitch * source.Height))).ToArray(),
                new ReadOnlySpan<byte>((void*)stencil.Contents(), checked((int)(stencilPitch * source.Height))).ToArray());
        }
        finally { depth.Release(); stencil.Release(); queue.Release(); }
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    static extern void CopyPlane(IntPtr receiver, IntPtr selector, IntPtr texture, nuint slice, nuint level,
        MTLOrigin origin, MTLSize size, IntPtr buffer, nuint offset, nuint rowPitch, nuint imagePitch, nuint options);
}
