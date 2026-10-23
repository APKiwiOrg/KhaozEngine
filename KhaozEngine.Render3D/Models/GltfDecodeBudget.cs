using System;

namespace KhaozEngine.Render3D;

public sealed record GltfDecodeLimits(long MaxPeakBytes = 33554432, int MaxVertices = 1048576,
    int MaxIndices = 3145728, int MaxImagePixels = 4194304, int MaxImages = 256);

/// <summary>A shared remaining allowance across retained payloads and concurrent decode scratch.</summary>
public sealed class GltfDecodeBudget
{
    readonly object _gate = new();
    long _owned, _temporary, _peak;
    public GltfDecodeLimits Limits { get; }
    public long RemainingBytes { get { lock (_gate) return Limits.MaxPeakBytes - _owned - _temporary; } }
    public long PeakBytes { get { lock (_gate) return _peak; } }
    public long OwnedBytes { get { lock (_gate) return _owned; } }

    public GltfDecodeBudget(GltfDecodeLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxPeakBytes < 0 || limits.MaxVertices < 0 || limits.MaxIndices < 0 ||
            limits.MaxImagePixels < 0 || limits.MaxImages < 0) throw new ArgumentOutOfRangeException(nameof(limits));
        Limits = limits;
    }

    public bool TryReserve(long ownedBytes, long temporaryBytes) => TryReserve(ownedBytes, temporaryBytes, out _);
    public bool TryReserve(long ownedBytes, long temporaryBytes, out GltfDecodeCapacity? capacity)
    {
        if (ownedBytes < 0 || temporaryBytes < 0) throw new ArgumentOutOfRangeException(nameof(ownedBytes));
        lock (_gate)
        {
            capacity = null;
            long remaining = Limits.MaxPeakBytes - _owned - _temporary;
            if (ownedBytes > remaining || temporaryBytes > remaining - ownedBytes)
            { capacity = PeakCapacity((decimal)ownedBytes + temporaryBytes); return false; }
            _owned += ownedBytes;
            _temporary += temporaryBytes;
            _peak = Math.Max(_peak, _owned + _temporary);
            return true;
        }
    }

    internal GltfDecodeCapacity PeakCapacity(decimal additional)
    { lock (_gate) return new(nameof(GltfDecodeLimits.MaxPeakBytes), (decimal)_owned + _temporary + additional, Limits.MaxPeakBytes); }

    public void ReleaseTemporary(long bytes)
    {
        lock (_gate)
        {
            if (bytes < 0 || bytes > _temporary) throw new ArgumentOutOfRangeException(nameof(bytes));
            _temporary -= bytes;
        }
    }

    public void ReleaseOwned(long bytes)
    {
        lock (_gate)
        {
            if (bytes < 0 || bytes > _owned) throw new ArgumentOutOfRangeException(nameof(bytes));
            _owned -= bytes;
        }
    }
}
