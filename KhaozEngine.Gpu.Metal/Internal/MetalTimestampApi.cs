using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    internal readonly record struct MetalTimestampResources(IntPtr Samples, IntPtr ResultBuffer,
        IntPtr ResultContents, IntPtr MarkerSource, IntPtr MarkerDestination, IntPtr Event, IntPtr[] BoundaryDescriptors);

    /// <summary>Fixed counter resources and GPU-only marker dependencies. Marker cost is never subtracted.</summary>
    internal sealed class MetalTimestampApi : IMetalTimestampApi
    {
        readonly IMetalTimestampNative _native;

        internal MetalTimestampApi(IMetalTimestampNative native)
        {
            _native = native ?? throw new ArgumentNullException(nameof(native));
            using var pool = ObjCAutoreleasePool.Enter();
            HasTimestampCounterSet = native.HasTimestampCounterSet;
            SupportsStageBoundary = native.SupportsSampling(0);
            SupportsBlitBoundary = native.SupportsSampling(4);
            if (!HasTimestampCounterSet || !(SupportsStageBoundary || SupportsBlitBoundary)) return;
            IntPtr probe = native.CreateSampleBuffer(1);
            SupportsTimestampCounters = probe != IntPtr.Zero;
            native.Release(probe);
        }

        internal bool HasTimestampCounterSet { get; }
        public bool SupportsTimestampCounters { get; }
        public bool SupportsStageBoundary { get; }
        public bool SupportsBlitBoundary { get; }

        public MetalTimestampResources CreatePool(uint capacity, bool stageBoundary)
        {
            using var pool = ObjCAutoreleasePool.Enter();
            if (!SupportsTimestampCounters) throw new NotSupportedException("This Metal device has no usable timestamp counters.");
            if (capacity == 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (stageBoundary ? !SupportsStageBoundary : !SupportsBlitBoundary)
                throw new NotSupportedException("This Metal device does not support that sampling boundary.");
            uint resultBytes = checked(capacity * sizeof(ulong));
            var descriptors = new IntPtr[stageBoundary ? checked((int)capacity) : 0];
            MetalTimestampResources resources = new(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, descriptors);
            try
            {
                resources = resources with { Samples = Require(_native.CreateSampleBuffer(capacity)) };
                resources = resources with { ResultBuffer = Require(_native.CreateBuffer(resultBytes)) };
                resources = resources with { ResultContents = Require(_native.Contents(resources.ResultBuffer)) };
                resources = resources with { MarkerSource = Require(_native.CreateBuffer(4)) };
                resources = resources with { MarkerDestination = Require(_native.CreateBuffer(4)) };
                resources = resources with { Event = Require(_native.CreateEvent()) };
                for (uint i = 0; i < descriptors.Length; i++)
                {
                    descriptors[i] = Require(_native.CreateBlitDescriptor());
                    _native.AttachSamples(descriptors[i], resources.Samples, ulong.MaxValue, i);
                }
                return resources;
            }
            catch
            {
                DestroyPool(resources);
                throw;
            }
        }

        public void DestroyPool(in MetalTimestampResources resources)
        {
            using var pool = ObjCAutoreleasePool.Enter();
            foreach (IntPtr descriptor in resources.BoundaryDescriptors) _native.Release(descriptor);
            _native.Release(resources.Event);
            _native.Release(resources.MarkerDestination);
            _native.Release(resources.MarkerSource);
            _native.Release(resources.ResultBuffer);
            _native.Release(resources.Samples);
        }

        public void SampleClocks(out ulong cpuNanoseconds, out ulong gpuTicks)
        {
            using var pool = ObjCAutoreleasePool.Enter();
            _native.SampleClocks(out cpuNanoseconds, out gpuTicks);
        }

        public void WriteBoundary(IntPtr commandBuffer, in MetalTimestampResources resources, uint index,
            ulong beforeValue, ulong afterValue, bool stageBoundary)
        {
            using var pool = ObjCAutoreleasePool.Enter();
            _native.Signal(commandBuffer, resources.Event, beforeValue);
            _native.Wait(commandBuffer, resources.Event, beforeValue);
            IntPtr descriptor = stageBoundary ? resources.BoundaryDescriptors[index] : IntPtr.Zero;
            IntPtr encoder = Require(_native.BeginBlit(commandBuffer, descriptor));
            try
            {
                _native.CopyMarker(encoder, resources.MarkerSource, resources.MarkerDestination, 4);
                if (!stageBoundary) _native.Sample(encoder, resources.Samples, index, barrier: true);
            }
            finally { _native.EndBlit(encoder); }
            _native.Signal(commandBuffer, resources.Event, afterValue);
            _native.Wait(commandBuffer, resources.Event, afterValue);
        }

        public void Resolve(IntPtr commandBuffer, in MetalTimestampResources resources, uint count)
        {
            using var pool = ObjCAutoreleasePool.Enter();
            IntPtr encoder = Require(_native.BeginBlit(commandBuffer, IntPtr.Zero));
            try { _native.Resolve(encoder, resources.Samples, count, resources.ResultBuffer); }
            finally { _native.EndBlit(encoder); }
        }

        public void CopyResolved(in MetalTimestampResources resources, uint count, Span<ulong> raw)
            => _native.CopyResolved(resources.ResultContents, count, raw);

        static IntPtr Require(IntPtr handle) => handle != IntPtr.Zero ? handle
            : throw new InvalidOperationException("Metal refused a timestamp resource or encoder.");
    }
}
