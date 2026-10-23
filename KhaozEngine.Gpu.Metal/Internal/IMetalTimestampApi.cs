using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    internal interface IMetalTimestampApi
    {
        bool SupportsTimestampCounters { get; }
        bool SupportsStageBoundary { get; }
        bool SupportsBlitBoundary { get; }
        MetalTimestampResources CreatePool(uint capacity, bool stageBoundary);
        void DestroyPool(in MetalTimestampResources resources);
        void SampleClocks(out ulong cpuNanoseconds, out ulong gpuTicks);
        void WriteBoundary(IntPtr commandBuffer, in MetalTimestampResources resources, uint index,
            ulong beforeValue, ulong afterValue, bool stageBoundary);
        void Resolve(IntPtr commandBuffer, in MetalTimestampResources resources, uint count);
        void CopyResolved(in MetalTimestampResources resources, uint count, Span<ulong> raw);
    }
}
