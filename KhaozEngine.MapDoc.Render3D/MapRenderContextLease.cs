using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Captured dependencies and cached columns retain their CPU charge across policy-only replacements.</summary>
internal sealed class MapRenderContextLease : IDisposable
{
    sealed class Shared
    {
        internal readonly object Gate = new();
        internal readonly List<MapRenderAdmission.CpuLease> Charges;
        internal MapRenderAdmission.CpuLease? Capture;
        internal MapWindowFoliageContext? Context;
        internal int Owners = 1;
        internal Shared(MapWindowFoliageContext context, MapRenderAdmission.CpuLease capture)
        { Context = context; Capture = capture; Charges = [capture]; }
    }
    readonly Shared _shared;
    bool _disposed;
    internal MapRenderContextLease(MapWindowFoliageContext context, MapRenderAdmission.CpuLease capture) => _shared = new(context, capture);
    MapRenderContextLease(Shared shared) => _shared = shared;
    internal MapWindowFoliageContext Context => _shared.Context ?? throw new ObjectDisposedException(nameof(MapRenderContextLease));
    internal long CpuBytes { get { lock (_shared.Gate) return _shared.Charges.Sum(c => c.Bytes); } }
    internal MapRenderContextLease Retain()
    {
        lock (_shared.Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            checked { _shared.Owners++; }
            return new(_shared);
        }
    }
    internal IDisposable Own(MapRenderAdmission.CpuLease charge)
    {
        lock (_shared.Gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _shared.Charges.Add(charge);
            return new SampleCharge(_shared, charge);
        }
    }
    sealed class SampleCharge : IMapFoliageCpuRetention
    {
        Shared? _shared;
        MapRenderAdmission.CpuLease? _charge;
        internal SampleCharge(Shared shared, MapRenderAdmission.CpuLease charge) { _shared = shared; _charge = charge; }
        public void ReduceTo(long bytes)
        {
            var shared = _shared ?? throw new ObjectDisposedException(nameof(SampleCharge));
            lock (shared.Gate) (_charge ?? throw new ObjectDisposedException(nameof(SampleCharge))).ReduceTo(bytes);
        }
        public void Dispose()
        {
            var shared = _shared;
            if (shared is null) return;
            lock (shared.Gate)
            {
                var charge = _charge;
                if (charge is null) return;
                _shared = null; _charge = null;
                shared.Charges.Remove(charge); charge.Dispose();
            }
        }
    }
    public void Dispose()
    {
        MapWindowFoliageContext context; MapRenderAdmission.CpuLease capture;
        lock (_shared.Gate)
        {
            if (_disposed) return; _disposed = true;
            if (--_shared.Owners != 0) return;
            context = _shared.Context!; capture = _shared.Capture!;
            _shared.Context = null; _shared.Capture = null; _shared.Charges.Remove(capture);
        }
        MapFoliageRenderCache.For(context).Clear();
        capture.Dispose(); context.PreparationOwnership?.Dispose();
    }
}
