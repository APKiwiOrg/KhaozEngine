using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>Native allocation and encoding calls, separated from counter policy and batch lifetime.</summary>
    internal interface IMetalTimestampNative
    {
        bool HasTimestampCounterSet { get; }
        bool SupportsSampling(uint point);
        IntPtr CreateSampleBuffer(uint capacity);
        IntPtr CreateBuffer(uint bytes);
        IntPtr Contents(IntPtr buffer);
        IntPtr CreateEvent();
        IntPtr CreateBlitDescriptor();
        void AttachSamples(IntPtr descriptor, IntPtr samples, ulong start, ulong end);
        void Release(IntPtr handle);
        void SampleClocks(out ulong cpuNanoseconds, out ulong gpuTicks);
        void Signal(IntPtr commandBuffer, IntPtr ev, ulong value);
        void Wait(IntPtr commandBuffer, IntPtr ev, ulong value);
        IntPtr BeginBlit(IntPtr commandBuffer, IntPtr descriptor);
        void CopyMarker(IntPtr encoder, IntPtr source, IntPtr destination, uint bytes);
        void Sample(IntPtr encoder, IntPtr samples, uint index, bool barrier);
        void EndBlit(IntPtr encoder);
        void Resolve(IntPtr encoder, IntPtr samples, uint count, IntPtr destination);
        void CopyResolved(IntPtr contents, uint count, Span<ulong> raw);
    }
}
