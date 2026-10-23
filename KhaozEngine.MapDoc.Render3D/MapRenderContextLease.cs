using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Captured dependencies and cached samples retain their CPU charge across policy-only replacements.</summary>
internal sealed class MapRenderContextLease : IDisposable
{
    sealed class Shared(MapWindowFoliageContext context, MapRenderAdmission.CpuLease capture)
    {
        internal readonly object Gate = new();
        internal readonly List<MapRenderAdmission.CpuLease> Charges = [capture];
        internal MapRenderAdmission.CpuLease Capture => capture;
        internal MapWindowFoliageContext Context => context;
        internal int Owners = 1;
    }
    readonly Shared _shared;
    bool _disposed;
    internal MapRenderContextLease(MapWindowFoliageContext context, MapRenderAdmission.CpuLease capture) => _shared = new(context, capture);
    MapRenderContextLease(Shared shared) => _shared = shared;
    internal MapWindowFoliageContext Context => _shared.Context;
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
    sealed class SampleCharge(Shared shared, MapRenderAdmission.CpuLease charge) : IDisposable
    {
        bool _disposed;
        public void Dispose()
        {
            lock (shared.Gate)
            {
                if (_disposed) return; _disposed = true;
                shared.Charges.Remove(charge); charge.Dispose();
            }
        }
    }
    public void Dispose()
    {
        lock (_shared.Gate)
        {
            if (_disposed) return; _disposed = true;
            if (--_shared.Owners != 0) return;
            _shared.Charges.Remove(_shared.Capture);
        }
        MapFoliageRenderCache.For(_shared.Context).Clear();
        _shared.Capture.Dispose(); _shared.Context.PreparationOwnership?.Dispose();
    }
}
