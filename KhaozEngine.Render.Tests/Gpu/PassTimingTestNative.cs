using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Tests.Gpu;

internal sealed class PassTimingTestNative : IMetalPassTimingNative, IMetalPassTimingEncoderApi
{
    public bool Supported { get; set; } = true;
    public string? Reason => Supported ? null : "unsupported fake device";
    internal bool Attached, TimedFactory, ResolveFailure, ErrorValue;
    internal bool ThrowOnOpen, CreationFailure;
    internal IntPtr OpenSamples, OpenDescriptor;
    internal (ulong First, ulong Last) Indices;
    internal PassDepthStencil Planes = new(true, true);
    internal IntPtr OpenEncoder = new(1000);
    internal bool ThrowOnSetUnknown, ThrowOnDontCare, NilEncoder;
    // Both the descriptor's store action while the encoder opened, and what the encoder was told before it ended.
    internal readonly List<string> DescriptorStoresAtOpen = [];
    internal readonly List<(IntPtr Encoder, bool Discard)> Resolutions = [];
    internal string DescriptorStore = "store";
    int _clock, _next;
    public PassTimingResources Create(int samples) => CreationFailure ? throw new InvalidOperationException("creation refused") : new(new(++_next), new(101), new(102));
    public void Release(PassTimingResources resources) { }
    public bool Owns(IntPtr commandBuffer) => true;
    public IntPtr Begin(MetalEncoderKind kind, IntPtr commandBuffer, IntPtr descriptor, PassTimingResources resources, int firstSample)
        => MetalPassTimingEncoder.Begin(this, kind, commandBuffer, descriptor, resources, firstSample);
    public void AttachRender(IntPtr descriptor, IntPtr samples, int first)
    { Attached = samples != IntPtr.Zero; OpenSamples = samples; Indices = ((ulong)first, (ulong)(first + 3)); }
    public void AttachEncoder(IntPtr descriptor, IntPtr samples, ulong first, ulong last)
    { Attached = samples != IntPtr.Zero; OpenSamples = samples; Indices = (first, last); }
    public IntPtr Open(MetalEncoderKind kind, IntPtr commandBuffer, IntPtr descriptor)
    {
        TimedFactory = Attached;
        OpenDescriptor = descriptor;
        if (kind == MetalEncoderKind.Render) DescriptorStoresAtOpen.Add(DescriptorStore);
        if (ThrowOnOpen) throw new InvalidOperationException();
        return NilEncoder ? IntPtr.Zero : OpenEncoder;
    }
    public PassTimingSignature Signature(IntPtr descriptor, IntPtr drawableTexture) => new()
    {
        RenderTargetWidth = (ulong)descriptor.ToInt64(),
        Depth = new() { StoreAction = DescriptorStore == "store" ? 1UL : 4UL },
    };
    public PassDepthStencil DepthStencil(IntPtr descriptor) => Planes;
    public void SetDescriptorStore(IntPtr descriptor, PassDepthStencil planes, bool unknown)
    {
        if (unknown && ThrowOnSetUnknown) throw new InvalidOperationException();
        DescriptorStore = unknown ? "unknown" : "store";
    }
    public void ResolveEncoderStore(IntPtr encoder, PassDepthStencil planes, bool discard)
    {
        if (discard && ThrowOnDontCare) throw new InvalidOperationException();
        Resolutions.Add((encoder, discard));
    }
    public void Resolve(IntPtr samples, int count, Span<ulong> values)
    {
        if (ResolveFailure) throw new InvalidOperationException();
        for (int i = 0; i < count; i++) values[i] = (ulong)(100 + i * 10);
        if (ErrorValue) values[0] = ulong.MaxValue;
    }
    public void SampleClocks(out ulong cpuNanoseconds, out ulong gpuTicks)
    {
        _clock++;
        cpuNanoseconds = (ulong)_clock * 1000;
        gpuTicks = (ulong)_clock * 100;
    }
}
