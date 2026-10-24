using System;
using System.Threading;

namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>Live owned resource payload. Native D3D11 allocation size and DXGI adapter capacity are different facts.</summary>
internal sealed class D3D11MemoryDiagnostics : IGpuResourceMemoryDiagnostics
{
    readonly object _gate = new();
    ulong _buffers, _textures, _staging, _peak, _count;

    internal IDisposable TrackBuffer(in GpuBufferDescription d, int frames)
    {
        var plan = D3D11ViewPolicy.ForBuffer(d.Usage);
        ulong staging = plan.Ring ? D3D11UniformRing.TotalBytesFor(d.SizeInBytes, frames)
            : plan.Staging ? d.SizeInBytes : 0;
        return Track(d.SizeInBytes, 0, staging);
    }

    internal IDisposable TrackTexture(in GpuTextureDescription d)
    {
        ulong texels = 0;
        uint mips = Math.Max(1, d.MipLevels);
        for (uint mip = 0; mip < Math.Min(mips, 32); mip++)
            texels = checked(texels + (ulong)D3D11UploadBounds.MipDimension(d.Width, mip)
                * D3D11UploadBounds.MipDimension(d.Height, mip));
        if (mips > 32) texels = checked(texels + mips - 32);
        ulong bytes = checked(texels * D3D11UploadBounds.ArraySlices(d) * TexelBytes(d.Format) * d.SampleCount);
        return Track(0, bytes, (d.Usage & GpuTextureUsage.Staging) != 0 ? bytes : 0);
    }

    // DXGI R32G8X24 is 64 bits. These are packed texel bytes, never native row pitch or allocation size.
    static uint TexelBytes(GpuPixelFormat format) => format switch
    {
        GpuPixelFormat.R8UNorm => 1,
        GpuPixelFormat.R16G16B16A16Float or GpuPixelFormat.D32FloatS8UInt => 8,
        GpuPixelFormat.R8G8B8A8UNorm or GpuPixelFormat.B8G8R8A8UNorm or GpuPixelFormat.R32Float
            or GpuPixelFormat.D24UNormS8UInt or GpuPixelFormat.R16G16Float => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    IDisposable Track(ulong buffers, ulong textures, ulong staging)
    {
        lock (_gate)
        {
            ulong newBuffers = checked(_buffers + buffers), newTextures = checked(_textures + textures);
            ulong newStaging = checked(_staging + staging), peak = Math.Max(_peak, checked(newBuffers + newTextures));
            ulong count = checked(_count + 1);
            var lease = new Lease(this, buffers, textures, staging);
            _buffers = newBuffers; _textures = newTextures; _staging = newStaging; _peak = peak; _count = count;
            return lease;
        }
    }

    sealed class Lease(D3D11MemoryDiagnostics owner, ulong buffers, ulong textures, ulong staging) : IDisposable
    {
        D3D11MemoryDiagnostics? _owner = owner;
        public void Dispose()
        {
            var live = Interlocked.Exchange(ref _owner, null);
            if (live is null) return;
            lock (live._gate)
            {
                live._buffers -= buffers; live._textures -= textures; live._staging -= staging; live._count--;
            }
        }
    }

    public GpuResourceMemorySnapshot ReadMemorySnapshot() => Snapshot(false);

    internal GpuResourceMemorySnapshot Snapshot(bool invalid)
    {
        lock (_gate)
        {
            GpuDiagnosticMetric Logical(ulong value, string source, string unit = "bytes") => invalid
                ? Missing("The device was disposed or lost.", unit, GpuDiagnosticStatus.Invalid)
                : new(new(GpuDiagnosticValueType.UInt64, UInt64: value), unit, source,
                    GpuDiagnosticStatus.Available, null, GpuDiagnosticProvenance.LogicalPayload);
            return new(Logical(_buffers, "D3D11 live owned buffer requested SizeInBytes, ring logical window only"),
                Logical(_textures, "D3D11 live owned packed DXGI texel payload including mips, cube faces, layers and samples"),
                Logical(_staging, "D3D11 ring ByteWidth and staging requested capacity, overlaps logical payload, excludes texture row padding"),
                Missing("History is not identified by the resource API."), Missing("Scratch is not identified by the resource API."),
                Logical(_peak, "D3D11 peak live buffer plus packed texture payload, excludes backing allocations"),
                Logical(_count, "D3D11 live owned buffers and textures, excludes views, shaders and swapchain-owned backbuffers", "count"),
                Missing("D3D11 does not expose committed allocation sizes through this backend. ByteWidth and texel bytes are payload."),
                Missing("No native per-resource residency query is enabled. Adapter memory size is not residency."),
                Missing("IDXGIAdapter3::QueryVideoMemoryInfo is not enabled. Adapter memory size is not a budget."), null);
        }
    }

    static GpuDiagnosticMetric Missing(string reason, string unit = "bytes", GpuDiagnosticStatus status = GpuDiagnosticStatus.Unsupported)
        => new(null, unit, "D3D11 memory diagnostics", status, reason, GpuDiagnosticProvenance.Unknown);
}
