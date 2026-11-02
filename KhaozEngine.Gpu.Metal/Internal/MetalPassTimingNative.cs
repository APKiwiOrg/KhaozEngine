using System;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal;

[SupportedOSPlatform("macos")]
internal sealed class MetalPassTimingNative : IMetalPassTimingNative, IMetalPassTimingEncoderApi
{
    readonly IntPtr _device;
    readonly MTLTimestampInterop _timestamps;
    public bool Supported { get; }
    public string? Reason { get; }

    internal MetalPassTimingNative(IntPtr device)
    {
        _device = device;
        _timestamps = new(device);
        using var pool = ObjCAutoreleasePool.Enter();
        try
        {
            Supported = _timestamps.HasTimestampCounterSet && _timestamps.SupportsSampling(0);
            Reason = Supported ? null : "MTLCommonCounterSetTimestamp or atStageBoundary sampling is unsupported.";
            // Resolve every selector before arming. No selector-cache allocation lands in a measured block.
            foreach (string name in SelectorNames) _ = ObjCRuntime.Sel(name);
        }
        catch (Exception error) { Supported = false; Reason = "Metal counter discovery failed: " + error.Message; }
    }

    public PassTimingResources Create(int samples)
    {
        using var pool = ObjCAutoreleasePool.Enter();
        IntPtr buffer = IntPtr.Zero, compute = IntPtr.Zero, blit = IntPtr.Zero;
        try
        {
            buffer = _timestamps.CreateSampleBuffer((uint)samples);
            if (buffer == IntPtr.Zero) throw new InvalidOperationException(_timestamps.SampleBufferFailureReason);
            compute = _timestamps.CreateSerialComputeDescriptor();
            blit = _timestamps.CreateBlitDescriptor();
            if (compute == IntPtr.Zero || blit == IntPtr.Zero) throw new InvalidOperationException("Metal pass descriptor creation failed.");
            return new(buffer, compute, blit);
        }
        catch { Release(new(buffer, compute, blit)); throw; }
    }

    public void Release(PassTimingResources resources)
    {
        _timestamps.Release(resources.Compute);
        _timestamps.Release(resources.Blit);
        _timestamps.Release(resources.Samples);
    }

    public bool Owns(IntPtr commandBuffer) => ObjCMsgSend.Send(commandBuffer, ObjCRuntime.Sel("device")) == _device;

    public IntPtr Begin(MetalEncoderKind kind, IntPtr commandBuffer, IntPtr descriptor,
        PassTimingResources resources, int firstSample)
        => MetalPassTimingEncoder.Begin(this, kind, commandBuffer, descriptor, resources, firstSample);

    public void AttachRender(IntPtr descriptor, IntPtr samples, int first) => _timestamps.AttachRenderSamples(descriptor, samples, first);
    public void AttachEncoder(IntPtr descriptor, IntPtr samples, ulong first, ulong last) => _timestamps.AttachSamples(descriptor, samples, first, last);
    public IntPtr Open(MetalEncoderKind kind, IntPtr commandBuffer, IntPtr descriptor)
        => kind == MetalEncoderKind.Render ? new MTLCommandBuffer(commandBuffer).RenderCommandEncoder(descriptor)
            : descriptor != IntPtr.Zero ? ObjCMsgSend.SendPtr(commandBuffer, ObjCRuntime.Sel(kind == MetalEncoderKind.Compute
                ? "computeCommandEncoderWithDescriptor:" : "blitCommandEncoderWithDescriptor:"), descriptor)
            : kind == MetalEncoderKind.Compute ? new MTLCommandBuffer(commandBuffer).ComputeCommandEncoder(MTLDispatchType.Serial)
            : new MTLCommandBuffer(commandBuffer).BlitCommandEncoder().Handle;

    public PassTimingSignature Signature(IntPtr descriptor, IntPtr drawableTexture)
    {
        IntPtr colors = ObjCMsgSend.Send(descriptor, ObjCRuntime.Sel("colorAttachments"));
        var c0 = Attachment(Color(colors, 0));
        var c1 = Attachment(Color(colors, 1));
        var c2 = Attachment(Color(colors, 2));
        var c3 = Attachment(Color(colors, 3));
        var c4 = Attachment(Color(colors, 4));
        var c5 = Attachment(Color(colors, 5));
        var c6 = Attachment(Color(colors, 6));
        var c7 = Attachment(Color(colors, 7));
        var depth = Attachment(ObjCMsgSend.Send(descriptor, ObjCRuntime.Sel("depthAttachment")));
        var stencil = Attachment(ObjCMsgSend.Send(descriptor, ObjCRuntime.Sel("stencilAttachment")));
        ulong samples = Math.Max(1, Math.Max(Math.Max(Math.Max(c0.SampleCount, c1.SampleCount), Math.Max(c2.SampleCount, c3.SampleCount)),
            Math.Max(Math.Max(Math.Max(c4.SampleCount, c5.SampleCount), Math.Max(c6.SampleCount, c7.SampleCount)), Math.Max(depth.SampleCount, stencil.SampleCount))));
        return new(c0, c1, c2, c3, c4, c5, c6, c7, depth, stencil,
            Read(descriptor, "renderTargetArrayLength"), Read(descriptor, "renderTargetWidth"),
            Read(descriptor, "renderTargetHeight"), samples, drawableTexture != IntPtr.Zero
                && ObjCMsgSend.Send(Color(colors, 0), ObjCRuntime.Sel("texture")) == drawableTexture);
    }

    public PassDepthStencil DepthStencil(IntPtr descriptor) => new(
        ObjCMsgSend.Send(ObjCMsgSend.Send(descriptor, ObjCRuntime.Sel("depthAttachment")), ObjCRuntime.Sel("texture")) != IntPtr.Zero,
        ObjCMsgSend.Send(ObjCMsgSend.Send(descriptor, ObjCRuntime.Sel("stencilAttachment")), ObjCRuntime.Sel("texture")) != IntPtr.Zero);

    public void SetDescriptorStore(IntPtr descriptor, PassDepthStencil planes, bool unknown)
    {
        nuint action = (nuint)(ulong)(unknown ? MTLStoreAction.Unknown : MTLStoreAction.Store);
        if (planes.Depth) ObjCMsgSend.SendVoidNUInt(ObjCMsgSend.Send(descriptor, ObjCRuntime.Sel("depthAttachment")), ObjCRuntime.Sel("setStoreAction:"), action);
        if (planes.Stencil) ObjCMsgSend.SendVoidNUInt(ObjCMsgSend.Send(descriptor, ObjCRuntime.Sel("stencilAttachment")), ObjCRuntime.Sel("setStoreAction:"), action);
    }

    public void ResolveEncoderStore(IntPtr encoder, PassDepthStencil planes, bool discard)
    {
        nuint action = (nuint)(ulong)(discard ? MTLStoreAction.DontCare : MTLStoreAction.Store);
        if (planes.Depth) ObjCMsgSend.SendVoidNUInt(encoder, ObjCRuntime.Sel("setDepthStoreAction:"), action);
        if (planes.Stencil) ObjCMsgSend.SendVoidNUInt(encoder, ObjCRuntime.Sel("setStencilStoreAction:"), action);
    }

    static IntPtr Color(IntPtr colors, nuint index) => ObjCMsgSend.SendPtrNUInt(colors, ObjCRuntime.Sel("objectAtIndexedSubscript:"), index);
    static ulong Read(IntPtr value, string name) => (ulong)ObjCMsgSend.SendNUInt(value, ObjCRuntime.Sel(name));
    static PassTimingAttachment Attachment(IntPtr attachment)
    {
        IntPtr texture = ObjCMsgSend.Send(attachment, ObjCRuntime.Sel("texture"));
        return new(Read(texture, "width"), Read(texture, "height"), Read(texture, "pixelFormat"),
            Read(texture, "textureType"), Read(texture, "arrayLength"), Read(texture, "sampleCount"),
            Read(attachment, "slice"), Read(attachment, "level"), Read(attachment, "depthPlane"),
            Read(attachment, "loadAction"), Read(attachment, "storeAction"));
    }

    public void Resolve(IntPtr samples, int count, Span<ulong> values)
    {
        // resolveCounterRange returns autoreleased NSData on Metal's completion queue.
        using var pool = ObjCAutoreleasePool.Enter();
        _timestamps.ResolveOnCpu(samples, count, values);
    }
    public void SampleClocks(out ulong cpuNanoseconds, out ulong gpuTicks)
    {
        using var pool = ObjCAutoreleasePool.Enter();
        _timestamps.SampleClocks(out cpuNanoseconds, out gpuTicks);
    }

    static readonly string[] SelectorNames = ["device", "sampleBufferAttachments", "objectAtIndexedSubscript:",
        "setSampleBuffer:", "setStartOfVertexSampleIndex:", "setEndOfVertexSampleIndex:",
        "setStartOfFragmentSampleIndex:", "setEndOfFragmentSampleIndex:", "setStartOfEncoderSampleIndex:",
        "setEndOfEncoderSampleIndex:", "computeCommandEncoderWithDescriptor:", "blitCommandEncoderWithDescriptor:",
        "renderCommandEncoderWithDescriptor:", "computeCommandEncoderWithDispatchType:", "blitCommandEncoder",
        "colorAttachments", "depthAttachment", "stencilAttachment", "texture", "width", "height", "pixelFormat",
        "textureType", "arrayLength", "sampleCount", "slice", "level", "depthPlane", "loadAction", "storeAction",
        "renderTargetArrayLength", "renderTargetWidth", "renderTargetHeight", "resolveCounterRange:", "length", "bytes",
        "sampleTimestamps:gpuTimestamp:", "setStoreAction:", "setDepthStoreAction:", "setStencilStoreAction:"];
}
