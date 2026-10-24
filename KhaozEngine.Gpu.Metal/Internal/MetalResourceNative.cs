using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal;

internal interface IMetalResourceNative
{
    MTLBuffer NewBuffer(nuint size);
    IntPtr Contents(MTLBuffer buffer);
    MTLTexture NewTexture(in GpuTextureDescription description, in MetalTextureViewPlan plan);
    void Release(IntPtr handle);
}

/// <summary>The existing buffer/texture allocation selectors, injectable without initializing Metal.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MetalResourceNative(MTLDevice device) : IMetalResourceNative
{
    public MTLBuffer NewBuffer(nuint size)
    {
        using var pool = ObjCAutoreleasePool.Enter();
        return device.NewBuffer(size, MTLResourceOptions.SharedDefaultCache);
    }
    public IntPtr Contents(MTLBuffer buffer)
    {
        using var pool = ObjCAutoreleasePool.Enter();
        return buffer.Contents();
    }
    public MTLTexture NewTexture(in GpuTextureDescription description, in MetalTextureViewPlan plan)
    {
        using var pool = ObjCAutoreleasePool.Enter();
        var descriptor = MTLTextureDescriptor.New();
        if (descriptor.IsNull) throw new InvalidOperationException("The Objective-C runtime has no MTLTextureDescriptor class.");
        try
        {
            descriptor.Configure(plan.Type, MetalFormats.ToPixelFormat(description.Format, plan.DepthStencil),
                description.Width, description.Height, description.MipLevels, description.ArrayLayers,
                description.SampleCount, plan.Usage, plan.Storage);
            return device.NewTexture(descriptor);
        }
        finally { descriptor.Release(); }
    }
    public void Release(IntPtr handle)
    {
        using var pool = ObjCAutoreleasePool.Enter();
        if (handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(handle);
    }
}
