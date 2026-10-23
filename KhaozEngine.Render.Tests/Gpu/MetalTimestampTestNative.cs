using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Tests.Gpu
{
    internal sealed class MetalTimestampTestNative : IMetalTimestampNative, IMetalCounterMetadataNative
    {
        int _next = 10;
        internal bool CounterSet = true, Stage = true, Blit, FailSamples;
        internal int Allocations, ClockReads, Copies, UnsupportedCalls;
        internal bool ClockError;
        internal ulong CpuEnd = 2000, GpuEnd = 600;
        internal ulong[] Raw = { 150, 175, 200 };
        internal bool Record = true;
        internal Action<string>? OnCall;
        internal int OpenBlits;
        internal Action? BeforeBoundary;
        internal readonly List<string> Trace = new();
        internal readonly List<(ulong Start, ulong End)> Attachments = new();
        internal readonly List<IntPtr> Released = new();
        internal readonly List<ulong> Signals = new();
        internal System.Collections.Immutable.ImmutableArray<MetalCounterSetMetadata> CounterSets = [];
        internal int MetadataReads;
        internal Exception? MetadataFailure;
        internal Exception? DiscoveryFailure;
        internal readonly List<uint> SamplingQueries = new();
        public string? SampleBufferFailureReason => FailSamples ? "injected sample allocation refused" : null;
        public System.Collections.Immutable.ImmutableArray<MetalCounterSetMetadata> ReadCounterSets()
        {
            MetadataReads++;
            if (MetadataFailure is not null) throw MetadataFailure;
            return CounterSets;
        }
        public bool HasTimestampCounterSet => DiscoveryFailure is null ? CounterSet : throw DiscoveryFailure;
        public bool SupportsSampling(uint point) { SamplingQueries.Add(point); return point == 0 ? Stage : point == 4 && Blit; }
        IntPtr Allocate() { Allocations++; return new IntPtr(_next++); }
        public IntPtr CreateSampleBuffer(uint capacity) => FailSamples ? IntPtr.Zero : Allocate();
        public IntPtr CreateBuffer(uint bytes) { if (Record) Trace.Add($"allocate {bytes}"); return Allocate(); }
        public IntPtr Contents(IntPtr buffer) => buffer;
        public IntPtr CreateEvent() => Allocate();
        public IntPtr CreateBlitDescriptor() => Allocate();
        public void AttachSamples(IntPtr descriptor, IntPtr samples, ulong start, ulong end)
        {
            if (!Stage) UnsupportedCalls++;
            Attachments.Add((start, end));
        }
        public void Release(IntPtr handle) { if (handle != IntPtr.Zero) Released.Add(handle); }
        public void SampleClocks(out ulong cpuNanoseconds, out ulong gpuTicks)
        {
            ClockReads++;
            if (ClockError) throw new InvalidOperationException("native clock error");
            cpuNanoseconds = ClockReads == 1 ? 1000ul : CpuEnd;
            gpuTicks = ClockReads == 1 ? 100ul : GpuEnd;
        }
        public void Signal(IntPtr commandBuffer, IntPtr ev, ulong value)
        {
            BeforeBoundary?.Invoke();
            if (Record) Signals.Add(value);
            if (Record) Trace.Add($"signal {value}");
        }
        public void Wait(IntPtr commandBuffer, IntPtr ev, ulong value) { if (Record) Trace.Add($"wait {value}"); }
        public IntPtr BeginBlit(IntPtr commandBuffer, IntPtr descriptor)
        {
            if (descriptor != IntPtr.Zero && !Stage) UnsupportedCalls++;
            if (Record) Trace.Add(descriptor == IntPtr.Zero ? "blit" : "stage blit");
            OnCall?.Invoke("begin blit");
            OpenBlits++;
            return new IntPtr(9);
        }
        public void CopyMarker(IntPtr encoder, IntPtr source, IntPtr destination, uint bytes) { if (Record) Trace.Add($"copy {bytes}"); OnCall?.Invoke("copy"); }
        public void Sample(IntPtr encoder, IntPtr samples, uint index, bool barrier)
        {
            if (!Blit) UnsupportedCalls++;
            if (Record) Trace.Add($"sample {index} barrier={barrier}");
            OnCall?.Invoke("sample");
        }
        public void EndBlit(IntPtr encoder) { OpenBlits--; if (Record) Trace.Add("end blit"); OnCall?.Invoke("end blit"); }
        public void Resolve(IntPtr encoder, IntPtr samples, uint count, IntPtr destination) { if (Record) Trace.Add($"resolve {count}"); OnCall?.Invoke("resolve"); }
        public void CopyResolved(IntPtr contents, uint count, Span<ulong> raw)
        {
            Copies++;
            Raw.AsSpan(0, (int)count).CopyTo(raw);
        }
    }
}
