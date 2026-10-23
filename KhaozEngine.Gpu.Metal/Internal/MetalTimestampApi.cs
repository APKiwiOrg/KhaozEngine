using System;
using System.Collections.Immutable;
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
            try
            {
                HasTimestampCounterSet = native.HasTimestampCounterSet;
                SupportsStageBoundary = native.SupportsSampling(0);
                SupportsBlitBoundary = native.SupportsSampling(4);
                if (!HasTimestampCounterSet) { UnavailableReason = "No usable timestamp counter exists in a timestamp counter set."; return; }
                if (!(SupportsStageBoundary || SupportsBlitBoundary)) { UnavailableReason = "Neither stage nor blit boundary sampling is supported."; return; }
                IntPtr probe = native.CreateSampleBuffer(1);
                try
                {
                    SupportsTimestampCounters = probe != IntPtr.Zero;
                    if (!SupportsTimestampCounters) UnavailableReason = (native as IMetalCounterMetadataNative)?.SampleBufferFailureReason
                        ?? "Metal refused the timestamp sample-buffer probe.";
                }
                finally { native.Release(probe); }
            }
            catch (Exception e)
            {
                SupportsTimestampCounters = false;
                CapabilityStatus = e is NotSupportedException ? GpuDiagnosticStatus.Unsupported : GpuDiagnosticStatus.Invalid;
                UnavailableReason = e.Message;
            }
            if (SupportsTimestampCounters) CapabilityStatus = GpuDiagnosticStatus.Available;
        }

        internal bool HasTimestampCounterSet { get; }
        internal string? UnavailableReason { get; }
        internal GpuDiagnosticStatus CapabilityStatus { get; } = GpuDiagnosticStatus.Unsupported;
        public bool SupportsTimestampCounters { get; }
        public bool SupportsStageBoundary { get; }
        public bool SupportsBlitBoundary { get; }

        internal MetalCounterMetadata ReadCounterMetadata()
        {
            using var pool = ObjCAutoreleasePool.Enter();
            return ReadCounterMetadataCore() with
            {
                TimestampCapability = new(SupportsTimestampCounters ? new(GpuDiagnosticValueType.Boolean, Boolean: true) : null,
                    "boolean", "MTLDevice timestamp counter, stage/blit sampling boundary and successful sample-buffer probe, capability only",
                    CapabilityStatus, UnavailableReason, GpuDiagnosticProvenance.HardwareLimit)
            };
        }

        MetalCounterMetadata ReadCounterMetadataCore()
        {
            if (_native is not IMetalCounterMetadataNative metadata)
                return new([], [], GpuDiagnosticStatus.Unsupported, "Native counter enumeration is unavailable.");
            try
            {
                var sets = metadata.ReadCounterSets();
                var points = ImmutableArray.CreateBuilder<uint>();
                for (uint point = 0; point <= 4; point++) if (_native.SupportsSampling(point)) points.Add(point);
                return new(sets, points.ToImmutable(), sets.IsEmpty ? GpuDiagnosticStatus.Unsupported : GpuDiagnosticStatus.Available,
                    sets.IsEmpty ? "The device advertises no counter sets." : null);
            }
            catch (Exception e)
            {
                return new([], [], e is NotSupportedException ? GpuDiagnosticStatus.Unsupported : GpuDiagnosticStatus.Invalid, e.Message);
            }
        }

        public MetalTimestampResources CreatePool(uint capacity, bool stageBoundary)
        {
            using var pool = ObjCAutoreleasePool.Enter();
            if (!SupportsTimestampCounters) throw new NotSupportedException("This Metal device has no usable timestamp counters. " + UnavailableReason);
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
            catch
            {
                // Match failed-End abandonment: cleanup must not replace the primary failure.
                try { _native.EndBlit(encoder); }
                catch { }
                throw;
            }
            _native.EndBlit(encoder);
            _native.Signal(commandBuffer, resources.Event, afterValue);
            _native.Wait(commandBuffer, resources.Event, afterValue);
        }

        public void Resolve(IntPtr commandBuffer, in MetalTimestampResources resources, uint count)
        {
            using var pool = ObjCAutoreleasePool.Enter();
            IntPtr encoder = Require(_native.BeginBlit(commandBuffer, IntPtr.Zero));
            try { _native.Resolve(encoder, resources.Samples, count, resources.ResultBuffer); }
            catch
            {
                try { _native.EndBlit(encoder); }
                catch { }
                throw;
            }
            _native.EndBlit(encoder);
        }

        public void CopyResolved(in MetalTimestampResources resources, uint count, Span<ulong> raw)
            => _native.CopyResolved(resources.ResultContents, count, raw);

        static IntPtr Require(IntPtr handle) => handle != IntPtr.Zero ? handle
            : throw new InvalidOperationException("Metal refused a timestamp resource or encoder.");
    }
}
