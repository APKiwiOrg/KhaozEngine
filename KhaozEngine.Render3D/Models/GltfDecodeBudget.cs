using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;

namespace KhaozEngine.Render3D;

/// <param name="MaxPeakBytes">Demand sites: GltfDecodeAdmission, GltfDecodeBudget, ImageRgba. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxVertices">Demand sites: GltfDecodeAdmission. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxIndices">Demand sites: GltfDecodeAdmission. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxImagePixels">Demand sites: GltfDecodeAdmission, ImageRgba, MapRenderMaterials. See docs/CAPACITY-DEMAND.md.</param>
/// <param name="MaxImages">Demand sites: GltfDecodeAdmission, MapRenderMaterials. See docs/CAPACITY-DEMAND.md.</param>
public sealed record GltfDecodeLimits(long MaxPeakBytes = 33554432, int MaxVertices = 1048576,
    int MaxIndices = 3145728, int MaxImagePixels = 4194304, int MaxImages = 256);

/// <summary>A shared remaining allowance across retained payloads and concurrent decode scratch.</summary>
public sealed class GltfDecodeBudget
{
    sealed class Shared
    {
        internal readonly object Gate = new();
        internal long Owned, Temporary, Peak;
    }
    readonly Shared _shared;
    internal object DemandIdentity => _shared;
    IGltfDecodeReservationObserver? _observer;
    internal bool Observe(IGltfDecodeReservationObserver observer)
    {
        lock (_shared.Gate)
        {
            if (_observer is not null) return false;
            _observer = observer;
            return true;
        }
    }
    internal void StopObserving(IGltfDecodeReservationObserver observer)
    { lock (_shared.Gate) { if (ReferenceEquals(_observer, observer)) _observer = null; } }
    public GltfDecodeLimits Limits { get; }
    public long RemainingBytes { get { lock (_shared.Gate) return Limits.MaxPeakBytes - _shared.Owned - _shared.Temporary; } }
    public long PeakBytes { get { lock (_shared.Gate) return _shared.Peak; } }
    public long OwnedBytes { get { lock (_shared.Gate) return _shared.Owned; } }

    // A reservation view shares the caller's ceiling, but observes only allocations made through that view.
    // Payload disposal retains its originating view, so releasing an older payload cannot refund a newer decode.
    internal GltfDecodeBudget ReservationView() => new(Limits, _shared);
    GltfDecodeBudget(GltfDecodeLimits limits, Shared shared) { Limits = limits; _shared = shared; }
    public GltfDecodeBudget(GltfDecodeLimits limits)
    {
        _shared = new();
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxPeakBytes < 0 || limits.MaxVertices < 0 || limits.MaxIndices < 0 ||
            limits.MaxImagePixels < 0 || limits.MaxImages < 0) throw new ArgumentOutOfRangeException(nameof(limits));
        Limits = limits;
    }

    public bool TryReserve(long ownedBytes, long temporaryBytes) => TryReserve(ownedBytes, temporaryBytes, out _);
    public bool TryReserve(long ownedBytes, long temporaryBytes, out GltfDecodeCapacity? capacity)
    {
        if (ownedBytes < 0 || temporaryBytes < 0) throw new ArgumentOutOfRangeException(nameof(ownedBytes));
        lock (_shared.Gate)
        {
            capacity = null;
            long remaining = Limits.MaxPeakBytes - _shared.Owned - _shared.Temporary;
            if (DemandDiagnostics.Check(_shared, "GltfDecodeBudget.MaxPeakBytes.1", "MaxPeakBytes", (decimal)_shared.Owned + _shared.Temporary, (decimal)ownedBytes + temporaryBytes, Limits.MaxPeakBytes, ownedBytes > remaining || temporaryBytes > remaining - ownedBytes, CapacityDemandLifetime.Transient))
            { capacity = PeakCapacity((decimal)ownedBytes + temporaryBytes); return false; }
            _observer?.Reserve(checked(ownedBytes + temporaryBytes));
            _shared.Owned += ownedBytes;
            _shared.Temporary += temporaryBytes;
            _shared.Peak = Math.Max(_shared.Peak, _shared.Owned + _shared.Temporary);
            return true;
        }
    }

    internal GltfDecodeCapacity PeakCapacity(decimal additional)
    { lock (_shared.Gate) return new(nameof(GltfDecodeLimits.MaxPeakBytes), (decimal)_shared.Owned + _shared.Temporary + additional, Limits.MaxPeakBytes); }

    public void ReleaseTemporary(long bytes)
    {
        lock (_shared.Gate)
        {
            if (bytes < 0 || bytes > _shared.Temporary) throw new ArgumentOutOfRangeException(nameof(bytes));
            _observer?.Release(bytes);
            _shared.Temporary -= bytes;
        }
    }

    public void ReleaseOwned(long bytes)
    {
        lock (_shared.Gate)
        {
            if (bytes < 0 || bytes > _shared.Owned) throw new ArgumentOutOfRangeException(nameof(bytes));
            _observer?.Release(bytes);
            _shared.Owned -= bytes;
        }
    }
}
