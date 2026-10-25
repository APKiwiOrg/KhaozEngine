using System;
using System.Collections.Generic;
using System.Threading;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed record MetalNativeMemorySnapshot(GpuDiagnosticMetric AllocatedBytes, bool? UnifiedMemory);

/// <summary>Owned requested payload and retained capacity. Neither is native residency or an allocation sum.</summary>
internal sealed class MetalMemoryDiagnostics : IGpuResourceMemoryDiagnostics
{
    readonly object _gate = new();
    readonly Dictionary<IntPtr, ulong> _stagingCapacities = new();
    ulong _buffers, _textures, _capacity, _peakBytes, _count, _peakCount;

    internal ulong PeakResourceCount { get { lock (_gate) return _peakCount; } }

    internal IDisposable TrackBuffer(in GpuBufferDescription d, int frames)
        => Track(d.SizeInBytes, 0, frames > 0 && MetalBufferPolicy.IsRingBacked(d.Usage)
            ? MetalRingStride.TotalBytesFor(d.SizeInBytes, frames)
            : (d.Usage & GpuBufferUsage.Staging) != 0 ? MetalBufferPolicy.AllocationBytes(d.SizeInBytes) : 0);

    internal IDisposable TrackTexture(in GpuTextureDescription d)
    {
        ulong texels = 0;
        uint mips = Math.Max(1, d.MipLevels);
        for (uint mip = 0; mip < Math.Min(32, mips); mip++)
            texels = checked(texels + (ulong)MetalStagingLayout.MipDimension(d.Width, mip)
                * MetalStagingLayout.MipDimension(d.Height, mip));
        if (mips > 32) texels = checked(texels + mips - 32);
        ulong layers = checked((ulong)d.ArrayLayers * ((d.Usage & GpuTextureUsage.Cubemap) != 0 ? 6ul : 1));
        ulong payload = checked(texels * layers * MetalStagingLayout.BytesPerTexel(d.Format) * d.SampleCount);
        ulong capacity = (d.Usage & GpuTextureUsage.Staging) == 0 ? 0
            : MetalStagingLayout.TotalBytes(new(d.Width, d.Height, d.MipLevels, d.ArrayLayers, d.Format));
        return Track(0, payload, capacity);
    }

    internal void OwnStaging(IntPtr handle, ulong bytes)
    {
        if (handle == IntPtr.Zero) throw new ArgumentException("A nil staging buffer cannot be owned.", nameof(handle));
        lock (_gate)
        {
            if (_stagingCapacities.ContainsKey(handle)) throw new InvalidOperationException("A Metal staging allocation was registered twice.");
            ulong capacity = checked(_capacity + bytes), count = checked(_count + 1);
            _stagingCapacities.Add(handle, bytes);
            _capacity = capacity; _count = count;
            _peakCount = Math.Max(_peakCount, count);
        }
    }

    internal void ReleaseStaging(IntPtr handle)
    {
        lock (_gate)
            if (_stagingCapacities.Remove(handle, out ulong bytes))
            {
                _capacity -= bytes; _count--;
            }
    }

    IDisposable Track(ulong buffers, ulong textures, ulong capacity)
    {
        lock (_gate)
        {
            ulong b = checked(_buffers + buffers), t = checked(_textures + textures);
            ulong c = checked(_capacity + capacity), n = checked(_count + 1);
            ulong peak = Math.Max(_peakBytes, checked(b + t));
            var lease = new Lease(this, buffers, textures, capacity);
            _buffers = b; _textures = t; _capacity = c; _count = n;
            _peakBytes = peak; _peakCount = Math.Max(_peakCount, n);
            return lease;
        }
    }

    sealed class Lease(MetalMemoryDiagnostics owner, ulong buffers, ulong textures, ulong capacity) : IDisposable
    {
        MetalMemoryDiagnostics? _owner = owner;
        public void Dispose()
        {
            var live = Interlocked.Exchange(ref _owner, null);
            if (live is null) return;
            lock (live._gate)
            {
                live._buffers -= buffers; live._textures -= textures; live._capacity -= capacity; live._count--;
            }
        }
    }

    public GpuResourceMemorySnapshot ReadMemorySnapshot() => Snapshot(false);

    internal GpuResourceMemorySnapshot Snapshot(bool invalid, Func<MetalNativeMemorySnapshot>? readNative = null)
    {
        MetalNativeMemorySnapshot native = new(Missing("No native device allocation snapshot was supplied."), null);
        if (invalid) native = new(Missing("The device was disposed or lost.", status: GpuDiagnosticStatus.Invalid), null);
        else if (readNative is not null)
        {
            try { native = readNative(); }
            catch (Exception e)
            {
                native = new(Missing(e.Message, status: e is NotSupportedException ? GpuDiagnosticStatus.Unsupported
                    : GpuDiagnosticStatus.Invalid), null);
            }
        }
        lock (_gate)
        {
            GpuDiagnosticMetric Logical(ulong value, string source, string unit = "bytes") => invalid
                ? Missing("The device was disposed or lost.", unit, GpuDiagnosticStatus.Invalid)
                : new(new(GpuDiagnosticValueType.UInt64, UInt64: value), unit, source,
                    GpuDiagnosticStatus.Available, null, GpuDiagnosticProvenance.LogicalPayload);
            return new(Logical(_buffers, "Metal live owned buffer requested payload, ring logical window only"),
                Logical(_textures, "Metal live owned packed upload texel payload including mips, cube faces, layers and samples, excludes native padding"),
                Logical(_capacity, "Metal live staging allocations and ring capacity, overlaps logical staging payload and ring windows, do not add to payload"),
                Missing("History is not identified by the resource API."), Missing("Scratch is not identified by the resource API."),
                Logical(_peakBytes, "Metal peak live buffer plus packed texture payload, excludes native allocations and extra staging capacity"),
                Logical(_count, "Metal live owned buffers, textures and internal staging buffers, excludes borrowed drawables, views, shaders and opaque counter sample storage", "count"),
                native.AllocatedBytes,
                Missing("Metal supplies no residency snapshot here. Device allocation and adapter capacity are not residency."),
                Missing("No hard native budget is exposed here. recommendedMaxWorkingSetSize is a performance recommendation, not a budget."),
                native.UnifiedMemory);
        }
    }

    internal static GpuDiagnosticMetric Missing(string reason, string unit = "bytes", GpuDiagnosticStatus status = GpuDiagnosticStatus.Unsupported)
        => new(null, unit, "Metal memory diagnostics", status, reason, GpuDiagnosticProvenance.Unknown);
}
