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
    readonly List<(MapPreparationReservation? Receipt, IDisposable? Cpu, long CpuBytes)> _charges = new();
    long _reservedBytes, _cpuBytes;
    internal long Bytes { get; private set; }
    internal void Grow(long bytes, MapSceneAdmission? admission, Func<long, IDisposable>? retain)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        long target = checked(Bytes + bytes);
        long reserved = admission is null ? 0 : target - _reservedBytes;
        long cpu = retain is null ? 0 : target - _cpuBytes;
        MapPreparationReservation? receipt = null;
        IDisposable? lease = null;
        try
        {
            // Growth belongs to this preparation, even when earlier slices still own the same registry.
            // Keep their receipts intact if either admission or CPU retention refuses the new allocation.
            if (reserved > 0)
            {
                using var operation = admission!.BeginPreparation();
                operation.Reserve(reserved);
                receipt = operation.Complete(reserved, publishToParent: false, retainDependencies: false);
            }
            if (cpu > 0) lease = retain!(cpu);
            if (receipt is not null || lease is not null) _charges.Add((receipt, lease, cpu));
            _reservedBytes += reserved; _cpuBytes += cpu; Bytes = target;
        }
        catch
        {
            lease?.Dispose(); receipt?.Dispose();
            throw;
        }
    }
    internal void ReduceTo(long target)
    {
        if (target < 0 || target > Bytes) throw new ArgumentOutOfRangeException(nameof(target));
        long reserved = Math.Max(0, _reservedBytes - target), cpu = Math.Max(0, _cpuBytes - target);
        for (int i = _charges.Count - 1; i >= 0 && (reserved > 0 || cpu > 0); i--)
        {
            var slice = _charges[i];
            long returned = Math.Min(reserved, slice.Receipt?.Bytes ?? 0);
            if (returned > 0)
            {
                slice.Receipt!.Release(returned);
                if (slice.Receipt.Bytes == 0) { slice.Receipt.Dispose(); slice.Receipt = null; }
                reserved -= returned; _reservedBytes -= returned;
            }
            returned = Math.Min(cpu, slice.CpuBytes);
            if (returned > 0)
            {
                if (returned == slice.CpuBytes) { slice.Cpu!.Dispose(); slice.Cpu = null; }
                else if (slice.Cpu is IMapFoliageCpuRetention resizable) resizable.ReduceTo(slice.CpuBytes - returned);
                else throw new InvalidOperationException("Foliage CPU inventory requires a reducible retained charge.");
                slice.CpuBytes -= returned; cpu -= returned; _cpuBytes -= returned;
            }
            if (slice.Receipt is null && slice.Cpu is null) _charges.RemoveAt(i);
            else _charges[i] = slice;
        }
        Bytes = target;
        if (_charges.Count == 0 || 2L * _charges.Count < _charges.Capacity) _charges.TrimExcess();
    }
    public void Dispose()
    {
        ReduceTo(0);
    }
}
