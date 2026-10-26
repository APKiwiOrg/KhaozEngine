using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Render3D;

internal interface IMapFoliageCpuRetention : IDisposable
{
    void ReduceTo(long bytes);
}

/// <summary>Resizable inventory independent of a captured context or column payload.</summary>
internal sealed class MapFoliageRetainedInventory : IDisposable
{
    MapPreparationReservation? _receipt;
    readonly List<(IDisposable Lease, long Bytes)> _cpu = new();
    long _cpuBytes;
    internal long Bytes { get; private set; }
    internal void Grow(long bytes, MapSceneAdmission? admission, Func<long, IDisposable>? retain)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        bool adopted = _receipt is null && admission is not null;
        if (adopted)
        {
            using var operation = admission!.BeginPreparation();
            operation.Reserve(checked(Bytes + bytes));
            _receipt = operation.Complete(checked(Bytes + bytes), publishToParent: false, retainDependencies: false);
        }
        else _receipt?.Reserve(bytes);
        long cpu = checked(bytes + (_cpu.Count == 0 ? Bytes : 0));
        try
        {
            if (retain is not null && cpu > 0)
            { var lease = retain(cpu); _cpu.Add((lease, cpu)); _cpuBytes = checked(_cpuBytes + cpu); }
            Bytes = checked(Bytes + bytes);
        }
        catch
        {
            if (adopted) { _receipt?.Dispose(); _receipt = null; }
            else _receipt?.Release(bytes);
            throw;
        }
    }
    internal void ReduceTo(long target)
    {
        if (target < 0 || target > Bytes) throw new ArgumentOutOfRangeException(nameof(target));
        _receipt?.Release(Bytes - target);
        Bytes = target;
        long remaining = Math.Max(0, _cpuBytes - target);
        while (remaining > 0)
        {
            var slice = _cpu[^1];
            long returned = Math.Min(remaining, slice.Bytes);
            if (returned == slice.Bytes) { _cpu.RemoveAt(_cpu.Count - 1); slice.Lease.Dispose(); }
            else
            {
                if (slice.Lease is not IMapFoliageCpuRetention resizable)
                    throw new InvalidOperationException("Foliage CPU inventory requires a reducible retained charge.");
                resizable.ReduceTo(slice.Bytes - returned); _cpu[^1] = (slice.Lease, slice.Bytes - returned);
            }
            remaining -= returned; _cpuBytes -= returned;
        }
        if (_cpu.Count == 0 || 2L * _cpu.Count < _cpu.Capacity) _cpu.TrimExcess();
    }
    public void Dispose()
    {
        ReduceTo(0); _receipt?.Dispose(); _receipt = null;
    }
}
