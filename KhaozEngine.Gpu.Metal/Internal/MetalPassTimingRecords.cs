using System;
using System.Linq;

namespace KhaozEngine.Gpu.Metal.Internal;

internal readonly record struct PassTimingResources(IntPtr Samples, IntPtr Compute, IntPtr Blit);
internal readonly record struct PassTimingAttachment(ulong Width, ulong Height, ulong PixelFormat,
    ulong TextureType, ulong ArrayLength, ulong SampleCount, ulong Slice, ulong Level, ulong DepthPlane,
    ulong LoadAction, ulong StoreAction);
internal readonly record struct PassTimingSignature(PassTimingAttachment Color0, PassTimingAttachment Color1,
    PassTimingAttachment Color2, PassTimingAttachment Color3, PassTimingAttachment Color4,
    PassTimingAttachment Color5, PassTimingAttachment Color6, PassTimingAttachment Color7,
    PassTimingAttachment Depth, PassTimingAttachment Stencil, ulong RenderTargetArrayLength,
    ulong RenderTargetWidth, ulong RenderTargetHeight, ulong SampleCount, bool Color0IsDrawable);
internal readonly record struct PassTimingEncoder(long CommandBuffer, int CallbackIndex, int Ordinal,
    string Kind, int SignatureId, ulong? StartVertex, ulong? EndVertex, ulong? StartFragment,
    ulong? EndFragment, ulong? StartEncoder, ulong? EndEncoder, string Status, int LabelId = -1, int LabelOccurrence = 0,
    bool DirectionalScope = false, string? StoreResolution = null);
internal readonly record struct PassTimingClockPair(int CallbackIndex, long BoundaryTimestamp,
    long BeforeTimestamp, long AfterTimestamp, ulong CpuNanoseconds, ulong GpuTicks);
internal readonly record struct PassTimingBlock(int StartCallback, int EndCallbackExclusive,
    long StartTimestamp, long EndTimestamp, bool Timed);
internal readonly record struct PassTimingClockSegment(int StartCallback, int EndCallback,
    ulong CpuOriginNanoseconds, ulong GpuOriginTicks, double NanosecondsPerGpuTick);
internal sealed record PassTimingSnapshot(bool Supported, string? Reason, string SamplingPoint,
    int SlotCount, int SampleCapacity, int BlockCallbacks, string GpuTickUnit, string ClockMapping,
    PassTimingBlock[] Blocks, FrameTimingBufferSnapshot<PassTimingClockPair> ClockPairs,
    PassTimingSignature[] Signatures, FrameTimingBufferSnapshot<PassTimingEncoder> Encoders,
    long CapacityOverflow, long SignatureOverflow, long UntimedEncoders, long SkippedCommandBuffers,
    long CommandStateOverflow, long ResolveFailures, long ErrorValues, long ClockFailures,
    long NativeFailures, int PendingCommandBuffers)
{
    public PassTimingClockSegment[] ClockSegments => ClockPairs.Records.Zip(ClockPairs.Records.Skip(1))
        .Where(p => p.Second.CpuNanoseconds > p.First.CpuNanoseconds && p.Second.GpuTicks > p.First.GpuTicks)
        .Select(
            p => new PassTimingClockSegment(p.First.CallbackIndex, p.Second.CallbackIndex,
                p.First.CpuNanoseconds, p.First.GpuTicks,
                (p.Second.CpuNanoseconds - p.First.CpuNanoseconds) / (double)(p.Second.GpuTicks - p.First.GpuTicks))).ToArray();

    internal static PassTimingSnapshot Unavailable() => new(false, "No native Metal diagnostics device.",
        "atStageBoundary", 0, 0, 72, "unavailable", "unavailable", [], new([], 0, 0), [], new([], 0, 0),
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

// Descriptor stage attachments, store actions of the attachments a descriptor already names, and a CPU resolve are all
// that exist on this seam. No GPU marker API is exposed.
internal interface IMetalPassTimingNative
{
    bool Supported { get; }
    string? Reason { get; }
    PassTimingResources Create(int samples);
    void Release(PassTimingResources resources);
    bool Owns(IntPtr commandBuffer);
    IntPtr Begin(MetalEncoderKind kind, IntPtr commandBuffer, IntPtr descriptor,
        PassTimingResources resources, int firstSample);
    PassTimingSignature Signature(IntPtr descriptor, IntPtr drawableTexture);
    PassDepthStencil DepthStencil(IntPtr descriptor);
    void SetDescriptorStore(IntPtr descriptor, PassDepthStencil planes, bool unknown);
    void ResolveEncoderStore(IntPtr encoder, PassDepthStencil planes, bool discard);
    void Resolve(IntPtr samples, int count, Span<ulong> values);
    void SampleClocks(out ulong cpuNanoseconds, out ulong gpuTicks);
}
